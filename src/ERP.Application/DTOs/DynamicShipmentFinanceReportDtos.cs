namespace ERP.Application.DTOs;

/// <summary>
/// 动态销售订单出货 / 财务进度报表（ERP-156）的只读数据传输对象：字段目录（有限白名单）、预览查询参数与预览结果页。
/// <para>本 DTO 只描述「选择哪些 ERP-032 订单证据字段 + 用什么有界筛选预览哪些订单」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 销售订单出货 / 财务进度报表字段目录项（ERP-156，只读）：来自 <c>SalesOrderShipmentFinanceOrder</c>
/// 证据行的有限白名单，由服务端规则统一供给，界面 / 导出共用同一份口径。
/// </summary>
public sealed record DynamicShipmentFinanceReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 销售订单出货 / 财务进度报表预览查询参数（ERP-156，全部为只读筛选）：
/// 选定字段（仅限白名单）、客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态有界筛选，以及稳定分页（单页上限 200）。
/// <para>所有筛选均复用 ERP-032 销售订单出货 / 财务进度报表的既有口径（出货状态 none / shipped、收款链接状态 linked / partial / unlinked）。</para>
/// </summary>
public sealed class DynamicShipmentFinanceReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>客户 Id 筛选（留空 = 全部；非正数直接拒绝，且仅在当前业务员数据范围内生效）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成行、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>出货状态筛选（none / shipped；留空 = 全部）</summary>
    public string? ShipmentStatus { get; set; }

    /// <summary>收款链接状态筛选（linked / partial / unlinked；留空 = 全部）</summary>
    public string? FinanceLinkStatus { get; set; }

    /// <summary>分组键（仅 none / customer / currency / shipmentStatus / financeLinkStatus；无效取值由服务端 fail closed 拒绝，默认 none）</summary>
    public string? GroupBy { get; set; }

    /// <summary>金额汇总模式（仅 none / customerCurrency / customerCurrencyShipment / customerCurrencyFinance；无效取值由服务端 fail closed 拒绝，默认 none）</summary>
    public string? SummaryMode { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 销售订单出货 / 财务进度报表字段目录（ERP-156，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicShipmentFinanceReportCatalogDto(
    List<DynamicShipmentFinanceReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 销售订单出货 / 财务进度报表预览结果页（ERP-156，只读）：按请求顺序返回选定列与分页行；
/// 行内仅包含选定的白名单 ERP-032 订单证据字段值，不泄露范围外订单数据。
/// <para>金额与数量一律保留原币与未知语义：<c>currency</c> 为原币，不同币种分别成行、绝不合并或换算；未知金额
/// （<c>linkedAmount</c> / <c>uncoveredAmount</c> / <c>submittedAmount</c> 为 null）与未知数量
/// （<c>orderedQuantity</c> / <c>shippedQuantity</c> / <c>pendingShipmentQuantity</c> / <c>outstandingQuantity</c> 为 null）
/// 照实保留，绝不推算或修复。</para>
/// <para>ERP-162 新增 <see cref="SummaryMode"/> / <see cref="Summaries"/>：仅当请求金额汇总（customerCurrency /
/// customerCurrencyShipment / customerCurrencyFinance）时，<see cref="Summaries"/> 才给出「当前授权预览页」按客户 + 原币
/// （可选出货状态 / 收款链接状态）的已知出货与财务金额汇总；订单金额保持原币，linked / uncovered / submitted 任一组成金额未知即合计为 null；
/// 默认 none 时为空。</para>
/// </summary>
public sealed record DynamicShipmentFinanceReportPageDto(
    List<DynamicShipmentFinanceReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string GroupBy = "none",
    List<DynamicShipmentFinanceReportGroupDto>? Groups = null,
    string SummaryMode = "none",
    List<DynamicShipmentFinanceReportSummaryDto>? Summaries = null);

/// <summary>
/// 销售订单出货 / 财务进度报表当前授权预览页的分组计数（ERP-160，只读）：当前页内按分组键聚合的销售订单张数分布。
/// 只统计销售订单张数、绝不求和任何金额或数量、绝不跨币种合并或换算；出货状态（none / partial / complete / over_shipped / unknown）
/// 与收款链接状态（linked / partial / unlinked / unknown）类别始终保留（计数可为 0），unknown 类别保持可见。
/// </summary>
public sealed record DynamicShipmentFinanceReportGroupDto(
    string Key,
    string Label,
    int Count);

/// <summary>
/// 销售订单出货 / 财务进度报表当前授权预览页的已知出货与财务金额汇总（ERP-162，只读）：客户与币种是强制分组边界，
/// 订单金额保持原币证据（绝不跨币种合并或换算）；linked / uncovered / submitted 合计只要任一行金额未知（null）即整体为 null（未知，不是 0），
/// 并显式给出已知 / 未知行数。shipmentStatus / financeLinkStatus 仅在对应汇总模式引入（customerCurrencyShipment / customerCurrencyFinance），
/// 其余模式为 null。<c>uncoveredAmount</c> 只作「未覆盖金额」，绝不是应收余额或收款授权。
/// </summary>
public sealed record DynamicShipmentFinanceReportSummaryDto(
    long CustomerId,
    string CustomerName,
    string Currency,
    string? ShipmentStatus,
    string? FinanceLinkStatus,
    int OrderCount,
    decimal OrderAmount,
    int KnownLinkedAmountRows,
    int UnknownLinkedAmountRows,
    decimal? LinkedAmount,
    int KnownUncoveredAmountRows,
    int UnknownUncoveredAmountRows,
    decimal? UncoveredAmount,
    int KnownSubmittedAmountRows,
    int UnknownSubmittedAmountRows,
    decimal? SubmittedAmount);
