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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-332 迁移 parity 真实四维证据的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （客户 + 业务员 + 商品 + 已审核销售订单 + 已审核销售出库），通过真实证据服务在同一有界夹具上
/// 运行旧来源接缝与通用数据集预览并比对，断言代表条目（固定 / 动态）真正到达 parity-passed，
/// 权限撤销 / 未知键 fail closed，其他账号配置不变。
/// <para>安全口径：写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c>、集成安全）；连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class ReportMigrationParityEvidenceSqlServerTests
    : IClassFixture<ReportMigrationParityEvidenceSqlServerFixture>
{
    private readonly ReportMigrationParityEvidenceSqlServerFixture _fixture;

    public ReportMigrationParityEvidenceSqlServerTests(ReportMigrationParityEvidenceSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportMigrationParityEvidenceSqlServerFixture.InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportMigrationParityEvidenceSqlServerFixture.DatabasePrefix, builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
    }

    [Fact]
    public async Task 动态销售订单_真实四维证据_parity_passed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var evidence = await BuildService(db).GetEvidenceAsync("dynamic:sales-order", _fixture.PrivilegedUserId);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Complete);
        Assert.True(evidence.DataGrainMatched);
        Assert.True(evidence.CurrencyUnitMatched);
        Assert.True(evidence.PermissionsMatched);
        Assert.True(evidence.OutputSemanticsMatched);
    }

    [Fact]
    public async Task 商品销量排名_真实四维证据_parity_passed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var evidence = await BuildService(db).GetEvidenceAsync("report:product-sales-ranking", _fixture.PrivilegedUserId);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Complete);
    }

    [Fact]
    public async Task 无菜单账号_旧来源被拒绝_证据null()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var user = new SysUser
        {
            UserName = "ERP332-NOMENU-" + Guid.NewGuid().ToString("N"),
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP332 无菜单账号",
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        try
        {
            var evidence = await BuildService(db).GetEvidenceAsync("dynamic:sales-order", user.Id);
            Assert.Null(evidence);
        }
        finally
        {
            Guard();
            db.SysUsers.Remove(user);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 未知旧报表键_证据null()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var evidence = await BuildService(db).GetEvidenceAsync("unknown:legacy-key", _fixture.PrivilegedUserId);

        Assert.Null(evidence);
    }

    private IReportMigrationParityEvidenceService BuildService(ErpDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _fixture.ConnectionString,
            })
            .Build();
        var billReader = new LegacyBillExportReadService(new StoredProcedureService(config));

        var reportService = new ReportService(db);
        var salesQuery = new DynamicSalesOrderReportQuery(db);
        var receivableQuery = new DynamicReceivableReportQuery(db);
        var purchaseQuery = new DynamicPurchaseOrderReportQuery(db);

        var legacySource = new LegacyReportSourceRegistry(
            db, reportService, salesQuery, receivableQuery, purchaseQuery, billReader);

        var providers = new IReportConfigurationDatasetProvider[]
        {
            new ProductSalesRankingReportConfigurationDatasetProvider(reportService, db),
            new SalesOrderReportConfigurationDatasetProvider(salesQuery, db),
        };

        return new ReportMigrationParityEvidenceService(
            legacySource,
            providers,
            new ReportMigrationParityComparator(),
            new ReportMigrationOutputSemanticsComparator(),
            new ReportConfigurationExecutionBudget(),
            new ReportMigrationParityEvidenceProvider());
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空业务夹具。
/// </summary>
public sealed class ReportMigrationParityEvidenceSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP332";

    private const string CustomerCode = "ERP332-C1";
    private const string UserName = "ERP332-ADMIN";
    private const string RoleCode = "ERP332-SYS";
    private const string EmployeeCode = "ERP332-E1";
    private const string ProductCode = "ERP332-P1";

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

        var menuSalesOrder = await EnsureMenuAsync(db, "sales-order");
        var menuProductRanking = await EnsureMenuAsync(db, "product-sales-ranking");

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in new[] { menuSalesOrder, menuProductRanking })
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var customer = await EnsureCustomerAsync(db);
        var employee = await EnsureEmployeeAsync(db);
        var product = await EnsureProductAsync(db);

        if (!await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP332-SO-1" && !o.IsDeleted))
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = "ERP332-SO-1",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                SalesmanId = employee.Id,
                Currency = Currency.USD,
                TotalAmount = 1200m,
                Status = DocumentStatus.Approved,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.StockOuts.AnyAsync(o => o.StockOutNo == "ERP332-OUT-1" && !o.IsDeleted))
        {
            var stockOut = new StockOut
            {
                StockOutNo = "ERP332-OUT-1",
                StockOutDate = DateTime.Today,
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
            };
            db.StockOuts.Add(stockOut);
            await db.SaveChangesAsync();

            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = product.Id,
                ProductName = "ERP332 商品",
                Spec = "标准",
                Unit = "PCS",
                Quantity = 30m,
            });
            await db.SaveChangesAsync();
        }

        PrivilegedUserId = user.Id;
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

        user = new SysUser
        {
            UserName = UserName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP332 隔离账号",
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

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP332 客户", Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseEmployee> EnsureEmployeeAsync(ErpDbContext db)
    {
        var employee = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == EmployeeCode && !e.IsDeleted);
        if (employee is not null)
            return employee;

        employee = new BaseEmployee { EmployeeCode = EmployeeCode, EmployeeName = "ERP332 业务员", IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<BaseProduct> EnsureProductAsync(ErpDbContext db)
    {
        var product = await db.BaseProducts.FirstOrDefaultAsync(p => p.ProductCode == ProductCode && !p.IsDeleted);
        if (product is not null)
            return product;

        product = new BaseProduct
        {
            ProductCode = ProductCode,
            ProductName = "ERP332 商品",
            Spec = "标准",
            Unit = "PCS",
            SalePrice = 40m,
            Status = 1,
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
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

