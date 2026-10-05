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
/// ERP-303 Stage 2 代理服务费月度汇总迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具，
/// 通过受控数据集适配器预览并与既有 <see cref="AgencyServiceFeeMonthlySummaryService"/> 逐行比对。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>说明：本测试项目不引用 ERP.Api，因此 Api 层的客户订单与收款核对 / 出货财务进度运行时适配器由
/// ERP.UnitTests（内存数据库，引用 Api）覆盖；本文件覆盖可直达的基础设施层月度汇总数据集。</para>
/// </summary>
public sealed class ReportConfigurationReconciliationMigrationSqlServerTests
    : IClassFixture<ReportConfigurationReconciliationMigrationSqlServerFixture>
{
    private readonly ReportConfigurationReconciliationMigrationSqlServerFixture _fixture;

    public ReportConfigurationReconciliationMigrationSqlServerTests(
        ReportConfigurationReconciliationMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationReconciliationMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationReconciliationMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 代理服务费月度汇总_SQLServer预览与既有查询逐行一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var scope = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };

        var provider = new AgencyServiceFeeMonthlyReportConfigurationDatasetProvider(db);
        var legacy = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(
            db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 200 }, scope);
        var preview = await provider.PreviewAsync(
            Definition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(legacy.Total, preview.Total);
        Assert.Equal(legacy.Rows.Count, preview.Rows.Count);
        foreach (var legacyRow in legacy.Rows)
        {
            var row = Assert.Single(preview.Rows, r =>
                (string)r["statementMonthText"]! == legacyRow.StatementMonthText &&
                (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.RegisteredTotalAmount, (decimal)row["registeredTotalAmount"]!);
            Assert.Equal(legacyRow.DraftTotalAmount, (decimal)row["draftTotalAmount"]!);
            Assert.Equal(legacyRow.VoidedTotalAmount, (decimal)row["voidedTotalAmount"]!);
        }
    }

    private static ReportConfigurationDefinition Definition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly,
            Fields = new List<string>
            {
                "statementMonthText", "customerName", "currency",
                "registeredTotalAmount", "draftTotalAmount", "voidedTotalAmount",
            },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 200, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空月度汇总夹具。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationReconciliationMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP303";

    private const string CustomerCode = "ERP303-C1";
    private const string UserName = "ERP303-ADMIN";
    private const string RoleCode = "ERP303-SYS";

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

        var customer = await EnsureCustomerAsync(db);
        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);
        var menu = await EnsureMenuAsync(db, "customer", "客户资料");

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id && !ur.IsDeleted))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        }

        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }

        await db.SaveChangesAsync();

        await EnsureStatementAsync(db, "ASF-ERP303-USD-1", customer.Id, 300m, "USD", AgencyServiceFeeStatementRules.StatusRecorded, new DateTime(2026, 9, 1));
        await EnsureStatementAsync(db, "ASF-ERP303-USD-2", customer.Id, 50m, "USD", AgencyServiceFeeStatementRules.StatusDraft, new DateTime(2026, 9, 1));
        await EnsureStatementAsync(db, "ASF-ERP303-CNY-1", customer.Id, 700m, "CNY", AgencyServiceFeeStatementRules.StatusRecorded, new DateTime(2026, 9, 2));

        PrivilegedUserId = user.Id;
    }


    private static async Task<AgencyServiceFeeStatement> EnsureStatementAsync(
        ErpDbContext db, string no, long customerId, decimal amount, string currency, int status, DateTime statementDate)
    {
        var statement = await db.AgencyServiceFeeStatements.FirstOrDefaultAsync(s => s.StatementNo == no && !s.IsDeleted);
        if (statement is not null)
            return statement;

        statement = new AgencyServiceFeeStatement
        {
            StatementNo = no,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(no),
            CustomerId = customerId,
            CustomerCode = CustomerCode,
            CustomerName = "ERP303 客户",
            Currency = currency,
            StatementDate = statementDate,
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            TotalAmount = amount,
            Status = status,
            IsDeleted = false,
        };
        db.AgencyServiceFeeStatements.Add(statement);
        await db.SaveChangesAsync();
        return statement;
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code, string name)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null)
            return menu;

        menu = new SysMenu { MenuName = name, MenuCode = code, MenuType = MenuType.Menu };
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
            DisplayName = "ERP303 隔离账号",
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

    private static async Task<BaseCustomer> EnsureCustomerAsync(ErpDbContext db)
    {
        var customer = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == CustomerCode && !c.IsDeleted);
        if (customer is not null)
            return customer;

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP303 客户", Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
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

