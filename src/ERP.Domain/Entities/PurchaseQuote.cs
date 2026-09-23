using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 供应商报价比价表
/// 用途：同一个采购需求（如「某款饰品 5000 个」）向多家供应商（工厂 / 档口）询价，
///       每家一行报价记录，用相同的 <see cref="QuoteNo"/> 归为一批，便于横向比价与标记选中。
/// 场景：义乌小商品采购普遍存在"同一款多家档口报价"的情况，代理采购模式下还需记录为哪个客户询价。
/// 说明：单表结构（不涉及主子表与存储过程），使用通用 CRUD 维护。
/// </summary>
public class PurchaseQuote : BaseEntity
{
    /// <summary>比价批次号（同一需求的多次报价共用，如 PQ-20260918-001）</summary>
    [Required, MaxLength(50)]
    public string QuoteNo { get; set; } = string.Empty;

    /// <summary>报价日期</summary>
    public DateTime QuoteDate { get; set; } = DateTime.Today;

    /// <summary>商品 Id（引用商品资料）</summary>
    public long? ProductId { get; set; }

    /// <summary>商品名称（冗余，便于直接查看与导出）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>需求数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>供应商 Id</summary>
    public long? SupplierId { get; set; }

    /// <summary>供应商名称（冗余）</summary>
    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>供应商类型（工厂 / 档口 / 贸易商）</summary>
    [MaxLength(20)]
    public string SupplierType { get; set; } = string.Empty;

    /// <summary>供应商报价单价</summary>
    public decimal QuotePrice { get; set; }

    /// <summary>报价总额（= 单价 × 数量）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>币种（CNY / USD）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>报价是否含税</summary>
    public bool TaxIncluded { get; set; }

    /// <summary>交期（天）</summary>
    public int DeliveryDays { get; set; }

    /// <summary>起订量 MOQ</summary>
    public int MinOrderQty { get; set; }

    /// <summary>付款条件（现结 / 月结30天 …）</summary>
    [MaxLength(100)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>是否为最终选中供应商</summary>
    public bool IsSelected { get; set; }

    /// <summary>比价状态（待比较 / 已选中 / 已放弃）</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "待比较";

    /// <summary>为客户询价（代理采购场景）</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>关联销售订单号（可选）</summary>
    [MaxLength(50)]
    public string RefOrderNo { get; set; } = string.Empty;

    /// <summary>选中理由 / 备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
