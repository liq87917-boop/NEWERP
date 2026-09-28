namespace ERP.Application.DTOs;

/// <summary>
/// 供应商比价 → 采购订单价格差异查询条件（ERP-105，全部为只读筛选参数）。
/// 只统计「已转采购订单」的比价行，按报价日期 + 可选供应商筛选并稳定分页。
/// </summary>
public sealed class PurchaseQuoteOrderPriceVarianceQuery
{
    /// <summary>报价日期开始（含当天；留空 = 不限）</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>报价日期结束（含当天；留空 = 不限）</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>供应商比价 → 采购订单价格差异视图（只读派生）</summary>
public sealed class PurchaseQuoteOrderPriceVarianceView
{
    /// <summary>符合筛选条件的「已转采购订单」比价行总数（分页前）</summary>
    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }

    /// <summary>是否命中分页截断（true = 本页之外仍有更多比价行）</summary>
    public bool Truncated { get; init; }

    /// <summary>本页已核对（口径一致且唯一定位）行数</summary>
    public int ResolvedCount { get; init; }

    /// <summary>本页未解决（歧义 / 口径不一致 / 陈旧链接 / 未链接）行数</summary>
    public int UnresolvedCount { get; init; }

    /// <summary>口径与边界说明（与 <see cref="ERP.Application.Services.PurchaseQuoteOrderPriceVarianceRules.RuleText"/> 同源）</summary>
    public string RuleText { get; init; } = string.Empty;

    /// <summary>无匹配时显式提示</summary>
    public string EmptyText { get; init; } = string.Empty;

    public List<PurchaseQuoteOrderPriceVarianceRow> Rows { get; init; } = new();
}

/// <summary>一条「来源比价行 ↔ 采购订单明细」的对照行：来源证据 + 订单证据 + 差异结论（未解决时无数值差异）</summary>
public sealed class PurchaseQuoteOrderPriceVarianceRow
{
    // ==================== 来源（比价行）证据 ====================

    public long QuoteId { get; set; }
    public string QuoteNo { get; set; } = string.Empty;
    public DateTime QuoteDate { get; set; }
    public long? SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Spec { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public bool TaxIncluded { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuotePrice { get; set; }
    public decimal QuoteAmount { get; set; }

    /// <summary>比价行持久化的订单链接（转换后写回的采购单号；为空 = 未链接）</summary>
    public string RefOrderNo { get; set; } = string.Empty;

    /// <summary>比价行状态（已转采购订单）</summary>
    public string QuoteStatus { get; set; } = string.Empty;

    // ==================== 采购订单证据 ====================

    public string OrderNo { get; set; } = string.Empty;
    public DateTime? OrderDate { get; set; }
    public long? OrderDetailId { get; set; }
    public string OrderSpec { get; set; } = string.Empty;
    public string OrderUnit { get; set; } = string.Empty;
    public string OrderCurrency { get; set; } = string.Empty;
    public bool? OrderTaxIncluded { get; set; }
    public decimal? OrderUnitPrice { get; set; }
    public decimal? OrderAmount { get; set; }

    // ==================== 差异结论 ====================

    /// <summary>true = 口径一致且唯一定位，可计算价格差异；false = 未解决（无数值差异）</summary>
    public bool Resolved { get; set; }

    /// <summary>单价差 = 采购单价 − 报价单价（仅 resolved 时有值）</summary>
    public decimal? UnitPriceDelta { get; set; }

    /// <summary>金额差 = 采购金额 − 报价总额（仅 resolved 时有值）</summary>
    public decimal? AmountDelta { get; set; }

    /// <summary>未解决原因（歧义 / 口径不一致 / 陈旧链接 / 未链接 / 明细缺失等；resolved 时为空）</summary>
    public string Reason { get; set; } = string.Empty;
}
