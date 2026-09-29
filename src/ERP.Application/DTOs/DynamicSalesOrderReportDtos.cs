namespace ERP.Application.DTOs;

/// <summary>
/// 动态销售订单报表（ERP-112）的只读数据传输对象：字段目录（有限白名单）、预览查询参数与预览结果页。
/// <para>本 DTO 只描述「选择哪些字段 + 用什么有界筛选预览哪些订单」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 销售订单报表字段目录项（ERP-112，只读）：来自 <c>SalesOrder</c> 实体持久化字段的有限白名单，
/// 由服务端规则统一供给，界面 / 导出 / 汇总共用同一份口径。
/// </summary>
public sealed record DynamicSalesOrderReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 销售订单报表预览查询参数（ERP-112，全部为只读筛选）：
/// 选定字段（仅限白名单）、订单日期 / 客户 / 状态 / 币种有界筛选、以及稳定分页（单页上限 200）。
/// <para>同一请求体亦用于 Excel 导出（ERP-115，只读）：服务端在导出时重新校验字段 / 筛选 / 页大小与当前账号权限，
/// 只导出「当前页」，可选按分组追加币种分开的页面小计。</para>
/// </summary>
public sealed class DynamicSalesOrderReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>订单日期起（含当日）</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>订单日期止（含当日）</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>客户 Id 筛选（在当前业务员数据范围内生效）</summary>
    public long? CustomerId { get; set; }

    /// <summary>状态筛选（可选：Pending / Submitted / Approved / Rejected / Completed / Cancelled）</summary>
    public string? Status { get; set; }

    /// <summary>币种筛选（可选：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public string? Currency { get; set; }

    /// <summary>分组键（仅 none / customer / month；无效取值由服务端 fail closed 拒绝）</summary>
    public string? GroupBy { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 销售订单报表字段目录（ERP-112，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicSalesOrderReportCatalogDto(
    List<DynamicSalesOrderReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 销售订单报表预览结果页（ERP-112，只读）：按请求顺序返回选定列与分页行；
/// 行内仅包含选定的白名单字段值，不泄露范围外订单数据。
/// <para>ERP-114 新增 <see cref="GroupBy"/> / <see cref="Groups"/>：仅当请求分组（customer / month）时，
/// <see cref="Groups"/> 才给出「当前预览页」按分组键 + 币种分开的页面小计；默认 none 时为空。</para>
/// </summary>
public sealed record DynamicSalesOrderReportPageDto(
    List<DynamicSalesOrderReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string GroupBy = "none",
    List<DynamicSalesOrderReportGroupDto>? Groups = null);

/// <summary>
/// 销售订单报表页面小计的「币种小计」项（ERP-114，只读）：同一分组键内按订单原币分开统计条数与金额，
/// 金额只对同币种求和，绝不跨币种换算或相加。
/// </summary>
public sealed record DynamicSalesOrderReportCurrencySubtotalDto(
    string Currency,
    int Count,
    decimal Amount);

/// <summary>
/// 销售订单报表分组小计（ERP-114，只读）：当前预览页内按分组键聚合的「页面小计」（非全量合计），
/// 每个分组内再按币种分开（<see cref="Subtotals"/>）。分组键与文案由服务端规则统一生成，确定性排序。
/// </summary>
public sealed record DynamicSalesOrderReportGroupDto(
    string Key,
    string Label,
    List<DynamicSalesOrderReportCurrencySubtotalDto> Subtotals);
