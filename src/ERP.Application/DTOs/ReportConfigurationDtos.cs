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
