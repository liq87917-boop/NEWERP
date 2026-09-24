namespace ERP.Application.DTOs;

/// <summary>
/// 新增收款引用（分摊）行请求（ERP-053）。
/// <para>客户端只能提交「收款单 Id + 销售订单 Id + 引用金额 + 备注」；收款单 / 客户 / 销售订单快照、
/// 状态与审计字段一律由服务端权威写入，<strong>不</strong>接受客户端提交的客户、币种或快照值。</para>
/// </summary>
public sealed class CustomerReceiptAllocationSaveDto
{
    /// <summary>收款单 Id（必须存在且未删除；收款单本身不会被本接口改写）</summary>
    public long ReceiptId { get; set; }

    /// <summary>销售订单 Id（必须存在、未删除、未取消，且客户与币种都与收款单一致）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>引用金额（原币；按币种精度取整后必须大于 0，且与既有有效行合计不得超过收款单金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>备注（可选，有界）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>作废收款引用行请求（ERP-053）：必须显式填写原因，作废保留原始值而不是删除。</summary>
public sealed class CustomerReceiptAllocationVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>收款引用（分摊）行台账查询参数（ERP-053，全部为可选过滤；结果分页返回）</summary>
public sealed class CustomerReceiptAllocationQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>收款单筛选（留空 = 全部收款单）</summary>
    public long? ReceiptId { get; set; }

    /// <summary>销售订单筛选（留空 = 全部订单）</summary>
    public long? SalesOrderId { get; set; }

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

    /// <summary>关键字（匹配收款单号 / 销售订单号 / 客户名称 / 客户编码；留空 = 不过滤）</summary>
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
/// 收款引用（分摊）行（ERP-053，读取用）：收款单快照 + 销售订单快照 + 引用金额 + 状态 / 作废留痕。
/// <para>可用性标注为**只读派生**：收款单被软删除或销售订单被软删除 / 取消后历史证据照常可读，
/// 只是显式标注为不可用 / 已取消；<strong>不</strong>代表款项已到账、订单已收款、债务已清偿或应收账款已核销。</para>
/// </summary>
public sealed record CustomerReceiptAllocationDto(
    long Id,
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    int ReceiptStatus,
    string ReceiptStatusText,
    decimal ReceiptAmount,
    long SalesOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    string OrderCurrency,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
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
    bool ReceiptAvailable,
    string ReceiptAvailabilityText,
    bool OrderAvailable,
    string OrderAvailabilityText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText);

/// <summary>
/// 收款单侧汇总（ERP-053，只读派生）：收款单快照 + 有效引用金额 / 未引用金额 / 行数 + 逐行明细。
/// <para><see cref="AllocatedAmount"/> 只按**有效（未作废）持久化行**派生，下限 0 处理；
/// 未引用金额绝不被猜测到任何订单，也<strong>不</strong>表示应收未收余额或催收依据。
/// 已作废行单独计数与列出，永不并入有效合计。</para>
/// </summary>
public sealed record CustomerReceiptAllocationReceiptSummaryDto(
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
    string BoundaryText,
    List<CustomerReceiptAllocationDto> Allocations);

/// <summary>
/// 可引用收款单候选（ERP-053，只读有界）：既有、未删除的客户收款单 + 已引用 / 未引用金额与资格文案
/// （资格文案同时附带客户可用性只读说明；客户停用 / 删除不改变历史快照可读性）。
/// <para><see cref="UnallocatedAmount"/> 只按**有效持久化行**派生（下限 0）；它只是本登记册口径的
/// 「收款单金额中尚未被任何引用行指向的部分」，<strong>不是</strong>应收账款余额、银行未到账金额或客户欠款。</para>
/// </summary>
public sealed record CustomerReceiptAllocationReceiptCandidateDto(
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    int ReceiptStatus,
    string ReceiptStatusText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    decimal ReceiptAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    int AllocationCount,
    bool Eligible,
    string EligibilityText);

/// <summary>
/// 可引用销售订单候选（ERP-053，只读有界）：同客户 + 同币种（含已取消订单并显式标注不可引用）。
/// <para><see cref="RemainingUnallocatedAmount"/> 只按**有效持久化行**派生，下限 0；仅为展示上下文，
/// <strong>不是</strong>订单应收余额、账龄、信用额度或催收依据。</para>
/// </summary>
public sealed record CustomerReceiptAllocationOrderCandidateDto(
    long SalesOrderId,
    string OrderNo,
    DateTime OrderDate,
    string StatusText,
    bool Cancelled,
    string Currency,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    decimal OrderedAmount,
    decimal AllocatedByThisReceipt,
    decimal AllocatedByOtherReceipts,
    decimal RemainingUnallocatedAmount,
    bool Eligible,
    string EligibilityText);
