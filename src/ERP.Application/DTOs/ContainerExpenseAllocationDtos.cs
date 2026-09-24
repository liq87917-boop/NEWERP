namespace ERP.Application.DTOs;

/// <summary>
/// 分摊基准输入行（ERP-042）：某参与方在本次分摊中使用的基数值。
/// <para><see cref="BasisValue"/> 为 <c>null</c> 表示**缺失**——服务端一律拒绝并要求显式填写，
/// 不会按体积经验值、历史比例或列表顺序推断（整柜法不使用本字段，基数取装柜清单持久化总量）。</para>
/// </summary>
public sealed class ContainerExpenseAllocationBasisDto
{
    /// <summary>装柜清单参与方 Id（必须是该装柜清单**启用中**的参与方）</summary>
    public long ParticipantId { get; set; }

    /// <summary>基数值（体积 m³ / 重量 kg / 箱数 / 金额，按所选分摊方法对应；缺失即拒绝）</summary>
    public decimal? BasisValue { get; set; }
}

/// <summary>
/// 装柜费用分摊请求（ERP-042，预览与生成共用）：对一条**柜级来源费用单**按某个装柜清单的启用参与方分摊。
/// <para>客户端只能提交「来源费用 + 装柜清单 + 方法 + 各参与方基数值 + 备注」；
/// 客户快照、比例、分摊金额、批次号与留痕字段一律由服务端权威计算与写入。</para>
/// </summary>
public sealed class ContainerExpenseAllocationRequest
{
    /// <summary>来源费用单 Id（必须是该柜的柜级费用单，且未参与任何批次、不是历史分摊明细行）</summary>
    public long SourceExpenseId { get; set; }

    /// <summary>装柜清单 Id（分摊范围 = 该清单启用中的参与方）</summary>
    public long LoadingListId { get; set; }

    /// <summary>分摊方法：按体积 / 按重量 / 按箱数 / 按金额 / 整柜（不填或未知方法一律拒绝）</summary>
    public string AllocationMethod { get; set; } = "按体积";

    /// <summary>整柜法的持久化基数种类：箱数 / 毛重 / 体积（仅整柜法使用；缺失或持久化值为 0 一律拒绝）</summary>
    public string WholeContainerBasisKind { get; set; } = "箱数";

    /// <summary>
    /// 整柜法目标参与方 Id：整柜法把全额归**显式指定的唯一参与方**；
    /// 未指定时要求该柜恰好只有一条启用参与方（多于一条且未指定一律拒绝，不按主参与方或列表顺序猜测）。
    /// </summary>
    public long? WholeContainerParticipantId { get; set; }

    /// <summary>备注（写入批次与生成的费用单行）</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>各参与方基准值（非整柜法必须覆盖该柜全部启用参与方；缺失或多余都会被告知）</summary>
    public List<ContainerExpenseAllocationBasisDto> Lines { get; set; } = new();
}

/// <summary>作废分摊批次请求（ERP-042）：必须显式填写原因，作废保留历史而不是删除。</summary>
public sealed class ContainerExpenseAllocationVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入批次留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>分摊批次台账查询参数（ERP-042，全部为可选过滤；结果分页返回）</summary>
public sealed class ContainerExpenseAllocationBatchQuery
{
    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>来源费用单 Id（可选）</summary>
    public long? SourceExpenseId { get; set; }

    /// <summary>装柜清单 Id（可选）</summary>
    public long? LoadingListId { get; set; }

    /// <summary>批次状态（1=有效 / 0=已作废；不填 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>柜号 / 装柜清单号 / 批次号关键字（可选，模糊匹配）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 PageQuery 同口径）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = 20;
        if (PageSize > 200) PageSize = 200;
    }
}

/// <summary>
/// 分摊结果行（ERP-042，预览与台账共用）：参与方 / 客户、方法、基数种类与来源、基数值、比例、分摊金额。
/// <para>金额为**原币**并按币种精度取整，行合计恒等于来源费用金额。</para>
/// </summary>
public sealed record ContainerExpenseAllocationLineDto(
    long ParticipantId,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string CustomerDisplay,
    bool CustomerAvailable,
    string CustomerAvailabilityText,
    bool IsPrimary,
    string AllocationMethod,
    string BasisKind,
    string BasisSource,
    decimal BasisValue,
    string BasisEvidence,
    decimal Ratio,
    decimal AllocatedAmount,
    decimal AllocatedAmountCny,
    string Currency,
    bool RemainderCarrier,
    long? ExpenseId,
    string ExpenseNo,
    int SortOrder);

/// <summary>
/// 分摊预览（ERP-042，**只读**）：来源费用、装柜清单、方法、各参与方比例与分摊金额、余差归属与边界声明。
/// <para>预览不写库：不新增费用单行、不建批次、不改写装柜清单 / 明细 / 参与方 / 单证 / 库存。</para>
/// </summary>
public sealed record ContainerExpenseAllocationPreviewDto(
    long SourceExpenseId,
    string SourceExpenseNo,
    string SourceExpenseType,
    decimal SourceAmount,
    decimal SourceAmountCny,
    string Currency,
    int AmountPrecision,
    string RefType,
    string RefNo,
    long LoadingListId,
    string LoadingListNo,
    string ContainerNo,
    string AllocationMethod,
    string BasisKind,
    string BasisSource,
    int ParticipantCount,
    decimal RatioTotal,
    decimal AllocatedTotal,
    decimal Remainder,
    long? RemainderCarrierParticipantId,
    string RemainderRuleText,
    string WholeContainerRuleText,
    string ScopeText,
    decimal PersistedTotalCartons,
    decimal PersistedTotalWeight,
    decimal PersistedTotalVolume,
    string BoundaryText,
    List<ContainerExpenseAllocationLineDto> Lines);

/// <summary>分摊生成结果（ERP-042）：批次（含批次号）+ 逐行留痕 + 生成的费用单号（同一事务写入）。</summary>
public sealed record ContainerExpenseAllocationGenerateResultDto(
    long BatchId,
    string BatchNo,
    long SourceExpenseId,
    string SourceExpenseNo,
    long LoadingListId,
    string LoadingListNo,
    string ContainerNo,
    string AllocationMethod,
    string Currency,
    decimal SourceAmount,
    decimal AllocatedTotal,
    int LineCount,
    string StatusText,
    List<string> ExpenseNos,
    List<ContainerExpenseAllocationLineDto> Lines);

/// <summary>分摊批次台账（ERP-042）：批次自身字段 + 状态文案 + 逐行留痕（读侧不写库）。</summary>
public sealed record ContainerExpenseAllocationBatchDto(
    long Id,
    string BatchNo,
    long SourceExpenseId,
    string SourceExpenseNo,
    long LoadingListId,
    string LoadingListNo,
    string ContainerNo,
    string AllocationMethod,
    string BasisKind,
    string Currency,
    decimal ExchangeRate,
    decimal SourceAmount,
    decimal AllocatedTotal,
    int LineCount,
    int Status,
    string StatusText,
    bool IsActive,
    bool IsVoided,
    DateTime? VoidedAt,
    string VoidReason,
    string Remark,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string BoundaryText,
    List<ContainerExpenseAllocationLineDto> Lines);

/// <summary>
/// 可分摊来源费用行（ERP-042，只读）：费用单自身字段 + 资格判定文案 + 留痕分类，
/// 便于界面一次性列出该柜的费用单并解释为什么某条不能作为分摊来源。
/// </summary>
public sealed record ContainerExpenseAllocationSourceDto(
    long Id,
    string ExpenseNo,
    string ExpenseType,
    decimal Amount,
    string Currency,
    string RefType,
    string RefNo,
    bool Eligible,
    string EligibilityText,
    string Lineage,
    string LineageText,
    string AllocationBatchNo);

/// <summary>
/// 分摊上下文（ERP-042，只读）：装柜清单与其启用参与方、持久化装柜总量、该柜的费用单资格清单、
/// 现存有效批次与已作废批次，以及支持的方法与边界声明。一次请求返回，无逐行数据库查询。
/// </summary>
public sealed record ContainerExpenseAllocationContextDto(
    long LoadingListId,
    string LoadingListNo,
    string ContainerNo,
    decimal PersistedTotalCartons,
    decimal PersistedTotalWeight,
    decimal PersistedTotalVolume,
    bool HasPersistedEvidence,
    int ActiveParticipantCount,
    int TotalParticipantCount,
    long? PrimaryParticipantId,
    string PrimaryParticipantCustomerName,
    bool LegacySingleCustomer,
    string ParticipantScopeText,
    List<ContainerLoadingParticipantDto> Participants,
    List<ContainerExpenseAllocationSourceDto> SourceExpenses,
    List<ContainerExpenseAllocationBatchDto> ActiveBatches,
    List<ContainerExpenseAllocationBatchDto> VoidedBatches,
    List<string> SupportedMethods,
    List<string> WholeContainerBasisKinds,
    string RemainderRuleText,
    string WholeContainerRuleText,
    string BoundaryText);
