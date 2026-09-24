using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 供应商资料
/// </summary>
public class BaseSupplier : BaseEntity
{
    /// <summary>供应商编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    /// <summary>供应商名称</summary>
    [Required, MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

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

    /// <summary>开户银行</summary>
    [MaxLength(200)]
    public string BankName { get; set; } = string.Empty;

    /// <summary>银行账号</summary>
    [MaxLength(100)]
    public string BankAccount { get; set; } = string.Empty;

    /// <summary>状态（1=启用，0=停用）</summary>
    public int Status { get; set; } = 1;

    // ============ 阶段 1 补齐：档口采购/供应商结算信息（全部可空） ============

    /// <summary>供应商类型（工厂 / 档口 / 贸易商 / 货代 / 报关行）</summary>
    [MaxLength(20)]
    public string SupplierType { get; set; } = string.Empty;

    /// <summary>档口位置（义乌国际商贸城：如「一区 1F-0123」）</summary>
    [MaxLength(100)]
    public string BoothLocation { get; set; } = string.Empty;

    /// <summary>主营品类</summary>
    [MaxLength(100)]
    public string MainCategory { get; set; } = string.Empty;

    /// <summary>结算方式（现结 / 月结30天 / 月结60天 等）</summary>
    [MaxLength(50)]
    public string SettlementMethod { get; set; } = string.Empty;

    /// <summary>开票能力（不票 / 普票 / 专票）—— 当前业务不要求发票，字段预留</summary>
    [MaxLength(20)]
    public string InvoiceAbility { get; set; } = "不票";

    /// <summary>开票税率（%）</summary>
    public decimal TaxRate { get; set; }

    /// <summary>常规交期（天）</summary>
    public int DeliveryDays { get; set; }

    /// <summary>返点/佣金比例（%）</summary>
    public decimal RebateRatio { get; set; }

    /// <summary>微信 / WhatsApp</summary>
    [MaxLength(50)]
    public string WeChat { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ ERP-038：供货商品货源关系（可选子表 BaseProductSuppliers 的读取标注） ============

    /// <summary>
    /// 该供应商**启用中**的货源关系条数（**非持久化列**，列表 / 详情读取时由服务端标注）；
    /// 没有维护任何货源关系的历史供应商为 0，行为与历史完全一致。
    /// </summary>
    [NotMapped]
    public int SourcingCount { get; set; }

    /// <summary>
    /// 该供应商的货源关系总条数（含停用，**非持久化列**，读取时由服务端标注）：
    /// 与 <see cref="SourcingCount"/> 的差值即「已停用货源关系数」，历史关系仍可读。
    /// </summary>
    [NotMapped]
    public int SourcingTotalCount { get; set; }
}
