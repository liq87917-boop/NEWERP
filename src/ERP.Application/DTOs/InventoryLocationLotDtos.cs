namespace ERP.Application.DTOs;

/// <summary>
/// 库存移动可携带的「仓库库位 + 批次」身份所覆盖的移动类型（ERP-096 基础，只读派生 / 校验层）。
/// <para>开发态基础：不改变既有商品级成本口径，不新增或修改任何表结构，不执行生产 SQL。</para>
/// </summary>
public enum InventoryLocationLotMovementKind
{
    /// <summary>采购入库</summary>
    StockIn = 1,

    /// <summary>销售出库</summary>
    StockOut = 2,

    /// <summary>仓库调拨（调出 + 调入两侧）</summary>
    Transfer = 3,

    /// <summary>退货（销售退货入库 / 采购退货出库共用）</summary>
    Return = 4
}

/// <summary>
/// 库存移动的「仓库库位 + 可选批次」输入（ERP-096）。
/// 供入库 / 出库 / 调拨 / 退货四类移动统一携带并校验；校验结果见
/// <see cref="ValidatedMovementLocationLot"/>。批次可空：留空表示不启用批次追溯。
/// </summary>
public sealed class MovementLocationLotInput
{
    /// <summary>移动类型</summary>
    public InventoryLocationLotMovementKind Kind { get; init; }

    /// <summary>业务仓库 Id（入库 / 出库 / 退货的落库仓；调拨的调出仓）</summary>
    public long WarehouseId { get; init; }

    /// <summary>库位编码（可空：仓库内的货架 / 库位细分）</summary>
    public string LocationCode { get; init; } = string.Empty;

    /// <summary>调拨调入仓 Id（仅调拨使用）</summary>
    public long ToWarehouseId { get; init; }

    /// <summary>调拨调入库位编码（可空）</summary>
    public string ToLocationCode { get; init; } = string.Empty;

    /// <summary>批次号（可空）</summary>
    public string LotNo { get; init; } = string.Empty;

    /// <summary>商品 Id（可空：允许手工行，仅用于展示 / 追溯）</summary>
    public long? ProductId { get; init; }

    /// <summary>商品名称（用于校验错误文案）</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>移动数量（基础单位，恒为正数）</summary>
    public decimal Quantity { get; init; }
}

/// <summary>
/// 校验通过后的库存移动「仓库库位 + 批次」身份（ERP-096）。
/// 仓库名称已在服务端解析，库位 / 批次已归一化（去首尾空白、超长拒绝）。
/// </summary>
public sealed record ValidatedMovementLocationLot(
    InventoryLocationLotMovementKind Kind,
    long WarehouseId,
    string WarehouseName,
    string LocationCode,
    long ToWarehouseId,
    string ToWarehouseName,
    string ToLocationCode,
    string LotNo,
    long? ProductId,
    string ProductName,
    decimal Quantity);

/// <summary>
/// 调拨守恒校验结果（ERP-096）：调出数量 = 调入数量、调出成本 = 调入成本（同一成本单价），
/// 且调出仓现存量足以覆盖调拨数量（拒绝负库存）。
/// </summary>
public sealed record TransferConservationResult(
    bool IsValid,
    bool QuantityConserved,
    bool CostConserved,
    bool SourceSufficient,
    decimal SourceOnHand,
    decimal TransferQuantity,
    string Message);

/// <summary>
/// 库位级（仓库）库存余额行（ERP-096，只读派生）：直接来自现有 <c>Stocks</c>（仓库 + 商品），
/// 与既有商品级总量天然兼容——商品总量 = 该商品所有库位行之和。
/// </summary>
public sealed record LocationBalanceLine(
    long WarehouseId,
    string WarehouseName,
    long ProductId,
    string ProductCode,
    string ProductName,
    string Spec,
    string Unit,
    decimal Quantity,
    decimal TotalCost,
    decimal AverageCost);

/// <summary>
/// 批次级库存余额行（ERP-096，只读派生）：从有效库存流水（未冲销、非红字）结合来源单据明细的
/// 批次号（<c>BatchNo</c>）派生；无法唯一归属到批次的数量计入空批次桶（<c>LotNo</c> 为空串），
/// 保证批次拆分与库位 / 商品总量严格对账。
/// </summary>
public sealed record LotBalanceLine(
    long WarehouseId,
    long ProductId,
    string ProductCode,
    string ProductName,
    string LotNo,
    decimal Quantity,
    decimal TotalCost);

/// <summary>商品级库存总量（ERP-096）：与既有 <c>Stocks</c> 汇总完全一致，用于对账。</summary>
public sealed record ProductBalanceTotal(
    long ProductId,
    string ProductCode,
    string ProductName,
    string Spec,
    string Unit,
    decimal Quantity,
    decimal TotalCost,
    decimal AverageCost);

/// <summary>
/// 库位 + 批次库存余额报表（ERP-096，只读派生）。
/// <para><see cref="ProductTotals"/> 与 <see cref="LocationLines"/> 直接来自现有库存行，不改变成本口径；
/// <see cref="LotLines"/> 为按批次拆分（含空批次桶），其数量总和与库位行逐仓逐商品对账（<see cref="IsReconciled"/>）。</para>
/// </summary>
public sealed record LocationLotBalanceReport(
    IReadOnlyList<ProductBalanceTotal> ProductTotals,
    IReadOnlyList<LocationBalanceLine> LocationLines,
    IReadOnlyList<LotBalanceLine> LotLines,
    bool IsReconciled,
    string Note);
