using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-342 采购入库衔接来源订单并防止累计超收单元测试。
/// <para>覆盖：关联权威订单成功、未关联保持历史行为、链接不权威（订单不存在 / 未审核 / 供应商不一致 / 同商品多行 /
/// 单位不兼容 / 商品缺失）时不做累计校验（与 ERP-033 成本回退同一口径、不猜测）、部分 / 满量批次累计、
/// 累计超限抛冲突且库存 · 流水 · 状态不变、取消释放额度、重复审核幂等、包装单位订单按基础单位累计、
/// 控制器授权仅继承基类（无权限扩展）。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class StockInOrderFulfillmentTests
{
    private const long SupplierA = 940001L;
    private const long SupplierB = 940002L;
    private const long WarehouseA = 940101L;
    private const long ProductA = 940201L;
    private const long ProductB = 940202L;

    // ==================== 链接权威性判定（非权威链接不做累计校验） ====================

    [Fact]
    public async Task Create_关联已审核订单且商品单位兼容_保存成功()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-1", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-1", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var result = await ctl.Create(NewStockIn(SupplierA, order.Id, (ProductA, "PCS", 6m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        Assert.Equal(order.Id, saved.PurchaseOrderId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task Create_未关联采购订单_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-2", "商品A", "PCS", string.Empty, 0);
        var ctl = NewController(db);

        var result = await ctl.Create(NewStockIn(SupplierA, null, (ProductA, "PCS", 6m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.StockIns.Single().PurchaseOrderId);
    }

    [Fact]
    public async Task Approve_链接不存在订单_不做累计校验_仍可入库()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-3", "商品A", "PCS", string.Empty, 0);
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, 999999L, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
        Assert.Equal(6m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task Approve_链接未审核订单_不做累计校验()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-4", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-4", SupplierA, DocumentStatus.Pending, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
    }

    [Fact]
    public async Task Approve_链接供应商不一致_不做累计校验()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-5", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-5", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierB, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
    }

    [Fact]
    public async Task Approve_链接商品不在订单_不做累计校验()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-6", "商品A", "PCS", string.Empty, 0);
        SeedProduct(db, ProductB, "P-FUL-6B", "商品B", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-6", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierA, (ProductB, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
    }

    [Fact]
    public async Task Approve_链接订单同商品多行_不做累计校验且不猜测()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-7", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-7", SupplierA, DocumentStatus.Approved,
            (ProductA, "PCS", 6m, 7.5m), (ProductA, "PCS", 4m, 8m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
    }

    [Fact]
    public async Task Approve_链接订单行单位不兼容_不做累计校验()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-8", "商品A", "PCS", "CTN", 12);
        var order = SeedOrder(db, "PO-FUL-8", SupplierA, DocumentStatus.Approved, (ProductA, "KG", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single().Status);
    }


    // ==================== 审核累计上限 ====================

    [Fact]
    public async Task Approve_部分批次_累计不超限_两单均通过()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-11", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-11", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var first = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, first);

        var second = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 4m));
        await SubmitAndApproveAsync(ctl, second);

        Assert.All(db.StockIns, s => Assert.Equal(DocumentStatus.Approved, s.Status));
        Assert.Equal(10m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task Approve_累计超限_抛RuleConflict且库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-12", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-12", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var first = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, first);

        var second = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 5m));
        await ctl.Submit(second);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过来源采购订单授权数量", ex.Message);

        // 失败不落任何库存 / 流水，且本单状态仍为已提交
        Assert.Equal(6m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Single(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockIns.Single(s => s.Id == second).Status);
    }

    [Fact]
    public async Task Approve_取消释放额度_后续可满量入库()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-13", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-13", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var first = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, first);
        await ctl.Cancel(first);

        var second = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 10m));
        await SubmitAndApproveAsync(ctl, second);

        Assert.Equal(10m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single(s => s.Id == first).Status);
        Assert.Equal(DocumentStatus.Approved, db.StockIns.Single(s => s.Id == second).Status);
    }

    [Fact]
    public async Task Approve_重复审核_幂等拒绝且不二次写入()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-14", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-FUL-14", SupplierA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        // 状态护栏：已审核不可再审核
        var byStatus = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, byStatus.Code);

        // 绕过状态护栏：流水幂等护栏兜住，库存与流水不被二次写入
        var entity = db.StockIns.Single();
        entity.Status = DocumentStatus.Submitted;
        await db.SaveChangesAsync();
        var byMovement = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Contains("已产生库存流水", byMovement.Message);
        Assert.Single(db.StockMovements);
        Assert.Equal(6m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task Approve_包装单位订单_按基础单位累计()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FUL-15", "商品A", "PCS", "CTN", 12);
        // 订单 1 箱 = 12 个基础单位；入库以基础单位 PCS 提交 12 个应正好收满
        var order = SeedOrder(db, "PO-FUL-15", SupplierA, DocumentStatus.Approved, (ProductA, "CTN", 1m, 90m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 12m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(12m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);

        var over = await CreateAsync(ctl, order.Id, SupplierA, (ProductA, "PCS", 1m));
        await ctl.Submit(over);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(over));
        Assert.Contains("超过来源采购订单授权数量 12", ex.Message);
    }

    // ==================== 权限与口径 ====================

    [Fact]
    public void Controller_授权仅继承基类_无权限扩展()
    {
        var controllerType = typeof(StockInController);

        // 基类要求 [Authorize]，派生类不额外叠加任何授权策略（不扩权）
        Assert.NotNull(controllerType.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    [Fact]
    public void RuleText_口径说明_明确不猜测()
    {
        Assert.False(string.IsNullOrWhiteSpace(StockInOrderFulfillmentRules.RuleText));
        Assert.Contains("绝不猜测", StockInOrderFulfillmentRules.RuleText);
        Assert.Contains("未关联或链接不权威的入库单不做累计校验", StockInOrderFulfillmentRules.RuleText);
    }


    // ==================== 辅助 ====================

    private static StockInController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static StockIn NewStockIn(long supplierId, long? purchaseOrderId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
        => new()
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplierId,
            WarehouseId = WarehouseA,
            Remark = "ERP-342_TEST",
            Details = lines.Select(l => new StockInDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };

    private static async Task<long> CreateAsync(StockInController ctl, long? purchaseOrderId, long supplierId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var result = await ctl.Create(NewStockIn(supplierId, purchaseOrderId, lines));
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task SubmitAndApproveAsync(StockInController ctl, long id)
    {
        await ctl.Submit(id);
        await ctl.Approve(id);
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name, string unit,
        string packageUnit, int unitsPerPackage)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage
        });
        db.SaveChanges();
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = status
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, quantity, unitPrice) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity,
                UnitPrice = unitPrice,
                Amount = quantity * unitPrice
            });
        }
        db.SaveChanges();
        order.TotalAmount = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).Sum(d => d.Amount);
        db.SaveChanges();
        return order;
    }
}

