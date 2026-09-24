using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// PurchaseOrderProgress 单元测试（ERP-026 采购订单收货 / 结算进度派生）。
/// 说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不触发浏览器验收。
/// 覆盖：无入库、部分入库（含待审 / 已驳回 / 已取消 / 已删除 / 他单入库不计入）、多次入库累计收齐、
/// 超收与订单外商品、同商品多行冲抵、结算引用可用 / 不唯一 / 不可用 / 无付款单、币种与供应商过滤、
/// 控制器端点与前端接线静态断言。
/// </summary>
public class PurchaseOrderProgressTests
{
    private const long SupplierA = 930001L;
    private const long ProductA = 930101L;
    private const long ProductB = 930102L;

    [Fact]
    public async Task 无入库_已收为零_状态为未收货()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-PRG-1", (ProductA, 8m));

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(8m, progress.OrderedQuantity);
        Assert.Equal(0m, progress.ReceivedQuantity);
        Assert.Equal(0m, progress.MatchedReceivedQuantity);
        Assert.Equal(0m, progress.UnmatchedReceivedQuantity);
        Assert.Equal(0m, progress.PendingQuantity);
        Assert.Equal(8m, progress.OutstandingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptNone, progress.ReceiptStatus);
        Assert.Single(progress.Lines);
        Assert.Equal(8m, progress.Lines[0].OutstandingQuantity);
        Assert.Empty(progress.UnmatchedReceipts);
        Assert.Empty(progress.Receipts);
        Assert.Null(progress.ArrivalProgressConsistent);   // 未登记人工到货进度 → 无法判定
    }

    [Fact]
    public async Task 部分入库_只计已审核_待审单列_其他状态与他单入库不计入()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-PRG-2", (ProductA, 10m));
        order.ArrivalProgress = "部分到货";
        db.SaveChanges();

        SeedStockIn(db, "RK-PRG-APPROVED", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedStockIn(db, "RK-PRG-SUBMITTED", order.Id, DocumentStatus.Submitted, (ProductA, 2m));
        SeedStockIn(db, "RK-PRG-PENDING", order.Id, DocumentStatus.Pending, (ProductA, 1m));
        SeedStockIn(db, "RK-PRG-CANCELLED", order.Id, DocumentStatus.Cancelled, (ProductA, 5m));
        SeedStockIn(db, "RK-PRG-REJECTED", order.Id, DocumentStatus.Rejected, (ProductA, 5m));
        var deleted = SeedStockIn(db, "RK-PRG-DELETED", order.Id, DocumentStatus.Approved, (ProductA, 6m));
        deleted.IsDeleted = true;
        db.SaveChanges();
        var otherOrder = SeedOrder(db, "PO-PRG-OTHER", (ProductA, 10m));
        SeedStockIn(db, "RK-PRG-OTHER", otherOrder.Id, DocumentStatus.Approved, (ProductA, 9m));

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(4m, progress.ReceivedQuantity);
        Assert.Equal(3m, progress.PendingQuantity);          // 已提交 2 + 待提交 1，不计入已收
        Assert.Equal(6m, progress.OutstandingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, progress.ReceiptStatus);
        Assert.True(progress.ArrivalProgressConsistent);      // 人工登记「部分到货」= 派生部分收货
        Assert.Equal(5, progress.Receipts.Count);             // 已删除入库单不进结果
        Assert.Single(progress.Receipts.Where(r => r.Counted));
        Assert.DoesNotContain(progress.Receipts, r => r.StockInNo == "RK-PRG-OTHER");
        Assert.Equal(3m, progress.Lines[0].PendingQuantity);
    }

    [Fact]
    public async Task 多次入库_累计收齐_状态为已收齐()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-PRG-3", (ProductA, 10m));
        order.ArrivalProgress = "已到货";
        db.SaveChanges();
        SeedStockIn(db, "RK-PRG-3A", order.Id, DocumentStatus.Approved, (ProductA, 6m));
        SeedStockIn(db, "RK-PRG-3B", order.Id, DocumentStatus.Approved, (ProductA, 4m));

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(10m, progress.ReceivedQuantity);
        Assert.Equal(10m, progress.MatchedReceivedQuantity);
        Assert.Equal(0m, progress.OutstandingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptComplete, progress.ReceiptStatus);
        Assert.Equal(PurchaseOrderProgress.ReceiptComplete, progress.Lines[0].ReceiptStatus);
        Assert.True(progress.ArrivalProgressConsistent);
    }

    [Fact]
    public async Task 超收与订单外商品_显式单列不并入订单行()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-PRG-4", (ProductA, 10m));
        SeedStockIn(db, "RK-PRG-4", order.Id, DocumentStatus.Approved, (ProductA, 12m), (ProductB, 3m));

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(15m, progress.ReceivedQuantity);
        Assert.Equal(12m, progress.MatchedReceivedQuantity);   // 超收部分留在订单行上，不列为订单外商品
        Assert.Equal(3m, progress.UnmatchedReceivedQuantity);
        Assert.Equal(progress.ReceivedQuantity,
            progress.MatchedReceivedQuantity + progress.UnmatchedReceivedQuantity);
        Assert.Equal(0m, progress.OutstandingQuantity);
        Assert.Equal(2m, progress.Lines[0].OverReceivedQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptOverReceived, progress.ReceiptStatus);
        var unmatched = Assert.Single(progress.UnmatchedReceipts);
        Assert.Equal(ProductB, unmatched.ProductId);
        Assert.Equal(3m, unmatched.ReceivedQuantity);
    }

    [Fact]
    public async Task 同商品多行_按明细顺序依次冲抵()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-PRG-5", (ProductA, 5m), (ProductA, 5m));
        SeedStockIn(db, "RK-PRG-5", order.Id, DocumentStatus.Approved, (ProductA, 7m));

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(10m, progress.OrderedQuantity);
        Assert.Equal(7m, progress.ReceivedQuantity);
        Assert.Equal(3m, progress.OutstandingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, progress.ReceiptStatus);
        Assert.Equal(5m, progress.Lines[0].ReceivedQuantity);
        Assert.Equal(0m, progress.Lines[0].OutstandingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptComplete, progress.Lines[0].ReceiptStatus);
        Assert.Equal(2m, progress.Lines[1].ReceivedQuantity);
        Assert.Equal(3m, progress.Lines[1].OutstandingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, progress.Lines[1].ReceiptStatus);
        Assert.Equal(7m, progress.MatchedReceivedQuantity);
    }

    [Fact]
    public async Task 结算_归属销售订单唯一时_暴露已结算与未结算()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = SeedSalesOrder(db, "SO-PRG-1");
        var order = SeedOrder(db, "PO-PRG-6", (ProductA, 10m));      // 订单总额 100
        order.OwningSalesOrderId = salesOrder.Id;
        order.OwningSalesOrderNo = salesOrder.OrderNo;
        order.SettlementProgress = "部分结算";
        db.SaveChanges();
        var apply = SeedPaymentApply(db, "HK-PRG-1", salesOrder.Id);
        SeedPayment(db, "FK-PRG-1", apply.Id, SupplierA, 40m, Currency.CNY, DocumentStatus.Approved);
        SeedPayment(db, "FK-PRG-2", apply.Id, SupplierA, 10m, Currency.CNY, DocumentStatus.Submitted);
        SeedPayment(db, "FK-PRG-3", apply.Id, SupplierA, 20m, Currency.USD, DocumentStatus.Approved);  // 币种不符
        SeedPayment(db, "FK-PRG-4", apply.Id, 999999L, 30m, Currency.CNY, DocumentStatus.Approved);   // 供应商不符
        SeedPayment(db, "FK-PRG-5", null, SupplierA, 50m, Currency.CNY, DocumentStatus.Approved);     // 未引用申请单

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        var settlement = progress.Settlement;
        Assert.Equal(PurchaseOrderProgress.LinkLinked, settlement.LinkStatus);
        Assert.Equal(40m, settlement.SettledAmount);
        Assert.Equal(60m, settlement.OutstandingAmount);
        Assert.Equal(10m, settlement.SubmittedAmount);
        Assert.False(settlement.OverSettled);
        Assert.Equal(3, settlement.Documents.Count);                       // FK-4 供应商不符、FK-5 无货款申请单引用，均不参与
        Assert.DoesNotContain(settlement.Documents, d => d.PaymentNo == "FK-PRG-4");
        Assert.DoesNotContain(settlement.Documents, d => d.PaymentNo == "FK-PRG-5");
        Assert.Single(settlement.Documents.Where(d => d.Counted));
        Assert.Contains("HK-PRG-1", settlement.Documents.Select(d => d.PaymentApplyNo));
        Assert.Contains("币种与本单不一致", settlement.LinkReason);
        Assert.Equal("部分结算", settlement.RecordedProgress);   // 人工登记文本原样回显，不被派生值覆盖
    }

    [Fact]
    public async Task 结算_超付时置超付标记且未结算归零()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = SeedSalesOrder(db, "SO-PRG-2");
        var order = SeedOrder(db, "PO-PRG-7", (ProductA, 10m));      // 订单总额 100
        order.OwningSalesOrderId = salesOrder.Id;
        db.SaveChanges();
        var apply = SeedPaymentApply(db, "HK-PRG-2", salesOrder.Id);
        SeedPayment(db, "FK-PRG-6", apply.Id, SupplierA, 150m, Currency.CNY, DocumentStatus.Approved);

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(150m, progress.Settlement.SettledAmount);
        Assert.Equal(0m, progress.Settlement.OutstandingAmount);
        Assert.True(progress.Settlement.OverSettled);
    }

    [Fact]
    public async Task 结算_归属销售订单下多张采购订单_不做推断()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = SeedSalesOrder(db, "SO-PRG-3");
        var first = SeedOrder(db, "PO-PRG-8", (ProductA, 10m));
        first.OwningSalesOrderId = salesOrder.Id;
        var second = SeedOrder(db, "PO-PRG-9", (ProductB, 10m));
        second.OwningSalesOrderId = salesOrder.Id;
        db.SaveChanges();
        var apply = SeedPaymentApply(db, "HK-PRG-3", salesOrder.Id);
        SeedPayment(db, "FK-PRG-7", apply.Id, SupplierA, 100m, Currency.CNY, DocumentStatus.Approved);

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, first.Id);

        Assert.Equal(PurchaseOrderProgress.LinkAmbiguous, progress.Settlement.LinkStatus);
        Assert.Null(progress.Settlement.SettledAmount);
        Assert.Null(progress.Settlement.OutstandingAmount);
        Assert.Null(progress.Settlement.SubmittedAmount);
        Assert.Empty(progress.Settlement.Documents);
        Assert.Contains("2 张采购订单", progress.Settlement.LinkReason);
    }

    [Fact]
    public async Task 结算_无归属销售订单或未维护供应商_金额为未知()
    {
        using var db = TestDbFactory.Create();
        var noOwner = SeedOrder(db, "PO-PRG-10", (ProductA, 10m));

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, noOwner.Id);

        Assert.Equal(PurchaseOrderProgress.LinkUnavailable, progress.Settlement.LinkStatus);
        Assert.Null(progress.Settlement.SettledAmount);
        Assert.Null(progress.Settlement.OutstandingAmount);
        Assert.Contains("未关联归属销售订单", progress.Settlement.LinkReason);

        var salesOrder = SeedSalesOrder(db, "SO-PRG-4");
        var noSupplier = SeedOrder(db, "PO-PRG-11", (ProductA, 10m));
        noSupplier.OwningSalesOrderId = salesOrder.Id;
        noSupplier.SupplierId = 0;
        db.SaveChanges();

        var second = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, noSupplier.Id);

        Assert.Equal(PurchaseOrderProgress.LinkUnavailable, second.Settlement.LinkStatus);
        Assert.Null(second.Settlement.SettledAmount);
        Assert.Contains("未维护供应商", second.Settlement.LinkReason);
    }

    [Fact]
    public async Task 结算_无货款申请单时_已结算为零_未结算为订单总额()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = SeedSalesOrder(db, "SO-PRG-5");
        var order = SeedOrder(db, "PO-PRG-12", (ProductA, 10m));
        order.OwningSalesOrderId = salesOrder.Id;
        db.SaveChanges();
        SeedPaymentApply(db, "HK-PRG-4", null);                       // 未引用本单归属销售订单

        var progress = await PurchaseOrderProgress.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(PurchaseOrderProgress.LinkLinked, progress.Settlement.LinkStatus);
        Assert.Equal(0m, progress.Settlement.SettledAmount);
        Assert.Equal(100m, progress.Settlement.OutstandingAmount);
        Assert.Equal(0m, progress.Settlement.SubmittedAmount);
        Assert.Empty(progress.Settlement.Documents);
        Assert.Contains("未找到引用该销售订单的货款申请单", progress.Settlement.LinkReason);
    }

    [Fact]
    public async Task 控制器端点_返回进度且订单不存在时抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-PRG-13", (ProductA, 10m));
        SeedStockIn(db, "RK-PRG-13", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));

        var result = await controller.Progress(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<PurchaseOrderProgressView>>(ok.Value);
        Assert.Equal(0, payload.Code);
        Assert.Equal(6m, payload.Data!.OutstandingQuantity);
        Assert.Equal("PO-PRG-13", payload.Data.OrderNo);
        await Assert.ThrowsAsync<BusinessException>(() => controller.Progress(999999));
    }

    [Fact]
    public void 前端接线_采购订单行操作与脚本均已登记()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var modules = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/modules-doc.js"));
        var index = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/index.html"));
        var script = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/purchase-order-progress.js"));

        Assert.Equal(1, modules.Split("onclick: 'showPurchaseOrderProgress'").Length - 1);
        Assert.Contains("/js/purchase-order-progress.js", index);
        Assert.Contains("/${id}/progress", script);
        Assert.Contains("settlementMoney", script);   // 未知金额必须与 0 区分显示
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, params (long ProductId, decimal Quantity)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 5, 1),
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0),
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
                Spec = "规格A",
                Unit = "PCS",
                Quantity = quantity,
                UnitPrice = 10m,
                Amount = quantity * 10m,
            });
        }
        db.SaveChanges();
        order.TotalAmount = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).Sum(d => d.Amount);
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = new DateTime(2026, 5, 2),
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = 1,
            Status = status,
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
        stockIn.TotalQuantity = db.StockInDetails.Where(d => d.StockInId == stockIn.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockIn;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo)
    {
        var salesOrder = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 4, 1),
            CustomerId = 1,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 4, 1, 8, 0, 0),
        };
        db.SalesOrders.Add(salesOrder);
        db.SaveChanges();
        return salesOrder;
    }

    private static FinancePaymentApply SeedPaymentApply(ErpDbContext db, string applyNo, long? salesOrderId)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 5, 3),
            SalesOrderId = salesOrderId,
            CustomerId = 1,
            Amount = 0m,
            Status = DocumentStatus.Approved,
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment SeedPayment(ErpDbContext db, string paymentNo, long? paymentApplyId, long supplierId,
        decimal amount, Currency currency, DocumentStatus status)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 5, 4),
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }
}
