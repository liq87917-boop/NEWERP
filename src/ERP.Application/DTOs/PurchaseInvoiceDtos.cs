namespace ERP.Application.DTOs;

/// <summary>
/// 供应商采购发票登记 / 修改请求（ERP-043，ERP-065 扩展）。
/// <para>客户端只能提交发票本身字段（类型 / 代码 / 号码 / 日期 / 到期日 / 付款条件 / 供应商 / 币种 / 三个金额 / 备注）；
/// 供应商编码名称快照、规范化身份列、状态与审计字段一律由服务端权威写入。</para>
/// </summary>
public sealed class PurchaseInvoiceSaveDto
{
    /// <summary>发票类型：普票 / 专票（未知类型一律拒绝）</summary>
    public string InvoiceType { get; set; } = "普票";

    /// <summary>发票代码（专票必填、普票可选）</summary>
    public string? InvoiceCode { get; set; }

    /// <summary>发票号码（必填；供应商发票上的号码）</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>开票日期（留空按当天；不改写任何既有单据日期）</summary>
    public DateTime? InvoiceDate { get; set; }

    /// <summary>供应商 Id（必须存在、未删除且启用；停用 / 删除的供应商一律拒绝）</summary>
    public long SupplierId { get; set; }

    /// <summary>币种（CNY / USD / EUR / HKD / GBP / JPY；关联采购订单时要求一致）</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>不含税金额（净额，原币）</summary>
    public decimal NetAmount { get; set; }

    /// <summary>税额（原币；允许为 0）</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>含税总额（价税合计，原币；必须等于净额 + 税额且大于 0）</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// 到期日（ERP-065，可选、显式证据）：留空 = **未知**（服务端不按供应商默认账期、付款条件或备注推算）；
    /// 填写时只取日期部分，且不得早于开票日期。
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// 付款条件快照（ERP-065，可选、显式证据）：留空 = 未提供；去首尾空白后长度 ≤ 200，
    /// 原样作为文本证据保存（服务端不解析文本、不折算账期）。
    /// </summary>
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>备注</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>关联（分摊）到采购订单的请求行（ERP-043）</summary>
public sealed class PurchaseInvoiceAllocationSaveDto
{
    /// <summary>采购订单 Id（必须存在、未删除、未取消，且供应商与币种都与发票一致）</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>关联金额（原币；必须大于 0，合计不得超过发票含税总额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>备注（可选）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 关联（分摊）整体替换请求（ERP-043）：草稿发票提交的是一份**完整**的关联清单，
/// 服务端在同一事务内整体替换（草稿期关联属于工作数据）；已登记 / 已作废发票拒绝任何关联改动。
/// </summary>
public sealed class PurchaseInvoiceAllocationSaveRequest
{
    /// <summary>关联行（可为空 = 清空全部关联，发票保持未关联状态）</summary>
    public List<PurchaseInvoiceAllocationSaveDto> Lines { get; set; } = new();
}

/// <summary>作废发票请求（ERP-043）：必须显式填写原因，作废保留历史而不是删除。</summary>
public sealed class PurchaseInvoiceVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入发票留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>供应商采购发票台账查询参数（ERP-043，全部为可选过滤；结果分页返回）</summary>
public sealed class PurchaseInvoiceQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多发票）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>发票类型筛选（普票 / 专票；留空 = 全部）</summary>
    public string? InvoiceType { get; set; }

    /// <summary>状态筛选（0 草稿 / 1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>币种筛选（留空 = 全部币种；不同币种分别汇总，绝不合并为一个金额）</summary>
    public string? Currency { get; set; }

    /// <summary>开票日期开始（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateFrom { get; set; }

    /// <summary>开票日期结束（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateTo { get; set; }

    /// <summary>关联状态筛选（linked 已全额关联 / partial 部分关联 / unlinked 未关联；留空 = 全部）</summary>
    public string? LinkageStatus { get; set; }

    /// <summary>关键字（匹配发票号码 / 发票代码 / 供应商名称 / 供应商编码；留空 = 不过滤）</summary>
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
/// 发票 → 采购订单 关联行（ERP-043，读取用）：采购订单快照 + 关联金额 + 订单当前可用性标注。
/// <para>可用性标注为**只读派生**：订单被软删除或取消后历史关联照常可读，只是显式标注为不可用 / 已取消。</para>
/// </summary>
public sealed record PurchaseInvoiceAllocationDto(
    long Id,
    long PurchaseOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    string Currency,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    decimal AllocatedAmount,
    int SortOrder,
    string Remark,
    bool OrderAvailable,
    string OrderAvailabilityText);

/// <summary>
/// 供应商采购发票台账 / 详情（ERP-043，ERP-065 扩展）：发票身份、金额、状态、已关联 / 未关联金额与关联行，
/// 以及**可选、显式**的到期日与付款条件证据。
/// <para><see cref="LinkedAmount"/> / <see cref="UnlinkedAmount"/> 只按**持久化关联行**计算，
/// 不会按单号、金额或日期相似度把未关联金额猜测到任何采购订单上。</para>
/// <para><see cref="DueDate"/> / <see cref="PaymentTerms"/> 只回显**登记时用户显式提交**的值：
/// 未填写时 <see cref="DueDateKnown"/> 为 false 且 <see cref="DueDateText"/> 明确显示「未知」，
/// 系统绝不按供应商默认账期、备注或历史发票推算到期日与账期。</para>
/// </summary>
public sealed record PurchaseInvoiceDto(
    long Id,
    string InvoiceType,
    string InvoiceTypeText,
    string InvoiceCodeRuleText,
    string InvoiceCode,
    string InvoiceNumber,
    string IdentityText,
    DateTime InvoiceDate,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    bool SupplierAvailable,
    string SupplierAvailabilityText,
    string Currency,
    int AmountDecimals,
    decimal NetAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    int Status,
    string StatusText,
    bool IsDraft,
    bool IsRecorded,
    bool IsVoided,
    DateTime? RecordedAt,
    DateTime? VoidedAt,
    string VoidReason,
    string Remark,
    DateTime? DueDate,
    bool DueDateKnown,
    string DueDateText,
    string PaymentTerms,
    string PaymentTermsText,
    int AllocationCount,
    decimal LinkedAmount,
    decimal UnlinkedAmount,
    string LinkageStatus,
    string LinkageText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string AmountEquationText,
    string LinkageRuleText,
    string EvidenceTermsRuleText,
    string BoundaryText,
    List<PurchaseInvoiceAllocationDto> Allocations);

/// <summary>关联预览行（ERP-043，只读）：候选采购订单快照 + 拟关联金额 + 资格判定文案。</summary>
public sealed record PurchaseInvoiceAllocationPreviewLineDto(
    long PurchaseOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    string Currency,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    decimal AllocatedAmount,
    string Remark,
    bool Eligible,
    string EligibilityText,
    decimal OrderTotalAmount,
    decimal LinkedByOtherInvoices,
    decimal LinkedByThisInvoice);

/// <summary>
/// 关联预览结果（ERP-043，只读不写库）：拟关联行 + 保存后的已关联 / 未关联金额。
/// <para>预览与保存共用同一套校验（<c>PurchaseInvoiceService.BuildAllocationPlanAsync</c>），预览所示即保存结果。</para>
/// </summary>
public sealed record PurchaseInvoiceAllocationPreviewDto(
    long InvoiceId,
    string IdentityText,
    long SupplierId,
    string SupplierName,
    string Currency,
    decimal GrossAmount,
    decimal PersistedLinkedAmount,
    decimal ProposedTotal,
    decimal UnlinkedAfterSave,
    string LinkageStatus,
    string LinkageText,
    int LineCount,
    string RuleText,
    string BoundaryText,
    List<PurchaseInvoiceAllocationPreviewLineDto> Lines);

/// <summary>
/// 可关联采购订单候选（ERP-043，只读有界）：同供应商 + 同币种（含已取消订单并显式标注不可关联）。
/// <para><see cref="RemainingUnallocatedAmount"/> 只按**已登记（非作废）发票**的持久化关联行派生，下限 0；
/// 它只是本登记册口径的「订单未被发票证据覆盖的金额」，<strong>不是</strong>应付余额、账龄或付款依据。</para>
/// </summary>
public sealed record PurchaseInvoiceOrderCandidateDto(
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
    decimal LinkedByThisInvoice,
    decimal LinkedByOtherInvoices,
    decimal RemainingUnallocatedAmount,
    bool Eligible,
    string EligibilityText);
