namespace ERP.Application.DTOs;

/// <summary>
/// 只读补货工作台查询参数（ERP-106，全部为只读筛选）：按仓库（必填）与可选商品筛选，
/// 结果按稳定库存行 Id 分页有界，不做跨仓汇总、不做单位换算。
/// </summary>
public sealed class StockReplenishmentWorksheetQuery
{
    /// <summary>仓库 Id（必填：补货工作台按仓库逐行查看，不跨仓汇总）</summary>
    public long? WarehouseId { get; set; }

    /// <summary>商品 Id（可选：显式 Id 等值匹配）</summary>
    public long? ProductId { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（默认 20，单次上限 200）</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>校验并修正分页参数（与 <see cref="ERP.Application.Common.PageQuery"/> 同口径 + 本模块上限）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = 20;
        if (PageSize > 200) PageSize = 200;
    }
}

/// <summary>
/// 只读补货工作台的货源参考行（ERP-106，**只读**）：仅展示启用中货源关系关联的供应商参考信息，
/// 不自动选择供应商、不生成订单、不改写库存。
/// </summary>
public sealed record StockReplenishmentSourcingDto(
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    bool SupplierAvailable,
    string SupplierItemCode,
    string PurchaseUnit,
    decimal MinOrderQty,
    int LeadTimeDays,
    bool IsPreferred);

/// <summary>
/// 只读补货工作台行（ERP-106，**只读**）：每个「仓库 + 商品」库存行 + 商品最低 / 上限库存阈值、
/// 补货建议与货源参考。逐行口径，不跨仓汇总、不做单位换算。
/// </summary>
public sealed record StockReplenishmentRowDto(
    long StockId,
    long WarehouseId,
    string WarehouseName,
    long ProductId,
    string ProductCode,
    string ProductName,
    string Spec,
    string Unit,
    decimal Quantity,
    decimal AvailableQuantity,
    decimal MinStock,
    decimal MaxStock,
    bool BelowMinimum,
    decimal? SuggestedTopUp,
    string Recommendation,
    string RecommendationText,
    bool SourcingAvailable,
    string SourcingText,
    List<StockReplenishmentSourcingDto> Sourcing);

/// <summary>
/// 只读补货工作台页（ERP-106，**只读**）：分页行 + 只读 / 边界 / 免责文案（与服务端规则同源）。
/// </summary>
public sealed record StockReplenishmentWorksheetDto(
    List<StockReplenishmentRowDto> Items,
    int Total,
    int Page,
    int PageSize,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
