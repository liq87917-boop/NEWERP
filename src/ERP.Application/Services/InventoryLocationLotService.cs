using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 库位 + 批次库存基础（ERP-096）服务：为入库 / 出库 / 调拨 / 退货四类移动统一校验
/// 「仓库库位 + 可选批次」身份，并从现有库存行与库存流水只读派生「库位级 / 批次级」余额，
/// 不改变既有商品级成本口径、不新增或修改任何表结构、不执行生产 SQL。
/// </summary>
public interface IInventoryLocationLotService
{
    /// <summary>
    /// 校验一次库存移动携带的「仓库库位 + 可选批次」身份并返回归一化结果。
    /// 仓库不存在、库位 / 批次超长、数量非正、调拨两仓相同等情况抛业务异常。
    /// </summary>
    Task<ValidatedMovementLocationLot> ValidateMovementAsync(MovementLocationLotInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只读派生「库位级 + 批次级」库存余额（可按仓库 / 商品过滤），并给出与既有商品级总量的对账结果。
    /// </summary>
    Task<LocationLotBalanceReport> GetLocationLotBalancesAsync(long? warehouseId, long? productId,
        CancellationToken cancellationToken = default);
}

/// <summary>库位 + 批次库存基础服务实现（只读派生 + 校验，不写库）。</summary>
public sealed class InventoryLocationLotService : IInventoryLocationLotService
{
    /// <summary>来源单据类型：采购入库单（与 <c>InventoryDocumentHelper.StockInType</c> 同值）</summary>
    private const string StockInType = "StockIn";

    /// <summary>来源单据类型：销售出库单</summary>
    private const string StockOutType = "StockOut";

    /// <summary>来源单据类型：仓库调拨单</summary>
    private const string StockTransferType = "StockTransfer";

    private readonly IErpDbContext _db;

    public InventoryLocationLotService(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ValidatedMovementLocationLot> ValidateMovementAsync(MovementLocationLotInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lotNo = InventoryLocationLotRules.NormalizeLot(input.LotNo);
        var locationCode = InventoryLocationLotRules.NormalizeLocationCode(input.LocationCode);
        InventoryLocationLotRules.EnsurePositiveQuantity(input.Quantity, input.ProductName);

        var fromWarehouse = await ResolveWarehouseAsync(
            InventoryLocationLotRules.RequireWarehouse(input.WarehouseId, "业务"), cancellationToken);

        var toWarehouseId = input.ToWarehouseId;
        var toWarehouseName = string.Empty;
        var toLocationCode = string.Empty;
        if (input.Kind == InventoryLocationLotMovementKind.Transfer)
        {
            toWarehouseId = InventoryLocationLotRules.RequireWarehouse(input.ToWarehouseId, "调入");
            InventoryLocationLotRules.EnsureDistinctWarehouses(input.WarehouseId, toWarehouseId);
            var toWarehouse = await ResolveWarehouseAsync(toWarehouseId, cancellationToken);
            toWarehouseName = toWarehouse.WarehouseName;
            toLocationCode = InventoryLocationLotRules.NormalizeLocationCode(input.ToLocationCode);

            // ERP-096 调拨守恒：数量守恒（调出 = 调入）、成本守恒（非负单价），
            // 且调出仓现存量必须足以覆盖调拨数量（拒绝负库存）。
            // 手工行（无商品 Id）无法核对现存量，跳过存量检查，仍保留数量 / 成本 / 两仓守恒。
            var sourceOnHand = input.ProductId.HasValue
                ? await GetSourceOnHandAsync(input.WarehouseId, input.ProductId.Value, cancellationToken)
                : input.Quantity;
            var conservation = InventoryLocationLotRules.EvaluateTransfer(
                input.WarehouseId, toWarehouseId, input.Quantity, input.UnitCost, sourceOnHand);
            if (!conservation.IsValid)
                throw BusinessException.InvalidParameter(conservation.Message);
        }

        return new ValidatedMovementLocationLot(
            input.Kind,
            input.WarehouseId,
            fromWarehouse.WarehouseName,
            locationCode,
            toWarehouseId,
            toWarehouseName,
            toLocationCode,
            lotNo,
            input.ProductId,
            input.ProductName,
            input.Quantity,
            input.UnitCost);
    }

    /// <summary>按仓库 Id 取仓库（不存在 / 已删除抛业务异常）。</summary>
    private async Task<BaseWarehouse> ResolveWarehouseAsync(long warehouseId, CancellationToken cancellationToken)
    {
        var warehouse = await _db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId && !w.IsDeleted, cancellationToken);
        return warehouse ?? throw BusinessException.InvalidParameter($"仓库 Id {warehouseId} 不存在或已删除");
    }

    /// <summary>取调出仓（仓库 + 商品）现存量；无商品或未建库存行时为 0。</summary>
    private async Task<decimal> GetSourceOnHandAsync(long warehouseId, long productId, CancellationToken cancellationToken)
    {
        return await _db.Stocks.AsNoTracking()
            .Where(s => !s.IsDeleted && s.WarehouseId == warehouseId && s.ProductId == productId)
            .Select(s => s.Quantity)
            .SumAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LocationLotBalanceReport> GetLocationLotBalancesAsync(long? warehouseId, long? productId,
        CancellationToken cancellationToken = default)
    {
        // 1) 库位级（仓库 + 商品）现存量：权威库存行
        var stocks = await _db.Stocks.AsNoTracking()
            .Where(s => !s.IsDeleted)
            .Where(s => !warehouseId.HasValue || s.WarehouseId == warehouseId.Value)
            .Where(s => !productId.HasValue || s.ProductId == productId.Value)
            .OrderBy(s => s.WarehouseId).ThenBy(s => s.ProductId).ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

        var warehouseIds = stocks.Select(s => s.WarehouseId).Distinct().ToList();
        var productIds = stocks.Select(s => s.ProductId).Distinct().ToList();

        var warehouseNames = await LoadWarehouseNamesAsync(warehouseIds, cancellationToken);
        var products = await LoadProductsAsync(productIds, cancellationToken);

        var locationLines = stocks.Select(s =>
        {
            warehouseNames.TryGetValue(s.WarehouseId, out var warehouseName);
            products.TryGetValue(s.ProductId, out var product);
            return new LocationBalanceLine(
                s.WarehouseId,
                string.IsNullOrWhiteSpace(warehouseName) ? $"仓库#{s.WarehouseId}" : warehouseName!,
                s.ProductId,
                product?.ProductCode ?? string.Empty,
                string.IsNullOrWhiteSpace(product?.ProductName) ? $"商品#{s.ProductId}" : product!.ProductName,
                product?.Spec ?? string.Empty,
                product?.Unit ?? string.Empty,
                s.Quantity,
                s.TotalCost,
                s.AverageCost);
        }).ToList();

        var productTotals = stocks.GroupBy(s => s.ProductId).Select(g =>
        {
            products.TryGetValue(g.Key, out var product);
            var quantity = g.Sum(s => s.Quantity);
            var totalCost = g.Sum(s => s.TotalCost);
            return new ProductBalanceTotal(
                g.Key,
                product?.ProductCode ?? string.Empty,
                string.IsNullOrWhiteSpace(product?.ProductName) ? $"商品#{g.Key}" : product!.ProductName,
                product?.Spec ?? string.Empty,
                product?.Unit ?? string.Empty,
                quantity,
                totalCost,
                quantity > 0 ? InventoryService.RoundCost(totalCost / quantity) : 0m);
        }).ToList();

        // 2) 批次级：有效库存流水（未冲销、非红字）结合来源单据明细 BatchNo 派生
        var movements = productIds.Count == 0 || warehouseIds.Count == 0
            ? new List<StockMovement>()
            : await _db.StockMovements.AsNoTracking()
                .Where(m => !m.IsDeleted && !m.IsReversal && !m.IsReversed && m.ProductId != null
                            && productIds.Contains(m.ProductId.Value) && warehouseIds.Contains(m.WarehouseId))
                .OrderBy(m => m.Id)
                .ToListAsync(cancellationToken);

        var lotByMovement = await ResolveLotsAsync(movements, cancellationToken);

        var movedByCell = movements
            .GroupBy(m => (m.WarehouseId, m.ProductId!.Value))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Direction * m.Quantity));

        var buckets = new Dictionary<(long WarehouseId, long ProductId, string LotNo), (decimal Quantity, decimal Amount)>();
        foreach (var movement in movements)
        {
            var lotNo = lotByMovement.TryGetValue(movement.Id, out var lot) ? lot ?? InventoryLocationLotRules.NoLot : InventoryLocationLotRules.NoLot;
            var key = (movement.WarehouseId, movement.ProductId!.Value, lotNo);
            var signedQuantity = movement.Direction * movement.Quantity;
            buckets[key] = buckets.TryGetValue(key, out var existing)
                ? (existing.Quantity + signedQuantity, existing.Amount + movement.Amount)
                : (signedQuantity, movement.Amount);
        }

        // 3) 对账兜底：现有库存与流水净额之差（历史库存 / 起点前数据）计入空批次桶，保证批次拆分与库位总量严格一致
        foreach (var stock in stocks)
        {
            var moved = movedByCell.TryGetValue((stock.WarehouseId, stock.ProductId), out var movedQty) ? movedQty : 0m;
            var residual = stock.Quantity - moved;
            if (Math.Abs(residual) <= InventoryLocationLotRules.ReconciliationTolerance) continue;

            var key = (stock.WarehouseId, stock.ProductId, InventoryLocationLotRules.NoLot);
            buckets[key] = buckets.TryGetValue(key, out var existing)
                ? (existing.Quantity + residual, existing.Amount)
                : (residual, 0m);
        }

        var lotLines = buckets
            .Select(kvp =>
            {
                products.TryGetValue(kvp.Key.ProductId, out var product);
                return new LotBalanceLine(
                    kvp.Key.WarehouseId,
                    kvp.Key.ProductId,
                    product?.ProductCode ?? string.Empty,
                    string.IsNullOrWhiteSpace(product?.ProductName) ? $"商品#{kvp.Key.ProductId}" : product!.ProductName,
                    kvp.Key.LotNo,
                    kvp.Value.Quantity,
                    kvp.Value.Amount);
            })
            .OrderBy(l => l.WarehouseId).ThenBy(l => l.ProductId).ThenBy(l => l.LotNo)
            .ToList();

        // 4) 对账：每仓每商品的批次数量之和必须等于库位行现存量
        var isReconciled = true;
        foreach (var stock in stocks)
        {
            var lotTotal = lotLines
                .Where(l => l.WarehouseId == stock.WarehouseId && l.ProductId == stock.ProductId)
                .Sum(l => l.Quantity);
            if (Math.Abs(lotTotal - stock.Quantity) > InventoryLocationLotRules.ReconciliationTolerance)
            {
                isReconciled = false;
                break;
            }
        }

        var note = "库位级余额直接来自现有库存行（仓库 + 商品），批次级余额由有效库存流水结合来源单据明细批次号派生；"
                   + "无法唯一归属批次的数量计入空批次桶，确保与商品级 / 库位级总量严格对账。"
                   + "本报表只读：不改变既有商品级成本口径，不新增或修改任何表结构。";

        return new LocationLotBalanceReport(productTotals, locationLines, lotLines, isReconciled, note);
    }

    private async Task<Dictionary<long, string>> LoadWarehouseNamesAsync(List<long> warehouseIds,
        CancellationToken cancellationToken)
    {
        if (warehouseIds.Count == 0) return new Dictionary<long, string>();
        var rows = await _db.BaseWarehouses.AsNoTracking()
            .Where(w => warehouseIds.Contains(w.Id))
            .Select(w => new { w.Id, w.WarehouseName })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.WarehouseName);
    }

    private async Task<Dictionary<long, BaseProduct>> LoadProductsAsync(List<long> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return new Dictionary<long, BaseProduct>();
        var rows = await _db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(p => p.Id);
    }

    /// <summary>
    /// 为每条有效流水解析批次号：仅当来源单据在该「单据 + 商品」单元格下<b>恰好一条</b>明细且批次号非空时，
    /// 才唯一归属到该批次；多批次 / 无批次来源（盘点 / 退货明细无批次列）一律返回 null（计入空批次桶），
    /// 不臆造归属。
    /// </summary>
    private async Task<Dictionary<long, string?>> ResolveLotsAsync(List<StockMovement> movements,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string?>();

        await ResolveLotsForAsync(result,
            movements.Where(m => m.SourceDocType == StockInType && m.ProductId != null),
            async ids =>
            {
                var rows = await _db.StockInDetails.AsNoTracking()
                    .Where(d => !d.IsDeleted && ids.Contains(d.StockInId))
                    .Select(d => new { d.StockInId, d.ProductId, d.BatchNo })
                    .ToListAsync(cancellationToken);
                return rows.Select(r => new LotDetailCell(r.StockInId, r.ProductId, r.BatchNo)).ToList();
            });

        await ResolveLotsForAsync(result,
            movements.Where(m => m.SourceDocType == StockOutType && m.ProductId != null),
            async ids =>
            {
                var rows = await _db.StockOutDetails.AsNoTracking()
                    .Where(d => !d.IsDeleted && ids.Contains(d.StockOutId))
                    .Select(d => new { d.StockOutId, d.ProductId, d.BatchNo })
                    .ToListAsync(cancellationToken);
                return rows.Select(r => new LotDetailCell(r.StockOutId, r.ProductId, r.BatchNo)).ToList();
            });

        await ResolveLotsForAsync(result,
            movements.Where(m => m.SourceDocType == StockTransferType && m.ProductId != null),
            async ids =>
            {
                var rows = await _db.StockTransferDetails.AsNoTracking()
                    .Where(d => !d.IsDeleted && ids.Contains(d.StockTransferId))
                    .Select(d => new { d.StockTransferId, d.ProductId, d.BatchNo })
                    .ToListAsync(cancellationToken);
                return rows.Select(r => new LotDetailCell(r.StockTransferId, r.ProductId ?? 0L, r.BatchNo)).ToList();
            });

        return result;
    }

    /// <summary>来源单据明细里用于解析批次的单元格（单据 Id + 商品 Id + 批次号）。</summary>
    private sealed record LotDetailCell(long DocId, long ProductId, string BatchNo);

    /// <summary>为一种来源单据类型的流水解析批次号（有界批量加载明细，避免逐行查库）。</summary>
    private static async Task ResolveLotsForAsync(
        Dictionary<long, string?> result,
        IEnumerable<StockMovement> movements,
        Func<List<long>, Task<List<LotDetailCell>>> loader)
    {
        var rows = movements.ToList();
        if (rows.Count == 0) return;

        var docIds = rows.Select(m => m.SourceDocId).Distinct().ToList();
        var details = await loader(docIds);

        var lotByCell = new Dictionary<(long DocId, long ProductId), string?>();
        foreach (var group in details.GroupBy(d => (d.DocId, d.ProductId)))
        {
            var cells = group.ToList();
            var nonEmpty = cells.Where(c => !string.IsNullOrWhiteSpace(c.BatchNo)).ToList();
            lotByCell[group.Key] = cells.Count == 1 && nonEmpty.Count == 1
                ? nonEmpty[0].BatchNo.Trim()
                : null;
        }

        foreach (var movement in rows)
        {
            result[movement.Id] = lotByCell.TryGetValue((movement.SourceDocId, movement.ProductId!.Value), out var lot)
                ? lot
                : null;
        }
    }
}
