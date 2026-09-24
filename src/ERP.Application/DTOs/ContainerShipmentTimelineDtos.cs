namespace ERP.Application.DTOs;

/// <summary>
/// 装柜出运出运证据时间线 / 跟踪工作台查询参数（ERP-059，全部筛选均为**显式字段**；结果分页返回）。
/// <para>口径：只按显式字段筛选（源记录类型 / Id、柜号、单号、B/L、S/O、起运·中转·目的港、计划开船·到港时间、
/// 记录事件类型与事件时间），不做跨记录「文本相似度」推断，也不会把不同客户 / 不同柜的记录并成一条。</para>
/// <para>未知筛选取值（源记录类型 / 事件类型 / 状态 / 时间区间颠倒）一律由服务端拒绝，不静默忽略筛选条件。</para>
/// </summary>
public sealed class ContainerShipmentTimelineQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>源记录类型筛选（<c>booking</c> / <c>pre-loading</c> / <c>loading-list</c>；留空 = 全部类型）</summary>
    public string? SourceType { get; set; }

    /// <summary>源记录 Id 显式筛选（留空 = 全部源记录）</summary>
    public long? SourceId { get; set; }

    /// <summary>引用状态筛选（1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>源记录单号显式筛选（订柜单号 / 预装柜单号 / 装柜清单号；留空 = 不过滤；不支持相似度匹配）</summary>
    public string? SourceNo { get; set; }

    /// <summary>柜号显式筛选（留空 = 不过滤）</summary>
    public string? ContainerNo { get; set; }

    /// <summary>提单号（B/L）显式筛选（留空 = 不过滤）</summary>
    public string? BillOfLadingNo { get; set; }

    /// <summary>订舱号（S/O）显式筛选（留空 = 不过滤）</summary>
    public string? ShippingOrderNo { get; set; }

    /// <summary>起运港显式筛选（留空 = 不过滤）</summary>
    public string? DeparturePort { get; set; }

    /// <summary>中转港显式筛选（留空 = 不过滤）</summary>
    public string? TransitPort { get; set; }

    /// <summary>目的港显式筛选（留空 = 不过滤）</summary>
    public string? DestinationPort { get; set; }

    /// <summary>计划开船时间区间起点（含当天；留空 = 不限；只匹配**已持久化**的计划值）</summary>
    public DateTime? PlannedDepartureFrom { get; set; }

    /// <summary>计划开船时间区间终点（含当天；留空 = 不限）</summary>
    public DateTime? PlannedDepartureTo { get; set; }

    /// <summary>计划到港时间区间起点（含当天；留空 = 不限；只匹配**已持久化**的计划值）</summary>
    public DateTime? PlannedArrivalFrom { get; set; }

    /// <summary>计划到港时间区间终点（含当天；留空 = 不限）</summary>
    public DateTime? PlannedArrivalTo { get; set; }

    /// <summary>记录事件类型筛选（ERP-058 allowlist；留空 = 不过滤；未知取值一律拒绝）</summary>
    public string? EventType { get; set; }

    /// <summary>记录事件时间区间起点（含当天；留空 = 不限）</summary>
    public DateTime? EventDateFrom { get; set; }

    /// <summary>记录事件时间区间终点（含当天；留空 = 不限）</summary>
    public DateTime? EventDateTo { get; set; }

    /// <summary>
    /// 记录事件筛选是否显式包含**已作废历史**（默认 <c>false</c> = 只看有效证据）。
    /// 打开时已作废证据才参与匹配与汇总，并在结果中显式标注为历史。
    /// </summary>
    public bool IncludeVoidedEvents { get; set; }

    /// <summary>关键字（只匹配显式列：源单号 / 柜号 / B/L / S/O / 承运人 / 目的港；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 PageQuery 同口径：页码下限 1、每页条数收敛到上限）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
    }
}

/// <summary>
/// 时间线条目（ERP-059，读取用）：一条 = 一个计划值（ETD / ETA，来自 ERP-057 出运引用）
/// 或一个实际事件（来自 ERP-058 里程碑证据）。
/// <para>计划与实际**分开标注**：<see cref="IsPlanned"/> / <see cref="IsActual"/> 互斥；
/// 缺失的事件不带时间戳（<see cref="HasTimestamp"/> = false）并按种类显示「无」或「未知」，
/// <strong>绝不</strong>用计划时间补齐实际事件，也<strong>绝不</strong>推断为已开船 / 已到港 / 已放行。</para>
/// </summary>
public sealed record ContainerShipmentTimelineEventDto(
    string Kind,
    string KindText,
    bool IsPlanned,
    bool IsActual,
    bool HasTimestamp,
    DateTime? EventAt,
    string EventAtText,
    string TimestampKindText,
    long? MilestoneId,
    string EventType,
    string EventTypeText,
    string SourceDescription,
    string Notes,
    string RecordedBy,
    DateTime? RecordedAt,
    int? Status,
    string StatusText,
    bool IsVoided,
    DateTime? VoidedAt,
    string VoidReason,
    string EvidenceCategoryText,
    string SourceLabelText);

/// <summary>
/// 计划时间与实际事件的时间差（ERP-059，读取用）：只有两条持久化时间戳**同时存在且日期时间语义一致**时
/// <see cref="Comparable"/> 才为 true；否则 <see cref="Text"/> 显式说明「无法比较」的原因。
/// <para><see cref="BasisText"/> 与 <see cref="Text"/> 都标注这是**算术证据**：不是承运人 / 海关确认、
/// 不是合同 SLA 结论，也不做任何时区换算。</para>
/// </summary>
public sealed record ContainerShipmentTimelineVarianceDto(
    string Kind,
    string Label,
    string PlannedLabel,
    DateTime? PlannedAt,
    string PlannedAtText,
    string PlannedKindText,
    string ActualLabel,
    DateTime? ActualAt,
    string ActualAtText,
    string ActualKindText,
    string ActualEvidenceText,
    bool Comparable,
    string Text,
    string BasisText);

/// <summary>
/// 时间线汇总（ERP-059，读取用）：有效 / 已作废 / 历史异常状态的条目数，以及各实际事件类型**最近一次**
/// 已登记证据时间（只来自有效里程碑；未登记时保持 <c>null</c>，由前端显示「无」/「未知」）。
/// <para><see cref="TimelineStateText"/> 只说明「登记了什么、缺什么」，不是业务状态结论。</para>
/// </summary>
public sealed record ContainerShipmentTimelineSummaryDto(
    int ActiveEventCount,
    int VoidedEventCount,
    int OtherStatusEventCount,
    DateTime? LatestActualDepartureAt,
    DateTime? LatestActualArrivalAt,
    DateTime? LatestInspectionAt,
    DateTime? LatestCustomsReleaseAt,
    string TimelineStateText,
    string StatusInferenceText);

/// <summary>
/// 跟踪工作台 / 详情中的一条「出运」记录（ERP-059，读取用）：ERP-057 出运引用证据 + 只读汇总。
/// <para><see cref="Linked"/> = false 表示该源记录**没有**有效出运引用（或引用已作废 / 源记录已删除）：
/// 此时所有证据字段保持「未知 / 无」，并显式给出原因 —— 系统**绝不**按柜号、S/O、B/L 等自由文本
/// 兜底挑选一条出运引用，也不会改派。</para>
/// </summary>
public sealed record ContainerShipmentTimelineShipmentDto(
    bool Linked,
    string NotLinkedReason,
    long ReferenceId,
    string SourceType,
    string SourceTypeText,
    long SourceId,
    string SourceNo,
    DateTime SourceDate,
    int SourceStatus,
    string SourceStatusText,
    string ContainerNo,
    string ShipmentMode,
    string ShipmentModeText,
    string ShippingOrderNo,
    string BillOfLadingNo,
    string CarrierName,
    string ForwarderName,
    string DeparturePort,
    string TransitPort,
    string DestinationPort,
    DateTime? PlannedDepartureAt,
    DateTime? PlannedArrivalAt,
    string TruckerName,
    string Remark,
    int Status,
    string StatusText,
    bool IsRecorded,
    bool IsVoided,
    int RevisionNo,
    DateTime? RecordedAt,
    DateTime? VoidedAt,
    string VoidReason,
    bool ReferenceAvailable,
    string ReferenceAvailabilityText,
    bool SourceAvailable,
    string SourceAvailabilityText,
    ContainerShipmentTimelineSummaryDto Summary);

/// <summary>
/// 出运证据时间线详情（ERP-059，读取用）：出运记录 + **有效**时间线（计划值 + 已登记实际事件）+
/// 显式的已作废历史视图（有界）+ 时间差算术证据 + 口径文案。
/// <para><see cref="Events"/> 只包含计划条目与**有效**（已登记）实际事件；已作废与历史异常状态的条目
/// 只出现在 <see cref="HistoryEvents"/>，从有效时间线中排除但按原值保留可读，超出上限时以
/// <see cref="HistoryTruncated"/> / <see cref="ActiveEventsTruncated"/> 显式说明，绝不静默截断。</para>
/// </summary>
public sealed record ContainerShipmentTimelineDetailDto(
    ContainerShipmentTimelineShipmentDto Shipment,
    List<ContainerShipmentTimelineEventDto> Events,
    List<ContainerShipmentTimelineEventDto> HistoryEvents,
    int ActiveEventCount,
    int HistoryCount,
    bool ActiveEventsTruncated,
    bool HistoryTruncated,
    int EventTakeLimit,
    int HistoryTakeLimit,
    List<ContainerShipmentTimelineVarianceDto> Variances,
    string PlannedVersusActualText,
    string NoStatusInferenceText,
    string EvidenceText,
    string HistoryText,
    string InfoText,
    string BoundaryText);

