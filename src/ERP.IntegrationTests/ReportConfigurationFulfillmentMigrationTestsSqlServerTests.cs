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
/// ERP-301 柜量统计 / 客户出货量 / 出货财务进度迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种
/// 非空夹具（客户 + 装柜清单 + 销售订单 + 出货明细 + 定金 / 货款申请），通过受控数据集适配器预览并与既有报表服务逐行比对。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationFulfillmentMigrationSqlServerTests
    : IClassFixture<ReportConfigurationFulfillmentMigrationSqlServerFixture>
{
    private readonly ReportConfigurationFulfillmentMigrationSqlServerFixture _fixture;

    public ReportConfigurationFulfillmentMigrationSqlServerTests(ReportConfigurationFulfillmentMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationFulfillmentMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationFulfillmentMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 柜量统计_客户出货量_出货财务进度_SQLServer预览与既有报表逐行一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);
        var scope = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };

        var containerProvider = new ContainerStatsReportConfigurationDatasetProvider(reportService, db);
        var containerLegacy = await reportService.GetContainerStatsAsync(DateTime.Today, DateTime.Today, scope);
        var containerPreview = await containerProvider.PreviewAsync(ContainerDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(containerLegacy.Count, containerPreview.Total);
        Assert.Equal(containerLegacy.Count, containerPreview.Rows.Count);
        foreach (var legacyRow in containerLegacy)
        {
            var row = Assert.Single(containerPreview.Rows, r =>
                (string)r["containerNo"]! == legacyRow.ContainerNo && (DateTime)r["loadingDate"]! == legacyRow.LoadingDate);
            Assert.Equal(legacyRow.TotalCartons, (decimal)row["totalCartons"]!);
            Assert.Equal(legacyRow.TotalWeight, (decimal)row["totalWeight"]!);
            Assert.Equal(legacyRow.TotalVolume, (decimal)row["totalVolume"]!);
        }

        var shipmentProvider = new CustomerShipmentReportConfigurationDatasetProvider(reportService, db);
        var shipmentLegacy = await reportService.GetCustomerShipmentStatsAsync(DateTime.Today, DateTime.Today, scope);
        var shipmentPreview = await shipmentProvider.PreviewAsync(ShipmentDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(shipmentLegacy.Count, shipmentPreview.Total);
        Assert.Equal(shipmentLegacy.Count, shipmentPreview.Rows.Count);
        foreach (var legacyRow in shipmentLegacy)
        {
            var row = Assert.Single(shipmentPreview.Rows, r => (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.TotalAmount, (decimal?)row["totalAmount"]);
            Assert.Equal(legacyRow.TotalQuantity, (decimal?)row["totalQuantity"]);
        }

        var financeProvider = new ShipmentFinanceReportConfigurationDatasetProvider(db);
        var financePreview = await financeProvider.PreviewAsync(FinanceDefinition(), Params(), _fixture.PrivilegedUserId);

        Assert.Equal(2, financePreview.Total);
        var linkedRow = Assert.Single(financePreview.Rows, r => (string)r["orderNo"]! == "ERP301-SO-USD");
        Assert.Equal("linked", (string)linkedRow["financeLinkStatus"]!);
        Assert.Equal(500m, (decimal)linkedRow["linkedAmount"]!);
        var unlinkedRow = Assert.Single(financePreview.Rows, r => (string)r["orderNo"]! == "ERP301-SO-EUR");
        Assert.Equal("unlinked", (string)unlinkedRow["financeLinkStatus"]!);
        Assert.Null(unlinkedRow["linkedAmount"]);
    }

    private static ReportConfigurationDefinition ContainerDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetContainerStats,
            Fields = new List<string> { "loadingDate", "containerNo", "loadingListCount", "totalCartons", "totalWeight", "totalVolume", "utilizationType" },
        };

    private static ReportConfigurationDefinition ShipmentDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetCustomerShipment,
            Fields = new List<string> { "customerName", "currency", "orderCount", "totalAmount", "totalQuantity" },
        };

    private static ReportConfigurationDefinition FinanceDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetShipmentFinance,
            Fields = new List<string> { "orderNo", "currency", "orderAmount", "financeLinkStatus", "linkedAmount", "uncoveredAmount" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 200, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具。
/// 写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationFulfillmentMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP301";

    private const string CustomerCode = "ERP301-C1";
    private const string UserName = "ERP301-ADMIN";
    private const string RoleCode = "ERP301-SYS";

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

        var menuContainer = await EnsureMenuAsync(db, "container-stats", "柜量与装柜利用率统计");
        var menuShipment = await EnsureMenuAsync(db, "customer-shipment", "客户出货量统计表");
        var menuSales = await EnsureMenuAsync(db, "sales-order", "销售订单");

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in new[] { menuContainer, menuShipment, menuSales })
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var customer = await EnsureCustomerAsync(db);

        if (!await db.ContainerLoadingLists.AnyAsync(l => l.LoadingListNo == "ERP301-ZL-1" && !l.IsDeleted))
        {
            db.ContainerLoadingLists.Add(new ContainerLoadingList
            {
                LoadingListNo = "ERP301-ZL-1",
                LoadingDate = DateTime.Today,
                ContainerNo = "ERP301-CNTR",
                CustomerId = customer.Id,
                TotalCartons = 10m,
                TotalWeight = 500m,
                TotalVolume = 30m,
                Status = DocumentStatus.Approved,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP301-SO-USD" && !o.IsDeleted))
        {
            var usd = new SalesOrder
            {
                OrderNo = "ERP301-SO-USD",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                Currency = Currency.USD,
                TotalAmount = 1000m,
                Status = DocumentStatus.Approved,
            };
            var eur = new SalesOrder
            {
                OrderNo = "ERP301-SO-EUR",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                Currency = Currency.EUR,
                TotalAmount = 800m,
                Status = DocumentStatus.Approved,
            };
            db.SalesOrders.AddRange(usd, eur);
            await db.SaveChangesAsync();

            db.SalesOrderDetails.AddRange(
                new SalesOrderDetail { SalesOrderId = usd.Id, ProductId = 1, ProductName = "商品1", Spec = "规格A", Unit = "PCS", Quantity = 10m },
                new SalesOrderDetail { SalesOrderId = eur.Id, ProductId = 2, ProductName = "商品2", Spec = "规格A", Unit = "PCS", Quantity = 5m });
            await db.SaveChangesAsync();

            var stockOut = new StockOut
            {
                StockOutNo = "ERP301-CK-1",
                StockOutDate = DateTime.Today,
                SalesOrderId = usd.Id,
                Status = DocumentStatus.Approved,
            };
            db.StockOuts.Add(stockOut);
            await db.SaveChangesAsync();
            db.StockOutDetails.Add(new StockOutDetail { StockOutId = stockOut.Id, ProductId = 1, ProductName = "商品1", Quantity = 4m });
            await db.SaveChangesAsync();

            db.FinanceDepositApplies.Add(new FinanceDepositApply
            {
                ApplyNo = "ERP301-DK-1",
                ApplyDate = DateTime.Today,
                SalesOrderId = usd.Id,
                Amount = 300m,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
            });
            db.FinancePaymentApplies.Add(new FinancePaymentApply
            {
                ApplyNo = "ERP301-HK-1",
                ApplyDate = DateTime.Today,
                SalesOrderId = usd.Id,
                Amount = 200m,
                Currency = Currency.USD,
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
            DisplayName = "ERP301 隔离账号",
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

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP301 客户", Status = 1, CreditStatus = "正常" };
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
