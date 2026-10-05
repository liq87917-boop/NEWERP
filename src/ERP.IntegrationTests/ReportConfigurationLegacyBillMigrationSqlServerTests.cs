using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-308 旧单据导出族迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具（收款单旧表 + 行），
/// 通过受控只读读取验证列顺序 / null / 原始币种金额 / 关键字 / 状态 / 日期筛选，缺失旧表结构返回显式 environment-blocked，
/// 受限制账号 fail closed，以及通用 Excel 导出可执行。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationLegacyBillMigrationSqlServerTests
    : IClassFixture<ReportConfigurationLegacyBillMigrationSqlServerFixture>
{
    private readonly ReportConfigurationLegacyBillMigrationSqlServerFixture _fixture;

    public ReportConfigurationLegacyBillMigrationSqlServerTests(ReportConfigurationLegacyBillMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationLegacyBillMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationLegacyBillMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    private LegacyBillExportReadService BuildReader()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = _fixture.ConnectionString })
            .Build();
        return new LegacyBillExportReadService(new StoredProcedureService(config));
    }

    private static ReportConfigurationDefinition ReceiptDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = LegacyBillExportCatalog.Resolve("receipt").DatasetKey,
            Fields = new List<string> { "BillNo", "ReceiptDate", "CustomerId", "Amount", "Currency", "PaymentMethod", "BankAccount", "Status", "Remark" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    [Fact]
    public async Task 受控只读读取_保留列顺序与null与原始值()
    {
        Guard();
        var reader = BuildReader();

        var page = await reader.ReadPageAsync(new LegacyBillExportQuery
        {
            FamilyKey = "receipt",
            Page = 1,
            PageSize = 20,
        });

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(
            LegacyBillExportCatalog.Resolve("receipt").Columns.Select(c => c.Key).ToArray(),
            page.Columns.Select(c => c.Key).ToArray());

        // ORDER BY Oid DESC：后插入的 RC-2 先出。
        var first = page.Rows[0];
        Assert.Equal("ERP308-RC-2", (string)first["BillNo"]!);
        Assert.Equal(350.75m, (decimal)first["Amount"]!);
        Assert.Equal("备注", (string)first["Remark"]!);

        var second = page.Rows[1];
        Assert.Equal("ERP308-RC-1", (string)second["BillNo"]!);
        Assert.Null(second["Remark"]);
        Assert.Equal(200.50m, (decimal)second["Amount"]!);
        Assert.Equal(new DateTime(2026, 9, 1), (DateTime)second["ReceiptDate"]!);
    }

    [Fact]
    public async Task 受控只读读取_关键字状态日期筛选()
    {
        Guard();
        var reader = BuildReader();

        var page = await reader.ReadPageAsync(new LegacyBillExportQuery
        {
            FamilyKey = "receipt",
            Keyword = "RC-1",
            Status = 2,
            StartDate = new DateTime(2026, 9, 1),
            EndDate = new DateTime(2026, 9, 30),
            Page = 1,
            PageSize = 20,
        });

        Assert.Equal(1, page.Total);
        Assert.Equal("ERP308-RC-1", (string)Assert.Single(page.Rows)["BillNo"]!);
    }

    [Fact]
    public async Task 缺失旧表结构_有界环境错误()
    {
        Guard();
        var reader = BuildReader();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => reader.ReadPageAsync(new LegacyBillExportQuery
        {
            FamilyKey = "sales-order",
            Page = 1,
            PageSize = 20,
        }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, ex.Code);
    }

    [Fact]
    public async Task 受限制用户_数据不暴露且预览拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedRestrictedUserAsync(db, "ERP308-RESTRICTED");

        var provider = new LegacyBillExportReportConfigurationDatasetProvider(
            db, BuildReader(), LegacyBillExportCatalog.Resolve("receipt").DatasetKey);

        Assert.Null(await provider.GetDatasetAsync(userId));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            ReceiptDefinition(), Params(), userId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 通用Excel导出_可执行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var provider = new LegacyBillExportReportConfigurationDatasetProvider(
            db, BuildReader(), LegacyBillExportCatalog.Resolve("receipt").DatasetKey);
        var preview = await provider.PreviewAsync(ReceiptDefinition(), Params(), _fixture.PrivilegedUserId);

        var excel = new ReportConfigurationExcelExporter().Build(preview);
        Assert.NotEmpty(excel);
        Assert.Equal("PK", Encoding.ASCII.GetString(excel, 0, 2));
    }

    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, string userName)
    {
        var existing = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == userName && !u.IsDeleted);
        if (existing is not null)
            return existing.Id;

        var user = new SysUser { UserName = userName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = userName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = userName + "-role", RoleCode = userName + "-role", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "receipt" && !m.IsDeleted)
            ?? throw new InvalidOperationException("缺少菜单种子 receipt");
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具（收款单旧表 + 行）。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationLegacyBillMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP308";
    private const string UserName = "ERP308-ADMIN";
    private const string RoleCode = "ERP308-SYS";

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

        var menu = await EnsureMenuAsync(db, "receipt");
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        await EnsureLegacyReceiptTableAsync(db);

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
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinanceReceipt WHERE BillNo = 'ERP308-RC-1') " +
            "INSERT INTO db_owner.FinanceReceipt (BillNo, ReceiptDate, CustomerId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP308-RC-1', '2026-09-01', 1001, 200.50, 1, N'电汇', 'ACCT-1', 2, NULL);" +
            "IF NOT EXISTS (SELECT 1 FROM db_owner.FinanceReceipt WHERE BillNo = 'ERP308-RC-2') " +
            "INSERT INTO db_owner.FinanceReceipt (BillNo, ReceiptDate, CustomerId, Amount, Currency, PaymentMethod, BankAccount, Status, Remark) " +
            "VALUES ('ERP308-RC-2', '2026-09-02', 1002, 350.75, 1, N'信用证', 'ACCT-2', 1, N'备注');");
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

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP308 隔离账号", Status = UserStatus.Enabled };
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


