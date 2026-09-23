using ERP.Application.Common;
using ERP.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 报表管理控制器
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportController : ControllerBase
{
    private readonly IReportService _reportService;

    public ReportController(IReportService reportService)
    {
        _reportService = reportService;
    }

    /// <summary>商品销量排名榜</summary>
    [HttpGet("product-sales-ranking")]
    public async Task<IActionResult> ProductSalesRanking([FromQuery] DateTime start, [FromQuery] DateTime end, [FromQuery] int top = 10)
    {
        var result = await _reportService.GetProductSalesRankingAsync(start, end, top);
        return Ok(ApiResponse<List<ReportDtos.ProductSalesRankItem>>.Success(result));
    }

    /// <summary>订单利润暂估表</summary>
    [HttpGet("order-profit")]
    public async Task<IActionResult> OrderProfit([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetOrderProfitEstimateAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.OrderProfitItem>>.Success(result));
    }

    /// <summary>客户出货量统计表</summary>
    [HttpGet("customer-shipment")]
    public async Task<IActionResult> CustomerShipment([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetCustomerShipmentStatsAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.CustomerShipmentItem>>.Success(result));
    }

    /// <summary>业务员产值报表</summary>
    [HttpGet("salesman-output")]
    public async Task<IActionResult> SalesmanOutput([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetSalesmanOutputAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.SalesmanOutputItem>>.Success(result));
    }

    /// <summary>资产负债表</summary>
    [HttpGet("balance-sheet")]
    public async Task<IActionResult> BalanceSheet([FromQuery] DateTime asOfDate)
    {
        var result = await _reportService.GetBalanceSheetAsync(asOfDate);
        return Ok(ApiResponse<ReportDtos.FinancialStatement>.Success(result));
    }

    /// <summary>利润表</summary>
    [HttpGet("income-statement")]
    public async Task<IActionResult> IncomeStatement([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetIncomeStatementAsync(start, end);
        return Ok(ApiResponse<ReportDtos.FinancialStatement>.Success(result));
    }

    /// <summary>现金流量表</summary>
    [HttpGet("cash-flow")]
    public async Task<IActionResult> CashFlow([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetCashFlowStatementAsync(start, end);
        return Ok(ApiResponse<ReportDtos.FinancialStatement>.Success(result));
    }

    /// <summary>应收账款账龄分析表（截止日期；默认今天）</summary>
    [HttpGet("ar-aging")]
    public async Task<IActionResult> ArAging([FromQuery] DateTime? asOfDate)
    {
        var result = await _reportService.GetArAgingAsync(asOfDate ?? DateTime.Today);
        return Ok(ApiResponse<List<ReportDtos.ArAgingItem>>.Success(result));
    }

    /// <summary>柜量与装柜利用率统计</summary>
    [HttpGet("container-stats")]
    public async Task<IActionResult> ContainerStats([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetContainerStatsAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.ContainerStatsItem>>.Success(result));
    }

    /// <summary>采购成本分析（按供应商）</summary>
    [HttpGet("purchase-cost")]
    public async Task<IActionResult> PurchaseCost([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetPurchaseCostAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.PurchaseCostItem>>.Success(result));
    }

    /// <summary>退税汇总（按退税期间）</summary>
    [HttpGet("tax-refund-summary")]
    public async Task<IActionResult> TaxRefundSummary()
    {
        var result = await _reportService.GetTaxRefundSummaryAsync();
        return Ok(ApiResponse<List<ReportDtos.TaxRefundSummaryItem>>.Success(result));
    }

    /// <summary>库存预警（低于安全库存 / 高于上限）</summary>
    [HttpGet("stock-alert")]
    public async Task<IActionResult> StockAlert()
    {
        var result = await _reportService.GetStockAlertAsync();
        return Ok(ApiResponse<List<ReportDtos.StockAlertItem>>.Success(result));
    }

    /// <summary>业务员提成表</summary>
    [HttpGet("sales-commission")]
    public async Task<IActionResult> SalesCommission([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetSalesCommissionAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.SalesCommissionItem>>.Success(result));
    }

    /// <summary>跟进提醒（下次跟进日期已到期 / 未来 N 天内即将到期；默认 N=7）</summary>
    [HttpGet("follow-up-due")]
    public async Task<IActionResult> FollowUpDue([FromQuery] DateTime? asOfDate, [FromQuery] int aheadDays = 7)
    {
        var result = await _reportService.GetFollowUpDueAsync(asOfDate ?? DateTime.Today, aheadDays);
        return Ok(ApiResponse<List<ReportDtos.FollowUpDueItem>>.Success(result));
    }

    /// <summary>报价成交率分析（ERP-018；按业务员聚合，计算口径见 docs/报价单与PI设计方案.md §10.3）</summary>
    [HttpGet("quotation-conversion")]
    public async Task<IActionResult> QuotationConversion([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        var result = await _reportService.GetQuotationConversionAsync(start, end);
        return Ok(ApiResponse<List<ReportDtos.QuotationConversionItem>>.Success(result));
    }
}
