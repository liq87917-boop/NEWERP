namespace ERP.Application.DTOs;

/// <summary>
/// 采购订单来源销售订单候选行（ERP-393，只读、有界、分页）：一条候选 = 一张落在当前账号客户数据范围之内、
/// 且「已审核、未删除、未取消」的销售订单。
/// <para>口径：候选只在既有「采购订单」（<c>purchase-order</c>）菜单 + 实时客户数据范围之内返回，
/// 可选按<b>归属客户</b>精确收窄；<b>复用</b> <c>PurchaseSalesOrderLinkRules</c> 的可选性口径
/// （<c>Status == Approved</c> 即已审核且未取消、未删除、归属客户档案存在且未删除），
/// <b>绝不强制币种一致</b>（币种只作展示，采购订单保留原始商业币种 / 单价），也绝不为候选臆造明细。</para>
/// <para>只暴露业务表单需要的权威 <c>Id / 单号 / 日期 / 客户 / 币种 / 状态 / 可选性</c>，绝不暴露金额 / 条款 / 余额等无关字段；
/// 候选是只读投影：不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 菜单 / 权限或用户授权，
/// 也绝不接受按猜测 Id 直取（候选选择不等于授权，最终保存仍按采购订单显式链接规则复核精确来源）。</para>
/// </summary>
public class PurchaseOrderSalesOrderSourceCandidateDto
{
    /// <summary>来源销售订单 Id（保存时写入 <c>PurchaseOrder.OwningSalesOrderId</c>，绝不臆造）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>来源销售订单号（权威快照，保存时由服务端原样派生回填）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源销售订单日期</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>权威客户 Id（来源销售订单的客户，绝不臆造）</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称（客户主数据权威名称；客户不存在 / 已删除时为空串）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>来源销售订单币种（既有 <c>Currency</c> 名称；仅供展示，<b>不作可选性门槛</b>，不要求与采购订单一致）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>来源销售订单状态（既有 <c>DocumentStatus</c> 名称：Pending / Submitted / Approved / … / Cancelled）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 该候选是否可被显式选择为归属来源：<b>复用</b> <c>PurchaseSalesOrderLinkRules.IsEligibleNewSource</c>
    /// （已审核、未删除、未取消）且归属客户档案存在且未删除。<b>与币种无关</b>。
    /// </summary>
    public bool Eligible { get; set; }

    /// <summary>不可选原因（<see cref="Eligible"/> 为 true 时为空串；服务端权威文案，界面原样展示）</summary>
    public string IneligibleReason { get; set; } = string.Empty;
}

/// <summary>
/// 采购订单来源销售订单候选分页结果（ERP-393，只读、有界）：<see cref="Total"/> / <see cref="Page"/> /
/// <see cref="PageSize"/> 均在关键字与分页参数<b>归一化之后</b>统计，且客户数据范围先于计数 / 分页下推到数据库。
/// </summary>
public class PurchaseOrderSalesOrderSourceCandidatePageDto
{
    /// <summary>当前页候选行（有界）</summary>
    public List<PurchaseOrderSalesOrderSourceCandidateDto> Items { get; set; } = new();

    /// <summary>归一化筛选（含客户数据范围）后的候选总数（先归一化再计数）</summary>
    public int Total { get; set; }

    /// <summary>归一化后的页码（从 1 开始）</summary>
    public int Page { get; set; }

    /// <summary>归一化后的每页条数（有界）</summary>
    public int PageSize { get; set; }
}

/// <summary>
/// 采购订单已存储来源（归属销售订单）的只读展示（ERP-393，详情 / 重开用）：未关联（历史）→ 显式「未关联」；
/// 已关联且来源有效 → 已关联 + 订单号；来源已取消 / 未审核 → 已关联 + 「只读保留」；
/// 来源已删除 / 无法解析 / <b>不在当前账号客户数据范围之内</b> → 「来源不可用，原链接原样保留」。
/// <para><b>不泄露</b>：当 <see cref="Unavailable"/> 为 true 时，<see cref="OrderNo"/> / <see cref="OrderDate"/> /
/// <see cref="CustomerId"/> / <see cref="CustomerName"/> / <see cref="Currency"/> / <see cref="Status"/>
/// 一律为空，绝不披露范围外（foreign）来源客户 / 订单字段。</para>
/// <para>本 DTO 绝不写库、绝不重绑定 / 清除链接，也绝不因来源失效而抛异常（历史必须可读）；
/// <see cref="EligibleForNewLink"/> 只表示该来源当前是否还能作为<b>新</b>链接目标（候选选择不等于授权）。</para>
/// </summary>
public class PurchaseOrderSalesOrderSourceViewDto
{
    /// <summary>采购订单持久化的归属销售订单 Id（未关联时为 <c>null</c>）</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>来源销售订单号（无法解析 / 不可用时为空串）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源销售订单日期（无法解析 / 不可用时为 <c>null</c>）</summary>
    public DateTime? OrderDate { get; set; }

    /// <summary>权威客户 Id（无法解析 / 不可用时为 <c>null</c>）</summary>
    public long? CustomerId { get; set; }

    /// <summary>权威客户名称（无法解析 / 不可用时为空串）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>来源销售订单币种（无法解析 / 不可用时为空串；仅供展示，不作可用性门槛）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>来源销售订单状态（无法解析 / 不可用时为空串）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>采购订单是否已存储来源链接（持久化 Id 为正整数）</summary>
    public bool Linked { get; set; }

    /// <summary>该来源当前是否仍可作为<b>新</b>链接目标（复用 <c>PurchaseSalesOrderLinkRules.IsEligibleNewSource</c>）</summary>
    public bool EligibleForNewLink { get; set; }

    /// <summary>来源是否不可用（已删除 / 已取消 / 未审核 / 范围外；原链接原样保留，绝不静默清除）</summary>
    public bool Unavailable { get; set; }

    /// <summary>显式状态文案（服务端权威文案，界面原样展示；不可用时不含任何范围外来源字段）</summary>
    public string Annotation { get; set; } = string.Empty;
}
