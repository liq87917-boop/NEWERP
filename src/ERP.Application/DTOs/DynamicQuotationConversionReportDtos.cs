using System.Text.Json.Serialization;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态报价成交率报表（ERP-206）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「报价单」（quotation）菜单授权与 <c>SalespersonDataScopeService</c>（ERP-097）业务员数据范围，
/// 分组口径与既有 <see cref="ReportDtos.QuotationConversionItem"/>（业务员 × 原币）完全一致；
/// 本 DTO 只描述「选择哪些报价成交率字段 + 用哪个有界日期窗口预览哪些分桶行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 报价成交率字段目录项（ERP-206，只读）：来自既有 <c>ReportDtos.QuotationConversionItem</c> 的有限白名单，
/// 分组语义固定为业务员 × 原币，不新增任何字段或派生。
/// </summary>
public sealed record DynamicQuotationConversionReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 报价成交率报表预览请求（ERP-206，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾日历日最多 366 天），以及稳定分页（页码从 1 开始，单页上限 200）。
/// </summary>
public sealed class DynamicQuotationConversionReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复 / 空键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>开始日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? Start { get; set; }

    /// <summary>结束日期（留空 = 今天；只取日期部分；含首尾，且不得早于开始日期）</summary>
    public DateTime? End { get; set; }

    /// <summary>可选应用筛选（客户 Id / 业务员姓名关键字 / 原币币种；留空 = 不过滤）</summary>
    public QuotationConversionFilterDto? Filter { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 报价成交率字段目录（ERP-206，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicQuotationConversionReportCatalogDto(
    List<DynamicQuotationConversionReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 报价成交率报表预览结果页（ERP-206，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 空客户记录。<see cref="Total"/> 为范围内分桶行总数（分页前），
/// <see cref="Truncated"/> 表示本页之外仍有更多分桶行，<see cref="EmptyText"/> 在空页时显式说明；
/// <see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文（供 Excel 导出标注日期口径）。
/// <see cref="Summary"/> 为 ERP-209 分币种汇总（在全部匹配分桶之上、分页之前派生，绝不跨币种合计）。
/// </summary>
public sealed record DynamicQuotationConversionReportPageDto(
    List<DynamicQuotationConversionReportFieldDto> Columns,
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
    string FilterText = "",
    DynamicQuotationConversionSummaryDto? Summary = null);

/// <summary>
/// 报价成交率分币种汇总项（ERP-209，只读派生）：把同一原币的全部匹配分桶合并为一枚汇总行。
/// 仅填充选定字段对应的汇总指标（未选定的金额 / 计数指标为 null，即「省略未选指标」）；
/// <see cref="Currency"/> 为显式分组上下文，始终存在。金额均为报价单原币，绝不跨币种合计。
/// </summary>
public sealed class DynamicQuotationConversionCurrencySummaryDto
{
    /// <summary>原币币种（规范化大写；空值 / 未知取值显式保留为「未知币种」，绝不默认币种）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>有效报价数（= 全部匹配分桶有效报价数合计）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QuotationCount { get; set; }

    /// <summary>已转出数（= 全部匹配分桶已转出数合计）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConvertedCount { get; set; }

    /// <summary>成交率% = 已转出总数 ÷ 有效报价总数 × 100（保留 2 位；分母为 0 时为 0；绝不按业务员成交率求平均）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ConversionRate { get; set; }

    /// <summary>已过期未成交（= 全部匹配分桶已过期未成交合计）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpiredCount { get; set; }

    /// <summary>已作废（= 全部匹配分桶已作废合计）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CancelledCount { get; set; }

    /// <summary>有效报价金额合计（原币）= 全部匹配分桶有效报价金额合计</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalAmount { get; set; }

    /// <summary>已转出金额合计（原币）= 全部匹配分桶已转出金额合计</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ConvertedAmount { get; set; }

    /// <summary>单笔成交均价（原币）= 已转出金额合计 ÷ 已转出总数（保留 2 位；分母为 0 时为 0；绝不按业务员均价求平均）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AvgConvertedAmount { get; set; }
}

/// <summary>
/// 报价成交率分币种汇总结果（ERP-209，只读派生）：在全部匹配分桶（ERP-208 筛选后）基础上按规范化原币合并，
/// 在分页前完成，绝不跨币种合计。仅返回选定字段对应的汇总列与指标；<see cref="CoverageText"/> 显式声明覆盖范围。
/// </summary>
public sealed record DynamicQuotationConversionSummaryDto(
    List<DynamicQuotationConversionReportFieldDto> Columns,
    List<DynamicQuotationConversionCurrencySummaryDto> Rows,
    int CurrencyCount,
    string CoverageText);
