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
/// ERP-284 Stage 1：通用报表配置平台并发生命周期真实 SQL Server 集成测试。
/// 用两个独立 <see cref="ErpDbContext"/> 复现「赢家先提交、败者以陈旧行版本提交」，
/// 验证赢家状态 / 版本生效、败者被映射为 <see cref="BusinessException.RuleConflict"/>、
/// 失败发布 / 恢复不产生幽灵修订、失败授权 pin / 撤销不改变赢家行。
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀
/// <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取
/// appsettings / .env / 生产凭据，也不执行任何生产库或生产 schema。</para>
/// </summary>
public sealed class ReportConfigurationConcurrencySqlServerTests : IClassFixture<ReportConfigurationConcurrencyFixture>
{
    private readonly ReportConfigurationConcurrencyFixture _fixture;

    public ReportConfigurationConcurrencySqlServerTests(ReportConfigurationConcurrencyFixture fixture)
    {
        _fixture = fixture;
    }

    // ==================== 1. 私有报表生命周期两上下文并发 ====================

    [Fact]
    public async Task Update_两上下文并发_赢家生效_败者规则冲突且不覆盖赢家()
    {
        await using var winner = _fixture.CreateDbContext();
        await using var loser = _fixture.CreateDbContext();

        var ownerId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-update");
        var winnerService = BuildService(winner);

        var created = await winnerService.CreateAsync(ownerId, SaveDto("原始名称"));

        // 败者上下文先读到旧行版本（跟踪陈旧快照）
        await loser.ReportConfigurations.FirstAsync(c => c.Id == created.Id);

        // 赢家提交合法更新
        var won = await winnerService.UpdateAsync(ownerId, created.Id, created.Version, SaveDto("赢家名称"));
        Assert.Equal(2, won.Version);

        // 败者以陈旧行版本提交 → EF 并发冲突 → 业务规则冲突
        var loserService = BuildService(loser);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => loserService.UpdateAsync(ownerId, created.Id, created.Version, SaveDto("败家名称")));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请刷新后重试", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.ReportConfigurations.AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.Equal("赢家名称", persisted.Name);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task Publish_两上下文并发_败者规则冲突且无幽灵修订()
    {
        await using var winner = _fixture.CreateDbContext();
        await using var loser = _fixture.CreateDbContext();

        var ownerId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-publish");
        var winnerService = BuildService(winner);

        var created = await winnerService.CreateAsync(ownerId, SaveDto("发布并发"));

        await loser.ReportConfigurations.FirstAsync(c => c.Id == created.Id);

        var won = await winnerService.PublishAsync(ownerId, created.Id, created.Version);
        Assert.Equal(1, won.CurrentPublishedVersion);

        var loserService = BuildService(loser);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => loserService.PublishAsync(ownerId, created.Id, created.Version));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请刷新后重试", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(1, await verify.ReportConfigurationRevisions.CountAsync(r => r.ReportConfigurationId == created.Id));
        var persisted = await verify.ReportConfigurations.AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.Equal(1, persisted.CurrentPublishedVersion);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task Delete_两上下文并发_败者规则冲突且不软删除赢家行()
    {
        await using var winner = _fixture.CreateDbContext();
        await using var loser = _fixture.CreateDbContext();

        var ownerId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-delete");
        var winnerService = BuildService(winner);

        var created = await winnerService.CreateAsync(ownerId, SaveDto("删除并发"));

        await loser.ReportConfigurations.FirstAsync(c => c.Id == created.Id);

        // 赢家先改名（行版本推进），败者再以陈旧行版本尝试软删除
        await winnerService.RenameAsync(ownerId, created.Id, created.Version, "赢家保留");

        var loserService = BuildService(loser);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => loserService.DeleteAsync(ownerId, created.Id, created.Version));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请刷新后重试", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.ReportConfigurations.AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.False(persisted.IsDeleted);
        Assert.Equal("赢家保留", persisted.Name);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task Restore_两上下文并发_败者规则冲突且无幽灵修订()
    {
        await using var winner = _fixture.CreateDbContext();
        await using var loser = _fixture.CreateDbContext();

        var ownerId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-restore");
        var winnerService = BuildService(winner);

        var created = await winnerService.CreateAsync(ownerId, SaveDto("恢复并发"));
        var published = await winnerService.PublishAsync(ownerId, created.Id, created.Version);

        await loser.ReportConfigurations.FirstAsync(c => c.Id == created.Id);

        // 赢家推进（改名），败者以陈旧行版本尝试恢复旧修订
        await winnerService.RenameAsync(ownerId, created.Id, published.Version, "赢家保留");

        var loserService = BuildService(loser);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => loserService.RestoreAsync(ownerId, created.Id, published.Version, 1));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请刷新后重试", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(1, await verify.ReportConfigurationRevisions.CountAsync(r => r.ReportConfigurationId == created.Id));
        var persisted = await verify.ReportConfigurations.AsNoTracking().SingleAsync(c => c.Id == created.Id);
        Assert.Equal(1, persisted.CurrentPublishedVersion);
        Assert.Equal(3, persisted.Version);
    }

    // ==================== 2. 既有授权 pin / 撤销两上下文并发 ====================

    [Fact]
    public async Task Grant_固定修订pin_两上下文并发_败者规则冲突且pin保持赢家值()
    {
        await using var winner = _fixture.CreateDbContext();
        await using var loser = _fixture.CreateDbContext();

        var ownerId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-grant");
        var recipientId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-grant-r");
        var winnerService = BuildService(winner);
        var winnerSharing = BuildSharing(winner);

        var created = await winnerService.CreateAsync(ownerId, SaveDto("共享pin并发"));
        var published = await winnerService.PublishAsync(ownerId, created.Id, created.Version);
        await winnerService.PublishAsync(ownerId, created.Id, published.Version);
        var grant = await winnerSharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        // 败者上下文先读到旧授权行版本
        await loser.ReportConfigurationGrants.FirstAsync(g => g.Id == grant.Id);

        // 赢家把 pin 改到修订2
        await winnerSharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 2, ExpectedVersion = grant.Version });

        var loserSharing = BuildSharing(loser);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => loserSharing.GrantAsync(ownerId, created.Id,
                new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1, ExpectedVersion = grant.Version }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请刷新后重试", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.ReportConfigurationGrants.AsNoTracking().SingleAsync(g => g.Id == grant.Id);
        Assert.Equal(2, persisted.RevisionVersion);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task Revoke_两上下文并发_败者规则冲突且授权仍有效()
    {
        await using var winner = _fixture.CreateDbContext();
        await using var loser = _fixture.CreateDbContext();

        var ownerId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-revoke");
        var recipientId = await SeedAuthorizedOwnerAsync(winner, "rc-conc-revoke-r");
        var winnerService = BuildService(winner);
        var winnerSharing = BuildSharing(winner);

        var created = await winnerService.CreateAsync(ownerId, SaveDto("撤销并发"));
        await winnerService.PublishAsync(ownerId, created.Id, created.Version);
        var grant = await winnerSharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        await loser.ReportConfigurationGrants.FirstAsync(g => g.Id == grant.Id);

        // 赢家改 pin（推进授权行版本）
        await winnerSharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1, ExpectedVersion = grant.Version });

        var loserSharing = BuildSharing(loser);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => loserSharing.RevokeAsync(ownerId, created.Id, recipientId, grant.Version));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请刷新后重试", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.ReportConfigurationGrants.AsNoTracking().SingleAsync(g => g.Id == grant.Id);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(2, persisted.Version);
    }

    // ==================== 脚手架 ====================

    private IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildCatalog(db));

    private IReportConfigurationSharingService BuildSharing(ErpDbContext db)
        => new ReportConfigurationSharingService(db, BuildCatalog(db));

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        });

    private static async Task<long> SeedAuthorizedOwnerAsync(ErpDbContext db, string tag)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var user = new SysUser
        {
            UserName = $"{tag}-{suffix}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = $"{tag}-{suffix}",
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"{tag}-role-{suffix}",
            RoleCode = $"{tag}-role-{suffix}",
            IsSystem = false,
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "sales-order" && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = "销售订单", MenuCode = "sales-order", MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition? definition = null)
        => new() { Name = name, Definition = definition ?? SalesOrderDefinition() };

    private static ReportConfigurationDefinition SalesOrderDefinition() => new()
    {
        SchemaVersion = 1,
        DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
        Fields = new List<string> { "orderNo", "currency", "totalAmount" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };
}

/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据（含报表配置三表），
/// 供两个独立 <see cref="ErpDbContext"/> 复现真实 SQL Server 乐观并发。
/// </summary>
public sealed class ReportConfigurationConcurrencyFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_CONCURRENCY_20261004";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = BuildDefaultConnectionString();
        }

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-284] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Contains(InstanceMarker, server, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ResetToFullDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
IF DB_ID(N'{QuoteSqlString(database)}') IS NOT NULL
BEGIN
    ALTER DATABASE {QuoteSqlIdentifier(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {QuoteSqlIdentifier(database)};
END";
            await cmd.ExecuteNonQueryAsync();
        }

        // 全新库走完整 NEWERP 启动序列（建全量表 + 结构升级 + 种子 + 再升级），报表配置三表一并存在。
        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-284] 并发场景就绪：完整 NEWERP 结构 + 种子数据（含报表配置三表）。");
    }

    private static string QuoteSqlString(string value) => value.Replace("'", "''");

    private static string QuoteSqlIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";
}




