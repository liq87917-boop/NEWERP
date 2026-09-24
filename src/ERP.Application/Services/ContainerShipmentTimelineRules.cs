using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运证据时间线与跟踪工作台的纯规则（ERP-059，无数据库依赖，便于逐条单测）：
/// 计划时间（ETD / ETA，来自 ERP-057 出运引用）与实际事件（实际开船 / 到港 / 查验 / 放行，来自 ERP-058 里程碑证据）
/// 的**分开标注**口径、缺失事件的「无 / 未知」文案、时间差算术证据口径（仅在两条持久化时间戳同时存在时给出），
/// 以及工作台筛选参数与边界文案。
/// <para>审计口径（本模块存在的前提）：ERP-057 已建立唯一权威的出运引用登记册、ERP-058 已在其下建立只追加的
/// 里程碑证据，因此本模块**只读呈现**：不新建任何表、不新增任何列，也不按柜号 / S/O / B/L 等自由文本
/// 匹配或合并记录。</para>
/// <para>边界：规则只判断「呈现口径是否成立」，不做任何推断与派生 —— 不把计划时间当作实际事件，
/// 不把缺失事件推断为已开船 / 已到港 / 已清关 / 延误 / 逾期，也不做任何状态机判定。</para>
/// </summary>
public static class ContainerShipmentTimelineRules
{
    // ==================== 1. 时间线条目种类 ====================

    /// <summary>时间线条目：计划开船（ETD，来自出运引用计划值）</summary>
    public const string KindPlannedDeparture = "planned-departure";

    /// <summary>时间线条目：计划到港（ETA，来自出运引用计划值）</summary>
    public const string KindPlannedArrival = "planned-arrival";

    /// <summary>时间线条目：实际开船 / 离港（来自 ERP-058 里程碑证据）</summary>
    public const string KindActualDeparture = "actual-departure";

    /// <summary>时间线条目：实际到港 / 抵达（来自 ERP-058 里程碑证据）</summary>
    public const string KindActualArrival = "actual-arrival";

    /// <summary>时间差对比项：开船（计划开船 vs 实际开船）</summary>
    public const string VarianceKindDeparture = "departure";

    /// <summary>时间差对比项：到港（计划到港 vs 实际到港）</summary>
    public const string VarianceKindArrival = "arrival";

    // ==================== 2. 有界常量 ====================

    /// <summary>筛选文本长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxFilterTextLength = 100;

    /// <summary>关键字长度上限</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>单条出运引用一次返回的**有效**时间线条目上限（有界；超出时显式说明被截断）</summary>
    public const int MaxActiveEvents = 200;

    /// <summary>单条出运引用一次返回的**已作废历史**条目上限（有界；超出时显式说明被截断）</summary>
    public const int MaxHistoryEvents = 200;

    /// <summary>计划 / 实际时间戳允许的最早取值（有界：与 ERP-057 / ERP-058 同一口径）</summary>
    public static readonly DateTime EarliestTimestamp = new(2000, 1, 1);

    /// <summary>计划 / 实际时间戳允许的最晚取值（不与上界同日，便于比较）</summary>
    public static readonly DateTime LatestTimestamp = new(2100, 1, 1);

    // ==================== 3. 文案（接口、界面与文档同源） ====================

    /// <summary>未知 / 未填写文案（未知不回落为 0、空或今天；与 ERP-040 / ERP-057 / ERP-058 同一口径）</summary>
    public const string UnknownText = ContainerShipmentTrackingRules.UnknownText;

    /// <summary>缺失事件文案（未登记的实际事件一律显示「无」，绝不推断为已发生）</summary>
    public const string MissingEventText = "无（未登记）";

    /// <summary>计划与实际分开标注的口径文案</summary>
    public const string PlannedVersusActualText =
        "计划时间（ETD / ETA）来自 ERP-057 出运引用登记册的计划值，实际事件（实际开船 / 实际到港 / 查验 / 放行）"
        + "来自 ERP-058 用户录入的里程碑证据：两者分开标注、各自带来源标注，不会互相替代；"
        + "未登记的实际事件一律显示「" + MissingEventText + "」，未填写的计划时间显示「" + UnknownText + "」。";

    /// <summary>不推断任何业务状态的口径文案（缺失事件绝不变成已开船 / 已到港 / 已清关 / 延误 / 逾期）</summary>
    public const string NoStatusInferenceText =
        "本时间线与跟踪工作台不产生任何业务状态：不因缺失事件、计划时间已过、单据状态或自由文本推断出"
        + "「已开船 / 已到港 / 已清关 / 已放行 / 延误 / 逾期」这类结论，也不推进装柜、报关、出运或任何业务单据的状态。";

    /// <summary>证据语义文案（仓库操作性证据，不是权威结论）</summary>
    public const string EvidenceText =
        "时间线上的实际事件都是用户录入的操作性证据：查验 / 放行不是海关决定、不是查验或放行结论，"
        + "也不代表允许出运；开船 / 到港不是承运人确认或航行回执，不构成清关许可、交付承诺或法律依据。";

    /// <summary>时间差口径文案（算术证据，不是 SLA 结论，也不做时区换算）</summary>
    public const string VarianceBasisText =
        "时间差是两条**已持久化**时间戳的**算术证据**：不换算时区、不补默认时区，也不解释为承运人 / 海关确认、"
        + "合同 SLA、延误责任或索赔依据；任一侧缺失或两侧日期时间语义（DateTimeKind）不一致时一律显示"
        + "「无法比较」，系统不推断缺失的一侧。";

    /// <summary>记录关联口径文案（显式 Id 关联，绝不按文本匹配或合并记录）</summary>
    public const string SourceLinkText =
        "记录只按显式的源记录类型 + Id（以及出运引用 Id、里程碑 Id）关联：系统不会按柜号、S/O、B/L、"
        + "承运人或任何自由文本相似度挑选或合并记录，也不会把不同客户 / 不同柜的记录并成一条。";

    /// <summary>模块边界文案（只读呈现，不写任何表）</summary>
    public const string BoundaryText =
        "本工作台是**只读**视图：只读取出运引用与里程碑证据并原样呈现，不写任何表、不改写订柜信息 / 预装柜单 / "
        + "装柜清单 / 出运引用 / 里程碑的任何列、状态与工作流，也不改写销售订单、采购订单、库存与库存成本、库存流水、"
        + "单证中心、发票、费用与分摊、收付款、税务、结算与海关记录，更不轮询承运人、海关、货代或任何外部系统。";

    /// <summary>规则文案（工作台筛选口径）</summary>
    public const string RuleText =
        "工作台口径：只按显式字段筛选（源记录类型 / Id、柜号、单号、B/L、S/O、起运·中转·目的港、计划开船·到港时间、"
        + "记录事件类型与事件时间），分页返回且有界；未知筛选取值（源记录类型 / 事件类型 / 状态 / 时间区间颠倒）一律拒绝，"
        + "不静默忽略筛选条件，也不做跨记录文本推断。";

    /// <summary>历史视图文案（已作废里程碑从有效时间线排除但保留可读）</summary>
    public const string HistoryText =
        "已作废的里程碑证据从**有效时间线**中排除，但按原值保留在历史视图中（含作废时间与原因）："
        + "作废不是删除，历史条目也不会被「修好」或改派到别的记录。";

    // ==================== 4. 条目 / 时间戳文案 ====================

    /// <summary>条目种类文案（计划 / 实际分开标注；未知种类照实回显，绝不假定为实际事件）</summary>
    public static string KindText(string? kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        KindPlannedDeparture => "计划开船（ETD，出运引用计划值）",
        KindPlannedArrival => "计划到港（ETA，出运引用计划值）",
        KindActualDeparture => "实际开船 / 离港（里程碑证据）",
        KindActualArrival => "实际到港 / 抵达（里程碑证据）",
        ContainerShipmentMilestoneRules.EventTypeInspection => "查验（里程碑证据）",
        ContainerShipmentMilestoneRules.EventTypeCustomsRelease => "放行（里程碑证据）",
        _ => $"未知（{kind}）"
    };

    /// <summary>实际事件条目的种类（沿用 ERP-058 事件类型取值；未知历史取值照实保留，不映射成已知类型）</summary>
    public static string KindActual(string? eventType) =>
        (eventType ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>条目是否来自计划值（ERP-057 出运引用）</summary>
    public static bool IsPlannedKind(string? kind) =>
        (kind ?? string.Empty).Trim().ToLowerInvariant() is KindPlannedDeparture or KindPlannedArrival;

    /// <summary>条目是否来自实际事件（ERP-058 里程碑证据）</summary>
    public static bool IsActualKind(string? kind) => !IsPlannedKind(kind);

    /// <summary>时间戳展示文案（<c>null</c> = 未登记 / 未知：按条目种类给出「无」或「未知」，不留空白也不回落为今天）</summary>
    public static string TimestampText(DateTime? value, bool isPlanned) => value is null
        ? (isPlanned ? "未知（未登记该计划时间）" : MissingEventText)
        : value.Value.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// 时间戳日期时间语义文案（保留录入时的时间语义，不做时区换算）：
    /// 数据库读回的值通常为 <see cref="DateTimeKind.Unspecified"/>（按录入原值），这里照实说明。
    /// </summary>
    public static string TimestampKindText(DateTime? value) => value?.Kind switch
    {
        DateTimeKind.Utc => "UTC",
        DateTimeKind.Local => "本地时间（Local）",
        DateTimeKind.Unspecified => "未指定时区（Unspecified，按录入原值）",
        _ => "未登记"
    };

    // ==================== 5. 时间差（算术证据，仅在可比较时给出） ====================

    /// <summary>
    /// 时间差算术证据：只有在**两条持久化时间戳同时存在**且日期时间语义（<see cref="DateTime.Kind"/>）一致时
    /// 才给出差值；任一侧缺失或语义不一致时显式说明「无法比较」，绝不推断缺失的一侧，
    /// 也不把差值解释为延误 / 逾期 / SLA 结论。
    /// </summary>
    /// <param name="label">对比项名称（如「开船时间差」）</param>
    /// <param name="planned">计划时间戳（来自出运引用；<c>null</c> = 未知）</param>
    /// <param name="actual">实际事件时间戳（来自里程碑证据；<c>null</c> = 无）</param>
    public static (bool Comparable, string Text) DescribeVariance(
        string label, DateTime? planned, DateTime? actual)
    {
        if (planned is null && actual is null)
            return (false, $"无法比较：计划时间与实际事件都没有登记"
                           + $"（计划显示「{UnknownText}」、实际事件显示「{MissingEventText}」），系统不推断任何一侧");

        if (planned is null)
            return (false, $"无法比较：缺少计划时间（实际事件已登记 {actual!.Value:yyyy-MM-dd HH:mm}）；"
                           + "系统不会按单据状态、柜号或任何自由文本推断计划时间");

        if (actual is null)
            return (false, $"无法比较：缺少实际事件（计划时间已登记 {planned.Value:yyyy-MM-dd HH:mm}）；"
                           + $"未登记的实际事件显示「{MissingEventText}」，系统不会推断为已发生");

        if (planned.Value.Kind != actual.Value.Kind)
            return (false, $"无法比较：两侧日期时间语义不一致"
                           + $"（计划 {TimestampKindText(planned)}、实际 {TimestampKindText(actual)}）；"
                           + "系统不做时区换算，也不假定两侧可比");

        var delta = actual.Value - planned.Value;
        if (delta == TimeSpan.Zero)
            return (true, $"{label}：实际与计划一致（{planned.Value:yyyy-MM-dd HH:mm}）；算术证据，不是延误 / 逾期结论");

        var magnitude = delta.Duration();
        var direction = delta > TimeSpan.Zero ? "晚" : "早";
        return (true, $"{label}：实际比计划{direction} {magnitude.Days} 天 {magnitude.Hours} 小时 {magnitude.Minutes} 分钟"
                      + "（算术证据，不是延误 / 逾期结论）");
    }

    // ==================== 6. 规范化与校验 ====================

    /// <summary>文本规范化（去首尾空白；空 = 未填写 / 不过滤；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeFilterText(string? raw, string fieldLabel)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(raw);
        if (value.Length > MaxFilterTextLength)
            throw BusinessException.InvalidParameter(
                $"{fieldLabel}最长 {MaxFilterTextLength} 个字符，当前 {value.Length} 个字符");
        return value;
    }

    /// <summary>显式等值筛选值（去首尾空白 + 统一大写；留空 = 不过滤；不做任何相似度匹配）</summary>
    public static string? NormalizeExactFilter(string? raw, string fieldLabel)
    {
        var value = NormalizeFilterText(raw, fieldLabel);
        return value.Length == 0 ? null : value.ToUpperInvariant();
    }

    /// <summary>关键字规范化（去首尾空白 + 长度校验；留空 = 不过滤）</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(keyword);
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    /// <summary>源记录类型筛选规范化（留空 = 不过滤；其他取值一律拒绝，不静默忽略筛选条件）</summary>
    public static string? NormalizeSourceTypeFilter(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : ContainerShipmentReferenceRules.NormalizeSourceType(raw);

    /// <summary>引用状态筛选规范化（留空 = 不过滤；其他取值一律拒绝）</summary>
    public static int? NormalizeStatusFilter(int? status) =>
        status is null ? null : ContainerShipmentReferenceRules.NormalizeStatusFilter(status);

    /// <summary>事件类型筛选规范化（留空 = 不过滤；其他取值一律拒绝）</summary>
    public static string? NormalizeEventTypeFilter(string? raw) =>
        ContainerShipmentMilestoneRules.NormalizeEventTypeFilter(raw);

    /// <summary>
    /// 时间区间筛选边界校验：两个端点都可选，且必须「起不晚于止」；
    /// 端点有值时有界（与计划时间 / 事件时间同一取值域），超界或区间颠倒直接拒绝，不静默忽略筛选条件。
    /// </summary>
    public static void EnsureRange(DateTime? from, DateTime? to, string fieldLabel)
    {
        EnsureBounded(from, fieldLabel);
        EnsureBounded(to, fieldLabel);
        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            throw BusinessException.InvalidParameter(
                $"{fieldLabel}筛选区间起点（{from.Value:yyyy-MM-dd}）晚于终点（{to.Value:yyyy-MM-dd}）："
                + "请修正区间，系统不会自动交换或忽略筛选条件");
    }

    /// <summary>单个时间端点有界校验（留空 = 不限）</summary>
    private static void EnsureBounded(DateTime? value, string fieldLabel)
    {
        if (value is null) return;
        if (value.Value < EarliestTimestamp || value.Value >= LatestTimestamp)
            throw BusinessException.InvalidParameter(
                $"{fieldLabel}必须在 {EarliestTimestamp:yyyy-MM-dd} 与 {LatestTimestamp:yyyy-MM-dd} 之间，"
                + $"收到 {value.Value:yyyy-MM-dd HH:mm}");
    }

    /// <summary>历史视图返回条数收敛（有界：不超过 <see cref="MaxHistoryEvents"/>）</summary>
    public static int NormalizeHistoryTake(int take) =>
        take <= 0 ? MaxHistoryEvents : Math.Min(take, MaxHistoryEvents);
}
