using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 库存盘点/调整单主表（ERP-009）
/// 业务口径：盘点时按商品录入账面数量与实盘数量，审核后按「实盘 - 账面」的差异调整库存，
/// 并写入可审计的库存流水；销审时按流水逐笔冲销（红字流水），不加倍、不丢失。
/// </summary>
public class StockAdjustment : BaseEntity
{
    /// <summary>盘点/调整单号（单据字轨生成，如 PD2609230001）</summary>
    /// <remarks>允许空串：单号由服务端字轨生成，禁止空串会让 [ApiController] 的模型校验提前拦成 400。</remarks>
    [Required(AllowEmptyStrings = true), MaxLength(50)]
    public string AdjustmentNo { get; set; } = string.Empty;

    /// <summary>盘点日期</summary>
    public DateTime AdjustmentDate { get; set; } = DateTime.Today;

    /// <summary>仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>仓库名称（冗余，列表与流水展示免关联）</summary>
    [MaxLength(100)]
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>调整类型（盘点调整 / 报损 / 报溢）</summary>
    [MaxLength(20)]
    public string AdjustType { get; set; } = "盘点调整";

    /// <summary>差异总数量（正数=盘盈，负数=盘亏；后端复核）</summary>
    public decimal TotalDiffQuantity { get; set; }

    /// <summary>差异总金额（= 差异数量 × 成本单价；库存成本的调整依据）</summary>
    public decimal TotalDiffAmount { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>盘点明细</summary>
    public List<StockAdjustmentDetail> Details { get; set; } = new();
}

/// <summary>
/// 库存盘点/调整单明细（一行一个商品：账面数量 / 实盘数量 / 差异）
/// </summary>
public class StockAdjustmentDetail : BaseEntity
{
    /// <summary>盘点单 Id</summary>
    public long StockAdjustmentId { get; set; }

    /// <summary>盘点单号（冗余，报表与流水免关联）</summary>
    [MaxLength(50)]
    public string AdjustmentNo { get; set; } = string.Empty;

    /// <summary>行号（LineNo 在部分 SQL Server 实例上为保留字，改用 SortNo）</summary>
    public int SortNo { get; set; }

    /// <summary>商品 Id</summary>
    public long? ProductId { get; set; }

    /// <summary>商品编码（冗余）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>账面数量（系统库存）</summary>
    public decimal BookQuantity { get; set; }

    /// <summary>实盘数量</summary>
    public decimal ActualQuantity { get; set; }

    /// <summary>差异数量 = 实盘 - 账面（后端复核）</summary>
    public decimal DiffQuantity { get; set; }

    /// <summary>成本单价（差异金额的计价基准；留 0 时按当前加权平均成本计价）</summary>
    public decimal UnitCost { get; set; }

    /// <summary>差异金额 = 差异数量 × 成本单价（后端复核）</summary>
    public decimal DiffAmount { get; set; }

    /// <summary>备注（差异原因）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 仓库调拨单主表（ERP-009）
/// 业务口径：审核后调出仓减少、调入仓增加，两侧使用同一成本单价，
/// 因此「调出数量 = 调入数量」「调出金额 = 调入金额」，总数量与总金额均守恒。
/// </summary>
public class StockTransfer : BaseEntity
{
    /// <summary>调拨单号（单据字轨生成，如 DB2609230001）</summary>
    [Required(AllowEmptyStrings = true), MaxLength(50)]
    public string TransferNo { get; set; } = string.Empty;

    /// <summary>调拨日期</summary>
    public DateTime TransferDate { get; set; } = DateTime.Today;

    /// <summary>调出仓库 Id</summary>
    public long FromWarehouseId { get; set; }

    /// <summary>调出仓库名称（冗余）</summary>
    [MaxLength(100)]
    public string FromWarehouseName { get; set; } = string.Empty;

    /// <summary>调入仓库 Id</summary>
    public long ToWarehouseId { get; set; }

    /// <summary>调入仓库名称（冗余）</summary>
    [MaxLength(100)]
    public string ToWarehouseName { get; set; } = string.Empty;

    /// <summary>调拨总数量</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>调拨总金额（按调出仓加权平均成本计价）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>调拨明细</summary>
    public List<StockTransferDetail> Details { get; set; } = new();
}

/// <summary>
/// 仓库调拨单明细（一行一个商品：数量 × 成本单价 = 金额）
/// </summary>
public class StockTransferDetail : BaseEntity
{
    /// <summary>调拨单 Id</summary>
    public long StockTransferId { get; set; }

    /// <summary>调拨单号（冗余）</summary>
    [MaxLength(50)]
    public string TransferNo { get; set; } = string.Empty;

    /// <summary>行号</summary>
    public int SortNo { get; set; }

    /// <summary>商品 Id</summary>
    public long? ProductId { get; set; }

    /// <summary>商品编码（冗余）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>调拨数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>成本单价（留 0 时按调出仓当前加权平均成本计价）</summary>
    public decimal UnitCost { get; set; }

    /// <summary>调拨金额 = 数量 × 成本单价（后端复核）</summary>
    public decimal Amount { get; set; }

    /// <summary>批次号（可空，便于按批次追溯）</summary>
    [MaxLength(50)]
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 销售退货单主表（ERP-009）
/// 业务口径：客户退回的商品重新入库（按原出库成本计价），可关联来源销售出库单做溯源；
/// 审核后库存增加并写入库存流水，销审后按流水逐笔冲销。
/// </summary>
public class SalesReturn : BaseEntity
{
    /// <summary>退货单号（单据字轨生成，如 XTH2609230001）</summary>
    [Required(AllowEmptyStrings = true), MaxLength(50)]
    public string ReturnNo { get; set; } = string.Empty;

    /// <summary>退货日期</summary>
    public DateTime ReturnDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>退货入库仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>退货入库仓库名称（冗余）</summary>
    [MaxLength(100)]
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>来源销售出库单 Id（可空：允许无来源单据直接退货）</summary>
    public long? SourceStockOutId { get; set; }

    /// <summary>来源销售出库单号（冗余，用于追溯与成本回取）</summary>
    [MaxLength(50)]
    public string SourceStockOutNo { get; set; } = string.Empty;

    /// <summary>退货原因（质量 / 型号 / 客户取消等）</summary>
    [MaxLength(200)]
    public string ReturnReason { get; set; } = string.Empty;

    /// <summary>退货总数量</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>退货总金额（= 数量 × 退货单价）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>退货明细</summary>
    public List<SalesReturnDetail> Details { get; set; } = new();
}

/// <summary>
/// 销售退货单明细（一行一个商品：数量 × 退货单价 = 金额；成本单价用于入库成本）
/// </summary>
public class SalesReturnDetail : BaseEntity
{
    /// <summary>退货单 Id</summary>
    public long SalesReturnId { get; set; }

    /// <summary>退货单号（冗余）</summary>
    [MaxLength(50)]
    public string ReturnNo { get; set; } = string.Empty;

    /// <summary>行号</summary>
    public int SortNo { get; set; }

    /// <summary>商品 Id</summary>
    public long? ProductId { get; set; }

    /// <summary>商品编码（冗余）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>退货数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>退货单价（对外结算价，不影响库存成本）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>退货金额 = 数量 × 退货单价</summary>
    public decimal Amount { get; set; }

    /// <summary>入库成本单价（留 0 时按来源出库流水成本，再取当前加权平均成本）</summary>
    public decimal UnitCost { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 采购退货单主表（ERP-009）
/// 业务口径：把已入库的采购商品退回供应商，审核后库存减少（按来源入库成本计价），
/// 可关联来源采购入库单做溯源；销审后按流水逐笔冲销。
/// </summary>
public class PurchaseReturn : BaseEntity
{
    /// <summary>退货单号（单据字轨生成，如 CTH2609230001）</summary>
    [Required(AllowEmptyStrings = true), MaxLength(50)]
    public string ReturnNo { get; set; } = string.Empty;

    /// <summary>退货日期</summary>
    public DateTime ReturnDate { get; set; } = DateTime.Today;

    /// <summary>供应商 Id</summary>
    public long? SupplierId { get; set; }

    /// <summary>供应商名称（冗余）</summary>
    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>退货出库仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>退货出库仓库名称（冗余）</summary>
    [MaxLength(100)]
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>来源采购入库单 Id（可空：允许无来源单据直接退货）</summary>
    public long? SourceStockInId { get; set; }

    /// <summary>来源采购入库单号（冗余，用于追溯与成本回取）</summary>
    [MaxLength(50)]
    public string SourceStockInNo { get; set; } = string.Empty;

    /// <summary>退货原因（质量 / 交期 / 规格不符等）</summary>
    [MaxLength(200)]
    public string ReturnReason { get; set; } = string.Empty;

    /// <summary>退货总数量</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>退货总金额（= 数量 × 退货单价）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>退货明细</summary>
    public List<PurchaseReturnDetail> Details { get; set; } = new();
}

/// <summary>
/// 采购退货单明细（一行一个商品：数量 × 退货单价 = 金额；成本单价用于出库成本）
/// </summary>
public class PurchaseReturnDetail : BaseEntity
{
    /// <summary>退货单 Id</summary>
    public long PurchaseReturnId { get; set; }

    /// <summary>退货单号（冗余）</summary>
    [MaxLength(50)]
    public string ReturnNo { get; set; } = string.Empty;

    /// <summary>行号</summary>
    public int SortNo { get; set; }

    /// <summary>商品 Id</summary>
    public long? ProductId { get; set; }

    /// <summary>商品编码（冗余）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>退货数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>退货单价（对供应商结算价）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>退货金额 = 数量 × 退货单价</summary>
    public decimal Amount { get; set; }

    /// <summary>出库成本单价（留 0 时按来源入库流水成本，再取当前加权平均成本）</summary>
    public decimal UnitCost { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}


/// <summary>
/// 库存流水（ERP-009）：每一次库存变动的可审计记录，同时是库存估价的成本基准。
/// <para>设计要点：</para>
/// <list type="bullet">
/// <item>一笔移动记录 = 一个仓库 + 一个商品的单向变动（调拨产生「调出 + 调入」两笔）；</item>
/// <item><see cref="UnitCost"/> / <see cref="Amount"/> 持久化成本基准，避免估价被后续业务反向改写；</item>
/// <item><see cref="BalanceQuantity"/> / <see cref="BalanceAmount"/> / <see cref="BalanceAverageCost"/>
///       记录移动后的库存快照，便于逐笔核对库存数量与库存金额；</item>
/// <item>销审不删除原流水，而是追加一条 <see cref="IsReversal"/> 红字流水并把原流水标记为
///       <see cref="IsReversed"/>，保证「已审核单据恰好改一次库存」「冲销有据可查」。</item>
/// </list>
/// </summary>
public class StockMovement : BaseEntity
{
    /// <summary>移动日期</summary>
    public DateTime MovementDate { get; set; } = DateTime.Today;

    /// <summary>移动类型（采购入库 / 销售出库 / 盘点调整 / 调拨 / 退货）</summary>
    public InventoryMovementType MovementType { get; set; }

    /// <summary>来源单据类型（实体名：StockAdjustment / StockTransfer / SalesReturn / PurchaseReturn）</summary>
    [MaxLength(50)]
    public string SourceDocType { get; set; } = string.Empty;

    /// <summary>来源单据 Id</summary>
    public long SourceDocId { get; set; }

    /// <summary>来源单据号（冗余，按单据追溯流水）</summary>
    [MaxLength(50)]
    public string SourceDocNo { get; set; } = string.Empty;

    /// <summary>仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>仓库名称（冗余）</summary>
    [MaxLength(100)]
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>商品 Id</summary>
    public long? ProductId { get; set; }

    /// <summary>商品编码（冗余）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>方向（1=入库增加，-1=出库减少）</summary>
    public int Direction { get; set; }

    /// <summary>移动数量（始终为正数，方向由 Direction 表达）</summary>
    public decimal Quantity { get; set; }

    /// <summary>本次移动的成本单价（成本基准，持久化；出库取移动加权平均成本）</summary>
    public decimal UnitCost { get; set; }

    /// <summary>带符号移动金额 = 方向 × 数量 × 成本单价</summary>
    public decimal Amount { get; set; }

    /// <summary>移动后库存数量</summary>
    public decimal BalanceQuantity { get; set; }

    /// <summary>移动后库存金额</summary>
    public decimal BalanceAmount { get; set; }

    /// <summary>移动后加权平均成本</summary>
    public decimal BalanceAverageCost { get; set; }

    /// <summary>是否为冲销（红字）流水</summary>
    public bool IsReversal { get; set; }

    /// <summary>被冲销的原流水 Id（仅红字流水有值）</summary>
    public long? ReversalOfMovementId { get; set; }

    /// <summary>该流水是否已被冲销（原流水被冲销后置 1，防止重复冲销）</summary>
    public bool IsReversed { get; set; }

    /// <summary>备注（冲销原因等）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

