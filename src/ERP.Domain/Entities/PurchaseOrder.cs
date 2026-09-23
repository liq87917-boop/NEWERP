using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 采购订单主表
/// </summary>
public class PurchaseOrder : BaseEntity
{
    /// <summary>采购单号</summary>
    [Required, MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期</summary>
    public DateTime OrderDate { get; set; } = DateTime.Today;

    /// <summary>供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>采购员 Id（员工）</summary>
    public long? BuyerId { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.CNY;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>订单总额</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>付款条件</summary>
    [MaxLength(200)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>交货日期</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>起运港 Id</summary>
    public long? PortId { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<PurchaseOrderDetail> Details { get; set; } = new();
}

/// <summary>
/// 采购订单明细
/// </summary>
public class PurchaseOrderDetail : BaseEntity
{
    /// <summary>采购订单 Id</summary>
    public long PurchaseOrderId { get; set; }

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

    /// <summary>单价</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>金额</summary>
    public decimal Amount { get; set; }

    /// <summary>交货日期</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
