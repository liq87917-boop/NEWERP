using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-028 订单财务核对（只读派生）单元测试：只认既有权威引用，声明 / 客户级 / 文本匹配一律只列出不计入，
/// 金额未知（null）与 0 严格区分，查询有界。
/// </summary>
public class OrderFinanceReconciliationTests
{
    private const string SalesOrderNo = "SO-RC-1";
    private const string PurchaseOrderNo = "PO-RC-1";

    [Fact]
    public async Task Sales_order_counts_only_authoritative_approved_same_currency_records()
    {
        using var db = TestDbFactory.Create();
        var order = AddSalesOrder(db, total: 1000m, currency: Currency.USD, customerId: 7);
        db.FinanceDepositApplies.AddRange(
            new FinanceDepositApply
            {
                ApplyNo = "DA-1", ApplyDate = new DateTime(2026, 1, 5), SalesOrderId = order.Id, CustomerId = 7,
                Amount = 300m, Currency = Currency.USD, Status = DocumentStatus.Approved,
            },
            new FinanceDepositApply
            {
                ApplyNo = "DA-2", ApplyDate = new DateTime(2026, 1, 6), SalesOrderId = order.Id, CustomerId = 7,
                Amount = 50m, Currency = Currency.USD, Status = DocumentStatus.Submitted,
            },
            new FinanceDepositApply
            {
                ApplyNo = "DA-3", ApplyDate = new DateTime(2026, 1, 7), SalesOrderId = order.Id, CustomerId = 7,
                Amount = 20m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
            });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);

        Assert.Equal(OrderFinanceReconciliation.OrderTypeSales, view.OrderType);
        Assert.Equal(SalesOrderNo, view.OrderNo);
        Assert.Equal("USD", view.Currency);
        Assert.Equal(1000m, view.OrderAmount);
        Assert.Equal(300m, view.LinkedAmount);
        Assert.Equal(700m, view.UnlinkedAmount);
        Assert.Equal(50m, view.SubmittedAmount);
        Assert.Equal(0m, view.CounterpartLinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountLinked, view.AmountStatus);
        Assert.Contains("另有 1 条同方向记录未计入", view.AmountNote);
        Assert.Equal(OrderFinanceReconciliation.RuleText, view.Rule);

        var deposit = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleDepositApply);
        Assert.Equal(OrderFinanceReconciliation.LinkLinked, deposit.LinkStatus);
        Assert.Equal(OrderFinanceReconciliation.DirectionIn, deposit.Direction);
        Assert.Equal("FinanceDepositApply.SalesOrderId", deposit.ReferenceField);
        Assert.Equal(300m, deposit.CountedAmount);
        Assert.Equal(50m, deposit.SubmittedAmount);
        Assert.Equal(0m, deposit.ListedAmount);
        Assert.Equal(1, deposit.ListedRecordCount);
        Assert.Equal(1, deposit.UnsummedRecordCount);
        Assert.Equal(new[] { "DA-1", "DA-2", "DA-3" }, deposit.Records.Select(r => r.DocumentNo));
        Assert.True(deposit.Records[0].Counted);
        Assert.Equal("FinanceDepositApply", deposit.Records[0].Source);
        Assert.False(deposit.Records[1].Counted);
        Assert.Contains("单据未审核", deposit.Records[1].Note);
        Assert.False(deposit.Records[2].Counted);
        Assert.Contains("币种与本单不一致", deposit.Records[2].Note);

        // 固定分组顺序：收款申请 2 组 + 付款单 + 费用单 + 客诉单 + 客户级 3 组
        Assert.Equal(new[]
        {
            OrderFinanceReconciliation.RoleDepositApply, OrderFinanceReconciliation.RolePaymentApply,
            OrderFinanceReconciliation.RoleSupplierPayment, OrderFinanceReconciliation.RoleExpense,
            OrderFinanceReconciliation.RoleComplaint, OrderFinanceReconciliation.RoleReceipt,
            OrderFinanceReconciliation.RoleContainerSettlement, OrderFinanceReconciliation.RoleBulkSettlement,
        }, view.Sections.Select(s => s.Role));
        Assert.Empty(view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RolePaymentApply).Records);
        Assert.Empty(view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleExpense).Records);
        Assert.Empty(view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleReceipt).Records);
    }

    [Fact]
    public async Task Sales_order_links_supplier_payments_only_through_the_payment_apply_chain()
    {
        using var db = TestDbFactory.Create();
        var order = AddSalesOrder(db, total: 1000m, currency: Currency.USD, customerId: 7);
        var applyA = new FinancePaymentApply
        {
            ApplyNo = "PA-1", ApplyDate = new DateTime(2026, 2, 1), SalesOrderId = order.Id, CustomerId = 7,
            Amount = 200m, Currency = Currency.USD, Status = DocumentStatus.Approved,
        };
        var applyB = new FinancePaymentApply
        {
            ApplyNo = "PA-2", ApplyDate = new DateTime(2026, 2, 2), SalesOrderId = order.Id, CustomerId = 7,
            Amount = 100m, Currency = Currency.USD, Status = DocumentStatus.Approved,
        };
        db.FinancePaymentApplies.AddRange(applyA, applyB);
        db.SaveChanges();
        db.FinancePayments.AddRange(
            new FinancePayment
            {
                PaymentNo = "PAY-1", PaymentDate = new DateTime(2026, 2, 5), SupplierId = 9,
                PaymentApplyId = applyA.Id, Amount = 150m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
            },
            new FinancePayment
            {
                PaymentNo = "PAY-2", PaymentDate = new DateTime(2026, 2, 6), SupplierId = 9, PaymentApplyId = null,
                Amount = 999m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
            },
            new FinancePayment
            {
                PaymentNo = "PAY-3", PaymentDate = new DateTime(2026, 2, 7), SupplierId = 9,
                PaymentApplyId = applyA.Id, Amount = 30m, Currency = Currency.USD, Status = DocumentStatus.Approved,
            });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);

        Assert.Equal(300m, view.LinkedAmount);
        Assert.Equal(30m, view.CounterpartLinkedAmount);
        Assert.Equal(700m, view.UnlinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountLinked, view.AmountStatus);

        var payment = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSupplierPayment);
        Assert.Equal(OrderFinanceReconciliation.DirectionOut, payment.Direction);
        Assert.Equal(2, payment.Records.Count);
        Assert.DoesNotContain(payment.Records, r => r.DocumentNo == "PAY-2");
        Assert.Equal(new[] { "PAY-1", "PAY-3" }, payment.Records.Select(r => r.DocumentNo));
        Assert.False(payment.Records[0].Counted);
        Assert.Contains("经货款申请单 PA-1", payment.Records[0].Note);
        Assert.Contains("币种与本单不一致", payment.Records[0].Note);
        Assert.True(payment.Records[1].Counted);
        Assert.Equal(30m, payment.CountedAmount);
        Assert.Equal(0m, payment.ListedAmount);
        Assert.Equal(1, payment.UnsummedRecordCount);
    }

    [Fact]
    public async Task Sales_order_lists_customer_level_and_declared_records_without_counting_them()
    {
        using var db = TestDbFactory.Create();
        var order = AddSalesOrder(db, total: 1000m, currency: Currency.USD, customerId: 7);
        db.FinanceReceipts.AddRange(
            new FinanceReceipt
            {
                ReceiptNo = "R-1", ReceiptDate = new DateTime(2026, 3, 1), CustomerId = 7, Amount = 500m,
                Currency = Currency.USD, Status = DocumentStatus.Approved,
            },
            new FinanceReceipt
            {
                ReceiptNo = "R-2", ReceiptDate = new DateTime(2026, 3, 2), CustomerId = 7, Amount = 80m,
                Currency = Currency.CNY, Status = DocumentStatus.Approved,
            },
            new FinanceReceipt
            {
                ReceiptNo = "R-OTHER", ReceiptDate = new DateTime(2026, 3, 3), CustomerId = 99, Amount = 700m,
                Currency = Currency.USD, Status = DocumentStatus.Approved,
            });
        db.FinanceContainerSettlements.Add(new FinanceContainerSettlement
        {
            SettlementNo = "CS-1", SettlementDate = new DateTime(2026, 3, 4), CustomerId = 7, TotalAmount = 120m,
            Status = DocumentStatus.Approved,
        });
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "E-1", ExpenseDate = new DateTime(2026, 3, 5),
            RefType = OrderFinanceReconciliation.ExpenseOrderRefType, RefNo = SalesOrderNo, Amount = 60m,
            Currency = "CNY", PaymentStatus = "未付",
        });
        db.FinanceComplaints.Add(new FinanceComplaint
        {
            ComplaintNo = "CMP-1", ComplaintDate = new DateTime(2026, 3, 6), CustomerId = 7,
            SalesOrderId = order.Id, ComplaintType = "质量", Status = DocumentStatus.Submitted,
        });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);

        Assert.Equal(0m, view.LinkedAmount);
        Assert.Equal(1000m, view.UnlinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountPartial, view.AmountStatus);
        Assert.Contains("不等于未收付", view.AmountNote);

        var receipt = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleReceipt);
        Assert.Equal(OrderFinanceReconciliation.LinkUnattributed, receipt.LinkStatus);
        Assert.Equal(2, receipt.Records.Count);
        Assert.All(receipt.Records, r => Assert.False(r.Counted));
        Assert.DoesNotContain(receipt.Records, r => r.DocumentNo == "R-OTHER");
        Assert.Equal(500m, receipt.ListedAmount);
        Assert.Equal(1, receipt.UnsummedRecordCount);
        Assert.Equal(0m, receipt.CountedAmount);

        var settlement = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleContainerSettlement);
        Assert.Single(settlement.Records);
        Assert.Equal(0m, settlement.ListedAmount);
        Assert.Equal(1, settlement.UnsummedRecordCount);
        Assert.Contains("无币种列", settlement.Records[0].Note);

        var expense = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleExpense);
        Assert.Equal(OrderFinanceReconciliation.LinkDeclared, expense.LinkStatus);
        Assert.Equal(OrderFinanceReconciliation.DirectionOut, expense.Direction);
        Assert.Single(expense.Records);
        Assert.False(expense.Records[0].Counted);
        Assert.Contains("非权威引用", expense.Records[0].Note);
        Assert.Contains("未付", expense.Records[0].Note);

        var complaint = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleComplaint);
        Assert.Equal(OrderFinanceReconciliation.LinkLinked, complaint.LinkStatus);
        Assert.Single(complaint.Records);
        Assert.Null(complaint.Records[0].Amount);
        Assert.False(complaint.Records[0].Counted);
        Assert.Contains("质量", complaint.Records[0].Note);
    }

    [Fact]
    public async Task Text_only_and_other_document_matches_are_never_counted()
    {
        using var db = TestDbFactory.Create();
        var order = AddSalesOrder(db, total: 1000m, currency: Currency.USD, customerId: 7);
        db.FinanceExpenses.AddRange(
            new FinanceExpense
            {
                ExpenseNo = "E-CONTAINER-REF", ExpenseDate = new DateTime(2026, 4, 1), RefType = "整柜",
                RefNo = SalesOrderNo, Amount = 111m, Currency = "CNY", PaymentStatus = "已付",
            },
            new FinanceExpense
            {
                ExpenseNo = "E-OTHER-ORDER", ExpenseDate = new DateTime(2026, 4, 2),
                RefType = OrderFinanceReconciliation.ExpenseOrderRefType, RefNo = "SO-OTHER", Amount = 222m,
                Currency = "CNY", PaymentStatus = "已付",
            });
        db.FinanceComplaints.Add(new FinanceComplaint
        {
            ComplaintNo = "CMP-OTHER", ComplaintDate = new DateTime(2026, 4, 3), CustomerId = 7,
            SalesOrderId = 999, ComplaintType = "延迟", Status = DocumentStatus.Submitted,
        });
        // 付款单供应商存在但未引用货款申请单：不归属本单，也不按供应商汇总
        db.FinancePayments.Add(new FinancePayment
        {
            PaymentNo = "PAY-SUP", PaymentDate = new DateTime(2026, 4, 4), SupplierId = 9, PaymentApplyId = null,
            Amount = 500m, Currency = Currency.USD, Status = DocumentStatus.Approved,
        });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);

        Assert.Empty(view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleExpense).Records);
        Assert.Empty(view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleComplaint).Records);
        Assert.Empty(view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSupplierPayment).Records);
        Assert.Equal(0m, view.LinkedAmount);
        Assert.Equal(0m, view.CounterpartLinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountLinked, view.AmountStatus);
    }

    [Fact]
    public async Task Purchase_order_reuses_the_authoritative_settlement_chain()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = AddSalesOrder(db, total: 4000m, currency: Currency.USD, customerId: 5, orderNo: "SO-OWN-1");
        var order = AddPurchaseOrder(db, total: 5000m, currency: Currency.CNY, supplierId: 9,
            owningSalesOrderId: salesOrder.Id, owningSalesOrderNo: salesOrder.OrderNo, owningCustomerId: 5);
        var apply = new FinancePaymentApply
        {
            ApplyNo = "PA-1", ApplyDate = new DateTime(2026, 5, 1), SalesOrderId = salesOrder.Id, CustomerId = 5,
            Amount = 2000m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
        };
        db.FinancePaymentApplies.Add(apply);
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = "DA-SO-1", ApplyDate = new DateTime(2026, 5, 2), SalesOrderId = salesOrder.Id, CustomerId = 5,
            Amount = 800m, Currency = Currency.USD, Status = DocumentStatus.Approved,
        });
        db.SaveChanges();
        db.FinancePayments.AddRange(
            new FinancePayment
            {
                PaymentNo = "PAY-1", PaymentDate = new DateTime(2026, 5, 5), SupplierId = 9,
                PaymentApplyId = apply.Id, Amount = 2000m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
            },
            new FinancePayment
            {
                PaymentNo = "PAY-2", PaymentDate = new DateTime(2026, 5, 6), SupplierId = 9,
                PaymentApplyId = apply.Id, Amount = 300m, Currency = Currency.USD, Status = DocumentStatus.Approved,
            },
            new FinancePayment
            {
                PaymentNo = "PAY-3", PaymentDate = new DateTime(2026, 5, 7), SupplierId = 9, PaymentApplyId = null,
                Amount = 999m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
            });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForPurchaseOrderAsync(db, order.Id);

        Assert.Equal(OrderFinanceReconciliation.OrderTypePurchase, view.OrderType);
        Assert.Equal(PurchaseOrderNo, view.OrderNo);
        Assert.Equal(2000m, view.LinkedAmount);
        Assert.Equal(3000m, view.UnlinkedAmount);
        Assert.Equal(0m, view.SubmittedAmount);
        Assert.Null(view.CounterpartLinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountLinked, view.AmountStatus);
        Assert.Contains("采购订单为付款方向", view.AmountNote);

        Assert.Equal(new[]
        {
            OrderFinanceReconciliation.RoleSettlement, OrderFinanceReconciliation.RoleSalesOrderApply,
            OrderFinanceReconciliation.RoleExpense, OrderFinanceReconciliation.RoleReceipt,
            OrderFinanceReconciliation.RoleContainerSettlement, OrderFinanceReconciliation.RoleBulkSettlement,
        }, view.Sections.Select(s => s.Role));

        var settlement = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSettlement);
        Assert.Equal(OrderFinanceReconciliation.LinkLinked, settlement.LinkStatus);
        Assert.Equal(OrderFinanceReconciliation.DirectionOut, settlement.Direction);
        Assert.Equal(2000m, settlement.CountedAmount);
        Assert.Equal(0m, settlement.ListedAmount);
        Assert.Equal(1, settlement.ListedRecordCount);
        Assert.Equal(1, settlement.UnsummedRecordCount);
        Assert.Equal(new[] { "PAY-1", "PAY-2" }, settlement.Records.Select(r => r.DocumentNo));
        Assert.DoesNotContain(settlement.Records, r => r.DocumentNo == "PAY-3");
        Assert.True(settlement.Records[0].Counted);
        Assert.Contains("经货款申请单 PA-1", settlement.Records[0].Note);
        Assert.False(settlement.Records[1].Counted);
        Assert.Contains("币种与本单不一致", settlement.Records[1].Note);

        // 归属销售订单级的定金 / 货款申请单（既有的 2 张）只列出、不归属到本采购单
        var salesApply = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSalesOrderApply);
        Assert.Equal(OrderFinanceReconciliation.LinkUnattributed, salesApply.LinkStatus);
        Assert.Equal(OrderFinanceReconciliation.DirectionIn, salesApply.Direction);
        Assert.Equal(new[] { "DA-SO-1", "PA-1" }, salesApply.Records.Select(r => r.DocumentNo));
        Assert.All(salesApply.Records, r => Assert.False(r.Counted));
        Assert.Equal(0m, salesApply.CountedAmount);
    }

    [Fact]
    public async Task Purchase_order_without_unique_owning_reference_reports_unknown_amounts()
    {
        using var db = TestDbFactory.Create();
        var order = AddPurchaseOrder(db, total: 5000m, currency: Currency.CNY, supplierId: 9);
        db.FinancePayments.Add(new FinancePayment
        {
            PaymentNo = "PAY-1", PaymentDate = new DateTime(2026, 6, 1), SupplierId = 9, PaymentApplyId = null,
            Amount = 1000m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
        });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForPurchaseOrderAsync(db, order.Id);

        Assert.Null(view.LinkedAmount);
        Assert.Null(view.UnlinkedAmount);
        Assert.Null(view.SubmittedAmount);
        Assert.Null(view.CounterpartLinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountUnknown, view.AmountStatus);
        Assert.Contains("未知", view.AmountNote);

        Assert.Equal(new[]
        {
            OrderFinanceReconciliation.RoleSettlement, OrderFinanceReconciliation.RoleSupplierPayment,
            OrderFinanceReconciliation.RoleSalesOrderApply, OrderFinanceReconciliation.RoleExpense,
            OrderFinanceReconciliation.RoleReceipt, OrderFinanceReconciliation.RoleContainerSettlement,
            OrderFinanceReconciliation.RoleBulkSettlement,
        }, view.Sections.Select(s => s.Role));

        var settlement = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSettlement);
        Assert.Equal(OrderFinanceReconciliation.LinkUnavailable, settlement.LinkStatus);
        Assert.Null(settlement.CountedAmount);
        Assert.Empty(settlement.Records);

        // 权威链不可用时，供应商级付款单只列出（不按供应商汇总计入）
        var supplierPayment = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSupplierPayment);
        Assert.Equal(OrderFinanceReconciliation.LinkUnattributed, supplierPayment.LinkStatus);
        Assert.Single(supplierPayment.Records);
        Assert.False(supplierPayment.Records[0].Counted);
        Assert.Contains("不按供应商汇总", supplierPayment.Records[0].Note);
        Assert.Equal(1000m, supplierPayment.ListedAmount);
        Assert.Equal(0m, supplierPayment.CountedAmount);

        var salesApply = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSalesOrderApply);
        Assert.Equal(OrderFinanceReconciliation.LinkUnavailable, salesApply.LinkStatus);
        Assert.Null(salesApply.CountedAmount);
        Assert.Contains("未关联归属销售订单", salesApply.LinkReason);

        var receipt = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleReceipt);
        Assert.Equal(OrderFinanceReconciliation.LinkUnavailable, receipt.LinkStatus);
        Assert.Null(receipt.CountedAmount);
        Assert.Contains("未维护客户", receipt.LinkReason);
    }

    [Fact]
    public async Task Purchase_order_with_ambiguous_owning_sales_order_is_not_inferred()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = AddSalesOrder(db, total: 4000m, currency: Currency.USD, customerId: 5, orderNo: "SO-OWN-2");
        var first = AddPurchaseOrder(db, total: 1000m, currency: Currency.CNY, supplierId: 9,
            owningSalesOrderId: salesOrder.Id, owningSalesOrderNo: salesOrder.OrderNo);
        AddPurchaseOrder(db, total: 2000m, currency: Currency.CNY, supplierId: 9,
            owningSalesOrderId: salesOrder.Id, owningSalesOrderNo: salesOrder.OrderNo, orderNo: "PO-RC-2");
        db.FinancePayments.Add(new FinancePayment
        {
            PaymentNo = "PAY-1", PaymentDate = new DateTime(2026, 7, 1), SupplierId = 9, PaymentApplyId = null,
            Amount = 700m, Currency = Currency.CNY, Status = DocumentStatus.Approved,
        });
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForPurchaseOrderAsync(db, first.Id);

        Assert.Null(view.LinkedAmount);
        Assert.Null(view.UnlinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountUnknown, view.AmountStatus);

        var settlement = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleSettlement);
        Assert.Equal(OrderFinanceReconciliation.LinkAmbiguous, settlement.LinkStatus);
        Assert.Contains("无法在既有引用下唯一归属", settlement.LinkReason);
        Assert.Contains("2 张采购订单", settlement.LinkReason);
        Assert.Contains("付款单（无法归属（仅列出））", view.AmountNote);
    }

    [Fact]
    public async Task Missing_orders_are_rejected()
    {
        using var db = TestDbFactory.Create();
        await Assert.ThrowsAsync<BusinessException>(() => OrderFinanceReconciliation.ForSalesOrderAsync(db, 999));
        await Assert.ThrowsAsync<BusinessException>(() => OrderFinanceReconciliation.ForPurchaseOrderAsync(db, 999));
    }

    [Fact]
    public async Task Records_are_capped_by_the_fixed_record_limit()
    {
        using var db = TestDbFactory.Create();
        var order = AddSalesOrder(db, total: 1000m, currency: Currency.USD, customerId: 7);
        for (var i = 1; i <= OrderFinanceReconciliation.RecordLimit + 50; i++)
        {
            db.FinanceReceipts.Add(new FinanceReceipt
            {
                ReceiptNo = $"R-{i:D3}", ReceiptDate = new DateTime(2026, 8, 1).AddMinutes(i), CustomerId = 7,
                Amount = 10m, Currency = Currency.USD, Status = DocumentStatus.Approved,
            });
        }
        db.SaveChanges();

        var view = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);

        var receipt = view.Sections.Single(s => s.Role == OrderFinanceReconciliation.RoleReceipt);
        Assert.Equal(OrderFinanceReconciliation.RecordLimit, receipt.Records.Count);
        Assert.Equal(OrderFinanceReconciliation.RecordLimit, receipt.ListedRecordCount);
        Assert.Equal(OrderFinanceReconciliation.RecordLimit * 10m, receipt.ListedAmount);
    }

    [Fact]
    public void Ui_and_controllers_expose_the_reconciliation_entry_point()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var modules = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/modules-doc.js"));
        var index = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/index.html"));
        var script = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/order-finance-reconciliation.js"));
        var salesController = File.ReadAllText(Path.Combine(root, "ERP.Api/Controllers/SalesOrderController.cs"));
        var purchaseController = File.ReadAllText(Path.Combine(root, "ERP.Api/Controllers/PurchaseOrderController.cs"));

        // 销售订单 + 采购订单各一个「财务核对」行操作
        Assert.Equal(2, modules.Split("onclick: 'showOrderFinanceReconciliation'").Length - 1);
        Assert.Contains("/js/order-finance-reconciliation.js", index);
        Assert.Contains("/finance-reconciliation", script);
        Assert.Contains("reconMoney", script);
        Assert.Contains("'未知'", script);
        Assert.Contains("/finance-reconciliation", salesController);
        Assert.Contains("OrderFinanceReconciliation.ForSalesOrderAsync", salesController);
        Assert.Contains("/finance-reconciliation", purchaseController);
        Assert.Contains("OrderFinanceReconciliation.ForPurchaseOrderAsync", purchaseController);
    }

    private static SalesOrder AddSalesOrder(ErpDbContext db, decimal total, Currency currency, long customerId,
        string orderNo = SalesOrderNo)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 1, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = total,
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder AddPurchaseOrder(ErpDbContext db, decimal total, Currency currency, long supplierId,
        long? owningSalesOrderId = null, string owningSalesOrderNo = "", long? owningCustomerId = null,
        string orderNo = PurchaseOrderNo)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 1, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = total,
            Status = DocumentStatus.Approved,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = owningSalesOrderNo,
            OwningCustomerId = owningCustomerId,
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }
}
