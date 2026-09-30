namespace ERP.Application.DTOs;

/// <summary>
/// 动态跟进提醒报表（ERP-193）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>复用 ERP-192 的「跟进提醒」菜单授权与 <c>SalespersonDataScopeService</c>（ERP-097）业务员数据范围；
/// 本 DTO 只描述「选择哪些跟进提醒证据字段 + 用什么有界筛选预览哪些跟进记录」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 跟进提醒证据字段目录项（ERP-193，只读）：来自 <c>CustomerFollowUp</c> 实体持久化字段的有限白名单，
/// 并含由「下次跟进日期 + as-of 日期」派生的 <c>dueDays</c>（到期天数）与 <c>dueStatus</c>（到期状态）。
/// </summary>
public sealed record DynamicFollowUpDueReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 跟进提醒报表预览查询参数（ERP-193，全部为只读筛选）：选定字段（仅限白名单）、as-of 日期与提前天数窗口、
/// 可选到期状态（overdue / today / upcoming），以及稳定分页（页码从 1 开始，单页上限 200）。
/// </summary>
public sealed class DynamicFollowUpDueReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>as-of 基准日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>提前天数（0 ~ 365，超出直接拒绝；默认 7）</summary>
    public int AheadDays { get; set; } = 7;

    /// <summary>到期状态筛选（可选：overdue / today / upcoming；非法取值直接拒绝）</summary>
    public string? DueStatus { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 跟进提醒证据字段目录（ERP-193，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicFollowUpDueReportCatalogDto(
    List<DynamicFollowUpDueReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 跟进提醒报表预览结果页（ERP-193，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 未分配 / 空客户记录。<see cref="Total"/> 为范围内记录总数（分页前），
/// <see cref="Truncated"/> 表示本页之外仍有更多记录，<see cref="EmptyText"/> 在空页时显式说明。
/// </summary>
public sealed record DynamicFollowUpDueReportPageDto(
    List<DynamicFollowUpDueReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool Truncated,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
