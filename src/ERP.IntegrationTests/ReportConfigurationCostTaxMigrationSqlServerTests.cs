using ERP.Application.Common;
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
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-305 采购成本 / 退税汇总迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （供应商 + 采购订单 + 退税台账），通过受控数据集适配器预览并与既有报表服务逐行比对，验证混合币种在聚合前
/// 拒绝、受限制账号 fail closed 数据范围边界，以及通用 Excel / PDF 导出可执行。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationCostTaxMigrationSqlServerTests
    : IClassFixture<ReportConfigurationCostTaxMigrationSqlServerFixture>
{
    private readonly ReportConfigurationCostTaxMigrationSqlServerFixture _fixture;

    public ReportConfigurationCostTaxMigrationSqlServerTests(ReportConfigurationCostTaxMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationCostTaxMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationCostTaxMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 采购成本与退税汇总_SQLServer预览与既有报表一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);
        var userId = _fixture.PrivilegedUserId;

        var purchaseProvider = new PurchaseCostReportConfigurationDatasetProvider(reportService, db);
        var purchaseLegacy = await reportService.GetPurchaseCostAsync(
            ReportConfigurationCostTaxMigrationSqlServerFixture.UniformDate,
            ReportConfigurationCostTaxMigrationSqlServerFixture.UniformDate);
        var purchasePreview = await purchaseProvider.PreviewAsync(
            PurchaseCostDefinition(ReportConfigurationCostTaxMigrationSqlServerFixture.UniformDate),
            Params(), userId);

        Assert.Equal(purchaseLegacy.Count, purchasePreview.Total);
        var purchaseRow = Assert.Single(purchasePreview.Rows);
        Assert.Equal("ERP305 供应商甲", (string)purchaseRow["supplierName"]!);
        Assert.Equal(2, (int)purchaseRow["orderCount"]!);
        Assert.Equal(150m, (decimal)purchaseRow["totalAmount"]!);
        Assert.Equal(75m, (decimal)purchaseRow["avgAmount"]!);

        var taxProvider = new TaxRefundSummaryReportConfigurationDatasetProvider(reportService, db);
        var taxLegacy = await reportService.GetTaxRefundSummaryAsync();
        var taxPreview = await taxProvider.PreviewAsync(TaxRefundDefinition(), Params(), userId);

        Assert.Equal(taxLegacy.Count, taxPreview.Total);
        var taxRow = Assert.Single(taxPreview.Rows);
        Assert.Equal("2026-08", (string)taxRow["refundPeriod"]!);
        Assert.Equal(2, (int)taxRow["recordCount"]!);
        Assert.Equal(150m, (decimal)taxRow["exportAmount"]!);
    }

    [Fact]
    public async Task 采购成本_混合币种_SQLServer聚合前拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;
        var provider = new PurchaseCostReportConfigurationDatasetProvider(new ReportService(db), db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            PurchaseCostDefinition(ReportConfigurationCostTaxMigrationSqlServerFixture.MixedDate),
            Params(), userId));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("供应商#", ex.Message);
    }

    [Fact]
    public async Task 受限制用户_SQLServer数据集不暴露且预览拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedRestrictedUserAsync(db, "ERP305-RESTRICTED");

        var purchaseProvider = new PurchaseCostReportConfigurationDatasetProvider(new ReportService(db), db);
        Assert.Null(await purchaseProvider.GetDatasetAsync(userId));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => purchaseProvider.PreviewAsync(
            PurchaseCostDefinition(ReportConfigurationCostTaxMigrationSqlServerFixture.UniformDate),
            Params(), userId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        var taxProvider = new TaxRefundSummaryReportConfigurationDatasetProvider(new ReportService(db), db);
        Assert.Null(await taxProvider.GetDatasetAsync(userId));
    }

    [Fact]
    public async Task 通用Excel与PDF导出_可执行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);
        var userId = _fixture.PrivilegedUserId;

        var purchaseProvider = new PurchaseCostReportConfigurationDatasetProvider(reportService, db);
        var purchasePreview = await purchaseProvider.PreviewAsync(
            PurchaseCostDefinition(ReportConfigurationCostTaxMigrationSqlServerFixture.UniformDate),
            Params(), userId);
        var excel = new ReportConfigurationExcelExporter().Build(purchasePreview);
        Assert.NotEmpty(excel);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(excel, 0, 2));

        var taxProvider = new TaxRefundSummaryReportConfigurationDatasetProvider(reportService, db);
        var taxPreview = await taxProvider.PreviewAsync(TaxRefundDefinition(), Params(), userId);
        var taxExcel = new ReportConfigurationExcelExporter().Build(taxPreview);
        Assert.NotEmpty(taxExcel);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationPdfExporter.Export(taxPreview, fontPath);
            Assert.NotEmpty(pdf);
        }
    }

    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, string userName)
    {
        // 幂等：专用 localdb 夹具跨测试运行持续存在，重复执行时复用既有受限制用户，绝不重复插入同名用户。
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

        foreach (var code in new[] { "purchase-cost", "tax-refund-summary" })
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }

            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        return user.Id;
    }

    private static ReportConfigurationDefinition PurchaseCostDefinition(DateTime date)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetPurchaseCost,
            Fields = new List<string> { "supplierName", "supplierType", "orderCount", "totalAmount", "avgAmount", "lastOrderDate" },
            Filters = new List<ReportConfigurationFilter>
            {
                new() { FieldKey = "orderDate", Operator = ReportConfigurationConstants.OperatorEq, Value = date.ToString("yyyy-MM-dd") },
            },
        };

    private static ReportConfigurationDefinition TaxRefundDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetTaxRefundSummary,
            Fields = new List<string> { "refundPeriod", "recordCount", "declaredCount", "refundedCount", "exportAmount", "refundableAmount", "refundedAmount", "unrefundedAmount" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);
}


/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具
/// （供应商 + 采购订单 + 退税台账）。写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationCostTaxMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP305";
    public static readonly DateTime UniformDate = new(2026, 9, 1);
    public static readonly DateTime MixedDate = new(2026, 9, 15);

    private const string UserName = "ERP305-ADMIN";
    private const string RoleCode = "ERP305-SYS";
    private const string SupplierUniformCode = "ERP305-S1";
    private const string SupplierMixedCode = "ERP305-S2";

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

        foreach (var code in new[] { "purchase-cost", "tax-refund-summary" })
        {
            var menu = await EnsureMenuAsync(db, code);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        await SeedPurchaseCostAsync(db);
        await SeedTaxRefundAsync(db);

        PrivilegedUserId = user.Id;
    }

    private static async Task SeedPurchaseCostAsync(ErpDbContext db)
    {
        var uniform = await EnsureSupplierAsync(db, SupplierUniformCode, "ERP305 供应商甲");
        var mixed = await EnsureSupplierAsync(db, SupplierMixedCode, "ERP305 供应商乙");

        await EnsurePurchaseOrderAsync(db, "ERP305-PO-U1", uniform.Id, UniformDate, Currency.CNY, 100m);
        await EnsurePurchaseOrderAsync(db, "ERP305-PO-U2", uniform.Id, UniformDate, Currency.CNY, 50m);
        await EnsurePurchaseOrderAsync(db, "ERP305-PO-M1", mixed.Id, MixedDate, Currency.CNY, 100m);
        await EnsurePurchaseOrderAsync(db, "ERP305-PO-M2", mixed.Id, MixedDate, Currency.USD, 50m);
    }

    private static async Task SeedTaxRefundAsync(ErpDbContext db)
    {
        await EnsureTaxRefundAsync(db, "ERP305-TR-1", "2026-08", "USD", 100m, 13m, 5m, "已申报");
        await EnsureTaxRefundAsync(db, "ERP305-TR-2", "2026-08", "USD", 50m, 6m, 0m, "待申报");
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

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP305 隔离账号", Status = UserStatus.Enabled };
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

    private static async Task<BaseSupplier> EnsureSupplierAsync(ErpDbContext db, string code, string name)
    {
        var supplier = await db.BaseSuppliers.FirstOrDefaultAsync(s => s.SupplierCode == code && !s.IsDeleted);
        if (supplier is not null)
            return supplier;

        supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static async Task EnsurePurchaseOrderAsync(ErpDbContext db, string orderNo, long supplierId, DateTime orderDate, Currency currency, decimal totalAmount)
    {
        if (await db.PurchaseOrders.AnyAsync(o => o.OrderNo == orderNo && !o.IsDeleted))
            return;

        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate,
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureTaxRefundAsync(ErpDbContext db, string refundNo, string period, string currency, decimal exportAmount, decimal refundableAmount, decimal refundedAmount, string status)
    {
        if (await db.BaseTaxRefunds.AnyAsync(r => r.RefundNo == refundNo && !r.IsDeleted))
            return;

        db.BaseTaxRefunds.Add(new BaseTaxRefund
        {
            RefundNo = refundNo,
            RefundPeriod = period,
            Currency = currency,
            ExportAmount = exportAmount,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            Status = status,
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

