using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-106 只读补货工作台单元测试。覆盖：
/// 低于最低库存 + 有效上限目标的补货建议（阈值边界）、缺上限目标 / 阈值缺失 / 阈值无效的显式标注、
/// 缺失商品行的可读性、停用供应商的货源不可用、无货源关系、多仓库不跨仓汇总、稳定分页有界、
/// 仓库必填校验、只读不写库，以及接口与前端接线契约。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed，不做浏览器验收。</para>
/// </summary>
public class StockReplenishmentWorksheetTests
{
    // ==================== 0. 测试脚手架 ====================

    private const long WarehouseA = 900001L;
    private const long WarehouseB = 900002L;
    private const long Product1 = 700001L;
    private const long Product2 = 700002L;
    private const long Supplier1 = 800001L;

    private static StockReplenishmentWorksheetController BuildController(ErpDbContext db) => new(db);

    private static StockReplenishmentWorksheetDto GetData(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<StockReplenishmentWorksheetDto>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name)
        => db.BaseWarehouses.Add(new BaseWarehouse
        {
            Id = id, WarehouseCode = $"WH{id}", WarehouseName = name
        }).Entity;

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name,
        decimal minStock = 0m, decimal maxStock = 0m, string unit = "PCS", bool deleted = false)
        => db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = unit,
            MinStock = minStock, MaxStock = maxStock, IsDeleted = deleted
        }).Entity;

    private static Stock SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity)
        => db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId, ProductId = productId, Quantity = quantity, AvailableQuantity = quantity
        }).Entity;

    private static BaseSupplier SeedSupplier(ErpDbContext db, long id, string code, string name,
        int status = 1, bool deleted = false)
        => db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id, SupplierCode = code, SupplierName = name, Status = status, IsDeleted = deleted
        }).Entity;

    private static BaseProductSupplier SeedRelation(ErpDbContext db, long productId, long supplierId,
        string itemCode = "", string unit = "", decimal moq = 0m, int leadTime = 0,
        bool preferred = false, int status = 1, bool deleted = false)
        => db.BaseProductSuppliers.Add(new BaseProductSupplier
        {
            ProductId = productId,
            SupplierId = supplierId,
            ScopeKey = ProductSupplierRules.BuildScopeKey(null),
            SupplierItemCode = itemCode,
            PurchaseUnit = unit,
            MinOrderQty = moq,
            LeadTimeDays = leadTime,
            IsPreferred = preferred,
            Status = status,
            IsDeleted = deleted
        }).Entity;

    // ==================== 1. 补货建议与阈值边界 ====================

    [Fact]
    public async Task Worksheet_recommends_top_up_when_below_minimum_and_valid_target()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 100m);
        SeedStock(db, WarehouseA, Product1, 5m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.True(row.BelowMinimum);
        Assert.Equal(95m, row.SuggestedTopUp);          // 100 - 5
        Assert.Equal(StockReplenishmentRules.StateReplenish, row.Recommendation);
        Assert.Equal(10m, row.MinStock);
        Assert.Equal(100m, row.MaxStock);
    }

    [Fact]
    public async Task Worksheet_marks_below_minimum_without_target_unavailable()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 0m);
        SeedStock(db, WarehouseA, Product1, 5m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.True(row.BelowMinimum);                  // 仍低于最低库存
        Assert.Null(row.SuggestedTopUp);                // 但无有效上限目标，不给数量
        Assert.Equal(StockReplenishmentRules.StateBelowMinNoTarget, row.Recommendation);
    }

    [Fact]
    public async Task Worksheet_marks_missing_threshold_when_min_stock_absent()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 0m, maxStock: 0m);
        SeedStock(db, WarehouseA, Product1, 5m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.False(row.BelowMinimum);
        Assert.Null(row.SuggestedTopUp);
        Assert.Equal(StockReplenishmentRules.StateMissingThreshold, row.Recommendation);
    }

    [Fact]
    public async Task Worksheet_marks_invalid_threshold_when_max_below_min()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 5m);
        SeedStock(db, WarehouseA, Product1, 4m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.True(row.BelowMinimum);                  // 低于最低库存
        Assert.Null(row.SuggestedTopUp);                // 但上限低于最低，阈值无效
        Assert.Equal(StockReplenishmentRules.StateInvalidThreshold, row.Recommendation);
    }

    [Fact]
    public void Rules_treat_quantity_at_minimum_as_adequate_not_replenished()
    {
        var atMin = StockReplenishmentRules.Evaluate(10m, 10m, 100m);
        Assert.Equal(StockReplenishmentRules.StateAdequate, atMin.State);
        Assert.Null(atMin.SuggestedTopUp);

        var belowByOne = StockReplenishmentRules.Evaluate(9m, 10m, 100m);
        Assert.Equal(StockReplenishmentRules.StateReplenish, belowByOne.State);
        Assert.Equal(91m, belowByOne.SuggestedTopUp);
    }

    // ==================== 2. 缺失商品 / 货源可用性 ====================

    [Fact]
    public async Task Worksheet_keeps_missing_product_row_readable()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 7m);        // 商品资料缺失（历史脏数据）
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.Equal(Product1, row.ProductId);
        Assert.Equal("商品#700001", row.ProductName);    // 退化为可辨识占位，不静默变空
        Assert.Equal(string.Empty, row.ProductCode);
        Assert.False(row.BelowMinimum);
        Assert.Equal(StockReplenishmentRules.StateMissingThreshold, row.Recommendation);
    }

    [Fact]
    public async Task Worksheet_marks_inactive_supplier_sourcing_unavailable()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 100m);
        SeedStock(db, WarehouseA, Product1, 5m);
        SeedSupplier(db, Supplier1, "S001", "义乌工厂", status: 0);   // 停用供应商
        SeedRelation(db, Product1, Supplier1, itemCode: "P-01", preferred: true);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.False(row.SourcingAvailable);             // 停用供应商 → 不可用
        var reference = Assert.Single(row.Sourcing);
        Assert.False(reference.SupplierAvailable);
        Assert.Contains("已停用", row.SourcingText);
    }

    [Fact]
    public async Task Worksheet_lists_no_sourcing_when_no_active_relations()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 100m);
        SeedStock(db, WarehouseA, Product1, 5m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);
        Assert.False(row.SourcingAvailable);
        Assert.Empty(row.Sourcing);
        Assert.Contains("无可用货源", row.SourcingText);
    }

    // ==================== 3. 多仓库 / 分页 / 只读 ====================

    [Fact]
    public async Task Worksheet_does_not_aggregate_across_warehouses()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "海外仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 100m);
        SeedStock(db, WarehouseA, Product1, 5m);
        SeedStock(db, WarehouseB, Product1, 50m);       // 同商品在另一仓库的库存
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        var row = Assert.Single(data.Items);            // 只返回仓库 A 的行
        Assert.Equal(WarehouseA, row.WarehouseId);
        Assert.Equal(5m, row.Quantity);                 // 不把仓库 B 的 50 加进来
        Assert.Equal(95m, row.SuggestedTopUp);          // 100 - 5（按仓库 A 独立计算）
    }

    [Fact]
    public async Task Worksheet_pages_stably_by_product_then_id()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedStock(db, WarehouseA, Product1, 1m);
        SeedStock(db, WarehouseA, Product2, 2m);
        // 同一商品两条库存行（同一仓库）按 Id 稳定排序
        SeedStock(db, WarehouseA, Product2, 3m);
        await db.SaveChangesAsync();

        var page1 = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA, Page = 1, PageSize = 2 }));
        var page2 = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA, Page = 2, PageSize = 2 }));

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.Equal(Product1, page1.Items[0].ProductId);
        Assert.Equal(Product2, page1.Items[1].ProductId);
    }

    [Fact]
    public async Task Worksheet_is_read_only_no_write()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯", minStock: 10m, maxStock: 100m);
        SeedStock(db, WarehouseA, Product1, 5m);
        SeedSupplier(db, Supplier1, "S001", "义乌工厂");
        SeedRelation(db, Product1, Supplier1);
        await db.SaveChangesAsync();

        var stockCount = db.Stocks.Count();
        var productCount = db.BaseProducts.Count();
        var supplierCount = db.BaseSuppliers.Count();
        var relationCount = db.BaseProductSuppliers.Count();
        var orderCount = db.PurchaseOrders.Count();

        var data = GetData(await BuildController(db).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = WarehouseA }));

        Assert.Single(data.Items);
        // 只读：无新增 / 无修改 / 无删除，无待保存的变更追踪，也不生成采购订单
        Assert.Equal(stockCount, db.Stocks.Count());
        Assert.Equal(productCount, db.BaseProducts.Count());
        Assert.Equal(supplierCount, db.BaseSuppliers.Count());
        Assert.Equal(relationCount, db.BaseProductSuppliers.Count());
        Assert.Equal(orderCount, db.PurchaseOrders.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
        Assert.False(string.IsNullOrWhiteSpace(data.ReadOnlyText));
        Assert.False(string.IsNullOrWhiteSpace(data.BoundaryText));
        Assert.False(string.IsNullOrWhiteSpace(data.DisclaimerText));
    }

    [Fact]
    public async Task Worksheet_requires_warehouse_filter()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "保温杯");
        SeedStock(db, WarehouseA, Product1, 5m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        await Assert.ThrowsAsync<BusinessException>(() =>
            controller.GetWorksheet(new StockReplenishmentWorksheetQuery { WarehouseId = null }));
    }

    // ==================== 4. 前端接线契约 ====================

    [Fact]
    public void Frontend_wiring_registers_worksheet()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "stock-replenishment-worksheet.js"));
        Assert.Contains("openStockReplenishmentWorksheet", js);
        Assert.Contains("/api/stocks/replenishment-worksheet", js);

        var modulesDoc2 = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js"));
        Assert.Contains("openStockReplenishmentWorksheet()", modulesDoc2);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/stock-replenishment-worksheet.js", index);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

