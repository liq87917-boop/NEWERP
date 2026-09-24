namespace ERP.Application.DTOs;

/// <summary>
/// 附件证据上传请求（ERP-061）。
/// <para>客户端只能提交：归属单据类型 + Id、文件名、声明的 Content-Type、有界说明与**内容流**；
/// 存储键、SHA-256 摘要、字节长度、媒体类型、归属单据号码 / 类型快照、上传人与登记时间、
/// 状态与审计字段一律由服务端权威生成，客户端提交同名值一律被忽略（DTO 里也没有这些字段）。</para>
/// <para>本请求<strong>不</strong>包含任何远端地址：服务端不会抓取 URL、不会访问生产对象存储，
/// 也不会把客户端路径当作存储位置。</para>
/// </summary>
public sealed class AttachmentEvidenceUploadRequest
{
    /// <summary>归属单据类型（白名单：SalesOrder / PurchaseOrder；留空或未知一律拒绝）</summary>
    public string OwnerType { get; set; } = string.Empty;

    /// <summary>归属单据 Id（必须指向存在且未删除的权威单据）</summary>
    public long OwnerId { get; set; }

    /// <summary>客户端文件名（只用于生成**净化后的快照**：路径一律忽略、不安全字符替换、长度有界）</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>声明的 Content-Type（必须与文件签名判定的类型一致：application/pdf / image/png / image/jpeg）</summary>
    public string DeclaredContentType { get; set; } = string.Empty;

    /// <summary>客户端声明的字节长度（只用于**提前**拒绝超限请求；落库长度一律以服务端实测为准）</summary>
    public long DeclaredLength { get; set; }

    /// <summary>证据说明（可选、有界；只作为纯文本标签显示）</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>内容流（只读一次；服务端有界读取，超过上限立即中止且不保存任何内容）</summary>
    public Stream? Content { get; set; }
}

/// <summary>作废附件证据请求（ERP-061）：必须显式填写原因，作废保留原始元数据与内容，不做硬删除。</summary>
public sealed class AttachmentEvidenceVoidRequest
{
    /// <summary>作废原因（必填、有界；用于解释更正原因，写入作废留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 附件证据台账查询参数（ERP-061，全部为可选过滤；结果分页返回，默认每页 50、单次最多 200 条）。
/// </summary>
public sealed class AttachmentEvidenceQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多条证据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>归属单据类型筛选（白名单取值；留空 = 全部类型）</summary>
    public string? OwnerType { get; set; }

    /// <summary>归属单据 Id 筛选（留空 = 全部单据；与类型配合使用时才精确到某一单据）</summary>
    public long? OwnerId { get; set; }

    /// <summary>状态筛选（0 有效 / 1 已作废；留空 = 全部，默认包含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>摘要筛选（8~64 位十六进制前缀 / 全值；用于按内容比对，**不是**证据身份，也不做去重）</summary>
    public string? Sha256 { get; set; }

    /// <summary>关键字（匹配归属单据号 / 原始文件名 / 说明，长度上限 100；不做全表无界扫描）</summary>
    public string? Keyword { get; set; }
}

/// <summary>
/// 可挂附件证据的单据候选（ERP-061，只读有界）：用于在界面上**显式选择**既有销售订单 / 采购订单。
/// </summary>
public sealed record AttachmentEvidenceOwnerOptionDto(
    string OwnerType,
    string OwnerTypeText,
    long OwnerId,
    string OwnerNo,
    string StatusText,
    int EvidenceCount,
    string SummaryText,
    bool Selectable,
    string SelectableText);

/// <summary>
/// 附件证据台账 / 详情（ERP-061）。
/// <para><see cref="OwnerAvailable"/> / <see cref="OwnerAvailabilityText"/> 说明归属单据当前是否仍存在；
/// <see cref="DownloadPath"/> 只给出 API 相对地址（<c>/api/attachment-evidences/{id}/content</c>），
/// <strong>不</strong>暴露存储键、文件系统路径或对象存储路径。</para>
/// <para>本 DTO 只描述用户上传的**仓库证据**：它不是报关 / 报税 / 银行 / 承运人 / 客户确认，
/// 也不代表任何业务单据已获批准、已付款、已出运或已结算。</para>
/// </summary>
public sealed record AttachmentEvidenceDto(
    long Id,
    string OwnerType,
    string OwnerTypeText,
    long OwnerId,
    string OwnerNo,
    string OwnerSnapshotText,
    bool OwnerAvailable,
    string OwnerAvailabilityText,
    string OriginalFileName,
    string MediaType,
    string MediaTypeText,
    long SizeBytes,
    string SizeText,
    string Sha256,
    string Description,
    string UploadedBy,
    DateTime RecordedAt,
    int Status,
    string StatusText,
    bool IsActive,
    bool IsVoided,
    bool ContentDownloadable,
    string DownloadAvailabilityText,
    string DownloadPath,
    DateTime? VoidedAt,
    string VoidReason,
    string StorageProvider,
    string StorageProviderText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText);

/// <summary>
/// 归属单据的附件证据**有界摘要**（ERP-062 建立；ERP-063 同样用于验货记录与样品记录，只读）：
/// 供列表页按当前页 Id **一次**批量取回「有几条仓库附件证据」。计数只表示已登记证据条数，
/// 既不触发任何存储访问，也不代表报关 / 报税 / 承运人 / 客户确认，更不代表验货合格、质量认证
/// 或样品已获批准（对应口径见 <see cref="AttachmentEvidenceMetadataDto"/> 中各类型边界文案）。
/// </summary>
public sealed record AttachmentEvidenceOwnerSummaryDto(
    string OwnerType,
    string OwnerTypeText,
    long OwnerId,
    string OwnerNo,
    string OwnerSnapshotText,
    bool OwnerAvailable,
    int TotalCount,
    int ActiveCount,
    int VoidedCount,
    bool HasActiveEvidence,
    string SummaryText,
    string BoundaryText);

/// <summary>附件证据内容读取结果（ERP-061，服务端内部使用：只交给控制器流式返回）。</summary>
public sealed record AttachmentEvidenceContentDto(
    Stream Content,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256);

/// <summary>
/// 附件证据模块元数据（ERP-061，只读；ERP-062 增补单证口径、ERP-063 增补验货记录与样品口径）：
/// 白名单归属类型、格式 / 大小口径、当前内容存储提供程序与边界文案，
/// 供界面与接口同源显示，避免前端硬编码与后端校验口径漂移。
/// </summary>
public sealed record AttachmentEvidenceMetadataDto(
    List<AttachmentEvidenceOptionDto> OwnerTypes,
    List<AttachmentEvidenceOptionDto> MediaTypes,
    List<AttachmentEvidenceOptionDto> StatusOptions,
    List<string> AllowedExtensions,
    long MaxSizeBytes,
    int MaxPageSize,
    int MaxPerOwner,
    int MaxOwnerOptions,
    int MaxSummaryOwnerIds,
    string SizePolicyText,
    string FormatPolicyText,
    string StorageProviderCode,
    string StorageProviderText,
    string StoragePolicyText,
    string DownloadPolicyText,
    string BoundaryText,
    string LegacyFileNotePolicyText,
    string TradeDocumentEvidenceBoundaryText,
    string QualityInspectionEvidenceBoundaryText,
    string SampleEvidenceBoundaryText);

/// <summary>白名单选项（ERP-061：值 + 中文文案）。</summary>
public sealed record AttachmentEvidenceOptionDto(string Value, string Label);

/// <summary>
/// 附件中心工作台查询参数（ERP-064，全部为可选过滤；结果分页返回，默认每页 50、单次最多 200 条）。
/// <para>本查询只使用**已持久化的显式元数据**：归属单据类型 / 归属单据 Id / 归属单据号码快照 /
/// 文件名快照 / 媒体类型 / 上传人 / 登记日期区间 / 状态。它**不**扫描文件内容、**不**抓取远端地址、
/// **不**做模糊跨记录匹配，也**绝不**因为文件名或摘要相同而合并记录。</para>
/// <para>结果一律按当前账号的既有「角色 → 菜单」授权收敛：未获菜单授权的归属类型既不返回记录，
/// 也不返回计数、文件名或摘要；显式传入未授权类型时按「无可见记录」返回空页（不披露任何存在性）。</para>
/// </summary>
public sealed class AttachmentEvidenceCenterQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多条证据元数据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>归属单据类型筛选（白名单取值；留空 = 全部**已授权**类型）</summary>
    public string? OwnerType { get; set; }

    /// <summary>归属单据 Id 筛选（留空 = 全部；必须为正整数）</summary>
    public long? OwnerId { get; set; }

    /// <summary>归属单据号码快照筛选（有界前缀 / 包含匹配，长度上限 50）</summary>
    public string? OwnerNo { get; set; }

    /// <summary>文件名快照筛选（有界包含匹配，长度上限 255；只匹配已持久化的净化文件名，不读取内容）</summary>
    public string? FileName { get; set; }

    /// <summary>媒体类型筛选（白名单取值：application/pdf / image/png / image/jpeg；留空 = 全部）</summary>
    public string? MediaType { get; set; }

    /// <summary>上传人筛选（有界包含匹配，长度上限 100）</summary>
    public string? UploadedBy { get; set; }

    /// <summary>登记日期区间起点（含；留空 = 不限）</summary>
    public DateTime? RecordedFrom { get; set; }

    /// <summary>登记日期区间终点（含；留空 = 不限；必须不早于起点）</summary>
    public DateTime? RecordedTo { get; set; }

    /// <summary>状态筛选（0 有效 / 1 已作废；留空 = 全部，默认包含已作废历史）</summary>
    public int? Status { get; set; }
}

/// <summary>
/// 附件中心工作台的归属类型授权明细（ERP-064，只读）：每个归属类型需要哪一个**既有菜单**授权、
/// 当前账号是否已授权。
/// <para>未授权类型只暴露「类型本身 + 所需菜单 + 未授权」这一事实（归属类型白名单本来就由
/// <c>GET /api/attachment-evidences/metadata</c> 公开），**不**暴露该类型的任何记录、计数、
/// 文件名、摘要或历史。</para>
/// </summary>
public sealed record AttachmentEvidenceCenterOwnerTypeScopeDto(
    string OwnerType,
    string OwnerTypeText,
    string RequiredMenuCode,
    string RequiredMenuText,
    bool Authorized,
    string AuthorizationText,
    string BoundaryText);

/// <summary>
/// 附件中心工作台的当前账号可见范围（ERP-064，只读）：按**既有**「角色 → 菜单」授权口径
/// （<c>SysUserRoles</c> → <c>SysRoleMenus</c> → <c>SysMenus.MenuCode</c>）推导当前账号可以访问哪些
/// 归属类型；无身份、无角色或无相关菜单授权时一律 fail closed（可见范围为空）。
/// </summary>
public sealed record AttachmentEvidenceCenterScopeDto(
    long UserId,
    string UserName,
    bool HasAnyAuthorizedOwnerType,
    List<AttachmentEvidenceCenterOwnerTypeScopeDto> OwnerTypes,
    string ScopeText,
    string UnauthorizedNoticeText,
    string ReadOnlyNoticeText,
    string UntrustedEvidenceNoticeText,
    string BoundaryText);

/// <summary>
/// 附件中心工作台在**授权范围内**的有界计数（ERP-064，只读）：只统计当前账号已获菜单授权的归属类型；
/// 未授权类型连计数行都不返回，因此摘要不会泄露不可访问父单据的存在性。
/// </summary>
public sealed record AttachmentEvidenceCenterOwnerTypeCountDto(
    string OwnerType,
    string OwnerTypeText,
    int TotalCount,
    int ActiveCount,
    int VoidedCount,
    string BoundaryText);

/// <summary>
/// 附件中心工作台摘要（ERP-064，只读、有界）：一次返回「当前账号可见范围 + 授权范围内计数 +
/// 筛选项白名单 + 全部口径文案」，供界面与接口同源显示，避免前端硬编码授权规则或口径漂移。
/// <para>摘要只做**计数**：不访问任何存储内容、不返回文件名 / 摘要 / 存储键，也不返回未授权类型的计数。</para>
/// </summary>
public sealed record AttachmentEvidenceCenterSummaryDto(
    AttachmentEvidenceCenterScopeDto Scope,
    int TotalCount,
    int ActiveCount,
    int VoidedCount,
    List<AttachmentEvidenceCenterOwnerTypeCountDto> OwnerTypeCounts,
    List<AttachmentEvidenceOptionDto> OwnerTypeOptions,
    List<AttachmentEvidenceOptionDto> MediaTypes,
    List<AttachmentEvidenceOptionDto> StatusOptions,
    int MaxPageSize,
    string SummaryText,
    string FilterPolicyText);
