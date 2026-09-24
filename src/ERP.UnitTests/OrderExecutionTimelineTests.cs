using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Xunit;

namespace ERP.UnitTests;

public class OrderExecutionTimelineTests
{
    [Fact]
    public async Task Sales_timeline_orders_existing_sources_chronologically()
    {
        using var db = TestDbFactory.Create();
        var order = new SalesOrder
        {
            OrderNo = "SO-TL-1", OrderDate = new DateTime(2026, 1, 1), CustomerId = 1,
            Status = DocumentStatus.Approved, CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        db.StockOuts.Add(new StockOut
        {
            StockOutNo = "OUT-1", StockOutDate = new DateTime(2026, 1, 3), SalesOrderId = order.Id,
            Status = DocumentStatus.Approved, TotalQuantity = 4
        });
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "DOC-1", DocType = "商业发票", SalesOrderNo = order.OrderNo,
            IssueDate = new DateTime(2026, 1, 2), Status = "已制作"
        });
        db.FinanceComplaints.Add(new FinanceComplaint
        {
            ComplaintNo = "CMP-1", ComplaintDate = new DateTime(2026, 1, 4), SalesOrderId = order.Id,
            ComplaintType = "质量", Status = DocumentStatus.Submitted
        });
        db.SaveChanges();

        var events = await OrderExecutionTimeline.ForSalesOrderAsync(db, order.Id);

        Assert.Equal(new[] { "sales_order_created", "trade_document", "stock_out", "complaint", "sales_order_updated" },
            events.Select(e => e.EventType));
        Assert.Equal("DOC-1", events[1].ReferenceNo);
        Assert.Equal("数量 4", events[2].Detail);
    }

    [Fact]
    public async Task Purchase_timeline_handles_partial_history_without_invented_events()
    {
        using var db = TestDbFactory.Create();
        var order = new PurchaseOrder
        {
            OrderNo = "PO-TL-1", SupplierId = 1, Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 2, 1), SupplierConfirmedDate = new DateTime(2026, 2, 2),
            ArrivalProgress = "部分到货", QcStatus = "验货中"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        db.StockIns.Add(new StockIn
        {
            StockInNo = "IN-1", StockInDate = new DateTime(2026, 2, 3), PurchaseOrderId = order.Id,
            SupplierId = 1, WarehouseId = 1, Status = DocumentStatus.Approved, TotalQuantity = 9
        });
        db.SaveChanges();

        var events = await OrderExecutionTimeline.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(new[] { "purchase_order_created", "supplier_confirmed_delivery", "stock_in", "purchase_order_updated" },
            events.Select(e => e.EventType));
        Assert.Contains("到货: 部分到货", events[0].Detail);
        Assert.Contains("验货: 验货中", events[0].Detail);
        Assert.DoesNotContain(events, e => e.EventType == "trade_document");
    }

    [Fact]
    public async Task Empty_related_history_returns_only_real_order_event()
    {
        using var db = TestDbFactory.Create();
        var order = new PurchaseOrder
        {
            OrderNo = "PO-EMPTY", SupplierId = 1, CreatedAt = new DateTime(2026, 3, 1)
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();

        var events = await OrderExecutionTimeline.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal("PurchaseOrder", item.Source));
        Assert.Equal(new[] { "purchase_order_created", "purchase_order_updated" }, events.Select(e => e.EventType));
        Assert.All(events, item => Assert.Equal("PO-EMPTY", item.ReferenceNo));
    }

    [Fact]
    public async Task Missing_order_is_rejected()
    {
        using var db = TestDbFactory.Create();
        await Assert.ThrowsAsync<BusinessException>(() => OrderExecutionTimeline.ForSalesOrderAsync(db, 999));
        await Assert.ThrowsAsync<BusinessException>(() => OrderExecutionTimeline.ForPurchaseOrderAsync(db, 999));
    }

    [Fact]
    public void Ui_exposes_timeline_actions_and_script()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var modules = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/modules-doc.js"));
        var index = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/index.html"));
        var script = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/order-timeline.js"));
        Assert.Equal(2, modules.Split("onclick: 'showOrderTimeline'").Length - 1);
        Assert.Contains("/js/order-timeline.js", index);
        Assert.Contains("/${id}/timeline", script);
    }
}
