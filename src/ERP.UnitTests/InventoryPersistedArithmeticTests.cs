using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-527 库存增减 / 冲销的「持久化算术」边界护栏单元测试（内存库 + 真实服务 / 真实控制器）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量一字同源：库存行数量 / 可用数量按 EF 默认精度落库为 <c>DECIMAL(18,2)</c>
/// （未显式 <c>HasPrecision</c>，与真实 <c>INFORMATION_SCHEMA</c> 一致），金额 <c>DECIMAL(18,4)</c>、
/// 成本 <c>DECIMAL(18,6)</c>；库存流水数量 / 金额 / 结存 <c>DECIMAL(18,4)</c>、单价 / 结存均价 <c>DECIMAL(18,6)</c>；</item>
/// <item>精确上限放行、上限加一个最小单位（<c>0.01</c> / <c>0.0001</c> / <c>0.000001</c>）与更细小数位受控拒绝；</item>
/// <item>入库：入参数量 / 成本不可表示、乘法与结存越界、结存数量过精度、平均成本不可表示一律受控拒绝
/// 且库存与流水跟踪零变更（失败路径不留新建库存行）；</item>
/// <item>出库：库存不足保留既有受控错误且不新建库存行；出库清零按账面金额核减后折算成本单价越界时受控拒绝；</item>
/// <item>冲销：先算出全部结存并校验、再落地 —— 单笔溢出、多笔中任一溢出、历史流水被改写为不可表示时
/// 全部零变更（原流水 <c>IsReversed</c> 与库存保持原样，不追加红字流水）；合法红字保留方向取反 / 金额取反 / 数量原值；</item>
/// <item>多行调拨单：调入侧结存溢出时整单回滚，含已调出的来源行（库存 / 流水 / 状态全不变）。</item>
/// </list>
/// 说明：除「实际 EF 精度」用例以 SQL Server 提供程序<b>仅构建模型</b>（不连接数据库）外，全部使用内存数据库。
/// </summary>
public class InventoryPersistedArithmeticTests
{
    private const long WarehouseA = 950001L;
    private const long WarehouseB = 950002L;
    private const long Product1 = 850001L;
    private const long Product2 = 850002L;

    private static readonly decimal MaxStockQuantity = InventoryPersistedArithmeticRules.MaxStockQuantityValue;
    private static readonly decimal MaxMovementValue = InventoryPersistedArithmeticRules.MaxMovementValue;
    private static readonly decimal MaxCostValue = InventoryPersistedArithmeticRules.MaxCostValue;

    /// <summary>
    /// 结存数量的**实际可落库上界**：库存行数量列 <c>DECIMAL(18,2)</c>（上界 9_999_999_999_999_999.99）
    /// 与库存流水结存数量快照列 <c>DECIMAL(18,4)</c>（上界 99_999_999_999_999.9999）的交集，
    /// 小数位取库存行口径 2 位，因此上界为 99_999_999_999_999.99、最小单位为 0.01。
    /// </summary>
    private static readonly decimal MaxBalanceQuantity = 99_999_999_999_999.99m;

    // ==================== 1. 实际 EF 精度与规则常量同源 + 边界量子 ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_ERP527_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        // 库存行：数量 / 可用数量未显式 HasPrecision → EF 默认 decimal(18,2)；金额 4 位、成本 6 位（显式声明）
        Assert.Equal("decimal(18,2)", ColumnType(model, typeof(Stock), nameof(Stock.Quantity)));
        Assert.Equal("decimal(18,2)", ColumnType(model, typeof(Stock), nameof(Stock.AvailableQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(Stock), nameof(Stock.TotalCost)));
        Assert.Equal("decimal(18,6)", ColumnType(model, typeof(Stock), nameof(Stock.AverageCost)));

        // 库存流水：数量 / 金额 / 结存 4 位；单价 / 结存均价 6 位（显式声明）
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(StockMovement), nameof(StockMovement.Quantity)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(StockMovement), nameof(StockMovement.Amount)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(StockMovement), nameof(StockMovement.BalanceQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(StockMovement), nameof(StockMovement.BalanceAmount)));
        Assert.Equal("decimal(18,6)", ColumnType(model, typeof(StockMovement), nameof(StockMovement.UnitCost)));
        Assert.Equal("decimal(18,6)", ColumnType(model, typeof(StockMovement), nameof(StockMovement.BalanceAverageCost)));

        // 规则常量复用 ERP-508 起的唯一标量边界规则（系统内不允许第二套区间常量）
        Assert.Equal(FinancePersistedAmountRules.DecimalScale, InventoryPersistedArithmeticRules.StockQuantityScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale4, InventoryPersistedArithmeticRules.MovementScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale6, InventoryPersistedArithmeticRules.CostScale);
        Assert.Equal(9_999_999_999_999_999.99m, InventoryPersistedArithmeticRules.MaxStockQuantityValue);
        Assert.Equal(99_999_999_999_999.9999m, InventoryPersistedArithmeticRules.MaxMovementValue);
        Assert.Equal(999_999_999_999.999999m, InventoryPersistedArithmeticRules.MaxCostValue);

        // 逐位原样可表示：精确上限放行；「上限 + 一个最小单位」与更细小数位一律拒绝
        Assert.True(InventoryPersistedArithmeticRules.IsRepresentable(MaxStockQuantity, 2));
        Assert.False(InventoryPersistedArithmeticRules.IsRepresentable(MaxStockQuantity + 0.01m, 2));
        Assert.False(InventoryPersistedArithmeticRules.IsRepresentable(1.001m, 2));
        Assert.True(InventoryPersistedArithmeticRules.IsRepresentable(MaxMovementValue, 4));
        Assert.False(InventoryPersistedArithmeticRules.IsRepresentable(MaxMovementValue + 0.0001m, 4));
        Assert.False(InventoryPersistedArithmeticRules.IsRepresentable(1.00001m, 4));
        Assert.True(InventoryPersistedArithmeticRules.IsRepresentable(MaxCostValue, 6));
        Assert.False(InventoryPersistedArithmeticRules.IsRepresentable(MaxCostValue + 0.000001m, 6));

        // 结存数量受「库存行数量列 18,2」与「库存流水结存快照列 18,4」双重约束：实际可落库上界为二者交集
        Assert.Equal(MaxBalanceQuantity,
            InventoryPersistedArithmeticRules.EnsureBalanceQuantity(MaxBalanceQuantity, "测试结存数量"));
        var mirrorOverflow = Assert.Throws<BusinessException>(() =>
            InventoryPersistedArithmeticRules.EnsureBalanceQuantity(MaxStockQuantity, "测试结存数量"));
        Assert.Equal(ErrorCodes.InvalidParameter, mirrorOverflow.Code);
    }

    // ==================== 2. 入库：结存与移动数值边界 ====================

    [Fact]
    public async Task 入库_结存数量精确上限放行_上限加最小单位拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var context = Context(WarehouseA, Product1);
        SeedStock(db, WarehouseA, Product1, MaxBalanceQuantity - 100m, 0m);

        await service.IncreaseAsync(context, 100m, 0m);
        await db.SaveChangesAsync();

        var stock = db.Stocks.Single();
        Assert.Equal(MaxBalanceQuantity, stock.Quantity);
        Assert.Equal(MaxBalanceQuantity, db.StockMovements.Single().BalanceQuantity);

        // 再加库存数量列的最小单位（0.01）→ 结存越过库存流水结存快照列上限：受控拒绝且零变更
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.IncreaseAsync(context, 0.01m, 0m));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(MaxBalanceQuantity, stock.Quantity);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task 入库_结存落在库存数量列内但超出流水结存快照_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        // 库存行数量列（18,2）能存到 9_999_999_999_999_999.99，但流水结存快照列（18,4）只到
        // 99_999_999_999_999.9999：结存落在两列之间的值同样必须在写入之前拒绝（否则真实 SQL 参数越界）。
        SeedStock(db, WarehouseA, Product1, MaxBalanceQuantity, 0m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.IncreaseAsync(Context(WarehouseA, Product1), 0.01m, 0m));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(MaxBalanceQuantity, db.Stocks.Single().Quantity);
        Assert.Empty(db.StockMovements);
    }

    [Theory]
    [InlineData("quantity-over-scale")]   // 1.00001：DECIMAL(18,4) 过精度
    [InlineData("quantity-over-max")]     // 1e14：超过 DECIMAL(18,4) 上限
    [InlineData("cost-over-max")]         // 1e12：超过 DECIMAL(18,6) 上限
    [InlineData("amount-over-max")]       // 数量 × 成本 超过 DECIMAL(18,4) 上限
    public async Task 入库_数值不可表示_受控拒绝且零变更(string scenario)
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var (quantity, cost) = scenario switch
        {
            "quantity-over-scale" => (1.00001m, 1m),
            "quantity-over-max" => (100_000_000_000_000m, 1m),
            "cost-over-max" => (1m, 1_000_000_000_000m),
            _ => (1_000_000m, 100_000_000_000m)
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.IncreaseAsync(Context(WarehouseA, Product1), quantity, cost));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoTracking(db);
    }

    [Fact]
    public async Task 入库_成本超过六位小数_按既有取整口径落库()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);

        // 既有口径保留：显式成本的 6 位小数取整仍唯一由 RoundCost 完成，金额按未取整成本计算；
        // 护栏只在该取整之后判定实际存储精度 —— 取整后仍在 DECIMAL(18,6) 区间内的成本照常放行。
        var movement = await service.IncreaseAsync(Context(WarehouseA, Product1), 1000m, 0.0003334m);
        await db.SaveChangesAsync();

        Assert.Equal(0.000333m, movement.UnitCost);
        Assert.Equal(0.3334m, movement.Amount);
        Assert.Equal(0.3334m, db.Stocks.Single().TotalCost);
    }

    [Fact]
    public async Task 入库_结存数量过精度_受控拒绝且零变更()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 100m);

        // 0.005 在 DECIMAL(18,4) 内可表示，但结存数量列（DECIMAL(18,2)）无法逐位原样表示 100.005 → 拒绝
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.IncreaseAsync(Context(WarehouseA, Product1), 0.005m, 1m));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var stock = db.Stocks.Single();
        Assert.Equal(100m, stock.Quantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 入库_平均成本不可表示_受控拒绝且不留下被跟踪库存行()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);

        // 0.01 × 999_999_999_999.999999 = 9_999_999_999.99999999 → 4 位取整进位为 1e10（金额列内可表示），
        // 但结存均价 = 1e10 / 0.01 = 1e12 超过 DECIMAL(18,6) 上限 → 拒绝；本次新建的库存行必须不被跟踪。
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.IncreaseAsync(Context(WarehouseA, Product1), 0.01m, MaxCostValue));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoTracking(db);
    }

    // ==================== 3. 出库：核减、清零与不足 ====================

    [Fact]
    public async Task 出库_清零按账面金额核减_折算成本单价越界_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        // 账面：0.01 件 / 金额 1e12（均在各自列内可表示）→ 清零时折算回单价 = 1e12 / 0.01 = 1e14，
        // 超过 DECIMAL(18,6) 上限 → 拒绝，且库存与流水零变更。
        SeedStock(db, WarehouseA, Product1, quantity: 0.01m, totalCost: 1_000_000_000_000m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.DecreaseAsync(Context(WarehouseA, Product1), 0.01m, 10m));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var stock = db.Stocks.Single();
        Assert.Equal(0.01m, stock.Quantity);
        Assert.Equal(1_000_000_000_000m, stock.TotalCost);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 出库_库存不足_保留既有受控错误且不新建库存行()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.DecreaseAsync(Context(WarehouseA, Product1), 1m, 0m));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        AssertRejectedWithNoTracking(db);
    }

    [Fact]
    public async Task 出库_结存数量过精度_受控拒绝且零变更()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);

        // 出库 0.005 后结存 9.995 无法在 DECIMAL(18,2) 内逐位原样表示 → 拒绝（不静默取整）
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.DecreaseAsync(Context(WarehouseA, Product1), 0.005m, 10m));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 4. 冲销：红字符号与「先算后写」零变更 ====================

    [Fact]
    public async Task 冲销_出库红字_方向取反金额取反且结存还原()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var context = Context(WarehouseA, Product1, sourceDocId: 7L);
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);

        var movement = await service.DecreaseAsync(context, 4m, 0m);       // 按均价 10 核减 40
        await db.SaveChangesAsync();
        Assert.Equal(-40m, movement.Amount);

        var reversals = await service.ReverseAsync(InventoryDocumentHelper.StockAdjustmentType, 7L, "ERP-527 冲销");
        await db.SaveChangesAsync();

        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);

        var reversal = Assert.Single(reversals);
        Assert.True(reversal.IsReversal);
        Assert.Equal(movement.Id, reversal.ReversalOfMovementId);
        Assert.Equal(1, reversal.Direction);        // 出库红字 = 入库方向
        Assert.Equal(40m, reversal.Amount);         // 金额取反
        Assert.Equal(4m, reversal.Quantity);        // 数量原值
        Assert.Equal(10m, reversal.UnitCost);       // 单价原值
        Assert.Equal(10m, reversal.BalanceQuantity);
        Assert.Equal(100m, reversal.BalanceAmount);
        Assert.Equal(10m, reversal.BalanceAverageCost);
        Assert.True(db.StockMovements.Single(m => m.Id == movement.Id).IsReversed);
    }

    [Fact]
    public async Task 冲销_结存数量越界_受控拒绝且原流水与库存不变()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        // 库存数量已在上限、金额为 0；历史出库流水的冲销要把 0.01 退回 → 结存越过 DECIMAL(18,2) 上限
        SeedStock(db, WarehouseA, Product1, quantity: MaxStockQuantity, totalCost: 0m);
        var outbound = SeedMovement(db, WarehouseA, Product1, direction: -1, quantity: 0.01m, unitCost: 0m,
            amount: -0.01m, sourceDocId: 9L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.ReverseAsync(InventoryDocumentHelper.StockAdjustmentType, 9L, "ERP-527 冲销"));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(MaxStockQuantity, db.Stocks.Single().Quantity);
        Assert.False(outbound.IsReversed);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task 冲销_多笔流水中任一越界_全部零变更()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 100m);
        SeedStock(db, WarehouseB, Product2, quantity: MaxStockQuantity, totalCost: 0m);

        // 第 1 笔可合法冲销（把 Product1 出库的 10 件退回），第 2 笔会把 Product2 结存推过上限：
        // 全部校验通过之前不得落地任何一笔（含第 1 笔的库存写入与流水标记）。
        var first = SeedMovement(db, WarehouseA, Product1, direction: -1, quantity: 10m, unitCost: 10m,
            amount: -100m, sourceDocId: 11L);
        var second = SeedMovement(db, WarehouseB, Product2, direction: -1, quantity: 1m, unitCost: 0m,
            amount: -1m, sourceDocId: 11L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.ReverseAsync(InventoryDocumentHelper.StockAdjustmentType, 11L, "ERP-527 冲销"));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(100m, db.Stocks.Single(s => s.WarehouseId == WarehouseA).Quantity);
        Assert.Equal(MaxStockQuantity, db.Stocks.Single(s => s.WarehouseId == WarehouseB).Quantity);
        Assert.False(first.IsReversed);
        Assert.False(second.IsReversed);
        Assert.Equal(2, db.StockMovements.Count());     // 未追加任何红字流水
    }

    [Fact]
    public async Task 冲销_历史流水数值不可表示_受控拒绝且零变更()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 100m);
        // 历史流水被外部改写为 6 位小数数量（DECIMAL(18,4) 无法逐位原样表示）→ 红字流水 fail closed
        var movement = SeedMovement(db, WarehouseA, Product1, direction: -1, quantity: 1.000001m, unitCost: 1m,
            amount: -1.000001m, sourceDocId: 13L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.ReverseAsync(InventoryDocumentHelper.StockAdjustmentType, 13L, "ERP-527 冲销"));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(100m, db.Stocks.Single().Quantity);
        Assert.False(movement.IsReversed);
        Assert.Single(db.StockMovements);
    }

    // ==================== 5. 多行单据：整单（含先前已记账的行）零变更 ====================

    [Fact]
    public async Task 调拨单_调入侧结存溢出_整单回滚含已调出的来源行()
    {
        using var db = TestDbFactory.Create();
        StockTransferTestAuthorization.SeedMasterData(db, WarehouseA, WarehouseB, Product1, Product2);
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        SeedStock(db, WarehouseA, Product2, quantity: 10m, totalCost: 100m);
        SeedStock(db, WarehouseB, Product1, quantity: 5m, totalCost: 50m);
        SeedStock(db, WarehouseB, Product2, quantity: MaxBalanceQuantity - 1m, totalCost: 0m);   // 第 2 行会越界

        var result = await ctl.Create(new StockTransfer
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = WarehouseA,
            ToWarehouseId = WarehouseB,
            Remark = "ERP-527_TEST",
            Details = new List<StockTransferDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Spec = "规格A", Unit = "PCS", Quantity = 1m, UnitCost = 10m },
                new() { ProductId = Product2, ProductName = "P2", Spec = "规格A", Unit = "PCS", Quantity = 2m, UnitCost = 10m }
            }
        });
        var id = CreatedId(result);
        await ctl.Submit(id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        // 整单回滚：第 1 行的调出 / 调入（已记账）与第 2 行都不得留下任何库存 / 流水 / 状态变化
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product2).Quantity);
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1).Quantity);
        Assert.Equal(MaxBalanceQuantity - 1m,
            db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product2).Quantity);
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.AsNoTracking().Single().Status);
    }

    // ==================== 测试辅助 ====================

    /// <summary>一次库存移动的审计上下文（与既有库存单据同口径，仅测试用）。</summary>
    private static InventoryMovementContext Context(long warehouseId, long productId,
        string productName = "P1", long sourceDocId = 1L) => new()
    {
        SourceDocType = InventoryDocumentHelper.StockAdjustmentType,
        SourceDocId = sourceDocId,
        SourceDocNo = $"PD-ERP527-{sourceDocId}",
        MovementType = InventoryMovementType.Adjustment,
        WarehouseId = warehouseId,
        WarehouseName = warehouseId == WarehouseA ? "仓A" : "仓B",
        ProductId = productId,
        ProductCode = "ERP527-P",
        ProductName = productName,
        Spec = "规格A",
        Unit = "PCS",
        MovementDate = DateTime.Today,
        Remark = "ERP-527_TEST"
    };

    /// <summary>播种库存行（显式给定结存数量与金额，便于构造边界场景）。</summary>
    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? InventoryService.RoundCost(totalCost / quantity) : 0m
        });
        db.SaveChanges();
    }

    /// <summary>播种一条历史库存流水（用于冲销场景；红字冲销必须能原样重放这条流水的数量 / 单价 / 金额）。</summary>
    private static StockMovement SeedMovement(ErpDbContext db, long warehouseId, long productId, int direction,
        decimal quantity, decimal unitCost, decimal amount, long sourceDocId)
    {
        var movement = new StockMovement
        {
            MovementDate = DateTime.Today,
            MovementType = InventoryMovementType.Adjustment,
            SourceDocType = InventoryDocumentHelper.StockAdjustmentType,
            SourceDocId = sourceDocId,
            SourceDocNo = $"PD-ERP527-{sourceDocId}",
            WarehouseId = warehouseId,
            WarehouseName = warehouseId == WarehouseA ? "仓A" : "仓B",
            ProductId = productId,
            ProductCode = "ERP527-P",
            ProductName = productId == Product1 ? "P1" : "P2",
            Spec = "规格A",
            Unit = "PCS",
            Direction = direction,
            Quantity = quantity,
            UnitCost = unitCost,
            Amount = amount,
            BalanceQuantity = 0m,
            BalanceAmount = 0m,
            BalanceAverageCost = 0m,
            CreatedAt = DateTime.Now
        };
        db.StockMovements.Add(movement);
        db.SaveChanges();
        return movement;
    }

    /// <summary>受控拒绝后的统一断言：零库存行、零流水、零被跟踪实体（失败不留下半截数据）。</summary>
    private static void AssertRejectedWithNoTracking(ErpDbContext db)
    {
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
}

