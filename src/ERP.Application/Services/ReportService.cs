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

    /// <summary>商品销量排名榜（按销售出库明细聚合）</summary>
    public async Task<List<ReportDtos.ProductSalesRankItem>> GetProductSalesRankingAsync(DateTime start, DateTime end, int top)
    {
        var detailGroups = await _db.StockOutDetails
            .Where(d => !d.IsDeleted)
            .Join(_db.StockOuts.Where(o => !o.IsDeleted && o.StockOutDate >= start && o.StockOutDate <= end),
                d => d.StockOutId, o => o.Id, (d, o) => d)
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
                Rank = i + 1
            });
        }
        return result;
    }

    /// <summary>订单利润暂估表（销售额 - 商品成本）</summary>
    public async Task<List<ReportDtos.OrderProfitItem>> GetOrderProfitEstimateAsync(DateTime start, DateTime end)
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

        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

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
        return result.OrderByDescending(x => x.OrderDate).ToList();
    }
}
