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
/// ERP-304 采购 + 财务家族预设的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （仓库 + 商品 + 客户 + 供应商 + 库存 + 销售订单 + 采购订单 + 收款 + 付款），通过受控数据集适配器预览并与既有
/// 财务报表服务逐行比对，同时验证受限制账号在财务数据集上的 fail closed 数据范围边界。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationProcurementFinancialPresetSqlServerTests
    : IClassFixture<ReportConfigurationProcurementFinancialPresetSqlServerFixture>
{
    private readonly ReportConfigurationProcurementFinancialPresetSqlServerFixture _fixture;

    public ReportConfigurationProcurementFinancialPresetSqlServerTests(ReportConfigurationProcurementFinancialPresetSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationProcurementFinancialPresetSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationProcurementFinancialPresetSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 七个数据集_SQLServer预览与既有报表一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);
        var userId = _fixture.PrivilegedUserId;

        var balanceProvider = new BalanceSheetReportConfigurationDatasetProvider(reportService, db);
        var balanceLegacy = await reportService.GetBalanceSheetAsync(DateTime.Today);
        var balancePreview = await balanceProvider.PreviewAsync(BalanceSheetDefinition(), Params(), userId);
        Assert.Equal(balanceLegacy.Lines.Count, balancePreview.Rows.Count);
        foreach (var line in balanceLegacy.Lines)
        {
            var row = Assert.Single(balancePreview.Rows, r => (string)r["lineName"]! == line.Name);
            Assert.Equal(line.Amount, (decimal)row["amount"]!);
        }

        var incomeProvider = new IncomeStatementReportConfigurationDatasetProvider(reportService, db);
        var incomeLegacy = await reportService.GetIncomeStatementAsync(DateTime.MinValue, DateTime.Today);
        var incomePreview = await incomeProvider.PreviewAsync(IncomeStatementDefinition(), Params(), userId);
        Assert.Equal(incomeLegacy.Lines.Count, incomePreview.Rows.Count);
        foreach (var line in incomeLegacy.Lines)
        {
            var row = Assert.Single(incomePreview.Rows, r => (string)r["lineName"]! == line.Name);
            Assert.Equal(line.Amount, (decimal)row["amount"]!);
        }

        var cashFlowProvider = new CashFlowReportConfigurationDatasetProvider(reportService, db);
        var cashFlowLegacy = await reportService.GetCashFlowStatementAsync(DateTime.MinValue, DateTime.Today);
        var cashFlowPreview = await cashFlowProvider.PreviewAsync(CashFlowDefinition(), Params(), userId);
        Assert.Equal(cashFlowLegacy.Lines.Count, cashFlowPreview.Rows.Count);
        foreach (var line in cashFlowLegacy.Lines)
        {
            var row = Assert.Single(cashFlowPreview.Rows, r => (string)r["lineName"]! == line.Name);
            Assert.Equal(line.Amount, (decimal)row["amount"]!);
        }

        var arProvider = new ArAgingReportConfigurationDatasetProvider(reportService, db);
        var arLegacy = await reportService.GetArAgingAsync(DateTime.Today);
        var arPreview = await arProvider.PreviewAsync(ArAgingDefinition(), Params(), userId);
        Assert.Equal(arLegacy.Count, arPreview.Total);
        Assert.Equal(arLegacy.Count, arPreview.Rows.Count);
        foreach (var item in arLegacy)
        {
            var row = Assert.Single(arPreview.Rows, r => (string)r["orderNo"]! == item.OrderNo);
            Assert.Equal(item.Balance, (decimal)row["balance"]!);
            Assert.Equal(item.CustomerName, (string)row["customerName"]!);
        }

        var purchaseProvider = new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db));
        var purchasePreview = await purchaseProvider.PreviewAsync(PurchaseOrderDefinition(), Params(), userId);
        Assert.Contains(purchasePreview.Rows, r => (string)r["orderNo"]! == ReportConfigurationProcurementFinancialPresetSqlServerFixture.PurchaseOrderNo);

        var supplierAgingProvider = new SupplierAgingReportConfigurationDatasetProvider(db);
        var agingPreview = await supplierAgingProvider.PreviewAsync(SupplierAgingDefinition(), Params(), userId);
        Assert.NotNull(agingPreview);

        var supplierExposureProvider = new SupplierExposureReportConfigurationDatasetProvider(db);
        var exposurePreview = await supplierExposureProvider.PreviewAsync(SupplierExposureDefinition(), Params(), userId);
        Assert.Contains(exposurePreview.Rows, r => (string)r["orderNo"]! == ReportConfigurationProcurementFinancialPresetSqlServerFixture.PurchaseOrderNo);
    }

    [Fact]
    public async Task 受限制用户_有财务菜单_SQLServer预览拒绝且不暴露()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var user = await SeedRestrictedUserAsync(db, "ERP304-RESTRICTED", "balance-sheet");

        var provider = new BalanceSheetReportConfigurationDatasetProvider(new ReportService(db), db);

        Assert.Null(await provider.GetDatasetAsync(user));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            BalanceSheetDefinition(), Params(), user));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, string userName, string menuCode)
    {
        var user = new SysUser { UserName = userName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = userName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = userName + "-role", RoleCode = userName + "-role", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static ReportConfigurationDefinition PurchaseOrderDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetPurchaseOrder,
            Fields = new List<string> { "orderNo", "orderDate", "supplierId", "currency", "totalAmount", "contractNo", "status", "remark" },
        };

    private static ReportConfigurationDefinition SupplierAgingDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSupplierAging,
            Fields = new List<string> { "invoiceNumber", "invoiceDate", "supplierName", "currency", "grossAmount", "dueDate", "agingBucket", "overdueDays", "remainingAmount", "note" },
        };

    private static ReportConfigurationDefinition SupplierExposureDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSupplierExposure,
            Fields = new List<string> { "orderNo", "orderDate", "supplierName", "currency", "orderedAmount", "linkStatus", "settledAmount", "outstandingAmount", "note" },
        };

    private static ReportConfigurationDefinition BalanceSheetDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetBalanceSheet,
            Fields = new List<string> { "lineName", "amount" },
        };

    private static ReportConfigurationDefinition IncomeStatementDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetIncomeStatement,
            Fields = new List<string> { "lineName", "amount" },
        };

    private static ReportConfigurationDefinition CashFlowDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetCashFlow,
            Fields = new List<string> { "lineName", "amount" },
        };

    private static ReportConfigurationDefinition ArAgingDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetArAging,
            Fields = new List<string> { "customerName", "orderNo", "currency", "orderAmount", "receivedAmount", "balance", "agingDays", "bucket", "status" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空采购 / 财务夹具。
/// 写库前每次断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationProcurementFinancialPresetSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP304";
    public const string PurchaseOrderNo = "ERP304-PO";

    private const string CustomerACode = "ERP304-C1";
    private const string CustomerBCode = "ERP304-C2";
    private const string SupplierCode = "ERP304-S1";
    private const string ProductCode = "ERP304-P1";
    private const string WarehouseCode = "ERP304-W1";
    private const string UserName = "ERP304-ADMIN";
    private const string RoleCode = "ERP304-SYS";

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

        var menuCodes = new[] { "purchase-order", "balance-sheet", "income-statement", "cash-flow", "ar-aging" };
        var menus = new List<SysMenu>();
        foreach (var code in menuCodes)
            menus.Add(await EnsureMenuAsync(db, code, code));

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id && !ur.IsDeleted))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in menus)
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var warehouse = await EnsureWarehouseAsync(db);
        var product = await EnsureProductAsync(db);
        var customerA = await EnsureCustomerAsync(db, CustomerACode, "ERP304 客户一");
        var customerB = await EnsureCustomerAsync(db, CustomerBCode, "ERP304 客户二");
        var supplier = await EnsureSupplierAsync(db);

        await EnsureStockAsync(db, warehouse.Id, product.Id, 5m);
        await EnsureSalesOrderAsync(db, customerA.Id);
        await EnsurePurchaseOrderAsync(db, supplier.Id);
        await EnsureFinanceReceiptAsync(db, customerB.Id);
        await EnsureFinancePaymentAsync(db, supplier.Id);

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

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP304 隔离账号", Status = UserStatus.Enabled };
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

    private static async Task<BaseWarehouse> EnsureWarehouseAsync(ErpDbContext db)
    {
        var warehouse = await db.BaseWarehouses.FirstOrDefaultAsync(w => w.WarehouseCode == WarehouseCode && !w.IsDeleted);
        if (warehouse is not null)
            return warehouse;

        warehouse = new BaseWarehouse { WarehouseCode = WarehouseCode, WarehouseName = "ERP304 主仓", Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse;
    }

    private static async Task<BaseProduct> EnsureProductAsync(ErpDbContext db)
    {
        var product = await db.BaseProducts.FirstOrDefaultAsync(p => p.ProductCode == ProductCode && !p.IsDeleted);
        if (product is not null)
            return product;

        product = new BaseProduct { ProductCode = ProductCode, ProductName = "ERP304 商品", Spec = "标准", Unit = "PCS", CostPrice = 100m, Status = 1 };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<BaseCustomer> EnsureCustomerAsync(ErpDbContext db, string code, string name)
    {
        var customer = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == code && !c.IsDeleted);
        if (customer is not null)
            return customer;

        customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", CreditDays = 30 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseSupplier> EnsureSupplierAsync(ErpDbContext db)
    {
        var supplier = await db.BaseSuppliers.FirstOrDefaultAsync(s => s.SupplierCode == SupplierCode && !s.IsDeleted);
        if (supplier is not null)
            return supplier;

        supplier = new BaseSupplier { SupplierCode = SupplierCode, SupplierName = "ERP304 供应商", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static async Task EnsureStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        if (await db.Stocks.AnyAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId && !s.IsDeleted))
            return;

        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureSalesOrderAsync(ErpDbContext db, long customerId)
    {
        if (await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP304-SO" && !o.IsDeleted))
            return;

        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "ERP304-SO",
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = 200m,
            Status = DocumentStatus.Approved,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsurePurchaseOrderAsync(ErpDbContext db, long supplierId)
    {
        if (await db.PurchaseOrders.AnyAsync(o => o.OrderNo == PurchaseOrderNo && !o.IsDeleted))
            return;

        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = PurchaseOrderNo,
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = 150m,
            Status = DocumentStatus.Approved,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureFinanceReceiptAsync(ErpDbContext db, long customerId)
    {
        if (await db.FinanceReceipts.AnyAsync(r => r.ReceiptNo == "ERP304-FR" && !r.IsDeleted))
            return;

        db.FinanceReceipts.Add(new FinanceReceipt
        {
            ReceiptNo = "ERP304-FR",
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureFinancePaymentAsync(ErpDbContext db, long supplierId)
    {
        if (await db.FinancePayments.AnyAsync(p => p.PaymentNo == "ERP304-FP" && !p.IsDeleted))
            return;

        db.FinancePayments.Add(new FinancePayment
        {
            PaymentNo = "ERP304-FP",
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            Amount = 300m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
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



