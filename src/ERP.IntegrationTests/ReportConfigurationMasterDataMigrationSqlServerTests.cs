using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-317 基础资料打印族迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具（客户 + 商品），
/// 通过受控数据集适配器验证字段顺序 / null / 软删除 / 有界分页 / 数值原样保留，覆盖至少两个基础资料族。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationMasterDataMigrationSqlServerTests
    : IClassFixture<ReportConfigurationMasterDataMigrationSqlServerFixture>
{
    private readonly ReportConfigurationMasterDataMigrationSqlServerFixture _fixture;

    public ReportConfigurationMasterDataMigrationSqlServerTests(ReportConfigurationMasterDataMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationMasterDataMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationMasterDataMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    private static ReportConfigurationDefinition Definition(string familyKey)
    {
        var family = ReportConfigurationMasterDataCatalog.Resolve(familyKey);
        return new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = family.DatasetKey,
            Fields = family.Columns.Select(c => c.Key).ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 100 },
        };
    }

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    [Fact]
    public async Task 客户预览_非空SQL_保留字段顺序null与软删除()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("customer"), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(
            ReportConfigurationMasterDataCatalog.Resolve("customer").Columns.Select(c => c.Key).ToArray(),
            preview.Columns.Select(c => c.Key).ToArray());
        Assert.True(preview.Total >= 2);
        Assert.DoesNotContain(preview.Rows, r =>
            string.Equals((string?)r["customerCode"], "ERP317-CUST-DEL", StringComparison.OrdinalIgnoreCase));

        var active = Assert.Single(preview.Rows, r =>
            string.Equals((string?)r["customerCode"], "ERP317-CUST-1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ERP317 客户一", (string?)active["customerName"]);
        Assert.Null(active["creditDays"]);
    }

    [Fact]
    public async Task 商品预览_非空SQL_保留字段顺序与数值()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("product").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("product"), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(
            ReportConfigurationMasterDataCatalog.Resolve("product").Columns.Select(c => c.Key).ToArray(),
            preview.Columns.Select(c => c.Key).ToArray());
        Assert.True(preview.Total >= 2);

        var product = Assert.Single(preview.Rows, r =>
            string.Equals((string?)r["productCode"], "ERP317-PROD-1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(12.50m, (decimal)product["salePrice"]!);
        Assert.Equal(7.25m, (decimal)product["costPrice"]!);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具（客户 + 商品）。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationMasterDataMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP317";
    private const string UserName = "ERP317-ADMIN";
    private const string RoleCode = "ERP317-SYS";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        await SeedFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private async Task SeedFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);
        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id && !ur.IsDeleted))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var code in new[] { "customer", "product" })
        {
            var menu = await EnsureMenuAsync(db, code);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        await EnsureMasterDataAsync(db);

        PrivilegedUserId = user.Id;
    }

    private static async Task EnsureMasterDataAsync(ErpDbContext db)
    {
        if (!await db.BaseCustomers.AnyAsync(c => !c.IsDeleted && c.CustomerCode == "ERP317-CUST-1"))
        {
            db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "ERP317-CUST-1", CustomerName = "ERP317 客户一", Status = 1 });
            await db.SaveChangesAsync();
        }

        if (!await db.BaseCustomers.AnyAsync(c => !c.IsDeleted && c.CustomerCode == "ERP317-CUST-2"))
        {
            db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "ERP317-CUST-2", CustomerName = "ERP317 客户二", CreditDays = 30, Status = 1 });
            await db.SaveChangesAsync();
        }

        if (!await db.BaseCustomers.AnyAsync(c => c.IsDeleted && c.CustomerCode == "ERP317-CUST-DEL"))
        {
            db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "ERP317-CUST-DEL", CustomerName = "ERP317 已删除客户", IsDeleted = true, Status = 1 });
            await db.SaveChangesAsync();
        }

        if (!await db.BaseProducts.AnyAsync(p => !p.IsDeleted && p.ProductCode == "ERP317-PROD-1"))
        {
            db.BaseProducts.Add(new BaseProduct
            {
                ProductCode = "ERP317-PROD-1",
                ProductName = "ERP317 商品一",
                Spec = "标准",
                Unit = "PCS",
                SalePrice = 12.50m,
                CostPrice = 7.25m,
                Status = 1,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.BaseProducts.AnyAsync(p => !p.IsDeleted && p.ProductCode == "ERP317-PROD-2"))
        {
            db.BaseProducts.Add(new BaseProduct
            {
                ProductCode = "ERP317-PROD-2",
                ProductName = "ERP317 商品二",
                Spec = "标准",
                Unit = "PCS",
                SalePrice = 20m,
                CostPrice = 11m,
                Status = 1,
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null)
            return menu;

        menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    private static async Task<SysUser> EnsureUserAsync(ErpDbContext db)
    {
        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == UserName && !u.IsDeleted);
        if (user is not null)
            return user;

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP317 隔离账号", Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SysRole> EnsureRoleAsync(ErpDbContext db)
    {
        var role = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == RoleCode && !r.IsDeleted);
        if (role is not null)
            return role;

        role = new SysRole { RoleName = RoleCode, RoleCode = RoleCode, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};Integrated Security=true;TrustServerCertificate=true;";

    private static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
    }
}


