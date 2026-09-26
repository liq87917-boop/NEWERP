namespace ERP.Application.DTOs;

/// <summary>
/// 新增「客户收款 → 客户销项发票」分摊行请求（ERP-073）。
/// <para>客户端只能提交**显式标识符与显式金额**：发票证据 Id + 收款单 Id + 分摊金额 + 备注；
/// 收款单 / 发票 / 客户快照、币种、状态、登记人（按已认证身份写入）、登记时间与任何余额字段
/// 一律由服务端权威写入，客户端提交的同名字段（若有）**不被采信**。</para>
/// <para>系统<strong>不</strong>按单号文本、金额、日期或相似度匹配收款单与发票，
/// 也<strong>不</strong>做任何汇率换算或跨币种合并。</para>
/// </summary>
public sealed class CustomerSalesInvoiceCollectionAllocationSaveDto
{
    /// <summary>客户销项发票证据 Id（必须存在、未删除且为「已登记」的 ERP-055 发票）</summary>
    public long CustomerSalesInvoiceEvidenceId { get; set; }

    /// <summary>收款单 Id（必须存在、未删除且未取消；收款单本身不会被本接口改写）</summary>
    public long ReceiptId { get; set; }

    /// <summary>分摊金额（原币；按币种精度取整后必须大于 0，且不得超过收款单可分摊余额与发票未分摊含税额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>备注（可选，有界）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>作废分摊行请求（ERP-073）：必须显式填写原因，作废保留原始值而不是删除。</summary>
public sealed class CustomerSalesInvoiceCollectionAllocationVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>收款分摊行台账查询参数（ERP-073，全部为可选过滤；结果分页返回）</summary>
public sealed class CustomerSalesInvoiceCollectionAllocationQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>发票筛选（留空 = 全部发票）</summary>
    public long? CustomerSalesInvoiceEvidenceId { get; set; }

    /// <summary>收款单筛选（留空 = 全部收款单）</summary>
    public long? ReceiptId { get; set; }

    /// <summary>客户筛选（留空 = 全部客户；不同客户绝不合并汇总）</summary>
    public long? CustomerId { get; set; }

    /// <summary>状态筛选（1 有效 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>币种筛选（留空 = 全部币种；不同币种分别汇总，绝不合并为一个金额）</summary>
    public string? Currency { get; set; }

    /// <summary>登记时间开始（含当天；留空 = 不限）</summary>
    public DateTime? AllocatedDateFrom { get; set; }

    /// <summary>登记时间结束（含当天；留空 = 不限）</summary>
    public DateTime? AllocatedDateTo { get; set; }

    /// <summary>关键字（匹配发票号码 / 发票代码 / 收款单号 / 客户名称 / 客户编码 / 备注；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 PageQuery 同口径）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
    }
}

/// <summary>
/// 收款分摊行（ERP-073，读取用）：发票快照 + 收款单快照 + 客户快照 + 分摊金额 + 登记 / 作废留痕。
/// <para>可用性标注为**只读派生**：收款单被取消 / 软删除、发票被作废 / 软删除后历史证据照常可读，
/// 只是显式标注为不可用；<strong>不</strong>代表款项已到账、发票已收款、已结清、已逾期或已记账。</para>
/// </summary>
public sealed record CustomerSalesInvoiceCollectionAllocationDto(
    long Id,
    long CustomerSalesInvoiceEvidenceId,
    string InvoiceType,
    string InvoiceCode,
    string InvoiceNumber,
    string IdentityText,
    DateTime InvoiceDate,
    int InvoiceStatus,
    string InvoiceStatusText,
    decimal InvoiceGrossAmount,
    string InvoiceCurrency,
    string InvoiceAmountText,
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    int ReceiptStatus,
    string ReceiptStatusText,
    decimal ReceiptAmount,
    string ReceiptAmountText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    int AmountDecimals,
    decimal AllocatedAmount,
    string AllocatedAmountText,
    string Remark,
    int Status,
    string StatusText,
    bool IsActive,
    bool IsVoided,
    DateTime AllocatedAt,
    string AllocatedBy,
    DateTime? VoidedAt,
    string VoidReason,
    bool ReceiptAvailable,
    string ReceiptAvailabilityText,
    bool InvoiceAvailable,
    string InvoiceAvailabilityText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string RuleText,
    string AmountRuleText,
    string DimensionSeparationText,
    string BoundaryText);

/// <summary>
/// 发票侧汇总（ERP-073，只读派生）：发票快照 + **本维度**有效分摊金额 / 未分摊含税额 / 行数 + 有界逐行明细。
/// <para><see cref="AllocatedAmount"/> 只按**有效（未作废）持久化行**派生，下限 0 处理；
/// 未分摊额只是本登记册口径的算术证据，<strong>不</strong>表示已付 / 已结清 / 逾期 / 收入确认 / 记账状态或应收余额。</para>
/// </summary>
public sealed record CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto(
    long CustomerSalesInvoiceEvidenceId,
    string InvoiceType,
    string InvoiceCode,
    string InvoiceNumber,
    string IdentityText,
    DateTime InvoiceDate,
    int InvoiceStatus,
    string InvoiceStatusText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    int AmountDecimals,
    decimal InvoiceGrossAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    int VoidedCount,
    string LinkageStatus,
    string LinkageText,
    bool InvoiceAvailable,
    string InvoiceAvailabilityText,
    string RuleText,
    string AmountRuleText,
    string DimensionSeparationText,
    string BoundaryText,
    List<CustomerSalesInvoiceCollectionAllocationDto> Allocations);

/// <summary>
/// 收款单侧汇总（ERP-073，只读派生）：收款单快照 + **本维度**有效分摊金额 / 可分摊余额 / 行数 + 有界逐行明细。
/// <para>可分摊余额只表示「本维度内尚未被分摊的收款金额」，<strong>不是</strong>银行未到账金额、
/// 应收账款余额或客户欠款；也不与 ERP-053 的销售订单收款引用相加。</para>
/// </summary>
public sealed record CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto(
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    int ReceiptStatus,
    string ReceiptStatusText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    int AmountDecimals,
    decimal ReceiptAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    int VoidedCount,
    string LinkageStatus,
    string LinkageText,
    bool ReceiptAvailable,
    string ReceiptAvailabilityText,
    string RuleText,
    string AmountRuleText,
    string DimensionSeparationText,
    string BoundaryText,
    List<CustomerSalesInvoiceCollectionAllocationDto> Allocations);

/// <summary>
/// 可分摊收款单候选（ERP-073，只读有界）：既有、未删除且未取消的客户收款单 + **本维度**已分摊 / 可分摊余额与资格文案。
/// <para><see cref="UnallocatedAmount"/> 只按**本维度有效持久化行**派生（下限 0）：它<strong>不是</strong>银行未到账金额、
/// 应收账款余额或客户欠款，也<strong>不</strong>与 ERP-053 的销售订单收款引用相加。</para>
/// </summary>
public sealed record CustomerSalesInvoiceCollectionAllocationReceiptCandidateDto(
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    int ReceiptStatus,
    string ReceiptStatusText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    int AmountDecimals,
    decimal ReceiptAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    bool Eligible,
    string EligibilityText);

/// <summary>
/// 可承接收款分摊的发票候选（ERP-073，只读有界）：已登记的客户销项发票证据 + **本维度**已分摊 / 未分摊含税额与资格文案。
/// <para>只有**已登记**的发票才会作为候选列出（草稿 / 已作废单独说明，不静默当作可分摊）；
/// <see cref="UnallocatedAmount"/> 只是本登记册口径的算术证据，<strong>不</strong>表示已付 / 已结清 / 逾期或已记账。</para>
/// </summary>
public sealed record CustomerSalesInvoiceCollectionAllocationInvoiceCandidateDto(
    long CustomerSalesInvoiceEvidenceId,
    string InvoiceType,
    string InvoiceCode,
    string InvoiceNumber,
    string IdentityText,
    DateTime InvoiceDate,
    int InvoiceStatus,
    string InvoiceStatusText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    int AmountDecimals,
    decimal InvoiceGrossAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    bool Eligible,
    string EligibilityText);

/// <summary>
/// 收款分摊证据模块元数据（ERP-073，只读）：白名单与边界、有界额度、以及接口 / 界面 / 文档同源的口径文案。
/// </summary>
public sealed record CustomerSalesInvoiceCollectionAllocationMetadataDto(
    List<string> SupportedCurrencies,
    int MaxAllocationsPerInvoice,
    int MaxAllocationsPerReceipt,
    int MaxReceiptCandidates,
    int MaxInvoiceCandidates,
    int MaxPageSize,
    string RuleText,
    string AmountRuleText,
    string UniquenessRuleText,
    string DimensionSeparationText,
    string HistoricalEvidenceText,
    string BoundaryText);

