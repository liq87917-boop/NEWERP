using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 报表管理控制器
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportController : ControllerBase
{
    /// <summary>跟进提醒报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string FollowUpDueMenuCode = "follow-up-due";

    /// <summary>跟进提醒报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string FollowUpDueMenuText = "跟进提醒";

    /// <summary>报价成交率报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string QuotationConversionMenuCode = "quotation";

    /// <summary>报价成交率报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string QuotationConversionMenuText = "报价单";

    /// <summary>商品销量排名报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string ProductSalesRankingMenuCode = "product-sales-ranking";

    /// <summary>商品销量排名报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string ProductSalesRankingMenuText = "商品销量排名榜";

    /// <summary>订单利润暂估报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string OrderProfitMenuCode = "order-profit";

    /// <summary>订单利润暂估报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string OrderProfitMenuText = "订单利润暂估表";

    /// <summary>客户出货量统计报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string CustomerShipmentMenuCode = "customer-shipment";

    /// <summary>客户出货量统计报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string CustomerShipmentMenuText = "客户出货量统计表";

    /// <summary>业务员产值报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string SalesmanOutputMenuCode = "salesman-output";

    /// <summary>业务员产值报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string SalesmanOutputMenuText = "业务员产值报表";

    /// <summary>业务员提成报表要求的既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    private const string SalesCommissionMenuCode = "sales-commission";

    /// <summary>业务员提成报表要求菜单的中文文案（与既有菜单名一致）</summary>
    private const string SalesCommissionMenuText = "业务员提成表";

    private readonly IReportService _reportService;
    private readonly IErpDbContext? _db;

    /// <summary>
    /// <paramref name="db"/> 为可空是为了不破坏既有仅注入 <see cref="IReportService"/> 的报表端点测试；
    /// 跟进提醒端点每次请求都要求已注入的数据库上下文，缺失时 fail closed（绝不静默返回未授权数据）。
    /// </summary>
    public ReportController(IReportService reportService, IErpDbContext? db = null)
    {
        _reportService = reportService;
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>商品销量排名榜（ERP-212；每次请求重新校验身份、菜单授权与业务员数据范围，缺失即 fail closed）</summary>
    [HttpGet("product-sales-ranking")]
    public async Task<IActionResult> ProductSalesRanking([FromQuery] DateTime start, [FromQuery] DateTime end, [FromQuery] int top = 10)
    {
        if (_db is null)
            throw new BusinessException("商品销量排名报表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看商品销量排名报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(ProductSalesRankingMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{ProductSalesRankingMenuText}」（{ProductSalesRankingMenuCode}）模块授权：拒绝查看商品销量排名报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetProductSalesRankingAsync(start, end, top, scope);
        return Ok(ApiResponse<List<ReportDtos.ProductSalesRankItem>>.Success(result));
    }

    /// <summary>订单利润暂估表（每次请求重新校验身份、菜单授权与业务员数据范围，缺失即 fail closed）</summary>
    [HttpGet("order-profit")]
    public async Task<IActionResult> OrderProfit([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        if (_db is null)
            throw new BusinessException("订单利润暂估表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看订单利润暂估表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(OrderProfitMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{OrderProfitMenuText}」（{OrderProfitMenuCode}）模块授权：拒绝查看订单利润暂估表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetOrderProfitEstimateAsync(start, end, scope);
        return Ok(ApiResponse<List<ReportDtos.OrderProfitItem>>.Success(result));
    }

    /// <summary>客户出货量统计表（每次请求重新校验身份、菜单授权与业务员数据范围，缺失即 fail closed）</summary>
    [HttpGet("customer-shipment")]
    public async Task<IActionResult> CustomerShipment([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        if (_db is null)
            throw new BusinessException("客户出货量统计表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看客户出货量统计表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(CustomerShipmentMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{CustomerShipmentMenuText}」（{CustomerShipmentMenuCode}）模块授权：拒绝查看客户出货量统计表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetCustomerShipmentStatsAsync(start, end, scope);
        return Ok(ApiResponse<List<ReportDtos.CustomerShipmentItem>>.Success(result));
    }

    /// <summary>业务员产值报表（ERP-235；每次请求重新校验身份、菜单授权与业务员数据范围，缺失即 fail closed；仅已分配业务员、已审核、未删除、授权客户的销售订单证据）</summary>
    [HttpGet("salesman-output")]
    public async Task<IActionResult> SalesmanOutput([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        if (_db is null)
            throw new BusinessException("业务员产值报表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看业务员产值报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(SalesmanOutputMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SalesmanOutputMenuText}」（{SalesmanOutputMenuCode}）模块授权：拒绝查看业务员产值报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetSalesmanOutputAsync(start, end, scope);
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

    /// <summary>业务员提成表（ERP-243；每次请求重新校验身份、菜单授权与业务员数据范围，缺失即 fail closed，不返回任何数据）</summary>
    [HttpGet("sales-commission")]
    public async Task<IActionResult> SalesCommission([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        if (_db is null)
            throw new BusinessException("业务员提成报表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看业务员提成报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(SalesCommissionMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SalesCommissionMenuText}」（{SalesCommissionMenuCode}）模块授权：拒绝查看业务员提成报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetSalesCommissionAsync(start, end, scope);
        return Ok(ApiResponse<List<ReportDtos.SalesCommissionItem>>.Success(result));
    }

    /// <summary>跟进提醒（下次跟进日期已到期 / 未来 N 天内即将到期；默认 N=7；每次请求重新校验身份、菜单授权与业务员数据范围）</summary>
    [HttpGet("follow-up-due")]
    public async Task<IActionResult> FollowUpDue([FromQuery] DateTime? asOfDate, [FromQuery] int aheadDays = 7)
    {
        if (aheadDays is < 0 or > 365)
            throw new BusinessException("跟进提醒的提前天数必须在 0 到 365 之间", ErrorCodes.InvalidParameter);

        if (_db is null)
            throw new BusinessException("跟进提醒报表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看跟进提醒报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(FollowUpDueMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{FollowUpDueMenuText}」（{FollowUpDueMenuCode}）模块授权：拒绝查看跟进提醒报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetFollowUpDueAsync(asOfDate ?? DateTime.Today, aheadDays, scope);
        return Ok(ApiResponse<List<ReportDtos.FollowUpDueItem>>.Success(result));
    }

    /// <summary>报价成交率分析（ERP-018；按业务员聚合，计算口径见 docs/报价单与PI设计方案.md §10.3；每次请求重新校验身份、菜单授权与业务员数据范围）</summary>
    [HttpGet("quotation-conversion")]
    public async Task<IActionResult> QuotationConversion([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        if (_db is null)
            throw new BusinessException("报价成交率报表缺少数据库上下文，无法解析当前账号授权", ErrorCodes.InternalError);

        var db = _db;
        var userId = CurrentUserId();
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看报价成交率报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            db, userId.Value);
        if (!menuCodes.Contains(QuotationConversionMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{QuotationConversionMenuText}」（{QuotationConversionMenuCode}）模块授权：拒绝查看报价成交率报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        var result = await _reportService.GetQuotationConversionAsync(start, end, scope);
        return Ok(ApiResponse<List<ReportDtos.QuotationConversionItem>>.Success(result));
    }

    /// <summary>
    /// 库存移动与呆滞报表（ERP-029，只读派生）：主表为库存行（现存量为基础单位），
    /// 出入库数量 / 最后移动日期 / 停滞天数取自库存流水（StockMovements）台账，按截止日期截断；
    /// 红字冲销流水以反方向计入毛额（原流水 + 红字成对净额为 0，不二次扣减）；
    /// 截止日期前无台账的行以「未知」呈现，不臆造日期或比率，也不估算库存成本 / 金额。
    /// 查询按仓库 / 商品 / 关键字筛选并在数据库内分页（单页上限 200 行），不存在逐行查库。
    /// </summary>
    [HttpGet("inventory-movement")]
    public async Task<IActionResult> InventoryMovement([FromQuery] ReportDtos.InventoryMovementReportQuery query)
    {
        var result = await _reportService.GetInventoryMovementReportAsync(query);
        return Ok(ApiResponse<ReportDtos.InventoryMovementReport>.Success(result));
    }

    /// <summary>
    /// 库存库龄与成本估值报表（ERP-034，只读派生）：主表为库存行（现存量为基础单位），
    /// 库龄分层（0-30 / 31-60 / 61-90 / 91-180 / 180 天以上）由库存流水（StockMovements）台账按 FIFO 派生，
    /// 红字冲销按 ReversalOfMovementId 权威配对（入库冲销扣回原层、出库冲销按原出库日期回补），原流水不删除、不改写；
    /// 没有台账分层依据的数量单列为「库龄未知」而不放进任何分层；估值只使用库存行持久化的移动加权平均成本与库存金额，
    /// 成本依据缺失的数量与金额单列为「未知」（金额 null），不做跨币种合并、也不从文本字典推断汇率。
    /// 查询按仓库 / 商品 / 关键字筛选并按截止日期截断，在数据库内分页（单页上限 200 行），不存在逐行查库。
    /// </summary>
    [HttpGet("inventory-aging")]
    public async Task<IActionResult> InventoryAging([FromQuery] ReportDtos.InventoryAgingReportQuery query)
    {
        var result = await _reportService.GetInventoryAgingReportAsync(query);
        return Ok(ApiResponse<ReportDtos.InventoryAgingReport>.Success(result));
    }
}
