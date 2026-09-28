using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-096 库位 + 批次库存基础单元测试：
/// 1) 纯规则：批次 / 库位编码归一化与长度校验、调拨守恒（数量 / 成本守恒 + 拒绝负库存）；
/// 2) 移动校验：入库 / 出库 / 调拨 / 退货携带「仓库库位 + 可选批次」并通过校验 / 拒绝非法输入；
/// 3) 余额派生：库位级余额来自现有库存行、批次级余额由有效流水 + 来源单据明细批次号派生，
///    且批次拆分与商品级 / 库位级总量严格对账。
/// 说明：全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行任何生产 SQL。
/// </summary>
public class InventoryLocationLotFoundationTests
{
    private const long WarehouseA = 960001L;
    private const long WarehouseB = 960002L;
    private const long Product1 = 760001L;
    private const long Product2 = 760002L;

    // ==================== 1. 纯规则：批次 / 库位 / 调拨守恒 ====================

    [Fact]
    public void 批次号_去空白且超长拒绝()
    {
        Assert.Equal("L-01", InventoryLocationLotRules.NormalizeLot("  L-01  "));
        Assert.Equal(string.Empty, InventoryLocationLotRules.NormalizeLot("   "));
        Assert.Equal(string.Empty, InventoryLocationLotRules.NormalizeLot(null));

        var tooLong = new string('X', InventoryLocationLotRules.IdentityMaxLength + 1);
        var ex = Assert.Throws<BusinessException>(() => InventoryLocationLotRules.NormalizeLot(tooLong));
        Assert.Contains("批次号", ex.Message);
    }

    [Fact]
    public void 库位编码_去空白且超长拒绝()
    {
        Assert.Equal("A-01", InventoryLocationLotRules.NormalizeLocationCode(" A-01 "));
        Assert.Equal(string.Empty, InventoryLocationLotRules.NormalizeLocationCode(" "));

        var tooLong = new string('Y', InventoryLocationLotRules.IdentityMaxLength + 1);
        var ex = Assert.Throws<BusinessException>(() => InventoryLocationLotRules.NormalizeLocationCode(tooLong));
        Assert.Contains("库位编码", ex.Message);
    }

    [Fact]
    public void 调拨守恒_库存充足时通过且数量成本守恒()
    {
        var result = InventoryLocationLotRules.EvaluateTransfer(WarehouseA, WarehouseB, quantity: 10m, unitCost: 12.5m, sourceOnHand: 30m);

        Assert.True(result.IsValid);
        Assert.True(result.QuantityConserved);
        Assert.True(result.CostConserved);
        Assert.True(result.SourceSufficient);
    }

    [Fact]
    public void 调拨守恒_负库存被拒绝()
    {
        var result = InventoryLocationLotRules.EvaluateTransfer(WarehouseA, WarehouseB, quantity: 10m, unitCost: 12.5m, sourceOnHand: 5m);

        Assert.False(result.IsValid);
        Assert.False(result.SourceSufficient);
        Assert.Contains("负库存", result.Message);
    }

    [Fact]
    public void 调拨守恒_调出调入仓相同被拒绝()
    {
        var result = InventoryLocationLotRules.EvaluateTransfer(WarehouseA, WarehouseA, quantity: 10m, unitCost: 12.5m, sourceOnHand: 30m);

        Assert.False(result.IsValid);
        Assert.Contains("不能相同", result.Message);
    }

    // ==================== 2. 移动校验 ====================

    [Fact]
    public async Task 入库移动_携带库位与批次_返回归一化结果()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "WH-A", "主仓");
        var service = new InventoryLocationLotService(db);

        var validated = await service.ValidateMovementAsync(new MovementLocationLotInput
        {
            Kind = InventoryLocationLotMovementKind.StockIn,
            WarehouseId = WarehouseA,
            LocationCode = " A-01 ",
            LotNo = " L-2026 ",
            ProductId = Product1,
            ProductName = "商品1",
            Quantity = 10m
        });

        Assert.Equal(WarehouseA, validated.WarehouseId);
        Assert.Equal("主仓", validated.WarehouseName);
        Assert.Equal("A-01", validated.LocationCode);
        Assert.Equal("L-2026", validated.LotNo);
    }

    [Fact]
    public async Task 移动校验_仓库不存在_抛业务异常()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryLocationLotService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.ValidateMovementAsync(
            new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.StockOut,
                WarehouseId = 999999L,
                Quantity = 1m,
                ProductName = "商品1"
            }));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task 移动校验_调拨两仓相同_抛业务异常()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "WH-A", "主仓");
        var service = new InventoryLocationLotService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.ValidateMovementAsync(
            new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.Transfer,
                WarehouseId = WarehouseA,
                ToWarehouseId = WarehouseA,
                Quantity = 1m,
                ProductName = "商品1"
            }));
        Assert.Contains("不能相同", ex.Message);
    }

    [Fact]
    public async Task 移动校验_数量非正_抛业务异常()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "WH-A", "主仓");
        var service = new InventoryLocationLotService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.ValidateMovementAsync(
            new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.Return,
                WarehouseId = WarehouseA,
                Quantity = 0m,
                ProductName = "商品1"
            }));
        Assert.Contains("大于 0", ex.Message);
    }

    // ==================== 3. 余额派生 ====================

    [Fact]
    public async Task 余额_库位批次拆分与商品总量严格对账()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "WH-A", "主仓");
        SeedProduct(db, Product1, "P1", "商品1", "大", "PCS");
        SeedProduct(db, Product2, "P2", "商品2", "中", "PCS");

        // 库位级现存量（权威库存行）
        db.Stocks.Add(new Stock { WarehouseId = WarehouseA, ProductId = Product1, Quantity = 30m, AvailableQuantity = 30m, TotalCost = 300m, AverageCost = 10m });
        db.Stocks.Add(new Stock { WarehouseId = WarehouseA, ProductId = Product2, Quantity = 5m, AvailableQuantity = 5m, TotalCost = 100m, AverageCost = 20m });

        // 批次来源：两张入库单，各一条明细（批次唯一可归属）
        db.StockInDetails.Add(new StockInDetail { StockInId = 10L, ProductId = Product1, BatchNo = "L1" });
        db.StockInDetails.Add(new StockInDetail { StockInId = 11L, ProductId = Product1, BatchNo = "L2" });
        db.StockMovements.Add(new StockMovement { SourceDocType = "StockIn", SourceDocId = 10L, WarehouseId = WarehouseA, ProductId = Product1, Direction = 1, Quantity = 20m, Amount = 200m });
        db.StockMovements.Add(new StockMovement { SourceDocType = "StockIn", SourceDocId = 11L, WarehouseId = WarehouseA, ProductId = Product1, Direction = 1, Quantity = 10m, Amount = 100m });
        await db.SaveChangesAsync();

        var service = new InventoryLocationLotService(db);
        var report = await service.GetLocationLotBalancesAsync(null, null);

        Assert.True(report.IsReconciled);

        // 商品级总量与库位行完全一致
        Assert.Equal(2, report.ProductTotals.Count);
        Assert.Equal(30m, report.ProductTotals.Single(t => t.ProductId == Product1).Quantity);
        Assert.Equal(5m, report.ProductTotals.Single(t => t.ProductId == Product2).Quantity);

        // 库位级余额直接来自 Stocks
        Assert.Equal(2, report.LocationLines.Count);
        Assert.Equal(30m, report.LocationLines.Single(l => l.ProductId == Product1).Quantity);
        Assert.Equal("主仓", report.LocationLines.Single(l => l.ProductId == Product1).WarehouseName);

        // 批次级拆分：L1=20、L2=10；P2 无流水 → 空批次桶兜底 5，保证与库位总量对账
        Assert.Equal(20m, report.LotLines.Single(l => l.ProductId == Product1 && l.LotNo == "L1").Quantity);
        Assert.Equal(10m, report.LotLines.Single(l => l.ProductId == Product1 && l.LotNo == "L2").Quantity);
        Assert.Equal(5m, report.LotLines.Single(l => l.ProductId == Product2 && l.LotNo == string.Empty).Quantity);
    }

    [Fact]
    public async Task 余额_同商品多批次无法唯一归属_计入空批次桶不臆造()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "WH-A", "主仓");
        SeedProduct(db, Product1, "P1", "商品1", "大", "PCS");

        db.Stocks.Add(new Stock { WarehouseId = WarehouseA, ProductId = Product1, Quantity = 30m, AvailableQuantity = 30m, TotalCost = 300m, AverageCost = 10m });

        // 同一张入库单同一商品两条明细（不同批次）→ 无法唯一归属，均计入空批次桶
        db.StockInDetails.Add(new StockInDetail { StockInId = 20L, ProductId = Product1, BatchNo = "A" });
        db.StockInDetails.Add(new StockInDetail { StockInId = 20L, ProductId = Product1, BatchNo = "B" });
        db.StockMovements.Add(new StockMovement { SourceDocType = "StockIn", SourceDocId = 20L, WarehouseId = WarehouseA, ProductId = Product1, Direction = 1, Quantity = 30m, Amount = 300m });
        await db.SaveChangesAsync();

        var report = await new InventoryLocationLotService(db).GetLocationLotBalancesAsync(null, null);

        Assert.True(report.IsReconciled);
        var unallocated = report.LotLines.Single(l => l.ProductId == Product1);
        Assert.Equal(string.Empty, unallocated.LotNo);
        Assert.Equal(30m, unallocated.Quantity);
    }

    // ==================== 测试辅助 ====================

    private static void SeedWarehouse(ErpDbContext db, long id, string code, string name)
    {
        db.BaseWarehouses.Add(new BaseWarehouse { Id = id, WarehouseCode = code, WarehouseName = name, Status = 1 });
        db.SaveChanges();
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name, string spec, string unit)
    {
        db.BaseProducts.Add(new BaseProduct { Id = id, ProductCode = code, ProductName = name, Spec = spec, Unit = unit });
        db.SaveChanges();
    }
}
