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
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-335 Stage 2 表头 / 明细组合一致读取作用域 SQL Server 集成测试：
/// 在专用 localdb 目标上自包含播种非空夹具，验证 Snapshot 一致只读事务在两次读取之间源数据变化时仍输出一致
/// （绝不混合表头 / 明细），以及权限撤销的 fail closed 与幂等夹具清理。
/// <para>安全口径：目标护栏强制专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationCompositionConsistencySqlServerTests
    : IClassFixture<ReportConfigurationCompositionConsistencySqlServerFixture>
{
    private const string SyntheticDocNo = "ERP335-TD-CONS";

    private readonly ReportConfigurationCompositionConsistencySqlServerFixture _fixture;

    public ReportConfigurationCompositionConsistencySqlServerTests(
        ReportConfigurationCompositionConsistencySqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationCompositionConsistencySqlServerFixture.InstanceMarker}",
            builder.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationCompositionConsistencySqlServerFixture.DatabasePrefix,
            builder.InitialCatalog, StringComparison.OrdinalIgnoreCase);
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
            Name = "ERP335-组合表头-" + suffix,
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "docNo", "docType", "amount", "currency"),
        });
        var detail = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "ERP335-组合明细-" + suffix,
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
    public async Task 组合_SQLServer_快照隔离_两次读取之间源变更_输出一致而非混合()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var configs = new ReportConfigurationService(db, catalog);
        var factory = new ReportConfigurationCompositionReadScopeFactory(db, providers);

        var (headerId, detailId) = await CreatePairAsync(configs, userId, "snapshot");
        Guard();

        using var lease = new ReportConfigurationExecutionBudget().Acquire(userId);
        await using var scope = await factory.OpenAsync(userId, "corr-erp335-snapshot");

        // 第一次读取：表头（在同一 Snapshot 一致只读事务内）。
        var headerPreview = await execution.PreviewAsync(userId, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = headerId,
            Page = 1,
            PageSize = ReportConfigurationBundleCompositionLimits.MaxParents,
        }, lease);

        // 两次读取之间，用独立连接把同一来源记录改掉（表头金额 + 明细行金额）。
        await using (var writer = _fixture.CreateDbContext())
        {
            var doc = await writer.TradeDocuments.SingleAsync(d => d.DocNo == SyntheticDocNo && !d.IsDeleted);
            doc.Amount = 999m;
            var item = await writer.TradeDocumentItems.SingleAsync(
                i => i.TradeDocumentId == doc.Id && i.LineNo == 1 && !i.IsDeleted);
            item.LineAmount = 999m;
            await writer.SaveChangesAsync();
        }

        // 第二次读取：明细（仍应看到同一 Snapshot 内的原始值，绝不出现混合表头 / 明细）。
        var detailPreview = await execution.PreviewAsync(userId, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = detailId,
            Page = 1,
            PageSize = ReportConfigurationBundleCompositionLimits.MaxTotalChildren,
        }, lease);

        Assert.Equal(100m, (decimal)headerPreview.Rows.Single()["amount"]!);
        Assert.Equal(10m, (decimal)detailPreview.Rows.Single()["lineAmount"]!);

        await scope.CompleteAsync();
    }

    [Fact]
    public async Task 组合_SQLServer_权限撤销_整体失败()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var configs = new ReportConfigurationService(db, catalog);
        var factory = new ReportConfigurationCompositionReadScopeFactory(db, providers);
        var bundle = new ReportConfigurationBundleService(execution, compositionReadScopeFactory: factory);

        var (headerId, detailId) = await CreatePairAsync(configs, userId, "revoke");
        Guard();

        var roleId = await db.SysUserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).FirstAsync();
        var menuId = await db.SysMenus.Where(m => m.MenuCode == "doc-center" && !m.IsDeleted)
            .Select(m => m.Id).FirstAsync();

        await using (var revokeDb = _fixture.CreateDbContext())
        {
            var link = await revokeDb.SysRoleMenus.FirstOrDefaultAsync(
                rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted);
            Assert.NotNull(link);
            revokeDb.SysRoleMenus.Remove(link!);
            await revokeDb.SaveChangesAsync();
        }

        try
        {
            Guard();
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => bundle.ComposePreviewAsync(userId, Request(headerId, detailId)));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
        finally
        {
            await using var restoreDb = _fixture.CreateDbContext();
            if (!await restoreDb.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted))
            {
                restoreDb.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
                await restoreDb.SaveChangesAsync();
            }
        }
    }
}



public sealed class ReportConfigurationCompositionConsistencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP335";
    public static readonly DateTime UniformDate = new(2026, 9, 1);

    private const string UserName = "ERP335-ADMIN";
    private const string RoleCode = "ERP335-SYS";
    private const string SyntheticDocNo = "ERP335-TD-CONS";

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
            await EnableSnapshotIsolationAsync(db);
        }

        await SeedFixtureAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var db = CreateDbContext();
            await CleanupAsync(db, PrivilegedUserId);
        }
        catch
        {
            // 夹具清理尽力而为；专用测试库不会影响其它夹具。
        }
    }

    public ErpDbContext CreateDbContext() => new(BuildOptions());

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

    private static async Task EnableSnapshotIsolationAsync(ErpDbContext db)
    {
        // 目标库名来自已通过 AssertDedicatedTarget 校验的专用连接串，仅在受控夹具内使用。
        var databaseName = new SqlConnectionStringBuilder(db.Database.GetConnectionString()!).InitialCatalog;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER DATABASE [" + databaseName + "] SET ALLOW_SNAPSHOT_ISOLATION ON;");
    }


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

        var menu = await EnsureMenuAsync(db, "doc-center");
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        var doc = await EnsureTradeDocumentAsync(db, SyntheticDocNo, "商业发票", 100m, "USD");
        await EnsureTradeItemAsync(db, doc.Id, 1, "ERP335-P1", 2m, "箱", 5m, 10m, "USD");

        PrivilegedUserId = user.Id;
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
            DisplayName = "ERP335 隔离账号",
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

    private static async Task<TradeDocument> EnsureTradeDocumentAsync(
        ErpDbContext db, string docNo, string docType, decimal amount, string currency)
    {
        var doc = await db.TradeDocuments.FirstOrDefaultAsync(d => d.DocNo == docNo && !d.IsDeleted);
        if (doc is not null)
            return doc;

        doc = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            CustomerName = "ERP335 客户甲",
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

    private static async Task EnsureTradeItemAsync(
        ErpDbContext db, long docId, int lineNo, string productCode,
        decimal quantity, string unit, decimal unitPrice, decimal lineAmount, string currency)
    {
        if (await db.TradeDocumentItems.AnyAsync(
            i => i.TradeDocumentId == docId && i.LineNo == lineNo && !i.IsDeleted))
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

    private static async Task CleanupAsync(ErpDbContext db, long userId)
    {
        var docIds = await db.TradeDocuments.Where(d => d.DocNo.StartsWith("ERP335-"))
            .Select(d => d.Id).ToListAsync();
        if (docIds.Count > 0)
        {
            var items = await db.TradeDocumentItems.Where(i => docIds.Contains(i.TradeDocumentId)).ToListAsync();
            foreach (var item in items)
                item.IsDeleted = true;

            var docs = await db.TradeDocuments.Where(d => docIds.Contains(d.Id)).ToListAsync();
            foreach (var doc in docs)
                doc.IsDeleted = true;
        }

        var configs = await db.ReportConfigurations.Where(c => c.OwnerUserId == userId && !c.IsDeleted).ToListAsync();
        foreach (var config in configs)
            config.IsDeleted = true;

        await db.SaveChangesAsync();
    }
}

