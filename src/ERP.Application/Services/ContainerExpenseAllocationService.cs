using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜费用分摊批次服务（ERP-042）。职责：
/// <list type="number">
/// <item><b>上下文</b>（<see cref="GetContextAsync"/>）：只读汇总装柜清单、参与方、持久化装柜总量、
/// 该柜费用单的可分摊资格与留痕分类、现存有效 / 已作废批次；</item>
/// <item><b>预览</b>（<see cref="PreviewAsync"/>）：只读计算各参与方比例与分摊金额（含余差归属），不写库；</item>
/// <item><b>生成</b>（<see cref="GenerateAsync"/>）：在**一次 SaveChanges** 内写入批次 + 逐行留痕 +
/// 既有费用单行（金额 / 币种 / 归属 / 分摊基数 / 比例 / 分摊金额沿用既有字段），失败不留部分行；</item>
/// <item><b>作废</b>（<see cref="VoidAsync"/>）：显式作废批次（保留历史与逐行留痕），不物理删除、不改写来源费用；</item>
/// <item><b>台账</b>（<see cref="ListBatchesAsync"/> / <see cref="GetBatchAsync"/>）与
/// <b>读侧留痕标注</b>（<see cref="AnnotateLineageAsync"/>）：分页、有界、无逐行查询。</item>
/// </list>
/// <para>边界（重要）：只新增既有 <see cref="FinanceExpense"/> 行与本两张留痕表 ——
/// <strong>不</strong>改写来源费用单的金额 / 归属 / 付款状态，<strong>不</strong>改写装柜清单与明细数量 / 箱数 / 重量 / 体积、
/// 参与方身份、订柜外贸与物流跟踪值、单证、库存与库存流水、采购订单与销售订单，也<strong>不</strong>记账、
/// <strong>不</strong>生成凭证 / 收款 / 付款 / 结算单。</para>
/// </summary>
public static class ContainerExpenseAllocationService
{
    /// <summary>单个装柜清单上下文最多回显的来源费用条数（有界）</summary>
    public const int MaxSourceScan = 50;

    /// <summary>单个装柜清单上下文最多回显的批次数（有界）</summary>
    public const int MaxBatchScan = 50;

    /// <summary>重复生成（同一来源费用 + 装柜清单已有有效批次，不区分方法）的对外文案</summary>
    public const string DuplicateBatchMessage =
        "该来源费用在本柜已存在有效分摊批次（不区分分摊方法），不能重复生成（如需更正请先作废原批次）";

    /// <summary>并发 / 唯一索引兜底时的对外文案</summary>
    public const string DuplicateBatchConflictMessage =
        "该来源费用在本柜的分摊批次正在被其他请求写入，请重试";

    /// <summary>批次号前缀（EAB-yyyyMMdd-序号）</summary>
    public const string BatchNoPrefix = "EAB-";

    /// <summary>费用单号前缀（EXP-yyyyMMdd-序号，与既有拼柜分摊口径一致）</summary>
    public const string ExpenseNoPrefix = "EXP-";

    // ==================== 1. 上下文（只读） ====================

    /// <summary>
    /// 分摊上下文（<b>只读，不写库</b>）：装柜清单与（含停用的）参与方、持久化装柜总量、
    /// 该柜费用单的资格判定与留痕分类、现存有效 / 已作废批次，以及方法 / 余差规则 / 边界声明。
    /// 参与方与来源费用均按有界上限返回，批次与分摊行一次批量查询（无逐行查询）。
    /// </summary>
    public static async Task<ContainerExpenseAllocationContextDto> GetContextAsync(
        IErpDbContext db, long loadingListId)
    {
        var loadingList = await ContainerLoadingParticipantService.EnsureLoadingListAsync(db, loadingListId);
        var participants = await ContainerLoadingParticipantService.ListAsync(db, loadingListId);
        var batches = await LoadBatchDtosAsync(db, loadingListId);
        var sources = await LoadSourceExpensesAsync(db, loadingList);

        var batchStatusByNo = batches
            .GroupBy(b => b.BatchNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);

        var activeParticipants = participants.Where(p => p.Selectable).ToList();
        var primary = participants.FirstOrDefault(p => p.IsPrimary && p.Selectable);

        return new ContainerExpenseAllocationContextDto(
            loadingList.Id,
            loadingList.LoadingListNo ?? string.Empty,
            loadingList.ContainerNo ?? string.Empty,
            loadingList.TotalCartons,
            loadingList.TotalWeight,
            loadingList.TotalVolume,
            loadingList.TotalCartons > 0 || loadingList.TotalWeight > 0 || loadingList.TotalVolume > 0,
            activeParticipants.Count,
            participants.Count,
            primary?.CustomerId,
            primary?.CustomerName ?? string.Empty,
            participants.Count == 0,
            ParticipantScopeText(participants),
            participants,
            sources.Select(e => BuildSourceDto(e, loadingList, batchStatusByNo)).ToList(),
            batches.Where(b => b.IsActive).ToList(),
            batches.Where(b => b.IsVoided).ToList(),
            ContainerExpenseAllocationRules.SupportedMethods.ToList(),
            ContainerExpenseAllocationRules.WholeContainerBasisKinds.ToList(),
            ContainerExpenseAllocationRules.RemainderRuleText,
            ContainerExpenseAllocationRules.WholeContainerRuleText,
            ContainerExpenseAllocationRules.BoundaryText);
    }

    /// <summary>分摊范围文案（显式说明哪些参与方参与分摊、哪些历史参与方不参与，不静默忽略）</summary>
    public static string ParticipantScopeText(IReadOnlyList<ContainerLoadingParticipantDto> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var active = participants.Where(p => p.Selectable).ToList();
        var inactive = participants.Count - active.Count;

        if (participants.Count == 0)
            return ContainerLoadingParticipantRules.LegacySingleCustomerText
                + "：该清单没有参与方行，不能按客户分摊（请先在「多客户参与方」维护参与客户）";

        if (active.Count == 0)
            return $"该清单的 {participants.Count} 条参与方全部停用或客户不可用：没有可分摊的参与方";

        var names = string.Join("、", active.Select(p =>
            ContainerLoadingParticipantRules.DisplayName(p.CustomerName, p.CustomerCode, p.CustomerId)));

        return inactive == 0
            ? $"分摊范围 = 该装柜清单启用中的 {active.Count} 条参与方：{names}"
            : $"分摊范围 = 该装柜清单启用中的 {active.Count} 条参与方：{names}"
              + $"（另有 {inactive} 条停用 / 不可用历史参与方不参与分摊）";
    }

    /// <summary>该柜（按柜号匹配归属单号）有界加载费用单，按 Id 倒序，最多 <see cref="MaxSourceScan"/> 条</summary>
    private static async Task<List<FinanceExpense>> LoadSourceExpensesAsync(
        IErpDbContext db, ContainerLoadingList loadingList)
    {
        var containerNo = (loadingList.ContainerNo ?? string.Empty).Trim();
        if (containerNo.Length == 0) return new List<FinanceExpense>();

        var key = containerNo.ToUpperInvariant();
        var refTypes = ContainerExpenseAllocationRules.ContainerRefTypes;

        return await db.FinanceExpenses.AsNoTracking()
            .Where(x => !x.IsDeleted && refTypes.Contains(x.RefType))
            .Where(x => x.RefNo != null && x.RefNo.Trim().ToUpper() == key)
            .OrderByDescending(x => x.Id)
            .Take(MaxSourceScan)
            .ToListAsync();
    }

    /// <summary>来源费用行（含资格文案与留痕分类，只读）</summary>
    private static ContainerExpenseAllocationSourceDto BuildSourceDto(
        FinanceExpense expense, ContainerLoadingList loadingList, IReadOnlyDictionary<string, int> batchStatusByNo)
    {
        ArgumentNullException.ThrowIfNull(expense);

        var (eligible, eligibility) = ContainerExpenseAllocationRules.EvaluateEligibility(expense, loadingList);

        var batchNo = (expense.AllocationBatchNo ?? string.Empty).Trim();
        int? batchStatus = null;
        if (batchNo.Length > 0 && batchStatusByNo.TryGetValue(batchNo, out var status)) batchStatus = status;

        return new ContainerExpenseAllocationSourceDto(
            expense.Id,
            expense.ExpenseNo ?? string.Empty,
            expense.ExpenseType ?? string.Empty,
            expense.Amount,
            ContainerExpenseAllocationRules.NormalizeCurrency(expense.Currency),
            expense.RefType ?? string.Empty,
            expense.RefNo ?? string.Empty,
            eligible,
            eligibility,
            ContainerExpenseAllocationRules.LineageOf(expense),
            ContainerExpenseAllocationRules.LineageText(expense, batchStatus),
            batchNo);
    }

    // ==================== 2. 读侧映射（台账 / 下游列表共用） ====================

    /// <summary>按装柜清单有界加载批次台账 DTO（<paramref name="loadingListId"/> &lt;= 0 表示不过滤清单）</summary>
    public static async Task<List<ContainerExpenseAllocationBatchDto>> LoadBatchDtosAsync(
        IErpDbContext db, long loadingListId, int? status = null)
    {
        var batches = await LoadBatchEntitiesAsync(db, loadingListId, status);
        return await MapBatchesAsync(db, batches);
    }

    /// <summary>按装柜清单 / 状态有界加载批次实体（最多 <see cref="MaxBatchScan"/> 条）</summary>
    public static async Task<List<FinanceExpenseAllocationBatch>> LoadBatchEntitiesAsync(
        IErpDbContext db, long loadingListId, int? status = null)
    {
        var query = db.FinanceExpenseAllocationBatches.AsNoTracking().Where(x => !x.IsDeleted);
        if (loadingListId > 0) query = query.Where(x => x.LoadingListId == loadingListId);
        if (status is not null) query = query.Where(x => x.Status == status.Value);

        return await query.OrderByDescending(x => x.Id).Take(MaxBatchScan).ToListAsync();
    }

    /// <summary>批次实体 → DTO：分摊行与客户可用性各一次批量查询（无逐行查询）</summary>
    private static async Task<List<ContainerExpenseAllocationBatchDto>> MapBatchesAsync(
        IErpDbContext db, IReadOnlyList<FinanceExpenseAllocationBatch> batches)
    {
        var result = new List<ContainerExpenseAllocationBatchDto>(batches.Count);
        if (batches.Count == 0) return result;

        var ids = batches.Select(x => x.Id).ToList();
        var lines = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(x => !x.IsDeleted && ids.Contains(x.BatchId))
            .OrderBy(x => x.BatchId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync();
        var linesByBatch = lines.GroupBy(x => x.BatchId).ToDictionary(g => g.Key, g => g.ToList());
        var customers = await LoadCustomerMapAsync(db, lines);

        foreach (var batch in batches)
        {
            var batchLines = linesByBatch.TryGetValue(batch.Id, out var list)
                ? list
                : new List<FinanceExpenseAllocationLine>();
            result.Add(MapBatch(batch, batchLines, customers));
        }
        return result;
    }

    /// <summary>批次 → DTO（含状态文案、有效 / 已作废标记与逐行留痕）</summary>
    private static ContainerExpenseAllocationBatchDto MapBatch(
        FinanceExpenseAllocationBatch batch, IReadOnlyList<FinanceExpenseAllocationLine> lines,
        IReadOnlyDictionary<long, BaseCustomer> customers)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return new ContainerExpenseAllocationBatchDto(
            batch.Id,
            batch.BatchNo ?? string.Empty,
            batch.SourceExpenseId,
            batch.SourceExpenseNo ?? string.Empty,
            batch.LoadingListId,
            batch.LoadingListNo ?? string.Empty,
            batch.ContainerNo ?? string.Empty,
            batch.AllocationMethod ?? string.Empty,
            batch.BasisKind ?? string.Empty,
            ContainerExpenseAllocationRules.NormalizeCurrency(batch.Currency),
            batch.ExchangeRate,
            batch.SourceAmount,
            batch.AllocatedTotal,
            batch.LineCount,
            batch.Status,
            ContainerExpenseAllocationRules.BatchStatusText(batch.Status),
            batch.Status == ContainerExpenseAllocationRules.BatchActive,
            batch.Status == ContainerExpenseAllocationRules.BatchVoided,
            batch.VoidedAt,
            batch.VoidReason ?? string.Empty,
            batch.Remark ?? string.Empty,
            batch.CreatedAt,
            batch.UpdatedAt,
            ContainerExpenseAllocationRules.BoundaryText,
            lines.Select(l => MapStoredLine(l, customers)).ToList());
    }

    /// <summary>已落库分摊行 → DTO（客户可用性按当前客户主数据显式标注，历史留痕照常可读）</summary>
    private static ContainerExpenseAllocationLineDto MapStoredLine(
        FinanceExpenseAllocationLine line, IReadOnlyDictionary<long, BaseCustomer> customers)
    {
        ArgumentNullException.ThrowIfNull(line);
        customers.TryGetValue(line.CustomerId, out var customer);
        var available = customer is not null
            && !customer.IsDeleted
            && customer.Status == ContainerLoadingParticipantRules.ActiveStatus;

        return new ContainerExpenseAllocationLineDto(
            line.ParticipantId,
            line.CustomerId,
            line.CustomerCode ?? string.Empty,
            line.CustomerName ?? string.Empty,
            ContainerLoadingParticipantRules.DisplayName(line.CustomerName, line.CustomerCode, line.CustomerId),
            available,
            ContainerLoadingParticipantRules.AvailabilityText(true, available),
            line.ParticipantPrimary,
            line.AllocationMethod ?? string.Empty,
            line.BasisKind ?? string.Empty,
            line.BasisSource ?? string.Empty,
            line.BasisValue,
            ContainerExpenseAllocationRules.BasisEvidenceText(
                line.AllocationMethod ?? string.Empty, line.BasisKind ?? string.Empty),
            line.Ratio,
            line.AllocatedAmount,
            line.AllocatedAmountCny,
            ContainerExpenseAllocationRules.NormalizeCurrency(line.Currency),
            false,
            line.ExpenseId,
            line.ExpenseNo ?? string.Empty,
            line.SortOrder);
    }

    /// <summary>分摊行涉及客户的一次批量查询（避免逐行查询）</summary>
    private static async Task<Dictionary<long, BaseCustomer>> LoadCustomerMapAsync(
        IErpDbContext db, IReadOnlyList<FinanceExpenseAllocationLine> lines)
    {
        if (lines.Count == 0) return new Dictionary<long, BaseCustomer>();
        var ids = lines.Select(x => x.CustomerId).Distinct().ToList();
        var customers = await db.BaseCustomers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync();
        return customers.ToDictionary(c => c.Id);
    }

    // ==================== 3. 预览（只读） ====================

    /// <summary>
    /// 分摊预览（<b>只读，不写库</b>）：对一条柜级来源费用按该柜启用参与方计算比例与分摊金额，
    /// 返回参与方 / 方法 / 基数种类与来源 / 基数值 / 比例 / 分摊金额与余差归属。
    /// 预览与生成共用同一计算入口（<see cref="BuildPlanAsync"/>），因此预览所示即生成结果。
    /// </summary>
    public static async Task<ContainerExpenseAllocationPreviewDto> PreviewAsync(
        IErpDbContext db, ContainerExpenseAllocationRequest request)
    {
        var plan = await BuildPlanAsync(db, request);
        return plan.Preview;
    }

    /// <summary>分摊计划（预览与生成的唯一计算入口：同一套校验与同一套结果）</summary>
    private sealed record AllocationPlan(
        ContainerLoadingList LoadingList,
        FinanceExpense Source,
        string Method,
        string BasisKind,
        string BasisSource,
        List<ContainerExpenseAllocationLineDto> Lines,
        ContainerExpenseAllocationPreviewDto Preview);

    /// <summary>
    /// 构建分摊计划：校验来源费用资格、参与方范围、方法与基数，按币种精度计算比例与金额
    /// （余差归基准值最大的参与方）。全部校验通过后才返回；不写库、不改写任何记录。
    /// </summary>
    private static async Task<AllocationPlan> BuildPlanAsync(
        IErpDbContext db, ContainerExpenseAllocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceExpenseId <= 0) throw BusinessException.InvalidParameter("请选择来源费用单");
        if (request.LoadingListId <= 0) throw BusinessException.InvalidParameter("请选择装柜清单");

        var method = ContainerExpenseAllocationRules.NormalizeMethod(request.AllocationMethod);
        ContainerExpenseAllocationRules.NormalizeRemark(request.Remark);

        var loadingList = await ContainerLoadingParticipantService.EnsureLoadingListAsync(db, request.LoadingListId);
        var source = await db.FinanceExpenses.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.SourceExpenseId);
        ContainerExpenseAllocationRules.EnsureEligibleSource(source, loadingList);
        if (source is null) throw BusinessException.NotFound("来源费用单不存在或已删除，不能作为分摊来源");

        var participants = await ContainerLoadingParticipantService.ListAsync(
            db, request.LoadingListId, activeOnly: true);
        if (participants.Count == 0)
            throw BusinessException.InvalidParameter(
                "该装柜清单没有启用中的参与方，不能按客户分摊（请先维护参与方或启用原参与方）");
        if (participants.Count > ContainerExpenseAllocationRules.MaxLinesPerBatch)
            throw BusinessException.InvalidParameter(
                $"该装柜清单启用中的参与方超过 {ContainerExpenseAllocationRules.MaxLinesPerBatch} 条，超出单次分摊上限");

        var currency = ContainerExpenseAllocationRules.NormalizeCurrency(source.Currency);

        string basisKind;
        string basisSource;
        decimal? persistedBasis = null;
        List<(ContainerLoadingParticipantDto Participant, decimal BasisValue)> inputs;

        if (ContainerExpenseAllocationRules.IsWholeContainer(method))
        {
            basisKind = ContainerExpenseAllocationRules.NormalizeWholeContainerBasisKind(request.WholeContainerBasisKind);
            var target = ResolveWholeContainerTarget(participants, request.WholeContainerParticipantId);
            persistedBasis = ContainerExpenseAllocationRules.PersistedBasisValue(loadingList, basisKind);
            if (persistedBasis <= 0)
                throw BusinessException.InvalidParameter(
                    $"整柜法基数缺失：装柜清单「{loadingList.LoadingListNo}」的"
                    + $"{ContainerExpenseAllocationRules.WholeContainerBasisText(basisKind)}为 {persistedBasis} —— "
                    + "持久化装柜证据不足，系统不会按经验推断（请先补齐装柜明细，或改用其他基数种类）");
            basisSource = ContainerExpenseAllocationRules.BasisSourcePersisted;
            inputs = new List<(ContainerLoadingParticipantDto, decimal)> { (target, persistedBasis.Value) };
        }
        else
        {
            basisKind = method;
            basisSource = ContainerExpenseAllocationRules.BasisSourceRequest;
            inputs = ResolveRequestBasis(participants, request.Lines);
        }

        var shares = ContainerExpenseAllocationRules.Distribute(
            source.Amount,
            currency,
            inputs.Select(i => new ContainerExpenseAllocationRules.AllocationBasis(i.Participant.Id, i.BasisValue))
                .ToList());

        return ComposePlan(
            loadingList, source, method, basisKind, basisSource, persistedBasis, participants, inputs, shares);
    }

    /// <summary>
    /// 把计算结果组装成预览（参与方 → 比例 / 金额 / 余差标记）并保留计划，
    /// 供预览与生成复用同一结果（预览所示即生成结果）。
    /// </summary>
    private static AllocationPlan ComposePlan(
        ContainerLoadingList loadingList,
        FinanceExpense source,
        string method,
        string basisKind,
        string basisSource,
        decimal? persistedBasis,
        IReadOnlyList<ContainerLoadingParticipantDto> participants,
        IReadOnlyList<(ContainerLoadingParticipantDto Participant, decimal BasisValue)> inputs,
        IReadOnlyList<ContainerExpenseAllocationRules.AllocationShare> shares)
    {
        var currency = ContainerExpenseAllocationRules.NormalizeCurrency(source.Currency);
        var lines = new List<ContainerExpenseAllocationLineDto>(shares.Count);
        var sort = 0;

        foreach (var share in shares)
        {
            var participant = participants.Single(p => p.Id == share.ParticipantId);
            lines.Add(new ContainerExpenseAllocationLineDto(
                participant.Id,
                participant.CustomerId,
                participant.CustomerCode ?? string.Empty,
                participant.CustomerName ?? string.Empty,
                ContainerLoadingParticipantRules.DisplayName(
                    participant.CustomerName, participant.CustomerCode, participant.CustomerId),
                participant.CustomerAvailable,
                participant.AvailabilityText,
                participant.IsPrimary,
                method,
                basisKind,
                basisSource,
                share.BasisValue,
                ContainerExpenseAllocationRules.BasisEvidenceText(method, basisKind),
                share.Ratio,
                share.AllocatedAmount,
                ContainerExpenseAllocationRules.ConvertToCny(share.AllocatedAmount, currency, source.ExchangeRate),
                currency,
                share.RemainderCarrier,
                null,
                string.Empty,
                sort++));
        }

        var allocatedTotal = lines.Sum(l => l.AllocatedAmount);
        var totalBasis = inputs.Sum(i => i.BasisValue);
        var exactTotal = shares.Sum(s => ContainerExpenseAllocationRules.RoundAmount(
            source.Amount * s.BasisValue / totalBasis, currency));

        var scopeText = ContainerExpenseAllocationRules.IsWholeContainer(method)
            ? $"整柜法：全额归参与方「{lines[0].CustomerDisplay}」；基数 = 装柜清单持久化"
              + $"{ContainerExpenseAllocationRules.WholeContainerBasisText(basisKind)} = {persistedBasis}"
            : ParticipantScopeText(participants);

        var preview = new ContainerExpenseAllocationPreviewDto(
            source.Id,
            source.ExpenseNo ?? string.Empty,
            source.ExpenseType ?? string.Empty,
            source.Amount,
            ContainerExpenseAllocationRules.ConvertToCny(source.Amount, currency, source.ExchangeRate),
            currency,
            ContainerExpenseAllocationRules.PrecisionOf(currency),
            source.RefType ?? string.Empty,
            source.RefNo ?? string.Empty,
            loadingList.Id,
            loadingList.LoadingListNo ?? string.Empty,
            loadingList.ContainerNo ?? string.Empty,
            method,
            basisKind,
            basisSource,
            lines.Count,
            lines.Sum(l => l.Ratio),
            allocatedTotal,
            allocatedTotal - exactTotal,
            lines.FirstOrDefault(l => l.RemainderCarrier)?.ParticipantId,
            ContainerExpenseAllocationRules.RemainderRuleText,
            ContainerExpenseAllocationRules.WholeContainerRuleText,
            scopeText,
            loadingList.TotalCartons,
            loadingList.TotalWeight,
            loadingList.TotalVolume,
            ContainerExpenseAllocationRules.BoundaryText,
            lines);

        return new AllocationPlan(
            loadingList, source, method, basisKind, basisSource, lines, preview);
    }

    // ==================== 4. 输入解析（显式且完整，绝不猜测） ====================

    /// <summary>
    /// 解析各参与方的请求基准值：必须**覆盖该装柜清单全部启用参与方**且逐个显式填写，
    /// 出现本柜以外的参与方、重复参与方、缺失或负数一律拒绝；显式填 0 视为「该客户不承担分摊」，
    /// 但基准值合计为 0 时仍会被拒绝（缺少可用证据，不按经验推断）。
    /// </summary>
    private static List<(ContainerLoadingParticipantDto Participant, decimal BasisValue)> ResolveRequestBasis(
        IReadOnlyList<ContainerLoadingParticipantDto> participants, List<ContainerExpenseAllocationBasisDto>? lines)
    {
        var provided = lines ?? new List<ContainerExpenseAllocationBasisDto>();
        var byId = new Dictionary<long, ContainerExpenseAllocationBasisDto>();
        foreach (var line in provided)
        {
            if (!byId.TryAdd(line.ParticipantId, line))
                throw BusinessException.Duplicate(
                    $"参与方（Id={line.ParticipantId}）重复提交基准值，请合并为一行");
        }

        var foreign = byId.Keys.Where(id => participants.All(p => p.Id != id)).ToList();
        if (foreign.Count > 0)
            throw BusinessException.InvalidParameter(
                $"参与方 {string.Join("、", foreign)} 不属于该装柜清单启用中的参与方"
                + "（可能已停用 / 已删除 / 属于其他柜）：分摊范围只包含本柜启用参与方");

        var result = new List<(ContainerLoadingParticipantDto, decimal)>();
        var missing = new List<string>();

        foreach (var participant in participants.OrderBy(p => p.Id))
        {
            var name = ContainerLoadingParticipantRules.DisplayName(
                participant.CustomerName, participant.CustomerCode, participant.CustomerId);

            if (!byId.TryGetValue(participant.Id, out var line) || line.BasisValue is null)
            {
                missing.Add(name);
                continue;
            }

            var value = line.BasisValue.Value;
            if (value < 0)
                throw BusinessException.InvalidParameter(
                    $"参与方「{name}」的基准值为负数（{value}）：基准值必须为非负数");

            result.Add((participant, value));
        }

        if (missing.Count > 0)
            throw BusinessException.InvalidParameter(
                $"缺少以下参与方的基准值：{string.Join("、", missing)} —— "
                + "基准值必须显式填写（服务端不会按体积经验值 / 历史比例 / 列表顺序推断）");

        return result;
    }

    /// <summary>
    /// 整柜法目标参与方：优先用请求显式指定的参与方（必须在启用范围内），
    /// 未指定时仅当该柜恰好一条启用参与方才可直接使用；其余情况一律拒绝（不按主参与方或列表顺序猜测）。
    /// </summary>
    private static ContainerLoadingParticipantDto ResolveWholeContainerTarget(
        IReadOnlyList<ContainerLoadingParticipantDto> participants, long? participantId)
    {
        if (participantId is not null)
        {
            var target = participants.FirstOrDefault(p => p.Id == participantId.Value);
            if (target is null)
                throw BusinessException.InvalidParameter(
                    $"整柜法目标参与方（Id={participantId}）不是该装柜清单启用中的参与方（可能已停用 / 已删除），"
                    + "请重新选择目标参与方");
            return target;
        }

        if (participants.Count == 1) return participants[0];

        throw BusinessException.InvalidParameter(
            $"整柜法必须显式指定唯一目标参与方：该柜有 {participants.Count} 条启用参与方，"
            + "系统不会按主参与方或列表顺序猜测");
    }

    // ==================== 5. 生成（一次 SaveChanges，失败不留部分行） ====================

    /// <summary>
    /// 生成分摊批次：写入批次 + 逐行留痕 + 既有费用单行（沿用金额 / 币种 / 汇率 / 归属 / 分摊基数 /
    /// 比例 / 分摊金额字段，并写入批次号与来源费用留痕）。同一「来源费用 + 装柜清单」已有**有效**批次时
    /// 拒绝重复生成（不区分分摊方法，避免同一笔柜级费用被重复分摊）；全部校验在写库前完成，
    /// 批次 / 分摊行 / 费用单行在<b>同一次 SaveChanges</b> 内提交，校验或持久化失败不会留下部分行。
    /// </summary>
    public static async Task<ContainerExpenseAllocationGenerateResultDto> GenerateAsync(
        IErpDbContext db, ContainerExpenseAllocationRequest request)
    {
        var plan = await BuildPlanAsync(db, request);
        var source = plan.Source;
        var loadingList = plan.LoadingList;

        // 业务口径：同一来源费用在同一装柜清单上最多只有一条有效批次（**不区分分摊方法**），
        // 否则同一笔柜级费用会被重复分摊到客户；过滤唯一索引（含方法）作为并发兜底。
        var duplicated = await db.FinanceExpenseAllocationBatches.AnyAsync(x => !x.IsDeleted
            && x.Status == ContainerExpenseAllocationRules.BatchActive
            && x.SourceExpenseId == source.Id
            && x.LoadingListId == loadingList.Id);
        if (duplicated)
            throw BusinessException.Duplicate(
                $"{DuplicateBatchMessage}（来源费用 {source.ExpenseNo} / 装柜清单 {loadingList.LoadingListNo}）");

        var today = DateTime.Today;
        var batchNo = await NextBatchNoAsync(db, today);
        var expenseSeq = await NextExpenseSeqAsync(db, today);
        var expensePrefix = $"{ExpenseNoPrefix}{today:yyyyMMdd}-";
        var currency = ContainerExpenseAllocationRules.NormalizeCurrency(source.Currency);
        var exchangeRate = source.ExchangeRate <= 0 ? 1m : source.ExchangeRate;
        var remark = ContainerExpenseAllocationRules.NormalizeRemark(request.Remark);
        var allocatedTotal = plan.Lines.Sum(l => l.AllocatedAmount);

        var batch = new FinanceExpenseAllocationBatch
        {
            BatchNo = batchNo,
            SourceExpenseId = source.Id,
            SourceExpenseNo = source.ExpenseNo ?? string.Empty,
            LoadingListId = loadingList.Id,
            LoadingListNo = loadingList.LoadingListNo ?? string.Empty,
            ContainerNo = loadingList.ContainerNo ?? string.Empty,
            AllocationMethod = plan.Method,
            BasisKind = plan.BasisKind,
            Currency = currency,
            ExchangeRate = exchangeRate,
            SourceAmount = source.Amount,
            AllocatedTotal = allocatedTotal,
            LineCount = plan.Lines.Count,
            Status = ContainerExpenseAllocationRules.BatchActive,
            Remark = remark
        };

        var expenseRemark = remark.Length > 0 ? remark : $"分摊批次 {batchNo}（来源费用 {source.ExpenseNo}）";
        var expenses = new List<FinanceExpense>(plan.Lines.Count);
        var storedLines = new List<FinanceExpenseAllocationLine>(plan.Lines.Count);
        var expenseNos = new List<string>(plan.Lines.Count);
        var seq = expenseSeq;
        var sort = 0;

        foreach (var line in plan.Lines)
        {
            var expenseNo = $"{expensePrefix}{seq:000}";
            seq++;

            var expense = new FinanceExpense
            {
                ExpenseNo = expenseNo,
                ExpenseDate = today,
                ExpenseType = source.ExpenseType ?? string.Empty,
                Amount = line.AllocatedAmount,
                Currency = currency,
                ExchangeRate = exchangeRate,
                AmountCny = line.AllocatedAmountCny,
                Payee = source.Payee ?? string.Empty,
                RefType = source.RefType ?? string.Empty,
                RefNo = source.RefNo ?? string.Empty,
                CustomerId = line.CustomerId,
                CustomerName = line.CustomerName ?? string.Empty,
                AllocationBase = plan.Method,
                AllocationRatio = line.Ratio,
                AllocatedAmount = line.AllocatedAmount,
                PaymentStatus = "未付",
                Remark = expenseRemark,
                AllocationBatchNo = batchNo,
                AllocationSourceExpenseId = source.Id,
                AllocationSourceExpenseNo = source.ExpenseNo ?? string.Empty
            };

            var stored = new FinanceExpenseAllocationLine
            {
                BatchNo = batchNo,
                SourceExpenseId = source.Id,
                SourceExpenseNo = source.ExpenseNo ?? string.Empty,
                LoadingListId = loadingList.Id,
                LoadingListNo = loadingList.LoadingListNo ?? string.Empty,
                ContainerNo = loadingList.ContainerNo ?? string.Empty,
                ParticipantId = line.ParticipantId,
                CustomerId = line.CustomerId,
                CustomerCode = line.CustomerCode ?? string.Empty,
                CustomerName = line.CustomerName ?? string.Empty,
                ParticipantPrimary = line.IsPrimary,
                AllocationMethod = plan.Method,
                BasisKind = plan.BasisKind,
                BasisSource = plan.BasisSource,
                BasisValue = line.BasisValue,
                Ratio = line.Ratio,
                AllocatedAmount = line.AllocatedAmount,
                AllocatedAmountCny = line.AllocatedAmountCny,
                Currency = currency,
                Expense = expense,                  // 由 EF 在同一 SaveChanges 内回填 ExpenseId
                ExpenseNo = expenseNo,
                SortOrder = sort++,
                Remark = remark
            };

            expenses.Add(expense);
            storedLines.Add(stored);
            batch.Lines.Add(stored);                // 由 EF 在同一 SaveChanges 内回填 BatchId
            expenseNos.Add(expenseNo);
        }

        db.FinanceExpenseAllocationBatches.Add(batch);
        db.FinanceExpenses.AddRange(expenses);
        db.FinanceExpenseAllocationLines.AddRange(storedLines);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (LooksLikeUniqueViolation(ex))
        {
            throw BusinessException.Duplicate(DuplicateBatchConflictMessage);
        }

        var resultLines = new List<ContainerExpenseAllocationLineDto>(plan.Lines.Count);
        for (var i = 0; i < plan.Lines.Count; i++)
            resultLines.Add(plan.Lines[i] with { ExpenseId = expenses[i].Id, ExpenseNo = expenseNos[i] });

        return new ContainerExpenseAllocationGenerateResultDto(
            batch.Id,
            batchNo,
            source.Id,
            source.ExpenseNo ?? string.Empty,
            loadingList.Id,
            loadingList.LoadingListNo ?? string.Empty,
            loadingList.ContainerNo ?? string.Empty,
            plan.Method,
            currency,
            source.Amount,
            allocatedTotal,
            plan.Lines.Count,
            ContainerExpenseAllocationRules.BatchStatusText(batch.Status),
            expenseNos,
            resultLines);
    }

    // ==================== 6. 作废与台账 ====================

    /// <summary>
    /// 作废分摊批次（<b>更正路径</b>）：只把批次状态改为「已作废」并记录作废原因与时间 ——
    /// 不物理删除批次与分摊行、不删除 / 不改写已生成的费用单行、不改写来源费用单，
    /// 也不产生任何收款 / 付款 / 结算 / 记账动作；作废后同一来源费用可重新生成。
    /// </summary>
    public static async Task<ContainerExpenseAllocationBatchDto> VoidAsync(
        IErpDbContext db, long batchId, string? reason)
    {
        if (batchId <= 0) throw BusinessException.InvalidParameter("分摊批次 Id 不合法");

        var reasonText = ContainerExpenseAllocationRules.NormalizeRemark(reason);
        if (reasonText.Length == 0)
            throw BusinessException.InvalidParameter("请填写作废原因：作废会保留历史，必须记录更正原因");

        var batch = await db.FinanceExpenseAllocationBatches
            .FirstOrDefaultAsync(x => x.Id == batchId && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"分摊批次（Id={batchId}）不存在或已删除");

        if (batch.Status == ContainerExpenseAllocationRules.BatchVoided)
            throw BusinessException.RuleConflict(
                $"分摊批次「{batch.BatchNo}」已于 {batch.VoidedAt:yyyy-MM-dd HH:mm} 作废"
                + $"（原因：{batch.VoidReason}），不能重复作废");

        batch.Status = ContainerExpenseAllocationRules.BatchVoided;
        batch.VoidedAt = DateTime.Now;
        batch.VoidReason = reasonText;
        batch.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        var lines = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(x => !x.IsDeleted && x.BatchId == batch.Id)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync();
        var customers = await LoadCustomerMapAsync(db, lines);
        return MapBatch(batch, lines, customers);
    }

    /// <summary>按来源费用 / 装柜清单 / 状态 / 关键字分页查询批次台账（有界、无逐行查询）</summary>
    public static async Task<PagedResult<ContainerExpenseAllocationBatchDto>> ListBatchesAsync(
        IErpDbContext db, ContainerExpenseAllocationBatchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();
        var status = ContainerExpenseAllocationRules.NormalizeBatchStatusFilter(query.Status);

        var source = db.FinanceExpenseAllocationBatches.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.SourceExpenseId is not null)
            source = source.Where(x => x.SourceExpenseId == query.SourceExpenseId.Value);
        if (query.LoadingListId is not null)
            source = source.Where(x => x.LoadingListId == query.LoadingListId.Value);
        if (status is not null)
            source = source.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x => x.BatchNo.Contains(keyword)
                || x.ContainerNo.Contains(keyword)
                || x.LoadingListNo.Contains(keyword)
                || x.SourceExpenseNo.Contains(keyword));
        }

        var total = await source.CountAsync();
        var batches = await source.OrderByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<ContainerExpenseAllocationBatchDto>
        {
            Items = await MapBatchesAsync(db, batches),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>按 Id 读取单个批次台账（含逐行留痕，只读）</summary>
    public static async Task<ContainerExpenseAllocationBatchDto> GetBatchAsync(IErpDbContext db, long batchId)
    {
        if (batchId <= 0) throw BusinessException.InvalidParameter("分摊批次 Id 不合法");

        var batch = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == batchId && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"分摊批次（Id={batchId}）不存在或已删除");

        var mapped = await MapBatchesAsync(db, new[] { batch });
        return mapped[0];
    }

    // ==================== 7. 读侧留痕标注与序号工具 ====================

    /// <summary>
    /// 为费用单列表 / 详情补写分摊留痕分类与文案（<b>只读，不写库</b>）：
    /// 有批次号的行按批次状态一次批量解析（有效 / 已作废 / 状态未知），
    /// 历史分摊行显式标注为「历史分摊（无批次留痕）」，既不做回填也不改写任何金额口径。
    /// </summary>
    public static async Task AnnotateLineageAsync(IErpDbContext db, IReadOnlyList<FinanceExpense> rows)
    {
        if (rows is null || rows.Count == 0) return;

        var batchNos = rows
            .Select(r => (r.AllocationBatchNo ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        var statusByNo = new Dictionary<string, int>(StringComparer.Ordinal);
        if (batchNos.Count > 0)
        {
            var batches = await db.FinanceExpenseAllocationBatches.AsNoTracking()
                .Where(x => !x.IsDeleted && batchNos.Contains(x.BatchNo))
                .Select(x => new { x.BatchNo, x.Status })
                .ToListAsync();
            foreach (var group in batches.GroupBy(x => x.BatchNo, StringComparer.Ordinal))
                statusByNo[group.Key] = group.First().Status;
        }

        foreach (var row in rows)
        {
            var batchNo = (row.AllocationBatchNo ?? string.Empty).Trim();
            int? status = null;
            if (batchNo.Length > 0 && statusByNo.TryGetValue(batchNo, out var found)) status = found;

            row.AllocationLineage = ContainerExpenseAllocationRules.LineageOf(row);
            row.AllocationLineageText = ContainerExpenseAllocationRules.LineageText(row, status);
        }
    }

    /// <summary>下一个批次号（EAB-yyyyMMdd-序号；按当日已有批次号的最大序号 +1）</summary>
    private static async Task<string> NextBatchNoAsync(IErpDbContext db, DateTime date)
    {
        var prefix = $"{BatchNoPrefix}{date:yyyyMMdd}-";
        var maxNo = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .Where(x => x.BatchNo.StartsWith(prefix))
            .OrderByDescending(x => x.BatchNo)
            .Select(x => x.BatchNo)
            .FirstOrDefaultAsync();

        var seq = 1;
        if (maxNo is not null && int.TryParse(maxNo[prefix.Length..], out var n)) seq = n + 1;
        return $"{prefix}{seq:000}";
    }

    /// <summary>当日费用单序号起点（沿用既有 EXP-yyyyMMdd-序号 口径）</summary>
    private static async Task<int> NextExpenseSeqAsync(IErpDbContext db, DateTime date)
    {
        var prefix = $"{ExpenseNoPrefix}{date:yyyyMMdd}-";
        var maxNo = await db.FinanceExpenses.AsNoTracking()
            .Where(x => x.ExpenseNo.StartsWith(prefix))
            .OrderByDescending(x => x.ExpenseNo)
            .Select(x => x.ExpenseNo)
            .FirstOrDefaultAsync();

        var seq = 1;
        if (maxNo is not null && int.TryParse(maxNo[prefix.Length..], out var n)) seq = n + 1;
        return seq;
    }

    /// <summary>
    /// 是否为唯一索引 / 唯一约束冲突（SQL Server 2601 重复键 / 2627 违反唯一约束）。
    /// 非唯一性冲突（如结构缺失）原样上抛，不被误报成重复。
    /// </summary>
    private static bool LooksLikeUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("UNIQUE", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
