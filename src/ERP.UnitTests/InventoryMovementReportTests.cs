using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-029 库存移动与呆滞报表（只读派生）单元测试：
/// 活跃库存 / 呆滞分类（含阈值边界）/ 红字冲销净额 / 无台账（未知）/ 窗口与截止日期口径 /
/// 仓库·商品·关键字筛选 / 分页有界 / 参数校验 / 不做成本估值 / 接口与前端接线。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed。</para>
/// </summary>
public class InventoryMovementReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long WarehouseA = 900001L;
    private const long WarehouseB = 900002L;
    private const long Product1 = 700001L;
    private const long Product2 = 700002L;

    // ==================== 1. 活跃库存：基础单位数量 / 最后移动日期 / 停滞天数 ====================

    [Fact]
    public async Task Report_active_stock_exposes_base_unit_quantities_last_movement_and_inactivity()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 12m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-4), direction: -1, quantity: 3m);
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(Query(inactiveDays: 90));

        var row = Assert.Single(report.Items);
        Assert.Equal(WarehouseA, row.WarehouseId);
        Assert.Equal("主仓", row.WarehouseName);
        Assert.Equal("P001", row.ProductCode);
        Assert.Equal("商品一", row.ProductName);
        Assert.Equal("PCS", row.Unit);
        Assert.Equal(12m, row.CurrentQuantity);              // 现存量来自库存行（基础单位）
        Assert.Equal(AsOf.AddDays(-4), row.LastMovementDate);
        Assert.Equal(10m, row.InboundQuantity);
        Assert.Equal(3m, row.OutboundQuantity);
        Assert.Equal(7m, row.NetQuantity);
        Assert.Equal(2, row.MovementCount);
        Assert.Equal(0, row.ReversalCount);
        Assert.Equal(4, row.InactivityDays);                  // 截止日期 - 最后移动日期
        Assert.Equal(InventoryMovementSemantics.HistoryLedger, row.HistoryStatus);
        Assert.Equal(InventoryMovementSemantics.ClassActive, row.Classification);
        Assert.Equal(string.Empty, row.Note);

        Assert.Equal(AsOf, report.AsOfDate);
        Assert.Equal(AsOf.AddDays(-89), report.WindowStart);
        Assert.Equal(AsOf, report.WindowEnd);
        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.ActiveCount);
        Assert.Equal(0, report.StagnantCount);
        Assert.Equal(0, report.InsufficientHistoryCount);
        Assert.Equal(12m, report.PageCurrentQuantity);
        Assert.Equal(10m, report.PageInboundQuantity);
        Assert.Equal(3m, report.PageOutboundQuantity);
        Assert.Equal(7m, report.PageNetQuantity);
        Assert.Equal(InventoryMovementSemantics.RuleText, report.Rule);
        Assert.Equal(InventoryMovementSemantics.PageScopeText, report.ScopeNote);
    }

    // ==================== 2. 呆滞分类：按阈值（>= 语义） ====================

    [Fact]
    public async Task Report_classifies_stagnant_exactly_at_threshold_and_active_below_it()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 5m);
        SeedStock(db, WarehouseA, Product2, 8m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-90), direction: 1, quantity: 5m);   // 恰好 90 天
        SeedMovement(db, WarehouseA, Product2, AsOf.AddDays(-89), direction: 1, quantity: 8m);   // 89 天
        await db.SaveChangesAsync();

        // 窗口放宽到 200 天：本用例只考察呆滞分类的阈值边界（>= 语义）
        var report = await Service(db).GetInventoryMovementReportAsync(
            Query(windowStart: AsOf.AddDays(-200), inactiveDays: 90));

        Assert.Equal(2, report.Items.Count);
        var stagnant = report.Items.Single(i => i.ProductId == Product1);
        var active = report.Items.Single(i => i.ProductId == Product2);
        Assert.Equal(90, stagnant.InactivityDays);
        Assert.Equal(InventoryMovementSemantics.ClassStagnant, stagnant.Classification);
        Assert.Equal(InventoryMovementSemantics.HistoryLedger, stagnant.HistoryStatus);
        Assert.Equal(89, active.InactivityDays);
        Assert.Equal(InventoryMovementSemantics.ClassActive, active.Classification);

        Assert.Equal(1, report.ActiveCount);
        Assert.Equal(1, report.StagnantCount);

        // 阈值提高后同一数据变为「正常流动」（分类完全跟随请求阈值）
        var relaxed = await Service(db).GetInventoryMovementReportAsync(
            Query(windowStart: AsOf.AddDays(-200), inactiveDays: 120));
        Assert.Equal(0, relaxed.StagnantCount);
        Assert.Equal(2, relaxed.ActiveCount);
        Assert.Equal(120, relaxed.InactiveDays);
    }

    // ==================== 3. 无台账行：显式「未知」，不臆造日期与比率 ====================

    [Fact]
    public async Task Report_keeps_rows_without_ledger_history_unknown_instead_of_inventing_dates()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 88m);            // 历史库存（ERP-025 之前的库存），没有任何流水
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(Query(inactiveDays: 90));

        var row = Assert.Single(report.Items);
        Assert.Equal(88m, row.CurrentQuantity);
        Assert.Null(row.LastMovementDate);
        Assert.Null(row.InactivityDays);                      // 未知：不是 0，也不是臆造日期
        Assert.Equal(0m, row.InboundQuantity);
        Assert.Equal(0m, row.OutboundQuantity);
        Assert.Equal(0m, row.NetQuantity);
        Assert.Equal(0, row.MovementCount);
        Assert.Equal(InventoryMovementSemantics.HistoryNoHistory, row.HistoryStatus);
        Assert.Equal(InventoryMovementSemantics.ClassUnknown, row.Classification);
        Assert.Contains("没有任何库存流水", row.Note);
        Assert.Contains("未知", row.Note);
        Assert.Contains("不臆造", row.Note);

        Assert.Equal(1, report.InsufficientHistoryCount);
        Assert.Equal(0, report.StagnantCount);                 // 无台账不得判为呆滞
        Assert.Equal(0, report.ActiveCount);
    }

    // ==================== 4. 红字冲销：成对净额，不二次扣减 ====================

    [Fact]
    public async Task Report_nets_reversal_rows_without_double_counting()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 6m);

        // 采购入库 10 后被销审冲销：原流水（+10）与红字流水（-10）成对净额为 0
        var inbound = SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-8), direction: 1, quantity: 10m, id: 1);
        inbound.IsReversed = true;
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-7), direction: -1, quantity: 10m,
            isReversal: true, reversalOf: 1, id: 2);

        // 销售出库 4 后被销审冲销：原流水（-4）与红字流水（+4）同样成对净额为 0
        var outbound = SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-3), direction: -1, quantity: 4m, id: 3);
        outbound.IsReversed = true;
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-2), direction: 1, quantity: 4m,
            isReversal: true, reversalOf: 3, id: 4);
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(Query(inactiveDays: 90));

        var row = Assert.Single(report.Items);
        Assert.Equal(14m, row.InboundQuantity);               // 毛额（含红字腿）
        Assert.Equal(14m, row.OutboundQuantity);
        Assert.Equal(0m, row.NetQuantity);                    // 净额：成对为 0，未二次扣减
        Assert.Equal(4, row.MovementCount);
        Assert.Equal(2, row.ReversalCount);
        Assert.Equal(6m, row.CurrentQuantity);                // 现存量与台账净额口径各自独立
        Assert.Equal(AsOf.AddDays(-2), row.LastMovementDate); // 红字流水也是台账事实
        Assert.Contains("红字冲销", row.Note);
        Assert.Contains("未做二次扣减", row.Note);
        Assert.Equal(0m, report.PageNetQuantity);
    }

    // ==================== 5. 移动窗口：只影响数量口径，不影响台账事实 ====================

    [Fact]
    public async Task Report_window_filters_quantities_but_keeps_history_and_flags_window_empty()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 6m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-200), direction: 1, quantity: 6m);
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(
            Query(windowStart: AsOf.AddDays(-29), windowEnd: AsOf, inactiveDays: 90));

        var row = Assert.Single(report.Items);
        Assert.Equal(0m, row.InboundQuantity);                // 窗口内确实没有流水：真实的 0
        Assert.Equal(0m, row.OutboundQuantity);
        Assert.Equal(0m, row.NetQuantity);
        Assert.Equal(0, row.MovementCount);
        Assert.Equal(AsOf.AddDays(-200), row.LastMovementDate);
        Assert.Equal(200, row.InactivityDays);
        Assert.Equal(InventoryMovementSemantics.HistoryWindowEmpty, row.HistoryStatus);
        Assert.Equal(InventoryMovementSemantics.ClassStagnant, row.Classification);
        Assert.Contains("真实的 0", row.Note);
        Assert.Equal(1, report.WindowEmptyCount);
        Assert.Equal(0, report.InsufficientHistoryCount);
    }

    // ==================== 6. 截止日期：之后的移动不属于本次口径 ====================

    [Fact]
    public async Task Report_cuts_history_and_totals_at_the_as_of_date()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 101m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-2), direction: 1, quantity: 2m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(3), direction: 1, quantity: 99m);   // 截止日期之后
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(
            Query(asOf: AsOf, windowStart: AsOf.AddDays(-89), windowEnd: AsOf, inactiveDays: 90));

        var row = Assert.Single(report.Items);
        Assert.Equal(2m, row.InboundQuantity);                // 99 未计入
        Assert.Equal(1, row.MovementCount);
        Assert.Equal(AsOf.AddDays(-2), row.LastMovementDate);
        Assert.Equal(2, row.InactivityDays);

        // 把截止日期往后推：同一批数据纳入后一笔移动（口径随时点变化，而不是丢弃台账）
        var later = await Service(db).GetInventoryMovementReportAsync(
            Query(asOf: AsOf.AddDays(5), windowStart: AsOf.AddDays(-89), windowEnd: AsOf.AddDays(5), inactiveDays: 90));
        var laterRow = Assert.Single(later.Items);
        Assert.Equal(101m, laterRow.InboundQuantity);
        Assert.Equal(AsOf.AddDays(3), laterRow.LastMovementDate);
        Assert.Equal(2, laterRow.InactivityDays);
    }

    // ==================== 7. 筛选：仓库 / 商品 / 关键字 / 现存量 ====================

    [Fact]
    public async Task Report_filters_by_warehouse_product_keyword_and_positive_quantity()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "备用仓");
        SeedStock(db, WarehouseA, Product1, 5m);
        SeedStock(db, WarehouseB, Product1, 7m);
        SeedStock(db, WarehouseA, Product2, 0m);              // 零数量行
        await db.SaveChangesAsync();

        var service = Service(db);

        // 默认仅现存量 > 0：零数量行不出现
        var all = await service.GetInventoryMovementReportAsync(Query());
        Assert.Equal(2, all.Total);
        Assert.DoesNotContain(all.Items, i => i.WarehouseId == WarehouseA && i.ProductId == Product2);

        // 仓库筛选
        var byWarehouse = await service.GetInventoryMovementReportAsync(
            Query(warehouseId: WarehouseB));
        var warehouseRow = Assert.Single(byWarehouse.Items);
        Assert.Equal(WarehouseB, warehouseRow.WarehouseId);
        Assert.Equal("备用仓", warehouseRow.WarehouseName);
        Assert.Equal(7m, warehouseRow.CurrentQuantity);

        // 商品筛选 + 放开现存量限制：零数量行列出并标注
        var byProduct = await service.GetInventoryMovementReportAsync(
            Query(productId: Product2, onlyPositiveQuantity: false));
        var zeroRow = Assert.Single(byProduct.Items);
        Assert.Equal(WarehouseA, zeroRow.WarehouseId);
        Assert.Equal(0m, zeroRow.CurrentQuantity);
        Assert.Contains("现存量 <= 0", zeroRow.Note);

        // 关键字筛选：匹配商品资料名称
        var byKeyword = await service.GetInventoryMovementReportAsync(
            Query(keyword: "商品二", onlyPositiveQuantity: false));
        var keywordRow = Assert.Single(byKeyword.Items);
        Assert.Equal(Product2, keywordRow.ProductId);
        Assert.Equal("P002", keywordRow.ProductCode);

        // 关键字无匹配：返回空集而不是全量
        var none = await service.GetInventoryMovementReportAsync(Query(keyword: "不存在的商品"));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);
    }

    // ==================== 8. 分页：有界且合计只统计本页 ====================

    [Fact]
    public async Task Report_pages_rows_and_scopes_totals_to_the_returned_page()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        for (var i = 1; i <= 5; i++)
        {
            var productId = Product1 + i;
            SeedProduct(db, productId, $"P00{i}", $"商品{i}");
            SeedStock(db, WarehouseA, productId, i);
            SeedMovement(db, WarehouseA, productId, AsOf.AddDays(-1), direction: 1, quantity: i);
        }
        await db.SaveChangesAsync();

        var service = Service(db);
        var page1 = await service.GetInventoryMovementReportAsync(Query(page: 1, pageSize: 2));
        Assert.Equal(5, page1.Total);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(new[] { Product1 + 1L, Product1 + 2L }, page1.Items.Select(i => i.ProductId));
        Assert.Equal(3m, page1.PageCurrentQuantity);           // 1 + 2：只统计本页
        Assert.Equal(3m, page1.PageInboundQuantity);

        var page3 = await service.GetInventoryMovementReportAsync(Query(page: 3, pageSize: 2));
        var lastRow = Assert.Single(page3.Items);
        Assert.Equal(Product1 + 5L, lastRow.ProductId);
        Assert.Equal(5m, page3.PageCurrentQuantity);
        Assert.Equal(5, page3.Total);
        Assert.Equal(3, page3.TotalPages);

        // 超过上限的 pageSize 按上限截断（有界查询）
        var capped = await service.GetInventoryMovementReportAsync(Query(page: 1, pageSize: 100000));
        Assert.Equal(ReportDtos.InventoryMovementReportQuery.MaxPageSize, capped.PageSize);
        Assert.Equal(5, capped.Items.Count);
    }

    // ==================== 9. 参数校验与归一化 ====================

    [Fact]
    public void Query_normalization_validates_threshold_and_window_and_caps_page_size()
    {
        var threshold = new ReportDtos.InventoryMovementReportQuery { AsOfDate = AsOf, InactiveDays = 0 };
        var thresholdError = Assert.Throws<BusinessException>(() => threshold.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, thresholdError.Code);
        Assert.Contains("呆滞阈值", thresholdError.Message);

        var inverted = new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = AsOf, WindowStart = AsOf, WindowEnd = AsOf.AddDays(-5), InactiveDays = 90
        };
        Assert.Contains("不能晚于", Assert.Throws<BusinessException>(() => inverted.Normalize()).Message);

        var capped = new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = AsOf, InactiveDays = 90, Page = 0, PageSize = 100000, WindowEnd = AsOf.AddDays(10)
        };
        capped.Normalize();
        Assert.Equal(ReportDtos.InventoryMovementReportQuery.MaxPageSize, capped.PageSize);
        Assert.Equal(1, capped.Page);
        Assert.Equal(AsOf, capped.WindowEnd);                          // 窗口结束晚于截止日期：按截止日期截断
        Assert.Equal(AsOf.AddDays(-89), capped.WindowStart);           // 未传窗口开始：按默认窗口天数推算
    }

    // ==================== 10. 不做成本估值 ====================

    [Fact]
    public void Report_exposes_no_cost_or_valuation_fields()
    {
        var names = typeof(ReportDtos.InventoryMovementItem).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Cost", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Amount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Value", StringComparison.Ordinal));
        Assert.Contains("不估算库存成本", InventoryMovementSemantics.RuleText);
    }

    // ==================== 11. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_report_payload()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 12m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m);
        await db.SaveChangesAsync();

        var controller = new ReportController(Service(db));
        var result = await controller.InventoryMovement(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<ReportDtos.InventoryMovementReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        var data = payload.Data!;
        var row = Assert.Single(data.Items);
        Assert.Equal(12m, row.CurrentQuantity);
        Assert.Equal(10, row.InactivityDays);
        Assert.Equal(InventoryMovementSemantics.ClassActive, row.Classification);
        Assert.Equal(InventoryMovementSemantics.RuleText, data.Rule);
    }

    // ==================== 12. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "inventory-movement-report.js"));

        Assert.Contains("function openInventoryMovementReport()", reportJs);
        Assert.Contains("/api/reports/inventory-movement?", reportJs);
        Assert.Contains("function loadImrWarehouses()", reportJs);
        Assert.Contains("function imrRenderTable(data)", reportJs);
        Assert.Contains("function exportImrCsv()", reportJs);
        Assert.Contains("'未知'", reportJs);                            // 未知渲染：不回落为 0
        Assert.Contains("IMR_CLASS_LABELS", reportJs);
        Assert.Contains("IMR_HISTORY_LABELS", reportJs);

        // 商品筛选是「商品资料下拉」（复用既有商品资料接口，keyword 匹配编码 / 名称），使用者不必先知道商品 Id；
        // 下拉只登记真实商品资料，接口不可用时仅降级为「留空 / 关键字」，不臆造编码或名称
        Assert.Contains("function loadImrProductOptions(keyword)", reportJs);
        Assert.Contains("/api/base/products?page=1&pageSize=50", reportJs);
        Assert.Contains("<select id=\"imr-product\"", reportJs);
        Assert.Contains("oninput=\"loadImrProductOptions(this.value)\"", reportJs);

        // 五项筛选控件齐备：仓库 / 商品 / 截止日期 / 移动窗口 / 呆滞阈值
        foreach (var id in new[]
                 {
                     "imr-warehouse", "imr-product", "imr-keyword", "imr-asof", "imr-window-start",
                     "imr-window-end", "imr-inactive", "imr-only-positive"
                 })
        {
            Assert.Contains(id, reportJs);
        }

        // 工具栏入口（库存查询页）与脚本注册
        var modulesDoc2 = File.ReadAllText(Path.Combine(js, "modules-doc2.js"));
        Assert.Contains("openInventoryMovementReport", modulesDoc2);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/inventory-movement-report.js", index);
    }

    // ==================== 13. 有界查询与只读：数据集访问次数与行数无关，且全程不写库 ====================

    [Fact]
    public async Task Report_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 5m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-1), direction: 1, quantity: 5m);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var service = new ReportService(counting.Proxy);

        var single = await service.GetInventoryMovementReportAsync(Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        // 常数级访问：库存行 + 窗口台账 + 历史台账 + 仓库名 + 商品信息（每条都是一次整表级查询）
        Assert.Equal(5, singleReads);

        // 再补 300 个库存行 + 对应台账（跨多页）：同一报表的数据集访问次数必须保持不变（无逐行查库）
        for (var i = 1; i <= 300; i++)
        {
            var productId = Product1 + i;
            SeedProduct(db, productId, $"P{i:0000}", $"商品{i}");
            SeedStock(db, WarehouseA, productId, i);
            SeedMovement(db, WarehouseA, productId, AsOf.AddDays(-2), direction: 1, quantity: i);
        }
        await db.SaveChangesAsync();

        var large = await service.GetInventoryMovementReportAsync(Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(ReportDtos.InventoryMovementReportQuery.MaxPageSize, large.Items.Count);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);                                                  // 行数 / 页大小变化不改变访问次数

        // 关键字走 EXISTS 子查询（不逐行回表）：整页 200 行与单行请求的访问次数一致
        await service.GetInventoryMovementReportAsync(Query(keyword: "P", pageSize: 200));       // 预热查询管道
        var beforeFull = counting.DatasetReads;
        var keywordFull = await service.GetInventoryMovementReportAsync(Query(keyword: "P", pageSize: 200));
        var keywordFullReads = counting.DatasetReads - beforeFull;
        var beforeSingle = counting.DatasetReads;
        var keywordSingle = await service.GetInventoryMovementReportAsync(Query(keyword: "P", pageSize: 1));
        var keywordSingleReads = counting.DatasetReads - beforeSingle;
        Assert.Equal(301, keywordFull.Total);
        Assert.Equal(200, keywordFull.Items.Count);
        Assert.Equal(301, keywordSingle.Total);                 // 关键字只影响筛选取值，总数与分页无关
        Assert.Single(keywordSingle.Items);                     // 单行页
        Assert.Equal(keywordFullReads, keywordSingleReads);     // 整页 200 行与单行：数据集访问次数一致

        Assert.Equal(0, counting.WriteCalls);                                                    // 只读报表：没有一次 SaveChanges
    }

    // ==================== 14. 移动窗口是闭区间：首尾当天（含时间部分）都计入 ====================

    [Fact]
    public async Task Report_counts_window_boundary_days_at_both_ends()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 5m);
        var start = AsOf.AddDays(-9);
        SeedMovement(db, WarehouseA, Product1, start.AddDays(-1), direction: 1, quantity: 1m);   // 窗口前：不计
        SeedMovement(db, WarehouseA, Product1, start.AddHours(9), direction: 1, quantity: 2m);   // 首日（带时间部分）
        SeedMovement(db, WarehouseA, Product1, AsOf.AddHours(23), direction: 1, quantity: 3m);   // 末日（带时间部分）
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(
            Query(windowStart: start, windowEnd: AsOf, inactiveDays: 90));

        var row = Assert.Single(report.Items);
        Assert.Equal(5m, row.InboundQuantity);                 // 2 + 3：首尾当天都计入，窗口前的 1 不计
        Assert.Equal(0m, row.OutboundQuantity);
        Assert.Equal(5m, row.NetQuantity);
        Assert.Equal(2, row.MovementCount);
        Assert.Equal(AsOf.AddHours(23), row.LastMovementDate);
        Assert.Equal(0, row.InactivityDays);
        Assert.Equal(InventoryMovementSemantics.HistoryLedger, row.HistoryStatus);
    }

    // ==================== 15. 红字跨窗口边界：只计窗口内的台账行，不跨窗口配对 ====================

    [Fact]
    public async Task Report_does_not_pair_reversals_across_the_window_boundary()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 0m);

        // 原入库（+10）在窗口之外、红字冲销（-10）在窗口之内：窗口内如实反映「只有红字腿」，
        // 报表不跨窗口配对、也不二次扣减——每一行仍可逐笔回溯到台账
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-60), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-3), direction: -1, quantity: 10m,
            isReversal: true, reversalOf: 1, id: 2);
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryMovementReportAsync(
            Query(windowStart: AsOf.AddDays(-9), windowEnd: AsOf, inactiveDays: 90, onlyPositiveQuantity: false));

        var row = Assert.Single(report.Items);
        Assert.Equal(0m, row.InboundQuantity);                 // 窗口内没有入库腿
        Assert.Equal(10m, row.OutboundQuantity);
        Assert.Equal(-10m, row.NetQuantity);
        Assert.Equal(1, row.MovementCount);
        Assert.Equal(1, row.ReversalCount);
        Assert.Equal(AsOf.AddDays(-3), row.LastMovementDate);  // 红字流水同样是台账事实
        Assert.Equal(3, row.InactivityDays);
        Assert.Contains("红字冲销", row.Note);
        Assert.Contains("未做二次扣减", row.Note);
    }

    // ==================== 助手 ====================

    private static ReportService Service(ErpDbContext db) => new(db);

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>默认查询：截止 AsOf、窗口 AsOf 前 90 天、阈值 90 天、仅现存量 &gt; 0</summary>
    private static ReportDtos.InventoryMovementReportQuery Query(DateTime? asOf = null, DateTime? windowStart = null,
        DateTime? windowEnd = null, long? warehouseId = null, long? productId = null, string? keyword = null,
        int inactiveDays = 90, bool onlyPositiveQuantity = true, int page = 1, int pageSize = 50)
    {
        var end = windowEnd ?? asOf ?? AsOf;
        return new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = asOf ?? AsOf,
            WindowStart = windowStart ?? end.AddDays(-89),
            WindowEnd = end,
            WarehouseId = warehouseId,
            ProductId = productId,
            Keyword = keyword,
            InactiveDays = inactiveDays,
            OnlyPositiveQuantity = onlyPositiveQuantity,
            Page = page,
            PageSize = pageSize
        };
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name)
        => db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = "PCS"
        });

    private static void SeedWarehouse(ErpDbContext db, long id, string name)
        => db.BaseWarehouses.Add(new BaseWarehouse
        {
            Id = id, WarehouseCode = $"WH{id}", WarehouseName = name
        });

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity)
        => db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId, ProductId = productId, Quantity = quantity, AvailableQuantity = quantity
        });

    /// <summary>写入一条库存流水（方向 1=入库 / -1=出库；红字行通过 isReversal + reversalOf 表达）</summary>
    private static StockMovement SeedMovement(ErpDbContext db, long warehouseId, long productId, DateTime movementDate,
        int direction, decimal quantity, bool isReversal = false, long? reversalOf = null, long? id = null)
    {
        var movement = new StockMovement
        {
            Id = id ?? 0,
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
            ReversalOfMovementId = reversalOf
        };
        db.StockMovements.Add(movement);
        return movement;
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：统计报表访问数据集（<c>DbSet</c> 属性）的次数与写入次数，
    /// 用于断言「分页 / 有界查询」与「无逐行查库、只读不写库」；不引入测试依赖，也不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（报表服务按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次报表实际发起的数据集查询次数</summary>
        public int DatasetReads { get; private set; }

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读报表恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal)) DatasetReads++;
            return targetMethod.Invoke(_inner, args);
        }
    }
}
