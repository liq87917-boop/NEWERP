using ERP.Application.Common;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单控制器（第二阶段：存储过程驱动 + 完整单据生命周期）
/// </summary>
[ApiController]
[Route("api/v2/sales-orders")]
[Authorize]
public partial class SalesOrderProcController : ControllerBase
{
    private readonly StoredProcedureService _sp;

    public SalesOrderProcController(StoredProcedureService sp)
    {
        _sp = sp;
    }

    /// <summary>保存（新增/更新，通过 sp_Biz_SalesOrder）</summary>
    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] SalesOrderSaveRequest request)
    {
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
    public async Task<IActionResult> Delete(long oid) => await ExecuteAction("Delete", oid, "删除成功");

    /// <summary>审核（sp_Biz_SalesOrder Audit → sp_Chk_SalesOrder）</summary>
    [HttpPost("{oid:long}/audit")]
    public async Task<IActionResult> Audit(long oid) => await ExecuteAction("Audit", oid, "审核通过");

    /// <summary>作废</summary>
    [HttpPost("{oid:long}/void")]
    public async Task<IActionResult> Void(long oid) => await ExecuteAction("Void", oid, "已作废");

    /// <summary>还原</summary>
    [HttpPost("{oid:long}/restore")]
    public async Task<IActionResult> Restore(long oid) => await ExecuteAction("Restore", oid, "已还原");

    private async Task<IActionResult> ExecuteAction(string action, long oid, string successMsg)
    {
        var result = await _sp.ExecuteAsync("db_owner.sp_Biz_SalesOrder", new Dictionary<string, object?>
        {
            ["@Action"] = action,
            ["@Oid"] = oid
        });
        if (!result.Success)
            return Ok(ApiResponse<object>.Fail(result.Msg, ErrorCodes.RuleConflict));
        return Ok(ApiResponse<object>.Success(null, successMsg));
    }
}
