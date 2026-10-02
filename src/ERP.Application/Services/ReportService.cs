using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 报表服务实现：基于各业务单据做聚合统计
/// </summary>
public partial class ReportService : IReportService
{
    private readonly IErpDbContext _db;

    public ReportService(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>商品销量排名金额口径标签：数量 × 商品当前售价，币种未知，仅估算，非实际发货收入</summary>
    public const string ProductSalesRankingAmountLabel = "当前价估算(币种未知，非实际发货收入)";

    /// <summary>商品销量排名允许的日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int ProductSalesRankingMaxDateRangeDays = 366;

    /// <summary>
    /// 商品销量排名榜（ERP-212，只读派生）：仅统计已审核、未删除、当前账号数据范围内的销售出库头，
    /// 再关联未删除明细按商品 / 规格 / 单位分桶聚合；签名数量（可为负）直接求和，不二次计入退货或库存流水。
    /// 日期 / Top 校验先于任何源读取；金额为数量 × 商品当前售价的估算（币种未知，非实际发货收入）。
    /// </summary>
    public async Task<List<ReportDtos.ProductSalesRankItem>> GetProductSalesRankingAsync(
        DateTime start, DateTime end, int top, SalespersonDataScope scope,
        ProductSalesRankingFilterDto? filter = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 日期与 Top 校验先于任何源读取（fail closed）
        var startDate = start.Date;
        var endDate = end.Date;
        if (endDate < startDate)
            throw new BusinessException("商品销量排名的结束日期不能早于开始日期", ErrorCodes.InvalidParameter);

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > ProductSalesRankingMaxDateRangeDays)
            throw new BusinessException($"商品销量排名的日期范围最多 {ProductSalesRankingMaxDateRangeDays} 天（含首尾）", ErrorCodes.InvalidParameter);

        if (top is < 1 or > 200)
            throw new BusinessException("商品销量排名的 Top 必须在 1 到 200 之间", ErrorCodes.InvalidParameter);

        // 结束日按排他边界处理（含首尾，即 < endDate 次日）
        var endExclusive = endDate.AddDays(1);

        // 2) 先对销售出库头做状态 / 日期 / 业务员数据范围过滤，再关联未删除明细聚合
        var stockOuts = _db.StockOuts
            .Where(o => !o.IsDeleted
                        && o.Status == DocumentStatus.Approved
                        && o.StockOutDate >= startDate
                        && o.StockOutDate < endExclusive);
        stockOuts = SalespersonDataScopeService.FilterByCustomer(stockOuts, scope, o => o.CustomerId);
        if (filter?.CustomerId is > 0)
            stockOuts = stockOuts.Where(o => o.CustomerId == filter.CustomerId.Value);

        var details = _db.StockOutDetails.Where(d => !d.IsDeleted);
        if (filter?.ProductId is > 0)
            details = details.Where(d => d.ProductId == filter.ProductId.Value);
        if (!string.IsNullOrEmpty(filter?.Unit))
            details = details.Where(d => d.Unit == filter.Unit);

        var detailGroups = await details
            .Join(stockOuts, d => d.StockOutId, o => o.Id, (d, o) => d)
            .GroupBy(d => new { d.ProductId, d.ProductName, d.Spec, d.Unit })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.ProductName,
                g.Key.Spec,
                g.Key.Unit,
                TotalQuantity = g.Sum(x => x.Quantity)
            })
            .OrderByDescending(x => x.TotalQuantity)
            .ThenBy(x => x.ProductId)
            .ThenBy(x => x.ProductName)
            .ThenBy(x => x.Spec)
            .ThenBy(x => x.Unit)
            .Take(top)
            .ToListAsync();

        var productIds = detailGroups.Select(x => x.ProductId).ToList();
        var products = await _db.BaseProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var result = new List<ReportDtos.ProductSalesRankItem>();
        for (var i = 0; i < detailGroups.Count; i++)
        {
            var g = detailGroups[i];
            products.TryGetValue(g.ProductId, out var p);
            result.Add(new ReportDtos.ProductSalesRankItem
            {
                ProductId = g.ProductId,
                ProductCode = p?.ProductCode ?? string.Empty,
                ProductName = g.ProductName,
                Spec = g.Spec,
                Unit = g.Unit,
                TotalQuantity = g.TotalQuantity,
                TotalAmount = g.TotalQuantity * (p?.SalePrice ?? 0),
                AmountLabel = ProductSalesRankingAmountLabel,
                Rank = i + 1
            });
        }
        return result;
    }

    /// <summary>订单利润暂估表：销售额口径标签（订单总额，原币，未换算汇率）</summary>
    public const string OrderProfitSalesAmountLabel = "销售额＝订单总额（原币，未换算汇率）";

    /// <summary>订单利润暂估表：成本证据标签（无可信历史成本依据，不回落为 0）</summary>
    public const string OrderProfitCostEvidence = "成本未知：无可信可比较的历史成本依据，不回落为0";

    /// <summary>订单利润暂估表：利润证据标签（绝不减去币种未知的当前成本价）</summary>
    public const string OrderProfitProfitEvidence = "利润未知：绝不减去币种未知的当前成本价";

    /// <summary>订单利润暂估表：当前价估算口径标签（币种未知，仅估算，非历史成本）</summary>
    public const string OrderProfitCurrentPriceEstimateLabel = "当前价估算(币种未知，仅估算，非历史成本)";

    /// <summary>订单利润暂估表：明细缺失 / 为空的估算原因</summary>
    public const string OrderProfitEstimateMissingDetailReason = "明细缺失或为空，无法估算当前价";

    /// <summary>订单利润暂估表：商品缺失 / 已删除的估算原因</summary>
    public const string OrderProfitEstimateMissingProductReason = "明细商品缺失或已删除，无法估算当前价";

    /// <summary>订单利润暂估表：明细缺少商品的估算原因</summary>
    public const string OrderProfitEstimateIncompleteDetailReason = "明细不完整（缺少商品），无法估算当前价";

    /// <summary>订单利润暂估表允许的日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int OrderProfitMaxDateRangeDays = 366;

    /// <summary>订单利润暂估表单次最多返回的订单头数（有界：读取 501 探测 500 上限）</summary>
    private const int OrderProfitMaxOrders = 500;

    /// <summary>订单利润暂估表单次最多关联的非删除明细数（有界：读取 10001 探测 10000 上限）</summary>
    private const int OrderProfitMaxDetails = 10000;

    /// <summary>
    /// 订单利润暂估表（ERP-219 / ERP-220，只读派生）：仅统计已审核、未删除、当前账号数据范围内的销售订单头。
    /// 销售额保留订单原币 <c>TotalAmount</c>（绝不换算汇率、不改动金额）；成本 / 利润 / 利润率为未知（null），
    /// 绝不减去币种未知的当前成本价、绝不回落为 0；「当前价估算」仅为数量 × 商品当前 <c>CostPrice</c> 的独立口径
    /// （币种未知，仅估算，非历史成本），明细 / 商品缺失或已删除时为 null 并给出显式原因。
    /// 日期校验（含溢出防护）先于任何源读取；订单 / 明细均为有界读取，超出上限立即 fail closed，不返回部分行或金额；
    /// 订单头在 SQL 端按 OrderDate 降序、Id 降序稳定排序后，才用固定批量查询解析商品 / 客户，无逐单查库。
    /// </summary>
    public async Task<List<ReportDtos.OrderProfitItem>> GetOrderProfitEstimateAsync(
        DateTime start, DateTime end, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 日期校验先于任何源读取（fail closed，含日期溢出防护）
        var startDate = start.Date;
        var endDate = end.Date;
        if (endDate < startDate)
            throw new BusinessException("订单利润暂估表的结束日期不能早于开始日期", ErrorCodes.InvalidParameter);

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > OrderProfitMaxDateRangeDays)
            throw new BusinessException($"订单利润暂估表的日期范围最多 {OrderProfitMaxDateRangeDays} 天（含首尾）", ErrorCodes.InvalidParameter);

        // 结束日按排他上界（含首尾，即 < 结束日次日）；窗口已限制在 366 天内，此处加一不会溢出
        var endExclusive = endDate.AddDays(1);

        // 2) 订单头：已审核、未删除、日期窗口、业务员数据范围；稳定排序后做有界读取（501 探测 500 上限）
        var ordersQuery = _db.SalesOrders
            .Where(o => !o.IsDeleted
                        && o.Status == DocumentStatus.Approved
                        && o.OrderDate >= startDate
                        && o.OrderDate < endExclusive);
        ordersQuery = SalespersonDataScopeService.FilterByCustomer(ordersQuery, scope, o => o.CustomerId);

        var orders = await ordersQuery
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.Id)
            .Take(OrderProfitMaxOrders + 1)
            .ToListAsync();

        if (orders.Count > OrderProfitMaxOrders)
        {
            throw new BusinessException(
                $"订单利润暂估表超出报告上限：范围内已审核销售订单超过 {OrderProfitMaxOrders} 张"
                + "（fail closed，不返回任何行或金额）",
                ErrorCodes.RuleConflict);
        }

        // 3) 明细：仅未删除且属于已读取订单头；有界读取（10001 探测 10000 上限）
        var orderIds = orders.Select(o => o.Id).ToList();
        var details = await _db.SalesOrderDetails
            .Where(d => orderIds.Contains(d.SalesOrderId) && !d.IsDeleted)
            .Take(OrderProfitMaxDetails + 1)
            .ToListAsync();

        if (details.Count > OrderProfitMaxDetails)
        {
            throw new BusinessException(
                $"订单利润暂估表超出报告上限：范围内非删除订单明细超过 {OrderProfitMaxDetails} 条"
                + "（fail closed，不返回任何行或金额）",
                ErrorCodes.RuleConflict);
        }

        // 4) 固定批量查询解析商品 / 客户（各一次整表按 Id 集合查询，无逐单查库）；
        //    商品只取未删除，用于「当前价估算」——商品缺失 / 已删除时估算为未知，绝不回落为 0
        var productIds = details.Select(d => d.ProductId).Distinct().ToList();
        var products = await _db.BaseProducts
            .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, p => p);

        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

        // 5) 按已稳定排序的订单头映射（保留 SQL 端 OrderDate 降序、Id 降序顺序）：
        //    销售额保留订单原币 TotalAmount；成本 / 利润 / 利润率为未知（null）；
        //    「当前价估算」仅作独立口径呈现，明细 / 商品缺失或已删除时为 null 并给出显式原因
        var result = new List<ReportDtos.OrderProfitItem>();
        foreach (var order in orders)
        {
            var orderDetails = details.Where(d => d.SalesOrderId == order.Id).ToList();
            var estimate = BuildOrderProfitCurrentPriceEstimate(orderDetails, products);
            result.Add(new ReportDtos.OrderProfitItem
            {
                OrderId = order.Id,
                CustomerId = order.CustomerId,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                CustomerName = customers.TryGetValue(order.CustomerId, out var name) ? name : string.Empty,
                Currency = OrderProfitCurrencyCode(order.Currency),
                CurrencyLabel = OrderProfitCurrencyLabel(order.Currency),
                SalesAmount = order.TotalAmount,
                SalesAmountLabel = OrderProfitSalesAmountLabel,
                CostAmount = null,
                Profit = null,
                ProfitRate = null,
                CostEvidence = OrderProfitCostEvidence,
                ProfitEvidence = OrderProfitProfitEvidence,
                CurrentPriceEstimate = estimate.Estimate,
                CurrentPriceEstimateLabel = OrderProfitCurrentPriceEstimateLabel,
                CurrentPriceEstimateReason = estimate.Reason
            });
        }
        return result;
    }

    /// <summary>原币币种编码：未知取值（未定义枚举值）归入「未知币种」，绝不默认币种或推断汇率</summary>
    private static string OrderProfitCurrencyCode(Currency currency)
        => Enum.IsDefined(typeof(Currency), currency) ? currency.ToString() : UnknownCurrencyGroup;

    /// <summary>原币币种标签（如「USD 美元」）；未知取值归入「未知币种」</summary>
    private static string OrderProfitCurrencyLabel(Currency currency) => currency switch
    {
        Currency.CNY => "CNY 人民币",
        Currency.USD => "USD 美元",
        Currency.EUR => "EUR 欧元",
        Currency.HKD => "HKD 港币",
        Currency.GBP => "GBP 英镑",
        Currency.JPY => "JPY 日元",
        _ => UnknownCurrencyGroup
    };

    /// <summary>
    /// 当前价估算（数量 × 商品当前 CostPrice，币种未知，仅估算）：
    /// 明细为空、明细缺少商品、或商品缺失 / 已删除时返回 null 并给出显式原因；商品存在且 CostPrice 为 0 时按已知 0 计入。
    /// </summary>
    private static (decimal? Estimate, string Reason) BuildOrderProfitCurrentPriceEstimate(
        List<SalesOrderDetail> orderDetails, Dictionary<long, BaseProduct> products)
    {
        if (orderDetails.Count == 0)
            return (null, OrderProfitEstimateMissingDetailReason);

        if (orderDetails.Any(d => d.ProductId <= 0))
            return (null, OrderProfitEstimateIncompleteDetailReason);

        if (orderDetails.Any(d => !products.ContainsKey(d.ProductId)))
            return (null, OrderProfitEstimateMissingProductReason);

        var estimate = orderDetails.Sum(d => d.Quantity * products[d.ProductId].CostPrice);
        return (estimate, string.Empty);
    }
}
