using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 样品管理（打样 / 寄样 / 借样 / 客户来样）
/// 用途：登记样品从「打样 → 寄出 → 客户反馈 → 是否转订单」的全过程，
///       并记录样品费与是否已结算，解决"样品寄了多少、花了多少、客户反馈如何"说不清的问题。
/// 场景：义乌饰品、文具、圣诞用品等品类打样频繁，样品费常需单独结算。
/// 说明：单表结构（不涉及主子表与存储过程），使用通用 CRUD 维护。
/// </summary>
public class Sample : BaseEntity
{
    /// <summary>样品编号（唯一）</summary>
    [Required, MaxLength(50)]
    public string SampleNo { get; set; } = string.Empty;

    /// <summary>样品日期</summary>
    public DateTime SampleDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>商品 Id（可选，引用商品资料）</summary>
    public long? ProductId { get; set; }

    /// <summary>商品名称 / 样品名称</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>样品类型（打样 / 寄样 / 借样 / 客户来样）</summary>
    [MaxLength(30)]
    public string SampleType { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>样品费（0 = 免费）</summary>
    public decimal SampleFee { get; set; }

    /// <summary>币种</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>样品费是否已结算（收/付）</summary>
    public bool FeeSettled { get; set; }

    /// <summary>寄出日期</summary>
    public DateTime? SendDate { get; set; }

    /// <summary>快递公司</summary>
    [MaxLength(50)]
    public string Express { get; set; } = string.Empty;

    /// <summary>运单号</summary>
    [MaxLength(50)]
    public string TrackingNo { get; set; } = string.Empty;

    /// <summary>客户反馈结果（待反馈 / 满意 / 需修改 / 已下单 / 未采用）</summary>
    [MaxLength(30)]
    public string Result { get; set; } = "待反馈";

    /// <summary>跟进业务员 Id</summary>
    public long? SalesmanId { get; set; }

    /// <summary>跟进业务员姓名（冗余）</summary>
    [MaxLength(50)]
    public string SalesmanName { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
