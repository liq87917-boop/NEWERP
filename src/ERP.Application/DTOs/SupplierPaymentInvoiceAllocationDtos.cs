namespace ERP.Application.DTOs;

/// <summary>
/// 新增「付款单 → 供应商采购发票」付款引用（分摊）行请求（ERP-066）。
/// <para>客户端只能提交「付款单 Id + 供应商采购发票 Id + 引用金额 + 备注」；付款单 / 供应商 / 发票快照、
/// 状态、登记人与审计字段一律由服务端权威写入，<strong>不</strong>接受客户端提交的供应商、币种、余额或任何快照值。</para>
/// </summary>
public sealed class SupplierPaymentInvoiceAllocationSaveDto
{
    /// <summary>付款单 Id（必须存在且未删除；付款单本身不会被本接口改写）</summary>
    public long PaymentId { get; set; }

    /// <summary>
    /// 供应商采购发票 Id（必须存在、未删除且**已登记**（未作废），且供应商与币种都与付款单一致）。
    /// <para>草稿发票与已作废发票一律拒绝：引用证据只建立在已冻结的发票证据之上。</para>
    /// </summary>
    public long PurchaseInvoiceId { get; set; }

    /// <summary>引用金额（原币；按币种精度取整后必须大于 0，且不得超过付款单未引用金额与发票未引用含税总额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>备注（可选，有界）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>作废付款发票引用行请求（ERP-066）：必须显式填写原因，作废保留原始值而不是删除。</summary>
public sealed class SupplierPaymentInvoiceAllocationVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>付款发票引用（分摊）行台账查询参数（ERP-066，全部为可选过滤；结果分页返回）</summary>
public sealed class SupplierPaymentInvoiceAllocationQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>付款单筛选（留空 = 全部付款单）</summary>
    public long? PaymentId { get; set; }

    /// <summary>供应商采购发票筛选（留空 = 全部发票）</summary>
    public long? PurchaseInvoiceId { get; set; }

    /// <summary>供应商筛选（留空 = 全部供应商；不同供应商绝不合并汇总）</summary>
    public long? SupplierId { get; set; }

    /// <summary>状态筛选（1 有效 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>币种筛选（留空 = 全部币种；不同币种分别汇总，绝不合并为一个金额）</summary>
    public string? Currency { get; set; }

    /// <summary>登记时间开始（含当天；留空 = 不限）</summary>
    public DateTime? AllocatedDateFrom { get; set; }

    /// <summary>登记时间结束（含当天；留空 = 不限）</summary>
    public DateTime? AllocatedDateTo { get; set; }

    /// <summary>关键字（匹配付款单号 / 发票号码 / 发票代码 / 供应商名称 / 供应商编码；留空 = 不过滤）</summary>
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
/// 付款发票引用（分摊）行（ERP-066，读取用）：付款单快照 + 供应商采购发票快照 + 引用金额 + 状态 / 作废留痕 + 登记人。
/// <para>可用性标注为**只读派生**：付款单被软删除，或发票被软删除 / 仍为草稿 / 已作废后，历史证据照常可读，
/// 只是显式标注为不可用；<strong>不</strong>代表付款已执行、发票已认证 / 抵扣、已付款或已核销。</para>
/// </summary>
public sealed record SupplierPaymentInvoiceAllocationDto(
    long Id,
    long PaymentId,
    string PaymentNo,
    DateTime PaymentDate,
    int PaymentStatus,
    string PaymentStatusText,
    decimal PaymentAmount,
    long PurchaseInvoiceId,
    string InvoiceType,
    string InvoiceTypeText,
    string InvoiceCode,
    string InvoiceNumber,
    string InvoiceIdentityText,
    DateTime InvoiceDate,
    int InvoiceStatus,
    string InvoiceStatusText,
    decimal InvoiceGrossAmount,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    string Currency,
    int AmountDecimals,
    decimal AllocatedAmount,
    string Remark,
    int Status,
    string StatusText,
    bool IsActive,
    bool IsVoided,
    DateTime AllocatedAt,
    string RecordedBy,
    DateTime? VoidedAt,
    string VoidReason,
    bool PaymentAvailable,
    string PaymentAvailabilityText,
    bool InvoiceAvailable,
    string InvoiceAvailabilityText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText);

/// <summary>
/// 付款单侧汇总（ERP-066，只读派生）：付款单快照 + **有效行**已引用 / 未引用金额 / 行数 + 有界逐行明细。
/// <para><see cref="InvoiceAllocatedAmount"/> 只按本册**有效（未作废）持久化行**派生，下限 0 处理；
/// 未引用金额绝不被猜测到任何发票。已作废行单独计数与列出，永不并入有效合计。</para>
/// <para>证据维度互不合并：<see cref="PurchaseOrderAllocatedAmount"/> 是 ERP-049「付款单 → 采购订单」引用行的
/// **独立只读派生值**，仅为对照展示，<strong>绝不</strong>与本册发票引用金额相加。
/// 口径见 <see cref="SeparateEvidenceText"/>。</para>
/// </summary>
public sealed record SupplierPaymentInvoiceAllocationPaymentSummaryDto(
    long PaymentId,
    string PaymentNo,
    DateTime PaymentDate,
    int PaymentStatus,
    string PaymentStatusText,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    string Currency,
    int AmountDecimals,
    decimal PaymentAmount,
    decimal InvoiceAllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    int VoidedCount,
    string LinkageStatus,
    string LinkageText,
    decimal PurchaseOrderAllocatedAmount,
    int PurchaseOrderAllocationCount,
    string SeparateEvidenceText,
    bool PaymentAvailable,
    string PaymentAvailabilityText,
    string RuleText,
    string BoundaryText,
    List<SupplierPaymentInvoiceAllocationDto> Allocations);

/// <summary>
/// 发票侧汇总（ERP-066，只读派生）：发票快照 + 全部有效引用行已引用金额 / 未引用含税总额 / 行数 + 有界逐行明细。
/// <para><see cref="AllocatedAmount"/> 只按**本册有效持久化行**（任意付款单）派生，下限 0；
/// 未引用含税总额只是「尚未被付款引用证据覆盖的发票金额」，<strong>不是</strong>应付余额、账龄或付款依据。</para>
/// </summary>
public sealed record SupplierPaymentInvoiceAllocationInvoiceSummaryDto(
    long PurchaseInvoiceId,
    string InvoiceType,
    string InvoiceTypeText,
    string InvoiceCode,
    string InvoiceNumber,
    string InvoiceIdentityText,
    DateTime InvoiceDate,
    int InvoiceStatus,
    string InvoiceStatusText,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
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
    string BoundaryText,
    List<SupplierPaymentInvoiceAllocationDto> Allocations);

/// <summary>
/// 可引用付款单候选（ERP-066，只读有界）：既有、未删除的供应商付款单 + 发票引用已引用 / 未引用金额与资格文案
/// （资格文案同时附带供应商可用性只读说明；供应商停用 / 删除不改变历史快照可读性）。
/// <para><see cref="UnallocatedAmount"/> 只是本册口径的「付款单金额中尚未被发票引用行指向的部分」，
/// <strong>不是</strong>银行未付金额、应付余额或发票余额，也<strong>不</strong>扣除 ERP-049 的采购订单引用金额。</para>
/// </summary>
public sealed record SupplierPaymentInvoiceAllocationPaymentCandidateDto(
    long PaymentId,
    string PaymentNo,
    DateTime PaymentDate,
    int PaymentStatus,
    string PaymentStatusText,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    string Currency,
    decimal PaymentAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    bool Eligible,
    string EligibilityText);

/// <summary>
/// 可引用供应商采购发票候选（ERP-066，只读有界）：只列**同供应商 + 同币种**的未删除发票
/// （含草稿 / 已作废并显式标注不可引用）。
/// <para><see cref="RemainingUnallocatedAmount"/> = 含税总额 − 本付款单已引用 − 其他付款单已引用（下限 0），
/// 只按本册**有效持久化行**派生，是**软上限提示**而非应付余额、账龄或付款依据。</para>
/// </summary>
public sealed record SupplierPaymentInvoiceAllocationInvoiceCandidateDto(
    long PurchaseInvoiceId,
    string InvoiceType,
    string InvoiceTypeText,
    string InvoiceCode,
    string InvoiceNumber,
    string InvoiceIdentityText,
    DateTime InvoiceDate,
    int InvoiceStatus,
    string InvoiceStatusText,
    bool Voided,
    string Currency,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    decimal InvoiceGrossAmount,
    decimal AllocatedByThisPayment,
    decimal AllocatedByOtherPayments,
    decimal RemainingUnallocatedAmount,
    bool Eligible,
    string EligibilityText);
