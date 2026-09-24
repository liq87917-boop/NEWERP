namespace ERP.Application.DTOs;

/// <summary>
/// 分摊证据行（ERP-060，**只读**）：ERP-042 已落库的分摊行（<c>FinanceExpenseAllocationLines</c>）原样呈现 ——
/// 参与方 / 客户快照、方法、基数种类与来源、基数值、比例、分摊金额（原币 + 按批次来源汇率折人民币）。
/// <para>本 DTO 不做任何重算、不改派任何记录：链接可用性（参与方 / 客户 / 柜号）由服务端按当前持久化行显式标注，
/// 历史异常链接照原值展示并标注「即可见、不修复」。</para>
/// </summary>
public sealed record ContainerAllocationEvidenceLineDto(
    long ParticipantId,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string CustomerDisplay,
    bool CustomerAvailable,
    string CustomerAvailabilityText,
    bool IsPrimary,
    long BatchId,
    string BatchNo,
    string BatchStatusText,
    bool IsVoided,
    long SourceExpenseId,
    string SourceExpenseNo,
    string AllocationMethod,
    string BasisKind,
    string BasisSource,
    decimal BasisValue,
    string BasisEvidenceText,
    decimal Ratio,
    decimal AllocatedAmount,
    decimal AllocatedAmountCny,
    string Currency,
    int AmountPrecision,
    string ExpenseNo,
    bool LinkInvalid,
    string LinkStatus,
    string LinkStatusText,
    int SortOrder);

/// <summary>
/// 分摊批次证据（ERP-060，**只读**）：批次自身的持久化字段 + 状态文案 + 柜号链接校验 + 该批次的逐行留痕。
/// <para>合计一律取自批次持久化列（<c>SourceAmount</c> / <c>AllocatedTotal</c> / <c>LineCount</c>），
/// 不用界面重算值替换权威值；逐行明细若超出有界上限会显式标注截断。</para>
/// </summary>
public sealed record ContainerAllocationEvidenceBatchDto(
    long BatchId,
    string BatchNo,
    long SourceExpenseId,
    string SourceExpenseNo,
    string AllocationMethod,
    string BasisKind,
    string Currency,
    int AmountPrecision,
    decimal ExchangeRate,
    decimal SourceAmount,
    decimal AllocatedTotal,
    decimal AllocatedTotalCny,
    int LineCount,
    int StoredLineCount,
    int Status,
    bool StatusKnown,
    string StatusText,
    bool IsActive,
    bool IsVoided,
    DateTime? VoidedAt,
    string VoidReason,
    string Remark,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string LoadingListNo,
    string ContainerNo,
    bool ContainerLinkConsistent,
    string ContainerLinkText,
    bool LinesTruncated,
    string CustomerSummaryText,
    string TotalsText,
    List<ContainerAllocationEvidenceLineDto> Lines);

/// <summary>
/// 按币种分组的客户分摊证据（ERP-060，**只读**）：同一币种内的客户分摊金额 / 比例 / 基数 / 批次号，
/// 不同币种之间<strong>不合并、不换算</strong>。
/// </summary>
public sealed record ContainerAllocationEvidenceCustomerDto(
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string CustomerDisplay,
    bool CustomerAvailable,
    string CustomerAvailabilityText,
    bool HasInvalidLink,
    string LinkStatusText,
    string Currency,
    int AmountPrecision,
    decimal AllocatedAmount,
    decimal AllocatedAmountCny,
    decimal RatioTotal,
    int LineCount,
    int BatchCount,
    string MethodsText,
    string BasisText,
    List<string> BatchNos);

/// <summary>
/// 币种分组（ERP-060，**只读**）：该币种的有效批次条数 / 有效分摊行数 / 分摊合计（原币）与来源金额合计，
/// 以及按客户拆分的分摊证据。分组合计取自批次持久化列，不跨币种相加。
/// </summary>
public sealed record ContainerAllocationEvidenceCurrencyGroupDto(
    string Currency,
    string CurrencyLabel,
    int AmountPrecision,
    int ActiveBatchCount,
    int ActiveLineCount,
    decimal AllocatedTotal,
    decimal AllocatedTotalCny,
    decimal SourceTotal,
    bool TotalsConsistent,
    bool CustomerDetailTruncated,
    string TotalsText,
    string ConversionText,
    List<ContainerAllocationEvidenceCustomerDto> Customers);

/// <summary>
/// 未分摊参考（ERP-060，**只读**）：本柜**柜级来源费用单**中当前不属于任何有效分摊批次的条数与金额合计，
/// 按币种单独分组；仅由显式持久化行比对得出，绝不推断为「零费用 / 已结清 / 应收应付」。
/// </summary>
public sealed record ContainerAllocationEvidenceUnallocatedDto(
    string Currency,
    int AmountPrecision,
    int SourceExpenseCount,
    decimal SourceAmount,
    int VoidedBatchSourceCount,
    List<string> SourceExpenseNos,
    bool Truncated,
    string Text);


/// <summary>
/// 装柜分摊证据（ERP-060，**只读**）：一个装柜清单（或一个结算单所关联的装柜清单）上的 ERP-042 分摊证据 ——
/// 有效批次与币种分组、已作废 / 历史异常批次、未分摊参考、失效链接标注与口径边界文案。
/// <para>缺失证据一律显示「无（未登记）」或「未知」，绝不解释为零费用、已结算、应收、应付或客户对账单。</para>
/// </summary>
public sealed record ContainerExpenseAllocationEvidenceDto(
    string ScopeType,
    long ScopeId,
    long? LoadingListId,
    string LoadingListNo,
    bool LoadingListAvailable,
    string LoadingListAvailabilityText,
    string ContainerNo,
    bool ContainerNoAvailable,
    string ContainerContextText,
    bool HasEvidence,
    string EvidenceStatusText,
    string EvidenceMissingText,
    bool EvidenceTruncated,
    int ActiveBatchCount,
    int VoidedBatchCount,
    int UnknownStatusBatchCount,
    int ActiveLineCount,
    int ActiveCustomerCount,
    List<string> Currencies,
    List<ContainerAllocationEvidenceCurrencyGroupDto> Groups,
    List<ContainerAllocationEvidenceBatchDto> ActiveBatches,
    List<ContainerAllocationEvidenceBatchDto> HistoryBatches,
    bool HistoryTruncated,
    int HistoryTake,
    int HistoryTotal,
    List<ContainerAllocationEvidenceUnallocatedDto> Unallocated,
    string UnallocatedContextText,
    int LegacyAllocationRowCount,
    string LegacyAllocationText,
    int InvalidLinkCount,
    string InvalidLinkText,
    string BoundaryText,
    string DisclaimerText,
    string ReadOnlyText,
    DateTime ReadAt);

/// <summary>
/// 装柜结算单分摊证据（ERP-060，**只读**）：结算单自身持久化字段的**只读回显** + 其关联装柜清单上的分摊证据，
/// 两者分开标注。分摊证据**不参与**结算金额计算，也不回写任何结算字段；金额对照只作算术证据。
/// </summary>
public sealed record ContainerSettlementAllocationEvidenceDto(
    long SettlementId,
    string SettlementNo,
    DateTime SettlementDate,
    string SettlementStatusText,
    long SettlementCustomerId,
    string SettlementCustomerDisplay,
    bool SettlementCustomerAvailable,
    string SettlementCustomerAvailabilityText,
    long? LoadingListId,
    string LoadingListNo,
    string ContainerNo,
    decimal TotalAmount,
    decimal FreightCost,
    decimal OtherCost,
    string SettlementTotalsText,
    bool EvidenceAvailable,
    string EvidenceUnavailableText,
    bool ComparisonComparable,
    string ComparisonText,
    ContainerExpenseAllocationEvidenceDto? Evidence,
    string BoundaryText,
    string DisclaimerText,
    string ReadOnlyText);


/// <summary>
/// 分摊证据工作台查询（ERP-060，全部为可选过滤；只按**显式持久化字段**筛选，结果分页有界）。
/// </summary>
public sealed class ContainerExpenseAllocationEvidenceQuery
{
    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（默认 20，单次上限 200）</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>柜号（**显式等值**：去空白 + 统一大小写；不做相似度匹配）</summary>
    public string? ContainerNo { get; set; }

    /// <summary>装柜清单号（**显式等值**）</summary>
    public string? LoadingListNo { get; set; }

    /// <summary>分摊批次号（**显式等值**）</summary>
    public string? BatchNo { get; set; }

    /// <summary>币种（**显式等值**；不同币种永不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>批次状态（1=有效 / 0=已作废；不填 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>客户 Id（显式 Id 匹配分摊行的客户链接，不按客户名文本合并记录）</summary>
    public long? CustomerId { get; set; }

    /// <summary>是否包含已作废 / 历史批次（默认 true；false 时只呈现有效批次）</summary>
    public bool IncludeHistory { get; set; } = true;

    /// <summary>关键字（只命中显式列：柜号 / 装柜清单号 / 批次号 / 来源费用单号 / 客户编码 / 客户名称）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 <see cref="ERP.Application.Common.PageQuery"/> 同口径 + 本模块上限）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = 20;
        if (PageSize > 200) PageSize = 200;
    }
}

/// <summary>工作台行内的币种摘要（ERP-060，只读；合计取自批次持久化列）</summary>
public sealed record ContainerAllocationEvidenceSummaryGroupDto(
    string Currency,
    string CurrencyLabel,
    int AmountPrecision,
    int ActiveBatchCount,
    decimal AllocatedTotal,
    int CustomerCount,
    string CustomerNamesText);

/// <summary>
/// 工作台行（ERP-060，**只读**）：一个装柜清单（一柜）上的分摊证据摘要 —— 有效 / 已作废批次条数、
/// 按币种分组的合计与客户、链接可用性、失效链接提示与只读声明。
/// </summary>
public sealed record ContainerAllocationEvidenceRowDto(
    long LoadingListId,
    bool LoadingListAvailable,
    string LoadingListAvailabilityText,
    string LoadingListNo,
    string ContainerNo,
    bool ContainerNoAvailable,
    bool ContainerLinkConsistent,
    string ContainerLinkText,
    int ActiveBatchCount,
    int VoidedBatchCount,
    int UnknownStatusBatchCount,
    int ActiveLineCount,
    int ActiveCustomerCount,
    List<string> Currencies,
    List<ContainerAllocationEvidenceSummaryGroupDto> Groups,
    string TotalsText,
    string BasisAndMethodText,
    string CustomerSummaryText,
    string EvidenceStatusText,
    int InvalidLinkCount,
    string InvalidLinkText,
    DateTime? LastBatchAt,
    string ReadOnlyText);

/// <summary>分摊证据工作台页（ERP-060，**只读**）：分页行 + 口径 / 边界 / 免责文案（与服务端规则同源）</summary>
public sealed record ContainerExpenseAllocationEvidenceWorkspaceDto(
    List<ContainerAllocationEvidenceRowDto> Items,
    int Total,
    int Page,
    int PageSize,
    bool ScanBounded,
    string ScanBoundedText,
    string BasisAndCurrencyText,
    string GroupingText,
    string UnallocatedRuleText,
    string MissingEvidenceText,
    string BoundaryText,
    string DisclaimerText,
    string ReadOnlyText);

