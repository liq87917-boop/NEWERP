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

    // ============ 外贸合同与运输信息（ERP-008 新增：外销合同必备条款，全部可空/带默认值以兼容历史单据） ============

    /// <summary>客户 PO 号（买方订单号，用于客户对账与报关核对）</summary>
    [MaxLength(50)]
    public string CustomerPoNo { get; set; } = string.Empty;

    /// <summary>外销合同号</summary>
    [MaxLength(50)]
    public string ContractNo { get; set; } = string.Empty;

    /// <summary>价格条款 / 贸易术语（FOB / CIF / CFR / EXW / DDP…）</summary>
    [MaxLength(50)]
    public string TradeTerms { get; set; } = string.Empty;

    /// <summary>目的港（文本；与 PortId 港口字典并存，便于合同与提单直接打印）</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>收货人 Consignee（提单用，默认取客户档案）</summary>
    [MaxLength(300)]
    public string Consignee { get; set; } = string.Empty;

    /// <summary>通知人 Notify Party（提单用）</summary>
    [MaxLength(300)]
    public string NotifyParty { get; set; } = string.Empty;

    /// <summary>唛头 Shipping Marks（多行文本）</summary>
    [MaxLength(500)]
    public string ShippingMarks { get; set; } = string.Empty;

    /// <summary>来源报价单 Id（报价单 → 销售订单追溯）</summary>
    public long? SourceQuotationId { get; set; }

    /// <summary>来源报价单号（冗余，报表与追溯免关联）</summary>
    [MaxLength(50)]
    public string SourceQuotationNo { get; set; } = string.Empty;

    /// <summary>来源形式发票 PI Id（PI → 销售订单追溯）</summary>
    public long? SourcePiId { get; set; }

    /// <summary>来源形式发票 PI 号（冗余）</summary>
    [MaxLength(50)]
    public string SourcePiNo { get; set; } = string.Empty;

    /// <summary>出口方式（0110 一般贸易 / 1039 市场采购 / 9610 跨境电商 / 9710 跨境电商 B2B）</summary>
    [MaxLength(20)]
    public string ExportMode { get; set; } = string.Empty;

    /// <summary>佣金 / 回佣比例（%，0~100）</summary>
    public decimal CommissionRatio { get; set; }

    /// <summary>业务性质（自营出口 / 代理出口 / 内销）</summary>
    [MaxLength(20)]
    public string BusinessNature { get; set; } = string.Empty;

    /// <summary>是否分批出货（分批出货时按明细行交货日期跟踪）</summary>
    public bool SplitShipment { get; set; }

    /// <summary>验货要求（如 SGS / 客户验货 / 免验）</summary>
    [MaxLength(500)]
    public string InspectionRequirement { get; set; } = string.Empty;

    /// <summary>包装要求（如 12 pcs/箱、客户指定彩盒）</summary>
    [MaxLength(500)]
    public string PackagingRequirement { get; set; } = string.Empty;

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
