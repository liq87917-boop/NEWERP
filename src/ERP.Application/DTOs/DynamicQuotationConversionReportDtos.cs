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
    DateTime End);
