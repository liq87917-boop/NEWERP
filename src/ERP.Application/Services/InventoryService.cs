using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 一次库存移动的全部审计上下文（来源单据 + 仓库 + 商品）
/// </summary>
public sealed class InventoryMovementContext
{
    /// <summary>来源单据类型（实体名，如 StockAdjustment）</summary>
    public string SourceDocType { get; init; } = string.Empty;

    /// <summary>来源单据 Id</summary>
    public long SourceDocId { get; init; }

    /// <summary>来源单据号</summary>
    public string SourceDocNo { get; init; } = string.Empty;

    /// <summary>移动类型</summary>
    public InventoryMovementType MovementType { get; init; }

    /// <summary>仓库 Id</summary>
    public long WarehouseId { get; init; }

    /// <summary>仓库名称（冗余落入流水）</summary>
    public string WarehouseName { get; init; } = string.Empty;

    /// <summary>商品 Id</summary>
    public long? ProductId { get; init; }

    /// <summary>商品编码（冗余落入流水）</summary>
    public string ProductCode { get; init; } = string.Empty;

    /// <summary>商品名称（冗余落入流水）</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>规格</summary>
    public string Spec { get; init; } = string.Empty;

    /// <summary>单位</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>移动日期（缺省取今天）</summary>
    public DateTime? MovementDate { get; init; }

    /// <summary>备注</summary>
    public string Remark { get; init; } = string.Empty;
}

/// <summary>
/// 库存移动与成本服务（ERP-009）：所有库存增减都必须经过本服务，
/// 保证「库存数量、库存金额、库存流水」三者同步且可审计。
/// <para>成本口径：移动加权平均法（moving weighted average）——入库按入库成本加权，
/// 出库按移动后加权平均成本核减；成本单价与金额持久化在库存流水上，不随后续业务变动而改写。</para>
/// </summary>
public interface IInventoryService
{
    /// <summary>取（或按仓库 + 商品新建）库存行</summary>
    Task<Stock> GetOrCreateStockAsync(long warehouseId, long productId, CancellationToken cancellationToken = default);

    /// <summary>按来源单据取成本单价（来源入库/出库流水成本）；无来源或无流水时返回 0</summary>
    Task<decimal> ResolveSourceCostAsync(string sourceDocType, long sourceDocId, long? productId,
        CancellationToken cancellationToken = default);

    /// <summary>增加库存（入库侧：采购入库 / 调拨入库 / 退货入库 / 盘盈）并写入流水</summary>
    Task<StockMovement> IncreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default);

    /// <summary>减少库存（出库侧：调拨出库 / 采购退货 / 盘亏）并写入流水；库存不足时抛业务异常</summary>
    Task<StockMovement> DecreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default);

    /// <summary>按来源单据冲销全部未冲销流水（销审用）；返回新增的红字流水</summary>
    Task<IReadOnlyList<StockMovement>> ReverseAsync(string sourceDocType, long sourceDocId, string reason,
        CancellationToken cancellationToken = default);

    /// <summary>按来源单据查询流水（含已冲销与红字流水，按 Id 升序）</summary>
    Task<IReadOnlyList<StockMovement>> ListMovementsAsync(string sourceDocType, long sourceDocId,
        CancellationToken cancellationToken = default);

    /// <summary>统计来源单据的有效（未冲销、非红字）流水条数</summary>
    Task<int> CountActiveMovementsAsync(string sourceDocType, long sourceDocId,
        CancellationToken cancellationToken = default);
}

/// <summary>库存移动与成本服务实现（移动加权平均法）</summary>
public sealed class InventoryService : IInventoryService
{
    /// <summary>金额（库存金额 / 移动金额）保留位数</summary>
    private const int AmountDecimals = 4;

    /// <summary>成本单价保留位数</summary>
    private const int CostDecimals = 6;

    private readonly IErpDbContext _db;

    public InventoryService(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>金额取整（四舍五入；同一份单据重复计算得到相同结果，保证估价确定）</summary>
    public static decimal RoundAmount(decimal value) => Math.Round(value, AmountDecimals, MidpointRounding.AwayFromZero);

    /// <summary>成本单价取整</summary>
    public static decimal RoundCost(decimal value) => Math.Round(value, CostDecimals, MidpointRounding.AwayFromZero);

    /// <inheritdoc />
    public async Task<Stock> GetOrCreateStockAsync(long warehouseId, long productId,
        CancellationToken cancellationToken = default)
    {
        var stock = await _db.Stocks
            .FirstOrDefaultAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId && !s.IsDeleted,
                cancellationToken);
        if (stock is not null) return stock;

        // 同一次工作单元内（如同一张单据的多行明细）必须复用同一行库存：新建行在 SaveChanges 之前
        // 查数据库查不到，否则会为「同一仓库 + 同一商品」插入第二行，导致库存被拆成多行、
        // 结存快照断裂，后续出库还可能在单行上误判库存不足。
        stock = _db.Stocks.Local.FirstOrDefault(s =>
            s.WarehouseId == warehouseId && s.ProductId == productId && !s.IsDeleted);
        if (stock is not null) return stock;

        stock = new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            CreatedAt = DateTime.Now
        };
        _db.Stocks.Add(stock);
        return stock;
    }

    /// <inheritdoc />
    public async Task<decimal> ResolveSourceCostAsync(string sourceDocType, long sourceDocId, long? productId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDocType) || sourceDocId <= 0) return 0m;

        var movement = await _db.StockMovements.AsNoTracking()
            .Where(m => !m.IsDeleted && m.SourceDocType == sourceDocType && m.SourceDocId == sourceDocId
                        && !m.IsReversal && (productId == null || m.ProductId == productId))
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return movement?.UnitCost ?? 0m;
    }

    /// <inheritdoc />
    public async Task<StockMovement> IncreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(context, quantity);
        var productId = RequireProductId(context);

        var stock = await GetOrCreateStockAsync(context.WarehouseId, productId, cancellationToken);
        // 成本单价缺省取当前加权平均（历史遗留的无成本入库也能落账），显式成本优先
        var cost = unitCost > 0 ? unitCost : stock.AverageCost;
        var amount = RoundAmount(quantity * cost);

        stock.Quantity += quantity;
        stock.AvailableQuantity += quantity;
        stock.TotalCost = RoundAmount(stock.TotalCost + amount);
        stock.AverageCost = AverageOf(stock);
        stock.UpdatedAt = DateTime.Now;

        var movement = BuildMovement(context, direction: 1, quantity, cost, amount, stock);
        _db.StockMovements.Add(movement);
        return movement;
    }

    /// <inheritdoc />
    public async Task<StockMovement> DecreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(context, quantity);
        var productId = RequireProductId(context);

        var stock = await GetOrCreateStockAsync(context.WarehouseId, productId, cancellationToken);
        // 充足性以物理库存为准（历史数据可能存在「可用数量未维护」的情况），不足直接拒绝、不落半截数据
        if (stock.Quantity < quantity)
            throw BusinessException.RuleConflict(
                $"商品 [{context.ProductName}] 库存不足：当前库存 {stock.Quantity}，需要 {quantity}");

        // 成本单价缺省取当前加权平均成本（出库计价基准）
        var cost = unitCost > 0 ? unitCost : stock.AverageCost;

        // 成本下限保护：
        // 1) 出库成本不得超过该库存行的账面金额，否则库存金额会被冲成负数，
        //    且「Σ 流水金额 = Stocks.TotalCost」的核对关系被破坏（销审也无法精确还原）；
        // 2) 出库把库存清零时按账面余额一次性核减，避免「 6 位成本 × 数量 → 4 位金额」取整残留：
        //    残留会被 AverageOf 直接归零，同样造成账实不符。
        // 两种情况都按实际核减金额折算回单价，保证流水金额 = 库存金额变动。
        var amount = RoundAmount(quantity * cost);
        if (amount > stock.TotalCost || quantity >= stock.Quantity)
        {
            amount = Math.Max(0m, stock.TotalCost);
            cost = RoundCost(amount / quantity);
        }

        stock.Quantity -= quantity;
        stock.AvailableQuantity = Math.Max(0m, stock.AvailableQuantity - quantity);
        stock.TotalCost = RoundAmount(stock.TotalCost - amount);
        stock.AverageCost = AverageOf(stock);
        stock.UpdatedAt = DateTime.Now;

        var movement = BuildMovement(context, direction: -1, quantity, cost, -amount, stock);
        _db.StockMovements.Add(movement);
        return movement;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockMovement>> ReverseAsync(string sourceDocType, long sourceDocId, string reason,
        CancellationToken cancellationToken = default)
    {
        var movements = await _db.StockMovements
            .Where(m => !m.IsDeleted && m.SourceDocType == sourceDocType && m.SourceDocId == sourceDocId
                        && !m.IsReversal && !m.IsReversed)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);

        var reversals = new List<StockMovement>();
        foreach (var movement in movements)
        {
            var stock = await _db.Stocks
                .FirstOrDefaultAsync(s => s.WarehouseId == movement.WarehouseId
                                          && s.ProductId == movement.ProductId && !s.IsDeleted, cancellationToken);
            if (stock is null)
                throw BusinessException.RuleConflict(
                    $"商品 [{movement.ProductName}] 的库存记录已不存在，无法销审冲销");

            if (movement.Direction > 0)
            {
                // 原为入库：冲销要从库存中减去；已被后续业务占用时拒绝（宁可销审失败，也不出现负库存）
                if (stock.Quantity < movement.Quantity)
                    throw BusinessException.RuleConflict(
                        $"商品 [{movement.ProductName}] 当前库存 {stock.Quantity} 不足以冲销 {movement.Quantity}，" +
                        "该入库已被后续业务占用，无法销审");

                // 账面金额不足以扣回该笔入库金额时同样拒绝：否则库存金额被扣成负数，
                // 「Σ 流水金额 = Stocks.TotalCost」的核对关系也会被破坏（销审无法精确还原）。
                if (movement.Amount > stock.TotalCost)
                    throw BusinessException.RuleConflict(
                        $"商品 [{movement.ProductName}] 当前库存金额 {stock.TotalCost} 不足以冲销入库金额 " +
                        $"{movement.Amount}，该入库成本已被后续业务占用，无法销审");

                stock.Quantity -= movement.Quantity;
                stock.AvailableQuantity = Math.Max(0m, stock.AvailableQuantity - movement.Quantity);
                stock.TotalCost = RoundAmount(stock.TotalCost - movement.Amount);
            }
            else
            {
                // 原为出库：冲销把货退回库存，金额按原流水金额还原（冲销 = 严格逆操作）
                stock.Quantity += movement.Quantity;
                stock.AvailableQuantity += movement.Quantity;
                stock.TotalCost = RoundAmount(stock.TotalCost + Math.Abs(movement.Amount));
            }

            stock.AverageCost = AverageOf(stock);
            stock.UpdatedAt = DateTime.Now;

            movement.IsReversed = true;
            movement.UpdatedAt = DateTime.Now;

            var reversal = new StockMovement
            {
                MovementDate = DateTime.Today,
                MovementType = movement.MovementType,
                SourceDocType = movement.SourceDocType,
                SourceDocId = movement.SourceDocId,
                SourceDocNo = movement.SourceDocNo,
                WarehouseId = movement.WarehouseId,
                WarehouseName = movement.WarehouseName,
                ProductId = movement.ProductId,
                ProductCode = movement.ProductCode,
                ProductName = movement.ProductName,
                Spec = movement.Spec,
                Unit = movement.Unit,
                Direction = -movement.Direction,
                Quantity = movement.Quantity,
                UnitCost = movement.UnitCost,
                Amount = -movement.Amount,
                BalanceQuantity = stock.Quantity,
                BalanceAmount = stock.TotalCost,
                BalanceAverageCost = stock.AverageCost,
                IsReversal = true,
                ReversalOfMovementId = movement.Id,
                CreatedAt = DateTime.Now,
                Remark = string.IsNullOrWhiteSpace(reason) ? "销审冲销" : reason
            };
            _db.StockMovements.Add(reversal);
            reversals.Add(reversal);
        }

        return reversals;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockMovement>> ListMovementsAsync(string sourceDocType, long sourceDocId,
        CancellationToken cancellationToken = default)
        => await _db.StockMovements.AsNoTracking()
            .Where(m => !m.IsDeleted && m.SourceDocType == sourceDocType && m.SourceDocId == sourceDocId)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> CountActiveMovementsAsync(string sourceDocType, long sourceDocId,
        CancellationToken cancellationToken = default)
        => await _db.StockMovements.CountAsync(
            m => !m.IsDeleted && m.SourceDocType == sourceDocType && m.SourceDocId == sourceDocId
                 && !m.IsReversal && !m.IsReversed, cancellationToken);

    // ==================== 私有助手 ====================

    private static void EnsurePositive(InventoryMovementContext context, decimal quantity)
    {
        if (quantity <= 0)
            throw BusinessException.InvalidParameter($"商品 [{context.ProductName}] 的数量必须大于 0");
    }

    private static long RequireProductId(InventoryMovementContext context)
        => context.ProductId is > 0
            ? context.ProductId.Value
            : throw BusinessException.InvalidParameter($"商品 [{context.ProductName}] 缺少商品 Id，无法维护库存");

    /// <summary>移动后加权平均成本（数量为 0 时金额与成本一并归零，避免残留金额造成估价漂移）</summary>
    private static decimal AverageOf(Stock stock)
    {
        if (stock.Quantity <= 0)
        {
            stock.TotalCost = 0m;
            return 0m;
        }
        return RoundCost(stock.TotalCost / stock.Quantity);
    }

    private static StockMovement BuildMovement(InventoryMovementContext context, int direction, decimal quantity,
        decimal unitCost, decimal amount, Stock stock) => new()
    {
        MovementDate = context.MovementDate ?? DateTime.Today,
        MovementType = context.MovementType,
        SourceDocType = context.SourceDocType,
        SourceDocId = context.SourceDocId,
        SourceDocNo = context.SourceDocNo,
        WarehouseId = context.WarehouseId,
        WarehouseName = context.WarehouseName,
        ProductId = context.ProductId,
        ProductCode = context.ProductCode,
        ProductName = context.ProductName,
        Spec = context.Spec,
        Unit = context.Unit,
        Direction = direction,
        Quantity = quantity,
        UnitCost = RoundCost(unitCost),
        Amount = RoundAmount(amount),
        BalanceQuantity = stock.Quantity,
        BalanceAmount = stock.TotalCost,
        BalanceAverageCost = stock.AverageCost,
        CreatedAt = DateTime.Now,
        Remark = context.Remark
    };
}

