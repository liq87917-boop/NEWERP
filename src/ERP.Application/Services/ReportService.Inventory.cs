using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 库存移动与呆滞报表（ERP-029，只读派生）。
/// <para>主表为库存行 <c>Stocks</c>（现存量是权威库存数量），移动口径全部取自库存流水 <c>StockMovements</c>（台账）：
/// 最后移动日期 / 窗口内出入库毛额 / 净额 / 停滞天数均可逐行回溯到台账；无台账的行以「未知」呈现，不臆造日期或比率。</para>
/// <para>查询约束：基础行在数据库内排序 + 分页（有界），名称与台账合计只按「本页 keys」批量查询，
/// 正常报表使用下不存在逐行查库。本报表只读：不落库、不改库存、不改单据状态、不新增或修改任何表列，也不做成本 / 金额估值。</para>
/// </summary>
public partial class ReportService
{
    /// <summary>库存移动与呆滞报表（ERP-029）</summary>
    public async Task<ReportDtos.InventoryMovementReport> GetInventoryMovementReportAsync(
        ReportDtos.InventoryMovementReportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();
        var asOf = query.AsOfDate!.Value;                 // 已归一化为日期
        var windowStart = query.WindowStart!.Value;
        var windowEnd = query.WindowEnd!.Value;
        var asOfExclusive = asOf.AddDays(1);              // 截止日期当天（含时间部分）也算在口径内
        var windowEndExclusive = windowEnd.AddDays(1);

        // 1) 基础行：库存行 + 筛选条件；排序与分页全部在数据库内完成（有界）
        var stocks = _db.Stocks.AsNoTracking().Where(s => !s.IsDeleted);
        if (query.WarehouseId.HasValue) stocks = stocks.Where(s => s.WarehouseId == query.WarehouseId.Value);
        if (query.ProductId.HasValue) stocks = stocks.Where(s => s.ProductId == query.ProductId.Value);
        if (query.OnlyPositiveQuantity) stocks = stocks.Where(s => s.Quantity > 0);
        if (query.Keyword is not null)
        {
            var keyword = query.Keyword;
            // EXISTS 子查询：关键字匹配商品资料的编码 / 名称，仍是单条 SQL，不做逐行查库
            stocks = stocks.Where(s => _db.BaseProducts.Any(p => p.Id == s.ProductId && !p.IsDeleted
                && (p.ProductCode.Contains(keyword) || p.ProductName.Contains(keyword))));
        }

        var total = await stocks.CountAsync(cancellationToken);
        var pageStocks = await stocks
            .OrderBy(s => s.WarehouseId).ThenBy(s => s.ProductId).ThenBy(s => s.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(s => new { s.WarehouseId, s.ProductId, s.Quantity })
            .ToListAsync(cancellationToken);

        // 2) 本页 keys：名称解析用两条有界批量查询（仓库名 / 商品信息），不做逐行查库
        var warehouseIds = pageStocks.Select(s => s.WarehouseId).Distinct().ToList();
        var productIds = pageStocks.Select(s => s.ProductId).Distinct().ToList();
        var warehouses = await LoadWarehouseNamesAsync(warehouseIds, cancellationToken);
        var products = await LoadProductInfosAsync(productIds, cancellationToken);

        // 3) 台账聚合（只针对本页 keys）：窗口内毛额 / 净额 / 行数 / 红字行数，以及截止日期前的最后移动日期
        var windowRows = productIds.Count == 0
            ? new List<MovementAggregate>()
            : (await _db.StockMovements.AsNoTracking()
                .Where(m => !m.IsDeleted && m.ProductId != null
                            && productIds.Contains(m.ProductId.Value) && warehouseIds.Contains(m.WarehouseId)
                            && m.MovementDate >= windowStart && m.MovementDate < windowEndExclusive)
                .GroupBy(m => new { m.WarehouseId, m.ProductId })
                .Select(g => new
                {
                    g.Key.WarehouseId,
                    g.Key.ProductId,
                    // 毛额：红字冲销流水本身带反方向，天然计入对侧（原流水 + 红字成对净额为 0，不二次扣减）
                    Inbound = g.Sum(m => m.Direction > 0 ? m.Quantity : 0m),
                    Outbound = g.Sum(m => m.Direction < 0 ? m.Quantity : 0m),
                    MovementCount = g.Count(),
                    ReversalCount = g.Sum(m => m.IsReversal ? 1 : 0)
                })
                .ToListAsync(cancellationToken))
                .Select(x => new MovementAggregate(x.WarehouseId, x.ProductId, x.Inbound, x.Outbound,
                    x.MovementCount, x.ReversalCount))
                .ToList();

        var historyRows = productIds.Count == 0
            ? new List<HistoryAggregate>()
            : (await _db.StockMovements.AsNoTracking()
                .Where(m => !m.IsDeleted && m.ProductId != null
                            && productIds.Contains(m.ProductId.Value) && warehouseIds.Contains(m.WarehouseId)
                            && m.MovementDate < asOfExclusive)
                .GroupBy(m => new { m.WarehouseId, m.ProductId })
                .Select(g => new
                {
                    g.Key.WarehouseId,
                    g.Key.ProductId,
                    LastMovementDate = g.Max(m => m.MovementDate)
                })
                .ToListAsync(cancellationToken))
                .Select(x => new HistoryAggregate(x.WarehouseId, x.ProductId, x.LastMovementDate))
                .ToList();

        var windowByKey = windowRows.Where(r => r.ProductId.HasValue)
            .ToDictionary(r => (r.WarehouseId, r.ProductId!.Value));
        var historyByKey = historyRows.Where(r => r.ProductId.HasValue)
            .ToDictionary(r => (r.WarehouseId, r.ProductId!.Value));

        // 4) 组装（本页内存计算，不再查库）；未知一律用 null / 明确的台账状态表达，绝不回落为 0 或臆造日期
        var items = new List<ReportDtos.InventoryMovementItem>(pageStocks.Count);
        foreach (var row in pageStocks)
        {
            windowByKey.TryGetValue((row.WarehouseId, row.ProductId), out var window);
            historyByKey.TryGetValue((row.WarehouseId, row.ProductId), out var history);
            products.TryGetValue(row.ProductId, out var product);

            var inbound = window?.Inbound ?? 0m;
            var outbound = window?.Outbound ?? 0m;
            var movementCount = window?.MovementCount ?? 0;
            var reversalCount = window?.ReversalCount ?? 0;
            var lastMovementDate = history?.LastMovementDate;
            var inactivityDays = lastMovementDate.HasValue
                ? (int)(asOf - lastMovementDate.Value.Date).TotalDays   // 已按 <= 截止日期截断，不会为负
                : (int?)null;

            var historyStatus = !lastMovementDate.HasValue
                ? InventoryMovementSemantics.HistoryNoHistory
                : (movementCount == 0
                    ? InventoryMovementSemantics.HistoryWindowEmpty
                    : InventoryMovementSemantics.HistoryLedger);
            var classification = historyStatus == InventoryMovementSemantics.HistoryNoHistory
                ? InventoryMovementSemantics.ClassUnknown
                : (inactivityDays!.Value >= query.InactiveDays
                    ? InventoryMovementSemantics.ClassStagnant
                    : InventoryMovementSemantics.ClassActive);

            items.Add(new ReportDtos.InventoryMovementItem
            {
                WarehouseId = row.WarehouseId,
                WarehouseName = warehouses.TryGetValue(row.WarehouseId, out var warehouseName)
                    ? warehouseName : string.Empty,
                ProductId = row.ProductId,
                ProductCode = product?.Code ?? string.Empty,
                ProductName = product?.Name ?? string.Empty,
                Spec = product?.Spec ?? string.Empty,
                Unit = product?.Unit ?? string.Empty,
                CurrentQuantity = row.Quantity,
                LastMovementDate = lastMovementDate,
                InboundQuantity = inbound,
                OutboundQuantity = outbound,
                NetQuantity = inbound - outbound,
                MovementCount = movementCount,
                ReversalCount = reversalCount,
                InactivityDays = inactivityDays,
                HistoryStatus = historyStatus,
                Classification = classification,
                Note = BuildNote(historyStatus, reversalCount, row.Quantity, asOf, windowStart, windowEnd)
            });
        }

        return new ReportDtos.InventoryMovementReport
        {
            AsOfDate = asOf,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            InactiveDays = query.InactiveDays,
            OnlyPositiveQuantity = query.OnlyPositiveQuantity,
            WarehouseId = query.WarehouseId,
            ProductId = query.ProductId,
            Keyword = query.Keyword ?? string.Empty,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            ActiveCount = items.Count(i => i.Classification == InventoryMovementSemantics.ClassActive),
            StagnantCount = items.Count(i => i.Classification == InventoryMovementSemantics.ClassStagnant),
            InsufficientHistoryCount = items.Count(i => i.HistoryStatus == InventoryMovementSemantics.HistoryNoHistory),
            WindowEmptyCount = items.Count(i => i.HistoryStatus == InventoryMovementSemantics.HistoryWindowEmpty),
            PageCurrentQuantity = items.Sum(i => i.CurrentQuantity),
            PageInboundQuantity = items.Sum(i => i.InboundQuantity),
            PageOutboundQuantity = items.Sum(i => i.OutboundQuantity),
            PageNetQuantity = items.Sum(i => i.NetQuantity),
            Items = items
        };
    }

    /// <summary>行级口径说明：只写「缺什么 / 为什么不推断」，与派生逻辑同源</summary>
    private static string BuildNote(string historyStatus, int reversalCount, decimal currentQuantity,
        DateTime asOf, DateTime windowStart, DateTime windowEnd)
    {
        var notes = new List<string>();
        if (historyStatus == InventoryMovementSemantics.HistoryNoHistory)
        {
            notes.Add($"截止 {asOf:yyyy-MM-dd} 之前没有任何库存流水：最后移动日期与停滞天数均为未知" +
                      "（历史库存或流水起点之前的数据），不臆造日期与比率。");
        }
        else if (historyStatus == InventoryMovementSemantics.HistoryWindowEmpty)
        {
            notes.Add($"窗口 {windowStart:yyyy-MM-dd} ~ {windowEnd:yyyy-MM-dd} 内没有台账流水：" +
                      "出入库合计是真实的 0（不是未知），最后移动日期取自窗口之外的台账。");
        }

        if (reversalCount > 0)
        {
            notes.Add($"窗口内 {reversalCount} 条红字冲销流水已按反方向计入毛额，" +
                      "原流水与其红字成对净额为 0，未做二次扣减。");
        }

        if (currentQuantity <= 0)
        {
            notes.Add("当前现存量 <= 0：呆滞分类仅按阈值给出，不代表仍有库存占用。");
        }

        return string.Join(" ", notes);
    }

    /// <summary>仓库名称（有界批量：只查本页 keys；仓库被删除或不存在时该键无值）</summary>
    private async Task<Dictionary<long, string>> LoadWarehouseNamesAsync(List<long> warehouseIds,
        CancellationToken cancellationToken)
    {
        if (warehouseIds.Count == 0) return new Dictionary<long, string>();
        var rows = await _db.BaseWarehouses.AsNoTracking()
            .Where(w => warehouseIds.Contains(w.Id) && !w.IsDeleted)
            .Select(w => new { w.Id, w.WarehouseName })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.WarehouseName);
    }

    /// <summary>商品信息（有界批量：只查本页 keys；商品被删除或不存在时该键无值，前端留空而不臆造名称）</summary>
    private async Task<Dictionary<long, ProductInfo>> LoadProductInfosAsync(List<long> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return new Dictionary<long, ProductInfo>();
        var rows = await _db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
            .Select(p => new { p.Id, p.ProductCode, p.ProductName, p.Spec, p.Unit })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => new ProductInfo(r.ProductCode, r.ProductName, r.Spec, r.Unit));
    }

    /// <summary>窗口内台账聚合行（仅本页 keys；商品 Id 分组键与数据库列同为可空，组装前已过滤空值）</summary>
    private sealed record MovementAggregate(long WarehouseId, long? ProductId, decimal Inbound, decimal Outbound,
        int MovementCount, int ReversalCount);

    /// <summary>截止日期前台账聚合行（仅本页 keys）</summary>
    private sealed record HistoryAggregate(long WarehouseId, long? ProductId, DateTime LastMovementDate);

    /// <summary>商品信息投影</summary>
    private sealed record ProductInfo(string Code, string Name, string Spec, string Unit);
}
