using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-068 供应商对账与账龄工作台（只读派生）单元测试：显式与缺失到期日、五个账龄桶的全部边界、
/// 部分与整笔分配、已作废历史与无效 / 无法确认链接、多供应商多币种（绝不跨币种合并）、as-of 过滤、
/// 有界读取与只读不写库、明细的身份与既有「角色 → 菜单」授权 fail closed、接口与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SupplierReconciliationAgingTests
{
    /// <summary>账龄基准日（显式 as-of；账龄只相对它计算）</summary>
    private static readonly DateTime AsOf = new(2026, 9, 24);

    private const long SupplierA = 968001L;
    private const long SupplierB = 968002L;
    private const long AuthorizedUserId = 9801L;
    private const long NoMenuUserId = 9802L;
    private const long WrongMenuUserId = 9803L;

    // ==================== 1. 账龄分桶：五个桶互斥且完整覆盖全部边界 ====================

    [Fact]
    public async Task Aging_buckets_cover_every_boundary_and_never_overlap()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");

        // 未到期：到期日 = as-of（恰好到期）与 as-of 之后
        SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf);
        SeedInvoice(db, 968102L, SupplierA, "CNY", "A002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));

        // 逾期 1 ~ 30 天（含边界 1 与 30）
        SeedInvoice(db, 968103L, SupplierA, "CNY", "A003", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-41), dueDate: AsOf.AddDays(-1));
        SeedInvoice(db, 968104L, SupplierA, "CNY", "A004", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-41), dueDate: AsOf.AddDays(-30));

        // 逾期 31 ~ 60 天（含边界 31 与 60）
        SeedInvoice(db, 968105L, SupplierA, "CNY", "A005", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-61), dueDate: AsOf.AddDays(-31));
        SeedInvoice(db, 968106L, SupplierA, "CNY", "A006", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-61), dueDate: AsOf.AddDays(-60));

        // 逾期 61 ~ 90 天（含边界 61 与 90）
        SeedInvoice(db, 968107L, SupplierA, "CNY", "A007", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-91), dueDate: AsOf.AddDays(-61));
        SeedInvoice(db, 968108L, SupplierA, "CNY", "A008", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-91), dueDate: AsOf.AddDays(-90));

        // 逾期 90 天以上（91 天）
        SeedInvoice(db, 968109L, SupplierA, "CNY", "A009", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-120), dueDate: AsOf.AddDays(-91));
        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query());
        var rows = report.Groups.SelectMany(g => g.Invoices).ToList();

        Assert.Equal(9, rows.Count);
        Assert.Equal(9, report.KnownDueDatePageInvoiceCount);
        Assert.Equal(0, report.UnknownDueDatePageInvoiceCount);

        Assert.Equal(SupplierReconciliationAgingSemantics.BucketNotDue, Bucket(rows, "A001"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketNotDue, Bucket(rows, "A002"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue1To30, Bucket(rows, "A003"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue1To30, Bucket(rows, "A004"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue31To60, Bucket(rows, "A005"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue31To60, Bucket(rows, "A006"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue61To90, Bucket(rows, "A007"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue61To90, Bucket(rows, "A008"));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdueOver90, Bucket(rows, "A009"));

        // 逾期天数 = as-of − 到期日（未到期为负 / 0，逐张可复算）
        Assert.Equal(0, Row(rows, "A001").OverdueDays);
        Assert.Equal(-5, Row(rows, "A002").OverdueDays);
        Assert.Equal(1, Row(rows, "A003").OverdueDays);
        Assert.Equal(30, Row(rows, "A004").OverdueDays);
        Assert.Equal(31, Row(rows, "A005").OverdueDays);
        Assert.Equal(60, Row(rows, "A006").OverdueDays);
        Assert.Equal(90, Row(rows, "A008").OverdueDays);
        Assert.Equal(91, Row(rows, "A009").OverdueDays);

        // 分桶合计互斥且完整：合计 = 全部 900（无重复计入、无遗漏）
        var group = Assert.Single(report.Groups);
        var buckets = group.Buckets.ToDictionary(b => b.Bucket);
        Assert.Equal(5, group.Buckets.Count);
        Assert.Equal(2, buckets[SupplierReconciliationAgingSemantics.BucketNotDue].InvoiceCount);
        Assert.Equal(2, buckets[SupplierReconciliationAgingSemantics.BucketOverdue1To30].InvoiceCount);
        Assert.Equal(2, buckets[SupplierReconciliationAgingSemantics.BucketOverdue31To60].InvoiceCount);
        Assert.Equal(2, buckets[SupplierReconciliationAgingSemantics.BucketOverdue61To90].InvoiceCount);
        Assert.Equal(1, buckets[SupplierReconciliationAgingSemantics.BucketOverdueOver90].InvoiceCount);
        Assert.Equal(900m, group.Buckets.Sum(b => b.GrossAmount));
        Assert.Equal(200m, buckets[SupplierReconciliationAgingSemantics.BucketOverdue1To30].GrossAmount);
        Assert.Equal(900m, group.GrossAmount);                              // 9 张 × 100：只统计有效证据发票

        // 「未知到期日」是独立分组、不是账龄桶
        Assert.Equal(0, group.UnknownDueDate.InvoiceCount);
        Assert.False(group.UnknownDueDate.IsAgingBucket);
        Assert.True(buckets[SupplierReconciliationAgingSemantics.BucketNotDue].IsAgingBucket);
    }

    // ==================== 2. 未知到期日：独立分组、不计算账龄、不可被到期日筛选命中 ====================

    [Fact]
    public async Task Invoices_without_due_date_are_grouped_separately_and_never_aged()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var dated = SeedInvoice(db, 968201L, SupplierA, "CNY", "B001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(-40),
            paymentTerms: "月结 30 天");
        SeedInvoice(db, 968202L, SupplierA, "CNY", "B002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), paymentTerms: "月结 30 天");
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 968921L, dated, 30m);

        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query());
        var rows = report.Groups.SelectMany(g => g.Invoices).ToList();

        var unknown = Row(rows, "B002");
        Assert.False(unknown.DueDateKnown);
        Assert.Null(unknown.DueDate);
        Assert.Null(unknown.AgingBucket);
        Assert.Null(unknown.OverdueDays);
        Assert.Contains("不计算账龄", unknown.AgingText);
        Assert.Contains("未知到期日", unknown.AgingBucketText);
        Assert.Contains("不计算账龄", unknown.AgingBucketText);
        Assert.Contains("绝不按开票日期", unknown.Note);

        // 账龄桶只统计有显式到期日的发票：B001 在 31~60 桶，B002 一张都不进
        var group = Assert.Single(report.Groups);
        Assert.Equal(1, group.KnownDueDateInvoiceCount);
        Assert.Equal(1, group.UnknownDueDateInvoiceCount);
        Assert.Equal(1, group.Buckets.Sum(b => b.InvoiceCount));
        Assert.Equal(100m, group.Buckets.Sum(b => b.GrossAmount));

        // 未知到期日独立分组（按供应商 + 币种），金额仍按原币、且不被推算
        var unknownGroup = Assert.Single(report.UnknownDueDateGroups);
        Assert.Equal(SupplierA, unknownGroup.SupplierId);
        Assert.Equal("CNY", unknownGroup.Currency);
        Assert.Equal(1, unknownGroup.InvoiceCount);
        Assert.Equal(200m, unknownGroup.GrossAmount);
        Assert.Contains("B002", Assert.Single(unknownGroup.InvoiceIdentities));
        Assert.Contains("不计算", unknownGroup.Note);
        Assert.Equal(1, group.UnknownDueDate.InvoiceCount);
        Assert.Equal(200m, group.UnknownDueDate.GrossAmount);
        Assert.False(group.UnknownDueDate.IsAgingBucket);
        Assert.Equal(1, report.UnknownDueDatePageInvoiceCount);

        // 到期日区间筛选只命中「登记了显式到期日」的发票：未知到期日的发票既不落区间内也不落区间外
        var datedOnly = await SupplierReconciliationAging.ForQueryAsync(db,
            Query(dueDateFrom: AsOf.AddDays(-100), dueDateTo: AsOf));
        Assert.Equal("B001", Assert.Single(datedOnly.Groups.SelectMany(g => g.Invoices)).InvoiceNumber);

        // 且系统绝不按开票日期把 B002 补进账龄：只按开票日期筛时它仍在「未知到期日」分组
        var byInvoiceDate = await SupplierReconciliationAging.ForQueryAsync(db,
            Query(invoiceDateFrom: AsOf.AddDays(-50), invoiceDateTo: AsOf));
        Assert.Equal(2, byInvoiceDate.PageInvoiceCount);
        Assert.Equal(1, byInvoiceDate.UnknownDueDatePageInvoiceCount);
        Assert.Equal(1, byInvoiceDate.Groups.Sum(g => g.Buckets.Sum(b => b.InvoiceCount)));
    }

    // ==================== 3. 剩余证据 = 含税总额 − 有效已分配（部分 / 整笔 / 缺口） ====================

    [Fact]
    public async Task Remaining_evidence_is_gross_minus_active_allocations()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var partial = SeedInvoice(db, 968301L, SupplierA, "CNY", "C001", 100m, 13m, 113m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-20), dueDate: AsOf.AddDays(-20));
        var full = SeedInvoice(db, 968302L, SupplierA, "CNY", "C002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-20), dueDate: AsOf.AddDays(-20));
        SeedInvoice(db, 968303L, SupplierA, "CNY", "C003", 50m, 6.5m, 56.5m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-20), dueDate: AsOf.AddDays(-20));
        await db.SaveChangesAsync();

        SeedInvoicePaymentEvidence(db, 968931L, partial, 40m, paymentAmount: 200m);
        SeedInvoicePaymentEvidence(db, 968932L, partial, 23m, paymentAmount: 200m);
        SeedInvoicePaymentEvidence(db, 968933L, full, 100m);
        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query());
        var rows = report.Groups.SelectMany(g => g.Invoices).ToList();

        var p = Row(rows, "C001");
        Assert.Equal(113m, p.GrossAmount);
        Assert.Equal(63m, p.ActiveAllocatedAmount);
        Assert.Equal(2, p.ActiveAllocationCount);
        Assert.Equal(2, p.ActivePaymentCount);
        Assert.Equal(50m, p.RemainingAmount);                       // 113 − 63
        Assert.Equal(SupplierReconciliationAgingSemantics.RemainingKnown, p.RemainingState);
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationPartial, p.AllocationState);
        Assert.Contains("不是应付余额", p.Note);
        Assert.Equal(113m, p.NetAmount + p.TaxAmount);               // 金额等式证据（价税合计）

        var f = Row(rows, "C002");
        Assert.Equal(100m, f.ActiveAllocatedAmount);
        Assert.Equal(0m, f.RemainingAmount);                        // 整笔分配：剩余 0 = 算术结果，但绝不表示已结清
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationFull, f.AllocationState);
        Assert.Contains("不代表已结清", f.Note);

        // 没有任何持久化引用行 = 明确的证据缺口（不是「未知」，也不是未付款 / 已付款）
        var n = Row(rows, "C003");
        Assert.Equal(0m, n.ActiveAllocatedAmount);
        Assert.Equal(0, n.ActiveAllocationCount);
        Assert.Equal(56.5m, n.RemainingAmount);
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationNone, n.AllocationState);
        Assert.Contains("证据缺口", n.Note);
        Assert.False(n.HasAllocationHistory);
        Assert.Contains("无历史", n.HistoricalEvidenceText);

        // 分组汇总同样按「含税总额 / 有效已分配 / 算术剩余」三类分列（原币），且不含无效证据
        var group = Assert.Single(report.Groups);
        Assert.Equal(269.5m, group.GrossAmount);                    // 113 + 100 + 56.5
        Assert.Equal(163m, group.ActiveAllocatedAmount);            // 63 + 100 + 0
        Assert.Equal(106.5m, group.RemainingAmount);                // 50 + 0 + 56.5
        Assert.Equal(1, group.NoAllocationInvoiceCount);
        Assert.Equal(0, group.OverAllocatedInvoiceCount);
        Assert.Equal(0, group.InvalidEvidenceInvoiceCount);
    }

    // ==================== 4. 已作废引用行：历史可见、绝不并入有效合计 ====================

    [Fact]
    public async Task Voided_allocations_stay_in_history_but_never_join_active_totals()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var invoice = SeedInvoice(db, 968401L, SupplierA, "CNY", "D001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-10));
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 968941L, invoice, 60m,
            status: SupplierPaymentInvoiceAllocationRules.StatusVoided);
        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query());
        var row = Assert.Single(report.Groups.SelectMany(g => g.Invoices));

        Assert.Equal(0m, row.ActiveAllocatedAmount);
        Assert.Equal(0, row.ActiveAllocationCount);
        Assert.Equal(100m, row.RemainingAmount);
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationHistoricalOnly, row.AllocationState);
        Assert.True(row.HasAllocationHistory);
        Assert.Equal(1, row.VoidedAllocationCount);
        Assert.Equal(60m, row.VoidedAllocationAmount);
        Assert.DoesNotContain("作废重登", row.HistoricalEvidenceText);   // 作废原因不在此口径内，仅金额与条数
        Assert.Contains("已作废引用行 1 条", row.HistoricalEvidenceText);
        Assert.Contains("绝不并入有效合计", row.HistoricalEvidenceText);

        // 有效合计只有「含税总额 100 / 有效已分配 0 / 剩余 100」：已作废 60 绝不并入
        var group = Assert.Single(report.Groups);
        Assert.Equal(100m, group.GrossAmount);
        Assert.Equal(0m, group.ActiveAllocatedAmount);
        Assert.Equal(100m, group.RemainingAmount);
        Assert.Equal(1, group.HistoricalOnlyInvoiceCount);

        // 分配状态筛选按持久化引用行判定：historical_only 命中，none / partial / full 都不命中
        Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationHistoricalOnly))).Groups);
        Assert.Empty((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationNone))).Groups);
        Assert.Empty((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationPartial))).Groups);
        Assert.Empty((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationFull))).Groups);
    }

    // ==================== 5. 草稿 / 已作废发票：单独标注且排除在有效合计之外 ====================

    [Fact]
    public async Task Draft_and_voided_invoices_are_labeled_and_excluded_from_active_totals()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var recorded = SeedInvoice(db, 968501L, SupplierA, "CNY", "E001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-40));
        var draft = SeedInvoice(db, 968502L, SupplierA, "CNY", "E002", 50m, 0m, 50m,
            PurchaseInvoiceRules.StatusDraft, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-40));
        SeedInvoice(db, 968503L, SupplierA, "CNY", "E003", 70m, 0m, 70m,
            PurchaseInvoiceRules.StatusVoided, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-40));
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 968951L, recorded, 20m);
        SeedInvoicePaymentEvidence(db, 968952L, draft, 10m);
        await db.SaveChangesAsync();

        // 默认只返回已登记证据：草稿 / 已作废必须显式选择状态筛选才可见
        var recordedOnly = await SupplierReconciliationAging.ForQueryAsync(db, Query());
        Assert.Equal(1, recordedOnly.PageInvoiceCount);
        Assert.Equal("E001", Assert.Single(recordedOnly.Groups.SelectMany(g => g.Invoices)).InvoiceNumber);

        var all = await SupplierReconciliationAging.ForQueryAsync(db, Query(invoiceStatus: "all"));
        Assert.Equal(3, all.PageInvoiceCount);
        Assert.Equal(1, all.ActiveEvidencePageInvoiceCount);
        Assert.Equal(1, all.DraftPageInvoiceCount);
        Assert.Equal(1, all.VoidedPageInvoiceCount);

        var rows = all.Groups.SelectMany(g => g.Invoices).ToList();
        var d = Row(rows, "E002");
        Assert.True(d.IsDraft);
        Assert.False(d.IsVoided);
        Assert.False(d.IsActiveEvidence);
        Assert.Contains("草稿", d.InvoiceStatusText);
        Assert.Equal(0m, d.ActiveAllocatedAmount);                  // 草稿发票上的引用行按「发票失效」分桶
        Assert.Equal(1, d.InvoiceInactiveAllocationCount);
        Assert.Equal(10m, d.InvoiceInactiveAllocationAmount);
        Assert.Equal(50m, d.RemainingAmount);
        Assert.Contains("草稿", d.Note);

        var v = Row(rows, "E003");
        Assert.True(v.IsVoided);
        Assert.False(v.IsActiveEvidence);
        Assert.Contains("作废", v.InvoiceStatusText);
        Assert.Contains("已作废", v.Note);

        // 有效合计只统计已登记发票：草稿 50 与已作废 70 一张都不进（分桶与币种汇总同理）
        var group = Assert.Single(all.Groups);
        Assert.Equal(100m, group.GrossAmount);
        Assert.Equal(20m, group.ActiveAllocatedAmount);
        Assert.Equal(80m, group.RemainingAmount);
        Assert.Equal(1, group.ActiveEvidenceInvoiceCount);
        Assert.Equal(1, group.Buckets.Sum(b => b.InvoiceCount));
        var currency = Assert.Single(all.Currencies);
        Assert.Equal(100m, currency.GrossAmount);
        Assert.Equal(20m, currency.ActiveAllocatedAmount);
        Assert.Equal(1, currency.ActiveEvidenceInvoiceCount);
    }

    // ==================== 6. 无效 / 无法确认链接：保持可见，绝不并入有效合计、绝不修复 ====================

    [Fact]
    public async Task Invalid_and_unavailable_links_stay_visible_and_are_never_repaired()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var invoice = SeedInvoice(db, 968601L, SupplierA, "CNY", "F001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-10));
        await db.SaveChangesAsync();

        // 引用行指向的付款单已删除 → 无法确认；币种不一致 → 无效证据（两者都保持可见）
        SeedInvoicePaymentEvidence(db, 968961L, invoice, 30m, paymentDeleted: true);
        SeedInvoicePaymentEvidence(db, 968962L, invoice, 20m, rowCurrency: "USD");
        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query());
        var row = Assert.Single(report.Groups.SelectMany(g => g.Invoices));

        Assert.Equal(0m, row.ActiveAllocatedAmount);                 // 无效证据绝不并入有效合计
        Assert.Equal(0, row.ActiveAllocationCount);
        Assert.Equal(100m, row.RemainingAmount);                     // 绝不把无效证据当作已分配来压低剩余
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationHistoricalOnly, row.AllocationState);
        Assert.True(row.HasAllocationHistory);
        Assert.True(row.HasInvalidOrUnavailableEvidence);
        Assert.Equal(1, row.InvalidAllocationCount);
        Assert.Equal(20m, row.InvalidAllocationAmount);
        Assert.Equal(1, row.UnavailableAllocationCount);
        Assert.Equal(30m, row.UnavailableAllocationAmount);
        Assert.Contains("无效证据", row.HistoricalEvidenceText);
        Assert.Contains("无法确认证据", row.HistoricalEvidenceText);
        Assert.Contains("不被修复", row.HistoricalEvidenceText);

        // 报表显式声明这些证据保持可见且不修复 / 不改派；汇总与币种行都不含它们
        Assert.Contains("保持可见", report.InvalidEvidenceNote);
        Assert.Contains("不被修复", report.InvalidEvidenceNote);
        Assert.Equal(1, report.InvalidEvidencePageInvoiceCount);
        var group = Assert.Single(report.Groups);
        Assert.Equal(100m, group.GrossAmount);
        Assert.Equal(0m, group.ActiveAllocatedAmount);
        Assert.Equal(100m, group.RemainingAmount);
        Assert.Equal(1, group.InvalidEvidenceInvoiceCount);
        Assert.Equal(0m, Assert.Single(report.Currencies).ActiveAllocatedAmount);
    }

    // ==================== 7. 多供应商多币种：分别成组，绝不跨币种合并 ====================

    [Fact]
    public async Task Multiple_suppliers_and_currencies_stay_separate_with_no_cross_currency_total()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedInvoice(db, 968701L, SupplierA, "CNY", "G001", 100m, 13m, 113m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-5), dueDate: AsOf.AddDays(-5));
        SeedInvoice(db, 968702L, SupplierA, "USD", "G002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-5), dueDate: AsOf.AddDays(-5));
        SeedInvoice(db, 968703L, SupplierB, "CNY", "G003", 50m, 6.5m, 56.5m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-5), dueDate: AsOf.AddDays(-65));
        SeedInvoice(db, 968704L, SupplierB, "USD", "G004", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-5), dueDate: AsOf.AddDays(-65));
        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query());

        Assert.Equal(4, report.PageInvoiceCount);
        Assert.Equal(4, report.Groups.Count);                       // 供应商 × 币种 = 2 × 2
        Assert.Equal(2, report.Currencies.Count);

        var cny = report.Currencies.Single(c => c.Currency == "CNY");
        var usd = report.Currencies.Single(c => c.Currency == "USD");
        Assert.Equal(2, cny.SupplierCount);
        Assert.Equal(169.5m, cny.GrossAmount);                      // 113 + 56.5（同币种跨供应商）
        Assert.Equal(2, cny.InvoiceCount);
        Assert.Equal(300m, usd.GrossAmount);                        // 100 + 200（绝不与 CNY 相加）
        Assert.Equal(2, usd.InvoiceCount);

        // 币种内账龄分桶独立：CNY 1~30 = 113、61~90 = 56.5；USD 1~30 = 100、61~90 = 200
        Assert.Equal(113m, BucketGross(cny, SupplierReconciliationAgingSemantics.BucketOverdue1To30));
        Assert.Equal(56.5m, BucketGross(cny, SupplierReconciliationAgingSemantics.BucketOverdue61To90));
        Assert.Equal(100m, BucketGross(usd, SupplierReconciliationAgingSemantics.BucketOverdue1To30));
        Assert.Equal(200m, BucketGross(usd, SupplierReconciliationAgingSemantics.BucketOverdue61To90));

        // 报表模型不存在任何跨币种总额字段：金额只能按币种分别查看
        var names = typeof(SupplierReconciliationAgingReport).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("GrandTotal", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("TotalAmount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("CrossCurrency", StringComparison.Ordinal));
        Assert.All(report.Currencies, c => Assert.False(string.IsNullOrWhiteSpace(c.Currency)));
        Assert.Contains("绝不", report.CurrencyIsolationNote);

        // 供应商 / 币种筛选只用持久化字段，且分组顺序稳定（供应商 → 币种）
        var usdOnly = await SupplierReconciliationAging.ForQueryAsync(db, Query(currency: "USD"));
        Assert.Equal(2, usdOnly.PageInvoiceCount);
        Assert.All(usdOnly.Groups, g => Assert.Equal("USD", g.Currency));
        Assert.Equal("USD", Assert.Single(usdOnly.Currencies).Currency);

        var supplierOnly = await SupplierReconciliationAging.ForQueryAsync(db, Query(supplierId: SupplierB));
        Assert.Equal(2, supplierOnly.PageInvoiceCount);
        Assert.All(supplierOnly.Groups, g => Assert.Equal(SupplierB, g.SupplierId));
        Assert.Equal(new[] { "CNY", "USD" },
            supplierOnly.Groups.Select(g => g.Currency).ToArray());
    }

    /// <summary>按发票号码取行（测试数据中号码唯一）</summary>
    private static SupplierReconciliationAgingInvoice Row(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows, string number)
        => rows.Single(r => r.InvoiceNumber == number);

    private static string? Bucket(IReadOnlyList<SupplierReconciliationAgingInvoice> rows, string number)
        => Row(rows, number).AgingBucket;

    private static decimal? BucketGross(SupplierReconciliationAgingCurrencySummary summary, string bucket)
        => summary.Buckets.Single(b => b.Bucket == bucket).GrossAmount;

    // ==================== 8. as-of 日期：账龄基准日显式、可复算、可拒绝非法值 ====================

    [Fact]
    public async Task Explicit_as_of_date_drives_aging_and_is_echoed()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968801L, SupplierA, "CNY", "H001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-60), dueDate: AsOf.AddDays(-20));
        await db.SaveChangesAsync();

        var early = await SupplierReconciliationAging.ForQueryAsync(db, Query(asOfDate: AsOf.AddDays(-25)));
        var earlyRow = Assert.Single(early.Groups.SelectMany(g => g.Invoices));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketNotDue, earlyRow.AgingBucket);
        Assert.Equal(-5, earlyRow.OverdueDays);
        Assert.Equal(AsOf.AddDays(-25), early.AsOfDate);
        Assert.Contains(AsOf.AddDays(-25).ToString("yyyy-MM-dd"), early.AsOfDateText);

        var onAsOf = await SupplierReconciliationAging.ForQueryAsync(db, Query(asOfDate: AsOf));
        var onAsOfRow = Assert.Single(onAsOf.Groups.SelectMany(g => g.Invoices));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue1To30, onAsOfRow.AgingBucket);
        Assert.Equal(20, onAsOfRow.OverdueDays);

        var later = await SupplierReconciliationAging.ForQueryAsync(db, Query(asOfDate: AsOf.AddDays(15)));
        var laterRow = Assert.Single(later.Groups.SelectMany(g => g.Invoices));
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue31To60, laterRow.AgingBucket);
        Assert.Equal(35, laterRow.OverdueDays);
        Assert.Equal(AsOf.AddDays(15), later.AsOfDate);

        // 同一张发票在三个 as-of 下金额不变、只有账龄桶变化（不变更任何来源记录）
        Assert.Equal(100m, earlyRow.GrossAmount);
        Assert.Equal(100m, laterRow.GrossAmount);
        Assert.Equal(0m, laterRow.ActiveAllocatedAmount);
        Assert.Equal(100m, laterRow.RemainingAmount);

        // 非法 as-of（年份过小）直接拒绝，不静默兜底为当天
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(asOfDate: new DateTime(1800, 1, 1))));
    }

    // ==================== 9. 筛选：只用持久化字段，非法取值一律拒绝 ====================

    [Fact]
    public async Task Filters_use_persisted_fields_only_and_reject_invalid_values()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var first = SeedInvoice(db, 968901L, SupplierA, "CNY", "I001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 3, 10),
            dueDate: new DateTime(2026, 4, 10), paymentTerms: "月结 30 天");
        SeedInvoice(db, 968902L, SupplierA, "CNY", "I002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, new DateTime(2026, 5, 10),
            dueDate: new DateTime(2026, 6, 10));
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 968991L, first, 40m);
        await db.SaveChangesAsync();

        Assert.Equal(2, (await SupplierReconciliationAging.ForQueryAsync(db, Query())).Total);
        Assert.Equal(1, (await SupplierReconciliationAging.ForQueryAsync(db,
            Query(invoiceId: first.Id))).PageInvoiceCount);
        Assert.Equal(2, (await SupplierReconciliationAging.ForQueryAsync(db,
            Query(invoiceDateFrom: new DateTime(2026, 1, 1),
                invoiceDateTo: new DateTime(2026, 12, 31)))).PageInvoiceCount);
        Assert.Equal("I002", Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(invoiceDateFrom: new DateTime(2026, 4, 1)))).Groups.SelectMany(g => g.Invoices)).InvoiceNumber);
        Assert.Equal("I001", Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(dueDateFrom: new DateTime(2026, 4, 1), dueDateTo: new DateTime(2026, 4, 30))))
            .Groups.SelectMany(g => g.Invoices)).InvoiceNumber);
        Assert.Equal("I001", Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationPartial)))
            .Groups.SelectMany(g => g.Invoices)).InvoiceNumber);
        Assert.Equal("I002", Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationNone)))
            .Groups.SelectMany(g => g.Invoices)).InvoiceNumber);

        // 关键字按持久化字段匹配（发票号码 / 代码 / 供应商编码与名称 / 付款条件文本）
        Assert.Equal("I002", Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(keyword: "I002"))).Groups.SelectMany(g => g.Invoices)).InvoiceNumber);
        Assert.Equal("I001", Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(keyword: "月结"))).Groups.SelectMany(g => g.Invoices)).InvoiceNumber);
        // 供应商名称快照也参与关键字匹配：同供应商的两张发票都会被命中
        Assert.Equal(2, (await SupplierReconciliationAging.ForQueryAsync(db,
            Query(keyword: "甲供应商"))).PageInvoiceCount);
        Assert.Equal(0, (await SupplierReconciliationAging.ForQueryAsync(db,
            Query(keyword: "不存在"))).PageInvoiceCount);
        Assert.Equal(0, (await SupplierReconciliationAging.ForQueryAsync(db, Query(currency: "JPY"))).PageInvoiceCount);
        Assert.Equal(0, (await SupplierReconciliationAging.ForQueryAsync(db,
            Query(supplierId: SupplierB))).PageInvoiceCount);

        // 非法取值一律拒绝（绝不静默忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(invoiceId: 0)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(invoiceStatus: "paid")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(allocationState: "settled")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(currency: "XYZ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(
                invoiceDateFrom: new DateTime(2026, 5, 1), invoiceDateTo: new DateTime(2026, 4, 1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(
                dueDateFrom: new DateTime(2026, 5, 1), dueDateTo: new DateTime(2026, 4, 1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForQueryAsync(db, Query(keyword: new string('k', 51))));

        // 分页参数钳制到有界范围（超出上限按上限截断，不无界装载）
        var clamped = Query(page: 0, pageSize: 1000);
        clamped.Normalize();
        Assert.Equal(1, clamped.Page);
        Assert.Equal(SupplierReconciliationAgingSemantics.MaxPageSize, clamped.PageSize);

        // 越界页：没有发票行，也不臆造合计
        var beyond = await SupplierReconciliationAging.ForQueryAsync(db, Query(page: 9, pageSize: 2));
        Assert.Equal(0, beyond.PageInvoiceCount);
        Assert.Empty(beyond.Groups);
        Assert.Empty(beyond.Currencies);
        Assert.Empty(beyond.UnknownDueDateGroups);
    }

    // ==================== 10. 有界读取：数据集访问次数与行数无关，且全程不写库 ====================

    [Fact]
    public async Task Queries_use_a_bounded_number_of_dataset_reads_and_never_write()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var invoice = SeedInvoice(db, 968911L, SupplierA, "CNY", "J001", 10m, 0m, 10m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1), dueDate: AsOf.AddDays(-1));
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 968993L, invoice, 5m);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var single = await SupplierReconciliationAging.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        Assert.Equal(1, single.PageInvoiceCount);
        Assert.Equal(5m, Assert.Single(single.Groups.SelectMany(g => g.Invoices)).ActiveAllocatedAmount);
        // 固定 7 次数据集访问：发票（筛选 + 分页共用 1 次 + 本页装载 1 次）+ 供应商 +
        // ERP-066 引用行 + 发票 + 付款单 + 付款单侧有效引用合计（复用 ERP-067 的同一套派生）
        Assert.Equal(7, singleReads);

        // 再补 300 张（都带一条引用行、跨多页）：数据集访问次数必须保持不变（无 N+1、无逐行查库）
        var extras = new List<PurchaseInvoice>();
        for (var i = 2; i <= 301; i++)
        {
            extras.Add(SeedInvoice(db, 969000L + i, SupplierA, "CNY", $"J{i:000}", 1m, 0m, 1m,
                PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-1), dueDate: AsOf.AddDays(-1)));
        }

        await db.SaveChangesAsync();

        var rowId = 969500L;
        foreach (var extra in extras)
        {
            SeedInvoicePaymentEvidence(db, rowId++, extra, 1m);
        }

        await db.SaveChangesAsync();

        var large = await SupplierReconciliationAging.ForQueryAsync(counting.Proxy, Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(SupplierReconciliationAgingSemantics.MaxPageSize, large.PageInvoiceCount);  // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                                                    // 只读：没有一次 SaveChanges

        // 汇总只统计本页：200 张 × 1 元（含税总额与有效已分配恒可确认）
        Assert.Equal(200m, large.Groups.Single().GrossAmount);
        Assert.Equal(200m, large.Groups.Single().ActiveAllocatedAmount);
        Assert.Equal(0m, large.Groups.Single().RemainingAmount);
        Assert.Equal(200, large.ActiveEvidencePageInvoiceCount);
    }

    // ==================== 11. 只读：不改写发票 / 引用行 / 付款单 / 供应商 ====================

    [Fact]
    public async Task Workspace_is_read_only_and_never_mutates_sources()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商", status: 0);          // 已停用：快照仍可读、不回填
        var invoice = SeedInvoice(db, 969101L, SupplierA, "CNY", "K001", 100m, 13m, 113m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-30), dueDate: AsOf.AddDays(-30));
        SeedInvoice(db, 969102L, SupplierB, "CNY", "K002", 50m, 0m, 50m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-30), dueDate: AsOf.AddDays(-30));
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 969191L, invoice, 20m);
        await db.SaveChangesAsync();

        var before = Snapshot(db);

        await SupplierReconciliationAging.ForQueryAsync(db, Query(invoiceStatus: "all", pageSize: 200));
        await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationPartial, keyword: "K"));
        await SupplierReconciliationAging.ForQueryAsync(db,
            Query(allocationState: SupplierReconciliationAgingSemantics.AllocationNone, currency: "CNY"));

        Assert.Equal(before, Snapshot(db));

        // 停用供应商的历史快照照常可读，且系统不「修复」它（不回填名称、不改状态）
        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query(supplierId: SupplierB));
        var row = Assert.Single(report.Groups.SelectMany(g => g.Invoices));
        Assert.Equal("乙供应商", row.SupplierName);
        Assert.False(row.SupplierAvailable);
        Assert.Contains("停用", row.SupplierAvailabilityText);
        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 12. 明细：重新校验身份与模块授权，来源失效一律 fail closed ====================

    [Fact]
    public async Task Detail_revalidates_authentication_and_module_authorization_and_fails_closed()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var invoice = SeedInvoice(db, 969201L, SupplierA, "CNY", "L001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-30), dueDate: AsOf.AddDays(-30));
        await db.SaveChangesAsync();
        SeedInvoicePaymentEvidence(db, 969291L, invoice, 40m);
        SeedAuthorization(db, AuthorizedUserId, SupplierReconciliationAging.RequiredMenuCode);
        SeedAuthorization(db, NoMenuUserId);                          // 有角色但无菜单授权
        SeedAuthorization(db, WrongMenuUserId, "finance-payment");    // 只有其它模块授权
        await db.SaveChangesAsync();

        // 未认证：拒绝，不返回任何证据
        var unauthenticated = await AssertBusinessAsync(ErrorCodes.Unauthorized,
            () => SupplierReconciliationAging.ForInvoiceDetailAsync(db, invoice.Id, null, AsOf));
        Assert.Contains("登录", unauthenticated.Message);

        // 无该模块授权 / 只有其它模块授权：拒绝（fail closed）
        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => SupplierReconciliationAging.ForInvoiceDetailAsync(db, invoice.Id, NoMenuUserId, AsOf));
        var forbidden = await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => SupplierReconciliationAging.ForInvoiceDetailAsync(db, invoice.Id, WrongMenuUserId, AsOf));
        Assert.Contains(SupplierReconciliationAging.RequiredMenuCode, forbidden.Message);
        Assert.Contains("fail closed", forbidden.Message);

        // 非法发票 Id：参数错误（不猜测、不兜底）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SupplierReconciliationAging.ForInvoiceDetailAsync(db, 0, AuthorizedUserId, AsOf));

        // 已授权：明细与列表行完全同源（同一派生方法），并回显授权复核说明
        var detail = await SupplierReconciliationAging.ForInvoiceDetailAsync(db, invoice.Id, AuthorizedUserId, AsOf);
        var listRow = Assert.Single((await SupplierReconciliationAging.ForQueryAsync(db,
            Query(invoiceId: invoice.Id))).Groups.SelectMany(g => g.Invoices));
        Assert.Equal(invoice.Id, detail.InvoiceId);
        Assert.Equal(AsOf, detail.AsOfDate);
        Assert.Equal(listRow.GrossAmount, detail.Row.GrossAmount);
        Assert.Equal(listRow.ActiveAllocatedAmount, detail.Row.ActiveAllocatedAmount);
        Assert.Equal(listRow.RemainingAmount, detail.Row.RemainingAmount);
        Assert.Equal(listRow.AgingBucket, detail.Row.AgingBucket);
        Assert.Equal(listRow.OverdueDays, detail.Row.OverdueDays);
        Assert.Equal(listRow.RemainingState, detail.Row.RemainingState);
        Assert.Equal(60m, detail.Row.RemainingAmount);
        Assert.Contains(SupplierReconciliationAging.RequiredMenuText, detail.AuthorizationNote);
        Assert.Contains("重新校验", detail.AuthorizationNote);
        Assert.Contains("fail closed", detail.SourceFailClosedNote);

        // 授权被回收后立即 fail closed（每次请求都重新查询授权，不依赖缓存）
        RevokeAllMenus(db, AuthorizedUserId);
        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => SupplierReconciliationAging.ForInvoiceDetailAsync(db, invoice.Id, AuthorizedUserId, AsOf));

        // 恢复授权后可用；但来源发票被软删除后一律拒绝（不返回部分证据、不修复、不改派）
        RestoreAllMenus(db, AuthorizedUserId);
        invoice.IsDeleted = true;
        await db.SaveChangesAsync();
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => SupplierReconciliationAging.ForInvoiceDetailAsync(db, invoice.Id, AuthorizedUserId, AsOf));

        // 已删除的发票也不再出现在列表（fail closed）
        Assert.Equal(0, (await SupplierReconciliationAging.ForQueryAsync(db, Query())).PageInvoiceCount);
    }

    // ==================== 13. 接口契约：类级授权 + 只读 GET + 无跨币种 / 账务结论字段 ====================

    [Fact]
    public void Controller_exposes_read_only_authorized_aging_endpoints()
    {
        var type = typeof(PurchaseInvoiceController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("api/purchase-invoices", type.GetCustomAttribute<RouteAttribute>()!.Template);

        Assert.Equal("reconciliation-aging",
            type.GetMethod(nameof(PurchaseInvoiceController.ReconciliationAging))!
                .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("reconciliation-aging/invoices/{invoiceId:long}",
            type.GetMethod(nameof(PurchaseInvoiceController.ReconciliationAgingInvoiceDetail))!
                .GetCustomAttribute<HttpGetAttribute>()!.Template);

        // 工作台只读：没有针对本工作台的写动作，也没有 AllowAnonymous
        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpPostAttribute>()?.Template
            ?.Contains("reconciliation-aging", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpPutAttribute>()?.Template
            ?.Contains("reconciliation-aging", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpDeleteAttribute>()?.Template
            ?.Contains("reconciliation-aging", StringComparison.Ordinal) == true);

        // 报表 / 行 / 分组 / 币种汇总模型都没有任何跨币种总额或账务结论字段
        var models = new[]
        {
            typeof(SupplierReconciliationAgingReport),
            typeof(SupplierReconciliationAgingInvoice),
            typeof(SupplierReconciliationAgingGroup),
            typeof(SupplierReconciliationAgingCurrencySummary),
            typeof(SupplierReconciliationAgingBucketTotal),
        };
        foreach (var model in models)
        {
            var names = model.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("GrandTotal", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("TotalAmount", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("CrossCurrency", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Payable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Settlement", StringComparison.Ordinal));
        }

        // 口径文案显式声明边界（不是总账 / 应付余额 / 法定对账单 / 付款授权 / 税务申报 / 结算确认，也不是按币种裁剪的发票登记册）
        Assert.Contains("不是", SupplierReconciliationAgingSemantics.BoundaryText);
        Assert.Contains("付款授权", SupplierReconciliationAgingSemantics.BoundaryText);
        Assert.Contains("税务", SupplierReconciliationAgingSemantics.BoundaryText);
        Assert.Contains("申报", SupplierReconciliationAgingSemantics.BoundaryText);
        Assert.Contains("结算确认", SupplierReconciliationAgingSemantics.BoundaryText);
        Assert.Contains("法定供应商对账单", SupplierReconciliationAgingSemantics.BoundaryText);
        Assert.Equal(5, SupplierReconciliationAgingSemantics.SupportedBuckets.Length);
        Assert.Equal(4, SupplierReconciliationAgingSemantics.SupportedAllocationFilters.Length);
    }

    // ==================== 14. 前端接线契约（不启动浏览器） ====================

    [Fact]
    public void Frontend_workspace_is_wired_with_the_same_semantics_without_browser()
    {
        var js = JsDirectory();
        var script = File.ReadAllText(Path.Combine(js, "supplier-reconciliation-aging.js"));

        Assert.Contains("function openSupplierReconciliationAgingWorkspace(", script);
        Assert.Contains("CURRENT_PAGE_CODE = 'supplier-reconciliation-aging'", script);
        Assert.Contains("const SRA_API = '/api/purchase-invoices/reconciliation-aging'", script);
        Assert.Contains("invoiceStatus", script);
        Assert.Contains("allocationState", script);
        Assert.Contains("dueDateFrom", script);
        Assert.Contains("asOfDate", script);
        Assert.Contains("unknown_due_date", script);              // 未知到期日独立分组（不并入任何账龄桶）
        Assert.Contains("算术剩余证据", script);
        Assert.Contains("有效已分配（ERP-066）", script);
        Assert.Contains("无跨币种总额", script);
        Assert.Contains("exportSraCsv", script);
        Assert.Contains("SRA_DATA", script);                      // 导出与屏幕同一份数据
        Assert.Contains("showSupplierReconciliationAgingDetail", script);
        Assert.Contains("${SRA_API}/invoices/${invoiceId}", script);
        Assert.Contains("fail closed", script);
        Assert.DoesNotContain("GrandTotal", script);
        Assert.DoesNotContain("跨币种合计", script);

        var wwwroot = Path.GetFullPath(Path.Combine(js, ".."));
        var index = File.ReadAllText(Path.Combine(wwwroot, "index.html"));
        Assert.Contains("/js/supplier-reconciliation-aging.js", index);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSupplierReconciliationAgingWorkspace()", modulesDoc);
        Assert.Contains("对账与账龄", modulesDoc);
        Assert.Contains("不是总账", modulesDoc);

        var modules = File.ReadAllText(Path.Combine(js, "modules.js"));
        Assert.Contains("openSupplierReconciliationAgingWorkspace", modules);
        Assert.Contains("对账与账龄", modules);
    }

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    // ==================== 15. 有界上限：金额一律按「未知」返回，绝不给部分合计 ====================

    [Fact]
    public async Task Truncated_reads_return_unknown_amounts_without_partial_totals()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var first = SeedInvoice(db, 969301L, SupplierA, "CNY", "M001", 10000m, 0m, 10000m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-5));
        var second = SeedInvoice(db, 969302L, SupplierA, "CNY", "M002", 10000m, 0m, 10000m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-10), dueDate: AsOf.AddDays(-5));
        await db.SaveChangesAsync();

        // 2 张发票 × 2501 条引用行 = 5002 条 > ERP-067 的 5000 行上限 → 命中上限：不给部分合计
        SeedBulkInvoicePaymentEvidence(db, 1L, first, 2501, 1m);
        SeedBulkInvoicePaymentEvidence(db, 2L, second, 2501, 1m);
        await db.SaveChangesAsync();

        var report = await SupplierReconciliationAging.ForQueryAsync(db, Query(pageSize: 50));

        Assert.True(report.Truncated);
        Assert.Contains("有界上限", report.TruncatedNote);
        Assert.Equal(2, report.PageInvoiceCount);
        Assert.Equal(2, report.UnknownRemainingPageInvoiceCount);
        Assert.Equal(0, report.OverAllocatedPageInvoiceCount);

        var rows = report.Groups.SelectMany(g => g.Invoices).ToList();
        Assert.All(rows, r =>
        {
            Assert.Null(r.ActiveAllocatedAmount);
            Assert.Null(r.ActiveAllocationCount);
            Assert.Null(r.RemainingAmount);
            Assert.Equal(SupplierReconciliationAgingSemantics.AllocationUnknown, r.AllocationState);
            Assert.Equal(SupplierReconciliationAgingSemantics.RemainingUnknown, r.RemainingState);
            Assert.Contains("未知", r.Note);
            Assert.Contains("未知", r.HistoricalEvidenceText);
        });

        // 含税总额仍完整（发票行本身完整加载），但分配证据与剩余一律「未知」，绝不相加出部分合计
        var group = Assert.Single(report.Groups);
        Assert.Equal(20000m, group.GrossAmount);
        Assert.Null(group.ActiveAllocatedAmount);
        Assert.Null(group.RemainingAmount);
        Assert.Equal(2, group.UnknownRemainingInvoiceCount);
        var bucket = group.Buckets.Single(b => b.Bucket == SupplierReconciliationAgingSemantics.BucketOverdue1To30);
        Assert.Equal(2, bucket.InvoiceCount);
        Assert.Null(bucket.ActiveAllocatedAmount);
        Assert.Null(bucket.RemainingAmount);
        Assert.Equal(2, bucket.UnknownRemainingInvoiceCount);
        Assert.Null(Assert.Single(report.Currencies).RemainingAmount);
    }

    // ==================== 测试基建（内存库；不连 SQL Server、不启动 API） ====================

    private static SupplierReconciliationAgingQuery Query(
        long? supplierId = null, string? currency = null, long? invoiceId = null,
        string? invoiceStatus = null, string? allocationState = null, string? keyword = null,
        DateTime? invoiceDateFrom = null, DateTime? invoiceDateTo = null,
        DateTime? dueDateFrom = null, DateTime? dueDateTo = null,
        DateTime? asOfDate = null, int page = 1, int pageSize = 50)
        => new()
        {
            SupplierId = supplierId,
            Currency = currency,
            InvoiceId = invoiceId,
            InvoiceStatus = invoiceStatus,
            AllocationState = allocationState,
            Keyword = keyword,
            InvoiceDateFrom = invoiceDateFrom,
            InvoiceDateTo = invoiceDateTo,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            AsOfDate = asOfDate ?? AsOf,
            Page = page,
            PageSize = pageSize,
        };

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static BaseSupplier SeedSupplier(
        ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            Id = id,
            SupplierCode = $"S{id}",
            SupplierName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        return supplier;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, long id, long supplierId, string currency, string number,
        decimal net, decimal tax, decimal gross, int status, DateTime invoiceDate,
        DateTime? dueDate = null, string invoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
        string code = "", string paymentTerms = "", bool deleted = false)
    {
        var supplier = db.BaseSuppliers.Local.FirstOrDefault(s => s.Id == supplierId);
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
            SupplierCode = supplier?.SupplierCode ?? $"S{supplierId}",
            SupplierName = supplier?.SupplierName ?? $"供应商{supplierId}",
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            DueDate = dueDate?.Date,
            PaymentTerms = paymentTerms,
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : invoiceDate.Date.AddDays(1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? invoiceDate.Date.AddDays(2) : null,
            VoidReason = status == PurchaseInvoiceRules.StatusVoided ? "作废测试" : string.Empty,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    /// <summary>
    /// 写入一条 ERP-066 持久化「付款单 → 采购发票」引用行（连同其付款单）：默认快照与发票一致（有效证据）；
    /// <paramref name="status"/> / <paramref name="rowCurrency"/> / <paramref name="paymentDeleted"/> 可构造
    /// 已作废 / 无效（币种不一致）/ 无法确认（付款单已删除）的历史证据。
    /// </summary>
    private static void SeedInvoicePaymentEvidence(
        ErpDbContext db, long id, PurchaseInvoice invoice, decimal allocatedAmount,
        decimal paymentAmount = 1000m,
        int status = SupplierPaymentInvoiceAllocationRules.StatusActive,
        string? rowCurrency = null, bool paymentDeleted = false)
    {
        var payment = BuildPayment(db, id + 5_000_000L, invoice, paymentAmount, paymentDeleted);
        db.SupplierPaymentInvoiceAllocations.Add(
            BuildInvoicePaymentRow(id, invoice, payment, allocatedAmount, rowCurrency, status));
    }

    /// <summary>
    /// 写入一张发票的大批量引用行（同一条付款单，逐行登记）：用于验证命中 ERP-067 有界上限时
    /// 金额一律按「未知」返回、绝不给部分合计。
    /// </summary>
    private static void SeedBulkInvoicePaymentEvidence(
        ErpDbContext db, long seed, PurchaseInvoice invoice, int count, decimal amountEach)
    {
        var payment = BuildPayment(db, 9_000_000L + seed, invoice, 10_000_000m, false);
        for (var i = 0; i < count; i++)
        {
            db.SupplierPaymentInvoiceAllocations.Add(BuildInvoicePaymentRow(
                9_500_000L + (seed * 100_000L) + i, invoice, payment, amountEach, null,
                SupplierPaymentInvoiceAllocationRules.StatusActive));
        }
    }

    private static FinancePayment BuildPayment(
        ErpDbContext db, long id, PurchaseInvoice invoice, decimal amount, bool deleted)
    {
        var payment = new FinancePayment
        {
            Id = id,
            PaymentNo = $"FK-068-{id}",
            PaymentDate = AsOf.AddDays(-3),
            SupplierId = invoice.SupplierId,
            Amount = amount,
            Currency = Enum.Parse<Currency>(invoice.Currency),
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        return payment;
    }

    /// <summary>构造一条引用行（全部快照取自持久化发票与付款单，与服务端权威口径同源）</summary>
    private static SupplierPaymentInvoiceAllocation BuildInvoicePaymentRow(
        long id, PurchaseInvoice invoice, FinancePayment payment, decimal allocatedAmount,
        string? rowCurrency, int status)
        => new()
        {
            Id = id,
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = payment.Amount,
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
        };

    /// <summary>播种「角色 → 菜单」授权（既有口径）：只授予传入的菜单编码；空数组 = 有角色但无菜单</summary>
    private static void SeedAuthorization(ErpDbContext db, long userId, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"对账工作台测试角色 {userId}",
            RoleCode = $"SRA-{userId}",
            Description = "ERP-068 授权测试",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes)
        {
            var menu = new SysMenu
            {
                ParentId = 0,
                MenuName = $"测试菜单 {code}",
                MenuCode = code,
                Path = $"/{code}",
                Icon = "test",
                SortOrder = 1,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    /// <summary>撤销该账号的全部菜单授权（模拟权限回收，角色与用户关系保持不变）</summary>
    private static void RevokeAllMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>恢复此前被撤销的菜单授权</summary>
    private static void RestoreAllMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = false;
        db.SaveChanges();
    }

    /// <summary>来源记录快照（验证工作台全程只读、不改写任何来源）</summary>
    private static string Snapshot(ErpDbContext db)
        => string.Join("|", db.PurchaseInvoices.AsNoTracking().OrderBy(i => i.Id)
                .Select(i => $"{i.Id}:{i.Status}:{i.GrossAmount}:{i.DueDate}:{i.PaymentTerms}:{i.IsDeleted}"))
            + "||" + string.Join("|", db.SupplierPaymentInvoiceAllocations.AsNoTracking()
                .OrderBy(a => a.Id)
                .Select(a => $"{a.Id}:{a.Status}:{a.AllocatedAmount}:{a.PaymentId}:{a.VoidReason}"))
            + "||" + string.Join("|", db.FinancePayments.AsNoTracking().OrderBy(p => p.Id)
                .Select(p => $"{p.Id}:{p.Amount}:{p.Status}:{p.IsDeleted}"))
            + "||" + string.Join("|", db.BaseSuppliers.AsNoTracking().OrderBy(s => s.Id)
                .Select(s => $"{s.Id}:{s.SupplierName}:{s.Status}:{s.IsDeleted}"));
}