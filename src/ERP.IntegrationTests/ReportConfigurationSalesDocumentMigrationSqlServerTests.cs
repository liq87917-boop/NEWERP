using ERP.Application.Common;
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
/// ERP-318 销售单据打印族迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （报价单 + 形式发票 PI，含多币种、多明细、空明细与已删除明细），通过受控数据集适配器验证
/// 表头 / 明细两种粒度、原币 / 基础单位保留、SortNo 行序、软删除排除与客户业务员数据范围（fail closed）。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationSalesDocumentMigrationSqlServerTests
    : IClassFixture<ReportConfigurationSalesDocumentMigrationSqlServerFixture>
{
    private readonly ReportConfigurationSalesDocumentMigrationSqlServerFixture _fixture;

    public ReportConfigurationSalesDocumentMigrationSqlServerTests(
        ReportConfigurationSalesDocumentMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal(
            $"(localdb)\\{ReportConfigurationSalesDocumentMigrationSqlServerFixture.InstanceMarker}",
            t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationSalesDocumentMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    private static ReportConfigurationDefinition Definition(
        string familyKey, IReadOnlyList<string>? fields = null)
    {
        var family = ReportConfigurationSalesDocumentCatalog.Resolve(familyKey);
        return new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = family.DatasetKey,
            Fields = (fields ?? family.Columns
                .Where(c => c.Grain != ReportSalesDocumentGrain.Detail)
                .Select(c => c.Key).ToList()).ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 100 },
        };
    }

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 100)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    [Fact]
    public async Task 报价单预览_非空SQL_表头与明细行序保留且软删除排除()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);

        var header = await provider.PreviewAsync(Definition("quotation"), Params(), _fixture.PrivilegedUserId);
        Assert.True(header.Total >= 2);
        Assert.Equal(
            ReportConfigurationSalesDocumentCatalog.Resolve("quotation").Columns
                .Where(c => c.Grain != ReportSalesDocumentGrain.Detail)
                .Select(c => c.Key).ToArray(),
            header.Columns.Select(c => c.Key).ToArray());

        var usd = Assert.Single(header.Rows, r =>
            string.Equals((string?)r["docNo"], "ERP318-QT-USD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("USD", (string?)usd["currency"]);
        Assert.Equal(120m, (decimal)usd["totalAmount"]!);

        var lineFields = new[] { "id", "docNo", "sortNo", "productCode", "quantity", "unit", "amount", "totalAmount" };
        var lines = await provider.PreviewAsync(Definition("quotation", lineFields), Params(), _fixture.PrivilegedUserId);

        Assert.DoesNotContain(lines.Rows, r =>
            string.Equals((string?)r["productCode"], "ERP318-P-DEL", StringComparison.OrdinalIgnoreCase));

        var qtUsdLines = lines.Rows
            .Where(r => string.Equals((string?)r["docNo"], "ERP318-QT-USD", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => (int)r["sortNo"]!)
            .ToList();
        Assert.Equal(2, qtUsdLines.Count);
        Assert.Equal(1, (int)qtUsdLines[0]["sortNo"]!);
        Assert.Equal(60m, (decimal)qtUsdLines[0]["amount"]!);
        Assert.Null(qtUsdLines[0]["totalAmount"]);
    }

    [Fact]
    public async Task PI预览_非空SQL_PI专属字段与跨币种分离()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("proforma-invoice").DatasetKey);

        var header = await provider.PreviewAsync(Definition("proforma-invoice"), Params(), _fixture.PrivilegedUserId);
        var pi = Assert.Single(header.Rows, r =>
            string.Equals((string?)r["docNo"], "ERP318-PI-1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("EUR", (string?)pi["currency"]);
        Assert.Equal("ERP318-QT-EUR", (string?)pi["quotationNo"]);
        Assert.Equal(30m, (decimal)pi["depositRatio"]!);
        Assert.Equal(90m, (decimal)pi["depositAmount"]!);

        var lineFields = new[] { "id", "docNo", "sortNo", "productCode", "quantity", "unit", "amount" };
        var lines = await provider.PreviewAsync(Definition("proforma-invoice", lineFields), Params(), _fixture.PrivilegedUserId);
        Assert.Equal(1, lines.Total);
        Assert.Equal(300m, (decimal)lines.Rows[0]["amount"]!);
    }

    [Fact]
    public async Task 客户数据范围_受限制业务员仅见分配客户()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("quotation"), Params(), _fixture.RestrictedUserId);

        Assert.Single(preview.Rows);
        Assert.Equal("ERP318-QT-USD", (string)preview.Rows[0]["docNo"]!);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具
/// （报价单 + 形式发票 PI，含多币种、多明细、空明细与已删除明细）。写库前每次都断言目标身份
/// （实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationSalesDocumentMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP318";
    private const string UserName = "ERP318-ADMIN";
    private const string RoleCode = "ERP318-SYS";
    private const string RestrictedUserName = "ERP318-SALES01";
    private const string RestrictedRoleCode = "ERP318-SALESROLE";
    private const string SalesmanEmployeeCode = "ERP318-SALES01";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }

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

    private async Task SeedFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var user = await EnsureUserAsync(db, UserName, "ERP318 隔离账号");
        var role = await EnsureRoleAsync(db, RoleCode, isSystem: true);
        await EnsureUserRoleAsync(db, user.Id, role.Id);
        foreach (var code in new[] { "quotation", "proforma-invoice" })
            await EnsureRoleMenuAsync(db, role.Id, code);

        var restrictedUser = await EnsureUserAsync(db, RestrictedUserName, "ERP318 受限制业务员");
        var restrictedRole = await EnsureRoleAsync(db, RestrictedRoleCode, isSystem: false);
        await EnsureUserRoleAsync(db, restrictedUser.Id, restrictedRole.Id);
        await EnsureRoleMenuAsync(db, restrictedRole.Id, "quotation");

        var employee = await EnsureEmployeeAsync(db, SalesmanEmployeeCode);
        var customer1 = await EnsureCustomerAsync(db, "ERP318-CUST-1", "ERP318 分配客户", employee.Id);
        var customer2 = await EnsureCustomerAsync(db, "ERP318-CUST-2", "ERP318 他人客户", null);

        await EnsureSalesDocumentsAsync(db, customer1.Id, customer2.Id);

        PrivilegedUserId = user.Id;
        RestrictedUserId = restrictedUser.Id;
    }

    private static async Task EnsureSalesDocumentsAsync(ErpDbContext db, long customer1Id, long customer2Id)
    {
        if (!await db.Quotations.AnyAsync(q => !q.IsDeleted && q.QuotationNo == "ERP318-QT-USD"))
        {
            var q = new Quotation
            {
                QuotationNo = "ERP318-QT-USD",
                QuotationDate = DateTime.Today,
                CustomerId = customer1Id,
                CustomerName = "ERP318 分配客户",
                Currency = Currency.USD,
                ExchangeRate = 1,
                TotalAmount = 120m,
                TotalAmountCny = 120m,
                SalesmanName = "ERP318 业务员",
                Status = DocumentStatus.Pending,
            };
            db.Quotations.Add(q);
            await db.SaveChangesAsync();
            db.QuotationDetails.AddRange(
                new QuotationDetail { QuotationId = q.Id, QuotationNo = q.QuotationNo, SortNo = 1, ProductCode = "ERP318-P-1", ProductName = "ERP318 商品一", Spec = "标准", Unit = "PCS", Quantity = 10m, UnitPrice = 6m, Amount = 60m },
                new QuotationDetail { QuotationId = q.Id, QuotationNo = q.QuotationNo, SortNo = 2, ProductCode = "ERP318-P-2", ProductName = "ERP318 商品二", Spec = "标准", Unit = "PCS", Quantity = 2m, UnitPrice = 30m, Amount = 60m },
                new QuotationDetail { QuotationId = q.Id, QuotationNo = q.QuotationNo, SortNo = 3, ProductCode = "ERP318-P-DEL", ProductName = "ERP318 已删除行", Spec = "标准", Unit = "PCS", Quantity = 1m, UnitPrice = 1m, Amount = 1m, IsDeleted = true });
            await db.SaveChangesAsync();
        }

        if (!await db.Quotations.AnyAsync(q => !q.IsDeleted && q.QuotationNo == "ERP318-QT-EUR"))
        {
            var q = new Quotation
            {
                QuotationNo = "ERP318-QT-EUR",
                QuotationDate = DateTime.Today,
                CustomerId = customer2Id,
                CustomerName = "ERP318 他人客户",
                Currency = Currency.EUR,
                ExchangeRate = 1,
                TotalAmount = 200m,
                TotalAmountCny = 200m,
                SalesmanName = "ERP318 业务员",
                Status = DocumentStatus.Approved,
            };
            db.Quotations.Add(q);
            await db.SaveChangesAsync();
            db.QuotationDetails.Add(new QuotationDetail { QuotationId = q.Id, QuotationNo = q.QuotationNo, SortNo = 1, ProductCode = "ERP318-P-3", ProductName = "ERP318 商品三", Spec = "标准", Unit = "BOX", Quantity = 5m, UnitPrice = 40m, Amount = 200m });
            await db.SaveChangesAsync();
        }

        if (!await db.Quotations.AnyAsync(q => !q.IsDeleted && q.QuotationNo == "ERP318-QT-EMPTY"))
        {
            db.Quotations.Add(new Quotation
            {
                QuotationNo = "ERP318-QT-EMPTY",
                QuotationDate = DateTime.Today,
                CustomerId = customer2Id,
                CustomerName = "ERP318 他人客户",
                Currency = Currency.USD,
                ExchangeRate = 1,
                TotalAmount = 0m,
                TotalAmountCny = 0m,
                SalesmanName = "ERP318 业务员",
                Status = DocumentStatus.Pending,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.ProformaInvoices.AnyAsync(p => !p.IsDeleted && p.PiNo == "ERP318-PI-1"))
        {
            var p = new ProformaInvoice
            {
                PiNo = "ERP318-PI-1",
                PiDate = DateTime.Today,
                CustomerId = customer1Id,
                CustomerName = "ERP318 分配客户",
                QuotationNo = "ERP318-QT-EUR",
                Currency = Currency.EUR,
                ExchangeRate = 1,
                TotalAmount = 300m,
                TotalAmountCny = 300m,
                DepositRatio = 30m,
                DepositAmount = 90m,
                SalesmanName = "ERP318 业务员",
                Status = DocumentStatus.Pending,
            };
            db.ProformaInvoices.Add(p);
            await db.SaveChangesAsync();
            db.ProformaInvoiceDetails.Add(new ProformaInvoiceDetail { PiId = p.Id, PiNo = p.PiNo, SortNo = 1, ProductCode = "ERP318-P-3", ProductName = "ERP318 商品三", Spec = "标准", Unit = "BOX", Quantity = 5m, UnitPrice = 60m, Amount = 300m });
            await db.SaveChangesAsync();
        }
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

    private static async Task<SysUser> EnsureUserAsync(ErpDbContext db, string userName, string displayName)
    {
        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == userName && !u.IsDeleted);
        if (user is not null)
            return user;

        user = new SysUser { UserName = userName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = displayName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SysRole> EnsureRoleAsync(ErpDbContext db, string roleCode, bool isSystem)
    {
        var role = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == roleCode && !r.IsDeleted);
        if (role is not null)
            return role;

        role = new SysRole { RoleName = roleCode, RoleCode = roleCode, IsSystem = isSystem };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task EnsureUserRoleAsync(ErpDbContext db, long userId, long roleId)
    {
        if (await db.SysUserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId && !ur.IsDeleted))
            return;

        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureRoleMenuAsync(ErpDbContext db, long roleId, string code)
    {
        var menu = await EnsureMenuAsync(db, code);
        if (await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
            return;

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        await db.SaveChangesAsync();
    }

    private static async Task<BaseEmployee> EnsureEmployeeAsync(ErpDbContext db, string code)
    {
        var employee = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == code && !e.IsDeleted);
        if (employee is not null)
            return employee;

        employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            Department = "销售部",
            Position = "业务员",
            IsSalesman = true,
            Status = 1,
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<BaseCustomer> EnsureCustomerAsync(ErpDbContext db, string code, string name, long? empId)
    {
        var customer = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == code && !c.IsDeleted);
        if (customer is not null)
            return customer;

        customer = new BaseCustomer { CustomerCode = code, CustomerName = name, EmpId = empId, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }
}





