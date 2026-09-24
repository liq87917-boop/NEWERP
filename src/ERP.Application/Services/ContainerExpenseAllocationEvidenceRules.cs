using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 装柜费用分摊证据（ERP-060）的纯规则（无数据库依赖，便于逐条单测）：
/// 缺失 / 未知 / 只读 / 免责文案，批次状态的安全文案，客户与柜号链接的失效判定，
/// 币种分组与「不换算、不合并」口径，未分摊参考说明，结算单金额对照的算术证据口径，
/// 以及工作台筛选取值的显式校验。
/// <para>审计口径：<strong>ERP-042 的分摊批次与分摊行是唯一权威的分摊证据</strong>，本模块只读呈现，
/// 不新建表 / 列、不重算金额、不回填历史、不改派任何记录、不产生记账 / 收付款 / 结算动作。</para>
/// </summary>
public static class ContainerExpenseAllocationEvidenceRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>证据范围：装柜清单（装柜明细入口）</summary>
    public const string ScopeLoadingList = "loading-list";

    /// <summary>证据范围：装柜结算单（结算工作流入口）</summary>
    public const string ScopeSettlement = "settlement";

    /// <summary>未知文案（缺失且无法判定的取值一律显示未知，不推断）</summary>
    public const string UnknownText = "未知";

    /// <summary>无证据文案（未登记任何有效分摊批次；绝不写成 0）</summary>
    public const string MissingEvidenceText = "无（未登记任何有效分摊批次）";

    /// <summary>缺失 / 未知的语义澄清（避免被读成零费用或已结清）</summary>
    public const string NotZeroText =
        "「无 / 未知」只表示没有已登记的分摊证据：不代表费用为零、不代表已结清 / 已结算、"
        + "不代表应收或应付、也不构成任何客户对账或结算结论";

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读视图：不写任何表，不改写装柜清单 / 分摊批次 / 分摊行 / 结算单 / 费用单 / 客户主数据，"
        + "不生成凭证、收款、付款或结算单，也不回填任何历史留痕";

    /// <summary>模块边界文案</summary>
    public const string BoundaryText =
        "证据来源是 ERP-042 已持久化的分摊批次与分摊行（一参与方一行）以及装柜清单 / 柜号等显式链接："
        + "本模块只按显式 Id 与显式字段读取呈现，不新建表 / 列、不重算或改写分摊金额与比例、"
        + "不改写装柜清单与明细、参与方身份、订柜跟踪值、单证、库存、订单、发票、费用与结算记录。";

    /// <summary>免责文案（哪些结论不能从分摊证据推出）</summary>
    public const string DisclaimerText =
        "分摊行是操作性成本分配证据：不是会计记账或凭证、不是付款授权或收款确认、不是税务处理依据、"
        + "不是结算确认或客户对账单，也不代表任何应收 / 应付 / 已收 / 已付结论。";

    /// <summary>币种与金额口径文案</summary>
    public const string BasisAndCurrencyText =
        "金额一律按原币呈现并按币种精度取整（0.5 进位）；折人民币只使用批次留痕中已持久化的来源汇率，"
        + "不同币种之间不合并、不换算，也不做任何跨币种合计。";

    /// <summary>分组口径文案</summary>
    public const string GroupingText =
        "证据按「币种 → 客户」分组：同一币种内同一客户的金额 / 比例可加总；"
        + "不同币种永不合并；不同客户即使名称文本相同也不合并记录（客户一律按持久化 CustomerId 与参与方链接识别）。";

    /// <summary>未分摊参考口径文案</summary>
    public const string UnallocatedRuleText =
        "未分摊参考只由两个显式持久化集合比对得出：本柜柜级来源费用单（归属类型为整柜 / 拼柜 / 散货，"
        + "归属单号等于本柜柜号）与有效分摊批次的来源费用；缺失即按原样列出，不推断是否需要分摊，"
        + "也不把「未分摊」读成任何收付款或结算状态。";

    /// <summary>有界扫描说明（超出上限时界面必须显式提示；常量拼接含整型上限，故为静态只读字段）</summary>
    public static readonly string ScanBoundedText =
        "查询有界：单个装柜清单最多扫描 " + MaxBatchScan + " 个批次与 " + MaxLineScan + " 行分摊明细，"
        + "工作台最多汇总 " + MaxBatchScan + " 个批次的容器；超过上限时截断并在界面显式说明，"
        + "且合计仍取自批次持久化列（不用截断后的行重算权威值）。";

    /// <summary>结算单字段只读口径文案</summary>
    public const string SettlementTotalsText =
        "以上结算单字段（结算总金额 / 海运费 / 其他费用 / 客户）均为本单持久化原值的只读回显："
        + "分摊证据不参与结算金额计算，也不会被写入结算单的任何字段。";

    /// <summary>装柜清单不可用（已删除）文案</summary>
    public const string LoadingListUnavailableText =
        "装柜清单已删除 / 不可用：留痕证据照原值呈现，链接不再校验，也不改派任何记录";

    /// <summary>柜号未登记文案（「未知」为全局统一文案，缺失一律照实显示未知）</summary>
    public const string ContainerNoMissingText =
        UnknownText + "：该装柜清单未登记柜号，无法按柜号比对本柜柜级来源费用（不是零费用，也不推断）";

    /// <summary>柜号上下文文案（有柜号时说明比对依据；未登记柜号时显示「未知」）</summary>
    public static string ContainerContextText(bool containerNoAvailable, string containerNo) =>
        containerNoAvailable
            ? $"柜号 {containerNo}：证据与未分摊参考均按本装柜清单与柜号的显式链接比对（不按柜号文本合并记录）"
            : ContainerNoMissingText;

    /// <summary>多币种时不做合计对照的文案</summary>
    public const string MultiCurrencyNoTotalText =
        "存在多种币种：按口径不合并、不换算，因此不做任何合计对照（逐币种单独呈现）";

    /// <summary>单个装柜清单最多扫描的批次数（有界）</summary>
    public const int MaxBatchScan = 200;

    /// <summary>单个装柜清单最多读取的分摊明细行数（有界；超出显式标注截断）</summary>
    public const int MaxLineScan = 2000;

    /// <summary>未分摊参考最多回显的费用单号条数（有界）</summary>
    public const int MaxUnallocatedExpenseNos = 20;

    /// <summary>批次客户摘要最多回显的客户条数（有界）</summary>
    public const int MaxCustomerSummaryItems = 10;

    /// <summary>历史视图默认条数</summary>
    public const int DefaultHistoryTake = 50;

    /// <summary>历史视图条数上限</summary>
    public const int MaxHistoryTake = 200;

    /// <summary>等值筛选 / 关键字长度上限</summary>
    public const int MaxFilterLength = 100;

    /// <summary>工作台每页条数上限</summary>
    public const int MaxPageSize = 200;

    /// <summary>批次状态过滤（复用 ERP-042 口径：只接受 1 / 0，未知取值一律拒绝）</summary>
    public static int? NormalizeStatusFilter(int? status)
        => ContainerExpenseAllocationRules.NormalizeBatchStatusFilter(status);

    // ==================== 1. 状态与链接判定 ====================

    /// <summary>批次状态为有效（1）</summary>
    public static bool IsActiveStatus(int status) => status == ContainerExpenseAllocationRules.BatchActive;

    /// <summary>批次状态为已作废（0）</summary>
    public static bool IsVoidedStatus(int status) => status == ContainerExpenseAllocationRules.BatchVoided;

    /// <summary>
    /// 批次状态安全文案（读侧专用）：未知状态照实回显（「未知状态（原值 N）」）而不是抛异常，
    /// 保证历史异常数据仍可读，且不被误判为有效。
    /// </summary>
    public static string BatchStatusTextSafe(int status) => status switch
    {
        ContainerExpenseAllocationRules.BatchActive => "有效",
        ContainerExpenseAllocationRules.BatchVoided => "已作废",
        _ => $"未知状态（原值 {status}）"
    };

    /// <summary>币种展示文案（含精度口径）</summary>
    public static string CurrencyLabel(string currency, int precision) =>
        precision == 0 ? $"{currency}（整数币种，0 位小数）" : $"{currency}（{precision} 位小数）";

    /// <summary>客户可用性文案（复用 ERP-041 的显式标注口径，避免停用客户被静默当成有效）</summary>
    public static string CustomerAvailabilityText(bool customerAvailable)
        => ContainerLoadingParticipantRules.AvailabilityText(true, customerAvailable);

    /// <summary>
    /// 分摊行的链接判定（**只判定、不修复**）：参与方是否存在、是否属于本柜、参与方客户与分摊行客户是否一致、
    /// 客户当前是否可用、批次柜号与装柜清单柜号是否一致。返回（是否失效 / 状态码 / 文案）。
    /// </summary>
    public static (bool Invalid, string Status, string Text) EvaluateLineLink(
        bool loadingListAvailable,
        bool containerConsistent,
        bool participantFound,
        bool participantBelongsToScope,
        bool participantCustomerMatches,
        bool customerAvailable)
    {
        if (!loadingListAvailable)
            return (true, "loading-list-unavailable",
                "装柜清单已删除 / 不可用：链接不可校验，历史留痕按原值展示（不改派）");
        if (!containerConsistent)
            return (true, "container-mismatch",
                "柜号不一致（历史异常链接：批次柜号与装柜清单柜号不同）：按留痕原值展示，不按柜号改派");
        if (!participantFound)
            return (true, "participant-missing",
                "参与方引用无效（该参与方 Id 不存在）：按留痕原值展示，不改派");
        if (!participantBelongsToScope)
            return (true, "participant-foreign",
                "参与方不属于本装柜清单（历史异常链接）：按留痕原值展示，不改派");
        if (!participantCustomerMatches)
            return (true, "participant-customer-mismatch",
                "参与方与分摊行客户不一致（历史异常链接）：按留痕原值展示，不改派");
        if (!customerAvailable)
            return (true, "customer-unavailable",
                $"客户已停用 / 已删除 / 不存在：仅历史可读{ContainerLoadingParticipantRules.UnavailableMark}");

        return (false, "ok", "链接有效（参与方 / 客户 / 柜号与留痕一致）");
    }

    /// <summary>批次柜号链接文案（只读校验，不做任何修复）</summary>
    public static string ContainerLinkText(bool loadingListAvailable, bool consistent)
    {
        if (!loadingListAvailable) return "装柜清单已删除 / 不可用：柜号链接不可校验（按留痕原值展示）";
        return consistent
            ? "柜号链接一致（批次柜号与装柜清单柜号相同）"
            : "柜号不一致（历史异常链接：按留痕原值展示，不按柜号改派）";
    }

    // ==================== 2. 证据状态与文案 ====================

    /// <summary>证据状态文案（有效 / 已作废 / 未知状态批次条数）</summary>
    public static string EvidenceStatusText(int activeBatchCount, int voidedBatchCount, int unknownStatusBatchCount)
    {
        if (activeBatchCount > 0)
            return voidedBatchCount + unknownStatusBatchCount == 0
                ? $"已登记 {activeBatchCount} 个有效分摊批次（按币种与客户分组呈现）"
                : $"已登记 {activeBatchCount} 个有效分摊批次"
                  + $"（另有已作废 {voidedBatchCount} 个 / 历史异常状态 {unknownStatusBatchCount} 个，只在历史视图查看）";

        if (voidedBatchCount + unknownStatusBatchCount > 0)
            return "无（未登记有效分摊批次；"
                + $"已作废 {voidedBatchCount} 个 / 历史异常状态 {unknownStatusBatchCount} 个可在历史视图查看）";

        return MissingEvidenceText;
    }

    /// <summary>缺失证据说明（有证据时说明口径，无证据时强调「无」不等于零费用）</summary>
    public static string EvidenceMissingText(int activeBatchCount) => activeBatchCount > 0
        ? "以上金额为 ERP-042 持久化分摊行的原币金额：不是会计记账、付款授权、税务处理或结算确认"
        : $"{MissingEvidenceText}；{NotZeroText}";

    /// <summary>只存在已作废 / 历史异常批次时的补充说明</summary>
    public static string VoidedOnlyText(int voidedBatchCount, int unknownStatusBatchCount) =>
        voidedBatchCount + unknownStatusBatchCount == 0
            ? string.Empty
            : $"已作废 / 历史异常状态批次 {voidedBatchCount + unknownStatusBatchCount} 个不计入有效合计，"
              + "但仍可在历史视图按原值查看；不作废、不删除、不改写";

    /// <summary>未分摊参考说明（按柜号可用性给出「未知」或条数口径）</summary>
    public static string UnallocatedContextText(
        bool containerNoAvailable, int unallocatedCount, int legacyRowCount)
    {
        if (!containerNoAvailable)
            return $"{ContainerNoMissingText}（历史分摊行（无批次留痕）{legacyRowCount} 条不计入未分摊参考）";

        var head = unallocatedCount == 0
            ? "本柜柜级来源费用单当前均属于有效分摊批次（未分摊参考为空——不代表费用为零或已结清）"
            : $"本柜柜级来源费用单中有 {unallocatedCount} 条不属于任何有效分摊批次（按币种单列，仅为未分摊参考）";

        return legacyRowCount == 0
            ? head
            : $"{head}；另有历史分摊行（无批次留痕）{legacyRowCount} 条：不计入分摊合计，也不做任何回填";
    }

    /// <summary>失效链接提示文案</summary>
    public static string InvalidLinkText(int invalidLinkCount) => invalidLinkCount == 0
        ? "所有证据行的参与方 / 客户 / 柜号链接均与留痕一致（无失效链接）"
        : $"存在 {invalidLinkCount} 条失效 / 历史异常链接：按留痕原值展示并逐行标注，"
          + "既不改派也不修复（不按客户名或柜号文本重新匹配）";

    /// <summary>历史分摊行（无批次留痕）说明</summary>
    public static string LegacyAllocationText(int legacyRowCount) => legacyRowCount == 0
        ? "无历史分摊行（无批次留痕）"
        : $"历史分摊行（无批次留痕）{legacyRowCount} 条：费用单自身带分摊基数 / 比例或分摊金额但未记录批次，"
          + "仅作只读辨识，不做任何回填";


    /// <summary>批次合计文案（合计取自批次持久化列）</summary>
    public static string BatchTotalsText(
        string currency, decimal sourceAmount, decimal allocatedTotal, decimal lineCount, bool consistent) =>
        consistent
            ? $"来源金额 {sourceAmount} {currency} = 分摊合计 {allocatedTotal} {currency}（{lineCount} 行；取自批次持久化列）"
            : $"来源金额 {sourceAmount} {currency} / 分摊合计 {allocatedTotal} {currency}（{lineCount} 行；"
              + "批次持久化合计与来源金额不一致——历史异常，照实呈现、不修正）";

    /// <summary>币种分组合计文案（不跨币种相加）</summary>
    public static string GroupTotalsText(
        string currency, decimal allocatedTotal, decimal sourceAmount, int batchCount, int lineCount, bool consistent) =>
        consistent
            ? $"{currency} 有效分摊合计 {allocatedTotal}（{batchCount} 个批次 / {lineCount} 行），来源金额合计 {sourceAmount}"
            : $"{currency} 有效分摊合计 {allocatedTotal} / 来源金额合计 {sourceAmount}"
              + $"（{batchCount} 个批次 / {lineCount} 行）：批次持久化合计与来源金额不一致——历史异常，照实呈现、不修正";

    /// <summary>折人民币口径文案（只使用批次留痕中已持久化的来源汇率）</summary>
    public static string ConversionText(string currency) => currency == "CNY"
        ? "本币种为 CNY：折人民币即原币金额（2 位小数）"
        : "折人民币按各批次留痕的来源汇率折算（2 位小数）：仅在本币种组内加总，不跨币种合并";

    /// <summary>客户摘要文案（有界条数 + 客户总数）</summary>
    public static string CustomerSummaryText(int customerCount, IReadOnlyList<string> items)
    {
        if (customerCount <= 0) return $"无客户分摊行（{MissingEvidenceText}）";
        var head = $"共 {customerCount} 个客户";
        if (items.Count == 0) return head;
        var text = string.Join("；", items);
        return items.Count < customerCount ? $"{head}（列出前 {items.Count} 个）：{text} …" : $"{head}：{text}";
    }

    /// <summary>单个客户在某个币种下的分摊摘要条目（客户显示名 + 金额 + 比例）</summary>
    public static string CustomerSummaryItem(
        string customerDisplay, decimal allocatedAmount, string currency, decimal ratio) =>
        $"{customerDisplay} {AmountText(allocatedAmount, currency)}（{ratio}%）";

    /// <summary>金额展示（原币 + 币种；0 位小数币种不补小数）</summary>
    public static string AmountText(decimal amount, string currency) =>
        ContainerExpenseAllocationRules.PrecisionOf(currency) == 0
            ? $"{amount:0} {currency}"
            : $"{amount:0.00} {currency}";

    /// <summary>
    /// 结算单金额与分摊证据的**算术对照**文案：只能在「存在有效证据且只有一个币种组」时给出减法结果，
    /// 并明确标注为算术证据 —— 不是结算差异、不是应收应付、不是对账或已结算结论。
    /// </summary>
    public static string SettlementComparisonText(
        bool evidenceAvailable,
        string singleCurrency,
        decimal settlementTotalAmount,
        decimal allocatedTotal,
        int groupCount)
    {
        if (!evidenceAvailable || groupCount == 0)
            return $"无有效分摊证据：无法与结算单金额对照（显示「{MissingEvidenceText}」不代表零费用或已结清，也不推断应收应付）";

        if (groupCount > 1 || string.IsNullOrEmpty(singleCurrency))
            return $"{MultiCurrencyNoTotalText}（当前 {groupCount} 个币种组）";

        var currency = singleCurrency;
        var difference = ContainerExpenseAllocationRules.RoundAmount(
            settlementTotalAmount - allocatedTotal, currency);

        return $"算术对照（仅算术证据，币种 {currency}）：结算单总金额 {settlementTotalAmount} − 有效分摊合计 {allocatedTotal} "
            + $"= {difference}。结算单未持久化币种列，该差额不构成结算差异、应收 / 应付、对账或已结算结论，"
            + "也与是否已收付款无关。";
    }

    // ==================== 3. 筛选与取值校验 ====================

    /// <summary>范围 Id 校验（必须为正整数）</summary>
    public static long EnsureScopeId(long id, string fieldLabel)
    {
        if (id <= 0) throw BusinessException.InvalidParameter($"请选择{fieldLabel}");
        return id;
    }

    /// <summary>
    /// 显式等值筛选规范化：去首尾空白 + 统一大写（与 ERP-042 的柜号比对口径一致），超过长度上限一律拒绝，
    /// 空值返回 <c>null</c>（= 不过滤）。不做相似度 / 前缀匹配，也不跨客户合并。
    /// </summary>
    public static string? NormalizeEqualsFilter(string? value, string fieldLabel)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxFilterLength)
            throw BusinessException.InvalidParameter($"{fieldLabel}长度不能超过 {MaxFilterLength} 个字符");
        return text.ToUpperInvariant();
    }

    /// <summary>关键字规范化：去首尾空白 + 长度上限（只用于显式列的关键字命中，不做跨客户合并）</summary>
    public static string? NormalizeKeyword(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxFilterLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxFilterLength} 个字符");
        return text;
    }

    /// <summary>币种筛选规范化（空值 = 不过滤；未知币种仍按大写币种码等值匹配，不做汇率换算）</summary>
    public static string? NormalizeCurrencyFilter(string? currency)
    {
        var text = (currency ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxFilterLength)
            throw BusinessException.InvalidParameter($"币种长度不能超过 {MaxFilterLength} 个字符");
        return ContainerExpenseAllocationRules.NormalizeCurrency(text);
    }

    /// <summary>客户 Id 筛选校验（显式正整数 Id）</summary>
    public static long? NormalizeCustomerIdFilter(long? customerId)
    {
        if (customerId is null) return null;
        if (customerId <= 0)
            throw BusinessException.InvalidParameter("客户 Id 必须为正整数（按客户筛选时不接受 0 或负数）");
        return customerId;
    }

    /// <summary>历史视图条数规范化（1 ≤ take ≤ <see cref="MaxHistoryTake"/>）</summary>
    public static int NormalizeHistoryTake(int take)
    {
        if (take < 1 || take > MaxHistoryTake)
            throw BusinessException.InvalidParameter(
                $"历史视图条数必须在 1 ~ {MaxHistoryTake} 之间（收到 {take}）");
        return take;
    }
}
