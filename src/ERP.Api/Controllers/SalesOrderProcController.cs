using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单控制器（第二阶段：存储过程驱动 + 完整单据生命周期）
/// <para>ERP-410：本控制器路由 <c>api/v2/sales-orders</c> 的五个写动作（<c>save</c> / <c>delete</c> /
/// <c>audit</c> / <c>void</c> / <c>restore</c>）是独立于 ERP-404 <c>api/v2/bills</c> 的旧库存储过程写路径
/// （<c>db_owner.sp_Biz_SalesOrder</c>），必须在任何 <c>sp_Biz_*</c> 调用、默认值 / 单号写入或成功响应
/// <b>之前</b>复用既有的有限变更策略 <see cref="LegacyBillMutationRules"/>（旧单据族 <c>sales-order</c>）：
/// 实时身份 + 既有「销售订单」功能菜单授权，随后按策略裁决。今天不存在权威的「旧 Oid ↔ 规范 Id」映射与
/// 已验证业务适配器，因此一律 fail closed，绝不推断 <c>Oid = Id</c>、绝不信任请求头金额、绝不静默转换
/// 不支持的请求字段、绝不执行 <c>sp_Biz_*</c>、绝不占用单号、绝不写日志 / 通知。</para>
/// <para>规范销售订单工作流（<c>api/sales-orders</c>）与转换工作流不受影响；本控制器的查询 / 翻页 / 导航路由
/// （<c>SalesOrderProcController.Query.cs</c>）保持不变，留待其自身的授权 / 迁移门槛。</para>
/// </summary>
[ApiController]
[Route("api/v2/sales-orders")]
[Authorize]
public partial class SalesOrderProcController : ControllerBase
{
    /// <summary>旧库销售订单族键（与 ERP-404 <see cref="LegacyBillMutationRules"/> 有限目录唯一一致）。</summary>
    public const string LegacyFamilyKey = "sales-order";

    private readonly StoredProcedureService _sp;
    private readonly IErpDbContext _db;

    public SalesOrderProcController(StoredProcedureService sp, IErpDbContext db)
    {
        _sp = sp;
        _db = db;
    }

    /// <summary>当前登录账号 Id（缺失 / 非法 = <c>null</c>，绝不当作匿名或管理员）。</summary>
    private long? CurrentUserId()
        => long.TryParse(User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) && id > 0
            ? id
            : null;

    /// <summary>
    /// ERP-410 旧销售订单有限变更策略门禁：复用 ERP-404 <see cref="LegacyBillMutationRules.AuthorizeAsync"/>
    /// （族 <c>sales-order</c>），在任何 <c>sp_Biz_SalesOrder</c> 调用之前裁决。动作标识与 Oid 身份由服务端
    /// 根据路由确定（绝不来自请求体），被拒绝时返回受控 <see cref="ApiResponse{T}"/> 失败信封
    /// （保持既有「HTTP 200 + 业务错误码」契约，不执行任何写入）；放行时返回 <c>null</c>
    /// （仅当目录登记了已验证业务适配器后才会发生）。
    /// </summary>
    private async Task<IActionResult?> GuardLegacyMutationAsync(LegacyBillOperation operation)
    {
        try
        {
            await LegacyBillMutationRules.AuthorizeAsync(_db, CurrentUserId(), LegacyFamilyKey, operation);
            return null;
        }
        catch (BusinessException ex)
        {
            Serilog.Log.Warning(
                "旧销售订单写入门禁拒绝：{Operation}（错误码 {Code}）；未执行 sp_Biz_SalesOrder、未占用单号、未写日志、未通知",
                operation, ex.Code);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }

    /// <summary>保存（新增/更新，通过 sp_Biz_SalesOrder）</summary>
    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] SalesOrderSaveRequest request)
    {
        // ERP-410：有限变更策略必须在任何 sp_Biz_SalesOrder 调用 / 默认值 / 单号写入 / 成功响应之前裁决。
        var denied = await GuardLegacyMutationAsync(LegacyBillOperation.Save);
        if (denied is not null) return denied;

        // 今天不存在任何已验证适配器（策略 HasValidatedAdapter 恒为 false），以下旧写路径不可达；
        // 绝不信任请求头金额（request.TotalAmount 等），也绝不把旧 Oid 当作规范 Id。
        var result = await _sp.ExecuteAsync("db_owner.sp_Biz_SalesOrder", new Dictionary<string, object?>
        {
            ["@Action"] = "Save",
            ["@Oid"] = request.Oid,
            ["@OrderDate"] = request.OrderDate,
            ["@CustId"] = request.CustId,
            ["@SalesmanId"] = request.SalesmanId,
            ["@Currency"] = request.Currency,
            ["@ExchangeRate"] = request.ExchangeRate,
            ["@TotalAmount"] = request.TotalAmount,
            ["@DepositRatio"] = request.DepositRatio,
            ["@DepositAmount"] = request.DepositAmount,
            ["@PaymentTerms"] = request.PaymentTerms,
            ["@DeliveryDate"] = request.DeliveryDate,
            ["@ShippingMethod"] = request.ShippingMethod,
            ["@PortId"] = request.PortId,
            ["@Remark"] = request.Remark
        });
        if (!result.Success)
            return Ok(ApiResponse<object>.Fail(result.Msg, ErrorCodes.RuleConflict));
        return Ok(ApiResponse<object>.Success(new { Oid = result.Oid, BillNo = result.BillNo }, "保存成功"));
    }

    /// <summary>删除</summary>
    [HttpPost("{oid:long}/delete")]
    public async Task<IActionResult> Delete(long oid) => await ExecuteAction(LegacyBillOperation.Delete, oid, "删除成功");

    /// <summary>审核（sp_Biz_SalesOrder Audit → sp_Chk_SalesOrder）</summary>
    [HttpPost("{oid:long}/audit")]
    public async Task<IActionResult> Audit(long oid) => await ExecuteAction(LegacyBillOperation.Audit, oid, "审核通过");

    /// <summary>作废</summary>
    [HttpPost("{oid:long}/void")]
    public async Task<IActionResult> Void(long oid) => await ExecuteAction(LegacyBillOperation.Void, oid, "已作废");

    /// <summary>还原</summary>
    [HttpPost("{oid:long}/restore")]
    public async Task<IActionResult> Restore(long oid) => await ExecuteAction(LegacyBillOperation.Restore, oid, "已还原");

    private async Task<IActionResult> ExecuteAction(LegacyBillOperation operation, long oid, string successMsg)
    {
        // ERP-410：状态流转同样必须在任何 sp_Biz_SalesOrder 调用 / 状态读取 / 成功响应之前 fail closed。
        var denied = await GuardLegacyMutationAsync(operation);
        if (denied is not null) return denied;

        var result = await _sp.ExecuteAsync("db_owner.sp_Biz_SalesOrder", new Dictionary<string, object?>
        {
            ["@Action"] = operation.ToString(),
            ["@Oid"] = oid
        });
        if (!result.Success)
            return Ok(ApiResponse<object>.Fail(result.Msg, ErrorCodes.RuleConflict));
        return Ok(ApiResponse<object>.Success(null, successMsg));
    }
}
