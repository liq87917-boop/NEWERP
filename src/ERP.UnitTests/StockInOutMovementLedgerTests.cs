using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-025 采购入库 / 销售出库接入库存流水（StockMovement）单元测试：
/// 1) 审核按基础单位写流水（方向 / 数量 / 成本 / 结存快照 / 来源单据可追溯）；
/// 2) 重复审核被幂等护栏拒绝且不产生第二条流水；
/// 3) 取消按流水生成红字冲销并还原库存，重复取消被拒绝；
/// 4) 出库库存不足整单拒绝、不写流水；
/// 5) 本次改造前已审核、没有流水的历史单据取消时按基础单位原路冲回 / 恢复；
/// 6) 包装单位 → 基础单位换算后的数量与流水数量一致。
/// 说明：全部使用内存数据库，不连接 SQL Server、不触碰任何业务库数据。
/// </summary>
public class StockInOutMovementLedgerTests
{
    private const long WarehouseA = 920001L;
    private const long Product1 = 720001L;

    // ==================== 采购入库 ====================

    [Fact]
    public async Task 采购入库_审核_写流水且库存与流水金额一致()
    {
        using var db = TestDbFactory.Create();
        // 已有库存 10 × 10 = 100（均价 10）：入库明细没有成本列，按当前加权平均成本计价
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewStockInController(db);
        var id = await CreateStockInAsync(ctl, quantity: 10m, unit: "PCS", purchaseOrderId: 321L);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        Assert.Equal(20m, stock.Quantity);
        Assert.Equal(20m, stock.AvailableQuantity);
        Assert.Equal(200m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.PurchaseIn, movement.MovementType);
        Assert.Equal(InventoryDocumentHelper.StockInType, movement.SourceDocType);
        Assert.Equal(id, movement.SourceDocId);
        Assert.Equal(db.StockIns.Single().StockInNo, movement.SourceDocNo);
        Assert.Equal(WarehouseA, movement.WarehouseId);
        Assert.Equal(Product1, movement.ProductId);
        Assert.Equal(1, movement.Direction);
        Assert.Equal(10m, movement.Quantity);
        Assert.Equal(10m, movement.UnitCost);
        Assert.Equal(100m, movement.Amount);
        Assert.Equal(20m, movement.BalanceQuantity);
        Assert.Equal(200m, movement.BalanceAmount);
        Assert.Equal(10m, movement.BalanceAverageCost);
        Assert.Contains("采购订单 Id 321", movement.Remark);
        Assert.False(movement.IsReversal);
        // 账实关系：本次入库流水净额（100）= 库存金额相对「已有库存 100」基准的变动额
        Assert.Equal(100m, db.StockMovements.Sum(m => m.Amount));

        // 单据状态与流水可追溯（GET /api/stock-ins/{id}/movements）
        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
        Assert.Single(TraceMovements(await ctl.GetMovements(id)));
    }

    [Fact]
    public async Task 采购入库_首次入库_无历史成本时成本为0且数量为基础单位数量()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewStockInController(db);
        var id = await CreateStockInAsync(ctl, quantity: 10m, unit: "PCS", purchaseOrderId: null);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(10m, stock.AvailableQuantity);
        Assert.Equal(0m, stock.TotalCost);
        Assert.Equal(0m, stock.AverageCost);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.Quantity);
        Assert.Equal(0m, movement.UnitCost);                                  // 不臆造成本：无来源成本时按 0 落账
        Assert.Equal("采购入库单审核入库", movement.Remark);
    }

    [Fact]
    public async Task 采购入库_重复审核_幂等护栏拒绝且不产生第二条流水()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewStockInController(db);
        var id = await CreateStockInAsync(ctl, quantity: 6m, unit: "PCS", purchaseOrderId: null);
        await ctl.Submit(id);
        await ctl.Approve(id);

        // 第一次重复审核：状态护栏（已审核不再是已提交）
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 把状态手工改回已提交（绕过状态护栏）：流水护栏必须兜住，库存不得被重复增加
        var entity = db.StockIns.Single();
        entity.Status = DocumentStatus.Submitted;
        db.SaveChanges();

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Contains("已产生库存流水", again.Message);

        Assert.Equal(6m, db.Stocks.Single().Quantity);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task 采购入库_取消_红字流水冲回库存_重复取消被拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewStockInController(db);
        var id = await CreateStockInAsync(ctl, quantity: 10m, unit: "PCS", purchaseOrderId: null);
        await ctl.Submit(id);
        await ctl.Approve(id);
        Assert.Equal(20m, db.Stocks.Single().Quantity);

        await ctl.Cancel(id);

        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(10m, stock.AvailableQuantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single().Status);

        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].IsReversed);
        Assert.False(movements[0].IsReversal);
        Assert.True(movements[1].IsReversal);
        Assert.Equal(movements[0].Id, movements[1].ReversalOfMovementId);
        Assert.Equal(-1, movements[1].Direction);
        Assert.Equal(-100m, movements[1].Amount);
        // 入库流水（+100）与红字冲销流水（-100）净额归零，库存金额回到取消前基准
        Assert.Equal(0m, db.StockMovements.Sum(m => m.Amount));

        // 重复取消：拒绝且不新增流水、库存不再变动
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(id));
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(2, db.StockMovements.Count());
        Assert.Equal(10m, db.Stocks.Single().Quantity);
    }

    // ==================== 销售出库 ====================

    [Fact]
    public async Task 销售出库_审核_按当前加权平均成本核减并写流水()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);      // 均价 10
        var ctl = NewStockOutController(db);
        var id = await CreateStockOutAsync(ctl, quantity: 4m, unit: "PCS", salesOrderId: 88L);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single();
        Assert.Equal(6m, stock.Quantity);
        Assert.Equal(6m, stock.AvailableQuantity);
        Assert.Equal(60m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.SalesOut, movement.MovementType);
        Assert.Equal(InventoryDocumentHelper.StockOutType, movement.SourceDocType);
        Assert.Equal(id, movement.SourceDocId);
        Assert.Equal(db.StockOuts.Single().StockOutNo, movement.SourceDocNo);
        Assert.Equal(WarehouseA, movement.WarehouseId);
        Assert.Equal(-1, movement.Direction);
        Assert.Equal(4m, movement.Quantity);
        Assert.Equal(10m, movement.UnitCost);
        Assert.Equal(-40m, movement.Amount);
        Assert.Equal(6m, movement.BalanceQuantity);
        Assert.Equal(60m, movement.BalanceAmount);
        Assert.Contains("销售订单 Id 88", movement.Remark);
        // 账实关系：本次出库流水净额（-40）= 库存金额相对「已有库存 100」基准的变动额
        Assert.Equal(-40m, db.StockMovements.Sum(m => m.Amount));

        Assert.Equal(DocumentStatus.Approved, db.StockOuts.Single().Status);
        Assert.Single(TraceMovements(await ctl.GetMovements(id)));
    }

    [Fact]
    public async Task 销售出库_库存不足_整单拒绝且不写流水()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var ctl = NewStockOutController(db);
        var id = await CreateStockOutAsync(ctl, quantity: 8m, unit: "PCS", salesOrderId: null);
        await ctl.Submit(id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("库存不足", ex.Message);

        Assert.Equal(5m, db.Stocks.Single().Quantity);                        // 未扣减
        Assert.Equal(50m, db.Stocks.Single().TotalCost);
        Assert.Empty(db.StockMovements);                                     // 不落半截流水
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single().Status);
    }

    [Fact]
    public async Task 销售出库_重复审核_幂等护栏拒绝且不产生第二条流水()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewStockOutController(db);
        var id = await CreateStockOutAsync(ctl, quantity: 4m, unit: "PCS", salesOrderId: null);
        await ctl.Submit(id);
        await ctl.Approve(id);

        // 绕过状态护栏（手工改回已提交）后，流水护栏必须兜住
        var entity = db.StockOuts.Single();
        entity.Status = DocumentStatus.Submitted;
        db.SaveChanges();

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Contains("已产生库存流水", again.Message);

        Assert.Equal(6m, db.Stocks.Single().Quantity);                        // 没有被重复扣减
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task 销售出库_取消_红字流水还原库存与金额_重复取消被拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewStockOutController(db);
        var id = await CreateStockOutAsync(ctl, quantity: 4m, unit: "PCS", salesOrderId: null);
        await ctl.Submit(id);
        await ctl.Approve(id);
        Assert.Equal(6m, db.Stocks.Single().Quantity);

        await ctl.Cancel(id);

        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(10m, stock.AvailableQuantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single().Status);

        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].IsReversed);
        Assert.True(movements[1].IsReversal);
        Assert.Equal(1, movements[1].Direction);
        Assert.Equal(40m, movements[1].Amount);
        // 出库流水（-40）与红字冲销流水（+40）净额归零，库存金额回到取消前基准
        Assert.Equal(0m, db.StockMovements.Sum(m => m.Amount));

        var trace = TraceMovements(await ctl.GetMovements(id));
        Assert.Equal(2, trace.Count);
        Assert.True(trace[1].IsReversal);                                    // 按单据可追溯到红字冲销行

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(id));
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(2, db.StockMovements.Count());
    }

    // ==================== 历史（无流水）单据兼容 + 基础单位换算 ====================

    [Fact]
    public async Task 历史无流水已审核入库单_取消_按基础单位原路冲回()
    {
        using var db = TestDbFactory.Create();
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA, ProductId = Product1, Quantity = 10m, AvailableQuantity = 10m
        });
        var entity = new StockIn
        {
            StockInNo = "RK-LEGACY",
            StockInDate = DateTime.Today,
            SupplierId = 1L,
            WarehouseId = WarehouseA,
            TotalQuantity = 10m,
            Status = DocumentStatus.Approved,
            Details = new List<StockInDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Unit = "PCS", Quantity = 10m }
            }
        };
        db.StockIns.Add(entity);
        db.SaveChanges();

        var ctl = NewStockInController(db);
        await ctl.Cancel(entity.Id);

        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(0m, db.Stocks.Single().AvailableQuantity);
        Assert.Empty(db.StockMovements);                                     // 历史单据没有流水可冲销
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single().Status);
    }

    [Fact]
    public async Task 历史无流水已审核出库单_取消_按基础单位原路恢复()
    {
        using var db = TestDbFactory.Create();
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA, ProductId = Product1, Quantity = 0m, AvailableQuantity = 0m
        });
        var entity = new StockOut
        {
            StockOutNo = "CK-LEGACY",
            StockOutDate = DateTime.Today,
            CustomerId = 1L,
            WarehouseId = WarehouseA,
            TotalQuantity = 5m,
            Status = DocumentStatus.Approved,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Unit = "PCS", Quantity = 5m }
            }
        };
        db.StockOuts.Add(entity);
        db.SaveChanges();

        var ctl = NewStockOutController(db);
        await ctl.Cancel(entity.Id);

        Assert.Equal(5m, db.Stocks.Single().Quantity);
        Assert.Equal(5m, db.Stocks.Single().AvailableQuantity);
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single().Status);
    }

    [Fact]
    public async Task 包装单位入库_流水数量与库存数量都是基础单位数量()
    {
        using var db = TestDbFactory.Create();
        var product = new BaseProduct
        {
            ProductCode = "P-BOX", ProductName = "Widget",
            Unit = "PCS", PackageUnit = "CTN", UnitsPerPackage = 12
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();

        var ctl = NewStockInController(db);
        var result = await ctl.Create(new StockIn
        {
            StockInDate = DateTime.Today,
            SupplierId = 1L,
            WarehouseId = WarehouseA,
            Remark = "ERP-025_TEST",
            Details = new List<StockInDetail>
            {
                new() { ProductId = product.Id, ProductName = product.ProductName, Unit = "CTN", Quantity = 2m }
            }
        });
        var id = CreatedId(result);
        await ctl.Submit(id);
        await ctl.Approve(id);

        Assert.Equal(24m, db.StockInDetails.Single().Quantity);               // 2 CTN → 24 PCS
        Assert.Equal(24m, db.Stocks.Single(s => s.ProductId == product.Id).Quantity);

        var movement = db.StockMovements.Single();
        Assert.Equal(24m, movement.Quantity);
        Assert.Equal("PCS", movement.Unit);
    }

    // ==================== 测试辅助 ====================

    private static StockInController NewStockInController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static StockOutController NewStockOutController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static async Task<long> CreateStockInAsync(StockInController ctl, decimal quantity, string unit,
        long? purchaseOrderId)
    {
        var result = await ctl.Create(new StockIn
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = 1L,
            WarehouseId = WarehouseA,
            Remark = "ERP-025_TEST",
            Details = new List<StockInDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Spec = "大", Unit = unit, Quantity = quantity }
            }
        });
        return CreatedId(result);
    }

    private static async Task<long> CreateStockOutAsync(StockOutController ctl, decimal quantity, string unit,
        long? salesOrderId)
    {
        var result = await ctl.Create(new StockOut
        {
            StockOutDate = DateTime.Today,
            SalesOrderId = salesOrderId,
            CustomerId = 1L,
            WarehouseId = WarehouseA,
            Remark = "ERP-025_TEST",
            Details = new List<StockOutDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Spec = "大", Unit = unit, Quantity = quantity }
            }
        });
        return CreatedId(result);
    }

    /// <summary>读取 Create 接口返回的 data.Id（控制器统一返回 { Id, XxxNo }）</summary>
    private static long CreatedId(IActionResult result)
    {
        Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    /// <summary>读取 GetMovements 接口返回的库存流水集合</summary>
    private static IReadOnlyList<StockMovement> TraceMovements(IActionResult result)
    {
        Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<IReadOnlyList<StockMovement>>>(
            Assert.IsType<OkObjectResult>(result).Value);
        return response.Data!;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity, decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        db.SaveChanges();
    }
}
