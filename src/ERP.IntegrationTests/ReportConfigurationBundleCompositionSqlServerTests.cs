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
using System.Text;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-316 Stage 2 表头 / 明细组合 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （两套不同币种 / 单位的单证 + 多行明细 + 一张空明细单证），通过服务端声明场景组合验证表头一次呈现、
/// 明细按父项有序关联、表头金额独立与明细按币种 / 单位分区，并断言 Excel / PDF 实际制品。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationBundleCompositionSqlServerTests
    : IClassFixture<ReportConfigurationBundleCompositionSqlServerFixture>
{
    private readonly ReportConfigurationBundleCompositionSqlServerFixture _fixture;

    public ReportConfigurationBundleCompositionSqlServerTests(ReportConfigurationBundleCompositionSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationBundleCompositionSqlServerFixture.InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationBundleCompositionSqlServerFixture.DatabasePrefix, builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
    }

    private static IReportConfigurationDatasetProvider[] BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new TradeDocumentReportConfigurationDatasetProvider(db),
        };

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        };

    private static async Task<(long HeaderId, long DetailId)> CreatePairAsync(
        IReportConfigurationService configs, long userId, string suffix)
    {
        var header = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "ERP316-组合表头-" + suffix,
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "docNo", "docType", "amount", "currency"),
        });
        var detail = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "ERP316-组合明细-" + suffix,
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "lineNo", "productCode", "quantity", "unit", "lineAmount", "lineCurrency"),
        });
        return (header.Id, detail.Id);
    }

    private static ReportConfigurationBundleCompositionRequest Request(long headerId, long detailId)
        => new()
        {
            CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
            HeaderConfigurationId = headerId,
            DetailConfigurationId = detailId,
        };

    [Fact]
    public async Task 组合_SQLServer两个父项不同币种单位_关联与合计及导出制品()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var configs = new ReportConfigurationService(db, catalog);
        var bundle = new ReportConfigurationBundleService(execution);

        var (headerId, detailId) = await CreatePairAsync(configs, userId, "main");
        Guard();

        var preview = await bundle.ComposePreviewAsync(userId, Request(headerId, detailId));

        Assert.Equal(3, preview.ParentCount);
        Assert.Equal(3, preview.DetailCount);

        var usd = Assert.Single(preview.Parents, p => (string)p.Header["docNo"]! == "ERP316-TD-USD");
        Assert.Equal(2, usd.Details.Count);
        Assert.Equal(200m, usd.Totals.HeaderAmount);
        Assert.Equal("USD", usd.Totals.HeaderCurrency);
        var usdAmount = Assert.Single(usd.Totals.DetailAmounts);
        Assert.Equal("USD", usdAmount.Currency);
        Assert.Equal(35m, usdAmount.Amount);
        var usdQuantity = Assert.Single(usd.Totals.DetailQuantities);
        Assert.Equal("箱", usdQuantity.Unit);
        Assert.Equal(5m, usdQuantity.Quantity);

        var eur = Assert.Single(preview.Parents, p => (string)p.Header["docNo"]! == "ERP316-TD-EUR");
        Assert.Single(eur.Details);
        Assert.Equal(50m, eur.Totals.HeaderAmount);
        Assert.Equal("EUR", eur.Totals.HeaderCurrency);
        var eurAmount = Assert.Single(eur.Totals.DetailAmounts);
        Assert.Equal("EUR", eurAmount.Currency);
        Assert.Equal(40m, eurAmount.Amount);
        var eurQuantity = Assert.Single(eur.Totals.DetailQuantities);
        Assert.Equal("个", eurQuantity.Unit);
        Assert.Equal(4m, eurQuantity.Quantity);

        var empty = Assert.Single(preview.Parents, p => (string)p.Header["docNo"]! == "ERP316-TD-EMPTY");
        Assert.False(empty.HasDetails);
        Assert.NotNull(empty.EmptyDetailsEvidence);

        using var lease = new ReportConfigurationExecutionBudget().Acquire(userId);
        var export = await bundle.BuildCompositionExportResultAsync(userId, Request(headerId, detailId), lease);

        var excel = new ReportConfigurationBundleExcelExporter().BuildComposed(export.Preview);
        Assert.True(excel.Length > 0);
        Assert.Equal((byte)'P', excel[0]);
        Assert.Equal((byte)'K', excel[1]);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationBundlePdfExporter.ExportComposed(export.Preview, fontPath);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf));
        }
    }


    [Fact]
    public async Task 组合_SQLServer_菜单撤销后整体失败()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var configs = new ReportConfigurationService(db, catalog);
        var bundle = new ReportConfigurationBundleService(execution);

        var (headerId, detailId) = await CreatePairAsync(configs, userId, "revoke");

        var roleId = await db.SysUserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).FirstAsync();
        var docCenterMenuId = await db.SysMenus.Where(m => m.MenuCode == "doc-center" && !m.IsDeleted).Select(m => m.Id).FirstAsync();

        // 用独立上下文撤销授权，测试后无论成败都恢复，避免污染共享夹具。
        await using (var revokeDb = _fixture.CreateDbContext())
        {
            var link = await revokeDb.SysRoleMenus.FirstOrDefaultAsync(rm => rm.RoleId == roleId && rm.MenuId == docCenterMenuId);
            Assert.NotNull(link);
            revokeDb.SysRoleMenus.Remove(link);
            await revokeDb.SaveChangesAsync();
        }

        try
        {
            Guard();
            var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.ComposePreviewAsync(userId, Request(headerId, detailId)));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
        finally
        {
            await using var restoreDb = _fixture.CreateDbContext();
            if (!await restoreDb.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == docCenterMenuId))
            {
                restoreDb.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = docCenterMenuId });
                await restoreDb.SaveChangesAsync();
            }
        }
    }
}


public sealed class ReportConfigurationBundleCompositionSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP316";
    public static readonly DateTime UniformDate = new(2026, 9, 1);

    private const string UserName = "ERP316-ADMIN";
    private const string RoleCode = "ERP316-SYS";

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

        foreach (var code in new[] { "doc-center" })
        {
            var menu = await EnsureMenuAsync(db, code);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var usd = await EnsureTradeDocumentAsync(db, "ERP316-TD-USD", "商业发票", 200m, "USD");
        await EnsureTradeItemAsync(db, usd.Id, 1, "ERP316-P1", 2m, "箱", 10m, 20m, "USD");
        await EnsureTradeItemAsync(db, usd.Id, 2, "ERP316-P2", 3m, "箱", 5m, 15m, "USD");

        var eur = await EnsureTradeDocumentAsync(db, "ERP316-TD-EUR", "商业发票", 50m, "EUR");
        await EnsureTradeItemAsync(db, eur.Id, 1, "ERP316-P3", 4m, "个", 10m, 40m, "EUR");

        await EnsureTradeDocumentAsync(db, "ERP316-TD-EMPTY", "装箱单", 0m, "USD");

        PrivilegedUserId = user.Id;
    }


    private static async Task<TradeDocument> EnsureTradeDocumentAsync(ErpDbContext db, string docNo, string docType, decimal amount, string currency)
    {
        var doc = await db.TradeDocuments.FirstOrDefaultAsync(d => d.DocNo == docNo && !d.IsDeleted);
        if (doc is not null)
            return doc;

        doc = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            CustomerName = "ERP316 客户甲",
            Amount = amount,
            Currency = currency,
            IssueDate = UniformDate,
            Status = "待制作",
            Copies = 1,
        };
        db.TradeDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private static async Task EnsureTradeItemAsync(ErpDbContext db, long docId, int lineNo, string productCode,
        decimal quantity, string unit, decimal unitPrice, decimal lineAmount, string currency)
    {
        if (await db.TradeDocumentItems.AnyAsync(i => i.TradeDocumentId == docId && i.LineNo == lineNo && !i.IsDeleted))
            return;

        db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            TradeDocumentId = docId,
            LineNo = lineNo,
            ProductCode = productCode,
            ProductNameCn = productCode,
            Quantity = quantity,
            Unit = unit,
            UnitPrice = unitPrice,
            LineAmount = lineAmount,
            Currency = currency,
        });
        await db.SaveChangesAsync();
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

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP316 隔离账号", Status = UserStatus.Enabled };
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

