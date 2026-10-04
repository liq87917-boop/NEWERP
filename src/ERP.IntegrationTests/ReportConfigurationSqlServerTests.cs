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
/// ERP-280 通用报表配置平台（ERP-260）SQL Server 集成测试：在专用本地库上验证
/// 「全新库完成 NEWERP 启动序列 → 报表定义保存 / 读取 / 草稿更新 / 发布修订持久化 / 跨所有者拒绝」。
/// <para>安全口径：写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀
/// <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationSqlServerTests : IClassFixture<ReportConfigurationSqlServerFixture>
{
    private readonly ReportConfigurationSqlServerFixture _fixture;

    public ReportConfigurationSqlServerTests(ReportConfigurationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ==================== 1. 启动序列 + 第 28 段对齐 ====================

    /// <summary>启动升级器在隔离库上重复执行幂等，且订柜跟踪列落在复数表、不存在单数表。</summary>
    [Fact]
    public async Task 启动序列_幂等完成且订柜跟踪列对齐EF复数表()
    {
        await using var db = _fixture.CreateDbContext();

        // 再执行两次（幂等验证）：即使前次探针已在第 28 段失败，修复后重跑应完整走通
        await SchemaUpgrader.EnsureUpgradedAsync(db);
        await SchemaUpgrader.EnsureUpgradedAsync(db);

        foreach (var column in ReportConfigurationSqlServerFixture.TrackingColumns)
        {
            await AssertColumnPresentAsync(column);
        }

        await AssertNoSingularContainerBookingTableAsync();
    }

    // ==================== 2. 报表定义保存 / 读取（owner scope） ====================

    [Fact]
    public async Task 报表定义_保存与读取_保留所有者与定义()
    {
        await using var db = _fixture.CreateDbContext();
        var ownerId = await SeedAuthorizedOwnerAsync(db, "rc-owner");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("集成-销售订单报表"));

        Assert.True(created.Id > 0);
        Assert.Equal(ownerId, created.OwnerUserId);
        Assert.Equal(ReportConfigurationStatus.Draft, created.Status);
        Assert.Equal(1, created.Version);
        Assert.Equal(0, created.CurrentPublishedVersion);

        var read = await service.GetAsync(ownerId, created.Id);
        Assert.Equal("集成-销售订单报表", read.Name);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, read.DatasetKey);
        Assert.Equal(3, read.Definition!.Fields.Count);
        Assert.Contains("totalAmount", read.Definition.Fields);
    }

    // ==================== 3. 草稿版本更新 ====================

    [Fact]
    public async Task 草稿更新_版本递增且定义持久化()
    {
        await using var db = _fixture.CreateDbContext();
        var ownerId = await SeedAuthorizedOwnerAsync(db, "rc-update");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("原始报表"));

        var next = SalesOrderDefinition();
        next.Fields = new List<string> { "orderNo", "currency", "totalAmount", "paymentTerms" };
        var updated = await service.UpdateAsync(ownerId, created.Id, created.Version, SaveDto("更新后的报表", next));

        Assert.Equal("更新后的报表", updated.Name);
        Assert.Equal(2, updated.Version);
        Assert.Equal(4, updated.Definition!.Fields.Count);

        var read = await service.GetAsync(ownerId, created.Id);
        Assert.Equal("更新后的报表", read.Name);
        Assert.Equal(4, read.Definition!.Fields.Count);
        Assert.Contains("paymentTerms", read.Definition.Fields);
    }

    // ==================== 4. 发布修订持久化 ====================

    [Fact]
    public async Task 发布修订_不可变快照持久化且可回读()
    {
        await using var db = _fixture.CreateDbContext();
        var ownerId = await SeedAuthorizedOwnerAsync(db, "rc-publish");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("要发布的报表"));

        var published = await service.PublishAsync(ownerId, created.Id, created.Version);

        Assert.Equal(ReportConfigurationStatus.Published, published.Status);
        Assert.Equal(1, published.CurrentPublishedVersion);
        Assert.Equal(2, published.Version);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.Version);
        Assert.Equal(published.Name, revision.Name);
        Assert.Equal(published.DatasetKey, revision.DatasetKey);
        Assert.Equal(3, revision.Definition!.Fields.Count);

        Assert.Equal(1, await CountRevisionRowsAsync(created.Id));
    }

    // ==================== 5. 跨所有者读取拒绝 ====================

    [Fact]
    public async Task 跨所有者读取_按不存在处理_不泄露()
    {
        await using var db = _fixture.CreateDbContext();
        var ownerA = await SeedAuthorizedOwnerAsync(db, "rc-owner-a");
        var ownerB = await SeedAuthorizedOwnerAsync(db, "rc-owner-b");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerA, SaveDto("A 的私有报表"));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.GetAsync(ownerB, created.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 脚手架 ====================

    private IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        }));

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

    private async Task AssertColumnPresentAsync(string column)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COL_LENGTH('db_owner.ContainerBookings', @column)";
        cmd.Parameters.AddWithValue("@column", column);
        var result = await cmd.ExecuteScalarAsync();

        Assert.NotNull(result);
        Assert.NotEqual(DBNull.Value, result);
    }

    private async Task AssertNoSingularContainerBookingTableAsync()
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = N'ContainerBooking'";

        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }

    private async Task<int> CountRevisionRowsAsync(long reportConfigurationId)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM db_owner.ReportConfigurationRevisions WHERE ReportConfigurationId = @id AND IsDeleted = 0";
        cmd.Parameters.AddWithValue("@id", reportConfigurationId);

        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次启动序列（自动建表 + 结构升级 + 种子 + 结构升级），全部测试复用同一目标库。
/// </summary>
public sealed class ReportConfigurationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_20261004";

    /// <summary>第 28 段补齐的订柜外贸 / 物流跟踪列（与 EF 实体逐一对应）。</summary>
    public static readonly string[] TrackingColumns =
    {
        "ShipmentMode",
        "BillOfLadingNo",
        "ShippingOrderNo",
        "TransitPort",
        "Etd",
        "Eta",
        "Atd",
        "Ata",
        "TruckerName",
        "CustomsBrokerId",
        "CustomsBrokerName",
        "InspectionRequired",
        "InspectionDate",
        "CustomsReleaseDate",
    };

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // 连接串只来自进程环境变量（调度器在子进程设置），缺省才使用专用 localdb 目标。
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = BuildDefaultConnectionString();
        }

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-280] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await BootstrapAsync();
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

    private async Task BootstrapAsync()
    {
        var options = BuildOptions();
        await using var db = new ErpDbContext(options);

        // 与 ERP.Api Program.cs 完全一致的启动序列（全新库自动建表 + 幂等结构升级 + 种子数据 + 再次结构升级）
        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrader.EnsureUpgradedAsync(db);
        await SeedData.InitializeAsync(db);
        await SchemaUpgrader.EnsureUpgradedAsync(db);

        Console.WriteLine("[ERP-280] 启动序列完成：EnsureCreated + EnsureUpgraded + SeedData + EnsureUpgraded（重复执行幂等）。");
    }
}
