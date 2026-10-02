using ERP.Application.Interfaces;
using System.Text.Json.Serialization;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态订单利润暂估报表（ERP-221）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「订单利润暂估表」（order-profit）菜单授权与 <c>SalespersonDataScopeService</c>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportDtos.OrderProfitItem"/>（ERP-219 / ERP-220）完全一致（订单 / 客户身份、订单日期、
/// 原币币种与销售额、显式未知成本 / 利润 / 利润率证据、独立「当前价估算」口径）；
/// 本 DTO 只描述「选择哪些订单利润暂估字段 + 用哪个有界日期窗口与分页预览哪些订单行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 订单利润暂估字段目录项（ERP-221，只读）：来自既有 <see cref="ReportDtos.OrderProfitItem"/> 的有限白名单，
/// 仅保留身份 / 日期 / 客户 / 原币 / 销售额与显式未知成本利润证据字段，「当前价估算」作为独立口径单独标注。
/// </summary>
public sealed record DynamicOrderProfitEstimateReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 订单利润暂估报表预览请求（ERP-221，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾日历日最多 366 天），以及稳定分页（页码从 1 开始，单页上限 200）。
/// </summary>
public sealed class DynamicOrderProfitEstimateReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复 / 空键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>开始日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? Start { get; set; }

    /// <summary>结束日期（留空 = 今天；只取日期部分；含首尾，且不得早于开始日期）</summary>
    public DateTime? End { get; set; }

    /// <summary>可选应用筛选（客户 Id / 原币币种；留空 = 不过滤）</summary>
    public OrderProfitEstimateFilterDto? Filter { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 动态订单利润暂估报表（ERP-223）的可选应用筛选 DTO：客户 Id 与原币币种有限选择。
/// <para>本 DTO 只描述「如何在既有业务员数据范围 + 日期窗口之外再收窄销售订单读取范围」，不含任何 SQL、连接串或写入语义；
/// 校验 / 规范化统一由 <see cref="Services.DynamicOrderProfitEstimateReportRules.NormalizeFilter"/> 完成（fail closed）。</para>
/// </summary>
public sealed class OrderProfitEstimateFilterDto
{
    /// <summary>客户 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝）</summary>
    public long? CustomerId { get; set; }

    /// <summary>
    /// 原币币种筛选（可选：留空 = 全部；仅接受已知 <c>Currency</c> 枚举码 CNY / USD / EUR / HKD / GBP / JPY，
    /// 非法 / 数字 / 未知取值直接拒绝，绝不回退为 CNY 或任何默认币种）。
    /// </summary>
    public string? Currency { get; set; }
}

/// <summary>
/// 订单利润暂估字段目录（ERP-221，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicOrderProfitEstimateReportCatalogDto(
    List<DynamicOrderProfitEstimateReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 订单利润暂估报表预览结果页（ERP-221，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 空客户记录。<see cref="Total"/> 为范围内订单行总数（分页前，服务端派生），
/// <see cref="TotalPages"/> 为总页数、<see cref="Truncated"/> 表示本页之外仍有更多订单行，
/// <see cref="PageOnlyText"/> 显式声明仅当前页覆盖；<see cref="EmptyText"/> 在空页时显式说明；
/// <see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文（供 Excel 导出标注日期口径）。
/// <see cref="CurrencyContextText"/> 显式声明金额为订单原币、绝不跨币种合计；<see cref="UnknownBasisText"/> 显式声明
/// 成本 / 利润 / 利润率为未知依据、当前价估算为独立口径（即使对应列被取消选择也始终呈现）；
/// <see cref="SourceLimitText"/> 显式声明来源上限（有界读取，超出 fail closed）。
/// </summary>
public sealed record DynamicOrderProfitEstimateReportPageDto(
    List<DynamicOrderProfitEstimateReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool Truncated,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    DateTime Start,
    DateTime End,
    string PageOnlyText,
    string CurrencyContextText,
    string UnknownBasisText,
    string SourceLimitText,
    string FilterText = "",
    DynamicOrderProfitEstimateSummaryDto? Summary = null);

/// <summary>
/// 订单利润暂估分币种汇总项（ERP-224，只读派生）：把同一原币的全部匹配已审核销售订单合并为一枚汇总行。
/// 汇总覆盖全部匹配的有界来源订单（与明细页 / 选定列无关），金额均为订单原币，绝不跨币种合计；
/// <see cref="Currency"/> 为显式分组上下文，始终存在；未知 / 空 / 非法币种为显式「未知币种」桶：
/// 有订单数、金额为 null（绝不回落为 0）；已知币种金额为签名销售额小计（可为负）。
/// </summary>
public sealed class DynamicOrderProfitEstimateCurrencySummaryDto
{
    /// <summary>原币币种（规范化大写；空值 / 未知取值显式保留为「未知币种」，绝不默认币种）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>已审核订单数（= 全部匹配有界来源订单数，与当前页 / 选定列无关）</summary>
    public int OrderCount { get; set; }

    /// <summary>销售额小计（原币、签名；未知币种为 null，绝不回落为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SalesAmount { get; set; }
}

/// <summary>
/// 订单利润暂估分币种汇总结果（ERP-224，只读派生）：在全部匹配已审核销售订单（ERP-223 筛选后）基础上按规范化原币合并，
/// 在分页前完成，绝不跨币种合计、绝不产生成本 / 利润 / 当前价估算或跨币种总额；
/// 汇总列固定为「原币币种 / 已审核订单数 / 销售额(原币)」，与明细页选定列无关；<see cref="CoverageText"/> 显式声明覆盖范围。
/// </summary>
public sealed record DynamicOrderProfitEstimateSummaryDto(
    List<DynamicOrderProfitEstimateReportFieldDto> Columns,
    List<DynamicOrderProfitEstimateCurrencySummaryDto> Rows,
    int CurrencyCount,
    string CoverageText);
