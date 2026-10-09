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
/// ERP-422 规范销售订单「新写入条款」校验单元测试（纯内存 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一权威规则</b>（<see cref="SalesOrderAmountRules.ValidateNewWrite"/>）：明细非空、数量为正且可表示、
/// 单价非负且可表示、币种为已定义枚举值、汇率大于 0 且可表示、定金 / 佣金比例 0~100、逐行金额与总额 / 定金
/// 在实际 EF 精度（<c>DECIMAL(18,2)</c>）内 checked 可表示；溢出 / 不可表示一律返回受控业务错误；</item>
/// <item><b>EF 精度契约</b>：用 SQL Server 提供程序**仅构建模型**（不打开连接）断言销售订单列的实际存储精度
/// 与规则常量同源；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与字段改写之前拒绝非法条款且零写入；客户端伪造合计 / 定金
/// 被服务端重算覆盖；提交 / 审核在行锁内复核已持久化条款、非法即原子拒绝且状态不变；历史读取 / 打印仍可读
/// 且不被修正。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class SalesOrderWriteValidationTests
{
    // ==================== 1. 唯一权威规则（纯内存） ====================

    [Fact]
    public void 新增写入校验_有效明细为空_拒绝()
    {
        var nullDetails = new SalesOrder { Details = null! };
        var empty = new SalesOrder { Details = new List<SalesOrderDetail>() };
        var allDeleted = NewOrder(new SalesOrderDetail
        {
            Quantity = 1m, UnitPrice = 1m, IsDeleted = true
        });

        foreach (var order in new[] { nullDetails, empty, allDeleted })
        {
            var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Equal(SalesOrderAmountRules.EmptyDetailsText, ex.Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.5)]
    [InlineData(0.001)]  // 正数量但会在 DECIMAL(18,2) 被静默舍入为 0
    [InlineData(0.004)]
    [InlineData(1.005)]
    [InlineData(0.0001)]
    public void 新增写入校验_数量非正或超出存储精度_拒绝(decimal quantity)
    {
        var order = NewOrder(new SalesOrderDetail { Quantity = quantity, UnitPrice = 10m });

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(1.005)]
    public void 新增写入校验_单价为负或超出存储精度_拒绝(decimal unitPrice)
    {
        var order = NewOrder(new SalesOrderDetail { Quantity = 1m, UnitPrice = unitPrice });

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负", ex.Message);
    }

    [Fact]
    public void 新增写入校验_币种未定义_拒绝()
    {
        var order = NewOrder(new SalesOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.Currency = (Currency)999;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CurrencyText, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7.1234)]   // 超出 DECIMAL(18,2)：落库会被静默取整
    public void 新增写入校验_汇率非正或不可表示_拒绝(decimal exchangeRate)
    {
        var order = NewOrder(new SalesOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.ExchangeRate = exchangeRate;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.ExchangeRateText, ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void 新增写入校验_定金比例越界_拒绝(decimal depositRatio)
    {
        var order = NewOrder(new SalesOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.DepositRatio = depositRatio;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("定金比例", ex.Message);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(101)]
    public void 新增写入校验_佣金比例越界_拒绝(decimal commissionRatio)
    {
        var order = NewOrder(new SalesOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.CommissionRatio = commissionRatio;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("佣金比例", ex.Message);
    }

    [Fact]
    public void 新增写入校验_金额乘法溢出_返回受控业务错误()
    {
        var order = NewOrder(new SalesOrderDetail
        {
            Quantity = SalesOrderAmountRules.MaxDecimal18Scale2,
            UnitPrice = SalesOrderAmountRules.MaxDecimal18Scale2
        });

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.AmountOverflowText, ex.Message);
    }

    [Fact]
    public void 新增写入校验_行金额不可表示_拒绝()
    {
        // 0.25 × 1.05 = 0.2625：两个因子都在精度内，但行金额会被落库取整 —— 必须拒绝，绝不写与明细合计不一致的金额。
        var order = NewOrder(new SalesOrderDetail { Quantity = 0.25m, UnitPrice = 1.05m });

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("行金额", ex.Message);
    }

    [Fact]
    public void 新增写入校验_定金金额不可表示_拒绝()
    {
        // 总额 1 × 33.33% = 0.3333 → 超出 DECIMAL(18,2)
        var order = NewOrder(new SalesOrderDetail { Quantity = 1m, UnitPrice = 1m });
        order.DepositRatio = 33.33m;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.DepositOverflowText, ex.Message);
    }

    [Fact]
    public void 新增写入校验_边界精度与零单价政策_通过()
    {
        // 零单价是既有政策：行金额与总额为 0，仍然合法。
        var order = NewOrder(
            new SalesOrderDetail { Quantity = 100m, UnitPrice = 0m },
            new SalesOrderDetail { Quantity = 1234.56m, UnitPrice = 2.5m });
        order.DepositRatio = 30m;

        SalesOrderAmountRules.ValidateNewWrite(order);
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.Calculate(order);

        Assert.Equal(0m, order.Details[0].Amount);
        Assert.Equal(3086.40m, order.Details[1].Amount);
        Assert.Equal(3086.40m, order.TotalAmount);
        Assert.Equal(925.92m, order.DepositAmount);
        Assert.All(order.Details, d => Assert.True(SalesOrderAmountRules.IsRepresentable(d.Amount)));
    }

    [Fact]
    public void 新增写入校验_最大可表示总额边界_通过()
    {
        var order = NewOrder(new SalesOrderDetail
        {
            Quantity = SalesOrderAmountRules.MaxDecimal18Scale2, UnitPrice = 1m
        });
        order.DepositRatio = 0m;

        SalesOrderAmountRules.ValidateNewWrite(order);
        Assert.True(SalesOrderAmountRules.IsRepresentable(SalesOrderAmountRules.MaxDecimal18Scale2));
        Assert.False(SalesOrderAmountRules.IsRepresentable(SalesOrderAmountRules.MaxDecimal18Scale2 + 1m));
        Assert.False(SalesOrderAmountRules.IsRepresentable(decimal.MinValue));
    }

    [Fact]
    public void 规范写入入口_委托唯一权威校验_拒绝口径一致()
    {
        var rejected = NewOrder(new SalesOrderDetail { Quantity = -1m, UnitPrice = 1m });
        var direct = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(rejected));
        var wrapped = Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureValidatedTerms(rejected));

        Assert.Equal(ErrorCodes.InvalidParameter, direct.Code);
        Assert.Equal(direct.Code, wrapped.Code);
        Assert.Equal(direct.Message, wrapped.Message);

        var accepted = NewOrder(new SalesOrderDetail { Quantity = 2m, UnitPrice = 3m });
        SalesOrderMutationRules.EnsureValidatedTerms(accepted);

        Assert.Contains("销售订单", SalesOrderMutationRules.ValidatedTermsText);
        Assert.Contains("ValidateNewWrite", SalesOrderMutationRules.ValidatedTermsText);
    }

    // ==================== 2. EF 实际精度契约（只构建模型，不打开连接） ====================

    [Fact]
    public void EF实际精度_销售订单数量金额汇率列_与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        var expected = $"decimal({SalesOrderAmountRules.DecimalPrecision},{SalesOrderAmountRules.DecimalScale})";

        Assert.Equal(expected, ColumnType(model, typeof(SalesOrderDetail), nameof(SalesOrderDetail.Quantity)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrderDetail), nameof(SalesOrderDetail.UnitPrice)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrderDetail), nameof(SalesOrderDetail.Amount)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.TotalAmount)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.DepositAmount)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.ExchangeRate)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.DepositRatio)));
        Assert.Equal(expected, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.CommissionRatio)));
    }

    // ==================== 3. 控制器：新增 ====================

    [Fact]
    public async Task 新增_数量过小_拒绝且不预约单号不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewOrder(
            new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 0.001m, UnitPrice = 10m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.False(await db.SalesOrders.AnyAsync());
        Assert.False(await db.SalesOrderDetails.AnyAsync());
        // 校验先于单号预约：单据字轨规则未被创建 / 未被消耗。
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_币种未定义_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(new SalesOrderDetail
        {
            ProductId = 1, ProductName = "P1", Quantity = 1m, UnitPrice = 1m
        });
        body.Currency = (Currency)77;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CurrencyText, ex.Message);
        Assert.False(await db.SalesOrders.AnyAsync());
    }

    [Fact]
    public async Task 新增_客户端伪造合计与定金_服务端按明细重算覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var ctl = NewController(db);
        var body = NewOrder(new SalesOrderDetail
        {
            ProductId = 1, ProductName = "P1", Quantity = 10m, UnitPrice = 100m
        });
        body.DepositRatio = 30m;
        body.TotalAmount = 999_999m;      // 伪造
        body.DepositAmount = 888_888m;    // 伪造

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var stored = await db.SalesOrders.AsNoTracking().SingleAsync();
        var lines = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == stored.Id).ToListAsync();
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(300m, stored.DepositAmount);
        Assert.Equal(1000m, lines.Sum(d => d.Amount));
        Assert.Equal(stored.TotalAmount, lines.Sum(d => d.Amount));
    }

    [Fact]
    public async Task 新增_精确落库_明细合计与定金逐分一致()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var ctl = NewController(db);
        var body = NewOrder(
            new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 100m, UnitPrice = 2.5m },
            new SalesOrderDetail { ProductId = 2, ProductName = "P2", Quantity = 40m, UnitPrice = 12.5m });
        body.DepositRatio = 40m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var stored = await db.SalesOrders.AsNoTracking().SingleAsync();
        var lines = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == stored.Id).ToListAsync();
        Assert.Equal(750m, stored.TotalAmount);
        Assert.Equal(300m, stored.DepositAmount);
        Assert.Equal(750m, lines.Sum(d => d.Amount));
    }

    // ==================== 4. 控制器：修改 / 提交 / 审核 ====================

    [Fact]
    public async Task 修改_非法条款_拒绝且原始表头明细状态不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer, DocumentStatus.Pending);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, NewOrder(
            new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 0m, UnitPrice = 100m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ChangeTracker.Entries());

        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.Equal(order.TotalAmount, persisted.TotalAmount);
        Assert.Equal(order.DepositAmount, persisted.DepositAmount);
        Assert.False(persisted.IsDeleted);
        var lines = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(10m, lines[0].Quantity);
        Assert.Equal(100m, lines[0].UnitPrice);
    }

    [Fact]
    public async Task 修改_非法条款_不产生任何下游库存财务变更且保留来源血缘()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer);
        var order = SeedOrder(db, customer, DocumentStatus.Pending, pi.Id, pi.PiNo);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, NewOrder(
            new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 1.005m, UnitPrice = 100m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(pi.Id, persisted.SourcePiId);
        Assert.Equal(pi.PiNo, persisted.SourcePiNo);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.False(await db.Stocks.AnyAsync());
        Assert.False(await db.StockMovements.AnyAsync());
    }

    [Fact]
    public async Task 修改_合法条款_精确重算并保留来源血缘()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer);
        var order = SeedOrder(db, customer, DocumentStatus.Pending, pi.Id, pi.PiNo);
        SeedMasterFixtures(db);

        var body = NewOrder(new SalesOrderDetail
        {
            ProductId = 7, ProductName = "P7", Quantity = 20m, UnitPrice = 50m
        });
        body.CustomerId = customer.Id;
        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id, body));

        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        var lines = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id).ToListAsync();
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(300m, persisted.DepositAmount);
        Assert.Equal(persisted.TotalAmount, lines.Sum(d => d.Amount));
        Assert.Equal(pi.Id, persisted.SourcePiId);
        Assert.Equal(pi.PiNo, persisted.SourcePiNo);
    }

    [Fact]
    public async Task 提交_已持久化条款非法_原子拒绝且状态与证据不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer, DocumentStatus.Pending);
        // 直接改库模拟历史 / 外部写入的非法条款（数量 0），绕过新写入校验。
        var line = await db.SalesOrderDetails.SingleAsync(d => d.SalesOrderId == order.Id);
        line.Quantity = 0m;
        line.Amount = 0m;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(order.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(DocumentStatus.Pending,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(0m, (await db.SalesOrderDetails.AsNoTracking()
            .SingleAsync(d => d.SalesOrderId == order.Id)).Quantity);
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer, DocumentStatus.Submitted);
        var stored = await db.SalesOrders.SingleAsync(o => o.Id == order.Id);
        stored.Currency = (Currency)999;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(order.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CurrencyText, ex.Message);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 提交审核_合法订单_精确落库且血缘不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer);
        var order = SeedOrder(db, customer, DocumentStatus.Pending, pi.Id, pi.PiNo);
        SeedMasterFixtures(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));

        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        var lines = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id).ToListAsync();
        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.Equal(persisted.TotalAmount, lines.Sum(d => d.Amount));
        Assert.Equal(persisted.TotalAmount * persisted.DepositRatio / 100m, persisted.DepositAmount);
        Assert.Equal(pi.Id, persisted.SourcePiId);
        Assert.Equal(pi.PiNo, persisted.SourcePiNo);
    }

    // ==================== 5. 历史读取 / 打印不修正 ====================

    [Fact]
    public async Task 历史读取与打印_非法存储条款仍可读且不被修正()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer, DocumentStatus.Approved);
        var line = await db.SalesOrderDetails.SingleAsync(d => d.SalesOrderId == order.Id);
        line.Quantity = -5m;                 // 非法历史条款
        line.UnitPrice = 1.005m;
        await db.SaveChangesAsync();

        var detail = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id)).Value);
        Assert.Equal(-5m, detail.Data!.Details.Single().Quantity);
        Assert.Equal(1.005m, detail.Data.Details.Single().UnitPrice);

        var printed = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id)).Value);
        Assert.Equal(-5m, printed.Data!.Details.Single().Quantity);

        // 读取 / 打印绝不落库修正历史值。
        var persistedLine = await db.SalesOrderDetails.AsNoTracking()
            .SingleAsync(d => d.SalesOrderId == order.Id);
        Assert.Equal(-5m, persistedLine.Quantity);
        Assert.Equal(1.005m, persistedLine.UnitPrice);
    }

    // ==================== 工厂与种子数据 ====================

    private static SalesOrder NewOrder(params SalesOrderDetail[] details) => new()
    {
        OrderDate = DateTime.Today,
        CustomerId = 1L,
        SalesmanId = 1L,
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        DepositRatio = 30m,
        Details = details.ToList()
    };

    private static SalesOrderController NewController(ErpDbContext db)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "ERP-422 客户",
            DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, BaseCustomer customer, DocumentStatus status,
        long? sourcePiId = null, string sourcePiNo = "", decimal quantity = 10m, decimal unitPrice = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-{Guid.NewGuid():N}"[..18],
            OrderDate = DateTime.Today,
            CustomerId = customer.Id,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Status = status,
            SourcePiId = sourcePiId,
            SourcePiNo = sourcePiNo ?? string.Empty,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 21, ProductName = "PI 商品", Spec = "标准", Unit = "PCS",
                    Quantity = quantity, UnitPrice = unitPrice
                }
            }
        };
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.Calculate(order);
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static ProformaInvoice SeedPi(ErpDbContext db, BaseCustomer customer)
    {
        var pi = new ProformaInvoice
        {
            PiNo = $"PI-{Guid.NewGuid():N}"[..18],
            PiDate = DateTime.Today,
            CustomerId = customer.Id,
            CustomerName = customer.CustomerName,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = 1000m,
            DepositRatio = 30m,
            SalesmanId = 1L,
            Status = DocumentStatus.Approved,
            Details = new List<ProformaInvoiceDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 21, ProductCode = "PX-1", ProductName = "PI 商品", Spec = "标准",
                    Unit = "PCS", Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    /// <summary>
    /// ERP-423：播种既有合法主数据（客户 / 在职业务员 / 商品）供规范销售订单写入使用；
    /// 只补寄存器中缺失的行，绝不改写生产校验口径。
    /// </summary>
    private static void SeedMasterFixtures(ErpDbContext db)
    {
        if (!db.BaseCustomers.Any(c => c.Id == 1L))
            db.BaseCustomers.Add(new BaseCustomer
            {
                Id = 1L, CustomerCode = "C-MR-1", CustomerName = "ERP423 客户", Status = 1, DepositRatio = 30m
            });
        if (!db.BaseEmployees.Any(e => e.Id == 1L))
            db.BaseEmployees.Add(new BaseEmployee
            {
                Id = 1L, EmployeeCode = "E-MR-1", EmployeeName = "ERP423 业务员", IsSalesman = true, Status = 1
            });

        foreach (var id in new[] { 1L, 2L, 7L, 21L })
        {
            if (!db.BaseProducts.Any(p => p.Id == id))
                db.BaseProducts.Add(new BaseProduct
                {
                    Id = id, ProductCode = $"P-MR-{id}", ProductName = $"ERP423 商品 {id}", Status = 1
                });
        }

        db.SaveChanges();
    }
}
