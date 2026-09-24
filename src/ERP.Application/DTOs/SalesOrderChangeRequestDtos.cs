using ERP.Domain.Enums;

namespace ERP.Application.DTOs;

/// <summary>
/// 销售订单变更申请明细（ERP-047，客户端输入）：一行 = 来源行（可选）在该申请中的拟议值。
/// <para>客户端<strong>不</strong>提交金额：<c>Amount</c> 一律由服务端按「数量 × 单价」重算；
/// 也不提交来源快照（来源明细由服务端在登记时冻结）。</para>
/// </summary>
public sealed class SalesOrderChangeRequestDetailSaveDto
{
    /// <summary>行号（1 起；&lt;= 0 时服务端按提交顺序补齐，仅用于界面稳定对照）</summary>
    public int LineNo { get; set; }

    /// <summary>拟议移除该来源行（true = 来源行在拟议中被移除，来源快照仍保留；新增行必须为 false）</summary>
    public bool Removed { get; set; }

    /// <summary>拟议商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>拟议商品名称（必填、有界）</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>拟议规格（可选、有界）</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>拟议单位（可选、有界）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>拟议数量（必须 &gt; 0，与销售订单同口径）</summary>
    public decimal Quantity { get; set; }

    /// <summary>拟议单价（不得为负，与销售订单同口径）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>拟议交货日期（可选，逐行跟踪用）</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>拟议备注（可选、有界）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 销售订单变更申请保存请求（ERP-047；新建草稿与编辑草稿共用）。
/// <para>客户端只能提交「拟议主表值 + 拟议明细值 + 变更原因 + 来源销售订单 Id」；
/// 申请单号、来源快照、拟议合计 / 定金金额、状态、提交 / 取消时间与审计字段一律由服务端权威写入。</para>
/// <para><b>缺省语义（重要，界面与接口同源）：</b>
/// 可空字段传 <c>null</c>（或数值型 &lt;= 0 / 枚举缺省）表示「本次不修改，沿用来源快照（编辑草稿时沿用上一次拟议值）」；
/// 文本字段传 <c>null</c> 同理沿用，传空串 <c>""</c> 表示**显式清空**；
/// <see cref="Details"/> 传空集合表示「拟议回到来源快照（原样留痕）」，要移除来源行必须逐行标记 <c>Removed</c>。</para>
/// <para>边界：本请求<strong>不</strong>包含任何「批准 / 套用」标志：服务端也没有审批阈值、审批人、
/// 生效时间等字段 —— 申请永远不会改写来源销售订单与下游记录。</para>
/// </summary>
public sealed class SalesOrderChangeRequestSaveDto
{
    /// <summary>来源销售订单 Id（新建必填：必须存在、未删除且未作废；编辑时若填写则必须与已登记来源一致）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>变更原因（必填、有界；作为人工审阅依据）</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>拟议订单日期（null = 沿用来源值）</summary>
    public DateTime? OrderDate { get; set; }

    /// <summary>拟议客户 Id（&lt;= 0 = 沿用来源值）</summary>
    public long CustomerId { get; set; }

    /// <summary>拟议业务员 Id（null = 沿用来源值；0 视为显式清空业务员）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>拟议币种（null = 沿用来源值；币种枚举从 1 开始，不存在 0 值）</summary>
    public Currency? Currency { get; set; }

    /// <summary>拟议汇率（null 或 &lt;= 0 = 沿用来源值；显式提交时必须 &gt; 0）</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>拟议定金比例（%，null = 沿用来源值；显式提交时必须在 0~100）</summary>
    public decimal? DepositRatio { get; set; }

    /// <summary>拟议付款条件（null = 沿用；"" = 显式清空；有界）</summary>
    public string? PaymentTerms { get; set; }

    /// <summary>拟议交货日期（null = 沿用来源值）</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>拟议运输方式（null = 沿用；"" = 显式清空；有界）</summary>
    public string? ShippingMethod { get; set; }

    /// <summary>拟议目的港 Id（null = 沿用来源值）</summary>
    public long? PortId { get; set; }

    /// <summary>拟议备注（null = 沿用；"" = 显式清空；有界）</summary>
    public string? Remark { get; set; }

    /// <summary>拟议客户 PO 号（null = 沿用；"" = 显式清空；有界）</summary>
    public string? CustomerPoNo { get; set; }

    /// <summary>拟议外销合同号（null = 沿用；"" = 显式清空；有界）</summary>
    public string? ContractNo { get; set; }

    /// <summary>拟议价格条款 / 贸易术语（null = 沿用；"" = 显式清空；有界）</summary>
    public string? TradeTerms { get; set; }

    /// <summary>拟议目的港文本（null = 沿用；"" = 显式清空；有界）</summary>
    public string? DestinationPort { get; set; }

    /// <summary>拟议收货人（null = 沿用；"" = 显式清空；有界）</summary>
    public string? Consignee { get; set; }

    /// <summary>拟议通知人（null = 沿用；"" = 显式清空；有界）</summary>
    public string? NotifyParty { get; set; }

    /// <summary>拟议唛头（null = 沿用；"" = 显式清空；有界）</summary>
    public string? ShippingMarks { get; set; }

    /// <summary>拟议出口方式（null = 沿用；"" = 显式清空；有界）</summary>
    public string? ExportMode { get; set; }

    /// <summary>拟议佣金比例（%，null = 沿用来源值；显式提交时必须在 0~100）</summary>
    public decimal? CommissionRatio { get; set; }

    /// <summary>拟议业务性质（null = 沿用；"" = 显式清空；有界）</summary>
    public string? BusinessNature { get; set; }

    /// <summary>拟议是否分批出货（null = 沿用来源值）</summary>
    public bool? SplitShipment { get; set; }

    /// <summary>拟议验货要求（null = 沿用；"" = 显式清空；有界）</summary>
    public string? InspectionRequirement { get; set; }

    /// <summary>拟议包装要求（null = 沿用；"" = 显式清空；有界）</summary>
    public string? PackagingRequirement { get; set; }

    /// <summary>拟议明细（空集合 = 回到来源快照；非空时必须先逐行覆盖全部来源行，之后的行视为新增行）</summary>
    public List<SalesOrderChangeRequestDetailSaveDto>? Details { get; set; }
}

/// <summary>取消销售订单变更申请请求（ERP-047）：必须显式填写原因；取消保留原始与拟议证据，不做硬删除。</summary>
public sealed class SalesOrderChangeRequestCancelRequest
{
    /// <summary>取消原因（必填；写入取消留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>变更申请台账查询参数（ERP-047，全部可选；结果分页返回，默认每页 50、单次最多 200 条）。</summary>
public sealed class SalesOrderChangeRequestQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多条申请）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>来源销售订单 Id 筛选（留空 = 全部来源订单）</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>状态筛选（0 草稿 / 1 已提交 / 2 已取消；留空 = 全部）</summary>
    public int? Status { get; set; }

    /// <summary>关键字（匹配申请单号 / 来源订单号 / 变更原因，长度上限 100；不做全表无界模糊扫描）</summary>
    public string? Keyword { get; set; }
}

/// <summary>白名单选项（ERP-047：值 + 中文文案，供界面与接口同源显示）。</summary>
public sealed record SalesOrderChangeRequestOptionDto(string Value, string Label);

/// <summary>
/// 来源销售订单候选（ERP-047，只读有界）：用于在界面上选择**既有且未删除**的销售订单发起变更申请。
/// <para>候选只按单号关键字检索，绝不按客户名 / 金额 / 日期相似度猜测来源单据。</para>
/// </summary>
public sealed record SalesOrderChangeRequestSourceOptionDto(
    long SalesOrderId,
    string OrderNo,
    string StatusText,
    string SummaryText,
    bool Selectable,
    string SelectableText);

/// <summary>
/// 主表字段对照（ERP-047）：同一字段的来源快照值与拟议值并排显示，<see cref="Changed"/> 只表示
/// 「拟议与来源快照不同」，**不**表示已批准或已生效。
/// </summary>
public sealed record SalesOrderChangeRequestFieldComparisonDto(
    string Field,
    string Label,
    string SourceText,
    string ProposedText,
    bool Changed);

/// <summary>
/// 变更申请明细对照（ERP-047）：来源行快照与拟议值并排显示；<see cref="HasSourceLine"/> = false 表示拟议新增行，
/// <see cref="ProposedRemoved"/> = true 表示来源行拟议移除（来源快照仍完整保留）。
/// </summary>
public sealed record SalesOrderChangeRequestDetailViewDto(
    int LineNo,
    bool HasSourceLine,
    string SourceProductName,
    string SourceSpec,
    string SourceUnit,
    decimal SourceQuantity,
    decimal SourceUnitPrice,
    decimal SourceAmount,
    DateTime? SourceDeliveryDate,
    string SourceRemark,
    bool ProposedRemoved,
    long ProposedProductId,
    string ProposedProductName,
    string ProposedSpec,
    string ProposedUnit,
    decimal ProposedQuantity,
    decimal ProposedUnitPrice,
    decimal ProposedAmount,
    DateTime? ProposedDeliveryDate,
    string ProposedRemark,
    bool Changed,
    bool QuantityChanged,
    bool UnitPriceChanged,
    string ComparisonText);

/// <summary>
/// 拟议主表原始值（ERP-047，只读回显）：供界面「编辑草稿」表单直接回填（日期 / 数值 / 枚举均为原始值，
/// 而不是对照用的格式化文案）。来源快照与拟议合计 / 定金金额仍以主对象字段为准。
/// </summary>
public sealed record SalesOrderChangeRequestProposedHeaderDto(
    DateTime OrderDate,
    long CustomerId,
    long? SalesmanId,
    Currency Currency,
    decimal ExchangeRate,
    decimal DepositRatio,
    string PaymentTerms,
    DateTime? DeliveryDate,
    string ShippingMethod,
    long? PortId,
    string Remark,
    string CustomerPoNo,
    string ContractNo,
    string TradeTerms,
    string DestinationPort,
    string Consignee,
    string NotifyParty,
    string ShippingMarks,
    string ExportMode,
    decimal CommissionRatio,
    string BusinessNature,
    bool SplitShipment,
    string InspectionRequirement,
    string PackagingRequirement);

/// <summary>
/// 变更申请台账 / 详情（ERP-047）。
/// <para><see cref="SourceChanged"/> / <see cref="SourceChangedText"/> / <see cref="SourceChangeDetailText"/> 是
/// **只读提示**：来源销售订单在快照之后发生变化时照实说明并给出对照依据，
/// <strong>不</strong>覆盖、<strong>不</strong>合并、<strong>不</strong>静默刷新拟议值。</para>
/// <para>本 DTO <strong>不</strong>包含任何「已批准 / 已套用」字段：<see cref="Status"/> 只有草稿 / 已提交 / 已取消，
/// <see cref="HeaderComparisons"/> 与明细对照的差异只表示「拟议与来源不同」，不代表任何审批结果。</para>
/// </summary>
public sealed record SalesOrderChangeRequestDto(
    long Id,
    string RequestNo,
    long SalesOrderId,
    string SalesOrderNo,
    string SourceStatusText,
    DateTime SourceUpdatedAt,
    string SourceSnapshotMarker,
    bool SourceAvailable,
    string SourceAvailabilityText,
    bool SourceChanged,
    string SourceChangedText,
    string SourceChangeDetailText,
    string Reason,
    int Status,
    string StatusText,
    bool IsDraft,
    bool IsSubmitted,
    bool IsCancelled,
    bool Editable,
    DateTime? SubmittedAt,
    DateTime? CancelledAt,
    string CancelledReason,
    SalesOrderChangeRequestProposedHeaderDto Proposed,
    decimal SourceTotalAmount,
    decimal ProposedTotalAmount,
    decimal SourceDepositAmount,
    decimal ProposedDepositAmount,
    List<SalesOrderChangeRequestFieldComparisonDto> HeaderComparisons,
    int HeaderChangedCount,
    List<SalesOrderChangeRequestDetailViewDto> Details,
    int AddedLineCount,
    int RemovedLineCount,
    int ChangedLineCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string ChangeSummaryText,
    string ApprovalBoundaryText,
    string BoundaryText);

/// <summary>
/// 变更申请模块元数据（ERP-047，只读）：状态 / 币种白名单、分页与上限、快照与金额口径、审批与边界文案，
/// 供界面与接口同源显示，避免前端硬编码与后端口径漂移。
/// </summary>
public sealed record SalesOrderChangeRequestMetadataDto(
    List<SalesOrderChangeRequestOptionDto> StatusOptions,
    List<SalesOrderChangeRequestOptionDto> CurrencyOptions,
    int MaxPageSize,
    int MaxPerSourceOrder,
    int MaxDetailLines,
    string SnapshotPolicyText,
    string AmountPolicyText,
    string SubmitPolicyText,
    string ApprovalBoundaryText,
    string BoundaryText);
