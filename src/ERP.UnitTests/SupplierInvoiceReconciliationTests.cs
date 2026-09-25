using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-044 供应商采购发票对账报表（只读派生）单元测试：多供应商 / 多币种分组（绝不跨币种合并）、
/// 订单侧订单金额 / 已开票金额 / 未开票余额（只按 ERP-043 持久化关联行派生）、
/// 发票侧未关联金额单列（绝不猜测到订单）、已作废发票默认排除且只经显式筛选可见、
/// 订单删除 / 取消时的未知处理（不按 0 / 已付款）、收货与结算上下文复用 ERP-026 权威口径、
/// 筛选（供应商 / 币种 / 订单日期 / 开票日期 / 关联状态 / 证据状态 / 关键字）、分页有界、
/// 固定次数数据集访问与只读不写库、接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SupplierInvoiceReconciliationTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 944001L;
    private const long SupplierB = 944002L;
    private const long OrderA = 944101L;
    private const long OrderB = 944102L;

    // ==================== 1. 分组：供应商 + 币种，不同币种绝不合并 ====================

    [Fact]
    public async Task Report_groups_invoice_evidence_by_supplier_and_currency_without_merging_currencies()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, OrderA, "PO-REC-1", SupplierA, Currency.CNY, 1000m);
        SeedOrder(db, OrderB, "PO-REC-2", SupplierA, Currency.USD, 200m);
        var cny = SeedInvoice(db, 944201L, SupplierA, "CNY", "0001", 100m, 13m, 113m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 9, 20));
        var usd = SeedInvoice(db, 944202L, SupplierA, "USD", "0002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 9, 21));
        var other = SeedInvoice(db, 944203L, SupplierB, "CNY", "0003", 50m, 6.5m, 56.5m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 9, 22));
        SeedAllocation(db, cny, OrderA, 113m);
        SeedAllocation(db, usd, OrderB, 100m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.PageInvoiceCount);
        Assert.Equal(3, report.Groups.Count);

        var aCny = report.Groups.Single(g => g.SupplierId == SupplierA && g.Currency == "CNY");
        Assert.Equal("甲供应商", aCny.SupplierName);
        Assert.Equal(1, aCny.InvoiceCount);
        Assert.Equal(113m, aCny.GrossAmount);
        Assert.Equal(113m, aCny.LinkedAmount);
        Assert.Equal(0m, aCny.UnlinkedAmount);
        Assert.Equal(1, aCny.OrderCount);
        Assert.Equal(1000m, aCny.OrderedAmount);
        Assert.Equal(113m, aCny.InvoicedAmount);
        Assert.Equal(887m, aCny.RemainingUninvoicedAmount);
        Assert.Equal(1, aCny.PartiallyInvoicedOrderCount);

        var aUsd = report.Groups.Single(g => g.SupplierId == SupplierA && g.Currency == "USD");
        Assert.Equal(200m, aUsd.OrderedAmount);
        Assert.Equal(100m, aUsd.InvoicedAmount);
        Assert.Equal(0, aUsd.FullyInvoicedOrderCount);
        Assert.Equal(1, aUsd.PartiallyInvoicedOrderCount);

        var bCny = report.Groups.Single(g => g.SupplierId == SupplierB && g.Currency == "CNY");
        Assert.Equal(56.5m, bCny.GrossAmount);

        // 币种汇总分别成行：CNY = 113 + 56.5（同币种跨供应商），USD = 100（不与 CNY 相加）
        Assert.Equal(2, report.Currencies.Count);
        var cnySummary = report.Currencies.Single(c => c.Currency == "CNY");
        Assert.Equal(2, cnySummary.SupplierCount);
        Assert.Equal(2, cnySummary.InvoiceCount);
        Assert.Equal(169.5m, cnySummary.GrossAmount);
        var usdSummary = report.Currencies.Single(c => c.Currency == "USD");
        Assert.Equal(100m, usdSummary.GrossAmount);
        Assert.Equal(1, usdSummary.SupplierCount);

        // 报表不存在任何「跨币种总额」字段：金额只能按币种分别查看
        var names = typeof(SupplierInvoiceReconciliationReport).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("TotalAmount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("GrandTotal", StringComparison.Ordinal));
    }

    // ==================== 2. 订单侧：订单金额 / 已开票金额 / 未开票余额只按持久化关联行派生 ====================

    [Fact]
    public async Task Report_derives_ordered_invoiced_and_remaining_amounts_from_persisted_rows_only()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, OrderA, "PO-REC-3", SupplierA, Currency.CNY, 1000m);
        var first = SeedInvoice(db, 944211L, SupplierA, "CNY", "0011", 265.49m, 34.51m, 300m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-3));
        var second = SeedInvoice(db, 944212L, SupplierA, "CNY", "0012", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-2));
        SeedAllocation(db, first, OrderA, 300m);
        SeedAllocation(db, second, OrderA, 200m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        var group = Assert.Single(report.Groups);
        Assert.Equal(1, group.OrderCount);
        Assert.Equal(1000m, group.OrderedAmount);
        Assert.Equal(500m, group.InvoicedAmount);             // 300 + 200：两张发票的持久化关联行
        Assert.Equal(500m, group.RemainingUninvoicedAmount);  // 1000 − 500
        Assert.Equal(1, group.PartiallyInvoicedOrderCount);

        var line = Assert.Single(group.Invoices.Single(i => i.InvoiceNumber == "0011").Orders);
        Assert.Equal(OrderA, line.PurchaseOrderId);
        Assert.Equal("PO-REC-3", line.OrderNo);
        Assert.Equal(1000m, line.OrderedAmount);
        Assert.Equal(300m, line.AllocatedAmount);             // 本发票的关联行金额
        Assert.Equal(500m, line.InvoicedAmount);              // 该订单全部有效发票（与本页筛选无关）
        Assert.Equal(500m, line.RemainingUninvoicedAmount);
        Assert.Equal(2, line.InvoiceCount);
        Assert.Equal(2, line.AllocationCount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced, line.CoverageStatus);
        Assert.Contains("未开票余额 = 订单金额 − 已开票金额", line.Note);
    }

    // ==================== 3. 发票侧：部分关联与整笔未关联的金额单列，绝不猜测到订单 ====================

    [Fact]
    public async Task Report_lists_partially_linked_and_wholly_unlinked_invoice_amounts_separately()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, OrderA, "PO-REC-4", SupplierA, Currency.CNY, 1000m);
        var partial = SeedInvoice(db, 944221L, SupplierA, "CNY", "0021", 265.49m, 34.51m, 300m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-4));
        var unlinked = SeedInvoice(db, 944222L, SupplierA, "CNY", "0022", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1));
        SeedAllocation(db, partial, OrderA, 120m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        Assert.Equal(1, report.PartialInvoiceCount);
        Assert.Equal(1, report.UnlinkedInvoiceCount);
        Assert.Equal(0, report.LinkedInvoiceCount);

        var group = Assert.Single(report.Groups);
        Assert.Equal(300m + 100m, group.GrossAmount);
        Assert.Equal(120m, group.LinkedAmount);
        Assert.Equal(280m, group.UnlinkedAmount);            // 180（部分关联未关联部分）+ 100（整笔未关联）

        var partialRow = group.Invoices.Single(i => i.InvoiceId == partial.Id);
        Assert.Equal(PurchaseInvoiceRules.LinkagePartial, partialRow.LinkageStatus);
        Assert.Equal(120m, partialRow.LinkedAmount);
        Assert.Equal(180m, partialRow.UnlinkedAmount);
        Assert.Single(partialRow.Orders);
        Assert.Contains("未关联金额只作本发票的未关联金额单列", partialRow.Note);

        var unlinkedRow = group.Invoices.Single(i => i.InvoiceId == unlinked.Id);
        Assert.Equal(PurchaseInvoiceRules.LinkageUnlinked, unlinkedRow.LinkageStatus);
        Assert.Equal(0m, unlinkedRow.LinkedAmount);
        Assert.Equal(100m, unlinkedRow.UnlinkedAmount);
        Assert.Equal(0, unlinkedRow.AllocationCount);
        Assert.Empty(unlinkedRow.Orders);                     // 未关联发票不会出现在任何订单行下

        // 未关联金额绝不猜测到订单：订单侧已开票金额只等于持久化关联行（120）
        Assert.Equal(120m, group.InvoicedAmount);
        Assert.Equal(880m, group.RemainingUninvoicedAmount);
    }

    // ==================== 4. 已作废发票：默认排除，只能经显式证据状态筛选查看，且永不并入有效合计 ====================

    [Fact]
    public async Task Report_excludes_voided_invoices_from_active_totals_and_exposes_them_by_explicit_filter()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, OrderA, "PO-REC-5", SupplierA, Currency.CNY, 1000m);
        var recorded = SeedInvoice(db, 944231L, SupplierA, "CNY", "0031", 265.49m, 34.51m, 300m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-5));
        SeedAllocation(db, recorded, OrderA, 300m);
        var voided = SeedInvoice(db, 944232L, SupplierA, "CNY", "0032", 132.74m, 17.26m, 150m,
            PurchaseInvoiceRules.StatusVoided, AsOf.AddDays(-4), voidReason: "供应商作废重开");
        SeedAllocation(db, voided, OrderA, 150m);
        await db.SaveChangesAsync();

        // 默认（仅已登记证据）：已作废发票完全不出现，也不影响任何有效合计
        var byDefault = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());
        Assert.Equal(1, byDefault.Total);
        Assert.Equal(0, byDefault.VoidedInvoiceCount);
        var group = Assert.Single(byDefault.Groups);
        Assert.Equal(300m, group.GrossAmount);
        Assert.Equal(0, group.VoidedInvoiceCount);
        Assert.Equal(0m, group.VoidedGrossAmount);
        Assert.Equal(300m, group.InvoicedAmount);              // 已作废的 150 不计入
        Assert.Equal(700m, group.RemainingUninvoicedAmount);

        // 显式「仅已作废历史证据」：可查看历史证据，但有效合计仍为 0（作废金额单独成列）
        var voidedOnly = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(evidenceStatus: SupplierInvoiceReconciliationSemantics.EvidenceVoided));
        Assert.Equal(1, voidedOnly.Total);
        Assert.Equal(1, voidedOnly.VoidedInvoiceCount);
        var voidedGroup = Assert.Single(voidedOnly.Groups);
        Assert.Equal(0, voidedGroup.InvoiceCount);             // 有效发票为 0
        Assert.Equal(0m, voidedGroup.GrossAmount);
        Assert.Equal(1, voidedGroup.VoidedInvoiceCount);
        Assert.Equal(150m, voidedGroup.VoidedGrossAmount);     // 历史金额单列，不并入有效合计
        Assert.Equal(0m, voidedGroup.InvoicedAmount);
        Assert.Equal(150m, voidedGroup.VoidedOrderAllocatedAmount);   // 作废关联金额单列（不并入有效合计）
        var voidedRow = Assert.Single(voidedGroup.Invoices);
        Assert.True(voidedRow.IsVoided);
        Assert.Equal("已作废", voidedRow.StatusText);
        Assert.Equal("供应商作废重开", voidedRow.VoidReason);
        Assert.Contains("不计入任何有效合计", voidedRow.EvidenceText);
        var voidedLine = Assert.Single(voidedRow.Orders);
        Assert.Equal(150m, voidedLine.AllocatedAmount);
        Assert.Equal(300m, voidedLine.InvoicedAmount);         // 有效发票（已登记）仍按订单口径如实显示
        Assert.Equal(150m, voidedLine.VoidedAllocatedAmount);  // 作废关联单独成列
        Assert.Contains("已作废", voidedLine.Note);

        // 全部状态：两行都在，但有效合计只算已登记，作废金额仍单独成列
        var all = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(evidenceStatus: SupplierInvoiceReconciliationSemantics.EvidenceAll));
        Assert.Equal(2, all.Total);
        var allGroup = Assert.Single(all.Groups);
        Assert.Equal(300m, allGroup.GrossAmount);
        Assert.Equal(150m, allGroup.VoidedGrossAmount);
        Assert.Equal(300m, allGroup.InvoicedAmount);
        Assert.Equal(150m, allGroup.VoidedOrderAllocatedAmount);
    }

    // ==================== 5. 订单不可用 / 已取消：金额与覆盖状态按未知显示，绝不按 0 ====================

    [Fact]
    public async Task Report_marks_missing_or_cancelled_order_amounts_unknown_instead_of_zero()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, OrderA, "PO-REC-6", SupplierA, Currency.CNY, 1000m, deleted: true);
        SeedOrder(db, OrderB, "PO-REC-7", SupplierA, Currency.CNY, 400m,
            status: DocumentStatus.Cancelled);
        var invoice = SeedInvoice(db, 944241L, SupplierA, "CNY", "0041", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-6));
        SeedAllocation(db, invoice, OrderA, 60m);
        SeedAllocation(db, invoice, OrderB, 40m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.OrderCount);
        Assert.Equal(1, group.UnknownOrderCount);
        Assert.Equal(1, group.CancelledOrderCount);
        Assert.Equal(0m, group.OrderedAmount);                 // 未知 / 已取消订单金额不计入合计
        Assert.Equal(0m, group.InvoicedAmount);
        Assert.Equal(0m, group.RemainingUninvoicedAmount);
        Assert.Equal(100m, group.LinkedAmount);                // 发票侧金额仍如实呈现

        var row = Assert.Single(group.Invoices);
        var deletedLine = row.Orders.Single(o => o.PurchaseOrderId == OrderA);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateUnavailable, deletedLine.OrderState);
        Assert.Null(deletedLine.OrderedAmount);                // 未知，不是 0
        Assert.Null(deletedLine.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageUnknown, deletedLine.CoverageStatus);
        Assert.Equal(60m, deletedLine.AllocatedAmount);         // 持久化关联行金额仍可读
        Assert.Equal(SupplierInvoiceReconciliationSemantics.ReceiptUnknown, deletedLine.ReceiptStatus);
        Assert.Null(deletedLine.ReceivedQuantity);              // 未知 ≠ 0
        Assert.Null(deletedLine.SettledAmount);                 // 未知 ≠ 已付款
        Assert.Contains("订单金额未知（不按 0 处理）", deletedLine.OrderStateText);
        Assert.Contains("未知", deletedLine.Note);

        var cancelledLine = row.Orders.Single(o => o.PurchaseOrderId == OrderB);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateCancelled, cancelledLine.OrderState);
        Assert.Equal(400m, cancelledLine.OrderedAmount);        // 订单金额仍在（仅历史参考）
        Assert.Null(cancelledLine.RemainingUninvoicedAmount);   // 已取消：不计算未开票余额
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageUnknown, cancelledLine.CoverageStatus);
        Assert.Contains("已取消", cancelledLine.OrderStateText);
        Assert.Equal(1, report.UnknownOrderCount);
    }

    // ==================== 6. 收货 / 结算上下文：复用 ERP-026 权威口径，未知一律 null ====================

    [Fact]
    public async Task Report_reuses_authoritative_receipt_and_settlement_context_and_keeps_unknown_null()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-REC-1");
        var linked = SeedOrder(db, OrderA, "PO-REC-8", SupplierA, Currency.CNY, 1000m,
            owningSalesOrderId: salesOrder.Id, quantity: 10m);
        var unlinked = SeedOrder(db, OrderB, "PO-REC-9", SupplierA, Currency.CNY, 500m, quantity: 5m);
        SeedStockIn(db, "RK-REC-1", linked.Id, DocumentStatus.Approved, 4m);
        SeedStockIn(db, "RK-REC-2", linked.Id, DocumentStatus.Submitted, 2m);   // 未审核：只列待审
        var apply = SeedPaymentApply(db, "HK-REC-1", salesOrder.Id);
        SeedPayment(db, "FK-REC-1", apply.Id, SupplierA, 250m, Currency.CNY, DocumentStatus.Approved);
        var invoice = SeedInvoice(db, 944251L, SupplierA, "CNY", "0051", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-2));
        SeedAllocation(db, invoice, linked.Id, 60m);
        SeedAllocation(db, invoice, unlinked.Id, 40m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        var row = Assert.Single(Assert.Single(report.Groups).Invoices);
        var linkedLine = row.Orders.Single(o => o.PurchaseOrderId == linked.Id);
        Assert.Equal(10m, linkedLine.OrderedQuantity);
        Assert.Equal(4m, linkedLine.ReceivedQuantity);
        Assert.Equal(6m, linkedLine.OutstandingQuantity);
        Assert.Equal(2m, linkedLine.PendingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, linkedLine.ReceiptStatus);
        Assert.Equal(PurchaseOrderProgress.LinkLinked, linkedLine.SettlementLinkStatus);
        Assert.Equal(250m, linkedLine.SettledAmount);
        Assert.Equal(750m, linkedLine.OutstandingSettlementAmount);

        // 无归属销售订单的订单：结算金额未知（null），绝不按 0 / 已付款处理
        var unlinkedLine = row.Orders.Single(o => o.PurchaseOrderId == unlinked.Id);
        Assert.Equal(PurchaseOrderProgress.LinkUnavailable, unlinkedLine.SettlementLinkStatus);
        Assert.Null(unlinkedLine.SettledAmount);
        Assert.Null(unlinkedLine.OutstandingSettlementAmount);
        Assert.Contains("未关联归属销售订单", unlinkedLine.SettlementLinkReason);
        Assert.Equal(5m, unlinkedLine.OrderedQuantity);
        Assert.Equal(0m, unlinkedLine.ReceivedQuantity);
    }

    // ==================== 7. 筛选：供应商 / 币种 / 订单日期 / 开票日期 / 关联状态 / 关键字 ====================

    [Fact]
    public async Task Report_filters_by_supplier_currency_dates_linkage_and_keyword()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        var orderEarly = SeedOrder(db, OrderA, "PO-REC-10", SupplierA, Currency.CNY, 100m,
            orderDate: new DateTime(2026, 3, 10));
        var orderLate = SeedOrder(db, OrderB, "PO-REC-11", SupplierA, Currency.CNY, 100m,
            orderDate: new DateTime(2026, 8, 10));
        var early = SeedInvoice(db, 944261L, SupplierA, "CNY", "0061", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 4, 1));
        var late = SeedInvoice(db, 944262L, SupplierA, "CNY", "0062", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 9, 1));
        var foreign = SeedInvoice(db, 944263L, SupplierB, "USD", "0063", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 9, 2));
        var partial = SeedInvoice(db, 944264L, SupplierA, "CNY", "0064", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 9, 3));
        SeedAllocation(db, early, orderEarly.Id, 100m);
        SeedAllocation(db, late, orderLate.Id, 100m);
        SeedAllocation(db, partial, orderLate.Id, 40m);
        await db.SaveChangesAsync();

        Assert.Equal(4, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query())).Total);

        var bySupplier = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(supplierId: SupplierB));
        Assert.Equal(1, bySupplier.Total);
        Assert.Equal("USD", Assert.Single(bySupplier.Groups).Currency);

        var byCurrency = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(currency: "usd"));
        Assert.Equal(1, byCurrency.Total);
        Assert.Equal("USD", byCurrency.Currency);                 // 币种按枚举名归一化

        var byInvoiceDate = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(invoiceFrom: new DateTime(2026, 9, 1), invoiceTo: new DateTime(2026, 9, 2)));
        Assert.Equal(2, byInvoiceDate.Total);
        Assert.DoesNotContain(byInvoiceDate.Groups.SelectMany(g => g.Invoices), i => i.InvoiceNumber == "0061");

        // 订单日期按持久化关联行的订单日期快照筛选：只返回已关联到该时段订单的发票
        var byOrderDate = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(orderFrom: new DateTime(2026, 8, 1), orderTo: new DateTime(2026, 8, 31)));
        Assert.Equal(2, byOrderDate.Total);
        Assert.DoesNotContain(byOrderDate.Groups.SelectMany(g => g.Invoices), i => i.InvoiceNumber == "0061");
        Assert.DoesNotContain(byOrderDate.Groups.SelectMany(g => g.Invoices), i => i.InvoiceNumber == "0063");

        var fullyLinked = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(linkageStatus: PurchaseInvoiceRules.LinkageLinked));
        Assert.Equal(2, fullyLinked.Total);                        // 0061 / 0062 全额关联
        var partiallyLinked = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(linkageStatus: PurchaseInvoiceRules.LinkagePartial));
        Assert.Equal(1, partiallyLinked.Total);
        Assert.Equal("0064", Assert.Single(partiallyLinked.Groups.SelectMany(g => g.Invoices)).InvoiceNumber);
        Assert.Equal(1, (await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(linkageStatus: PurchaseInvoiceRules.LinkageUnlinked))).Total);   // 0063 整笔未关联

        // 关键字：发票号码 / 供应商名称 / 关联行的采购单号快照
        Assert.Equal(1, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(keyword: "0062"))).Total);
        Assert.Equal(3, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(keyword: "甲供应商"))).Total);
        Assert.Equal(2, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(keyword: "PO-REC-11"))).Total);
        Assert.Equal(0, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(keyword: "不存在"))).Total);
    }

    // ==================== 8. 分页：稳定排序，合计只统计本页 ====================

    [Fact]
    public async Task Report_pages_invoices_stably_and_scopes_totals_to_the_returned_page()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 1; i <= 5; i++)
        {
            SeedInvoice(db, 944270L + i, SupplierA, "CNY", $"007{i}", i * 10m, 0m, i * 10m,
                PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 1, i));
        }

        await db.SaveChangesAsync();

        var first = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(page: 1, pageSize: 2));
        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.PageInvoiceCount);
        Assert.Equal(90m, first.Groups.Single().GrossAmount);      // 只统计本页：50 + 40（开票日期倒序）

        var second = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        Assert.Equal(2, second.PageInvoiceCount);
        Assert.Equal(50m, second.Groups.Single().GrossAmount);     // 30 + 20

        var third = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(page: 3, pageSize: 2));
        Assert.Equal(1, third.PageInvoiceCount);
        Assert.Equal(10m, third.Groups.Single().GrossAmount);

        // 稳定排序：三页合起来恰好覆盖全部发票，无重复、无遗漏
        var numbers = first.Groups.SelectMany(g => g.Invoices).Select(i => i.InvoiceNumber)
            .Concat(second.Groups.SelectMany(g => g.Invoices).Select(i => i.InvoiceNumber))
            .Concat(third.Groups.SelectMany(g => g.Invoices).Select(i => i.InvoiceNumber))
            .ToList();
        Assert.Equal(new[] { "0075", "0074", "0073", "0072", "0071" }, numbers);

        // 越界页：没有发票行，也不臆造合计
        var beyond = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query(page: 9, pageSize: 2));
        Assert.Equal(0, beyond.PageInvoiceCount);
        Assert.Empty(beyond.Groups);
        Assert.Empty(beyond.Currencies);
    }

    // ==================== 9. 有界查询与只读：数据集访问次数与行数无关，且全程不写库 ====================

    [Fact]
    public async Task Report_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-REC-2");
        var order = SeedOrder(db, OrderA, "PO-BOUND-1", SupplierA, Currency.CNY, 10m,
            owningSalesOrderId: salesOrder.Id, quantity: 1m);
        SeedStockIn(db, "RK-BOUND-1", order.Id, DocumentStatus.Approved, 1m);
        var apply = SeedPaymentApply(db, "HK-REC-2", salesOrder.Id);
        SeedPayment(db, "FK-REC-2", apply.Id, SupplierA, 5m, Currency.CNY, DocumentStatus.Approved);
        var invoice = SeedInvoice(db, 944281L, SupplierA, "CNY", "0081", 10m, 0m, 10m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1));
        SeedAllocation(db, invoice, order.Id, 10m);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var single = await SupplierInvoiceReconciliation.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        Assert.Equal(1, single.PageInvoiceCount);
        // 常数级访问：发票筛选（含分页）+ 本页发票 + 本页关联行 + 本页订单 + 订单关联行 + 关联发票状态 +
        // 供应商名 + 收货 2 次 + 结算 3 次 + 付款引用证据 2 次
        //（ERP-050：订单 + 持久化引用行；本用例没有付款引用行 → 付款单与付款单侧聚合不再访问）
        // + 已分配付款引用证据 2 次（ERP-067：ERP-066 持久化引用行 + 发票；本用例没有引用行 →
        //   付款单与付款单侧有效引用合计不再访问）
        Assert.Equal(16, singleReads);

        // 再补 300 张发票（跨多页，且都关联同一张订单）：同一报表的数据集访问次数必须保持不变（无 N+1）
        for (var i = 2; i <= 301; i++)
        {
            var extra = SeedInvoice(db, 944300L + i, SupplierA, "CNY", $"1{i:000}", 1m, 0m, 1m,
                PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1));
            SeedAllocation(db, extra, order.Id, 1m);
        }

        await db.SaveChangesAsync();

        var large = await SupplierInvoiceReconciliation.ForQueryAsync(counting.Proxy, Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(SupplierInvoiceReconciliationQuery.MaxPageSize, large.PageInvoiceCount);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                                                   // 只读报表：没有一次 SaveChanges

        // 汇总只统计本页：200 张 × 1 元
        Assert.Equal(200m, large.Groups.Single().GrossAmount);
    }

    // ==================== 10. 只读：不改写发票 / 采购订单 / 库存 / 收付款记录 ====================

    [Fact]
    public async Task Report_does_not_mutate_invoices_orders_stock_payments_or_tax_records()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, OrderA, "PO-REC-12", SupplierA, Currency.CNY, 800m, quantity: 4m);
        var invoice = SeedInvoice(db, 944291L, SupplierA, "CNY", "0091", 100m, 13m, 113m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-3));
        SeedAllocation(db, invoice, order.Id, 113m);
        SeedStockIn(db, "RK-REC-3", order.Id, DocumentStatus.Approved, 2m);
        db.Stocks.Add(new Stock { WarehouseId = 1, ProductId = 1, Quantity = 7m });
        db.BaseTaxRefunds.Add(new BaseTaxRefund
        {
            RefundNo = "TS-REC-1",
            SalesOrderNo = "SO-REC-9",
            RefundableAmount = 321m,
            Status = "待申报"
        });
        await db.SaveChangesAsync();

        var orderBefore = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        var invoiceBefore = await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        var allocationBefore = await db.PurchaseInvoiceAllocations.AsNoTracking().FirstAsync();
        var stockBefore = await db.Stocks.AsNoTracking().FirstAsync();
        var refundBefore = await db.BaseTaxRefunds.AsNoTracking().FirstAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());
        Assert.Equal(1, report.Total);

        var orderAfter = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        var invoiceAfter = await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        var allocationAfter = await db.PurchaseInvoiceAllocations.AsNoTracking().FirstAsync();
        var stockAfter = await db.Stocks.AsNoTracking().FirstAsync();
        var refundAfter = await db.BaseTaxRefunds.AsNoTracking().FirstAsync();

        Assert.Equal(orderBefore.Status, orderAfter.Status);
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(orderBefore.ArrivalProgress, orderAfter.ArrivalProgress);
        Assert.Equal(orderBefore.SettlementProgress, orderAfter.SettlementProgress);
        Assert.Equal(invoiceBefore.Status, invoiceAfter.Status);
        Assert.Equal(invoiceBefore.GrossAmount, invoiceAfter.GrossAmount);
        Assert.Equal(invoiceBefore.RecordedAt, invoiceAfter.RecordedAt);
        Assert.Equal(allocationBefore.AllocatedAmount, allocationAfter.AllocatedAmount);
        Assert.Equal(stockBefore.Quantity, stockAfter.Quantity);
        Assert.Equal(refundBefore.RefundableAmount, refundAfter.RefundableAmount);
        Assert.Equal(refundBefore.Status, refundAfter.Status);
    }

    // ==================== 11. 参数校验：非法取值拒绝，分页钳制到有界范围 ====================

    [Fact]
    public void Query_normalization_validates_inputs_and_caps_paging()
    {
        var invalidCurrency = Query(currency: "XYZ");
        var currencyError = Assert.Throws<BusinessException>(() => invalidCurrency.Normalize());
        Assert.Contains("币种", currencyError.Message);

        var invalidLinkage = Query(linkageStatus: "settled");
        var linkageError = Assert.Throws<BusinessException>(() => invalidLinkage.Normalize());
        Assert.Contains("关联状态", linkageError.Message);

        var invalidEvidence = Query(evidenceStatus: "paid");
        var evidenceError = Assert.Throws<BusinessException>(() => invalidEvidence.Normalize());
        Assert.Contains("证据状态", evidenceError.Message);

        var invertedOrderDates = Query(orderFrom: new DateTime(2026, 5, 1), orderTo: new DateTime(2026, 4, 1));
        Assert.Throws<BusinessException>(() => invertedOrderDates.Normalize());
        var invertedInvoiceDates = Query(
            invoiceFrom: new DateTime(2026, 5, 1), invoiceTo: new DateTime(2026, 4, 1));
        Assert.Throws<BusinessException>(() => invertedInvoiceDates.Normalize());

        var longKeyword = Query(keyword: new string('x', SupplierInvoiceReconciliationQuery.MaxKeywordLength + 1));
        var keywordError = Assert.Throws<BusinessException>(() => longKeyword.Normalize());
        Assert.Contains("关键字长度", keywordError.Message);

        var clamped = Query(supplierId: 0, keyword: "   ", page: 0, pageSize: 9999);
        clamped.Normalize();
        Assert.Null(clamped.SupplierId);
        Assert.Null(clamped.Keyword);
        Assert.Null(clamped.Currency);
        Assert.Null(clamped.LinkageStatus);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.EvidenceRecorded, clamped.EvidenceStatus);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(SupplierInvoiceReconciliationQuery.MaxPageSize, clamped.PageSize);

        // 默认：只统计已登记证据（已作废与草稿都不进入默认口径）
        var defaults = new SupplierInvoiceReconciliationQuery();
        defaults.Normalize();
        Assert.Equal(SupplierInvoiceReconciliationSemantics.EvidenceRecorded, defaults.EvidenceStatus);
        Assert.Equal(SupplierInvoiceReconciliationQuery.DefaultPageSize, defaults.PageSize);

        // 合法取值按既定口径归一化（大小写不敏感）
        var normalized = Query(currency: " usd ", evidenceStatus: " ALL ", linkageStatus: "linked");
        normalized.Normalize();
        Assert.Equal("USD", normalized.Currency);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.EvidenceAll, normalized.EvidenceStatus);
        Assert.Equal(PurchaseInvoiceRules.LinkageLinked, normalized.LinkageStatus);
    }

    // ==================== 12. 边界：不是应付账款台账 / 付款授权 / 税务申报 / 账龄表 ====================

    [Fact]
    public void Report_exposes_no_payable_aging_or_tax_fields_and_states_the_boundary()
    {
        foreach (var type in new[]
                 {
                     typeof(SupplierInvoiceReconciliationInvoice),
                     typeof(SupplierInvoiceReconciliationOrderLine),
                     typeof(SupplierInvoiceReconciliationGroup),
                     typeof(SupplierInvoiceReconciliationCurrencySummary),
                     typeof(SupplierInvoiceReconciliationReport)
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Paid", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("TaxFiling", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("PayableBalance", StringComparison.Ordinal));
        }

        Assert.Contains("不是", SupplierInvoiceReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("应付账款台账", SupplierInvoiceReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("付款授权", SupplierInvoiceReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("税务申报", SupplierInvoiceReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("账龄", SupplierInvoiceReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("不得当作应付余额", SupplierInvoiceReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("绝不合并", SupplierInvoiceReconciliationSemantics.RuleText);
        Assert.Contains("持久化关联行", SupplierInvoiceReconciliationSemantics.RuleText);
        Assert.Contains("未关联金额", SupplierInvoiceReconciliationSemantics.RuleText);
        Assert.Contains("只统计本次返回页", SupplierInvoiceReconciliationSemantics.ScopeNoteText);
        Assert.Contains("覆盖状态", SupplierInvoiceReconciliationSemantics.CoverageText(
            SupplierInvoiceReconciliationSemantics.CoverageUnknown));
        Assert.Contains("已登记", SupplierInvoiceReconciliationSemantics.EvidenceStatusText("bad-value"));

        // 只读报表只暴露一个 GET 路由（无新增 / 修改 / 核销入口）
        var routes = typeof(PurchaseInvoiceController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("reconciliation", routes);
        Assert.Single(routes.Where(r => r == "reconciliation"));
        Assert.DoesNotContain(typeof(PurchaseInvoiceController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("reconciliation", StringComparison.Ordinal));
    }

    // ==================== 13. 空数据集：无行、无币种汇总，不臆造金额 ====================

    [Fact]
    public async Task Report_returns_empty_page_for_empty_dataset_without_inventing_amounts()
    {
        using var db = TestDbFactory.Create();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        Assert.Equal(0, report.Total);
        Assert.Equal(0, report.PageInvoiceCount);
        Assert.Equal(0, report.TotalPages);
        Assert.Equal(0, report.OrderCount);
        Assert.Equal(0, report.UnknownOrderCount);
        Assert.Equal(0, report.ReceiptUnknownCount);
        Assert.Empty(report.Groups);
        Assert.Empty(report.Currencies);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.EvidenceRecorded, report.EvidenceStatus);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.EvidenceStatusText(
            SupplierInvoiceReconciliationSemantics.EvidenceRecorded), report.EvidenceStatusText);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.RuleText, report.Rule);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.ScopeNoteText, report.ScopeNote);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.LedgerBoundaryText, report.LedgerBoundary);
        Assert.Equal(PurchaseInvoiceRules.AmountEquationText, report.AmountEquation);
        Assert.Equal(PurchaseInvoiceRules.LinkageRuleText, report.LinkageRule);
    }

    // ==================== 14. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_reconciliation_payload()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, OrderA, "PO-REC-14", SupplierA, Currency.CNY, 1000m, quantity: 2m);
        var invoice = SeedInvoice(db, 944295L, SupplierA, "CNY", "0095", 265.49m, 34.51m, 300m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1));
        SeedAllocation(db, invoice, order.Id, 300m);
        await db.SaveChangesAsync();

        var controller = new PurchaseInvoiceController(db);
        var result = await controller.Reconciliation(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<SupplierInvoiceReconciliationReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        var data = payload.Data!;
        Assert.Equal(1, data.Total);
        var group = Assert.Single(data.Groups);
        Assert.Equal(1000m, group.OrderedAmount);
        Assert.Equal(300m, group.InvoicedAmount);
        Assert.Equal(700m, group.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.LedgerBoundaryText, data.LedgerBoundary);
    }

    // ==================== 15. 与 ERP-043 写入链路一致：只读 ERP-043 持久化行 ====================

    [Fact]
    public async Task Report_reads_the_persisted_rows_created_by_the_erp043_register_workflow()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, OrderA, "PO-REC-15", SupplierA, Currency.CNY, 500m, quantity: 5m);
        await db.SaveChangesAsync();
        var controller = new PurchaseInvoiceController(db);

        var invoice = AssertOk<PurchaseInvoiceDto>(await controller.Create(new PurchaseInvoiceSaveDto
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeSpecial,
            InvoiceCode = "0440-3190",
            InvoiceNumber = "88001",
            InvoiceDate = AsOf.AddDays(-2),
            SupplierId = SupplierA,
            Currency = "CNY",
            NetAmount = 200m,
            TaxAmount = 26m,
            GrossAmount = 226m
        }));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id,
            new PurchaseInvoiceAllocationSaveRequest
            {
                Lines = new List<PurchaseInvoiceAllocationSaveDto>
                {
                    new() { PurchaseOrderId = order.Id, AllocatedAmount = 126m }
                }
            }));
        AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());
        Assert.Equal(1, report.Total);
        var group = Assert.Single(report.Groups);
        Assert.Equal(226m, group.GrossAmount);
        Assert.Equal(126m, group.LinkedAmount);
        Assert.Equal(100m, group.UnlinkedAmount);
        Assert.Equal(126m, group.InvoicedAmount);
        Assert.Equal(374m, group.RemainingUninvoicedAmount);
        Assert.Equal(1, group.PartiallyInvoicedOrderCount);

        var row = Assert.Single(group.Invoices);
        Assert.Equal(PurchaseInvoiceRules.InvoiceTypeSpecial, row.InvoiceType);
        Assert.Contains("0440-3190", row.IdentityText);
        Assert.Equal(PurchaseInvoiceRules.LinkagePartial, row.LinkageStatus);
        Assert.Equal(1, row.AllocationCount);
        var line = Assert.Single(row.Orders);
        Assert.Equal(OrderA, line.PurchaseOrderId);
        Assert.Equal(500m, line.OrderedAmount);
        Assert.Equal(126m, line.AllocatedAmount);
        Assert.Equal(374m, line.RemainingUninvoicedAmount);

        // 草稿只是工作数据：默认（仅已登记）不进入报表
        var draft = AssertOk<PurchaseInvoiceDto>(await controller.Create(new PurchaseInvoiceSaveDto
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceNumber = "88002",
            InvoiceDate = AsOf.AddDays(-1),
            SupplierId = SupplierA,
            Currency = "CNY",
            NetAmount = 50m,
            TaxAmount = 0m,
            GrossAmount = 50m
        }));
        Assert.Equal(1, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query())).Total);
        var draftOnly = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(evidenceStatus: SupplierInvoiceReconciliationSemantics.EvidenceDraft));
        Assert.Equal(1, draftOnly.Total);
        Assert.Equal(draft.Id, Assert.Single(draftOnly.Groups.SelectMany(g => g.Invoices)).InvoiceId);

        // 作废后默认口径不再统计；历史仍可查，且作废关联金额不并入有效已开票金额
        AssertOk<PurchaseInvoiceDto>(await controller.Void(invoice.Id,
            new PurchaseInvoiceVoidRequest { Reason = "供应商作废重开" }));
        Assert.Equal(0, (await SupplierInvoiceReconciliation.ForQueryAsync(db, Query())).Total);

        var history = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            Query(evidenceStatus: SupplierInvoiceReconciliationSemantics.EvidenceAll));
        Assert.Equal(2, history.Total);
        var voidedRow = history.Groups.SelectMany(g => g.Invoices).Single(i => i.InvoiceId == invoice.Id);
        Assert.True(voidedRow.IsVoided);
        var voidedLine = Assert.Single(voidedRow.Orders);
        Assert.Equal(0m, voidedLine.InvoicedAmount);           // 作废关联不计入有效已开票金额
        Assert.Equal(126m, voidedLine.VoidedAllocatedAmount);   // 历史参考单独成列
        Assert.Equal(500m, voidedLine.RemainingUninvoicedAmount);
        // 「全部状态」下有效合计只统计未作废发票：本页有效发票只有那张无关联的草稿，故不含任何订单侧合计
        Assert.Equal(1, history.Groups.Single().InvoiceCount);
        Assert.Equal(50m, history.Groups.Single().GrossAmount);
        Assert.Equal(0m, history.Groups.Single().InvoicedAmount);
        Assert.Equal(0m, history.Groups.Single().RemainingUninvoicedAmount);
        Assert.Equal(126m, history.Groups.Single().VoidedOrderAllocatedAmount);   // 作废关联金额单列

        // 报表与登记流程都不改写采购订单
        var orderAfter = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(500m, orderAfter.TotalAmount);
        Assert.NotEqual(DocumentStatus.Cancelled, orderAfter.Status);
    }

    // ==================== 16. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "supplier-invoice-reconciliation.js"));

        Assert.Contains("function openSupplierInvoiceReconciliationReport()", reportJs);
        Assert.Contains("/api/purchase-invoices/reconciliation?", reportJs);
        Assert.Contains("function sirRenderCurrencyTable(data)", reportJs);
        Assert.Contains("function sirRenderGroupTable(data)", reportJs);
        Assert.Contains("function sirRenderInvoiceTable(data)", reportJs);
        Assert.Contains("function exportSirCsv()", reportJs);
        Assert.Contains("SIR_LINKAGE_LABELS", reportJs);
        Assert.Contains("SIR_COVERAGE_LABELS", reportJs);
        Assert.Contains("SIR_RECEIPT_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);                 // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("不是", reportJs);                    // 页面显式声明不是应付账款台账 / 付款授权 / 税务申报 / 账龄表
        Assert.Contains("CURRENCY_NAME_OPTS", reportJs);      // 币种按枚举名筛选，不做汇率换算

        // 筛选控件齐备：供应商 / 币种 / 订单日期（起止）/ 开票日期（起止）/ 关联状态 / 证据状态 / 关键字 / 每页
        foreach (var id in new[]
                 {
                     "sir-supplier", "sir-currency", "sir-order-date-from", "sir-order-date-to",
                     "sir-invoice-date-from", "sir-invoice-date-to", "sir-linkage", "sir-evidence",
                     "sir-keyword", "sir-pagesize"
                 })
        {
            Assert.Contains(id, reportJs);
        }

        // 工具栏入口（采购订单页）与脚本注册
        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSupplierInvoiceReconciliationReport", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/supplier-invoice-reconciliation.js", index);
    }

    // ==================== 12. 已分配付款引用证据（ERP-067 第四类独立证据） ====================

    [Fact]
    public async Task Report_separates_allocated_payment_evidence_from_ordered_and_invoiced_amounts()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, OrderA, "PO-IP-REC-1", SupplierA, Currency.CNY, 1000m);
        var invoice = SeedInvoice(db, 944401L, SupplierA, "CNY", "0099", 800m, 0m, 800m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1));
        SeedAllocation(db, invoice, order.Id, 800m);
        SeedInvoicePaymentEvidence(db, 944501L, invoice, allocatedAmount: 500m, paymentAmount: 900m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        var group = Assert.Single(report.Groups);
        var row = Assert.Single(group.Invoices);
        Assert.Equal(500m, row.AllocatedPaymentAmount);
        Assert.Equal(1, row.AllocatedPaymentCount);
        Assert.Equal(1, row.AllocatedPaymentDocumentCount);
        Assert.Equal(400m, row.AllocatedPaymentUnallocatedAmount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelRecorded, row.AllocatedPaymentEvidenceLabel);

        Assert.Equal(500m, group.AllocatedPaymentAmount);
        Assert.Equal(1, group.AllocatedPaymentInvoiceCount);
        Assert.Equal(0, group.NoAllocatedPaymentInvoiceCount);
        Assert.Equal(0, group.UnknownAllocatedPaymentInvoiceCount);
        Assert.Equal(0, group.HistoricalAllocatedPaymentCount);

        var currency = Assert.Single(report.Currencies);
        Assert.Equal(500m, currency.AllocatedPaymentAmount);
        Assert.Equal(1, currency.AllocatedPaymentInvoiceCount);

        Assert.Equal(1, report.AllocatedPaymentInvoiceCount);
        Assert.Equal(0, report.NoAllocatedPaymentInvoiceCount);
        Assert.Equal(0, report.UnknownAllocatedPaymentInvoiceCount);

        // 四类证据分列、绝不轧差：订单金额 1000 / 已开票 800 / 已关联 800 / 已分配付款引用 500 各自独立
        Assert.Equal(1000m, group.OrderedAmount);
        Assert.Equal(800m, group.InvoicedAmount);
        Assert.Equal(800m, group.LinkedAmount);
        Assert.Equal(500m, group.AllocatedPaymentAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.FourClassEvidenceText, report.FourEvidenceClasses);
    }

    [Fact]
    public async Task Report_excludes_voided_and_invalid_allocated_payment_evidence_from_active_totals()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, OrderA, "PO-IP-REC-2", SupplierA, Currency.CNY, 1000m);
        var invoice = SeedInvoice(db, 944402L, SupplierA, "CNY", "0100", 800m, 0m, 800m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1));
        SeedAllocation(db, invoice, order.Id, 800m);
        SeedInvoicePaymentEvidence(db, 944502L, invoice, allocatedAmount: 200m, paymentAmount: 900m);
        SeedInvoicePaymentEvidence(db, 944503L, invoice, allocatedAmount: 100m, paymentAmount: 900m,
            status: SupplierPaymentInvoiceAllocationRules.StatusVoided);
        SeedInvoicePaymentEvidence(db, 944504L, invoice, allocatedAmount: 50m, paymentAmount: 900m,
            rowCurrency: "USD");                                     // 币种不一致 → 无效，绝不换算并入
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, Query());

        var row = Assert.Single(Assert.Single(report.Groups).Invoices);
        Assert.Equal(200m, row.AllocatedPaymentAmount);              // 已作废 100 + 无效 50 绝不并入
        Assert.Equal(1, row.AllocatedPaymentCount);
        Assert.Equal(100m, row.VoidedAllocatedPaymentAmount);
        Assert.Equal(1, row.VoidedAllocatedPaymentCount);
        Assert.Equal(50m, row.InvalidAllocatedPaymentAmount);
        Assert.Equal(1, row.InvalidAllocatedPaymentCount);

        var group = Assert.Single(report.Groups);
        Assert.Equal(200m, group.AllocatedPaymentAmount);
        Assert.Equal(2, group.HistoricalAllocatedPaymentCount);      // 已作废 1 + 无效 1
        Assert.Contains("不换算", PurchaseOrderInvoicePaymentEvidenceSemantics.BucketText(
            PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid));
    }

    // ==================== 助手 ====================

    private static SupplierInvoiceReconciliationQuery Query(long? supplierId = null, string? currency = null,
        DateTime? orderFrom = null, DateTime? orderTo = null, DateTime? invoiceFrom = null,
        DateTime? invoiceTo = null, string? linkageStatus = null, string? evidenceStatus = null,
        string? keyword = null, int page = 1, int pageSize = 50)
        => new()
        {
            SupplierId = supplierId,
            Currency = currency,
            OrderDateFrom = orderFrom,
            OrderDateTo = orderTo,
            InvoiceDateFrom = invoiceFrom,
            InvoiceDateTo = invoiceTo,
            LinkageStatus = linkageStatus,
            EvidenceStatus = evidenceStatus,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        Assert.NotNull(payload.Data);
        return payload.Data!;
    }

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id,
            SupplierCode = $"S{id}",
            SupplierName = name,
            Status = 1
        });

    private static PurchaseOrder SeedOrder(ErpDbContext db, long id, string orderNo, long supplierId,
        Currency currency, decimal totalAmount, DocumentStatus status = DocumentStatus.Approved,
        bool deleted = false, long? owningSalesOrderId = null, decimal quantity = 0m,
        DateTime? orderDate = null)
    {
        var order = new PurchaseOrder
        {
            Id = id,
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = owningSalesOrderId.HasValue ? $"SO#{owningSalesOrderId.Value}" : string.Empty,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };

        if (quantity > 0)
        {
            order.Details.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = id,
                ProductId = 1,
                ProductName = "商品1",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = quantity,
                UnitPrice = totalAmount / quantity,
                Amount = totalAmount
            });
        }

        db.PurchaseOrders.Add(order);
        return order;
    }

    private static PurchaseInvoice SeedInvoice(ErpDbContext db, long id, long supplierId, string currency,
        string number, decimal net, decimal tax, decimal gross, int status, DateTime invoiceDate,
        string invoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary, string code = "",
        string voidReason = "")
    {
        var invoice = new PurchaseInvoice
        {
            Id = id,
            InvoiceType = invoiceType,
            InvoiceCode = code,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(code),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = invoiceDate.Date,
            SupplierId = supplierId,
            SupplierCode = $"S{supplierId}",
            SupplierName = db.BaseSuppliers.Local.FirstOrDefault(s => s.Id == supplierId)?.SupplierName
                ?? $"供应商{supplierId}",
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : invoiceDate.Date.AddDays(1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? invoiceDate.Date.AddDays(2) : null,
            VoidReason = voidReason
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    /// <summary>写入一条 ERP-043 持久化关联行（订单快照取自订单当前值，币种与发票一致）</summary>
    private static PurchaseInvoiceAllocation SeedAllocation(ErpDbContext db, PurchaseInvoice invoice,
        long purchaseOrderId, decimal allocatedAmount, int sortOrder = 0)
    {
        var order = db.PurchaseOrders.Local.FirstOrDefault(o => o.Id == purchaseOrderId);
        var allocation = new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoice.Id,
            PurchaseOrderId = purchaseOrderId,
            OrderNo = order?.OrderNo ?? $"PO-{purchaseOrderId}",
            OrderDate = order?.OrderDate ?? new DateTime(2026, 9, 1),
            OrderCurrency = order?.Currency.ToString() ?? invoice.Currency,
            SupplierId = invoice.SupplierId,
            SupplierCode = invoice.SupplierCode,
            SupplierName = invoice.SupplierName,
            AllocatedAmount = allocatedAmount,
            Currency = invoice.Currency,
            SortOrder = sortOrder
        };
        db.PurchaseInvoiceAllocations.Add(allocation);
        return allocation;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo)
    {
        var salesOrder = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-30),
            CustomerId = 1,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(salesOrder);
        return salesOrder;
    }

    private static void SeedStockIn(ErpDbContext db, string stockInNo, long purchaseOrderId,
        DocumentStatus status, decimal quantity) => db.StockIns.Add(new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = AsOf.AddDays(-5),
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = 1,
            Status = status,
            TotalQuantity = quantity,
            Details = new List<StockInDetail>
            {
                new()
                {
                    ProductId = 1,
                    ProductName = "商品1",
                    Unit = "PCS",
                    Quantity = quantity
                }
            }
        });

    private static FinancePaymentApply SeedPaymentApply(ErpDbContext db, string applyNo, long salesOrderId)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = 1,
            Amount = 0m,
            Status = DocumentStatus.Approved
        };
        db.FinancePaymentApplies.Add(apply);
        return apply;
    }

    private static void SeedPayment(ErpDbContext db, string paymentNo, long paymentApplyId,
        long supplierId, decimal amount, Currency currency, DocumentStatus status)
        => db.FinancePayments.Add(new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = AsOf.AddDays(-2),
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            Status = status
        });

    /// <summary>
    /// 写入一条 ERP-066 持久化「付款单 → 采购发票」引用行（连同其付款单）：
    /// 默认快照与发票一致（有效证据）；<paramref name="status"/> / <paramref name="rowCurrency"/> 可构造
    /// 已作废 / 无效（币种不一致）的历史证据，用于验证绝不换算、绝不并入有效合计。
    /// </summary>
    private static void SeedInvoicePaymentEvidence(ErpDbContext db, long id, PurchaseInvoice invoice,
        decimal allocatedAmount, decimal paymentAmount = 1000m,
        int status = SupplierPaymentInvoiceAllocationRules.StatusActive, string? rowCurrency = null)
    {
        var currency = Enum.Parse<Currency>(invoice.Currency);
        var payment = new FinancePayment
        {
            Id = id + 1_000_000L,
            PaymentNo = $"FK-066-{id}",
            PaymentDate = AsOf.AddDays(-3),
            SupplierId = invoice.SupplierId,
            Amount = paymentAmount,
            Currency = currency,
            Status = DocumentStatus.Approved
        };
        db.FinancePayments.Add(payment);

        db.SupplierPaymentInvoiceAllocations.Add(new SupplierPaymentInvoiceAllocation
        {
            Id = id,
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = paymentAmount,
            PurchaseInvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceIdentityText = SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice),
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status),
            InvoiceGrossAmount = invoice.GrossAmount,
            SupplierId = invoice.SupplierId,
            SupplierCode = invoice.SupplierCode,
            SupplierName = invoice.SupplierName,
            AllocatedAmount = allocatedAmount,
            Currency = rowCurrency ?? invoice.Currency,
            Status = status,
            AllocatedAt = AsOf.AddDays(-2),
            RecordedBy = "tester",
            VoidedAt = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? AsOf.AddDays(-1) : null,
            VoidReason = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? "作废重登" : string.Empty
        });
    }
}
