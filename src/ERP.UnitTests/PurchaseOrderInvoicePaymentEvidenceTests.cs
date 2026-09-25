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
/// ERP-067 采购订单「已分配付款引用证据」（只读派生）单元测试：无证据（证据缺口，不呈现已付款 / 未付款 / 逾期）、
/// 发票仅关联本订单时的订单归属金额、发票被多张订单共同关联时只作发票级金额（不按比例摊派）、
/// 多付款单与多发票合计、已作废引用行与发票失效（草稿 / 已作废）单独分桶且不抬高有效合计、
/// 无效历史证据（供应商 / 币种 / 快照不一致原样保留）、无法确认证据（付款单或发票已删除）、
/// 订单已取消 / 已删除的未知处理、多供应商与多币种不合并、批量保序与非法 Id 拒绝、命中上限按未知、
/// 只读不写库、发票级聚合复用入口（含付款单未指向发票金额）、ERP-044 对账报表第四类证据分列不轧差，
/// 以及接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed、不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderInvoicePaymentEvidenceTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);
    private const long SupplierA = 960001L;
    private const long SupplierB = 960002L;

    // ==================== 1. 无证据：可读、「无已分配付款引用证据」，绝不呈现为未付款 / 已付款 / 逾期 ====================

    [Fact]
    public async Task Detail_reports_evidence_gap_and_never_paid_or_settled()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960101L, "PO-IP-1", SupplierA, totalAmount: 1000m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960101L);
        var summary = detail.Summary;

        Assert.True(summary.OrderAvailable);
        Assert.Equal("PO-IP-1", summary.OrderNo);
        Assert.Equal(1000m, summary.OrderedAmount);
        Assert.Equal("CNY", summary.OrderCurrency);
        Assert.Equal(2, summary.AmountDecimals);
        Assert.False(summary.HasEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.False(summary.Truncated);
        Assert.Equal(0m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(0m, summary.AttributableAllocatedPaymentAmount);
        Assert.Equal(0, summary.ActiveAllocationCount);
        Assert.Equal(0, summary.ActivePaymentCount);
        Assert.Equal(0, summary.LinkedInvoiceCount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelNone, summary.EvidenceLabel);
        Assert.Empty(detail.Lines);
        Assert.Equal(0, detail.LineCount);
        Assert.Contains("不代表未付款", summary.EvidenceNote);
        Assert.Contains("不构成应付余额", summary.EvidenceNote);
        Assert.Contains("不是总账", detail.Boundary);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.RuleText, detail.Rule);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.RuleText, detail.AllocationRule);
        Assert.Equal(PurchaseInvoiceRules.LinkageRuleText, detail.InvoiceLinkageRule);
    }

    // ==================== 2. 发票仅关联本订单：有效引用行可安全归属本订单 ====================

    [Fact]
    public async Task Detail_attributes_active_evidence_when_invoice_only_linked_to_this_order()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960111L, "PO-IP-2", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 960201L, "FK-IP-1", SupplierA, amount: 600m);
        var invoice = SeedInvoice(db, 960301L, SupplierA, "CNY", "INV-IP-1", 800m);
        SeedInvoiceLink(db, 960401L, invoice, 960111L, 800m);
        SeedPaymentInvoiceAllocation(db, 960501L, payment, invoice, 500m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960111L);
        var summary = detail.Summary;

        Assert.Equal(500m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(500m, summary.AttributableAllocatedPaymentAmount);
        Assert.Equal(0m, summary.UnattributableAllocatedPaymentAmount);
        Assert.Equal(0, summary.UnattributableInvoiceCount);
        Assert.Equal(1, summary.AttributableInvoiceCount);
        Assert.Equal(1, summary.ActiveAllocationCount);
        Assert.Equal(1, summary.ActivePaymentCount);
        Assert.Equal(1, summary.LinkedInvoiceCount);
        Assert.True(summary.HasEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelRecorded, summary.EvidenceLabel);

        var line = Assert.Single(detail.Lines);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded, line.Bucket);
        Assert.True(line.IsRecordedEvidence);
        Assert.False(line.IsVoided);
        Assert.True(line.OrderAttributable);
        Assert.True(line.PaymentAvailable);
        Assert.True(line.InvoiceAvailable);
        Assert.Equal(500m, line.AllocatedAmount);
        Assert.Equal("有效", line.StatusText);
        Assert.Equal("已登记", line.InvoiceStatusText);
        Assert.Equal(100m, line.PaymentUnallocatedAmount);        // 付款单 600 中未指向任何发票 100
        Assert.Contains("可归属本订单", line.AttributionText);
        Assert.Contains("不代表已付款", line.Reason);
        Assert.Equal(CurrencyAmountRules.PrecisionOf("CNY"), line.AmountDecimals);
    }

    // ==================== 3. 多付款单 / 多发票合计：按持久化有效行汇总 ====================

    [Fact]
    public async Task Detail_sums_multiple_payments_and_invoices()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960121L, "PO-IP-3", SupplierA, totalAmount: 2000m);
        var first = SeedPayment(db, 960211L, "FK-IP-2", SupplierA, amount: 700m);
        var second = SeedPayment(db, 960212L, "FK-IP-3", SupplierA, amount: 400m);
        var firstInvoice = SeedInvoice(db, 960311L, SupplierA, "CNY", "INV-IP-2", 900m);
        var secondInvoice = SeedInvoice(db, 960312L, SupplierA, "CNY", "INV-IP-3", 600m);
        SeedInvoiceLink(db, 960411L, firstInvoice, 960121L, 900m);
        SeedInvoiceLink(db, 960412L, secondInvoice, 960121L, 600m);
        SeedPaymentInvoiceAllocation(db, 960511L, first, firstInvoice, 300m);
        SeedPaymentInvoiceAllocation(db, 960512L, first, secondInvoice, 350m);
        SeedPaymentInvoiceAllocation(db, 960513L, second, secondInvoice, 200m);
        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960121L)).Summary;

        Assert.Equal(850m, summary.ActiveAllocatedPaymentAmount);          // 300 + 350 + 200
        Assert.Equal(850m, summary.AttributableAllocatedPaymentAmount);    // 两张发票都只关联本订单
        Assert.Equal(3, summary.ActiveAllocationCount);
        Assert.Equal(2, summary.ActivePaymentCount);
        Assert.Equal(2, summary.AttributableInvoiceCount);
        Assert.Equal(2, summary.LinkedInvoiceCount);
    }

    // ==================== 4. 发票还被其他订单关联：只作发票级金额，绝不按比例摊派 ====================

    [Fact]
    public async Task Detail_keeps_invoice_level_amount_separate_when_invoice_is_shared()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960131L, "PO-IP-4", SupplierA, totalAmount: 1000m);
        SeedOrder(db, 960132L, "PO-IP-5", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 960221L, "FK-IP-4", SupplierA, amount: 900m);
        var invoice = SeedInvoice(db, 960321L, SupplierA, "CNY", "INV-IP-4", 1200m);
        SeedInvoiceLink(db, 960421L, invoice, 960131L, 600m);
        SeedInvoiceLink(db, 960422L, invoice, 960132L, 600m);
        SeedPaymentInvoiceAllocation(db, 960521L, payment, invoice, 700m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960131L);
        var summary = detail.Summary;

        Assert.Equal(700m, summary.ActiveAllocatedPaymentAmount);          // 发票级金额照实列出
        Assert.Equal(0m, summary.AttributableAllocatedPaymentAmount);      // 但绝不摊派到本订单
        Assert.Equal(700m, summary.UnattributableAllocatedPaymentAmount);
        Assert.Equal(1, summary.UnattributableInvoiceCount);
        Assert.Equal(0, summary.AttributableInvoiceCount);

        var line = Assert.Single(detail.Lines);
        Assert.True(line.IsRecordedEvidence);
        Assert.False(line.OrderAttributable);
        Assert.Contains("只作发票级金额", line.AttributionText);
        Assert.Contains("还被其他采购订单关联", line.Reason);
        Assert.Contains("只作**发票级金额**单列", summary.EvidenceNote);
    }

    // ==================== 5. 已作废引用行：单独分桶、不并入有效合计、历史可查 ====================

    [Fact]
    public async Task Detail_excludes_voided_allocation_but_keeps_it_inspectable()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960141L, "PO-IP-6", SupplierA, totalAmount: 1000m);
        var active = SeedPayment(db, 960231L, "FK-IP-5", SupplierA, amount: 1000m);
        var voided = SeedPayment(db, 960232L, "FK-IP-6", SupplierA, amount: 1000m);
        var invoice = SeedInvoice(db, 960331L, SupplierA, "CNY", "INV-IP-5", 900m);
        SeedInvoiceLink(db, 960431L, invoice, 960141L, 900m);
        SeedPaymentInvoiceAllocation(db, 960531L, active, invoice, 300m);
        SeedPaymentInvoiceAllocation(db, 960532L, voided, invoice, 200m,
            status: SupplierPaymentInvoiceAllocationRules.StatusVoided, voidReason: "引用录错，重新登记");
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960141L);
        var summary = detail.Summary;

        Assert.Equal(300m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(300m, summary.AttributableAllocatedPaymentAmount);
        Assert.Equal(1, summary.ActiveAllocationCount);
        Assert.Equal(200m, summary.VoidedAmount);
        Assert.Equal(1, summary.VoidedCount);
        Assert.True(summary.HasHistoricalEvidence);

        var voidedLine = detail.Lines.Single(l => l.AllocationId == 960532L);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided, voidedLine.Bucket);
        Assert.True(voidedLine.IsVoided);
        Assert.False(voidedLine.IsRecordedEvidence);
        Assert.False(voidedLine.OrderAttributable);
        Assert.Equal("引用录错，重新登记", voidedLine.VoidReason);
        Assert.Equal("已作废", voidedLine.StatusText);
        Assert.Contains("不计入有效合计", voidedLine.Reason);
        Assert.Contains("已作废引用行", summary.EvidenceNote);
    }

    // ==================== 6. 发票已失效（草稿 / 已作废）：引用行仍有效也绝不计入有效合计 ====================

    [Fact]
    public async Task Detail_excludes_allocations_whose_invoice_is_draft_or_voided()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960151L, "PO-IP-7", SupplierA, totalAmount: 2000m);
        var payment = SeedPayment(db, 960241L, "FK-IP-7", SupplierA, amount: 1000m);
        var draft = SeedInvoice(db, 960341L, SupplierA, "CNY", "INV-IP-6", 800m,
            status: PurchaseInvoiceRules.StatusDraft);
        var voided = SeedInvoice(db, 960342L, SupplierA, "CNY", "INV-IP-7", 800m,
            status: PurchaseInvoiceRules.StatusVoided);
        SeedInvoiceLink(db, 960441L, draft, 960151L, 800m);
        SeedInvoiceLink(db, 960442L, voided, 960151L, 800m);
        SeedPaymentInvoiceAllocation(db, 960541L, payment, draft, 300m);
        SeedPaymentInvoiceAllocation(db, 960542L, payment, voided, 400m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960151L);
        var summary = detail.Summary;

        Assert.Equal(0m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(0, summary.ActiveAllocationCount);
        Assert.Equal(700m, summary.InvoiceInactiveAmount);
        Assert.Equal(2, summary.InvoiceInactiveCount);
        Assert.True(summary.HasHistoricalEvidence);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelHistoricalOnly, summary.EvidenceLabel);

        var draftLine = detail.Lines.Single(l => l.AllocationId == 960541L);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive, draftLine.Bucket);
        Assert.Equal("草稿", draftLine.InvoiceStatusText);
        Assert.False(draftLine.InvoiceAvailable);
        Assert.Contains("仍为草稿", draftLine.Reason);

        var voidedLine = detail.Lines.Single(l => l.AllocationId == 960542L);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive, voidedLine.Bucket);
        Assert.Equal("已作废", voidedLine.InvoiceStatusText);
        Assert.Contains("发票已作废", voidedLine.Reason);
        Assert.Contains("发票已失效", summary.EvidenceNote);
    }

    // ==================== 7. 无效历史证据：供应商 / 币种 / 快照不一致 → 原样保留、绝不修复 ====================

    [Fact]
    public async Task Detail_marks_supplier_currency_and_snapshot_mismatch_as_invalid()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, 960161L, "PO-IP-8", SupplierA, totalAmount: 3000m);
        var payment = SeedPayment(db, 960251L, "FK-IP-8", SupplierA, amount: 2000m);
        var invoice = SeedInvoice(db, 960351L, SupplierA, "CNY", "INV-IP-8", 2000m);
        SeedInvoiceLink(db, 960451L, invoice, 960161L, 2000m);
        // ① 供应商快照与付款单 / 发票不一致
        SeedPaymentInvoiceAllocation(db, 960551L, payment, invoice, 100m, snapshotSupplierId: SupplierB);
        // ② 引用行币种与付款单 / 发票币种不一致（且不换算）
        SeedPaymentInvoiceAllocation(db, 960552L, payment, invoice, 200m, snapshotCurrency: "USD");
        // ③ 引用金额大于引用行付款单金额快照（快照自相矛盾）
        SeedPaymentInvoiceAllocation(db, 960553L, payment, invoice, 300m, paymentAmountSnapshot: 10m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960161L);
        var summary = detail.Summary;

        Assert.Equal(0m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(0, summary.ActiveAllocationCount);
        Assert.Equal(600m, summary.InvalidAmount);              // 金额原样保留，绝不修复 / 换算 / 改派
        Assert.Equal(3, summary.InvalidCount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelHistoricalOnly, summary.EvidenceLabel);
        Assert.All(detail.Lines, l =>
        {
            Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid, l.Bucket);
            Assert.False(l.OrderAttributable);
            Assert.Contains("无效", l.Reason);
        });
        Assert.Contains("不一致", detail.Lines.Single(l => l.AllocationId == 960551L).Reason);
        Assert.Contains("不换算", detail.Lines.Single(l => l.AllocationId == 960552L).Reason);
        Assert.Contains("快照自相矛盾", detail.Lines.Single(l => l.AllocationId == 960553L).Reason);
        Assert.Contains("无效历史证据", summary.EvidenceNote);
    }

    // ==================== 8. 无法确认的证据：付款单 / 发票已删除 ====================

    [Fact]
    public async Task Detail_marks_deleted_payment_or_invoice_as_unavailable()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960171L, "PO-IP-9", SupplierA, totalAmount: 2000m);
        var deletedPayment = SeedPayment(db, 960261L, "FK-IP-9", SupplierA, amount: 500m, deleted: true);
        var payment = SeedPayment(db, 960262L, "FK-IP-10", SupplierA, amount: 500m);
        var invoice = SeedInvoice(db, 960361L, SupplierA, "CNY", "INV-IP-9", 800m);
        var deletedInvoice = SeedInvoice(db, 960362L, SupplierA, "CNY", "INV-IP-10", 800m, deleted: true);
        SeedInvoiceLink(db, 960461L, invoice, 960171L, 800m);
        SeedInvoiceLink(db, 960462L, deletedInvoice, 960171L, 800m);
        SeedPaymentInvoiceAllocation(db, 960561L, deletedPayment, invoice, 150m);
        SeedPaymentInvoiceAllocation(db, 960562L, payment, deletedInvoice, 250m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960171L);
        var summary = detail.Summary;

        Assert.Equal(0m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(400m, summary.UnavailableAmount);
        Assert.Equal(2, summary.UnavailableCount);

        var paymentGone = detail.Lines.Single(l => l.AllocationId == 960561L);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable, paymentGone.Bucket);
        Assert.False(paymentGone.PaymentAvailable);
        Assert.Null(paymentGone.PaymentStatus);
        Assert.Contains("付款单不存在或已删除", paymentGone.Reason);

        var invoiceGone = detail.Lines.Single(l => l.AllocationId == 960562L);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable, invoiceGone.Bucket);
        Assert.False(invoiceGone.InvoiceAvailable);
        Assert.Contains("采购发票不存在或已删除", invoiceGone.Reason);
        Assert.Contains("无法确认的证据", summary.EvidenceNote);
    }

    // ==================== 9. 订单已取消 / 已删除：订单金额未知，绝不按 0，也绝不推断付款状态 ====================

    [Fact]
    public async Task Detail_reports_cancelled_and_deleted_orders_without_inventing_zero()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960181L, "PO-IP-10", SupplierA, totalAmount: 1000m,
            status: DocumentStatus.Cancelled);
        SeedOrder(db, 960182L, "PO-IP-11", SupplierA, totalAmount: 1000m, deleted: true);
        var payment = SeedPayment(db, 960271L, "FK-IP-11", SupplierA, amount: 900m);
        var invoice = SeedInvoice(db, 960371L, SupplierA, "CNY", "INV-IP-11", 800m);
        SeedInvoiceLink(db, 960471L, invoice, 960181L, 800m);
        SeedInvoiceLink(db, 960472L, invoice, 960182L, 800m);
        SeedPaymentInvoiceAllocation(db, 960571L, payment, invoice, 400m);
        await db.SaveChangesAsync();

        var cancelled = (await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960181L)).Summary;
        Assert.True(cancelled.OrderAvailable);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateCancelled, cancelled.OrderState);
        Assert.Equal(1000m, cancelled.OrderedAmount);
        Assert.Contains("已取消", cancelled.OrderStateText);
        Assert.Equal(400m, cancelled.ActiveAllocatedPaymentAmount);           // 证据照实列出
        Assert.Equal(0m, cancelled.AttributableAllocatedPaymentAmount);       // 发票被两张订单关联 → 不摊派

        var deleted = (await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960182L)).Summary;
        Assert.False(deleted.OrderAvailable);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateUnavailable, deleted.OrderState);
        Assert.Null(deleted.OrderedAmount);                                   // 未知，绝不按 0
        Assert.Equal(400m, deleted.ActiveAllocatedPaymentAmount);
        Assert.Contains("未知", deleted.OrderStateText);
    }

    // ==================== 10. 多供应商 / 多币种：分组与折算边界 ====================

    [Fact]
    public async Task Detail_never_merges_other_suppliers_or_currencies()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, 960191L, "PO-IP-12", SupplierA, currency: Currency.USD, totalAmount: 1000m);
        var usdPayment = SeedPayment(db, 960281L, "FK-IP-12", SupplierA, amount: 500m,
            currency: Currency.USD);
        var usdInvoice = SeedInvoice(db, 960381L, SupplierA, "USD", "INV-IP-12", 500m);
        SeedInvoiceLink(db, 960481L, usdInvoice, 960191L, 500m);
        SeedPaymentInvoiceAllocation(db, 960581L, usdPayment, usdInvoice, 300m);
        // 其他供应商的引用行不会出现在本订单的关联发票上（只能通过本订单的关联发票被看到）
        var otherPayment = SeedPayment(db, 960282L, "FK-IP-13", SupplierB, amount: 500m,
            currency: Currency.USD);
        var otherInvoice = SeedInvoice(db, 960382L, SupplierB, "USD", "INV-IP-13", 500m);
        SeedPaymentInvoiceAllocation(db, 960582L, otherPayment, otherInvoice, 400m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960191L);
        var summary = detail.Summary;

        Assert.Equal("USD", summary.OrderCurrency);
        Assert.Equal(2, summary.AmountDecimals);                              // JPY / KRW / VND / IDR 为 0 位小数；USD 为 2 位
        Assert.Equal(300m, summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(1, summary.ActiveAllocationCount);
        Assert.All(detail.Lines, l =>
        {
            Assert.Equal("USD", l.Currency);
            Assert.Equal(SupplierA, l.SupplierId);                            // 绝不混入其他供应商
        });
    }

    // ==================== 11. 批量：保序、去重、非法 / 超限 Id 一律拒绝（不静默丢行） ====================

    [Fact]
    public async Task Batch_preserves_order_dedupes_and_rejects_invalid_or_oversized_ids()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960201L, "PO-IP-13", SupplierA, totalAmount: 1000m);
        SeedOrder(db, 960202L, "PO-IP-14", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 960291L, "FK-IP-14", SupplierA, amount: 900m);
        var invoice = SeedInvoice(db, 960391L, SupplierA, "CNY", "INV-IP-14", 900m);
        SeedInvoiceLink(db, 960491L, invoice, 960201L, 900m);
        SeedPaymentInvoiceAllocation(db, 960591L, payment, invoice, 250m);
        await db.SaveChangesAsync();

        var batch = await PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(db,
            new PurchaseOrderInvoicePaymentEvidenceQuery { Ids = "960202, 960201,960201" });
        Assert.Equal(2, batch.ItemCount);
        Assert.Equal(new[] { 960202L, 960201L }, batch.Items.Select(i => i.PurchaseOrderId).ToArray());
        Assert.Equal(0m, batch.Items[0].ActiveAllocatedPaymentAmount);
        Assert.Equal(250m, batch.Items[1].ActiveAllocatedPaymentAmount);
        Assert.False(batch.Truncated);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.RuleText, batch.Rule);

        var invalid = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(db,
                new PurchaseOrderInvoicePaymentEvidenceQuery { Ids = "960201,abc" }));
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var oversized = string.Join(',', Enumerable.Range(1, 201));
        var tooMany = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(db,
                new PurchaseOrderInvoicePaymentEvidenceQuery { Ids = oversized }));
        Assert.Equal(ErrorCodes.InvalidParameter, tooMany.Code);

        var empty = await PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(db,
            new PurchaseOrderInvoicePaymentEvidenceQuery());
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.ItemCount);
    }

    // ==================== 12. 订单不存在：返回「未知」汇总，绝不静默丢行 ====================

    [Fact]
    public async Task Batch_returns_unknown_summary_for_missing_order_instead_of_dropping_row()
    {
        using var db = TestDbFactory.Create();

        var batch = await PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(db,
            new PurchaseOrderInvoicePaymentEvidenceQuery { Ids = "960999" });

        var item = Assert.Single(batch.Items);
        Assert.Equal(960999L, item.PurchaseOrderId);
        Assert.False(item.OrderAvailable);
        Assert.Null(item.OrderedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateUnavailable, item.OrderState);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelNone, item.EvidenceLabel);
        Assert.Contains("未知", item.OrderStateText);
        Assert.Contains(PurchaseOrderInvoicePaymentEvidenceSemantics.UnknownOrderNote, item.EvidenceNote);
        Assert.Contains("不按 0 处理", item.EvidenceNote);
    }

    // ==================== 13. 有界读取：命中行数上限按「未知」，绝不给部分合计 ====================

    [Fact]
    public async Task Detail_marks_result_unknown_when_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960221L, "PO-IP-16", SupplierA, totalAmount: 100000m);
        var payment = SeedPayment(db, 960311L, "FK-IP-16", SupplierA, amount: 100000m);
        var invoice = SeedInvoice(db, 960411L, SupplierA, "CNY", "INV-IP-16", 100000m);
        SeedInvoiceLink(db, 960511L, invoice, 960221L, 100000m);
        for (var i = 0; i < PurchaseOrderInvoicePaymentEvidenceSemantics.MaxOrderEvidenceRows + 1; i++)
        {
            SeedPaymentInvoiceAllocation(db, 970000L + i, payment, invoice, 1m);
        }
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960221L);
        var summary = detail.Summary;

        Assert.True(summary.Truncated);
        Assert.Null(summary.ActiveAllocatedPaymentAmount);
        Assert.Null(summary.AttributableAllocatedPaymentAmount);
        Assert.Null(summary.ActiveAllocationCount);
        Assert.Null(summary.VoidedCount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelUnknown, summary.EvidenceLabel);
        Assert.Contains("有界上限", summary.EvidenceNote);
        // 明细只展示已读取部分，且绝不因命中上限而被当成 0 或部分合计
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.MaxOrderEvidenceRows, detail.LineCount);
    }

    // ==================== 14. 只读：不写库、不改写任何来源记录 ====================

    [Fact]
    public async Task Detail_is_read_only_and_does_not_mutate_source_records()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960231L, "PO-IP-17", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 960321L, "FK-IP-17", SupplierA, amount: 900m);
        var invoice = SeedInvoice(db, 960421L, SupplierA, "CNY", "INV-IP-17", 800m);
        SeedInvoiceLink(db, 960521L, invoice, 960231L, 800m);
        SeedPaymentInvoiceAllocation(db, 960621L, payment, invoice, 500m);
        await db.SaveChangesAsync();

        await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(db, 960231L);
        await PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(db, new PurchaseOrderInvoicePaymentEvidenceQuery
        {
            Ids = "960231",
        });
        await PurchaseOrderInvoicePaymentEvidence.AggregatesForInvoicesAsync(db, new[] { invoice.Id });

        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
        Assert.Equal(500m, db.SupplierPaymentInvoiceAllocations.Single().AllocatedAmount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusActive,
            db.SupplierPaymentInvoiceAllocations.Single().Status);
        Assert.Equal(800m, db.PurchaseInvoices.Single().GrossAmount);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, db.PurchaseInvoices.Single().Status);
        Assert.Equal(1000m, db.PurchaseOrders.Single().TotalAmount);
        Assert.Equal(900m, db.FinancePayments.Single().Amount);
    }

    // ==================== 15. 发票级聚合复用入口：有效 / 未指向发票金额 + 发票数上限 ====================

    [Fact]
    public async Task Invoice_aggregate_reports_active_and_unallocated_payment_side_amounts()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var first = SeedPayment(db, 960331L, "FK-IP-18", SupplierA, amount: 900m);
        var second = SeedPayment(db, 960332L, "FK-IP-19", SupplierA, amount: 400m);
        var invoice = SeedInvoice(db, 960431L, SupplierA, "CNY", "INV-IP-18", 1200m);
        SeedPaymentInvoiceAllocation(db, 960631L, first, invoice, 600m);
        SeedPaymentInvoiceAllocation(db, 960632L, second, invoice, 300m);
        SeedPaymentInvoiceAllocation(db, 960633L, second, invoice, 100m,
            status: SupplierPaymentInvoiceAllocationRules.StatusVoided, voidReason: "作废重登");
        await db.SaveChangesAsync();

        var set = await PurchaseOrderInvoicePaymentEvidence.AggregatesForInvoicesAsync(db, new[] { invoice.Id });
        var aggregate = set.Get(invoice.Id);

        Assert.NotNull(aggregate);
        Assert.Equal(900m, aggregate!.ActiveAmount);              // 600 + 300（已作废 100 绝不并入）
        Assert.Equal(2, aggregate.ActiveCount);
        Assert.Equal(2, aggregate.ActivePaymentCount);
        Assert.Equal(1300m, aggregate.RecordedPaymentAmount);     // 900 + 400（按付款单去重）
        Assert.Equal(400m, aggregate.UnallocatedPaymentAmount);   // 900−600 + 400−300：两张付款单未指向发票的合计
        Assert.Equal(100m, aggregate.VoidedAmount);
        Assert.Equal(1, aggregate.VoidedCount);
        Assert.True(aggregate.HasAnyRow);
        Assert.True(aggregate.HasHistoricalRow);

        var capped = await PurchaseOrderInvoicePaymentEvidence.AggregatesForInvoicesAsync(
            db, new[] { invoice.Id, 960432L }, invoiceCap: 1);
        Assert.True(capped.Truncated);
        Assert.Null(capped.Get(invoice.Id));                      // 命中发票数上限 → 未知，绝不给部分合计
    }

    // ==================== 16. ERP-044 对账报表：第四类独立证据分列，绝不轧差 ====================

    [Fact]
    public async Task Reconciliation_report_exposes_allocated_payment_evidence_without_netting()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, 960241L, "PO-IP-18", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 960341L, "FK-IP-20", SupplierA, amount: 900m);
        var invoice = SeedInvoice(db, 960441L, SupplierA, "CNY", "INV-IP-19", 800m);
        SeedInvoiceLink(db, 960531L, invoice, 960241L, 800m);
        SeedPaymentInvoiceAllocation(db, 960641L, payment, invoice, 500m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(
            db, new SupplierInvoiceReconciliationQuery());

        var group = Assert.Single(report.Groups);
        var invoiceRow = Assert.Single(group.Invoices);
        Assert.Equal(500m, invoiceRow.AllocatedPaymentAmount);
        Assert.Equal(1, invoiceRow.AllocatedPaymentCount);
        Assert.Equal(1, invoiceRow.AllocatedPaymentDocumentCount);
        Assert.Equal(400m, invoiceRow.AllocatedPaymentUnallocatedAmount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.LabelRecorded,
            invoiceRow.AllocatedPaymentEvidenceLabel);

        Assert.Equal(500m, group.AllocatedPaymentAmount);
        Assert.Equal(1, group.AllocatedPaymentInvoiceCount);
        Assert.Equal(0, group.NoAllocatedPaymentInvoiceCount);
        Assert.Equal(0, group.UnknownAllocatedPaymentInvoiceCount);
        Assert.Equal(1, report.AllocatedPaymentInvoiceCount);
        Assert.Equal(0, report.NoAllocatedPaymentInvoiceCount);
        Assert.Equal(0, report.UnknownAllocatedPaymentInvoiceCount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.RuleText, report.AllocatedPaymentRule);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BoundaryText, report.AllocatedPaymentBoundary);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.FourClassEvidenceText, report.FourEvidenceClasses);

        // 四类证据分列：订单金额（已订）/ 已开票（发票证据）/ 已分配付款引用证据各自独立，绝不轧差
        Assert.Equal(1000m, group.OrderedAmount);
        Assert.Equal(800m, group.InvoicedAmount);
        Assert.Equal(800m, group.LinkedAmount);
        Assert.Equal(500m, group.AllocatedPaymentAmount);
        Assert.Equal(1000m, order.TotalAmount);
        Assert.Contains("绝不轧差", report.FourEvidenceClasses);
        Assert.Contains("不是总账", report.AllocatedPaymentBoundary);
    }

    // ==================== 17. 接口端点：详情 + 列表批量汇总 ====================

    [Fact]
    public async Task Controller_endpoints_return_invoice_payment_evidence_payloads()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 960251L, "PO-IP-19", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 960351L, "FK-IP-21", SupplierA, amount: 1000m);
        var invoice = SeedInvoice(db, 960451L, SupplierA, "CNY", "INV-IP-20", 900m);
        SeedInvoiceLink(db, 960541L, invoice, 960251L, 900m);
        SeedPaymentInvoiceAllocation(db, 960651L, payment, invoice, 350m);
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));

        var detail = AssertOk<PurchaseOrderInvoicePaymentEvidenceDetail>(
            await controller.InvoicePaymentEvidence(960251L));
        Assert.Equal(350m, detail.Summary.ActiveAllocatedPaymentAmount);
        Assert.Equal(350m, detail.Summary.AttributableAllocatedPaymentAmount);
        Assert.Single(detail.Lines);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.ScopeText, detail.ScopeNote);

        var batch = AssertOk<PurchaseOrderInvoicePaymentEvidenceBatch>(
            await controller.InvoicePaymentEvidenceSummaries("960251"));
        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(350m, batch.Items[0].ActiveAllocatedPaymentAmount);
        Assert.Equal(PurchaseOrderInvoicePaymentEvidenceSemantics.RuleText, batch.Rule);

        var empty = AssertOk<PurchaseOrderInvoicePaymentEvidenceBatch>(
            await controller.InvoicePaymentEvidenceSummaries(null));
        Assert.Empty(empty.Items);

        var routes = typeof(PurchaseOrderController)
            .GetMethod(nameof(PurchaseOrderController.InvoicePaymentEvidence))!
            .GetCustomAttributes<Microsoft.AspNetCore.Mvc.HttpGetAttribute>()
            .Select(a => a.Template)
            .ToArray();
        Assert.Contains("{id:long}/invoice-payment-evidence", routes);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 18. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_wiring_exposes_column_and_detail_action_without_per_row_requests()
    {
        var js = JsDirectory();

        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/purchase-order-invoice-payment-evidence.js", index);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("render: row => purchaseOrderInvoicePaymentEvidenceCellHtml(row)", modulesDoc);
        Assert.Contains("showPurchaseOrderInvoicePaymentEvidence", modulesDoc);
        Assert.Contains("付款发票证据", modulesDoc);
        Assert.Contains("绝不与上一列的「付款引用证据」相加", modulesDoc);

        var script = File.ReadAllText(Path.Combine(js, "purchase-order-invoice-payment-evidence.js"));
        Assert.Contains("'/api/purchase-orders/invoice-payment-evidence-summaries'", script);
        Assert.Contains("const POIP_MAX_BATCH = 200", script);
        Assert.Contains("function purchaseOrderInvoicePaymentEvidenceCellHtml", script);
        Assert.Contains("function showPurchaseOrderInvoicePaymentEvidence", script);
        Assert.Contains("function poipBadgeHtml", script);
        Assert.Contains("i += POIP_MAX_BATCH", script);                     // 按页分批，绝不逐行请求
        Assert.Contains("'未知'", script);                                 // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("truncated", script);                              // 命中后端有界上限时显示未知
        Assert.Contains("无已分配付款引用证据", script);
        Assert.Contains("仅发票级", script);                                // 发票级金额与订单归属金额分开标注
        Assert.Contains("不是总账", script);                                // 页面显式声明证据视图边界
        Assert.Contains("CURRENT_MODULE_CODE !== 'purchase-order'", script);

        // ERP-044 报表页面必须把已分配付款引用证据作为第四类独立证据展示（不并入任何既有金额）
        var report = File.ReadAllText(Path.Combine(js, "supplier-invoice-reconciliation.js"));
        Assert.Contains("allocatedPaymentAmount", report);
        Assert.Contains("data.allocatedPaymentRule", report);
        Assert.Contains("data.allocatedPaymentBoundary", report);
        Assert.Contains("data.fourEvidenceClasses", report);
    }

    // ==================== 助手 ====================

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
        Currency currency = Currency.CNY, decimal totalAmount = 1000m,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            Id = id,
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-5),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        return order;
    }

    private static FinancePayment SeedPayment(ErpDbContext db, long id, string paymentNo, long supplierId,
        decimal amount = 1000m, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            Id = id,
            PaymentNo = paymentNo,
            PaymentDate = AsOf.AddDays(-3),
            SupplierId = supplierId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        return payment;
    }

    /// <summary>写入一张 ERP-065 发票证据（税额 0、净额 = 含税，便于聚焦付款引用证据口径）</summary>
    private static PurchaseInvoice SeedInvoice(ErpDbContext db, long id, long supplierId, string currency,
        string number, decimal gross, int status = PurchaseInvoiceRules.StatusRecorded,
        bool deleted = false, DateTime? dueDate = null, string paymentTerms = "")
    {
        var invoice = new PurchaseInvoice
        {
            Id = id,
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = AsOf.AddDays(-2),
            SupplierId = supplierId,
            SupplierCode = $"S{supplierId}",
            SupplierName = $"供应商{supplierId}",
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            DueDate = dueDate,
            PaymentTerms = paymentTerms,
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : AsOf.AddDays(-1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? AsOf : null,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    /// <summary>写入一条 ERP-043 / ERP-065 持久化「发票 → 采购订单」关联行（决定证据能否归属本订单）</summary>
    private static void SeedInvoiceLink(ErpDbContext db, long id, PurchaseInvoice invoice, long orderId,
        decimal allocatedAmount, int sortOrder = 0)
    {
        var order = db.PurchaseOrders.Local.FirstOrDefault(o => o.Id == orderId);
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            Id = id,
            PurchaseInvoiceId = invoice.Id,
            PurchaseOrderId = orderId,
            OrderNo = order?.OrderNo ?? $"PO-{orderId}",
            OrderDate = order?.OrderDate ?? AsOf.AddDays(-5),
            OrderCurrency = order?.Currency.ToString() ?? invoice.Currency,
            SupplierId = invoice.SupplierId,
            SupplierCode = invoice.SupplierCode,
            SupplierName = invoice.SupplierName,
            AllocatedAmount = allocatedAmount,
            Currency = invoice.Currency,
            SortOrder = sortOrder
        });
    }

    /// <summary>
    /// 写入一条 ERP-066 持久化「付款单 → 采购发票」引用行：默认快照与付款单 / 发票一致（有效证据）；
    /// 通过 <paramref name="snapshotSupplierId"/> 等参数可构造历史 / 无效证据，用于验证绝不换算 / 合并 / 改派。
    /// </summary>
    private static SupplierPaymentInvoiceAllocation SeedPaymentInvoiceAllocation(ErpDbContext db, long id,
        FinancePayment? payment, PurchaseInvoice? invoice, decimal allocatedAmount,
        int status = SupplierPaymentInvoiceAllocationRules.StatusActive,
        long? snapshotSupplierId = null, string? snapshotCurrency = null,
        decimal? paymentAmountSnapshot = null, string voidReason = "", bool deleted = false)
    {
        var supplierId = snapshotSupplierId ?? payment?.SupplierId ?? invoice?.SupplierId ?? SupplierA;
        var currency = snapshotCurrency ?? payment?.Currency.ToString() ?? invoice?.Currency ?? "CNY";

        var row = new SupplierPaymentInvoiceAllocation
        {
            Id = id,
            PaymentId = payment?.Id ?? 0,
            PaymentNo = payment?.PaymentNo ?? $"FK-{id}",
            PaymentDate = payment?.PaymentDate ?? AsOf.AddDays(-3),
            PaymentStatus = payment is null ? 0 : (int)payment.Status,
            PaymentStatusText = payment is null
                ? string.Empty
                : SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = paymentAmountSnapshot ?? payment?.Amount ?? 0m,
            PurchaseInvoiceId = invoice?.Id ?? 0,
            InvoiceType = invoice?.InvoiceType ?? PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = invoice?.InvoiceCode ?? string.Empty,
            InvoiceNumber = invoice?.InvoiceNumber ?? $"INV-{id}",
            InvoiceIdentityText = invoice is null
                ? string.Empty
                : SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice),
            InvoiceDate = invoice?.InvoiceDate ?? AsOf.AddDays(-2),
            InvoiceStatus = invoice?.Status ?? PurchaseInvoiceRules.StatusRecorded,
            InvoiceStatusText = invoice is null
                ? string.Empty
                : SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status),
            InvoiceGrossAmount = invoice?.GrossAmount ?? 0m,
            SupplierId = supplierId,
            SupplierCode = $"S{supplierId}",
            SupplierName = $"供应商{supplierId}",
            AllocatedAmount = allocatedAmount,
            Currency = currency,
            Status = status,
            AllocatedAt = AsOf.AddDays(-2),
            RecordedBy = "tester",
            VoidedAt = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? AsOf.AddDays(-1) : null,
            VoidReason = voidReason,
            IsDeleted = deleted
        };
        db.SupplierPaymentInvoiceAllocations.Add(row);
        return row;
    }
}
