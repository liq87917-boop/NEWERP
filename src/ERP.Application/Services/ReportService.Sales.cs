using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
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
    /// 客户出货量统计表（只读派生）：仅统计已审核、未删除、当前账号数据范围内的销售订单头，按客户 × 原币分组；
    /// 已知币种按签名原币合计金额、未知 / 无效币种金额为 null 仅保留订单头计数；精确单位数量分组由非删除明细派生，
    /// 旧口径数量合计仅在明细证据完整且单一非空单位时可知（否则 null）。本表口径为「已审核销售订单证据」，
    /// 不是实际出库 / 装柜数量，也不是实际收款。日期校验先于任何源读取；订单头在业务员数据范围之后做 501 行探测
    /// （500 张上限），明细按 10001 条探测（10000 条上限），超出即 fail closed 且不返回任何行或金额。
    /// <see cref="CustomerShipmentFilterDto"/> 可选筛选（客户 Id / 原币币种）在业务员数据范围之后、501 订单头上限探测与
    /// 10001 明细探测之前与范围相交并过滤（参数化 EF 谓词）；省略筛选保持既有全部已知 / 未知币种证据。
    /// </summary>
    public async Task<List<ReportDtos.CustomerShipmentItem>> GetCustomerShipmentStatsAsync(
        DateTime start, DateTime end, SalespersonDataScope scope, CustomerShipmentFilterDto? filter = null)
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

        // 2.1) ERP-231 可选应用筛选：作用在业务员数据范围之后、501 订单头上限探测与 10001 明细探测之前（参数化 EF 谓词）
        ordersQuery = ApplyCustomerShipmentFilter(ordersQuery, filter);

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

        // 4) 固定批量查询解析客户名称（仅按已限定范围内的客户 Id 一次查询；无逐单查库）
        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

        // 5) 按客户 × 原币分组：金额仅在已知币种下按签名原币合计，未知 / 无效币种金额为 null 仅保留订单头计数；
        //    精确单位数量分组由非删除明细派生，旧口径数量合计仅在证据完整且单一非空单位时可知（否则 null）。
        //    稳定排序（客户 Id 升序，同客户按币种编码升序），绝不跨币种比较金额排序。
        var detailsByOrder = details
            .GroupBy(d => d.SalesOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return orders
            .GroupBy(o => o.CustomerId)
            .SelectMany(customerGroup =>
            {
                var customerName = customers.TryGetValue(customerGroup.Key, out var name) ? name : string.Empty;
                return customerGroup
                    .GroupBy(o => CustomerShipmentEvidenceRules.CurrencyGroupKey(o.Currency))
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(currencyGroup => BuildCustomerShipmentItem(
                        customerGroup.Key, customerName, currencyGroup.ToList(), detailsByOrder));
            })
            .OrderBy(x => x.CustomerId)
            .ThenBy(x => x.Currency, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 构造单个「客户 × 原币」分组行：金额只在已知币种下做签名原币小计，未知 / 无效币种为 null；
    /// 精确单位数量分组与旧口径数量合计（可空）由 <see cref="CustomerShipmentEvidenceRules"/> 派生。
    /// </summary>
    private static ReportDtos.CustomerShipmentItem BuildCustomerShipmentItem(
        long customerId,
        string customerName,
        List<SalesOrder> currencyOrders,
        Dictionary<long, List<SalesOrderDetail>> detailsByOrder)
    {
        var currencyKey = CustomerShipmentEvidenceRules.CurrencyGroupKey(currencyOrders[0].Currency);
        var isKnown = CustomerShipmentEvidenceRules.IsKnownCurrency(currencyOrders[0].Currency);

        var groupDetails = currencyOrders
            .SelectMany(o => detailsByOrder.TryGetValue(o.Id, out var d) ? d : new List<SalesOrderDetail>())
            .ToList();

        var unitGroups = CustomerShipmentEvidenceRules.BuildUnitGroups(groupDetails);
        var (totalQuantity, completenessReason) =
            CustomerShipmentEvidenceRules.BuildLegacyTotalQuantity(currencyOrders, detailsByOrder);

        return new ReportDtos.CustomerShipmentItem
        {
            CustomerId = customerId,
            CustomerName = customerName,
            Currency = currencyKey,
            CurrencyLabel = CustomerShipmentEvidenceRules.CurrencyGroupLabel(currencyKey),
            OrderCount = currencyOrders.Count,
            TotalAmount = isKnown ? (decimal?)currencyOrders.Sum(o => o.TotalAmount) : null,
            QuantityLabel = CustomerShipmentQuantityLabel,
            AmountLabel = CustomerShipmentAmountLabel,
            CurrencyEvidence = isKnown
                ? CustomerShipmentEvidenceRules.KnownCurrencyEvidence
                : CustomerShipmentEvidenceRules.UnknownCurrencyEvidence,
            TotalQuantity = totalQuantity,
            QuantityCompletenessReason = completenessReason,
            UnitGroups = unitGroups.ToList()
        };
    }

    /// <summary>
    /// 应用 ERP-231 可选筛选（在业务员数据范围之后、501 订单头上限探测与分页之前）：客户 Id 精确匹配、
    /// 原币币种已知枚举码精确匹配。筛选已由调用方规范化；全部为参数化 EF 谓词，非任意 SQL。
    /// </summary>
    private static IQueryable<SalesOrder> ApplyCustomerShipmentFilter(
        IQueryable<SalesOrder> source, CustomerShipmentFilterDto? filter)
    {
        if (filter is null)
            return source;

        if (filter.CustomerId is > 0)
            source = source.Where(o => o.CustomerId == filter.CustomerId.Value);

        if (!string.IsNullOrEmpty(filter.Currency)
            && Enum.TryParse<Currency>(filter.Currency, true, out var currency))
        {
            source = source.Where(o => o.Currency == currency);
        }

        return source;
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
