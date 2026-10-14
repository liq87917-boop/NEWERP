using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-537 采购订单税率（<c>db_owner.PurchaseOrders.TaxRate</c>，权威升级脚本声明
/// <c>DECIMAL(18,4)</c>）的**真实 SQL Server** 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）：
/// <list type="number">
/// <item><b>声明列本身</b>：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认该列为 <c>decimal(18,4)</c>，
/// 其余采购订单 / 明细小数列仍为 <c>decimal(18,2)</c>；</item>
/// <item><b>四位小数原样往返</b>：以真实既有授权驱动真实 <see cref="PurchaseOrderController"/> 新增，
/// <c>0.1250</c> / <c>13.0555</c> / <c>99.9999</c> / <c>100</c> / <c>0</c> 以
/// <c>CONVERT(varchar(64), TaxRate)</c> 逐位断言；</item>
/// <item><b>拒绝而不取整</b>：第 5 位小数、<c>99.99995</c>（半进位取整后恰好落到上限 100）、越界税率在
/// 新增 / 修改时受控拒绝，行数与既有行字段不变、不消耗单号；</item>
/// <item><b>护栏必要性</b>：绕过服务端护栏的原始 EF 写入会被真实列静默取整（证明护栏不可省略）。</item>
/// </list>
/// <para>专用目标护栏：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PurchaseOrderTaxRatePersistedBoundsSqlServerTests
    : IClassFixture<PurchaseOrderTaxRatePersistedBoundsSqlServerFixture>
{
    private readonly PurchaseOrderTaxRatePersistedBoundsSqlServerFixture _fixture;

    public PurchaseOrderTaxRatePersistedBoundsSqlServerTests(
        PurchaseOrderTaxRatePersistedBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => PurchaseOrderTaxRatePersistedBoundsSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static PurchaseOrderController NewController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase-orders";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private PurchaseOrder NewBody(params PurchaseOrderDetail[] details) => new()
    {
        OrderDate = DateTime.Today,
        SupplierId = _fixture.SupplierId,
        Currency = Currency.CNY,
        ExchangeRate = 1m,
        TaxRate = 0m,
        Details = details.ToList()
    };

    private PurchaseOrderDetail Line(decimal quantity = 10m, decimal unitPrice = 100m) => new()
    {
        ProductId = _fixture.ProductId, ProductName = "ERP537 商品", Spec = "标准", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice
    };

    // ==================== 1. 声明列精度（EF 模型 + 真实 INFORMATION_SCHEMA 双向确认） ====================

    [Fact]
    public async Task 声明列精度_税率为四位小数其余列仍两位()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var taxRate = db.Model.FindEntityType(typeof(PurchaseOrder))!
            .FindProperty(nameof(PurchaseOrder.TaxRate))!;
        Assert.Equal("decimal(18,4)", taxRate.GetColumnType());
        Assert.Equal(18, taxRate.GetPrecision());
        Assert.Equal(4, taxRate.GetScale());

        // 真实列声明（与 SchemaUpgrader 第 22 段同源）双向确认；其余小数列保持两位小数口径。
        Assert.Equal("decimal(18,4)", await ColumnTypeInSqlAsync("PurchaseOrders", "TaxRate"));
        Assert.Equal("decimal(18,2)", await ColumnTypeInSqlAsync("PurchaseOrders", "TotalAmount"));
        Assert.Equal("decimal(18,2)", await ColumnTypeInSqlAsync("PurchaseOrders", "ExchangeRate"));
        Assert.Equal("decimal(18,2)", await ColumnTypeInSqlAsync("PurchaseOrderDetails", "Quantity"));
        Assert.Equal("decimal(18,2)", await ColumnTypeInSqlAsync("PurchaseOrderDetails", "UnitPrice"));
    }

    // ==================== 2. 四位小数原样往返（真实列文本逐位断言） ====================

    [Theory]
    [InlineData(0, "0.0000")]
    [InlineData(0.1250, "0.1250")]
    [InlineData(13.0555, "13.0555")]
    [InlineData(99.9999, "99.9999")]
    [InlineData(100, "100.0000")]
    public async Task 新增_四位小数税率_真实列逐位原样往返(decimal rate, string expectedText)
    {
        Guard();
        var body = NewBody(Line());
        body.TaxRate = rate;

        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewController(db, _fixture.PrivilegedUserId).Create(body));

        var stored = await NewestFixtureOrderAsync();
        Assert.Equal(rate, stored.TaxRate);                             // 实体回读一致
        Assert.Equal(expectedText, await TaxRateTextAsync(stored.Id));  // 真实列文本逐位一致
    }

    [Fact]
    public async Task 新增_半进位取整恰好落到上限_拒绝而恰好上限本身放行()
    {
        Guard();
        var before = await CountOrdersAsync();

        var rejected = NewBody(Line());
        rejected.TaxRate = 99.99995m;      // 半进位取整后为 100.0000：仍按原值拒绝，绝不取整
        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewController(db, _fixture.PrivilegedUserId).Create(rejected));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Equal(PurchaseOrderAmountRules.TaxRateScaleText, ex.Message);
        }
        Assert.Equal(before, await CountOrdersAsync());

        var accepted = NewBody(Line());
        accepted.TaxRate = 100m;           // 恰好上限：放行并原样存储
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewController(db, _fixture.PrivilegedUserId).Create(accepted));

        var stored = await NewestFixtureOrderAsync();
        Assert.Equal(100m, stored.TaxRate);
        Assert.Equal("100.0000", await TaxRateTextAsync(stored.Id));
    }

    // ==================== 3. 拒绝而不取整：零写入且不消耗单号 ====================

    [Theory]
    [InlineData(0.12505)]
    [InlineData(13.05555)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task 新增_五位小数或越界_拒绝且零写入不消耗单号(decimal rate)
    {
        Guard();
        var beforeOrders = await CountOrdersAsync();
        var beforeRules = await NumberRulesSignatureAsync();

        var body = NewBody(Line());
        body.TaxRate = rate;
        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewController(db, _fixture.PrivilegedUserId).Create(body));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        Assert.Equal(beforeOrders, await CountOrdersAsync());
        Assert.Equal(beforeRules, await NumberRulesSignatureAsync());
    }

    // ==================== 4. 修改（既有行逐位不变 / 原样往返） ====================

    [Fact]
    public async Task 修改_五位小数税率_拒绝且既有行逐位不变()
    {
        Guard();
        var seeded = await SeedOrderAsync(3.5m);
        var before = await TaxRateTextAsync(seeded.Id);

        var body = NewBody(Line());
        body.TaxRate = 13.05555m;
        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewController(db, _fixture.PrivilegedUserId).Update(seeded.Id, body));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Equal(PurchaseOrderAmountRules.TaxRateScaleText, ex.Message);
        }

        Assert.Equal(before, await TaxRateTextAsync(seeded.Id));   // 既有行逐位不变
    }

    [Fact]
    public async Task 修改_四位小数税率_真实列逐位原样往返()
    {
        Guard();
        var seeded = await SeedOrderAsync(3.5m);

        var body = NewBody(Line());
        body.TaxRate = 0.1250m;
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(
                await NewController(db, _fixture.PrivilegedUserId).Update(seeded.Id, body));

        Assert.Equal("0.1250", await TaxRateTextAsync(seeded.Id));
    }

    // ==================== 5. 护栏必要性：绕过服务端护栏的原始 EF 写入被真实列静默取整 ====================

    [Fact]
    public async Task 绕过护栏的原始EF写入_真实列静默取整而非逐位原样()
    {
        Guard();
        await using (var db = _fixture.CreateDbContext())
        {
            var order = NewBody(Line());
            order.OrderNo = $"PO-537-RAW-{Guid.NewGuid():N}"[..20];
            order.Status = DocumentStatus.Pending;
            order.TaxRate = 13.05555m;      // 绕过 PurchaseOrderAmountRules 直接落库
            PurchaseOrderAmountRules.ApplyDetailAmounts(order);
            PurchaseOrderAmountRules.Calculate(order);
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var stored = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
            Assert.NotEqual(13.05555m, stored.TaxRate);   // 真实列静默改写，绝不逐位原样
            Assert.True(PurchaseOrderAmountRules.IsTaxRateRepresentable(stored.TaxRate));
            Assert.Equal("13.0556", await TaxRateTextAsync(order.Id));
        }
    }

    // ==================== SQL 读取辅助（只读，绝不修改任何行） ====================

    private async Task<string> ColumnTypeInSqlAsync(string table, string column)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' "
            + "+ CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = 'db_owner' AND TABLE_NAME = @table AND COLUMN_NAME = @column";
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return (string?)await cmd.ExecuteScalarAsync() ?? string.Empty;
    }

    private async Task<string> TaxRateTextAsync(long orderId)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(varchar(64), TaxRate) FROM db_owner.PurchaseOrders WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        return (string?)await cmd.ExecuteScalarAsync() ?? string.Empty;
    }

    private async Task<int> CountOrdersAsync()
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM db_owner.PurchaseOrders";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<string> NumberRulesSignatureAsync()
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT CAST(COUNT(*) AS varchar(20)) + ':' + CAST(ISNULL(SUM(CurrentSequence), 0) AS varchar(30)) "
            + "FROM db_owner.SysDocumentNumberRules";
        return (string?)await cmd.ExecuteScalarAsync() ?? string.Empty;
    }

    private async Task<PurchaseOrder> NewestFixtureOrderAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.SupplierId == _fixture.SupplierId)
            .OrderByDescending(o => o.Id)
            .FirstAsync();
    }

    private async Task<PurchaseOrder> SeedOrderAsync(decimal taxRate)
    {
        await using var db = _fixture.CreateDbContext();
        var order = NewBody(Line());
        order.OrderNo = $"PO-537-{Guid.NewGuid():N}"[..18];
        order.Status = DocumentStatus.Pending;
        order.TaxRate = taxRate;
        PurchaseOrderAmountRules.ApplyDetailAmounts(order);
        PurchaseOrderAmountRules.Calculate(order);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

/// <summary>
/// ERP-537 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 与 <c>Integrated Security=true</c>；每次运行只创建一个 <b>全新 GUID 后缀库</b>；
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class PurchaseOrderTaxRatePersistedBoundsSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_ERP537TR";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long SupplierId { get; private set; }
    public long ProductId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-537] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        await using (var conn = new SqlConnection(
            new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-537] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 供应商 / 商品夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        var role = new SysRole
        {
            RoleName = "ERP537 特权角色", RoleCode = $"ERP537-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"erp537-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP537 隔离账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = user.Id;

        var menu = await db.SysMenus.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
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
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-537-{Guid.NewGuid():N}", SupplierName = "ERP537 供应商", Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        var product = new BaseProduct
        {
            ProductCode = $"P-537-{Guid.NewGuid():N}", ProductName = "ERP537 商品",
            Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        SupplierId = supplier.Id;
        ProductId = product.Id;

        Console.WriteLine("[ERP-537] 既有授权 + 供应商 / 商品夹具就绪（受控只读，不新增权限模型）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseOrderTaxRatePersistedBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseOrderTaxRatePersistedBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}
