using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜费用分摊证据服务（ERP-060）。职责：把 ERP-042 已持久化的分摊批次与分摊行按
/// 「装柜清单（一柜）→ 币种 → 客户」**只读**呈现，并在装柜明细入口、装柜结算单工作流入口与
/// 一个有界分页的工作台中复用同一套口径：
/// <list type="number">
/// <item><see cref="GetForLoadingListAsync"/>：单个装柜清单的分摊证据（有效批次 / 币种分组 / 客户 /
/// 未分摊参考 / 已作废历史 / 失效链接标注）；</item>
/// <item><see cref="GetForSettlementAsync"/>：装柜结算单的持久化字段只读回显 + 其关联装柜清单上的分摊证据
/// （分摊证据**不参与**结算金额，也不回写结算单）；</item>
/// <item><see cref="ListAsync"/>：按显式字段筛选的有界分页工作台（容器维度汇总）。</item>
/// </list>
/// <para>审计口径：ERP-042 的 <c>FinanceExpenseAllocationBatches</c> / <c>FinanceExpenseAllocationLines</c>
/// 是分摊证据的唯一权威来源；本服务<strong>不新建表 / 列、不重算金额、不写库</strong>
/// （全库查询均为 <c>AsNoTracking</c>，无 Add / Update / Remove / SaveChanges），
/// 也不改写装柜清单、明细、参与方、订柜跟踪值、单证、库存、订单、发票、费用与结算记录。</para>
/// <para>读取性能：同一范围内固定次数的批量查询（装柜清单 / 批次 / 分摊行 / 客户 / 参与方 / 柜级来源费用），
/// 与行数无关；一切扫描有界（<see cref="ContainerExpenseAllocationEvidenceRules.MaxBatchScan"/> /
/// <see cref="ContainerExpenseAllocationEvidenceRules.MaxLineScan"/>），截断时显式标注。</para>
/// </summary>
public static class ContainerExpenseAllocationEvidenceService
{
    // ==================== 1. 装柜清单维度（只读） ====================

    /// <summary>
    /// 单个装柜清单的分摊证据（<b>只读，不写库</b>）：有效批次与「币种 → 客户」分组、
    /// 已作废 / 历史异常批次（可按 <paramref name="includeHistory"/> 排除）、未分摊参考、
    /// 失效链接标注与口径边界文案。装柜清单不存在或已删除时照实标注不可用，留痕仍可读。
    /// </summary>
    public static async Task<ContainerExpenseAllocationEvidenceDto> GetForLoadingListAsync(
        IErpDbContext db, long loadingListId,
        bool includeHistory = true,
        int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
    {
        var id = ContainerExpenseAllocationEvidenceRules.EnsureScopeId(loadingListId, "装柜清单");
        var take = ContainerExpenseAllocationEvidenceRules.NormalizeHistoryTake(historyTake);
        var scope = await LoadScopeAsync(db, id);
        return BuildDetail(scope, ContainerExpenseAllocationEvidenceRules.ScopeLoadingList, id, includeHistory, take);
    }

    // ==================== 2. 装柜结算单维度（只读） ====================

    /// <summary>
    /// 装柜结算单的分摊证据（<b>只读，不写库</b>）：结算单持久化字段（结算总金额 / 海运费 / 其他费用 / 客户）
    /// 只读回显 + 其显式关联装柜清单上的分摊证据，两者分开标注；分摊证据既不参与结算金额计算，
    /// 也不会写入结算单任何字段。结算单未关联装柜清单时证据显示「未知」，不做任何推断。
    /// </summary>
    public static async Task<ContainerSettlementAllocationEvidenceDto> GetForSettlementAsync(
        IErpDbContext db, long settlementId,
        bool includeHistory = true,
        int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
    {
        var id = ContainerExpenseAllocationEvidenceRules.EnsureScopeId(settlementId, "装柜结算单");
        var take = ContainerExpenseAllocationEvidenceRules.NormalizeHistoryTake(historyTake);

        var settlement = await db.FinanceContainerSettlements.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        if (settlement is null || settlement.IsDeleted)
            throw BusinessException.NotFound("装柜结算单不存在或已删除，不能查看分摊证据");

        var customer = settlement.CustomerId <= 0
            ? null
            : await db.BaseCustomers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == settlement.CustomerId);
        var customerAvailable = customer is not null
            && !customer.IsDeleted
            && customer.Status == ContainerLoadingParticipantRules.ActiveStatus;

        if (settlement.LoadingListId is null or <= 0)
        {
            return BuildSettlementDto(
                settlement, customer, customerAvailable, evidence: null, string.Empty, string.Empty,
                evidenceUnavailableText: "结算单未关联装柜清单（LoadingListId 为空）：分摊证据未知，"
                    + "不按柜号或客户推断，也不代表零费用或已结清",
                comparisonComparable: false,
                comparisonText: ContainerExpenseAllocationEvidenceRules.SettlementComparisonText(
                    false, string.Empty, settlement.TotalAmount, 0m, 0));
        }

        var scope = await LoadScopeAsync(db, settlement.LoadingListId.Value);
        var evidence = BuildDetail(
            scope, ContainerExpenseAllocationEvidenceRules.ScopeSettlement, id, includeHistory, take);

        var singleCurrency = evidence.Groups.Count == 1 ? evidence.Groups[0].Currency : string.Empty;
        var allocatedTotal = evidence.Groups.Count == 1 ? evidence.Groups[0].AllocatedTotal : 0m;

        return BuildSettlementDto(
            settlement, customer, customerAvailable, evidence,
            evidence.ContainerNo, evidence.LoadingListNo,
            evidenceUnavailableText: string.Empty,
            comparisonComparable: evidence.HasEvidence && evidence.Groups.Count == 1,
            comparisonText: ContainerExpenseAllocationEvidenceRules.SettlementComparisonText(
                evidence.HasEvidence, singleCurrency, settlement.TotalAmount, allocatedTotal, evidence.Groups.Count));
    }

    /// <summary>组装结算单分摊证据 DTO（结算字段只读回显 + 证据 + 对照口径文案）</summary>
    private static ContainerSettlementAllocationEvidenceDto BuildSettlementDto(
        FinanceContainerSettlement settlement,
        BaseCustomer? customer,
        bool customerAvailable,
        ContainerExpenseAllocationEvidenceDto? evidence,
        string containerNo,
        string loadingListNo,
        string evidenceUnavailableText,
        bool comparisonComparable,
        string comparisonText) =>
        new(
            settlement.Id,
            settlement.SettlementNo ?? string.Empty,
            settlement.SettlementDate,
            ContainerShipmentReferenceRules.DocumentStatusText((int)settlement.Status),
            settlement.CustomerId,
            ContainerLoadingParticipantRules.DisplayName(
                customer?.CustomerName, customer?.CustomerCode, settlement.CustomerId),
            customerAvailable,
            ContainerExpenseAllocationEvidenceRules.CustomerAvailabilityText(customerAvailable),
            settlement.LoadingListId,
            loadingListNo,
            containerNo,
            settlement.TotalAmount,
            settlement.FreightCost,
            settlement.OtherCost,
            ContainerExpenseAllocationEvidenceRules.SettlementTotalsText,
            evidence is not null,
            evidenceUnavailableText,
            comparisonComparable,
            comparisonText,
            evidence,
            ContainerExpenseAllocationEvidenceRules.BoundaryText,
            ContainerExpenseAllocationEvidenceRules.DisclaimerText,
            ContainerExpenseAllocationEvidenceRules.ReadOnlyText);


    // ==================== 3. 有界装载（只读，与行数无关的固定查询次数） ====================

    /// <summary>证据范围装载结果（只读快照：装柜清单 + 批次 + 分摊行 + 客户 / 参与方 / 柜级来源费用映射）</summary>
    private sealed record EvidenceScope(
        long LoadingListId,
        ContainerLoadingList? LoadingList,
        bool LoadingListAvailable,
        string LoadingListNo,
        string ContainerNo,
        bool ContainerNoAvailable,
        List<FinanceExpenseAllocationBatch> Batches,
        bool BatchScanBounded,
        Dictionary<long, List<FinanceExpenseAllocationLine>> LinesByBatch,
        bool LineScanBounded,
        Dictionary<long, BaseCustomer> Customers,
        Dictionary<long, ContainerLoadingListParticipant> Participants,
        List<FinanceExpense> ContainerSources,
        bool ContainerSourceScanBounded);

    /// <summary>
    /// 按装柜清单 Id 有界装载证据（6 次批量查询：装柜清单 / 批次 / 分摊行 / 客户 / 参与方 / 柜级来源费用）：
    /// 无论批次与行数多少，查询次数恒定（无逐行数据库访问）；超出上限时置截断标记并由调用方显式说明。
    /// </summary>
    private static async Task<EvidenceScope> LoadScopeAsync(IErpDbContext db, long loadingListId)
    {
        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == loadingListId);
        var loadingListAvailable = loadingList is not null && !loadingList.IsDeleted;
        var containerNo = (loadingList?.ContainerNo ?? string.Empty).Trim();

        var batchScan = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .Where(x => x.LoadingListId == loadingListId)
            .OrderByDescending(x => x.Id)
            .Take(ContainerExpenseAllocationEvidenceRules.MaxBatchScan + 1)
            .ToListAsync();
        var batchScanBounded = batchScan.Count > ContainerExpenseAllocationEvidenceRules.MaxBatchScan;
        var batches = batchScanBounded
            ? batchScan.Take(ContainerExpenseAllocationEvidenceRules.MaxBatchScan).ToList()
            : batchScan;

        var batchIds = batches.Select(b => b.Id).ToList();
        var lineScan = batchIds.Count == 0
            ? new List<FinanceExpenseAllocationLine>()
            : await db.FinanceExpenseAllocationLines.AsNoTracking()
                .Where(l => batchIds.Contains(l.BatchId))
                .OrderBy(l => l.BatchId).ThenBy(l => l.SortOrder).ThenBy(l => l.Id)
                .Take(ContainerExpenseAllocationEvidenceRules.MaxLineScan + 1)
                .ToListAsync();
        var lineScanBounded = lineScan.Count > ContainerExpenseAllocationEvidenceRules.MaxLineScan;
        var lines = lineScanBounded
            ? lineScan.Take(ContainerExpenseAllocationEvidenceRules.MaxLineScan).ToList()
            : lineScan;

        var customers = await LoadCustomersAsync(db, lines.Select(l => l.CustomerId));
        var participants = await LoadParticipantsAsync(db, lines.Select(l => l.ParticipantId));
        var (sources, sourceScanBounded) = await LoadContainerSourceExpensesAsync(db, containerNo);

        var linesByBatch = lines
            .GroupBy(l => l.BatchId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToList());

        return new EvidenceScope(
            loadingListId,
            loadingList,
            loadingListAvailable,
            (loadingList?.LoadingListNo ?? string.Empty).Trim(),
            containerNo,
            containerNo.Length > 0,
            batches,
            batchScanBounded,
            linesByBatch,
            lineScanBounded,
            customers,
            participants,
            sources,
            sourceScanBounded);
    }

    /// <summary>分摊行涉及客户的一次批量查询（避免逐行查询；缺失的客户即「客户不可用」）</summary>
    private static async Task<Dictionary<long, BaseCustomer>> LoadCustomersAsync(
        IErpDbContext db, IEnumerable<long> customerIds)
    {
        var ids = customerIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, BaseCustomer>();
        var customers = await db.BaseCustomers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync();
        return customers.ToDictionary(c => c.Id);
    }

    /// <summary>分摊行涉及参与方的一次批量查询（用于校验持久化链接，不做任何改派）</summary>
    private static async Task<Dictionary<long, ContainerLoadingListParticipant>> LoadParticipantsAsync(
        IErpDbContext db, IEnumerable<long> participantIds)
    {
        var ids = participantIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, ContainerLoadingListParticipant>();
        var participants = await db.ContainerLoadingListParticipants.AsNoTracking()
            .Where(p => ids.Contains(p.Id)).ToListAsync();
        return participants.ToDictionary(p => p.Id);
    }

    /// <summary>
    /// 本柜柜级来源费用单（归属类型为整柜 / 拼柜 / 散货且归属单号等于本柜柜号）的有界装载：
    /// 口径与 ERP-042 的来源费用扫描一致（<see cref="ContainerExpenseAllocationService.MaxSourceScan"/> 条上限），
    /// 仅用于「未分摊参考」，不推断是否需要分摊。
    /// </summary>
    private static async Task<(List<FinanceExpense> Sources, bool Bounded)> LoadContainerSourceExpensesAsync(
        IErpDbContext db, string containerNo)
    {
        if (containerNo.Length == 0) return (new List<FinanceExpense>(), false);

        var key = containerNo.ToUpperInvariant();
        var refTypes = ContainerExpenseAllocationRules.ContainerRefTypes;
        var scan = await db.FinanceExpenses.AsNoTracking()
            .Where(x => !x.IsDeleted && refTypes.Contains(x.RefType))
            .Where(x => x.RefNo != null && x.RefNo.Trim().ToUpper() == key)
            .OrderByDescending(x => x.Id)
            .Take(ContainerExpenseAllocationService.MaxSourceScan + 1)
            .ToListAsync();

        var bounded = scan.Count > ContainerExpenseAllocationService.MaxSourceScan;
        return (bounded
            ? scan.Take(ContainerExpenseAllocationService.MaxSourceScan).ToList()
            : scan, bounded);
    }


    // ==================== 4. 证据组装（纯映射，只读） ====================

    /// <summary>组装装柜清单 / 结算单范围的分摊证据（有效批次 + 币种分组 + 历史 + 未分摊参考 + 边界文案）</summary>
    private static ContainerExpenseAllocationEvidenceDto BuildDetail(
        EvidenceScope scope, string scopeType, long scopeId, bool includeHistory, int historyTake)
    {
        var activeBatches = scope.Batches.Where(b => ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status)).ToList();
        var historyBatches = scope.Batches.Where(b => !ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status)).ToList();

        var activeDtos = activeBatches.Select(b => BuildBatch(scope, b)).ToList();
        var historyDtos = historyBatches.Select(b => BuildBatch(scope, b)).ToList();
        var keptHistory = includeHistory ? historyDtos.Take(historyTake).ToList() : new List<ContainerAllocationEvidenceBatchDto>();

        var groups = BuildGroups(activeDtos);

        var activeLineCount = activeBatches.Sum(b => b.LineCount);
        var activeCustomerCount = activeDtos
            .SelectMany(b => b.Lines)
            .Select(l => l.CustomerId)
            .Distinct()
            .Count();

        // 失效链接计数：逐行标注（含柜号不一致行）为主；批次柜号不一致但没有任何已装载行时按批次计 1 条，
        // 避免同一问题被重复计数（既不夸大也不隐藏）。
        var containerMismatchBatchesWithoutLines = scope.Batches.Count(b =>
            ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status)
            && EvaluateContainerLink(scope, b) == false
            && !(scope.LinesByBatch.TryGetValue(b.Id, out var loaded) && loaded.Count > 0));
        var invalidLines = activeDtos.Concat(historyDtos).SelectMany(b => b.Lines).Count(l => l.LinkInvalid);
        var (orphanBatchReferences, legacyRowCount, unallocated) = BuildUnallocated(scope);

        var invalidLinkCount = invalidLines + containerMismatchBatchesWithoutLines + orphanBatchReferences;
        var unknownStatusCount = scope.Batches.Count(b =>
            !ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status)
            && !ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(b.Status));
        var voidedCount = historyBatches.Count - unknownStatusCount;

        var hasEvidence = activeBatches.Count > 0;
        var currencies = groups.Select(g => g.Currency).ToList();

        return new ContainerExpenseAllocationEvidenceDto(
            scopeType,
            scopeId,
            scope.LoadingListId,
            scope.LoadingListNo,
            scope.LoadingListAvailable,
            scope.LoadingListAvailable
                ? "装柜清单可用：证据按该清单显式链接读取（不改派、不回填）"
                : ContainerExpenseAllocationEvidenceRules.LoadingListUnavailableText,
            scope.ContainerNo,
            scope.ContainerNoAvailable,
            ContainerExpenseAllocationEvidenceRules.ContainerContextText(
                scope.ContainerNoAvailable, scope.ContainerNo),
            hasEvidence,
            ContainerExpenseAllocationEvidenceRules.EvidenceStatusText(
                activeBatches.Count, voidedCount, unknownStatusCount),
            ContainerExpenseAllocationEvidenceRules.EvidenceMissingText(activeBatches.Count)
                + (voidedCount + unknownStatusCount == 0
                    ? string.Empty
                    : "；" + ContainerExpenseAllocationEvidenceRules.VoidedOnlyText(
                        voidedCount, unknownStatusCount)),
            scope.BatchScanBounded || scope.LineScanBounded,
            activeBatches.Count,
            voidedCount,
            unknownStatusCount,
            activeLineCount,
            activeCustomerCount,
            currencies,
            groups,
            activeDtos,
            keptHistory,
            historyDtos.Count > keptHistory.Count,
            historyTake,
            historyDtos.Count,
            unallocated,
            BuildUnallocatedContextText(scope, unallocated, legacyRowCount),
            legacyRowCount,
            ContainerExpenseAllocationEvidenceRules.LegacyAllocationText(legacyRowCount),
            invalidLinkCount,
            ContainerExpenseAllocationEvidenceRules.InvalidLinkText(invalidLinkCount),
            ContainerExpenseAllocationEvidenceRules.BoundaryText,
            ContainerExpenseAllocationEvidenceRules.DisclaimerText,
            ContainerExpenseAllocationEvidenceRules.ReadOnlyText,
            DateTime.Now);
    }

    /// <summary>未分摊参考上下文文案（含扫描截断说明）</summary>
    private static string BuildUnallocatedContextText(
        EvidenceScope scope,
        IReadOnlyList<ContainerAllocationEvidenceUnallocatedDto> unallocated,
        int legacyRowCount)
    {
        var count = unallocated.Sum(u => u.SourceExpenseCount);
        var text = ContainerExpenseAllocationEvidenceRules.UnallocatedContextText(
            scope.ContainerNoAvailable, count, legacyRowCount);
        var bounded = scope.ContainerSourceScanBounded
            ? $"；本柜柜级来源费用单超过 {ContainerExpenseAllocationService.MaxSourceScan} 条，未分摊参考按最近 {ContainerExpenseAllocationService.MaxSourceScan} 条比对"
            : string.Empty;
        var truncated = scope.BatchScanBounded || scope.LineScanBounded
            ? $"；{ContainerExpenseAllocationEvidenceRules.ScanBoundedText}"
            : string.Empty;
        return text + bounded + truncated;
    }


    /// <summary>
    /// 未分摊参考与历史留痕分类（只读）：把本柜柜级来源费用单与「有效批次来源费用」比对 ——
    /// 有效批次来源 → 已分摊；费用单自身带历史分摊痕迹 → 历史分摊行（无批次留痕，单独计数）；
    /// 费用单批次号在本柜批次中不存在 → 失效批次引用（不改派）；其余按币种进入未分摊参考。
    /// </summary>
    private static (int OrphanBatchReferences, int LegacyRowCount, List<ContainerAllocationEvidenceUnallocatedDto> Buckets)
        BuildUnallocated(EvidenceScope scope)
    {
        var activeSourceIds = scope.Batches
            .Where(b => ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status))
            .Select(b => b.SourceExpenseId).ToHashSet();
        var voidedSourceIds = scope.Batches
            .Where(b => ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(b.Status))
            .Select(b => b.SourceExpenseId).ToHashSet();
        var batchNos = scope.Batches
            .Select(b => (b.BatchNo ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var legacyRowCount = 0;
        var orphanBatchReferences = 0;
        var unallocated = new List<FinanceExpense>();

        foreach (var expense in scope.ContainerSources)
        {
            if (activeSourceIds.Contains(expense.Id)) continue;

            if (ContainerExpenseAllocationRules.HasLegacyAllocationEvidence(expense))
            {
                legacyRowCount++;
                continue;
            }

            var batchNo = (expense.AllocationBatchNo ?? string.Empty).Trim();
            if (batchNo.Length > 0 && !batchNos.Contains(batchNo))
            {
                orphanBatchReferences++;
                continue;
            }

            unallocated.Add(expense);
        }

        var buckets = unallocated
            .GroupBy(e => ContainerExpenseAllocationRules.NormalizeCurrency(e.Currency), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var currency = g.Key;
                var precision = ContainerExpenseAllocationRules.PrecisionOf(currency);
                var count = g.Count();
                var amount = g.Sum(e => e.Amount);
                var voidedCount = g.Count(e => voidedSourceIds.Contains(e.Id));
                var expenseNos = g.OrderByDescending(e => e.Id)
                    .Select(e => e.ExpenseNo ?? string.Empty)
                    .Take(ContainerExpenseAllocationEvidenceRules.MaxUnallocatedExpenseNos)
                    .ToList();
                var truncated = count > expenseNos.Count;
                var voidedText = voidedCount == 0
                    ? string.Empty
                    : $"，其中 {voidedCount} 条所属批次已作废（有效分摊为无，需重新分摊）";
                var text = $"{currency} 未分摊参考：{count} 条柜级来源费用单 / 合计 "
                    + $"{ContainerExpenseAllocationEvidenceRules.AmountText(amount, currency)}{voidedText}"
                    + "；未分摊只表示未登记有效分摊批次，不代表费用为零、已结清或应收应付";

                return new ContainerAllocationEvidenceUnallocatedDto(
                    currency, precision, count, amount, voidedCount, expenseNos, truncated, text);
            })
            .ToList();

        return (orphanBatchReferences, legacyRowCount, buckets);
    }

    /// <summary>批次柜号链接判定（只读校验）：<c>null</c> = 装柜清单不可用，无法校验</summary>
    private static bool? EvaluateContainerLink(EvidenceScope scope, FinanceExpenseAllocationBatch batch)
    {
        if (!scope.LoadingListAvailable) return null;
        return string.Equals(
            (batch.ContainerNo ?? string.Empty).Trim(),
            scope.ContainerNo,
            StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>批次证据 DTO（合计取自批次持久化列；逐行留痕按有界装载结果映射）</summary>
    private static ContainerAllocationEvidenceBatchDto BuildBatch(
        EvidenceScope scope, FinanceExpenseAllocationBatch batch)
    {
        var currency = ContainerExpenseAllocationRules.NormalizeCurrency(batch.Currency);
        var precision = ContainerExpenseAllocationRules.PrecisionOf(currency);
        var lines = scope.LinesByBatch.TryGetValue(batch.Id, out var found)
            ? found
            : new List<FinanceExpenseAllocationLine>();
        var linesTruncated = batch.LineCount > lines.Count;
        var containerLink = EvaluateContainerLink(scope, batch);
        var statusKnown = ContainerExpenseAllocationEvidenceRules.IsActiveStatus(batch.Status)
            || ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(batch.Status);
        var lineDtos = lines
            .Select(l => BuildLine(scope, batch, l, currency, precision, containerLink == true))
            .ToList();

        var byCustomer = lineDtos
            .GroupBy(l => l.CustomerId)
            .Select(g => (
                Display: g.First().CustomerDisplay,
                Amount: g.Sum(x => x.AllocatedAmount),
                Ratio: g.Sum(x => x.Ratio)))
            .ToList();
        var summaryItems = byCustomer
            .Take(ContainerExpenseAllocationEvidenceRules.MaxCustomerSummaryItems)
            .Select(c => ContainerExpenseAllocationEvidenceRules.CustomerSummaryItem(
                c.Display, c.Amount, currency, c.Ratio))
            .ToList();

        return new ContainerAllocationEvidenceBatchDto(
            batch.Id,
            batch.BatchNo ?? string.Empty,
            batch.SourceExpenseId,
            batch.SourceExpenseNo ?? string.Empty,
            batch.AllocationMethod ?? string.Empty,
            batch.BasisKind ?? string.Empty,
            currency,
            precision,
            batch.ExchangeRate,
            batch.SourceAmount,
            batch.AllocatedTotal,
            ContainerExpenseAllocationRules.ConvertToCny(batch.AllocatedTotal, currency, batch.ExchangeRate),
            batch.LineCount,
            lineDtos.Count,
            batch.Status,
            statusKnown,
            ContainerExpenseAllocationEvidenceRules.BatchStatusTextSafe(batch.Status),
            ContainerExpenseAllocationEvidenceRules.IsActiveStatus(batch.Status),
            ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(batch.Status),
            batch.VoidedAt,
            batch.VoidReason ?? string.Empty,
            batch.Remark ?? string.Empty,
            batch.CreatedAt,
            batch.UpdatedAt,
            batch.LoadingListNo ?? string.Empty,
            batch.ContainerNo ?? string.Empty,
            containerLink == true,
            ContainerExpenseAllocationEvidenceRules.ContainerLinkText(
                scope.LoadingListAvailable, containerLink == true),
            linesTruncated,
            ContainerExpenseAllocationEvidenceRules.CustomerSummaryText(byCustomer.Count, summaryItems),
            ContainerExpenseAllocationEvidenceRules.BatchTotalsText(
                currency, batch.SourceAmount, batch.AllocatedTotal, batch.LineCount,
                batch.AllocatedTotal == batch.SourceAmount),
            lineDtos);
    }


    /// <summary>分摊行证据 DTO（含参与方 / 客户 / 柜号链接校验，只标注不改派）</summary>
    private static ContainerAllocationEvidenceLineDto BuildLine(
        EvidenceScope scope,
        FinanceExpenseAllocationBatch batch,
        FinanceExpenseAllocationLine line,
        string currency,
        int precision,
        bool containerConsistent)
    {
        var customer = scope.Customers.TryGetValue(line.CustomerId, out var foundCustomer) ? foundCustomer : null;
        var customerAvailable = customer is not null
            && !customer.IsDeleted
            && customer.Status == ContainerLoadingParticipantRules.ActiveStatus;
        var participant = scope.Participants.TryGetValue(line.ParticipantId, out var foundParticipant)
            ? foundParticipant
            : null;

        var (invalid, status, text) = ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(
            scope.LoadingListAvailable,
            containerConsistent,
            participantFound: participant is not null,
            participantBelongsToScope: participant is not null && participant.LoadingListId == scope.LoadingListId,
            participantCustomerMatches: participant is not null && participant.CustomerId == line.CustomerId,
            customerAvailable: customerAvailable);

        var method = line.AllocationMethod ?? string.Empty;
        var basisKind = line.BasisKind ?? string.Empty;

        return new ContainerAllocationEvidenceLineDto(
            line.ParticipantId,
            line.CustomerId,
            line.CustomerCode ?? string.Empty,
            line.CustomerName ?? string.Empty,
            ContainerLoadingParticipantRules.DisplayName(line.CustomerName, line.CustomerCode, line.CustomerId),
            customerAvailable,
            ContainerExpenseAllocationEvidenceRules.CustomerAvailabilityText(customerAvailable),
            line.ParticipantPrimary,
            line.BatchId == 0 ? batch.Id : line.BatchId,
            line.BatchNo ?? batch.BatchNo ?? string.Empty,
            ContainerExpenseAllocationEvidenceRules.BatchStatusTextSafe(batch.Status),
            ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(batch.Status),
            batch.SourceExpenseId == 0 ? line.SourceExpenseId : batch.SourceExpenseId,
            batch.SourceExpenseNo ?? line.SourceExpenseNo ?? string.Empty,
            method,
            basisKind,
            line.BasisSource ?? string.Empty,
            line.BasisValue,
            ContainerExpenseAllocationRules.BasisEvidenceText(method, basisKind),
            line.Ratio,
            line.AllocatedAmount,
            line.AllocatedAmountCny,
            ContainerExpenseAllocationRules.NormalizeCurrency(line.Currency),
            precision,
            line.ExpenseNo ?? string.Empty,
            invalid,
            status,
            text,
            line.SortOrder);
    }


    /// <summary>按币种分组（不同币种永不合并、不换算）</summary>
    private static List<ContainerAllocationEvidenceCurrencyGroupDto> BuildGroups(
        IReadOnlyList<ContainerAllocationEvidenceBatchDto> activeBatches) =>
        activeBatches
            .GroupBy(b => b.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var currency = g.Key;
                var precision = g.First().AmountPrecision;
                var allocatedTotal = g.Sum(b => b.AllocatedTotal);
                var sourceTotal = g.Sum(b => b.SourceAmount);
                var lineCount = g.Sum(b => b.LineCount);
                var consistent = g.All(b => b.AllocatedTotal == b.SourceAmount);

                var customers = g.SelectMany(b => b.Lines)
                    .GroupBy(l => l.CustomerId)
                    .OrderBy(cg => cg.Key)
                    .Select(cg => BuildCustomerGroup(currency, precision, cg.ToList()))
                    .ToList();

                return new ContainerAllocationEvidenceCurrencyGroupDto(
                    currency,
                    ContainerExpenseAllocationEvidenceRules.CurrencyLabel(currency, precision),
                    precision,
                    g.Count(),
                    lineCount,
                    allocatedTotal,
                    g.Sum(b => b.AllocatedTotalCny),
                    sourceTotal,
                    consistent,
                    g.Any(b => b.LinesTruncated),
                    ContainerExpenseAllocationEvidenceRules.GroupTotalsText(
                        currency, allocatedTotal, sourceTotal, g.Count(), lineCount, consistent),
                    ContainerExpenseAllocationEvidenceRules.ConversionText(currency),
                    customers);
            })
            .ToList();

    /// <summary>某客户在某币种下的分摊证据（金额 / 比例 / 基数 / 批次号；链接失效逐行标注）</summary>
    private static ContainerAllocationEvidenceCustomerDto BuildCustomerGroup(
        string currency, int precision, List<ContainerAllocationEvidenceLineDto> lines)
    {
        var first = lines[0];
        var invalid = lines.FirstOrDefault(l => l.LinkInvalid);

        return new ContainerAllocationEvidenceCustomerDto(
            first.CustomerId,
            first.CustomerCode,
            first.CustomerName,
            first.CustomerDisplay,
            first.CustomerAvailable,
            first.CustomerAvailabilityText,
            invalid is not null,
            invalid?.LinkStatusText ?? first.LinkStatusText,
            currency,
            precision,
            lines.Sum(l => l.AllocatedAmount),
            lines.Sum(l => l.AllocatedAmountCny),
            lines.Sum(l => l.Ratio),
            lines.Count,
            lines.Select(l => l.BatchId).Distinct().Count(),
            string.Join("、", lines.Select(l => l.AllocationMethod).Distinct(StringComparer.Ordinal)),
            string.Join("、", lines
                .Select(l => $"{l.BasisKind}（{l.BasisSource}）")
                .Distinct(StringComparer.Ordinal)),
            lines.Select(l => l.BatchNo)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList());
    }


    // ==================== 5. 工作台（只读、有界分页） ====================

    /// <summary>
    /// 分摊证据工作台（<b>只读，不写库</b>）：按**显式持久化字段**（柜号 / 装柜清单号 / 批次号 / 币种 /
    /// 批次状态 / 客户 Id / 关键字）筛选并分页汇总装柜清单维度的分摊证据。按批次装载后按装柜清单分组，
    /// 单页内的装柜清单 / 分摊行 / 客户 / 参与方均批量装载（与页内行数无关的固定查询次数），
    /// 扫描上限 <see cref="ContainerExpenseAllocationEvidenceRules.MaxBatchScan"/>，截断时显式标注。
    /// </summary>
    public static async Task<ContainerExpenseAllocationEvidenceWorkspaceDto> ListAsync(
        IErpDbContext db, ContainerExpenseAllocationEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = ContainerExpenseAllocationEvidenceRules.NormalizeStatusFilter(query.Status);
        var containerNo = ContainerExpenseAllocationEvidenceRules.NormalizeEqualsFilter(query.ContainerNo, "柜号");
        var loadingListNo = ContainerExpenseAllocationEvidenceRules.NormalizeEqualsFilter(query.LoadingListNo, "装柜清单号");
        var batchNo = ContainerExpenseAllocationEvidenceRules.NormalizeEqualsFilter(query.BatchNo, "分摊批次号");
        var currency = ContainerExpenseAllocationEvidenceRules.NormalizeCurrencyFilter(query.Currency);
        var customerId = ContainerExpenseAllocationEvidenceRules.NormalizeCustomerIdFilter(query.CustomerId);
        var keyword = ContainerExpenseAllocationEvidenceRules.NormalizeKeyword(query.Keyword);

        var batchesQuery = db.FinanceExpenseAllocationBatches.AsNoTracking();
        if (!query.IncludeHistory)
            batchesQuery = batchesQuery.Where(x => x.Status == ContainerExpenseAllocationRules.BatchActive);
        if (status.HasValue)
            batchesQuery = batchesQuery.Where(x => x.Status == status.Value);
        if (containerNo is not null)
            batchesQuery = batchesQuery.Where(x =>
                x.ContainerNo != null && x.ContainerNo.Trim().ToUpper() == containerNo);
        if (loadingListNo is not null)
            batchesQuery = batchesQuery.Where(x =>
                x.LoadingListNo != null && x.LoadingListNo.Trim().ToUpper() == loadingListNo);
        if (batchNo is not null)
            batchesQuery = batchesQuery.Where(x =>
                x.BatchNo != null && x.BatchNo.Trim().ToUpper() == batchNo);
        if (currency is not null)
            batchesQuery = batchesQuery.Where(x =>
                x.Currency != null && x.Currency.Trim().ToUpper() == currency);
        if (customerId.HasValue)
        {
            var id = customerId.Value;
            batchesQuery = batchesQuery.Where(b =>
                db.FinanceExpenseAllocationLines.Any(l => l.BatchId == b.Id && l.CustomerId == id));
        }
        if (keyword is not null)
        {
            batchesQuery = batchesQuery.Where(b =>
                (b.ContainerNo != null && b.ContainerNo.Contains(keyword))
                || (b.LoadingListNo != null && b.LoadingListNo.Contains(keyword))
                || (b.BatchNo != null && b.BatchNo.Contains(keyword))
                || (b.SourceExpenseNo != null && b.SourceExpenseNo.Contains(keyword))
                || db.FinanceExpenseAllocationLines.Any(l => l.BatchId == b.Id
                    && ((l.CustomerCode != null && l.CustomerCode.Contains(keyword))
                        || (l.CustomerName != null && l.CustomerName.Contains(keyword)))));
        }

        var scanned = await batchesQuery.OrderByDescending(x => x.Id)
            .Take(ContainerExpenseAllocationEvidenceRules.MaxBatchScan + 1)
            .ToListAsync();
        var scanBounded = scanned.Count > ContainerExpenseAllocationEvidenceRules.MaxBatchScan;
        if (scanBounded)
            scanned = scanned.Take(ContainerExpenseAllocationEvidenceRules.MaxBatchScan).ToList();

        var grouped = scanned
            .GroupBy(b => b.LoadingListId)
            .Select(g => new
            {
                LoadingListId = g.Key,
                MaxBatchId = g.Max(b => b.Id),
                Batches = g.OrderByDescending(b => b.Id).ToList()
            })
            .OrderByDescending(g => g.MaxBatchId)
            .ToList();

        var pageContainers = grouped
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        var pageBatchIds = pageContainers.SelectMany(c => c.Batches).Select(b => b.Id).ToList();
        var pageListIds = pageContainers.Select(c => c.LoadingListId).Distinct().ToList();

        var loadingLists = pageListIds.Count == 0
            ? new List<ContainerLoadingList>()
            : await db.ContainerLoadingLists.AsNoTracking()
                .Where(x => pageListIds.Contains(x.Id)).ToListAsync();
        var listsById = loadingLists.ToDictionary(x => x.Id);

        var lines = pageBatchIds.Count == 0
            ? new List<FinanceExpenseAllocationLine>()
            : await db.FinanceExpenseAllocationLines.AsNoTracking()
                .Where(l => pageBatchIds.Contains(l.BatchId))
                .OrderBy(l => l.BatchId).ThenBy(l => l.SortOrder).ThenBy(l => l.Id)
                .Take(ContainerExpenseAllocationEvidenceRules.MaxLineScan + 1)
                .ToListAsync();
        var lineScanBounded = lines.Count > ContainerExpenseAllocationEvidenceRules.MaxLineScan;
        if (lineScanBounded)
            lines = lines.Take(ContainerExpenseAllocationEvidenceRules.MaxLineScan).ToList();

        var linesByBatch = lines
            .GroupBy(l => l.BatchId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToList());
        var customers = await LoadCustomersAsync(db, lines.Select(l => l.CustomerId));
        var participants = await LoadParticipantsAsync(db, lines.Select(l => l.ParticipantId));

        var items = pageContainers
            .Select(c => BuildRow(c.LoadingListId, c.Batches, listsById, linesByBatch, customers, participants))
            .ToList();

        return new ContainerExpenseAllocationEvidenceWorkspaceDto(
            items,
            grouped.Count,
            query.Page,
            query.PageSize,
            scanBounded || lineScanBounded,
            scanBounded || lineScanBounded
                ? ContainerExpenseAllocationEvidenceRules.ScanBoundedText
                : "查询有界：结果已按显式字段筛选并分页，单次扫描不超过上限（未截断）",
            ContainerExpenseAllocationEvidenceRules.BasisAndCurrencyText,
            ContainerExpenseAllocationEvidenceRules.GroupingText,
            ContainerExpenseAllocationEvidenceRules.UnallocatedRuleText,
            ContainerExpenseAllocationEvidenceRules.MissingEvidenceText,
            ContainerExpenseAllocationEvidenceRules.BoundaryText,
            ContainerExpenseAllocationEvidenceRules.DisclaimerText,
            ContainerExpenseAllocationEvidenceRules.ReadOnlyText);
    }

    // ==================== 6. 工作台行映射（只读） ====================

    /// <summary>工作台行（容器维度摘要）：按币种汇总批次持久化合计 + 客户条数 + 链接与失效提示</summary>
    private static ContainerAllocationEvidenceRowDto BuildRow(
        long loadingListId,
        List<FinanceExpenseAllocationBatch> batches,
        IReadOnlyDictionary<long, ContainerLoadingList> listsById,
        IReadOnlyDictionary<long, List<FinanceExpenseAllocationLine>> linesByBatch,
        IReadOnlyDictionary<long, BaseCustomer> customers,
        IReadOnlyDictionary<long, ContainerLoadingListParticipant> participants)
    {
        listsById.TryGetValue(loadingListId, out var loadingList);
        var loadingListAvailable = loadingList is not null && !loadingList.IsDeleted;
        var containerNo = (loadingList?.ContainerNo ?? string.Empty).Trim();

        var activeBatches = batches
            .Where(b => ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status)).ToList();
        var voidedCount = batches.Count(b => ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(b.Status));
        var unknownStatusCount = batches.Count(b => !ContainerExpenseAllocationEvidenceRules.IsActiveStatus(b.Status)
            && !ContainerExpenseAllocationEvidenceRules.IsVoidedStatus(b.Status));

        var containerMismatch = loadingListAvailable && batches.Any(b => !string.Equals(
            (b.ContainerNo ?? string.Empty).Trim(), containerNo, StringComparison.OrdinalIgnoreCase));

        var lines = activeBatches
            .SelectMany(b => linesByBatch.TryGetValue(b.Id, out var found)
                ? found
                : new List<FinanceExpenseAllocationLine>())
            .ToList();

        var invalidLines = CountInvalidLines(
            loadingListId, activeBatches, linesByBatch, customers, participants, loadingListAvailable, containerNo);
        var mismatchWithoutLines = containerMismatch
            && !activeBatches.Any(b => linesByBatch.TryGetValue(b.Id, out var ls) && ls.Count > 0);
        invalidLines += mismatchWithoutLines ? 1 : 0;

        var groups = activeBatches
            .GroupBy(b => ContainerExpenseAllocationRules.NormalizeCurrency(b.Currency), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var currency = g.Key;
                var batchIds = g.Select(b => b.Id).ToHashSet();
                var groupLines = lines.Where(l => batchIds.Contains(l.BatchId)).ToList();
                var customerIds = groupLines.Select(l => l.CustomerId).Distinct().ToList();
                var names = customerIds
                    .Take(ContainerExpenseAllocationEvidenceRules.MaxCustomerSummaryItems)
                    .Select(id =>
                    {
                        var line = groupLines.FirstOrDefault(l => l.CustomerId == id);
                        return line is null
                            ? $"客户#{id}"
                            : ContainerLoadingParticipantRules.DisplayName(line.CustomerName, line.CustomerCode, id);
                    })
                    .ToList();

                return new ContainerAllocationEvidenceSummaryGroupDto(
                    currency,
                    ContainerExpenseAllocationEvidenceRules.CurrencyLabel(
                        currency, ContainerExpenseAllocationRules.PrecisionOf(currency)),
                    ContainerExpenseAllocationRules.PrecisionOf(currency),
                    g.Count(),
                    g.Sum(b => b.AllocatedTotal),
                    customerIds.Count,
                    ContainerExpenseAllocationEvidenceRules.CustomerSummaryText(customerIds.Count, names));
            })
            .ToList();

        var activeLineCount = activeBatches.Sum(b => b.LineCount);
        var activeCustomerCount = lines.Select(l => l.CustomerId).Distinct().Count();
        var totalsText = groups.Count == 0
            ? ContainerExpenseAllocationEvidenceRules.MissingEvidenceText
            : string.Join("；", groups.Select(g =>
                $"{g.Currency} {g.AllocatedTotal}（{g.ActiveBatchCount} 个有效批次 / {g.CustomerCount} 个客户）"));

        var basisText = activeBatches.Count == 0
            ? "无有效分摊批次（无基数留痕可呈现）"
            : $"方法：{JoinDistinct(activeBatches.Select(b => b.AllocationMethod))}；"
              + $"基数种类：{JoinDistinct(activeBatches.Select(b => b.BasisKind))}";

        var customerSummary = groups.Count == 0
            ? ContainerExpenseAllocationEvidenceRules.CustomerSummaryText(0, Array.Empty<string>())
            : string.Join("｜", groups.Select(g => $"{g.Currency}：{g.CustomerNamesText}"));

        var invalidLinkCount = invalidLines;
        return new ContainerAllocationEvidenceRowDto(
            loadingListId,
            loadingListAvailable,
            loadingListAvailable
                ? "装柜清单可用：证据按该清单显式链接读取（不改派、不回填）"
                : ContainerExpenseAllocationEvidenceRules.LoadingListUnavailableText,
            (loadingList?.LoadingListNo ?? string.Empty).Trim(),
            containerNo,
            containerNo.Length > 0,
            !containerMismatch,
            ContainerExpenseAllocationEvidenceRules.ContainerLinkText(loadingListAvailable, !containerMismatch),
            activeBatches.Count,
            voidedCount,
            unknownStatusCount,
            activeLineCount,
            activeCustomerCount,
            groups.Select(g => g.Currency).ToList(),
            groups,
            totalsText,
            basisText,
            customerSummary,
            ContainerExpenseAllocationEvidenceRules.EvidenceStatusText(
                activeBatches.Count, voidedCount, unknownStatusCount),
            invalidLinkCount,
            ContainerExpenseAllocationEvidenceRules.InvalidLinkText(invalidLinkCount),
            batches.Count == 0 ? null : batches.Max(b => b.CreatedAt),
            ContainerExpenseAllocationEvidenceRules.ReadOnlyText);
    }


    /// <summary>统计失效 / 历史异常链接行数（与详情口径同一判定入口，只统计不修复）</summary>
    private static int CountInvalidLines(
        long loadingListId,
        IReadOnlyList<FinanceExpenseAllocationBatch> batches,
        IReadOnlyDictionary<long, List<FinanceExpenseAllocationLine>> linesByBatch,
        IReadOnlyDictionary<long, BaseCustomer> customers,
        IReadOnlyDictionary<long, ContainerLoadingListParticipant> participants,
        bool loadingListAvailable,
        string containerNo)
    {
        var count = 0;
        foreach (var batch in batches)
        {
            if (!linesByBatch.TryGetValue(batch.Id, out var batchLines)) continue;

            var containerConsistent = string.Equals(
                (batch.ContainerNo ?? string.Empty).Trim(), containerNo, StringComparison.OrdinalIgnoreCase);

            foreach (var line in batchLines)
            {
                var customer = customers.TryGetValue(line.CustomerId, out var foundCustomer) ? foundCustomer : null;
                var customerAvailable = customer is not null
                    && !customer.IsDeleted
                    && customer.Status == ContainerLoadingParticipantRules.ActiveStatus;
                var participant = participants.TryGetValue(line.ParticipantId, out var foundParticipant)
                    ? foundParticipant
                    : null;

                var (invalid, _, _) = ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(
                    loadingListAvailable,
                    containerConsistent,
                    participantFound: participant is not null,
                    participantBelongsToScope: participant is not null && participant.LoadingListId == loadingListId,
                    participantCustomerMatches: participant is not null && participant.CustomerId == line.CustomerId,
                    customerAvailable: customerAvailable);
                if (invalid) count++;
            }
        }

        return count;
    }

    /// <summary>去重拼接（按序去重；空集合返回「—」）</summary>
    private static string JoinDistinct(IEnumerable<string?> values)
    {
        var items = values
            .Select(v => (v ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return items.Count == 0 ? "—" : string.Join("、", items);
    }
}
