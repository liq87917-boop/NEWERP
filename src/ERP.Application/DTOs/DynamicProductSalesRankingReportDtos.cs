using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态商品销量排名报表（ERP-213）的只读数据传输对象：有限发货数量证据字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「商品销量排名榜」菜单授权与 <c>SalespersonDataScopeService</c>（ERP-097）业务员数据范围；
/// 发货数量口径与既有 <see cref="ReportDtos.ProductSalesRankItem"/>（ERP-212）完全一致（已审核销售出库的发货数量），
/// 并明确排除既有「当前价估算金额 / 金额口径」字段；本 DTO 只描述「选择哪些发货数量证据字段 + 用哪个有界日期窗口与 Top
/// 预览哪些排名行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 商品销量排名发货数量证据字段目录项（ERP-213，只读）：来自既有 <c>ReportDtos.ProductSalesRankItem</c> 的有限白名单，
/// 仅保留发货数量证据字段（排名 / 商品Id / 商品编码 / 商品名称 / 规格 / 单位 / 发货数量），排除金额估算字段。
/// </summary>
public sealed record DynamicProductSalesRankingReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 商品销量排名报表可选应用筛选（ERP-215，只读）：客户 Id / 商品 Id 为正整数标识，单位文本有界且拒绝控制字符。
/// <para>本 DTO 只描述「如何在既有业务员数据范围 + 日期窗口 + Top 之外再收窄发货数量排名读取范围」，不含任何 SQL、连接串或写入语义；
/// 校验 / 规范化统一由 <see cref="Services.DynamicProductSalesRankingReportRules.NormalizeFilter"/> 完成（fail closed）。</para>
/// </summary>
public sealed class ProductSalesRankingFilterDto
{
    /// <summary>客户 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝；与当前业务员数据范围求交集）</summary>
    public long? CustomerId { get; set; }

    /// <summary>商品 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝）</summary>
    public long? ProductId { get; set; }

    /// <summary>单位筛选（可选：去首尾空白后最多 30 字符；留空 = 不过滤；不含控制字符；精确匹配，不做单位换算）</summary>
    public string? Unit { get; set; }
}

/// <summary>
/// 商品销量排名报表预览请求（ERP-213，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾最多 366 天）与有界 Top（1 ~ 200），以及 ERP-215 可选客户 / 商品 / 单位筛选。
/// </summary>
public sealed class DynamicProductSalesRankingReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复 / 空键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>开始日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? Start { get; set; }

    /// <summary>结束日期（留空 = 今天；只取日期部分；含首尾，且不得早于开始日期）</summary>
    public DateTime? End { get; set; }

    /// <summary>Top（1 ~ 200，超出直接拒绝；默认 10）</summary>
    public int Top { get; set; } = 10;

    /// <summary>可选应用筛选（客户 Id / 商品 Id / 单位；留空 = 不过滤，保持既有排名行为）</summary>
    public ProductSalesRankingFilterDto? Filter { get; set; }

    /// <summary>分组键（仅 none / unit；留空 = none 不分组；未知取值由服务端 fail closed 拒绝）</summary>
    public string? GroupBy { get; set; }
}

/// <summary>
/// 商品销量排名字段目录（ERP-213，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicProductSalesRankingReportCatalogDto(
    List<DynamicProductSalesRankingReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxTop,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 商品销量排名「按单位分组」汇总桶（ERP-216，只读派生）：仅针对当前 Top 结果（绝非完整日期范围）。
/// <see cref="Unit"/> 为精确单位文本（区分大小写 / 空白，绝不合并或换算）；<see cref="RankingBucketCount"/> 为排名桶数
/// （同一商品/规格/单位的排名行数，非唯一商品或单据数）；<see cref="TotalQuantity"/> 为同单位签名数量小计（可为负 / 零）；
/// 空白或未知单位进入 <see cref="IsUnknown"/> 桶，其数量恒为 null（仅呈现桶数），绝不含金额。
/// </summary>
public sealed record DynamicProductSalesRankingReportGroupDto(
    string Unit,
    string Label,
    int RankingBucketCount,
    decimal? TotalQuantity,
    bool IsUnknown);

/// <summary>
/// 商品销量排名报表预览结果页（ERP-213，只读）：按请求顺序返回选定列与 Top 有界行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 未分配 / 空客户记录。<see cref="Total"/> 为本次实际返回的排名行数（≤ Top），
/// <see cref="TopLimited"/> 表示返回行数已达 Top、可能存在 Top 之外的排名（绝不声称 Top 之外完整）；
/// <see cref="EmptyText"/> 在空结果时显式说明；<see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文；
/// <see cref="UnitContextText"/> 显式声明单位不兼容、绝不跨单位合计数量；<see cref="ApprovedShipmentText"/> 显式声明发货证据口径；
/// <see cref="FilterText"/> 为已规范化的客户 / 商品 / 单位筛选上下文（只含 Id 与单位文本，绝不泄露范围外客户名称）。
/// <para>ERP-216：<see cref="GroupBy"/> 为规范化分组键（none / unit）；<see cref="Groups"/> 仅在 unit 分组时给出
/// 「当前 Top 结果」按精确单位汇总的排名桶数与签名数量小计（空白 / 未知单位数量为 null）；<see cref="GroupContextText"/>
/// 显式声明该分组只针对当前 Top 结果、排名桶数非唯一商品 / 单据数、绝不跨单位合计数量也不含金额。</para>
/// </summary>
public sealed record DynamicProductSalesRankingReportPageDto(
    List<DynamicProductSalesRankingReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Top,
    bool TopLimited,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string UnitContextText,
    string ApprovedShipmentText,
    DateTime Start,
    DateTime End,
    string FilterText = "",
    string GroupBy = "none",
    List<DynamicProductSalesRankingReportGroupDto>? Groups = null,
    string GroupContextText = "");
