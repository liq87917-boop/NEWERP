using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（一）：柜量统计、采购成本分析
/// 全部为只读查询，不修改任何业务数据。
/// </summary>
public partial class ReportService
{
    /// <summary>
    /// 柜量与装柜利用率统计（按柜号聚合「装柜清单」）
    /// 装载率按 40HQ = 68 m³ 基准估算；客户数 &gt; 1 判定为拼柜
    /// </summary>
    public async Task<List<ReportDtos.ContainerStatsItem>> GetContainerStatsAsync(DateTime start, DateTime end)
    {
        const decimal baseVolume = 68m;   // 40HQ 基准容积（m³）

        var lists = await _db.ContainerLoadingLists
            .Where(x => !x.IsDeleted && x.LoadingDate >= start && x.LoadingDate <= end)
            .ToListAsync();
        if (lists.Count == 0) return new List<ReportDtos.ContainerStatsItem>();

        return lists
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ContainerNo) ? "(未填柜号)" : x.ContainerNo)
            .Select(g =>
            {
                var customers = g.Select(x => x.CustomerId).Distinct().Count();
                var volume = g.Sum(x => x.TotalVolume);
                return new ReportDtos.ContainerStatsItem
                {
                    ContainerNo = g.Key,
                    LoadingDate = g.Max(x => x.LoadingDate),
                    CustomerCount = customers,
                    TotalCartons = g.Sum(x => x.TotalCartons),
                    TotalWeight = g.Sum(x => x.TotalWeight),
                    TotalVolume = volume,
                    Utilization = volume > 0 ? Math.Round(volume / baseVolume * 100, 2) : 0,
                    TypeText = customers > 1 ? "拼柜" : "整柜"
                };
            })
            .OrderByDescending(x => x.LoadingDate)
            .ToList();
    }

    /// <summary>采购成本分析（按供应商聚合采购订单，排除已取消/已驳回）</summary>
    public async Task<List<ReportDtos.PurchaseCostItem>> GetPurchaseCostAsync(DateTime start, DateTime end)
    {
        var orders = await _db.PurchaseOrders
            .Where(o => !o.IsDeleted && o.OrderDate >= start && o.OrderDate <= end
                        && o.Status != DocumentStatus.Cancelled && o.Status != DocumentStatus.Rejected)
            .ToListAsync();
        if (orders.Count == 0) return new List<ReportDtos.PurchaseCostItem>();

        var supplierIds = orders.Select(o => o.SupplierId).Distinct().ToList();
        var suppliers = await _db.BaseSuppliers
            .Where(s => supplierIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s);

        return orders
            .GroupBy(o => o.SupplierId)
            .Select(g =>
            {
                suppliers.TryGetValue(g.Key, out var sup);
                var total = g.Sum(x => x.TotalAmount);
                var count = g.Count();
                return new ReportDtos.PurchaseCostItem
                {
                    SupplierName = sup?.SupplierName ?? ("供应商#" + g.Key),
                    SupplierType = sup?.SupplierType ?? string.Empty,
                    OrderCount = count,
                    TotalAmount = total,
                    AvgAmount = count > 0 ? Math.Round(total / count, 2) : 0,
                    LastOrderDate = g.Max(x => x.OrderDate)
                };
            })
            .OrderByDescending(x => x.TotalAmount)
            .ToList();
    }
}
