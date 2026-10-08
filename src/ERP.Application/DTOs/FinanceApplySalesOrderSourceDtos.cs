namespace ERP.Application.DTOs;

/// <summary>
/// 财务申请单（定金申请单）来源销售订单候选行（ERP-390，只读、有界、分页）：一条候选 = 一张
/// <b>精确客户</b>名下、未删除的销售订单。
/// <para>口径：候选只在既有「定金申请单」（<c>deposit-apply</c>）菜单 + 既有「销售订单」（<c>sales-order</c>）菜单 +
/// 实时客户数据范围之内、按<b>精确客户 Id</b>返回；已取消订单显式标记 <see cref="Eligible"/> = <c>false</c> +
/// 原因，币种与申请单不一致时同样显式标记不可选（历史已记录的链接只读保留，绝不静默重绑定）。</para>
/// <para>只暴露业务表单需要的权威 <c>Id / 单号 / 日期 / 客户 / 币种 / 状态</c>，绝不暴露金额 / 条款 / 余额等无关字段；
/// 候选是只读投影：不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 菜单 / 权限或用户授权，
/// 也绝不接受按猜测 Id 直取任意客户订单（候选选择不等于授权，最终保存仍按定金申请单生命周期规则复核）。</para>
/// </summary>
public class FinanceApplySalesOrderCandidateDto
{
    /// <summary>来源销售订单 Id（保存时写入 <c>FinanceDepositApply.SalesOrderId</c>，绝不臆造）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>来源销售订单号（权威快照，保存时原样回填）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源销售订单日期</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>权威客户 Id（= 请求的精确客户 Id，绝不返回其它客户订单）</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称（客户主数据权威名称）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>来源销售订单币种（既有 <c>Currency</c> 名称，已归一化；用于与申请单币种精确比对，不做汇率换算）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>来源销售订单状态（既有 <c>DocumentStatus</c> 名称：Pending / Submitted / Approved / … / Cancelled）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>该候选是否可被显式选择为<b>新</b>来源：未删除、未取消且币种与申请单归一化币种一致。</summary>
    public bool Eligible { get; set; }

    /// <summary>不可选原因（<see cref="Eligible"/> 为 true 时为空串；服务端权威文案，界面原样展示）</summary>
    public string IneligibleReason { get; set; } = string.Empty;
}

/// <summary>
/// 财务申请单来源销售订单候选分页结果（ERP-390，只读、有界）：<see cref="Total"/> / <see cref="Page"/> /
/// <see cref="PageSize"/> 均在关键字与分页参数<b>归一化之后</b>统计，绝不无界拉取。
/// </summary>
public class FinanceApplySalesOrderCandidatePageDto
{
    /// <summary>当前页候选行（有界）</summary>
    public List<FinanceApplySalesOrderCandidateDto> Items { get; set; } = new();

    /// <summary>归一化筛选后的候选总数（先归一化再计数）</summary>
    public int Total { get; set; }

    /// <summary>归一化后的页码（从 1 开始）</summary>
    public int Page { get; set; }

    /// <summary>归一化后的每页条数（有界）</summary>
    public int PageSize { get; set; }
}

/// <summary>
/// 财务申请单已存储来源的只读展示（ERP-390，详情 / 重开用）：未关联（历史）→ 显式「未关联」；
/// 已关联且来源有效 → 已关联 + 订单号；来源已取消 → 已关联 + 「来源已取消，只读保留」；来源已删除 / 无法解析 →
/// 「来源不可用，原链接原样保留」。
/// <para>本 DTO 绝不写库、绝不重绑定 / 清除链接，也绝不因来源失效而抛异常（历史必须可读）；
/// <see cref="EligibleForNewLink"/> 只表示该来源当前是否还能作为<b>新</b>链接目标（候选选择不等于授权）。</para>
/// </summary>
public class FinanceApplySalesOrderSourceViewDto
{
    /// <summary>申请单持久化的来源销售订单 Id（未关联时为 <c>null</c>）</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>来源销售订单号（无法解析时为空串）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源销售订单日期（无法解析时为 <c>null</c>）</summary>
    public DateTime? OrderDate { get; set; }

    /// <summary>来源销售订单币种（已归一化；无法解析时为空串）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>来源销售订单状态（无法解析时为空串）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>申请单是否已存储来源链接（持久化 Id 为正整数）</summary>
    public bool Linked { get; set; }

    /// <summary>该来源当前是否仍可作为<b>新</b>链接目标（未删除、未取消且币种兼容）</summary>
    public bool EligibleForNewLink { get; set; }

    /// <summary>来源是否不可用（已删除 / 无法解析；原链接原样保留，绝不静默清除）</summary>
    public bool Unavailable { get; set; }

    /// <summary>显式状态文案（与定金申请单生命周期规则 <c>DescribeStoredSourceAsync</c> 同源，界面原样展示）</summary>
    public string Annotation { get; set; } = string.Empty;
}
