namespace ERP.Application.DTOs;

/// <summary>
/// 新增付款引用（分摊）行请求（ERP-049）。
/// <para>客户端只能提交「付款单 Id + 采购订单 Id + 引用金额 + 备注」；付款单 / 供应商 / 采购订单快照、
/// 状态与审计字段一律由服务端权威写入，<strong>不</strong>接受客户端提交的供应商、币种或快照值。</para>
/// </summary>
public sealed class SupplierPaymentAllocationSaveDto
{
    /// <summary>付款单 Id（必须存在且未删除；付款单本身不会被本接口改写）</summary>
    public long PaymentId { get; set; }

    /// <summary>采购订单 Id（必须存在、未删除、未取消，且供应商与币种都与付款单一致）</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>引用金额（原币；按币种精度取整后必须大于 0，且与既有有效行合计不得超过付款单金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>备注（可选，有界）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>作废付款引用行请求（ERP-049）：必须显式填写原因，作废保留原始值而不是删除。</summary>
public sealed class SupplierPaymentAllocationVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>付款引用（分摊）行台账查询参数（ERP-049，全部为可选过滤；结果分页返回）</summary>
public sealed class SupplierPaymentAllocationQuery
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

    /// <summary>采购订单筛选（留空 = 全部订单）</summary>
    public long? PurchaseOrderId { get; set; }

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

    /// <summary>关键字（匹配付款单号 / 采购单号 / 供应商名称 / 供应商编码；留空 = 不过滤）</summary>
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
/// 付款引用（分摊）行（ERP-049，读取用）：付款单快照 + 采购订单快照 + 引用金额 + 状态 / 作废留痕。
/// <para>可用性标注为**只读派生**：付款单被软删除或采购订单被软删除 / 取消后历史证据照常可读，
/// 只是显式标注为不可用 / 已取消；<strong>不</strong>代表付款已执行、订单已结清或发票已核销。</para>
/// </summary>
public sealed record SupplierPaymentAllocationDto(
    long Id,
    long PaymentId,
    string PaymentNo,
    DateTime PaymentDate,
    int PaymentStatus,
    string PaymentStatusText,
    decimal PaymentAmount,
    long PurchaseOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    string OrderCurrency,
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
    DateTime? VoidedAt,
    string VoidReason,
    bool PaymentAvailable,
    string PaymentAvailabilityText,
    bool OrderAvailable,
    string OrderAvailabilityText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText);

/// <summary>
/// 付款单侧汇总（ERP-049，只读派生）：付款单快照 + 有效引用金额 / 未引用金额 / 行数 + 逐行明细。
/// <para><see cref="AllocatedAmount"/> 只按**有效（未作废）持久化行**派生，下限 0 处理；
/// 未引用金额绝不被猜测到任何订单。已作废行单独计数与列出，永不并入有效合计。</para>
/// </summary>
public sealed record SupplierPaymentAllocationPaymentSummaryDto(
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
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    int VoidedCount,
    string LinkageStatus,
    string LinkageText,
    bool PaymentAvailable,
    string PaymentAvailabilityText,
    string RuleText,
    string BoundaryText,
    List<SupplierPaymentAllocationDto> Allocations);

/// <summary>
/// 可引用付款单候选（ERP-049，只读有界）：既有、未删除的供应商付款单 + 已引用 / 未引用金额与资格文案
/// （资格文案同时附带供应商可用性只读说明；供应商停用 / 删除不改变历史快照可读性）。
/// <para><see cref="UnallocatedAmount"/> 只按**有效持久化行**派生（下限 0）；它只是本登记册口径的
/// 「付款单金额中尚未被任何引用行指向的部分」，<strong>不是</strong>银行未付金额、应付余额或发票余额。</para>
/// </summary>
public sealed record SupplierPaymentAllocationPaymentCandidateDto(
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
/// 可引用采购订单候选（ERP-049，只读有界）：同供应商 + 同币种（含已取消订单并显式标注不可引用）。
/// <para><see cref="RemainingUnallocatedAmount"/> 只按**有效持久化行**派生，下限 0；仅为展示上下文，
/// <strong>不是</strong>订单应付余额、账龄或结算依据。</para>
/// </summary>
public sealed record SupplierPaymentAllocationOrderCandidateDto(
    long PurchaseOrderId,
    string OrderNo,
    DateTime OrderDate,
    string StatusText,
    bool Cancelled,
    string Currency,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    decimal OrderedAmount,
    decimal AllocatedByThisPayment,
    decimal AllocatedByOtherPayments,
    decimal RemainingUnallocatedAmount,
    bool Eligible,
    string EligibilityText);
