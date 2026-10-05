using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-312 受控打印模板绑定目录的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空打印模板夹具，
/// 通过真实 EF 上下文验证目录枚举 / 精确菜单拒绝 / 不支持族显式阻塞 / 绑定字段顺序与别名注入。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationPrintTemplateSqlServerTests
    : IClassFixture<ReportConfigurationPrintTemplateSqlServerFixture>
{
    private readonly ReportConfigurationPrintTemplateSqlServerFixture _fixture;

    public ReportConfigurationPrintTemplateSqlServerTests(ReportConfigurationPrintTemplateSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationPrintTemplateSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationPrintTemplateSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    private sealed class FakeReader : ILegacyBillExportReadService
    {
        public Task<LegacyBillExportPage> ReadPageAsync(LegacyBillExportQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new LegacyBillExportPage());
    }

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new LegacyBillExportReportConfigurationDatasetProvider(
                db, new FakeReader(), LegacyBillExportCatalog.Resolve("sales-order").DatasetKey),
        });

    private static ReportConfigurationPrintTemplateCatalog BuildService(ErpDbContext db)
        => new(db, BuildCatalog(db));

    private static ReportPrintTemplateBindingRequest Bind(long templateId, params string[] fieldKeys)
        => new() { FamilyKey = "sales-order", TemplateId = templateId, FieldKeys = fieldKeys.ToList() };

    [Fact]
    public async Task Catalog_非空已保存模板_保留身份与设置()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var service = BuildService(db);

        var catalog = await service.GetCatalogAsync(_fixture.PrivilegedUserId);

        var family = Assert.Single(catalog.Families, f => f.FamilyKey == "sales-order");
        Assert.True(family.Supported);
        Assert.Equal(ReportPrintTemplateCompatibilityText.Compatible, family.CompatibilityStatus);
        Assert.Equal("bill-export:sales-order", family.DatasetKey);

        var template = Assert.Single(family.Templates);
        Assert.Equal(_fixture.TemplateId, template.Id);
        Assert.Equal("SQL 打印模板", template.TemplateName);
        Assert.Equal("A4", template.PaperSize);
        Assert.Equal(12, template.FontSize);
        Assert.Equal("Microsoft YaHei", template.FontFamily);
        Assert.True(template.ShowCompanyHeader);
        Assert.True(template.ShowDetailTable);
        Assert.True(template.ShowRemark);
        Assert.Equal("页脚", template.FooterText);
        Assert.Equal(new[] { "BillNo", "OrderDate" }, template.FieldKeys);
    }

    [Fact]
    public async Task Catalog_缺少专用导出菜单_精确拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedRestrictedUserAsync(db, "ERP312-RESTRICTED");
        var service = BuildService(db);

        var catalog = await service.GetCatalogAsync(userId);

        Assert.DoesNotContain(catalog.Families, f => f.FamilyKey == "sales-order");
    }

    [Fact]
    public async Task Catalog_不支持族_显式阻塞()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var service = BuildService(db);

        var catalog = await service.GetCatalogAsync(_fixture.PrivilegedUserId);

        var customer = Assert.Single(catalog.Families, f => f.FamilyKey == "customer");
        Assert.False(customer.Supported);
        Assert.Equal(ReportPrintTemplateCompatibilityText.Unsupported, customer.CompatibilityStatus);
        Assert.NotEmpty(customer.UnsupportedReason);
        Assert.Empty(customer.FieldAliases);
    }

    [Fact]
    public async Task Bind_字段顺序与别名注入()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var service = BuildService(db);

        var ordered = await service.BindAsync(Bind(_fixture.TemplateId, "OrderDate", "BillNo"), _fixture.PrivilegedUserId);
        Assert.Equal(2, ordered.BoundColumns.Count);
        Assert.Equal("OrderDate", ordered.BoundColumns[0].ColumnKey);
        Assert.Equal("BillNo", ordered.BoundColumns[1].ColumnKey);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.BindAsync(Bind(_fixture.TemplateId, "BillNo; DROP TABLE"), _fixture.PrivilegedUserId));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, string userName)
    {
        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == userName && !u.IsDeleted);
        if (user is not null)
            return user.Id;

        user = new SysUser { UserName = userName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = userName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = userName + "-role", RoleCode = userName + "-role", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "sales-order" && !m.IsDeleted)
            ?? throw new InvalidOperationException("缺少菜单种子 sales-order");
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具
/// （sales-order 打印模板 + 特权账号）。写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationPrintTemplateSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP312";
    private const string UserName = "ERP312-ADMIN";
    private const string RoleCode = "ERP312-SYS";
    private const string TemplateName = "SQL 打印模板";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }
    public long TemplateId { get; private set; }

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

        foreach (var code in new[] { "sales-order", "sales-order-export", "customer" })
        {
            var menu = await EnsureMenuAsync(db, code);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        TemplateId = await EnsureTemplateAsync(db);

        PrivilegedUserId = user.Id;
    }

    private static async Task<long> EnsureTemplateAsync(ErpDbContext db)
    {
        var existing = await db.SysPrintTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => !t.IsDeleted && t.BillType == "sales-order" && t.TemplateName == TemplateName);
        if (existing is not null)
            return existing.Id;

        var template = new SysPrintTemplate
        {
            BillType = "sales-order",
            TemplateName = TemplateName,
            Title = "销售订单（SQL）",
            IsDefault = true,
            PaperSize = "A4",
            FontSize = 12,
            FontFamily = "Microsoft YaHei",
            ShowCompanyHeader = true,
            ShowDetailTable = true,
            ShowRemark = true,
            FooterText = "页脚",
            FieldKeys = "[\"BillNo\",\"OrderDate\"]",
        };
        db.SysPrintTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
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

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP312 隔离账号", Status = UserStatus.Enabled };
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

