using ERP.Application.Common;

namespace ERP.Application.DTOs;

/// <summary>
/// 客户销项发票收款时效证据查询条件（ERP-111，全部为只读筛选参数）。
/// 只筛选「未删除且已登记」的客户销项发票证据，并按客户 / 开票日期区间 / 关键字过滤。
/// </summary>
public sealed class CustomerInvoiceCollectionTimingQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多张发票）</summary>
    public const int MaxPageSize = 200;

    /// <summary>客户筛选（留空 = 全部客户；不同客户绝不合并汇总）</summary>
    public long? CustomerId { get; set; }

    /// <summary>开票日期开始（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateFrom { get; set; }

    /// <summary>开票日期结束（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateTo { get; set; }

    /// <summary>关键字（匹配发票号码 / 发票代码 / 客户编码 / 客户名称；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// 归一化并校验：分页参数钳制到有界范围、关键字去除首尾空白、开票日期归一到当天；
    /// 开票日期区间倒置时直接抛业务异常（参数错误），不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        if (InvoiceDateFrom.HasValue) InvoiceDateFrom = InvoiceDateFrom.Value.Date;
        if (InvoiceDateTo.HasValue) InvoiceDateTo = InvoiceDateTo.Value.Date;
        if (InvoiceDateFrom.HasValue && InvoiceDateTo.HasValue && InvoiceDateFrom.Value > InvoiceDateTo.Value)
            throw BusinessException.InvalidParameter("开票日期区间不得倒置：开始日期不能晚于结束日期");
    }
}

/// <summary>单张已登记发票的收款时效行（只读派生，不落库）</summary>
public sealed class CustomerInvoiceCollectionTimingItem
{
    public long InvoiceId { get; init; }
    public string InvoiceType { get; init; } = string.Empty;
    public string InvoiceCode { get; init; } = string.Empty;
    public string InvoiceNumber { get; init; } = string.Empty;
    public string IdentityText { get; init; } = string.Empty;
    public DateTime InvoiceDate { get; init; }
    public long CustomerId { get; init; }
    public string CustomerCode { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal GrossAmount { get; init; }
    public string GrossAmountText { get; init; } = string.Empty;

    /// <summary>首张有效收款单号（null = 未知）</summary>
    public string? FirstReceiptNo { get; init; }

    /// <summary>首张有效收款日期（null = 未知）</summary>
    public DateTime? FirstReceiptDate { get; init; }

    /// <summary>末张有效收款单号（null = 未知）</summary>
    public string? LastReceiptNo { get; init; }

    /// <summary>末张有效收款日期（null = 未知）</summary>
    public DateTime? LastReceiptDate { get; init; }

    /// <summary>首收间隔天数 = 首张有效收款日期 − 开票日期；null = 未知</summary>
    public int? FirstCollectionDays { get; init; }

    /// <summary>末收间隔天数 = 末张有效收款日期 − 开票日期；null = 未知</summary>
    public int? LastCollectionDays { get; init; }

    /// <summary>可比较已分摊金额（原币，只来自有效、币种一致且不早于开票日期的分摊收款）</summary>
    public decimal ComparableAllocatedAmount { get; init; }

    public string ComparableAllocatedText { get; init; } = string.Empty;

    /// <summary>可比较剩余金额 = 含税总额 − 可比较已分摊（下限 0；只作算术证据，不代表应收余额）</summary>
    public decimal ComparableRemainingAmount { get; init; }

    public string ComparableRemainingText { get; init; } = string.Empty;

    /// <summary>可比较有效分摊行条数</summary>
    public int ComparableAllocationCount { get; init; }

    /// <summary>缺收款单（收款单不存在 / 已删除）分摊行条数</summary>
    public int MissingCount { get; init; }

    /// <summary>已作废分摊行条数</summary>
    public int VoidedCount { get; init; }

    /// <summary>收款单已取消的分摊行条数</summary>
    public int CancelledCount { get; init; }

    /// <summary>早于开票日期的收款条数</summary>
    public int PreInvoiceCount { get; init; }

    /// <summary>币种不一致的收款条数</summary>
    public int CurrencyConflictCount { get; init; }

    /// <summary>是否存在异常证据（缺链接 / 已作废 / 已取消 / 早于开票日期 / 币种不一致），仅标注、不影响已派生收款时效</summary>
    public bool IsAnomalous { get; init; }

    /// <summary>异常证据逐条说明（缺链接 / 已作废 / 已取消 / 早于开票日期 / 币种不一致等）</summary>
    public List<string> Anomalies { get; init; } = new();

    /// <summary>状态判定说明（未知原因 / 异常详情等）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本页收款时效计数（合计与计数只统计本次返回页的已登记发票）</summary>
public sealed class CustomerInvoiceCollectionTimingCounts
{
    public int Total { get; init; }
    public int Available { get; init; }
    public int Unavailable { get; init; }
    public int Anomalous { get; init; }
}

/// <summary>客户销项发票收款时效证据报表（只读派生）</summary>
public sealed class CustomerInvoiceCollectionTimingReport
{
    public long? CustomerId { get; init; }
    public DateTime? InvoiceDateFrom { get; init; }
    public DateTime? InvoiceDateTo { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>收款时效口径说明（界面与文档同源，由服务端在装配时填充）</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>范围说明（界面与文档同源，由服务端在装配时填充）</summary>
    public string ScopeNote { get; init; } = string.Empty;

    /// <summary>边界说明（界面与文档同源，由服务端在装配时填充）</summary>
    public string Boundary { get; init; } = string.Empty;

    public CustomerInvoiceCollectionTimingCounts Counts { get; init; } = new();
    public List<CustomerInvoiceCollectionTimingItem> Items { get; init; } = new();
}
