using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-299 库存移动 / 库存库龄 / 库存预警三个固定报表迁移为受控数据集适配器的单元测试：
/// 覆盖目录暴露与币种 / 单位口径、预览与既有固定报表逐行一致、分页 / 空页、撤销菜单立即收敛（fail closed）、
/// 未知字段 / 筛选 / 分页超限拒绝、以及无台账 / 成本未知的可空证据语义。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationInventoryMigrationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role");
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static IReportService NewReportService(ErpDbContext db) => new ReportService(db);

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
    {
        var reportService = NewReportService(db);
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetInventoryMovement => new InventoryMovementReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetInventoryAging => new InventoryAgingReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetStockAlert => new StockAlertReportConfigurationDatasetProvider(reportService, db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };
    }

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new(new IReportConfigurationDatasetProvider[]
        {
            new InventoryMovementReportConfigurationDatasetProvider(NewReportService(db), db),
            new InventoryAgingReportConfigurationDatasetProvider(NewReportService(db), db),
            new StockAlertReportConfigurationDatasetProvider(NewReportService(db), db),
        });

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 100)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    private static List<string> MovementDefaultFields()
        => new() { "warehouseId", "warehouseName", "productId", "productCode", "productName", "spec", "unit", "currentQuantity", "lastMovementDate", "inboundQuantity", "outboundQuantity", "netQuantity", "movementCount", "reversalCount", "inactivityDays", "historyStatus", "classification", "note" };

    private static List<string> AgingDefaultFields()
        => new() { "warehouseId", "productId", "productName", "unit", "currentQuantity", "knownAgedQuantity", "unknownAgeQuantity", "evidenceStatus", "averageCost", "costStatus", "costCurrency", "authoritativeAmount", "agedAmount", "unknownAgeAmount", "unknownCostQuantity", "note" };

    private static List<string> StockAlertDefaultFields()
        => new() { "productName", "spec", "unit", "warehouseName", "quantity", "minStock", "maxStock", "diff", "alertLevel" };

    // ==================== 1. 库存数据种子 ====================

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name, string unit = "PCS", decimal minStock = 0m, decimal maxStock = 0m)
    {
        var product = new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = unit,
            MinStock = minStock, MaxStock = maxStock,
        };
        db.BaseProducts.Add(product);
        return product;
    }

    private static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name)
    {
        var warehouse = new BaseWarehouse { Id = id, WarehouseCode = $"WH{id}", WarehouseName = name };
        db.BaseWarehouses.Add(warehouse);
        return warehouse;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal averageCost = 0m, decimal totalCost = 0m)
        => db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            AverageCost = averageCost,
            TotalCost = totalCost,
        });

    private static void SeedMovement(ErpDbContext db, long warehouseId, long productId, DateTime movementDate,
        int direction, decimal quantity, bool isReversal = false, long? reversalOf = null)
        => db.StockMovements.Add(new StockMovement
        {
            MovementDate = movementDate,
            MovementType = direction > 0 ? InventoryMovementType.PurchaseIn : InventoryMovementType.SalesOut,
            SourceDocType = direction > 0 ? "StockIn" : "StockOut",
            SourceDocId = 1,
            SourceDocNo = direction > 0 ? "SI-0001" : "SO-0001",
            WarehouseId = warehouseId,
            WarehouseName = "主仓",
            ProductId = productId,
            ProductCode = "P001",
            ProductName = "商品一",
            Spec = "标准",
            Unit = "PCS",
            Direction = direction,
            Quantity = quantity,
            UnitCost = 0m,
            Amount = 0m,
            IsReversal = isReversal,
            IsReversed = false,
            ReversalOfMovementId = reversalOf,
        });

    // ==================== 2. 目录暴露与币种 / 单位口径 ====================

    [Fact]
    public async Task Catalog_三个库存数据集全部暴露且字段白名单与币种口径真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "inv", "stock-query", "stock-alert");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        Assert.Equal(3, result.Datasets.Count);

        var movement = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetInventoryMovement);
        Assert.Equal("数量按基础单位；成本按移动加权平均；不跨币种合并", movement.CurrencyUnitSemantics);
        Assert.Equal("stock-query", movement.RequiredMenuCode);
        Assert.Equal(18, movement.Fields.Count);
        var currentQuantity = Assert.Single(movement.Fields, f => f.Key == "currentQuantity");
        Assert.Equal("基础单位", currentQuantity.CurrencyUnit);
        Assert.True(currentQuantity.Aggregatable);

        var aging = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetInventoryAging);
        Assert.Equal("数量按基础单位；成本/金额按移动加权平均；不跨币种合并", aging.CurrencyUnitSemantics);
        Assert.Equal("stock-query", aging.RequiredMenuCode);
        Assert.Equal(32, aging.Fields.Count);
        Assert.Contains(aging.Fields, f => f.Key == "bucket0To30Amount" && f.CurrencyUnit == "金额（CNY）");
        Assert.Contains(aging.Fields, f => f.Key == "bucketOver180Quantity" && f.CurrencyUnit == "基础单位");

        var alert = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetStockAlert);
        Assert.Equal("数量按基础单位；无金额/币种", alert.CurrencyUnitSemantics);
        Assert.Equal("stock-alert", alert.RequiredMenuCode);
        Assert.Equal(9, alert.Fields.Count);
        Assert.Contains(alert.Fields, f => f.Key == "quantity" && f.CurrencyUnit == "基础单位");
    }

    [Fact]
    public async Task Catalog_未授权菜单_对应数据集不暴露()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "inv-only", "stock-query");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        Assert.Equal(2, result.Datasets.Count);
        Assert.DoesNotContain(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetStockAlert);
    }

    // ==================== 3. 预览与既有固定报表逐行一致 ====================

    [Fact]
    public async Task 库存移动_预览与既有固定报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "mov-preview", "stock-query");
        SeedProduct(db, 700001L, "P001", "商品一");
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 12m);
        SeedMovement(db, 900001L, 700001L, DateTime.Today.AddDays(-10), 1, 10m);
        SeedMovement(db, 900001L, 700001L, DateTime.Today.AddDays(-4), -1, 3m);
        await db.SaveChangesAsync();

        var reportService = NewReportService(db);
        var provider = new InventoryMovementReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetInventoryMovementReportAsync(new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = DateTime.Today,
            InactiveDays = 90,
            OnlyPositiveQuantity = true,
            Page = 1,
            PageSize = 200,
        });

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, MovementDefaultFields().ToArray()),
            Params(pageSize: 200), user.Id);

        Assert.Equal(legacy.Total, preview.Total);
        Assert.Equal(legacy.Items.Count, preview.Rows.Count);

        var legacyRow = Assert.Single(legacy.Items);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(legacyRow.WarehouseId, (long)row["warehouseId"]!);
        Assert.Equal(legacyRow.WarehouseName, (string)row["warehouseName"]!);
        Assert.Equal(legacyRow.CurrentQuantity, (decimal)row["currentQuantity"]!);
        Assert.Equal(legacyRow.LastMovementDate, (DateTime?)row["lastMovementDate"]);
        Assert.Equal(legacyRow.InboundQuantity, (decimal)row["inboundQuantity"]!);
        Assert.Equal(legacyRow.OutboundQuantity, (decimal)row["outboundQuantity"]!);
        Assert.Equal(legacyRow.NetQuantity, (decimal)row["netQuantity"]!);
        Assert.Equal(legacyRow.InactivityDays, (int?)row["inactivityDays"]);
        Assert.Equal(InventoryMovementSemantics.ClassActive, (string)row["classification"]!);
    }

    [Fact]
    public async Task 库存库龄_预览与既有固定报表逐行一致且金额CNY()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "aging-preview", "stock-query");
        SeedProduct(db, 700001L, "P001", "商品一");
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 10m, averageCost: 5m, totalCost: 50m);
        SeedMovement(db, 900001L, 700001L, DateTime.Today.AddDays(-10), 1, 10m);
        await db.SaveChangesAsync();

        var reportService = NewReportService(db);
        var provider = new InventoryAgingReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetInventoryAgingReportAsync(new ReportDtos.InventoryAgingReportQuery
        {
            AsOfDate = DateTime.Today,
            OnlyPositiveQuantity = true,
            Page = 1,
            PageSize = 200,
        });

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryAging, AgingDefaultFields().ToArray()),
            Params(pageSize: 200), user.Id);

        Assert.Equal(legacy.Total, preview.Total);
        Assert.Equal(legacy.Items.Count, preview.Rows.Count);

        var legacyRow = Assert.Single(legacy.Items);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(legacyRow.CurrentQuantity, (decimal)row["currentQuantity"]!);
        Assert.Equal(legacyRow.KnownAgedQuantity, (decimal)row["knownAgedQuantity"]!);
        Assert.Equal(legacyRow.AuthoritativeAmount, (decimal?)row["authoritativeAmount"]);
        Assert.Equal(legacyRow.AgedAmount, (decimal?)row["agedAmount"]);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, (string)row["costCurrency"]!);
        Assert.Equal(InventoryAgingSemantics.CostKnown, (string)row["costStatus"]!);
    }

    [Fact]
    public async Task 库存预警_预览与既有固定报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alert-preview", "stock-alert");
        SeedProduct(db, 700001L, "P001", "商品一", minStock: 10m, maxStock: 0m);
        SeedProduct(db, 700002L, "P002", "商品二", minStock: 0m, maxStock: 5m);
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 3m);
        SeedStock(db, 900001L, 700002L, 8m);
        await db.SaveChangesAsync();

        var reportService = NewReportService(db);
        var provider = new StockAlertReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetStockAlertAsync();

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetStockAlert, StockAlertDefaultFields().ToArray()),
            Params(pageSize: 100), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);

        foreach (var legacyRow in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["productName"]! == legacyRow.ProductName);
            Assert.Equal(legacyRow.Quantity, (decimal)row["quantity"]!);
            Assert.Equal(legacyRow.MinStock, (decimal)row["minStock"]!);
            Assert.Equal(legacyRow.MaxStock, (decimal)row["maxStock"]!);
            Assert.Equal(legacyRow.Diff, (decimal)row["diff"]!);
            Assert.Equal(legacyRow.AlertLevel, (string)row["alertLevel"]!);
        }
    }

    // ==================== 4. 分页 / 空页 ====================

    [Fact]
    public async Task 库存移动_分页与既有固定报表分页一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "mov-page", "stock-query");
        SeedProduct(db, 700001L, "P001", "商品一");
        SeedProduct(db, 700002L, "P002", "商品二");
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 5m);
        SeedStock(db, 900001L, 700002L, 8m);
        SeedMovement(db, 900001L, 700001L, DateTime.Today.AddDays(-3), 1, 5m);
        SeedMovement(db, 900001L, 700002L, DateTime.Today.AddDays(-2), 1, 8m);
        await db.SaveChangesAsync();

        var provider = new InventoryMovementReportConfigurationDatasetProvider(NewReportService(db), db);

        var page1 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, "productCode", "currentQuantity"),
            Params(page: 1, pageSize: 1), user.Id);

        Assert.Equal(2, page1.Total);
        Assert.Single(page1.Rows);
        Assert.Equal(2, page1.TotalPages);

        var page2 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, "productCode", "currentQuantity"),
            Params(page: 2, pageSize: 1), user.Id);
        Assert.Single(page2.Rows);
        Assert.NotEqual(page1.Rows[0]["productCode"], page2.Rows[0]["productCode"]);
    }

    [Fact]
    public async Task 库存移动_无匹配行_返回空页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "mov-empty", "stock-query");
        SeedProduct(db, 700001L, "P001", "商品一");
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 0m);
        await db.SaveChangesAsync();

        var provider = new InventoryMovementReportConfigurationDatasetProvider(NewReportService(db), db);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, "productCode", "currentQuantity"),
            Params(), user.Id);

        Assert.Equal(0, preview.Total);
        Assert.Empty(preview.Rows);
        Assert.Equal(0, preview.TotalPages);
    }

    // ==================== 5. 菜单撤销立即收敛（fail closed） ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetInventoryMovement, "stock-query")]
    [InlineData(ReportConfigurationConstants.DatasetInventoryAging, "stock-query")]
    [InlineData(ReportConfigurationConstants.DatasetStockAlert, "stock-alert")]
    public async Task 菜单撤销后_数据集不暴露且预览拒绝(string datasetKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoke-" + datasetKey, menuCode);
        var provider = BuildProvider(db, datasetKey);

        Assert.NotNull(await provider.GetDatasetAsync(user.Id));

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(datasetKey, DefaultFieldsFor(datasetKey).ToArray()),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetStockAlert);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetStockAlert, "productName", "quantity"),
            Params(), null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 6. 未知字段 / 筛选 / 分页超限拒绝（bounded failure） ====================

    [Fact]
    public async Task 未知字段_预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown-field", "stock-query");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetInventoryMovement);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, "notAField"),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 未知筛选字段_预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown-filter", "stock-query");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetInventoryMovement);

        var definition = Definition(ReportConfigurationConstants.DatasetInventoryMovement, "productCode");
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "warehouseName", Operator = ReportConfigurationConstants.OperatorEq, Value = "主仓" },
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(definition, Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 分页超限_预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "page-over", "stock-query");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetInventoryMovement);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, "productCode"),
            Params(page: 1, pageSize: 201), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 可空证据语义 ====================

    [Fact]
    public async Task 库存移动_无台账行_日期与停滞天数可空()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "mov-null", "stock-query");
        SeedProduct(db, 700001L, "P001", "商品一");
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 88m);
        await db.SaveChangesAsync();

        var provider = new InventoryMovementReportConfigurationDatasetProvider(NewReportService(db), db);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryMovement, "currentQuantity", "lastMovementDate", "inactivityDays", "historyStatus", "classification"),
            Params(), user.Id);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(88m, (decimal)row["currentQuantity"]!);
        Assert.Null(row["lastMovementDate"]);
        Assert.Null(row["inactivityDays"]);
        Assert.Equal(InventoryMovementSemantics.HistoryNoHistory, (string)row["historyStatus"]!);
        Assert.Equal(InventoryMovementSemantics.ClassUnknown, (string)row["classification"]!);
    }

    [Fact]
    public async Task 库存库龄_成本未知_金额可空且数量单列()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "aging-null", "stock-query");
        SeedProduct(db, 700001L, "P001", "商品一");
        SeedWarehouse(db, 900001L, "主仓");
        SeedStock(db, 900001L, 700001L, 30m);
        await db.SaveChangesAsync();

        var provider = new InventoryAgingReportConfigurationDatasetProvider(NewReportService(db), db);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetInventoryAging, "currentQuantity", "evidenceStatus", "costStatus", "authoritativeAmount", "agedAmount", "unknownAgeAmount", "unknownCostQuantity"),
            Params(), user.Id);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(30m, (decimal)row["currentQuantity"]!);
        Assert.Equal(InventoryAgingSemantics.CostUnknown, (string)row["costStatus"]!);
        Assert.Null(row["authoritativeAmount"]);
        Assert.Null(row["agedAmount"]);
        Assert.Null(row["unknownAgeAmount"]);
        Assert.Equal(30m, (decimal)row["unknownCostQuantity"]!);
    }

    private static List<string> DefaultFieldsFor(string datasetKey)
        => datasetKey == ReportConfigurationConstants.DatasetInventoryMovement
            ? MovementDefaultFields()
            : datasetKey == ReportConfigurationConstants.DatasetInventoryAging
                ? AgingDefaultFields()
                : StockAlertDefaultFields();

    // ==================== 8. 迁移预设登记（覆盖固定与动态登记册条目） ====================

    [Theory]
    [InlineData("report:inventory-movement")]
    [InlineData("report:inventory-aging")]
    [InlineData("report:stock-alert")]
    [InlineData("dynamic:inventory-movement")]
    [InlineData("dynamic:inventory-aging")]
    public async Task 迁移预设_五个登记册条目均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();

        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }
}






