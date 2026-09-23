using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 采购入库单主表
/// </summary>
public class StockIn : BaseEntity
{
    /// <summary>入库单号</summary>
    [Required, MaxLength(50)]
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
}
