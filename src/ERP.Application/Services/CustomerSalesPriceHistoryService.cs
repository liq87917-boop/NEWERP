using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户销售订单价格历史（ERP-108，只读派生）：按商品聚合「已审核、未删除」订单的「未删除」明细，
/// 仅相同口径（客户 + 商品 + 规格 + 单位 + 币种）归为一组并保留原始单价与贸易条款，
/// 口径不一致的证据分组成行单列。只读、不写库，业务员数据范围是硬边界。
/// </summary>
public static class CustomerSalesPriceHistoryService
{
    public static async Task<CustomerSalesPriceHistoryView> QueryAsync(
        IErpDbContext db,
        CustomerSalesPriceHistoryQuery query,
        HashSet<long>? allowedCustomerIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var productId = CustomerSalesPriceHistoryRules.NormalizeProductId(query);
        var (page, pageSize) = CustomerSalesPriceHistoryRules.NormalizePaging(query);
        var (dateFrom, dateTo) = CustomerSalesPriceHistoryRules.NormalizeDateRange(query);

        var source = db.SalesOrderDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && d.ProductId == productId)
            .Join(
                db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted && o.Status == DocumentStatus.Approved),
                d => d.SalesOrderId,
                o => o.Id,
                (d, o) => new { Detail = d, Order = o });

        if (allowedCustomerIds is not null)
            source = source.Where(x => allowedCustomerIds.Contains(x.Order.CustomerId));
        if (query.CustomerId is > 0)
            source = source.Where(x => x.Order.CustomerId == query.CustomerId.Value);
        if (dateFrom is not null)
            source = source.Where(x => x.Order.OrderDate >= dateFrom.Value);
        if (dateTo is not null)
            source = source.Where(x => x.Order.OrderDate < dateTo.Value);

        var totalCount = await source.CountAsync(ct);

        var pageRows = await source
            .OrderBy(x => x.Order.OrderDate)
            .ThenBy(x => x.Order.Id)
            .ThenBy(x => x.Detail.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var names = await LoadCustomerNamesAsync(db, pageRows.Select(x => x.Order.CustomerId).Distinct().ToList(), ct);

        var ordered = new List<CustomerSalesPriceHistoryGroup>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in pageRows)
        {
            var currency = x.Order.Currency.ToString();
            var customerName = names.TryGetValue(x.Order.CustomerId, out var n) ? n : string.Empty;
            var key = CustomerSalesPriceHistoryRules.ComparisonKey(
                x.Order.CustomerId, x.Detail.ProductId, x.Detail.Spec, x.Detail.Unit, currency);

            if (!index.TryGetValue(key, out var idx))
            {
                idx = ordered.Count;
                index[key] = idx;
                ordered.Add(new CustomerSalesPriceHistoryGroup
                {
                    CustomerId = x.Order.CustomerId,
                    CustomerName = customerName,
                    ProductId = x.Detail.ProductId,
                    ProductName = x.Detail.ProductName,
                    Spec = x.Detail.Spec,
                    Unit = x.Detail.Unit,
                    Currency = currency,
                    GroupKey = key,
                    BasisText = CustomerSalesPriceHistoryRules.BasisText(
                        customerName, x.Detail.ProductName, x.Detail.Spec, x.Detail.Unit, currency),
                    Rows = new List<CustomerSalesPriceHistoryRow>(),
                });
            }

            ordered[idx].Rows.Add(new CustomerSalesPriceHistoryRow
            {
                OrderId = x.Order.Id,
                OrderNo = x.Order.OrderNo,
                OrderDate = x.Order.OrderDate,
                CustomerId = x.Order.CustomerId,
                CustomerName = customerName,
                ProductId = x.Detail.ProductId,
                ProductName = x.Detail.ProductName,
                Spec = x.Detail.Spec,
                Unit = x.Detail.Unit,
                Currency = currency,
                UnitPrice = x.Detail.UnitPrice,
                Quantity = x.Detail.Quantity,
                Amount = x.Detail.Amount,
                TradeTerms = x.Order.TradeTerms,
            });
        }

        foreach (var g in ordered)
            g.RowCount = g.Rows.Count;

        return new CustomerSalesPriceHistoryView
        {
            ProductId = productId,
            ProductName = pageRows.Select(x => x.Detail.ProductName).FirstOrDefault(n => n.Length > 0) ?? string.Empty,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Truncated = (page - 1) * pageSize + pageRows.Count < totalCount,
            GroupCount = ordered.Count,
            Groups = ordered,
            RuleText = CustomerSalesPriceHistoryRules.RuleText,
            EmptyText = totalCount == 0
                ? "没有符合筛选条件的已审核销售订单价格历史（或订单 / 明细已被软删除）"
                : string.Empty,
        };
    }

    private static async Task<Dictionary<long, string>> LoadCustomerNamesAsync(
        IErpDbContext db, List<long> customerIds, CancellationToken ct)
    {
        var map = new Dictionary<long, string>();
        if (customerIds.Count == 0)
            return map;

        var customers = await db.BaseCustomers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CustomerName })
            .ToListAsync(ct);

        foreach (var c in customers)
            map[c.Id] = c.CustomerName ?? string.Empty;
        return map;
    }
}
