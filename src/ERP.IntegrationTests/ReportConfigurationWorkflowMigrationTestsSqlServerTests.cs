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
/// ERP-302 跟进提醒 / 报价成交率 / 业务员产值迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种
/// 非空夹具（客户 + 员工 + 跟进记录 + 报价单 + 销售订单），通过受控数据集适配器预览并与既有报表服务逐行比对。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationWorkflowMigrationSqlServerTests
    : IClassFixture<ReportConfigurationWorkflowMigrationSqlServerFixture>
{
    private readonly ReportConfigurationWorkflowMigrationSqlServerFixture _fixture;

    public ReportConfigurationWorkflowMigrationSqlServerTests(ReportConfigurationWorkflowMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationWorkflowMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationWorkflowMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 跟进提醒_报价成交率_业务员产值_SQLServer预览与既有报表逐行一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);
        var scope = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };

        var followProvider = new FollowUpDueReportConfigurationDatasetProvider(reportService, db);
        var followLegacy = await reportService.GetFollowUpDueAsync(DateTime.Today, 7, scope);
        var followPreview = await followProvider.PreviewAsync(FollowUpDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(followLegacy.Count, followPreview.Total);
        Assert.Equal(followLegacy.Count, followPreview.Rows.Count);
        foreach (var legacyRow in followLegacy)
        {
            var row = Assert.Single(followPreview.Rows, r => (string)r["salesmanName"]! == legacyRow.SalesmanName);
            Assert.Equal(legacyRow.CustomerName, (string)row["customerName"]!);
            Assert.Equal(legacyRow.DueDays, (int)row["dueDays"]!);
            Assert.Equal(legacyRow.DueStatus, (string)row["dueStatus"]!);
        }

        var quotationProvider = new QuotationConversionReportConfigurationDatasetProvider(reportService, db);
        var quotationLegacy = await reportService.GetQuotationConversionAsync(DateTime.Today, DateTime.Today, scope);
        var quotationPreview = await quotationProvider.PreviewAsync(QuotationDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(quotationLegacy.Count, quotationPreview.Total);
        Assert.Equal(quotationLegacy.Count, quotationPreview.Rows.Count);
        foreach (var legacyRow in quotationLegacy)
        {
            var row = Assert.Single(quotationPreview.Rows, r => (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.QuotationCount, (int)row["quotationCount"]!);
            Assert.Equal(legacyRow.TotalAmount, (decimal)row["totalAmount"]!);
        }

        var salesmanProvider = new SalesmanOutputReportConfigurationDatasetProvider(reportService, db);
        var salesmanLegacy = await reportService.GetSalesmanOutputAsync(DateTime.Today, DateTime.Today, scope);
        var salesmanPreview = await salesmanProvider.PreviewAsync(SalesmanDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(salesmanLegacy.Count, salesmanPreview.Total);
        Assert.Equal(salesmanLegacy.Count, salesmanPreview.Rows.Count);
        foreach (var legacyRow in salesmanLegacy)
        {
            var row = Assert.Single(salesmanPreview.Rows, r => (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.SalesmanId, (long)row["salesmanId"]!);
            Assert.Equal(legacyRow.OrderCount, (int)row["orderCount"]!);
            Assert.Equal(legacyRow.TotalAmount, (decimal?)row["totalAmount"]);
        }
    }

    private static ReportConfigurationDefinition FollowUpDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetFollowUpDue,
            Fields = new List<string> { "customerName", "salesmanName", "followDate", "nextFollowDate", "dueDays", "dueStatus", "subject" },
        };

    private static ReportConfigurationDefinition QuotationDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetQuotationConversion,
            Fields = new List<string> { "salesmanName", "currency", "quotationCount", "convertedCount", "totalAmount", "convertedAmount" },
        };

    private static ReportConfigurationDefinition SalesmanDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesmanOutput,
            Fields = new List<string> { "salesmanId", "salesmanName", "currency", "orderCount", "totalAmount", "totalProfit" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 200, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationWorkflowMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP302";

    private const string CustomerCode = "ERP302-C1";
    private const string UserName = "ERP302-ADMIN";
    private const string RoleCode = "ERP302-SYS";
    private const string EmployeeCode = "ERP302-E1";

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

        var menuFollow = await EnsureMenuAsync(db, "follow-up-due", "跟进提醒");
        var menuQuotation = await EnsureMenuAsync(db, "quotation", "报价单");
        var menuSalesman = await EnsureMenuAsync(db, "salesman-output", "业务员产值报表");

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in new[] { menuFollow, menuQuotation, menuSalesman })
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var customer = await EnsureCustomerAsync(db);
        var employee = await EnsureEmployeeAsync(db);

        if (!await db.CustomerFollowUps.AnyAsync(f => f.FollowNo == "ERP302-FU-1" && !f.IsDeleted))
        {
            db.CustomerFollowUps.AddRange(
                new CustomerFollowUp
                {
                    FollowNo = "ERP302-FU-1",
                    FollowDate = DateTime.Today,
                    CustomerId = customer.Id,
                    CustomerName = "ERP302 客户",
                    FollowType = "电话",
                    ContactPerson = "对接人",
                    SalesmanId = employee.Id,
                    SalesmanName = "ERP302 张三",
                    Subject = "ERP302 跟进主题一",
                    Content = "跟进内容",
                    Result = "有意向",
                    NextFollowDate = DateTime.Today.AddDays(-2),
                },
                new CustomerFollowUp
                {
                    FollowNo = "ERP302-FU-2",
                    FollowDate = DateTime.Today,
                    CustomerId = customer.Id,
                    CustomerName = "ERP302 客户",
                    FollowType = "电话",
                    ContactPerson = "对接人",
                    SalesmanId = employee.Id,
                    SalesmanName = "ERP302 李四",
                    Subject = "ERP302 跟进主题二",
                    Content = "跟进内容",
                    Result = "待跟进",
                    NextFollowDate = DateTime.Today,
                },
                new CustomerFollowUp
                {
                    FollowNo = "ERP302-FU-3",
                    FollowDate = DateTime.Today,
                    CustomerId = customer.Id,
                    CustomerName = "ERP302 客户",
                    FollowType = "电话",
                    ContactPerson = "对接人",
                    SalesmanId = employee.Id,
                    SalesmanName = "ERP302 王五",
                    Subject = "ERP302 跟进主题三",
                    Content = "跟进内容",
                    Result = "待跟进",
                    NextFollowDate = DateTime.Today.AddDays(3),
                });
            await db.SaveChangesAsync();
        }


        if (!await db.Quotations.AnyAsync(q => q.QuotationNo == "ERP302-QT-USD" && !q.IsDeleted))
        {
            db.Quotations.AddRange(
                new Quotation
                {
                    QuotationNo = "ERP302-QT-USD",
                    QuotationDate = DateTime.Today,
                    CustomerId = customer.Id,
                    CustomerName = "ERP302 客户",
                    SalesmanName = "ERP302 张三",
                    Currency = Currency.USD,
                    TotalAmount = 100m,
                    Status = DocumentStatus.Pending,
                },
                new Quotation
                {
                    QuotationNo = "ERP302-QT-EUR",
                    QuotationDate = DateTime.Today,
                    CustomerId = customer.Id,
                    CustomerName = "ERP302 客户",
                    SalesmanName = "ERP302 张三",
                    Currency = Currency.EUR,
                    TotalAmount = 200m,
                    Status = DocumentStatus.Pending,
                });
            await db.SaveChangesAsync();
        }

        if (!await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP302-SO-USD" && !o.IsDeleted))
        {
            db.SalesOrders.AddRange(
                new SalesOrder
                {
                    OrderNo = "ERP302-SO-USD",
                    OrderDate = DateTime.Today,
                    CustomerId = customer.Id,
                    SalesmanId = employee.Id,
                    Currency = Currency.USD,
                    TotalAmount = 100m,
                    Status = DocumentStatus.Approved,
                },
                new SalesOrder
                {
                    OrderNo = "ERP302-SO-EUR",
                    OrderDate = DateTime.Today,
                    CustomerId = customer.Id,
                    SalesmanId = employee.Id,
                    Currency = Currency.EUR,
                    TotalAmount = 200m,
                    Status = DocumentStatus.Approved,
                });
            await db.SaveChangesAsync();
        }

        PrivilegedUserId = user.Id;
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
            DisplayName = "ERP302 隔离账号",
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

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP302 客户", Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseEmployee> EnsureEmployeeAsync(ErpDbContext db)
    {
        var employee = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == EmployeeCode && !e.IsDeleted);
        if (employee is not null)
            return employee;

        employee = new BaseEmployee { EmployeeCode = EmployeeCode, EmployeeName = "ERP302 张三", IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
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

