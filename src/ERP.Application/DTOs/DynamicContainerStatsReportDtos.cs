using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-252）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「柜量与装柜利用率统计」（container-stats）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportDtos.ContainerStatsItem"/>（ERP-251）完全一致（装柜日历日 × 精确原始非空白柜号证据桶，
/// 签名持久化头箱数 / 毛重 / 体积证据，装载率 / 柜型恒为未知）；本 DTO 只描述「选择哪些证据字段 + 用哪个有界日期窗口与可选筛选、
/// 分页预览哪些证据桶」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 柜量与装柜利用率证据字段目录项（ERP-252，只读）：来自既有 <see cref="ReportDtos.ContainerStatsItem"/> 的有限白名单，
/// 仅保留装柜日历日 / 原始柜号 / 装柜清单数 / 授权范围客户数 / 箱数 / 毛重 / 体积 / 未知装载率柜型 / 未知原因字段。
/// </summary>
public sealed record DynamicContainerStatsReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 柜量与装柜利用率证据预览请求（ERP-252，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾日历日最多 366 天），可选应用筛选（客户 Id / 柜号关键字），
/// 以及稳定分页（页码从 1 开始，单页上限 200）。
/// </summary>
public sealed class DynamicContainerStatsReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复 / 空键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>开始日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? Start { get; set; }

    /// <summary>结束日期（留空 = 今天；只取日期部分；含首尾，且不得早于开始日期）</summary>
    public DateTime? End { get; set; }

    /// <summary>可选应用筛选（客户 Id / 柜号关键字；留空 = 不过滤）</summary>
    public DynamicContainerStatsReportFilterDto? Filter { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-252）的可选应用筛选 DTO：客户 Id / 柜号关键字有限选择。
/// <para>本 DTO 只描述「如何在既有业务员数据范围 + 日期窗口之外再收窄装柜清单头读取范围」，不含任何 SQL、连接串或写入语义；
/// 校验 / 规范化统一由 <see cref="Services.DynamicContainerStatsReportRules.NormalizeFilter"/> 完成（fail closed）。</para>
/// <para>客户 Id 仅是装柜清单头持久化属性（非权限边界）：绝不因客户筛选而扩展数据范围，也不会在聚合后再筛选。</para>
/// <para>柜号关键字仅是字面文本筛选（<c>ContainerLoadingLists.ContainerNo.Contains</c> 的字面包含，非 SQL 通配符、非目录泄露），
/// 空白关键字保留缺号（空白柜号）证据桶，非空白关键字仅匹配已持久化的非空白原始柜号；绝不因关键字而扩展数据范围。</para>
/// </summary>
public sealed class DynamicContainerStatsReportFilterDto
{
    /// <summary>客户 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝）</summary>
    public long? CustomerId { get; set; }

    /// <summary>
    /// 柜号关键字筛选（可选：去首尾空白后最多 80 字符、拒绝控制字符；留空 = 不过滤；
    /// 字面文本匹配，<c>%</c> / <c>_</c> 按字面文本而非 SQL 通配符，匹配沿用数据库既有排序规则）。
    /// </summary>
    public string? ContainerNo { get; set; }
}

/// <summary>
/// 柜量与装柜利用率证据字段目录（ERP-252，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源），
/// 以及仅含筛选能力说明（不含任何客户 / 装柜清单数据）的支持筛选口径。
/// </summary>
public sealed record DynamicContainerStatsReportCatalogDto(
    List<DynamicContainerStatsReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    int DefaultPageSize,
    string ReadOnlyText,
    string BoundaryText,
    string FilterText);

/// <summary>
/// 柜量与装柜利用率证据范围上下文（ERP-252，只读、服务端派生）：明确区分「证据桶」与「已审核装柜清单头数」，
/// 并显式声明证据依据为「已审核·未删除·授权范围装柜清单头证据」，非实际发货 / 实体柜 / 收入 / 装载率 / 柜型权威。
/// </summary>
public sealed record DynamicContainerStatsReportContextDto(
    string Label,
    int BucketCount,
    int ApprovedLists,
    string EvidenceBasis);

/// <summary>
/// 柜量与装柜利用率证据预览结果页（ERP-252，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外记录。<see cref="Total"/> 为证据桶总数（分页前，服务端派生），<see cref="TotalPages"/> 为总页数、
/// <see cref="Truncated"/> 表示本页之外仍有更多行，<see cref="PageOnly"/> 表示本页仅覆盖当前分页行（绝不声称一次性返回全部行）；
/// <see cref="EmptyText"/> 在空页时显式说明；<see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文。
/// <see cref="SourceContextText"/> / <see cref="SourceLimitText"/> / <see cref="UnitContextText"/> /
/// <see cref="UnknownCapacityContextText"/> / <see cref="TypeContextText"/> / <see cref="ShippingContextText"/>
/// 显式声明来源 / 来源上限 / 数量单位 / 未知实际容积 / 柜型 / 出运口径（即使对应列被取消选择也始终呈现）；
/// <see cref="FilterText"/> 显式声明已规范化的应用筛选上下文；
/// <see cref="CustomerScopeContextText"/> / <see cref="GroupingContextText"/> 显式声明客户范围与分组身份口径（即使对应列被取消选择也始终呈现）。
/// </summary>
public sealed record DynamicContainerStatsReportPageDto(
    List<DynamicContainerStatsReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool Truncated,
    bool PageOnly,
    string PageOnlyText,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    DateTime Start,
    DateTime End,
    string SourceContextText,
    string SourceLimitText,
    string UnitContextText,
    string UnknownCapacityContextText,
    string TypeContextText,
    string ShippingContextText,
    string FilterText,
    string CustomerScopeContextText,
    string GroupingContextText,
    DynamicContainerStatsReportContextDto Context);
