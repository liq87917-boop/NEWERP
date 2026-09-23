using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 预装柜单主表
/// </summary>
public class ContainerPreLoading : BaseEntity
{
    /// <summary>预装柜单号</summary>
    [Required, MaxLength(50)]
    public string PreLoadingNo { get; set; } = string.Empty;

    /// <summary>装柜日期</summary>
    public DateTime LoadingDate { get; set; } = DateTime.Today;

    /// <summary>订柜 Id</summary>
    public long? BookingId { get; set; }

    /// <summary>柜号</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>封条号</summary>
    [MaxLength(50)]
    public string SealNo { get; set; } = string.Empty;

    /// <summary>总箱数</summary>
    public decimal TotalCartons { get; set; }

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
    public List<ContainerPreLoadingDetail> Details { get; set; } = new();
}

/// <summary>
/// 预装柜单明细
/// </summary>
public class ContainerPreLoadingDetail : BaseEntity
{
    /// <summary>预装柜单 Id</summary>
    public long PreLoadingId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>箱数</summary>
    public decimal Cartons { get; set; }

    /// <summary>毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 装柜清单主表
/// </summary>
public class ContainerLoadingList : BaseEntity
{
    /// <summary>装柜清单号</summary>
    [Required, MaxLength(50)]
    public string LoadingListNo { get; set; } = string.Empty;

    /// <summary>预装柜单 Id</summary>
    public long? PreLoadingId { get; set; }

    /// <summary>装柜日期</summary>
    public DateTime LoadingDate { get; set; } = DateTime.Today;

    /// <summary>柜号</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>唛头</summary>
    [MaxLength(200)]
    public string ShippingMark { get; set; } = string.Empty;

    /// <summary>总箱数</summary>
    public decimal TotalCartons { get; set; }

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
    public List<ContainerLoadingDetail> Details { get; set; } = new();
}

/// <summary>
/// 装柜清单明细
/// </summary>
public class ContainerLoadingDetail : BaseEntity
{
    /// <summary>装柜清单 Id</summary>
    public long LoadingListId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>箱数</summary>
    public decimal Cartons { get; set; }

    /// <summary>毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
