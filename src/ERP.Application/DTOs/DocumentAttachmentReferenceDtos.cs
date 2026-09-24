namespace ERP.Application.DTOs;

/// <summary>
/// 附件引用登记请求（ERP-045）。
/// <para>客户端只能提交「引用元数据」本身：父单据类型与 Id、分类、显示名、不透明引用标识、
/// 可选内容类型 / 字节数 / 校验和、备注，以及**来源授权确认**；
/// 父单据号码 / 类型快照、登记时间、授权时间、状态与审计字段一律由服务端权威写入。</para>
/// <para>本请求<strong>不</strong>包含任何文件内容、上传地址或下载地址：服务端不会上传 / 下载 /
/// 预览 / 抓取任何对象，也不会用引用标识发起网络或文件系统访问。</para>
/// </summary>
public sealed class DocumentAttachmentReferenceSaveDto
{
    /// <summary>父单据类型（白名单：SalesOrder / PurchaseOrder / ContainerLoadingList / TradeDocument；留空或未知一律拒绝）</summary>
    public string ParentType { get; set; } = string.Empty;

    /// <summary>父单据 Id（必须指向存在且未删除的权威父单据）</summary>
    public long ParentId { get; set; }

    /// <summary>附件分类（白名单，见元数据接口返回的分类清单；未知分类一律拒绝）</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>安全显示名（必填、有界；拒绝控制字符、HTML / 脚本标记与链接形态）</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>不透明引用标识（必填、有界；只接受不透明令牌，拒绝链接 / 路径 / 空白 / 标记字符 / <c>..</c> 穿越）</summary>
    public string ReferenceId { get; set; } = string.Empty;

    /// <summary>内容类型（可选，如 <c>application/pdf</c>；只接受 <c>type/subtype</c> 形态）</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>字节大小（可选，0 = 未提供 / 未知；上限 10 GiB，超出拒绝）</summary>
    public long SizeBytes { get; set; }

    /// <summary>校验和（可选，只接受 16~128 位十六进制摘要）</summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>备注（可选、有界）</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// 来源授权确认（创建时**必须**为 true）：表示登记人声明其已获授权引用该来源；
    /// 该确认<strong>不</strong>授予系统任何存储访问权，也<strong>不</strong>代表生产数据已被系统认定合规。
    /// </summary>
    public bool SourceAuthorizationAcknowledged { get; set; }

    /// <summary>来源授权确认说明（必填、有界：授权依据 / 范围；与确认标记一起落库留痕）</summary>
    public string SourceAuthorizationNote { get; set; } = string.Empty;

    /// <summary>确认人 / 确认来源（可选，最长 100 字符，服务端原样记录）</summary>
    public string AuthorizedBy { get; set; } = string.Empty;
}

/// <summary>作废附件引用请求（ERP-045）：必须显式填写原因，作废保留原始元数据与历史，不做硬删除。</summary>
public sealed class DocumentAttachmentReferenceVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入登记留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 附件引用台账查询参数（ERP-045，全部为可选过滤；结果分页返回，默认每页 50、单次最多 200 条）。
/// </summary>
public sealed class DocumentAttachmentReferenceQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多条引用）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>父单据类型筛选（白名单取值；留空 = 全部类型）</summary>
    public string? ParentType { get; set; }

    /// <summary>父单据 Id 筛选（留空 = 全部父单据；与父单据类型配合使用时才精确到某一单据）</summary>
    public long? ParentId { get; set; }

    /// <summary>分类筛选（白名单取值；留空 = 全部分类）</summary>
    public string? Category { get; set; }

    /// <summary>状态筛选（0 有效 / 1 已作废；留空 = 全部，默认包含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>关键字（匹配父单据号 / 显示名 / 引用标识 / 备注，长度上限 100；不做全表无界扫描）</summary>
    public string? Keyword { get; set; }
}

/// <summary>
/// 父单据候选（ERP-045，只读有界）：用于在界面上选择**既有**销售订单 / 采购订单 / 装柜清单 / 出口单证。
/// </summary>
public sealed record DocumentAttachmentReferenceParentOptionDto(
    string ParentType,
    string ParentTypeText,
    long ParentId,
    string ParentNo,
    string StatusText,
    string SummaryText,
    bool Selectable,
    string SelectableText);

/// <summary>
/// 附件引用台账 / 详情（ERP-045）。
/// <para><see cref="ReferenceAvailable"/> / <see cref="ReferenceUnavailableText"/> 是**只读安全标注**：
/// 历史或外部写入的不安全引用值不会作为链接 / 标记渲染，只显示不可用文本；
/// <see cref="ParentAvailable"/> / <see cref="ParentAvailabilityText"/> 说明父单据当前是否仍存在。</para>
/// <para>本 DTO 只描述**元数据引用**，<strong>不</strong>代表文件已经可下载、安全、真实或已获下载授权。</para>
/// </summary>
public sealed record DocumentAttachmentReferenceDto(
    long Id,
    string ParentType,
    string ParentTypeText,
    long ParentId,
    string ParentNo,
    string ParentSnapshotText,
    bool ParentAvailable,
    string ParentAvailabilityText,
    string Category,
    string CategoryText,
    string DisplayName,
    bool ReferenceAvailable,
    string ReferenceId,
    string ReferenceUnavailableText,
    string ContentType,
    long SizeBytes,
    string SizeText,
    string Checksum,
    string Notes,
    bool SourceAuthorizationAcknowledged,
    string SourceAuthorizationNote,
    string AuthorizedBy,
    DateTime AuthorizedAt,
    DateTime RegisteredAt,
    int Status,
    string StatusText,
    bool IsActive,
    bool IsVoided,
    DateTime? VoidedAt,
    string VoidReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string MetadataOnlyNoticeText,
    string BoundaryText);

/// <summary>
/// 附件引用模块元数据（ERP-045，只读）：白名单类型 / 分类、引用口径、大小口径与边界文案，
/// 供界面与接口同源显示，避免前端硬编码与后端校验口径漂移。
/// </summary>
public sealed record DocumentAttachmentReferenceMetadataDto(
    List<DocumentAttachmentReferenceOptionDto> ParentTypes,
    List<DocumentAttachmentReferenceOptionDto> Categories,
    List<DocumentAttachmentReferenceOptionDto> StatusOptions,
    long MaxSizeBytes,
    int MaxPageSize,
    int MaxPerParent,
    string SizePolicyText,
    string ReferencePolicyText,
    string AuthorizationPolicyText,
    string MetadataOnlyNoticeText,
    string BoundaryText);

/// <summary>白名单选项（ERP-045：值 + 中文文案）。</summary>
public sealed record DocumentAttachmentReferenceOptionDto(string Value, string Label);
