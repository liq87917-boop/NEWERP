using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态客户出货量证据报表（ERP-229）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「客户出货量统计表」（customer-shipment）菜单授权与 <c>SalespersonDataScopeService</c>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportDtos.CustomerShipmentItem"/>（ERP-227 / ERP-228）完全一致（客户 × 原币证据行，
/// 已审核订单数 / 原币金额小计 / 精确单位分组 / 显式未知币种与未知单位证据）；本 DTO 只描述
/// 「选择哪些客户出货量证据字段 + 用哪个有界日期窗口与分页预览哪些客户 × 原币行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 客户出货量证据字段目录项（ERP-229，只读）：来自既有 <see cref="ReportDtos.CustomerShipmentItem"/> 的有限白名单，
/// 仅保留客户身份 / 原币币种 / 已审核订单数 / 原币金额小计 / 已知单一单位数量与显式数量 / 来源证据字段。
/// </summary>
public sealed record DynamicCustomerShipmentReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 客户出货量证据预览请求（ERP-229，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾日历日最多 366 天），以及稳定分页（页码从 1 开始，单页上限 200）。
/// </summary>
public sealed class DynamicCustomerShipmentReportRequest
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
/// 客户出货量证据字段目录（ERP-229，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicCustomerShipmentReportCatalogDto(
    List<DynamicCustomerShipmentReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    int DefaultPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 客户出货量证据范围上下文（ERP-229，只读、服务端派生）：明确区分「客户 × 原币证据行」与
/// 「去重客户数 / 去重订单数」，并显式声明证据依据为「已审核销售订单」，非实际出库 / 装柜 / 收款。
/// </summary>
public sealed record DynamicCustomerShipmentReportContextDto(
    string Label,
    int CustomerCurrencyRows,
    int UniqueCustomers,
    int UniqueOrders,
    string EvidenceBasis);

/// <summary>
/// 客户出货量证据预览结果页（ERP-229，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 未分配客户记录。<see cref="Total"/> 为客户 × 原币证据行总数（分页前，服务端派生），
/// <see cref="TotalPages"/> 为总页数、<see cref="Truncated"/> 表示本页之外仍有更多行，
/// <see cref="PageOnly"/> 表示本页仅覆盖当前分页行（绝不声称一次性返回全部行）；<see cref="EmptyText"/> 在空页时显式说明；
/// <see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文（供 Excel 导出标注日期口径）。
/// <see cref="CurrencyContextText"/> / <see cref="UnitContextText"/> / <see cref="UnknownContextText"/> /
/// <see cref="SourceContextText"/> 显式声明原币 / 精确单位 / 未知 / 来源口径（即使对应列被取消选择也始终呈现）；
/// <see cref="SourceLimitText"/> 显式声明来源上限（有界读取，超出 fail closed）；<see cref="Context"/> 携带去重客户 / 订单数。
/// </summary>
public sealed record DynamicCustomerShipmentReportPageDto(
    List<DynamicCustomerShipmentReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool Truncated,
    bool PageOnly,
    string PageOnlyText,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    DateTime Start,
    DateTime End,
    string CurrencyContextText,
    string UnitContextText,
    string UnknownContextText,
    string SourceContextText,
    string SourceLimitText,
    DynamicCustomerShipmentReportContextDto Context);
