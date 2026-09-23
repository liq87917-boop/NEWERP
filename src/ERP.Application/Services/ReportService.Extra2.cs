using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（二）：退税汇总、库存预警
/// 全部为只读查询，不修改任何业务数据。
/// </summary>
public partial class ReportService
{
    /// <summary>退税汇总（按退税所属期间聚合：记录数、出口额、可退/已退/未退税额）</summary>
    public async Task<List<ReportDtos.TaxRefundSummaryItem>> GetTaxRefundSummaryAsync()
    {
        var list = await _db.BaseTaxRefunds.Where(x => !x.IsDeleted).ToListAsync();
        return list
            .GroupBy(x => string.IsNullOrWhiteSpace(x.RefundPeriod) ? "(未填期间)" : x.RefundPeriod)
            .Select(g => new ReportDtos.TaxRefundSummaryItem
            {
                RefundPeriod = g.Key,
                RecordCount = g.Count(),
                DeclaredCount = g.Count(x => x.Status == "已申报"),
                RefundedCount = g.Count(x => x.Status == "已退税"),
                ExportAmount = g.Sum(x => x.ExportAmount),
                RefundableAmount = g.Sum(x => x.RefundableAmount),
                RefundedAmount = g.Sum(x => x.RefundedAmount),
                UnrefundedAmount = g.Sum(x => x.RefundableAmount) - g.Sum(x => x.RefundedAmount)
            })
            .OrderByDescending(x => x.RefundPeriod)
            .ToList();
    }

    /// <summary>
    /// 库存预警：商品设有「安全库存」或「库存上限」时才参与预警；
    /// 现存量低于安全库存 → 低于安全库存；高于上限 → 超出库存上限
    /// </summary>
    public async Task<List<ReportDtos.StockAlertItem>> GetStockAlertAsync()
    {
        var stocks = await _db.Stocks.Where(s => !s.IsDeleted).ToListAsync();
        if (stocks.Count == 0) return new List<ReportDtos.StockAlertItem>();

        var productIds = stocks.Select(s => s.ProductId).Distinct().ToList();
        var products = await _db.BaseProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var warehouseIds = stocks.Select(s => s.WarehouseId).Distinct().ToList();
        var warehouses = await _db.BaseWarehouses
            .Where(w => warehouseIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w);

        var result = new List<ReportDtos.StockAlertItem>();
        foreach (var s in stocks)
        {
            if (!products.TryGetValue(s.ProductId, out var p)) continue;
            var min = p.MinStock;
            var max = p.MaxStock;
            if (min <= 0 && max <= 0) continue;          // 未设置阈值不预警

            string? level = null;
            decimal diff = 0;
            if (min > 0 && s.Quantity < min) { level = "低于安全库存"; diff = min - s.Quantity; }
            else if (max > 0 && s.Quantity > max) { level = "超出库存上限"; diff = s.Quantity - max; }
            if (level is null) continue;

            warehouses.TryGetValue(s.WarehouseId, out var w);
            result.Add(new ReportDtos.StockAlertItem
            {
                ProductName = p.ProductName,
                Spec = p.Spec,
                Unit = p.Unit,
                WarehouseName = w?.WarehouseName ?? string.Empty,
                Quantity = s.Quantity,
                MinStock = min,
                MaxStock = max,
                Diff = diff,
                AlertLevel = level
            });
        }

        return result
            .OrderBy(x => x.AlertLevel)
            .ThenBy(x => x.ProductName)
            .ToList();
    }
}
