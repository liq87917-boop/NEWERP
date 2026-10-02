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

    // ==================== 分组维度语义（有限） ====================

    /// <summary>恒等维度语义：按稳定主键原样分组（如客户 Id）。</summary>
    public const string GroupingSemanticsIdentity = "identity";

    /// <summary>日历月维度语义：按日期字段的「年-月」归并为自然日历月。</summary>
    public const string GroupingSemanticsCalendarMonth = "calendar-month";

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

    /// <summary>受限计算列（ERP-266）：仅支持已授权数值字段引用 / 数值字面量 / + - * / 的有界算术 AST；无任意公式 / SQL / 脚本</summary>
    public const string CapabilityComputedColumns = "computed-columns";

    /// <summary>自定义公式：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityCustomFormula = "custom-formula";

    /// <summary>跨数据集联接：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityCrossDatasetJoin = "cross-dataset-join";

    /// <summary>透视（pivot）：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityPivot = "pivot";

    /// <summary>全匹配合计（all-match total）：Stage 1 明确不支持（不提供惰性成功）</summary>
    public const string CapabilityAllMatchTotal = "all-match-total";

    /// <summary>有界匹配集预览能力（ERP-273）：一致快照读取选定匹配事实页；全量合计仍不支持。</summary>
    public const string CapabilityMatchedSet = "matched-set";

    /// <summary>预览页面覆盖口径：小计仅覆盖「当前预览页」，绝不声称全匹配合计</summary>
    public const string CoverageCurrentPage = "current-page";

    /// <summary>预览覆盖口径：有界匹配集（ERP-273，≤1000 条一致快照；非全量合计）</summary>
    public const string CoverageMatchedSet = "matched-set";

    // ==================== 既有数据集键（适配器夹具，非数据库全量发现） ====================

    public const string DatasetSalesOrder = "sales-order";
    public const string DatasetReceivable = "receivable";

    // ==================== 受控关系（ERP-268：客户维度） ====================

    /// <summary>客户维度关系键（稳定、受控；唯一允许的关系键）</summary>
    public const string RelationCustomer = "customer";

    /// <summary>关系基数：多对一（有限；拒绝任意 / 一对多 / 事实联接）</summary>
    public const string CardinalityManyToOne = "many-to-one";
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
    IReadOnlyList<string> FilterOperators)
{
    /// <summary>是否可作为保存排序的有限持久化字段（仅原生持久化键；计算列 / 关系 / 派生金额字段不可排序）。</summary>
    public bool Sortable { get; init; }

    /// <summary>不可排序时的人类可读说明（计算列 / 关系 / 派生金额字段）。</summary>
    public string? SortUnavailableReason { get; init; }
}

/// <summary>
/// 指标描述符（有限、只读）：从已授权字段派生，携带稳定的字段键 / 中文标签 / 类型 / 行粒度 /
/// 单位 / 币种行为 / 允许的聚合函数与显式的当前页覆盖口径。字段 / 函数 / 覆盖取值只允许
/// <see cref="ReportConfigurationConstants"/> 中的有限取值。
/// </summary>
public sealed record ReportConfigurationMetricDto(
    string Key,
    string Label,
    string Type,
    string Grain,
    string? Unit,
    string CurrencyBehavior,
    IReadOnlyList<string> AllowedFunctions,
    string Coverage);

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
    string BoundaryText)
{
    /// <summary>由已授权字段派生的指标描述符（粒度 / 单位 / 币种行为 / 允许函数 / 当前页覆盖口径）。</summary>
    public List<ReportConfigurationMetricDto> Metrics { get; init; } = new();

    /// <summary>
    /// 分组维度目录（有限、只读、有序）：描述每个可用基础分组维度的稳定键 / 中文标签 / 底层授权必需字段 / 类型与语义。
    /// <para>ERP-271：同一报表可最多按两个有区别的基础维度分组（初始 customer / month 任意顺序）；空列表回退为
    /// 按 <see cref="GroupingKeys"/> 与 GroupCustomerFieldKey / GroupMonthFieldKey 的旧契约派生。</para>
    /// </summary>
    public List<ReportConfigurationGroupingDimensionDto> GroupingDimensions { get; init; } = new();

    /// <summary>按客户分组使用的底层字段键（销售订单 / 应收账款均为 customerId）。</summary>
    public string? GroupCustomerFieldKey { get; init; }

    /// <summary>按月份分组使用的底层日期字段键（销售订单 orderDate / 应收账款 invoiceDate）。</summary>
    public string? GroupMonthFieldKey { get; init; }

    /// <summary>受控关系目录（ERP-268）：稳定关系元数据与允许字段清单；空表示该数据集不暴露任何关系。</summary>
    public List<ReportConfigurationRelationDto> Relations { get; init; } = new();

    /// <summary>排序口径说明：列出有限可排序字段，并说明计算列 / 关系 / 派生金额字段不可排序的原因。</summary>
    public string? SortingExplanation { get; init; }
}

/// <summary>
/// 分组维度目录项（有限、只读）：一个可用于分组的基础维度的稳定键 / 中文标签 / 底层授权必需字段 / 类型与语义。
/// <para>ERP-271：语义取值仅限 <see cref="ReportConfigurationConstants"/> 中的 identity / calendar-month；
/// 底层字段必须是已授权、未隐藏的基础字段；相关 / 计算 / 未授权字段一律不作为分组维度。</para>
/// </summary>
public sealed record ReportConfigurationGroupingDimensionDto(
    string Key,
    string Label,
    string FieldKey,
    string FieldType,
    string Semantics);

/// <summary>
/// 分组维度值（有限、只读）：某个事实行在某分组维度上的稳定类型化取值。
/// <para><see cref="Value"/> 为该维度的稳定规范串（客户 = 主键十进制字符串；月份 = yyyy-MM）；
/// <see cref="IsUnknown"/> 为 true 表示该维度缺失 / 非法，形成显式「未知」桶，绝不并入其它维度。</para>
/// </summary>
public sealed record ReportConfigurationGroupDimensionValueDto(
    string Key,
    string Label,
    string? Value,
    bool IsUnknown);

/// <summary>
/// 受控关系字段（有限、只读）：目标维度允许选择的有限文本字段（仅客户编码 / 国别），
/// 绝不暴露地址 / 联系人 / 银行 / 隐私字段。
/// </summary>
public sealed record ReportConfigurationRelationFieldDto(
    string Key,
    string Label,
    string Type);

/// <summary>
/// 受控关系目录项（有限、只读）：稳定关系键、来源事实粒度 / 键、目标维度唯一键、多对一基数、
/// 目标权限、标签 / 类型、允许字段清单与缺失 / 删除语义。全部为服务端静态白名单，绝不来自客户端。
/// </summary>
public sealed record ReportConfigurationRelationDto(
    string Key,
    string Label,
    string SourceFactGrain,
    string SourceFactKey,
    string TargetDimensionUniqueKey,
    string Cardinality,
    string RequiredMenuCode,
    string RequiredMenuText,
    IReadOnlyList<ReportConfigurationRelationFieldDto> Fields,
    string MissingDeletedSemantics);

/// <summary>用户选定的关系（关系键 + 有限字段；字段只允许目标维度白名单内的文本字段）。</summary>
public sealed class ReportConfigurationRelationSelection
{
    /// <summary>关系键（必须与目录关系键一致）</summary>
    public string RelationKey { get; set; } = string.Empty;

    /// <summary>选定的目标维度字段键（有限、去重、保持顺序）</summary>
    public List<string> Fields { get; set; } = new();
}

/// <summary>关系证据（有界、只读）：关系键 / 字段 / 状态汇总，绝不包含客户姓名 / 编码等隐私值。</summary>
public sealed class ReportConfigurationRelationEvidenceDto
{
    /// <summary>关系键</summary>
    public string RelationKey { get; set; } = string.Empty;

    /// <summary>选定的字段键</summary>
    public IReadOnlyList<string> Fields { get; set; } = new List<string>();

    /// <summary>状态汇总（resolved / missing / deleted / forbidden 计数，不含隐私值）</summary>
    public string StatusSummary { get; set; } = string.Empty;
}

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

    /// <summary>受限计算列（ERP-266）：结构化算术 AST，最多 8 列；旧定义不含此属性时可正常反序列化（向后兼容）</summary>
    public List<ReportConfigurationComputedColumn> ComputedColumns { get; set; } = new();

    /// <summary>请求的能力标志（仅限数据集支持能力；未支持能力显式拒绝）</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>展示 / 分页（可选；分页边界受数据集约束）</summary>
    public ReportConfigurationPresentation? Presentation { get; set; }

    /// <summary>受控关系选择（ERP-268）：关系键 + 有限字段；旧定义不含此属性时向后兼容为空。</summary>
    public List<ReportConfigurationRelationSelection> Relations { get; set; } = new();

    /// <summary>
    /// 有界透视定义（ERP-272，可选；旧定义不含此属性时向后兼容为 null = 非透视）。
    /// <para>只选择「一个行维度 + 一个不同的列维度」，并复用 <see cref="Aggregates"/> 作为选中的基础指标；</para>
    /// <para>绝不承载任意公式 / SQL / 脚本 / 跨事实联接 / 全匹配合计。</para>
    /// </summary>
    public ReportConfigurationPivotDefinition? Pivot { get; set; }

    /// <summary>
    /// 预览覆盖口径（ERP-273，可选；旧定义缺省为 current-page）：current-page = 当前预览页（默认），
    /// matched-set = 有界一致匹配集（≤1000 条一致快照；仍展示选中页，绝不追加全匹配合计）。
    /// </summary>
    public string Coverage { get; set; } = ReportConfigurationConstants.CoverageCurrentPage;
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

/// <summary>
/// 受限计算列（ERP-266）：可复用的行级数值计算列。表达式为结构化算术 AST，
/// 仅允许已授权数值字段引用、数值字面量与 + - * / 四种二元运算；绝不承载任意公式 / SQL / 脚本 / eval。
/// </summary>
public sealed class ReportConfigurationComputedColumn
{
    /// <summary>计算列键（唯一；不得与数据集基础字段键冲突，不得引用其它计算列）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>展示标签（纯展示）</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>算术表达式 AST 根节点</summary>
    public ReportConfigurationFormulaNode Expression { get; set; } = new();
}

/// <summary>
/// 计算列算术 AST 节点（有界）：Kind 仅限 field / literal / add / subtract / multiply / divide。
/// <para>field 节点仅带 <see cref="FieldKey"/>；literal 节点仅带 <see cref="Literal"/>；
/// 二元节点仅带 <see cref="Left"/> / <see cref="Right"/>；其它组合在纯校验器中被拒绝。</para>
/// </summary>
public sealed class ReportConfigurationFormulaNode
{
    /// <summary>节点种类：field / literal / add / subtract / multiply / divide</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>字段引用键（仅 field 节点；必须为已授权、未隐藏、数值类型的基础字段）</summary>
    public string? FieldKey { get; set; }

    /// <summary>数值字面量（仅 literal 节点；绝对值 ≤ 1e12）</summary>
    public decimal? Literal { get; set; }

    /// <summary>左子树（仅二元节点）</summary>
    public ReportConfigurationFormulaNode? Left { get; set; }

    /// <summary>右子树（仅二元节点）</summary>
    public ReportConfigurationFormulaNode? Right { get; set; }
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

    /// <summary>分组键覆盖（可选；空 = 使用已保存分组；仅 none / customer / month；旧单分组契约）</summary>
    public string? GroupBy { get; set; }

    /// <summary>
    /// 有序复合分组维度覆盖（ERP-271，可选；空 = 使用已保存分组 / 旧 GroupBy）。
    /// <para>与 <see cref="GroupBy"/> 同时提供时显式拒绝（冲突覆盖）；最多两个有区别的基础维度，保持提交顺序。</para>
    /// </summary>
    public List<string>? Groupings { get; set; }
}

/// <summary>经执行服务解析、校验后的预览参数（有界、只读）</summary>
public sealed record ReportConfigurationPreviewParameters(
    int Page,
    int PageSize,
    string GroupBy,
    string? SortFieldKey,
    string? SortDirection)
{
    /// <summary>
    /// 有序有效分组维度（ERP-271）：0 = 不分组，1 = 旧单分组（<see cref="GroupBy"/>），2 = 复合分组。
    /// <para>复合分组时 <see cref="GroupBy"/> 保持 none（适配器读取未分组源页 + 授权隐藏依赖），由通用引擎负责分组。</para>
    /// </summary>
    public IReadOnlyList<string> Groupings { get; init; } = new List<string>();
}

/// <summary>通用报表列（有界、只读）：有限字段键 + 真实中文标签 / 类型 / 币种单位语义；计算列附未知值口径说明</summary>
public sealed record ReportConfigurationColumnDto(
    string Key,
    string Label,
    string Type,
    string? CurrencyUnit,
    string? UnknownReason = null,
    bool IsComputed = false);

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
    IReadOnlyList<ReportConfigurationCurrencyPartitionDto> Partitions)
{
    /// <summary>
    /// 有序分组维度值（ERP-271）：复合分组时按保存顺序携带每个维度的键 / 标签 / 稳定值 / 是否未知；
    /// 旧单分组与不分组时为空列表（沿用旧 Key / Label 契约）。
    /// </summary>
    public IReadOnlyList<ReportConfigurationGroupDimensionValueDto> Dimensions { get; init; } = new List<ReportConfigurationGroupDimensionValueDto>();
}

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

/// <summary>计算列证据（有界、只读）：单元 / 未知值口径与依赖字段，供 UI / Excel / PDF 共用同一份口径。</summary>
public sealed class ReportConfigurationComputedColumnEvidenceDto
{
    /// <summary>计算列键</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>展示标签</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>推导出的单位（原币金额 / % / 无量纲）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>未知值口径（null 的显式有界原因说明，绝不静默置零）</summary>
    public string UnknownReason { get; set; } = string.Empty;

    /// <summary>依赖的基础字段键（已授权、未隐藏、数值类型；即使未选展示也会在源读取时获取）</summary>
    public IReadOnlyList<string> Dependencies { get; set; } = new List<string>();
}

/// <summary>选中指标的单个汇总单元格（当前页：分组 × 币种；未知币种单独隔离并附原因）。</summary>
public sealed class ReportConfigurationMetricCellDto
{
    /// <summary>分组键（不分组时为空）</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>分组中文标签（不分组时为「全部」）</summary>
    public string GroupLabel { get; set; } = string.Empty;

    /// <summary>
    /// 有序分组维度值（ERP-271）：复合分组时按保存顺序携带每个维度的键 / 标签 / 稳定值 / 是否未知；
    /// 旧单分组与不分组时为空列表（沿用旧 GroupKey / GroupLabel 契约）。
    /// </summary>
    public List<ReportConfigurationGroupDimensionValueDto> Dimensions { get; set; } = new();

    /// <summary>币种（非货币指标为空；未知币种单独隔离）</summary>
    public string? Currency { get; set; }

    /// <summary>指标值（sum / count / avg / min / max；空 / 全 null / 溢出为 null，绝不静默置零）</summary>
    public decimal? Value { get; set; }

    /// <summary>已知值条数（参与计算的非 null 值条数；count = 该值）</summary>
    public int KnownCount { get; set; }

    /// <summary>缺失值条数（null / 缺失）</summary>
    public int MissingCount { get; set; }

    /// <summary>来源条数（当前页该分组 × 币种的总行数 = 已知 + 缺失）</summary>
    public int SourceCount { get; set; }

    /// <summary>显式原因（未知币种 / 数值溢出等）；无异常为 null</summary>
    public string? Reason { get; set; }
}

/// <summary>选中指标的汇总结果（仅用户已选择的聚合；金额按币种分区，绝不跨币种 / 单位合并）。</summary>
public sealed class ReportConfigurationMetricResultDto
{
    /// <summary>字段键（稳定）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>聚合函数（sum / count / avg / min / max）</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>字段中文标签</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>单位（原币金额 / % / 空）</summary>
    public string? Unit { get; set; }

    /// <summary>币种行为（currency-partition / none）</summary>
    public string CurrencyBehavior { get; set; } = string.Empty;

    /// <summary>当前页分组 × 币种汇总单元格</summary>
    public List<ReportConfigurationMetricCellDto> Cells { get; set; } = new();
}

/// <summary>通用报表配置预览结果（有界、只读）</summary>
public sealed class ReportConfigurationPreviewDto
{
    /// <summary>所属私有报表配置 Id</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布版本号（仅当 <see cref="IsPinnedRevision"/> 为 true 时非空）</summary>
    public int? PinnedRevisionVersion { get; set; }

    /// <summary>是否为固定发布修订预览（false = 当前草稿）</summary>
    public bool IsPinnedRevision { get; set; }

    /// <summary>当前预览定义的名称（草稿 = 配置名称；固定发布修订 = 修订名称快照）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>当前预览定义的版本标识（草稿 = 配置预期版本令牌；固定发布修订 = 修订版本号）</summary>
    public int Version { get; set; }

    /// <summary>规范化查询筛选文本（字段标签 + 有限操作符 + 值；无筛选为空字符串）</summary>
    public string NormalizedFiltersText { get; set; } = string.Empty;

    /// <summary>规范化日期范围文本（仅日期筛选字段；无日期筛选为空字符串）</summary>
    public string DateRangeText { get; set; } = string.Empty;

    /// <summary>生效排序字段键（保存的有限持久化排序字段；无排序时为空）</summary>
    public string? SortFieldKey { get; set; }

    /// <summary>生效排序方向（asc / desc；无排序时为空）</summary>
    public string? SortDirection { get; set; }

    /// <summary>规范化排序证据文本（含默认排序与并列身份决断；供 UI / Excel / PDF 共用同一口径）</summary>
    public string SortEvidence { get; set; } = string.Empty;

    /// <summary>数据集键</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>通用类型化列（按选定字段顺序）</summary>
    public List<ReportConfigurationColumnDto> Columns { get; set; } = new();

    /// <summary>当前预览页行（仅含选定字段值）</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>总命中条数（分页前）</summary>
    public int Total { get; set; }

    /// <summary>有界匹配集命中条数（ERP-273；current-page 模式与 <see cref="Total"/> 同值，matched-set 模式 = 一致快照命中数）</summary>
    public int MatchedCount { get; set; }

    /// <summary>当前页码</summary>
    public int Page { get; set; }

    /// <summary>每页条数</summary>
    public int PageSize { get; set; }

    /// <summary>总页数</summary>
    public int TotalPages { get; set; }

    /// <summary>生效分组键（none / customer / month；旧单分组契约，复合分组时为 none）</summary>
    public string GroupBy { get; set; } = ReportConfigurationConstants.GroupNone;

    /// <summary>
    /// 有序生效分组维度（ERP-271）：0 = 不分组，1 = 旧单分组，2 = 复合分组；
    /// 供工作台 / Excel / PDF 按保存顺序渲染全部复合维度。
    /// </summary>
    public List<string> Groupings { get; set; } = new();

    /// <summary>当前预览页分组小计（分组时才非空；组内按币种分区，绝不跨币种相加）</summary>
    public List<ReportConfigurationGroupSubtotalDto>? Groups { get; set; }

    /// <summary>选中的指标汇总（仅用户已选择的聚合；按当前页计算，分组 + 币种分区，绝不追加全匹配合计）</summary>
    public List<ReportConfigurationMetricResultDto> Metrics { get; set; } = new();

    /// <summary>有界透视结果（ERP-272，仅透视定义时非空；与普通行 / 指标汇总分离存储，绝不混入全匹配合计）</summary>
    public ReportConfigurationPivotResultDto? Pivot { get; set; }

    /// <summary>计算列证据（有界：键 / 标签 / 单位 / 未知值口径 / 依赖）；无计算列为空</summary>
    public List<ReportConfigurationComputedColumnEvidenceDto> ComputedColumns { get; set; } = new();

    /// <summary>计算列单元格未知值原因（与 <see cref="Rows"/> 平行；仅计算列 null 单元格携带有界原因）</summary>
    public List<Dictionary<string, string?>> CellReasons { get; set; } = new();

    /// <summary>关系证据（ERP-268）：关系键 / 字段 / 状态汇总；无关系选择时为空。</summary>
    public List<ReportConfigurationRelationEvidenceDto> RelationEvidence { get; set; } = new();

    /// <summary>证据上下文（数据集键 / 粒度 / 币种单位口径 / 只读 / 覆盖口径）</summary>
    public ReportConfigurationEvidenceContextDto? Evidence { get; set; }
}

// ==================== ERP-272 Stage 1：有界透视契约 ====================

/// <summary>
/// 有界透视定义（schema 版本化，与 <see cref="ReportConfigurationDefinition"/> 一同持久化）：
/// 一个授权行维度 + 一个不同的授权列维度；选中指标复用 <see cref="ReportConfigurationDefinition.Aggregates"/>。
/// <para>维度仅限数据集授权分组维度（初始 customer / month）；指标仅限已授权可聚合字段 + 有限函数，最多 4 个。</para>
/// </summary>
public sealed class ReportConfigurationPivotDefinition
{
    /// <summary>透视 schema 版本（只接受当前受支持版本）</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>行维度键（必须与列维度不同；仅限数据集授权分组维度）</summary>
    public string RowDimension { get; set; } = string.Empty;

    /// <summary>列维度键（必须与行维度不同；仅限数据集授权分组维度）</summary>
    public string ColumnDimension { get; set; } = string.Empty;
}

/// <summary>透视轴桶（行 / 列维度的一个稳定类型化取值；未知桶显式，绝不并入其它维度）。</summary>
public sealed class ReportConfigurationPivotAxisDto
{
    /// <summary>类型化轴键（长度前缀复合键，避免碰撞 / 注入）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>展示标签（转义由渲染层负责）</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>确定性排序键（未知桶置后）</summary>
    public string SortKey { get; set; } = string.Empty;

    /// <summary>有序维度值（单维度：仅一个元素，携带键 / 标签 / 稳定值 / 是否未知）</summary>
    public List<ReportConfigurationGroupDimensionValueDto> Dimensions { get; set; } = new();

    /// <summary>是否「全未知」桶（维度值缺失 / 非法）</summary>
    public bool IsUnknown { get; set; }
}

/// <summary>透视单元格（稀疏：空交叉点不产生单元格 = 无事实，null 聚合值 + 已知计数 0）。</summary>
public sealed class ReportConfigurationPivotCellDto
{
    /// <summary>行轴索引</summary>
    public int RowIndex { get; set; }

    /// <summary>列轴索引</summary>
    public int ColumnIndex { get; set; }

    /// <summary>币种（货币指标按币种分区；非货币指标为空）</summary>
    public string? Currency { get; set; }

    /// <summary>指标值（sum / count / avg / min / max；空 / 全 null / 溢出为 null，绝不静默置零）</summary>
    public decimal? Value { get; set; }

    /// <summary>已知值条数（参与计算的非 null 值条数；count = 该值）</summary>
    public int KnownCount { get; set; }

    /// <summary>缺失值条数（null / 缺失）</summary>
    public int MissingCount { get; set; }

    /// <summary>来源条数（该交叉点 × 币种的总行数 = 已知 + 缺失）</summary>
    public int SourceCount { get; set; }

    /// <summary>显式原因（未知币种 / 数值溢出等）；无异常为 null</summary>
    public string? Reason { get; set; }
}

/// <summary>透视单指标矩阵（当前页：行 × 列 × 币种分区；稀疏单元格列表）。</summary>
public sealed class ReportConfigurationPivotMetricDto
{
    /// <summary>字段键（稳定）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>聚合函数（sum / count / avg / min / max）</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>字段中文标签</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>单位（原币金额 / % / 空）</summary>
    public string? Unit { get; set; }

    /// <summary>币种行为（currency-partition / none）</summary>
    public string CurrencyBehavior { get; set; } = string.Empty;

    /// <summary>稀疏单元格（仅存在事实的交叉点；空交叉点 = 无事实，绝不造 0 / 全量合计）</summary>
    public List<ReportConfigurationPivotCellDto> Cells { get; set; } = new();

    /// <summary>该指标全部单元格已知值总条数</summary>
    public int KnownCount { get; set; }

    /// <summary>该指标全部单元格缺失值总条数</summary>
    public int MissingCount { get; set; }

    /// <summary>该指标全部单元格来源总条数</summary>
    public int SourceCount { get; set; }
}

/// <summary>
/// 有界透视结果（仅当前预览页；绝不声称全匹配合计 / 全局报表合计）。
/// <para>行 / 列轴有序确定性；单元格按币种分区，绝不跨币种 / 单位合并；空交叉点无单元格。</para>
/// </summary>
public sealed class ReportConfigurationPivotResultDto
{
    /// <summary>行维度键</summary>
    public string RowDimension { get; set; } = string.Empty;

    /// <summary>列维度键</summary>
    public string ColumnDimension { get; set; } = string.Empty;

    /// <summary>行轴（按排序键升序）</summary>
    public List<ReportConfigurationPivotAxisDto> RowAxis { get; set; } = new();

    /// <summary>列轴（按排序键升序）</summary>
    public List<ReportConfigurationPivotAxisDto> ColumnAxis { get; set; } = new();

    /// <summary>选中指标矩阵（每个指标一个稀疏矩阵）</summary>
    public List<ReportConfigurationPivotMetricDto> Metrics { get; set; } = new();

    /// <summary>参与透视的当前页来源事实行数（与分页命中总数分离，绝不声称全局合计）</summary>
    public int SourceRowCount { get; set; }

    /// <summary>覆盖口径（显式 current-page）</summary>
    public string Coverage { get; set; } = ReportConfigurationConstants.CoverageCurrentPage;
}

/// <summary>重命名私有报表配置请求契约</summary>
public sealed class ReportConfigurationRenameDto
{
    /// <summary>新名称（1 ~ 200 字符）</summary>
    public string Name { get; set; } = string.Empty;
}

// ==================== ERP-265 Stage 1：只读共享授权契约 ====================

/// <summary>
/// 所有者授权 / 变更固定修订的请求契约（客户端提交；所有者 Id 由服务端认证注入，客户端不得提交）。
/// <para>只接受被授权人用户 Id（不提供用户目录检索），并固定一个不可变发布修订版本号。</para>
/// </summary>
public sealed class ReportConfigurationGrantRequestDto
{
    /// <summary>被授权人用户 Id（现有激活 ERP 用户）</summary>
    public long RecipientUserId { get; set; }

    /// <summary>要固定共享的不可变发布修订版本号</summary>
    public int RevisionVersion { get; set; }

    /// <summary>已存在有效授权时的预期版本令牌（新建授权留空；变更固定修订时必填，防止陈旧写入）</summary>
    public int? ExpectedVersion { get; set; }
}

/// <summary>只读共享授权项（所有者管理视图；含被授权人显示信息与预期版本令牌）。</summary>
public sealed class ReportConfigurationGrantDto
{
    /// <summary>授权 Id</summary>
    public long Id { get; set; }

    /// <summary>被授权的私有报表配置 Id</summary>
    public long ReportConfigurationId { get; set; }

    /// <summary>被授权人用户 Id</summary>
    public long RecipientUserId { get; set; }

    /// <summary>被授权人登录账号</summary>
    public string RecipientUserName { get; set; } = string.Empty;

    /// <summary>被授权人显示姓名</summary>
    public string RecipientDisplayName { get; set; } = string.Empty;

    /// <summary>被固定的不可变发布修订版本号</summary>
    public int RevisionVersion { get; set; }

    /// <summary>当前预期版本令牌（后续变更固定修订 / 撤销需原样回传）</summary>
    public int Version { get; set; }

    /// <summary>授权人（所有者）用户 Id</summary>
    public long GrantedByUserId { get; set; }

    /// <summary>授权时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>最近变更时间（变更固定修订时更新）</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>被授权人共享列表项（只暴露被固定的发布快照，绝不暴露草稿 / 其它修订 / 历史）。</summary>
public sealed class ReportConfigurationSharedSummaryDto
{
    /// <summary>被共享的私有报表配置 Id</summary>
    public long ReportConfigurationId { get; set; }

    /// <summary>固定发布修订的名称快照</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>固定发布修订的数据集键快照</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>固定发布修订版本号</summary>
    public int RevisionVersion { get; set; }

    /// <summary>发布时间</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>所有者用户 Id</summary>
    public long OwnerUserId { get; set; }

    /// <summary>所有者显示姓名（仅用于「来自谁」的展示）</summary>
    public string OwnerDisplayName { get; set; } = string.Empty;
}

/// <summary>被授权人共享详情（只暴露被固定的发布快照定义，绝不暴露所有者草稿 / 其它修订 / 历史 / 编辑权）。</summary>
public sealed class ReportConfigurationSharedDetailDto
{
    /// <summary>被共享的私有报表配置 Id</summary>
    public long ReportConfigurationId { get; set; }

    /// <summary>固定发布修订的名称快照</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>固定发布修订的数据集键快照</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>固定发布修订版本号</summary>
    public int RevisionVersion { get; set; }

    /// <summary>schema 版本快照</summary>
    public int SchemaVersion { get; set; }

    /// <summary>发布时间</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>已反序列化的被固定定义快照</summary>
    public ReportConfigurationDefinition? Definition { get; set; }
}
