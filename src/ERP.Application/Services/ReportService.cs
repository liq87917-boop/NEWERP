using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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

    /// <summary>订单利润暂估表允许的日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int OrderProfitMaxDateRangeDays = 366;

    /// <summary>订单利润暂估表单次最多返回的订单头数（有界：读取 501 探测 500 上限）</summary>
    private const int OrderProfitMaxOrders = 500;

    /// <summary>订单利润暂估表单次最多关联的非删除明细数（有界：读取 10001 探测 10000 上限）</summary>
    private const int OrderProfitMaxDetails = 10000;

    /// <summary>
    /// 订单利润暂估表（销售额 - 商品成本，只读派生）：仅统计已审核、未删除、当前账号数据范围内的销售订单头，
    /// 再关联未删除明细按商品成本价求和（成本 / 金额估算口径沿用既有实现，货币语义由依赖的货币任务处理）。
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

        // 4) 固定批量查询解析商品 / 客户（各一次整表按 Id 集合查询，无逐单查库）
        var productIds = details.Select(d => d.ProductId).Distinct().ToList();
        var products = await _db.BaseProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

        // 5) 按已稳定排序的订单头映射（保留 SQL 端 OrderDate 降序、Id 降序顺序）
        var result = new List<ReportDtos.OrderProfitItem>();
        foreach (var order in orders)
        {
            var orderDetails = details.Where(d => d.SalesOrderId == order.Id).ToList();
            var cost = orderDetails.Sum(d =>
                d.Quantity * (products.TryGetValue(d.ProductId, out var p) ? p.CostPrice : 0));
            var profit = order.TotalAmount - cost;
            result.Add(new ReportDtos.OrderProfitItem
            {
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                CustomerName = customers.TryGetValue(order.CustomerId, out var name) ? name : string.Empty,
                SalesAmount = order.TotalAmount,
                CostAmount = cost,
                Profit = profit,
                ProfitRate = order.TotalAmount == 0 ? 0 : Math.Round(profit / order.TotalAmount * 100, 2)
            });
        }
        return result;
    }
}
