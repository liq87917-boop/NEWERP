using ERP.Domain.Entities;

namespace ERP.Application.DTOs;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的数据契约：数据集 / 字段目录（有限、只读）与「可保存的报表定义」。
/// <para>目录只暴露当前账号已授权数据集的有限字段白名单；定义只描述「选择哪些字段 + 有界类型化筛选 +
/// 支持的分组 / 聚合 + 展示」，不含任意 SQL、脚本、用户自选表或联接语义。</para>
/// </summary>
public static class ReportConfigurationConstants
{
    // ==================== 字段类型（有限、与既有目录 DataType 对齐） ====================

    public const string TypeText = "text";
    public const string TypeNumber = "number";
    public const string TypeDate = "date";
    public const string TypeBoolean = "boolean";
    public const string TypeEnum = "enum";

    // ==================== 类型化筛选操作符（有限） ====================

    public const string OperatorEq = "eq";
    public const string OperatorNe = "ne";
    public const string OperatorIn = "in";
    public const string OperatorGt = "gt";
    public const string OperatorGte = "gte";
    public const string OperatorLt = "lt";
    public const string OperatorLte = "lte";
    public const string OperatorBetween = "between";

    // ==================== 分组键（有限） ====================

    public const string GroupNone = "none";
    public const string GroupCustomer = "customer";
    public const string GroupMonth = "month";

    // ==================== 聚合函数（有限） ====================

    public const string AggregateSum = "sum";
    public const string AggregateCount = "count";
    public const string AggregateAverage = "avg";
    public const string AggregateMin = "min";
    public const string AggregateMax = "max";

    // ==================== 能力标志（有限） ====================

    public const string CapabilityPreview = "preview";
    public const string CapabilityGrouping = "grouping";
    public const string CapabilityDateRange = "date-range";
    public const string CapabilityPaging = "paging";

    /// <summary>自定义公式：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityCustomFormula = "custom-formula";

    /// <summary>跨数据集联接：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityCrossDatasetJoin = "cross-dataset-join";

    /// <summary>透视（pivot）：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityPivot = "pivot";

    /// <summary>全匹配合计（all-match total）：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityAllMatchTotal = "all-match-total";

    /// <summary>预览页面覆盖口径：小计仅覆盖「当前预览页」，绝不声称全匹配合计</summary>
    public const string CoverageCurrentPage = "current-page";

    // ==================== 既有数据集键（适配器夹具，非数据库全量发现） ====================

    public const string DatasetSalesOrder = "sales-order";
    public const string DatasetReceivable = "receivable";
}

/// <summary>
/// 数据集字段目录项（有限、只读）：来自既有数据集适配器的白名单字段，附带真实的中文标签、类型、
/// 币种 / 单位语义、可筛选 / 可聚合能力与可见性。字段类型 / 操作符只允许 <see cref="ReportConfigurationConstants"/>
/// 中的有限取值。
/// </summary>
public sealed record ReportConfigurationFieldDto(
    string Key,
    string Label,
    string Type,
    string? CurrencyUnit,
    bool Filterable,
    bool Aggregatable,
    bool Hidden,
    IReadOnlyList<string> FilterOperators);

/// <summary>
/// 数据集目录（有限、只读）：当前账号已授权的单一数据集及其字段白名单、行粒度、币种 / 单位口径、
/// 允许分组键、支持 / 不支持能力与分页边界。字段与能力均为有限枚举，绝不扩大到数据库全量元数据发现。
/// </summary>
public sealed record ReportConfigurationDatasetDto(
    string DatasetKey,
    string Label,
    string Grain,
    string CurrencyUnitSemantics,
    string RequiredMenuCode,
    string RequiredMenuText,
    List<ReportConfigurationFieldDto> Fields,
    IReadOnlyList<string> GroupingKeys,
    IReadOnlyList<string> SupportedCapabilities,
    IReadOnlyList<string> UnsupportedCapabilities,
    int DefaultPageSize,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>通用报表配置目录（有限、只读）：当前账号可访问的全部已授权数据集。</summary>
public sealed record ReportConfigurationCatalogDto(
    int SchemaVersion,
    List<ReportConfigurationDatasetDto> Datasets);


/// <summary>
/// 可保存 / 执行的通用报表定义契约（有界）：schema 版本、数据集键、有序字段键、类型化筛选、
/// 支持的分组 / 聚合与展示。任何超出边界或引用未授权 / 不支持能力的定义在保存 / 执行前被纯校验器拒绝。
/// </summary>
public sealed class ReportConfigurationDefinition
{
    /// <summary>Schema 版本（只接受当前受支持版本）</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>数据集键（必须与已授权数据集匹配）</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>有序字段键（仅限数据集白名单，去重、保持顺序；1 ~ 32 个）</summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>类型化筛选（仅限数据集可筛选字段与有限操作符；0 ~ 32 个）</summary>
    public List<ReportConfigurationFilter> Filters { get; set; } = new();

    /// <summary>支持的分组键（仅限数据集允许分组键，去重；0 ~ 8 个）</summary>
    public List<string> Grouping { get; set; } = new();

    /// <summary>聚合定义（仅限可聚合字段与有限函数；0 ~ 4 个）</summary>
    public List<ReportConfigurationAggregate> Aggregates { get; set; } = new();

    /// <summary>请求的能力标志（仅限数据集支持能力；未支持能力显式拒绝）</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>展示 / 分页（可选；分页边界受数据集约束）</summary>
    public ReportConfigurationPresentation? Presentation { get; set; }
}

/// <summary>类型化筛选：字段键 + 有限操作符 + 与字段类型匹配的值。</summary>
public sealed class ReportConfigurationFilter
{
    /// <summary>筛选字段键（必须可筛选且未隐藏）</summary>
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>操作符（eq / ne / in / gt / gte / lt / lte / between）</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>筛选值（in 时传入数组；between 时为下界）</summary>
    public object? Value { get; set; }

    /// <summary>between 时的上界（其余操作符忽略）</summary>
    public object? Value2 { get; set; }
}

/// <summary>聚合定义：有限函数 + 可聚合字段。</summary>
public sealed class ReportConfigurationAggregate
{
    /// <summary>聚合函数（sum / count / avg / min / max）</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>聚合字段键（必须存在、未隐藏且与函数类型兼容）</summary>
    public string FieldKey { get; set; } = string.Empty;
}

/// <summary>展示 / 分页（可选；排序字段必须属于已选字段，分页受数据集上限约束）。</summary>
public sealed class ReportConfigurationPresentation
{
    /// <summary>报表标题（纯展示文本，不参与执行）</summary>
    public string? Title { get; set; }

    /// <summary>排序字段键（可选；必须属于 <see cref="ReportConfigurationDefinition.Fields"/>）</summary>
    public string? SortFieldKey { get; set; }

    /// <summary>排序方向（可选：asc / desc）</summary>
    public string? SortDirection { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int? Page { get; set; }

    /// <summary>每页条数（1 ~ 数据集上限）</summary>
    public int? PageSize { get; set; }
}


// ==================== ERP-260 Stage 1：私有报表配置服务契约 ====================

/// <summary>
/// 保存 / 更新私有报表配置的请求契约（客户端提交；所有者 Id 由服务端认证注入，客户端不得提交）。
/// </summary>
public sealed class ReportConfigurationSaveDto
{
    /// <summary>配置名称（1 ~ 200 字符）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>有界报表定义（保存前按当前账号已授权数据集目录校验）</summary>
    public ReportConfigurationDefinition? Definition { get; set; }
}

/// <summary>私有报表配置详情（加载 / 保存后返回；含已反序列化的定义与预期版本令牌）。</summary>
public sealed class ReportConfigurationDto
{
    /// <summary>配置 Id</summary>
    public long Id { get; set; }

    /// <summary>所有者用户 Id（服务端认证写入）</summary>
    public long OwnerUserId { get; set; }

    /// <summary>配置名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数据集键</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>schema 版本</summary>
    public int SchemaVersion { get; set; }

    /// <summary>生命周期状态（草稿 / 已发布）</summary>
    public ReportConfigurationStatus Status { get; set; }

    /// <summary>当前预期版本令牌（后续更新 / 发布 / 恢复 / 删除需原样回传）</summary>
    public int Version { get; set; }

    /// <summary>最近一次已发布修订版本号（0 = 从未发布）</summary>
    public int CurrentPublishedVersion { get; set; }

    /// <summary>已反序列化的有界定义（与保存 JSON 一致）</summary>
    public ReportConfigurationDefinition? Definition { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>更新时间</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>私有报表配置列表项（owner-only；不含定义正文，避免列表把定义整体拉回）。</summary>
public sealed class ReportConfigurationSummaryDto
{
    /// <summary>配置 Id</summary>
    public long Id { get; set; }

    /// <summary>配置名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数据集键</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>生命周期状态（草稿 / 已发布）</summary>
    public ReportConfigurationStatus Status { get; set; }

    /// <summary>当前预期版本令牌</summary>
    public int Version { get; set; }

    /// <summary>最近一次已发布修订版本号（0 = 从未发布）</summary>
    public int CurrentPublishedVersion { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>更新时间</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>私有报表配置发布修订项（不可变快照；含已反序列化的定义）。</summary>
public sealed class ReportConfigurationRevisionDto
{
    /// <summary>修订 Id</summary>
    public long Id { get; set; }

    /// <summary>所属配置 Id</summary>
    public long ReportConfigurationId { get; set; }

    /// <summary>发布版本号（同一配置内单调递增）</summary>
    public int Version { get; set; }

    /// <summary>发布时的配置名称快照</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>发布时的数据集键快照</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>schema 版本快照</summary>
    public int SchemaVersion { get; set; }

    /// <summary>发布时间</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>发布人 Id</summary>
    public long PublishedBy { get; set; }

    /// <summary>已反序列化的被固定定义快照</summary>
    public ReportConfigurationDefinition? Definition { get; set; }
}

// ==================== ERP-261 Stage 1：通用执行 / 预览契约 ====================

/// <summary>
/// 私有报表配置预览请求（客户端提交；所有者 Id 由服务端认证注入）。
/// <para><see cref="RevisionVersion"/> 为空 = 预览当前草稿定义；非空 = 预览指定的不可变发布修订快照。</para>
/// </summary>
public sealed class ReportConfigurationPreviewRequest
{
    /// <summary>要预览的私有报表配置 Id</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布版本号（可选；空 = 当前草稿定义）</summary>
    public int? RevisionVersion { get; set; }

    /// <summary>页码覆盖（可选；空 = 使用已保存展示分页 / 数据集默认）</summary>
    public int? Page { get; set; }

    /// <summary>每页条数覆盖（可选；空 = 使用已保存展示分页 / 数据集默认；受数据集上限约束）</summary>
    public int? PageSize { get; set; }

    /// <summary>分组键覆盖（可选；空 = 使用已保存分组；仅 none / customer / month）</summary>
    public string? GroupBy { get; set; }
}

/// <summary>经执行服务解析、校验后的预览参数（有界、只读）</summary>
public sealed record ReportConfigurationPreviewParameters(
    int Page,
    int PageSize,
    string GroupBy);

/// <summary>通用报表列（有界、只读）：有限字段键 + 真实中文标签 / 类型 / 币种单位语义</summary>
public sealed record ReportConfigurationColumnDto(
    string Key,
    string Label,
    string Type,
    string? CurrencyUnit);

/// <summary>分组页面小计的「币种分区」：金额只对同币种求和，绝不跨币种换算或相加</summary>
public sealed record ReportConfigurationCurrencyPartitionDto(
    string Currency,
    int Count,
    decimal? Amount,
    decimal? GrossAmount,
    decimal? EffectiveAllocatedAmount,
    decimal? RemainingAmount,
    string RemainingState);

/// <summary>分组页面小计（仅当前预览页，非全量合计；组内按币种分区）</summary>
public sealed record ReportConfigurationGroupSubtotalDto(
    string Key,
    string Label,
    IReadOnlyList<ReportConfigurationCurrencyPartitionDto> Partitions);

/// <summary>
/// 预览证据上下文：数据集键 / 行粒度 / 币种单位口径 / 只读与边界文案 / 页面覆盖口径。
/// <para>页面小计只来自「同一批有界、已授权预览行」，<see cref="Coverage"/> 明确标注覆盖范围。</para>
/// </summary>
public sealed record ReportConfigurationEvidenceContextDto(
    string DatasetKey,
    string Grain,
    string CurrencyUnitSemantics,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string Coverage);

/// <summary>通用报表配置预览结果（有界、只读）</summary>
public sealed class ReportConfigurationPreviewDto
{
    /// <summary>所属私有报表配置 Id</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布版本号（仅当 <see cref="IsPinnedRevision"/> 为 true 时非空）</summary>
    public int? PinnedRevisionVersion { get; set; }

    /// <summary>是否为固定发布修订预览（false = 当前草稿）</summary>
    public bool IsPinnedRevision { get; set; }

    /// <summary>数据集键</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>通用类型化列（按选定字段顺序）</summary>
    public List<ReportConfigurationColumnDto> Columns { get; set; } = new();

    /// <summary>当前预览页行（仅含选定字段值）</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>总命中条数（分页前）</summary>
    public int Total { get; set; }

    /// <summary>当前页码</summary>
    public int Page { get; set; }

    /// <summary>每页条数</summary>
    public int PageSize { get; set; }

    /// <summary>总页数</summary>
    public int TotalPages { get; set; }

    /// <summary>生效分组键（none / customer / month）</summary>
    public string GroupBy { get; set; } = ReportConfigurationConstants.GroupNone;

    /// <summary>当前预览页分组小计（分组时才非空；组内按币种分区，绝不跨币种相加）</summary>
    public List<ReportConfigurationGroupSubtotalDto>? Groups { get; set; }

    /// <summary>证据上下文（数据集键 / 粒度 / 币种单位口径 / 只读 / 覆盖口径）</summary>
    public ReportConfigurationEvidenceContextDto? Evidence { get; set; }
}

/// <summary>重命名私有报表配置请求契约</summary>
public sealed class ReportConfigurationRenameDto
{
    /// <summary>新名称（1 ~ 200 字符）</summary>
    public string Name { get; set; } = string.Empty;
}
