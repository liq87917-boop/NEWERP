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
/// ERP-009 库存单据与库存成本单元测试：
/// 1) 盘点/调整：差异计算、审核按差异改库存（恰好一次）、重复审核拒绝、销审冲销还原；
/// 2) 调拨：数量守恒（总量不变）、成本守恒（调出金额 = 调入金额）、库存不足拒绝、销审冲销；
/// 3) 退货：销售退货入库 / 采购退货出库、来源单据成本回取、库存不足拒绝、销审冲销；
/// 4) 成本：移动加权平均成本（入库加权、出库按均价核减）、库存流水成本基准持久化；
/// 5) 单据字轨前缀与库存流水（含红字冲销）查询。
/// 说明：全部使用内存数据库，不连接 SQL Server、不触碰任何业务库数据。
/// </summary>
public class InventoryMovementTests
{
    private const long WarehouseA = 900001L;
    private const long WarehouseB = 900002L;
    private const long Product1 = 700001L;
    private const long Product2 = 700002L;

    // ==================== 库存盘点 / 调整 ====================

    [Fact]
    public async Task 盘点单_创建_差异数量与差异金额由后端复核()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewAdjustmentController(db);

        var result = await ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = 10m, ActualQuantity = 8m, UnitCost = 12.5m },
                new() { ProductId = Product2, ProductName = "P2", BookQuantity = 5m, ActualQuantity = 9m, UnitCost = 4m }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var entity = db.StockAdjustments.Single();
        Assert.StartsWith("PD", entity.AdjustmentNo);
        Assert.Equal(DocumentStatus.Pending, entity.Status);
        Assert.Equal(2m, entity.TotalDiffQuantity);              // -2 + 4
        Assert.Equal(-9m, entity.TotalDiffAmount);               // -2*12.5 + 4*4

        var details = db.StockAdjustmentDetails.OrderBy(d => d.SortNo).ToList();
        Assert.Equal(1, details[0].SortNo);
        Assert.Equal(2, details[1].SortNo);
        Assert.Equal(-2m, details[0].DiffQuantity);
        Assert.Equal(-25m, details[0].DiffAmount);
        Assert.Equal(4m, details[1].DiffQuantity);
        Assert.Equal(16m, details[1].DiffAmount);
    }

    [Fact]
    public async Task 盘点单_审核_按差异调整库存并写流水_恰好一次()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);      // 均价 10
        var ctl = NewAdjustmentController(db);
        var id = await CreateAdjustmentAsync(ctl, bookQuantity: 10m, actualQuantity: 7m, unitCost: 10m);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        Assert.Equal(7m, stock.Quantity);
        Assert.Equal(70m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.Adjustment, movement.MovementType);
        Assert.Equal(-1, movement.Direction);
        Assert.Equal(3m, movement.Quantity);
        Assert.Equal(-30m, movement.Amount);
        Assert.Equal(7m, movement.BalanceQuantity);
        Assert.Equal(70m, movement.BalanceAmount);
        Assert.Equal(InventoryDocumentHelper.StockAdjustmentType, movement.SourceDocType);
        Assert.Equal(db.StockAdjustments.Single().AdjustmentNo, movement.SourceDocNo);
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single().Status);

        // 重复审核：既不改库存也不新增流水
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(7m, db.Stocks.Single().Quantity);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task 盘点单_销审_库存还原并生成红字流水_重复销审拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewAdjustmentController(db);
        var id = await CreateAdjustmentAsync(ctl, bookQuantity: 10m, actualQuantity: 12m, unitCost: 10m);

        await ctl.Submit(id);
        await ctl.Approve(id);
        Assert.Equal(12m, db.Stocks.Single(s => s.ProductId == Product1).Quantity);

        await ctl.Unaudit(id);

        var stock = db.Stocks.Single(s => s.ProductId == Product1);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);
        Assert.Equal(DocumentStatus.Pending, db.StockAdjustments.Single().Status);

        // 原流水被标记为已冲销，并追加一条红字流水（不删除历史，可审计）
        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].IsReversed);
        Assert.True(movements[1].IsReversal);
        Assert.Equal(-1, movements[1].Direction);
        Assert.Equal(-20m, movements[1].Amount);
        Assert.Equal(movements[0].Id, movements[1].ReversalOfMovementId);

        // 重复销审：状态已回到待提交，直接拒绝（不会重复冲销）
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(2, db.StockMovements.Count());
    }

    [Fact]
    public async Task 盘点单_销审_入库已被后续占用_拒绝且库存不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 0m, totalCost: 0m);
        var ctl = NewAdjustmentController(db);
        var id = await CreateAdjustmentAsync(ctl, bookQuantity: 0m, actualQuantity: 5m, unitCost: 10m);

        await ctl.Submit(id);
        await ctl.Approve(id);
        Assert.Equal(5m, db.Stocks.Single().Quantity);

        // 模拟后续业务把盘盈的货领走（只剩 1）
        var stock = db.Stocks.Single();
        stock.Quantity = 1m;
        stock.TotalCost = 10m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(1m, db.Stocks.Single().Quantity);                        // 拒绝销审时不改动库存
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single().Status);
        Assert.False(db.StockMovements.Single().IsReversed);
    }

    [Fact]
    public async Task 盘点单_已审核_不能取消也不能删除_需先销审()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var ctl = NewAdjustmentController(db);
        var id = await CreateAdjustmentAsync(ctl, bookQuantity: 5m, actualQuantity: 4m, unitCost: 10m);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var cancelEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(id));
        Assert.Equal(ErrorCodes.RuleConflict, cancelEx.Code);
        var deleteEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(id));
        Assert.Equal(ErrorCodes.RuleConflict, deleteEx.Code);
        Assert.False(db.StockAdjustments.Single().IsDeleted);
    }

    [Fact]
    public async Task 盘点单_未提交不能审核_无明细不能审核()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewAdjustmentController(db);
        var id = await CreateAdjustmentAsync(ctl, bookQuantity: 1m, actualQuantity: 2m, unitCost: 1m);

        var pendingEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, pendingEx.Code);

        await ctl.Submit(id);
        var empty = new StockAdjustment { AdjustmentNo = "PD-EMPTY", WarehouseId = WarehouseA };
        db.StockAdjustments.Add(empty);
        db.SaveChanges();

        var emptyEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(empty.Id));
        Assert.Equal(ErrorCodes.RuleConflict, emptyEx.Code);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 仓库调拨 ====================

    [Fact]
    public async Task 调拨单_审核_两仓数量守恒且成本单价一致()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);   // 均价 10
        var ctl = NewTransferController(db);
        var id = await CreateTransferAsync(ctl, quantity: 30m, unitCost: 0m);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        var to = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1);
        Assert.Equal(70m, from.Quantity);
        Assert.Equal(700m, from.TotalCost);
        Assert.Equal(30m, to.Quantity);
        Assert.Equal(300m, to.TotalCost);
        // 数量守恒：两仓合计不变；金额守恒：调出减少额 = 调入增加额
        Assert.Equal(100m, from.Quantity + to.Quantity);
        Assert.Equal(1000m, from.TotalCost + to.TotalCost);
        Assert.Equal(10m, from.AverageCost);
        Assert.Equal(10m, to.AverageCost);

        var transfer = db.StockTransfers.Single();
        Assert.Equal(30m, transfer.TotalQuantity);
        Assert.Equal(300m, transfer.TotalAmount);
        Assert.Equal(10m, db.StockTransferDetails.Single().UnitCost);            // 成本单价回填到明细

        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.Equal(InventoryMovementType.TransferOut, movements[0].MovementType);
        Assert.Equal(-1, movements[0].Direction);
        Assert.Equal(WarehouseA, movements[0].WarehouseId);
        Assert.Equal(InventoryMovementType.TransferIn, movements[1].MovementType);
        Assert.Equal(1, movements[1].Direction);
        Assert.Equal(WarehouseB, movements[1].WarehouseId);
        Assert.Equal(movements[0].UnitCost, movements[1].UnitCost);              // 两侧同一成本单价
        Assert.Equal(-movements[0].Amount, movements[1].Amount);                 // 金额一进一出守恒
    }

    [Fact]
    public async Task 调拨单_审核_库存不足_拒绝且不产生任何流水()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var ctl = NewTransferController(db);
        var id = await CreateTransferAsync(ctl, quantity: 20m, unitCost: 10m);

        await ctl.Submit(id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA).Quantity);
        Assert.DoesNotContain(db.Stocks, s => s.WarehouseId == WarehouseB);      // 调入仓未被创建
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single().Status);   // 状态保持已提交，可修正后重审
    }

    [Fact]
    public async Task 调拨单_同仓调拨被拒绝_审核写入流水恰好一次()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewTransferController(db);

        var same = new StockTransfer
        {
            TransferNo = "DB-SAME",
            FromWarehouseId = WarehouseA,
            ToWarehouseId = WarehouseA,
            Status = DocumentStatus.Submitted,
            Details = new List<StockTransferDetail> { new() { ProductId = Product1, ProductName = "P1", Quantity = 1m, UnitCost = 10m } }
        };
        db.StockTransfers.Add(same);
        db.SaveChanges();

        var sameEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(same.Id));
        Assert.Equal(ErrorCodes.RuleConflict, sameEx.Code);
        Assert.Empty(db.StockMovements);

        var id = await CreateTransferAsync(ctl, quantity: 5m, unitCost: 10m);
        await ctl.Submit(id);
        await ctl.Approve(id);
        Assert.Equal(2, db.StockMovements.Count());

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(2, db.StockMovements.Count());                              // 重复审核不新增流水
    }

    [Fact]
    public async Task 调拨单_销审_两仓库存还原_调入被消耗后拒绝销审()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        var ctl = NewTransferController(db);
        var id = await CreateTransferAsync(ctl, quantity: 40m, unitCost: 10m);

        await ctl.Submit(id);
        await ctl.Approve(id);
        await ctl.Unaudit(id);

        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        var to = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1);
        Assert.Equal(100m, from.Quantity);
        Assert.Equal(1000m, from.TotalCost);
        Assert.Equal(0m, to.Quantity);
        Assert.Equal(0m, to.TotalCost);
        Assert.Equal(4, db.StockMovements.Count());                              // 2 条原流水 + 2 条红字冲销
        Assert.True(db.StockMovements.Count(m => m.IsReversal) == 2);

        // 第二次：调入仓的货已被领走 → 拒绝销审（不出现负库存）
        var second = await CreateTransferAsync(ctl, quantity: 10m, unitCost: 10m);
        await ctl.Submit(second);
        await ctl.Approve(second);
        var destStock = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1);
        destStock.Quantity = 0m;
        destStock.TotalCost = 0m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(second));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.StockTransfers.Single(t => t.Id == second).Status);
    }

    // ==================== 销售退货 / 采购退货 ====================

    [Fact]
    public async Task 销售退货_审核_退货入库并按明细成本计价_来源单据可追溯()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewSalesReturnController(db);
        var id = await CreateSalesReturnAsync(ctl, quantity: 4m, unitPrice: 25m, unitCost: 8m,
            sourceStockOutId: 88L, sourceNo: "CK2609230001");

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        Assert.Equal(4m, stock.Quantity);
        Assert.Equal(32m, stock.TotalCost);                                      // 4 × 8
        Assert.Equal(8m, stock.AverageCost);

        var entity = db.SalesReturns.Single();
        Assert.Equal(100m, entity.TotalAmount);                                  // 4 × 25（对外结算金额）
        Assert.Equal(88L, entity.SourceStockOutId);
        Assert.Equal("CK2609230001", entity.SourceStockOutNo);                   // 来源出库单可追溯
        Assert.Equal("质量", entity.ReturnReason);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.SalesReturn, movement.MovementType);
        Assert.Equal(1, movement.Direction);
        Assert.Equal(8m, movement.UnitCost);
        Assert.Equal(32m, movement.Amount);
        Assert.Equal(4m, movement.BalanceQuantity);
        Assert.Contains("CK2609230001", movement.Remark);                        // 流水里也保留来源出库单

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Single(db.StockMovements);

        await ctl.Unaudit(id);
        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(0m, db.Stocks.Single().TotalCost);
        Assert.Equal(2, db.StockMovements.Count());
    }

    [Fact]
    public async Task 销售退货_明细未填成本_按来源出库流水成本计价()
    {
        using var db = TestDbFactory.Create();
        // 来源销售出库单（真实单据行，用于来源单号回填）+ 来源出库流水（成本 6）
        db.StockOuts.Add(new StockOut
        {
            Id = 501L,
            StockOutNo = "CK2609230501",
            StockOutDate = DateTime.Today,
            CustomerId = 1L,
            WarehouseId = WarehouseA,
            TotalQuantity = 10m
        });
        db.StockMovements.Add(new StockMovement
        {
            MovementDate = DateTime.Today,
            MovementType = InventoryMovementType.SalesOut,
            SourceDocType = InventoryDocumentHelper.StockOutType,
            SourceDocId = 501L,
            SourceDocNo = "CK2609230501",
            WarehouseId = WarehouseA,
            ProductId = Product1,
            ProductName = "P1",
            Direction = -1,
            Quantity = 10m,
            UnitCost = 6m,
            Amount = -60m
        });
        db.SaveChanges();

        var ctl = NewSalesReturnController(db);
        var id = await CreateSalesReturnAsync(ctl, quantity: 5m, unitPrice: 20m, unitCost: 0m,
            sourceStockOutId: 501L, sourceNo: string.Empty);
        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single(m => m.MovementType == InventoryMovementType.SalesReturn);
        Assert.Equal(6m, movement.UnitCost);                                      // 回取来源出库流水成本
        Assert.Equal(30m, movement.Amount);
        Assert.Equal("CK2609230501", db.SalesReturns.Single().SourceStockOutNo);   // 来源单号按来源 Id 自动带出
        Assert.Equal(30m, db.Stocks.Single(s => s.ProductId == Product1).TotalCost);
    }

    [Fact]
    public async Task 采购退货_审核_退货出库_库存不足拒绝且库存不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 60m);       // 均价 6
        var ctl = NewPurchaseReturnController(db);
        var id = await CreatePurchaseReturnAsync(ctl, quantity: 4m, unitPrice: 7m, unitCost: 0m,
            sourceStockInId: 77L, sourceNo: "RK2609230077");

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single();
        Assert.Equal(6m, stock.Quantity);
        Assert.Equal(36m, stock.TotalCost);
        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.PurchaseReturn, movement.MovementType);
        Assert.Equal(-1, movement.Direction);
        Assert.Equal(6m, movement.UnitCost);                                     // 明细未填成本 → 按当前加权平均成本
        Assert.Equal(28m, db.PurchaseReturns.Single().TotalAmount);

        // 超出库存的退货：拒绝且不改库存、不产生流水
        var tooMany = await CreatePurchaseReturnAsync(ctl, quantity: 100m, unitPrice: 7m, unitCost: 0m,
            sourceStockInId: null, sourceNo: string.Empty);
        await ctl.Submit(tooMany);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(tooMany));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(6m, db.Stocks.Single().Quantity);
        Assert.Single(db.StockMovements);

        // 销审：出库被冲销、库存还原
        await ctl.Unaudit(id);
        Assert.Equal(10m, db.Stocks.Single().Quantity);
        Assert.Equal(60m, db.Stocks.Single().TotalCost);
    }

    // ==================== 库存成本（移动加权平均法） ====================

    [Fact]
    public async Task 成本_两次入库_按移动加权平均计算_出库按均价核减()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var context = Context(WarehouseA, Product1);

        await service.IncreaseAsync(context, 10m, 10m);      // 10 × 10 = 100
        await db.SaveChangesAsync();
        await service.IncreaseAsync(context, 10m, 14m);      // +10 × 14 = 140 → 均价 12
        await db.SaveChangesAsync();

        var stock = db.Stocks.Single();
        Assert.Equal(20m, stock.Quantity);
        Assert.Equal(240m, stock.TotalCost);
        Assert.Equal(12m, stock.AverageCost);

        var outMovement = await service.DecreaseAsync(context, 5m, 0m);          // 未指定成本 → 用均价
        await db.SaveChangesAsync();
        Assert.Equal(12m, outMovement.UnitCost);
        Assert.Equal(-60m, outMovement.Amount);
        Assert.Equal(15m, stock.Quantity);
        Assert.Equal(180m, stock.TotalCost);
        Assert.Equal(12m, stock.AverageCost);
    }

    [Fact]
    public async Task 成本_数量归零后金额与均价一并归零()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var context = Context(WarehouseA, Product1);

        await service.IncreaseAsync(context, 3m, 10m);
        await db.SaveChangesAsync();
        await service.DecreaseAsync(context, 3m, 0m);
        await db.SaveChangesAsync();

        var stock = db.Stocks.Single();
        Assert.Equal(0m, stock.Quantity);
        Assert.Equal(0m, stock.TotalCost);
        Assert.Equal(0m, stock.AverageCost);
    }

    [Fact]
    public async Task 成本_流水持久化成本基准与移动后结存快照()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var context = Context(WarehouseA, Product2);

        var movement = await service.IncreaseAsync(context, 7m, 3.333333m);
        await db.SaveChangesAsync();

        Assert.Equal(3.333333m, movement.UnitCost);          // 成本基准按 6 位小数持久化
        Assert.Equal(23.3333m, movement.Amount);             // 金额按 4 位小数（7 × 3.333333 = 23.333331）
        Assert.Equal(7m, movement.BalanceQuantity);
        Assert.Equal(23.3333m, movement.BalanceAmount);
        Assert.Equal(3.333329m, movement.BalanceAverageCost); // 结存均价 = 结存金额 / 结存数量，可与库存表互相核对
        Assert.Equal(1, movement.Direction);
    }

    [Fact]
    public async Task 成本_数量非正数_抛参数异常()
    {
        using var db = TestDbFactory.Create();
        var service = new InventoryService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.IncreaseAsync(Context(WarehouseA, Product1), 0m, 1m));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 单据字轨与库存流水查询 ====================

    [Fact]
    public async Task 单据号_库存单据使用各自字轨前缀()
    {
        using var db = TestDbFactory.Create();
        var no = new DocumentNumberService(db);

        Assert.StartsWith("PD", await no.GenerateAsync(DocumentType.StockAdjustment));
        Assert.StartsWith("DB", await no.GenerateAsync(DocumentType.StockTransfer));
        Assert.StartsWith("XTH", await no.GenerateAsync(DocumentType.SalesReturn));
        Assert.StartsWith("CTH", await no.GenerateAsync(DocumentType.PurchaseReturn));
    }

    [Fact]
    public async Task 库存流水_按来源单据号可查询并含红字冲销行()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var adjustment = NewAdjustmentController(db);
        var id = await CreateAdjustmentAsync(adjustment, bookQuantity: 10m, actualQuantity: 8m, unitCost: 10m);
        await adjustment.Submit(id);
        await adjustment.Approve(id);
        await adjustment.Unaudit(id);

        var docNo = db.StockAdjustments.Single().AdjustmentNo;
        var controller = new StockController(db);

        var result = await controller.GetMovements(new PageQuery { Page = 1, PageSize = 20 }, null, null, docNo, null);
        var response = Assert.IsType<ApiResponse<PagedResult<StockMovement>>>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(2, response.Data!.Total);
        Assert.Contains(response.Data.Items, m => m.IsReversal);
        Assert.Contains(response.Data.Items, m => m.IsReversed);

        // 单据自身的流水接口：含冲销行
        var byDoc = await adjustment.GetMovements(id);
        var byDocResponse = Assert.IsType<ApiResponse<IReadOnlyList<StockMovement>>>(
            Assert.IsType<OkObjectResult>(byDoc).Value);
        Assert.Equal(2, byDocResponse.Data!.Count);
        Assert.Equal(0, await new InventoryService(db).CountActiveMovementsAsync(
            InventoryDocumentHelper.StockAdjustmentType, id));                   // 冲销后已无有效流水
    }


    // ==================== 测试辅助 ====================

    private static StockAdjustmentController NewAdjustmentController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static StockTransferController NewTransferController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static SalesReturnController NewSalesReturnController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static PurchaseReturnController NewPurchaseReturnController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static InventoryMovementContext Context(long warehouseId, long productId) => new()
    {
        SourceDocType = InventoryDocumentHelper.StockAdjustmentType,
        SourceDocId = 1,
        SourceDocNo = "PD-TEST",
        MovementType = InventoryMovementType.Adjustment,
        WarehouseId = warehouseId,
        ProductId = productId,
        ProductName = "P" + productId
    };

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

    private static async Task<long> CreateAdjustmentAsync(StockAdjustmentController ctl, decimal bookQuantity,
        decimal actualQuantity, decimal unitCost)
    {
        var result = await ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Remark = "INV_TEST",
            Details = new List<StockAdjustmentDetail>
            {
                new()
                {
                    ProductId = Product1, ProductName = "P1", Spec = "大", Unit = "PCS",
                    BookQuantity = bookQuantity, ActualQuantity = actualQuantity, UnitCost = unitCost
                }
            }
        });
        return CreatedId(result);
    }

    private static async Task<long> CreateTransferAsync(StockTransferController ctl, decimal quantity, decimal unitCost)
    {
        var result = await ctl.Create(new StockTransfer
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = WarehouseA,
            ToWarehouseId = WarehouseB,
            Remark = "INV_TEST",
            Details = new List<StockTransferDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Spec = "大", Unit = "PCS", Quantity = quantity, UnitCost = unitCost }
            }
        });
        return CreatedId(result);
    }

    private static async Task<long> CreateSalesReturnAsync(SalesReturnController ctl, decimal quantity, decimal unitPrice,
        decimal unitCost, long? sourceStockOutId, string sourceNo)
    {
        var result = await ctl.Create(new SalesReturn
        {
            ReturnDate = DateTime.Today,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = sourceNo,
            ReturnReason = "质量",
            Remark = "INV_TEST",
            Details = new List<SalesReturnDetail>
            {
                new()
                {
                    ProductId = Product1, ProductName = "P1", Spec = "大", Unit = "PCS",
                    Quantity = quantity, UnitPrice = unitPrice, UnitCost = unitCost
                }
            }
        });
        return CreatedId(result);
    }

    private static async Task<long> CreatePurchaseReturnAsync(PurchaseReturnController ctl, decimal quantity,
        decimal unitPrice, decimal unitCost, long? sourceStockInId, string sourceNo)
    {
        var result = await ctl.Create(new PurchaseReturn
        {
            ReturnDate = DateTime.Today,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = sourceNo,
            ReturnReason = "规格不符",
            Remark = "INV_TEST",
            Details = new List<PurchaseReturnDetail>
            {
                new()
                {
                    ProductId = Product1, ProductName = "P1", Spec = "大", Unit = "PCS",
                    Quantity = quantity, UnitPrice = unitPrice, UnitCost = unitCost
                }
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
}

