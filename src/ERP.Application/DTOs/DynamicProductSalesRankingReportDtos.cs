using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态商品销量排名报表（ERP-213）的只读数据传输对象：有限发货数量证据字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「商品销量排名榜」菜单授权与 <c>SalespersonDataScopeService</c>（ERP-097）业务员数据范围；
/// 发货数量口径与既有 <see cref="ReportDtos.ProductSalesRankItem"/>（ERP-212）完全一致（已审核销售出库的发货数量），
/// 并明确排除既有「当前价估算金额 / 金额口径」字段；本 DTO 只描述「选择哪些发货数量证据字段 + 用哪个有界日期窗口与 Top
/// 预览哪些排名行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 商品销量排名发货数量证据字段目录项（ERP-213，只读）：来自既有 <c>ReportDtos.ProductSalesRankItem</c> 的有限白名单，
/// 仅保留发货数量证据字段（排名 / 商品Id / 商品编码 / 商品名称 / 规格 / 单位 / 发货数量），排除金额估算字段。
/// </summary>
public sealed record DynamicProductSalesRankingReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 商品销量排名报表预览请求（ERP-213，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾最多 366 天）与有界 Top（1 ~ 200）。
/// </summary>
public sealed class DynamicProductSalesRankingReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复 / 空键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>开始日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? Start { get; set; }

    /// <summary>结束日期（留空 = 今天；只取日期部分；含首尾，且不得早于开始日期）</summary>
    public DateTime? End { get; set; }

    /// <summary>Top（1 ~ 200，超出直接拒绝；默认 10）</summary>
    public int Top { get; set; } = 10;
}

/// <summary>
/// 商品销量排名字段目录（ERP-213，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicProductSalesRankingReportCatalogDto(
    List<DynamicProductSalesRankingReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxTop,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 商品销量排名报表预览结果页（ERP-213，只读）：按请求顺序返回选定列与 Top 有界行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 未分配 / 空客户记录。<see cref="Total"/> 为本次实际返回的排名行数（≤ Top），
/// <see cref="TopLimited"/> 表示返回行数已达 Top、可能存在 Top 之外的排名（绝不声称 Top 之外完整）；
/// <see cref="EmptyText"/> 在空结果时显式说明；<see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文；
/// <see cref="UnitContextText"/> 显式声明单位不兼容、绝不跨单位合计数量；<see cref="ApprovedShipmentText"/> 显式声明发货证据口径。
/// </summary>
public sealed record DynamicProductSalesRankingReportPageDto(
    List<DynamicProductSalesRankingReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Top,
    bool TopLimited,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string UnitContextText,
    string ApprovedShipmentText,
    DateTime Start,
    DateTime End);
