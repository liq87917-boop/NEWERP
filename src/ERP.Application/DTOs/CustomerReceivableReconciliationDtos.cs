using ERP.Application.Common;
using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 客户应收账款对账与账龄工作台查询条件（ERP-074；全部为只读筛选参数，非法取值直接拒绝而不静默忽略）。
/// 账龄只相对显式 as-of 日期计算，且只对登记了显式到期日的发票计算；当前 ERP-055 发票证据模型尚未持久化到期日，
/// 因此本查询不含到期日区间筛选，现有发票全部进入「未知到期日」分组。
/// </summary>
public sealed class CustomerReceivableReconciliationQuery
{
    public long? InvoiceId { get; set; }
    public long? CustomerId { get; set; }
    public string? Currency { get; set; }
    public string? Keyword { get; set; }
    public DateTime? InvoiceDateFrom { get; set; }
    public DateTime? InvoiceDateTo { get; set; }
    public string? InvoiceStatus { get; set; }
    public string? AllocationState { get; set; }
    public DateTime? AsOfDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = CustomerReceivableReconciliationRules.DefaultPageSize;

    public void Normalize()
    {
        if (InvoiceId is <= 0)
            throw BusinessException.InvalidParameter($"发票 Id 必须为正整数：{InvoiceId}");
        if (CustomerId is <= 0) CustomerId = null;

        Currency = string.IsNullOrWhiteSpace(Currency)
            ? null
            : CustomerReceivableReconciliationRules.NormalizeCurrencyStrict(Currency);

        InvoiceStatus = CustomerReceivableReconciliationRules.NormalizeInvoiceStatusFilter(InvoiceStatus);
        AllocationState = CustomerReceivableReconciliationRules.NormalizeAllocationFilter(AllocationState);

        var kw = Keyword?.Trim();
        if (kw is { Length: > CustomerReceivableReconciliationRules.MaxKeywordLength })
            throw BusinessException.InvalidParameter(
                $"关键字长度不能超过 {CustomerReceivableReconciliationRules.MaxKeywordLength} 字符（避免全表模糊扫描）");
        Keyword = kw;

        InvoiceDateFrom = InvoiceDateFrom?.Date;
        InvoiceDateTo = InvoiceDateTo?.Date;
        if (InvoiceDateFrom.HasValue && InvoiceDateTo.HasValue && InvoiceDateFrom > InvoiceDateTo)
            throw BusinessException.InvalidParameter(
                $"发票日期开始 {InvoiceDateFrom:yyyy-MM-dd} 不能晚于结束 {InvoiceDateTo:yyyy-MM-dd}");

        AsOfDate = AsOfDate?.Date;
        Page = Math.Max(1, Page);
        PageSize = Math.Clamp(PageSize, 1, CustomerReceivableReconciliationRules.MaxPageSize);
    }
}

/// <summary>工作台报表（本页发票行 + 按币种 / 客户币种分组的只读派生汇总；绝不跨币种合并）</summary>
public sealed class CustomerReceivableReconciliationReport
{
    public long? InvoiceId { get; init; }
    public long? CustomerId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Keyword { get; init; } = string.Empty;
    public DateTime? InvoiceDateFrom { get; init; }
    public DateTime? InvoiceDateTo { get; init; }
    public string InvoiceStatus { get; init; } = string.Empty;
    public string InvoiceStatusText { get; init; } = string.Empty;
    public string AllocationState { get; init; } = string.Empty;
    public string AllocationStateText { get; init; } = string.Empty;
    public DateTime AsOfDate { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
    public int PageInvoiceCount { get; init; }
    public List<CustomerReceivableReconciliationInvoiceRow> Invoices { get; init; } = new();
    public List<CustomerReceivableReconciliationCurrencySummary> Currencies { get; init; } = new();
    public List<CustomerReceivableReconciliationGroup> Groups { get; init; } = new();
    public List<CustomerReceivableReconciliationUnknownDueDateGroup> UnknownDueDateGroups { get; init; } = new();
}

/// <summary>单张发票的对账证据行（只读派生：发票含税总额 + 有效分摊 + 算术剩余证据 + 分配状态）</summary>
public sealed class CustomerReceivableReconciliationInvoiceRow
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
    public int AmountDecimals { get; init; }
    public decimal GrossAmount { get; init; }
    public int Status { get; init; }
    public bool IsDraft { get; init; }
    public bool IsVoided { get; init; }
    public bool IsActiveEvidence { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public int TotalRowCount { get; init; }
    public int ActiveRowCount { get; init; }
    public decimal ActiveAmount { get; init; }
    public int VoidedRowCount { get; init; }
    public int EffectiveCount { get; init; }
    public decimal EffectiveAmount { get; init; }
    public int EffectiveReceiptCount { get; init; }
    public string AllocationState { get; init; } = string.Empty;
    public string AllocationStateText { get; init; } = string.Empty;
    public decimal? RemainingAmount { get; init; }
    public string RemainingState { get; init; } = string.Empty;
    public string RemainingStateText { get; init; } = string.Empty;
    public bool DueDateKnown { get; init; }
    public string AgingBucket { get; init; } = string.Empty;
    public string AgingBucketText { get; init; } = string.Empty;
}

/// <summary>按币种的汇总（不同币种分别成行，绝无跨币种总额）</summary>
public sealed class CustomerReceivableReconciliationCurrencySummary
{
    public string Currency { get; init; } = string.Empty;
    public int AmountDecimals { get; init; }
    public int InvoiceCount { get; init; }
    public decimal GrossAmount { get; init; }
    public decimal EffectiveAllocatedAmount { get; init; }
    public decimal? RemainingAmount { get; init; }
}

/// <summary>按客户 + 币种分组的对账汇总</summary>
public sealed class CustomerReceivableReconciliationGroup
{
    public long CustomerId { get; init; }
    public string CustomerCode { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public int InvoiceCount { get; init; }
    public decimal GrossAmount { get; init; }
    public decimal EffectiveAllocatedAmount { get; init; }
    public decimal? RemainingAmount { get; init; }
    public List<CustomerReceivableReconciliationInvoiceRow> Invoices { get; init; } = new();
}

/// <summary>「未知到期日」分组（独立成组，不参与账龄桶；含有界发票身份清单）</summary>
public sealed class CustomerReceivableReconciliationUnknownDueDateGroup
{
    public long CustomerId { get; init; }
    public string CustomerCode { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public int AmountDecimals { get; init; }
    public int InvoiceCount { get; init; }
    public decimal GrossAmount { get; init; }
    public decimal EffectiveAllocatedAmount { get; init; }
    public decimal? RemainingAmount { get; init; }
    public List<string> InvoiceIdentities { get; init; } = new();
    public string Note { get; init; } = string.Empty;
}

/// <summary>单张发票的分摊证据行（含已作废历史；只读展示，金额与计数以数据集侧聚合为准）</summary>
public sealed class CustomerReceivableReconciliationAllocationRow
{
    public long AllocationId { get; init; }
    public long ReceiptId { get; init; }
    public string ReceiptNo { get; init; } = string.Empty;
    public DateTime ReceiptDate { get; init; }
    public int ReceiptStatus { get; init; }
    public string ReceiptStatusText { get; init; } = string.Empty;
    public bool ReceiptAvailable { get; init; }
    public string ReceiptAvailabilityText { get; init; } = string.Empty;
    public decimal AllocatedAmount { get; init; }
    public string AmountText { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public int Status { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool IsEffective { get; init; }
    public string EffectivenessText { get; init; } = string.Empty;
    public DateTime AllocatedAt { get; init; }
    public string AllocatedBy { get; init; } = string.Empty;
    public DateTime? VoidedAt { get; init; }
    public string VoidReason { get; init; } = string.Empty;
    public string Remark { get; init; } = string.Empty;
}

/// <summary>单张发票的对账证据明细（重新校验授权；fail closed）</summary>
public sealed class CustomerReceivableReconciliationInvoiceDetail
{
    public long InvoiceId { get; init; }
    public DateTime AsOfDate { get; init; }
    public CustomerReceivableReconciliationInvoiceRow Row { get; init; } = new();
    public bool AllocationsTruncated { get; init; }
    public List<CustomerReceivableReconciliationAllocationRow> Allocations { get; init; } = new();
    public string AuthorizationNote { get; init; } = string.Empty;
}

/// <summary>工作台元数据（只读：白名单、有界额度与口径文案）</summary>
public sealed class CustomerReceivableReconciliationMetadataDto
{
    public List<string> SupportedBuckets { get; init; } = new();
    public List<string> SupportedAllocationFilters { get; init; } = new();
    public List<string> SupportedInvoiceStatuses { get; init; } = new();
    public List<string> SupportedCurrencies { get; init; } = new();
    public int DefaultPageSize { get; init; }
    public int MaxPageSize { get; init; }
    public int MaxAllocationRowsPerPage { get; init; }
    public int MaxDetailsPerInvoice { get; init; }
    public string RequiredMenuCode { get; init; } = string.Empty;
    public string RequiredMenuText { get; init; } = string.Empty;
}
