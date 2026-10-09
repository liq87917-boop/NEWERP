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
/// ERP-426 规范采购订单「新写入条款」校验单元测试（纯内存 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一权威规则</b>（<see cref="PurchaseOrderAmountRules.ValidateNewWrite"/>）：明细非空、数量为正且可表示、
/// 单价非负且可表示、币种为已定义枚举值、汇率大于 0 且可表示、税率 0~100、逐行金额与总额
/// 在实际 EF 精度（<c>DECIMAL(18,2)</c>）内 checked 可表示；溢出 / 不可表示一律返回受控业务错误；</item>
/// <item><b>EF 精度契约</b>：用 SQL Server 提供程序**仅构建模型**（不打开连接）断言采购订单列的实际存储精度
/// 与规则常量同源；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与字段改写之前拒绝非法条款且零写入；客户端伪造合计 / 状态 /
/// 审计被服务端重置或按明细重算覆盖；提交 / 审核在行锁内复核已持久化条款、非法即原子拒绝且状态不变；
/// 历史读取 / 打印仍可读且不被修正。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class PurchaseOrderWriteValidationTests
{
    // ==================== 1. 唯一权威规则（纯内存） ====================

    [Fact]
    public void 新增写入校验_有效明细为空_拒绝()
    {
        var nullDetails = new PurchaseOrder { Details = null! };
        var empty = new PurchaseOrder { Details = new List<PurchaseOrderDetail>() };
        var allDeleted = NewOrder(new PurchaseOrderDetail
        {
            Quantity = 1m, UnitPrice = 1m, IsDeleted = true
        });

        foreach (var order in new[] { nullDetails, empty, allDeleted })
        {
            var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Equal(PurchaseOrderAmountRules.EmptyDetailsText, ex.Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.5)]
    [InlineData(0.001)]  // 正数量但会在 DECIMAL(18,2) 被静默舍入为 0
    [InlineData(0.004)]
    [InlineData(1.005)]
    public void 新增写入校验_数量非正或超出存储精度_拒绝(decimal quantity)
    {
        var order = NewOrder(new PurchaseOrderDetail { Quantity = quantity, UnitPrice = 10m });

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(1.005)]
    public void 新增写入校验_单价为负或超出存储精度_拒绝(decimal unitPrice)
    {
        var order = NewOrder(new PurchaseOrderDetail { Quantity = 1m, UnitPrice = unitPrice });

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负数", ex.Message);
    }

    [Fact]
    public void 新增写入校验_币种未定义_拒绝()
    {
        var order = NewOrder(new PurchaseOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.Currency = (Currency)999;

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.CurrencyText, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7.1234)]   // 超出 DECIMAL(18,2) 精度
    public void 新增写入校验_汇率非正或超出存储精度_拒绝(decimal exchangeRate)
    {
        var order = NewOrder(new PurchaseOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.ExchangeRate = exchangeRate;

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.ExchangeRateText, ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void 新增写入校验_税率越界_拒绝(decimal taxRate)
    {
        var order = NewOrder(new PurchaseOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.TaxRate = taxRate;

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("税率", ex.Message);
    }

    [Fact]
    public void 新增写入校验_金额乘法溢出_返回受控业务错误()
    {
        var order = NewOrder(new PurchaseOrderDetail
        {
            Quantity = PurchaseOrderAmountRules.MaxDecimal18Scale2,
            UnitPrice = PurchaseOrderAmountRules.MaxDecimal18Scale2
        });

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.AmountOverflowText, ex.Message);
    }

    [Fact]
    public void 新增写入校验_行金额不可表示_拒绝()
    {
        // 0.25 × 1.05 = 0.2625：两个因子都在精度内，但行金额会被落库取整 —— 必须拒绝，绝不写与明细合计不一致的金额。
        var order = NewOrder(new PurchaseOrderDetail { Quantity = 0.25m, UnitPrice = 1.05m });

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("行金额", ex.Message);
    }

    [Fact]
    public void 新增写入校验_边界精度与零单价政策_通过()
    {
        // 零单价是既有政策：行金额与总额为 0，仍然合法。
        var order = NewOrder(
            new PurchaseOrderDetail { Quantity = 100m, UnitPrice = 0m },
            new PurchaseOrderDetail { Quantity = 1234.56m, UnitPrice = 2.5m });

        PurchaseOrderAmountRules.ValidateNewWrite(order);
        PurchaseOrderAmountRules.ApplyDetailAmounts(order);
        PurchaseOrderAmountRules.Calculate(order);

        Assert.Equal(0m, order.Details[0].Amount);
        Assert.Equal(3086.40m, order.Details[1].Amount);
        Assert.Equal(3086.40m, order.TotalAmount);
        Assert.All(order.Details, d => Assert.True(PurchaseOrderAmountRules.IsRepresentable(d.Amount)));
    }

    [Fact]
    public void 新增写入校验_最大可表示总额边界_通过()
    {
        var order = NewOrder(new PurchaseOrderDetail
        {
            Quantity = PurchaseOrderAmountRules.MaxDecimal18Scale2, UnitPrice = 1m
        });

        PurchaseOrderAmountRules.ValidateNewWrite(order);
        Assert.True(PurchaseOrderAmountRules.IsRepresentable(PurchaseOrderAmountRules.MaxDecimal18Scale2));
        Assert.False(PurchaseOrderAmountRules.IsRepresentable(PurchaseOrderAmountRules.MaxDecimal18Scale2 + 1m));
        Assert.False(PurchaseOrderAmountRules.IsRepresentable(decimal.MinValue));
    }

    [Fact]
    public void 规范写入入口_委托唯一权威校验_拒绝口径一致()
    {
        var rejected = NewOrder(new PurchaseOrderDetail { Quantity = -1m, UnitPrice = 1m });
        var direct = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(rejected));
        var wrapped = Assert.Throws<BusinessException>(() => PurchaseOrderMutationRules.EnsureValidatedTerms(rejected));

        Assert.Equal(ErrorCodes.InvalidParameter, direct.Code);
        Assert.Equal(direct.Code, wrapped.Code);
        Assert.Equal(direct.Message, wrapped.Message);

        var accepted = NewOrder(new PurchaseOrderDetail { Quantity = 2m, UnitPrice = 3m });
        PurchaseOrderMutationRules.EnsureValidatedTerms(accepted);

        Assert.Contains("采购订单", PurchaseOrderMutationRules.ValidatedTermsText);
        Assert.Contains("ValidateNewWrite", PurchaseOrderMutationRules.ValidatedTermsText);
    }

    // ==================== 2. EF 实际精度契约（只构建模型，不打开连接） ====================

    [Fact]
    public void EF实际精度_采购订单数量金额汇率税率列_与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        var expected = $"decimal({PurchaseOrderAmountRules.DecimalPrecision},{PurchaseOrderAmountRules.DecimalScale})";

        Assert.Equal(expected, ColumnType(model, typeof(PurchaseOrderDetail), nameof(PurchaseOrderDetail.Quantity)));
        Assert.Equal(expected, ColumnType(model, typeof(PurchaseOrderDetail), nameof(PurchaseOrderDetail.UnitPrice)));
        Assert.Equal(expected, ColumnType(model, typeof(PurchaseOrderDetail), nameof(PurchaseOrderDetail.Amount)));
        Assert.Equal(expected, ColumnType(model, typeof(PurchaseOrder), nameof(PurchaseOrder.TotalAmount)));
        Assert.Equal(expected, ColumnType(model, typeof(PurchaseOrder), nameof(PurchaseOrder.ExchangeRate)));
        Assert.Equal(expected, ColumnType(model, typeof(PurchaseOrder), nameof(PurchaseOrder.TaxRate)));
    }

    // ==================== 3. 控制器：新增 ====================

    [Fact]
    public async Task 新增_数量过小_拒绝且不预约单号不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewOrder(
            new PurchaseOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 0.001m, UnitPrice = 10m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.PurchaseOrderDetails.AnyAsync());
        // 校验先于单号预约：单据字轨规则未被创建 / 未被消耗。
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_负单价_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewOrder(
            new PurchaseOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 1m, UnitPrice = -0.01m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负数", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_币种未定义_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(new PurchaseOrderDetail
        {
            ProductId = 1, ProductName = "P1", Quantity = 1m, UnitPrice = 1m
        });
        body.Currency = (Currency)77;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.CurrencyText, ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 新增_无明细_拒绝且不预约单号()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new PurchaseOrder
        {
            OrderDate = DateTime.Today, SupplierId = 1L, Currency = Currency.CNY, ExchangeRate = 1m
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.EmptyDetailsText, ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_客户端伪造合计状态与审计_服务端重置覆盖()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(new PurchaseOrderDetail
        {
            ProductId = 1, ProductName = "P1", Quantity = 10m, UnitPrice = 100m, Amount = 999_999m
        });
        body.Status = DocumentStatus.Approved;             // 伪造状态
        body.TotalAmount = 999_999m;                       // 伪造合计
        body.CreatedAt = new DateTime(2000, 1, 1);         // 伪造审计
        body.OrderNo = "PO-FORGED";

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var stored = await db.PurchaseOrders.AsNoTracking().SingleAsync();
        var lines = await db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == stored.Id).ToListAsync();
        Assert.Equal(DocumentStatus.Pending, stored.Status);           // 状态由服务端重置
        Assert.StartsWith("PO", stored.OrderNo);
        Assert.NotEqual("PO-FORGED", stored.OrderNo);
        Assert.True(stored.CreatedAt > new DateTime(2020, 1, 1));      // 审计由服务端重置
        Assert.Equal(1000m, stored.TotalAmount);                       // 合计按明细重算
        Assert.Equal(1000m, lines.Sum(d => d.Amount));                 // 行金额按明细重算
    }

    [Fact]
    public async Task 新增_精确落库_明细合计与总额逐分一致()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(
            new PurchaseOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 100m, UnitPrice = 2.5m },
            new PurchaseOrderDetail { ProductId = 2, ProductName = "P2", Quantity = 200m, UnitPrice = 2.5m });

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var stored = await db.PurchaseOrders.AsNoTracking().SingleAsync();
        var lines = await db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == stored.Id).ToListAsync();
        Assert.Equal(750m, stored.TotalAmount);
        Assert.Equal(750m, lines.Sum(d => d.Amount));
        Assert.All(lines, d => Assert.True(PurchaseOrderAmountRules.IsRepresentable(d.Amount)));
    }

    // ==================== 4. 控制器：修改 / 提交 / 审核 ====================

    [Fact]
    public async Task 修改_非法条款_拒绝且原始表头明细状态不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, NewOrder(
            new PurchaseOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 0m, UnitPrice = 100m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ChangeTracker.Entries());

        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.False(persisted.IsDeleted);
        var lines = await db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == order.Id).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(10m, lines[0].Quantity);
        Assert.Equal(100m, lines[0].UnitPrice);
        // 无任何下游库存 / 财务变更。
        Assert.False(await db.StockIns.AnyAsync());
        Assert.False(await db.Stocks.AnyAsync());
        Assert.False(await db.StockMovements.AnyAsync());
    }

    [Fact]
    public async Task 修改_非法条款_保留已链接归属来源血缘()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var order = SeedOrder(db, DocumentStatus.Pending, owningSalesOrderId: 4242L, owningCustomerId: 99L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, NewOrder(
            new PurchaseOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 1.005m, UnitPrice = 100m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(4242L, persisted.OwningSalesOrderId);
        Assert.Equal(99L, persisted.OwningCustomerId);
        Assert.Equal(1000m, persisted.TotalAmount);
    }

    [Fact]
    public async Task 修改_合法条款_精确重算且无下游变更()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id, NewOrder(
            new PurchaseOrderDetail { ProductId = 7, ProductName = "P7", Quantity = 20m, UnitPrice = 50m })));

        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        var lines = await db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == order.Id).ToListAsync();
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(persisted.TotalAmount, lines.Sum(d => d.Amount));
        Assert.False(await db.Stocks.AnyAsync());
        Assert.False(await db.StockMovements.AnyAsync());
    }

    [Fact]
    public async Task 提交_已持久化条款非法_原子拒绝且状态与证据不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        // 直接改库模拟历史 / 外部写入的非法条款（数量 0），绕过新写入校验。
        var line = await db.PurchaseOrderDetails.SingleAsync(d => d.PurchaseOrderId == order.Id);
        line.Quantity = 0m;
        line.Amount = 0m;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(order.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(DocumentStatus.Pending,
            (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(0m, (await db.PurchaseOrderDetails.AsNoTracking()
            .SingleAsync(d => d.PurchaseOrderId == order.Id)).Quantity);
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var order = SeedOrder(db, DocumentStatus.Submitted);

        var stored = await db.PurchaseOrders.SingleAsync(o => o.Id == order.Id);
        stored.Currency = (Currency)999;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(order.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.CurrencyText, ex.Message);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 提交审核_合法订单_精确落库且血缘不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var (customerId, salesOrderId) = SeedApprovedSalesSource(db);
        var order = SeedOrder(db, DocumentStatus.Pending, salesOrderId, customerId);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));

        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        var lines = await db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == order.Id).ToListAsync();
        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.Equal(persisted.TotalAmount, lines.Sum(d => d.Amount));
        Assert.Equal(salesOrderId, persisted.OwningSalesOrderId);
        Assert.Equal(customerId, persisted.OwningCustomerId);
    }

    // ==================== 5. 历史读取 / 打印不修正 ====================

    [Fact]
    public async Task 历史读取与打印_非法存储条款仍可读且不被修正()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var order = SeedOrder(db, DocumentStatus.Approved);
        var line = await db.PurchaseOrderDetails.SingleAsync(d => d.PurchaseOrderId == order.Id);
        line.Quantity = -5m;                 // 非法历史条款
        line.UnitPrice = 1.005m;
        await db.SaveChangesAsync();

        var detail = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id)).Value);
        Assert.Equal(-5m, detail.Data!.Details.Single().Quantity);
        Assert.Equal(1.005m, detail.Data.Details.Single().UnitPrice);

        var printed = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id)).Value);
        Assert.Equal(-5m, printed.Data!.Details.Single().Quantity);

        // 读取 / 打印绝不落库修正历史值。
        var persistedLine = await db.PurchaseOrderDetails.AsNoTracking()
            .SingleAsync(d => d.PurchaseOrderId == order.Id);
        Assert.Equal(-5m, persistedLine.Quantity);
        Assert.Equal(1.005m, persistedLine.UnitPrice);
    }

    // ==================== 工厂与种子数据 ====================

    private static PurchaseOrder NewOrder(params PurchaseOrderDetail[] details) => new()
    {
        OrderDate = DateTime.Today,
        SupplierId = 1L,
        Currency = Currency.CNY,
        ExchangeRate = 1m,
        TaxRate = 0m,
        Details = details.ToList()
    };

    private static PurchaseOrderController NewController(ErpDbContext db)
    {
        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, SeedPrivilegedPurchaseUser(db));
        return controller;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, DocumentStatus status,
        long? owningSalesOrderId = null, long? owningCustomerId = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO-426-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = 1L,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TaxRate = 0m,
            OwningSalesOrderId = owningSalesOrderId,
            OwningCustomerId = owningCustomerId,
            Status = status,
            TotalAmount = 1000m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = 1L, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种一条已审核销售订单及其客户（供归属来源血缘测试使用，直接落库不取行锁）。</summary>
    private static (long CustomerId, long SalesOrderId) SeedApprovedSalesSource(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-426-{Guid.NewGuid():N}", CustomerName = "ERP426 客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();

        var salesOrder = new SalesOrder
        {
            OrderNo = $"SO-426-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            CustomerId = customer.Id,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = 1L, ProductName = "商品A", UnitPrice = 10m, Quantity = 100m, Amount = 1000m }
            }
        };
        db.SalesOrders.Add(salesOrder);
        db.SaveChanges();
        return (customer.Id, salesOrder.Id);
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有 purchase-order 菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedPurchaseUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "ERP426 特权角色", RoleCode = $"ERP426-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"erp426-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP426 隔离账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        return user.Id;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
}




