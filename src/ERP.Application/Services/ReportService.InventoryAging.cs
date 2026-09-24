using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 库存库龄与成本估值报表（ERP-034，只读派生）。
/// <para>主表为库存行 <c>Stocks</c>（现存量是权威库存数量），库龄分层由库存流水 <c>StockMovements</c> 按 FIFO 派生：
/// 入库流水形成「层」，出库流水按最早的分层消耗，剩余分层即现存量的库龄依据；红字冲销按
/// <c>ReversalOfMovementId</c> 权威配对（入库冲销扣回原层、出库冲销按原出库日期回补），原流水不删除、不改写。</para>
/// <para>没有台账分层依据的数量（流水起点之前的历史库存）单列为「库龄未知」，不臆造入库日期、也不放进任何分层；
/// 估值只使用库存行持久化的移动加权平均成本与库存金额（ERP-009），成本依据缺失时数量与金额一律记为未知（null）。</para>
/// <para>查询约束：基础行在数据库内排序 + 分页（有界），名称与台账事实只按「本页 keys」批量查询（各一次），
/// 正常报表使用下不存在逐行查库。本报表只读：不落库、不改库存、不改单据状态、不新增或修改任何表列。</para>
/// </summary>
public partial class ReportService
{
    /// <summary>库存库龄与成本估值报表（ERP-034）</summary>
    public async Task<ReportDtos.InventoryAgingReport> GetInventoryAgingReportAsync(
        ReportDtos.InventoryAgingReportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();
        var asOf = query.AsOfDate!.Value;                 // 已归一化为日期
        var asOfExclusive = asOf.AddDays(1);              // 截止日期当天（含时间部分）也算在口径内

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
            .Select(s => new { s.WarehouseId, s.ProductId, s.Quantity, s.AverageCost, s.TotalCost })
            .ToListAsync(cancellationToken);

        // 2) 本页 keys：名称解析用两条有界批量查询（仓库名 / 商品信息），不做逐行查库
        var warehouseIds = pageStocks.Select(s => s.WarehouseId).Distinct().ToList();
        var productIds = pageStocks.Select(s => s.ProductId).Distinct().ToList();
        var warehouses = await LoadWarehouseNamesAsync(warehouseIds, cancellationToken);
        var products = await LoadProductInfosAsync(productIds, cancellationToken);

        // 3) 台账事实（只针对本页 keys）：截止日期之前（含当天）的全部流水，单条查询按「移动日期 + 流水 Id」读出，
        //    FIFO 分层在内存中派生；截止日期之后的流水既不进入口径、也不被删除
        var facts = productIds.Count == 0
            ? new List<AgingMovementFact>()
            : (await _db.StockMovements.AsNoTracking()
                .Where(m => !m.IsDeleted && m.ProductId != null
                            && productIds.Contains(m.ProductId.Value) && warehouseIds.Contains(m.WarehouseId)
                            && m.MovementDate < asOfExclusive)
                .OrderBy(m => m.MovementDate).ThenBy(m => m.Id)
                .Select(m => new
                {
                    m.Id,
                    m.WarehouseId,
                    m.ProductId,
                    m.MovementDate,
                    m.Direction,
                    m.Quantity,
                    m.IsReversal,
                    m.ReversalOfMovementId
                })
                .ToListAsync(cancellationToken))
              .Select(x => new AgingMovementFact(x.Id, x.WarehouseId, x.ProductId!.Value, x.MovementDate,
                  x.Direction, x.Quantity, x.IsReversal, x.ReversalOfMovementId))
              .ToList();

        var factsByKey = facts.GroupBy(f => (f.WarehouseId, f.ProductId))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AgingMovementFact>)g.ToList());

        // 4) 组装（本页内存计算，不再查库）；未知一律用 null / 明确的证据状态表达，绝不回落为 0 或臆造日期
        var items = new List<ReportDtos.InventoryAgingItem>(pageStocks.Count);
        foreach (var row in pageStocks)
        {
            factsByKey.TryGetValue((row.WarehouseId, row.ProductId), out var keyFacts);
            products.TryGetValue(row.ProductId, out var product);

            var aging = BuildAgingLayers(keyFacts ?? Array.Empty<AgingMovementFact>());
            var ledgerQuantity = aging.Layers.Sum(l => l.Remaining);           // 台账分层合计
            var current = row.Quantity > 0m ? row.Quantity : 0m;
            var unknownAgeQuantity = current > ledgerQuantity ? current - ledgerQuantity : 0m;
            var ledgerDeficit = ledgerQuantity > current ? ledgerQuantity - current : 0m;
            if (ledgerDeficit > 0m) ConsumeFifo(aging.Layers, ledgerDeficit);   // FIFO 口径：从最早分层扣减

            var buckets = BuildEmptyBuckets();
            foreach (var layer in aging.Layers)
            {
                if (layer.Remaining <= 0m) continue;
                var ageDays = (int)(asOf - layer.Date.Date).TotalDays;         // 已按 <= 截止日期截断，不会为负
                FindBucket(buckets, InventoryAgingSemantics.BucketOf(ageDays)).Quantity += layer.Remaining;
            }

            var knownAgedQuantity = buckets.Sum(b => b.Quantity);
            var costKnown = row.AverageCost > 0m && row.TotalCost > 0m;
            var unknownAgeAmount = costKnown
                ? ApportionAmounts(buckets, unknownAgeQuantity, row.AverageCost, row.TotalCost)
                : (decimal?)null;
            var evidenceStatus = unknownAgeQuantity <= 0m
                ? InventoryAgingSemantics.EvidenceFull
                : (knownAgedQuantity <= 0m
                    ? InventoryAgingSemantics.EvidenceNone
                    : InventoryAgingSemantics.EvidencePartial);

            items.Add(new ReportDtos.InventoryAgingItem
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
                KnownAgedQuantity = knownAgedQuantity,
                UnknownAgeQuantity = unknownAgeQuantity,
                EvidenceStatus = evidenceStatus,
                AverageCost = row.AverageCost,
                CostStatus = costKnown ? InventoryAgingSemantics.CostKnown : InventoryAgingSemantics.CostUnknown,
                AuthoritativeAmount = costKnown ? row.TotalCost : null,
                AgedAmount = costKnown ? buckets.Sum(b => b.Amount ?? 0m) : null,
                UnknownAgeAmount = unknownAgeAmount,
                UnknownCostQuantity = costKnown ? 0m : current,
                LedgerDeficitQuantity = ledgerDeficit,
                UnpairedQuantity = aging.UnpairedQuantity,
                ReversalCount = aging.ReversalCount,
                Buckets = buckets,
                Note = BuildAgingNote(evidenceStatus, unknownAgeQuantity, ledgerDeficit, aging.UnpairedQuantity,
                    aging.ReversalCount, row.Quantity, costKnown, asOf)
            });
        }

        return BuildAgingReport(query, asOf, total, items);
    }

    /// <summary>组装报表结果（合计、分层合计与计数只统计本页，行序与分页顺序一致）</summary>
    private static ReportDtos.InventoryAgingReport BuildAgingReport(ReportDtos.InventoryAgingReportQuery query,
        DateTime asOf, int total, List<ReportDtos.InventoryAgingItem> items)
    {
        var pageBuckets = BuildEmptyBuckets();
        var bucketAmountKnown = new bool[pageBuckets.Count];
        foreach (var item in items)
        {
            for (var i = 0; i < pageBuckets.Count; i++)
            {
                pageBuckets[i].Quantity += item.Buckets[i].Quantity;
                if (item.Buckets[i].Amount.HasValue)
                {
                    pageBuckets[i].Amount = (pageBuckets[i].Amount ?? 0m) + item.Buckets[i].Amount!.Value;
                    bucketAmountKnown[i] = true;
                }
            }
        }

        for (var i = 0; i < pageBuckets.Count; i++)
        {
            // 本页相关行都没有成本依据时，分层金额是「未知」（null）而不是 0
            if (!bucketAmountKnown[i]) pageBuckets[i].Amount = null;
        }

        var knownCostItems = items.Where(i => i.CostStatus == InventoryAgingSemantics.CostKnown).ToList();
        return new ReportDtos.InventoryAgingReport
        {
            AsOfDate = asOf,
            OnlyPositiveQuantity = query.OnlyPositiveQuantity,
            WarehouseId = query.WarehouseId,
            ProductId = query.ProductId,
            Keyword = query.Keyword ?? string.Empty,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            CostCurrency = InventoryAgingSemantics.CostCurrency,
            PageCurrentQuantity = items.Sum(i => i.CurrentQuantity),
            PageKnownAgedQuantity = items.Sum(i => i.KnownAgedQuantity),
            PageUnknownAgeQuantity = items.Sum(i => i.UnknownAgeQuantity),
            PageAuthoritativeAmount = knownCostItems.Count == 0
                ? null
                : knownCostItems.Sum(i => i.AuthoritativeAmount ?? 0m),
            PageUnknownCostQuantity = items.Sum(i => i.UnknownCostQuantity),
            KnownCostCount = knownCostItems.Count,
            UnknownCostCount = items.Count - knownCostItems.Count,
            FullEvidenceCount = items.Count(i => i.EvidenceStatus == InventoryAgingSemantics.EvidenceFull),
            PartialEvidenceCount = items.Count(i => i.EvidenceStatus == InventoryAgingSemantics.EvidencePartial),
            NoEvidenceCount = items.Count(i => i.EvidenceStatus == InventoryAgingSemantics.EvidenceNone),
            PageBuckets = pageBuckets,
            Items = items
        };
    }

    /// <summary>台账事实投影（FIFO 分层输入：只取派生库龄所需的最小列）</summary>
    private sealed record AgingMovementFact(long Id, long WarehouseId, long ProductId, DateTime MovementDate,
        int Direction, decimal Quantity, bool IsReversal, long? ReversalOfMovementId);

    /// <summary>FIFO 分层：一层 = 一次权威入库（或一次「出库红字冲销」回补）形成的数量与它的移动日期</summary>
    private sealed class AgingLayer
    {
        /// <summary>形成该层的流水 Id（出库冲销回补时取被冲销原流水的 Id，配对语义唯一）</summary>
        public long OriginMovementId { get; init; }

        /// <summary>层的库龄日期（= 原入库流水的移动日期）</summary>
        public DateTime Date { get; init; }

        /// <summary>该层剩余数量</summary>
        public decimal Remaining { get; set; }
    }

    /// <summary>某个仓库 + 商品在本次口径内的一次 FIFO 分层结果</summary>
    private sealed record AgingLayers(IReadOnlyList<AgingLayer> Layers, decimal UnpairedQuantity, int ReversalCount);

    /// <summary>
    /// 按台账事实派生 FIFO 分层（纯内存，不查库、不写库）：
    /// 入库（方向 +1）形成新层；出库（方向 -1）按 FIFO 消耗最早的分层；
    /// 红字冲销按 <c>ReversalOfMovementId</c> 权威配对——冲销一张入库时扣回该入库自己的层，
    /// 冲销一张出库时按「原出库的移动日期与数量」回补一层（数量与库龄都回到原状态）；
    /// 原流水不可见（已删除或不在口径内）时按流水自身方向兜底参与分层，不猜测配对关系。
    /// </summary>
    private static AgingLayers BuildAgingLayers(IReadOnlyList<AgingMovementFact> movements)
    {
        var layers = new List<AgingLayer>();
        var layerByMovementId = new Dictionary<long, AgingLayer>();
        var originals = new Dictionary<long, AgingMovementFact>();
        foreach (var movement in movements) originals[movement.Id] = movement;   // 异常重复 Id 取最后一条，不抛异常

        var unpaired = 0m;
        var reversalCount = 0;

        foreach (var m in movements)
        {
            if (m.IsReversal && m.ReversalOfMovementId.HasValue
                && originals.TryGetValue(m.ReversalOfMovementId.Value, out var original))
            {
                reversalCount++;
                if (original.Direction > 0)
                {
                    // 红字冲销一张入库：优先扣回该入库自己的分层；该分层已被后续出库消耗的部分按 FIFO 扣减
                    var taken = 0m;
                    if (layerByMovementId.TryGetValue(original.Id, out var layer) && layer.Remaining > 0m)
                    {
                        taken = Math.Min(layer.Remaining, m.Quantity);
                        layer.Remaining -= taken;
                    }

                    unpaired += ConsumeFifo(layers, m.Quantity - taken);
                }
                else
                {
                    AddLayer(layers, layerByMovementId, new AgingLayer
                    {
                        OriginMovementId = original.Id,
                        Date = original.MovementDate,
                        Remaining = m.Quantity
                    });
                }

                continue;
            }

            if (m.Direction > 0)
            {
                AddLayer(layers, layerByMovementId, new AgingLayer
                {
                    OriginMovementId = m.Id,
                    Date = m.MovementDate,
                    Remaining = m.Quantity
                });
            }
            else
            {
                unpaired += ConsumeFifo(layers, m.Quantity);
            }
        }

        return new AgingLayers(layers, unpaired, reversalCount);
    }

    /// <summary>登记一层（并记录「形成该层的流水 Id → 层」映射，供红字冲销按 ReversalOfMovementId 权威配对）</summary>
    private static void AddLayer(List<AgingLayer> layers, Dictionary<long, AgingLayer> layerByMovementId,
        AgingLayer layer)
    {
        layers.Add(layer);
        layerByMovementId[layer.OriginMovementId] = layer;
    }

    /// <summary>
    /// FIFO 消耗：按「层的日期（同日按来源流水 Id）」从最早的分层开始扣减；
    /// 返回无法扣减的余额（流水起点之前的入库，缺证据），由调用方留痕而不臆造分层。
    /// </summary>
    private static decimal ConsumeFifo(IReadOnlyList<AgingLayer> layers, decimal quantity)
    {
        var remaining = quantity;
        foreach (var layer in layers.OrderBy(l => l.Date).ThenBy(l => l.OriginMovementId))
        {
            if (remaining <= 0m) break;
            if (layer.Remaining <= 0m) continue;
            var taken = Math.Min(layer.Remaining, remaining);
            layer.Remaining -= taken;
            remaining -= taken;
        }

        return remaining > 0m ? remaining : 0m;
    }

    /// <summary>库龄分层骨架（固定 5 格：0-30 / 31-60 / 61-90 / 91-180 / 180 天以上，顺序与口径常量一致）</summary>
    private static List<ReportDtos.InventoryAgeBucketItem> BuildEmptyBuckets() =>
        InventoryAgingSemantics.BucketKeys.Select(key => new ReportDtos.InventoryAgeBucketItem
        {
            Key = key,
            Label = InventoryAgingSemantics.BucketLabel(key),
            MinDays = InventoryAgingSemantics.BucketMinDays(key),
            MaxDays = InventoryAgingSemantics.BucketMaxDays(key)
        }).ToList();

    /// <summary>按分层键取回骨架中的那一格（键来自同一套口径常量，不会缺失）</summary>
    private static ReportDtos.InventoryAgeBucketItem FindBucket(List<ReportDtos.InventoryAgeBucketItem> buckets,
        string key) => buckets.First(b => string.Equals(b.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// 分层金额分摊：按行持久化的移动加权平均成本计算「5 个库龄分层 + 库龄未知」各格金额（金额 4 位小数），
    /// 并把取整残差并入数量最多的一格，使「分层金额合计 + 库龄未知金额 = 行权威金额（Stocks.TotalCost）」恒成立。
    /// </summary>
    /// <returns>库龄未知数量的金额</returns>
    private static decimal ApportionAmounts(List<ReportDtos.InventoryAgeBucketItem> buckets,
        decimal unknownAgeQuantity, decimal averageCost, decimal authoritativeAmount)
    {
        var quantities = new decimal[buckets.Count + 1];
        for (var i = 0; i < buckets.Count; i++) quantities[i] = buckets[i].Quantity;
        quantities[^1] = unknownAgeQuantity;

        var amounts = new decimal[quantities.Length];
        for (var i = 0; i < quantities.Length; i++)
        {
            amounts[i] = InventoryAgingSemantics.RoundAmount(quantities[i] * averageCost);
        }

        var largest = 0;
        for (var i = 1; i < quantities.Length; i++)
        {
            if (quantities[i] > quantities[largest]) largest = i;
        }

        amounts[largest] += authoritativeAmount - amounts.Sum();

        for (var i = 0; i < buckets.Count; i++) buckets[i].Amount = amounts[i];
        return amounts[^1];
    }

    /// <summary>行级口径说明：只写「缺什么 / 为什么不推断」，与派生逻辑同源</summary>
    private static string BuildAgingNote(string evidenceStatus, decimal unknownAgeQuantity, decimal ledgerDeficit,
        decimal unpairedQuantity, int reversalCount, decimal currentQuantity, bool costKnown, DateTime asOf)
    {
        var notes = new List<string>();

        if (evidenceStatus == InventoryAgingSemantics.EvidenceNone && currentQuantity > 0m)
        {
            notes.Add($"截止 {asOf:yyyy-MM-dd} 之前没有任何库存流水：全部现存量库龄未知（流水起点之前的历史库存），" +
                      "不臆造入库日期、也不放进任何分层。");
        }
        else if (unknownAgeQuantity > 0m)
        {
            notes.Add($"{unknownAgeQuantity:0.####} 的基础单位数量没有台账分层依据（流水起点之前的历史库存），" +
                      "单列为「库龄未知」，不臆造入库日期、也不放进任何分层。");
        }

        if (ledgerDeficit > 0m)
        {
            notes.Add($"台账分层合计大于现存量 {ledgerDeficit:0.####}：已按 FIFO 口径在最早的分层扣减，" +
                      "不新增、不改写任何流水。");
        }

        if (unpairedQuantity > 0m)
        {
            notes.Add($"{unpairedQuantity:0.####} 的出库（或红字）数量没有可扣减的台账分层" +
                      "（流水起点之前的入库），不臆造分层。");
        }

        if (reversalCount > 0)
        {
            notes.Add($"截止日期前有 {reversalCount} 条红字冲销流水，已按 ReversalOfMovementId 权威配对处理" +
                      "（入库冲销扣回原层、出库冲销按原出库日期回补），原流水未被删除或改写。");
        }

        if (currentQuantity <= 0m)
        {
            notes.Add("现存量 <= 0：库龄分层与金额仅按库存行与台账给出，不代表仍有库存占用。");
        }

        if (!costKnown)
        {
            notes.Add("库存行未建立成本依据（加权平均成本或库存金额为 0）：数量单列为成本未知，" +
                      "金额记为未知（null），不估算成本、也不按汇率换算。");
        }

        return string.Join(" ", notes);
    }

}

