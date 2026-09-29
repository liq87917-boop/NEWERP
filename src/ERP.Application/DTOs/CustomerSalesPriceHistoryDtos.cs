namespace ERP.Application.DTOs;

/// <summary>
/// 客户销售订单价格历史查询条件（ERP-108，全部为只读筛选参数）。
/// 按商品（必填）过滤已审核销售订单明细价格，可再按客户与订单日期区间筛选并稳定分页。
/// </summary>
public sealed class CustomerSalesPriceHistoryQuery
{
    /// <summary>商品 Id（必填）</summary>
    public long? ProductId { get; set; }

    /// <summary>客户筛选（留空 = 全部客户）</summary>
    public long? CustomerId { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>客户销售订单价格历史视图（只读派生）</summary>
public sealed class CustomerSalesPriceHistoryView
{
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;

    /// <summary>符合筛选条件的已审核销售订单明细行总数（分页前）</summary>
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }

    /// <summary>是否命中分页截断（true = 本页之外仍有更多明细行）</summary>
    public bool Truncated { get; init; }
    public int GroupCount { get; init; }

    /// <summary>口径与边界说明（与 <see cref="ERP.Application.Services.CustomerSalesPriceHistoryRules.RuleText"/> 同源）</summary>
    public string RuleText { get; init; } = string.Empty;

    /// <summary>无匹配时显式提示</summary>
    public string EmptyText { get; init; } = string.Empty;

    public List<CustomerSalesPriceHistoryGroup> Groups { get; init; } = new();
}

/// <summary>
/// 一个价格历史口径组：组内「客户 + 商品 + 规格 + 单位 + 币种」完全一致；
/// 口径不一致的证据（不同单位 / 币种 / 规格 / 客户）各自成组单列，绝不跨口径比较或合并。
/// </summary>
public sealed class CustomerSalesPriceHistoryGroup
{
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string GroupKey { get; init; } = string.Empty;
    public string BasisText { get; init; } = string.Empty;
    public int RowCount { get; set; }
    public List<CustomerSalesPriceHistoryRow> Rows { get; init; } = new();
}

/// <summary>一条价格历史行：保留来源订单 + 原始单价与贸易条款（绝不改写）</summary>
public sealed class CustomerSalesPriceHistoryRow
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>原始单价（只读回显，绝不重定价）</summary>
    public decimal UnitPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal Amount { get; init; }

    /// <summary>贸易条款（原始值保留，如 FOB / CIF / CFR / EXW / DDP）</summary>
    public string TradeTerms { get; init; } = string.Empty;
}
