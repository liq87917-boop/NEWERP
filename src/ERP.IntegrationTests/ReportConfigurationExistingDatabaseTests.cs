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
/// ERP-281 通用报表配置平台（ERP-260 / ERP-265）既有库引导 SQL Server 集成测试：
/// 在专用本地库上复现「既有 NEWERP 库（已有业务表与种子数据）只缺报表配置三表」，
/// 验证 <see cref="SchemaUpgrader"/> 第 47 段幂等补齐三表，并复用既有应用服务验证
/// 保存 / 读取 / 发布 / 固定修订共享 / 撤销 / 陈旧版本拒绝。
/// <para>安全口径：破坏性夹具前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationExistingDatabaseTests : IClassFixture<ReportConfigurationExistingDatabaseFixture>
{
    private readonly ReportConfigurationExistingDatabaseFixture _fixture;

    public ReportConfigurationExistingDatabaseTests(ReportConfigurationExistingDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // ==================== 1. 既有库引导：只补齐缺失三表，保留既有表与种子数据 ====================

    [Fact]
    public async Task 启动引导_补齐三个报表表且保留既有表与种子数据()
    {
        // 确定性复现「既有库只缺报表表」：再删一次三表，消除用例执行顺序影响。
        await _fixture.DropReportTablesAsync();

        Assert.Equal(0, await TableCountAsync("ReportConfigurations"));
        Assert.Equal(0, await TableCountAsync("ReportConfigurationRevisions"));
        Assert.Equal(0, await TableCountAsync("ReportConfigurationGrants"));

        await using var db = _fixture.CreateDbContext();
        await SchemaUpgrader.EnsureUpgradedAsync(db);

        Assert.Equal(1, await TableCountAsync("ReportConfigurations"));
        Assert.Equal(1, await TableCountAsync("ReportConfigurationRevisions"));
        Assert.Equal(1, await TableCountAsync("ReportConfigurationGrants"));

        // 既有表与种子数据不受影响（admin 用户仍存在）。
        Assert.Equal(1, await TableCountAsync("SysUsers"));
        Assert.Equal(1, await ScalarIntAsync(
            "SELECT COUNT(*) FROM db_owner.SysUsers WHERE UserName = N'admin' AND IsDeleted = 0"));

        await AssertReportTableMetadataAsync();
    }

    // ==================== 2. 重复启动引导：数据保留 ====================

    [Fact]
    public async Task 启动引导_重复执行_保留配置修订与授权数据()
    {
        await using var db = _fixture.CreateDbContext();
        await SchemaUpgrader.EnsureUpgradedAsync(db); // 幂等：缺表则补，有表则跳过

        var ownerId = await SeedAuthorizedOwnerAsync(db, "rc-existing-owner");
        var recipientId = await SeedAuthorizedOwnerAsync(db, "rc-existing-recipient");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("保留性验证-报表"));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var configCount = await ScalarIntAsync("SELECT COUNT(*) FROM db_owner.ReportConfigurations WHERE IsDeleted = 0");
        var revisionCount = await ScalarIntAsync("SELECT COUNT(*) FROM db_owner.ReportConfigurationRevisions WHERE IsDeleted = 0");
        var grantCount = await ScalarIntAsync("SELECT COUNT(*) FROM db_owner.ReportConfigurationGrants WHERE IsDeleted = 0");

        // 再次执行两次启动引导（幂等），定义 / 修订 / 授权必须原样保留。
        await SchemaUpgrader.EnsureUpgradedAsync(db);
        await SchemaUpgrader.EnsureUpgradedAsync(db);

        Assert.Equal(configCount, await ScalarIntAsync("SELECT COUNT(*) FROM db_owner.ReportConfigurations WHERE IsDeleted = 0"));
        Assert.Equal(revisionCount, await ScalarIntAsync("SELECT COUNT(*) FROM db_owner.ReportConfigurationRevisions WHERE IsDeleted = 0"));
        Assert.Equal(grantCount, await ScalarIntAsync("SELECT COUNT(*) FROM db_owner.ReportConfigurationGrants WHERE IsDeleted = 0"));

        var read = await service.GetAsync(ownerId, created.Id);
        Assert.Equal("保留性验证-报表", read.Name);
    }

    // ==================== 3. 复用既有应用服务：保存 / 读取 / 发布 / 共享 / 撤销 / 陈旧拒绝 ====================

    [Fact]
    public async Task 应用服务_保存读取发布共享撤销与陈旧版本拒绝()
    {
        await using var db = _fixture.CreateDbContext();
        await SchemaUpgrader.EnsureUpgradedAsync(db);

        var ownerId = await SeedAuthorizedOwnerAsync(db, "rc-flow-owner");
        var recipientId = await SeedAuthorizedOwnerAsync(db, "rc-flow-recipient");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        // 保存
        var created = await service.CreateAsync(ownerId, SaveDto("集成-销售订单报表"));
        Assert.True(created.Id > 0);
        Assert.Equal(ownerId, created.OwnerUserId);
        Assert.Equal(ReportConfigurationStatus.Draft, created.Status);
        Assert.Equal(1, created.Version);
        Assert.Equal(0, created.CurrentPublishedVersion);

        // 读取
        var read = await service.GetAsync(ownerId, created.Id);
        Assert.Equal("集成-销售订单报表", read.Name);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, read.DatasetKey);
        Assert.Equal(3, read.Definition!.Fields.Count);
        Assert.Contains("totalAmount", read.Definition.Fields);

        // 陈旧版本拒绝
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.UpdateAsync(ownerId, created.Id, created.Version + 1, SaveDto("陈旧写入")));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 发布：追加不可变修订快照
        var published = await service.PublishAsync(ownerId, created.Id, created.Version);
        Assert.Equal(ReportConfigurationStatus.Published, published.Status);
        Assert.Equal(1, published.CurrentPublishedVersion);
        Assert.Equal(2, published.Version);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.Version);

        // 固定修订共享（pin 到修订 1）
        var grant = await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });
        Assert.Equal(recipientId, grant.RecipientUserId);
        Assert.Equal(1, grant.RevisionVersion);

        var shared = await sharing.ListSharedAsync(recipientId);
        Assert.Contains(shared, s => s.ReportConfigurationId == created.Id && s.RevisionVersion == 1);

        // 撤销：被授权人共享列表立即收敛为空
        await sharing.RevokeAsync(ownerId, created.Id, recipientId, grant.Version);
        Assert.Empty(await sharing.ListSharedAsync(recipientId));
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

    private async Task<int> TableCountAsync(string table)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(*)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = N'db_owner' AND t.name = @table";
        cmd.Parameters.AddWithValue("@table", table);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> ScalarIntAsync(string sql)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task AssertReportTableMetadataAsync()
    {
        // 列元数据：与 ErpDbContext.Reporting 精确映射一致（含 rowversion / 列类型与可空性）。
        await AssertTableColumnsAsync("ReportConfigurations", new Dictionary<string, (string Type, int MaxLength, bool Nullable)>
        {
            ["Id"] = ("bigint", 8, false),
            ["OwnerUserId"] = ("bigint", 8, false),
            ["Name"] = ("nvarchar", 400, false),
            ["DatasetKey"] = ("nvarchar", 100, false),
            ["DefinitionJson"] = ("nvarchar", -1, false),
            ["SchemaVersion"] = ("int", 4, false),
            ["Status"] = ("int", 4, false),
            ["Version"] = ("int", 4, false),
            ["CurrentPublishedVersion"] = ("int", 4, false),
            ["CreatedAt"] = ("datetime2", 8, false),
            ["CreatedBy"] = ("bigint", 8, true),
            ["UpdatedAt"] = ("datetime2", 8, true),
            ["UpdatedBy"] = ("bigint", 8, true),
            ["IsDeleted"] = ("bit", 1, false),
            ["RowVersion"] = ("timestamp", 8, false),
        });

        await AssertTableColumnsAsync("ReportConfigurationRevisions", new Dictionary<string, (string Type, int MaxLength, bool Nullable)>
        {
            ["Id"] = ("bigint", 8, false),
            ["ReportConfigurationId"] = ("bigint", 8, false),
            ["OwnerUserId"] = ("bigint", 8, false),
            ["Version"] = ("int", 4, false),
            ["Name"] = ("nvarchar", 400, false),
            ["DatasetKey"] = ("nvarchar", 100, false),
            ["DefinitionJson"] = ("nvarchar", -1, false),
            ["SchemaVersion"] = ("int", 4, false),
            ["PublishedAt"] = ("datetime2", 8, false),
            ["PublishedBy"] = ("bigint", 8, false),
            ["CreatedAt"] = ("datetime2", 8, false),
            ["CreatedBy"] = ("bigint", 8, true),
            ["UpdatedAt"] = ("datetime2", 8, true),
            ["UpdatedBy"] = ("bigint", 8, true),
            ["IsDeleted"] = ("bit", 1, false),
            ["RowVersion"] = ("timestamp", 8, false),
        });

        await AssertTableColumnsAsync("ReportConfigurationGrants", new Dictionary<string, (string Type, int MaxLength, bool Nullable)>
        {
            ["Id"] = ("bigint", 8, false),
            ["RecipientUserId"] = ("bigint", 8, false),
            ["ReportConfigurationId"] = ("bigint", 8, false),
            ["RevisionVersion"] = ("int", 4, false),
            ["GrantedByUserId"] = ("bigint", 8, false),
            ["Version"] = ("int", 4, false),
            ["CreatedAt"] = ("datetime2", 8, false),
            ["CreatedBy"] = ("bigint", 8, true),
            ["UpdatedAt"] = ("datetime2", 8, true),
            ["UpdatedBy"] = ("bigint", 8, true),
            ["IsDeleted"] = ("bit", 1, false),
            ["RowVersion"] = ("timestamp", 8, false),
        });

        // 索引元数据：分页索引 + 唯一修订元组 + 过滤唯一授权索引。
        await AssertIndexesAsync("ReportConfigurations", new Dictionary<string, (bool Unique, bool Filtered)>
        {
            ["IX_ReportConfigurations_OwnerUserId_IsDeleted"] = (false, false),
        });

        await AssertIndexesAsync("ReportConfigurationRevisions", new Dictionary<string, (bool Unique, bool Filtered)>
        {
            ["UX_ReportConfigurationRevisions_ConfigurationId_Version"] = (true, false),
            ["IX_ReportConfigurationRevisions_ConfigurationId"] = (false, false),
        });

        await AssertIndexesAsync("ReportConfigurationGrants", new Dictionary<string, (bool Unique, bool Filtered)>
        {
            ["UX_ReportConfigurationGrants_Recipient_Configuration"] = (true, true),
            ["IX_ReportConfigurationGrants_ConfigurationId_IsDeleted"] = (false, false),
            ["IX_ReportConfigurationGrants_RecipientUserId_IsDeleted"] = (false, false),
        });

        // 外键：修订 / 授权 → 配置（与既有 SchemaUpgrader 外键口径一致，软删除为主、数据库级 NO ACTION 兜底）。
        await AssertForeignKeyAsync("ReportConfigurationRevisions",
            "FK_ReportConfigurationRevisions_ReportConfigurations_ReportConfigurationId", "NO_ACTION");
        await AssertForeignKeyAsync("ReportConfigurationGrants",
            "FK_ReportConfigurationGrants_ReportConfigurations_ReportConfigurationId", "NO_ACTION");
    }

    private async Task AssertTableColumnsAsync(
        string table, IReadOnlyDictionary<string, (string Type, int MaxLength, bool Nullable)> expected)
    {
        var actual = await GetColumnsAsync(table);
        foreach (var (column, meta) in expected)
        {
            Assert.True(actual.TryGetValue(column, out var found), $"缺少列 {table}.{column}");
            Assert.Equal(meta.Type, found!.Type);
            Assert.Equal(meta.MaxLength, found.MaxLength);
            Assert.Equal(meta.Nullable, found.Nullable);
        }
    }

    private async Task AssertIndexesAsync(
        string table, IReadOnlyDictionary<string, (bool Unique, bool Filtered)> expected)
    {
        var indexes = await GetIndexesAsync(table);
        foreach (var (name, meta) in expected)
        {
            var index = Assert.Single(indexes, i => i.Name == name);
            Assert.Equal(meta.Unique, index.Unique);
            Assert.Equal(meta.Filtered, index.Filtered);
        }
    }

    private async Task AssertForeignKeyAsync(string table, string fkName, string deleteAction)
    {
        var fks = await GetForeignKeysAsync(table);
        var fk = Assert.Single(fks, f => f.Name == fkName);
        Assert.Equal(deleteAction, fk.DeleteAction);
    }

    private async Task<Dictionary<string, ColumnInfo>> GetColumnsAsync(string table)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT c.name, t.name, c.max_length, c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@fullName)";
        cmd.Parameters.AddWithValue("@fullName", $"db_owner.{table}");

        var result = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = new ColumnInfo(
                reader.GetString(1), reader.GetInt16(2), reader.GetBoolean(3));
        }

        return result;
    }

    private async Task<List<IndexInfo>> GetIndexesAsync(string table)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT i.name, i.is_unique, i.has_filter
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(@fullName) AND i.name IS NOT NULL";
        cmd.Parameters.AddWithValue("@fullName", $"db_owner.{table}");

        var result = new List<IndexInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new IndexInfo(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return result;
    }

    private async Task<List<ForeignKeyInfo>> GetForeignKeysAsync(string table)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT fk.name, fk.delete_referential_action_desc
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID(@fullName)";
        cmd.Parameters.AddWithValue("@fullName", $"db_owner.{table}");

        var result = new List<ForeignKeyInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ForeignKeyInfo(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private sealed record ColumnInfo(string Type, int MaxLength, bool Nullable);
    private sealed record IndexInfo(string Name, bool Unique, bool Filtered);
    private sealed record ForeignKeyInfo(string Name, string DeleteAction);

}

/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为「既有 NEWERP 库 + 种子数据，但报表配置三表缺失」状态。
/// </summary>
public sealed class ReportConfigurationExistingDatabaseFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_EXISTING_20261004";

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

        Console.WriteLine($"[ERP-281] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToExistingDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>按依赖顺序删除报表配置三表（用于确定性复现「既有库只缺三表」）。</summary>
    public async Task DropReportTablesAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('db_owner.ReportConfigurationGrants', 'U') IS NOT NULL
    DROP TABLE db_owner.ReportConfigurationGrants;
IF OBJECT_ID('db_owner.ReportConfigurationRevisions', 'U') IS NOT NULL
    DROP TABLE db_owner.ReportConfigurationRevisions;
IF OBJECT_ID('db_owner.ReportConfigurations', 'U') IS NOT NULL
    DROP TABLE db_owner.ReportConfigurations;";
        await cmd.ExecuteNonQueryAsync();
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

        Assert.Contains(InstanceMarker, server, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ResetToExistingDatabaseAsync()
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

        // 全新库走完整 NEWERP 启动序列（建全量表 + 结构升级 + 种子 + 再升级）。
        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        // 模拟「既有库只缺报表配置三表」：删除三表，保留其余全部表与种子数据。
        await DropReportTablesAsync();

        Console.WriteLine("[ERP-281] 既有库场景就绪：完整 NEWERP 结构 + 种子数据，报表配置三表已移除。");
    }

    private static string QuoteSqlString(string value) => value.Replace("'", "''");

    private static string QuoteSqlIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";
}
