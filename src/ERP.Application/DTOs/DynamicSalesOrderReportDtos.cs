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
    string DisclaimerText);
