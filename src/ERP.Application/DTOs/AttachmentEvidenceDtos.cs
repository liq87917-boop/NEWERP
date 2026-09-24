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
/// 归属单据的附件证据**有界摘要**（ERP-062，只读）：供列表页按当前页 Id **一次**批量取回
/// 「有几条仓库附件证据」。计数只表示已登记证据条数，既不触发任何存储访问，也不代表
/// 报关 / 报税 / 承运人 / 客户确认。
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
/// 附件证据模块元数据（ERP-061，只读）：白名单归属类型、格式 / 大小口径、当前内容存储提供程序与边界文案，
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
    string TradeDocumentEvidenceBoundaryText);

/// <summary>白名单选项（ERP-061：值 + 中文文案）。</summary>
public sealed record AttachmentEvidenceOptionDto(string Value, string Label);
