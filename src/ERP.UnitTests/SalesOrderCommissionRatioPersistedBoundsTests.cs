using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-536 销售订单佣金比例（<c>db_owner.SalesOrders.CommissionRatio</c>，权威升级脚本声明
/// <c>DECIMAL(18,4)</c>）的**持久化精度边界**单元测试（内存库 + 真实控制器；不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>纯判定</b>（<see cref="SalesOrderAmountRules.IsCommissionRatioRepresentable"/>）：既有 0~100
/// 政策区间内且最多 4 位小数放行；第 5 位小数、半进位取整恰好落到上限、越界与量级溢出一律拒绝；</item>
/// <item><b>唯一权威校验</b>（<see cref="SalesOrderAmountRules.Validate"/> / <c>ValidateNewWrite</c>）在原值上判定，
/// 绝不取整 / 改写：可表示的四位小数逐位原样保留；</item>
/// <item><b>EF 映射契约</b>：仅 <c>SalesOrder.CommissionRatio</c> 映射为 <c>decimal(18,4)</c>，
/// 其余销售订单 / 明细小数列仍为 <c>decimal(18,2)</c>（口径不变）；</item>
/// <item><b>真实控制器新增 / 修改</b>：五位小数 / 越界请求受控拒绝且零写入、不预约单号、既有行逐字段不变；
/// 四位小数请求原样往返，服务端明细 / 合计 / 定金重算与既有授权结构不变。</item>
/// </list>
/// <para>「真实 SQL 列上逐位往返」由
/// <c>ERP.IntegrationTests/SalesOrderCommissionRatioPersistedBoundsSqlServerTests.cs</c> 在专用 localdb 上验证；
/// 内存库不施加列精度，因此本文件只断言提交值与权威规则的判定结果。</para>
/// </summary>
public class SalesOrderCommissionRatioPersistedBoundsTests
{
    // ==================== 1. 可表示性纯判定（政策区间 + 四位小数） ====================

    [Theory]
    [InlineData(0)]
    [InlineData(0.0001)]
    [InlineData(0.1250)]
    [InlineData(3.5)]
    [InlineData(12.3456)]
    [InlineData(99.9999)]
    [InlineData(100)]
    public void 可表示性纯判定_政策区间内四位小数_放行(decimal value)
        => Assert.True(SalesOrderAmountRules.IsCommissionRatioRepresentable(value));

    [Theory]
    [InlineData(-0.00005)]          // 负的第 5 位小数（量级在区间内）
    [InlineData(-1)]                // 低于政策下限
    [InlineData(0.12505)]           // 第 5 位小数
    [InlineData(12.34567)]          // 第 5 位小数
    [InlineData(99.99995)]          // 半进位取整后恰好落到上限 100：仍按原值拒绝
    [InlineData(100.0001)]          // 高于政策上限
    [InlineData(101)]
    [InlineData(100000000000000)]   // 1e14：量级溢出
    [InlineData(1e28)]              // 远大于政策上限（decimal 范围内）
    public void 可表示性纯判定_第5位小数或越界或量级溢出_拒绝(decimal value)
        => Assert.False(SalesOrderAmountRules.IsCommissionRatioRepresentable(value));

    // ==================== 2. 唯一权威校验：原值判定，绝不改写 ====================

    [Theory]
    [InlineData(0)]
    [InlineData(0.1250)]
    [InlineData(12.3456)]
    [InlineData(99.9999)]
    [InlineData(100)]
    public void 校验_四位小数内_原样放行且不改写(decimal value)
    {
        var order = NewOrder(Line());
        order.CommissionRatio = value;

        SalesOrderAmountRules.Validate(order);
        SalesOrderAmountRules.ValidateNewWrite(order);

        Assert.Equal(value, order.CommissionRatio);
    }

    [Theory]
    [InlineData(0.12505)]
    [InlineData(12.34567)]
    [InlineData(99.99995)]
    public void 校验_第5位小数_受控拒绝且文案指向声明精度(decimal value)
    {
        var order = NewOrder(Line());
        order.CommissionRatio = value;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.Validate(order));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CommissionRatioScaleText, ex.Message);
        Assert.Equal(value, order.CommissionRatio);   // 拒绝不改写原值
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(101)]
    [InlineData(100000000000000)]
    public void 校验_越界或量级溢出_沿用既有0到100拒绝文案(decimal value)
    {
        var order = NewOrder(Line());
        order.CommissionRatio = value;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.Validate(order));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("0~100", ex.Message, StringComparison.Ordinal);
        Assert.Equal(value, order.CommissionRatio);
    }

    [Theory]
    [InlineData(0.12505)]
    [InlineData(12.34567)]
    public void 新增写入校验_五位小数佣金比例_拒绝(decimal value)
    {
        var order = NewOrder(Line());
        order.CommissionRatio = value;

        var ex = Assert.Throws<BusinessException>(() => SalesOrderAmountRules.ValidateNewWrite(order));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CommissionRatioScaleText, ex.Message);
        // 与规范写入入口同一口径。
        var wrapped = Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureValidatedTerms(order));
        Assert.Equal(ex.Code, wrapped.Code);
        Assert.Equal(ex.Message, wrapped.Message);
    }

    // ==================== 3. EF 实际列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void EF实际列精度_仅佣金比例为四位小数其余列仍两位()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP536_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal(18, SalesOrderAmountRules.DecimalPrecision);
        Assert.Equal(2, SalesOrderAmountRules.DecimalScale);
        Assert.Equal(4, SalesOrderAmountRules.CommissionRatioScale);

        const string twoDecimals = "decimal(18,2)";
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrderDetail), nameof(SalesOrderDetail.Quantity)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrderDetail), nameof(SalesOrderDetail.UnitPrice)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrderDetail), nameof(SalesOrderDetail.Amount)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.TotalAmount)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.DepositAmount)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.ExchangeRate)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.DepositRatio)));

        // ERP-536：佣金比例与权威升级脚本的 db_owner.SalesOrders.CommissionRatio 声明同源。
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(SalesOrder), nameof(SalesOrder.CommissionRatio)));
        var commission = model.FindEntityType(typeof(SalesOrder))!
            .FindProperty(nameof(SalesOrder.CommissionRatio))!;
        Assert.Equal(18, commission.GetPrecision());
        Assert.Equal(4, commission.GetScale());
    }

    [Fact]
    public void 源码契约_EF映射与建表声明及唯一索引同源()
    {
        var config = ReadSource("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");
        Assert.Contains(
            "modelBuilder.Entity<SalesOrder>().Property(x => x.CommissionRatio).HasPrecision(18, 4)",
            config, StringComparison.Ordinal);
        // 只声明佣金比例：销售订单 / 明细其余列不新增任何精度声明（默认两位小数口径不变）。
        Assert.DoesNotContain("modelBuilder.Entity<SalesOrder>().Property(x => x.TotalAmount)", config, StringComparison.Ordinal);
        Assert.DoesNotContain("modelBuilder.Entity<SalesOrder>().Property(x => x.ExchangeRate)", config, StringComparison.Ordinal);
        Assert.DoesNotContain("modelBuilder.Entity<SalesOrderDetail>().Property", config, StringComparison.Ordinal);

        var upgrader = ReadSource("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs");
        Assert.Contains("'db_owner.SalesOrders', 'CommissionRatio')", upgrader, StringComparison.Ordinal);
        Assert.Contains("ADD CommissionRatio DECIMAL(18,4)", upgrader, StringComparison.Ordinal);

        var rules = ReadSource("src", "ERP.Application", "Services", "SalesOrderAmountRules.cs");
        Assert.Contains("CommissionRatioScale = 4", rules, StringComparison.Ordinal);
        Assert.Contains("IsCommissionRatioRepresentable", rules, StringComparison.Ordinal);
        Assert.Contains("ValidateCommissionRatio", rules, StringComparison.Ordinal);

        // 新增 / 修改仍在单号预约与任何字段改写之前先按唯一权威口径校验（被拒绝的请求零写入、零占号）。
        var controller = ReadSource("src", "ERP.Api", "Controllers", "SalesOrderController.cs");
        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart);
        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("SalesOrderMutationRules.EnsureValidatedTerms(entity)", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));
        var update = controller[updateStart..];
        Assert.True(update.IndexOf("SalesOrderMutationRules.EnsureValidatedTerms(entity)", StringComparison.Ordinal)
                    < update.IndexOf("existing.CommissionRatio = entity.CommissionRatio", StringComparison.Ordinal));
    }

    // ==================== 4. 真实控制器：新增 ====================

    [Fact]
    public async Task 新增_五位小数佣金比例_拒绝且零写入不占单号()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.CommissionRatio = 12.34567m;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CommissionRatioScaleText, ex.Message);
        Assert.False(await db.SalesOrders.AnyAsync());
        Assert.False(await db.SalesOrderDetails.AnyAsync());
        // 校验先于单号预约：单据字轨规则未被创建 / 未被消耗。
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_四位小数佣金比例_原样落库往返()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.CommissionRatio = 12.3456m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var stored = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.Equal(12.3456m, stored.CommissionRatio);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(300m, stored.DepositAmount);
    }

    [Fact]
    public async Task 新增_零佣金比例_既有政策不变()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.CommissionRatio = 0m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        Assert.Equal(0m, (await db.SalesOrders.AsNoTracking().SingleAsync()).CommissionRatio);
    }

    // ==================== 5. 真实控制器：修改 ====================

    [Fact]
    public async Task 修改_五位小数佣金比例_拒绝且既有行逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer, 3.5m);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.CommissionRatio = 0.12505m;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CommissionRatioScaleText, ex.Message);

        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(3.5m, persisted.CommissionRatio);
        Assert.Equal(order.TotalAmount, persisted.TotalAmount);
        Assert.Equal(order.DepositAmount, persisted.DepositAmount);
        Assert.Equal(order.OrderNo, persisted.OrderNo);
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task 修改_四位小数佣金比例_原样落库往返且服务端重算不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer, 3.5m);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.CommissionRatio = 0.1250m;

        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id, body));

        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(0.1250m, persisted.CommissionRatio);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(300m, persisted.DepositAmount);
    }

    // ==================== 工厂与种子数据 ====================

    private static SalesOrderDetail Line() => new()
    {
        ProductId = 1, ProductName = "P1", Quantity = 10m, UnitPrice = 100m
    };

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
            Id = 1L,
            CustomerCode = $"C-536-{Guid.NewGuid():N}",
            CustomerName = "ERP536 客户",
            Status = 1,
            DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, BaseCustomer customer, decimal commissionRatio)
    {
        SeedMasterFixtures(db);
        var order = NewOrder(Line());
        order.OrderNo = $"SO-536-{Guid.NewGuid():N}"[..18];
        order.CustomerId = customer.Id;
        order.CommissionRatio = commissionRatio;
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.Calculate(order);
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种既有合法主数据（客户 / 在职业务员 / 商品）供规范销售订单写入使用。</summary>
    private static void SeedMasterFixtures(ErpDbContext db)
    {
        if (!db.BaseCustomers.Any(c => c.Id == 1L))
            db.BaseCustomers.Add(new BaseCustomer
            {
                Id = 1L, CustomerCode = "C-MR-536", CustomerName = "ERP536 主数据客户",
                Status = 1, DepositRatio = 30m
            });
        if (!db.BaseEmployees.Any(e => e.Id == 1L))
            db.BaseEmployees.Add(new BaseEmployee
            {
                Id = 1L, EmployeeCode = "E-MR-536", EmployeeName = "ERP536 业务员",
                IsSalesman = true, Status = 1
            });

        foreach (var id in new[] { 1L, 2L })
        {
            if (!db.BaseProducts.Any(p => p.Id == id))
                db.BaseProducts.Add(new BaseProduct
                {
                    Id = id, ProductCode = $"P-MR-536-{id}", ProductName = $"ERP536 商品 {id}", Status = 1
                });
        }

        db.SaveChanges();
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));
}
