using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-101 销售订单退货影响（只读派生）单元测试：
/// 毛出货 / 有效退货 / 净出货派生、链接状态、审核状态、删除、超退不钳制、客户不一致、
/// 来源已删除 / 来源不属于本单 / 未关联来源的异常退货、只读不写库、订单不存在、接口端点、路由只读、前端接线、有界读取。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SalesOrderReturnImpactTests
{
    private static readonly DateTime Day = new(2026, 9, 20);
    private const long CustomerA = 880001L;
    private const long CustomerB = 880002L;
    private const long ProductA = 880101L;

    // ==================== 1. 毛出货 / 有效退货 / 净出货 派生 ====================

    [Fact]
    public async Task Derives_gross_returned_and_net_per_product()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-1", CustomerA, (ProductA, 10m));
        var stockOut = SeedStockOut(db, "CK-1", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedReturn(db, "XTH-1", stockOut.Id, DocumentStatus.Approved, lines: (ProductA, 4m));

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.False(view.Truncated);
        Assert.Equal(10m, view.GrossShippedTotal);
        Assert.Equal(4m, view.ValidReturnedTotal);
        Assert.Equal(6m, view.NetShippedTotal);

        var line = Assert.Single(view.Products);
        Assert.Equal(ProductA, line.ProductId);
        Assert.Equal(10m, line.GrossShipped);
        Assert.Equal(4m, line.ValidReturned);
        Assert.Equal(6m, line.NetShipped);
        Assert.False(line.OverReturned);
        Assert.Equal(SalesOrderReturnImpactSemantics.AnomalyNone, line.Anomaly);

        var ret = Assert.Single(view.Returns);
        Assert.Equal(SalesOrderReturnImpactSemantics.ReturnValid, ret.Classification);
        Assert.True(ret.Applied);
    }

    // ==================== 2. 仅已审核未删除出库计入毛出货；仅已审核退货计入有效退货 ====================

    [Fact]
    public async Task Only_approved_nondeleted_stock_outs_and_approved_returns_count()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-2", CustomerA, (ProductA, 10m));
        var approved = SeedStockOut(db, "CK-OK", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedStockOut(db, "CK-SUB", order.Id, DocumentStatus.Submitted, lines: (ProductA, 5m));
        SeedStockOut(db, "CK-DEL", order.Id, DocumentStatus.Approved, deleted: true, lines: (ProductA, 3m));
        SeedReturn(db, "XTH-V", approved.Id, DocumentStatus.Approved, lines: (ProductA, 4m));
        SeedReturn(db, "XTH-SUB", approved.Id, DocumentStatus.Submitted, lines: (ProductA, 2m));

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        var line = Assert.Single(view.Products);
        Assert.Equal(10m, line.GrossShipped);   // 仅已审核未删除出库
        Assert.Equal(4m, line.ValidReturned);   // 仅已审核退货
        Assert.Equal(6m, line.NetShipped);

        Assert.Equal(2, view.Returns.Count);
        Assert.Contains(view.Returns, r => r.Classification == SalesOrderReturnImpactSemantics.ReturnValid);
        Assert.Contains(view.Returns, r => r.Classification == SalesOrderReturnImpactSemantics.ReturnNonApproved);
    }

    // ==================== 3. 超退不静默钳制（净额可为负） ====================

    [Fact]
    public async Task Over_return_is_not_clamped()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-3", CustomerA, (ProductA, 5m));
        var stockOut = SeedStockOut(db, "CK-3", order.Id, DocumentStatus.Approved, lines: (ProductA, 5m));
        SeedReturn(db, "XTH-3", stockOut.Id, DocumentStatus.Approved, lines: (ProductA, 8m));

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        var line = Assert.Single(view.Products);
        Assert.Equal(5m, line.GrossShipped);
        Assert.Equal(8m, line.ValidReturned);
        Assert.Equal(-3m, line.NetShipped);   // 超退：净额为负，不钳制为 0
        Assert.True(line.OverReturned);
        Assert.Equal(SalesOrderReturnImpactSemantics.AnomalyOverReturn, line.Anomaly);
        Assert.Equal(-3m, view.NetShippedTotal);
    }

    // ==================== 4. 来源出库单已删除 → 悬空异常（不计入） ====================

    [Fact]
    public async Task Return_linked_to_deleted_stock_out_is_missing_source_exception()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-4", CustomerA, (ProductA, 10m));
        var deletedStockOut = SeedStockOut(db, "CK-DELSRC", order.Id, DocumentStatus.Approved, deleted: true, lines: (ProductA, 10m));
        SeedReturn(db, "XTH-4", deletedStockOut.Id, DocumentStatus.Approved, lines: (ProductA, 3m));

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        var line = Assert.Single(view.Products);
        Assert.Equal(0m, line.GrossShipped);   // 已删除出库不计入毛出货
        Assert.Equal(0m, line.ValidReturned);  // 来源已删除退货不计入
        Assert.Equal(0m, line.NetShipped);

        var ret = Assert.Single(view.Returns);
        Assert.Equal(SalesOrderReturnImpactSemantics.ReturnMissingSource, ret.Classification);
        Assert.False(ret.Applied);
    }

    // ==================== 5. 客户不一致 → 异常（不计入） ====================

    [Fact]
    public async Task Return_with_wrong_customer_is_exception_not_applied()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        var order = SeedOrder(db, "SO-101-5", CustomerA, (ProductA, 10m));
        var stockOut = SeedStockOut(db, "CK-5", order.Id, DocumentStatus.Approved, customerId: CustomerA, lines: (ProductA, 10m));
        SeedReturn(db, "XTH-5", stockOut.Id, DocumentStatus.Approved,
            customerId: CustomerB, customerName: "乙客户", lines: (ProductA, 4m));

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.Equal(10m, view.GrossShippedTotal);
        Assert.Equal(0m, view.ValidReturnedTotal);   // 客户不一致不计入
        var ret = Assert.Single(view.Returns);
        Assert.Equal(SalesOrderReturnImpactSemantics.ReturnWrongCustomer, ret.Classification);
        Assert.False(ret.Applied);
    }

    // ==================== 6. 未关联 / 来源不属于本单 → 异常（不计入） ====================

    [Fact]
    public async Task Unlinked_and_unmatched_returns_are_exceptions_not_applied()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-6", CustomerA, (ProductA, 10m));
        SeedStockOut(db, "CK-6", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));

        var otherOrder = SeedOrder(db, "SO-OTHER", CustomerA, (ProductA, 5m));
        var otherStockOut = SeedStockOut(db, "CK-OTHER", otherOrder.Id, DocumentStatus.Approved, lines: (ProductA, 5m));

        SeedReturn(db, "XTH-UNLINKED", null, DocumentStatus.Approved, lines: (ProductA, 2m));
        SeedReturn(db, "XTH-UNMATCHED", otherStockOut.Id, DocumentStatus.Approved, lines: (ProductA, 2m));

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.Equal(2, view.Exceptions.Count);
        Assert.Contains(view.Exceptions, e => e.Classification == SalesOrderReturnImpactSemantics.ReturnUnlinked);
        Assert.Contains(view.Exceptions, e => e.Classification == SalesOrderReturnImpactSemantics.ReturnUnmatched);
        Assert.DoesNotContain(view.Returns, r => r.Applied);
        Assert.Equal(0m, view.ValidReturnedTotal);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task View_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-7", CustomerA, (ProductA, 10m));
        var stockOut = SeedStockOut(db, "CK-7", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedReturn(db, "XTH-7", stockOut.Id, DocumentStatus.Approved, lines: (ProductA, 4m));
        await db.SaveChangesAsync();

        var ordersBefore = db.SalesOrders.Count();
        var stockOutsBefore = db.StockOuts.Count();
        var returnsBefore = db.SalesReturns.Count();

        _ = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.Equal(stockOutsBefore, db.StockOuts.Count());
        Assert.Equal(returnsBefore, db.SalesReturns.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(),
            e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    // ==================== 8. 订单不存在 → 明确业务错误 ====================

    [Fact]
    public async Task Missing_order_throws_not_found()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SalesOrderReturnImpact.ForOrderAsync(db, 987654L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 9. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_return_impact_payload()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-CTRL", CustomerA, (ProductA, 10m));
        var stockOut = SeedStockOut(db, "CK-CTRL", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedReturn(db, "XTH-CTRL", stockOut.Id, DocumentStatus.Approved, lines: (ProductA, 4m));
        await db.SaveChangesAsync();

        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        var result = await controller.ReturnImpact(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<SalesOrderReturnImpactView>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        Assert.Equal(6m, payload.Data!.NetShippedTotal);
    }

    // ==================== 10. 只读 GET 路由与口径边界 ====================

    [Fact]
    public void View_exposes_only_read_get_route_and_states_boundary()
    {
        var methods = typeof(SalesOrderController).GetMethods();
        var getRoutes = methods.SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty).ToList();
        Assert.Contains("{id:long}/return-impact", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "{id:long}/return-impact"));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("return-impact", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("return-impact", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("return-impact", StringComparison.Ordinal));

        Assert.Contains("只读派生", SalesOrderReturnImpactSemantics.RuleText);
        Assert.Contains("不改写", SalesOrderReturnImpactSemantics.RuleText);
        Assert.Contains("未知", SalesOrderReturnImpactSemantics.RuleText);
        Assert.Contains("超退", SalesOrderReturnImpactSemantics.RuleText);
    }

    // ==================== 11. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "sales-order-return-impact.js"));

        Assert.Contains("function showSalesOrderReturnImpact(id)", reportJs);
        Assert.Contains("/api/sales-orders/${id}/return-impact", reportJs);
        Assert.Contains("SRI_CLASSIFICATION_LABELS", reportJs);
        Assert.Contains("SRI_ANOMALY_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("showSalesOrderReturnImpact", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/sales-order-return-impact.js", index);
    }

    // ==================== 12. 有界读取：命中上限 → 未知（不回落为 0） ====================

    [Fact]
    public async Task Hitting_collection_ceiling_marks_quantities_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-101-BOUND", CustomerA, (ProductA, 1m));
        for (var i = 0; i < 200; i++)
        {
            db.StockOuts.Add(new StockOut
            {
                StockOutNo = $"CK-B-{i}",
                StockOutDate = Day,
                SalesOrderId = order.Id,
                CustomerId = CustomerA,
                WarehouseId = 1,
                Status = DocumentStatus.Approved,
            });
        }
        db.SaveChanges();

        var view = await SalesOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.True(view.Truncated);
        Assert.Null(view.GrossShippedTotal);
        Assert.Null(view.ValidReturnedTotal);
        Assert.Null(view.NetShippedTotal);

        var line = Assert.Single(view.Products);
        Assert.Null(line.GrossShipped);
        Assert.Null(line.NetShipped);
        Assert.Equal(SalesOrderReturnImpactSemantics.AnomalyUnknown, line.Anomaly);
    }

    // ==================== 助手 ====================

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedCustomer(ErpDbContext db, long id, string name)
        => db.BaseCustomers.Add(new BaseCustomer { Id = id, CustomerCode = $"C{id}", CustomerName = name });

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = Day,
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = lines.Sum(l => l.Quantity),
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
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

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId,
        DocumentStatus status, bool deleted = false, long customerId = CustomerA,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = Day,
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
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
        return stockOut;
    }

    private static SalesReturn SeedReturn(ErpDbContext db, string returnNo, long? sourceStockOutId,
        DocumentStatus status, bool deleted = false, long? customerId = CustomerA, string customerName = "甲客户",
        params (long ProductId, decimal Quantity)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = returnNo,
            ReturnDate = Day,
            CustomerId = customerId,
            CustomerName = customerName,
            WarehouseId = 1,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = sourceStockOutId == null ? string.Empty : $"CK-{sourceStockOutId}",
            Status = status,
            IsDeleted = deleted,
        };
        db.SalesReturns.Add(ret);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.SalesReturnDetails.Add(new SalesReturnDetail
            {
                SalesReturnId = ret.Id,
                ReturnNo = returnNo,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
                UnitPrice = 1m,
                Amount = quantity,
            });
        }
        db.SaveChanges();
        return ret;
    }
}
