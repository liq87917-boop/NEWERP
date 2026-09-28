using ERP.Application.Common;
using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 客户对账证据导出查询条件（ERP-086；全部为只读筛选参数，非法取值直接拒绝而不静默忽略）。
/// 复用 ERP-074 工作台的同一套筛选口径（客户 / 发票身份 / 发票日期区间 / 币种 / 发票状态 / 分配状态），
/// 另加「证据类」与「导出格式」两个导出专用筛选；行数受 <see cref="MaxRows"/> 有界钳制。
/// </summary>
public sealed class CustomerReconciliationStatementQuery
{
    public long? CustomerId { get; set; }
    public long? InvoiceId { get; set; }
    public string? Currency { get; set; }
    public DateTime? InvoiceDateFrom { get; set; }
    public DateTime? InvoiceDateTo { get; set; }
    public string? InvoiceStatus { get; set; }
    public string? AllocationState { get; set; }
    public string? EvidenceClass { get; set; }
    public DateTime? AsOfDate { get; set; }

    /// <summary>导出格式（csv / html；留空按 csv）</summary>
    public string Format { get; set; } = "csv";

    /// <summary>导出行数上限（1 ~ 5000；超出按上限钳制，绝不静默截断）</summary>
    public int MaxRows { get; set; } = CustomerReconciliationStatementRules.DefaultMaxRows;

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
        EvidenceClass = CustomerReconciliationStatementRules.NormalizeEvidenceClass(EvidenceClass);
        Format = CustomerReconciliationStatementRules.NormalizeFormat(Format);

        InvoiceDateFrom = InvoiceDateFrom?.Date;
        InvoiceDateTo = InvoiceDateTo?.Date;
        if (InvoiceDateFrom.HasValue && InvoiceDateTo.HasValue && InvoiceDateFrom > InvoiceDateTo)
            throw BusinessException.InvalidParameter(
                $"发票日期开始 {InvoiceDateFrom:yyyy-MM-dd} 不能晚于结束 {InvoiceDateTo:yyyy-MM-dd}");

        AsOfDate = AsOfDate?.Date;
        MaxRows = Math.Clamp(MaxRows, 1, CustomerReconciliationStatementRules.MaxExportRows);
    }
}

/// <summary>单张发票的对账证据导出行（四类证据分别标注；算术剩余证据 = 发票含税总额 − 有效分摊合计）</summary>
public sealed class CustomerReconciliationStatementRowDto
{
    // ============ 发票身份 ============
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

    // ============ ① 发票含税总额证据（ERP-055） ============
    public decimal InvoiceGrossAmount { get; init; }
    public bool IsDraft { get; init; }
    public bool IsVoided { get; init; }
    public bool IsActiveEvidence { get; init; }
    public string InvoiceStatusText { get; init; } = string.Empty;

    // ============ ② 有效收款分摊证据（ERP-073，只按未作废且有效的持久化分摊行派生） ============
    public decimal EffectiveAllocatedAmount { get; init; }
    public int EffectiveAllocationCount { get; init; }
    public int EffectiveReceiptCount { get; init; }

    // ============ ③ 收款分摊上下文（ERP-075） ============
    public int ReceiptAllocationTotalRows { get; init; }
    public int ReceiptVoidedRows { get; init; }
    public string ReceiptAllocationState { get; init; } = string.Empty;
    public string ReceiptAllocationStateText { get; init; } = string.Empty;

    // ============ ④ 发票 → 出货链接证据（ERP-076；无权威持久化链接册，fail closed 为「未知」） ============
    public string ShipmentLinkState { get; init; } = string.Empty;
    public string ShipmentLinkText { get; init; } = string.Empty;

    // ============ 算术剩余证据 ============
    public decimal? RemainingAmount { get; init; }
    public string RemainingState { get; init; } = string.Empty;
    public string RemainingStateText { get; init; } = string.Empty;

    // ============ 账龄口径（当前 ERP-055 无持久化到期日，一律进入「未知到期日」分组） ============
    public bool DueDateKnown { get; init; }
    public string AgingBucket { get; init; } = string.Empty;
    public string AgingBucketText { get; init; } = string.Empty;
}

/// <summary>按币种的导出汇总（不同币种分别成行，绝无跨币种总额）</summary>
public sealed class CustomerReconciliationCurrencySummaryDto
{
    public string Currency { get; init; } = string.Empty;
    public int AmountDecimals { get; init; }
    public int InvoiceCount { get; init; }
    public decimal InvoiceGrossAmount { get; init; }
    public decimal EffectiveAllocatedAmount { get; init; }
    public decimal? RemainingAmount { get; init; }
}

/// <summary>客户对账证据导出（ERP-086）：只读、有界、分币种；含 as-of、筛选口径、生成时间与边界声明</summary>
public sealed class CustomerReconciliationStatementDto
{
    public long? CustomerId { get; init; }
    public string CustomerCode { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime AsOfDate { get; init; }
    public DateTime GeneratedAt { get; init; }

    public string CurrencyFilter { get; init; } = string.Empty;
    public string InvoiceDateRangeText { get; init; } = string.Empty;
    public string InvoiceStatusFilter { get; init; } = string.Empty;
    public string InvoiceStatusFilterText { get; init; } = string.Empty;
    public string AllocationStateFilter { get; init; } = string.Empty;
    public string AllocationStateFilterText { get; init; } = string.Empty;
    public string EvidenceClassFilter { get; init; } = string.Empty;
    public string EvidenceClassFilterText { get; init; } = string.Empty;

    public int Total { get; init; }
    public int RowCount { get; init; }
    public bool Truncated { get; init; }
    public string TruncatedNote { get; init; } = string.Empty;

    public string BoundaryText { get; init; } = string.Empty;
    public string ShipmentLinkUnavailableText { get; init; } = string.Empty;
    public string CurrencySeparationText { get; init; } = string.Empty;

    public List<CustomerReconciliationStatementRowDto> Rows { get; init; } = new();
    public List<CustomerReconciliationCurrencySummaryDto> Currencies { get; init; } = new();
}

/// <summary>导出元数据（只读：证据类 / 格式 / 有界额度 / 边界口径）</summary>
public sealed class CustomerReconciliationStatementMetadataDto
{
    public List<string> SupportedEvidenceClasses { get; init; } = new();
    public List<string> SupportedFormats { get; init; } = new();
    public int DefaultMaxRows { get; init; }
    public int MaxExportRows { get; init; }
    public string RequiredMenuCode { get; init; } = string.Empty;
    public string RequiredMenuText { get; init; } = string.Empty;
    public string BoundaryText { get; init; } = string.Empty;
    public string ShipmentLinkUnavailableText { get; init; } = string.Empty;
    public string CurrencySeparationText { get; init; } = string.Empty;
}
