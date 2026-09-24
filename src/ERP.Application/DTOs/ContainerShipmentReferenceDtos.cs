namespace ERP.Application.DTOs;

/// <summary>
/// 新增出运引用证据请求（ERP-057）。
/// <para>客户端只能提交「源记录类型 + 源记录 Id + 出运证据字段」；源记录单号 / 日期 / 状态 / 柜号快照、
/// 状态、修订号与审计字段一律由服务端权威写入，<strong>不</strong>接受客户端提交的快照值。</para>
/// <para>全部出运证据字段都是**可选**的：留空 / <c>null</c> 表示「未知」，服务端不推断、不默认。</para>
/// </summary>
public class ContainerShipmentReferenceSaveDto
{
    /// <summary>源记录类型（<c>booking</c> / <c>pre-loading</c> / <c>loading-list</c>；必填）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>源记录 Id（必填；必须是存在且未删除的对应类型记录）</summary>
    public long SourceId { get; set; }

    /// <summary>出运方式（<c>LCL</c> / <c>FCL</c>；留空 = 未知，其他取值一律拒绝）</summary>
    public string ShipmentMode { get; set; } = string.Empty;

    /// <summary>订舱号 / 托运单号（S/O）引用文本（可选）</summary>
    public string ShippingOrderNo { get; set; } = string.Empty;

    /// <summary>提单号（B/L）引用文本（可选）</summary>
    public string BillOfLadingNo { get; set; } = string.Empty;

    /// <summary>承运人名称（可选）</summary>
    public string CarrierName { get; set; } = string.Empty;

    /// <summary>货代名称（可选）</summary>
    public string ForwarderName { get; set; } = string.Empty;

    /// <summary>起运港（可选）</summary>
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>中转港（可选）</summary>
    public string TransitPort { get; set; } = string.Empty;

    /// <summary>目的港（可选）</summary>
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>计划开船 / 起运时间（可选；与计划到港时间必须先后一致）</summary>
    public DateTime? PlannedDepartureAt { get; set; }

    /// <summary>计划到港 / 抵达时间（可选；与计划开船时间必须先后一致）</summary>
    public DateTime? PlannedArrivalAt { get; set; }

    /// <summary>拖车 / 集卡服务商名称（可选）</summary>
    public string TruckerName { get; set; } = string.Empty;

    /// <summary>报关行字典项 Id（可选；必须是未删除、已启用、类型为 CustomsBroker 的字典项）</summary>
    public long? CustomsBrokerId { get; set; }

    /// <summary>备注（可选，有界）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 修订出运引用证据请求（ERP-057）。
/// <para>修订必须填写**原因**：服务端会先把修订前的原值写入只追加的修订留痕表，再写回新值，
/// 修订号递增；修订<strong>不</strong>改写任何源记录。</para>
/// <para>源记录类型 / Id 如提交则必须与库中一致（不允许把引用改派到其它记录：改派一律拒绝，
/// 如需指向别的柜请新建一条引用并作废旧的）。</para>
/// </summary>
public sealed class ContainerShipmentReferenceUpdateDto : ContainerShipmentReferenceSaveDto
{
    /// <summary>修订原因（必填，有界；写入修订留痕，作为「为什么改」的审计依据）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>作废出运引用证据请求（ERP-057）：必须显式填写原因，作废保留原值而不是删除。</summary>
public sealed class ContainerShipmentReferenceVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>出运引用证据台账查询参数（ERP-057，全部为可选过滤；结果分页返回）</summary>
public sealed class ContainerShipmentReferenceQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>源记录类型筛选（留空 = 全部类型；未知取值一律拒绝）</summary>
    public string? SourceType { get; set; }

    /// <summary>源记录 Id 筛选（留空 = 全部源记录）</summary>
    public long? SourceId { get; set; }

    /// <summary>状态筛选（1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>出运方式筛选（LCL / FCL，留空 = 全部；未知取值一律拒绝）</summary>
    public string? ShipmentMode { get; set; }

    /// <summary>登记时间开始（含当天；留空 = 不限）</summary>
    public DateTime? RecordedDateFrom { get; set; }

    /// <summary>登记时间结束（含当天；留空 = 不限）</summary>
    public DateTime? RecordedDateTo { get; set; }

    /// <summary>关键字（匹配源单号 / 柜号 / 提单号 / 订舱号 / 承运人 / 货代；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 PageQuery 同口径）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
    }
}

/// <summary>
/// 出运引用证据行（ERP-057，读取用）：源记录快照 + 用户录入的出运证据 + 状态 / 修订 / 作废留痕。
/// <para>可用性标注为**只读派生**：源记录被软删除或报关行字典项被停用 / 删除后历史证据照常可读，
/// 只是显式标注为不可用；<strong>不</strong>代表承运人 / 海关 / 货代确认，也<strong>不</strong>代表
/// 装柜状态、报关结论或放行许可。</para>
/// </summary>
public sealed record ContainerShipmentReferenceDto(
    long Id,
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
    long? CustomsBrokerId,
    string CustomsBrokerName,
    string Remark,
    int Status,
    string StatusText,
    bool IsRecorded,
    bool IsVoided,
    int RevisionNo,
    DateTime RecordedAt,
    DateTime? LastRevisedAt,
    string LastRevisionReason,
    DateTime? VoidedAt,
    string VoidReason,
    int RevisionCount,
    bool SourceAvailable,
    string SourceAvailabilityText,
    bool CustomsBrokerAvailable,
    string CustomsBrokerAvailabilityText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText);

/// <summary>
/// 出运引用修订留痕（ERP-057，读取用，只追加）：一条记录 = 被取代的那一版修订及其**修订前原值**。
/// <para>留痕只读、不可改、不可删：界面据此展示「V1 → V2 改了什么、为什么改、什么时候改的」，
/// 而不是让历史值被静默覆盖。</para>
/// </summary>
public sealed record ContainerShipmentReferenceRevisionDto(
    long Id,
    long ContainerShipmentReferenceId,
    int RevisionNo,
    string SourceType,
    long SourceId,
    string SourceNo,
    DateTime SupersededAt,
    string Reason,
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
    long? CustomsBrokerId,
    string CustomsBrokerName,
    string Remark);

/// <summary>
/// 出运引用详情（ERP-057）：当前证据 + 完整修订留痕（有界）+ 说明文案。
/// <para><see cref="RevisionCount"/> 为留痕表中的条数；留痕超过单次返回上限时以
/// <see cref="RevisionsTruncated"/> 显式说明，绝不静默截断后当成完整历史。</para>
/// </summary>
public sealed record ContainerShipmentReferenceDetailDto(
    ContainerShipmentReferenceDto Reference,
    List<ContainerShipmentReferenceRevisionDto> Revisions,
    int RevisionCount,
    bool RevisionsTruncated,
    int RevisionTakeLimit,
    string InfoText,
    string BoundaryText);

/// <summary>
/// 可登记出运引用的源记录候选（ERP-057，只读有界）：只列出该类型下**未删除**的既有记录，
/// 并标注是否已有有效出运引用。
/// <para>候选列表只用于**显式选择**：系统<strong>不</strong>按柜号 / 订单号 / 单证号等自由文本
/// 自动挑选或匹配记录（与 ERP-040 的「未关联不猜引用」同一口径）。</para>
/// </summary>
public sealed record ContainerShipmentReferenceSourceCandidateDto(
    string SourceType,
    long SourceId,
    string SourceNo,
    DateTime SourceDate,
    int SourceStatus,
    string SourceStatusText,
    string ContainerNo,
    bool AlreadyReferenced,
    long? ExistingReferenceId,
    bool Eligible,
    string EligibilityText);
