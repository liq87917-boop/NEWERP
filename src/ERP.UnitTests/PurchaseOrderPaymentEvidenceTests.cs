using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-050 采购订单付款引用证据（只读派生）单元测试：无证据（证据缺口，不呈现已付款 / 已结清 / 逾期）、
/// 部分 / 全额 / 多付款单引用与快照、未指向本单金额、已作废历史证据单独分桶且不抬高有效合计、
/// 无效历史证据（供应商 / 币种 / 订单币种快照不一致，金额原样保留、不换算 / 不合并 / 不改派）、
/// 无法确认证据（付款单已删除）、多供应商与多币种不合并、批量保序与未知订单、
/// 有界读取（固定 4 次数据集访问、命中上限按未知、无逐行查库）、只读不写库、
/// ERP-044 对账报表第三类证据（订单金额 / 已开票金额 / 付款引用金额分列）以及接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderPaymentEvidenceTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);
    private const long SupplierA = 950001L;
    private const long SupplierB = 950002L;

    // ==================== 1. 无证据：可读、按「无付款引用证据」呈现，绝不呈现为未付款 / 已付款 / 逾期 ====================

    [Fact]
    public async Task Detail_reports_no_evidence_as_an_evidence_gap_and_never_as_paid_or_settled()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950101L, "PO-PAY-1", SupplierA, totalAmount: 1000m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950101L);
        var summary = detail.Summary;

        Assert.True(summary.OrderAvailable);
        Assert.Equal("PO-PAY-1", summary.OrderNo);
        Assert.Equal(1000m, summary.OrderedAmount);
        Assert.False(summary.HasPaymentEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.False(summary.Truncated);
        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.Equal(0, summary.RecordedPaymentCount);
        Assert.Equal(0, summary.AllocationCount);
        Assert.Equal(0m, summary.RecordedPaymentAmount);
        Assert.Equal(0m, summary.UnallocatedPaymentAmount);
        Assert.Equal(0m, summary.VoidedAllocatedAmount);
        Assert.Equal(0m, summary.InvalidAllocatedAmount);
        Assert.Equal(0m, summary.UnavailableAllocatedAmount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelNone, summary.EvidenceLabel);
        Assert.Empty(detail.Lines);
        Assert.Equal(0, detail.LineCount);
        Assert.Equal("CNY", summary.OrderCurrency);
        Assert.Equal(2, summary.AmountDecimals);
        Assert.Contains("不代表未付款", summary.EvidenceNote);
        Assert.Contains("不是银行付款金额", summary.EvidenceNote);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.RuleText, detail.Rule);
        Assert.Equal(SupplierPaymentAllocationRules.RuleText, detail.LinkageRule);
    }

    // ==================== 2. 部分引用：多张付款单按持久化有效行合计，并给出未指向本单的上下文 ====================

    [Fact]
    public async Task Detail_sums_multiple_payments_and_reports_unallocated_context()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950111L, "PO-PAY-2", SupplierA, totalAmount: 1000m);
        var first = SeedPayment(db, 950201L, "FK-PAY-1", SupplierA, amount: 600m);
        var second = SeedPayment(db, 950202L, "FK-PAY-2", SupplierA, amount: 300m);
        SeedAllocation(db, 950301L, first, 950111L, 400m);
        SeedAllocation(db, 950302L, second, 950111L, 100m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950111L);
        var summary = detail.Summary;

        Assert.Equal(500m, summary.RecordedAllocatedAmount);
        Assert.Equal(2, summary.RecordedPaymentCount);
        Assert.Equal(2, summary.AllocationCount);
        Assert.Equal(900m, summary.RecordedPaymentAmount);          // 600 + 300（按付款单去重后的快照金额）
        Assert.Equal(400m, summary.UnallocatedPaymentAmount);       // 900 − 500：未指向本订单（可能指向其他订单）
        Assert.True(summary.HasPaymentEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelRecorded, summary.EvidenceLabel);
        Assert.Equal(2, detail.Lines.Count);
        Assert.All(detail.Lines, l => Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BucketRecorded, l.Bucket));

        var firstLine = detail.Lines.Single(l => l.PaymentId == 950201L);
        Assert.Equal(400m, firstLine.AllocatedAmount);
        Assert.Equal(600m, firstLine.PaymentAmount);
        Assert.Equal(200m, firstLine.PaymentUnallocatedAmount);     // 付款单 600 中未指向本订单 200
        Assert.True(firstLine.IsRecordedEvidence);
        Assert.False(firstLine.IsVoided);
        Assert.Equal("有效", firstLine.StatusText);
        Assert.True(firstLine.PaymentAvailable);
        Assert.Contains("不代表已付款", firstLine.Reason);

        Assert.Contains("有效付款引用证据 500 CNY", summary.EvidenceNote);
        Assert.Contains("未指向本订单 400 CNY", summary.EvidenceNote);
        Assert.Contains("不是应付余额", summary.EvidenceNote);
    }

    [Fact]
    public async Task Detail_reports_fully_referenced_payment_without_unallocated_part()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950112L, "PO-PAY-3", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950203L, "FK-PAY-3", SupplierA, amount: 500m);
        SeedAllocation(db, 950303L, payment, 950112L, 500m);
        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950112L)).Summary;

        Assert.Equal(500m, summary.RecordedAllocatedAmount);
        Assert.Equal(1, summary.RecordedPaymentCount);
        Assert.Equal(500m, summary.RecordedPaymentAmount);
        Assert.Equal(0m, summary.UnallocatedPaymentAmount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelRecorded, summary.EvidenceLabel);
    }

    // ==================== 3. 已作废 / 无效 / 无法确认：单独分桶、绝不并入有效合计、详情仍可查 ====================

    [Fact]
    public async Task Detail_excludes_voided_evidence_from_active_total_but_keeps_it_inspectable()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950121L, "PO-PAY-4", SupplierA, totalAmount: 1000m);
        var active = SeedPayment(db, 950211L, "FK-PAY-4", SupplierA, amount: 1000m);
        var voided = SeedPayment(db, 950212L, "FK-PAY-5", SupplierA, amount: 1000m);
        SeedAllocation(db, 950311L, active, 950121L, 300m);
        SeedAllocation(db, 950312L, voided, 950121L, 200m,
            status: SupplierPaymentAllocationRules.StatusVoided, voidReason: "引用录错，重新登记");
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950121L);
        var summary = detail.Summary;

        Assert.Equal(300m, summary.RecordedAllocatedAmount);        // 已作废 200 绝不并入
        Assert.Equal(1, summary.RecordedPaymentCount);
        Assert.Equal(200m, summary.VoidedAllocatedAmount);
        Assert.Equal(1, summary.VoidedAllocationCount);
        Assert.Equal(2, summary.AllocationCount);
        Assert.True(summary.HasHistoricalEvidence);
        Assert.Contains("已作废历史证据", summary.EvidenceNote);

        var voidedLine = detail.Lines.Single(l => l.AllocationId == 950312L);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BucketVoided, voidedLine.Bucket);
        Assert.True(voidedLine.IsVoided);
        Assert.False(voidedLine.IsRecordedEvidence);
        Assert.Equal("引用录错，重新登记", voidedLine.VoidReason);
        Assert.Equal(200m, voidedLine.AllocatedAmount);
        Assert.Contains("已作废", voidedLine.Reason);
    }

    [Fact]
    public async Task Detail_keeps_only_historical_evidence_as_a_gap_and_never_as_unpaid()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950122L, "PO-PAY-5", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950213L, "FK-PAY-6", SupplierA, amount: 400m);
        SeedAllocation(db, 950313L, payment, 950122L, 400m,
            status: SupplierPaymentAllocationRules.StatusVoided, voidReason: "已更正");
        await db.SaveChangesAsync();

        var summary = (await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950122L)).Summary;

        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.Equal(0, summary.RecordedPaymentCount);
        Assert.True(summary.HasPaymentEvidence);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelHistoricalOnly, summary.EvidenceLabel);
        Assert.Contains("不代表未付款", summary.EvidenceNote);
    }

    // ==================== 4. 无效历史证据：供应商 / 币种 / 快照不一致（金额原样保留，绝不换算或合并） ====================

    [Fact]
    public async Task Detail_flags_supplier_mismatch_as_invalid_without_merging()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950131L, "PO-PAY-6", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950221L, "FK-PAY-7", SupplierA, amount: 1000m);
        SeedAllocation(db, 950321L, payment, 950131L, 250m, snapshotSupplierId: SupplierB);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950131L);
        var summary = detail.Summary;

        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.Equal(250m, summary.InvalidAllocatedAmount);         // 金额原样保留在无效分桶
        Assert.Equal(1, summary.InvalidAllocationCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelHistoricalOnly, summary.EvidenceLabel);

        var line = Assert.Single(detail.Lines);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BucketInvalid, line.Bucket);
        Assert.False(line.IsRecordedEvidence);
        Assert.Contains("供应商快照", line.Reason);
        Assert.Contains("不改派到其他订单", line.Reason);
        Assert.Contains("无效历史证据", summary.EvidenceNote);
    }

    [Fact]
    public async Task Detail_flags_currency_mismatch_as_invalid_without_conversion()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950132L, "PO-PAY-7", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950222L, "FK-PAY-8", SupplierA, amount: 1000m);
        SeedAllocation(db, 950322L, payment, 950132L, 100m, snapshotCurrency: "USD", snapshotOrderCurrency: "USD");
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950132L);

        Assert.Equal(0m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(100m, detail.Summary.InvalidAllocatedAmount);
        Assert.Equal(1, detail.Summary.InvalidAllocationCount);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("USD", line.Currency);                          // 原币保留，绝不换算成 CNY
        Assert.Contains("不做汇率换算", line.Reason);
    }

    [Fact]
    public async Task Detail_flags_self_contradictory_order_currency_snapshot_as_invalid()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950133L, "PO-PAY-8", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950223L, "FK-PAY-9", SupplierA, amount: 1000m);
        SeedAllocation(db, 950323L, payment, 950133L, 150m, snapshotOrderCurrency: "EUR");
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950133L);

        Assert.Equal(0m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(1, detail.Summary.InvalidAllocationCount);
        Assert.Equal(150m, detail.Summary.InvalidAllocatedAmount);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("EUR", line.OrderCurrency);
        Assert.Contains("快照自相矛盾", line.Reason);
    }


    [Fact]
    public async Task Detail_treats_deleted_payment_and_non_positive_amount_as_non_active_evidence()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950141L, "PO-PAY-9", SupplierA, totalAmount: 1000m);
        var kept = SeedPayment(db, 950231L, "FK-PAY-10", SupplierA, amount: 600m);
        var deleted = SeedPayment(db, 950232L, "FK-PAY-11", SupplierA, amount: 300m, deleted: true);
        SeedAllocation(db, 950331L, kept, 950141L, 400m);
        SeedAllocation(db, 950332L, deleted, 950141L, 300m);
        SeedAllocation(db, 950333L, kept, 950141L, 0m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950141L);
        var summary = detail.Summary;

        Assert.Equal(400m, summary.RecordedAllocatedAmount);
        Assert.Equal(300m, summary.UnavailableAllocatedAmount);      // 付款单已删除：无法确认
        Assert.Equal(1, summary.UnavailableAllocationCount);
        Assert.Equal(0m, summary.InvalidAllocatedAmount);            // 0 金额行既不并入有效合计，也不进入无效分桶
        Assert.Equal(1, summary.InvalidAllocationCount);

        var deletedLine = detail.Lines.Single(l => l.AllocationId == 950332L);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable, deletedLine.Bucket);
        Assert.False(deletedLine.PaymentAvailable);
        Assert.Contains("已删除", deletedLine.PaymentStatusText);
        Assert.Contains("无法确认", deletedLine.Reason);

        var zeroLine = detail.Lines.Single(l => l.AllocationId == 950333L);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BucketInvalid, zeroLine.Bucket);
        Assert.Contains("不是正数", zeroLine.Reason);
    }

    [Fact]
    public async Task Batch_keeps_multiple_suppliers_and_currencies_separate_without_merging()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950151L, "PO-PAY-10", SupplierA, Currency.CNY, 1000m);
        SeedOrder(db, 950152L, "PO-PAY-11", SupplierB, Currency.USD, 2000m);
        var cnyPayment = SeedPayment(db, 950241L, "FK-PAY-12", SupplierA, 500m, Currency.CNY);
        var usdPayment = SeedPayment(db, 950242L, "FK-PAY-13", SupplierB, 800m, Currency.USD);
        SeedAllocation(db, 950341L, cnyPayment, 950151L, 500m);
        SeedAllocation(db, 950342L, usdPayment, 950152L, 800m);
        await db.SaveChangesAsync();

        var batch = await PurchaseOrderPaymentEvidence.ForOrdersAsync(db,
            new PurchaseOrderPaymentEvidenceQuery { Ids = "950151,950152" });

        Assert.False(batch.Truncated);
        Assert.Equal(2, batch.ItemCount);
        Assert.Equal(2, batch.RequestedCount);

        var first = batch.Items.Single(i => i.PurchaseOrderId == 950151L);
        Assert.Equal("CNY", first.OrderCurrency);
        Assert.Equal(500m, first.RecordedAllocatedAmount);
        Assert.Equal(0m, first.UnallocatedPaymentAmount);

        var second = batch.Items.Single(i => i.PurchaseOrderId == 950152L);
        Assert.Equal("USD", second.OrderCurrency);
        Assert.Equal(800m, second.RecordedAllocatedAmount);           // 绝不与 CNY 合并成一个金额
        Assert.Equal(0m, second.UnallocatedPaymentAmount);
        Assert.Equal(2, second.AmountDecimals);
    }

    // ==================== 5. 批量保序 / 未知订单 / 命中上限按未知（绝不部分合计） ====================

    [Fact]
    public async Task Batch_returns_summaries_in_request_order_and_marks_unknown_orders()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950161L, "PO-PAY-12", SupplierA, totalAmount: 1000m);
        SeedOrder(db, 950162L, "PO-PAY-13", SupplierA, totalAmount: 500m, deleted: true);
        await db.SaveChangesAsync();

        var batch = await PurchaseOrderPaymentEvidence.ForOrdersAsync(db,
            new PurchaseOrderPaymentEvidenceQuery { Ids = "950162,950999,950161,950161" });

        Assert.Equal(3, batch.RequestedCount);                        // 去重后 3 张（保序）
        Assert.Equal(3, batch.ItemCount);
        Assert.Equal(950162L, batch.Items[0].PurchaseOrderId);
        Assert.Equal(950999L, batch.Items[1].PurchaseOrderId);        // 未找到的订单不丢行
        Assert.Equal(950161L, batch.Items[2].PurchaseOrderId);

        Assert.False(batch.Items[0].OrderAvailable);
        Assert.Equal(0m, batch.Items[0].RecordedAllocatedAmount);
        Assert.False(batch.Items[1].OrderAvailable);
        Assert.Null(batch.Items[1].OrderedAmount);                    // 未知，绝不按 0
        Assert.True(batch.Items[2].OrderAvailable);
        Assert.Equal(1000m, batch.Items[2].OrderedAmount);
    }

    [Fact]
    public async Task Batch_reports_unknown_for_every_order_when_the_batch_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950171L, "PO-PAY-14", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950251L, "FK-PAY-14", SupplierA, amount: 100000m);
        for (var i = 0; i < PurchaseOrderPaymentEvidenceSemantics.MaxBatchEvidenceRows + 1; i++)
            SeedAllocation(db, 960000L + i, payment, 950171L, 1m);
        await db.SaveChangesAsync();

        var batch = await PurchaseOrderPaymentEvidence.ForOrdersAsync(db,
            new PurchaseOrderPaymentEvidenceQuery { Ids = "950171" });

        Assert.True(batch.Truncated);
        var item = Assert.Single(batch.Items);
        Assert.True(item.Truncated);
        Assert.Null(item.RecordedAllocatedAmount);
        Assert.Null(item.RecordedPaymentCount);
        Assert.Null(item.AllocationCount);
        Assert.Null(item.RecordedPaymentAmount);
        Assert.Null(item.UnallocatedPaymentAmount);
        Assert.Null(item.VoidedAllocatedAmount);
        Assert.Null(item.InvalidAllocationCount);
        Assert.Null(item.UnavailableAllocationCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelUnknown, item.EvidenceLabel);
        Assert.Contains("有界上限", item.EvidenceNote);
    }

    [Fact]
    public async Task Detail_reports_unknown_when_the_order_detail_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950172L, "PO-PAY-15", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950252L, "FK-PAY-15", SupplierA, amount: 100000m);
        for (var i = 0; i < PurchaseOrderPaymentEvidenceSemantics.MaxOrderEvidenceRows + 1; i++)
            SeedAllocation(db, 961000L + i, payment, 950172L, 1m);
        await db.SaveChangesAsync();

        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950172L);

        Assert.True(detail.Summary.Truncated);
        Assert.Null(detail.Summary.RecordedAllocatedAmount);
        Assert.Null(detail.Summary.AllocationCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.MaxOrderEvidenceRows, detail.LineCount);
        Assert.Contains("有界上限", detail.Summary.EvidenceNote);
    }

    // ==================== 6. 参数校验 / 订单不存在 / 空请求零访问 ====================

    [Fact]
    public async Task Detail_throws_not_found_for_missing_or_deleted_orders_and_invalid_ids()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950181L, "PO-PAY-16", SupplierA, totalAmount: 100m, deleted: true);
        SeedOrder(db, 950182L, "PO-PAY-17", SupplierA, totalAmount: 100m);
        await db.SaveChangesAsync();

        var invalid = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderPaymentEvidence.ForOrderAsync(db, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950999L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950181L));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        Assert.Equal("PO-PAY-17",
            (await PurchaseOrderPaymentEvidence.ForOrderAsync(db, 950182L)).Summary.OrderNo);
    }

    [Fact]
    public async Task Query_rejects_invalid_or_oversized_ids_and_empty_requests_touch_no_data()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950191L, "PO-PAY-18", SupplierA, totalAmount: 100m);
        await db.SaveChangesAsync();

        var invalid = Assert.Throws<BusinessException>(() => new PurchaseOrderPaymentEvidenceQuery
        {
            Ids = "950191,abc"
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var zero = Assert.Throws<BusinessException>(() => new PurchaseOrderPaymentEvidenceQuery
        {
            Ids = "0"
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, zero.Code);

        var oversized = Assert.Throws<BusinessException>(() => new PurchaseOrderPaymentEvidenceQuery
        {
            Ids = string.Join(',', Enumerable.Range(1, PurchaseOrderPaymentEvidenceSemantics.MaxBatchOrders + 1))
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, oversized.Code);

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var empty = await PurchaseOrderPaymentEvidence.ForOrdersAsync(counting.Proxy,
            new PurchaseOrderPaymentEvidenceQuery { Ids = "  " });
        Assert.Equal(0, empty.RequestedCount);
        Assert.Empty(empty.Items);
        Assert.Equal(0, counting.DatasetReads);                       // 空请求不访问任何数据集
        Assert.Equal(0, counting.WriteCalls);
    }

    [Fact]
    public async Task Engine_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db, 950191L, "PO-PAY-19", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950261L, "FK-PAY-16", SupplierA, amount: 900m);
        SeedAllocation(db, 950351L, payment, 950191L, 400m);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var detail = await PurchaseOrderPaymentEvidence.ForOrderAsync(counting.Proxy, 950191L);

        // 固定 4 次数据集访问：订单 + 持久化引用行 + 付款单 + 付款单侧有效引用合计聚合（与行数无关，无逐行查库）
        Assert.Equal(4, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);                         // 只读派生：没有一次 SaveChanges
        Assert.Equal(400m, detail.Summary.RecordedAllocatedAmount);
    }



    // ==================== 7. ERP-049 登记链路一致性 + 非变更边界 ====================

    [Fact]
    public async Task Evidence_follows_the_erp_049_register_chain_and_never_mutates_sources()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, 950201L, "PO-PAY-20", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950271L, "FK-PAY-17", SupplierA, amount: 1000m);
        db.BaseTaxRefunds.Add(new BaseTaxRefund
        {
            RefundNo = "TS-PAY-1", SalesOrderNo = "SO-PAY-9", RefundableAmount = 123m, Status = "待申报"
        });
        await db.SaveChangesAsync();

        // ERP-049 服务端登记：写入服务端快照并形成有效证据
        var register = new SupplierPaymentAllocationController(db);
        var created = AssertOk<SupplierPaymentAllocationDto>(await register.Create(
            new SupplierPaymentAllocationSaveDto
            {
                PaymentId = payment.Id, PurchaseOrderId = order.Id, AllocatedAmount = 250m, Remark = "分三次付款的首笔"
            }));

        var afterCreate = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(250m, afterCreate.Summary.RecordedAllocatedAmount);
        Assert.Equal(1, afterCreate.Summary.RecordedPaymentCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BucketRecorded, Assert.Single(afterCreate.Lines).Bucket);

        // 作废后：有效合计归零，历史证据保留可读（金额与原因均可见）
        AssertOk<SupplierPaymentAllocationDto>(await register.Void(created.Id,
            new SupplierPaymentAllocationVoidRequest { Reason = "引用录错，重新登记" }));

        var afterVoid = await PurchaseOrderPaymentEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(0m, afterVoid.Summary.RecordedAllocatedAmount);
        Assert.Equal(250m, afterVoid.Summary.VoidedAllocatedAmount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelHistoricalOnly, afterVoid.Summary.EvidenceLabel);
        Assert.Equal("引用录错，重新登记", Assert.Single(afterVoid.Lines).VoidReason);

        // 非变更边界：订单 / 付款单 / 引用行快照 / 退税记录都不被本视图改写
        var orderAfter = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.Equal(1000m, orderAfter.TotalAmount);
        Assert.Equal("未到货", orderAfter.ArrivalProgress);
        Assert.Equal("未结算", orderAfter.SettlementProgress);

        var paymentAfter = await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(DocumentStatus.Approved, paymentAfter.Status);
        Assert.Equal(1000m, paymentAfter.Amount);
        Assert.False(paymentAfter.IsDeleted);

        var rowAfter = await db.SupplierPaymentAllocations.AsNoTracking().FirstAsync(a => a.Id == created.Id);
        Assert.Equal(SupplierPaymentAllocationRules.StatusVoided, rowAfter.Status);
        Assert.Equal(250m, rowAfter.AllocatedAmount);
        Assert.Equal(SupplierA, rowAfter.SupplierId);
        Assert.Equal("CNY", rowAfter.Currency);

        Assert.Equal(123m, (await db.BaseTaxRefunds.AsNoTracking()
            .FirstAsync(t => t.RefundNo == "TS-PAY-1")).RefundableAmount);
    }

    // ==================== 8. ERP-044 对账报表：订单金额 / 已开票金额 / 付款引用金额三类证据分列 ====================

    [Fact]
    public async Task Reconciliation_report_lists_payment_reference_evidence_as_a_third_labelled_class()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 950211L, "PO-PAY-21", SupplierA, totalAmount: 1000m);
        var invoice = SeedInvoice(db, 950281L, SupplierA, "CNY", "0201", 300m);
        SeedInvoiceAllocation(db, 950401L, invoice, 950211L, 300m);
        var payment = SeedPayment(db, 950291L, "FK-PAY-18", SupplierA, amount: 600m);
        SeedAllocation(db, 950411L, payment, 950211L, 400m);
        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db, new SupplierInvoiceReconciliationQuery());

        Assert.Equal(1, report.PaymentReferenceOrderCount);
        Assert.Equal(0, report.NoPaymentReferenceOrderCount);
        Assert.Equal(0, report.UnknownPaymentReferenceOrderCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.RuleText, report.PaymentEvidenceRule);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BoundaryText, report.PaymentEvidenceBoundary);

        var orderLine = Assert.Single(Assert.Single(report.Groups).Invoices).Orders[0];
        Assert.Equal(1000m, orderLine.OrderedAmount);                 // 订单金额
        Assert.Equal(300m, orderLine.InvoicedAmount);                 // 已开票金额（发票证据）
        Assert.Equal(400m, orderLine.PaymentReferenceAmount);         // 付款引用金额（第三类独立证据）
        Assert.Equal(1, orderLine.PaymentAllocationCount);
        Assert.Equal(1, orderLine.PaymentCount);
        Assert.Equal(200m, orderLine.PaymentUnallocatedAmount);       // 付款单 600 中未指向本订单 200
        Assert.Equal(0, orderLine.VoidedPaymentAllocationCount);
        Assert.Equal(0, orderLine.InvalidPaymentAllocationCount);
        Assert.Equal(0, orderLine.UnavailablePaymentAllocationCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelRecorded, orderLine.PaymentEvidenceLabel);
        Assert.Contains("付款引用证据 400", orderLine.Note);
        Assert.Contains("不是银行付款金额", orderLine.Note);

        var group = Assert.Single(report.Groups);
        Assert.Equal(400m, group.PaymentReferenceAmount);
        Assert.Equal(1, group.PaymentReferenceOrderCount);
        Assert.Equal(0, group.NoPaymentReferenceOrderCount);
        Assert.Equal(0, group.UnknownPaymentReferenceOrderCount);
        Assert.Equal(0, group.HistoricalPaymentAllocationCount);

        var currency = Assert.Single(report.Currencies);
        Assert.Equal(400m, currency.PaymentReferenceAmount);
        Assert.Equal(1, currency.PaymentReferenceOrderCount);
    }


    [Fact]
    public async Task Reconciliation_report_shows_payment_evidence_unknown_when_the_aggregate_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");

        // 3 张发票 × 200 张订单 = 600 张不同订单（> ERP-050 的 500 张有界聚合上限）
        var orderId = 970000L;
        var allocationId = 971000L;
        for (var invoiceIndex = 0; invoiceIndex < 3; invoiceIndex++)
        {
            var invoice = SeedInvoice(db, 970100L + invoiceIndex, SupplierA, "CNY", $"03{invoiceIndex:00}", 200m);
            for (var i = 0; i < 200; i++)
            {
                var current = orderId++;
                SeedOrder(db, current, $"PO-PAY-CAP-{current}", SupplierA, totalAmount: 10m);
                SeedInvoiceAllocation(db, allocationId++, invoice, current, 1m);
            }
        }

        await db.SaveChangesAsync();

        var report = await SupplierInvoiceReconciliation.ForQueryAsync(db,
            new SupplierInvoiceReconciliationQuery { PageSize = 50 });

        Assert.Equal(3, report.PageInvoiceCount);
        Assert.Equal(600, report.UnknownPaymentReferenceOrderCount);   // 命中上限：一律按未知，不给部分合计
        Assert.Equal(0, report.PaymentReferenceOrderCount);
        Assert.Equal(0, report.NoPaymentReferenceOrderCount);

        var group = Assert.Single(report.Groups);
        Assert.Equal(0m, group.PaymentReferenceAmount);
        Assert.Equal(600, group.UnknownPaymentReferenceOrderCount);

        var orderLine = group.Invoices[0].Orders[0];
        Assert.Null(orderLine.PaymentReferenceAmount);
        Assert.Null(orderLine.PaymentAllocationCount);
        Assert.Null(orderLine.PaymentCount);
        Assert.Null(orderLine.VoidedPaymentAllocationCount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.LabelUnknown, orderLine.PaymentEvidenceLabel);
        Assert.Contains("有界聚合上限", orderLine.Note);
        Assert.Contains("不代表未付款", orderLine.Note);
    }

    // ==================== 9. 接口端点：详情 + 列表批量汇总 ====================

    [Fact]
    public async Task Controller_endpoints_return_payment_evidence_payloads()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, 950221L, "PO-PAY-22", SupplierA, totalAmount: 1000m);
        var payment = SeedPayment(db, 950301L, "FK-PAY-19", SupplierA, amount: 1000m);
        SeedAllocation(db, 950421L, payment, 950221L, 350m);
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));

        var detail = AssertOk<PurchaseOrderPaymentEvidenceDetail>(await controller.PaymentEvidence(950221L));
        Assert.Equal(350m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Single(detail.Lines);

        var batch = AssertOk<PurchaseOrderPaymentEvidenceBatch>(
            await controller.PaymentEvidenceSummaries("950221"));
        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(350m, batch.Items[0].RecordedAllocatedAmount);
        Assert.Equal(PurchaseOrderPaymentEvidenceSemantics.RuleText, batch.Rule);

        var empty = AssertOk<PurchaseOrderPaymentEvidenceBatch>(
            await controller.PaymentEvidenceSummaries(null));
        Assert.Empty(empty.Items);
    }

    // ==================== 10. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_wiring_exposes_payment_evidence_column_and_detail_action_without_per_row_requests()
    {
        var js = JsDirectory();

        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/purchase-order-payment-evidence.js", index);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("render: row => purchaseOrderPaymentEvidenceCellHtml(row)", modulesDoc);
        Assert.Contains("showPurchaseOrderPaymentEvidence", modulesDoc);
        Assert.Contains("付款引用证据", modulesDoc);

        var script = File.ReadAllText(Path.Combine(js, "purchase-order-payment-evidence.js"));
        Assert.Contains("'/api/purchase-orders/payment-evidence-summaries'", script);
        Assert.Contains("const POPE_MAX_BATCH = 200", script);
        Assert.Contains("function purchaseOrderPaymentEvidenceCellHtml", script);
        Assert.Contains("function showPurchaseOrderPaymentEvidence", script);
        Assert.Contains("function popeBadgeHtml", script);
        Assert.Contains("i += POPE_MAX_BATCH", script);                 // 按页分批，绝不逐行请求
        Assert.Contains("'未知'", script);                             // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("truncated", script);                          // 命中后端有界上限时显示未知
        Assert.Contains("无付款引用证据", script);
        Assert.Contains("不是应付余额", script);                        // 页面显式声明不是付款 / 应付 / 税 / 结算
        Assert.Contains("CURRENT_MODULE_CODE !== 'purchase-order'", script);

        // ERP-044 报表页面必须把付款引用证据作为第三类独立证据展示（不并入已开票金额）
        var report = File.ReadAllText(Path.Combine(js, "supplier-invoice-reconciliation.js"));
        Assert.Contains("paymentReferenceAmount", report);
        Assert.Contains("data.paymentEvidenceRule", report);
        Assert.Contains("data.paymentEvidenceBoundary", report);
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


    /// <summary>
    /// 写入一条 ERP-049 持久化付款引用行：默认快照与付款单 / 订单一致（有效证据）；
    /// 通过 <paramref name="snapshotSupplierId"/> 等参数可构造历史 / 无效证据，用于验证绝不换算 / 合并 / 改派。
    /// </summary>
    private static SupplierPaymentAllocation SeedAllocation(ErpDbContext db, long id, FinancePayment? payment,
        long orderId, decimal allocatedAmount, int status = SupplierPaymentAllocationRules.StatusActive,
        long? snapshotSupplierId = null, string? snapshotCurrency = null, string? snapshotOrderCurrency = null,
        long? rawPaymentId = null, decimal? paymentAmountSnapshot = null, string? paymentNoSnapshot = null,
        string remark = "", string voidReason = "", bool deleted = false)
    {
        var order = db.PurchaseOrders.Local.FirstOrDefault(o => o.Id == orderId);
        var supplierId = snapshotSupplierId ?? payment?.SupplierId ?? SupplierA;
        var currency = snapshotCurrency ?? (payment is null ? "CNY" : payment.Currency.ToString());

        var row = new SupplierPaymentAllocation
        {
            Id = id,
            PaymentId = rawPaymentId ?? payment?.Id ?? 0,
            PaymentNo = paymentNoSnapshot ?? payment?.PaymentNo ?? $"FK-{id}",
            PaymentDate = payment?.PaymentDate ?? AsOf.AddDays(-3),
            PaymentStatus = payment is null ? 0 : (int)payment.Status,
            PaymentStatusText = payment is null
                ? string.Empty
                : SupplierPaymentAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = paymentAmountSnapshot ?? payment?.Amount ?? 0m,
            PurchaseOrderId = orderId,
            OrderNo = order?.OrderNo ?? $"PO-{orderId}",
            OrderDate = order?.OrderDate ?? AsOf.AddDays(-5),
            OrderStatus = order is null ? 0 : (int)order.Status,
            OrderCurrency = snapshotOrderCurrency ?? (order is null ? currency : order.Currency.ToString()),
            SupplierId = supplierId,
            SupplierCode = $"S{supplierId}",
            SupplierName = $"供应商{supplierId}",
            AllocatedAmount = allocatedAmount,
            Currency = currency,
            Status = status,
            AllocatedAt = AsOf.AddDays(-2),
            VoidedAt = status == SupplierPaymentAllocationRules.StatusVoided ? AsOf.AddDays(-1) : null,
            VoidReason = voidReason,
            Remark = remark,
            IsDeleted = deleted
        };
        db.SupplierPaymentAllocations.Add(row);
        return row;
    }

    /// <summary>写入一张 ERP-043 已登记发票（税额 0、净额 = 含税，便于聚焦证据口径）</summary>
    private static PurchaseInvoice SeedInvoice(ErpDbContext db, long id, long supplierId, string currency,
        string number, decimal gross)
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
            Status = PurchaseInvoiceRules.StatusRecorded,
            RecordedAt = AsOf.AddDays(-1)
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    /// <summary>写入一条 ERP-043 发票 → 采购订单 持久化关联行（快照与发票 / 订单一致）</summary>
    private static PurchaseInvoiceAllocation SeedInvoiceAllocation(ErpDbContext db, long id,
        PurchaseInvoice invoice, long orderId, decimal allocatedAmount, int sortOrder = 0)
    {
        var order = db.PurchaseOrders.Local.FirstOrDefault(o => o.Id == orderId);
        var allocation = new PurchaseInvoiceAllocation
        {
            Id = id,
            PurchaseInvoiceId = invoice.Id,
            PurchaseOrderId = orderId,
            OrderNo = order?.OrderNo ?? $"PO-{orderId}",
            OrderDate = order?.OrderDate ?? AsOf.AddDays(-5),
            OrderCurrency = order is null ? invoice.Currency : order.Currency.ToString(),
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

}
