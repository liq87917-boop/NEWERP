using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费**对账与账龄工作台**（ERP-072，只读派生）：在 ERP-070 的持久化对账单证据与
/// ERP-071 的持久化「客户收款 → 代理服务费对账单」分摊行之上派生
/// ①对账单合计证据、②**有效**收款分摊证据、③算术剩余证据，并按**显式到期日**与**显式 as-of 日期**
/// 计算互斥账龄桶（未登记到期日的对账单进入独立的「未知到期日」分组）。
/// <para>关键口径（与 <see cref="AgencyServiceFeeReconciliationRules"/> 同源）：</para>
/// <list type="bullet">
/// <item><strong>不新增 / 不修改任何表与列</strong>，不做任何回填、不写库；</item>
/// <item>有效分摊 = 未作废 + 对账单当前仍为已登记 + 收款单当前仍可读且未取消 + 客户与币种快照自洽；
/// 已作废行与无效行**保留可见但绝不并入**有效合计；</item>
/// <item>不同币种**分别成行**，绝不合并 / 换算，模型**没有任何跨币种总额字段**；</item>
/// <item>读取为**固定次数**数据集访问（不随行数增长、无逐行查库）；</item>
/// <item>打开明细时重新校验登录身份与既有「角色 → 菜单」模块授权，来源失效一律 fail closed。</item>
/// </list>
/// <para>边界：本服务只读，不开票、不记账、不核销、不收款或付款、不催收或联系客户、不调用任何外部服务，
/// 也不改写对账单证据、分摊行、收款单、协议、客户、订单、装柜清单、单证、发票、库存、费用与结算记录。</para>
/// </summary>
public static class AgencyServiceFeeReconciliationService
{
    // ==================== 0. 元数据（只读） ====================

    /// <summary>模块元数据（白名单、有界额度与口径文案；与界面 / 文档同源）</summary>
    public static AgencyServiceFeeReconciliationMetadataDto GetMetadata() => new()
    {
        SupportedBuckets = AgencyServiceFeeReconciliationRules.SupportedBuckets.ToList(),
        SupportedAllocationFilters = AgencyServiceFeeReconciliationRules.SupportedAllocationFilters.ToList(),
        SupportedStatementStatuses = AgencyServiceFeeReconciliationRules.SupportedStatementStatuses.ToList(),
        SupportedSourceTypes = AgencyServiceFeeStatementRules.SupportedSourceTypes.ToList(),
        SupportedCurrencies = AgencyServiceFeeReconciliationRules.SupportedCurrencies.ToList(),
    };

    /// <summary>
    /// 工作台报表（只读派生，分页有界）：筛选（客户 / 协议 / 对账单身份 / 服务来源 / 对账日期区间 /
    /// 显式到期日区间 / 币种 / 状态 / 分配状态，全部只用持久化字段）→ 分页 → 本页对账单一次性批量装载派生证据。
    /// <para>汇总、账龄桶与币种汇总都只统计**本次返回页**；账龄只按显式到期日与显式 as-of 日期计算。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeReconciliationReport> ForQueryAsync(
        IErpDbContext db, AgencyServiceFeeReconciliationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var asOfDate = query.AsOfDate ?? DateTime.Today;
        var statementStatus = query.StatementStatus ?? AgencyServiceFeeReconciliationRules.StatementStatusRecorded;

        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();

        var pageIds = await source
            .OrderBy(s => s.CustomerId)
            .ThenBy(s => s.Currency)
            .ThenBy(s => s.DueDate)
            .ThenByDescending(s => s.StatementDate)
            .ThenByDescending(s => s.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(s => s.Id)
            .ToListAsync();

        var statements = new List<AgencyServiceFeeStatement>();
        if (pageIds.Count > 0)
        {
            // 分页已定：只为本页对账单装载实体，再按分页顺序还原（避免对全部匹配行做无界装载）
            var loaded = await db.AgencyServiceFeeStatements.AsNoTracking()
                .Where(s => pageIds.Contains(s.Id))
                .ToListAsync();
            var byId = loaded.ToDictionary(s => s.Id);
            statements = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var context = await LoadPageContextAsync(db, statements);
        var rows = statements.Select(s => MapStatement(s, context, asOfDate)).ToList();

        return BuildReport(query, statementStatus, asOfDate, total, rows);
    }

    /// <summary>组装报表（纯映射 + 本页汇总；不访问数据库）</summary>
    private static AgencyServiceFeeReconciliationReport BuildReport(
        AgencyServiceFeeReconciliationQuery query, string statementStatus, DateTime asOfDate,
        int total, List<AgencyServiceFeeReconciliationStatementRow> rows)
        => new()
        {
            StatementId = query.StatementId,
            CustomerId = query.CustomerId,
            AgreementId = query.AgreementId,
            Currency = query.Currency ?? string.Empty,
            SourceType = query.SourceType ?? string.Empty,
            SourceTypeText = query.SourceType is null
                ? "全部服务来源（销售订单 / 装柜清单）"
                : AgencyServiceFeeStatementRules.SourceTypeText(query.SourceType),
            Keyword = query.Keyword ?? string.Empty,
            StatementDateFrom = query.StatementDateFrom,
            StatementDateTo = query.StatementDateTo,
            DueDateFrom = query.DueDateFrom,
            DueDateTo = query.DueDateTo,
            StatementStatus = statementStatus,
            StatementStatusText = AgencyServiceFeeReconciliationRules.StatementStatusFilterText(statementStatus),
            AllocationState = query.AllocationState ?? string.Empty,
            AllocationStateText = query.AllocationState is null
                ? "全部分配状态（none / historical_only / partial / full）"
                : AgencyServiceFeeReconciliationRules.AllocationStateText(query.AllocationState),
            AsOfDate = asOfDate,
            AsOfDateText = $"账龄基准日（as-of）：{asOfDate:yyyy-MM-dd}",
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            PageStatementCount = rows.Count,
            ActiveEvidencePageStatementCount = rows.Count(r => r.IsActiveEvidence),
            DraftPageStatementCount = rows.Count(r => r.IsDraft),
            VoidedPageStatementCount = rows.Count(r => r.IsVoided),
            KnownDueDatePageStatementCount = rows.Count(r => r.DueDateKnown),
            UnknownDueDatePageStatementCount = rows.Count(r => !r.DueDateKnown),
            NoAllocationPageStatementCount = rows.Count(r =>
                r.AllocationState == AgencyServiceFeeReconciliationRules.AllocationNone),
            HistoricalOnlyPageStatementCount = rows.Count(r =>
                r.AllocationState == AgencyServiceFeeReconciliationRules.AllocationHistoricalOnly),
            InvalidEvidencePageStatementCount = rows.Count(r => r.HasInvalidOrUnavailableEvidence),
            OverAllocatedPageStatementCount = rows.Count(r =>
                r.RemainingState == AgencyServiceFeeReconciliationRules.RemainingOverAllocated),
            UnknownRemainingPageStatementCount = rows.Count(r => r.RemainingAmount is null),
            SourceTruncatedPageStatementCount = rows.Count(r => r.SourceTruncated),
            Currencies = BuildCurrencySummaries(rows),
            Groups = BuildGroups(rows),
            UnknownDueDateGroups = BuildUnknownDueDateGroups(rows),
        };

    // ==================== 2. 筛选（全部只用持久化字段） ====================

    /// <summary>
    /// 筛选（全部只用持久化字段）：对账单 Id / 客户 / 协议 Id / 币种 / 状态 / 服务来源（对账单行来源类型）/
    /// 对账日期区间 / 显式到期日区间 / 分配状态（ERP-071 分摊行 DB 侧聚合比较）/ 关键字；
    /// 到期日区间只命中「登记了到期日」的对账单（未知到期日不可筛，也绝不按对账日期补一个到期日）。
    /// </summary>
    private static IQueryable<AgencyServiceFeeStatement> ApplyFilters(
        IErpDbContext db, AgencyServiceFeeReconciliationQuery query)
    {
        var source = db.AgencyServiceFeeStatements.AsNoTracking().Where(s => !s.IsDeleted);

        if (query.StatementId is { } statementId) source = source.Where(s => s.Id == statementId);
        if (query.CustomerId is { } customerId) source = source.Where(s => s.CustomerId == customerId);
        if (query.AgreementId is { } agreementId) source = source.Where(s => s.AgreementId == agreementId);
        if (query.Currency is { } currency) source = source.Where(s => s.Currency == currency);

        switch (query.StatementStatus)
        {
            case AgencyServiceFeeReconciliationRules.StatementStatusDraft:
                source = source.Where(s => s.Status == AgencyServiceFeeStatementRules.StatusDraft);
                break;
            case AgencyServiceFeeReconciliationRules.StatementStatusVoided:
                source = source.Where(s => s.Status == AgencyServiceFeeStatementRules.StatusVoided);
                break;
            case AgencyServiceFeeReconciliationRules.StatementStatusAll:
                break;
            default:
                source = source.Where(s => s.Status == AgencyServiceFeeStatementRules.StatusRecorded);
                break;
        }

        if (query.SourceType is { } sourceType)
        {
            var type = sourceType;
            var lines = db.AgencyServiceFeeStatementLines;
            source = source.Where(s => lines.Any(l => !l.IsDeleted && l.StatementId == s.Id && l.SourceType == type));
        }

        if (query.StatementDateFrom is { } dateFrom) source = source.Where(s => s.StatementDate >= dateFrom);
        if (query.StatementDateTo is { } dateTo)
        {
            var dateToExclusive = dateTo.AddDays(1);
            source = source.Where(s => s.StatementDate < dateToExclusive);
        }

        if (query.DueDateFrom is { } dueFrom)
            source = source.Where(s => s.DueDate != null && s.DueDate >= dueFrom);

        if (query.DueDateTo is { } dueTo)
        {
            var dueToExclusive = dueTo.AddDays(1);
            source = source.Where(s => s.DueDate != null && s.DueDate < dueToExclusive);
        }

        switch (query.AllocationState)
        {
            // 无任何持久化分摊行（既无有效行也无历史 / 无效行）
            case AgencyServiceFeeReconciliationRules.AllocationNone:
            {
                // 需要读取持久化分摊行时才访问该数据集（避免无谓的数据集访问）
                var allocations = db.AgencyServiceFeeCollectionAllocations;
                source = source.Where(s => !allocations.Any(a => !a.IsDeleted && a.StatementId == s.Id));
                break;
            }

            // 没有有效分摊行，但存在历史 / 无效分摊行（保留可见，绝不并入有效合计）
            case AgencyServiceFeeReconciliationRules.AllocationHistoricalOnly:
            {
                var allocations = db.AgencyServiceFeeCollectionAllocations;
                source = source.Where(s =>
                    !allocations.Any(a => !a.IsDeleted
                        && a.StatementId == s.Id
                        && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
                    && allocations.Any(a => !a.IsDeleted && a.StatementId == s.Id));
                break;
            }

            case AgencyServiceFeeReconciliationRules.AllocationPartial:
            {
                var allocations = db.AgencyServiceFeeCollectionAllocations;
                source = source.Where(s =>
                    allocations
                        .Where(a => !a.IsDeleted
                            && a.StatementId == s.Id
                            && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
                        .Sum(a => (decimal?)a.AllocatedAmount) > 0
                    && allocations
                        .Where(a => !a.IsDeleted
                            && a.StatementId == s.Id
                            && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
                        .Sum(a => (decimal?)a.AllocatedAmount) < s.TotalAmount);
                break;
            }

            case AgencyServiceFeeReconciliationRules.AllocationFull:
            {
                var allocations = db.AgencyServiceFeeCollectionAllocations;
                source = source.Where(s => allocations
                        .Where(a => !a.IsDeleted
                            && a.StatementId == s.Id
                            && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
                        .Sum(a => (decimal?)a.AllocatedAmount) >= s.TotalAmount);
                break;
            }
        }

        if (query.Keyword is { } keyword)
        {
            source = source.Where(s => s.StatementNo.Contains(keyword)
                || s.CustomerCode.Contains(keyword)
                || s.CustomerName.Contains(keyword)
                || s.AgreementNo.Contains(keyword)
                || s.Remark.Contains(keyword));
        }

        return source;
    }

    // ==================== 3. 本页派生证据（固定次数数据集访问） ====================

    /// <summary>
    /// 单张对账单的持久化派生证据（ERP-072）：分摊行按状态聚合、**有效**分摊聚合（含收款单数）、
    /// 服务来源行按类型计数与有界身份清单。全部为读取时派生，不落库。
    /// </summary>
    private sealed class StatementFacts
    {
        /// <summary>本维度全部未删除分摊行数（含已作废与无效）</summary>
        public int AllRowCount { get; init; }

        /// <summary>**有效**分摊行数</summary>
        public int EffectiveCount { get; init; }

        /// <summary>**有效**分摊金额（原币）</summary>
        public decimal EffectiveAmount { get; init; }

        /// <summary>**有效**分摊涉及的收款单数</summary>
        public int EffectiveReceiptCount { get; init; }

        /// <summary>已作废分摊行数</summary>
        public int VoidedCount { get; init; }

        /// <summary>已作废分摊金额（原币；历史可见，绝不并入有效合计）</summary>
        public decimal VoidedAmount { get; init; }

        /// <summary>无效 / 无法确认分摊行数（有效但来源不可确认 + 未知状态行）</summary>
        public int InvalidCount { get; init; }

        /// <summary>无效 / 无法确认分摊金额（原币）</summary>
        public decimal InvalidAmount { get; init; }

        /// <summary>服务来源行数（全部未删除行）</summary>
        public int LineCount { get; init; }

        /// <summary>来源类型计数（类型 → 行数；只含持久化行存在的类型）</summary>
        public List<(string SourceType, int Count)> SourceTypeCounts { get; init; } = new();

        /// <summary>来源身份摘要（有界条数；只作人工核对展示）</summary>
        public List<string> SourceIdentities { get; init; } = new();

        /// <summary>来源身份摘要是否被有界截断（是则不给部分结论）</summary>
        public bool SourceTruncated { get; init; }
    }

    /// <summary>本页派生上下文（本页对账单的派生证据 + 客户 / 协议可用性；只读快照）</summary>
    private sealed class PageContext
    {
        public Dictionary<long, StatementFacts> Facts { get; init; } = new();
        public Dictionary<long, BaseCustomer> Customers { get; init; } = new();
        public Dictionary<long, AgencyServiceFeeAgreement> Agreements { get; init; } = new();
    }

    /// <summary>空事实（当某张对账单没有任何派生证据时使用；绝不臆造金额）</summary>
    private static readonly StatementFacts EmptyFacts = new();

    // ---- 数据集侧聚合的中间载体（读取后在内存中派生，避免把自定义类型放进查询表达式） ----

    /// <summary>分摊行按（对账单, 状态）聚合的中间结果</summary>
    private sealed record AllocationStatusStat(long StatementId, int Status, int Count, decimal Amount);

    /// <summary>**有效**分摊按对账单聚合的中间结果</summary>
    private sealed record AllocationAggregateStat(long StatementId, int Count, decimal Amount);

    /// <summary>有效分摊的（对账单, 收款单）组合（用于去重计数）</summary>
    private sealed record AllocationPairStat(long StatementId, long ReceiptId);

    /// <summary>服务来源行按（对账单, 来源类型）计数</summary>
    private sealed record SourceTypeStat(long StatementId, string? SourceType, int Count);

    /// <summary>有界的来源身份行</summary>
    private sealed record SourceIdentityRow(long StatementId, string? SourceType, string? SourceNo);

    /// <summary>按对账单汇总派生事实（纯计算，不访问数据集）：有效 / 已作废 / 无效分摊与来源计数。</summary>
    private static Dictionary<long, StatementFacts> BuildFacts(
        IReadOnlyList<AgencyServiceFeeStatement> statements,
        IReadOnlyList<AllocationStatusStat> statusStats,
        IReadOnlyList<AllocationAggregateStat> effectiveStats,
        IReadOnlyList<AllocationPairStat> effectivePairs,
        IReadOnlyList<SourceTypeStat> lineStats,
        IReadOnlyList<SourceIdentityRow> identityRows)
    {
        var facts = new Dictionary<long, StatementFacts>();
        foreach (var statement in statements)
        {
            var statusRows = statusStats.Where(s => s.StatementId == statement.Id).ToList();
            var activeRows = statusRows
                .Where(s => s.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive).ToList();
            var voidedRows = statusRows
                .Where(s => s.Status == AgencyServiceFeeCollectionAllocationRules.StatusVoided).ToList();
            var unknownRows = statusRows
                .Where(s => s.Status != AgencyServiceFeeCollectionAllocationRules.StatusActive
                    && s.Status != AgencyServiceFeeCollectionAllocationRules.StatusVoided).ToList();

            var activeCount = activeRows.Sum(s => s.Count);
            var activeAmount = activeRows.Sum(s => s.Amount);
            var effective = effectiveStats.FirstOrDefault(e => e.StatementId == statement.Id);
            var effectiveCount = effective?.Count ?? 0;
            var effectiveAmount = effective?.Amount ?? 0m;

            var typeCounts = lineStats.Where(l => l.StatementId == statement.Id)
                .Select(l => (SourceType: l.SourceType ?? string.Empty, l.Count))
                .OrderBy(l => l.SourceType, StringComparer.Ordinal)
                .ToList();
            var totalLines = typeCounts.Sum(l => l.Count);

            var identities = identityRows.Where(l => l.StatementId == statement.Id)
                .Take(AgencyServiceFeeReconciliationRules.MaxSourceIdentitiesPerRow)
                .Select(l => AgencyServiceFeeStatementRules.SourceIdentityText(l.SourceType, l.SourceNo))
                .ToList();

            facts[statement.Id] = new StatementFacts
            {
                AllRowCount = statusRows.Sum(s => s.Count),
                EffectiveCount = effectiveCount,
                EffectiveAmount = effectiveAmount,
                EffectiveReceiptCount = effectivePairs.Count(p => p.StatementId == statement.Id),
                VoidedCount = voidedRows.Sum(s => s.Count),
                VoidedAmount = voidedRows.Sum(s => s.Amount),
                InvalidCount = activeCount - effectiveCount + unknownRows.Sum(s => s.Count),
                InvalidAmount = activeAmount - effectiveAmount + unknownRows.Sum(s => s.Amount),
                LineCount = totalLines,
                SourceTypeCounts = typeCounts,
                SourceIdentities = identities,
                SourceTruncated = totalLines > identities.Count,
            };
        }

        return facts;
    }

    /// <summary>
    /// 本页一次性批量装载派生证据（本页对账单**非空**时固定 7 次数据集访问，与对账单张数 / 行数无关，
    /// 绝无逐行查库）：①分摊行按状态聚合 ②**有效**分摊聚合 ③有效分摊的（对账单, 收款单）组合
    /// ④本页客户 ⑤本页协议 ⑥本页服务来源行按类型计数 ⑦有界的来源身份清单。
    /// <para>「有效」= 未作废 + 对账单存在且未删除且仍为已登记 + 快照客户与币种一致 +
    /// 收款单存在且未删除且未取消（全部在数据集侧用持久化字段判定，不逐行回读）。</para>
    /// </summary>
    private static async Task<PageContext> LoadPageContextAsync(
        IErpDbContext db, IReadOnlyList<AgencyServiceFeeStatement> statements)
    {
        if (statements.Count == 0) return new PageContext();

        var statementIds = statements.Select(s => s.Id).ToList();
        var customerIds = statements.Select(s => s.CustomerId).Distinct().ToList();
        var agreementIds = statements.Select(s => s.AgreementId).Distinct().ToList();
        var statementIdSet = statementIds.ToHashSet();
        var customerIdSet = customerIds.ToHashSet();
        var agreementIdSet = agreementIds.ToHashSet();

        // 数据集句柄先取出（固定次数访问；绝不在查询表达式里逐行回读上下文属性）
        var statementSet = db.AgencyServiceFeeStatements;
        var allocationSet = db.AgencyServiceFeeCollectionAllocations;
        var receiptSet = db.FinanceReceipts;
        var customerSet = db.BaseCustomers;
        var agreementSet = db.AgencyServiceFeeAgreements;
        var lineSet = db.AgencyServiceFeeStatementLines;

        // ① 分摊行按状态聚合（含已作废历史；绝不逐行回读）
        var statusStats = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted && statementIds.Contains(a.StatementId))
                .GroupBy(a => new { a.StatementId, a.Status })
                .Select(g => new
                {
                    g.Key.StatementId,
                    g.Key.Status,
                    Count = g.Count(),
                    Amount = g.Sum(a => a.AllocatedAmount),
                })
                .ToListAsync())
            .Select(x => new AllocationStatusStat(x.StatementId, x.Status, x.Count, x.Amount))
            .ToList();

        // ② **有效**分摊聚合（未作废 + 对账单仍已登记 + 客户 / 币种自洽 + 收款单仍可读未取消）
        var effectiveStats = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted
                    && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive
                    && statementIds.Contains(a.StatementId)
                    && statementSet.Any(s => s.Id == a.StatementId && !s.IsDeleted
                        && s.Status == AgencyServiceFeeStatementRules.StatusRecorded
                        && s.Currency == a.Currency && s.CustomerId == a.CustomerId)
                    && receiptSet.Any(r => r.Id == a.ReceiptId && !r.IsDeleted
                        && r.Status != DocumentStatus.Cancelled))
                .GroupBy(a => a.StatementId)
                .Select(g => new
                {
                    StatementId = g.Key,
                    Count = g.Count(),
                    Amount = g.Sum(a => a.AllocatedAmount),
                })
                .ToListAsync())
            .Select(x => new AllocationAggregateStat(x.StatementId, x.Count, x.Amount))
            .ToList();

        // ③ 有效分摊的（对账单, 收款单）组合 → 有效分摊涉及的收款单数（有界：单侧有效行 ≤ 50）
        var effectivePairs = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted
                    && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive
                    && statementIds.Contains(a.StatementId)
                    && statementSet.Any(s => s.Id == a.StatementId && !s.IsDeleted
                        && s.Status == AgencyServiceFeeStatementRules.StatusRecorded
                        && s.Currency == a.Currency && s.CustomerId == a.CustomerId)
                    && receiptSet.Any(r => r.Id == a.ReceiptId && !r.IsDeleted
                        && r.Status != DocumentStatus.Cancelled))
                .GroupBy(a => new { a.StatementId, a.ReceiptId })
                .Select(g => new { g.Key.StatementId, g.Key.ReceiptId })
                .ToListAsync())
            .Select(x => new AllocationPairStat(x.StatementId, x.ReceiptId))
            .ToList();

        // ④ 本页客户（一次批量装载；停用 / 已删除客户照实标注，快照照常可读）
        var customers = (await customerSet.AsNoTracking()
                .Where(c => customerIdSet.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        // ⑤ 本页关联协议（一次批量装载；已作废 / 已删除协议照实标注）
        var agreements = (await agreementSet.AsNoTracking()
                .Where(a => agreementIdSet.Contains(a.Id)).ToListAsync())
            .ToDictionary(a => a.Id);

        // ⑥ 本页服务来源行按类型计数（数据集侧分组，与行数无关）
        var lineStats = (await lineSet.AsNoTracking()
                .Where(l => !l.IsDeleted && statementIdSet.Contains(l.StatementId))
                .GroupBy(l => new { l.StatementId, l.SourceType })
                .Select(g => new { g.Key.StatementId, g.Key.SourceType, Count = g.Count() })
                .ToListAsync())
            .Select(x => new SourceTypeStat(x.StatementId, x.SourceType, x.Count))
            .ToList();

        // ⑦ 有界的来源身份清单（只作人工核对展示；计数以 ⑥ 为准）
        var identityLimit = statements.Count * AgencyServiceFeeReconciliationRules.MaxSourceIdentitiesPerRow + 1;
        var identityRows = (await lineSet.AsNoTracking()
                .Where(l => !l.IsDeleted && statementIdSet.Contains(l.StatementId))
                .OrderBy(l => l.StatementId).ThenBy(l => l.LineNo).ThenBy(l => l.Id)
                .Select(l => new { l.StatementId, l.SourceType, l.SourceNo })
                .Take(identityLimit)
                .ToListAsync())
            .Select(x => new SourceIdentityRow(x.StatementId, x.SourceType, x.SourceNo))
            .ToList();

        return new PageContext
        {
            Facts = BuildFacts(statements, statusStats, effectiveStats, effectivePairs, lineStats, identityRows),
            Customers = customers,
            Agreements = agreements,
        };
    }

    // ==================== 4. 行派生（纯计算，不访问数据集） ====================

    /// <summary>
    /// 单张对账单行的只读派生：算术剩余证据 = 对账单合计 − **有效**已分摊（算术恒等式，绝不裁剪下限）；
    /// 账龄只按显式到期日与显式 as-of 日期计算；草稿 / 已作废单独标注且不参与有效合计；
    /// 已作废与无效分摊证据保持可见、绝不修复或改派；币种一律原币。
    /// </summary>
    private static AgencyServiceFeeReconciliationStatementRow MapStatement(
        AgencyServiceFeeStatement statement, PageContext context, DateTime asOfDate)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var currency = CurrencyAmountRules.NormalizeCurrency(statement.Currency);
        var decimals = CurrencyAmountRules.PrecisionOf(currency);
        var statementAmount = statement.TotalAmount;

        var facts = context.Facts.TryGetValue(statement.Id, out var found) ? found : EmptyFacts;
        context.Customers.TryGetValue(statement.CustomerId, out var customer);
        context.Agreements.TryGetValue(statement.AgreementId, out var agreement);

        var isActive = AgencyServiceFeeReconciliationRules.IsActiveEvidence(statement.Status);
        var isDraft = statement.Status == AgencyServiceFeeStatementRules.StatusDraft;
        var isVoided = statement.Status == AgencyServiceFeeStatementRules.StatusVoided;

        var hasAnyRow = facts.AllRowCount > 0 || facts.InvalidCount > 0;
        var overAllocated = facts.EffectiveAmount > statementAmount;
        var hasInvalid = facts.InvalidCount > 0 || overAllocated;
        var allocationState = AgencyServiceFeeReconciliationRules.AllocationStateOf(
            facts.EffectiveAmount, statementAmount, hasAnyRow, hasInvalid);
        var remainingState = AgencyServiceFeeReconciliationRules.RemainingStateOf(allocationState);
        var remainingAmount = remainingState == AgencyServiceFeeReconciliationRules.RemainingKnown
            ? statementAmount - facts.EffectiveAmount
            : (decimal?)null;

        var bucket = AgencyServiceFeeReconciliationRules.AgingBucketOf(
            statement.DueDate, asOfDate, out var overdueDays);

        var customerAvailable = customer is not null && !customer.IsDeleted && customer.Status == 1;
        var agreementAvailable = AgencyServiceFeeStatementRules.IsAgreementSelectable(
            agreement?.Status, agreement?.IsDeleted ?? true);

        return new AgencyServiceFeeReconciliationStatementRow
        {
            StatementId = statement.Id,
            StatementNo = statement.StatementNo,
            IdentityText = AgencyServiceFeeStatementRules.IdentityText(statement.StatementNo),
            StatementDate = statement.StatementDate,
            ServicePeriodFrom = statement.ServicePeriodFrom,
            ServicePeriodTo = statement.ServicePeriodTo,
            ServicePeriodText = AgencyServiceFeeStatementRules.ServicePeriodText(
                statement.ServicePeriodFrom, statement.ServicePeriodTo),
            CustomerId = statement.CustomerId,
            CustomerCode = statement.CustomerCode,
            CustomerName = statement.CustomerName,
            CustomerAvailable = customerAvailable,
            CustomerAvailabilityText = CustomerAvailabilityText(customer, customerAvailable),
            Currency = currency,
            AmountDecimals = decimals,
            StatementAmount = statementAmount,
            StatementAmountText = AgencyServiceFeeStatementRules.AmountText(statementAmount, currency),
            StatementStatus = statement.Status,
            StatementStatusText = AgencyServiceFeeStatementRules.StatusText(statement.Status),
            IsActiveEvidence = isActive,
            IsDraft = isDraft,
            IsVoided = isVoided,
            RecordedAt = statement.RecordedAt,
            RecordedBy = statement.RecordedBy,
            VoidedAt = statement.VoidedAt,
            VoidReason = statement.VoidReason,
            DueDate = statement.DueDate,
            DueDateKnown = statement.DueDate is not null,
            DueDateText = AgencyServiceFeeStatementRules.DueDateText(statement.DueDate),
            AgreementId = statement.AgreementId,
            AgreementNo = statement.AgreementNo,
            AgreementCurrency = statement.AgreementCurrency,
            AgreementTermsText = statement.AgreementTermsText,
            AgreementAvailable = agreementAvailable,
            AgreementAvailabilityText = AgencyServiceFeeStatementRules.AgreementAvailabilityText(
                agreement?.Status, agreement?.IsDeleted ?? true),
            LineCount = facts.LineCount,
            SourceSummaryText = SourceSummaryText(facts),
            SourceIdentities = facts.SourceIdentities,
            SourceTruncated = facts.SourceTruncated,
            ActiveAllocatedAmount = facts.EffectiveAmount,
            ActiveAllocationCount = facts.EffectiveCount,
            ActiveReceiptCount = facts.EffectiveReceiptCount,
            ActiveAllocationText = ActiveAllocationText(facts, currency),
            VoidedAllocationCount = facts.VoidedCount,
            VoidedAllocationAmount = facts.VoidedAmount,
            InvalidAllocationCount = facts.InvalidCount,
            InvalidAllocationAmount = facts.InvalidAmount,
            HistoricalEvidenceText = HistoricalEvidenceText(facts, currency),
            RemainingAmount = remainingAmount,
            RemainingState = remainingState,
            RemainingStateText = AgencyServiceFeeReconciliationRules.RemainingStateText(remainingState),
            AllocationState = allocationState,
            AllocationStateText = AgencyServiceFeeReconciliationRules.AllocationStateText(allocationState),
            LinkState = hasInvalid
                ? AgencyServiceFeeReconciliationRules.LinkStateUnavailable
                : AgencyServiceFeeReconciliationRules.LinkStateAvailable,
            LinkStateText = LinkStateText(hasInvalid),
            HasInvalidOrUnavailableEvidence = hasInvalid,
            AgingBucket = bucket,
            AgingBucketText = bucket is null
                ? AgencyServiceFeeReconciliationRules.BucketText(
                    AgencyServiceFeeReconciliationRules.UnknownDueDateBucket)
                : AgencyServiceFeeReconciliationRules.BucketText(bucket),
            OverdueDays = overdueDays,
            AgingText = AgencyServiceFeeReconciliationRules.AgingText(bucket, overdueDays),
            Note = RowNote(isActive, statement.DueDate is not null, hasInvalid, allocationState),
        };
    }

    /// <summary>客户可用性文案（不可用时照实说明；历史快照照常可读，绝不回填）</summary>
    private static string CustomerAvailabilityText(BaseCustomer? customer, bool available)
        => available
            ? "客户可读（只读引用，未被本模块改写）"
            : "客户已停用 / 已删除（历史快照仍可读，不回填、不修复）";

    /// <summary>服务来源摘要（按持久化来源类型计数；身份清单有界时显式说明计数完整）</summary>
    private static string SourceSummaryText(StatementFacts facts)
    {
        if (facts.LineCount == 0)
            return "无持久化服务来源行（证据缺口：不代表服务未提供，也不推算任何费用）";

        var parts = facts.SourceTypeCounts
            .Select(c => $"{AgencyServiceFeeStatementRules.SourceTypeText(c.SourceType)} ×{c.Count}")
            .ToList();
        var suffix = facts.SourceTruncated
            ? $"（来源身份清单只展示前 {AgencyServiceFeeReconciliationRules.MaxSourceIdentitiesPerRow} 条，计数完整）"
            : string.Empty;
        return string.Join(" / ", parts) + suffix;
    }

    /// <summary>有效分摊文案（无有效行时显式说明证据缺口 ≠ 未付款 / 已付款）</summary>
    private static string ActiveAllocationText(StatementFacts facts, string currency)
        => facts.EffectiveCount == 0
            ? AgencyServiceFeeReconciliationRules.NoAllocationText
            : $"{facts.EffectiveCount} 条有效分摊 = "
              + $"{AgencyServiceFeeStatementRules.AmountText(facts.EffectiveAmount, currency)}"
              + $"（涉及 {facts.EffectiveReceiptCount} 张收款单；本维度口径，不与销售订单收款引用相加）";

    /// <summary>历史 / 无效分摊证据文案（保留可见，绝不并入有效合计）</summary>
    private static string HistoricalEvidenceText(StatementFacts facts, string currency)
        => facts.VoidedCount == 0 && facts.InvalidCount == 0
            ? "无历史 / 无效分摊证据"
            : $"已作废分摊 {facts.VoidedCount} 条 / "
              + $"{AgencyServiceFeeStatementRules.AmountText(facts.VoidedAmount, currency)}；"
              + $"无效或无法确认 {facts.InvalidCount} 条 / "
              + $"{AgencyServiceFeeStatementRules.AmountText(facts.InvalidAmount, currency)}"
              + "（保留可见，绝不并入有效合计；不修复、不改派、不合并）";

    /// <summary>链接状态文案（无效证据照实标注，绝不修复或改派）</summary>
    private static string LinkStateText(bool hasInvalid)
        => hasInvalid
            ? "存在无效 / 无法确认分摊证据（收款单已删除 / 已取消、对账单已作废或快照客户 / 币种不一致）："
              + "照实标注，绝不修复、改派或重算"
            : "全部分摊行来源可确认（只读引用，未被本模块改写）";

    /// <summary>行说明（证据事实与未知 / 无效处理；不给任何法律或账务断言）</summary>
    private static string RowNote(bool isActive, bool dueDateKnown, bool hasInvalid, string allocationState)
    {
        var notes = new List<string>();
        if (!isActive)
            notes.Add("草稿 / 已作废对账单：金额单独可见，不并入有效对账合计与账龄桶合计");
        if (!dueDateKnown)
            notes.Add("未登记显式到期日：账龄不计算（绝不按客户账期、协议文字或对账日期推算）");
        if (hasInvalid)
            notes.Add(AgencyServiceFeeReconciliationRules.InvalidEvidenceNote);
        if (allocationState == AgencyServiceFeeReconciliationRules.AllocationOverAllocated)
        {
            notes.Add(AgencyServiceFeeReconciliationRules.RemainingStateText(
                AgencyServiceFeeReconciliationRules.RemainingOverAllocated));
        }
        if (notes.Count == 0)
            notes.Add("仓库内只读派生证据：不是总账 / 应收余额 / 付款通知 / 收入确认 / 结算确认");

        return string.Join("；", notes);
    }

    // ==================== 5. 本页汇总（客户 × 币种；绝无跨币种总额） ====================

    /// <summary>「客户 + 币种」分组（含账龄桶、未知到期日分组与组内行；金额只统计有效证据）</summary>
    private static List<AgencyServiceFeeReconciliationGroup> BuildGroups(
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> rows)
        => rows
            .GroupBy(r => new { r.CustomerId, r.Currency })
            .Select(g => BuildGroup(g.First(), g.ToList()))
            .OrderBy(g => g.CustomerId)
            .ThenBy(g => g.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>单个「客户 + 币种」分组（本页）</summary>
    private static AgencyServiceFeeReconciliationGroup BuildGroup(
        AgencyServiceFeeReconciliationStatementRow sample,
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> rows)
    {
        var active = rows.Where(r => r.IsActiveEvidence).ToList();

        return new AgencyServiceFeeReconciliationGroup
        {
            CustomerId = sample.CustomerId,
            CustomerCode = sample.CustomerCode,
            CustomerName = sample.CustomerName,
            CustomerAvailable = sample.CustomerAvailable,
            CustomerAvailabilityText = sample.CustomerAvailabilityText,
            Currency = sample.Currency,
            AmountDecimals = sample.AmountDecimals,
            StatementCount = rows.Count,
            ActiveEvidenceStatementCount = active.Count,
            DraftStatementCount = rows.Count(r => r.IsDraft),
            VoidedStatementCount = rows.Count(r => r.IsVoided),
            KnownDueDateStatementCount = active.Count(r => r.DueDateKnown),
            UnknownDueDateStatementCount = active.Count(r => !r.DueDateKnown),
            InvalidEvidenceStatementCount = rows.Count(r => r.HasInvalidOrUnavailableEvidence),
            UnknownRemainingStatementCount = active.Count(r => r.RemainingAmount is null),
            OverAllocatedStatementCount = active.Count(r =>
                r.RemainingState == AgencyServiceFeeReconciliationRules.RemainingOverAllocated),
            StatementAmount = active.Sum(r => r.StatementAmount),
            ActiveAllocatedAmount = KnownAmounts(active, r => r.ActiveAllocatedAmount),
            RemainingAmount = KnownAmounts(active, r => r.RemainingAmount),
            Buckets = BuildBucketTotals(active),
            UnknownDueDate = BuildUnknownDueDateBucketTotal(active),
            Statements = rows.ToList(),
            Note = "组内金额一律同一币种原币：绝不与其它币种相加、合并或换算；"
                + "对账单合计 / 有效已分摊 / 算术剩余三类数字严格分列，互不轧差；"
                + "草稿与已作废对账单不计入任何有效合计与账龄桶合计。",
        };
    }

    /// <summary>币种汇总（每币种一行；**没有**跨币种总额）</summary>
    private static List<AgencyServiceFeeReconciliationCurrencySummary> BuildCurrencySummaries(
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> rows)
        => rows
            .GroupBy(r => r.Currency)
            .Select(g =>
            {
                var items = g.ToList();
                var active = items.Where(r => r.IsActiveEvidence).ToList();
                return new AgencyServiceFeeReconciliationCurrencySummary
                {
                    Currency = g.Key,
                    AmountDecimals = items[0].AmountDecimals,
                    StatementCount = items.Count,
                    ActiveEvidenceStatementCount = active.Count,
                    DraftStatementCount = items.Count(r => r.IsDraft),
                    VoidedStatementCount = items.Count(r => r.IsVoided),
                    KnownDueDateStatementCount = active.Count(r => r.DueDateKnown),
                    UnknownDueDateStatementCount = active.Count(r => !r.DueDateKnown),
                    InvalidEvidenceStatementCount = items.Count(r => r.HasInvalidOrUnavailableEvidence),
                    StatementAmount = active.Sum(r => r.StatementAmount),
                    ActiveAllocatedAmount = KnownAmounts(active, r => r.ActiveAllocatedAmount),
                    RemainingAmount = KnownAmounts(active, r => r.RemainingAmount),
                    UnknownRemainingStatementCount = active.Count(r => r.RemainingAmount is null),
                    OverAllocatedStatementCount = active.Count(r =>
                        r.RemainingState == AgencyServiceFeeReconciliationRules.RemainingOverAllocated),
                    Buckets = BuildBucketTotals(active),
                    UnknownDueDate = BuildUnknownDueDateBucketTotal(active),
                };
            })
            .OrderBy(c => c.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>「未知到期日」独立分组（按客户 + 币种；不参与任何账龄桶）</summary>
    private static List<AgencyServiceFeeReconciliationUnknownDueDateGroup> BuildUnknownDueDateGroups(
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> rows)
        => rows
            .Where(r => r.IsActiveEvidence && !r.DueDateKnown)
            .GroupBy(r => new { r.CustomerId, r.Currency })
            .Select(g =>
            {
                var items = g.ToList();
                return new AgencyServiceFeeReconciliationUnknownDueDateGroup
                {
                    CustomerId = g.Key.CustomerId,
                    CustomerCode = items[0].CustomerCode,
                    CustomerName = items[0].CustomerName,
                    Currency = g.Key.Currency,
                    AmountDecimals = items[0].AmountDecimals,
                    StatementCount = items.Count,
                    StatementAmount = items.Sum(r => r.StatementAmount),
                    ActiveAllocatedAmount = KnownAmounts(items, r => r.ActiveAllocatedAmount),
                    RemainingAmount = KnownAmounts(items, r => r.RemainingAmount),
                    StatementIdentities = items
                        .Take(AgencyServiceFeeReconciliationRules.MaxUnknownDueIdentitiesPerGroup)
                        .Select(r => r.IdentityText)
                        .ToList(),
                    Note = AgencyServiceFeeReconciliationRules.UnknownDueDateNote,
                };
            })
            .OrderBy(g => g.CustomerId)
            .ThenBy(g => g.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>五个账龄桶合计（只统计有效证据；互斥且完整）</summary>
    private static List<AgencyServiceFeeReconciliationBucketTotal> BuildBucketTotals(
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> activeRows)
        => AgencyServiceFeeReconciliationRules.SupportedBuckets
            .Select(bucket => BuildBucketTotal(
                bucket, activeRows.Where(r => r.AgingBucket == bucket).ToList(), isAgingBucket: true))
            .ToList();

    /// <summary>「未知到期日」桶合计（**独立分组，不参与任何账龄桶**）</summary>
    private static AgencyServiceFeeReconciliationBucketTotal BuildUnknownDueDateBucketTotal(
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> activeRows)
        => BuildBucketTotal(
            AgencyServiceFeeReconciliationRules.UnknownDueDateBucket,
            activeRows.Where(r => !r.DueDateKnown).ToList(),
            isAgingBucket: false);

    /// <summary>单个桶 / 分组合计（金额按原币；未知时不给部分合计）</summary>
    private static AgencyServiceFeeReconciliationBucketTotal BuildBucketTotal(
        string bucket, IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> rows, bool isAgingBucket)
        => new()
        {
            Bucket = bucket,
            BucketText = AgencyServiceFeeReconciliationRules.BucketText(bucket),
            IsAgingBucket = isAgingBucket,
            StatementCount = rows.Count,
            StatementAmount = rows.Sum(r => r.StatementAmount),
            ActiveAllocatedAmount = KnownAmounts(rows, r => r.ActiveAllocatedAmount),
            RemainingAmount = KnownAmounts(rows, r => r.RemainingAmount),
            UnknownRemainingStatementCount = rows.Count(r => r.RemainingAmount is null),
            Note = isAgingBucket
                ? "账龄桶合计只统计**已登记**（有效证据）且**登记了显式到期日**的对账单；"
                  + "边界取含：30 / 60 / 90 天归入本桶，次日起归入下一桶。"
                : AgencyServiceFeeReconciliationRules.UnknownDueDateNote,
        };

    /// <summary>求和（任一行未知即整体未知：绝不用 0 顶替，也不给部分合计）</summary>
    private static decimal? KnownAmounts(
        IReadOnlyList<AgencyServiceFeeReconciliationStatementRow> rows,
        Func<AgencyServiceFeeReconciliationStatementRow, decimal?> selector)
    {
        var values = rows.Select(selector).ToList();
        return values.Any(v => v is null) ? null : values.Sum(v => v!.Value);
    }

    // ==================== 6. 单张对账单明细（重新校验授权；fail closed） ====================

    /// <summary>
    /// 单张对账单的对账证据明细（只读派生）：打开明细时**重新校验**当前登录身份（<paramref name="userId"/>）
    /// 与既有「角色 → 菜单」模块授权（<see cref="AgencyServiceFeeReconciliationRules.RequiredMenuCode"/>），
    /// 并重新读取权威来源；对账单不存在 / 已删除时拒绝。
    /// <para>fail closed：未认证 → 未认证错误；无该模块授权 → 权限不足；对账单不存在或已删除 → 不存在；
    /// 任何一种都<strong>不返回任何证据</strong>、不返回部分金额，也不做来源修复或改派。</para>
    /// <para>返回行与列表行**完全同源**（同一派生方法），界面在列表与明细之间不会出现两套口径。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeReconciliationStatementDetail> ForStatementDetailAsync(
        IErpDbContext db, long statementId, long? userId, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (statementId <= 0)
            throw BusinessException.InvalidParameter($"对账单 Id 必须为正整数：{statementId}");

        var asOf = (asOfDate ?? DateTime.Today).Date;

        // 1) 身份（未认证 → 拒绝；不返回任何证据）
        if (userId is null or <= 0)
        {
            throw new BusinessException(
                "请先登录后再查看代理服务费对账证据明细", ErrorCodes.Unauthorized);
        }

        // 2) 既有「角色 → 菜单」模块授权（未授权 → 拒绝；不返回任何证据）
        var menuCodes = await LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(
                AgencyServiceFeeReconciliationRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{AgencyServiceFeeReconciliationRules.RequiredMenuText}」"
                + $"（{AgencyServiceFeeReconciliationRules.RequiredMenuCode}）模块授权：拒绝打开对账证据明细"
                + "（fail closed，不返回任何证据、不做来源修复或改派）",
                ErrorCodes.Forbidden);
        }

        // 3) 权威来源：对账单必须存在且未删除（已删除 / 不存在 → 拒绝，绝不修复、绝不改派）
        var statement = await db.AgencyServiceFeeStatements.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == statementId && !s.IsDeleted);
        if (statement is null)
        {
            throw BusinessException.NotFound(
                "代理服务费对账单证据不存在或已删除：无法确认对账与账龄证据"
                + "（fail closed，不返回部分证据，也不做任何修复或改派）");
        }

        var context = await LoadPageContextAsync(db, new[] { statement });
        var row = MapStatement(statement, context, asOf);

        // 4) 分摊行清单（有界，含已作废历史；只作展示，金额与计数以数据集侧聚合为准）
        var loadedRows = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.StatementId == statement.Id)
            .OrderByDescending(a => a.AllocatedAt).ThenByDescending(a => a.Id)
            .Take(AgencyServiceFeeReconciliationRules.MaxDetailsPerStatement + 1)
            .ToListAsync();
        var allocationsTruncated = loadedRows.Count > AgencyServiceFeeReconciliationRules.MaxDetailsPerStatement;
        var displayRows = loadedRows.Take(AgencyServiceFeeReconciliationRules.MaxDetailsPerStatement).ToList();

        var receiptIds = displayRows.Select(a => a.ReceiptId).Distinct().ToList();
        var receipts = (await db.FinanceReceipts.AsNoTracking()
                .Where(r => receiptIds.Contains(r.Id)).ToListAsync())
            .ToDictionary(r => r.Id);

        return new AgencyServiceFeeReconciliationStatementDetail
        {
            StatementId = statementId,
            AsOfDate = asOf,
            Row = row,
            AllocationsTruncated = allocationsTruncated,
            Allocations = displayRows.Select(a => MapAllocationRow(a, statement, receipts)).ToList(),
            AuthorizationNote =
                $"打开明细时已重新校验登录身份与「{AgencyServiceFeeReconciliationRules.RequiredMenuText}」"
                + $"（{AgencyServiceFeeReconciliationRules.RequiredMenuCode}）模块授权（复用既有「角色 → 菜单」口径），"
                + "并重新读取持久化对账单、ERP-071 分摊行与收款单证据（只读，全程不写库）。",
        };
    }

    /// <summary>分摊行 → 明细行（含有效性判定与可用性标注；绝不改写历史证据）</summary>
    private static AgencyServiceFeeReconciliationAllocationRow MapAllocationRow(
        AgencyServiceFeeCollectionAllocation row,
        AgencyServiceFeeStatement statement,
        IReadOnlyDictionary<long, FinanceReceipt> receipts)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        receipts.TryGetValue(row.ReceiptId, out var receipt);

        var receiptExists = receipt is not null;
        var receiptAvailable = receiptExists
            && !receipt!.IsDeleted
            && receipt.Status != DocumentStatus.Cancelled;
        var isVoided = row.Status == AgencyServiceFeeCollectionAllocationRules.StatusVoided;
        var statementActive = AgencyServiceFeeReconciliationRules.IsActiveEvidence(statement.Status);
        var currencyMatches = string.Equals(
            currency, CurrencyAmountRules.NormalizeCurrency(statement.Currency), StringComparison.Ordinal);
        var customerMatches = row.CustomerId == statement.CustomerId;
        var isEffective = !isVoided && statementActive && receiptAvailable && currencyMatches && customerMatches;

        return new AgencyServiceFeeReconciliationAllocationRow
        {
            AllocationId = row.Id,
            ReceiptId = row.ReceiptId,
            ReceiptNo = row.ReceiptNo,
            ReceiptDate = row.ReceiptDate,
            ReceiptStatus = row.ReceiptStatus,
            ReceiptStatusText = row.ReceiptStatusText,
            ReceiptAvailable = receiptAvailable,
            ReceiptAvailabilityText = receiptAvailable
                ? "收款单可读（只读引用，未被本模块改写）"
                : (receiptExists
                    ? "收款单已取消（历史快照仍可读，不再计入有效分摊）"
                    : "收款单已删除或不存在（历史快照仍可读，不再计入有效分摊）"),
            AllocatedAmount = row.AllocatedAmount,
            AmountText = AgencyServiceFeeStatementRules.AmountText(row.AllocatedAmount, currency),
            Currency = currency,
            Status = row.Status,
            StatusText = AgencyServiceFeeCollectionAllocationRules.StatusText(row.Status),
            IsEffective = isEffective,
            EffectivenessText = isEffective
                ? "计入有效已分摊合计（未作废 + 对账单已登记 + 收款单可读未取消 + 快照客户 / 币种一致）"
                : (isVoided
                    ? "不计入有效合计：分摊行已作废（历史可见，不参与任何有效合计）"
                    : "不计入有效合计：无效 / 无法确认证据（来源已失效或快照不一致，保留可见、绝不修复或改派）"),
            AllocatedAt = row.AllocatedAt,
            AllocatedBy = row.AllocatedBy,
            VoidedAt = row.VoidedAt,
            VoidReason = row.VoidReason,
            Remark = row.Remark,
        };
    }

    /// <summary>
    /// 当前账号被允许访问的菜单编码（fail closed：无身份 / 无角色 / 无菜单授权 → 空集合）：
    /// 复用**既有**授权口径 <c>SysUserRoles</c> → <c>SysRoleMenus</c> → <c>SysMenus.MenuCode</c>
    /// （忽略按钮型菜单与被删除角色 / 菜单）；每次请求都重新查询，因此撤销授权后立即收敛。
    /// </summary>
    private static async Task<HashSet<string>> LoadAuthorizedMenuCodesAsync(IErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        if (roleIds.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roleSet = roleIds.ToHashSet();
        var menuIds = await db.SysRoleMenus.AsNoTracking()
            .Where(rm => roleSet.Contains(rm.RoleId) && !rm.IsDeleted)
            .Select(rm => rm.MenuId)
            .ToListAsync();
        if (menuIds.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var menuIdSet = menuIds.ToHashSet();
        var codes = await db.SysMenus.AsNoTracking()
            .Where(m => menuIdSet.Contains(m.Id) && !m.IsDeleted && m.MenuType != MenuType.Button)
            .Select(m => m.MenuCode)
            .ToListAsync();

        return codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
