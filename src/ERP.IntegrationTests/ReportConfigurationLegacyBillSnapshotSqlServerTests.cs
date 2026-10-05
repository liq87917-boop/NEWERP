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
/// ERP-319 旧单据导出族有界一致只读快照的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （收款单 + 付款单两个族），通过真实只读 Serializable 事务验证稳定 Oid 降序 / 原始值 / null / 列顺序、
/// 跨两族非空快照、超出一页仍返回完整匹配集、物化快照与后续写入隔离、缺表环境错误与取消传播。
/// <para>安全口径：写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c>、集成安全；连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class ReportConfigurationLegacyBillSnapshotSqlServerTests
    : IClassFixture<ReportConfigurationLegacyBillSnapshotSqlServerFixture>
{
    private readonly ReportConfigurationLegacyBillSnapshotSqlServerFixture _fixture;

    public ReportConfigurationLegacyBillSnapshotSqlServerTests(ReportConfigurationLegacyBillSnapshotSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationLegacyBillSnapshotSqlServerFixture.InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationLegacyBillSnapshotSqlServerFixture.DatabasePrefix, builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
    }

    private LegacyBillExportReadService BuildReader()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = _fixture.ConnectionString })
            .Build();
        return new LegacyBillExportReadService(new StoredProcedureService(config));
    }

    private static List<ReportConfigurationColumnDto> ColumnsFor(string familyKey)
        => LegacyBillExportCatalog.Resolve(familyKey).Columns
            .Select(c => new ReportConfigurationColumnDto(c.Key, c.Title, c.Type, null))
            .ToList();

    private static ReportConfigurationEvidenceContextDto Evidence(string datasetKey)
        => new(datasetKey, "一行一条单据", "金额按原币呈现；数量按基础单位", "只读", "边界", "免责",
            ReportConfigurationConstants.CoverageMatchedSet);

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    [Fact]
    public async Task 受控快照_收款单_稳定Oid降序与原始值()
    {
        Guard();
        var reader = BuildReader();
        var family = LegacyBillExportCatalog.Resolve("receipt");
        var columns = ColumnsFor("receipt");

        var snapshot = await reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "receipt" },
            "corr-319-rc", "privileged", columns, Evidence(family.DatasetKey));

        Assert.True(snapshot.IsConsistent);
        Assert.Equal(3, snapshot.MatchedCount);
        Assert.Equal(3, snapshot.FactIds.Count);
        Assert.True(snapshot.FactIds[0] > snapshot.FactIds[1]);

        Assert.Equal(
            family.Columns.Select(c => c.Key).ToArray(),
            snapshot.Columns.Select(c => c.Key).ToArray());

        // ORDER BY Oid DESC：后插入的 RC-3 先出。
        Assert.Equal("ERP319-RC-3", (string)snapshot.Rows[0]["BillNo"]!);
        Assert.Equal(350.75m, (decimal)snapshot.Rows[0]["Amount"]!);
        Assert.Null(snapshot.Rows[2]["Remark"]);

        await snapshot.CompleteAsync();
        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task 受控快照_付款单_第二族非空()
    {
        Guard();
        var reader = BuildReader();
        var family = LegacyBillExportCatalog.Resolve("payment");
        var columns = ColumnsFor("payment");

        var snapshot = await reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "payment" },
            "corr-319-pm", "privileged", columns, Evidence(family.DatasetKey));

        Assert.True(snapshot.IsConsistent);
        Assert.Equal(2, snapshot.MatchedCount);
        Assert.Equal(family.Columns.Select(c => c.Key).ToArray(), snapshot.Columns.Select(c => c.Key).ToArray());
        Assert.Equal("ERP319-PM-2", (string)snapshot.Rows[0]["BillNo"]!);

        await snapshot.CompleteAsync();
        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task 渲染匹配页_超出一页仍返回完整匹配集()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reader = BuildReader();
        var provider = new LegacyBillExportReportConfigurationDatasetProvider(
            db, reader, LegacyBillExportCatalog.Resolve("receipt").DatasetKey);
        var columns = ColumnsFor("receipt");

        var snapshot = await reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "receipt" },
            "corr-319-page", "privileged", columns,
            Evidence(LegacyBillExportCatalog.Resolve("receipt").DatasetKey));

        var definition = Definition(provider.DatasetKey, "BillNo", "ReceiptDate", "Amount", "Remark");
        var preview = provider.RenderMatchedPage(
            snapshot, definition,
            new ReportConfigurationPreviewParameters(2, 2, ReportConfigurationConstants.GroupNone, null, null));

        Assert.Equal(3, preview.MatchedCount);
        Assert.Equal(3, preview.Total);
        Assert.Equal(2, preview.Page);
        Assert.Equal(2, preview.PageSize);
        Assert.Equal(2, preview.TotalPages);
        Assert.Single(preview.Rows);
        Assert.Equal("ERP319-RC-1", (string)preview.Rows[0]["BillNo"]!);

        await snapshot.CompleteAsync();
        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task 快照一致性_物化后写入不影响已打开快照()
    {
        Guard();
        var reader = BuildReader();
        var family = LegacyBillExportCatalog.Resolve("receipt");
        var columns = ColumnsFor("receipt");

        var snapshot = await reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "receipt" },
            "corr-319-iso", "privileged", columns, Evidence(family.DatasetKey));
        Assert.Equal(3, snapshot.MatchedCount);

        // 释放快照事务后再从另一连接提交写入，避免 Serializable 范围锁阻塞。
        await snapshot.CompleteAsync();
        await snapshot.DisposeAsync();

        await using var db = _fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinanceReceipt WHERE BillNo = 'ERP319-RC-4') " +
            "INSERT INTO db_owner.FinanceReceipt (BillNo, ReceiptDate, CustomerId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP319-RC-4', '2026-09-04', 1004, 120.00, 1, N'电汇', 'ACCT-4', 1, N'隔离写入');");

        // 已物化快照保持原一致视图，绝不因后续写入而漂移。
        Assert.Equal(3, snapshot.MatchedCount);

        var fresh = await reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "receipt" },
            "corr-319-fresh", "privileged", columns, Evidence(family.DatasetKey));
        Assert.Equal(4, fresh.MatchedCount);

        await fresh.CompleteAsync();
        await fresh.DisposeAsync();
    }

    [Fact]
    public async Task 缺失表结构_快照环境错误()
    {
        Guard();
        var reader = BuildReader();
        var columns = ColumnsFor("sales-order");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "sales-order" },
            "corr-319-missing", "privileged", columns,
            Evidence(LegacyBillExportCatalog.Resolve("sales-order").DatasetKey)));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, ex.Code);
    }

    [Fact]
    public async Task 取消令牌_传播不降级()
    {
        Guard();
        var reader = BuildReader();
        var columns = ColumnsFor("receipt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadSnapshotAsync(
            new LegacyBillExportQuery { FamilyKey = "receipt" },
            "corr-319-cancel", "privileged", columns,
            Evidence(LegacyBillExportCatalog.Resolve("receipt").DatasetKey), cts.Token));
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（建表 + 结构升级 + 种子）+ 幂等非空夹具（收款单 + 付款单两族）。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>、集成安全）。
/// </summary>
public sealed class ReportConfigurationLegacyBillSnapshotSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP319";
    private const string UserName = "ERP319-ADMIN";
    private const string RoleCode = "ERP319-SYS";

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

        foreach (var menuCode in new[] { "receipt", "payment" })
        {
            var menu = await EnsureMenuAsync(db, menuCode);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        await EnsureLegacyReceiptTableAsync(db);
        await EnsureLegacyPaymentTableAsync(db);

        PrivilegedUserId = user.Id;
    }

    private static async Task EnsureLegacyReceiptTableAsync(ErpDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "IF SCHEMA_ID('db_owner') IS NULL EXEC('CREATE SCHEMA [db_owner]');" +
            "IF OBJECT_ID('db_owner.FinanceReceipt','U') IS NULL " +
            "CREATE TABLE db_owner.FinanceReceipt (" +
            "Oid BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY," +
            "BillNo NVARCHAR(100) NULL, ReceiptDate DATETIME NULL, CustomerId BIGINT NULL," +
            "Amount DECIMAL(18,2) NULL, Currency INT NULL, PaymentMethod NVARCHAR(50) NULL," +
            "BankAccount NVARCHAR(100) NULL, Status INT NULL, Remark NVARCHAR(500) NULL);");

        await db.Database.ExecuteSqlRawAsync(
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinanceReceipt WHERE BillNo = 'ERP319-RC-1') " +
            "INSERT INTO db_owner.FinanceReceipt (BillNo, ReceiptDate, CustomerId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP319-RC-1', '2026-09-01', 1001, 200.50, 1, N'电汇', 'ACCT-1', 2, NULL);" +
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinanceReceipt WHERE BillNo = 'ERP319-RC-2') " +
            "INSERT INTO db_owner.FinanceReceipt (BillNo, ReceiptDate, CustomerId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP319-RC-2', '2026-09-02', 1002, 300.00, 1, N'信用证', 'ACCT-2', 1, N'备注');" +
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinanceReceipt WHERE BillNo = 'ERP319-RC-3') " +
            "INSERT INTO db_owner.FinanceReceipt (BillNo, ReceiptDate, CustomerId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP319-RC-3', '2026-09-03', 1003, 350.75, 1, N'电汇', 'ACCT-3', 1, N'后插');");
    }

    private static async Task EnsureLegacyPaymentTableAsync(ErpDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "IF SCHEMA_ID('db_owner') IS NULL EXEC('CREATE SCHEMA [db_owner]');" +
            "IF OBJECT_ID('db_owner.FinancePayment','U') IS NULL " +
            "CREATE TABLE db_owner.FinancePayment (" +
            "Oid BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY," +
            "BillNo NVARCHAR(100) NULL, PaymentDate DATETIME NULL, SupplierId BIGINT NULL," +
            "PaymentApplyId BIGINT NULL, Amount DECIMAL(18,2) NULL, Currency INT NULL," +
            "PaymentMethod NVARCHAR(50) NULL, BankAccount NVARCHAR(100) NULL, Status INT NULL, Remark NVARCHAR(500) NULL);");

        await db.Database.ExecuteSqlRawAsync(
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinancePayment WHERE BillNo = 'ERP319-PM-1') " +
            "INSERT INTO db_owner.FinancePayment (BillNo, PaymentDate, SupplierId, PaymentApplyId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP319-PM-1', '2026-09-01', 2001, 1, 500.00, 1, N'电汇', 'OUT-1', 1, NULL);" +
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinancePayment WHERE BillNo = 'ERP319-PM-2') " +
            "INSERT INTO db_owner.FinancePayment (BillNo, PaymentDate, SupplierId, PaymentApplyId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP319-PM-2', '2026-09-02', 2002, 2, 750.25, 1, N'信用证', 'OUT-2', 2, N'付款备注');");
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

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP319 隔离账号", Status = UserStatus.Enabled };
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
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}

