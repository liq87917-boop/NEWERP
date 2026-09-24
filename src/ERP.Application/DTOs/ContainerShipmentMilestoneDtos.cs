namespace ERP.Application.DTOs;

/// <summary>
/// 登记装柜出运里程碑证据请求（ERP-058）。
/// <para>客户端只能提交「父出运引用 Id + 事件类型 + 事件时间 + 来源说明 / 备注 / 记录人」；
/// 状态、登记时间与审计字段一律由服务端权威写入，<strong>不</strong>接受客户端提交。</para>
/// <para>可选字段（来源说明 / 备注 / 记录人）留空表示「未知」：服务端不按计划时间、单据状态、
/// 父出运引用内容或自由文本推断补全。</para>
/// </summary>
public class ContainerShipmentMilestoneSaveDto
{
    /// <summary>父出运引用 Id（必填；必须是 ERP-057 中存在、未删除且未作废的出运引用）</summary>
    public long ContainerShipmentReferenceId { get; set; }

    /// <summary>事件类型（<c>actual-departure</c> / <c>actual-arrival</c> / <c>inspection</c> / <c>customs-release</c>；必填）</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>事件发生时间（必填且必须有界；绝不按计划开船 / 计划到港时间或单据状态推断）</summary>
    public DateTime? EventAt { get; set; }

    /// <summary>来源说明（可选，有界；空 = 未知，如「船公司网站截图」）</summary>
    public string SourceDescription { get; set; } = string.Empty;

    /// <summary>备注（可选，有界）</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>记录人（可选，有界；空 = 未知，不按登录用户强行推断）</summary>
    public string RecordedBy { get; set; } = string.Empty;
}

/// <summary>作废装柜出运里程碑证据请求（ERP-058）：必须显式填写原因，作废保留原始证据而不是删除。</summary>
public sealed class ContainerShipmentMilestoneVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入作废留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>装柜出运里程碑证据查询参数（ERP-058，全部为可选过滤；结果分页返回）</summary>
public sealed class ContainerShipmentMilestoneQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>父出运引用 Id 筛选（留空 = 全部父记录）</summary>
    public long? ContainerShipmentReferenceId { get; set; }

    /// <summary>事件类型筛选（留空 = 全部类型；未知取值一律拒绝）</summary>
    public string? EventType { get; set; }

    /// <summary>状态筛选（1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>事件时间开始（含当天；留空 = 不限）</summary>
    public DateTime? EventDateFrom { get; set; }

    /// <summary>事件时间结束（含当天；留空 = 不限）</summary>
    public DateTime? EventDateTo { get; set; }

    /// <summary>关键字（匹配来源说明 / 备注 / 记录人；留空 = 不过滤）</summary>
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
/// 装柜出运里程碑证据行（ERP-058，读取用）：父出运引用快照（只读派生）+ 用户录入的操作性事件证据。
/// <para>父记录信息（源记录类型 / 单号 / 柜号 / 状态）为**只读派生**：父出运引用被删除或不存在时
/// 历史里程碑照常可读，只是显式标注不可用，绝不改派到别的出运引用。</para>
/// <para><see cref="EvidenceCategoryText"/> 显式说明「这是仓库操作性证据」：查验 / 放行不是海关决定，
/// 开船 / 到港不是承运人确认，都不构成出运许可。</para>
/// </summary>
public sealed record ContainerShipmentMilestoneDto(
    long Id,
    long ContainerShipmentReferenceId,
    string ParentSourceType,
    string ParentSourceTypeText,
    string ParentSourceNo,
    string ParentContainerNo,
    string ParentStatusText,
    bool ParentAvailable,
    string ParentAvailabilityText,
    string EventType,
    string EventTypeText,
    DateTime EventAt,
    string SourceDescription,
    string Notes,
    string RecordedBy,
    DateTime RecordedAt,
    int Status,
    string StatusText,
    bool IsRecorded,
    bool IsVoided,
    DateTime? VoidedAt,
    string VoidReason,
    string EvidenceCategoryText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText);

/// <summary>
/// 装柜出运里程碑证据详情（ERP-058）：当前证据 + 父记录关联口径 / 证据语义 / 模块边界文案。
/// <para>详情视图只读：里程碑不提供修改接口（更正走显式作废），也不提供硬删除。</para>
/// </summary>
public sealed record ContainerShipmentMilestoneDetailDto(
    ContainerShipmentMilestoneDto Milestone,
    string InfoText,
    string BoundaryText);

/// <summary>
/// 可挂里程碑证据的出运引用候选（ERP-058，只读有界）：只列出 ERP-057 中未删除、未作废的出运引用，
/// 并批量统计其里程碑条数。
/// <para>候选列表只用于**显式选择**父记录：系统<strong>不</strong>按柜号 / S/O / B/L 等自由文本
/// 自动挑选或匹配父记录；没有任何里程碑的历史出运引用照常出现在候选中，不需要任何回填。</para>
/// </summary>
public sealed record ContainerShipmentMilestoneParentCandidateDto(
    long ContainerShipmentReferenceId,
    string SourceType,
    string SourceTypeText,
    string SourceNo,
    string ContainerNo,
    DateTime SourceDate,
    int SourceStatus,
    string SourceStatusText,
    bool Available,
    int MilestoneCount,
    int ActiveMilestoneCount,
    string EligibilityText);

