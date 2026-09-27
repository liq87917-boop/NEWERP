using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户应收账款对账与账龄工作台（ERP-074，只读派生）：在 ERP-055 客户销项发票证据与
/// ERP-073「客户收款 → 销项发票」分摊证据之上派生 ①发票含税总额证据、②有效收款分摊证据、③算术剩余证据，
/// 并按显式到期日与显式 as-of 日期计算互斥账龄桶（当前 ERP-055 发票证据模型尚未持久化到期日，
/// 因此现有发票全部进入独立的「未知到期日」分组）。
/// </summary>
public static class CustomerReceivableReconciliationService
{
    /// <summary>模块元数据（白名单、有界额度与口径文案；与界面 / 文档同源）</summary>
    public static CustomerReceivableReconciliationMetadataDto GetMetadata() => new()
    {
        SupportedBuckets = CustomerReceivableReconciliationRules.SupportedBuckets.ToList(),
        SupportedAllocationFilters = CustomerReceivableReconciliationRules.SupportedAllocationFilters.ToList(),
        SupportedInvoiceStatuses = CustomerReceivableReconciliationRules.SupportedInvoiceStatuses.ToList(),
        SupportedCurrencies = CustomerReceivableReconciliationRules.SupportedCurrencies.ToList(),
        DefaultPageSize = CustomerReceivableReconciliationRules.DefaultPageSize,
        MaxPageSize = CustomerReceivableReconciliationRules.MaxPageSize,
        MaxAllocationRowsPerPage = CustomerReceivableReconciliationRules.MaxAllocationRowsPerPage,
        MaxDetailsPerInvoice = CustomerReceivableReconciliationRules.MaxDetailsPerInvoice,
        RequiredMenuCode = CustomerReceivableReconciliationRules.RequiredMenuCode,
        RequiredMenuText = CustomerReceivableReconciliationRules.RequiredMenuText,
    };

    /// <summary>
    /// 工作台报表（只读派生，分页有界）：筛选（客户 / 发票身份 / 发票日期区间 / 币种 / 发票状态 / 分配状态 /
    /// 关键字，全部只用持久化字段）→ 分页 → 本页发票一次性批量装载派生证据。
    /// </summary>
    public static async Task<CustomerReceivableReconciliationReport> ForQueryAsync(
        IErpDbContext db, CustomerReceivableReconciliationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var asOfDate = (query.AsOfDate ?? DateTime.Today).Date;
        var invoiceStatus = query.InvoiceStatus ?? CustomerReceivableReconciliationRules.InvoiceStatusRecorded;

        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();

        var pageIds = await source
            .OrderBy(i => i.CustomerId)
            .ThenBy(i => i.Currency)
            .ThenByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(i => i.Id)
            .ToListAsync();

        var invoices = new List<CustomerSalesInvoiceEvidence>();
        if (pageIds.Count > 0)
        {
            var loaded = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
                .Where(i => pageIds.Contains(i.Id))
                .ToListAsync();
            var byId = loaded.ToDictionary(i => i.Id);
            invoices = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var context = await LoadPageContextAsync(db, invoices);
        var rows = invoices.Select(i => MapInvoice(i, context, asOfDate)).ToList();

        return BuildReport(query, invoiceStatus, asOfDate, total, rows);
    }

    /// <summary>组装报表（纯映射 + 本页汇总；不访问数据库）</summary>
    private static CustomerReceivableReconciliationReport BuildReport(
        CustomerReceivableReconciliationQuery query, string invoiceStatus, DateTime asOfDate,
        int total, List<CustomerReceivableReconciliationInvoiceRow> rows)
        => new()
        {
            InvoiceId = query.InvoiceId,
            CustomerId = query.CustomerId,
            Currency = query.Currency ?? string.Empty,
            Keyword = query.Keyword ?? string.Empty,
            InvoiceDateFrom = query.InvoiceDateFrom,
            InvoiceDateTo = query.InvoiceDateTo,
            InvoiceStatus = invoiceStatus,
            InvoiceStatusText = CustomerReceivableReconciliationRules.InvoiceStatusText(invoiceStatus),
            AllocationState = query.AllocationState ?? string.Empty,
            AllocationStateText = query.AllocationState is null
                ? "全部分配状态（none / historical_only / partial / full）"
                : CustomerReceivableReconciliationRules.AllocationStateText(query.AllocationState),
            AsOfDate = asOfDate,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            PageInvoiceCount = rows.Count,
            Invoices = rows,
            Currencies = BuildCurrencySummaries(rows),
            Groups = BuildGroups(rows),
            UnknownDueDateGroups = BuildUnknownDueDateGroups(rows),
        };

    // ==================== 1. 筛选（全部只用持久化字段） ====================

    /// <summary>筛选：发票 Id / 客户 / 币种 / 发票日期区间 / 发票状态 / 分配状态（分摊行 DB 侧聚合）/ 关键字</summary>
    private static IQueryable<CustomerSalesInvoiceEvidence> ApplyFilters(
        IErpDbContext db, CustomerReceivableReconciliationQuery query)
    {
        var source = db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(i => !i.IsDeleted);

        if (query.InvoiceId.HasValue)
            source = source.Where(i => i.Id == query.InvoiceId.Value);

        if (query.CustomerId.HasValue)
            source = source.Where(i => i.CustomerId == query.CustomerId.Value);

        if (!string.IsNullOrWhiteSpace(query.Currency))
            source = source.Where(i => i.Currency == query.Currency);

        if (query.InvoiceDateFrom.HasValue)
            source = source.Where(i => i.InvoiceDate >= query.InvoiceDateFrom.Value);
        if (query.InvoiceDateTo.HasValue)
            source = source.Where(i => i.InvoiceDate <= query.InvoiceDateTo.Value);

        source = query.InvoiceStatus switch
        {
            CustomerReceivableReconciliationRules.InvoiceStatusDraft =>
                source.Where(i => i.Status == CustomerSalesInvoiceEvidenceRules.StatusDraft),
            CustomerReceivableReconciliationRules.InvoiceStatusVoided =>
                source.Where(i => i.Status == CustomerSalesInvoiceEvidenceRules.StatusVoided),
            CustomerReceivableReconciliationRules.InvoiceStatusAll => source,
            _ => source.Where(i => i.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded),
        };

        if (query.AllocationState is not null)
        {
            var allocation = db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking();
            source = query.AllocationState switch
            {
                CustomerReceivableReconciliationRules.AllocationNone =>
                    source.Where(i => !allocation.Any(a => !a.IsDeleted
                        && a.CustomerSalesInvoiceEvidenceId == i.Id)),
                CustomerReceivableReconciliationRules.AllocationHistoricalOnly =>
                    source.Where(i => allocation.Any(a => !a.IsDeleted
                            && a.CustomerSalesInvoiceEvidenceId == i.Id)
                        && !allocation.Any(a => !a.IsDeleted
                            && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                            && a.CustomerSalesInvoiceEvidenceId == i.Id)),
                CustomerReceivableReconciliationRules.AllocationPartial =>
                    source.Where(i => allocation
                            .Where(a => !a.IsDeleted
                                && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                                && a.CustomerSalesInvoiceEvidenceId == i.Id)
                            .Sum(a => a.AllocatedAmount) > 0
                        && allocation
                            .Where(a => !a.IsDeleted
                                && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                                && a.CustomerSalesInvoiceEvidenceId == i.Id)
                            .Sum(a => a.AllocatedAmount) < i.GrossAmount),
                CustomerReceivableReconciliationRules.AllocationFull =>
                    source.Where(i => allocation
                        .Where(a => !a.IsDeleted
                            && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                            && a.CustomerSalesInvoiceEvidenceId == i.Id)
                        .Sum(a => a.AllocatedAmount) >= i.GrossAmount),
                _ => source,
            };
        }

        var keyword = query.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            source = source.Where(i =>
                i.InvoiceCode.Contains(keyword)
                || i.InvoiceNumber.Contains(keyword)
                || i.CustomerCode.Contains(keyword)
                || i.CustomerName.Contains(keyword)
                || i.Remark.Contains(keyword));
        }

        return source;
    }

    // ==================== 1. 本页派生证据（固定次数数据集访问） ====================

    /// <summary>本页一次性批量装载派生证据（本页发票非空时固定 6 次数据集访问，与发票张数 / 行数无关，无逐行查库）</summary>
    private static async Task<PageContext> LoadPageContextAsync(
        IErpDbContext db, IReadOnlyList<CustomerSalesInvoiceEvidence> invoices)
    {
        if (invoices.Count == 0) return new PageContext();

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var customerIds = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var customerIdSet = customerIds.ToHashSet();

        var invoiceSet = db.CustomerSalesInvoiceEvidences;
        var allocationSet = db.CustomerSalesInvoiceCollectionAllocations;
        var receiptSet = db.FinanceReceipts;
        var customerSet = db.BaseCustomers;

        // ① 分摊行按状态聚合（含已作废历史；绝不逐行回读）
        var statusStats = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId))
                .GroupBy(a => new { a.CustomerSalesInvoiceEvidenceId, a.Status })
                .Select(g => new
                {
                    g.Key.CustomerSalesInvoiceEvidenceId,
                    g.Key.Status,
                    Count = g.Count(),
                    Amount = g.Sum(a => a.AllocatedAmount),
                })
                .ToListAsync())
            .Select(x => new AllocationStatusStat(x.CustomerSalesInvoiceEvidenceId, x.Status, x.Count, x.Amount))
            .ToList();

        // ② 有效分摊聚合（未作废 + 发票仍已登记 + 客户 / 币种自洽 + 收款单仍可读未取消）
        var effectiveStats = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted
                    && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                    && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId)
                    && invoiceSet.Any(i => i.Id == a.CustomerSalesInvoiceEvidenceId && !i.IsDeleted
                        && i.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded
                        && i.Currency == a.Currency && i.CustomerId == a.CustomerId)
                    && receiptSet.Any(r => r.Id == a.ReceiptId && !r.IsDeleted
                        && r.Status != DocumentStatus.Cancelled))
                .GroupBy(a => a.CustomerSalesInvoiceEvidenceId)
                .Select(g => new
                {
                    InvoiceId = g.Key,
                    Count = g.Count(),
                    Amount = g.Sum(a => a.AllocatedAmount),
                })
                .ToListAsync())
            .Select(x => new AllocationAggregateStat(x.InvoiceId, x.Count, x.Amount))
            .ToList();

        // ③ 有效分摊的（发票, 收款单）组合 → 有效分摊涉及的收款单数
        var effectivePairs = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted
                    && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                    && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId)
                    && invoiceSet.Any(i => i.Id == a.CustomerSalesInvoiceEvidenceId && !i.IsDeleted
                        && i.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded
                        && i.Currency == a.Currency && i.CustomerId == a.CustomerId)
                    && receiptSet.Any(r => r.Id == a.ReceiptId && !r.IsDeleted
                        && r.Status != DocumentStatus.Cancelled))
                .GroupBy(a => new { a.CustomerSalesInvoiceEvidenceId, a.ReceiptId })
                .Select(g => new { g.Key.CustomerSalesInvoiceEvidenceId, g.Key.ReceiptId })
                .ToListAsync())
            .Select(x => new AllocationPairStat(x.CustomerSalesInvoiceEvidenceId, x.ReceiptId))
            .ToList();

        // ④ 本页客户（一次批量装载）
        var customers = (await customerSet.AsNoTracking()
                .Where(c => customerIdSet.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        return new PageContext
        {
            Facts = BuildFacts(invoices, statusStats, effectiveStats, effectivePairs),
            Customers = customers,
        };
    }

    /// <summary>把聚合结果按发票折叠为行事实（纯计算，不访问数据集）</summary>
    private static Dictionary<long, InvoiceFacts> BuildFacts(
        IReadOnlyList<CustomerSalesInvoiceEvidence> invoices,
        IReadOnlyList<AllocationStatusStat> statusStats,
        IReadOnlyList<AllocationAggregateStat> effectiveStats,
        IReadOnlyList<AllocationPairStat> effectivePairs)
    {
        var facts = invoices.ToDictionary(i => i.Id, _ => new InvoiceFacts());

        foreach (var s in statusStats)
        {
            if (!facts.TryGetValue(s.InvoiceId, out var f)) continue;
            f.TotalRowCount += s.Count;
            if (s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive)
            {
                f.ActiveRowCount += s.Count;
                f.ActiveAmount += s.Amount;
            }
            else if (s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided)
            {
                f.VoidedRowCount += s.Count;
            }
        }

        foreach (var e in effectiveStats)
        {
            if (facts.TryGetValue(e.InvoiceId, out var f))
            {
                f.EffectiveCount = e.Count;
                f.EffectiveAmount = e.Amount;
            }
        }

        foreach (var p in effectivePairs)
        {
            if (facts.TryGetValue(p.InvoiceId, out var f)) f.EffectiveReceiptCount++;
        }

        return facts;
    }

    // ==================== 2. 行映射与派生状态 ====================

    /// <summary>把一张发票 + 其派生事实映射为对账证据行（纯计算，不访问数据库）</summary>
    private static CustomerReceivableReconciliationInvoiceRow MapInvoice(
        CustomerSalesInvoiceEvidence invoice, PageContext context, DateTime asOfDate)
    {
        var facts = context.Facts.TryGetValue(invoice.Id, out var f) ? f : EmptyFacts;
        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var isDraft = invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusDraft;
        var isVoided = invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusVoided;
        var isActive = !isDraft && !isVoided;

        var allocationState = AllocationStateOf(facts, invoice.GrossAmount);
        var (remainingAmount, remainingState) = RemainingOf(facts, invoice.GrossAmount, isActive);

        return new CustomerReceivableReconciliationInvoiceRow
        {
            InvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            IdentityText = CustomerSalesInvoiceEvidenceRules.IdentityText(
                invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
            InvoiceDate = invoice.InvoiceDate,
            CustomerId = invoice.CustomerId,
            CustomerCode = invoice.CustomerCode,
            CustomerName = invoice.CustomerName,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            GrossAmount = invoice.GrossAmount,
            Status = invoice.Status,
            IsDraft = isDraft,
            IsVoided = isVoided,
            IsActiveEvidence = isActive,
            StatusText = CustomerSalesInvoiceEvidenceRules.StatusText(invoice.Status),
            TotalRowCount = facts.TotalRowCount,
            ActiveRowCount = facts.ActiveRowCount,
            ActiveAmount = facts.ActiveAmount,
            VoidedRowCount = facts.VoidedRowCount,
            EffectiveCount = facts.EffectiveCount,
            EffectiveAmount = facts.EffectiveAmount,
            EffectiveReceiptCount = facts.EffectiveReceiptCount,
            AllocationState = allocationState,
            AllocationStateText = CustomerReceivableReconciliationRules.AllocationStateText(allocationState),
            RemainingAmount = remainingAmount,
            RemainingState = remainingState,
            RemainingStateText = CustomerReceivableReconciliationRules.RemainingStateText(remainingState),
            DueDateKnown = false,
            AgingBucket = CustomerReceivableReconciliationRules.UnknownDueDateBucket,
            AgingBucketText = CustomerReceivableReconciliationRules.BucketText(
                CustomerReceivableReconciliationRules.UnknownDueDateBucket),
        };
    }

    /// <summary>分配状态判定（只按持久化分摊行派生；over_allocated 只在读取时派生）</summary>
    private static string AllocationStateOf(InvoiceFacts facts, decimal grossAmount)
    {
        if (facts.TotalRowCount == 0) return CustomerReceivableReconciliationRules.AllocationNone;
        if (facts.EffectiveCount == 0) return CustomerReceivableReconciliationRules.AllocationHistoricalOnly;
        if (facts.EffectiveAmount > grossAmount) return CustomerReceivableReconciliationRules.AllocationOverAllocated;
        if (facts.EffectiveAmount < grossAmount) return CustomerReceivableReconciliationRules.AllocationPartial;
        return CustomerReceivableReconciliationRules.AllocationFull;
    }

    /// <summary>算术剩余证据判定（非有效证据或超额分摊时不给剩余金额）</summary>
    private static (decimal? Remaining, string State) RemainingOf(
        InvoiceFacts facts, decimal grossAmount, bool isActiveEvidence)
    {
        if (!isActiveEvidence) return (null, CustomerReceivableReconciliationRules.RemainingUnknown);
        if (facts.EffectiveAmount > grossAmount)
            return (null, CustomerReceivableReconciliationRules.RemainingOverAllocated);
        return (grossAmount - facts.EffectiveAmount, CustomerReceivableReconciliationRules.RemainingKnown);
    }

    /// <summary>求和（任一行未知即整体未知：绝不用 0 顶替，也不给部分合计）</summary>
    private static decimal? KnownAmounts(
        IReadOnlyList<CustomerReceivableReconciliationInvoiceRow> rows,
        Func<CustomerReceivableReconciliationInvoiceRow, decimal?> selector)
    {
        var values = rows.Select(selector).ToList();
        return values.Any(v => v is null) ? null : values.Sum(v => v!.Value);
    }

    // ==================== 3. 汇总与分组 ====================

    /// <summary>按币种汇总（只统计有效证据；不同币种分别成行，绝无跨币种总额）</summary>
    private static List<CustomerReceivableReconciliationCurrencySummary> BuildCurrencySummaries(
        IReadOnlyList<CustomerReceivableReconciliationInvoiceRow> rows)
        => rows
            .Where(r => r.IsActiveEvidence)
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CustomerReceivableReconciliationCurrencySummary
            {
                Currency = g.Key,
                AmountDecimals = CurrencyAmountRules.PrecisionOf(g.Key),
                InvoiceCount = g.Count(),
                GrossAmount = g.Sum(r => r.GrossAmount),
                EffectiveAllocatedAmount = g.Sum(r => r.EffectiveAmount),
                RemainingAmount = KnownAmounts(g.ToList(), r => r.RemainingAmount),
            })
            .ToList();

    /// <summary>按客户 + 币种分组的对账汇总（本页发票）</summary>
    private static List<CustomerReceivableReconciliationGroup> BuildGroups(
        IReadOnlyList<CustomerReceivableReconciliationInvoiceRow> rows)
        => rows
            .GroupBy(r => new { r.CustomerId, r.CustomerCode, r.CustomerName, r.Currency })
            .OrderBy(g => g.Key.CustomerId)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new CustomerReceivableReconciliationGroup
            {
                CustomerId = g.Key.CustomerId,
                CustomerCode = g.Key.CustomerCode,
                CustomerName = g.Key.CustomerName,
                Currency = g.Key.Currency,
                InvoiceCount = g.Count(),
                GrossAmount = g.Sum(r => r.GrossAmount),
                EffectiveAllocatedAmount = g.Sum(r => r.EffectiveAmount),
                RemainingAmount = KnownAmounts(g.ToList(), r => r.RemainingAmount),
                Invoices = g.ToList(),
            })
            .ToList();

    /// <summary>「未知到期日」分组（独立成组，不参与账龄桶；含有界发票身份清单）</summary>
    private static List<CustomerReceivableReconciliationUnknownDueDateGroup> BuildUnknownDueDateGroups(
        IReadOnlyList<CustomerReceivableReconciliationInvoiceRow> rows)
        => rows
            .Where(r => !r.DueDateKnown)
            .GroupBy(r => new { r.CustomerId, r.CustomerCode, r.CustomerName, r.Currency })
            .OrderBy(g => g.Key.CustomerId)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new CustomerReceivableReconciliationUnknownDueDateGroup
            {
                CustomerId = g.Key.CustomerId,
                CustomerCode = g.Key.CustomerCode,
                CustomerName = g.Key.CustomerName,
                Currency = g.Key.Currency,
                AmountDecimals = CurrencyAmountRules.PrecisionOf(g.Key.Currency),
                InvoiceCount = g.Count(),
                GrossAmount = g.Sum(r => r.GrossAmount),
                EffectiveAllocatedAmount = g.Sum(r => r.EffectiveAmount),
                RemainingAmount = KnownAmounts(g.ToList(), r => r.RemainingAmount),
                InvoiceIdentities = g
                    .Take(CustomerReceivableReconciliationRules.MaxUnknownDueIdentitiesPerGroup)
                    .Select(r => r.IdentityText)
                    .ToList(),
                Note = CustomerReceivableReconciliationRules.BucketText(
                    CustomerReceivableReconciliationRules.UnknownDueDateBucket),
            })
            .ToList();

    // ==================== 4. 单张发票明细（重新校验授权；fail closed） ====================

    /// <summary>
    /// 单张发票的对账证据明细（只读派生）：打开明细时重新校验当前登录身份与既有「角色 → 菜单」模块授权，
    /// 并重新读取权威来源；发票不存在 / 已删除时拒绝。fail closed：不返回任何证据、不返回部分金额。
    /// </summary>
    public static async Task<CustomerReceivableReconciliationInvoiceDetail> ForInvoiceDetailAsync(
        IErpDbContext db, long invoiceId, long? userId, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (invoiceId <= 0)
            throw BusinessException.InvalidParameter($"发票 Id 必须为正整数：{invoiceId}");

        var asOf = (asOfDate ?? DateTime.Today).Date;

        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看客户应收账款对账证据明细", ErrorCodes.Unauthorized);

        var menuCodes = await LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(
                CustomerReceivableReconciliationRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{CustomerReceivableReconciliationRules.RequiredMenuText}」"
                + $"（{CustomerReceivableReconciliationRules.RequiredMenuCode}）模块授权：拒绝打开应收账款对账证据明细"
                + "（fail closed，不返回任何证据、不做来源修复或改派）",
                ErrorCodes.Forbidden);
        }

        var invoice = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted);
        if (invoice is null)
        {
            throw BusinessException.NotFound(
                "客户销项发票证据不存在或已删除：无法确认应收账款对账与账龄证据"
                + "（fail closed，不返回部分证据，也不做任何修复或改派）");
        }

        var context = await LoadPageContextAsync(db, new[] { invoice });
        var row = MapInvoice(invoice, context, asOf);

        var loadedRows = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id)
            .OrderByDescending(a => a.AllocatedAt).ThenByDescending(a => a.Id)
            .Take(CustomerReceivableReconciliationRules.MaxDetailsPerInvoice + 1)
            .ToListAsync();
        var allocationsTruncated = loadedRows.Count > CustomerReceivableReconciliationRules.MaxDetailsPerInvoice;
        var displayRows = loadedRows.Take(CustomerReceivableReconciliationRules.MaxDetailsPerInvoice).ToList();

        var receiptIds = displayRows.Select(a => a.ReceiptId).Distinct().ToList();
        var receipts = (await db.FinanceReceipts.AsNoTracking()
                .Where(r => receiptIds.Contains(r.Id)).ToListAsync())
            .ToDictionary(r => r.Id);

        return new CustomerReceivableReconciliationInvoiceDetail
        {
            InvoiceId = invoiceId,
            AsOfDate = asOf,
            Row = row,
            AllocationsTruncated = allocationsTruncated,
            Allocations = displayRows.Select(a => MapAllocationRow(a, invoice, receipts)).ToList(),
            AuthorizationNote =
                $"打开明细时已重新校验登录身份与「{CustomerReceivableReconciliationRules.RequiredMenuText}」"
                + $"（{CustomerReceivableReconciliationRules.RequiredMenuCode}）模块授权（复用既有「角色 → 菜单」口径），"
                + "并重新读取持久化发票、ERP-073 分摊行与收款单证据（只读，全程不写库）。",
        };
    }

    /// <summary>把一条分摊行映射为明细行（有效性与收款单可读性在此判定；纯计算）</summary>
    private static CustomerReceivableReconciliationAllocationRow MapAllocationRow(
        CustomerSalesInvoiceCollectionAllocation row, CustomerSalesInvoiceEvidence invoice,
        Dictionary<long, FinanceReceipt> receipts)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var receiptExists = receipts.TryGetValue(row.ReceiptId, out var receipt);
        var receiptAvailable = receiptExists
            && receipt is not null
            && !receipt!.IsDeleted
            && receipt.Status != DocumentStatus.Cancelled;
        var isVoided = row.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided;
        var invoiceActive = invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded;
        var currencyMatches = string.Equals(
            currency, CurrencyAmountRules.NormalizeCurrency(invoice.Currency), StringComparison.Ordinal);
        var customerMatches = row.CustomerId == invoice.CustomerId;
        var isEffective = !isVoided && invoiceActive && receiptAvailable && currencyMatches && customerMatches;

        return new CustomerReceivableReconciliationAllocationRow
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
            AmountText = CustomerSalesInvoiceCollectionAllocationRules.AmountText(row.AllocatedAmount, currency),
            Currency = currency,
            Status = row.Status,
            StatusText = CustomerSalesInvoiceCollectionAllocationRules.StatusText(row.Status),
            IsEffective = isEffective,
            EffectivenessText = isEffective
                ? "计入有效已分摊合计（未作废 + 发票已登记 + 收款单可读未取消 + 快照客户 / 币种一致）"
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

    /// <summary>当前账号被允许访问的菜单编码（fail closed：无身份 / 无角色 / 无菜单授权 → 空集合）</summary>
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

    // ==================== 5. 私有承载类型 ====================

    /// <summary>单张发票的派生事实（本维度分摊行按状态 / 有效性聚合；纯计算产物）</summary>
    private sealed class InvoiceFacts
    {
        public int TotalRowCount { get; set; }
        public int ActiveRowCount { get; set; }
        public decimal ActiveAmount { get; set; }
        public int VoidedRowCount { get; set; }
        public int EffectiveCount { get; set; }
        public decimal EffectiveAmount { get; set; }
        public int EffectiveReceiptCount { get; set; }
    }

    /// <summary>本页派生上下文（本页发票的派生事实 + 客户主数据快照；只读）</summary>
    private sealed class PageContext
    {
        public Dictionary<long, InvoiceFacts> Facts { get; init; } = new();
        public Dictionary<long, BaseCustomer> Customers { get; init; } = new();
    }

    /// <summary>空事实（当某张发票没有任何派生证据时使用；绝不臆造金额）</summary>
    private static readonly InvoiceFacts EmptyFacts = new();

    /// <summary>分摊行按（发票, 状态）聚合的中间结果</summary>
    private sealed record AllocationStatusStat(long InvoiceId, int Status, int Count, decimal Amount);

    /// <summary>有效分摊按发票聚合的中间结果</summary>
    private sealed record AllocationAggregateStat(long InvoiceId, int Count, decimal Amount);

    /// <summary>有效分摊的（发票, 收款单）组合（用于去重计数）</summary>
    private sealed record AllocationPairStat(long InvoiceId, long ReceiptId);
}