namespace ERP.Application.DTOs;

/// <summary>
/// 动态客户订单与收款核对报表（ERP-164）的只读数据传输对象：字段目录（有限白名单）、预览查询参数、预览结果页与未关联收款证据。
/// <para>本 DTO 只描述「选择哪些 ERP-046 订单证据字段 + 用什么有界筛选预览哪些订单」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 客户订单与收款核对报表字段目录项（ERP-164，只读）：来自 <c>SalesOrderReceiptReconciliationOrderRow</c>
/// 证据行的有限白名单，由服务端规则统一供给，界面 / 导出共用同一份口径。
/// </summary>
public sealed record DynamicReceiptReconciliationReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 客户订单与收款核对报表预览查询参数（ERP-164，全部为只读筛选）：
/// 选定字段（仅限白名单）、客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态有界筛选，以及稳定分页（单页上限 200）。
/// <para>所有筛选均复用 ERP-046 客户订单与收款核对报表的既有口径；非法取值一律 fail closed 拒绝，绝不静默忽略。</para>
/// </summary>
public sealed class DynamicReceiptReconciliationReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，目录顺序）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>选定的未关联收款证据字段键（仅限白名单；留空 = 返回全部收款证据白名单字段，目录顺序）</summary>
    public List<string>? ReceiptFields { get; set; }

    /// <summary>客户 Id 筛选（留空 = 全部；非正数直接拒绝，且仅在当前业务员数据范围内生效）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成行、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>出货状态筛选（none / shipped；留空 = 全部）</summary>
    public string? ShipmentStatus { get; set; }

    /// <summary>收款链接状态筛选（linked / partial / unlinked；留空 = 全部）</summary>
    public string? ReceiptLinkStatus { get; set; }

    /// <summary>收款证据状态筛选（active 默认 / pending / historical / all；历史与未审核金额永不并入有效合计）</summary>
    public string? ReceiptStatus { get; set; }

    /// <summary>订单状态筛选（active 默认：排除已取消 / cancelled：仅已取消 / all：全部未删除；软删除订单一律排除）</summary>
    public string? OrderStatus { get; set; }

    /// <summary>关键字（匹配订单号 / 外销合同号 / 客户 PO 号；超长直接拒绝）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// 客户订单与收款核对报表字段目录（ERP-164，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源）。
/// </summary>
public sealed record DynamicReceiptReconciliationReportCatalogDto(
    List<DynamicReceiptReconciliationReportFieldDto> Fields,
    List<DynamicReceiptReconciliationReportFieldDto> ReceiptFields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    string ReadOnlyText,
    string BoundaryText);

/// <summary>
/// 未关联收款证据行（ERP-164，只读）：收款单只记录客户（<c>FinanceReceipt.CustomerId</c>），没有订单级引用，
/// 因此链接状态恒为 unlinked，金额只按收款单自身原币原样列出，绝不并入订单侧合计、绝不猜测匹配到任何订单。
/// </summary>
public sealed record DynamicReceiptReconciliationReportReceiptDto(
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    long CustomerId,
    string CustomerName,
    string Currency,
    decimal Amount,
    string PaymentMethod,
    string Status,
    string EvidenceStatus,
    string EvidenceText,
    string ReceiptLinkageStatus,
    string ReceiptLinkageText,
    string ReferenceField,
    string Note);

/// <summary>
/// 客户订单与收款核对报表预览结果页（ERP-164，只读）：按请求顺序返回选定列与分页订单行，以及本页客户范围内的未关联收款证据。
/// <para>行内仅包含选定的白名单 ERP-046 订单证据字段值，不泄露范围外订单数据；收款申请链接证据、收款引用登记证据、销项发票登记证据
/// 与客户级未关联收款证据各自独立、绝不合并、绝不相加。金额与数量一律保留原币与未知语义：未知为 null，绝不推算或修复。</para>
/// </summary>
public sealed record DynamicReceiptReconciliationReportPageDto(
    List<DynamicReceiptReconciliationReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    List<DynamicReceiptReconciliationReportReceiptDto> UnlinkedReceipts,
    List<DynamicReceiptReconciliationReportFieldDto> ReceiptColumns,
    List<Dictionary<string, object?>> ReceiptRows,
    bool UnlinkedReceiptTruncated,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
