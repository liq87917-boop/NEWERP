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
/// 库存行身份（仓库 + 商品）：用于按<b>确定性顺序</b>锁定库存行，避免对向调拨互相死锁（ERP-354）。
/// </summary>
public readonly record struct StockIdentity(long WarehouseId, long ProductId);

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

    /// <summary>
    /// 读取权威当前库存数量（库存行不存在按 0）：只读，不创建库存行、不改变数量 / 金额 / 成本。
    /// 用于盘点过账前的「账面数量基线」核验（ERP-355）。
    /// </summary>
    Task<decimal> GetCurrentQuantityAsync(long warehouseId, long productId, CancellationToken cancellationToken = default);

    /// <summary>按来源单据取成本单价（来源入库/出库流水成本）；无来源或无流水时返回 0</summary>
    Task<decimal> ResolveSourceCostAsync(string sourceDocType, long sourceDocId, long? productId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 增加库存（入库侧：采购入库 / 调拨入库 / 退货入库 / 盘盈）并写入流水。
    /// <para>ERP-527：入参、移动金额与「移动后结存」在**任何被跟踪实体变更之前**先按实际 EF 存储精度
    /// （数量 / 可用数量 <c>DECIMAL(18,2)</c>、金额 <c>DECIMAL(18,4)</c>、成本 <c>DECIMAL(18,6)</c>）
    /// 校验，越界 / 过精度一律受控拒绝且零变更。</para>
    /// </summary>
    Task<StockMovement> IncreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 减少库存（出库侧：调拨出库 / 采购退货 / 盘亏）并写入流水；库存不足时抛业务异常。
    /// <para>ERP-527：与 <see cref="IncreaseAsync"/> 同一套持久化算术护栏（含出库清零按账面金额核减后
    /// 折算回成本单价的 <c>DECIMAL(18,6)</c> 可表示性）。</para>
    /// </summary>
    Task<StockMovement> DecreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按来源单据冲销全部未冲销流水（销审用）；返回新增的红字流水。
    /// <para>ERP-527：先算出全部冲销后的结存并逐笔按实际存储精度校验，全部通过后才写入被跟踪实体；
    /// 任一越界 / 溢出即受控拒绝且库存、原流水标记与红字流水全部保持原样。</para>
    /// </summary>
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
    public async Task<decimal> GetCurrentQuantityAsync(long warehouseId, long productId,
        CancellationToken cancellationToken = default)
        => await _db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId && !s.IsDeleted)
            .Select(s => (decimal?)s.Quantity)
            .FirstOrDefaultAsync(cancellationToken) ?? 0m;

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
        // ERP-527：入参先按实际 EF 存储精度校验（库存流水数量 DECIMAL(18,4)），不触碰任何被跟踪实体。
        InventoryPersistedArithmeticRules.EnsureMovementQuantity(quantity, context.ProductName);

        var stock = await GetOrCreateStockAsync(context.WarehouseId, productId, cancellationToken);
        try
        {
            // 成本单价缺省取当前加权平均（历史遗留的无成本入库也能落账），显式成本优先
            var cost = unitCost > 0 ? unitCost : stock.AverageCost;
            // ERP-527：成本单价在落库前先按既有 RoundCost（6 位、AwayFromZero）取整（唯一取整入口不变），
            // 护栏只在该取整之后判定实际存储精度 —— 越过 DECIMAL(18,6) 才受控拒绝，绝不额外截断。
            var persistedCost = InventoryPersistedArithmeticRules.EnsureUnitCost(RoundCost(cost), context.ProductName);
            var amount = InventoryPersistedArithmeticRules.ComputeMovementAmount(quantity, cost, context.ProductName);

            // ERP-527：先把「移动后结存」算完并校验，最后才写入被跟踪实体 —— 任一步失败都不改库存行、
            // 不新增库存流水，调用方事务因此能整体回滚（含同单据先前已记账的行）。
            var balance = Settle(
                InventoryPersistedArithmeticRules.SafeAdd(stock.Quantity, quantity, BalanceLabel(context, "结存数量")),
                InventoryPersistedArithmeticRules.SafeAdd(stock.AvailableQuantity, quantity, BalanceLabel(context, "可用数量")),
                RoundAmount(InventoryPersistedArithmeticRules.SafeAdd(stock.TotalCost, amount, BalanceLabel(context, "结存金额"))));
            EnsureBalanceRepresentable(balance, context.ProductName);

            ApplyBalance(stock, balance);

            var movement = BuildMovement(context, direction: 1, quantity, persistedCost, amount, stock);
            _db.StockMovements.Add(movement);
            return movement;
        }
        catch
        {
            DiscardIfNewlyCreated(stock);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<StockMovement> DecreaseAsync(InventoryMovementContext context, decimal quantity, decimal unitCost,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(context, quantity);
        var productId = RequireProductId(context);
        // ERP-527：入参先按实际 EF 存储精度校验（库存流水数量 DECIMAL(18,4)），不触碰任何被跟踪实体。
        InventoryPersistedArithmeticRules.EnsureMovementQuantity(quantity, context.ProductName);

        var stock = await GetOrCreateStockAsync(context.WarehouseId, productId, cancellationToken);
        try
        {
            // 充足性以物理库存为准（历史数据可能存在「可用数量未维护」的情况），不足直接拒绝、不落半截数据
            if (stock.Quantity < quantity)
                throw BusinessException.RuleConflict(
                    $"商品 [{context.ProductName}] 库存不足：当前库存 {stock.Quantity}，需要 {quantity}");

            // 成本单价缺省取当前加权平均成本（出库计价基准）
            var cost = unitCost > 0 ? unitCost : stock.AverageCost;
            // ERP-527：与入库同口径 —— 先按既有 RoundCost 取整，再在该取整之后判定实际存储精度。
            var persistedCost = InventoryPersistedArithmeticRules.EnsureUnitCost(RoundCost(cost), context.ProductName);

            // 成本下限保护：
            // 1) 出库成本不得超过该库存行的账面金额，否则库存金额会被冲成负数，
            //    且「Σ 流水金额 = Stocks.TotalCost」的核对关系被破坏（销审也无法精确还原）；
            // 2) 出库把库存清零时按账面余额一次性核减，避免「 6 位成本 × 数量 → 4 位金额」取整残留：
            //    残留会被结存归零抹掉（数量归零时金额与成本一并归零），同样造成账实不符。
            // 两种情况都按实际核减金额折算回单价，保证流水金额 = 库存金额变动。
            var amount = InventoryPersistedArithmeticRules.ComputeMovementAmount(quantity, cost, context.ProductName);
            if (amount > stock.TotalCost || quantity >= stock.Quantity)
            {
                amount = Math.Max(0m, stock.TotalCost);
                // ERP-527：折算回的成本单价同样必须在库存 / 流水成本列（DECIMAL(18,6)）内可表示。
                persistedCost = InventoryPersistedArithmeticRules.EnsureUnitCost(
                    RoundCost(InventoryPersistedArithmeticRules.SafeDivide(amount, quantity, context.ProductName)),
                    context.ProductName);
            }

            var balance = Settle(
                InventoryPersistedArithmeticRules.SafeSubtract(stock.Quantity, quantity, BalanceLabel(context, "结存数量")),
                Math.Max(0m, InventoryPersistedArithmeticRules.SafeSubtract(stock.AvailableQuantity, quantity,
                    BalanceLabel(context, "可用数量"))),
                RoundAmount(InventoryPersistedArithmeticRules.SafeSubtract(stock.TotalCost, amount,
                    BalanceLabel(context, "结存金额"))));
            EnsureBalanceRepresentable(balance, context.ProductName);

            ApplyBalance(stock, balance);

            var movement = BuildMovement(context, direction: -1, quantity, persistedCost, -amount, stock);
            _db.StockMovements.Add(movement);
            return movement;
        }
        catch
        {
            DiscardIfNewlyCreated(stock);
            throw;
        }
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

        // ERP-527：先把每一笔冲销的「移动后结存」全部算出来并逐笔按实际存储精度校验，
        // 全部通过之后才写入被跟踪实体 —— 任何一笔越界 / 溢出都在**零变更**的前提下受控拒绝
        // （库存行、原流水的 IsReversed 标记、红字流水与单据状态全部保持原样，由调用方事务整体回滚）。
        var shadows = new Dictionary<Stock, StockBalance>();
        var plans = new List<(StockMovement Movement, Stock Stock, StockBalance Balance)>(movements.Count);
        foreach (var movement in movements)
        {
            var stock = await _db.Stocks
                .FirstOrDefaultAsync(s => s.WarehouseId == movement.WarehouseId
                                          && s.ProductId == movement.ProductId && !s.IsDeleted, cancellationToken);
            if (stock is null)
                throw BusinessException.RuleConflict(
                    $"商品 [{movement.ProductName}] 的库存记录已不存在，无法销审冲销");

            // 同一库存行可能出现在多笔流水中：以「尚未落地的提案结存」继续推算，保证多笔冲销的顺序语义不变。
            var current = shadows.TryGetValue(stock, out var shadow)
                ? shadow
                : new StockBalance(stock.Quantity, stock.AvailableQuantity, stock.TotalCost, stock.AverageCost);

            StockBalance balance;
            if (movement.Direction > 0)
            {
                // 原为入库：冲销要从库存中减去；已被后续业务占用时拒绝（宁可销审失败，也不出现负库存）
                if (current.Quantity < movement.Quantity)
                    throw BusinessException.RuleConflict(
                        $"商品 [{movement.ProductName}] 当前库存 {current.Quantity} 不足以冲销 {movement.Quantity}，" +
                        "该入库已被后续业务占用，无法销审");

                // 账面金额不足以扣回该笔入库金额时同样拒绝：否则库存金额被扣成负数，
                // 「Σ 流水金额 = Stocks.TotalCost」的核对关系也会被破坏（销审无法精确还原）。
                if (movement.Amount > current.TotalCost)
                    throw BusinessException.RuleConflict(
                        $"商品 [{movement.ProductName}] 当前库存金额 {current.TotalCost} 不足以冲销入库金额 " +
                        $"{movement.Amount}，该入库成本已被后续业务占用，无法销审");

                balance = Settle(
                    InventoryPersistedArithmeticRules.SafeSubtract(current.Quantity, movement.Quantity,
                        ReverseLabel(movement, "结存数量")),
                    Math.Max(0m, InventoryPersistedArithmeticRules.SafeSubtract(current.AvailableQuantity,
                        movement.Quantity, ReverseLabel(movement, "可用数量"))),
                    RoundAmount(InventoryPersistedArithmeticRules.SafeSubtract(current.TotalCost, movement.Amount,
                        ReverseLabel(movement, "结存金额"))));
            }
            else
            {
                // 原为出库：冲销把货退回库存，金额按原流水金额还原（冲销 = 严格逆操作）
                balance = Settle(
                    InventoryPersistedArithmeticRules.SafeAdd(current.Quantity, movement.Quantity,
                        ReverseLabel(movement, "结存数量")),
                    InventoryPersistedArithmeticRules.SafeAdd(current.AvailableQuantity, movement.Quantity,
                        ReverseLabel(movement, "可用数量")),
                    RoundAmount(InventoryPersistedArithmeticRules.SafeAdd(current.TotalCost, Math.Abs(movement.Amount),
                        ReverseLabel(movement, "结存金额"))));
            }

            // 红字流水本身必须仍能被原样持久化：历史流水被外部改写（过精度 / 越界）时同样 fail closed，
            // 但保留原流水数量的既有符号与数量原值（红字只把方向取反、金额取反）。
            InventoryPersistedArithmeticRules.EnsureMovementQuantity(movement.Quantity, movement.ProductName);
            InventoryPersistedArithmeticRules.EnsureUnitCost(movement.UnitCost, movement.ProductName);
            InventoryPersistedArithmeticRules.EnsureMovementAmount(-movement.Amount, movement.ProductName);
            EnsureBalanceRepresentable(balance, movement.ProductName);

            shadows[stock] = balance;
            plans.Add((movement, stock, balance));
        }

        // 全部校验通过后才落地：改库存结存、标记原流水已冲销、追加红字流水（历史记录不删除）。
        var reversals = new List<StockMovement>(plans.Count);
        foreach (var (movement, stock, balance) in plans)
        {
            ApplyBalance(stock, balance);

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
                BalanceQuantity = balance.Quantity,
                BalanceAmount = balance.TotalCost,
                BalanceAverageCost = balance.AverageCost,
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

    /// <summary>
    /// 确定性库存行锁定顺序：过滤非法键、去重后按「仓库 Id 升序 → 商品 Id 升序」排序。
    /// 纯函数，便于逐条单测（不依赖数据库、不改变任何数量或成本）。
    /// <para>调用方（仓库调拨控制器）在同一可串行化事务内按此顺序对库存行加 <c>UPDLOCK/HOLDLOCK</c>，
    /// 因此对向调拨（A→B 与 B→A）与多明细调拨不会形成环形等待。</para>
    /// </summary>
    public static IReadOnlyList<StockIdentity> OrderStockIdentities(IEnumerable<StockIdentity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(r => r.WarehouseId > 0 && r.ProductId > 0)
            .Distinct()
            .OrderBy(r => r.WarehouseId)
            .ThenBy(r => r.ProductId)
            .ToList();
    }

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

    /// <summary>移动后结存提案（ERP-527）：先把数量 / 可用数量 / 金额 / 加权平均成本算完并校验，最后才写入库存行。</summary>
    private readonly record struct StockBalance(decimal Quantity, decimal AvailableQuantity, decimal TotalCost,
        decimal AverageCost);

    /// <summary>
    /// 移动后结存：数量为 0（或历史脏数据下的负数）时金额与成本一并归零，避免残留金额造成估价漂移；
    /// 否则加权平均成本按既有 6 位小数、<c>AwayFromZero</c> 取整口径折算（仅做「提案计算」，不改任何实体）。
    /// </summary>
    private static StockBalance Settle(decimal quantity, decimal availableQuantity, decimal totalCost)
        => quantity <= 0
            ? new StockBalance(quantity, availableQuantity, 0m, 0m)
            : new StockBalance(quantity, availableQuantity, totalCost,
                RoundCost(InventoryPersistedArithmeticRules.SafeDivide(totalCost, quantity, "加权平均成本")));

    /// <summary>
    /// 结存提案必须逐字段落在实际 EF 存储精度内：结存数量同时受库存行数量列（<c>DECIMAL(18,2)</c>）与
    /// 库存流水结存数量快照列（<c>DECIMAL(18,4)</c>）约束；可用数量 <c>DECIMAL(18,2)</c>；
    /// 结存金额 <c>DECIMAL(18,4)</c>；加权平均成本 <c>DECIMAL(18,6)</c>。
    /// </summary>
    private static void EnsureBalanceRepresentable(StockBalance balance, string productName)
    {
        InventoryPersistedArithmeticRules.EnsureBalanceQuantity(balance.Quantity, $"商品 [{productName}] 的结存数量");
        InventoryPersistedArithmeticRules.EnsureStockQuantity(balance.AvailableQuantity,
            $"商品 [{productName}] 的可用数量");
        InventoryPersistedArithmeticRules.EnsureStockAmount(balance.TotalCost, $"商品 [{productName}] 的结存金额");
        InventoryPersistedArithmeticRules.EnsureAverageCost(balance.AverageCost,
            $"商品 [{productName}] 的加权平均成本");
    }

    /// <summary>把已校验的结存提案写入被跟踪库存行（只在全部校验通过之后调用，失败路径绝不触碰实体）。</summary>
    private static void ApplyBalance(Stock stock, StockBalance balance)
    {
        stock.Quantity = balance.Quantity;
        stock.AvailableQuantity = balance.AvailableQuantity;
        stock.TotalCost = balance.TotalCost;
        stock.AverageCost = balance.AverageCost;
        stock.UpdatedAt = DateTime.Now;
    }

    /// <summary>失败时把本次调用新建（尚未持久化）的库存行移出跟踪：失败不留下任何被跟踪的半截数据。</summary>
    private void DiscardIfNewlyCreated(Stock stock)
    {
        if (_db is DbContext context && context.Entry(stock).State == EntityState.Added)
            _db.Stocks.Remove(stock);
    }

    private static string BalanceLabel(InventoryMovementContext context, string field)
        => $"商品 [{context.ProductName}] 的{field}";

    private static string ReverseLabel(StockMovement movement, string field)
        => $"商品 [{movement.ProductName}] 冲销后的{field}";

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
/// <summary>
/// 库存「持久化算术」边界口径（ERP-527）：把库存增加 / 减少 / 冲销在<b>任何被跟踪实体变更之前</b>必须满足的
/// 实际存储精度判定收敛到唯一入口，供 <see cref="InventoryService"/> 调用：
/// <list type="number">
/// <item>库存行数量 / 可用数量在实际 EF 模型中<b>未</b>显式 <c>HasPrecision</c>，因此按 EF 默认精度落库为
/// <c>DECIMAL(18,2)</c>（与真实 <c>INFORMATION_SCHEMA</c> 的 <c>Stocks.Quantity</c> / <c>AvailableQuantity</c>
/// 一致）；更细小数位或更大绝对值的结存会在写入时被 SQL 静默取整 / 溢出，因此一律受控拒绝；</item>
/// <item>库存行金额（<see cref="Stock.TotalCost"/>）与库存流水数量 / 金额 / 结存（<c>Quantity</c> / <c>Amount</c> /
/// <c>BalanceQuantity</c> / <c>BalanceAmount</c>）为 <c>DECIMAL(18,4)</c>；成本单价与加权平均成本
/// （<see cref="Stock.AverageCost"/>、库存流水 <c>UnitCost</c> / <c>BalanceAverageCost</c>）为
/// <c>DECIMAL(18,6)</c> —— 精度常量与 <c>ErpDbContext.Config.cs</c> 的显式 <c>HasPrecision(...)</c> 声明
/// （以及 <c>SchemaUpgrader</c> 第 23 段建表脚本）<b>一字同源</b>；</item>
/// <item>全部判定都是<b>纯函数</b>：不访问数据库、不改写任何被跟踪实体，也不重新取整既有业务口径
/// （金额 4 位小数、成本 6 位小数仍唯一由 <see cref="InventoryService.RoundAmount"/> /
/// <see cref="InventoryService.RoundCost"/> 按 <c>AwayFromZero</c> 完成；移动加权平均、成本缺省兜底、
/// 出库清零按账面金额核减、冲销 = 严格逆操作等既有语义原样保留）；</item>
/// <item>标量区间口径<b>复用</b>系统唯一标量边界规则 <see cref="FinancePersistedAmountRules"/>
/// （ERP-508 起，不新增第二套区间常量），本类只补充「逐位原样可表示 + 受控算术」。</item>
/// </list>
/// </summary>
public static class InventoryPersistedArithmeticRules
{
    /// <summary>库存流水数量 / 冲销数量 / 库存金额 / 结存数量 / 结存金额的实际小数位（<c>DECIMAL(18,4)</c>）。</summary>
    public const int MovementScale = FinancePersistedAmountRules.DecimalScale4;

    /// <summary>成本单价 / 加权平均成本的实际小数位（<c>DECIMAL(18,6)</c>）。</summary>
    public const int CostScale = FinancePersistedAmountRules.DecimalScale6;

    /// <summary>库存行数量 / 可用数量的实际小数位（EF 默认精度 <c>DECIMAL(18,2)</c>）。</summary>
    public const int StockQuantityScale = FinancePersistedAmountRules.DecimalScale;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（库存流水数量 / 金额 / 结存、库存金额）。</summary>
    public const decimal MaxMovementValue = FinancePersistedAmountRules.MaxDecimal18Scale4;

    /// <summary><c>DECIMAL(18,4)</c> 的最小可表示值。</summary>
    public const decimal MinMovementValue = FinancePersistedAmountRules.MinDecimal18Scale4;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（成本单价 / 加权平均成本）。</summary>
    public const decimal MaxCostValue = FinancePersistedAmountRules.MaxDecimal18Scale6;

    /// <summary><c>DECIMAL(18,6)</c> 的最小可表示值。</summary>
    public const decimal MinCostValue = FinancePersistedAmountRules.MinDecimal18Scale6;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示值（库存行数量 / 可用数量）。</summary>
    public const decimal MaxStockQuantityValue = FinancePersistedAmountRules.MaxDecimal18Scale2;

    /// <summary><c>DECIMAL(18,2)</c> 的最小可表示值。</summary>
    public const decimal MinStockQuantityValue = FinancePersistedAmountRules.MinDecimal18Scale2;

    /// <summary>越界 / 过精度拒绝文案（接口 / 文档同源）。</summary>
    public const string OverflowText =
        "库存数量 / 金额超出可存储范围：已拒绝，绝不静默截断或取整";

    /// <summary>
    /// 值能否在实际存储精度 <c>DECIMAL(18,scale)</c> 内<b>逐位原样</b>表示：小数位不超过 <paramref name="scale"/>
    /// 且绝对值有界。区间判定复用 <see cref="FinancePersistedAmountRules.IsWithinDecimal18"/>（支持 2 / 4 / 6）。
    /// </summary>
    public static bool IsRepresentable(decimal value, int scale)
        => decimal.Round(value, scale, MidpointRounding.AwayFromZero) == value
           && FinancePersistedAmountRules.IsWithinDecimal18(value, scale);

    /// <summary>校验（并按原值返回）值可在 <c>DECIMAL(18,scale)</c> 内原样表示；否则抛受控业务错误。</summary>
    public static decimal EnsureRepresentable(decimal value, int scale, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!IsRepresentable(value, scale))
            throw BusinessException.InvalidParameter(
                $"{label} {value} 超出存储精度（DECIMAL(18,{scale})）：已拒绝，绝不静默截断或取整");
        return value;
    }

    /// <summary>库存流水数量（含红字冲销数量）：必须能在 <c>DECIMAL(18,4)</c> 内原样表示。</summary>
    public static decimal EnsureMovementQuantity(decimal quantity, string productName)
        => EnsureRepresentable(quantity, MovementScale, $"商品 [{productName}] 的移动数量");

    /// <summary>库存流水金额（含红字冲销金额）：必须能在 <c>DECIMAL(18,4)</c> 内原样表示。</summary>
    public static decimal EnsureMovementAmount(decimal amount, string productName)
        => EnsureRepresentable(amount, MovementScale, $"商品 [{productName}] 的移动金额");

    /// <summary>
    /// 成本单价 / 折算回的成本单价：必须能在 <c>DECIMAL(18,6)</c> 内原样表示。
    /// <para>调用方必须传入**已按既有 <see cref="InventoryService.RoundCost"/> 取整**的成本值：
    /// 6 位小数取整仍唯一由该既有入口完成，本方法只在该取整之后判定实际存储精度（越界 / 进位越界才拒绝）。</para>
    /// </summary>
    public static decimal EnsureUnitCost(decimal unitCost, string productName)
        => EnsureRepresentable(unitCost, CostScale, $"商品 [{productName}] 的成本单价");

    /// <summary>库存行数量 / 可用数量：必须能在 <c>DECIMAL(18,2)</c> 内原样表示（否则结存会被静默取整）。</summary>
    public static decimal EnsureStockQuantity(decimal quantity, string label)
        => EnsureRepresentable(quantity, StockQuantityScale, label);

    /// <summary>
    /// 结存数量的**双重口径**：同一个结存数量会同时写入库存行 <see cref="Stock.Quantity"/>（<c>DECIMAL(18,2)</c>，
    /// EF 默认精度）与库存流水的结存快照 <see cref="StockMovement.BalanceQuantity"/>（<c>DECIMAL(18,4)</c>，显式声明），
    /// 因此必须同时落在两列的区间内，且小数位不超过库存行数量列的 2 位（数量侧**不引入**任何新取整）。
    /// <para>效果：库存数量列虽然能存到 <c>9_999_999_999_999_999.99</c>，但流水结存快照列只到
    /// <c>99_999_999_999_999.9999</c>，因此实际可落库的结存上界是二者交集（小数位 ≤ 2 时为 <c>99_999_999_999_999.99</c>）。</para>
    /// </summary>
    public static decimal EnsureBalanceQuantity(decimal quantity, string label)
    {
        EnsureStockQuantity(quantity, label);
        return EnsureRepresentable(quantity, MovementScale, $"{label}（库存流水结存数量快照）");
    }

    /// <summary>库存行结存金额：必须能在 <c>DECIMAL(18,4)</c> 内原样表示。</summary>
    public static decimal EnsureStockAmount(decimal amount, string label)
        => EnsureRepresentable(amount, MovementScale, label);

    /// <summary>移动加权平均成本：必须能在 <c>DECIMAL(18,6)</c> 内原样表示。</summary>
    public static decimal EnsureAverageCost(decimal averageCost, string label)
        => EnsureRepresentable(averageCost, CostScale, label);

    /// <summary>
    /// 移动金额 = 数量 × 成本单价（受控乘法 + 既有 <see cref="InventoryService.RoundAmount"/> 4 位小数取整），
    /// 并校验结果可在库存流水金额列内原样持久化；溢出 / 越界一律受控拒绝。
    /// </summary>
    public static decimal ComputeMovementAmount(decimal quantity, decimal unitCost, string productName)
        => EnsureMovementAmount(
            InventoryService.RoundAmount(SafeMultiply(quantity, unitCost, productName)),
            productName);

    /// <summary>受控加法：CLR 十进制溢出（<see cref="OverflowException"/>）→ 受控业务错误，绝不崩溃、绝不截断。</summary>
    public static decimal SafeAdd(decimal left, decimal right, string label)
    {
        try
        {
            return left + right;
        }
        catch (OverflowException)
        {
            throw Overflow(label, $"{left} + {right}");
        }
    }

    /// <summary>受控减法：CLR 十进制溢出 → 受控业务错误。</summary>
    public static decimal SafeSubtract(decimal left, decimal right, string label)
    {
        try
        {
            return left - right;
        }
        catch (OverflowException)
        {
            throw Overflow(label, $"{left} - {right}");
        }
    }

    /// <summary>受控乘法：CLR 十进制溢出 → 受控业务错误（复用系统唯一标量乘法规程）。</summary>
    public static decimal SafeMultiply(decimal left, decimal right, string label)
        => FinancePersistedAmountRules.SafeMultiply(left, right, $"商品 [{label}] 的移动金额");

    /// <summary>受控除法（出库清零时按账面金额折算回成本单价）：CLR 溢出 / 除零 → 受控业务错误。</summary>
    public static decimal SafeDivide(decimal dividend, decimal divisor, string label)
    {
        try
        {
            return dividend / divisor;
        }
        catch (OverflowException)
        {
            throw Overflow(label, $"{dividend} / {divisor}");
        }
        catch (DivideByZeroException)
        {
            throw Overflow(label, $"{dividend} / {divisor}");
        }
    }

    private static BusinessException Overflow(string label, string expression)
        => BusinessException.InvalidParameter(
            $"{label}的十进制算术溢出（{expression} 超出可表示范围）：已拒绝，绝不静默截断或取整");
}


