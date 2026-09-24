namespace ERP.Application.DTOs;

/// <summary>
/// 客户销项发票证据登记 / 修改请求（ERP-055）。
/// <para>客户端只能提交发票本身字段（类型 / 代码 / 号码 / 日期 / 客户 / 币种 / 三个金额 / 显式交叉引用 / 备注）；
/// 客户编码名称快照、单证号与类型快照、规范化身份列、状态与审计字段一律由服务端权威写入。</para>
/// </summary>
public sealed class CustomerSalesInvoiceEvidenceSaveDto
{
    /// <summary>发票类型：普票 / 专票 / 出口发票（未知类型一律拒绝）</summary>
    public string InvoiceType { get; set; } = "普票";

    /// <summary>发票代码（专票必填、普票与出口发票可选）</summary>
    public string? InvoiceCode { get; set; }

    /// <summary>发票号码（必填；发票上的号码）</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>开票日期（留空按当天；不改写任何既有单据日期）</summary>
    public DateTime? InvoiceDate { get; set; }

    /// <summary>客户 Id（必须存在、未删除且启用；停用 / 删除的客户一律拒绝）</summary>
    public long CustomerId { get; set; }

    /// <summary>币种（CNY / USD / EUR / HKD / GBP / JPY；分摊销售订单时要求一致）</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>不含税金额（净额，原币）</summary>
    public decimal NetAmount { get; set; }

    /// <summary>税额（原币；允许为 0）</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>含税总额（价税合计，原币；必须等于净额 + 税额且大于 0）</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// 显式引用的单证中心单证 Id（可选；必须是存在、未删除且类型为商业发票的单证）。
    /// <para>只作为**有界证据**：不读取单证金额、不转换单证、不自动链接；留空表示不做任何交叉引用。</para>
    /// </summary>
    public long? TradeDocumentId { get; set; }

    /// <summary>显式填写的商业发票号 / 单证引用说明（可选、有界文本；系统绝不据此自动匹配或链接）</summary>
    public string CommercialInvoiceReference { get; set; } = string.Empty;

    /// <summary>备注</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>分摊到销售订单的请求行（ERP-055）</summary>
public sealed class CustomerSalesInvoiceAllocationSaveDto
{
    /// <summary>销售订单 Id（必须存在、未删除、未取消，且客户与币种都与发票一致）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>分摊金额（原币；必须大于 0，合计不得超过发票含税总额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>备注（可选）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 分摊整体替换请求（ERP-055）：草稿发票提交的是一份**完整**的分摊清单，
/// 服务端校验通过后整体替换（草稿期分摊属于工作数据）；已登记 / 已作废发票拒绝任何分摊改动。
/// </summary>
public sealed class CustomerSalesInvoiceAllocationSaveRequest
{
    /// <summary>分摊行（可为空 = 清空全部分摊，发票保持未分摊状态）</summary>
    public List<CustomerSalesInvoiceAllocationSaveDto> Lines { get; set; } = new();
}

/// <summary>作废发票证据请求（ERP-055）：必须显式填写原因，作废保留历史而不是删除。</summary>
public sealed class CustomerSalesInvoiceVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>客户销项发票证据台账查询参数（ERP-055，全部为可选过滤；结果分页返回）</summary>
public sealed class CustomerSalesInvoiceEvidenceQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多张发票）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>客户筛选（留空 = 全部客户；不同客户绝不合并汇总）</summary>
    public long? CustomerId { get; set; }

    /// <summary>发票类型筛选（普票 / 专票 / 出口发票；留空 = 全部）</summary>
    public string? InvoiceType { get; set; }

    /// <summary>状态筛选（0 草稿 / 1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>币种筛选（留空 = 全部币种；不同币种分别汇总，绝不合并为一个金额）</summary>
    public string? Currency { get; set; }
    /// <summary>开票日期开始（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateFrom { get; set; }

    /// <summary>开票日期结束（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateTo { get; set; }

    /// <summary>分摊状态筛选（linked 已全额分摊 / partial 部分分摊 / unlinked 未分摊；留空 = 全部）</summary>
    public string? LinkageStatus { get; set; }

    /// <summary>销售订单筛选（留空 = 全部；只用于按分摊行反查发票）</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>关键字（匹配发票号码 / 发票代码 / 客户名称 / 客户编码 / 商业发票引用；留空 = 不过滤）</summary>
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
/// 客户销项发票证据 → 销售订单 分摊行（ERP-055，读取用）：销售订单快照 + 分摊金额 + 订单当前可用性标注。
/// <para>可用性标注为**只读派生**：订单被软删除 / 取消后历史分摊照常可读，只是显式标注为不可用；
/// <strong>不</strong>代表已开票、已收款、已核销、应收余额或订单已结清。</para>
/// </summary>
public sealed record CustomerSalesInvoiceAllocationDto(
    long Id,
    long SalesOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    string OrderCurrency,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    decimal AllocatedAmount,
    int SortOrder,
    string Remark,
    bool OrderAvailable,
    string OrderAvailabilityText);

/// <summary>
/// 客户销项发票证据（ERP-055，读取用）：发票身份 / 金额 / 状态 + 客户快照 + 显式单证交叉引用 +
/// 已分摊 / 未分摊金额 + 有界分摊明细。
/// <para>已分摊金额只按**有效持久化分摊行**派生，未分摊金额下限 0；未分摊部分绝不被猜测到任何订单，
/// 也<strong>不</strong>表示应收未收余额、未开票税金或催收依据。<see cref="LinkageText"/> 与
/// <see cref="BoundaryText"/> 由服务端与前端同源文案给出。</para>
/// </summary>
public sealed record CustomerSalesInvoiceEvidenceDto(
    long Id,
    string InvoiceType,
    string InvoiceCode,
    string InvoiceNumber,
    string IdentityText,
    DateTime InvoiceDate,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    bool CustomerAvailable,
    string CustomerAvailabilityText,
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
    long? TradeDocumentId,
    string TradeDocumentNo,
    string TradeDocumentDocType,
    string CommercialInvoiceReference,
    bool TradeDocumentAvailable,
    string TradeDocumentAvailabilityText,
    int AllocationCount,
    decimal LinkedAmount,
    decimal UnlinkedAmount,
    string LinkageStatus,
    string LinkageText,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string AmountEquationText,
    string LinkageRuleText,
    string TradeDocumentSeparationText,
    string BoundaryText,
    List<CustomerSalesInvoiceAllocationDto> Allocations);

/// <summary>分摊预览行（ERP-055，只读，不写库）：销售订单快照 + 资格文案 + 本次拟分摊金额与只读派生金额</summary>
public sealed record CustomerSalesInvoiceAllocationPreviewLineDto(
    long SalesOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    string Currency,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    decimal AllocatedAmount,
    string Remark,
    bool Eligible,
    string EligibilityText,
    decimal OrderedAmount,
    decimal AllocatedByOtherInvoices,
    decimal AllocatedByThisInvoice);

/// <summary>
/// 分摊预览结果（ERP-055，只读，不写库）：发票身份 + 拟分摊合计 + 保存后的已分摊 / 未分摊金额 + 逐行明细；
/// 预览与保存共用同一校验口径（订单资格 / 金额精度 / 合计上限）。
/// </summary>
public sealed record CustomerSalesInvoiceAllocationPreviewDto(
    long InvoiceId,
    string IdentityText,
    long CustomerId,
    string CustomerName,
    string Currency,
    decimal GrossAmount,
    decimal PersistedLinkedAmount,
    decimal ProposedLinkedAmount,
    decimal UnlinkedAmount,
    string LinkageStatus,
    string LinkageText,
    int AllocationCount,
    string LinkageRuleText,
    string BoundaryText,
    List<CustomerSalesInvoiceAllocationPreviewLineDto> Lines);

/// <summary>
/// 可分摊销售订单候选（ERP-055，只读有界）：同客户 + 同币种（含已取消订单并显式标注不可分摊）。
/// <para><see cref="RemainingUnallocatedAmount"/> 只按**有效持久化分摊行**派生、下限 0；仅为展示上下文，
/// <strong>不是</strong>订单应收余额、账龄、信用额度或催收依据。</para>
/// </summary>
public sealed record CustomerSalesInvoiceOrderCandidateDto(
    long SalesOrderId,
    string OrderNo,
    DateTime OrderDate,
    string OrderStatusText,
    bool Cancelled,
    string Currency,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    decimal OrderedAmount,
    decimal AllocatedByThisInvoice,
    decimal AllocatedByOtherInvoices,
    decimal RemainingUnallocatedAmount,
    bool Eligible,
    string EligibilityText);

/// <summary>
/// 可显式交叉引用的单证中心商业发票候选（ERP-055，只读有界）：只列出既有、未删除且类型为商业发票的单证，
/// 仅用于**显式选择**交叉引用来源；本登记册不读取单证金额、不转换单证、不建立自动链接。
/// </summary>
public sealed record CustomerSalesInvoiceTradeDocumentCandidateDto(
    long TradeDocumentId,
    string DocNo,
    string DocType,
    DateTime? IssueDate,
    string SalesOrderNo,
    string CustomerName,
    string Currency,
    decimal Amount,
    bool Eligible,
    string EligibilityText);
