using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运里程碑证据的纯规则（ERP-058，无数据库依赖，便于逐条单测）：
/// 事件类型 allowlist（实际开船 / 实际到港 / 查验 / 放行）、状态机（已登记 → 已作废）、
/// 事件时间有界校验、文本边界校验，以及「仓库证据 vs 权威结论」的显式文案。
/// <para>审计口径（本模块存在的前提）：ERP-057 已建立唯一权威的出运引用登记册，本模块只在其
/// <strong>之下</strong>追加操作性事件留痕：不新建出运 / 跟踪主数据、不复制订柜跟踪列、
/// 不按柜号 / S/O / B/L 等自由文本匹配任何记录，也不把里程碑当作承运人 / 海关确认。</para>
/// <para>边界：规则只判断「录入值是否合法」，不做任何推断与派生 —— 不按计划开船 / 计划到港时间、
/// 单据状态或自由文本推断事件时间，不把「查验 / 放行」解释为清关结论或出运许可，
/// 也不写任何单据、订单、库存、财务与外部系统数据。</para>
/// </summary>
public static class ContainerShipmentMilestoneRules
{
    // ==================== 1. 事件类型（显式 allowlist） ====================

    /// <summary>事件类型：实际开船 / 实际离港</summary>
    public const string EventTypeActualDeparture = "actual-departure";

    /// <summary>事件类型：实际到港 / 实际抵达</summary>
    public const string EventTypeActualArrival = "actual-arrival";

    /// <summary>事件类型：查验（如海关 / 商检查验）</summary>
    public const string EventTypeInspection = "inspection";

    /// <summary>事件类型：放行（如报关放行 / 提货放行）</summary>
    public const string EventTypeCustomsRelease = "customs-release";

    /// <summary>支持的事件类型（其他取值一律拒绝，不静默忽略）</summary>
    public static readonly IReadOnlyList<string> SupportedEventTypes = new[]
    {
        EventTypeActualDeparture, EventTypeActualArrival, EventTypeInspection, EventTypeCustomsRelease
    };

    // ==================== 2. 状态机 ====================

    /// <summary>状态：已登记（有效证据，可作废）</summary>
    public const int StatusRecorded = 1;

    /// <summary>状态：已作废（只读，保留原始事件类型 / 时间 / 来源与作废原因，不物理删除）</summary>
    public const int StatusVoided = 2;

    // ==================== 3. 有界常量 ====================

    /// <summary>来源说明长度上限</summary>
    public const int MaxSourceDescriptionLength = 200;

    /// <summary>备注长度上限</summary>
    public const int MaxNotesLength = 500;

    /// <summary>记录人长度上限</summary>
    public const int MaxRecordedByLength = 100;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>父出运引用候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxParentCandidates = 200;

    /// <summary>事件时间允许的最早取值（有界：避免录入明显不可能的年份）</summary>
    public static readonly DateTime EarliestEventAt = new(2000, 1, 1);

    /// <summary>事件时间允许的最晚取值（不与上界同日，便于比较）</summary>
    public static readonly DateTime LatestEventAt = new(2100, 1, 1);

    // ==================== 4. 文案（接口、界面与文档同源） ====================

    /// <summary>未知 / 未填写文案（未知不回落为空、0 或今天；与 ERP-040 / ERP-057 同一口径）</summary>
    public const string UnknownText = ContainerShipmentTrackingRules.UnknownText;

    /// <summary>父记录关联口径文案（显式父 Id，绝不按文本猜记录，也不改派）</summary>
    public const string ParentLinkText =
        "里程碑只按显式的父出运引用 Id 挂在 ERP-057 出运引用之下：系统不会按柜号、S/O、B/L 或任何自由文本"
        + "相似度挑选父记录；父记录被删除或不存在时历史里程碑照常可读，只是显式标注不可用，也不能改派到别的出运引用。";

    /// <summary>证据语义文案（不是承运人 / 海关 / 货代确认，不是放行许可）</summary>
    public const string EvidenceText =
        "本登记册记录的是用户录入的操作性里程碑证据：不是承运人 / 海关 / 货代的确认或回执，"
        + "不是报关或海关放行结论，也不构成清关许可、交付承诺、法律依据或出运许可；"
        + "缺失的选项一律显示「未知」，系统不按计划时间、单据状态或自由文本推断。";

    /// <summary>模块边界文案（明确只写本登记册，不改写任何既有记录）</summary>
    public const string BoundaryText =
        "里程碑登记只写本登记册自己的表：不改写所属出运引用与订柜信息 / 预装柜单 / 装柜清单的任何列、"
        + "状态与工作流，也不会自动推进装柜、报关、出运或任何业务单据，不改写销售订单、采购订单、库存与库存成本、"
        + "库存流水、单证中心、发票、费用与分摊、收付款、税务与结算记录，也不轮询承运人、海关、货代或任何外部系统。";

    /// <summary>规则文案</summary>
    public const string RuleText =
        "登记口径：每条里程碑显式挂在一条既有、未删除且未作废的出运引用之下；事件类型只接受"
        + "实际开船 / 实际到港 / 查验 / 放行，其他取值一律拒绝；事件时间必填且有界，"
        + "同一父记录 + 事件类型 + 事件时间不允许重复有效登记；更正走显式作废（必填原因），"
        + "不提供硬删除与静默改写。";

    /// <summary>查验 / 放行类事件的显式标注（仓库证据，不是权威结论）</summary>
    public const string InspectionAndReleaseEvidenceText =
        "查验 / 放行只是用户录入的仓库操作性证据：不是海关决定、不是查验或放行结论，也不代表允许出运。";

    /// <summary>开船 / 到港类事件的显式标注（仓库证据，不是承运人确认）</summary>
    public const string MovementEvidenceText =
        "开船 / 到港只是用户录入的操作性事件证据：不是承运人确认或航行回执，也不构成交付承诺。";

    // ==================== 5. 规范化与校验 ====================

    /// <summary>
    /// 事件类型规范化：忽略首尾空白与大小写；空串 = 未指定（拒绝，事件类型必填）；
    /// 其他取值一律拒绝（不接受自由文本当作事件类型）。
    /// </summary>
    public static string NormalizeEventType(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                $"请选择里程碑事件类型：只允许 {string.Join(" / ", SupportedEventTypes)}");
        if (!SupportedEventTypes.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"里程碑事件类型「{raw}」不受支持：只允许 {string.Join(" / ", SupportedEventTypes)}"
                + "（不接受自由文本作为事件类型）");
        return value;
    }

    /// <summary>事件类型是否受支持（忽略大小写与首尾空白；不抛异常，供只读过滤器复用）</summary>
    public static bool IsSupportedEventType(string? raw) =>
        SupportedEventTypes.Contains((raw ?? string.Empty).Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>事件类型文案（未知取值照实回显，绝不假定为实际开船）</summary>
    public static string EventTypeText(string? eventType) =>
        (eventType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            EventTypeActualDeparture => "实际开船 / 离港",
            EventTypeActualArrival => "实际到港 / 抵达",
            EventTypeInspection => "查验",
            EventTypeCustomsRelease => "放行",
            _ => $"未知（{eventType}）"
        };

    /// <summary>是否查验 / 放行类事件（决定证据性质文案：更强调「不是海关权威结论」）</summary>
    public static bool IsInspectionOrRelease(string? eventType)
    {
        var value = (eventType ?? string.Empty).Trim().ToLowerInvariant();
        return value == EventTypeInspection || value == EventTypeCustomsRelease;
    }

    /// <summary>证据性质文案：查验 / 放行类事件显式标注为仓库证据而不是权威海关结论</summary>
    public static string EvidenceCategoryText(string? eventType) =>
        IsInspectionOrRelease(eventType) ? InspectionAndReleaseEvidenceText : MovementEvidenceText;

    /// <summary>
    /// 状态文案：未知取值<strong>照实说明</strong>而不是抛异常（历史异常状态的证据必须保持可读，
    /// 不能被静默修正为「已登记 / 已作废」）。
    /// </summary>
    public static string StatusText(int status) => status switch
    {
        StatusRecorded => "已登记",
        StatusVoided => "已作废",
        _ => $"未知（{status}）"
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        if (status is not (StatusRecorded or StatusVoided))
            throw BusinessException.InvalidParameter(
                $"里程碑状态筛选只能是 {StatusRecorded}（已登记）/ {StatusVoided}（已作废），收到 {status}");
        return status;
    }

    /// <summary>事件类型过滤规范化（留空 = 不过滤；其他取值一律拒绝）</summary>
    public static string? NormalizeEventTypeFilter(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : NormalizeEventType(raw);

    /// <summary>
    /// 事件时间规范化（服务端权威）：必填（<c>null</c> 直接拒绝 —— 事件发生时间不能由计划时间或
    /// 单据状态推断），有值时必须处在有界区间内。
    /// </summary>
    public static DateTime NormalizeEventAt(DateTime? raw)
    {
        if (raw is null)
            throw BusinessException.InvalidParameter(
                "请填写事件发生时间：里程碑必须记录用户实际观测到的时间，系统不会按计划开船 / 计划到港时间或单据状态推断");
        var value = raw.Value;
        if (value < EarliestEventAt || value >= LatestEventAt)
            throw BusinessException.InvalidParameter(
                $"事件发生时间必须在 {EarliestEventAt:yyyy-MM-dd} 与 {LatestEventAt:yyyy-MM-dd} 之间，"
                + $"收到 {value:yyyy-MM-dd HH:mm}");
        return value;
    }

    /// <summary>文本规范化（去首尾空白）+ 长度校验（超长拒绝，不静默截断；空 = 未填写）</summary>
    public static string NormalizeText(string? raw, int maxLength, string fieldLabel)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(raw);
        ContainerShipmentTrackingRules.EnsureLength(value, maxLength, fieldLabel);
        return value;
    }

    /// <summary>来源说明规范化（可选，有界；空 = 未知）</summary>
    public static string NormalizeSourceDescription(string? raw) =>
        NormalizeText(raw, MaxSourceDescriptionLength, "来源说明");

    /// <summary>备注规范化（可选，有界）</summary>
    public static string NormalizeNotes(string? raw) => NormalizeText(raw, MaxNotesLength, "备注");

    /// <summary>记录人规范化（可选，有界；空 = 未知，不按登录用户强行推断）</summary>
    public static string NormalizeRecordedBy(string? raw) =>
        NormalizeText(raw, MaxRecordedByLength, "记录人");

    /// <summary>作废原因规范化（必填 + 长度校验：作废保留原始证据，必须记录更正原因）</summary>
    public static string NormalizeVoidReason(string? reason)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(reason);
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写作废原因：作废会保留原始事件类型、时间与来源说明，必须记录更正原因");
        if (value.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter($"作废原因长度不能超过 {MaxVoidReasonLength} 个字符");
        return value;
    }

    /// <summary>关键字规范化（去首尾空白 + 长度校验；留空 = 不过滤）</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(keyword);
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    // ==================== 6. 父记录状态文案 / 资格 ====================

    /// <summary>父记录资格判定：只有存在、未删除且未作废的出运引用才能被追加里程碑证据</summary>
    public static (bool Eligible, string Text) EvaluateParentEligibility(
        bool exists, bool deleted, int parentStatus)
    {
        if (!exists)
            return (false, "指定的出运引用不存在，不能登记里程碑证据（历史里程碑仍可读）");
        if (deleted)
            return (false, "指定的出运引用已删除，不能登记里程碑证据（历史里程碑仍可读）");
        if (parentStatus == ContainerShipmentReferenceRules.StatusVoided)
            return (false, "指定的出运引用已作废，不能新增里程碑证据（历史里程碑仍可读）");
        return (true, "出运引用可挂里程碑（只读关联，不会改写该出运引用）");
    }

    /// <summary>父记录可用性文案（被删除 / 不存在时照实说明，历史里程碑快照仍可读）</summary>
    public static string ParentAvailabilityText(bool available, bool parentVoided) => (available, parentVoided) switch
    {
        (true, true) => "父出运引用已作废：历史里程碑证据保留可读，但不能新增里程碑，也不能改派到别的出运引用",
        (true, false) => "父出运引用可用（只读关联，不会改写该出运引用）",
        _ => "父出运引用已删除或不存在：历史里程碑证据仍可读，但不能新增里程碑，也不能改派到别的出运引用"
    };

    /// <summary>
    /// 父出运引用状态文案（读取侧）：未知状态码<strong>照实说明</strong>而不是抛异常，
    /// 保证历史异常父记录不会让里程碑读取失败，也不会被静默修正。
    /// </summary>
    public static string ParentStatusText(int status) => status switch
    {
        StatusRecorded => "已登记",
        StatusVoided => "已作废",
        _ => $"未知（{status}）"
    };

    /// <summary>已作废里程碑只读：不允许重复作废或改写（保留原始类型 / 时间 / 来源）</summary>
    public static void EnsureRecordedForVoid(int status)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                "该里程碑证据已是已作废状态，不能重复作废"
                + "（已作废证据保留原始类型 / 事件时间 / 来源说明与作废原因，不提供重写或硬删除）");
    }
}
