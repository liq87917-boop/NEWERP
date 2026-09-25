using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-072 代理服务费对账与账龄工作台（只读派生）单元测试：显式与缺失到期日、五个账龄桶的全部边界、
/// 部分 / 全额分摊与算术剩余证据、已作废历史与无效 / 无法确认链接、草稿 / 已作废对账单排除、
/// 多客户多币种（绝不跨币种合并）、as-of 过滤、全部持久化字段筛选与非法取值拒绝、
/// 有界读取（数据集访问次数与行数无关）与只读不写库、明细的身份与既有「角色 → 菜单」授权 fail closed、
/// 展示清单有界而聚合合计精确、来源身份有界而计数精确，以及结构 / 请求 / 前端与路由接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收
/// （浏览器验收按 <c>browser_deferred</c> 延后到 <c>FINAL-UI-ACCEPTANCE</c>）。</para>
/// </summary>
public class AgencyServiceFeeReconciliationTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>账龄基准日（显式 as-of；账龄只相对它计算）</summary>
    private static readonly DateTime AsOf = new(2026, 9, 25);

    private const long CustomerA = 977001L;
    private const long CustomerB = 977002L;
    private const long AuthorizedUserId = 9901L;
    private const long NoMenuUserId = 9902L;
    private const long WrongMenuUserId = 9903L;

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>登记一份 ERP-069 协议证据（只作只读身份留痕；本工作台不派生任何费用）</summary>
    private static AgencyServiceFeeAgreement SeedAgreement(
        ErpDbContext db, string agreementNo, long customerId, string currency,
        int status = AgencyServiceFeeAgreementRules.StatusRecorded, bool deleted = false)
    {
        var agreement = new AgencyServiceFeeAgreement
        {
            AgreementNo = agreementNo,
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = "比例费率",
            RatePercent = 3m,
            FeeBasis = "按订单金额",
            Status = status,
            RecordedAt = new DateTime(2026, 1, 2),
            RecordedBy = "张三",
            IsDeleted = deleted
        };
        db.AgencyServiceFeeAgreements.Add(agreement);
        db.SaveChanges();
        return agreement;
    }

    /// <summary>登记一张 ERP-070 对账单证据（合计由服务端在真实流程中计算；本测试直接给持久化合计）</summary>
    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount,
        string currency = "USD", int status = AgencyServiceFeeStatementRules.StatusRecorded,
        DateTime? dueDate = null, DateTime? statementDate = null, bool deleted = false,
        string customerCode = "C001", string customerName = "义乌进出口",
        long agreementId = 0, string agreementNo = "ASF-2026-001")
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = customerCode,
            CustomerName = customerName,
            Currency = currency,
            StatementDate = statementDate ?? new DateTime(2026, 9, 1),
            DueDate = dueDate,
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = agreementId,
            AgreementNo = agreementNo,
            AgreementCurrency = currency,
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = status,
            RecordedAt = status == AgencyServiceFeeStatementRules.StatusRecorded
                ? new DateTime(2026, 9, 2)
                : null,
            RecordedBy = status == AgencyServiceFeeStatementRules.StatusRecorded ? "张三" : string.Empty,
            IsDeleted = deleted
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    /// <summary>登记一条对账单服务来源引用行（只作来源摘要派生；金额不在本工作台使用）</summary>
    private static AgencyServiceFeeStatementLine SeedLine(
        ErpDbContext db, long statementId, int lineNo, string sourceType, long sourceId, string sourceNo,
        decimal amount = 100m, string currency = "USD")
    {
        var line = new AgencyServiceFeeStatementLine
        {
            StatementId = statementId,
            LineNo = lineNo,
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            SourceDate = new DateTime(2026, 8, 10),
            SourceStatus = 1,
            SourceStatusText = "已审核",
            SourceCustomerId = CustomerA,
            SourceCustomerCode = "C001",
            SourceCustomerName = "义乌进出口",
            SourceCurrency = sourceType == AgencyServiceFeeStatementRules.SourceTypeSalesOrder ? currency : string.Empty,
            Description = "服务费行",
            BasisNote = "按订单金额",
            Amount = amount,
            Currency = currency,
            Status = AgencyServiceFeeStatementRules.StatusRecorded
        };
        db.AgencyServiceFeeStatementLines.Add(line);
        db.SaveChanges();
        return line;
    }

    /// <summary>登记一张客户收款单（本工作台只读它：金额 / 币种 / 客户 / 状态都不会被改写）</summary>
    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved,
        bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = status,
            IsDeleted = deleted
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    /// <summary>
    /// 登记一条 ERP-071 收款分摊行（<paramref name="currency"/> / <paramref name="customerId"/> 可显式制造
    /// 「快照不一致」的无效证据场景；金额与状态由调用方显式给出）。
    /// </summary>
    private static AgencyServiceFeeCollectionAllocation SeedAllocation(
        ErpDbContext db, AgencyServiceFeeStatement statement, FinanceReceipt receipt, decimal amount,
        int status = AgencyServiceFeeCollectionAllocationRules.StatusActive,
        string? currency = null, long? customerId = null, DateTime? allocatedAt = null,
        decimal? statementTotalOverride = null)
    {
        var row = new AgencyServiceFeeCollectionAllocation
        {
            StatementId = statement.Id,
            StatementNo = statement.StatementNo,
            StatementDate = statement.StatementDate,
            StatementStatus = statement.Status,
            StatementStatusText = AgencyServiceFeeCollectionAllocationRules.StatementStatusText(statement.Status),
            StatementTotalAmount = statementTotalOverride ?? statement.TotalAmount,
            StatementCurrency = statement.Currency,
            StatementAgreementId = statement.AgreementId,
            StatementAgreementNo = statement.AgreementNo,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = AgencyServiceFeeCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
            ReceiptAmount = receipt.Amount,
            CustomerId = customerId ?? statement.CustomerId,
            CustomerCode = statement.CustomerCode,
            CustomerName = statement.CustomerName,
            AllocatedAmount = amount,
            Currency = currency ?? statement.Currency,
            Status = status,
            AllocatedAt = allocatedAt ?? new DateTime(2026, 9, 21),
            AllocatedBy = "张三"
        };
        db.AgencyServiceFeeCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    /// <summary>工作台查询（默认只统计已登记有效证据、as-of = 测试基准日）</summary>
    private static AgencyServiceFeeReconciliationQuery Query(
        long? customerId = null, string? currency = null, long? agreementId = null, long? statementId = null,
        string? sourceType = null, string? statementStatus = null, string? allocationState = null,
        DateTime? statementDateFrom = null, DateTime? statementDateTo = null,
        DateTime? dueDateFrom = null, DateTime? dueDateTo = null, string? keyword = null,
        DateTime? asOf = null, int page = 1, int pageSize = 200)
        => new()
        {
            CustomerId = customerId,
            Currency = currency,
            AgreementId = agreementId,
            StatementId = statementId,
            SourceType = sourceType,
            StatementStatus = statementStatus,
            AllocationState = allocationState,
            StatementDateFrom = statementDateFrom,
            StatementDateTo = statementDateTo,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            Keyword = keyword,
            AsOfDate = asOf ?? AsOf,
            Page = page,
            PageSize = pageSize
        };

    private static AgencyServiceFeeReconciliationStatementRow Row(
        AgencyServiceFeeReconciliationReport report, string statementNo)
        => report.Groups.SelectMany(g => g.Statements).Single(r => r.StatementNo == statementNo);

    private static AgencyServiceFeeReconciliationBucketTotal Bucket(
        AgencyServiceFeeReconciliationReport report, string bucket)
        => Assert.Single(report.Currencies).Buckets.Single(b => b.Bucket == bucket);

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 账龄：五个桶互斥且完整覆盖全部边界 ====================

    [Fact]
    public async Task Aging_buckets_cover_every_boundary_and_never_overlap()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");

        // 未到期：到期日 = as-of（恰好到期）与 as-of 之后
        SeedStatement(db, "A001", customer.Id, 100m, dueDate: AsOf);
        SeedStatement(db, "A002", customer.Id, 100m, dueDate: AsOf.AddDays(5));

        // 逾期 1 ~ 30 天（含边界 1 与 30）
        SeedStatement(db, "A003", customer.Id, 100m, dueDate: AsOf.AddDays(-1));
        SeedStatement(db, "A004", customer.Id, 100m, dueDate: AsOf.AddDays(-30));

        // 逾期 31 ~ 60 天（含边界 31 与 60）
        SeedStatement(db, "A005", customer.Id, 100m, dueDate: AsOf.AddDays(-31));
        SeedStatement(db, "A006", customer.Id, 100m, dueDate: AsOf.AddDays(-60));

        // 逾期 61 ~ 90 天（含边界 61 与 90）
        SeedStatement(db, "A007", customer.Id, 100m, dueDate: AsOf.AddDays(-61));
        SeedStatement(db, "A008", customer.Id, 100m, dueDate: AsOf.AddDays(-90));

        // 逾期 90 天以上（91 天）
        SeedStatement(db, "A009", customer.Id, 100m, dueDate: AsOf.AddDays(-91));

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());

        Assert.Equal(9, report.PageStatementCount);
        Assert.Equal(9, report.KnownDueDatePageStatementCount);
        Assert.Equal(0, report.UnknownDueDatePageStatementCount);

        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketNotDue, Row(report, "A001").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketNotDue, Row(report, "A002").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue1To30, Row(report, "A003").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue1To30, Row(report, "A004").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue31To60, Row(report, "A005").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue31To60, Row(report, "A006").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue61To90, Row(report, "A007").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue61To90, Row(report, "A008").AgingBucket);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdueOver90, Row(report, "A009").AgingBucket);

        // 逾期天数（as-of − 到期日；未到期为负数）
        Assert.Equal(-5, Row(report, "A002").OverdueDays);
        Assert.Equal(1, Row(report, "A003").OverdueDays);
        Assert.Equal(30, Row(report, "A004").OverdueDays);
        Assert.Equal(91, Row(report, "A009").OverdueDays);
        Assert.Contains("逾期 91 天", Row(report, "A009").AgingText);

        // 桶合计互斥且完整：每张恰好进一个桶，张数与金额守恒
        var buckets = Assert.Single(report.Currencies).Buckets;
        Assert.Equal(5, buckets.Count);
        Assert.Equal(9, buckets.Sum(b => b.StatementCount));
        Assert.Equal(900m, buckets.Sum(b => b.StatementAmount));
        Assert.Equal(2, Bucket(report, AgencyServiceFeeReconciliationRules.BucketNotDue).StatementCount);
        Assert.Equal(1, Bucket(report, AgencyServiceFeeReconciliationRules.BucketOverdueOver90).StatementCount);
        Assert.All(buckets, b => Assert.True(b.IsAgingBucket));

        // 越界分页：不返回任何证据、也不给任何汇总
        var beyond = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(page: 9, pageSize: 2));
        Assert.Equal(0, beyond.PageStatementCount);
        Assert.Empty(beyond.Groups);
        Assert.Empty(beyond.Currencies);
        Assert.Empty(beyond.UnknownDueDateGroups);
    }

    // ==================== 2. 未知到期日：独立成组、绝不计算账龄 ====================

    [Fact]
    public async Task Statements_without_due_date_are_grouped_separately_and_never_aged()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "B001", customer.Id, 100m, dueDate: AsOf.AddDays(-40), statementDate: AsOf.AddDays(-50));
        SeedStatement(db, "B002", customer.Id, 200m, dueDate: null, statementDate: AsOf.AddDays(-10));

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());

        var unknown = Row(report, "B002");
        Assert.False(unknown.DueDateKnown);
        Assert.Null(unknown.DueDate);
        Assert.Null(unknown.AgingBucket);
        Assert.Null(unknown.OverdueDays);
        Assert.Contains("不计算账龄", unknown.AgingText);
        Assert.Contains("未知到期日", unknown.AgingBucketText);
        Assert.Contains("不计算", unknown.AgingBucketText);
        Assert.Contains("未登记显式到期日", unknown.Note);

        // 账龄桶只统计有显式到期日的对账单：B002 一张都不进
        Assert.Equal(1, Bucket(report, AgencyServiceFeeReconciliationRules.BucketOverdue31To60).StatementCount);
        Assert.Equal(1, report.KnownDueDatePageStatementCount);
        Assert.Equal(1, report.UnknownDueDatePageStatementCount);
        Assert.Equal(1, report.Currencies.Sum(c => c.Buckets.Sum(b => b.StatementCount)));
        Assert.Equal(100m, report.Currencies.Sum(c => c.Buckets.Sum(b => b.StatementAmount)));

        // 「未知到期日」是独立分组（按客户 + 币种），不参与任何账龄桶
        var group = Assert.Single(report.UnknownDueDateGroups);
        Assert.Equal(customer.Id, group.CustomerId);
        Assert.Equal("USD", group.Currency);
        Assert.Equal(1, group.StatementCount);
        Assert.Equal(200m, group.StatementAmount);
        Assert.Contains("B002", Assert.Single(group.StatementIdentities));
        Assert.Contains("不计算", group.Note);

        var currencyUnknown = Assert.Single(report.Currencies).UnknownDueDate;
        Assert.False(currencyUnknown.IsAgingBucket);
        Assert.Equal(1, currencyUnknown.StatementCount);
        Assert.Equal(200m, currencyUnknown.StatementAmount);
        Assert.Equal(
            AgencyServiceFeeReconciliationRules.BucketText(AgencyServiceFeeReconciliationRules.UnknownDueDateBucket),
            currencyUnknown.BucketText);

        // 到期日区间筛选只命中「登记了显式到期日」的对账单：未知到期日既不落区间内也不落区间外
        var datedOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(dueDateFrom: AsOf.AddDays(-100), dueDateTo: AsOf));
        Assert.Equal("B001", Assert.Single(datedOnly.Groups.SelectMany(g => g.Statements)).StatementNo);

        // 且系统绝不按对账日期把 B002 补进账龄：只按对账日期筛时它仍在「未知到期日」分组
        var byStatementDate = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(statementDateFrom: AsOf.AddDays(-60), statementDateTo: AsOf));
        Assert.Equal(2, byStatementDate.PageStatementCount);
        Assert.Equal(1, byStatementDate.UnknownDueDatePageStatementCount);
        Assert.Equal(1, byStatementDate.Currencies.Sum(c => c.Buckets.Sum(b => b.StatementCount)));
    }

    // ==================== 3. 算术剩余证据 = 对账单合计 − 有效已分摊 ====================

    [Fact]
    public async Task Remaining_evidence_equals_statement_total_minus_effective_allocations()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var partial = SeedStatement(db, "C001", customer.Id, 113m, dueDate: AsOf.AddDays(-20));
        var full = SeedStatement(db, "C002", customer.Id, 100m, dueDate: AsOf.AddDays(-20));
        var none = SeedStatement(db, "C003", customer.Id, 56.5m, dueDate: AsOf.AddDays(-20));

        var receipt = SeedReceipt(db, "R001", customer.Id, 500m);
        SeedAllocation(db, partial, receipt, 40m);
        SeedAllocation(db, partial, receipt, 23m, allocatedAt: new DateTime(2026, 9, 22));
        SeedAllocation(db, full, receipt, 100m, allocatedAt: new DateTime(2026, 9, 23));
        await db.SaveChangesAsync();

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());

        var partialRow = Row(report, "C001");
        Assert.Equal(113m, partialRow.StatementAmount);
        Assert.Equal(63m, partialRow.ActiveAllocatedAmount);
        Assert.Equal(2, partialRow.ActiveAllocationCount);
        Assert.Equal(1, partialRow.ActiveReceiptCount);              // 同一张收款单两次分摊 → 只算 1 张
        Assert.Equal(50m, partialRow.RemainingAmount);
        Assert.Equal(AgencyServiceFeeReconciliationRules.AllocationPartial, partialRow.AllocationState);
        Assert.Equal(AgencyServiceFeeReconciliationRules.RemainingKnown, partialRow.RemainingState);
        Assert.Contains("部分分摊", partialRow.AllocationStateText);
        Assert.Equal(0, partialRow.VoidedAllocationCount);
        Assert.Equal(0m, partialRow.InvalidAllocationAmount);

        var fullRow = Row(report, "C002");
        Assert.Equal(0m, fullRow.RemainingAmount);
        Assert.Equal(AgencyServiceFeeReconciliationRules.AllocationFull, fullRow.AllocationState);
        Assert.Contains("全额分摊", fullRow.AllocationStateText);

        var noneRow = Row(report, "C003");
        Assert.Equal(0m, noneRow.ActiveAllocatedAmount);
        Assert.Equal(56.5m, noneRow.RemainingAmount);
        Assert.Equal(AgencyServiceFeeReconciliationRules.AllocationNone, noneRow.AllocationState);
        Assert.Contains("证据缺口", noneRow.AllocationStateText);
        Assert.Contains("不代表", noneRow.ActiveAllocationText);
        Assert.False(noneRow.HasInvalidOrUnavailableEvidence);
        Assert.Equal(AgencyServiceFeeReconciliationRules.LinkStateAvailable, noneRow.LinkState);

        // 组/币种汇总 = 有效证据之和（三类数字严格分列）
        var group = Assert.Single(report.Groups);
        Assert.Equal(3, group.ActiveEvidenceStatementCount);
        Assert.Equal(269.5m, group.StatementAmount);
        Assert.Equal(163m, group.ActiveAllocatedAmount);
        Assert.Equal(106.5m, group.RemainingAmount);
    }

    // ==================== 4. 作废分摊只作历史可见，绝不并入有效合计 ====================

    [Fact]
    public async Task Voided_allocations_stay_in_history_but_never_join_effective_totals()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "D001", customer.Id, 500m, dueDate: AsOf.AddDays(-10));
        var receipt = SeedReceipt(db, "R001", customer.Id, 1000m);

        SeedAllocation(db, statement, receipt, 120m);
        var voided = SeedAllocation(db, statement, receipt, 80m,
            status: AgencyServiceFeeCollectionAllocationRules.StatusVoided,
            allocatedAt: new DateTime(2026, 9, 22));
        voided.VoidedAt = new DateTime(2026, 9, 23);
        voided.VoidReason = "分摊对象填写错误，作废后重新登记";
        await db.SaveChangesAsync();

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());
        var row = Row(report, "D001");

        Assert.Equal(120m, row.ActiveAllocatedAmount);      // 已作废 80 不并入
        Assert.Equal(1, row.ActiveAllocationCount);
        Assert.Equal(380m, row.RemainingAmount);
        Assert.Equal(1, row.VoidedAllocationCount);
        Assert.Equal(80m, row.VoidedAllocationAmount);
        Assert.Contains("已作废分摊 1 条", row.HistoricalEvidenceText);
        Assert.Contains("绝不并入有效合计", row.HistoricalEvidenceText);
        Assert.Equal(AgencyServiceFeeReconciliationRules.AllocationPartial, row.AllocationState);

        // 汇总同样只统计有效证据
        Assert.Equal(120m, Assert.Single(report.Groups).ActiveAllocatedAmount);
        Assert.Equal(380m, Assert.Single(report.Groups).RemainingAmount);

        // 原始值保留可读（作废不硬删除、不静默替换）
        var stored = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Single(a => a.Id == voided.Id);
        Assert.Equal(80m, stored.AllocatedAmount);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusVoided, stored.Status);
        Assert.Contains("作废", stored.VoidReason);
    }

    // ==================== 5. 无效 / 无法确认链接保持可见、绝不修复或改派 ====================

    [Fact]
    public async Task Invalid_and_unavailable_links_stay_visible_and_are_never_repaired()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");

        // ① 收款单已取消
        var cancelledStatement = SeedStatement(db, "E001", customer.Id, 300m, dueDate: AsOf.AddDays(-5));
        var cancelledReceipt = SeedReceipt(db, "R001", customer.Id, 500m, status: DocumentStatus.Cancelled);
        SeedAllocation(db, cancelledStatement, cancelledReceipt, 150m);

        // ② 收款单已删除
        var deletedStatement = SeedStatement(db, "E002", customer.Id, 300m, dueDate: AsOf.AddDays(-5));
        var deletedReceipt = SeedReceipt(db, "R002", customer.Id, 500m, deleted: true);
        SeedAllocation(db, deletedStatement, deletedReceipt, 200m);

        // ③ 快照币种与对账单不一致
        var currencyStatement = SeedStatement(db, "E003", customer.Id, 300m, dueDate: AsOf.AddDays(-5));
        var eurReceipt = SeedReceipt(db, "R003", customer.Id, 500m, currency: Currency.EUR);
        SeedAllocation(db, currencyStatement, eurReceipt, 100m, currency: "EUR");

        // ④ 对账单在分摊后被作废（分摊行未随之作废 → 变为无效证据）
        var voidedStatement = SeedStatement(db, "E004", customer.Id, 300m, dueDate: AsOf.AddDays(-5));
        var voidedReceipt = SeedReceipt(db, "R004", customer.Id, 500m);
        SeedAllocation(db, voidedStatement, voidedReceipt, 100m);
        voidedStatement.Status = AgencyServiceFeeStatementRules.StatusVoided;
        voidedStatement.VoidedAt = AsOf;
        voidedStatement.VoidReason = "对账单登记有误，显式作废";
        await db.SaveChangesAsync();

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementStatus: "all"));

        var cancelledRow = Row(report, "E001");
        Assert.Equal(0m, cancelledRow.ActiveAllocatedAmount);
        Assert.Equal(300m, cancelledRow.RemainingAmount);
        Assert.Equal(1, cancelledRow.InvalidAllocationCount);
        Assert.Equal(150m, cancelledRow.InvalidAllocationAmount);
        Assert.True(cancelledRow.HasInvalidOrUnavailableEvidence);
        Assert.Equal(AgencyServiceFeeReconciliationRules.LinkStateUnavailable, cancelledRow.LinkState);
        Assert.Contains("绝不修复", cancelledRow.LinkStateText);
        Assert.Contains("无效", cancelledRow.Note);
        Assert.Equal(AgencyServiceFeeReconciliationRules.AllocationHistoricalOnly, cancelledRow.AllocationState);

        var deletedRow = Row(report, "E002");
        Assert.Equal(0m, deletedRow.ActiveAllocatedAmount);
        Assert.Equal(200m, deletedRow.InvalidAllocationAmount);
        Assert.True(deletedRow.HasInvalidOrUnavailableEvidence);

        var currencyRow = Row(report, "E003");
        Assert.Equal(0m, currencyRow.ActiveAllocatedAmount);
        Assert.Equal(100m, currencyRow.InvalidAllocationAmount);
        Assert.Equal(300m, currencyRow.RemainingAmount);          // 不做汇率换算，也不把 EUR 当作 USD

        var voidedRow = Row(report, "E004");
        Assert.True(voidedRow.IsVoided);
        Assert.Contains("已作废", voidedRow.Note);
        Assert.Equal(100m, voidedRow.InvalidAllocationAmount);    // 对账单作废后分摊证据变为无效证据

        // 无效证据留在行内可见、绝不并入有效合计，也不被修复或改派
        Assert.Equal(0m, report.Groups.Sum(g => g.ActiveAllocatedAmount ?? 0m));
        Assert.Equal(4, report.InvalidEvidencePageStatementCount);
        Assert.Equal(0, report.OverAllocatedPageStatementCount);
        var storedReceipt = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == cancelledReceipt.Id);
        Assert.Equal(DocumentStatus.Cancelled, storedReceipt.Status);
        Assert.False(storedReceipt.IsDeleted);
        Assert.Equal(1, db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Count(a => a.StatementId == cancelledStatement.Id && !a.IsDeleted));
    }

    // ==================== 6. 草稿 / 已作废对账单：单独标注、排除在有效合计之外 ====================

    [Fact]
    public async Task Draft_and_voided_statements_are_labeled_and_excluded_from_active_totals()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "F001", customer.Id, 600m, dueDate: AsOf.AddDays(-30));
        SeedStatement(db, "F002", customer.Id, 200m, dueDate: AsOf.AddDays(-30),
            status: AgencyServiceFeeStatementRules.StatusDraft);
        SeedStatement(db, "F003", customer.Id, 100m, dueDate: AsOf.AddDays(-30),
            status: AgencyServiceFeeStatementRules.StatusVoided);

        // 默认只返回已登记（未作废）证据：草稿与已作废需显式选择状态筛选才可见
        var recordedOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());
        Assert.Equal(1, recordedOnly.Total);
        Assert.Equal(1, recordedOnly.ActiveEvidencePageStatementCount);
        Assert.Equal(0, recordedOnly.DraftPageStatementCount);
        Assert.Equal(0, recordedOnly.VoidedPageStatementCount);
        Assert.Equal(600m, Assert.Single(recordedOnly.Currencies).StatementAmount);

        var all = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementStatus: "all"));
        Assert.Equal(3, all.PageStatementCount);
        Assert.Equal(1, all.ActiveEvidencePageStatementCount);
        Assert.Equal(1, all.DraftPageStatementCount);
        Assert.Equal(1, all.VoidedPageStatementCount);

        var draft = Row(all, "F002");
        Assert.True(draft.IsDraft);
        Assert.False(draft.IsActiveEvidence);
        Assert.Contains("草稿", draft.StatementStatusText);
        Assert.Contains("不并入有效对账合计", draft.Note);

        var voided = Row(all, "F003");
        Assert.True(voided.IsVoided);
        Assert.False(voided.IsActiveEvidence);
        Assert.Contains("已作废", voided.StatementStatusText);

        // 有效合计与账龄桶合计只统计已登记证据（草稿 200 + 已作废 100 都不进）
        Assert.Equal(600m, Assert.Single(all.Currencies).StatementAmount);
        Assert.Equal(600m, all.Currencies.Sum(c => c.Buckets.Sum(b => b.StatementAmount)));
        Assert.Equal(1, all.Currencies.Sum(c => c.Buckets.Sum(b => b.StatementCount)));
        Assert.Equal(600m, Assert.Single(all.Groups).StatementAmount);

        // 草稿 / 已作废筛选各自可见（历史证据单独可读）
        var drafts = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementStatus: "draft"));
        Assert.Equal("F002", Assert.Single(drafts.Groups.SelectMany(g => g.Statements)).StatementNo);
        var voidedOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementStatus: "voided"));
        Assert.Equal("F003", Assert.Single(voidedOnly.Groups.SelectMany(g => g.Statements)).StatementNo);
    }

    // ==================== 7. 多客户多币种：分别成行、绝无跨币种总额 ====================

    [Fact]
    public async Task Multiple_customers_and_currencies_stay_separate_with_no_cross_currency_total()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "杭州贸易");

        SeedStatement(db, "G001", customerA.Id, 100m, currency: "USD", dueDate: AsOf.AddDays(-10));
        SeedStatement(db, "G002", customerA.Id, 200m, currency: "CNY", dueDate: AsOf.AddDays(-10));
        SeedStatement(db, "G003", customerB.Id, 300m, currency: "USD", dueDate: AsOf.AddDays(-10));

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());

        // 分组：客户 × 币种（3 组），组内金额只统计本组币种
        Assert.Equal(3, report.Groups.Count);
        Assert.Equal(2, report.Currencies.Count);
        Assert.Equal(100m, report.Groups.Single(g => g.Currency == "USD" && g.CustomerId == customerA.Id).StatementAmount);
        Assert.Equal(200m, report.Groups.Single(g => g.Currency == "CNY" && g.CustomerId == customerA.Id).StatementAmount);
        Assert.Equal(300m, report.Groups.Single(g => g.Currency == "USD" && g.CustomerId == customerB.Id).StatementAmount);

        // 币种汇总：USD 行 = 两个客户之和（同一币种），CNY 行独立；**没有任何跨币种总额字段**
        Assert.Equal(400m, report.Currencies.Single(c => c.Currency == "USD").StatementAmount);
        Assert.Equal(200m, report.Currencies.Single(c => c.Currency == "CNY").StatementAmount);

        var reportNames = typeof(AgencyServiceFeeReconciliationReport)
            .GetProperties().Select(p => p.Name).ToList();
        var summaryNames = typeof(AgencyServiceFeeReconciliationCurrencySummary)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(reportNames, n => n.Contains("TotalAmount", StringComparison.Ordinal)
            || n.Contains("CrossCurrencyTotal", StringComparison.Ordinal)
            || n.Contains("GrandTotal", StringComparison.Ordinal));
        Assert.DoesNotContain(summaryNames, n => n.Contains("CrossCurrencyTotal", StringComparison.Ordinal)
            || n.Contains("GrandTotal", StringComparison.Ordinal)
            || n.Contains("TotalAmount", StringComparison.Ordinal));

        // 币种筛选只返回该币种（绝不把另一币种并入）
        var usdOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(currency: "USD"));
        Assert.Equal(2, usdOnly.PageStatementCount);
        Assert.Equal(400m, Assert.Single(usdOnly.Currencies).StatementAmount);

        // 客户筛选只返回该客户（不同客户绝不合并）
        var customerOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(customerId: customerB.Id));
        Assert.Single(customerOnly.Groups);
        Assert.Equal(300m, customerOnly.Groups[0].StatementAmount);
    }

    // ==================== 8. 超过对账单合计：按无效证据显示，绝不轧为 0 ====================

    [Fact]
    public async Task Over_allocated_evidence_is_reported_as_invalid_without_clamping()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "H001", customer.Id, 100m, dueDate: AsOf.AddDays(-10));
        var receipt = SeedReceipt(db, "R001", customer.Id, 1000m);

        // 与 ERP-071 源规则矛盾的持久化数据（分摊 120 > 合计 100）：工作台照实显示，不修复、不轧为 0
        SeedAllocation(db, statement, receipt, 120m, statementTotalOverride: 120m);

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());
        var row = Row(report, "H001");

        Assert.Equal(AgencyServiceFeeReconciliationRules.AllocationOverAllocated, row.AllocationState);
        Assert.Equal(AgencyServiceFeeReconciliationRules.RemainingOverAllocated, row.RemainingState);
        Assert.Null(row.RemainingAmount);
        Assert.Contains("绝不轧为 0", row.RemainingStateText);
        Assert.True(row.HasInvalidOrUnavailableEvidence);
        Assert.Equal(1, report.OverAllocatedPageStatementCount);
        Assert.Null(Assert.Single(report.Groups).RemainingAmount);
    }

    // ==================== 9. as-of 日期驱动账龄并被回显 ====================

    [Fact]
    public async Task Explicit_as_of_date_drives_aging_and_is_echoed()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "I001", customer.Id, 100m, dueDate: new DateTime(2026, 9, 1));

        var closer = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(asOf: new DateTime(2026, 9, 10)));
        Assert.Equal(new DateTime(2026, 9, 10), closer.AsOfDate);
        Assert.Contains("2026-09-10", closer.AsOfDateText);
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdue1To30, Row(closer, "I001").AgingBucket);
        Assert.Equal(9, Row(closer, "I001").OverdueDays);

        var later = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(asOf: new DateTime(2026, 12, 1)));
        Assert.Equal(AgencyServiceFeeReconciliationRules.BucketOverdueOver90, Row(later, "I001").AgingBucket);
        Assert.Equal(91, Row(later, "I001").OverdueDays);

        // 非法 as-of：直接拒绝（不静默回退到当天）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(asOf: new DateTime(1800, 1, 1))));
    }

    // ==================== 10. 筛选全部只用持久化字段；非法取值直接拒绝 ====================

    [Fact]
    public async Task Filters_use_persisted_fields_only_and_reject_invalid_values()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "杭州贸易");
        var agreementA = SeedAgreement(db, "ASF-2026-001", customerA.Id, "USD");
        var agreementB = SeedAgreement(db, "ASF-2026-002", customerB.Id, "CNY");

        var partial = SeedStatement(db, "J001", customerA.Id, 100m, currency: "USD",
            dueDate: new DateTime(2026, 9, 20), statementDate: new DateTime(2026, 9, 1),
            customerCode: "C001", customerName: "义乌进出口",
            agreementId: agreementA.Id, agreementNo: agreementA.AgreementNo);
        var full = SeedStatement(db, "J002", customerA.Id, 100m, currency: "USD",
            dueDate: new DateTime(2026, 8, 31), statementDate: new DateTime(2026, 8, 15),
            customerCode: "C001", customerName: "义乌进出口",
            agreementId: agreementA.Id, agreementNo: agreementA.AgreementNo);
        var none = SeedStatement(db, "J003", customerA.Id, 100m, currency: "USD",
            dueDate: new DateTime(2026, 8, 31), statementDate: new DateTime(2026, 8, 15),
            customerCode: "C001", customerName: "义乌进出口",
            agreementId: agreementA.Id, agreementNo: agreementA.AgreementNo);
        var historyOnly = SeedStatement(db, "J004", customerA.Id, 100m, currency: "USD",
            dueDate: new DateTime(2026, 8, 31), statementDate: new DateTime(2026, 8, 15),
            customerCode: "C001", customerName: "义乌进出口",
            agreementId: agreementA.Id, agreementNo: agreementA.AgreementNo);
        SeedStatement(db, "J005", customerB.Id, 500m, currency: "CNY",
            dueDate: new DateTime(2026, 9, 30), statementDate: new DateTime(2026, 9, 5),
            customerCode: "C002", customerName: "杭州贸易",
            agreementId: agreementB.Id, agreementNo: agreementB.AgreementNo);

        Assert.NotNull(none);
        SeedLine(db, partial.Id, 1, AgencyServiceFeeStatementRules.SourceTypeSalesOrder, 7001L, "SO-2026-001");
        SeedLine(db, historyOnly.Id, 1, AgencyServiceFeeStatementRules.SourceTypeLoadingList, 7002L, "LL-2026-001");
        var receipt = SeedReceipt(db, "R001", customerA.Id, 1000m);
        SeedAllocation(db, partial, receipt, 40m);
        SeedAllocation(db, full, receipt, 100m, allocatedAt: new DateTime(2026, 9, 22));
        SeedAllocation(db, historyOnly, receipt, 10m,
            status: AgencyServiceFeeCollectionAllocationRules.StatusVoided,
            allocatedAt: new DateTime(2026, 9, 23));

        // 对账单身份（精确 Id）
        var byId = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementId: partial.Id));
        Assert.Equal("J001", Assert.Single(byId.Groups.SelectMany(g => g.Statements)).StatementNo);

        // 客户 / 协议 / 币种 / 服务来源
        var byCustomer = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(customerId: customerB.Id));
        Assert.Equal("J005", Assert.Single(byCustomer.Groups.SelectMany(g => g.Statements)).StatementNo);
        var byAgreement = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(agreementId: agreementB.Id));
        Assert.Equal("J005", Assert.Single(byAgreement.Groups.SelectMany(g => g.Statements)).StatementNo);
        var byCurrency = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(currency: "CNY"));
        Assert.Equal("J005", Assert.Single(byCurrency.Groups.SelectMany(g => g.Statements)).StatementNo);
        var bySource = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(sourceType: AgencyServiceFeeStatementRules.SourceTypeLoadingList));
        Assert.Equal("J004", Assert.Single(bySource.Groups.SelectMany(g => g.Statements)).StatementNo);

        // 对账日期区间（含当天）
        var byDate = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(statementDateFrom: new DateTime(2026, 8, 15), statementDateTo: new DateTime(2026, 8, 15)));
        Assert.Equal(3, byDate.PageStatementCount);

        // 显式到期日区间只命中登记了到期日的对账单
        var byDue = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(dueDateFrom: new DateTime(2026, 9, 1), dueDateTo: new DateTime(2026, 9, 30)));
        Assert.Equal(2, byDue.PageStatementCount);

        // 关键字（对账单号 / 客户编码 / 客户名称 / 协议号 / 备注）
        var byKeyword = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(keyword: "杭州"));
        Assert.Equal("J005", Assert.Single(byKeyword.Groups.SelectMany(g => g.Statements)).StatementNo);

        // 分配状态：none / historical_only / partial / full（全部只用持久化分摊行判定）
        var noneOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(allocationState: AgencyServiceFeeReconciliationRules.AllocationNone));
        var noneNos = noneOnly.Groups.SelectMany(g => g.Statements).Select(r => r.StatementNo).ToList();
        Assert.Contains("J003", noneNos);
        Assert.Contains("J005", noneNos);
        Assert.DoesNotContain("J001", noneNos);
        Assert.DoesNotContain("J004", noneNos);

        var historicalOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(allocationState: AgencyServiceFeeReconciliationRules.AllocationHistoricalOnly));
        Assert.Equal("J004", Assert.Single(historicalOnly.Groups.SelectMany(g => g.Statements)).StatementNo);

        var partialOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(allocationState: AgencyServiceFeeReconciliationRules.AllocationPartial));
        Assert.Equal("J001", Assert.Single(partialOnly.Groups.SelectMany(g => g.Statements)).StatementNo);

        var fullOnly = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(allocationState: AgencyServiceFeeReconciliationRules.AllocationFull));
        Assert.Equal("J002", Assert.Single(fullOnly.Groups.SelectMany(g => g.Statements)).StatementNo);

        // 非法取值一律拒绝（不静默兜底、不忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementStatus: "unknown")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(allocationState: "over_allocated")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(sourceType: "purchase-order")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(currency: "XYZ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementId: -1)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db,
                Query(statementDateFrom: AsOf, statementDateTo: AsOf.AddDays(-1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db,
                Query(dueDateFrom: AsOf, dueDateTo: AsOf.AddDays(-1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(keyword: new string('K', 101))));

        // 分页参数在归一化时钳制到有界范围（单页上限 200）
        var clamped = Query(page: 0, pageSize: 5000);
        clamped.Normalize();
        Assert.Equal(1, clamped.Page);
        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxPageSize, clamped.PageSize);
    }

    // ==================== 11. 有界读取：数据集访问次数与行数无关，且全程不写库 ====================

    [Fact]
    public async Task Queries_use_a_bounded_number_of_dataset_reads_and_never_write()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "K001", customer.Id, 10m, dueDate: AsOf.AddDays(-1));
        var receipt = SeedReceipt(db, "R001", customer.Id, 100m);
        SeedAllocation(db, statement, receipt, 5m);

        var counting = AgencyServiceFeeStatementTests.StatementReadCounter.Wrap(db);
        var single = await AgencyServiceFeeReconciliationService.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        Assert.Equal(1, single.PageStatementCount);
        Assert.Equal(5m, Assert.Single(single.Groups.SelectMany(g => g.Statements)).ActiveAllocatedAmount);
        // 固定 8 次数据集访问：对账单（筛选 + 分页 / 计数 1 次 + 本页装载 1 次）+ 分摊行（按状态聚合、
        // 有效分摊聚合、有效分摊（对账单, 收款单）组合）3 次 + 本页客户 + 本页协议 + 来源行 2 次
        Assert.Equal(8, singleReads);

        // 再补 300 张（跨多页）：数据集访问次数必须保持不变（无 N+1、无逐行查库）
        for (var i = 2; i <= 301; i++)
            SeedStatement(db, $"K{i:000}", customer.Id, 1m, dueDate: AsOf.AddDays(-1));

        var large = await AgencyServiceFeeReconciliationService.ForQueryAsync(counting.Proxy, Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxPageSize, large.PageStatementCount);
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                       // 只读：没有一次 SaveChanges
        Assert.Equal(200m, Assert.Single(large.Groups).StatementAmount);
    }

    // ==================== 12. 只读：不改写对账单 / 分摊行 / 收款单 / 客户 / 协议 / 来源行 ====================

    [Fact]
    public async Task Workspace_is_read_only_and_never_mutates_sources()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var disabledCustomer = SeedCustomer(db, "C002", "已停用客户", status: 0);
        var agreement = SeedAgreement(db, "ASF-2026-001", customer.Id, "USD");
        var statement = SeedStatement(db, "L001", customer.Id, 200m, dueDate: AsOf.AddDays(-30),
            agreementId: agreement.Id, agreementNo: agreement.AgreementNo);
        var disabledStatement = SeedStatement(db, "L002", disabledCustomer.Id, 50m, dueDate: AsOf.AddDays(-30),
            customerCode: "C002", customerName: "已停用客户",
            agreementId: agreement.Id, agreementNo: agreement.AgreementNo);
        SeedLine(db, statement.Id, 1, AgencyServiceFeeStatementRules.SourceTypeSalesOrder, 8001L, "SO-2026-009");
        var receipt = SeedReceipt(db, "R001", customer.Id, 500m);
        SeedAllocation(db, statement, receipt, 20m);
        SeedAllocation(db, disabledStatement, receipt, 30m, customerId: disabledCustomer.Id,
            allocatedAt: new DateTime(2026, 9, 22));
        SeedAuthorization(db, AuthorizedUserId, AgencyServiceFeeReconciliationRules.RequiredMenuCode);

        var before = Snapshot(db);

        await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementStatus: "all"));
        await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(allocationState: AgencyServiceFeeReconciliationRules.AllocationPartial));
        await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(currency: "USD", keyword: "L00"));
        await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(customerId: disabledCustomer.Id));
        await AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, statement.Id, AuthorizedUserId, AsOf);

        Assert.Equal(before, Snapshot(db));

        // 停用客户的历史快照照常可读，且系统不「修复」它（不回填名称、不改状态）
        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db,
            Query(customerId: disabledCustomer.Id));
        var row = Assert.Single(report.Groups.SelectMany(g => g.Statements));
        Assert.Equal("已停用客户", row.CustomerName);
        Assert.False(row.CustomerAvailable);
        Assert.Contains("停用", row.CustomerAvailabilityText);
        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 13. 明细：重新校验身份与既有模块授权；来源失效一律 fail closed ====================

    [Fact]
    public async Task Detail_revalidates_authentication_and_module_authorization_and_fails_closed()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "M001", customer.Id, 300m, dueDate: AsOf.AddDays(-5));
        var receipt = SeedReceipt(db, "R001", customer.Id, 500m);
        var active = SeedAllocation(db, statement, receipt, 120m);
        var voided = SeedAllocation(db, statement, receipt, 80m,
            status: AgencyServiceFeeCollectionAllocationRules.StatusVoided,
            allocatedAt: new DateTime(2026, 9, 22));
        SeedAuthorization(db, AuthorizedUserId, AgencyServiceFeeReconciliationRules.RequiredMenuCode);
        SeedAuthorization(db, NoMenuUserId);                          // 有角色但无菜单授权
        SeedAuthorization(db, WrongMenuUserId, "finance-payment");    // 只有其它模块授权
        await db.SaveChangesAsync();

        // 未认证：拒绝，不返回任何证据
        var unauthenticated = await AssertBusinessAsync(ErrorCodes.Unauthorized,
            () => AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, statement.Id, null, AsOf));
        Assert.Contains("登录", unauthenticated.Message);

        // 无该模块授权 / 只有其它模块授权：拒绝（fail closed）
        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, statement.Id, NoMenuUserId, AsOf));
        var forbidden = await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, statement.Id, WrongMenuUserId, AsOf));
        Assert.Contains(AgencyServiceFeeReconciliationRules.RequiredMenuCode, forbidden.Message);
        Assert.Contains("fail closed", forbidden.Message);

        // 已授权：返回与列表同源的行 + 有界分摊明细（含已作废历史）
        var detail = await AgencyServiceFeeReconciliationService.ForStatementDetailAsync(
            db, statement.Id, AuthorizedUserId, AsOf);
        var row = detail.Row;
        Assert.Equal(120m, row.ActiveAllocatedAmount);
        Assert.Equal(180m, row.RemainingAmount);
        Assert.Equal(2, detail.Allocations.Count);
        Assert.Equal(1, detail.Allocations.Count(a => a.IsEffective));
        Assert.False(detail.AllocationsTruncated);
        Assert.Contains("重新校验", detail.AuthorizationNote);
        var effectiveRow = detail.Allocations.Single(a => a.AllocationId == active.Id);
        Assert.True(effectiveRow.IsEffective);
        Assert.Contains("计入有效已分摊合计", effectiveRow.EffectivenessText);
        var voidedRow = detail.Allocations.Single(a => a.AllocationId == voided.Id);
        Assert.False(voidedRow.IsEffective);
        Assert.Contains("已作废", voidedRow.EffectivenessText);
        Assert.Contains("历史可见", voidedRow.EffectivenessText);

        // 与列表同源：同一张对账单的列表行数字与明细行完全一致
        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementId: statement.Id));
        var listRow = Assert.Single(report.Groups.SelectMany(g => g.Statements));
        Assert.Equal(listRow.ActiveAllocatedAmount, row.ActiveAllocatedAmount);
        Assert.Equal(listRow.RemainingAmount, row.RemainingAmount);
        Assert.Equal(listRow.AllocationState, row.AllocationState);
        Assert.Equal(listRow.AgingBucket, row.AgingBucket);

        // 非法 Id 直接拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, 0, AuthorizedUserId, AsOf));

        // 来源已删除：拒绝（fail closed），不返回部分证据
        statement.IsDeleted = true;
        await db.SaveChangesAsync();
        var missing = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, statement.Id, AuthorizedUserId, AsOf));
        Assert.Contains("fail closed", missing.Message);
        statement.IsDeleted = false;
        await db.SaveChangesAsync();

        // 授权回收后立即收敛（每次请求都重新查询授权）
        RevokeAllMenus(db, AuthorizedUserId);
        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => AgencyServiceFeeReconciliationService.ForStatementDetailAsync(db, statement.Id, AuthorizedUserId, AsOf));
        RestoreAllMenus(db, AuthorizedUserId);
        var restored = await AgencyServiceFeeReconciliationService.ForStatementDetailAsync(
            db, statement.Id, AuthorizedUserId, AsOf);
        Assert.Equal(120m, restored.Row.ActiveAllocatedAmount);
    }

    // ==================== 14. 明细清单有界，但聚合合计仍然精确 ====================

    [Fact]
    public async Task Detail_truncates_display_list_without_losing_aggregate_totals()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "N001", customer.Id, 2000m, dueDate: AsOf.AddDays(-5));
        var receipt = SeedReceipt(db, "R001", customer.Id, 5000m);
        SeedAuthorization(db, AuthorizedUserId, AgencyServiceFeeReconciliationRules.RequiredMenuCode);

        // 与 ERP-071 单侧 50 条有效行规则矛盾的持久化数据（105 条）：展示清单有界，合计仍然精确
        for (var i = 0; i < 105; i++)
        {
            SeedAllocation(db, statement, receipt, 10m, allocatedAt: new DateTime(2026, 9, 20).AddMinutes(i));
        }

        var detail = await AgencyServiceFeeReconciliationService.ForStatementDetailAsync(
            db, statement.Id, AuthorizedUserId, AsOf);

        Assert.True(detail.AllocationsTruncated);
        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxDetailsPerStatement, detail.Allocations.Count);
        Assert.Equal(105, detail.Row.ActiveAllocationCount);
        Assert.Equal(1050m, detail.Row.ActiveAllocatedAmount);
        Assert.Equal(950m, detail.Row.RemainingAmount);

        // 列表同样给出精确聚合（不因展示上限而给部分合计）
        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query(statementId: statement.Id));
        var listRow = Assert.Single(report.Groups.SelectMany(g => g.Statements));
        Assert.Equal(1050m, listRow.ActiveAllocatedAmount);
        Assert.Equal(950m, listRow.RemainingAmount);
        Assert.Equal(1050m, Assert.Single(report.Groups).ActiveAllocatedAmount);
    }

    // ==================== 15. 来源身份清单有界，但来源计数仍然精确 ====================

    [Fact]
    public async Task Source_identities_are_bounded_while_counts_stay_exact()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "O001", customer.Id, 800m, dueDate: AsOf.AddDays(-5));

        for (var i = 1; i <= 8; i++)
        {
            SeedLine(db, statement.Id, i, AgencyServiceFeeStatementRules.SourceTypeSalesOrder,
                9000L + i, $"SO-2026-{i:000}", amount: 100m);
        }

        var report = await AgencyServiceFeeReconciliationService.ForQueryAsync(db, Query());
        var row = Row(report, "O001");

        Assert.Equal(8, row.LineCount);
        Assert.True(row.SourceTruncated);
        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxSourceIdentitiesPerRow, row.SourceIdentities.Count);
        Assert.Contains("×8", row.SourceSummaryText);
        Assert.Contains("计数完整", row.SourceSummaryText);
        Assert.Contains("销售订单", row.SourceSummaryText);
        Assert.Equal(1, report.SourceTruncatedPageStatementCount);
    }

    // ==================== 16. 元数据：白名单、有界额度与口径文案齐备 ====================

    [Fact]
    public void Metadata_exposes_buckets_filters_and_bounds()
    {
        var metadata = AgencyServiceFeeReconciliationService.GetMetadata();

        Assert.Equal(AgencyServiceFeeReconciliationRules.SupportedBuckets.ToList(), metadata.SupportedBuckets);
        Assert.Equal(5, metadata.SupportedBuckets.Count);
        Assert.Equal(
            AgencyServiceFeeReconciliationRules.SupportedAllocationFilters.ToList(),
            metadata.SupportedAllocationFilters);
        Assert.Equal(
            AgencyServiceFeeReconciliationRules.SupportedStatementStatuses.ToList(),
            metadata.SupportedStatementStatuses);
        Assert.Equal(AgencyServiceFeeStatementRules.SupportedSourceTypes.ToList(), metadata.SupportedSourceTypes);
        Assert.Equal(AgencyServiceFeeReconciliationRules.SupportedCurrencies.ToList(), metadata.SupportedCurrencies);

        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxPageSize, metadata.MaxPageSize);
        Assert.Equal(AgencyServiceFeeReconciliationRules.DefaultPageSize, metadata.DefaultPageSize);
        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxAllocationRowsPerPage, metadata.MaxAllocationRowsPerPage);
        Assert.Equal(AgencyServiceFeeReconciliationRules.MaxDetailsPerStatement, metadata.MaxDetailsPerStatement);
        Assert.Equal("customer", metadata.RequiredMenuCode);
        Assert.Equal("客户资料", metadata.RequiredMenuText);

        Assert.Contains("只读的代理服务费对账与账龄视图", metadata.RuleText);
        Assert.Contains("只对", metadata.AgingRuleText);
        Assert.Contains("绝不", metadata.NoCrossCurrencyText);
        Assert.Contains("固定次数数据集访问", metadata.ScopeText);
        Assert.Contains("不是", metadata.BoundaryText);
        Assert.Contains("不计算", metadata.UnknownDueDateNote);
        Assert.Contains("保持可见", metadata.InvalidEvidenceNote);
        Assert.Contains("fail closed", metadata.SourceFailClosedText);
        Assert.Contains("完全同源", metadata.ExportNote);
        Assert.Contains("绝不相加", metadata.DimensionSeparationText);
        Assert.Contains("永不并入", metadata.HistoricalEvidenceText);
        Assert.Contains("全程只读", metadata.ReadOnlyText);
    }

    // ==================== 17. 结构契约：不新增任何表或列，全程只读 ====================

    [Fact]
    public void 结构契约_工作台不新增任何表或列且源码中没有任何写库调用()
    {
        // 上下文模型契约：没有任何「对账 / 账龄」新模型（工作台是既有证据之上的纯只读派生）
        var sets = typeof(ERP.Application.Interfaces.IErpDbContext)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(sets, n => n.Contains("Reconciliation", StringComparison.Ordinal));
        Assert.DoesNotContain(sets, n => n.Contains("Aging", StringComparison.Ordinal));
        Assert.Contains("AgencyServiceFeeStatements", sets);
        Assert.Contains("AgencyServiceFeeStatementLines", sets);
        Assert.Contains("AgencyServiceFeeCollectionAllocations", sets);

        // 幂等升级脚本不含本工作台的任何建表 / 建列 / 回填语句（本轮未改 SchemaUpgrader）
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.DoesNotContain("AgencyServiceFeeReconciliation", script);
        Assert.DoesNotContain("ReconciliationAging", script);

        // 服务源码：没有任何写库调用（只读派生）
        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "AgencyServiceFeeReconciliationService.cs"));
        Assert.DoesNotContain("SaveChanges", service);
        Assert.DoesNotContain("AddAsync", service);
        Assert.DoesNotContain("RemoveAsync", service);
        Assert.DoesNotContain("ExecuteUpdate", service);
        Assert.DoesNotContain("ExecuteDelete", service);
        Assert.DoesNotContain("BeginTransaction", service);
        Assert.Contains("不新增 / 不修改任何表与列", service);

        // 控制器：只有 GET（打开明细重新校验授权），没有任何写入口
        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "AgencyServiceFeeReconciliationController.cs"));
        Assert.Contains("[Route(\"api/agency-service-fee-reconciliation\")]", controller);
        Assert.Contains("[HttpGet]", controller);
        Assert.Contains("[HttpGet(\"metadata\")]", controller);
        Assert.Contains("statements/{statementId:long}/detail", controller);
        Assert.Contains("[Authorize]", controller);
        Assert.DoesNotContain("[HttpPost", controller);
        Assert.DoesNotContain("[HttpPut", controller);
        Assert.DoesNotContain("[HttpDelete", controller);
        Assert.Contains("fail closed", controller);
    }

    // ==================== 18. 请求契约与前端 / 路由接线契约 ====================

    [Fact]
    public void 请求契约_只有只读筛选字段_前端与路由接线契约齐备()
    {
        // 查询 DTO 只有只读筛选字段：不接受任何金额 / 合计 / 写库字段
        var names = typeof(AgencyServiceFeeReconciliationQuery)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Amount", StringComparison.Ordinal)
            || n.Contains("Total", StringComparison.Ordinal)
            || n.Contains("Balance", StringComparison.Ordinal)
            || n.Contains("Remark", StringComparison.Ordinal)
            || n.Contains("CustomerCode", StringComparison.Ordinal));
        Assert.Contains("AsOfDate", names);
        Assert.Contains("AllocationState", names);
        Assert.Contains("StatementStatus", names);

        // 前端注册与菜单入口（工具栏入口带括号，行操作只写函数名 → 渲染时注入行 Id）
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/agency-service-fee-reconciliation.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("onclick: 'openAgencyServiceFeeReconciliationWorkspace()'", modules);
        Assert.Contains("onclick: 'openAgencyServiceFeeReconciliationWorkspace'", modules);

        var js = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "agency-service-fee-reconciliation.js"));
        Assert.Contains("async function openAgencyServiceFeeReconciliationWorkspace", js);
        Assert.Contains("'/api/agency-service-fee-reconciliation'", js);
        Assert.Contains("/statements/", js);
        Assert.Contains("/detail", js);
        Assert.Contains("loadAgencyServiceFeeReconciliation", js);
        Assert.Contains("exportAgencyServiceFeeReconciliationCsv", js);
        Assert.Contains("showAgencyServiceFeeReconciliationDetail", js);
        Assert.Contains("无跨币种总额", js);
        Assert.Contains("未知到期日", js);
        Assert.Contains("fail closed", js);
        Assert.Contains("不是", js);
        Assert.Contains("付款通知", js);
        Assert.Contains("不并入有效合计", js);
    }

    /// <summary>来源记录快照（验证工作台全程只读、不改写任何来源）</summary>
    private static string Snapshot(ErpDbContext db)
        => string.Join("|", db.AgencyServiceFeeStatements.AsNoTracking().OrderBy(s => s.Id).Select(s =>
                $"{s.Id}:{s.Status}:{s.TotalAmount}:{s.DueDate}:{s.VoidReason}:{s.IsDeleted}"))
           + "||" + string.Join("|", db.AgencyServiceFeeStatementLines.AsNoTracking().OrderBy(l => l.Id)
               .Select(l => $"{l.Id}:{l.SourceType}:{l.SourceNo}:{l.Amount}:{l.IsDeleted}"))
           + "||" + string.Join("|", db.AgencyServiceFeeCollectionAllocations.AsNoTracking().OrderBy(a => a.Id)
               .Select(a => $"{a.Id}:{a.Status}:{a.AllocatedAmount}:{a.ReceiptId}:{a.VoidReason}:{a.IsDeleted}"))
           + "||" + string.Join("|", db.FinanceReceipts.AsNoTracking().OrderBy(r => r.Id)
               .Select(r => $"{r.Id}:{r.Amount}:{r.Status}:{r.IsDeleted}"))
           + "||" + string.Join("|", db.BaseCustomers.AsNoTracking().OrderBy(c => c.Id)
               .Select(c => $"{c.Id}:{c.CustomerName}:{c.Status}:{c.IsDeleted}"))
           + "||" + string.Join("|", db.AgencyServiceFeeAgreements.AsNoTracking().OrderBy(a => a.Id)
               .Select(a => $"{a.Id}:{a.Status}:{a.RatePercent}:{a.IsDeleted}"));

    /// <summary>播种「角色 → 菜单」授权（既有口径）：只授予传入的菜单编码；空数组 = 有角色但无菜单</summary>
    private static void SeedAuthorization(ErpDbContext db, long userId, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"对账工作台测试角色 {userId}",
            RoleCode = $"ASFR-{userId}",
            Description = "ERP-072 授权测试",
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
}
