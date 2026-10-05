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
/// ERP-300 商品销量排名 / 订单利润暂估 / 业务员提成迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种
/// 非空商业夹具（客户 + 业务员 + 商品 + 销售出库明细 + 销售订单），通过受控数据集适配器预览并与既有报表服务逐行比对。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationCommercialMigrationSqlServerTests
    : IClassFixture<ReportConfigurationCommercialMigrationSqlServerFixture>
{
    private readonly ReportConfigurationCommercialMigrationSqlServerFixture _fixture;

    public ReportConfigurationCommercialMigrationSqlServerTests(ReportConfigurationCommercialMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationCommercialMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationCommercialMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 商品销量排名_订单利润暂估_业务员提成_SQLServer预览与既有报表逐行一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);
        var scope = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };

        var rankingProvider = new ProductSalesRankingReportConfigurationDatasetProvider(reportService, db);
        var rankingLegacy = await reportService.GetProductSalesRankingAsync(DateTime.Today, DateTime.Today, 200, scope);
        var rankingPreview = await rankingProvider.PreviewAsync(RankingDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(rankingLegacy.Count, rankingPreview.Total);
        Assert.Equal(rankingLegacy.Count, rankingPreview.Rows.Count);
        foreach (var legacyRow in rankingLegacy)
        {
            var row = Assert.Single(rankingPreview.Rows, r => (long)r["productId"]! == legacyRow.ProductId && (string)r["unit"]! == legacyRow.Unit);
            Assert.Equal(legacyRow.TotalQuantity, (decimal)row["totalQuantity"]!);
            Assert.Equal(legacyRow.TotalAmount, (decimal)row["totalAmount"]!);
        }

        var profitProvider = new OrderProfitEstimateReportConfigurationDatasetProvider(reportService, db);
        var profitLegacy = await reportService.GetOrderProfitEstimateAsync(DateTime.Today, DateTime.Today, scope);
        var profitPreview = await profitProvider.PreviewAsync(ProfitDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(profitLegacy.Count, profitPreview.Total);
        Assert.Equal(profitLegacy.Count, profitPreview.Rows.Count);
        foreach (var legacyRow in profitLegacy)
        {
            var row = Assert.Single(profitPreview.Rows, r => (string)r["orderNo"]! == legacyRow.OrderNo);
            Assert.Equal(legacyRow.Currency, (string)row["currency"]!);
            Assert.Equal(legacyRow.SalesAmount, (decimal)row["salesAmount"]!);
            Assert.Null(row["costAmount"]);
            Assert.Null(row["profit"]);
            Assert.Null(row["profitRate"]);
        }

        var commissionProvider = new SalesCommissionReportConfigurationDatasetProvider(reportService, db);
        var commissionLegacy = await reportService.GetSalesCommissionAsync(DateTime.Today, DateTime.Today, scope);
        var commissionPreview = await commissionProvider.PreviewAsync(CommissionDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(commissionLegacy.Count, commissionPreview.Total);
        Assert.Equal(commissionLegacy.Count, commissionPreview.Rows.Count);
        foreach (var legacyRow in commissionLegacy)
        {
            var row = Assert.Single(commissionPreview.Rows, r => (string)r["currency"]! == legacyRow.Currency && (long?)r["salesmanId"] == legacyRow.SalesmanId);
            Assert.Equal(legacyRow.OrderCount, (int)row["orderCount"]!);
            Assert.Equal(legacyRow.SalesAmount, (decimal?)row["salesAmount"]);
            Assert.Null(row["profit"]);
            Assert.Null(row["commissionAmount"]);
        }
    }

    private static ReportConfigurationDefinition RankingDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetProductSalesRanking,
            Fields = new List<string> { "rank", "productId", "productCode", "productName", "spec", "unit", "totalQuantity", "totalAmount" },
        };

    private static ReportConfigurationDefinition ProfitDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetOrderProfit,
            Fields = new List<string> { "orderNo", "currency", "salesAmount", "costAmount", "profit", "profitRate" },
        };

    private static ReportConfigurationDefinition CommissionDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesCommission,
            Fields = new List<string> { "salesmanId", "salesmanName", "currency", "orderCount", "salesAmount", "profit", "commissionAmount" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 200, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空商业夹具。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationCommercialMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP300";

    private const string CustomerCode = "ERP300-C1";
    private const string EmployeeCode = "ERP300-S1";
    private const string ProductCode = "ERP300-P1";
    private const string UserName = "ERP300-ADMIN";
    private const string RoleCode = "ERP300-SYS";

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

        await SeedCommercialFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private async Task SeedCommercialFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var menuRanking = await EnsureMenuAsync(db, "product-sales-ranking", "商品销量排名榜");
        var menuProfit = await EnsureMenuAsync(db, "order-profit", "订单利润暂估表");
        var menuCommission = await EnsureMenuAsync(db, "sales-commission", "业务员提成表");

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id && !ur.IsDeleted))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in new[] { menuRanking, menuProfit, menuCommission })
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var customer = await EnsureCustomerAsync(db);
        var employee = await EnsureEmployeeAsync(db);
        var product = await EnsureProductAsync(db);

        await EnsureStockOutAsync(db, customer.Id, product.Id);

        if (!await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP300-SO-USD" && !o.IsDeleted))
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = "ERP300-SO-USD",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                SalesmanId = employee.Id,
                Currency = Currency.USD,
                TotalAmount = 120m,
                Status = DocumentStatus.Approved,
            });
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = "ERP300-SO-EUR",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                SalesmanId = employee.Id,
                Currency = Currency.EUR,
                TotalAmount = 80m,
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
            DisplayName = "ERP300 隔离账号",
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

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP300 客户", Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseEmployee> EnsureEmployeeAsync(ErpDbContext db)
    {
        var employee = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == EmployeeCode && !e.IsDeleted);
        if (employee is not null)
            return employee;

        employee = new BaseEmployee { EmployeeCode = EmployeeCode, EmployeeName = "ERP300 业务员", IsSalesman = true, Status = 1 };
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
            ProductName = "ERP300 商品",
            Spec = "标准",
            Unit = "PCS",
            SalePrice = 10m,
            Status = 1,
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task EnsureStockOutAsync(ErpDbContext db, long customerId, long productId)
    {
        if (await db.StockOuts.AnyAsync(o => o.StockOutNo == "ERP300-OUT" && !o.IsDeleted))
            return;

        var stockOut = new StockOut
        {
            StockOutNo = "ERP300-OUT",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
        };
        db.StockOuts.Add(stockOut);
        await db.SaveChangesAsync();

        db.StockOutDetails.Add(new StockOutDetail
        {
            StockOutId = stockOut.Id,
            ProductId = productId,
            ProductName = "ERP300 商品",
            Spec = "标准",
            Unit = "PCS",
            Quantity = 30m,
        });
        await db.SaveChangesAsync();
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
