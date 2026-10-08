namespace ERP.Application.DTOs;

/// <summary>
/// 装柜结算单来源装柜清单候选行（ERP-392，只读、有界、分页）：一条候选 = 一张<b>当前精确客户</b>确实是
/// 权威归属客户的装柜清单（ERP-041 有效参与方客户之一，或历史单客户兼容字段客户），且未删除、未取消。
/// <para>口径：候选只在既有「装柜结算单」（<c>container-settlement</c>）菜单 + 既有「装柜清单」（<c>loading-list</c>）
/// 菜单 + 实时客户数据范围之内返回；共享柜的<b>全部有效参与方客户</b>与显式上游（预装柜单 → 订柜信息）客户
/// 都必须落在当前账号范围内，否则整张清单不返回（不复用任何越范围共享柜）。归属只认显式参与方与显式兼容客户字段，
/// <b>绝不按柜号 / 单号等自由文本猜测链接</b>。</para>
/// <para>只暴露业务表单需要的权威 <c>Id / 单号 / 日期 / 柜号 / 客户 / 状态</c> 与参与方数量、上游归属；
/// 装柜结算单没有币种字段，因此候选<b>绝不</b>返回任何金额、汇率或跨币种合计，也不发明任何金额公式。</para>
/// </summary>
public class ContainerSettlementLoadingListCandidateDto
{
    /// <summary>来源装柜清单 Id（保存时写入 <c>FinanceContainerSettlement.LoadingListId</c>，绝不臆造）</summary>
    public long LoadingListId { get; set; }

    /// <summary>装柜清单号（权威展示快照）</summary>
    public string LoadingListNo { get; set; } = string.Empty;

    /// <summary>装柜日期</summary>
    public DateTime LoadingDate { get; set; }

    /// <summary>柜号（仅展示，绝不用于归属判定）</summary>
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>权威客户 Id（= 请求的精确客户 Id，且必为该清单的有效参与方或历史兼容客户）</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称（客户主数据当前名称；无法解析时为空串）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>装柜清单状态（既有 <c>DocumentStatus</c> 名称：Pending / Submitted / Approved / …）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>该候选是否可被显式选择为<b>新</b>来源：未删除且未取消（本接口只返回可选项，恒为 <c>true</c>）</summary>
    public bool Eligible { get; set; }

    /// <summary>不可选原因（<see cref="Eligible"/> 为 true 时为空串；服务端权威文案，界面原样展示）</summary>
    public string IneligibleReason { get; set; } = string.Empty;

    /// <summary>有效（启用、未删除）参与方数量；0 = 历史单客户视图（按持久化兼容客户字段判定）</summary>
    public int ActiveParticipantCount { get; set; }

    /// <summary>是否为历史单客户视图（没有任何有效参与方，客户归属只来自兼容客户字段）</summary>
    public bool LegacySingleCustomer { get; set; }

    /// <summary>显式上游预装柜单 Id（无显式链接时为 <c>null</c>，绝不按文本补链接）</summary>
    public long? PreLoadingId { get; set; }

    /// <summary>显式上游订柜客户 Id（预装柜单 → 订柜信息可解析时；否则 <c>null</c>）</summary>
    public long? UpstreamBookingCustomerId { get; set; }
}

/// <summary>
/// 装柜结算单来源装柜清单候选分页结果（ERP-392，只读、有界）：<see cref="Total"/> / <see cref="Page"/> /
/// <see cref="PageSize"/> 均在关键字与分页参数<b>归一化之后</b>、且在客户数据范围之下推之后统计，绝不无界拉取。
/// </summary>
public class ContainerSettlementLoadingListCandidatePageDto
{
    /// <summary>当前页候选行（有界）</summary>
    public List<ContainerSettlementLoadingListCandidateDto> Items { get; set; } = new();

    /// <summary>归一化 + 范围过滤后的候选总数（先归一化再计数）</summary>
    public int Total { get; set; }

    /// <summary>归一化后的页码（从 1 开始）</summary>
    public int Page { get; set; }

    /// <summary>归一化后的每页条数（有界）</summary>
    public int PageSize { get; set; }
}

/// <summary>
/// 装柜结算单已存储来源装柜清单的只读展示（ERP-392，详情 / 重开用）：未关联（历史）→ 显式「未关联」；
/// 已关联且来源有效 → 已关联 + 单号；来源已取消 → 已关联 + 「来源已取消，只读保留」；来源已删除 / 无法解析 →
/// 「来源不可用，原链接原样保留」。
/// <para>本 DTO 绝不写库、绝不重绑定 / 清除链接，也绝不因来源失效而抛异常（历史必须可读）；
/// <see cref="EligibleForNewLink"/> 只表示该来源当前是否还能作为<b>新</b>链接目标（候选选择不等于授权）。</para>
/// </summary>
public class ContainerSettlementLoadingSourceViewDto
{
    /// <summary>结算单持久化的来源装柜清单 Id（未关联时为 <c>null</c>）</summary>
    public long? LoadingListId { get; set; }

    /// <summary>装柜清单号（无法解析时为空串）</summary>
    public string LoadingListNo { get; set; } = string.Empty;

    /// <summary>装柜日期（无法解析时为 <c>null</c>）</summary>
    public DateTime? LoadingDate { get; set; }

    /// <summary>柜号（仅展示；无法解析时为空串）</summary>
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>装柜清单状态（无法解析时为空串）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>结算单是否已存储来源链接（持久化 Id 为正整数）</summary>
    public bool Linked { get; set; }

    /// <summary>该来源当前是否仍可作为<b>新</b>链接目标（未删除、未取消；已删除 / 已取消为 <c>false</c>）</summary>
    public bool EligibleForNewLink { get; set; }

    /// <summary>来源是否不可用（已删除 / 无法解析；原链接原样保留，绝不静默清除）</summary>
    public bool Unavailable { get; set; }

    /// <summary>来源是否已取消（原链接只读保留，绝不静默清除）</summary>
    public bool Cancelled { get; set; }

    /// <summary>显式状态文案（与 <c>FinanceContainerSettlementLifecycleRules</c> 同源，界面原样展示）</summary>
    public string Annotation { get; set; } = string.Empty;
}
