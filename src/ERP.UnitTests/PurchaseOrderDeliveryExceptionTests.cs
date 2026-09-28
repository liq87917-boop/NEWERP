using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-099 采购交期异常工作台（只读派生）单元测试：
/// 逾期 / 即将到期 / 在途正常 / 已收齐的交期状态派生、晚确认（确认晚于要求 / 要求已过仍未确认）、
/// 部分收货仍为未收齐、仅已审核未删除入库计入已收、缺日期未知、只读不写库、
/// 分页有界与稳定排序、供应商 / 关键字筛选、批量取数（无逐单查库）、接口端点与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderDeliveryExceptionTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 990001L;
    private const long SupplierB = 990002L;
    private const long ProductA = 990101L;

    // ==================== 1. 交期状态派生：逾期 / 即将到期 / 在途正常 / 已收齐 ====================

    [Fact]
    public async Task Workspace_derives_overdue_due_soon_on_schedule_and_received_states()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");

        SeedOrder(db, "PO-OVER", SupplierA, deliveryDate: AsOf.AddDays(-10), confirmedDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedOrder(db, "PO-SOON", SupplierA, deliveryDate: AsOf.AddDays(3), confirmedDate: AsOf.AddDays(3), lines: (ProductA, 10m));
        SeedOrder(db, "PO-OK", SupplierA, deliveryDate: AsOf.AddDays(20), confirmedDate: AsOf.AddDays(20), lines: (ProductA, 10m));
        var received = SeedOrder(db, "PO-RECV", SupplierA, deliveryDate: AsOf.AddDays(-10), confirmedDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockIn(db, "RK-RECV", received.Id, DocumentStatus.Approved, lines: (ProductA, 10m));

        var report = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query());

        var byNo = report.Items.ToDictionary(i => i.OrderNo);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryOverdue, byNo["PO-OVER"].DeliveryStatus);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryDueSoon, byNo["PO-SOON"].DeliveryStatus);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryOnSchedule, byNo["PO-OK"].DeliveryStatus);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryReceived, byNo["PO-RECV"].DeliveryStatus);
    }

    // ==================== 2. 晚确认：确认晚于要求 / 要求已过仍未确认 ====================

    [Fact]
    public async Task Workspace_marks_late_confirmation_when_confirmed_later_than_requested_or_missing_confirmation()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");

        // 供应商确认交期晚于要求交期
        SeedOrder(db, "PO-LATE1", SupplierA, deliveryDate: AsOf.AddDays(5), confirmedDate: AsOf.AddDays(12), lines: (ProductA, 10m));
        // 要求交期已过 as-of，供应商仍未确认
        SeedOrder(db, "PO-LATE2", SupplierA, deliveryDate: AsOf.AddDays(-6), confirmedDate: null, lines: (ProductA, 10m));

        var report = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var byNo = report.Items.ToDictionary(i => i.OrderNo);

        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryLateConfirmation, byNo["PO-LATE1"].DeliveryStatus);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryLateConfirmation, byNo["PO-LATE2"].DeliveryStatus);
        Assert.Contains("晚于要求交期", byNo["PO-LATE1"].Note);
        Assert.Contains("尚未确认交期", byNo["PO-LATE2"].Note);
    }

    // ==================== 3. 部分收货仍为未收齐（open），按日期继续判定 ====================

    [Fact]
    public async Task Partial_receipt_remains_open_and_is_not_received()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-PART", SupplierA, deliveryDate: AsOf.AddDays(-10), confirmedDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockIn(db, "RK-PART", order.Id, DocumentStatus.Approved, lines: (ProductA, 5m));

        var report = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, item.ReceiptStatus);
        Assert.Equal(5m, item.ReceivedQuantity);
        Assert.Equal(10m, item.OrderedQuantity);
        Assert.Equal(5m, item.OutstandingQuantity);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryOverdue, item.DeliveryStatus);
    }

    // ==================== 4. 仅已审核且未删除入库计入已收 ====================

    [Fact]
    public async Task Only_approved_nondeleted_stock_ins_count_toward_receipt()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-MIX", SupplierA, deliveryDate: AsOf.AddDays(-10), confirmedDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockIn(db, "RK-APPROVED", order.Id, DocumentStatus.Approved, lines: (ProductA, 4m));             // 计入已收
        SeedStockIn(db, "RK-PENDING", order.Id, DocumentStatus.Submitted, lines: (ProductA, 3m));           // 待审，不计入已收
        SeedStockIn(db, "RK-DELETED", order.Id, DocumentStatus.Approved, deleted: true, lines: (ProductA, 3m)); // 已删除，不计入

        var report = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(4m, item.ReceivedQuantity);
        Assert.Equal(3m, item.PendingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, item.ReceiptStatus);
    }

    // ==================== 5. 缺日期 → 未知（不推断） ====================

    [Fact]
    public async Task Missing_dates_are_shown_as_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-NODATE", SupplierA, deliveryDate: null, confirmedDate: null, lines: (ProductA, 10m));

        var report = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryUnknown, item.DeliveryStatus);
        Assert.Null(item.RequestedDate);
        Assert.Null(item.ConfirmedDate);
        Assert.Contains("缺少", item.Note);
    }

    // ==================== 6. 只读不写库 ====================

    [Fact]
    public async Task Workspace_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-NOWRITE", SupplierA, deliveryDate: AsOf.AddDays(-10), confirmedDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockIn(db, "RK-NOWRITE", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        await db.SaveChangesAsync();

        var ordersBefore = db.PurchaseOrders.Count();
        var stockInsBefore = db.StockIns.Count();

        _ = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query());

        Assert.Equal(ordersBefore, db.PurchaseOrders.Count());
        Assert.Equal(stockInsBefore, db.StockIns.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(),
            e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    // ==================== 7. 分页有界与稳定排序 ====================

    [Fact]
    public async Task Paging_is_bounded_and_stable()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");

        SeedOrder(db, "PO-B-1", SupplierB, orderDate: AsOf, deliveryDate: AsOf, confirmedDate: AsOf, lines: (ProductA, 1m));
        SeedOrder(db, "PO-A-1", SupplierA, orderDate: AsOf.AddDays(2), deliveryDate: AsOf, confirmedDate: AsOf, lines: (ProductA, 1m));
        SeedOrder(db, "PO-A-2", SupplierA, orderDate: AsOf.AddDays(1), deliveryDate: AsOf, confirmedDate: AsOf, lines: (ProductA, 1m));

        var page1 = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query(pageSize: 2));
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(SupplierA, page1.Items[0].SupplierId);
        Assert.Equal("PO-A-2", page1.Items[0].OrderNo);   // 同供应商按订单日期升序
        Assert.Equal("PO-A-1", page1.Items[1].OrderNo);

        var page2 = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        Assert.Single(page2.Items);
        Assert.Equal("PO-B-1", page2.Items[0].OrderNo);

        // 分页参数有界：超过上限钳制到 200
        var clamped = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query(pageSize: 9999));
        Assert.Equal(PurchaseOrderDeliveryExceptionQuery.MaxPageSize, clamped.PageSize);
    }

    // ==================== 8. 供应商 / 关键字筛选 ====================

    [Fact]
    public async Task Supplier_and_keyword_filters_narrow_results()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, "PO-FILT-1", SupplierA, deliveryDate: AsOf, confirmedDate: AsOf, lines: (ProductA, 1m));
        SeedOrder(db, "PO-FILT-2", SupplierB, deliveryDate: AsOf, confirmedDate: AsOf, lines: (ProductA, 1m));

        var bySupplier = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query(supplierId: SupplierA));
        Assert.Equal(1, bySupplier.Total);
        Assert.Equal(SupplierA, Assert.Single(bySupplier.Items).SupplierId);

        var byKeyword = await PurchaseOrderDeliveryExceptions.ForQueryAsync(db, Query(keyword: "FILT-2"));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("PO-FILT-2", Assert.Single(byKeyword.Items).OrderNo);
    }

    // ==================== 9. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_delivery_exception_payload()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-CTRL", SupplierA, deliveryDate: AsOf.AddDays(-10), confirmedDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));
        var result = await controller.DeliveryExceptions(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<PurchaseOrderDeliveryExceptionReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(PurchaseOrderDeliveryExceptionSemantics.DeliveryOverdue,
            Assert.Single(payload.Data.Items).DeliveryStatus);
    }

    // ==================== 10. 只读 GET 路由与口径边界 ====================

    [Fact]
    public void Report_exposes_only_read_get_route_and_states_boundary()
    {
        var methods = typeof(PurchaseOrderController).GetMethods();
        var getRoutes = methods.SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty).ToList();
        Assert.Contains("delivery-exceptions", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "delivery-exceptions"));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("delivery", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("delivery", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("delivery", StringComparison.Ordinal));

        Assert.Contains("只读派生", PurchaseOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("不改写", PurchaseOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("未知", PurchaseOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("逾期", PurchaseOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("晚确认", PurchaseOrderDeliveryExceptionSemantics.RuleText);

        foreach (var type in new[]
                 {
                     typeof(PurchaseOrderDeliveryExceptionItem),
                     typeof(PurchaseOrderDeliveryExceptionReport),
                     typeof(PurchaseOrderDeliveryExceptionCounts),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Payable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.Ordinal));
        }
    }

    // ==================== 11. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "purchase-order-delivery-exceptions.js"));

        Assert.Contains("function openPurchaseOrderDeliveryExceptions()", reportJs);
        Assert.Contains("/api/purchase-orders/delivery-exceptions?", reportJs);
        Assert.Contains("function pdeRenderKpi(data)", reportJs);
        Assert.Contains("function pdeRenderTable(data)", reportJs);
        Assert.Contains("function pdeRenderPagination(data)", reportJs);
        Assert.Contains("function exportPdeCsv()", reportJs);
        Assert.Contains("PDE_STATUS_LABELS", reportJs);
        Assert.Contains("PDE_RECEIPT_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);

        foreach (var id in new[] { "pde-supplier", "pde-asof", "pde-keyword", "pde-pagesize" })
            Assert.Contains(id, reportJs);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openPurchaseOrderDeliveryExceptions", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/purchase-order-delivery-exceptions.js", index);
    }

    // ==================== 12. 批量取数：无逐单查库、只读不写库 ====================

    [Fact]
    public async Task Workspace_batches_queries_without_per_order_db_calls()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 0; i < 5; i++)
        {
            var order = SeedOrder(db, $"PO-BATCH-{i}", SupplierA, deliveryDate: AsOf, confirmedDate: AsOf, lines: (ProductA, 1m));
            SeedStockIn(db, $"RK-BATCH-{i}", order.Id, DocumentStatus.Approved, lines: (ProductA, 1m));
        }

        var counting = CountingDbContext.Wrap(db);

        var single = await PurchaseOrderDeliveryExceptions.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;

        var large = await PurchaseOrderDeliveryExceptions.ForQueryAsync(counting.Proxy, Query(pageSize: 5));
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Single(single.Items);
        Assert.Equal(5, large.Items.Count);
        Assert.Equal(singleReads, largeReads);   // 固定次数数据集访问：无逐单查库
        Assert.Equal(0, counting.WriteCalls);     // 只读：没有一次 SaveChanges
    }

    // ==================== 助手 ====================

    private static PurchaseOrderDeliveryExceptionQuery Query(long? supplierId = null, DateTime? asOfDate = null,
        string? keyword = null, int page = 1, int pageSize = 50)
        => new()
        {
            SupplierId = supplierId,
            AsOfDate = asOfDate ?? AsOf,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier { Id = id, SupplierCode = $"S{id}", SupplierName = name });

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId,
        DateTime? orderDate = null, DateTime? deliveryDate = null, DateTime? confirmedDate = null,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            DeliveryDate = deliveryDate,
            SupplierConfirmedDate = confirmedDate,
            TotalAmount = lines.Sum(l => l.Quantity),
            Status = DocumentStatus.Approved,
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
                UnitPrice = 1m,
                Amount = quantity,
            });
        }
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId,
        DocumentStatus status, bool deleted = false, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = AsOf.AddDays(-5),
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = 1,
            Status = status,
            IsDeleted = deleted,
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = stockIn.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }
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
