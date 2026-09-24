using ERP.Application.Common;
using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

public sealed class OrderTimelineEvent
{
    public string EventType { get; init; } = string.Empty;
    public string ReferenceNo { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>从既有订单、库存和单证记录派生只读执行时间线；不写入或臆造业务事件。</summary>
public static class OrderExecutionTimeline
{
    private const int RelatedEventLimit = 200;

    public static async Task<List<OrderTimelineEvent>> ForSalesOrderAsync(IErpDbContext db, long id)
    {
        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");
        var events = BaseEvents("sales_order", order.OrderNo, order.CreatedAt, order.UpdatedAt,
            order.Status.ToString(), string.Empty);

        var stockOuts = await db.StockOuts.AsNoTracking()
            .Where(o => o.SalesOrderId == id && !o.IsDeleted)
            .OrderBy(o => o.StockOutDate).ThenBy(o => o.Id).Take(RelatedEventLimit).ToListAsync();
        events.AddRange(stockOuts.Select(o => new OrderTimelineEvent
        {
            EventType = "stock_out", ReferenceNo = o.StockOutNo, OccurredAt = o.StockOutDate,
            Status = o.Status.ToString(), Source = "StockOut", Detail = $"数量 {o.TotalQuantity}"
        }));

        var documents = await db.TradeDocuments.AsNoTracking()
            .Where(d => d.SalesOrderNo == order.OrderNo && !d.IsDeleted)
            .OrderBy(d => d.IssueDate ?? d.CreatedAt).ThenBy(d => d.Id).Take(RelatedEventLimit).ToListAsync();
        events.AddRange(documents.Select(d => new OrderTimelineEvent
        {
            EventType = "trade_document", ReferenceNo = d.DocNo,
            OccurredAt = d.IssueDate ?? d.CreatedAt, Status = d.Status, Source = "TradeDocument",
            Detail = d.DocType
        }));

        var complaints = await db.FinanceComplaints.AsNoTracking()
            .Where(c => c.SalesOrderId == id && !c.IsDeleted)
            .OrderBy(c => c.ComplaintDate).ThenBy(c => c.Id).Take(RelatedEventLimit).ToListAsync();
        events.AddRange(complaints.Select(c => new OrderTimelineEvent
        {
            EventType = "complaint", ReferenceNo = c.ComplaintNo, OccurredAt = c.ComplaintDate,
            Status = c.Status.ToString(), Source = "FinanceComplaint", Detail = c.ComplaintType
        }));
        return Sort(events);
    }

    public static async Task<List<OrderTimelineEvent>> ForPurchaseOrderAsync(IErpDbContext db, long id)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        var snapshot = string.Join("；", new[]
        {
            Text("到货", order.ArrivalProgress), Text("验货", order.QcStatus), Text("结算", order.SettlementProgress)
        }.Where(x => x.Length > 0));
        var events = BaseEvents("purchase_order", order.OrderNo, order.CreatedAt, order.UpdatedAt,
            order.Status.ToString(), snapshot);
        if (order.SupplierConfirmedDate.HasValue)
        {
            events.Add(new OrderTimelineEvent
            {
                EventType = "supplier_confirmed_delivery", ReferenceNo = order.OrderNo,
                OccurredAt = order.SupplierConfirmedDate.Value, Status = "confirmed", Source = "PurchaseOrder",
                Detail = "供应商确认交期"
            });
        }

        var stockIns = await db.StockIns.AsNoTracking()
            .Where(o => o.PurchaseOrderId == id && !o.IsDeleted)
            .OrderBy(o => o.StockInDate).ThenBy(o => o.Id).Take(RelatedEventLimit).ToListAsync();
        events.AddRange(stockIns.Select(o => new OrderTimelineEvent
        {
            EventType = "stock_in", ReferenceNo = o.StockInNo, OccurredAt = o.StockInDate,
            Status = o.Status.ToString(), Source = "StockIn", Detail = $"数量 {o.TotalQuantity}"
        }));
        return Sort(events);
    }

    private static List<OrderTimelineEvent> BaseEvents(string type, string reference, DateTime createdAt,
        DateTime? updatedAt, string status, string detail)
    {
        var events = new List<OrderTimelineEvent>
        {
            new() { EventType = type + "_created", ReferenceNo = reference, OccurredAt = createdAt,
                Status = status, Source = type == "sales_order" ? "SalesOrder" : "PurchaseOrder", Detail = detail }
        };
        if (updatedAt.HasValue && updatedAt.Value != createdAt)
            events.Add(new OrderTimelineEvent { EventType = type + "_updated", ReferenceNo = reference,
                OccurredAt = updatedAt.Value, Status = status,
                Source = type == "sales_order" ? "SalesOrder" : "PurchaseOrder", Detail = detail });
        return events;
    }

    private static List<OrderTimelineEvent> Sort(IEnumerable<OrderTimelineEvent> events) => events
        .OrderBy(e => e.OccurredAt).ThenBy(e => e.EventType).ThenBy(e => e.ReferenceNo).ToList();

    private static string Text(string label, string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label}: {value.Trim()}";
}
