using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 报表服务：客户出货量统计、业务员产值
/// </summary>
public partial class ReportService
{
    /// <summary>客户出货量统计表允许的日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int CustomerShipmentMaxDateRangeDays = 366;

    /// <summary>客户出货量统计表订单头读取上限（范围内已审核销售订单）：500 张</summary>
    private const int CustomerShipmentMaxOrders = 500;

    /// <summary>客户出货量统计表明细读取上限（范围内非删除订单明细）：10000 条</summary>
    private const int CustomerShipmentMaxDetails = 10000;

    /// <summary>客户出货量统计表数量口径证据标签：已审核订单明细数量，非实际出库 / 装柜数量</summary>
    public const string CustomerShipmentQuantityLabel = "已审核订单明细数量合计（非实际出库/装柜数量）";

    /// <summary>客户出货量统计表金额口径证据标签：已审核订单金额（原币），非实际收款金额</summary>
    public const string CustomerShipmentAmountLabel = "已审核订单金额合计（原币，非实际收款金额）";

    /// <summary>
    /// 客户出货量统计表（只读派生）：仅统计已审核、未删除、当前账号数据范围内的销售订单头，
    /// 再关联未删除明细按客户聚合已审核订单数量与金额。本表口径为「已审核销售订单证据」，
    /// 不是实际出库 / 装柜数量，也不是实际收款（跨币种 / 跨单位分组由依赖任务 ERP-228 另行修正）。
    /// 日期校验先于任何源读取；订单头在业务员数据范围之后做 501 行探测（500 张上限），
    /// 明细按 10001 条探测（10000 条上限），超出即 fail closed 且不返回任何行或金额。
    /// </summary>
    public async Task<List<ReportDtos.CustomerShipmentItem>> GetCustomerShipmentStatsAsync(
        DateTime start, DateTime end, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 日期校验先于任何源读取（fail closed，含溢出防护）
        var startDate = start.Date;
        var endDate = end.Date;
        if (endDate < startDate)
            throw new BusinessException("客户出货量统计表的结束日期不能早于开始日期", ErrorCodes.InvalidParameter);

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > CustomerShipmentMaxDateRangeDays)
            throw new BusinessException(
                $"客户出货量统计表的日期范围最多 {CustomerShipmentMaxDateRangeDays} 天（含首尾）",
                ErrorCodes.InvalidParameter);

        // 结束日按排他上界处理（含首尾，即 < 结束日次日）；窗口已限制在 366 天内，此处加一不会溢出
        var endExclusive = endDate.AddDays(1);

        // 2) 订单头：已审核、未删除、日期窗口、业务员数据范围；稳定排序后有界读取（501 行探测 500 上限）
        var ordersQuery = _db.SalesOrders
            .Where(o => !o.IsDeleted
                        && o.Status == DocumentStatus.Approved
                        && o.OrderDate >= startDate
                        && o.OrderDate < endExclusive);
        ordersQuery = SalespersonDataScopeService.FilterByCustomer(ordersQuery, scope, o => o.CustomerId);

        var orders = await ordersQuery
            .OrderBy(o => o.CustomerId)
            .ThenBy(o => o.Id)
            .Take(CustomerShipmentMaxOrders + 1)
            .ToListAsync();

        if (orders.Count > CustomerShipmentMaxOrders)
        {
            throw new BusinessException(
                $"客户出货量统计表超出报告上限：范围内已审核销售订单超过 {CustomerShipmentMaxOrders} 张"
                + "（fail closed，不返回任何行或金额）",
                ErrorCodes.RuleConflict);
        }

        // 3) 明细：仅未删除且属于已读取订单头；有界读取（10001 条探测 10000 上限）
        var orderIds = orders.Select(o => o.Id).ToList();
        var details = await _db.SalesOrderDetails
            .Where(d => orderIds.Contains(d.SalesOrderId) && !d.IsDeleted)
            .OrderBy(d => d.Id)
            .Take(CustomerShipmentMaxDetails + 1)
            .ToListAsync();

        if (details.Count > CustomerShipmentMaxDetails)
        {
            throw new BusinessException(
                $"客户出货量统计表超出报告上限：范围内非删除订单明细超过 {CustomerShipmentMaxDetails} 条"
                + "（fail closed，不返回任何行或金额）",
                ErrorCodes.RuleConflict);
        }

        var quantityByOrder = details
            .GroupBy(d => d.SalesOrderId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        // 4) 固定批量查询解析客户名称（仅按已限定范围内的客户 Id 一次查询；无逐单查库）
        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

        // 5) 按客户聚合：仅已审核、未删除、范围内订单的数量与金额；稳定排序（金额降序，客户 Id 升序平局）
        return orders
            .GroupBy(o => o.CustomerId)
            .Select(g => new ReportDtos.CustomerShipmentItem
            {
                CustomerId = g.Key,
                CustomerName = customers.TryGetValue(g.Key, out var name) ? name : string.Empty,
                OrderCount = g.Count(),
                TotalQuantity = g.Sum(o => quantityByOrder.TryGetValue(o.Id, out var q) ? q : 0),
                TotalAmount = g.Sum(o => o.TotalAmount),
                QuantityLabel = CustomerShipmentQuantityLabel,
                AmountLabel = CustomerShipmentAmountLabel
            })
            .OrderByDescending(x => x.TotalAmount)
            .ThenBy(x => x.CustomerId)
            .ToList();
    }

    /// <summary>业务员产值报表</summary>
    public async Task<List<ReportDtos.SalesmanOutputItem>> GetSalesmanOutputAsync(DateTime start, DateTime end)
    {
        var orders = await _db.SalesOrders
            .Where(o => !o.IsDeleted && o.OrderDate >= start && o.OrderDate <= end
                        && o.Status != DocumentStatus.Cancelled)
            .ToListAsync();

        var orderIds = orders.Select(o => o.Id).ToList();
        var details = await _db.SalesOrderDetails
            .Where(d => orderIds.Contains(d.SalesOrderId) && !d.IsDeleted)
            .ToListAsync();

        var productIds = details.Select(d => d.ProductId).Distinct().ToList();
        var products = await _db.BaseProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var salesmanIds = orders.Where(o => o.SalesmanId.HasValue)
            .Select(o => o.SalesmanId!.Value).Distinct().ToList();
        var employees = await _db.BaseEmployees
            .Where(e => salesmanIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.EmployeeName);

        var result = new List<ReportDtos.SalesmanOutputItem>();
        foreach (var g in orders.Where(o => o.SalesmanId.HasValue).GroupBy(o => o.SalesmanId!.Value))
        {
            var groupOrders = g.ToList();
            var groupOrderIds = groupOrders.Select(o => o.Id).ToList();
            var profit = details
                .Where(d => groupOrderIds.Contains(d.SalesOrderId))
                .Sum(d => d.Quantity * (products.TryGetValue(d.ProductId, out var p) ? p.SalePrice - p.CostPrice : 0));

            result.Add(new ReportDtos.SalesmanOutputItem
            {
                SalesmanId = g.Key,
                SalesmanName = employees.TryGetValue(g.Key, out var name) ? name : string.Empty,
                OrderCount = groupOrders.Count,
                TotalAmount = groupOrders.Sum(o => o.TotalAmount),
                TotalProfit = profit
            });
        }
        return result.OrderByDescending(x => x.TotalAmount).ToList();
    }
}
