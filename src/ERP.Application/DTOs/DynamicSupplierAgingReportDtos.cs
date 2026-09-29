namespace ERP.Application.DTOs;

/// <summary>
/// 动态供应商对账与账龄报表（ERP-140）的只读数据传输对象：字段目录（有限白名单）、预览查询参数与预览结果页。
/// <para>本 DTO 只描述「选择哪些发票证据字段 + 用什么有界筛选预览哪些供应商发票证据」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 供应商对账与账龄报表字段目录项（ERP-140，只读）：来自 <c>SupplierReconciliationAgingInvoice</c>
/// 证据行的有限白名单，由服务端规则统一供给，界面共用同一份口径。
/// </summary>
public sealed record DynamicSupplierAgingReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 供应商对账与账龄报表预览查询参数（ERP-140，全部为只读筛选）：
/// 选定字段（仅限白名单）、供应商 / 币种 / 发票状态 / 分配状态 / 开票日期 / 到期日 / as-of 有界筛选、
/// 以及稳定分页（单页上限 200）。所有筛选均复用 ERP-068 <see cref="SupplierReconciliationAgingQuery"/> 口径。
/// </summary>
public sealed class DynamicSupplierAgingReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>供应商 Id 筛选（留空 = 全部；非正数直接拒绝）</summary>
    public long? SupplierId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成行、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>发票状态筛选（recorded 默认 / draft / voided / all；草稿与已作废金额永不并入有效合计）</summary>
    public string? InvoiceStatus { get; set; }

    /// <summary>分配状态筛选（none / historical_only / partial / full；留空 = 全部）</summary>
    public string? AllocationState { get; set; }

    /// <summary>分组键（仅 none / supplier / currency / agingBucket / allocationState；无效取值由服务端 fail closed 拒绝）</summary>
    public string? GroupBy { get; set; }

    /// <summary>金额汇总模式（仅 none / supplierCurrency / supplierCurrencyAging；无效取值由服务端 fail closed 拒绝，默认 none）</summary>
    public string? SummaryMode { get; set; }

    /// <summary>关键字（匹配发票号码 / 代码 / 供应商编码与名称 / 付款条件文本；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>开票日期开始（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateFrom { get; set; }

    /// <summary>开票日期结束（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateTo { get; set; }

    /// <summary>显式到期日开始（含当天；只命中登记了到期日的发票；留空 = 不限）</summary>
    public DateTime? DueDateFrom { get; set; }

    /// <summary>显式到期日结束（含当天；只命中登记了到期日的发票；留空 = 不限）</summary>
    public DateTime? DueDateTo { get; set; }

    /// <summary>账龄 as-of 日期（显式；留空 = 当天；账龄只相对该日期与显式到期日计算）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 供应商对账与账龄报表字段目录（ERP-140，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicSupplierAgingReportCatalogDto(
    List<DynamicSupplierAgingReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 供应商对账与账龄报表预览结果页（ERP-140，只读）：按请求顺序返回选定列与分页行；
/// 行内仅包含选定的白名单发票证据字段值，不泄露范围外数据。
/// <para>金额一律按原币分别成行：<c>currency</c> 为原币，不同币种绝不合并、不做汇率换算；未知到期日（<c>agingBucket</c> 为 null）
/// 与未知 / 无效分配证据（<c>allocationState</c> / <c>remainingState</c> / 各计数与金额字段）照实保留，绝不推算或修复。</para>
/// <para>ERP-144 新增 <see cref="GroupBy"/> / <see cref="Groups"/>：仅当请求分组（supplier / currency / agingBucket / allocationState）时，
/// <see cref="Groups"/> 才给出「当前授权预览页」按分组键的发票张数分布；只统计张数、绝不求和任何金额；默认 none 时为空。</para>
/// <para>ERP-146 新增 <see cref="SummaryMode"/> / <see cref="Summaries"/>：仅当请求金额汇总（supplierCurrency / supplierCurrencyAging）时，
/// <see cref="Summaries"/> 才给出「当前授权预览页」按供应商 + 原币（可选账龄分桶）的已知有效含税总额 / 有效已分配 / 剩余证据金额；
/// 草稿 / 已作废金额绝不并入，未知 / 无效分配证据按「未知」（null）返回，绝不轧为 0 或给部分合计；默认 none 时为空。</para>
/// </summary>
public sealed record DynamicSupplierAgingReportPageDto(
    List<DynamicSupplierAgingReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string GroupBy = "none",
    List<DynamicSupplierAgingReportGroupDto>? Groups = null,
    string SummaryMode = "none",
    List<DynamicSupplierAgingReportSummaryDto>? Summaries = null);

/// <summary>
/// 供应商对账与账龄报表页面分组计数（ERP-144，只读）：当前授权预览页内按分组键聚合的发票张数分布。
/// 只统计发票张数、绝不求和任何金额、绝不跨币种合并或换算；未知到期日（<c>agingBucket</c> 为 null）
/// 与无效 / 未知分配证据（<c>allocationState</c> 为 over_allocated / unknown）类别始终保留（计数可为 0）。
/// </summary>
public sealed record DynamicSupplierAgingReportGroupDto(
    string Key,
    string Label,
    int Count);

/// <summary>
/// 供应商对账与账龄报表当前页金额汇总（ERP-146，只读）：按供应商 + 原币分组，可选账龄分桶拆分。
/// 只汇总计入有效应付证据合计（已登记未作废）的发票证据：含税总额来自完整加载的发票行（恒可确认，直接求和）；
/// 有效已分配与算术剩余证据只要任一行未知（命中有界上限）或无效（超过含税总额），对应合计即按「未知」（null）返回。
/// 草稿 / 已作废金额绝不并入；<see cref="AgingBucket"/> 仅在账龄汇总模式下出现，未知到期日独立分组、绝不推算。
/// </summary>
public sealed record DynamicSupplierAgingReportSummaryDto(
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    string Currency,
    string? AgingBucket,
    string? AgingBucketText,
    int InvoiceCount,
    decimal GrossAmount,
    decimal? ActiveAllocatedAmount,
    decimal? RemainingAmount,
    int UnknownRemainingInvoiceCount,
    int OverAllocatedInvoiceCount);
