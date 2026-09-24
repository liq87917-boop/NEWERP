using ERP.Application.Common;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 装柜费用分摊批次的纯规则（ERP-042，无数据库依赖，便于逐条单测）：
/// 分摊方法与整柜法口径、币种精度与四舍五入、余差归属的稳定规则、基准值缺失 / 为负 / 合计为 0 的拒绝口径、
/// 来源费用可分摊资格判定，以及「批次留痕 / 历史分摊 / 未分摊」的留痕分类与文案。
/// <para>边界：本规则只做**计算与校验**，不写库、不改写来源费用单、装柜清单与明细，也不产生任何
/// 收付款 / 结算 / 记账动作；分摊结果始终落在既有 <see cref="FinanceExpense"/> 行上。</para>
/// </summary>
public static class ContainerExpenseAllocationRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>按体积分摊（m³）</summary>
    public const string MethodByVolume = "按体积";

    /// <summary>按重量分摊（kg）</summary>
    public const string MethodByWeight = "按重量";

    /// <summary>按箱数分摊</summary>
    public const string MethodByCarton = "按箱数";

    /// <summary>按金额分摊</summary>
    public const string MethodByAmount = "按金额";

    /// <summary>整柜法：全额归**显式指定的唯一参与方**（未指定时要求该柜恰好一条启用参与方），比例固定 100%</summary>
    public const string MethodWholeContainer = "整柜";

    /// <summary>支持的分摊方法（超出范围一律拒绝，不做隐式兜底）</summary>
    public static readonly string[] SupportedMethods =
        { MethodByVolume, MethodByWeight, MethodByCarton, MethodByAmount, MethodWholeContainer };

    /// <summary>整柜法的持久化基数种类：装柜清单总箱数</summary>
    public const string WholeContainerBasisCartons = "箱数";

    /// <summary>整柜法的持久化基数种类：装柜清单总毛重（kg）</summary>
    public const string WholeContainerBasisWeight = "毛重";

    /// <summary>整柜法的持久化基数种类：装柜清单总体积（m³）</summary>
    public const string WholeContainerBasisVolume = "体积";

    /// <summary>整柜法允许的持久化基数种类</summary>
    public static readonly string[] WholeContainerBasisKinds =
        { WholeContainerBasisCartons, WholeContainerBasisWeight, WholeContainerBasisVolume };

    /// <summary>基数来源：持久化装柜证据（整柜法；缺失或为 0 一律拒绝）</summary>
    public const string BasisSourcePersisted = "持久化装柜证据";

    /// <summary>基数来源：用户确认的请求值（服务端校验非负且合计大于 0）</summary>
    public const string BasisSourceRequest = "用户确认请求值";

    /// <summary>批次状态：有效</summary>
    public const int BatchActive = 1;

    /// <summary>批次状态：已作废（保留历史，不物理删除）</summary>
    public const int BatchVoided = 0;

    /// <summary>留痕分类：由分摊批次生成（可回溯批次与来源费用）</summary>
    public const string LineageBatch = "Batch";

    /// <summary>留痕分类：历史分摊行（无批次留痕；读取可辨识，不做任何自动回填）</summary>
    public const string LineageLegacy = "Legacy";

    /// <summary>留痕分类：未分摊</summary>
    public const string LineageNone = "None";

    /// <summary>可作为分摊来源的「柜级」归属类型</summary>
    public static readonly string[] ContainerRefTypes = { "整柜", "拼柜", "散货" };

    /// <summary>单次分摊允许的参与方行数上限（与装柜清单参与方上限一致，保证视图有界）</summary>
    public const int MaxLinesPerBatch = 100;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>比例小数位（4 位）</summary>
    public const int RatioDecimals = 4;

    /// <summary>折人民币固定 2 位小数（与既有 <see cref="FinanceExpense.AmountCny"/> 口径一致）</summary>
    public const int CnyDecimals = 2;

    /// <summary>余差归属规则文案（预览与文档共用，避免界面与后端口径不一致）</summary>
    public const string RemainderRuleText =
        "分摊金额按币种精度四舍五入（0.5 进位）；四舍五入产生的余差归「基准值最大」的参与方，"
        + "基准值并列时归参与方 Id 较小者（与列表顺序无关），因此各行合计恒等于来源费用金额。";

    /// <summary>整柜法口径文案</summary>
    public const string WholeContainerRuleText =
        "整柜法：把来源费用全额（100%）归显式指定的唯一参与方，基数为该装柜清单**持久化**的总箱数 / 总毛重 / 总体积"
        + "（请求需指定其中一种，缺失或为 0 一律拒绝，不按经验推断）。";

    /// <summary>历史分摊行文案（无批次留痕，保持可读、不自动改写）</summary>
    public const string LegacyLineageText =
        "历史分摊（无批次留痕：费用单自身带分摊基数 / 比例或分摊金额，但未记录批次与来源费用；仅作只读辨识，不做任何回填）";

    /// <summary>未分摊文案</summary>
    public const string NotAllocatedText = "未分摊（柜级 / 客户级费用行，未参与任何分摊批次）";

    /// <summary>模块边界文案（接口与界面统一声明）</summary>
    public const string BoundaryText =
        "分摊只新增既有费用单行并记录批次 / 来源留痕：不改写来源费用单金额与归属、不改写装柜清单与明细数量 / 箱数 / 重量 / 体积、"
        + "不改写参与方身份、订柜外贸与物流跟踪值、单证、库存与库存流水、采购订单与销售订单，也不记账、不生成收款 / 付款 / 结算单。";

    /// <summary>备注规范化：去首尾空白并校验长度（超长直接拒绝，不静默截断）</summary>
    public static string NormalizeRemark(string? remark)
    {
        var value = (remark ?? string.Empty).Trim();
        if (value.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");
        return value;
    }

    // ==================== 1. 币种精度与取整 ====================

    /// <summary>币种规范化（去空白并大写；空值按 CNY 处理）</summary>
    public static string NormalizeCurrency(string? currency)
    {
        var value = (currency ?? string.Empty).Trim().ToUpperInvariant();
        return value.Length == 0 ? "CNY" : value;
    }

    /// <summary>
    /// 币种金额小数位：JPY / KRW / VND / IDR 等无小数币种为 0 位，其余（含未知币种）按 2 位处理。
    /// </summary>
    public static int PrecisionOf(string? currency) => NormalizeCurrency(currency) switch
    {
        "JPY" or "KRW" or "VND" or "IDR" => 0,
        _ => 2
    };

    /// <summary>按币种精度四舍五入（0.5 进位，确定性；不使用银行家舍入）</summary>
    public static decimal RoundAmount(decimal amount, string? currency)
        => Math.Round(amount, PrecisionOf(currency), MidpointRounding.AwayFromZero);

    /// <summary>比例取整：固定 4 位小数，0.5 进位</summary>
    public static decimal RoundRatio(decimal ratio) => Math.Round(ratio, RatioDecimals, MidpointRounding.AwayFromZero);

    /// <summary>折人民币换算：按汇率换算后固定 2 位小数</summary>
    public static decimal ConvertToCny(decimal amount, string? currency, decimal exchangeRate)
    {
        var rate = exchangeRate <= 0 ? 1m : exchangeRate;
        return NormalizeCurrency(currency) == "CNY"
            ? RoundAmount(amount, "CNY")
            : Math.Round(amount * rate, CnyDecimals, MidpointRounding.AwayFromZero);
    }

    // ==================== 2. 方法与基数口径 ====================

    /// <summary>是否为受支持的分摊方法</summary>
    public static bool IsSupportedMethod(string? method) =>
        SupportedMethods.Contains((method ?? string.Empty).Trim(), StringComparer.Ordinal);

    /// <summary>分摊方法规范化（必须显式且受支持；空值或未知方法一律拒绝，不做隐式兜底）</summary>
    public static string NormalizeMethod(string? method)
    {
        var value = (method ?? string.Empty).Trim();
        if (!IsSupportedMethod(value))
            throw BusinessException.InvalidParameter(
                $"分摊方法「{value}」不受支持：只允许 {string.Join(" / ", SupportedMethods)}");
        return value;
    }

    /// <summary>
    /// 是否为整柜法。
    /// <para>注意基数种类口径：逐行基准法（按体积 / 按重量 / 按箱数 / 按金额）的基数种类与方法同名；
    /// <b>整柜法</b>的比例恒为 100%，其基数种类是请求显式指定的**持久化装柜证据**
    /// （<see cref="WholeContainerBasisCartons"/> / <see cref="WholeContainerBasisWeight"/> /
    /// <see cref="WholeContainerBasisVolume"/>），因此留痕中的 BasisKind 为选定的持久化基数种类，
    /// 而 AllocationMethod 才记录「整柜」。</para>
    /// </summary>
    public static bool IsWholeContainer(string? method) =>
        string.Equals((method ?? string.Empty).Trim(), MethodWholeContainer, StringComparison.Ordinal);

    /// <summary>是否为柜级归属类型（只有柜级费用单才能作为分摊来源）</summary>
    public static bool IsContainerLevelRefType(string? refType) =>
        ContainerRefTypes.Contains((refType ?? string.Empty).Trim(), StringComparer.Ordinal);

    /// <summary>整柜法基数种类规范化（只接受 箱数 / 毛重 / 体积）</summary>
    public static string NormalizeWholeContainerBasisKind(string? basisKind)
    {
        var value = (basisKind ?? string.Empty).Trim();
        if (!WholeContainerBasisKinds.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"整柜法的基数种类「{value}」不受支持：只允许 {string.Join(" / ", WholeContainerBasisKinds)}"
                + "（对应装柜清单持久化的总箱数 / 总毛重 / 总体积）");
        return value;
    }

    /// <summary>整柜法持久化基数种类的中文量纲说明（界面与留痕使用）</summary>
    public static string WholeContainerBasisText(string basisKind) => NormalizeWholeContainerBasisKind(basisKind) switch
    {
        WholeContainerBasisWeight => "总毛重（kg）",
        WholeContainerBasisVolume => "总体积（m³）",
        _ => "总箱数"
    };

    /// <summary>按整柜法基数种类读取装柜清单**持久化**总量（为 0 时由调用方拒绝，不按经验推断）</summary>
    public static decimal PersistedBasisValue(ContainerLoadingList loadingList, string basisKind)
    {
        ArgumentNullException.ThrowIfNull(loadingList);
        return NormalizeWholeContainerBasisKind(basisKind) switch
        {
            WholeContainerBasisWeight => loadingList.TotalWeight,
            WholeContainerBasisVolume => loadingList.TotalVolume,
            _ => loadingList.TotalCartons
        };
    }

    /// <summary>来源费用基准证据文案（说明每行基数从哪来：持久化或用户确认）</summary>
    public static string BasisEvidenceText(string method, string basisKind) =>
        IsWholeContainer(method)
            ? $"来源：{BasisSourcePersisted}（装柜清单持久化{WholeContainerBasisText(basisKind)}）"
            : $"来源：{BasisSourceRequest}（服务端校验非负且合计大于 0）";

    // ==================== 3. 分摊计算（含余差稳定归属） ====================

    /// <summary>参与方基准值输入（服务端已按范围与显式性校验）</summary>
    public sealed record AllocationBasis(long ParticipantId, decimal BasisValue);

    /// <summary>参与方分摊结果（比例与金额均为服务端权威计算结果）</summary>
    public sealed record AllocationShare(
        long ParticipantId, decimal BasisValue, decimal Ratio, decimal AllocatedAmount, bool RemainderCarrier);

    /// <summary>
    /// 按基准值分摊总额：比例 4 位小数，金额按币种精度取整，余差归基准值最大的参与方（并列取参与方 Id 较小者）。
    /// <para>拒绝口径（一律抛业务异常，绝不猜测）：基准为空、超过行数上限、参与方重复、基准值为负、
    /// 基准值合计为 0、来源金额不为正、取整后余差无法落在承接行。</para>
    /// </summary>
    public static List<AllocationShare> Distribute(
        decimal totalAmount, string? currency, IReadOnlyList<AllocationBasis> basis)
    {
        ArgumentNullException.ThrowIfNull(basis);

        if (basis.Count == 0)
            throw BusinessException.InvalidParameter("请至少填写一个参与方的分摊基准值");
        if (basis.Count > MaxLinesPerBatch)
            throw BusinessException.InvalidParameter(
                $"单次分摊的参与方行数不能超过 {MaxLinesPerBatch} 条（收到 {basis.Count} 条）");
        if (totalAmount <= 0)
            throw BusinessException.InvalidParameter($"来源费用金额必须大于 0（收到 {totalAmount}），不能分摊");

        var duplicated = basis.GroupBy(b => b.ParticipantId).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToList();
        if (duplicated.Count > 0)
            throw BusinessException.Duplicate(
                $"同一参与方重复出现：{string.Join("、", duplicated)}（请合并为一行）");

        var negative = basis.Where(b => b.BasisValue < 0).Select(b => b.ParticipantId).ToList();
        if (negative.Count > 0)
            throw BusinessException.InvalidParameter(
                $"参与方 {string.Join("、", negative)} 的基准值为负数：基准值必须为非负数");

        var totalBasis = basis.Sum(b => b.BasisValue);
        if (totalBasis <= 0)
            throw BusinessException.InvalidParameter(
                "参与方基准值合计为 0：缺少可用的分摊依据，系统不会按经验推断"
                + "（请显式填写体积 / 重量 / 箱数 / 金额，或改用「整柜」法并指定持久化基数）");

        // 余差承接行：基准值最大；并列时取参与方 Id 较小者 —— 稳定且与列表顺序无关
        var carrierId = basis
            .OrderByDescending(b => b.BasisValue)
            .ThenBy(b => b.ParticipantId)
            .First().ParticipantId;

        var shares = new List<AllocationShare>(basis.Count);
        decimal assigned = 0;
        foreach (var item in basis.OrderBy(b => b.ParticipantId))
        {
            if (item.ParticipantId == carrierId) continue;
            var amount = RoundAmount(totalAmount * item.BasisValue / totalBasis, currency);
            assigned += amount;
            shares.Add(new AllocationShare(
                item.ParticipantId, item.BasisValue,
                RoundRatio(item.BasisValue / totalBasis * 100m), amount, false));
        }

        var carrier = basis.Single(b => b.ParticipantId == carrierId);
        var carrierAmount = totalAmount - assigned;      // 余差全部落在承接行，行合计恒等于来源金额
        if (carrierAmount < 0)
            throw BusinessException.InvalidParameter(
                $"币种「{NormalizeCurrency(currency)}」的金额精度无法让各行合计与来源金额一致，请调整基准值或改用其他币种精度口径");

        shares.Add(new AllocationShare(
            carrierId, carrier.BasisValue,
            RoundRatio(carrier.BasisValue / totalBasis * 100m), carrierAmount, true));

        return shares.OrderBy(s => s.ParticipantId).ToList();
    }

    // ==================== 4. 来源费用可分摊资格 ====================

    /// <summary>历史分摊痕迹判定用的分摊基数取值（含旧手工分摊，与既有模块下拉一致）</summary>
    private static readonly string[] EvidenceBasisValues =
        { MethodByVolume, MethodByWeight, MethodByCarton, MethodByAmount, "手工分摊" };

    /// <summary>该费用单是否带历史分摊痕迹（旧「拼柜分摊」接口生成的行：无批次留痕，不可再次作为分摊来源）</summary>
    public static bool HasLegacyAllocationEvidence(FinanceExpense expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        var basis = (expense.AllocationBase ?? string.Empty).Trim();
        return EvidenceBasisValues.Contains(basis, StringComparer.Ordinal)
            && (expense.AllocationRatio > 0 || expense.AllocatedAmount > 0);
    }

    /// <summary>
    /// 校验来源费用可作为分摊来源（全部依据**持久化字段**，不做经验推断）：
    /// 存在且未删除、归属类型为柜级、归属单号与装柜清单柜号一致、金额大于 0、
    /// 未属于任何分摊批次、且本身不是历史分摊生成的明细行。
    /// </summary>
    public static void EnsureEligibleSource(FinanceExpense? expense, ContainerLoadingList loadingList)
    {
        ArgumentNullException.ThrowIfNull(loadingList);

        if (expense is null || expense.IsDeleted)
            throw BusinessException.NotFound("来源费用单不存在或已删除，不能作为分摊来源");

        if (!IsContainerLevelRefType(expense.RefType))
            throw BusinessException.InvalidParameter(
                $"费用单「{expense.ExpenseNo}」的归属类型为「{expense.RefType}」，不是柜级费用"
                + $"（只允许 {string.Join(" / ", ContainerRefTypes)} 作为分摊来源）");

        var refNo = (expense.RefNo ?? string.Empty).Trim();
        var containerNo = (loadingList.ContainerNo ?? string.Empty).Trim();
        if (refNo.Length == 0 || containerNo.Length == 0
            || !string.Equals(refNo, containerNo, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.RuleConflict(
                $"费用单「{expense.ExpenseNo}」的归属单号「{expense.RefNo}」与装柜清单「{loadingList.LoadingListNo}」"
                + $"的柜号「{loadingList.ContainerNo}」不一致，不能按该柜分摊");

        if (expense.Amount <= 0)
            throw BusinessException.InvalidParameter(
                $"费用单「{expense.ExpenseNo}」的金额必须大于 0（当前 {expense.Amount}），不能分摊");

        var batchNo = (expense.AllocationBatchNo ?? string.Empty).Trim();
        if (batchNo.Length > 0)
            throw BusinessException.RuleConflict(
                $"费用单「{expense.ExpenseNo}」已属于分摊批次「{batchNo}」，"
                + "不能重复作为分摊来源（如需更正请先作废原批次）");

        if (HasLegacyAllocationEvidence(expense))
            throw BusinessException.RuleConflict(
                $"费用单「{expense.ExpenseNo}」本身是历史分摊生成的明细行（分摊基数 {expense.AllocationBase} / "
                + $"比例 {expense.AllocationRatio} / 分摊金额 {expense.AllocatedAmount}），不能再作为分摊来源；"
                + "请选择该柜的柜级总额费用单");
    }

    /// <summary>来源费用资格判定（**不抛异常**）：返回「是否可分摊」与原因文案，供上下文面板逐行说明</summary>
    public static (bool Eligible, string Text) EvaluateEligibility(
        FinanceExpense? expense, ContainerLoadingList? loadingList)
    {
        if (expense is null) return (false, "来源费用单不存在或已删除，不能作为分摊来源");
        if (loadingList is null) return (false, "未知：未加载装柜清单，无法判定柜号一致性");

        try
        {
            EnsureEligibleSource(expense, loadingList);
            return (true, "可作为分摊来源");
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>来源费用资格文案（来源费用面板逐行说明，便于用户判断为何不可分摊；不抛异常）</summary>
    public static string EligibilityText(FinanceExpense expense, ContainerLoadingList? loadingList) =>
        EvaluateEligibility(expense, loadingList).Text;

    // ==================== 5. 批次状态与留痕分类 ====================

    /// <summary>批次状态文案</summary>
    public static string BatchStatusText(int status) => status switch
    {
        BatchActive => "有效",
        BatchVoided => "已作废",
        _ => throw BusinessException.InvalidParameter($"批次状态只能是 {BatchActive}（有效）或 {BatchVoided}（已作废），收到 {status}")
    };

    /// <summary>批次状态过滤规范化（为空 = 不过滤）</summary>
    public static int? NormalizeBatchStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = BatchStatusText(status.Value);
        return status;
    }

    /// <summary>费用单留痕分类（批次留痕 / 历史分摊（无批次留痕） / 未分摊）</summary>
    public static string LineageOf(FinanceExpense expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        if (!string.IsNullOrWhiteSpace(expense.AllocationBatchNo)) return LineageBatch;
        return HasLegacyAllocationEvidence(expense) ? LineageLegacy : LineageNone;
    }

    /// <summary>
    /// 费用单留痕文案（读取侧显式标注；<paramref name="batchStatus"/> 由服务端按批次一次批量解析，
    /// 为空表示批次状态未知——此时照实说明，不假定为有效）。
    /// </summary>
    public static string LineageText(FinanceExpense expense, int? batchStatus = null)
    {
        switch (LineageOf(expense))
        {
            case LineageBatch:
                var status = batchStatus is null
                    ? "（批次状态未知）"
                    : batchStatus == BatchActive ? "（有效批次）" : "（已作废批次）";
                var source = string.IsNullOrWhiteSpace(expense.AllocationSourceExpenseNo)
                    ? string.Empty
                    : $"，来源费用 {expense.AllocationSourceExpenseNo.Trim()}";
                return $"批次留痕：{expense.AllocationBatchNo?.Trim()}{status}{source}";
            case LineageLegacy:
                return LegacyLineageText;
            default:
                return NotAllocatedText;
        }
    }
}
