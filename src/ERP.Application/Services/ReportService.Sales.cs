using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 报表服务：客户出货量统计、业务员产值
/// </summary>
public partial class ReportService
{
    /// <summary>客户出货量统计表</summary>
    public async Task<List<ReportDtos.CustomerShipmentItem>> GetCustomerShipmentStatsAsync(DateTime start, DateTime end)
    {
        var orders = await _db.SalesOrders
            .Where(o => !o.IsDeleted && o.OrderDate >= start && o.OrderDate <= end
                        && o.Status != DocumentStatus.Cancelled)
            .ToListAsync();

        var orderIds = orders.Select(o => o.Id).ToList();
        var quantityByOrder = await _db.SalesOrderDetails
            .Where(d => orderIds.Contains(d.SalesOrderId) && !d.IsDeleted)
            .GroupBy(d => d.SalesOrderId)
            .Select(g => new { OrderId = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.OrderId, x => x.Qty);

        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

        return orders
            .GroupBy(o => o.CustomerId)
            .Select(g => new ReportDtos.CustomerShipmentItem
            {
                CustomerId = g.Key,
                CustomerName = customers.TryGetValue(g.Key, out var name) ? name : string.Empty,
                OrderCount = g.Count(),
                TotalQuantity = g.Sum(o => quantityByOrder.TryGetValue(o.Id, out var q) ? q : 0),
                TotalAmount = g.Sum(o => o.TotalAmount)
            })
            .OrderByDescending(x => x.TotalAmount)
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
