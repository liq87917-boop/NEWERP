using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（一）：柜量统计、采购成本分析
/// 全部为只读查询，不修改任何业务数据。
/// </summary>
public partial class ReportService
{
    /// <summary>柜量与装柜利用率统计允许的日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int ContainerStatsMaxDateRangeDays = 366;

    /// <summary>柜量与装柜利用率统计装柜清单头读取上限（范围内已审核、未删除、授权范围装柜清单）：500 张</summary>
    private const int ContainerStatsMaxLists = 500;

    /// <summary>
    /// 柜量与装柜利用率统计证据桶（ERP-251，只读派生）：仅统计已审核、未删除、当前账号数据范围内的装柜清单头，
    /// 按「装柜日历日 × 精确原始非空白柜号」分桶（空白 / 纯空白柜号按装柜清单 Id 独立，绝不合并）；
    /// 签名持久化箱数 / 毛重 / 体积分别求和作为头证据；装载率与柜型恒为未知（无权威容积 / 整柜 / 满载证据）。
    /// 日期校验先于任何源读取；来源探测在业务员数据范围之后做 501 行探测（500 张上限），超出即 fail closed。
    /// 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL，不查询明细 / 商品 / 订柜 / 参与方。
    /// </summary>
    public async Task<List<ReportDtos.ContainerStatsItem>> GetContainerStatsAsync(
        DateTime start, DateTime end, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 日期校验先于任何源读取（fail closed，含溢出防护）
        var startDate = start.Date;
        var endDate = end.Date;
        if (endDate < startDate)
            throw new BusinessException("柜量与装柜利用率统计的结束日期不能早于开始日期", ErrorCodes.InvalidParameter);

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > ContainerStatsMaxDateRangeDays)
            throw new BusinessException(
                $"柜量与装柜利用率统计的日期范围最多 {ContainerStatsMaxDateRangeDays} 天（含首尾）",
                ErrorCodes.InvalidParameter);

        // 结束日按排他上界处理（含首尾，即 < 结束日次日）；窗口已限制在 366 天内，此处加一不会溢出
        var endExclusive = endDate.AddDays(1);

        // 2) 装柜清单头：已审核、未删除、日期窗口、业务员数据范围；稳定排序后做 501 行来源探测（500 上限）
        var listsQuery = _db.ContainerLoadingLists
            .Where(x => !x.IsDeleted
                        && x.Status == DocumentStatus.Approved
                        && x.LoadingDate >= startDate
                        && x.LoadingDate < endExclusive);
        listsQuery = SalespersonDataScopeService.FilterByCustomer(listsQuery, scope, x => x.CustomerId);

        var probe = await listsQuery
            .OrderBy(x => x.LoadingDate)
            .ThenBy(x => x.ContainerNo)
            .ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.LoadingDate,
                x.ContainerNo,
                x.CustomerId,
                x.TotalCartons,
                x.TotalWeight,
                x.TotalVolume
            })
            .Take(ContainerStatsMaxLists + 1)
            .ToListAsync();

        // 3) 超出 500 张授权范围内匹配装柜清单头即 fail closed，不返回任何行
        if (probe.Count > ContainerStatsMaxLists)
            throw new BusinessException(
                $"柜量与装柜利用率统计的授权范围内装柜清单超过 {ContainerStatsMaxLists} 张，请缩小日期范围后重试",
                ErrorCodes.RuleConflict);

        if (probe.Count == 0)
            return new List<ReportDtos.ContainerStatsItem>();

        // 4) 内存中按稳定证据桶聚合（仅装柜清单头，无逐行查库）
        var lists = probe.Select(x => new ContainerLoadingList
        {
            Id = x.Id,
            LoadingDate = x.LoadingDate,
            ContainerNo = x.ContainerNo,
            CustomerId = x.CustomerId,
            TotalCartons = x.TotalCartons,
            TotalWeight = x.TotalWeight,
            TotalVolume = x.TotalVolume
        }).ToList();

        return ContainerStatsEvidenceRules.BuildBuckets(lists).ToList();
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
