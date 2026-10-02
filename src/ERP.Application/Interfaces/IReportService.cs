using ERP.Application.DTOs;
using ERP.Application.Services;

namespace ERP.Application.Interfaces;

/// <summary>
/// 报表查询服务接口
/// </summary>
public interface IReportService
{
    /// <summary>商品销量排名榜（仅已审核、未删除、当前账号数据范围内的销售出库；按商品/规格/单位分桶）</summary>
    Task<List<ReportDtos.ProductSalesRankItem>> GetProductSalesRankingAsync(
        DateTime start, DateTime end, int top, SalespersonDataScope scope,
        ProductSalesRankingFilterDto? filter = null);

    /// <summary>订单利润暂估表（仅已审核、未删除、当前账号数据范围内的销售订单；日期 / 订单 / 明细均有界，超出即 fail closed）</summary>
    /// <param name="filter">ERP-223 可选应用筛选（客户 Id / 原币币种）；传 null 保持既有行为。</param>
    Task<List<ReportDtos.OrderProfitItem>> GetOrderProfitEstimateAsync(
        DateTime start, DateTime end, SalespersonDataScope scope, OrderProfitEstimateFilterDto? filter = null);

    /// <summary>客户出货量统计表（仅已审核、未删除、当前账号数据范围内的销售订单证据；日期 / 订单 / 明细均有界，超出即 fail closed）</summary>
    /// <param name="filter">ERP-231 可选应用筛选（客户 Id / 原币币种）；传 null 保持既有行为。</param>
    Task<List<ReportDtos.CustomerShipmentItem>> GetCustomerShipmentStatsAsync(
        DateTime start, DateTime end, SalespersonDataScope scope, CustomerShipmentFilterDto? filter = null);

    /// <summary>业务员产值报表（仅已审核、未删除、已分配业务员、当前账号数据范围内的销售订单证据；日期有界，超出即 fail closed）</summary>
    /// <param name="filter">ERP-237 可选应用筛选（客户 Id / 业务员 Id / 原币币种）；传 null 保持既有行为。</param>
    Task<List<ReportDtos.SalesmanOutputItem>> GetSalesmanOutputAsync(
        DateTime start, DateTime end, SalespersonDataScope scope, SalesmanOutputFilterDto? filter = null);

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

    /// <summary>
    /// 业务员提成表（ERP-243，只读派生）：仅已审核、未删除、当前账号数据范围内的销售订单头，按「持久化业务员桶 × 原始原币」分组；
    /// 金额仅已知币种签名小计、未知 / 无效币种金额为 null 仅计数；利润 / 利润率 / 提成额恒为未知（null）；
    /// 系统参数 SalesCommissionRate 仅为可空的当前参考比例（至多读取 2 条未删除记录，缺失 / 重复 / 非法一律未知）。日期有界，超出即 fail closed。
    /// </summary>
    Task<List<ReportDtos.SalesCommissionItem>> GetSalesCommissionAsync(DateTime start, DateTime end, SalespersonDataScope scope);

    /// <summary>
    /// 动态业务员提成证据报表预览（ERP-244，只读派生）：先校验字段 / 日期 / 分页 / 可选应用筛选（fail closed），
    /// 再按当前账号业务员数据范围在数据库端过滤并应用客户 / 业务员 / 币种谓词，稳定排序后在 Take(501) 之前做来源上限探测，
    /// 最后按「持久化业务员桶 × 原始原币」分组、只投影选定字段并返回 total / 分页 / 口径上下文（即使证据列被隐藏，日期 /
    /// 规范化筛选 / 来源上限 / 来源依据 / 原币分组 / 未知成本提成 / 当前参考比例上下文仍始终呈现）。
    /// 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL。
    /// </summary>
    Task<DynamicSalesCommissionReportPageDto> GetDynamicSalesCommissionReportAsync(
        DynamicSalesCommissionReportRequest request, SalespersonDataScope scope);

    /// <summary>跟进提醒（下次跟进日期已到期或即将到期的记录；按当前账号业务员数据范围过滤）</summary>
    Task<List<ReportDtos.FollowUpDueItem>> GetFollowUpDueAsync(DateTime asOfDate, int aheadDays, SalespersonDataScope scope);

    /// <summary>
    /// 动态跟进提醒报表预览（ERP-193，只读派生）：先校验字段 / 到期状态 / 提前天数 / 分页，再按当前账号业务员数据范围
    /// 在数据库端过滤、计数、稳定排序与分页，最后只投影选定字段并返回 total / truncation / empty 上下文。
    /// </summary>
    Task<DynamicFollowUpDueReportPageDto> GetDynamicFollowUpDueReportAsync(
        DynamicFollowUpDueReportRequest request, SalespersonDataScope scope);

    /// <summary>报价成交率分析（ERP-018，按业务员聚合；分子 = 已转 PI / 已转销售订单 / 状态已完成；按当前账号业务员数据范围过滤）</summary>
    /// <param name="filter">ERP-208 可选应用筛选（客户 Id / 业务员关键字 / 原币币种）；传 null 保持既有行为。</param>
    Task<List<ReportDtos.QuotationConversionItem>> GetQuotationConversionAsync(
        DateTime start, DateTime end, SalespersonDataScope scope, QuotationConversionFilterDto? filter = null);

    /// <summary>
    /// 库存移动与呆滞报表（ERP-029，只读派生）：主表为库存行，出入库 / 最后移动日期 / 停滞天数取自库存流水台账；
    /// 红字冲销流水按反方向参与毛额（原流水与红字成对净额为 0，不二次扣减）；无台账的行以「未知」呈现，不估算成本。
    /// </summary>
    Task<ReportDtos.InventoryMovementReport> GetInventoryMovementReportAsync(
        ReportDtos.InventoryMovementReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 库存库龄与成本估值报表（ERP-034，只读派生）：主表为库存行，库龄分层由库存流水台账按 FIFO 派生
    /// （红字冲销按 ReversalOfMovementId 权威配对，不二次扣减）；没有台账分层依据的数量单列为「库龄未知」，
    /// 不放进任何分层；估值只使用库存行持久化的移动加权平均成本与库存金额，成本依据缺失时数量与金额记为未知（null）。
    /// </summary>
    Task<ReportDtos.InventoryAgingReport> GetInventoryAgingReportAsync(
        ReportDtos.InventoryAgingReportQuery query, CancellationToken cancellationToken = default);
}
