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
/// ERP-054 销售订单收款引用证据（只读派生）单元测试：无证据（证据缺口，不呈现未收款 / 已收款 / 已结清 / 逾期）、
/// 部分 / 全额 / 多收款单引用与快照、未指向本单与订单金额未被引用证据覆盖的上下文、
/// 已作废历史证据单独分桶且不抬高有效合计、无效历史证据（客户 / 币种 / 订单币种快照不一致，金额原样保留、
/// 不换算 / 不合并 / 不改派）、无法确认证据（收款单 / 订单已删除）、多客户与多币种不合并、
/// 批量保序与未知订单、有界读取（固定 4 次数据集访问、命中上限按未知、无逐行查库）、只读不写库、
/// ERP-046 报表第三类证据（已订 / 已出数量、收款申请链接、收款引用登记证据分列）以及接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SalesOrderReceiptEvidenceTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);
    private const string CustomerA = "C054-A";
    private const string CustomerB = "C054-B";

    // ==================== 1. 无证据：可读、按「无收款引用证据」呈现，绝不呈现为未收款 / 已收款 / 逾期 ====================

    [Fact]
    public async Task Detail_reports_no_evidence_as_an_evidence_gap_and_never_as_unpaid_or_received()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-1", CustomerA, Currency.USD, 1000m);

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);
        var summary = detail.Summary;

        Assert.True(summary.OrderAvailable);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.OrderStateAvailable, summary.OrderState);
        Assert.Equal("SO-EV-1", summary.OrderNo);
        Assert.Equal(1000m, summary.OrderedAmount);
        Assert.False(summary.HasReceiptAllocationEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.False(summary.Truncated);
        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.Equal(0, summary.RecordedReceiptCount);
        Assert.Equal(0, summary.AllocationCount);
        Assert.Equal(0m, summary.RecordedReceiptAmount);
        Assert.Equal(0m, summary.UnreferencedReceiptAmount);
        Assert.Equal(1000m, summary.UnreferencedOrderAmount);        // 订单金额中没有任何有效引用证据指向（不是应收余额）
        Assert.Equal(0m, summary.VoidedAllocatedAmount);
        Assert.Equal(0m, summary.InvalidAllocatedAmount);
        Assert.Equal(0m, summary.UnavailableAllocatedAmount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationNone, summary.AllocationStatus);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelNone, summary.EvidenceLabel);
        Assert.Empty(detail.Lines);
        Assert.Equal(0, detail.LineCount);
        Assert.Equal("USD", summary.OrderCurrency);
        Assert.Equal(2, summary.AmountDecimals);
        Assert.Contains("不代表未收款", summary.EvidenceNote);
        Assert.Contains("不是银行入账金额", summary.EvidenceNote);
        Assert.Contains("不是应收余额", summary.EvidenceNote);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.RuleText, detail.Rule);
        Assert.Equal(CustomerReceiptAllocationRules.RuleText, detail.LinkageRule);
        Assert.Equal(CustomerReceiptAllocationRules.BoundaryText, detail.AllocationBoundary);
    }

    // ==================== 2. 部分引用：多张收款单按持久化有效行合计，并给出未指向本单的上下文 ====================

    [Fact]
    public async Task Detail_sums_multiple_receipts_and_reports_unreferenced_context()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-2", CustomerA, Currency.USD, 1000m);
        var first = SeedReceipt(db, "SK-EV-1", CustomerA, 600m, Currency.USD);
        var second = SeedReceipt(db, "SK-EV-2", CustomerA, 300m, Currency.USD);
        SeedRow(db, 954001L, first, order, 400m);
        SeedRow(db, 954002L, second, order, 100m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);
        var summary = detail.Summary;

        Assert.Equal(500m, summary.RecordedAllocatedAmount);
        Assert.Equal(2, summary.RecordedReceiptCount);
        Assert.Equal(2, summary.AllocationCount);
        Assert.Equal(900m, summary.RecordedReceiptAmount);            // 600 + 300（按收款单去重后的快照金额）
        Assert.Equal(400m, summary.UnreferencedReceiptAmount);       // 900 − 500：未指向本订单（可能指向其他订单）
        Assert.Equal(500m, summary.UnreferencedOrderAmount);         // 1000 − 500：订单金额中未被引用证据指向的部分
        Assert.True(summary.HasReceiptAllocationEvidence);
        Assert.False(summary.HasHistoricalEvidence);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationRecorded, summary.AllocationStatus);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelRecorded, summary.EvidenceLabel);
        Assert.Equal(2, detail.Lines.Count);
        Assert.All(detail.Lines, l => Assert.Equal(SalesOrderReceiptEvidenceSemantics.BucketRecorded, l.Bucket));

        var firstLine = detail.Lines.Single(l => l.ReceiptId == first.Id);
        Assert.Equal(400m, firstLine.AllocatedAmount);
        Assert.Equal(600m, firstLine.ReceiptAmount);
        Assert.Equal(200m, firstLine.ReceiptUnreferencedAmount);     // 收款单 600 中未指向本订单 200
        Assert.True(firstLine.IsRecordedEvidence);
        Assert.False(firstLine.IsVoided);
        Assert.Equal("有效", firstLine.StatusText);
        Assert.True(firstLine.ReceiptAvailable);
        Assert.Contains("不代表已收款", firstLine.Reason);
        Assert.Equal(CustomerIdOf(CustomerA), firstLine.CustomerId);
        Assert.Equal("C054-SNAP", firstLine.CustomerCode);
        Assert.Equal("SO-EV-2", firstLine.OrderNo);

        Assert.Contains("有效收款引用证据 500 USD", summary.EvidenceNote);
        Assert.Contains("未指向本订单 400 USD", summary.EvidenceNote);
        Assert.Contains("不是应收余额", summary.EvidenceNote);
    }

    [Fact]
    public async Task Detail_reports_fully_referenced_receipt_without_unreferenced_part()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-3", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-3", CustomerA, 500m, Currency.USD);
        SeedRow(db, 954003L, receipt, order, 500m);
        await db.SaveChangesAsync();

        var summary = (await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id)).Summary;

        Assert.Equal(500m, summary.RecordedAllocatedAmount);
        Assert.Equal(0m, summary.UnreferencedReceiptAmount);
        Assert.Equal(500m, summary.UnreferencedOrderAmount);
        Assert.Equal("SO-EV-3", summary.OrderNo);
    }

    // ==================== 3. 已作废证据：不计入有效合计，但保留可读（原始值 + 作废原因） ====================

    [Fact]
    public async Task Detail_excludes_voided_evidence_from_active_total_but_keeps_it_inspectable()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-4", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-4", CustomerA, 800m, Currency.USD);
        SeedRow(db, 954004L, receipt, order, 250m, status: CustomerReceiptAllocationRules.StatusVoided,
            voidReason: "引用录错，重新登记");
        SeedRow(db, 954005L, receipt, order, 100m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);
        var summary = detail.Summary;

        Assert.Equal(100m, summary.RecordedAllocatedAmount);          // 已作废行绝不并入有效合计
        Assert.Equal(250m, summary.VoidedAllocatedAmount);
        Assert.Equal(1, summary.VoidedAllocationCount);
        Assert.Equal(2, summary.AllocationCount);                     // 含历史证据的总条数
        Assert.True(summary.HasHistoricalEvidence);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelRecorded, summary.EvidenceLabel);

        var voidedLine = detail.Lines.Single(l => l.IsVoided);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BucketVoided, voidedLine.Bucket);
        Assert.Equal("已作废", voidedLine.StatusText);
        Assert.Equal(250m, voidedLine.AllocatedAmount);               // 原始金额保留
        Assert.Equal("引用录错，重新登记", voidedLine.VoidReason);
        Assert.NotNull(voidedLine.VoidedAt);
        Assert.Contains("引用行已作废", voidedLine.Reason);
        Assert.Contains("另有已作废历史证据 1 条", summary.EvidenceNote);
    }

    // ==================== 4. 只有历史 / 无效证据：证据缺口，绝不呈现为未收款 ====================

    [Fact]
    public async Task Detail_keeps_only_historical_evidence_as_a_gap_and_never_as_unpaid()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-5", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-5", CustomerA, 100m, Currency.USD);
        SeedRow(db, 954006L, receipt, order, 100m,
            status: CustomerReceiptAllocationRules.StatusVoided, voidReason: "作废");
        await db.SaveChangesAsync();

        var summary = (await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id)).Summary;

        Assert.Equal(0m, summary.RecordedAllocatedAmount);
        Assert.True(summary.HasReceiptAllocationEvidence);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelHistoricalOnly, summary.EvidenceLabel);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationHistoricalOnly, summary.AllocationStatus);
        Assert.Contains("不代表未收款", summary.EvidenceNote);
        Assert.Contains("已结清", summary.EvidenceNote);
        Assert.DoesNotContain("已收讫", summary.EvidenceLabel);
    }

    // ==================== 5. 历史漂移：客户 / 币种 / 快照不一致按无效证据单列，绝不换算 / 合并 / 改派 ====================

    [Fact]
    public async Task Detail_flags_customer_mismatch_as_invalid_without_merging()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-6", CustomerA, Currency.USD, 1000m);
        var otherOrder = SeedOrder(db, "SO-EV-6B", CustomerB, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-6", CustomerA, 500m, Currency.USD);
        SeedRow(db, 954007L, receipt, order, 300m, customerId: 999999L);   // 客户快照与收款单客户不一致
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(300m, detail.Summary.InvalidAllocatedAmount);
        Assert.Equal(1, detail.Summary.InvalidAllocationCount);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BucketInvalid, line.Bucket);
        Assert.Equal(300m, line.AllocatedAmount);                    // 金额原样保留，绝不换算 / 合并 / 改派
        Assert.Contains("客户快照", line.Reason);
        Assert.Contains("不一致", line.Reason);
        Assert.Contains("无效历史证据", line.BucketText);

        // 另一张订单完全没有引用行：不一致的历史证据绝不被「改派」过去
        var other = await SalesOrderReceiptEvidence.ForOrderAsync(db, otherOrder.Id);
        Assert.Empty(other.Lines);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelNone, other.Summary.EvidenceLabel);
    }

    [Fact]
    public async Task Detail_flags_currency_mismatch_as_invalid_without_conversion()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-7", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-7", CustomerA, 500m, Currency.USD);
        SeedRow(db, 954008L, receipt, order, 200m, currency: "CNY");   // 引用行币种与收款单币种不一致
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(200m, detail.Summary.InvalidAllocatedAmount);
        var line = Assert.Single(detail.Lines);
        Assert.Contains("币种", line.Reason);
        Assert.Contains("不做汇率换算", line.Reason);
        Assert.Contains("另有无效历史证据（客户 / 币种或快照不一致） 1 条", detail.Summary.EvidenceNote);
    }

    [Fact]
    public async Task Detail_flags_self_contradictory_order_currency_snapshot_as_invalid()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-8", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-8", CustomerA, 500m, Currency.USD);
        SeedRow(db, 954009L, receipt, order, 150m, orderCurrency: "EUR");   // 订单币种快照自相矛盾
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(150m, detail.Summary.InvalidAllocatedAmount);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("EUR", line.OrderCurrency);
        Assert.Contains("自相矛盾", line.Reason);
    }

    // ==================== 6. 无法确认 / 非正数：绝不并入有效合计，也绝不按 0 顶替 ====================

    [Fact]
    public async Task Detail_treats_deleted_receipt_and_non_positive_amount_as_non_active_evidence()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-9", CustomerA, Currency.USD, 1000m);
        var deletedReceipt = SeedReceipt(db, "SK-EV-DEL", CustomerA, 300m, Currency.USD, deleted: true);
        var normalReceipt = SeedReceipt(db, "SK-EV-9", CustomerA, 300m, Currency.USD);
        SeedRow(db, 954010L, deletedReceipt, order, 300m);        // 收款单已删除 → 无法确认
        SeedRow(db, 954011L, normalReceipt, order, 0m);           // 金额非正数 → 无效
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(300m, detail.Summary.UnavailableAllocatedAmount);
        Assert.Equal(1, detail.Summary.UnavailableAllocationCount);
        Assert.Equal(0m, detail.Summary.InvalidAllocatedAmount);
        Assert.Equal(1, detail.Summary.InvalidAllocationCount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelHistoricalOnly, detail.Summary.EvidenceLabel);

        var unavailable = detail.Lines.Single(l => l.Bucket == SalesOrderReceiptEvidenceSemantics.BucketUnavailable);
        Assert.False(unavailable.ReceiptAvailable);
        Assert.Null(unavailable.ReceiptStatus);
        Assert.Contains("无法确认", unavailable.ReceiptStatusText);
        Assert.Contains("收款单不存在或已删除", unavailable.Reason);

        var invalid = detail.Lines.Single(l => l.Bucket == SalesOrderReceiptEvidenceSemantics.BucketInvalid);
        Assert.Contains("不是正数", invalid.Reason);
        Assert.Contains("另有无法确认的证据（收款单或订单已删除 / 不存在） 1 条", detail.Summary.EvidenceNote);
    }

    // ==================== 7. 批量汇总：多客户 / 多币种不合并、按请求顺序、未知订单不丢行 ====================

    [Fact]
    public async Task Batch_keeps_multiple_customers_and_currencies_separate_without_merging()
    {
        using var db = TestDbFactory.Create();
        var orderUsdA = SeedOrder(db, "SO-EV-10", CustomerA, Currency.USD, 1000m);
        var orderCnyB = SeedOrder(db, "SO-EV-11", CustomerB, Currency.CNY, 800m);
        var receiptUsd = SeedReceipt(db, "SK-EV-10", CustomerA, 600m, Currency.USD);
        var receiptCny = SeedReceipt(db, "SK-EV-11", CustomerB, 400m, Currency.CNY);
        SeedRow(db, 954012L, receiptUsd, orderUsdA, 250m);
        SeedRow(db, 954013L, receiptCny, orderCnyB, 100m);
        await db.SaveChangesAsync();

        var batch = await SalesOrderReceiptEvidence.ForOrdersAsync(db,
            new SalesOrderReceiptEvidenceQuery { Ids = $"{orderUsdA.Id},{orderCnyB.Id}" });

        Assert.Equal(2, batch.RequestedCount);
        Assert.Equal(2, batch.ItemCount);
        Assert.False(batch.Truncated);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.RuleText, batch.Rule);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BoundaryText, batch.Boundary);

        Assert.Equal("USD", batch.Items[0].OrderCurrency);
        Assert.Equal(250m, batch.Items[0].RecordedAllocatedAmount);
        Assert.Equal(1, batch.Items[0].RecordedReceiptCount);
        Assert.Equal("CNY", batch.Items[1].OrderCurrency);
        Assert.Equal(100m, batch.Items[1].RecordedAllocatedAmount);

        // 批量汇总没有跨币种 / 跨客户的合计字段（绝不把两类金额相加）
        Assert.DoesNotContain(typeof(SalesOrderReceiptEvidenceBatch).GetProperties(),
            p => p.Name.Contains("Total", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Batch_returns_summaries_in_request_order_and_marks_unknown_orders()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-12", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-12", CustomerA, 300m, Currency.USD);
        SeedRow(db, 954014L, receipt, order, 100m);
        await db.SaveChangesAsync();

        var batch = await SalesOrderReceiptEvidence.ForOrdersAsync(db,
            new SalesOrderReceiptEvidenceQuery { Ids = $"  {order.Id} , 999999 ,{order.Id} " });

        Assert.Equal(2, batch.RequestedCount);                                  // 去重 + 保序
        Assert.Equal(order.Id, batch.Items[0].SalesOrderId);
        Assert.Equal(100m, batch.Items[0].RecordedAllocatedAmount);
        Assert.Equal(999999L, batch.Items[1].SalesOrderId);
        Assert.False(batch.Items[1].OrderAvailable);
        Assert.Null(batch.Items[1].OrderedAmount);                              // 未知，绝不按 0
        Assert.Equal(0m, batch.Items[1].RecordedAllocatedAmount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelNone, batch.Items[1].EvidenceLabel);
        Assert.Equal(string.Empty, batch.Items[1].OrderNo);
    }

    // ==================== 8. 有界读取：命中上限一律按未知，不报部分合计 ====================

    [Fact]
    public async Task Batch_reports_unknown_for_every_order_when_the_batch_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-13", CustomerA, Currency.USD, 1000000m);
        var receipt = SeedReceipt(db, "SK-EV-13", CustomerA, 1000000m, Currency.USD);
        for (var i = 0; i < SalesOrderReceiptEvidenceSemantics.MaxBatchEvidenceRows + 1; i++)
            SeedRow(db, 960000L + i, receipt, order, 1m);
        await db.SaveChangesAsync();

        var batch = await SalesOrderReceiptEvidence.ForOrdersAsync(db,
            new SalesOrderReceiptEvidenceQuery { Ids = order.Id.ToString() });

        Assert.True(batch.Truncated);
        var item = Assert.Single(batch.Items);
        Assert.True(item.Truncated);
        Assert.Null(item.RecordedAllocatedAmount);
        Assert.Null(item.RecordedReceiptCount);
        Assert.Null(item.AllocationCount);
        Assert.Null(item.RecordedReceiptAmount);
        Assert.Null(item.UnreferencedReceiptAmount);
        Assert.Null(item.UnreferencedOrderAmount);
        Assert.Null(item.VoidedAllocatedAmount);
        Assert.Null(item.InvalidAllocationCount);
        Assert.Null(item.UnavailableAllocationCount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationUnknown, item.AllocationStatus);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelUnknown, item.EvidenceLabel);
        Assert.Contains("有界上限", item.EvidenceNote);
    }

    [Fact]
    public async Task Detail_reports_unknown_when_the_order_detail_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-14", CustomerA, Currency.USD, 1000000m);
        var receipt = SeedReceipt(db, "SK-EV-14", CustomerA, 1000000m, Currency.USD);
        for (var i = 0; i < SalesOrderReceiptEvidenceSemantics.MaxOrderEvidenceRows + 1; i++)
            SeedRow(db, 961000L + i, receipt, order, 1m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);

        Assert.True(detail.Summary.Truncated);
        Assert.Null(detail.Summary.RecordedAllocatedAmount);
        Assert.Null(detail.Summary.AllocationCount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.MaxOrderEvidenceRows, detail.LineCount);
        Assert.Contains("有界上限", detail.Summary.EvidenceNote);
    }

    // ==================== 9. 参数校验 / 订单不存在 / 空请求零访问 ====================

    [Fact]
    public async Task Detail_throws_not_found_for_missing_or_deleted_orders_and_invalid_ids()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedOrder(db, "SO-EV-15", CustomerA, Currency.USD, 100m, deleted: true);
        await db.SaveChangesAsync();

        var invalid = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderReceiptEvidence.ForOrderAsync(db, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderReceiptEvidence.ForOrderAsync(db, 999999L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var deletedOrder = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderReceiptEvidence.ForOrderAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, deletedOrder.Code);
    }

    [Fact]
    public async Task Query_rejects_invalid_or_oversized_ids_and_empty_requests_touch_no_data()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-16", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var invalid = Assert.Throws<BusinessException>(() => new SalesOrderReceiptEvidenceQuery
        {
            Ids = $"{order.Id},abc"
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var zero = Assert.Throws<BusinessException>(() => new SalesOrderReceiptEvidenceQuery
        {
            Ids = "0"
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, zero.Code);

        var oversized = Assert.Throws<BusinessException>(() => new SalesOrderReceiptEvidenceQuery
        {
            Ids = string.Join(',', Enumerable.Range(1, SalesOrderReceiptEvidenceSemantics.MaxBatchOrders + 1))
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, oversized.Code);

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var empty = await SalesOrderReceiptEvidence.ForOrdersAsync(counting.Proxy,
            new SalesOrderReceiptEvidenceQuery { Ids = "  " });
        Assert.Equal(0, empty.RequestedCount);
        Assert.Empty(empty.Items);
        Assert.Equal(0, counting.DatasetReads);                       // 空请求不访问任何数据集
        Assert.Equal(0, counting.WriteCalls);
    }

    [Fact]
    public async Task Engine_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-17", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-17", CustomerA, 900m, Currency.USD);
        SeedRow(db, 954015L, receipt, order, 400m);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(counting.Proxy, order.Id);

        // 固定 4 次数据集访问：订单 + 持久化引用行 + 收款单 + 收款单侧有效引用合计聚合（与行数无关，无逐行查库）
        Assert.Equal(4, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);                         // 只读派生：没有一次 SaveChanges
        Assert.Equal(400m, detail.Summary.RecordedAllocatedAmount);
    }

    // ==================== 10. ERP-053 登记链路一致性 + 非变更边界 ====================

    [Fact]
    public async Task Evidence_follows_the_erp_053_register_chain_and_never_mutates_sources()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-EV-18", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-18", CustomerA, 1000m, Currency.USD);
        db.BaseTaxRefunds.Add(new BaseTaxRefund
        {
            RefundNo = "TS-EV-1", SalesOrderNo = "SO-EV-18", RefundableAmount = 123m, Status = "待申报"
        });
        await db.SaveChangesAsync();

        // ERP-053 服务端登记：写入服务端快照并形成有效证据
        var register = new CustomerReceiptAllocationController(db);
        var created = AssertOk<CustomerReceiptAllocationDto>(await register.Create(
            new CustomerReceiptAllocationSaveDto
            {
                ReceiptId = receipt.Id, SalesOrderId = order.Id, AllocatedAmount = 250m, Remark = "首款"
            }));

        var afterCreate = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(250m, afterCreate.Summary.RecordedAllocatedAmount);
        Assert.Equal(1, afterCreate.Summary.RecordedReceiptCount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BucketRecorded, Assert.Single(afterCreate.Lines).Bucket);

        // 作废后：有效合计归零，历史证据保留可读（金额与原因均可见）
        AssertOk<CustomerReceiptAllocationDto>(await register.Void(created.Id,
            new CustomerReceiptAllocationVoidRequest { Reason = "引用录错，重新登记" }));

        var afterVoid = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(0m, afterVoid.Summary.RecordedAllocatedAmount);
        Assert.Equal(250m, afterVoid.Summary.VoidedAllocatedAmount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelHistoricalOnly, afterVoid.Summary.EvidenceLabel);
        Assert.Equal("引用录错，重新登记", Assert.Single(afterVoid.Lines).VoidReason);

        // 非变更边界：订单 / 收款单 / 引用行快照 / 客户 / 退税记录都不被本视图改写
        var orderAfter = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.Equal(1000m, orderAfter.TotalAmount);
        Assert.Equal(Currency.USD, orderAfter.Currency);

        var receiptAfter = await db.FinanceReceipts.AsNoTracking().FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(DocumentStatus.Approved, receiptAfter.Status);
        Assert.Equal(1000m, receiptAfter.Amount);
        Assert.False(receiptAfter.IsDeleted);

        var rowAfter = await db.CustomerReceiptAllocations.AsNoTracking().FirstAsync(a => a.Id == created.Id);
        Assert.Equal(CustomerReceiptAllocationRules.StatusVoided, rowAfter.Status);
        Assert.Equal(250m, rowAfter.AllocatedAmount);
        Assert.Equal(CustomerIdOf(CustomerA), rowAfter.CustomerId);
        Assert.Equal("USD", rowAfter.Currency);

        var customerAfter = await db.BaseCustomers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.Equal(100000m, customerAfter.CreditLimit);
        Assert.Equal("正常", customerAfter.CreditStatus);

        Assert.Equal(123m, (await db.BaseTaxRefunds.AsNoTracking()
            .FirstAsync(t => t.RefundNo == "TS-EV-1")).RefundableAmount);
    }

    // ==================== 11. ERP-046 报表：收款引用证据作为独立标注的证据类别 ====================

    [Fact]
    public async Task Reconciliation_report_exposes_receipt_allocation_evidence_as_a_labelled_class()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-EV-19", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-19", CustomerA, 600m, Currency.USD);
        SeedRow(db, 954016L, receipt, order, 400m);
        await db.SaveChangesAsync();

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            new SalesOrderReceiptReconciliationQuery());

        Assert.Equal(1, report.ReceiptAllocationOrderCount);
        Assert.Equal(0, report.HistoricalOnlyReceiptAllocationOrderCount);
        Assert.Equal(0, report.NoReceiptAllocationOrderCount);
        Assert.Equal(0, report.UnknownReceiptAllocationOrderCount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.RuleText, report.ReceiptAllocationRule);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BoundaryText, report.ReceiptAllocationBoundary);

        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(1000m, row.OrderAmount);                                   // 第一类：订单金额
        Assert.Equal(SalesOrderProgress.ShipmentNone, row.ShipmentStatus);      // 第二类：出货数量证据（无出库单）
        Assert.Null(row.LinkedReceiptAmount);                                   // 第二类：收款申请链接（无申请单 → 未知）
        Assert.Equal(400m, row.RecordedReceiptAllocationAmount);                // 第三类：收款引用登记证据
        Assert.Equal(1, row.ReceiptAllocationCount);
        Assert.Equal(1, row.RecordedReceiptCount);
        Assert.Equal(600m, row.UnreferencedOrderAmount);
        Assert.Equal(0, row.VoidedReceiptAllocationCount);
        Assert.Equal(0, row.InvalidReceiptAllocationCount);
        Assert.Equal(0, row.UnavailableReceiptAllocationCount);
        Assert.False(row.ReceiptAllocationTruncated);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationRecorded, row.ReceiptAllocationStatus);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelRecorded, row.ReceiptAllocationEvidenceLabel);
        Assert.Contains("收款引用证据", row.ReceiptAllocationNote);
        Assert.Contains("不是银行入账金额", row.ReceiptAllocationNote);
        Assert.Contains("两类独立口径", row.ReceiptAllocationNote);

        var group = Assert.Single(report.Groups);
        Assert.Equal(1, group.ReceiptAllocationOrderCount);
        Assert.Equal(0, group.NoReceiptAllocationOrderCount);
        Assert.Equal(400m, group.RecordedReceiptAllocationAmount);
    }

    [Fact]
    public async Task Reconciliation_report_shows_no_evidence_as_a_gap_and_respects_the_aggregate_cap()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-EV-20", CustomerA, Currency.USD, 500m);
        await db.SaveChangesAsync();

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            new SalesOrderReceiptReconciliationQuery());

        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationNone, row.ReceiptAllocationStatus);
        Assert.Equal(0m, row.RecordedReceiptAllocationAmount);
        Assert.Equal(0, row.ReceiptAllocationCount);
        Assert.Equal(500m, row.UnreferencedOrderAmount);
        Assert.Contains("不代表未收款", row.ReceiptAllocationNote);
        Assert.Null(row.LinkedReceiptAmount);                       // 未登记收款申请：未知，不按 0
        Assert.Equal(1, report.NoReceiptAllocationOrderCount);

        // 命中有界聚合上限：不给部分合计（调用方一律按未知显示）
        var capped = await SalesOrderReceiptEvidence.AggregatesForOrdersAsync(db,
            new List<long> { order.Id }, orderCap: 1);
        Assert.False(capped.Truncated);
        var overCap = await SalesOrderReceiptEvidence.AggregatesForOrdersAsync(db,
            new List<long> { order.Id, order.Id + 1 }, orderCap: 1);
        Assert.True(overCap.Truncated);
        Assert.Empty(overCap.ByOrder);
        Assert.Equal(0m, overCap.Get(order.Id).RecordedAmount);     // 未参与聚合 → 全 0（调用方按未知显示）
    }

    // ==================== 12. 接口端点：详情 + 列表批量汇总 ====================

    [Fact]
    public async Task Controller_endpoints_return_receipt_evidence_payloads()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-21", CustomerA, Currency.USD, 1000m);
        var receipt = SeedReceipt(db, "SK-EV-21", CustomerA, 600m, Currency.USD);
        SeedRow(db, 954017L, receipt, order, 350m);
        await db.SaveChangesAsync();

        var controller = new SalesOrderController(db, new DocumentNumberService(db));

        var detail = AssertOk<SalesOrderReceiptEvidenceDetail>(await controller.ReceiptEvidence(order.Id));
        Assert.Equal(350m, detail.Summary.RecordedAllocatedAmount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Single(detail.Lines);

        var batch = AssertOk<SalesOrderReceiptEvidenceBatch>(
            await controller.ReceiptEvidenceSummaries(new SalesOrderReceiptEvidenceQuery { Ids = order.Id.ToString() }));
        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(350m, batch.Items[0].RecordedAllocatedAmount);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.RuleText, batch.Rule);

        var empty = AssertOk<SalesOrderReceiptEvidenceBatch>(
            await controller.ReceiptEvidenceSummaries(new SalesOrderReceiptEvidenceQuery()));
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.RequestedCount);
    }

    // ==================== 13. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_wiring_exposes_receipt_evidence_column_and_detail_action_without_per_row_requests()
    {
        var js = JsDirectory();

        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/sales-order-receipt-evidence.js", index);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("render: row => salesOrderReceiptEvidenceCellHtml(row)", modulesDoc);
        Assert.Contains("showSalesOrderReceiptEvidence", modulesDoc);
        Assert.Contains("收款引用证据", modulesDoc);

        var script = File.ReadAllText(Path.Combine(js, "sales-order-receipt-evidence.js"));
        Assert.Contains("'/api/sales-orders/receipt-evidence-summaries'", script);
        Assert.Contains("const SORE_MAX_BATCH = 200", script);
        Assert.Contains("function salesOrderReceiptEvidenceCellHtml", script);
        Assert.Contains("function showSalesOrderReceiptEvidence", script);
        Assert.Contains("function soreBadgeHtml", script);
        Assert.Contains("i += SORE_MAX_BATCH", script);                 // 按页分批，绝不逐行请求
        Assert.Contains("'未知'", script);                             // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("truncated", script);                          // 命中后端有界上限时显示未知
        Assert.Contains("无收款引用证据", script);
        Assert.Contains("不是应收余额", script);                        // 页面显式声明不是入账 / 应收 / 核销 / 结算
        Assert.Contains("CURRENT_MODULE_CODE !== 'sales-order'", script);

        // ERP-046 报表页面必须把收款引用证据作为独立证据列展示（不并入已关联收款金额）
        var report = File.ReadAllText(Path.Combine(js, "sales-order-receipt-reconciliation.js"));
        Assert.Contains("receiptAllocationStatus", report);
        Assert.Contains("recordedReceiptAllocationAmount", report);
        Assert.Contains("data.receiptAllocationRule", report);
        Assert.Contains("data.receiptAllocationBoundary", report);
        Assert.Contains("引用证据外订单金额", report);
    }

    // ==================== 14. 非应收账款台账 / 对账单 / 账龄：字段、路由与文案边界 ====================

    [Fact]
    public void Evidence_exposes_no_receivable_ledger_statement_or_aging_fields_and_states_the_boundary()
    {
        foreach (var type in new[]
                 {
                     typeof(SalesOrderReceiptEvidenceSummary), typeof(SalesOrderReceiptEvidenceLine),
                     typeof(SalesOrderReceiptEvidenceDetail), typeof(SalesOrderReceiptEvidenceBatch),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Overdue", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Receivable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Statement", StringComparison.Ordinal));
        }

        // 说明（ERP-056）：ERP-046 报表行 / 分组 / 报表自 ERP-056 起刻意新增销项发票证据字段（Invoice*），
        // 因此不再对这三种类型断言「不含 Invoice 字段」；其「不含应收 / 账龄 / 到期日 / 结算字段」的边界断言
        // 由 ERP-056 的 SalesOrderInvoiceEvidenceTests 继续覆盖，本用例只保留收款引用证据自身的字段边界。
        foreach (var type in new[]
                 {
                     typeof(SalesOrderReceiptReconciliationOrderRow), typeof(SalesOrderReceiptReconciliationGroup),
                     typeof(SalesOrderReceiptReconciliationReport),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Overdue", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Receivable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Statement", StringComparison.Ordinal));
        }

        Assert.Contains("不是银行入账", SalesOrderReceiptEvidenceSemantics.BoundaryText);
        Assert.Contains("不是应收账款台账或应收余额", SalesOrderReceiptEvidenceSemantics.BoundaryText);
        Assert.Contains("不是货款核销", SalesOrderReceiptEvidenceSemantics.BoundaryText);
        Assert.Contains("也不是客户对账单", SalesOrderReceiptEvidenceSemantics.BoundaryText);
        Assert.Contains("不判断是否已收款", SalesOrderReceiptEvidenceSemantics.BoundaryText);
        Assert.Contains("绝不推断为未收款", SalesOrderReceiptEvidenceSemantics.RuleText);
        Assert.Contains("不得当作应收余额", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);

        // 只读：收款引用证据只有 GET 路由，没有任何创建 / 修改 / 作废 / 删除入口
        var getRoutes = typeof(SalesOrderController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("{id:long}/receipt-evidence", getRoutes);
        Assert.Contains("receipt-evidence-summaries", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "{id:long}/receipt-evidence"));
        Assert.Single(getRoutes.Where(r => r == "receipt-evidence-summaries"));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("receipt-evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("receipt-evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("receipt-evidence", StringComparison.Ordinal));
    }

    // ==================== 15. 无引用行的既有订单仍可读，且绝不回填 ====================

    [Fact]
    public async Task Legacy_orders_without_allocation_rows_remain_readable_without_backfill()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-EV-22", CustomerA, Currency.USD, 750m);
        SeedReceipt(db, "SK-EV-22", CustomerA, 750m, Currency.USD);       // 存在收款单但不登记引用
        await db.SaveChangesAsync();

        var detail = await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.LabelNone, detail.Summary.EvidenceLabel);
        Assert.Empty(detail.Lines);

        var batch = await SalesOrderReceiptEvidence.ForOrdersAsync(db,
            new SalesOrderReceiptEvidenceQuery { Ids = order.Id.ToString() });
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationNone, batch.Items[0].AllocationStatus);

        // 只读派生绝不回填任何引用行（历史数据保持原样）
        Assert.Empty(await db.CustomerReceiptAllocations.AsNoTracking().ToListAsync());
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

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, string customerCode,
        Currency currency, decimal totalAmount, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = CustomerIdOf(customerCode),
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, string customerCode,
        decimal amount, Currency currency, bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = CustomerIdOf(customerCode),
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    /// <summary>客户编码 → 客户 Id（测试内固定映射；引用行与订单 / 收款单客户保持一致）</summary>
    private static long CustomerIdOf(string customerCode) => customerCode switch
    {
        CustomerA => 954001L,
        _ => 954002L,
    };

    /// <summary>
    /// 直接落库一条 ERP-053 收款引用行（用于构造历史漂移 / 作废 / 无法确认等已存在证据，不走登记接口）。
    /// </summary>
    private static CustomerReceiptAllocation SeedRow(ErpDbContext db, long id, FinanceReceipt? receipt,
        SalesOrder? order, decimal amount, int status = 1, long? customerId = null,
        string? currency = null, string? orderCurrency = null, string voidReason = "")
    {
        var currencyText = currency ?? (receipt?.Currency.ToString() ?? "CNY");
        var row = new CustomerReceiptAllocation
        {
            Id = id,
            ReceiptId = receipt?.Id ?? 0,
            ReceiptNo = receipt?.ReceiptNo ?? string.Empty,
            ReceiptDate = receipt?.ReceiptDate ?? new DateTime(2026, 9, 10),
            ReceiptStatus = (int)(receipt?.Status ?? DocumentStatus.Approved),
            ReceiptStatusText = string.Empty,
            ReceiptAmount = receipt?.Amount ?? 0m,
            SalesOrderId = order?.Id ?? 0,
            OrderNo = order?.OrderNo ?? string.Empty,
            OrderDate = order?.OrderDate ?? new DateTime(2026, 9, 1),
            OrderStatus = (int)(order?.Status ?? DocumentStatus.Approved),
            OrderCurrency = orderCurrency ?? currencyText,
            CustomerId = customerId ?? receipt?.CustomerId ?? 0,
            CustomerCode = "C054-SNAP",
            CustomerName = "客户快照",
            AllocatedAmount = amount,
            Currency = currencyText,
            Status = status,
            AllocatedAt = AsOf,
            VoidedAt = status == CustomerReceiptAllocationRules.StatusVoided ? AsOf.AddDays(1) : null,
            VoidReason = voidReason
        };
        db.CustomerReceiptAllocations.Add(row);
        return row;
    }
}
