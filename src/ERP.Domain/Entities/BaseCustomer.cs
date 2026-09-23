using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 客户资料
/// </summary>
public class BaseCustomer : BaseEntity
{
    /// <summary>客户编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称</summary>
    [Required, MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>英文名称</summary>
    [MaxLength(200)]
    public string EnglishName { get; set; } = string.Empty;

    /// <summary>联系人</summary>
    [MaxLength(50)]
    public string ContactPerson { get; set; } = string.Empty;

    /// <summary>联系电话</summary>
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>邮箱</summary>
    [MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    /// <summary>国家/地区</summary>
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;

    /// <summary>详细地址</summary>
    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    /// <summary>付款条件</summary>
    [MaxLength(200)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>信用额度</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>定金比例（%），销售订单选择客户时自动带出</summary>
    public decimal DepositRatio { get; set; }

    /// <summary>税号</summary>
    [MaxLength(100)]
    public string TaxNumber { get; set; } = string.Empty;

    /// <summary>业务员 Id（引用员工表 Oid）</summary>
    public long? EmpId { get; set; }

    /// <summary>状态（1=启用，0=停用）</summary>
    public int Status { get; set; } = 1;

    // ============ 阶段 1 补齐：义乌外贸业务必填信息（全部可空，不阻塞历史数据） ============

    /// <summary>业务性质（自营出口 / 代理出口 / 内销）</summary>
    [MaxLength(20)]
    public string BusinessNature { get; set; } = string.Empty;

    /// <summary>默认币种（如 USD / EUR）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>结算方式（按客户协商，如 T/T、L/C、D/P，自由填写）</summary>
    [MaxLength(100)]
    public string SettlementMethod { get; set; } = string.Empty;

    /// <summary>贸易条款（FOB / CIF / EXW 等）</summary>
    [MaxLength(50)]
    public string TradeTerms { get; set; } = string.Empty;

    /// <summary>目的港</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>收货人 Consignee（提单/单证用）</summary>
    [MaxLength(300)]
    public string Consignee { get; set; } = string.Empty;

    /// <summary>通知人 Notify Party（提单用）</summary>
    [MaxLength(300)]
    public string NotifyParty { get; set; } = string.Empty;

    /// <summary>默认唛头（Shipping Mark）</summary>
    [MaxLength(300)]
    public string DefaultShippingMark { get; set; } = string.Empty;

    /// <summary>账期天数（0=现结；按客户协商，可为空）</summary>
    public int? CreditDays { get; set; }

    /// <summary>佣金/回佣比例（%）</summary>
    public decimal CommissionRatio { get; set; }

    /// <summary>客户等级（A / B / C 或自定义）</summary>
    [MaxLength(20)]
    public string CustomerLevel { get; set; } = string.Empty;

    /// <summary>信用状态（正常 / 预警 / 暂停）</summary>
    [MaxLength(20)]
    public string CreditStatus { get; set; } = "正常";

    /// <summary>客户来源（展会 / 平台 / 转介绍 等）</summary>
    [MaxLength(50)]
    public string Source { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
