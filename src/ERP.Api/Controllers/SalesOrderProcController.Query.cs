using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单控制器：查询与翻页导航
/// <para>ERP-411：<c>api/v2/sales-orders</c> 是独立于 ERP-405 <c>api/v2/bills</c> 的旧库读路径，
/// 其列表 / 翻页在改造前直接拼 SQL 计数 / 取行（无实时身份、无既有「销售订单」功能菜单、无客户数据范围），
/// 会泄露越权客户的计数、金额与标识。本文件把这套专用读路径收敛到与 ERP-405 <b>同一份</b>读侧门禁
/// （<see cref="LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync"/>，族 <c>sales-order</c>）与
/// <b>同一个</b>受控只读服务 <see cref="ILegacyBillReadService"/> 之下：</para>
/// <list type="number">
/// <item><b>范围先于读取</b>：实时身份 + 既有「销售订单」功能菜单 + 精确客户数据范围（旧 <c>CustId</c> 归属列）
/// 一律在计数 / 分页 / 导航之前裁决；导出菜单（<c>sales-order-export</c>）绝不当作模块权限。</item>
/// <item><b>有限参数化</b>：只经由受控服务使用服务端常量列白名单与参数化值，绝无 <c>SELECT *</c> / 任意 SQL / 标识符 / 联接；
/// 专用路由的响应投影保持不变（列表含定金、导航不含定金），金额 / 币种 / 单位 / <c>null</c> 原值原样保留，稳定按 <c>Oid</c> 排序。</item>
/// <item><b>prev / next 锚点</b>：先在同范围内核验锚点，越权 / 不存在锚点与「范围内没有更多」返回同一结果，绝不泄露锚点存在性。</item>
/// <item><b>缺表 / 缺列</b>：映射为显式 environment-blocked，绝不回退到规范（EF 复数）表，也绝不推断「旧 <c>Oid</c> = 规范 <c>Id</c>」。</item>
/// </list>
/// <para>规范销售订单工作流（<c>api/sales-orders</c>）与 ERP-308 报表退役门槛完全不变。</para>
/// </summary>
public partial class SalesOrderProcController
{
    /// <summary>
    /// 分页查询（支持单据号搜索、状态筛选）。ERP-411：先做读侧门禁（实时身份 + 既有「销售订单」功能菜单 +
    /// 客户数据范围），再由受控只读服务在范围约束下完成计数与分页；越权行绝不出现，响应只含专用路由既有投影字段。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] int? status)
    {
        try
        {
            var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(
                _db, CurrentUserId(), LegacyFamilyKey);
            query.Normalize();

            var page = await _legacyReads.ReadPageAsync(LegacyFamilyKey, new LegacyBillReadQuery
            {
                Keyword = query.Keyword,
                Status = status,
                Page = query.Page,
                PageSize = query.PageSize,
            }, scope, RequestCancellation());

            var items = page.Items.Select(ProjectListRow).ToList();
            return Ok(ApiResponse<object>.Success(new
            {
                items,
                total = page.Total,
                page = page.Page,
                pageSize = page.PageSize,
            }));
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("查询", ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }

    /// <summary>
    /// 翻页导航（第一张 / 上一张 / 下一张 / 最后一张）。ERP-411：范围先于读取；<c>prev</c> / <c>next</c> 会在同一范围内
    /// 核验锚点，不可访问（越权 / 不存在）锚点与「范围内没有更多」返回同一结果，绝不泄露锚点存在性。
    /// </summary>
    [HttpGet("navigate")]
    public async Task<IActionResult> Navigate([FromQuery] long oid, [FromQuery] string direction)
    {
        try
        {
            var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(
                _db, CurrentUserId(), LegacyFamilyKey);

            var navigation = await _legacyReads.NavigateAsync(
                LegacyFamilyKey, oid, direction, scope, RequestCancellation());
            if (!navigation.Found || navigation.Row is null)
                return Ok(ApiResponse<object>.Fail("没有更多单据", ErrorCodes.NotFound));

            return Ok(ApiResponse<object>.Success(ProjectNavigateRow(navigation.Row)));
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("翻页导航", ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }

    /// <summary>
    /// 专用列表投影（改造前字段集：<c>Oid</c> / <c>BillNo</c> / <c>OrderDate</c> / <c>CustId</c> /
    /// <c>TotalAmount</c> / <c>DepositAmount</c> / <c>Status</c>）；原值（含 <c>null</c>、原币、十进制精度）原样保留，
    /// 绝不新增受控服务白名单之外的列。
    /// </summary>
    private static object ProjectListRow(IReadOnlyDictionary<string, object?> row) => new
    {
        Oid = Field(row, "Oid"),
        BillNo = Field(row, "BillNo"),
        OrderDate = Field(row, "OrderDate"),
        CustId = Field(row, "CustId"),
        TotalAmount = Field(row, "TotalAmount"),
        DepositAmount = Field(row, "DepositAmount"),
        Status = Field(row, "Status"),
    };

    /// <summary>
    /// 专用导航投影（改造前字段集：<c>Oid</c> / <c>BillNo</c> / <c>OrderDate</c> / <c>CustId</c> /
    /// <c>TotalAmount</c> / <c>Status</c>，导航不返回定金）。
    /// </summary>
    private static object ProjectNavigateRow(IReadOnlyDictionary<string, object?> row) => new
    {
        Oid = Field(row, "Oid"),
        BillNo = Field(row, "BillNo"),
        OrderDate = Field(row, "OrderDate"),
        CustId = Field(row, "CustId"),
        TotalAmount = Field(row, "TotalAmount"),
        Status = Field(row, "Status"),
    };

    /// <summary>从受控服务返回的授权列行中取原值；列缺失按 <c>null</c>（绝不臆造值）。</summary>
    private static object? Field(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) ? value : null;

    /// <summary>请求取消令牌（直接实例化控制器的单元测试无 HttpContext 时安全退化为 <see cref="CancellationToken.None"/>）。</summary>
    private CancellationToken RequestCancellation()
        => HttpContext?.RequestAborted ?? CancellationToken.None;

    /// <summary>读侧门禁拒绝的结构化告警（只记录受控取值，不写库、不影响主流程）。</summary>
    private static void LogLegacyReadDenied(string operation, BusinessException ex)
        => Serilog.Log.Warning("旧销售订单读侧门禁拒绝：{Operation}（错误码 {Code}）", operation, ex.Code);
}

/// <summary>销售订单保存请求</summary>
public class SalesOrderSaveRequest
{
    public long Oid { get; set; }
    public DateTime OrderDate { get; set; } = DateTime.Today;
    public long CustId { get; set; }
    public long? SalesmanId { get; set; }
    public int Currency { get; set; } = 2;
    public decimal ExchangeRate { get; set; } = 1;
    public decimal TotalAmount { get; set; }
    public decimal DepositRatio { get; set; }
    public decimal DepositAmount { get; set; }
    public string? PaymentTerms { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? ShippingMethod { get; set; }
    public long? PortId { get; set; }
    public string? Remark { get; set; }
}
