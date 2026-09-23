using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 销售订单主表
/// </summary>
public class SalesOrder : BaseEntity
{
    /// <summary>订单号</summary>
    [Required, MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期</summary>
    public DateTime OrderDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>业务员 Id</summary>
    public long? SalesmanId { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>订单总额</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>定金比例（%）</summary>
    public decimal DepositRatio { get; set; }

    /// <summary>定金金额</summary>
    public decimal DepositAmount { get; set; }

    /// <summary>付款条件</summary>
    [MaxLength(200)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>交货日期</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>运输方式</summary>
    [MaxLength(100)]
    public string ShippingMethod { get; set; } = string.Empty;

    /// <summary>目的港 Id（BaseOtherInfo 港口）</summary>
    public long? PortId { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<SalesOrderDetail> Details { get; set; } = new();
}

/// <summary>
/// 销售订单明细
/// </summary>
public class SalesOrderDetail : BaseEntity
{
    /// <summary>销售订单 Id</summary>
    public long SalesOrderId { get; set; }

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
