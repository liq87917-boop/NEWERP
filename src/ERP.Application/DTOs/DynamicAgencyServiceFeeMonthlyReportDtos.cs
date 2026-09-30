using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态代理服务费月度汇总报表（ERP-181）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>预览复用 ERP-180 的 <see cref="AgencyServiceFeeMonthlySummaryService.ForQueryAsync"/>：
/// 按「对账日期所属年月 + 客户 + 原币」分组，仅未删除且已登记的对账单计入原币合计，
/// 草稿与已作废单独计数；本 DTO 只描述「选择哪些证据字段 + 复用既有客户 / 币种 / 对账日期筛选 + 有界分页」，
/// 不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 字段目录项（ERP-181，只读）：来自 ERP-180 月度汇总行 <see cref="AgencyServiceFeeMonthlySummaryRow"/> 的有限白名单，
/// 由服务端规则统一供给，预览 / 界面共用同一份口径。
/// </summary>
public sealed record DynamicAgencyServiceFeeMonthlyReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 分组键目录项（ERP-184，只读）：有限分组选择 none / month / customer，由服务端规则统一供给。
/// </summary>
public sealed record DynamicAgencyServiceFeeMonthlyReportGroupByDto(
    string Key,
    string Label);

/// <summary>
/// 字段目录（ERP-181，只读）：白名单字段 + 分组键选择 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicAgencyServiceFeeMonthlyReportCatalogDto(
    List<DynamicAgencyServiceFeeMonthlyReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText,
    string EvidenceOnlyText,
    List<DynamicAgencyServiceFeeMonthlyReportGroupByDto> GroupBys);

/// <summary>
/// 预览查询参数（ERP-181，全部为只读筛选）：选定字段（仅限白名单）、复用 ERP-180 的客户 / 币种 /
/// 对账日期区间筛选，以及稳定分页（单页上限 200）。
/// </summary>
public sealed class DynamicAgencyServiceFeeMonthlyReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>客户筛选（留空 = 全部客户；复用 ERP-180 口径）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成组、绝不合并；非法取值由服务端拒绝）</summary>
    public string? Currency { get; set; }

    /// <summary>对账日期开始（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateFrom { get; set; }

    /// <summary>对账日期结束（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateTo { get; set; }

    /// <summary>分组键（仅 none / month / customer；无效取值由服务端 fail closed 拒绝，默认 none）</summary>
    public string? GroupBy { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = AgencyServiceFeeMonthlySummaryRules.DefaultPageSize;
}

/// <summary>
/// 预览结果页（ERP-181，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外客户数据；保留 ERP-180 的稳定分页、原币隔离、状态金额口径与空结果提示。
/// <para>ERP-184 / ERP-186 新增 <see cref="GroupBy"/> / <see cref="GroupCounts"/>：仅当请求分组（month / customer）时，
/// <see cref="GroupCounts"/> 给出「当前授权预览页」按分组键的计数（已登记 / 草稿 / 已作废 / 总计张数）与
/// 已登记 / 草稿 / 已作废原币金额，原币严格隔离、只统计当前页、绝不做整份报表或会计合计；默认 none 时为空。</para>
/// </summary>
public sealed record DynamicAgencyServiceFeeMonthlyReportPageDto(
    List<DynamicAgencyServiceFeeMonthlyReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool Truncated,
    int GroupCount,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string EvidenceOnlyText,
    string CurrencyIsolationText,
    string NoProrationText,
    string GroupBy = "none",
    List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto> GroupCounts = null!,
    string GroupCountScopeText = "");

/// <summary>
/// 当前授权预览页的分组汇总（ERP-184 / ERP-186，只读）：按「对账月份」或「客户」分组、原币严格隔离，
/// 统计当前页的月度汇总行数（<see cref="RowCount"/>）与已登记 / 草稿 / 已作废 / 总计张数，并分别汇总
/// 已登记 / 草稿 / 已作废原币金额（<see cref="RegisteredTotalAmount"/> / <see cref="DraftTotalAmount"/> / <see cref="VoidedTotalAmount"/>）。
/// <para>本汇总只统计当前授权预览页、不是整份报表总计；金额按原币保留精度、绝不跨币种 / 跨页合计，
/// 且只作为证据数字（不代表收入 / 应收 / 已收款），服务期间跨月不按期间分摊。</para>
/// </summary>
public sealed record DynamicAgencyServiceFeeMonthlyReportGroupCountDto(
    string GroupBy,
    int? StatementYear,
    int? StatementMonth,
    string StatementMonthText,
    long? CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    int RowCount,
    int RegisteredCount,
    int DraftCount,
    int VoidedCount,
    int StatementCount,
    decimal RegisteredTotalAmount,
    string RegisteredTotalAmountText,
    decimal DraftTotalAmount,
    string DraftTotalAmountText,
    decimal VoidedTotalAmount,
    string VoidedTotalAmountText);
