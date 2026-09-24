using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    // ============ ERP-036：指定货代（复用「其他资料」数据字典 InfoType=Forwarder，仅作主数据指引） ============

    /// <summary>
    /// 指定货代 Id（引用 <see cref="BaseOtherInfo"/> 中 <c>InfoType = Forwarder</c> 的字典项；可空，不建外键）。
    /// <para>仅记录「该客户通常走哪家货代」的指引，不会自动写入订舱 / 装柜 / 报关 / 费用单据，
    /// 也不授予任何外部货代系统的访问权。</para>
    /// </summary>
    public long? ForwarderId { get; set; }

    /// <summary>
    /// 指定货代名称快照（由服务端按字典项权威写入，客户端提交的自由文本一律不被采信）。
    /// 保留快照是为了字典项后来被停用 / 删除时，历史客户资料仍能显示当时的货代名称。
    /// </summary>
    [MaxLength(100)]
    public string ForwarderName { get; set; } = string.Empty;

    /// <summary>
    /// 指定货代引用是否仍可选用（**非持久化列**，读取时由服务端标注）：
    /// 未指定货代，或指定货代仍是「启用、未删除、类型为 Forwarder」的字典项时为 <c>true</c>；
    /// 字典项被删除 / 停用 / 改类型后为 <c>false</c>（历史引用照常显示，但不允许再次选用）。
    /// </summary>
    [NotMapped]
    public bool ForwarderAvailable { get; set; } = true;
}
