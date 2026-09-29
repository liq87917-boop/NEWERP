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
/// ERP-102 销售交期异常工作台（只读派生）单元测试：
/// 逾期 / 即将到期 / 在途正常 / 已出齐的交期状态派生、明细交期优先于订单头交期、日期边界、
/// 部分出货仍为未出齐、仅已审核未删除出库计入已出、缺日期未知、出货证据截断未知、只读不写库、
/// 分页有界与稳定排序、客户 / 关键字筛选、批量取数（固定次数数据集访问、无逐单查库）、接口端点与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SalesOrderDeliveryExceptionTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 980001L;
    private const long CustomerB = 980002L;
    private const long ProductA = 980101L;
    private const long ProductB = 980102L;

    // ==================== 1. 交期状态派生：逾期 / 即将到期 / 在途正常 / 已出齐 ====================

    [Fact]
    public async Task Workspace_derives_overdue_due_soon_on_schedule_and_fulfilled_states()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");

        SeedOrder(db, "SO-OVER", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedOrder(db, "SO-SOON", CustomerA, headerDeliveryDate: AsOf.AddDays(3), lines: (ProductA, 10m));
        SeedOrder(db, "SO-OK", CustomerA, headerDeliveryDate: AsOf.AddDays(20), lines: (ProductA, 10m));
        var fulfilled = SeedOrder(db, "SO-FUL", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockOut(db, "CK-FUL", fulfilled.Id, DocumentStatus.Approved, lines: (ProductA, 10m));

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());

        var byNo = report.Items.ToDictionary(i => i.OrderNo);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOverdue, byNo["SO-OVER"].DeliveryStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryDueSoon, byNo["SO-SOON"].DeliveryStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOnSchedule, byNo["SO-OK"].DeliveryStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryFulfilled, byNo["SO-FUL"].DeliveryStatus);
    }

    // ==================== 2. 明细交期优先于订单头交期 ====================

    [Fact]
    public async Task Workspace_line_delivery_date_takes_precedence_over_header()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");

        // 订单头交期在很远的未来（本应为在途），但明细行交期已过 → 行交期优先，判逾期
        var order = SeedOrder(db, "SO-LINE", CustomerA, headerDeliveryDate: AsOf.AddDays(30), lines: (ProductA, 10m));
        SeedDetail(db, order.Id, ProductB, 5m, lineDeliveryDate: AsOf.AddDays(-10));

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(AsOf.AddDays(30).Date, item.HeaderDeliveryDate);
        Assert.Single(item.LineDeliveryDates);
        Assert.Equal(AsOf.AddDays(-10).Date, item.EffectiveDeliveryDate);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOverdue, item.DeliveryStatus);
        Assert.Contains("已过", item.Note);
    }

    // ==================== 3. 日期边界：含当天 / 含第 7 天 ====================

    [Fact]
    public async Task Workspace_date_boundaries_are_inclusive()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");

        SeedOrder(db, "SO-B0", CustomerA, headerDeliveryDate: AsOf, lines: (ProductA, 1m));              // 当天 → due_soon
        SeedOrder(db, "SO-B1", CustomerA, headerDeliveryDate: AsOf.AddDays(-1), lines: (ProductA, 1m));  // 前一天 → overdue
        SeedOrder(db, "SO-B2", CustomerA, headerDeliveryDate: AsOf.AddDays(7), lines: (ProductA, 1m));   // 第 7 天 → due_soon
        SeedOrder(db, "SO-B3", CustomerA, headerDeliveryDate: AsOf.AddDays(8), lines: (ProductA, 1m));   // 第 8 天 → on_schedule

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var byNo = report.Items.ToDictionary(i => i.OrderNo);

        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryDueSoon, byNo["SO-B0"].DeliveryStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOverdue, byNo["SO-B1"].DeliveryStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryDueSoon, byNo["SO-B2"].DeliveryStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOnSchedule, byNo["SO-B3"].DeliveryStatus);
    }
    // ==================== 4. 部分出货仍为未出齐（open），按日期继续判定 ====================

    [Fact]
    public async Task Partial_shipment_remains_open_and_is_not_fulfilled()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-PART", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockOut(db, "CK-PART", order.Id, DocumentStatus.Approved, lines: (ProductA, 5m));

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(SalesOrderProgress.ShipmentPartial, item.ShipmentStatus);
        Assert.Equal(5m, item.ApprovedShippedQuantity);
        Assert.Equal(10m, item.OrderedQuantity);
        Assert.Equal(5m, item.OutstandingQuantity);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOverdue, item.DeliveryStatus);
    }

    // ==================== 5. 仅已审核且未删除出库计入已出 ====================

    [Fact]
    public async Task Only_approved_nondeleted_stock_outs_count_toward_shipment()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-MIX", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockOut(db, "CK-APPROVED", order.Id, DocumentStatus.Approved, lines: (ProductA, 4m));               // 计入已出
        SeedStockOut(db, "CK-SUBMITTED", order.Id, DocumentStatus.Submitted, lines: (ProductA, 3m));            // 待审，不计入已出
        SeedStockOut(db, "CK-REJECTED", order.Id, DocumentStatus.Rejected, lines: (ProductA, 2m));              // 已驳回，不计
        SeedStockOut(db, "CK-DELETED", order.Id, DocumentStatus.Approved, deleted: true, lines: (ProductA, 1m)); // 已删除，不计

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(4m, item.ApprovedShippedQuantity);
        Assert.Equal(3m, item.PendingQuantity);
        Assert.Equal(SalesOrderProgress.ShipmentPartial, item.ShipmentStatus);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOverdue, item.DeliveryStatus);
    }

    // ==================== 6. 缺日期 → 未知（不推断） ====================

    [Fact]
    public async Task Missing_dates_are_shown_as_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NODATE", CustomerA, headerDeliveryDate: null, lines: (ProductA, 10m));

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryUnknown, item.DeliveryStatus);
        Assert.Null(item.HeaderDeliveryDate);
        Assert.Empty(item.LineDeliveryDates);
        Assert.Null(item.EffectiveDeliveryDate);
        Assert.Contains("缺少", item.Note);
    }

    // ==================== 7. 出货证据截断 → 未知（不静默给不完整数量） ====================

    [Fact]
    public async Task Truncated_evidence_is_shown_as_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        // 单张订单命中 ERP-032 单次派生命中上限（> 200 条明细）→ 整单数量按未知
        var order = SeedOrder(db, "SO-TRUNC", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: Array.Empty<(long ProductId, decimal Quantity)>());
        for (var i = 0; i < 201; i++) SeedDetail(db, order.Id, ProductA, 1m);

        var report = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());
        var item = Assert.Single(report.Items);

        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryUnknown, item.DeliveryStatus);
        Assert.Null(item.OrderedQuantity);
        Assert.Null(item.ApprovedShippedQuantity);
        Assert.Equal(SalesOrderProgress.ShipmentUnknown, item.ShipmentStatus);
        Assert.Contains("上限", item.Note);
    }

    // ==================== 8. 只读不写库、不改订单状态 ====================

    [Fact]
    public async Task Workspace_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-NOWRITE", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        SeedStockOut(db, "CK-NOWRITE", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        await db.SaveChangesAsync();

        var ordersBefore = db.SalesOrders.Count();
        var stockOutsBefore = db.StockOuts.Count();

        _ = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query());

        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.Equal(stockOutsBefore, db.StockOuts.Count());
        var orderAfter = db.SalesOrders.First(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.False(orderAfter.IsDeleted);
    }
    // ==================== 9. 分页有界与稳定排序 ====================

    [Fact]
    public async Task Paging_is_bounded_and_stable()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-PG-1", CustomerA, orderDate: AsOf.AddDays(-2), headerDeliveryDate: AsOf, lines: (ProductA, 1m));
        SeedOrder(db, "SO-PG-2", CustomerA, orderDate: AsOf.AddDays(-1), headerDeliveryDate: AsOf, lines: (ProductA, 1m));
        SeedOrder(db, "SO-PG-3", CustomerB, orderDate: AsOf.AddDays(-3), headerDeliveryDate: AsOf, lines: (ProductA, 1m));

        var page1 = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query(page: 1, pageSize: 2));
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(new[] { "SO-PG-1", "SO-PG-2" }, page1.Items.Select(i => i.OrderNo).ToArray());

        var page2 = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        Assert.Single(page2.Items);
        Assert.Equal("SO-PG-3", page2.Items[0].OrderNo);

        // 每页条数有界：超出上限钳制到 200
        var clamped = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query(pageSize: 999));
        Assert.Equal(200, clamped.PageSize);
    }

    // ==================== 10. 客户 / 关键字筛选 ====================

    [Fact]
    public async Task Customer_and_keyword_filters_narrow_results()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-FILT-1", CustomerA, headerDeliveryDate: AsOf, lines: (ProductA, 1m));
        SeedOrder(db, "SO-FILT-2", CustomerB, headerDeliveryDate: AsOf, lines: (ProductA, 1m));

        var byCustomer = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query(customerId: CustomerA));
        Assert.Equal(1, byCustomer.Total);
        Assert.Equal(CustomerA, Assert.Single(byCustomer.Items).CustomerId);

        var byKeyword = await SalesOrderDeliveryExceptions.ForQueryAsync(db, Query(keyword: "FILT-2"));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("SO-FILT-2", Assert.Single(byKeyword.Items).OrderNo);
    }

    // ==================== 11. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_delivery_exception_payload()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-CTRL", CustomerA, headerDeliveryDate: AsOf.AddDays(-10), lines: (ProductA, 10m));
        await db.SaveChangesAsync();

        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        var result = await controller.DeliveryExceptions(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<SalesOrderDeliveryExceptionReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(SalesOrderDeliveryExceptionSemantics.DeliveryOverdue,
            Assert.Single(payload.Data.Items).DeliveryStatus);
    }
    // ==================== 12. 只读 GET 路由与口径边界 ====================

    [Fact]
    public void Report_exposes_only_read_get_route_and_states_boundary()
    {
        var methods = typeof(SalesOrderController).GetMethods();
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

        Assert.Contains("只读派生", SalesOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("不改写", SalesOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("未知", SalesOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("逾期", SalesOrderDeliveryExceptionSemantics.RuleText);
        Assert.Contains("明细行优先", SalesOrderDeliveryExceptionSemantics.RuleText);

        foreach (var type in new[]
                 {
                     typeof(SalesOrderDeliveryExceptionItem),
                     typeof(SalesOrderDeliveryExceptionReport),
                     typeof(SalesOrderDeliveryExceptionCounts),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Payable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Receivable", StringComparison.Ordinal));
        }
    }

    // ==================== 13. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "sales-order-delivery-exceptions.js"));

        Assert.Contains("function openSalesOrderDeliveryExceptions()", reportJs);
        Assert.Contains("/api/sales-orders/delivery-exceptions?", reportJs);
        Assert.Contains("function sodeRenderKpi(data)", reportJs);
        Assert.Contains("function sodeRenderTable(data)", reportJs);
        Assert.Contains("function sodeRenderPagination(data)", reportJs);
        Assert.Contains("function exportSodeCsv()", reportJs);
        Assert.Contains("SODE_STATUS_LABELS", reportJs);
        Assert.Contains("SODE_SHIPMENT_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);

        foreach (var id in new[] { "sode-customer", "sode-asof", "sode-keyword", "sode-pagesize" })
            Assert.Contains(id, reportJs);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSalesOrderDeliveryExceptions", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/sales-order-delivery-exceptions.js", index);
    }

    // ==================== 14. 批量取数：无逐单查库、只读不写库 ====================

    [Fact]
    public async Task Workspace_batches_queries_without_per_order_db_calls()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 0; i < 5; i++)
        {
            var order = SeedOrder(db, $"SO-BATCH-{i}", CustomerA, headerDeliveryDate: AsOf, lines: (ProductA, 1m));
            SeedStockOut(db, $"CK-BATCH-{i}", order.Id, DocumentStatus.Approved, lines: (ProductA, 1m));
        }

        var counting = CountingDbContext.Wrap(db);

        var single = await SalesOrderDeliveryExceptions.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;

        var large = await SalesOrderDeliveryExceptions.ForQueryAsync(counting.Proxy, Query(pageSize: 5));
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Single(single.Items);
        Assert.Equal(5, large.Items.Count);
        Assert.Equal(singleReads, largeReads);   // 固定次数数据集访问：无逐单查库
        Assert.Equal(0, counting.WriteCalls);     // 只读：没有一次 SaveChanges
    }
    // ==================== 助手 ====================

    private static SalesOrderDeliveryExceptionQuery Query(long? customerId = null, DateTime? asOfDate = null,
        string? keyword = null, int page = 1, int pageSize = 50)
        => new()
        {
            CustomerId = customerId,
            AsOfDate = asOfDate ?? AsOf,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedCustomer(ErpDbContext db, long id, string name)
        => db.BaseCustomers.Add(new BaseCustomer { Id = id, CustomerCode = $"C{id}", CustomerName = name });

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        DateTime? orderDate = null, DateTime? headerDeliveryDate = null,
        DocumentStatus status = DocumentStatus.Approved, string contractNo = "",
        string customerPoNo = "", params (long ProductId, decimal Quantity)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = status,
            DeliveryDate = headerDeliveryDate,
            ContractNo = contractNo,
            CustomerPoNo = customerPoNo,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
            SeedDetail(db, order.Id, productId, quantity);
        return order;
    }

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity,
        DateTime? lineDeliveryDate = null)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = 1m,
            Amount = quantity,
            DeliveryDate = lineDeliveryDate,
        });
        db.SaveChanges();
    }

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId,
        DocumentStatus status, bool deleted = false, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            WarehouseId = 1,
            Status = status,
            IsDeleted = deleted,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }
        db.SaveChanges();
        stockOut.TotalQuantity = db.StockOutDetails.Where(d => d.StockOutId == stockOut.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockOut;
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




