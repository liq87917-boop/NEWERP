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
/// ERP-537 采购订单税率（<c>db_owner.PurchaseOrders.TaxRate</c>，权威升级脚本声明 <c>DECIMAL(18,4)</c>）的
/// **持久化精度边界**单元测试（内存库 + 真实控制器；不连接 SQL Server、不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>纯判定</b>（<see cref="PurchaseOrderAmountRules.IsTaxRateRepresentable"/>）：既有 0~100
/// 政策区间内且最多 4 位小数放行；第 5 位小数、半进位取整恰好落到上限、越界与量级溢出一律拒绝；</item>
/// <item><b>唯一权威校验</b>（<see cref="PurchaseOrderAmountRules.Validate"/> / <c>ValidateNewWrite</c>）在原值上判定，
/// 绝不取整 / 改写：可表示的四位小数逐位原样保留；</item>
/// <item><b>EF 映射契约</b>：仅 <c>PurchaseOrder.TaxRate</c> 映射为 <c>decimal(18,4)</c>，
/// 其余采购订单 / 明细小数列仍为 <c>decimal(18,2)</c>（口径不变）；</item>
/// <item><b>真实控制器新增 / 修改</b>：五位小数 / 越界请求受控拒绝且零写入、不预约单号、既有行逐字段不变；
/// 四位小数请求原样往返，服务端明细 / 合计重算与既有授权结构不变。</item>
/// </list>
/// <para>「真实 SQL 列上逐位往返」由
/// <c>ERP.IntegrationTests/PurchaseOrderTaxRatePersistedBoundsSqlServerTests.cs</c> 在专用 localdb 上验证；
/// 内存库不施加列精度，因此本文件只断言提交值与权威规则的判定结果。</para>
/// </summary>
public class PurchaseOrderTaxRatePersistedBoundsTests
{
    private const long SupplierId = 537001L;
    private const long ProductId = 537101L;

    // ==================== 1. 可表示性纯判定（政策区间 + 四位小数） ====================

    [Theory]
    [InlineData(0)]
    [InlineData(0.0001)]
    [InlineData(0.1250)]
    [InlineData(3.5)]
    [InlineData(13.0555)]
    [InlineData(99.9999)]
    [InlineData(100)]
    public void 可表示性纯判定_政策区间内四位小数_放行(decimal value)
        => Assert.True(PurchaseOrderAmountRules.IsTaxRateRepresentable(value));

    [Theory]
    [InlineData(-0.00005)]          // 负的第 5 位小数（量级在区间内）
    [InlineData(-1)]                // 低于政策下限
    [InlineData(0.12505)]           // 第 5 位小数
    [InlineData(13.05555)]          // 第 5 位小数
    [InlineData(99.99995)]          // 半进位取整后恰好落到上限 100：仍按原值拒绝
    [InlineData(100.0001)]          // 高于政策上限
    [InlineData(101)]
    [InlineData(100000000000000)]   // 1e14：量级溢出
    [InlineData(1e28)]              // 远大于政策上限（decimal 范围内）
    public void 可表示性纯判定_第5位小数或越界或量级溢出_拒绝(decimal value)
        => Assert.False(PurchaseOrderAmountRules.IsTaxRateRepresentable(value));

    // ==================== 2. 唯一权威校验：原值判定，绝不改写 ====================

    [Theory]
    [InlineData(0)]
    [InlineData(0.1250)]
    [InlineData(13.0555)]
    [InlineData(99.9999)]
    [InlineData(100)]
    public void 校验_四位小数内_原样放行且不改写(decimal value)
    {
        var order = NewOrder(Line());
        order.TaxRate = value;

        PurchaseOrderAmountRules.Validate(order);
        PurchaseOrderAmountRules.ValidateNewWrite(order);

        Assert.Equal(value, order.TaxRate);
    }

    [Theory]
    [InlineData(0.12505)]
    [InlineData(13.05555)]
    [InlineData(99.99995)]
    public void 校验_第5位小数_受控拒绝且文案指向声明精度(decimal value)
    {
        var order = NewOrder(Line());
        order.TaxRate = value;

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.Validate(order));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.TaxRateScaleText, ex.Message);
        Assert.Equal(value, order.TaxRate);   // 拒绝不改写原值
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void 校验_越界税率_沿用既有0到100政策文案(decimal value)
    {
        var order = NewOrder(Line());
        order.TaxRate = value;

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.Validate(order));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("0~100", ex.Message, StringComparison.Ordinal);
        Assert.Equal(value, order.TaxRate);
    }

    [Theory]
    [InlineData(13.05555)]
    [InlineData(99.99995)]
    public void 新增写入校验_五位小数税率_拒绝且与规范写入入口同口径(decimal value)
    {
        var order = NewOrder(Line());
        order.TaxRate = value;

        var ex = Assert.Throws<BusinessException>(() => PurchaseOrderAmountRules.ValidateNewWrite(order));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.TaxRateScaleText, ex.Message);

        // 与规范写入入口（EnsureValidatedTerms）同一口径，不存在第二套税率精度规则。
        var wrapped = Assert.Throws<BusinessException>(() => PurchaseOrderMutationRules.EnsureValidatedTerms(order));
        Assert.Equal(ex.Code, wrapped.Code);
        Assert.Equal(ex.Message, wrapped.Message);
    }

    // ==================== 3. EF 实际列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void EF实际列精度_仅税率为四位小数其余列仍两位()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP537_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal(18, PurchaseOrderAmountRules.DecimalPrecision);
        Assert.Equal(2, PurchaseOrderAmountRules.DecimalScale);
        Assert.Equal(4, PurchaseOrderAmountRules.TaxRateScale);

        const string twoDecimals = "decimal(18,2)";
        Assert.Equal(twoDecimals, ColumnType(model, typeof(PurchaseOrderDetail), nameof(PurchaseOrderDetail.Quantity)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(PurchaseOrderDetail), nameof(PurchaseOrderDetail.UnitPrice)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(PurchaseOrderDetail), nameof(PurchaseOrderDetail.Amount)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(PurchaseOrder), nameof(PurchaseOrder.TotalAmount)));
        Assert.Equal(twoDecimals, ColumnType(model, typeof(PurchaseOrder), nameof(PurchaseOrder.ExchangeRate)));

        // ERP-537：税率与权威升级脚本的 db_owner.PurchaseOrders.TaxRate DECIMAL(18,4) 声明同源。
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(PurchaseOrder), nameof(PurchaseOrder.TaxRate)));
        var taxRate = model.FindEntityType(typeof(PurchaseOrder))!
            .FindProperty(nameof(PurchaseOrder.TaxRate))!;
        Assert.Equal(PurchaseOrderAmountRules.DecimalPrecision, taxRate.GetPrecision());
        Assert.Equal(PurchaseOrderAmountRules.TaxRateScale, taxRate.GetScale());
    }

    [Fact]
    public void 源码契约_EF映射与建表声明及护栏先于占号与写入()
    {
        var config = ReadSource("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");
        Assert.Contains(
            "modelBuilder.Entity<PurchaseOrder>().Property(x => x.TaxRate).HasPrecision(18, 4)",
            config, StringComparison.Ordinal);
        // 只声明税率：采购订单 / 明细其余列不新增任何精度声明（默认两位小数口径不变）。
        Assert.DoesNotContain("modelBuilder.Entity<PurchaseOrder>().Property(x => x.TotalAmount)",
            config, StringComparison.Ordinal);
        Assert.DoesNotContain("modelBuilder.Entity<PurchaseOrder>().Property(x => x.ExchangeRate)",
            config, StringComparison.Ordinal);

        var upgrader = ReadSource("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs");
        Assert.Contains("ADD TaxRate DECIMAL(18,4) NOT NULL DEFAULT 0", upgrader, StringComparison.Ordinal);

        // 新增：条款校验（含税率四位小数逐位原样判定）先于单号预约；修改：先于表头 / 明细改写。
        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseOrderController.cs");
        var createStart = controller.IndexOf("public async Task<IActionResult> Create(", StringComparison.Ordinal);
        Assert.True(createStart > 0);
        var create = controller[createStart..];
        Assert.True(
            create.IndexOf("PurchaseOrderMutationRules.EnsureValidatedTerms(entity)", StringComparison.Ordinal)
            < create.IndexOf("_noService.GenerateAsync(DocumentType.PurchaseOrder)", StringComparison.Ordinal));

        var updateStart = controller.IndexOf("public async Task<IActionResult> Update(", StringComparison.Ordinal);
        Assert.True(updateStart > 0);
        var update = controller[updateStart..];
        Assert.True(
            update.IndexOf("PurchaseOrderMutationRules.EnsureValidatedTerms(entity)", StringComparison.Ordinal)
            < update.IndexOf("existing.TaxRate = entity.TaxRate", StringComparison.Ordinal));
    }

    // ==================== 4. 真实控制器：新增 ====================

    [Fact]
    public async Task 新增_五位小数税率_拒绝且零写入不占单号()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.TaxRate = 13.05555m;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.TaxRateScaleText, ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.PurchaseOrderDetails.AnyAsync());
        // 校验先于单号预约：单据字轨规则未被创建 / 未被消耗。
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_四位小数税率_原样落库往返()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.TaxRate = 13.0555m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var stored = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details).SingleAsync();
        Assert.Equal(13.0555m, stored.TaxRate);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(1000m, stored.Details.Single(d => !d.IsDeleted).Amount);
    }

    [Fact]
    public async Task 新增_零税率_既有政策不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.TaxRate = 0m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        Assert.Equal(0m, (await db.PurchaseOrders.AsNoTracking().SingleAsync()).TaxRate);
    }

    // ==================== 5. 真实控制器：修改 ====================

    [Fact]
    public async Task 修改_五位小数税率_拒绝且既有行逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, taxRate: 3.5m);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.TaxRate = 13.05555m;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderAmountRules.TaxRateScaleText, ex.Message);

        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(3.5m, persisted.TaxRate);
        Assert.Equal(order.TotalAmount, persisted.TotalAmount);
        Assert.Equal(order.OrderNo, persisted.OrderNo);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task 修改_四位小数税率_原样落库往返且服务端重算不变()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, taxRate: 3.5m);
        var ctl = NewController(db);
        var body = NewOrder(Line());
        body.TaxRate = 0.1250m;

        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id, body));

        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(0.1250m, persisted.TaxRate);
        Assert.Equal(1000m, persisted.TotalAmount);
    }

    // ==================== 工厂与种子数据 ====================

    private static PurchaseOrderDetail Line() => new()
    {
        ProductId = ProductId, ProductName = "P1", Spec = "标准", Unit = "PCS",
        Quantity = 10m, UnitPrice = 100m
    };

    private static PurchaseOrder NewOrder(params PurchaseOrderDetail[] details) => new()
    {
        OrderDate = DateTime.Today,
        SupplierId = SupplierId,
        Currency = Currency.CNY,
        ExchangeRate = 1m,
        TaxRate = 0m,
        Details = details.ToList()
    };

    private static PurchaseOrderController NewController(ErpDbContext db)
    {
        SeedMasterFixtures(db);
        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, SeedPrivilegedPurchaseUser(db));
        return controller;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, decimal taxRate)
    {
        SeedMasterFixtures(db);
        var order = NewOrder(Line());
        order.OrderNo = $"PO-537-{Guid.NewGuid():N}"[..18];
        order.TaxRate = taxRate;
        PurchaseOrderAmountRules.ApplyDetailAmounts(order);
        PurchaseOrderAmountRules.Calculate(order);
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种既有合法主数据（供应商 / 商品）供规范采购订单写入复核使用。</summary>
    private static void SeedMasterFixtures(ErpDbContext db)
    {
        if (!db.BaseSuppliers.Any(s => s.Id == SupplierId))
            db.BaseSuppliers.Add(new BaseSupplier
            {
                Id = SupplierId, SupplierCode = $"S-537-{SupplierId}", SupplierName = "ERP537 供应商", Status = 1
            });
        if (!db.BaseProducts.Any(p => p.Id == ProductId))
            db.BaseProducts.Add(new BaseProduct
            {
                Id = ProductId, ProductCode = $"P-537-{ProductId}", ProductName = "ERP537 商品",
                Unit = "PCS", Status = 1
            });
        db.SaveChanges();
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有 purchase-order 菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedPurchaseUser(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
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
        }

        var role = new SysRole
        {
            RoleName = "ERP537 特权角色", RoleCode = $"ERP537-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"erp537-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP537 隔离账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            new[] { directory!.FullName }.Concat(segments).ToArray()));
    }
}
