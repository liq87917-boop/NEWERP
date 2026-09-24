namespace ERP.Application.Interfaces;

/// <summary>
/// 报表查询服务接口
/// </summary>
public interface IReportService
{
    /// <summary>商品销量排名榜</summary>
    Task<List<ReportDtos.ProductSalesRankItem>> GetProductSalesRankingAsync(DateTime start, DateTime end, int top);

    /// <summary>订单利润暂估表</summary>
    Task<List<ReportDtos.OrderProfitItem>> GetOrderProfitEstimateAsync(DateTime start, DateTime end);

    /// <summary>客户出货量统计表</summary>
    Task<List<ReportDtos.CustomerShipmentItem>> GetCustomerShipmentStatsAsync(DateTime start, DateTime end);

    /// <summary>业务员产值报表</summary>
    Task<List<ReportDtos.SalesmanOutputItem>> GetSalesmanOutputAsync(DateTime start, DateTime end);

    /// <summary>资产负债表</summary>
    Task<ReportDtos.FinancialStatement> GetBalanceSheetAsync(DateTime asOfDate);

    /// <summary>利润表</summary>
    Task<ReportDtos.FinancialStatement> GetIncomeStatementAsync(DateTime start, DateTime end);

    /// <summary>现金流量表</summary>
    Task<ReportDtos.FinancialStatement> GetCashFlowStatementAsync(DateTime start, DateTime end);

    /// <summary>应收账款账龄分析（截止日期，按销售订单逐笔）</summary>
    Task<List<ReportDtos.ArAgingItem>> GetArAgingAsync(DateTime asOfDate);

    /// <summary>柜量与装柜利用率统计（按柜号聚合）</summary>
    Task<List<ReportDtos.ContainerStatsItem>> GetContainerStatsAsync(DateTime start, DateTime end);

    /// <summary>采购成本分析（按供应商聚合采购订单）</summary>
    Task<List<ReportDtos.PurchaseCostItem>> GetPurchaseCostAsync(DateTime start, DateTime end);

    /// <summary>退税汇总（按退税期间聚合）</summary>
    Task<List<ReportDtos.TaxRefundSummaryItem>> GetTaxRefundSummaryAsync();

    /// <summary>库存预警（低于安全库存 / 高于上限）</summary>
    Task<List<ReportDtos.StockAlertItem>> GetStockAlertAsync();

    /// <summary>业务员提成表（销售额 / 毛利 / 提成额，提成比例取自系统参数）</summary>
    Task<List<ReportDtos.SalesCommissionItem>> GetSalesCommissionAsync(DateTime start, DateTime end);

    /// <summary>跟进提醒（下次跟进日期已到期或即将到期的记录）</summary>
    Task<List<ReportDtos.FollowUpDueItem>> GetFollowUpDueAsync(DateTime asOfDate, int aheadDays);

    /// <summary>报价成交率分析（ERP-018，按业务员聚合；分子 = 已转 PI / 已转销售订单 / 状态已完成）</summary>
    Task<List<ReportDtos.QuotationConversionItem>> GetQuotationConversionAsync(DateTime start, DateTime end);

    /// <summary>
    /// 库存移动与呆滞报表（ERP-029，只读派生）：主表为库存行，出入库 / 最后移动日期 / 停滞天数取自库存流水台账；
    /// 红字冲销流水按反方向参与毛额（原流水与红字成对净额为 0，不二次扣减）；无台账的行以「未知」呈现，不估算成本。
    /// </summary>
    Task<ReportDtos.InventoryMovementReport> GetInventoryMovementReportAsync(
        ReportDtos.InventoryMovementReportQuery query, CancellationToken cancellationToken = default);
}
