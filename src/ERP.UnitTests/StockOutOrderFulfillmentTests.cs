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
/// ERP-343 销售出库衔接来源订单并防止累计超发单元测试。
/// <para>覆盖：关联权威订单成功、未关联保持历史行为、链接不权威（订单不存在 / 未审核 / 客户不一致 / 同商品多行 /
/// 单位不兼容 / 商品缺失）时不做累计校验（不猜测）、部分 / 满量批次累计、
/// 累计超限抛冲突且库存 · 流水 · 状态不变、取消释放额度、重复审核幂等、包装单位订单按基础单位累计、
/// 控制器授权仅继承基类（无权限扩展）。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class StockOutOrderFulfillmentTests
{
    private const long CustomerA = 941001L;
    private const long CustomerB = 941002L;
    private const long WarehouseA = 941101L;
    private const long ProductA = 941201L;
    private const long ProductB = 941202L;

    // ==================== 链接权威性判定（非权威链接不做累计校验） ====================

    [Fact]
    public async Task Create_关联已审核订单且商品单位兼容_保存成功()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-1", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "SO-FUL-1", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var result = await ctl.Create(NewStockOut(CustomerA, order.Id, (ProductA, "PCS", 6m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockOuts.Include(o => o.Details).Single();
        Assert.Equal(order.Id, saved.SalesOrderId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task Create_未关联销售订单_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-2", "商品A", "PCS", string.Empty, 0);
        var ctl = NewController(db);

        var result = await ctl.Create(NewStockOut(CustomerA, null, (ProductA, "PCS", 6m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.StockOuts.Single().SalesOrderId);
    }

    [Fact]
    public async Task Create_链接不存在订单_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-3", "商品A", "PCS", string.Empty, 0);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, 999999L, (ProductA, "PCS", 6m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task Create_链接未审核订单_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-4", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "SO-FUL-4", CustomerA, DocumentStatus.Pending, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, order.Id, (ProductA, "PCS", 6m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task Create_链接客户不一致_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-5", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "SO-FUL-5", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerB, order.Id, (ProductA, "PCS", 6m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task Create_链接商品不在订单_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-6", "商品A", "PCS", string.Empty, 0);
        SeedProduct(db, ProductB, "S-FUL-6B", "商品B", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "SO-FUL-6", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, order.Id, (ProductB, "PCS", 6m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task Create_链接订单同商品多行_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-7", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "SO-FUL-7", CustomerA, DocumentStatus.Approved,
            (ProductA, "PCS", 6m, 7.5m), (ProductA, "PCS", 4m, 8m));
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, order.Id, (ProductA, "PCS", 6m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task Create_链接订单行单位不兼容_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-8", "商品A", "PCS", "CTN", 12);
        var order = SeedOrder(db, "SO-FUL-8", CustomerA, DocumentStatus.Approved, (ProductA, "KG", 10m, 7.5m));
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, order.Id, (ProductA, "PCS", 6m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task Approve_提交后来源订单失效_拒绝履约且库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-9", "商品A", "PCS", string.Empty, 0);
        SeedStock(db, WarehouseA, ProductA, 6m);
        var order = SeedOrder(db, "SO-FUL-9", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 6m));
        await ctl.Submit(id);

        // 提交后来源订单被取消：审核前 fail closed，不扣库存 / 流水，状态保持已提交
        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single().Status);
        Assert.Empty(db.StockMovements);
        Assert.Equal(6m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }


    // ==================== 审核累计上限 ====================

    [Fact]
    public async Task Approve_部分批次_累计不超限_两单均通过()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-11", "商品A", "PCS", string.Empty, 0);
        SeedStock(db, WarehouseA, ProductA, 10m);
        var order = SeedOrder(db, "SO-FUL-11", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var first = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, first);

        var second = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 4m));
        await SubmitAndApproveAsync(ctl, second);

        Assert.All(db.StockOuts, s => Assert.Equal(DocumentStatus.Approved, s.Status));
        Assert.Equal(0m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task Approve_累计超限_抛RuleConflict且库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-12", "商品A", "PCS", string.Empty, 0);
        SeedStock(db, WarehouseA, ProductA, 10m);
        var order = SeedOrder(db, "SO-FUL-12", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var first = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, first);

        var second = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 5m));
        await ctl.Submit(second);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过来源销售订单授权数量", ex.Message);

        // 失败不落任何库存 / 流水，且本单状态仍为已提交
        Assert.Equal(4m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Single(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single(s => s.Id == second).Status);
    }

    [Fact]
    public async Task Approve_取消释放额度_后续可满量出库()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-13", "商品A", "PCS", string.Empty, 0);
        SeedStock(db, WarehouseA, ProductA, 10m);
        var order = SeedOrder(db, "SO-FUL-13", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var first = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, first);
        await ctl.Cancel(first);

        var second = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 10m));
        await SubmitAndApproveAsync(ctl, second);

        Assert.Equal(0m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Cancelled, db.StockOuts.Single(s => s.Id == first).Status);
        Assert.Equal(DocumentStatus.Approved, db.StockOuts.Single(s => s.Id == second).Status);
    }

    [Fact]
    public async Task Approve_重复审核_幂等拒绝且不二次写入()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-14", "商品A", "PCS", string.Empty, 0);
        SeedStock(db, WarehouseA, ProductA, 10m);
        var order = SeedOrder(db, "SO-FUL-14", CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m, 7.5m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 6m));
        await SubmitAndApproveAsync(ctl, id);

        // 状态护栏：已审核不可再审核
        var byStatus = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, byStatus.Code);

        // 绕过状态护栏：流水幂等护栏兜住，库存与流水不被二次写入
        var entity = db.StockOuts.Single();
        entity.Status = DocumentStatus.Submitted;
        await db.SaveChangesAsync();
        var byMovement = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Contains("已产生库存流水", byMovement.Message);
        Assert.Single(db.StockMovements);
        Assert.Equal(4m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task Approve_包装单位订单_按基础单位累计()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "S-FUL-15", "商品A", "PCS", "CTN", 12);
        SeedStock(db, WarehouseA, ProductA, 12m);
        // 订单 1 箱 = 12 个基础单位；出库以基础单位 PCS 提交 12 个应正好出齐
        var order = SeedOrder(db, "SO-FUL-15", CustomerA, DocumentStatus.Approved, (ProductA, "CTN", 1m, 90m));
        var ctl = NewController(db);

        var id = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 12m));
        await SubmitAndApproveAsync(ctl, id);

        Assert.Equal(0m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);

        var over = await CreateAsync(ctl, order.Id, CustomerA, (ProductA, "PCS", 1m));
        await ctl.Submit(over);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(over));
        Assert.Contains("超过来源销售订单授权数量 12", ex.Message);
    }

    // ==================== 权限与口径 ====================

    [Fact]
    public void Controller_授权仅继承基类_无权限扩展()
    {
        var controllerType = typeof(StockOutController);

        // 基类要求 [Authorize]，派生类不额外叠加任何授权策略（不扩权）
        Assert.NotNull(controllerType.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    [Fact]
    public void RuleText_口径说明_明确不猜测()
    {
        Assert.False(string.IsNullOrWhiteSpace(StockOutOrderFulfillmentRules.RuleText));
        Assert.Contains("绝不猜测", StockOutOrderFulfillmentRules.RuleText);
        Assert.Contains("显式链接无效", StockOutOrderFulfillmentRules.RuleText);
    }



    // ==================== 辅助 ====================

    private static StockOutController NewController(ErpDbContext db)
        => StockOutTestAuthorization.Create(db);

    private static StockOut NewStockOut(long customerId, long? salesOrderId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
        => new()
        {
            StockOutDate = DateTime.Today,
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            Remark = "ERP-343_TEST",
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };

    private static async Task<long> CreateAsync(StockOutController ctl, long? salesOrderId, long customerId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var result = await ctl.Create(NewStockOut(customerId, salesOrderId, lines));
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task SubmitAndApproveAsync(StockOutController ctl, long id)
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

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity
        });
        db.SaveChanges();
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, quantity, unitPrice) in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
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
        order.TotalAmount = db.SalesOrderDetails.Where(d => d.SalesOrderId == order.Id).Sum(d => d.Amount);
        db.SaveChanges();
        return order;
    }
}

