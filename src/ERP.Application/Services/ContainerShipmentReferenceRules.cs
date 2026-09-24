using ERP.Application.Common;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运引用证据登记的纯规则（ERP-057，无数据库依赖，便于逐条单测）：
/// 源记录类型 allowlist（订柜信息 / 预装柜单 / 装柜清单）、状态机（已登记 → 已作废）、
/// 出运证据文本与计划时间的边界校验、以及源记录与报关行引用的资格文案。
/// <para>审计口径（本模块存在的前提）：装柜链路已有**唯一**的持久化引用关系
/// （<c>ContainerPreLoading.BookingId</c> / <c>ContainerLoadingList.PreLoadingId</c>），
/// 订柜信息由 ERP-040 承载本套跟踪值的权威记录；因此本模块<strong>只登记证据</strong>：
/// 不新建出运主数据、不复制订柜跟踪列、不按柜号 / 订单号 / 单证号等自由文本匹配任何记录。</para>
/// <para>边界：规则只判断「录入值是否合法」，不做任何推断与派生 —— 不按柜型 / 体积 / 客户 / 航线猜出运方式，
/// 不由自由文本猜港口、日期或报关行，也不写任何单据、库存、财务与外部系统数据。</para>
/// </summary>
public static class ContainerShipmentReferenceRules
{
    // ==================== 1. 源记录类型（显式 allowlist） ====================

    /// <summary>源记录类型：订柜信息（<c>ContainerBooking</c>，ERP-040 跟踪值权威记录）</summary>
    public const string SourceTypeBooking = "booking";

    /// <summary>源记录类型：预装柜单（<c>ContainerPreLoading</c>）</summary>
    public const string SourceTypePreLoading = "pre-loading";

    /// <summary>源记录类型：装柜清单（<c>ContainerLoadingList</c>）</summary>
    public const string SourceTypeLoadingList = "loading-list";

    /// <summary>支持的源记录类型（其他取值一律拒绝，不静默忽略）</summary>
    public static readonly IReadOnlyList<string> SupportedSourceTypes =
        new[] { SourceTypeBooking, SourceTypePreLoading, SourceTypeLoadingList };

    // ==================== 2. 状态机 ====================

    /// <summary>状态：已登记（可修订 / 可作废，是源记录当前的出运引用证据）</summary>
    public const int StatusRecorded = 1;

    /// <summary>状态：已作废（只读，保留原始值与修订留痕，不物理删除、不静默替换）</summary>
    public const int StatusVoided = 2;

    // ==================== 3. 有界常量 ====================

    /// <summary>承运人名称长度上限</summary>
    public const int CarrierNameMaxLength = 200;

    /// <summary>货代名称长度上限</summary>
    public const int ForwarderNameMaxLength = 200;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>修订原因长度上限</summary>
    public const int MaxRevisionReasonLength = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>单条出运引用最多返回的修订留痕条数（有界；超出时显式说明被截断）</summary>
    public const int MaxRevisionTake = 50;

    /// <summary>源记录候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxSourceCandidates = 200;

    /// <summary>
    /// 同一条源记录允许保留的**有效**出运引用条数：1 条（一条权威记录只有一套出运引用证据）。
    /// 已作废行不占用该额度：作废后可重新登记一条新的有效引用，新旧并存可查。
    /// </summary>
    public const int MaxReferencesPerSource = 1;

    /// <summary>计划时间允许的最早取值（有界：避免录入明显不可能的年份）</summary>
    public static readonly DateTime EarliestPlannedDate = new(2000, 1, 1);

    /// <summary>计划时间允许的最晚取值（不与上界同日，便于比较）</summary>
    public static readonly DateTime LatestPlannedDate = new(2100, 1, 1);

    // ==================== 4. 文案（接口、界面与文档同源） ====================

    /// <summary>未知 / 未填写文案（未知不回落为 0、空或今天；与 ERP-040 同一口径）</summary>
    public const string UnknownText = ContainerShipmentTrackingRules.UnknownText;

    /// <summary>源记录关联口径文案</summary>
    public const string SourceLinkText =
        "源记录只按显式的类型 + Id 关联：系统不会按柜号、订单号、单证号或任何自由文本相似度猜测记录；"
        + "源记录被删除或改名后历史证据照常可读，只是显式标注不可用，也不能改派到其它记录。";

    /// <summary>证据语义文案（不是承运人 / 海关 / 货代确认）</summary>
    public const string EvidenceText =
        "本登记册记录的是用户录入的操作性出运证据：不是承运人 / 海关 / 货代的确认或回执，不是提单正本，"
        + "不是报关或海关放行结论，也不构成任何清关许可、交付承诺或法律依据；"
        + "缺失的引用一律显示「未知」，系统不按柜型、体积、客户、航线或自由文本推断补全。";

    /// <summary>模块边界文案（明确只写证据，不改写装柜链路与任何下游记录）</summary>
    public const string BoundaryText =
        "出运引用登记只写本登记册自己的两张表：不改写订柜信息 / 预装柜单 / 装柜清单的任何列、状态与工作流"
        + "（不推进装柜状态、不改柜号、不写单据号），也不改写销售订单、采购订单、库存与库存成本、库存流水、"
        + "单证中心、发票、费用与分摊、收付款、税务与结算记录；登记 / 修订 / 作废都不会联系承运人、海关、"
        + "货代或任何外部跟踪系统。";

    /// <summary>规则文案</summary>
    public const string RuleText =
        "登记口径：一条出运引用显式指向恰好一条既有、未删除的源记录（订柜信息 / 预装柜单 / 装柜清单），"
        + "同一条源记录最多保留 1 条有效引用；出运方式只接受 LCL / FCL / 未指定，其他取值一律拒绝；"
        + "计划开船时间不得晚于计划到港时间；更正走显式修订（记录修订前原值）或显式作废（必填原因），"
        + "不提供硬删除与静默替换。";

    // ==================== 5. 规范化与校验 ====================

    /// <summary>
    /// 源记录类型规范化：忽略首尾空白与大小写；空串 = 未指定（拒绝，源记录类型必填）；
    /// 其他取值一律拒绝（不接受自由文本当作权威类型）。
    /// </summary>
    public static string NormalizeSourceType(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                $"请选择源记录类型：只允许 {string.Join(" / ", SupportedSourceTypes)}");
        if (!SupportedSourceTypes.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"源记录类型「{raw}」不受支持：只允许 {string.Join(" / ", SupportedSourceTypes)}"
                + "（不接受自由文本作为源记录类型）");
        return value;
    }

    /// <summary>源记录类型是否受支持（忽略大小写与首尾空白；不抛异常，供只读过滤器复用）</summary>
    public static bool IsSupportedSourceType(string? raw) =>
        SupportedSourceTypes.Contains((raw ?? string.Empty).Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>源记录类型文案（未知取值照实回显，不假定为订柜信息）</summary>
    public static string SourceTypeText(string? sourceType) =>
        (sourceType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            SourceTypeBooking => "订柜信息",
            SourceTypePreLoading => "预装柜单",
            SourceTypeLoadingList => "装柜清单",
            _ => $"未知（{sourceType}）"
        };

    /// <summary>引用状态文案（未知状态码照实说明，绝不按「已登记」兜底）</summary>
    public static string StatusText(int status) => status switch
    {
        StatusRecorded => "已登记",
        StatusVoided => "已作废",
        _ => throw BusinessException.InvalidParameter(
            $"出运引用状态只能是 {StatusRecorded}（已登记）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>出运方式过滤规范化（留空 = 不过滤；取值复用 ERP-040 出运方式域，其他取值一律拒绝）</summary>
    public static string? NormalizeModeFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var mode = ContainerShipmentTrackingRules.NormalizeShipmentMode(raw);
        return mode.Length == 0 ? null : mode;
    }

    /// <summary>计划时间规范化：留空保持 <c>null</c>（= 未知），有值时必须处在有界区间内</summary>
    public static DateTime? NormalizePlannedTimestamp(DateTime? raw, string fieldLabel)
    {
        if (raw is null) return null;
        var value = raw.Value;
        if (value < EarliestPlannedDate || value >= LatestPlannedDate)
            throw BusinessException.InvalidParameter(
                $"{fieldLabel}必须在 {EarliestPlannedDate:yyyy-MM-dd} 与 {LatestPlannedDate:yyyy-MM-dd} 之间，"
                + $"收到 {value:yyyy-MM-dd HH:mm}");
        return value;
    }

    /// <summary>
    /// 计划时间一致性校验（服务端权威）：两个时间都可选；只有二者**同时**存在时，才要求
    /// 计划开船时间不晚于计划到港时间；任一为空都保持「未知」，绝不推断、也绝不因此补一个默认时间。
    /// </summary>
    public static void EnsurePlannedTimestampsCoherent(DateTime? plannedDepartureAt, DateTime? plannedArrivalAt)
    {
        if (plannedDepartureAt.HasValue && plannedArrivalAt.HasValue
            && plannedDepartureAt.Value > plannedArrivalAt.Value)
            throw BusinessException.InvalidParameter(
                $"计划开船时间（{plannedDepartureAt.Value:yyyy-MM-dd HH:mm}）不能晚于计划到港时间"
                + $"（{plannedArrivalAt.Value:yyyy-MM-dd HH:mm}）：请修正时间，或清空其中一个保持「未知」"
                + "（系统不会自动改写或补全时间）");
    }

    /// <summary>文本规范化（去首尾空白）+ 长度校验（超长拒绝，不静默截断；空 = 未填写）</summary>
    public static string NormalizeText(string? raw, int maxLength, string fieldLabel)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(raw);
        ContainerShipmentTrackingRules.EnsureLength(value, maxLength, fieldLabel);
        return value;
    }

    /// <summary>备注规范化（可选，有界）</summary>
    public static string NormalizeRemark(string? raw) => NormalizeText(raw, MaxRemarkLength, "备注");

    /// <summary>作废原因规范化（必填 + 长度校验：作废保留原始值，必须记录更正原因）</summary>
    public static string NormalizeVoidReason(string? reason)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(reason);
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写作废原因：作废会保留原始出运证据与修订留痕，必须记录更正原因");
        if (value.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter($"作废原因长度不能超过 {MaxVoidReasonLength} 个字符");
        return value;
    }

    /// <summary>修订原因规范化（必填 + 长度校验：修订前的原值会写入留痕，必须记录修订原因）</summary>
    public static string NormalizeRevisionReason(string? reason)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(reason);
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写修订原因：修订会先保留修订前的原值，必须记录「为什么改」以便追溯");
        if (value.Length > MaxRevisionReasonLength)
            throw BusinessException.InvalidParameter($"修订原因长度不能超过 {MaxRevisionReasonLength} 个字符");
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

    // ==================== 6. 源记录与报关行状态文案 / 资格 ====================

    /// <summary>源记录状态文案快照（未知状态码照实回显，不假定为已审核）</summary>
    public static string DocumentStatusText(int status) => status switch
    {
        (int)DocumentStatus.Pending => "待提交",
        (int)DocumentStatus.Submitted => "已提交",
        (int)DocumentStatus.Approved => "已审核",
        (int)DocumentStatus.Rejected => "已驳回",
        (int)DocumentStatus.Completed => "已完成",
        (int)DocumentStatus.Cancelled => "已取消",
        _ => $"未知（{status}）"
    };

    /// <summary>源记录资格判定：只有存在且未删除的源记录才能被登记 / 继续引用</summary>
    public static (bool Eligible, string Text) EvaluateSourceEligibility(
        bool exists, bool deleted, string sourceTypeText)
    {
        if (!exists)
            return (false, $"指定的{sourceTypeText}记录不存在，不能登记出运引用（历史证据仍可读）");
        if (deleted)
            return (false, $"指定的{sourceTypeText}记录已删除，不能登记出运引用（历史证据仍可读）");
        return (true, $"{sourceTypeText}记录可引用（只读关联，不会改写该记录）");
    }

    /// <summary>源记录可用性文案（被删除 / 不存在时照实说明，历史快照仍可读）</summary>
    public static string SourceAvailabilityText(bool available, string sourceTypeText) =>
        available
            ? $"{sourceTypeText}记录可用（只读关联）"
            : $"{sourceTypeText}记录已删除或不存在：历史出运证据快照仍可读，但不能再改派或新增引用";

    /// <summary>报关行引用可用性文案（字典项停用 / 删除 / 改类型后显式标注，历史名称照常显示）</summary>
    public static string CustomsBrokerAvailabilityText(bool available, string brokerName)
    {
        var name = ContainerShipmentTrackingRules.NormalizeText(brokerName);
        if (name.Length == 0) return "未指定报关行（未知）";
        return available
            ? "报关行字典项可用"
            : $"报关行「{name}」已停用 / 删除 / 类型不符：历史名称快照照常显示，但不能再次选用";
    }

    /// <summary>只有已登记的引用可以修订 / 作废（已作废证据只读，不允许重写）</summary>
    public static void EnsureRecordedForChange(int status, string sourceNoText)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"{sourceNoText}的出运引用已是已作废状态，不能修订或重复作废"
                + "（已作废证据保留原始值与修订留痕，不提供重写或硬删除；如需重新记录请新建一条引用）");
    }

    /// <summary>
    /// 源记录改派校验：已登记的引用不允许把源记录类型 / Id 改成别的记录
    /// （避免历史证据被静默改派；如需指向别的柜请新建引用并作废旧的）。
    /// <para>请求未提供源记录（类型为空且 Id 不大于 0）时视为「保持原样」，不视为改派。</para>
    /// </summary>
    public static void EnsureSourceUnchanged(
        string storedSourceType, long storedSourceId, string? requestedSourceType, long requestedSourceId)
    {
        var typeProvided = !string.IsNullOrWhiteSpace(requestedSourceType);
        var idProvided = requestedSourceId > 0;
        if (!typeProvided && !idProvided) return;

        var sameType = typeProvided
            && string.Equals(storedSourceType, NormalizeSourceType(requestedSourceType), StringComparison.Ordinal);
        var sameId = idProvided && storedSourceId == requestedSourceId;
        if (sameType && sameId) return;

        throw BusinessException.InvalidParameter(
            $"不允许把已登记的出运引用改派到其它源记录（当前指向 {SourceTypeText(storedSourceType)} "
            + $"Id={storedSourceId}）：如需指向别的柜 / 清单，请新建一条出运引用并作废本条");
    }
}
