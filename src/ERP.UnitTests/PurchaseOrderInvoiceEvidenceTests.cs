using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-048 采购订单发票证据（只读派生）单元测试：无证据 / 全额 / 部分覆盖、多张发票合计、
/// 草稿与已作废历史证据单独分桶且不抬高已开票金额、供应商与币种不一致的无效历史证据（不换算 / 不合并 / 不改派）、
/// 订单取消 / 删除时的未知处理（不按 0 / 不当作已付款）、批量有界读取（无逐行查库）、命中上限按未知、
/// 只读不写库、接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderInvoiceEvidenceTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);
    private const long SupplierA = 948001L;
    private const long SupplierB = 948002L;

    // ==================== 1. 无证据：可读、按「无发票证据」呈现，绝不呈现为已付款 / 已结清 / 逾期 ====================

    [Fact]
    public async Task Detail_reports_no_evidence_as_an_evidence_gap_and_never_as_paid_or_settled()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948101L, "PO-EV-1", SupplierA, Currency.CNY, 1000m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948101L);
        var summary = detail.Summary;

        Assert.True(summary.OrderAvailable);
        Assert.Equal("PO-EV-1", summary.OrderNo);
        Assert.Equal(1000m, summary.OrderedAmount);
        Assert.False(summary.HasInvoiceEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.False(summary.Truncated);
        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.Equal(0, summary.RecordedInvoiceCount);
        Assert.Equal(1000m, summary.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced, summary.CoverageStatus);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageText(
            SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced), summary.CoverageText);
        Assert.Equal("无发票证据", summary.CoverageLabel);
        Assert.Contains("不代表未付款", summary.EvidenceNote);
        Assert.Contains("不是应付余额", summary.EvidenceNote);
        Assert.Empty(detail.Lines);
        Assert.Equal(0, detail.LineCount);
        Assert.Equal("CNY", summary.OrderCurrency);
        Assert.Equal(2, summary.AmountDecimals);
    }

    // ==================== 2. 部分 / 全额覆盖：多张已登记发票按持久化关联行合计 ====================

    [Fact]
    public async Task Detail_sums_multiple_recorded_invoices_and_reports_partial_coverage()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948111L, "PO-EV-2", SupplierA, Currency.CNY, 1000m);
        var first = SeedInvoice(db, 948201L, SupplierA, "CNY", "0001", 300m, PurchaseInvoiceRules.StatusRecorded);
        var second = SeedInvoice(db, 948202L, SupplierA, "CNY", "0002", 200m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948301L, first, 948111L, 300m);
        SeedAllocation(db, 948302L, second, 948111L, 200m);
        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948111L)).Summary;

        Assert.Equal(500m, summary.RecordedAllocatedAmount);
        Assert.Equal(2, summary.RecordedInvoiceCount);
        Assert.Equal(500m, summary.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced, summary.CoverageStatus);
        Assert.Equal("部分开票", summary.CoverageLabel);
        Assert.Equal(2, summary.AllocationCount);
        Assert.True(summary.HasInvoiceEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.Contains("未开票金额", summary.EvidenceNote);
    }

    [Fact]
    public async Task Detail_reports_full_coverage_when_recorded_evidence_covers_the_order_total()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948112L, "PO-EV-3", SupplierA, Currency.CNY, 452m);
        var invoice = SeedInvoice(db, 948203L, SupplierA, "CNY", "0003", 452m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948303L, invoice, 948112L, 452m);
        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948112L)).Summary;

        Assert.Equal(452m, summary.RecordedAllocatedAmount);
        Assert.Equal(1, summary.RecordedInvoiceCount);
        Assert.Equal(0m, summary.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageFullyInvoiced, summary.CoverageStatus);
        Assert.Equal("已全额开票", summary.CoverageLabel);
        Assert.Equal(1, summary.AllocationCount);
    }

    // ==================== 3. 草稿与已作废证据：单独分桶、绝不并入已开票金额、详情仍可查 ====================

    [Fact]
    public async Task Detail_excludes_draft_and_voided_evidence_from_recorded_total_but_keeps_it_inspectable()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948121L, "PO-EV-4", SupplierA, Currency.CNY, 1000m);
        var recorded = SeedInvoice(db, 948211L, SupplierA, "CNY", "0011", 100m, PurchaseInvoiceRules.StatusRecorded);
        var draft = SeedInvoice(db, 948212L, SupplierA, "CNY", "0012", 200m, PurchaseInvoiceRules.StatusDraft);
        var voided = SeedInvoice(db, 948213L, SupplierA, "CNY", "0013", 300m, PurchaseInvoiceRules.StatusVoided,
            voidReason: "开票有误，重开");
        SeedAllocation(db, 948311L, recorded, 948121L, 100m);
        SeedAllocation(db, 948312L, draft, 948121L, 200m);
        SeedAllocation(db, 948313L, voided, 948121L, 300m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948121L);
        var summary = detail.Summary;

        // 已开票金额只含已登记（未作废）证据：草稿 200 与已作废 300 绝不并入
        Assert.Equal(100m, summary.RecordedAllocatedAmount);
        Assert.Equal(1, summary.RecordedInvoiceCount);
        Assert.Equal(900m, summary.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced, summary.CoverageStatus);

        Assert.Equal(200m, summary.DraftAllocatedAmount);
        Assert.Equal(1, summary.DraftInvoiceCount);
        Assert.Equal(300m, summary.VoidedAllocatedAmount);
        Assert.Equal(1, summary.VoidedInvoiceCount);
        Assert.Equal(3, summary.AllocationCount);
        Assert.True(summary.HasHistoricalEvidence);
        Assert.Contains("草稿证据", summary.EvidenceNote);
        Assert.Contains("已作废历史证据", summary.EvidenceNote);

        // 历史证据仍可逐条查看（显式分桶 + 不计入原因）
        Assert.Equal(3, detail.LineCount);
        var draftLine = detail.Lines.Single(l => l.PurchaseInvoiceId == draft.Id);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.BucketDraft, draftLine.Bucket);
        Assert.True(draftLine.IsDraft);
        Assert.False(draftLine.IsRecordedEvidence);
        Assert.Equal(200m, draftLine.AllocatedAmount);
        Assert.Contains("草稿", draftLine.Reason);

        var voidedLine = detail.Lines.Single(l => l.PurchaseInvoiceId == voided.Id);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.BucketVoided, voidedLine.Bucket);
        Assert.True(voidedLine.IsVoided);
        Assert.Equal(300m, voidedLine.AllocatedAmount);
        Assert.Equal("开票有误，重开", voidedLine.VoidReason);
        Assert.Contains("已作废", voidedLine.Reason);

        var recordedLine = detail.Lines.Single(l => l.PurchaseInvoiceId == recorded.Id);
        Assert.True(recordedLine.IsRecordedEvidence);
        Assert.Contains("计入已开票金额", recordedLine.Reason);
    }

    // ==================== 4. 无效历史证据：供应商 / 币种 / 快照不一致绝不换算、合并或改派 ====================

    [Fact]
    public async Task Detail_flags_supplier_currency_and_snapshot_mismatches_as_invalid_history()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948131L, "PO-EV-5", SupplierA, Currency.CNY, 1000m);

        // (a) 订单供应商与发票供应商不一致（订单供应商后续被改动）
        var otherSupplier = SeedInvoice(db, 948221L, SupplierB, "CNY", "0021", 10m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948321L, otherSupplier, 948131L, 10m);

        // (b) 发票币种与订单币种不一致（不做汇率换算）
        var usdInvoice = SeedInvoice(db, 948222L, SupplierA, "USD", "0022", 20m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948322L, usdInvoice, 948131L, 20m);

        // (c) 关联行快照与发票自相矛盾（供应商快照 = 另一供应商）
        var cnyInvoice = SeedInvoice(db, 948223L, SupplierA, "CNY", "0023", 30m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948323L, cnyInvoice, 948131L, 30m, snapshotSupplierId: SupplierB);

        // (d) 关联行订单币种快照与发票币种不一致
        var cnyInvoice2 = SeedInvoice(db, 948224L, SupplierA, "CNY", "0024", 40m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948324L, cnyInvoice2, 948131L, 40m, snapshotOrderCurrency: "USD");

        // (e) 一张完全一致的已登记证据：只有它计入已开票金额
        var valid = SeedInvoice(db, 948225L, SupplierA, "CNY", "0025", 100m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948325L, valid, 948131L, 100m);

        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948131L);
        var summary = detail.Summary;

        Assert.Equal(100m, summary.RecordedAllocatedAmount);
        Assert.Equal(1, summary.RecordedInvoiceCount);
        Assert.Equal(900m, summary.RemainingUninvoicedAmount);

        // 四类不一致全部落到无效历史证据：不计入已开票金额，金额原样保留（绝不换算 / 合并 / 改派）
        Assert.Equal(4, summary.InvalidAllocationCount);
        Assert.Equal(4, summary.InvalidInvoiceCount);
        Assert.Equal(100m, summary.InvalidAllocatedAmount);
        Assert.True(summary.HasHistoricalEvidence);
        Assert.Contains("无效历史证据", summary.EvidenceNote);

        Assert.Equal(10m, detail.Lines.Single(l => l.PurchaseInvoiceId == otherSupplier.Id).AllocatedAmount);
        Assert.Equal(20m, detail.Lines.Single(l => l.PurchaseInvoiceId == usdInvoice.Id).AllocatedAmount);
        Assert.All(detail.Lines.Where(l => l.PurchaseInvoiceId != valid.Id), l =>
        {
            Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid, l.Bucket);
            Assert.False(l.IsRecordedEvidence);
        });

        Assert.Contains("供应商", detail.Lines.Single(l => l.PurchaseInvoiceId == otherSupplier.Id).Reason);
        var currencyReason = detail.Lines.Single(l => l.PurchaseInvoiceId == usdInvoice.Id).Reason;
        Assert.Contains("币种", currencyReason);
        Assert.Contains("不做汇率换算", currencyReason);
        Assert.Contains("关联行快照与发票不一致",
            detail.Lines.Single(l => l.PurchaseInvoiceId == cnyInvoice.Id).Reason);
        Assert.Contains("订单币种快照",
            detail.Lines.Single(l => l.PurchaseInvoiceId == cnyInvoice2.Id).Reason);
    }

    // ==================== 5. 无法确认的证据：发票不存在 / 已删除时金额未知，绝不按 0 或已开票 ====================

    [Fact]
    public async Task Detail_treats_missing_or_deleted_invoices_as_unavailable_evidence()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948141L, "PO-EV-6", SupplierA, Currency.CNY, 500m);

        // 关联行指向的发票不在库（如历史数据清理后残留）
        SeedAllocation(db, 948331L, null, 948141L, 50m, rawInvoiceId: 948299L, snapshotSupplierId: SupplierA,
            snapshotCurrency: "CNY", snapshotOrderCurrency: "CNY");

        // 关联行指向的发票已软删除
        var deleted = SeedInvoice(db, 948231L, SupplierA, "CNY", "0031", 60m, PurchaseInvoiceRules.StatusRecorded,
            deleted: true);
        SeedAllocation(db, 948332L, deleted, 948141L, 60m);

        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948141L)).Summary;

        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.Equal(0, summary.RecordedInvoiceCount);
        Assert.Equal(110m, summary.UnavailableAllocatedAmount);
        Assert.Equal(2, summary.UnavailableAllocationCount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced, summary.CoverageStatus);
        Assert.Equal("未开票（仅有历史 / 无效证据）", summary.CoverageLabel);
        Assert.True(summary.HasHistoricalEvidence);
        Assert.Contains("发票证据缺口", summary.EvidenceNote);
    }

    // ==================== 6. 已取消订单：覆盖状态与未开票金额未知，已登记金额仅作历史参考 ====================

    [Fact]
    public async Task Detail_reports_cancelled_orders_as_unknown_coverage_with_history_only_amounts()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948151L, "PO-EV-7", SupplierA, Currency.CNY, 1000m, DocumentStatus.Cancelled);
        var invoice = SeedInvoice(db, 948241L, SupplierA, "CNY", "0041", 300m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948341L, invoice, 948151L, 300m);
        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948151L)).Summary;

        Assert.True(summary.OrderAvailable);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateCancelled, summary.OrderState);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageUnknown, summary.CoverageStatus);
        Assert.Equal("未知", summary.CoverageLabel);
        Assert.Null(summary.RemainingUninvoicedAmount);
        Assert.Equal(300m, summary.RecordedAllocatedAmount);       // 历史参考：订单已取消，不代表已付款
        Assert.Contains("已取消", summary.EvidenceNote);
        Assert.Contains("不推断付款", summary.EvidenceNote);
    }

    // ==================== 7. 批量汇总：与请求的订单一一对应、保序、未知订单不丢行 ====================

    [Fact]
    public async Task Batch_returns_one_summary_per_requested_order_in_request_order()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948111L, "PO-EV-2", SupplierA, Currency.CNY, 1000m);
        SeedOrder(db, 948112L, "PO-EV-3", SupplierA, Currency.CNY, 452m);
        var first = SeedInvoice(db, 948201L, SupplierA, "CNY", "0001", 300m, PurchaseInvoiceRules.StatusRecorded);
        var second = SeedInvoice(db, 948202L, SupplierA, "CNY", "0002", 200m, PurchaseInvoiceRules.StatusRecorded);
        var third = SeedInvoice(db, 948204L, SupplierA, "CNY", "0004", 452m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948301L, first, 948111L, 300m);
        SeedAllocation(db, 948302L, second, 948111L, 200m);
        SeedAllocation(db, 948304L, third, 948112L, 452m);
        await db.SaveChangesAsync();

        var batch = await PurchaseOrderInvoiceEvidence.ForOrdersAsync(db,
            new PurchaseOrderInvoiceEvidenceQuery { Ids = "948112, 948111, 948199" });

        Assert.Equal(3, batch.RequestedCount);
        Assert.Equal(3, batch.ItemCount);
        Assert.False(batch.Truncated);
        Assert.Equal(new[] { 948112L, 948111L, 948199L }, batch.Items.Select(i => i.PurchaseOrderId).ToArray());
        Assert.Equal(452m, batch.Items[0].RecordedAllocatedAmount);
        Assert.Equal(500m, batch.Items[1].RecordedAllocatedAmount);
        Assert.Equal(2, batch.Items[1].RecordedInvoiceCount);

        // 请求中存在但库里没有的订单：返回未知汇总而不是静默丢行（绝不按 0 或「已开票」呈现）
        var missing = batch.Items[2];
        Assert.False(missing.OrderAvailable);
        Assert.Null(missing.OrderedAmount);
        Assert.Null(missing.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageUnknown, missing.CoverageStatus);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.OrderStateUnavailable, missing.OrderState);
        Assert.Contains("已删除", missing.EvidenceNote);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.RuleText, batch.Rule);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.ScopeText, batch.ScopeNote);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.BoundaryText, batch.Boundary);

        // 同一订单 Id 重复提交：去重后只返回一条（不重复计数、不重复请求）
        var deduped = await PurchaseOrderInvoiceEvidence.ForOrdersAsync(db,
            new PurchaseOrderInvoiceEvidenceQuery { Ids = "948111,948111," });
        Assert.Equal(1, deduped.RequestedCount);
        Assert.Equal(948111L, Assert.Single(deduped.Items).PurchaseOrderId);
    }

    // ==================== 8. 参数校验：非法 / 超界订单 Id 一律拒绝；空请求不访问数据库 ====================

    [Fact]
    public async Task Batch_rejects_invalid_or_unbounded_order_id_lists_and_empty_request_touches_nothing()
    {
        using var db = TestDbFactory.Create();

        foreach (var ids in new[] { "abc", "948111,-1", "0", "948111,0" })
        {
            var error = await Assert.ThrowsAsync<BusinessException>(() =>
                PurchaseOrderInvoiceEvidence.ForOrdersAsync(db,
                    new PurchaseOrderInvoiceEvidenceQuery { Ids = ids }));
            Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        }

        var tooMany = string.Join(',', Enumerable
            .Range(1, PurchaseOrderInvoiceEvidenceSemantics.MaxBatchOrders + 1)
            .Select(i => (948000L + i).ToString(CultureInfo.InvariantCulture)));
        var overflow = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderInvoiceEvidence.ForOrdersAsync(db,
                new PurchaseOrderInvoiceEvidenceQuery { Ids = tooMany }));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Contains("有界", overflow.Message);

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var empty = await PurchaseOrderInvoiceEvidence.ForOrdersAsync(counting.Proxy,
            new PurchaseOrderInvoiceEvidenceQuery());
        Assert.Equal(0, empty.RequestedCount);
        Assert.Empty(empty.Items);
        Assert.Equal(0, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 9. 有界读取：数据集访问次数恒定（无逐行查库），且从不写库 ====================

    [Fact]
    public async Task Evidence_derivation_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948161L, "PO-BOUND-1", SupplierA, Currency.CNY, 1000m);
        var invoice = SeedInvoice(db, 948251L, SupplierA, "CNY", "0051", 100m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948351L, invoice, 948161L, 100m);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);

        var batch = await PurchaseOrderInvoiceEvidence.ForOrdersAsync(counting.Proxy,
            new PurchaseOrderInvoiceEvidenceQuery { Ids = "948161" });
        Assert.Equal(1, batch.ItemCount);
        // 常数级访问：订单 + 持久化关联行 + 关联发票
        Assert.Equal(3, counting.DatasetReads);

        var beforeDetail = counting.DatasetReads;
        var detail = await PurchaseOrderInvoiceEvidence.ForOrderAsync(counting.Proxy, 948161L);
        Assert.Equal(100m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(3, counting.DatasetReads - beforeDetail);

        // 再补 200 张发票（都关联同一张订单）：同一接口的数据集访问次数必须保持不变（无 N+1）
        for (var i = 2; i <= 201; i++)
        {
            var extra = SeedInvoice(db, 948400L + i, SupplierA, "CNY", $"1{i:000}", 1m,
                PurchaseInvoiceRules.StatusRecorded);
            SeedAllocation(db, 948800L + i, extra, 948161L, 1m);
        }
        await db.SaveChangesAsync();

        var beforeLarge = counting.DatasetReads;
        var large = await PurchaseOrderInvoiceEvidence.ForOrdersAsync(counting.Proxy,
            new PurchaseOrderInvoiceEvidenceQuery { Ids = "948161" });
        var largeItem = large.Items.Single();
        Assert.Equal(300m, largeItem.RecordedAllocatedAmount);      // 100（首次）+ 200 × 1
        Assert.Equal(201, largeItem.RecordedInvoiceCount);
        Assert.Equal(3, counting.DatasetReads - beforeLarge);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 10. 只读：不改写订单 / 发票 / 关联行与下游记录 ====================

    [Fact]
    public async Task Evidence_derivation_does_not_mutate_order_invoice_allocation_or_other_records()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, 948171L, "PO-EV-9", SupplierA, Currency.CNY, 800m);
        order.ArrivalProgress = "部分到货";
        order.SettlementProgress = "部分结算";
        var invoice = SeedInvoice(db, 948261L, SupplierA, "CNY", "0061", 113m, PurchaseInvoiceRules.StatusRecorded);
        var allocation = SeedAllocation(db, 948361L, invoice, 948171L, 113m);
        db.Stocks.Add(new Stock { WarehouseId = 1, ProductId = 1, Quantity = 7m });
        await db.SaveChangesAsync();

        var orderUpdatedAt = order.UpdatedAt;
        var orderStatus = order.Status;
        var orderTotal = order.TotalAmount;
        var invoiceStatus = invoice.Status;
        var invoiceRecordedAt = invoice.RecordedAt;
        var invoiceUpdatedAt = invoice.UpdatedAt;
        var invoiceVoidReason = invoice.VoidReason;
        var allocationAmount = allocation.AllocatedAmount;

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        await PurchaseOrderInvoiceEvidence.ForOrderAsync(counting.Proxy, 948171L);
        await PurchaseOrderInvoiceEvidence.ForOrdersAsync(counting.Proxy,
            new PurchaseOrderInvoiceEvidenceQuery { Ids = "948171" });

        Assert.Equal(0, counting.WriteCalls);                    // 只读：一次 SaveChanges 都没有
        Assert.False(db.ChangeTracker.HasChanges());             // 也没有任何被跟踪的待提交变更

        var reloadedOrder = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == 948171L);
        Assert.Equal(orderStatus, reloadedOrder.Status);
        Assert.Equal(orderTotal, reloadedOrder.TotalAmount);
        Assert.Equal(orderUpdatedAt, reloadedOrder.UpdatedAt);
        Assert.Equal("部分到货", reloadedOrder.ArrivalProgress);
        Assert.Equal("部分结算", reloadedOrder.SettlementProgress);

        var reloadedInvoice = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == 948261L);
        Assert.Equal(invoiceStatus, reloadedInvoice.Status);
        Assert.Equal(invoiceRecordedAt, reloadedInvoice.RecordedAt);
        Assert.Null(reloadedInvoice.VoidedAt);
        Assert.Equal(invoiceUpdatedAt, reloadedInvoice.UpdatedAt);
        Assert.Equal(invoiceVoidReason, reloadedInvoice.VoidReason);

        var reloadedAllocation = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == 948361L);
        Assert.Equal(allocationAmount, reloadedAllocation.AllocatedAmount);
        Assert.False(reloadedAllocation.IsDeleted);
        Assert.Equal(7m, (await db.Stocks.AsNoTracking().SingleAsync()).Quantity);
    }

    // ==================== 11. 详情有界：命中行数上限时金额与计数按未知，明细只显示已读取部分 ====================

    [Fact]
    public async Task Detail_reports_unknown_amounts_and_truncation_when_the_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948181L, "PO-EV-10", SupplierA, Currency.CNY, 1000m);
        var invoice = SeedInvoice(db, 948271L, SupplierA, "CNY", "0071", 1m, PurchaseInvoiceRules.StatusRecorded);
        for (var i = 0; i < PurchaseOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows + 1; i++)
            SeedAllocation(db, 949000L + i, invoice, 948181L, 1m, sortOrder: i);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948181L);
        var summary = detail.Summary;

        Assert.True(summary.Truncated);
        Assert.Null(summary.RecordedAllocatedAmount);
        Assert.Null(summary.RecordedInvoiceCount);
        Assert.Null(summary.AllocationCount);
        Assert.Null(summary.RemainingUninvoicedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageUnknown, summary.CoverageStatus);
        Assert.Equal("未知", summary.CoverageLabel);
        Assert.Contains("有界上限", summary.EvidenceNote);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows, detail.LineCount);
    }

    // ==================== 12. 批量有界：命中上限时全部订单按未知返回，绝不报部分合计 ====================

    [Fact]
    public async Task Batch_reports_unknown_for_every_order_when_the_batch_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948191L, "PO-EV-11", SupplierA, Currency.CNY, 1000m);
        var invoice = SeedInvoice(db, 948281L, SupplierA, "CNY", "0081", 1m, PurchaseInvoiceRules.StatusRecorded);
        for (var i = 0; i < PurchaseOrderInvoiceEvidenceSemantics.MaxBatchEvidenceRows + 1; i++)
            SeedAllocation(db, 950000L + i, invoice, 948191L, 1m, sortOrder: i);
        await db.SaveChangesAsync();

        var batch = await PurchaseOrderInvoiceEvidence.ForOrdersAsync(db,
            new PurchaseOrderInvoiceEvidenceQuery { Ids = "948191" });

        Assert.True(batch.Truncated);
        var item = Assert.Single(batch.Items);
        Assert.True(item.Truncated);
        Assert.Null(item.RecordedAllocatedAmount);
        Assert.Null(item.RecordedInvoiceCount);
        Assert.Null(item.DraftAllocatedAmount);
        Assert.Null(item.VoidedAllocatedAmount);
        Assert.Null(item.InvalidAllocationCount);
        Assert.Null(item.AllocationCount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageUnknown, item.CoverageStatus);
        Assert.Contains("有界上限", item.EvidenceNote);
    }

    // ==================== 13. 订单不存在 / 已删除 / Id 非法：明确拒绝，绝不返回空证据 ====================

    [Fact]
    public async Task Detail_throws_not_found_for_missing_or_deleted_orders_and_invalid_ids()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948195L, "PO-EV-12", SupplierA, Currency.CNY, 100m, deleted: true);
        SeedOrder(db, 948196L, "PO-EV-13", SupplierA, Currency.CNY, 100m);
        await db.SaveChangesAsync();

        var invalid = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948999L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948195L));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        Assert.Equal("PO-EV-13",
            (await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948196L)).Summary.OrderNo);
    }

    // ==================== 14. 接口端点：详情 + 列表批量汇总 ====================

    [Fact]
    public async Task Controller_endpoints_return_invoice_evidence_payloads()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 948111L, "PO-EV-2", SupplierA, Currency.CNY, 1000m);
        var invoice = SeedInvoice(db, 948201L, SupplierA, "CNY", "0001", 300m, PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, 948301L, invoice, 948111L, 300m);
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));

        var detail = AssertOk<PurchaseOrderInvoiceEvidenceDetail>(await controller.InvoiceEvidence(948111L));
        Assert.Equal(300m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(700m, detail.Summary.RemainingUninvoicedAmount);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Single(detail.Lines);

        var batch = AssertOk<PurchaseOrderInvoiceEvidenceBatch>(
            await controller.InvoiceEvidenceSummaries("948111"));
        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(300m, batch.Items.Single().RecordedAllocatedAmount);

        var empty = AssertOk<PurchaseOrderInvoiceEvidenceBatch>(await controller.InvoiceEvidenceSummaries(null));
        Assert.Equal(0, empty.ItemCount);
        Assert.Empty(empty.Items);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => controller.InvoiceEvidenceSummaries("nope"));
        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
    }

    // ==================== 15. 口径复用与边界：与 ERP-043 / ERP-044 同源，且不是应付 / 付款 / 税务 / 结算 ====================

    [Fact]
    public async Task Semantics_declare_invoice_evidence_boundary_and_reuse_erp043_erp044_rules()
    {
        var boundary = PurchaseOrderInvoiceEvidenceSemantics.BoundaryText;
        foreach (var claim in new[]
                 {
                     "不是应付账款台账或应付余额", "不是付款授权", "不是税务", "也不是结算进度",
                     "不得当作欠款金额或可付款金额"
                 })
        {
            Assert.Contains(claim, boundary);
        }

        Assert.Contains("已登记且未作废", PurchaseOrderInvoiceEvidenceSemantics.RuleText);
        Assert.Contains("CurrencyAmountRules", PurchaseOrderInvoiceEvidenceSemantics.RuleText);
        Assert.Contains("ERP-044", PurchaseOrderInvoiceEvidenceSemantics.RuleText);
        Assert.Contains("不换算", PurchaseOrderInvoiceEvidenceSemantics.BucketText(
            PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid));
        Assert.Contains("不代表未付款", PurchaseOrderInvoiceEvidenceSemantics.NoEvidenceNote);
        Assert.Contains("有界上限", PurchaseOrderInvoiceEvidenceSemantics.TruncatedNote);
        Assert.Equal(5, PurchaseOrderInvoiceEvidenceSemantics.SupportedBuckets.Length);
        Assert.True(PurchaseOrderInvoiceEvidenceSemantics.IsHistoricalBucket(
            PurchaseOrderInvoiceEvidenceSemantics.BucketDraft));
        Assert.False(PurchaseOrderInvoiceEvidenceSemantics.IsHistoricalBucket(
            PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded));

        using var db = TestDbFactory.Create();
        SeedOrder(db, 948197L, "PO-EV-14", SupplierA, Currency.CNY, 100m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948197L);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.RuleText, detail.Rule);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.ScopeText, detail.ScopeNote);
        Assert.Equal(PurchaseOrderInvoiceEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Equal(PurchaseInvoiceRules.AmountEquationText, detail.AmountEquation);
        Assert.Equal(PurchaseInvoiceRules.LinkageRuleText, detail.LinkageRule);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageText(
                SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced),
            detail.Summary.CoverageText);
    }

    // ==================== 16. 与 ERP-043 登记链路一致：只读其持久化关联行（草稿 → 已登记 → 已作废） ====================

    [Fact]
    public async Task Detail_reads_the_persisted_rows_created_by_the_erp043_register_workflow()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 948198L, "PO-EV-15", SupplierA, Currency.CNY, 500m);
        await db.SaveChangesAsync();

        var controller = new PurchaseInvoiceController(db);
        var invoice = AssertOk<PurchaseInvoiceDto>(await controller.Create(new PurchaseInvoiceSaveDto
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeSpecial,
            InvoiceCode = "0440-0001",
            InvoiceNumber = "99001",
            InvoiceDate = AsOf,
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
                    new() { PurchaseOrderId = 948198L, AllocatedAmount = 126m }
                }
            }));

        // 草稿：工作数据，绝不计入已开票金额
        var draft = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948198L);
        Assert.Equal(0m, draft.Summary.RecordedAllocatedAmount);
        Assert.Equal(0, draft.Summary.RecordedInvoiceCount);
        Assert.Equal(126m, draft.Summary.DraftAllocatedAmount);
        Assert.Equal(500m, draft.Summary.RemainingUninvoicedAmount);

        AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));
        var recorded = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948198L);
        Assert.Equal(126m, recorded.Summary.RecordedAllocatedAmount);
        Assert.Equal(1, recorded.Summary.RecordedInvoiceCount);
        Assert.Equal(374m, recorded.Summary.RemainingUninvoicedAmount);
        Assert.Equal(0m, recorded.Summary.DraftAllocatedAmount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced,
            recorded.Summary.CoverageStatus);

        AssertOk<PurchaseInvoiceDto>(await controller.Void(invoice.Id,
            new PurchaseInvoiceVoidRequest { Reason = "开票有误，重开" }));
        var voided = await PurchaseOrderInvoiceEvidence.ForOrderAsync(db, 948198L);
        Assert.Equal(0m, voided.Summary.RecordedAllocatedAmount);
        Assert.Equal(126m, voided.Summary.VoidedAllocatedAmount);
        Assert.Equal(1, voided.Summary.VoidedInvoiceCount);
        Assert.Equal(SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced, voided.Summary.CoverageStatus);
        Assert.Equal("未开票（仅有历史 / 无效证据）", voided.Summary.CoverageLabel);
        Assert.Equal("开票有误，重开", Assert.Single(voided.Lines).VoidReason);
    }

    // ==================== 17. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_wiring_exposes_evidence_column_and_detail_action_without_per_row_requests()
    {
        var js = JsDirectory();

        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/purchase-order-invoice-evidence.js", index);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("render: row => purchaseOrderInvoiceEvidenceCellHtml(row)", modulesDoc);
        Assert.Contains("showPurchaseOrderInvoiceEvidence", modulesDoc);
        Assert.Contains("发票证据", modulesDoc);

        var script = File.ReadAllText(Path.Combine(js, "purchase-order-invoice-evidence.js"));
        Assert.Contains("'/api/purchase-orders/invoice-evidence-summaries'", script);
        Assert.Contains("const POIE_MAX_BATCH = 200", script);
        Assert.Contains("function purchaseOrderInvoiceEvidenceCellHtml", script);
        Assert.Contains("function showPurchaseOrderInvoiceEvidence", script);
        Assert.Contains("function poieBadgeHtml", script);
        Assert.Contains("i += POIE_MAX_BATCH", script);            // 按页分批，绝不逐行请求
        Assert.Contains("'未知'", script);                         // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("truncated", script);                      // 命中后端有界上限时显示未知
        Assert.Contains("不是应付余额", script);                     // 页面显式声明不是应付 / 付款 / 税务 / 结算
        Assert.Contains("CURRENT_MODULE_CODE !== 'purchase-order'", script);
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
        Currency currency, decimal totalAmount, DocumentStatus status = DocumentStatus.Approved,
        bool deleted = false)
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

    /// <summary>写入一张 ERP-043 发票（税额 0、净额 = 含税，便于聚焦证据分桶口径）</summary>
    private static PurchaseInvoice SeedInvoice(ErpDbContext db, long id, long supplierId, string currency,
        string number, decimal gross, int status, bool deleted = false, string voidReason = "")
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
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : AsOf.AddDays(-1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? AsOf : null,
            VoidReason = voidReason,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    /// <summary>
    /// 写入一条 ERP-043 持久化关联行：默认快照与发票一致（有效证据）；
    /// 通过 <paramref name="snapshotSupplierId"/> 等参数可构造历史 / 无效证据，用于验证绝不换算 / 合并 / 改派。
    /// </summary>
    private static PurchaseInvoiceAllocation SeedAllocation(ErpDbContext db, long id, PurchaseInvoice? invoice,
        long purchaseOrderId, decimal allocatedAmount, long? rawInvoiceId = null, long? snapshotSupplierId = null,
        string? snapshotCurrency = null, string? snapshotOrderCurrency = null, int sortOrder = 0)
    {
        var order = db.PurchaseOrders.Local.FirstOrDefault(o => o.Id == purchaseOrderId);
        var supplierId = snapshotSupplierId ?? invoice?.SupplierId ?? 0;

        var allocation = new PurchaseInvoiceAllocation
        {
            Id = id,
            PurchaseInvoiceId = rawInvoiceId ?? invoice?.Id ?? 0,
            PurchaseOrderId = purchaseOrderId,
            OrderNo = order?.OrderNo ?? $"PO-{purchaseOrderId}",
            OrderDate = order?.OrderDate ?? AsOf.AddDays(-5),
            OrderCurrency = snapshotOrderCurrency
                ?? order?.Currency.ToString()
                ?? invoice?.Currency
                ?? "CNY",
            SupplierId = supplierId,
            SupplierCode = $"S{supplierId}",
            SupplierName = $"供应商{supplierId}",
            AllocatedAmount = allocatedAmount,
            Currency = snapshotCurrency ?? invoice?.Currency ?? "CNY",
            SortOrder = sortOrder
        };
        db.PurchaseInvoiceAllocations.Add(allocation);
        return allocation;
    }
}
