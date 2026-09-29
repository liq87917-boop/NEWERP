namespace ERP.Application.DTOs;

/// <summary>
/// 动态库存库龄与成本估值报表（ERP-135）的只读数据传输对象：字段目录（有限白名单）、预览查询参数与预览结果页。
/// <para>本 DTO 只描述「选择哪些字段 + 用什么有界筛选预览哪些库存行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 库存库龄报表字段目录项（ERP-135，只读）：来自 ERP-034 <c>InventoryAgingItem</c> 的有限白名单，
/// 由服务端规则统一供给，预览 / 界面共用同一份口径。
/// </summary>
public sealed record DynamicInventoryAgingReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 库存库龄报表预览查询参数（ERP-135，全部为只读筛选）：
/// 选定字段（仅限白名单）、仓库 / 商品 / 截止日期有界筛选、以及稳定分页（单页上限 200）。
/// </summary>
public sealed class DynamicInventoryAgingReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>截止日期（报表时点，默认今天；截止日期之后的流水不进入库龄口径）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>仓库 Id 筛选（留空 = 全部仓库）</summary>
    public long? WarehouseId { get; set; }

    /// <summary>商品 Id 筛选（留空 = 全部商品）</summary>
    public long? ProductId { get; set; }

    /// <summary>分组键（仅 none / warehouse / ageEvidence / costEvidence；无效取值由服务端 fail closed 拒绝）</summary>
    public string? GroupBy { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 库存库龄报表字段目录（ERP-135，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicInventoryAgingReportCatalogDto(
    List<DynamicInventoryAgingReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 库存库龄报表预览结果页（ERP-135，只读）：按请求顺序返回选定列与分页行；
/// 行内仅包含选定的白名单字段值，不泄露范围外库存数据。
/// <para>未知库龄（库龄未知数量）与未知成本（成本状态 unknown、金额 null）语义保持不变，
/// 金额一律为持久化的库存成本币种（CNY），不跨币种合并、不推断汇率。</para>
/// <para>ERP-137 新增 <see cref="GroupBy"/> / <see cref="Groups"/>：仅当请求分组（warehouse / ageEvidence / costEvidence）时，
/// <see cref="Groups"/> 才给出「当前授权预览页」按分组键的行数分布；只统计行数、绝不求和任何数量或金额；默认 none 时为空。</para>
/// </summary>
public sealed record DynamicInventoryAgingReportPageDto(
    List<DynamicInventoryAgingReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    DateTime AsOfDate,
    string CostCurrency,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    string GroupBy = "none",
    List<DynamicInventoryAgingReportGroupDto>? Groups = null);

/// <summary>
/// 库存库龄报表页面分组计数（ERP-137，只读）：当前授权预览页内按分组键聚合的行数分布。
/// 只统计行数，绝不跨不同商品 / 基础单位求和任何数量，也不把未知成本金额当作 0 求和；
/// 固定证据分类（库龄依据 full / partial / none，成本依据 known / unknown）的空分类始终保留（计数可为 0）。
/// </summary>
public sealed record DynamicInventoryAgingReportGroupDto(
    string Key,
    string Label,
    int Count);
