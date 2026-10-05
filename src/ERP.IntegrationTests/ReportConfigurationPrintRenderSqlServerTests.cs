using System.Text;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-313 受控打印渲染的 SQL Server 集成测试：在专用 localdb 目标上自包含播种打印模板与私有报表配置，
/// 通过真实 EF 上下文与真实绑定 / 执行服务验证标准布局非空绑定、PDF 签名与被拒绝字段整单失败。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationPrintRenderSqlServerTests
    : IClassFixture<ReportConfigurationPrintRenderSqlServerFixture>
{
    private readonly ReportConfigurationPrintRenderSqlServerFixture _fixture;

    public ReportConfigurationPrintRenderSqlServerTests(ReportConfigurationPrintRenderSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationPrintRenderSqlServerFixture.InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationPrintRenderSqlServerFixture.DatabasePrefix, builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
    }

    private sealed class FakeReader : ILegacyBillExportReadService
    {
        public LegacyBillExportPage Page { get; set; } = new();

        public Task<LegacyBillExportPage> ReadPageAsync(LegacyBillExportQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(Page);
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db, FakeReader reader)
        => new IReportConfigurationDatasetProvider[]
        {
            new LegacyBillExportReportConfigurationDatasetProvider(
                db, reader, LegacyBillExportCatalog.Resolve("sales-order").DatasetKey),
        };

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db, FakeReader reader)
        => new ReportConfigurationCatalog(BuildProviders(db, reader));

    private static ReportConfigurationPrintTemplateCatalog BuildTemplateCatalog(ErpDbContext db, FakeReader reader)
        => new(db, BuildCatalog(db, reader));

    private static ReportConfigurationExecutionService BuildExecution(ErpDbContext db, FakeReader reader)
        => new(db, BuildProviders(db, reader));

    private static ReportConfigurationPrintRenderService BuildRender(ErpDbContext db, FakeReader reader)
        => new(db, BuildTemplateCatalog(db, reader), BuildExecution(db, reader), new ReportConfigurationExecutionBudget());

    private async Task<long> SeedConfigAsync(ErpDbContext db, FakeReader reader, params string[] fields)
    {
        var service = new ReportConfigurationService(db, BuildCatalog(db, reader));
        var created = await service.CreateAsync(
            _fixture.PrivilegedUserId,
            new ReportConfigurationSaveDto
            {
                Name = "SQL 打印渲染报表",
                Definition = new ReportConfigurationDefinition
                {
                    SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
                    DatasetKey = LegacyBillExportCatalog.Resolve("sales-order").DatasetKey,
                    Fields = fields.ToList(),
                },
            });
        return created.Id;
    }

    [Fact]
    public async Task Render_标准布局_非空绑定与PDF签名()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var reader = new FakeReader
        {
            Page = new LegacyBillExportPage
            {
                Columns = LegacyBillExportCatalog.Resolve("sales-order").Columns.ToList(),
                Rows = new List<Dictionary<string, object?>>
                {
                    new(StringComparer.Ordinal)
                    {
                        ["BillNo"] = "SO-ERP313-1",
                        ["OrderDate"] = new DateTime(2026, 9, 1),
                    },
                },
                Total = 1,
                Page = 1,
                PageSize = 20,
                TotalPages = 1,
            },
        };

        var configurationId = await SeedConfigAsync(db, reader, "BillNo", "OrderDate");
        var render = BuildRender(db, reader);

        var result = await render.PreviewAsync(
            _fixture.PrivilegedUserId,
            new ReportConfigurationPrintRenderRequest
            {
                ConfigurationId = configurationId,
                TemplateId = _fixture.TemplateId,
            });

        Assert.Equal(ReportPrintRenderLayoutText.Standard, result.Layout);
        Assert.Equal(new[] { "BillNo", "OrderDate" }, result.Columns.Select(c => c.ColumnKey).ToArray());
        var row = Assert.Single(result.Rows);
        Assert.Equal("SO-ERP313-1", row["BillNo"]);

        var font = SimHeiPdfFontResolver.FindFontPath();
        if (font is not null)
        {
            var bytes = ReportConfigurationPrintPdfExporter.Export(result, font);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        }
    }

    [Fact]
    public async Task Render_模板字段不在报表选定字段内_整单失败()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var reader = new FakeReader
        {
            Page = new LegacyBillExportPage
            {
                Columns = LegacyBillExportCatalog.Resolve("sales-order").Columns.ToList(),
                Rows = new List<Dictionary<string, object?>> { new(StringComparer.Ordinal) { ["BillNo"] = "SO-ERP313-2" } },
                Total = 1,
                Page = 1,
                PageSize = 20,
                TotalPages = 1,
            },
        };

        // 报表只选定 BillNo；模板 FieldKeys = [BillNo, OrderDate] → OrderDate 不在授权选定字段内。
        var configurationId = await SeedConfigAsync(db, reader, "BillNo");
        var render = BuildRender(db, reader);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => render.PreviewAsync(
            _fixture.PrivilegedUserId,
            new ReportConfigurationPrintRenderRequest
            {
                ConfigurationId = configurationId,
                TemplateId = _fixture.TemplateId,
            }));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("OrderDate", ex.Message);
    }

}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具
/// （sales-order 打印模板 + 特权账号）。写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationPrintRenderSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP313";
    private const string UserName = "ERP313-ADMIN";
    private const string RoleCode = "ERP313-SYS";
    private const string TemplateName = "SQL 打印渲染模板";

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
            Title = "销售订单（SQL 打印渲染）",
            IsDefault = true,
            PaperSize = "A4",
            FontSize = 12,
            TitleFontSize = 16,
            CompanyFontSize = 18,
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

        user = new SysUser
        {
            UserName = UserName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP313 隔离账号",
            Status = UserStatus.Enabled,
        };
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
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog, StringComparison.OrdinalIgnoreCase);
    }
}

