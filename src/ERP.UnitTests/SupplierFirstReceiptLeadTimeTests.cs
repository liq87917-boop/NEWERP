using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-109 供应商首收交期（只读派生）单元测试：
/// 多张入库取最早有效入库、缺链接 / 未审核 / 已删除 / 供应商不一致 / 早于订单日期（负间隔）等异常证据不计入首收、
/// 只筛选已审核采购订单并按供应商 / 订单日期过滤、分页有界与稳定排序、批量取数（无逐单查库）、
/// 只读不写库、接口端点与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SupplierFirstReceiptLeadTimeTests
{
    private static readonly DateTime Day = new(2026, 9, 10);
    private const long SupplierA = 991001L;
    private const long SupplierB = 991002L;

    // ==================== 1. 多张入库：取最早有效已审核入库 ====================

    [Fact]
    public async Task Derives_first_valid_approved_receipt_across_multiple_receipts()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-FIRST", SupplierA);
        SeedStockIn(db, "RK-LATE", order.Id, DocumentStatus.Approved, Day.AddDays(5));
        SeedStockIn(db, "RK-EARLY", order.Id, DocumentStatus.Approved, Day.AddDays(1));
        SeedStockIn(db, "RK-MID", order.Id, DocumentStatus.Approved, Day.AddDays(3));

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal("RK-EARLY", item.FirstReceiptNo);
        Assert.Equal(Day.AddDays(1), item.FirstReceiptDate);
        Assert.Equal(1, item.ElapsedDays);
        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeReceived, item.LeadTimeStatus);
        Assert.False(item.IsAnomalous);
    }

    // ==================== 2. 缺链接：首收不可用 ====================

    [Fact]
    public async Task Missing_links_remain_unavailable()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-NONE", SupplierA);

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Null(item.FirstReceiptNo);
        Assert.Null(item.FirstReceiptDate);
        Assert.Null(item.ElapsedDays);
        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeUnavailable, item.LeadTimeStatus);
        Assert.False(item.IsAnomalous);
    }

    // ==================== 3. 未审核入库不计入首收 ====================

    [Fact]
    public async Task Unapproved_receipts_are_not_counted()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var orderOnlyPending = SeedOrder(db, "PO-PENDING", SupplierA);
        SeedStockIn(db, "RK-PENDING", orderOnlyPending.Id, DocumentStatus.Submitted, Day.AddDays(2));

        var orderMixed = SeedOrder(db, "PO-MIXED", SupplierA);
        SeedStockIn(db, "RK-APPROVED", orderMixed.Id, DocumentStatus.Approved, Day.AddDays(4));
        SeedStockIn(db, "RK-SUBMITTED", orderMixed.Id, DocumentStatus.Submitted, Day.AddDays(1));

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query());
        var byNo = report.Items.ToDictionary(i => i.OrderNo);

        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeUnavailable, byNo["PO-PENDING"].LeadTimeStatus);
        Assert.True(byNo["PO-PENDING"].IsAnomalous);
        Assert.Contains(SupplierFirstReceiptLeadTimeRules.AnomalyUnapprovedReceipt, byNo["PO-PENDING"].Anomalies);

        Assert.Equal("RK-APPROVED", byNo["PO-MIXED"].FirstReceiptNo);
        Assert.Equal(4, byNo["PO-MIXED"].ElapsedDays);
        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeReceived, byNo["PO-MIXED"].LeadTimeStatus);
        Assert.True(byNo["PO-MIXED"].IsAnomalous);
        Assert.Contains(SupplierFirstReceiptLeadTimeRules.AnomalyUnapprovedReceipt, byNo["PO-MIXED"].Anomalies);
    }

    // ==================== 4. 已删除入库不计入首收 ====================

    [Fact]
    public async Task Deleted_receipts_are_excluded_and_flagged()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-DELETED", SupplierA);
        SeedStockIn(db, "RK-DELETED", order.Id, DocumentStatus.Approved, Day.AddDays(2), deleted: true);

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeUnavailable, item.LeadTimeStatus);
        Assert.Null(item.FirstReceiptDate);
        Assert.True(item.IsAnomalous);
        Assert.Contains(SupplierFirstReceiptLeadTimeRules.AnomalyDeletedReceipt, item.Anomalies);
    }

    // ==================== 5. 供应商不一致入库不计入首收 ====================

    [Fact]
    public async Task Wrong_supplier_receipts_are_excluded_and_flagged()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        var order = SeedOrder(db, "PO-WRONGSUP", SupplierA);
        SeedStockIn(db, "RK-WRONGSUP", order.Id, DocumentStatus.Approved, Day.AddDays(2), supplierId: SupplierB);

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeUnavailable, item.LeadTimeStatus);
        Assert.Null(item.FirstReceiptDate);
        Assert.True(item.IsAnomalous);
        Assert.Contains(SupplierFirstReceiptLeadTimeRules.AnomalyWrongSupplierReceipt, item.Anomalies);
    }

    // ==================== 6. 早于订单日期：负间隔不钳制为 0 ====================

    [Fact]
    public async Task Pre_order_receipt_yields_negative_interval_not_clamped()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-PRE", SupplierA);
        SeedStockIn(db, "RK-PRE", order.Id, DocumentStatus.Approved, Day.AddDays(-2));

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal(Day.AddDays(-2), item.FirstReceiptDate);
        Assert.Equal(-2, item.ElapsedDays);
        Assert.Equal(SupplierFirstReceiptLeadTimeRules.LeadTimeNegativeInterval, item.LeadTimeStatus);
        Assert.True(item.IsAnomalous);
        Assert.Contains(SupplierFirstReceiptLeadTimeRules.AnomalyPreOrderReceipt, item.Anomalies);
    }


    // ==================== 7. 只筛选已审核采购订单，并按供应商 / 订单日期过滤 ====================

    [Fact]
    public async Task Filters_approved_orders_by_supplier_and_date()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");

        var approvedA = SeedOrder(db, "PO-APPROVED-A", SupplierA, Day.AddDays(-1));
        SeedStockIn(db, "RK-A", approvedA.Id, DocumentStatus.Approved, Day.AddDays(1));
        SeedOrder(db, "PO-APPROVED-B", SupplierB, Day.AddDays(-1));
        // 未审核采购订单不应进入报表
        SeedOrder(db, "PO-PENDING-ORDER", SupplierA, Day.AddDays(-1), status: DocumentStatus.Pending);

        var report = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db,
            Query(supplierId: SupplierA, orderDateFrom: Day.AddDays(-2), orderDateTo: Day));

        Assert.Equal(1, report.Total);
        var item = Assert.Single(report.Items);
        Assert.Equal("PO-APPROVED-A", item.OrderNo);

        // 日期区间过滤：结束日期更早时应无结果
        var empty = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db,
            Query(orderDateTo: Day.AddDays(-2)));
        Assert.Equal(0, empty.Total);
    }

    // ==================== 8. 分页有界与稳定排序 ====================

    [Fact]
    public async Task Paging_is_bounded_and_stably_ordered()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");

        SeedOrder(db, "PO-A-OLD", SupplierA, Day.AddDays(-3));
        SeedOrder(db, "PO-A-NEW", SupplierA, Day.AddDays(-1));
        SeedOrder(db, "PO-B", SupplierB, Day.AddDays(-2));

        var page1 = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query(page: 1, pageSize: 2));
        var page2 = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(db, Query(page: 2, pageSize: 2));

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        // 稳定排序：供应商 → 订单日期 → 单据 Id
        Assert.Equal(new[] { "PO-A-OLD", "PO-A-NEW" }, page1.Items.Select(i => i.OrderNo).ToArray());
        Assert.Equal("PO-B", page2.Items.Single().OrderNo);

        // 有界：pageSize 超上限被钳制到 MaxPageSize
        var query = Query(pageSize: 99999);
        query.Normalize();
        Assert.Equal(SupplierFirstReceiptLeadTimeQuery.MaxPageSize, query.PageSize);

        // 订单日期区间倒置被拒绝
        var inverted = Query(orderDateFrom: Day.AddDays(1), orderDateTo: Day);
        Assert.Throws<BusinessException>(() => inverted.Normalize());
    }

    // ==================== 9. 只读不写库 + 批量取数（无逐单查库） ====================

    [Fact]
    public async Task Report_is_read_only_and_batches_without_per_order_calls()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 0; i < 5; i++)
        {
            var order = SeedOrder(db, $"PO-BATCH-{i}", SupplierA, Day.AddDays(-i));
            SeedStockIn(db, $"RK-BATCH-{i}", order.Id, DocumentStatus.Approved, Day.AddDays(2));
        }

        var counting = CountingDbContext.Wrap(db);

        var single = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;

        var large = await SupplierFirstReceiptLeadTimeService.ForQueryAsync(counting.Proxy, Query(pageSize: 5));
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Single(single.Items);
        Assert.Equal(5, large.Items.Count);
        Assert.Equal(singleReads, largeReads);   // 固定次数数据集访问：无逐单查库
        Assert.Equal(0, counting.WriteCalls);     // 只读：没有一次 SaveChanges
    }


    // ==================== 10. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_lead_time_payload()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-CTRL", SupplierA);
        SeedStockIn(db, "RK-CTRL", order.Id, DocumentStatus.Approved, Day.AddDays(3));
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));
        var result = await controller.SupplierFirstReceiptLeadTimes(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<SupplierFirstReceiptLeadTimeReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(3, Assert.Single(payload.Data.Items).ElapsedDays);
    }

    // ==================== 11. 只读 GET 路由与口径边界 ====================

    [Fact]
    public void Report_exposes_only_read_get_route_and_states_boundary()
    {
        var methods = typeof(PurchaseOrderController).GetMethods();
        var getRoutes = methods.SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty).ToList();
        Assert.Contains("first-receipt-lead-times", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "first-receipt-lead-times"));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("lead-time", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("lead-time", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("lead-time", StringComparison.Ordinal));

        Assert.Contains("只读派生", SupplierFirstReceiptLeadTimeRules.RuleText);
        Assert.Contains("PurchaseOrderId", SupplierFirstReceiptLeadTimeRules.RuleText);
        Assert.Contains("未知", SupplierFirstReceiptLeadTimeRules.RuleText);
        Assert.Contains("不是完整交付完成度", SupplierFirstReceiptLeadTimeRules.BoundaryText);

        foreach (var type in new[]
                 {
                     typeof(SupplierFirstReceiptLeadTimeItem),
                     typeof(SupplierFirstReceiptLeadTimeReport),
                     typeof(SupplierFirstReceiptLeadTimeCounts),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Payable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("OnTime", StringComparison.Ordinal));
        }
    }


    // ==================== 12. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "supplier-first-receipt-lead-times.js"));

        Assert.Contains("function openSupplierFirstReceiptLeadTimes()", reportJs);
        Assert.Contains("/api/purchase-orders/first-receipt-lead-times?", reportJs);
        Assert.Contains("function sfrtRenderKpi(data)", reportJs);
        Assert.Contains("function sfrtRenderTable(data)", reportJs);
        Assert.Contains("function sfrtRenderPagination(data)", reportJs);
        Assert.Contains("function exportSfrtCsv()", reportJs);
        Assert.Contains("SFRT_STATUS_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);
        Assert.Contains("navigate('purchase-order'", reportJs);
        Assert.Contains("navigate('stock-in'", reportJs);

        foreach (var id in new[] { "sfrt-supplier", "sfrt-date-from", "sfrt-date-to", "sfrt-keyword", "sfrt-pagesize" })
            Assert.Contains(id, reportJs);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSupplierFirstReceiptLeadTimes", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/supplier-first-receipt-lead-times.js", index);
    }

    // ==================== 助手 ====================

    private static SupplierFirstReceiptLeadTimeQuery Query(long? supplierId = null,
        DateTime? orderDateFrom = null, DateTime? orderDateTo = null, string? keyword = null,
        int page = 1, int pageSize = 50)
        => new()
        {
            SupplierId = supplierId,
            OrderDateFrom = orderDateFrom,
            OrderDateTo = orderDateTo,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize,
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier { Id = id, SupplierCode = $"S{id}", SupplierName = name });

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? Day,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            Status = status,
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId,
        DocumentStatus status, DateTime? stockInDate = null, bool deleted = false, long? supplierId = null)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = stockInDate ?? Day,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplierId ?? SupplierA,
            WarehouseId = 1,
            Status = status,
            IsDeleted = deleted,
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        return stockIn;
    }


    /// <summary>用于断言「分页 / 有界查询」「无逐单查库」与「只读不写库」；不改动生产代码。</summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int DatasetReads { get; private set; }
        public int WriteCalls { get; private set; }

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

