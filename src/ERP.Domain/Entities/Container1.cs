using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 收货计划
/// </summary>
public class ContainerReceivingPlan : BaseEntity
{
    /// <summary>计划单号</summary>
    [Required, MaxLength(50)]
    public string PlanNo { get; set; } = string.Empty;

    /// <summary>计划日期</summary>
    public DateTime PlanDate { get; set; } = DateTime.Today;

    /// <summary>供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>订柜单号（关联订柜信息）</summary>
    [MaxLength(50)]
    public string BookingNo { get; set; } = string.Empty;

    /// <summary>柜型</summary>
    public ContainerType ContainerType { get; set; } = ContainerType.GP40;

    /// <summary>柜号</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>预计到货日期</summary>
    public DateTime? ExpectedArrivalDate { get; set; }

    /// <summary>目的港 Id</summary>
    public long? PortId { get; set; }

    /// <summary>目的地</summary>
    [MaxLength(200)]
    public string Destination { get; set; } = string.Empty;

    /// <summary>总件数</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 订柜信息
/// </summary>
public class ContainerBooking : BaseEntity
{
    /// <summary>订柜单号</summary>
    [Required, MaxLength(50)]
    public string BookingNo { get; set; } = string.Empty;

    /// <summary>订柜日期</summary>
    public DateTime BookingDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>供应商 Id</summary>
    public long? SupplierId { get; set; }

    /// <summary>柜型</summary>
    public ContainerType ContainerType { get; set; } = ContainerType.GP40;

    /// <summary>船公司/物流公司</summary>
    [MaxLength(200)]
    public string ShippingCompany { get; set; } = string.Empty;

    /// <summary>航次</summary>
    [MaxLength(100)]
    public string VoyageNo { get; set; } = string.Empty;

    /// <summary>开船日期</summary>
    public DateTime? SailingDate { get; set; }

    /// <summary>起运港</summary>
    [MaxLength(100)]
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>目的港</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
