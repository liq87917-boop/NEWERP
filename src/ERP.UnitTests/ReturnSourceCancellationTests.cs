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
/// ERP-359 来源出库单 / 来源入库单「取消前的退货引用护栏」单元测试：
/// <list type="number">
/// <item>存在仍然生效的已审核退货单显式引用来源单据时，取消被 fail closed 拒绝，且单据 / 明细 / 状态 / 库存 / 流水全部保持原样；</item>
/// <item>null 来源的无关退货（包括来源单号文本相同）永不阻断，绝不按单号文本推断链接；</item>
/// <item>未完审核（待提交 / 已提交）或已取消的退货单同样不阻断；</item>
/// <item>销审（红字冲销）后即可按既有流程取消来源单据，红字冲销流水原样保留；</item>
/// <item>重复取消来源单据仍按既有规则幂等拒绝，不重复冲销库存。</item>
/// </list>
/// <para>说明：全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不触碰任何业务库数据；
/// 身份为既有特权入库 / 出库操作员与真实业务员（既有菜单授权 + 真实客户数据范围），不使用匿名 / 管理员兜底。</para>
/// </summary>
public class ReturnSourceCancellationTests
{
    private const long WarehouseA = 930001L;
    private const long Product1 = 730001L;
    private const long CustomerA = 830001L;
    private const long SupplierA = 930002L;

    // ==================== 来源销售出库单 ====================

    [Fact]
    public async Task 来源出库单取消_存在已审核关联销售退货_拒绝且保留全部原始证据()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var stockOut = SeedApprovedStockOut(db, 501L, "CK-GUARD-0501", CustomerA, WarehouseA,
            (Product1, "PCS", 5m));
        // 依然生效的已审核销售退货单：显式来源指向本出库单
        var blocking = SeedSalesReturn(db, stockOut.Id, stockOut.StockOutNo, CustomerA, WarehouseA,
            DocumentStatus.Approved, (Product1, "PCS", 5m));

        var ctl = NewStockOutController(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(stockOut.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("销审", ex.Message);                       // 可执行的退货冲销要求
        Assert.Contains(blocking.ReturnNo, ex.Message);            // 明确指出阻断的退货单

        // 原始单据 / 明细 / 状态 / 库存 / 流水全部保持原样（拒绝先于任何冲销与状态变更）
        var persisted = db.StockOuts.Single(o => o.Id == stockOut.Id);
        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.Equal("CK-GUARD-0501", persisted.StockOutNo);
        Assert.Single(persisted.Details);
        Assert.Equal(5m, persisted.Details.Single().Quantity);
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Empty(db.StockMovements);

        var returnRow = db.SalesReturns.Single(r => r.Id == blocking.Id);
        Assert.Equal(DocumentStatus.Approved, returnRow.Status);
        Assert.Equal(stockOut.Id, returnRow.SourceStockOutId);
        Assert.False(returnRow.IsDeleted);
    }

    [Fact]
    public async Task 来源出库单取消_null来源或未完审核退货_不阻断且按既有流程冲销()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var stockOut = SeedApprovedStockOut(db, 502L, "CK-GUARD-0502", CustomerA, WarehouseA,
            (Product1, "PCS", 5m));
        // 无来源（null）的已审核退货：显式无来源语义，永不阻断
        SeedSalesReturn(db, sourceStockOutId: null, "CK-GUARD-0502", CustomerA, WarehouseA,
            DocumentStatus.Approved, (Product1, "PCS", 5m));
        // 显式来源但尚未审核（已提交）的退货：不构成生效引用，不阻断
        SeedSalesReturn(db, stockOut.Id, stockOut.StockOutNo, CustomerA, WarehouseA,
            DocumentStatus.Submitted, (Product1, "PCS", 3m));

        var ctl = NewStockOutController(db);
        var ok = await ctl.Cancel(stockOut.Id);

        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single().Status);
        // 该来源单为「历史无流水」的已审核单据：按既有 ERP-025 兜底口径原路恢复（+5），不阻断即放行
        Assert.Equal(15m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        // 无关 / 未完审核退货原样保留，未被改写
        Assert.Equal(2, db.SalesReturns.Count());
        Assert.All(db.SalesReturns.ToList(), r => Assert.False(r.IsDeleted));
    }

    [Fact]
    public async Task 来源出库单取消_不按来源单号文本推断链接_null来源同号退货不阻断()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var stockOut = SeedApprovedStockOut(db, 503L, "CK-NO-INFER-1", CustomerA, WarehouseA,
            (Product1, "PCS", 5m));
        // 来源 Id 为 null 但来源单号文本与本出库单完全相同：必须视为无关退货，绝不据此推断引用
        var unrelated = SeedSalesReturn(db, sourceStockOutId: null, "CK-NO-INFER-1", CustomerA, WarehouseA,
            DocumentStatus.Approved, (Product1, "PCS", 99m));

        var ctl = NewStockOutController(db);
        var ok = await ctl.Cancel(stockOut.Id);

        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single().Status);
        Assert.Equal(DocumentStatus.Approved, db.SalesReturns.Single(r => r.Id == unrelated.Id).Status);
    }

    [Fact]
    public async Task 来源出库单取消_关联退货已销审_放行且保留红字冲销流水()
    {
        using var db = TestDbFactory.Create();
        SalesReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "仓A");
        SalesReturnTestAuthorization.SeedProduct(db, Product1, "SRC-P1", "PCS");
        var salesmanId = SalesReturnTestAuthorization.SeedAuthorizedSalesman(db, CustomerA);
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);

        var outCtl = NewStockOutController(db);
        var outId = await CreateStockOutAsync(outCtl, 5m);
        await outCtl.Submit(outId);
        await outCtl.Approve(outId);
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        var returnCtl = SalesReturnTestAuthorization.ForUser(db, salesmanId);
        var sourceNo = db.StockOuts.Single(o => o.Id == outId).StockOutNo;
        var returnId = CreatedId(await returnCtl.Create(new SalesReturn
        {
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = outId,
            SourceStockOutNo = sourceNo,
            ReturnReason = "质量",
            Details = new List<SalesReturnDetail>
            {
                new() { ProductId = Product1, ProductName = "商品SRC-P1", Spec = "规格A", Unit = "PCS",
                    Quantity = 4m, UnitPrice = 10m, UnitCost = 8m }
            }
        }));
        await returnCtl.Submit(returnId);
        await returnCtl.Approve(returnId);
        Assert.Equal(9m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        // 销审（红字冲销）后该退货不再构成生效引用 → 来源出库单可取消
        await returnCtl.Unaudit(returnId);
        Assert.Equal(DocumentStatus.Pending, db.SalesReturns.Single(r => r.Id == returnId).Status);
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        var ok = await outCtl.Cancel(outId);
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single(o => o.Id == outId).Status);
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        // 红字冲销轨迹原样保留（退货销审 1 条 + 出库取消 1 条，均为冲销行）
        Assert.Equal(2, db.StockMovements.Count(m => m.IsReversal));
    }

    [Fact]
    public async Task 来源出库单取消_关联退货已取消_放行且既有流程不变()
    {
        using var db = TestDbFactory.Create();
        SalesReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "仓A");
        SalesReturnTestAuthorization.SeedProduct(db, Product1, "SRC-P1", "PCS");
        var salesmanId = SalesReturnTestAuthorization.SeedAuthorizedSalesman(db, CustomerA);
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);

        var outCtl = NewStockOutController(db);
        var outId = await CreateStockOutAsync(outCtl, 5m);
        await outCtl.Submit(outId);
        await outCtl.Approve(outId);

        var returnCtl = SalesReturnTestAuthorization.ForUser(db, salesmanId);
        var sourceNo = db.StockOuts.Single(o => o.Id == outId).StockOutNo;
        var returnId = CreatedId(await returnCtl.Create(new SalesReturn
        {
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = outId,
            SourceStockOutNo = sourceNo,
            ReturnReason = "质量",
            Details = new List<SalesReturnDetail>
            {
                new() { ProductId = Product1, ProductName = "商品SRC-P1", Spec = "规格A", Unit = "PCS",
                    Quantity = 4m, UnitPrice = 10m, UnitCost = 8m }
            }
        }));
        // 待提交退货单直接取消：不再是生效引用
        await returnCtl.Cancel(returnId);
        Assert.Equal(DocumentStatus.Cancelled, db.SalesReturns.Single(r => r.Id == returnId).Status);

        var ok = await outCtl.Cancel(outId);
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single(o => o.Id == outId).Status);
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
    }

    [Fact]
    public async Task 来源出库单取消_重复取消_按既有规则幂等拒绝且不重复冲销()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var outCtl = NewStockOutController(db);
        var outId = await CreateStockOutAsync(outCtl, 5m);
        await outCtl.Submit(outId);
        await outCtl.Approve(outId);

        var first = await outCtl.Cancel(outId);
        Assert.IsType<OkObjectResult>(first);
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(2, db.StockMovements.Count());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => outCtl.Cancel(outId));
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(2, db.StockMovements.Count());
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
    }

    // ==================== 来源采购入库单 ====================

    [Fact]
    public async Task 来源入库单取消_存在已审核关联采购退货_拒绝且保留全部原始证据()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var receipt = SeedApprovedStockIn(db, 601L, "RK-GUARD-0601", SupplierA, WarehouseA,
            (Product1, "PCS", 5m));
        var blocking = SeedPurchaseReturn(db, receipt.Id, receipt.StockInNo, SupplierA, WarehouseA,
            DocumentStatus.Approved, (Product1, "PCS", 5m));

        var ctl = NewStockInController(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(receipt.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("销审", ex.Message);
        Assert.Contains(blocking.ReturnNo, ex.Message);

        var persisted = db.StockIns.Single(o => o.Id == receipt.Id);
        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.Single(persisted.Details);
        Assert.Equal(5m, persisted.Details.Single().Quantity);
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Empty(db.StockMovements);

        var returnRow = db.PurchaseReturns.Single(r => r.Id == blocking.Id);
        Assert.Equal(DocumentStatus.Approved, returnRow.Status);
        Assert.Equal(receipt.Id, returnRow.SourceStockInId);
        Assert.False(returnRow.IsDeleted);
    }

    [Fact]
    public async Task 来源入库单取消_null来源或未完审核退货_不阻断且按既有流程冲销()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewStockInController(db);
        var receiptId = await CreateStockInAsync(ctl, 5m);
        await ctl.Submit(receiptId);
        await ctl.Approve(receiptId);
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        SeedPurchaseReturn(db, sourceStockInId: null, "RK-NO-INFER-1", SupplierA, WarehouseA,
            DocumentStatus.Approved, (Product1, "PCS", 5m));
        SeedPurchaseReturn(db, sourceStockInId: receiptId, "RK-NO-INFER-2", SupplierA, WarehouseA,
            DocumentStatus.Submitted, (Product1, "PCS", 3m));

        var ok = await ctl.Cancel(receiptId);
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single(o => o.Id == receiptId).Status);
        Assert.Equal(0m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(2, db.PurchaseReturns.Count());
    }

    [Fact]
    public async Task 来源入库单取消_关联退货已销审_放行且保留红字冲销流水()
    {
        using var db = TestDbFactory.Create();
        PurchaseReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "仓A");
        PurchaseReturnTestAuthorization.SeedProduct(db, Product1, "PR-P1", "PCS");
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierA, "供应商A");
        var operatorId = PurchaseReturnTestAuthorization.SeedAuthorizedOperator(db);
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);

        var inCtl = NewStockInController(db);
        var inId = await CreateStockInAsync(inCtl, 5m);
        await inCtl.Submit(inId);
        await inCtl.Approve(inId);
        Assert.Equal(15m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        var returnCtl = PurchaseReturnTestAuthorization.ForUser(db, operatorId);
        var sourceNo = db.StockIns.Single(o => o.Id == inId).StockInNo;
        var returnId = CreatedId(await returnCtl.Create(new PurchaseReturn
        {
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            SourceStockInId = inId,
            SourceStockInNo = sourceNo,
            ReturnReason = "规格不符",
            Details = new List<PurchaseReturnDetail>
            {
                new() { ProductId = Product1, ProductName = "商品PR-P1", Spec = "规格A", Unit = "PCS",
                    Quantity = 4m, UnitPrice = 10m, UnitCost = 8m }
            }
        }));
        await returnCtl.Submit(returnId);
        await returnCtl.Approve(returnId);
        Assert.Equal(11m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        await returnCtl.Unaudit(returnId);
        Assert.Equal(DocumentStatus.Pending, db.PurchaseReturns.Single(r => r.Id == returnId).Status);
        Assert.Equal(15m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);

        var ok = await inCtl.Cancel(inId);
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single(o => o.Id == inId).Status);
        Assert.Equal(10m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(2, db.StockMovements.Count(m => m.IsReversal));
    }

    // ==================== 规则层 ====================

    [Fact]
    public void 规则层_锁语句为上游单据行的更新锁且拒绝文案可执行()
    {
        Assert.Contains("db_owner.StockOuts", ReturnSourceCancellationRules.LockStockOutRowSql);
        Assert.Contains("UPDLOCK", ReturnSourceCancellationRules.LockStockOutRowSql);
        Assert.Contains("HOLDLOCK", ReturnSourceCancellationRules.LockStockOutRowSql);
        Assert.Contains("db_owner.StockIns", ReturnSourceCancellationRules.LockStockInRowSql);
        Assert.Contains("UPDLOCK", ReturnSourceCancellationRules.LockStockInRowSql);
        Assert.Contains("HOLDLOCK", ReturnSourceCancellationRules.LockStockInRowSql);
        Assert.Contains("销审", ReturnSourceCancellationRules.ReversalRequirementText);
        Assert.Contains("SourceStockOutId", ReturnSourceCancellationRules.RuleText);
        Assert.Contains("SourceStockInId", ReturnSourceCancellationRules.RuleText);
    }

    [Fact]
    public void 规则层_内存库不视为关系型提供程序()
    {
        using var db = TestDbFactory.Create();
        Assert.False(ReturnSourceCancellationRules.IsRelationalProvider(db));
    }

    // ==================== 测试辅助 ====================

    private static StockOutController NewStockOutController(ErpDbContext db)
        => StockOutTestAuthorization.Create(db);

    private static StockInController NewStockInController(ErpDbContext db)
        => StockInLegacyTestFixture.Create(db, SupplierA, WarehouseA);

    private static async Task<long> CreateStockOutAsync(StockOutController ctl, decimal quantity)
    {
        var result = await ctl.Create(new StockOut
        {
            StockOutDate = DateTime.Today,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            Remark = "ERP-359_TEST",
            Details = new List<StockOutDetail>
            {
                new() { ProductId = Product1, ProductName = "商品SRC-P1", Spec = "规格A", Unit = "PCS",
                    Quantity = quantity }
            }
        });
        return CreatedId(result);
    }

    private static async Task<long> CreateStockInAsync(StockInController ctl, decimal quantity)
    {
        var result = await ctl.Create(new StockIn
        {
            StockInDate = DateTime.Today,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Remark = "ERP-359_TEST",
            Details = new List<StockInDetail>
            {
                new() { ProductId = Product1, ProductName = "商品PR-P1", Spec = "规格A", Unit = "PCS",
                    Quantity = quantity }
            }
        });
        return CreatedId(result);
    }

    private static StockOut SeedApprovedStockOut(ErpDbContext db, long id, string no, long customerId,
        long warehouseId, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            Id = id,
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = DocumentStatus.Approved,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockOuts.Add(shipment);
        db.SaveChanges();
        return shipment;
    }

    private static StockIn SeedApprovedStockIn(ErpDbContext db, long id, string no, long supplierId,
        long warehouseId, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            Id = id,
            StockInNo = no,
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = DocumentStatus.Approved,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockInDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockIns.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static SalesReturn SeedSalesReturn(ErpDbContext db, long? sourceStockOutId, string sourceNo,
        long customerId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-GUARD-{Guid.NewGuid():N}".Substring(0, 20),
            ReturnDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "客户A",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = sourceNo,
            ReturnReason = "质量",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new SalesReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m,
                Amount = Math.Round(l.Quantity * 10m, 4),
                UnitCost = 8m
            }).ToList()
        };
        db.SalesReturns.Add(ret);
        db.SaveChanges();
        return ret;
    }

    private static PurchaseReturn SeedPurchaseReturn(ErpDbContext db, long? sourceStockInId, string sourceNo,
        long supplierId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var ret = new PurchaseReturn
        {
            ReturnNo = $"CTH-GUARD-{Guid.NewGuid():N}".Substring(0, 20),
            ReturnDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierName = "供应商A",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = sourceNo,
            ReturnReason = "规格不符",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new PurchaseReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m,
                Amount = Math.Round(l.Quantity * 10m, 4),
                UnitCost = 8m
            }).ToList()
        };
        db.PurchaseReturns.Add(ret);
        db.SaveChanges();
        return ret;
    }

    private static long CreatedId(IActionResult result)
    {
        Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

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
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        db.SaveChanges();
    }
}
