namespace ERP.Application.DTOs;

/// <summary>
/// 动态供应商采购敞口预览（ERP-148）的只读数据传输对象：字段目录（有限白名单）、预览查询参数与预览结果页。
/// <para>本 DTO 只描述「选择哪些采购订单敞口证据字段 + 用什么有界筛选预览哪些采购订单」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 供应商采购敞口报表字段目录项（ERP-148，只读）：来自 <c>SupplierPurchaseExposureOrder</c>
/// 证据行的有限白名单，由服务端规则统一供给，界面共用同一份口径。
/// </summary>
public sealed record DynamicSupplierExposureReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 供应商采购敞口预览查询参数（ERP-148，全部为只读筛选）：
/// 选定字段（仅限白名单）、供应商 / 币种 / 订单日期 / 链接状态有界筛选，以及稳定分页（单页上限 200）。
/// 所有筛选均复用 ERP-031 供应商采购敞口报表（<c>SupplierPurchaseExposureQuery</c>）口径。
/// </summary>
public sealed class DynamicSupplierExposureReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>供应商 Id 筛选（留空 = 全部；非正数直接拒绝）</summary>
    public long? SupplierId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成行、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>链接状态筛选（linked / ambiguous / unavailable；留空 = 全部）</summary>
    public string? LinkStatus { get; set; }

    /// <summary>关键字（匹配采购单号 / 采购合同号 / 归属销售订单号；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 供应商采购敞口报表字段目录（ERP-148，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicSupplierExposureReportCatalogDto(
    List<DynamicSupplierExposureReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 供应商采购敞口预览结果页（ERP-148，只读）：按请求顺序返回选定列与分页行；
/// 行内仅包含选定的白名单采购订单敞口证据字段值，不泄露范围外数据。
/// <para>金额一律按原币分别成行：<c>currency</c> 为原币，不同币种绝不合并、不做汇率换算；未知结算金额
/// （<c>settledAmount</c> / <c>outstandingAmount</c> / <c>submittedAmount</c> 为 null）与未知收货数量
/// （<c>orderedQuantity</c> / <c>receivedQuantity</c> / <c>outstandingQuantity</c> / <c>pendingQuantity</c> 为 null）
/// 照实保留，绝不推算或修复。</para>
/// </summary>
public sealed record DynamicSupplierExposureReportPageDto(
    List<DynamicSupplierExposureReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
