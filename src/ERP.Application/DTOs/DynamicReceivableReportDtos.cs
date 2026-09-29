namespace ERP.Application.DTOs;

/// <summary>
/// 动态客户应收账款证据报表（ERP-117）的只读数据传输对象：字段目录（有限白名单）、预览查询参数与预览结果页。
/// <para>本 DTO 只描述「选择哪些字段 + 用什么有界筛选预览哪些客户销项发票证据」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 应收账款证据报表字段目录项（ERP-117，只读）：来自 <c>CustomerReceivableReconciliationInvoiceRow</c>（ERP-074 对账证据行）
/// 持久化字段的有限白名单，由服务端规则统一供给；不含账龄 / 到期日字段（不主张欠款、账龄或催收结论）。
/// </summary>
public sealed record DynamicReceivableReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 应收账款证据报表预览查询参数（ERP-117，全部为只读筛选）：
/// 选定字段（仅限白名单）、客户 / 发票日期 / 币种 / 分配状态有界筛选、以及稳定分页（单页上限 100）。
/// <para>金额只按原币呈现，绝不做汇率换算或跨币种合并；算术剩余证据 = 发票含税总额 − 有效已分摊金额。</para>
/// </summary>
public sealed class DynamicReceivableReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>发票日期起（含当日）</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>发票日期止（含当日）</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>客户 Id 筛选（在当前业务员数据范围内生效）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（系统支持的币种代码，如 CNY / USD / EUR；非法取值直接拒绝）</summary>
    public string? Currency { get; set; }

    /// <summary>分配状态筛选（可选：none / historical_only / partial / full；非法取值直接拒绝）</summary>
    public string? AllocationState { get; set; }

    /// <summary>发票状态筛选（可选：recorded / draft / voided / all；默认 recorded；非法取值直接拒绝）</summary>
    public string? InvoiceStatus { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 100，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 应收账款证据报表字段目录（ERP-117，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicReceivableReportCatalogDto(
    List<DynamicReceivableReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 应收账款证据报表预览结果页（ERP-117，只读）：按请求顺序返回选定列与分页行；
/// 行内仅包含选定的白名单字段值，不泄露范围外客户 / 发票证据数据。
/// </summary>
public sealed record DynamicReceivableReportPageDto(
    List<DynamicReceivableReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
