using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 询价单主表
/// </summary>
public class Inquiry : BaseEntity
{
    /// <summary>询价单号</summary>
    [Required, MaxLength(50)]
    public string InquiryNo { get; set; } = string.Empty;

    /// <summary>询价日期</summary>
    public DateTime InquiryDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>联系人</summary>
    [MaxLength(50)]
    public string ContactPerson { get; set; } = string.Empty;

    /// <summary>联系电话</summary>
    [MaxLength(50)]
    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>业务员 Id（员工）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>报价有效期（天）</summary>
    public int ValidDays { get; set; } = 30;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<InquiryDetail> Details { get; set; } = new();
}

/// <summary>
/// 询价单明细
/// </summary>
public class InquiryDetail : BaseEntity
{
    /// <summary>询价单 Id</summary>
    public long InquiryId { get; set; }

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

    /// <summary>目标价</summary>
    public decimal TargetPrice { get; set; }

    /// <summary>期望交期</summary>
    public DateTime? ExpectedDeliveryDate { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
