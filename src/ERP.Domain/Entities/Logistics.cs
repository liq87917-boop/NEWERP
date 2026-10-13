using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 采购入库单主表
/// </summary>
public class StockIn : BaseEntity
{
    /// <summary>
    /// 入库单号（服务端权威生成：新增入口一律在实时授权、来源归属与累计容量校验通过之后由
    /// <c>IDocumentNumberService</c> 预约覆盖，调用方提交值不参与持久化）。
    /// <para>ERP-477：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-428 订单号 / ERP-474 询价单号同源口径）。
    /// 手工录入表单不提交入库单号，MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填
    /// （允许空串）；而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略入库单号」在模型绑定 /
    /// 模型校验阶段直接拒绝为 HTTP 400 <c>{"errors":{"StockInNo":[...]}}</c>，服务端权威单号根本没有机会生成。
    /// 移除显式特性后：省略 / 空串照常绑定并进入已授权的 <c>StockInController.Create</c>，服务端仍按唯一权威口径
    /// 覆盖入库单号与状态；数据库列仍是 NOT NULL（非空 CLR 引用类型 + 唯一索引），长度仍受
    /// <see cref="MaxLengthAttribute"/>(50) 约束，非法 / 越权请求的校验顺序不变（授权与来源校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
    public string StockInNo { get; set; } = string.Empty;

    /// <summary>入库日期</summary>
    public DateTime StockInDate { get; set; } = DateTime.Today;

    /// <summary>采购订单 Id</summary>
    public long? PurchaseOrderId { get; set; }

    /// <summary>供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>总数量</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>总毛重（kg）</summary>
    public decimal TotalWeight { get; set; }

    /// <summary>总体积（m³）</summary>
    public decimal TotalVolume { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<StockInDetail> Details { get; set; } = new();
}

/// <summary>
/// 采购入库单明细
/// </summary>
public class StockInDetail : BaseEntity
{
    /// <summary>入库单 Id</summary>
    public long StockInId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>批次号</summary>
    [MaxLength(50)]
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 销售出库单主表
/// </summary>
public class StockOut : BaseEntity
{
    /// <summary>出库单号</summary>
    [Required, MaxLength(50)]
    public string StockOutNo { get; set; } = string.Empty;

    /// <summary>出库日期</summary>
    public DateTime StockOutDate { get; set; } = DateTime.Today;

    /// <summary>销售订单 Id</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>总数量</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>总毛重（kg）</summary>
    public decimal TotalWeight { get; set; }

    /// <summary>总体积（m³）</summary>
    public decimal TotalVolume { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<StockOutDetail> Details { get; set; } = new();
}

/// <summary>
/// 销售出库单明细
/// </summary>
public class StockOutDetail : BaseEntity
{
    /// <summary>出库单 Id</summary>
    public long StockOutId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>批次号</summary>
    [MaxLength(50)]
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 库存表（按仓库 + 商品汇总）
/// </summary>
public class Stock : BaseEntity
{
    /// <summary>仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>当前库存数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>可用数量</summary>
    public decimal AvailableQuantity { get; set; }

    /// <summary>锁定数量（已占用待出库）</summary>
    public decimal LockedQuantity { get; set; }

    /// <summary>
    /// 移动加权平均成本单价（ERP-009：库存成本基准）。
    /// 由库存流水按「移动加权平均法」维护，保留 6 位小数；无库存时为 0。
    /// </summary>
    public decimal AverageCost { get; set; }

    /// <summary>
    /// 库存金额（= 数量 × 加权平均成本；出库按该金额核减，保留 4 位小数）。
    /// 与 <see cref="AverageCost"/>、库存流水中的 BalanceAmount 三者互相校验，保证估价可追溯。
    /// </summary>
    public decimal TotalCost { get; set; }
}
