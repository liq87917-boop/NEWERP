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
/// ERP-056 销售订单销项发票证据（只读派生）单元测试：无证据（证据缺口，不呈现未开票 / 已开票 / 欠税 / 已收款 /
/// 已结清 / 逾期）、部分 / 全额 / 多发票分摊与快照、未指向本单与订单金额未被发票证据覆盖的上下文、
/// 草稿证据与有效证据区分、已作废历史证据单独分桶且不抬高有效合计、无效历史证据（客户 / 币种 / 订单币种快照 /
/// 金额等式不一致，金额原样保留、不换算 / 不合并 / 不改派）、无法确认证据（发票证据 / 订单已删除）、
/// 多客户与多币种不合并、批量保序与未知订单、有界读取（固定 4 次数据集访问、命中上限按未知、无逐行查库）、
/// 只读不写库、ERP-046 报表第四类证据（订单金额 / 收款申请链接 / 收款引用登记证据 / 销项发票登记证据分列）
/// 以及接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SalesOrderInvoiceEvidenceTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);
    private const string CustomerA = "C056-A";
    private const string CustomerB = "C056-B";

    // ==================== 1. 无证据：可读、按「无销项发票证据」呈现，绝不呈现为未开票 / 欠税 / 已收款 ====================

    [Fact]
    public async Task Detail_reports_no_evidence_as_an_evidence_gap_and_never_as_uninvoiced_or_received()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-1", CustomerA, Currency.USD, 1000m);

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        var summary = detail.Summary;

        Assert.True(summary.OrderAvailable);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.OrderStateAvailable, summary.OrderState);
        Assert.Equal("SO-IV-1", summary.OrderNo);
        Assert.Equal(1000m, summary.OrderedAmount);
        Assert.False(summary.HasInvoiceEvidence);
        Assert.False(summary.HasNonActiveEvidence);
        Assert.False(summary.Truncated);
        Assert.Equal(0m, summary.RecordedInvoicedAmount);
        Assert.Equal(0, summary.RecordedInvoiceCount);
        Assert.Equal(0, summary.InvoiceAllocationCount);
        Assert.Equal(0m, summary.RecordedInvoiceGrossAmount);
        Assert.Equal(0m, summary.UnreferencedInvoiceAmount);
        Assert.Equal(1000m, summary.InvoiceUnreferencedOrderAmount);   // 订单金额中没有任何有效发票证据分摊到（不是应收余额）
        Assert.Equal(0m, summary.DraftInvoiceAllocatedAmount);
        Assert.Equal(0m, summary.VoidedInvoiceAllocatedAmount);
        Assert.Equal(0m, summary.InvalidInvoiceAllocatedAmount);
        Assert.Equal(0m, summary.UnavailableInvoiceAllocatedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceNone, summary.InvoiceEvidenceStatus);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNone, summary.InvoiceEvidenceLabel);
        Assert.Empty(detail.Lines);
        Assert.Equal(0, detail.LineCount);
        Assert.Equal("USD", summary.OrderCurrency);
        Assert.Equal(2, summary.AmountDecimals);
        Assert.Contains("不代表未开票", summary.InvoiceEvidenceNote);
        Assert.Contains("不是已开票金额", summary.InvoiceEvidenceNote);
        Assert.Contains("不是应交税金", summary.InvoiceEvidenceNote);
        Assert.Contains("不是应收余额", summary.InvoiceEvidenceNote);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.RuleText, detail.Rule);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkageRuleText, detail.LinkageRule);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.BoundaryText, detail.InvoiceRegisterBoundary);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.AmountEquationText, detail.AmountEquationRule);
    }

    // ==================== 2. 部分分摊：多张发票按持久化有效分摊行合计，并给出未指向本单的上下文 ====================

    [Fact]
    public async Task Detail_sums_multiple_invoices_and_reports_unreferenced_context()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-2", CustomerA, Currency.USD, 1000m);
        var first = SeedInvoice(db, 956001L, "INV-IV-1", CustomerA, Currency.USD, 600m, 0m, 600m);
        var second = SeedInvoice(db, 956002L, "INV-IV-2", CustomerA, Currency.USD, 300m, 0m, 300m);
        SeedRow(db, 955001L, first, order, 400m);
        SeedRow(db, 955002L, second, order, 100m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        var summary = detail.Summary;

        Assert.Equal(500m, summary.RecordedInvoicedAmount);
        Assert.Equal(2, summary.RecordedInvoiceCount);
        Assert.Equal(2, summary.InvoiceAllocationCount);
        Assert.Equal(900m, summary.RecordedInvoiceGrossAmount);          // 600 + 300（按发票去重后的含税总额快照）
        Assert.Equal(400m, summary.UnreferencedInvoiceAmount);           // 900 − 500：未指向本订单（可能指向其他订单）
        Assert.Equal(500m, summary.InvoiceUnreferencedOrderAmount);      // 1000 − 500：订单金额中未被发票证据分摊的部分
        Assert.True(summary.HasInvoiceEvidence);
        Assert.False(summary.HasNonActiveEvidence);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceRecorded, summary.InvoiceEvidenceStatus);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelRecorded, summary.InvoiceEvidenceLabel);
        Assert.Equal(2, detail.Lines.Count);
        Assert.All(detail.Lines, l => Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketRecorded, l.Bucket));

        var firstLine = detail.Lines.Single(l => l.InvoiceId == first.Id);
        Assert.Equal(400m, firstLine.AllocatedAmount);
        Assert.Equal(600m, firstLine.GrossAmount);
        Assert.Equal(0m, firstLine.TaxAmount);
        Assert.Equal(200m, firstLine.InvoiceUnreferencedAmount);         // 发票 600 中未指向本订单 200
        Assert.True(firstLine.IsRecordedEvidence);
        Assert.True(firstLine.IsRecorded);
        Assert.False(firstLine.IsDraft);
        Assert.False(firstLine.IsVoided);
        Assert.Equal("已登记", firstLine.InvoiceStatusText);
        Assert.True(firstLine.InvoiceAvailable);
        Assert.Contains("不代表已开票", firstLine.Reason);
        Assert.Equal(CustomerIdOf(CustomerA), firstLine.CustomerId);
        Assert.Equal("C056-SNAP", firstLine.CustomerCode);
        Assert.Equal("SO-IV-2", firstLine.OrderNo);

        Assert.Contains("有效销项发票证据 500 USD", summary.InvoiceEvidenceNote);
        Assert.Contains("未指向本订单 400 USD", summary.InvoiceEvidenceNote);
        Assert.Contains("不是应收余额", summary.InvoiceEvidenceNote);
    }

    // ==================== 3. 全额分摊：没有未指向本单的部分，也不产生应收余额 ====================

    [Fact]
    public async Task Detail_reports_fully_allocated_invoice_without_unreferenced_part()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-3", CustomerA, Currency.CNY, 500m);
        var invoice = SeedInvoice(db, 956003L, "INV-IV-3", CustomerA, Currency.CNY, 400m, 100m, 500m);
        SeedRow(db, 955003L, invoice, order, 500m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(500m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(500m, detail.Summary.RecordedInvoiceGrossAmount);
        Assert.Equal(0m, detail.Summary.UnreferencedInvoiceAmount);
        Assert.Equal(0m, detail.Summary.InvoiceUnreferencedOrderAmount);
        Assert.Equal(1, detail.Summary.RecordedInvoiceCount);
        Assert.Equal(0m, Assert.Single(detail.Lines).InvoiceUnreferencedAmount);
        Assert.Equal(100m, Assert.Single(detail.Lines).TaxAmount);       // 税额只作展示，不是应交税金结论
    }

    // ==================== 4. 草稿证据：与有效证据区分，绝不计入有效合计 ====================

    [Fact]
    public async Task Detail_separates_draft_evidence_from_recorded_and_never_counts_it()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-4", CustomerA, Currency.USD, 1000m);
        var draft = SeedInvoice(db, 956004L, "INV-IV-4", CustomerA, Currency.USD, 300m, 0m, 300m,
            status: CustomerSalesInvoiceEvidenceRules.StatusDraft);
        SeedRow(db, 955004L, draft, order, 300m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        var summary = detail.Summary;

        Assert.Equal(0m, summary.RecordedInvoicedAmount);                 // 草稿不算有效证据
        Assert.Equal(0, summary.RecordedInvoiceCount);
        Assert.Equal(300m, summary.DraftInvoiceAllocatedAmount);
        Assert.Equal(1, summary.DraftInvoiceAllocationCount);
        Assert.Equal(1, summary.InvoiceAllocationCount);
        Assert.True(summary.HasInvoiceEvidence);
        Assert.True(summary.HasNonActiveEvidence);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceNonActiveOnly, summary.InvoiceEvidenceStatus);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNonActiveOnly, summary.InvoiceEvidenceLabel);

        var line = Assert.Single(detail.Lines);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketDraft, line.Bucket);
        Assert.False(line.IsRecordedEvidence);
        Assert.True(line.IsDraft);
        Assert.False(line.IsVoided);
        Assert.Equal("草稿", line.InvoiceStatusText);
        Assert.Contains("仍是草稿", line.Reason);
        Assert.Contains("工作数据", line.Reason);
        Assert.Contains("草稿", summary.InvoiceEvidenceNote);
    }

    // ==================== 5. 已作废证据：不计入有效合计，但保留可读与原因 ====================

    [Fact]
    public async Task Detail_excludes_voided_evidence_from_active_total_but_keeps_it_inspectable()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-5", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956005L, "INV-IV-5", CustomerA, Currency.USD, 250m, 0m, 250m,
            status: CustomerSalesInvoiceEvidenceRules.StatusVoided, voidReason: "发票录错，重新登记");
        SeedRow(db, 955005L, invoice, order, 250m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(250m, detail.Summary.VoidedInvoiceAllocatedAmount);
        Assert.Equal(1, detail.Summary.VoidedInvoiceAllocationCount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNonActiveOnly, detail.Summary.InvoiceEvidenceLabel);

        var line = Assert.Single(detail.Lines);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketVoided, line.Bucket);
        Assert.True(line.IsVoided);
        Assert.False(line.IsRecordedEvidence);
        Assert.Equal("已作废", line.InvoiceStatusText);
        Assert.Equal("发票录错，重新登记", line.VoidReason);
        Assert.Equal(250m, line.AllocatedAmount);                        // 原始值保留，绝不重写
        Assert.NotNull(line.VoidedAt);
        Assert.Contains("已作废", line.Reason);
    }

    // ==================== 6~9. 历史漂移：无效证据单列，不换算 / 不合并 / 不改派 ====================

    [Fact]
    public async Task Detail_flags_customer_mismatch_as_invalid_without_merging()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-6", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956006L, "INV-IV-6", CustomerA, Currency.USD, 400m, 0m, 400m);
        SeedRow(db, 955006L, invoice, order, 400m, customerId: CustomerIdOf(CustomerB));
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(400m, detail.Summary.InvalidInvoiceAllocatedAmount);
        Assert.Equal(1, detail.Summary.InvalidInvoiceAllocationCount);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketInvalid, line.Bucket);
        Assert.Contains("客户", line.Reason);
        Assert.Contains("不合并", line.Reason);
    }

    [Fact]
    public async Task Detail_flags_currency_mismatch_as_invalid_without_conversion()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-7", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956007L, "INV-IV-7", CustomerA, Currency.EUR, 400m, 0m, 400m);
        SeedRow(db, 955007L, invoice, order, 400m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(400m, detail.Summary.InvalidInvoiceAllocatedAmount);
        Assert.Contains("币种", Assert.Single(detail.Lines).Reason);
        Assert.Contains("汇率", Assert.Single(detail.Lines).Reason);
    }

    [Fact]
    public async Task Detail_flags_self_contradictory_order_currency_snapshot_as_invalid()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-8", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956008L, "INV-IV-8", CustomerA, Currency.USD, 400m, 0m, 400m);
        SeedRow(db, 955008L, invoice, order, 400m, orderCurrency: "EUR");
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(1, detail.Summary.InvalidInvoiceAllocationCount);
        Assert.Contains("快照自相矛盾", Assert.Single(detail.Lines).Reason);
    }

    [Fact]
    public async Task Detail_flags_broken_amount_equation_as_invalid()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-9", CustomerA, Currency.USD, 1000m);
        // 净额 100 + 税额 0 ≠ 含税总额 200：ERP-055 写入规则不允许，历史漂移必须按无效单列
        var invoice = SeedInvoice(db, 956009L, "INV-IV-9", CustomerA, Currency.USD, 100m, 0m, 200m);
        SeedRow(db, 955009L, invoice, order, 200m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        var line = Assert.Single(detail.Lines);

        Assert.Equal(0m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketInvalid, line.Bucket);
        Assert.Contains("金额等式不成立", line.Reason);
        Assert.Equal(200m, line.AllocatedAmount);                        // 金额原样呈现，不重算
    }

    // ==================== 10. 无法确认证据：发票证据已删除、订单已删除、非正数金额 ====================

    [Fact]
    public async Task Detail_treats_deleted_invoice_and_non_positive_amount_as_non_active_evidence()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-10", CustomerA, Currency.USD, 1000m);
        var deleted = SeedInvoice(db, 956010L, "INV-IV-10", CustomerA, Currency.USD, 300m, 0m, 300m, deleted: true);
        var live = SeedInvoice(db, 956011L, "INV-IV-11", CustomerA, Currency.USD, 300m, 0m, 300m);
        SeedRow(db, 955010L, deleted, order, 300m);
        SeedRow(db, 955011L, live, order, 0m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.Equal(0m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(300m, detail.Summary.UnavailableInvoiceAllocatedAmount);
        Assert.Equal(0m, detail.Summary.InvalidInvoiceAllocatedAmount);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketUnavailable,
            detail.Lines.Single(l => l.AllocationId == 955010L).Bucket);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
            detail.Lines.Single(l => l.AllocationId == 955011L).Bucket);
        Assert.False(detail.Lines.Single(l => l.AllocationId == 955010L).InvoiceAvailable);
        Assert.Contains("不是正数", detail.Lines.Single(l => l.AllocationId == 955011L).Reason);
    }

    [Fact]
    public async Task Detail_treats_deleted_order_as_unavailable_evidence()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-11", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956012L, "INV-IV-12", CustomerA, Currency.USD, 300m, 0m, 300m);
        var row = SeedRow(db, 955012L, invoice, order, 300m);
        await db.SaveChangesAsync();

        // 订单随后被软删除：历史分摊保留可读，但证据无法按权威口径核对 → 无法确认
        order.IsDeleted = true;
        row.OrderNo = "SO-IV-11";
        await db.SaveChangesAsync();
        var batch = await SalesOrderInvoiceEvidence.ForOrdersAsync(db,
            new SalesOrderInvoiceEvidenceQuery { Ids = order.Id.ToString() });

        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.OrderStateUnavailable, batch.Items[0].OrderState);
        Assert.Null(batch.Items[0].OrderedAmount);
        Assert.Null(batch.Items[0].InvoiceUnreferencedOrderAmount);
        Assert.Equal(300m, batch.Items[0].UnavailableInvoiceAllocatedAmount);
        Assert.Equal(0m, batch.Items[0].RecordedInvoicedAmount);
    }

    // ==================== 11~12. 批量：多客户多币种不合并、保序与未知订单 ====================

    [Fact]
    public async Task Batch_keeps_multiple_customers_and_currencies_separate_without_merging()
    {
        using var db = TestDbFactory.Create();
        var orderA = SeedOrder(db, "SO-IV-12", CustomerA, Currency.USD, 1000m);
        var orderB = SeedOrder(db, "SO-IV-13", CustomerB, Currency.JPY, 2000m);
        var invoiceA = SeedInvoice(db, 956013L, "INV-IV-13", CustomerA, Currency.USD, 400m, 0m, 400m);
        var invoiceB = SeedInvoice(db, 956014L, "INV-IV-14", CustomerB, Currency.JPY, 900m, 0m, 900m);
        SeedRow(db, 955013L, invoiceA, orderA, 400m);
        SeedRow(db, 955014L, invoiceB, orderB, 900m);
        await db.SaveChangesAsync();

        var batch = await SalesOrderInvoiceEvidence.ForOrdersAsync(db, new SalesOrderInvoiceEvidenceQuery
        {
            Ids = $"{orderA.Id},{orderB.Id}"
        });

        Assert.Equal(2, batch.ItemCount);
        Assert.Equal(400m, batch.Items[0].RecordedInvoicedAmount);
        Assert.Equal("USD", batch.Items[0].OrderCurrency);
        Assert.Equal(2, batch.Items[0].AmountDecimals);
        Assert.Equal(900m, batch.Items[1].RecordedInvoicedAmount);
        Assert.Equal("JPY", batch.Items[1].OrderCurrency);
        Assert.Equal(0, batch.Items[1].AmountDecimals);                  // 无小数币种：精度口径复用 CurrencyAmountRules
        Assert.NotEqual(batch.Items[0].SalesOrderId, batch.Items[1].SalesOrderId);
    }

    [Fact]
    public async Task Batch_returns_summaries_in_request_order_and_marks_unknown_orders()
    {
        using var db = TestDbFactory.Create();
        var first = SeedOrder(db, "SO-IV-14", CustomerA, Currency.USD, 100m);
        var second = SeedOrder(db, "SO-IV-15", CustomerA, Currency.USD, 200m);
        var missing = SeedOrder(db, "SO-IV-16", CustomerA, Currency.USD, 300m, deleted: true);
        await db.SaveChangesAsync();

        var batch = await SalesOrderInvoiceEvidence.ForOrdersAsync(db, new SalesOrderInvoiceEvidenceQuery
        {
            Ids = $"{second.Id},{missing.Id},{first.Id},{second.Id}"
        });

        Assert.Equal(3, batch.RequestedCount);                          // 去重后
        Assert.Equal(3, batch.ItemCount);
        Assert.Equal(second.Id, batch.Items[0].SalesOrderId);
        Assert.Equal(missing.Id, batch.Items[1].SalesOrderId);
        Assert.Equal(first.Id, batch.Items[2].SalesOrderId);
        Assert.False(batch.Items[1].OrderAvailable);
        Assert.Null(batch.Items[1].OrderedAmount);                      // 订单不可用：金额未知（不按 0）
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceNone, batch.Items[1].InvoiceEvidenceStatus);
    }

    // ==================== 13~14. 有界读取：命中上限一律按「未知」，不给部分合计 ====================

    [Fact]
    public async Task Batch_reports_unknown_for_every_order_when_the_batch_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-17", CustomerA, Currency.USD, 100000m);
        var invoice = SeedInvoice(db, 956015L, "INV-IV-15", CustomerA, Currency.USD, 100000m, 0m, 100000m);
        for (var i = 0; i < SalesOrderInvoiceEvidenceSemantics.MaxBatchEvidenceRows + 1; i++)
            SeedRow(db, 961000L + i, invoice, order, 1m);
        await db.SaveChangesAsync();

        var batch = await SalesOrderInvoiceEvidence.ForOrdersAsync(db,
            new SalesOrderInvoiceEvidenceQuery { Ids = order.Id.ToString() });

        Assert.True(batch.Truncated);
        Assert.True(batch.Items[0].Truncated);
        Assert.Null(batch.Items[0].RecordedInvoicedAmount);
        Assert.Null(batch.Items[0].InvoiceAllocationCount);
        Assert.Null(batch.Items[0].InvalidInvoiceAllocatedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceUnknown, batch.Items[0].InvoiceEvidenceStatus);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelUnknown, batch.Items[0].InvoiceEvidenceLabel);
        Assert.Contains("未知", batch.Items[0].InvoiceEvidenceNote);
    }

    [Fact]
    public async Task Detail_reports_unknown_when_the_order_detail_row_cap_is_hit()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-18", CustomerA, Currency.USD, 100000m);
        var invoice = SeedInvoice(db, 956016L, "INV-IV-16", CustomerA, Currency.USD, 100000m, 0m, 100000m);
        for (var i = 0; i < SalesOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows + 1; i++)
            SeedRow(db, 962000L + i, invoice, order, 1m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);

        Assert.True(detail.Summary.Truncated);
        Assert.Null(detail.Summary.RecordedInvoicedAmount);
        Assert.Null(detail.Summary.RecordedInvoiceCount);
        Assert.Null(detail.Summary.DraftInvoiceAllocatedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows, detail.LineCount); // 明细只显示已读取部分
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceUnknown, detail.Summary.InvoiceEvidenceStatus);
    }

    // ==================== 15~16. 不存在 / 非法输入 / 空请求不访问数据集 ====================

    [Fact]
    public async Task Detail_throws_not_found_for_missing_or_deleted_orders_and_invalid_ids()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedOrder(db, "SO-IV-19", CustomerA, Currency.USD, 100m, deleted: true);
        await db.SaveChangesAsync();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderInvoiceEvidence.ForOrderAsync(db, 987654L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderInvoiceEvidence.ForOrderAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);

        var invalid = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderInvoiceEvidence.ForOrderAsync(db, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);
    }

    [Fact]
    public async Task Query_rejects_invalid_or_oversized_ids_and_empty_requests_touch_no_data()
    {
        using var db = TestDbFactory.Create();

        var invalid = Assert.Throws<BusinessException>(() => new SalesOrderInvoiceEvidenceQuery
        {
            Ids = "1,abc"
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var negative = Assert.Throws<BusinessException>(() => new SalesOrderInvoiceEvidenceQuery
        {
            Ids = "-3"
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, negative.Code);

        var oversized = Assert.Throws<BusinessException>(() => new SalesOrderInvoiceEvidenceQuery
        {
            Ids = string.Join(',', Enumerable.Range(1, SalesOrderInvoiceEvidenceSemantics.MaxBatchOrders + 1))
        }.Normalize());
        Assert.Equal(ErrorCodes.InvalidParameter, oversized.Code);

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var empty = await SalesOrderInvoiceEvidence.ForOrdersAsync(counting.Proxy,
            new SalesOrderInvoiceEvidenceQuery { Ids = "  " });
        Assert.Equal(0, empty.RequestedCount);
        Assert.Empty(empty.Items);
        Assert.Equal(0, counting.DatasetReads);                       // 空请求不访问任何数据集
        Assert.Equal(0, counting.WriteCalls);
    }

    [Fact]
    public async Task Engine_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-20", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956017L, "INV-IV-17", CustomerA, Currency.USD, 900m, 0m, 900m);
        SeedRow(db, 955017L, invoice, order, 400m);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(counting.Proxy, order.Id);

        // 固定 4 次数据集访问：订单 + 持久化分摊行 + 发票证据 + 发票侧分摊合计（与行数无关，无逐行查库）
        Assert.Equal(4, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);                         // 只读派生：没有一次 SaveChanges
        Assert.Equal(400m, detail.Summary.RecordedInvoicedAmount);
    }

    // ==================== 17. ERP-055 登记链路一致性 + 非变更边界 ====================

    [Fact]
    public async Task Evidence_follows_the_erp_055_register_chain_and_never_mutates_sources()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-IV-21", CustomerA, Currency.USD, 1000m);
        db.BaseTaxRefunds.Add(new BaseTaxRefund
        {
            RefundNo = "TS-IV-1", SalesOrderNo = "SO-IV-21", RefundableAmount = 123m, Status = "待申报"
        });
        await db.SaveChangesAsync();

        // ERP-055 服务端登记：新建草稿 → 分摊到本订单 → 登记（草稿不算有效证据）
        var register = new CustomerSalesInvoiceEvidenceController(db);
        var created = AssertOk<CustomerSalesInvoiceEvidenceDto>(await register.Create(
            new CustomerSalesInvoiceEvidenceSaveDto
            {
                InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
                InvoiceNumber = "INV-IV-21",
                InvoiceDate = AsOf,
                CustomerId = CustomerIdOf(CustomerA),
                Currency = "USD",
                NetAmount = 1000m,
                TaxAmount = 0m,
                GrossAmount = 1000m,
                Remark = "ERP-056 链路"
            }));

        // 新建草稿但尚未分摊：还没有任何分摊行 → 证据缺口（不是未开票）
        var beforeAllocation = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(0m, beforeAllocation.Summary.RecordedInvoicedAmount);
        Assert.Equal(0, beforeAllocation.Summary.InvoiceAllocationCount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNone, beforeAllocation.Summary.InvoiceEvidenceLabel);

        AssertOk<CustomerSalesInvoiceEvidenceDto>(await register.SaveAllocations(created.Id,
            new CustomerSalesInvoiceAllocationSaveRequest
            {
                Lines = new List<CustomerSalesInvoiceAllocationSaveDto>
                {
                    new() { SalesOrderId = order.Id, AllocatedAmount = 250m, Remark = "首期" }
                }
            }));

        // 仍是草稿：分摊只是工作数据，不计入有效合计
        var draft = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(0m, draft.Summary.RecordedInvoicedAmount);
        Assert.Equal(1, draft.Summary.DraftInvoiceAllocationCount);
        Assert.Equal(250m, draft.Summary.DraftInvoiceAllocatedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNonActiveOnly, draft.Summary.InvoiceEvidenceLabel);

        AssertOk<CustomerSalesInvoiceEvidenceDto>(await register.Record(created.Id));

        var afterRecord = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(250m, afterRecord.Summary.RecordedInvoicedAmount);
        Assert.Equal(1, afterRecord.Summary.RecordedInvoiceCount);
        Assert.Equal(1000m, afterRecord.Summary.RecordedInvoiceGrossAmount);
        Assert.Equal(750m, afterRecord.Summary.UnreferencedInvoiceAmount);
        Assert.Equal(750m, afterRecord.Summary.InvoiceUnreferencedOrderAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BucketRecorded, Assert.Single(afterRecord.Lines).Bucket);

        // 作废后：有效合计归零，历史证据保留可读（金额与原因均可见）
        AssertOk<CustomerSalesInvoiceEvidenceDto>(await register.Void(created.Id,
            new CustomerSalesInvoiceVoidRequest { Reason = "发票录错，重新登记" }));

        var afterVoid = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(0m, afterVoid.Summary.RecordedInvoicedAmount);
        Assert.Equal(250m, afterVoid.Summary.VoidedInvoiceAllocatedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNonActiveOnly, afterVoid.Summary.InvoiceEvidenceLabel);
        Assert.Equal("发票录错，重新登记", Assert.Single(afterVoid.Lines).VoidReason);

        // 非变更边界：订单 / 发票证据 / 分摊行 / 客户 / 退税记录都不被本视图改写
        var orderAfter = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.Equal(1000m, orderAfter.TotalAmount);
        Assert.Equal(Currency.USD, orderAfter.Currency);

        var invoiceAfter = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .FirstAsync(i => i.Id == created.Id);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided, invoiceAfter.Status);
        Assert.Equal(1000m, invoiceAfter.GrossAmount);
        Assert.Equal(0m, invoiceAfter.TaxAmount);
        Assert.False(invoiceAfter.IsDeleted);

        var rowAfter = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .FirstAsync(a => a.CustomerSalesInvoiceEvidenceId == created.Id);
        Assert.Equal(250m, rowAfter.AllocatedAmount);
        Assert.Equal(CustomerIdOf(CustomerA), rowAfter.CustomerId);
        Assert.Equal("USD", rowAfter.Currency);
        Assert.False(rowAfter.IsDeleted);

        var customerAfter = await db.BaseCustomers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.Equal(100000m, customerAfter.CreditLimit);
        Assert.Equal("正常", customerAfter.CreditStatus);

        Assert.Equal(123m, (await db.BaseTaxRefunds.AsNoTracking()
            .FirstAsync(t => t.RefundNo == "TS-IV-1")).RefundableAmount);
    }

    // ==================== 18. ERP-046 报表：销项发票证据作为独立标注的证据类别 ====================

    [Fact]
    public async Task Reconciliation_report_exposes_invoice_evidence_as_a_labelled_class()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-IV-22", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956018L, "INV-IV-18", CustomerA, Currency.USD, 600m, 0m, 600m);
        SeedRow(db, 955018L, invoice, order, 400m);
        await db.SaveChangesAsync();

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            new SalesOrderReceiptReconciliationQuery());

        Assert.Equal(1, report.InvoiceEvidenceOrderCount);
        Assert.Equal(0, report.HistoricalOnlyInvoiceEvidenceOrderCount);
        Assert.Equal(0, report.NoInvoiceEvidenceOrderCount);
        Assert.Equal(0, report.UnknownInvoiceEvidenceOrderCount);
        // 报表刻意不提供跨币种的页级销项发票金额合计：金额只在分组（客户 + 币种）与订单行上给出
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.RuleText, report.InvoiceEvidenceRule);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BoundaryText, report.InvoiceEvidenceBoundary);

        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(1000m, row.OrderAmount);                                  // 第一类：订单金额
        Assert.Null(row.LinkedReceiptAmount);                                  // 第二类：收款申请链接无权威引用 → 未知（不是 0）
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceRecorded, row.InvoiceEvidenceStatus);
        Assert.Equal(400m, row.RecordedInvoicedAmount);                        // 第四类：销项发票登记证据
        Assert.Equal(600m, row.RecordedInvoiceGrossAmount);
        Assert.Equal(200m, row.UnreferencedInvoiceAmount);
        Assert.Equal(600m, row.InvoiceUnreferencedOrderAmount);
        Assert.Equal(1, row.RecordedInvoiceCount);
        Assert.Equal(1, row.InvoiceAllocationCount);
        Assert.Contains("有效已分摊 400", row.InvoiceEvidenceNote);
        Assert.Contains("绝不相加", row.InvoiceEvidenceNote);

        var group = Assert.Single(report.Groups);
        Assert.Equal(1, group.InvoiceEvidenceOrderCount);
        Assert.Equal(400m, group.RecordedInvoicedAmount);
        Assert.Contains("销项发票登记证据", group.Note);
        Assert.Contains("ERP-056", report.Rule);
    }

    [Fact]
    public async Task Reconciliation_report_shows_no_evidence_as_a_gap_and_respects_the_aggregate_cap()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-23", CustomerA, Currency.USD, 500m);
        await db.SaveChangesAsync();

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            new SalesOrderReceiptReconciliationQuery());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);

        Assert.Equal(0, report.InvoiceEvidenceOrderCount);
        Assert.Equal(0, report.HistoricalOnlyInvoiceEvidenceOrderCount);
        Assert.Equal(1, report.NoInvoiceEvidenceOrderCount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceNone, row.InvoiceEvidenceStatus);
        Assert.Contains("不代表未开票", row.InvoiceEvidenceNote);              // 证据缺口，不是未开票 / 欠税

        // 超出有界聚合上限：一律按未知，不给部分合计
        var capped = await SalesOrderInvoiceEvidence.AggregatesForOrdersAsync(db,
            new List<long> { order.Id, order.Id + 1 }, orderCap: 1);
        Assert.True(capped.Truncated);
        Assert.Empty(capped.ByOrder);
        Assert.Equal(0m, capped.Get(order.Id).RecordedAmount);
    }

    // ==================== 19. 接口与前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public async Task Controller_endpoints_return_invoice_evidence_payloads()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-24", CustomerA, Currency.USD, 1000m);
        var invoice = SeedInvoice(db, 956019L, "INV-IV-19", CustomerA, Currency.USD, 600m, 0m, 600m);
        SeedRow(db, 955019L, invoice, order, 350m);
        await db.SaveChangesAsync();

        var controller = new SalesOrderController(db, new DocumentNumberService(db));

        var detail = AssertOk<SalesOrderInvoiceEvidenceDetail>(await controller.InvoiceEvidence(order.Id));
        Assert.Equal(350m, detail.Summary.RecordedInvoicedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.BoundaryText, detail.Boundary);
        Assert.Single(detail.Lines);

        var batch = AssertOk<SalesOrderInvoiceEvidenceBatch>(
            await controller.InvoiceEvidenceSummaries(new SalesOrderInvoiceEvidenceQuery { Ids = order.Id.ToString() }));
        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(350m, batch.Items[0].RecordedInvoicedAmount);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.RuleText, batch.Rule);

        var empty = AssertOk<SalesOrderInvoiceEvidenceBatch>(
            await controller.InvoiceEvidenceSummaries(new SalesOrderInvoiceEvidenceQuery()));
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.RequestedCount);
    }

    [Fact]
    public void Frontend_wiring_exposes_invoice_evidence_column_and_detail_action_without_per_row_requests()
    {
        var js = JsDirectory();

        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/sales-order-invoice-evidence.js", index);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("render: row => salesOrderInvoiceEvidenceCellHtml(row)", modulesDoc);
        Assert.Contains("showSalesOrderInvoiceEvidence", modulesDoc);
        Assert.Contains("销项发票证据", modulesDoc);

        var script = File.ReadAllText(Path.Combine(js, "sales-order-invoice-evidence.js"));
        Assert.Contains("'/api/sales-orders/invoice-evidence-summaries'", script);
        Assert.Contains("const SOIE_MAX_BATCH = 200", script);
        Assert.Contains("function salesOrderInvoiceEvidenceCellHtml", script);
        Assert.Contains("function showSalesOrderInvoiceEvidence", script);
        Assert.Contains("function soieBadgeHtml", script);
        Assert.Contains("i += SOIE_MAX_BATCH", script);                 // 按页分批，绝不逐行请求
        Assert.Contains("'未知'", script);                             // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("truncated", script);                          // 命中后端有界上限时显示未知
        Assert.Contains("无销项发票证据", script);
        Assert.Contains("不是应收余额", script);                        // 页面显式声明不是开票 / 税金 / 应收 / 核销 / 结算
        Assert.Contains("CURRENT_MODULE_CODE !== 'sales-order'", script);

        // ERP-046 报表页面必须把销项发票证据作为独立证据列展示（不并入订单金额、已关联收款或收款引用证据）
        var report = File.ReadAllText(Path.Combine(js, "sales-order-receipt-reconciliation.js"));
        Assert.Contains("invoiceEvidenceStatus", report);
        Assert.Contains("recordedInvoicedAmount", report);
        Assert.Contains("data.invoiceEvidenceRule", report);
        Assert.Contains("data.invoiceEvidenceBoundary", report);
        Assert.Contains("发票证据外订单金额", report);
    }

    // ==================== 20. 非开票系统 / 税务申报 / 应收台账 / 账龄：字段、路由与文案边界 ====================

    [Fact]
    public void Evidence_exposes_no_receivable_ledger_tax_liability_or_aging_fields_and_states_the_boundary()
    {
        foreach (var type in new[]
                 {
                     typeof(SalesOrderInvoiceEvidenceSummary), typeof(SalesOrderInvoiceEvidenceLine),
                     typeof(SalesOrderInvoiceEvidenceDetail), typeof(SalesOrderInvoiceEvidenceBatch),
                     typeof(SalesOrderReceiptReconciliationOrderRow),
                     typeof(SalesOrderReceiptReconciliationGroup),
                     typeof(SalesOrderReceiptReconciliationReport),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Overdue", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Receivable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Statement", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("TaxLiability", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Settled", StringComparison.Ordinal));
            // 说明：「OutstandingQuantity」是 ERP-032 既有的出货数量字段（未出数量），不是结算状态，故不列入黑名单
            Assert.DoesNotContain(names, n => n.Contains("Unpaid", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Balance", StringComparison.Ordinal));
        }

        // ERP-046 报表行确实把销项发票证据作为独立证据类别暴露（允许 Invoice* 字段，但仍无应收 / 账龄 / 结算字段）
        var rowNames = typeof(SalesOrderReceiptReconciliationOrderRow)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("InvoiceEvidenceStatus", rowNames);
        Assert.Contains("RecordedInvoicedAmount", rowNames);
        Assert.Contains("InvoiceUnreferencedOrderAmount", rowNames);

        Assert.Contains("不是发票开具系统", SalesOrderInvoiceEvidenceSemantics.BoundaryText);
        Assert.Contains("不是税务申报与销项税金计算", SalesOrderInvoiceEvidenceSemantics.BoundaryText);
        Assert.Contains("不是应收账款台账或应收余额", SalesOrderInvoiceEvidenceSemantics.BoundaryText);
        Assert.Contains("也不产生任何记账、凭证、开票或收付款记录", SalesOrderInvoiceEvidenceSemantics.BoundaryText);
        Assert.Contains("不判断是否已开票", SalesOrderInvoiceEvidenceSemantics.BoundaryText);
        Assert.Contains("绝不推断为未开票", SalesOrderInvoiceEvidenceSemantics.RuleText);
        Assert.Contains("不得当作应收余额", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);

        // 只读：销项发票证据只有 GET 路由，没有任何创建 / 修改 / 作废 / 删除入口
        var getRoutes = typeof(SalesOrderController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("{id:long}/invoice-evidence", getRoutes);
        Assert.Contains("invoice-evidence-summaries", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "{id:long}/invoice-evidence"));
        Assert.Single(getRoutes.Where(r => r == "invoice-evidence-summaries"));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("invoice-evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("invoice-evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("invoice-evidence", StringComparison.Ordinal));
    }

    // ==================== 21. 无分摊行的既有订单仍可读，且绝不回填 ====================

    [Fact]
    public async Task Legacy_orders_without_allocation_rows_remain_readable_without_backfill()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-IV-25", CustomerA, Currency.USD, 750m);
        SeedInvoice(db, 956020L, "INV-IV-20", CustomerA, Currency.USD, 750m, 0m, 750m);
        await db.SaveChangesAsync();

        var detail = await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id);
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.LabelNone, detail.Summary.InvoiceEvidenceLabel);
        Assert.Empty(detail.Lines);

        var batch = await SalesOrderInvoiceEvidence.ForOrdersAsync(db,
            new SalesOrderInvoiceEvidenceQuery { Ids = order.Id.ToString() });
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceNone, batch.Items[0].InvoiceEvidenceStatus);

        // 只读派生绝不回填任何分摊行（历史数据保持原样）
        Assert.Empty(await db.CustomerSalesInvoiceAllocations.AsNoTracking().ToListAsync());
        Assert.Equal(1, await db.CustomerSalesInvoiceEvidences.AsNoTracking().CountAsync());
    }

    // ==================== 22. 四类证据并存：订单 / 出货数量、收款申请链接、收款引用证据、销项发票证据分列 ====================

    [Fact]
    public async Task Report_keeps_order_shipment_receipt_and_invoice_evidence_as_separate_fields()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-IV-26", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, 956101L, 10m);
        SeedStockOut(db, "CK-IV-1", order.Id, DocumentStatus.Approved, 4m);

        // 第二类：收款引用登记证据（ERP-053 持久化引用行）
        var receipt = SeedReceipt(db, "SK-IV-1", CustomerA, 600m, Currency.USD);
        SeedReceiptAllocation(db, 957001L, receipt, order, 400m);

        // 第三类：销项发票登记证据（ERP-055 持久化发票证据行 + 分摊行）
        var invoice = SeedInvoice(db, 956021L, "INV-IV-21", CustomerA, Currency.USD, 600m, 0m, 600m);
        SeedRow(db, 955021L, invoice, order, 400m);
        await db.SaveChangesAsync();

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            new SalesOrderReceiptReconciliationQuery());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);

        // 第一类：订单金额与已订 / 已出数量（ERP-032 权威派生：已审核出库单）
        Assert.Equal(1000m, row.OrderAmount);
        Assert.Equal(10m, row.OrderedQuantity);
        Assert.Equal(4m, row.ShippedQuantity);
        Assert.Equal(6m, row.OutstandingQuantity);
        Assert.True(row.HasApprovedShipment);

        // 第二类：收款申请链接（本单没有定金 / 货款申请 ⇒ 未知，绝不当作 0 或已收）
        Assert.Null(row.LinkedReceiptAmount);

        // 第三类：收款引用登记证据（独立证据，与上两类分开标注）
        Assert.Equal(SalesOrderReceiptEvidenceSemantics.AllocationRecorded, row.ReceiptAllocationStatus);
        Assert.Equal(400m, row.RecordedReceiptAllocationAmount);

        // 第四类：销项发票登记证据（独立证据，与上面三类分开标注）
        Assert.Equal(SalesOrderInvoiceEvidenceSemantics.EvidenceRecorded, row.InvoiceEvidenceStatus);
        Assert.Equal(400m, row.RecordedInvoicedAmount);
        Assert.Equal(600m, row.RecordedInvoiceGrossAmount);
        Assert.Equal(600m, row.InvoiceUnreferencedOrderAmount);

        // 四类证据绝不相互冲抵、绝不相加成一个应收余额：报表只给分列字段与口径说明
        Assert.NotEqual(row.OrderAmount, row.RecordedReceiptAllocationAmount + row.RecordedInvoicedAmount);
        Assert.Contains("绝不相加", row.ReceiptAllocationNote);
        Assert.Contains("绝不相加", row.InvoiceEvidenceNote);
        Assert.Contains("绝不相加", report.Rule);
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

    /// <summary>客户 Id 固定映射（与订单 / 发票证据 / 分摊行快照共用同一套 Id，避免测试内自相矛盾）</summary>
    private static long CustomerIdOf(string customerCode) => customerCode switch
    {
        CustomerA => 956001L,
        _ => 956002L,
    };

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            Id = CustomerIdOf(code),
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

    /// <summary>
    /// 直接落库一张 ERP-055 客户销项发票证据（用于构造有效 / 草稿 / 作废 / 已删除 / 金额等式漂移等证据，
    /// 不走登记接口）。
    /// </summary>
    private static CustomerSalesInvoiceEvidence SeedInvoice(ErpDbContext db, long id, string invoiceNumber,
        string customerCode, Currency currency, decimal netAmount, decimal taxAmount, decimal grossAmount,
        int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded, bool deleted = false,
        string voidReason = "")
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            Id = id,
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(invoiceNumber),
            InvoiceDate = new DateTime(2026, 9, 12),
            CustomerId = CustomerIdOf(customerCode),
            CustomerCode = "C056-SNAP",
            CustomerName = "客户快照",
            Currency = currency.ToString(),
            NetAmount = netAmount,
            TaxAmount = taxAmount,
            GrossAmount = grossAmount,
            Status = status,
            RecordedAt = status == CustomerSalesInvoiceEvidenceRules.StatusRecorded ? AsOf : null,
            VoidedAt = status == CustomerSalesInvoiceEvidenceRules.StatusVoided ? AsOf.AddDays(1) : null,
            VoidReason = voidReason,
            IsDeleted = deleted
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        return invoice;
    }

    /// <summary>
    /// 直接落库一条 ERP-055 发票 → 销售订单 分摊行（用于构造历史漂移 / 草稿 / 已作废 / 无法确认等已存在证据，
    /// 不走登记接口）。
    /// </summary>
    private static CustomerSalesInvoiceAllocation SeedRow(ErpDbContext db, long id,
        CustomerSalesInvoiceEvidence? invoice, SalesOrder? order, decimal amount,
        long? customerId = null, string? currency = null, string? orderCurrency = null, bool deleted = false)
    {
        var currencyText = currency ?? invoice?.Currency ?? CurrencyAmountRules.DefaultCurrency;
        var row = new CustomerSalesInvoiceAllocation
        {
            Id = id,
            CustomerSalesInvoiceEvidenceId = invoice?.Id ?? 0,
            SalesOrderId = order?.Id ?? 0,
            OrderNo = order?.OrderNo ?? string.Empty,
            OrderDate = order?.OrderDate ?? new DateTime(2026, 9, 1),
            OrderStatus = (int)(order?.Status ?? DocumentStatus.Approved),
            OrderCurrency = orderCurrency ?? currencyText,
            CustomerId = customerId ?? invoice?.CustomerId ?? 0,
            CustomerCode = "C056-SNAP",
            CustomerName = "客户快照",
            AllocatedAmount = amount,
            Currency = currencyText,
            CreatedAt = AsOf,
            IsDeleted = deleted
        };
        db.CustomerSalesInvoiceAllocations.Add(row);
        return row;
    }

    /// <summary>销售订单明细（ERP-032 已订数量来源）</summary>
    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m,
        });
        db.SaveChanges();
    }

    /// <summary>已审核销售出库单（ERP-032 已出数量来源）</summary>
    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long salesOrderId,
        DocumentStatus status, decimal shippedQuantity)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerIdOf(CustomerA),
            WarehouseId = 1,
            Status = status,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        db.StockOutDetails.Add(new StockOutDetail
        {
            StockOutId = stockOut.Id,
            ProductId = 956101L,
            ProductName = "商品956101",
            Unit = "PCS",
            Quantity = shippedQuantity,
        });
        db.SaveChanges();
        return stockOut;
    }

    /// <summary>既有客户收款单（ERP-053 收款引用证据的来源）</summary>
    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, string customerCode,
        decimal amount, Currency currency)
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
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    /// <summary>直接落库一条 ERP-053 收款引用行（用于验证四类证据并存时分列标注）</summary>
    private static CustomerReceiptAllocation SeedReceiptAllocation(ErpDbContext db, long id,
        FinanceReceipt receipt, SalesOrder order, decimal amount)
    {
        var row = new CustomerReceiptAllocation
        {
            Id = id,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = string.Empty,
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = receipt.CustomerId,
            CustomerCode = "C056-SNAP",
            CustomerName = "客户快照",
            AllocatedAmount = amount,
            Currency = receipt.Currency.ToString(),
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = AsOf,
        };
        db.CustomerReceiptAllocations.Add(row);
        return row;
    }
}










