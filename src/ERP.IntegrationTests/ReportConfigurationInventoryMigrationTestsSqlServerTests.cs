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
/// ERP-299 库存移动 / 库存库龄 / 库存预警迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空库存夹具
/// （商品阈值 + 仓库 + 库存行 + 库存流水），通过受控数据集适配器预览并与既有固定报表服务逐行比对。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationInventoryMigrationSqlServerTests
    : IClassFixture<ReportConfigurationInventoryMigrationSqlServerFixture>
{
    private readonly ReportConfigurationInventoryMigrationSqlServerFixture _fixture;

    public ReportConfigurationInventoryMigrationSqlServerTests(ReportConfigurationInventoryMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationInventoryMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationInventoryMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 库存移动_库龄_预警_SQLServer预览与固定报表逐行一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reportService = new ReportService(db);

        var movementProvider = new InventoryMovementReportConfigurationDatasetProvider(reportService, db);
        var movementLegacy = await reportService.GetInventoryMovementReportAsync(new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = DateTime.Today,
            InactiveDays = 90,
            OnlyPositiveQuantity = true,
            Page = 1,
            PageSize = 200,
        });
        var movementPreview = await movementProvider.PreviewAsync(
            MovementDefinition(), Params(200), _fixture.PrivilegedUserId);

        Assert.Equal(movementLegacy.Total, movementPreview.Total);
        Assert.Equal(movementLegacy.Items.Count, movementPreview.Rows.Count);
        foreach (var legacyRow in movementLegacy.Items)
        {
            var row = Assert.Single(movementPreview.Rows, r => (long)r["productId"]! == legacyRow.ProductId);
            Assert.Equal(legacyRow.CurrentQuantity, (decimal)row["currentQuantity"]!);
            Assert.Equal(legacyRow.InboundQuantity, (decimal)row["inboundQuantity"]!);
            Assert.Equal(legacyRow.OutboundQuantity, (decimal)row["outboundQuantity"]!);
            Assert.Equal(legacyRow.Classification, (string)row["classification"]!);
        }

        var agingProvider = new InventoryAgingReportConfigurationDatasetProvider(reportService, db);
        var agingLegacy = await reportService.GetInventoryAgingReportAsync(new ReportDtos.InventoryAgingReportQuery
        {
            AsOfDate = DateTime.Today,
            OnlyPositiveQuantity = true,
            Page = 1,
            PageSize = 200,
        });
        var agingPreview = await agingProvider.PreviewAsync(
            AgingDefinition(), Params(200), _fixture.PrivilegedUserId);

        Assert.Equal(agingLegacy.Total, agingPreview.Total);
        Assert.Equal(agingLegacy.Items.Count, agingPreview.Rows.Count);
        foreach (var legacyRow in agingLegacy.Items)
        {
            var row = Assert.Single(agingPreview.Rows, r => (long)r["productId"]! == legacyRow.ProductId);
            Assert.Equal(legacyRow.CurrentQuantity, (decimal)row["currentQuantity"]!);
            Assert.Equal(legacyRow.CostStatus, (string)row["costStatus"]!);
            Assert.Equal(legacyRow.AuthoritativeAmount, (decimal?)row["authoritativeAmount"]);
        }

        var alertProvider = new StockAlertReportConfigurationDatasetProvider(reportService, db);
        var alertLegacy = await reportService.GetStockAlertAsync();
        var alertPreview = await alertProvider.PreviewAsync(
            AlertDefinition(), Params(100), _fixture.PrivilegedUserId);

        Assert.Equal(alertLegacy.Count, alertPreview.Total);
        Assert.Equal(alertLegacy.Count, alertPreview.Rows.Count);
        foreach (var legacyRow in alertLegacy)
        {
            var row = Assert.Single(alertPreview.Rows, r => (string)r["productName"]! == legacyRow.ProductName);
            Assert.Equal(legacyRow.Quantity, (decimal)row["quantity"]!);
            Assert.Equal(legacyRow.AlertLevel, (string)row["alertLevel"]!);
        }
    }

    private static ReportConfigurationDefinition MovementDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetInventoryMovement,
            Fields = new List<string> { "warehouseId", "productId", "productCode", "productName", "unit", "currentQuantity", "inboundQuantity", "outboundQuantity", "netQuantity", "classification" },
        };

    private static ReportConfigurationDefinition AgingDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetInventoryAging,
            Fields = new List<string> { "warehouseId", "productId", "productName", "unit", "currentQuantity", "knownAgedQuantity", "unknownAgeQuantity", "evidenceStatus", "costStatus", "authoritativeAmount", "agedAmount", "unknownAgeAmount" },
        };

    private static ReportConfigurationDefinition AlertDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetStockAlert,
            Fields = new List<string> { "productName", "spec", "unit", "warehouseName", "quantity", "minStock", "maxStock", "diff", "alertLevel" },
        };

    private static ReportConfigurationPreviewParameters Params(int pageSize)
        => new(1, pageSize, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空库存夹具。
/// 写库前每次断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationInventoryMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP299";

    private const string ProductACode = "ERP299-P1";
    private const string ProductBCode = "ERP299-P2";
    private const string WarehouseCode = "ERP299-W1";
    private const string UserName = "ERP299-ADMIN";
    private const string RoleCode = "ERP299-SYS";

    public string ConnectionString { get; private set; } = null!;
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

        await SeedInventoryFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private async Task SeedInventoryFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var menuQuery = await db.SysMenus.Where(m => m.MenuCode == "stock-query" && !m.IsDeleted).FirstOrDefaultAsync();
        if (menuQuery is null)
        {
            menuQuery = new SysMenu { MenuName = "库存查询", MenuCode = "stock-query", MenuType = MenuType.Menu };
            db.SysMenus.Add(menuQuery);
            await db.SaveChangesAsync();
        }

        var menuAlert = await db.SysMenus.Where(m => m.MenuCode == "stock-alert" && !m.IsDeleted).FirstOrDefaultAsync();
        if (menuAlert is null)
        {
            menuAlert = new SysMenu { MenuName = "库存预警表", MenuCode = "stock-alert", MenuType = MenuType.Menu };
            db.SysMenus.Add(menuAlert);
            await db.SaveChangesAsync();
        }

        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == UserName && !u.IsDeleted);
        if (user is null)
        {
            user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP299 隔离账号", Status = UserStatus.Enabled };
            db.SysUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var role = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == RoleCode && !r.IsDeleted);
        if (role is null)
        {
            role = new SysRole { RoleName = RoleCode, RoleCode = RoleCode, IsSystem = false };
            db.SysRoles.Add(role);
            await db.SaveChangesAsync();
        }

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in new[] { menuQuery, menuAlert })
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var warehouse = await db.BaseWarehouses.FirstOrDefaultAsync(w => w.WarehouseCode == WarehouseCode && !w.IsDeleted);
        if (warehouse is null)
        {
            warehouse = new BaseWarehouse { WarehouseCode = WarehouseCode, WarehouseName = "ERP299 主仓", Status = 1 };
            db.BaseWarehouses.Add(warehouse);
            await db.SaveChangesAsync();
        }

        var productA = await EnsureProductAsync(db, ProductACode, "ERP299 商品一", minStock: 10m, maxStock: 0m);
        var productB = await EnsureProductAsync(db, ProductBCode, "ERP299 商品二", minStock: 0m, maxStock: 5m);

        await EnsureStockAsync(db, warehouse.Id, productA.Id, 3m, 5m, 15m);
        await EnsureStockAsync(db, warehouse.Id, productB.Id, 8m, 0m, 0m);

        await EnsureMovementAsync(db, warehouse.Id, productA.Id, DateTime.Today.AddDays(-10), 1, 5m);
        await EnsureMovementAsync(db, warehouse.Id, productA.Id, DateTime.Today.AddDays(-4), -1, 2m);
        await EnsureMovementAsync(db, warehouse.Id, productB.Id, DateTime.Today.AddDays(-8), 1, 10m);
        await EnsureMovementAsync(db, warehouse.Id, productB.Id, DateTime.Today.AddDays(-2), -1, 2m);

        PrivilegedUserId = user.Id;
    }

    private static async Task<BaseProduct> EnsureProductAsync(ErpDbContext db, string code, string name, decimal minStock, decimal maxStock)
    {
        var product = await db.BaseProducts.FirstOrDefaultAsync(p => p.ProductCode == code && !p.IsDeleted);
        if (product is not null)
            return product;

        product = new BaseProduct { ProductCode = code, ProductName = name, Spec = "标准", Unit = "PCS", MinStock = minStock, MaxStock = maxStock, Status = 1 };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task EnsureStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity, decimal averageCost, decimal totalCost)
    {
        if (await db.Stocks.AnyAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId && !s.IsDeleted))
            return;

        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            AverageCost = averageCost,
            TotalCost = totalCost,
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureMovementAsync(ErpDbContext db, long warehouseId, long productId, DateTime movementDate, int direction, decimal quantity)
    {
        if (await db.StockMovements.AnyAsync(m => m.WarehouseId == warehouseId && m.ProductId == productId
                && m.MovementDate == movementDate && m.Direction == direction && m.Quantity == quantity && !m.IsDeleted))
            return;

        db.StockMovements.Add(new StockMovement
        {
            MovementDate = movementDate,
            MovementType = direction > 0 ? InventoryMovementType.PurchaseIn : InventoryMovementType.SalesOut,
            SourceDocType = direction > 0 ? "StockIn" : "StockOut",
            SourceDocId = 1,
            SourceDocNo = direction > 0 ? "ERP299-SI" : "ERP299-SO",
            WarehouseId = warehouseId,
            WarehouseName = "ERP299 主仓",
            ProductId = productId,
            ProductCode = direction > 0 ? ProductACode : ProductACode,
            ProductName = "ERP299 商品",
            Spec = "标准",
            Unit = "PCS",
            Direction = direction,
            Quantity = quantity,
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




