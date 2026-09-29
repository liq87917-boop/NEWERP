namespace ERP.Application.DTOs;

/// <summary>
/// 客户报告包预览的只读数据传输对象（ERP-122）：把「同一客户的销售订单」与「同一客户的发票 + 显式收款分摊证据」
/// 拆成两个独立有界分区返回，两个分区各自独立计数、原币呈现、剩余证据保留 known / unknown / over_allocated 标签，
/// 绝不推断或拼接发票到销售订单的跨单链接。本 DTO 只描述数据形状，不含任何 SQL、连接串或写入语义。
/// </summary>

/// <summary>
/// 客户报告包预览查询参数（ERP-122，全部为只读筛选）：
/// 必须提供正整数客户 Id；日期区间与分页均有界（每页 1 ~ 100）。
/// 两个分区复用既有作用域化报表查询接口（ERP-112 / ERP-117），各自独立重检菜单授权与业务员数据范围。
/// </summary>
public sealed class CustomerReportPacketRequest
{
    /// <summary>客户 Id（必填、正整数；在当前业务员数据范围内生效）</summary>
    public long CustomerId { get; set; }

    /// <summary>日期起（含当日；同时作用于销售订单订单日期与发票开票日期）</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>日期止（含当日）</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>页码（从 1 开始；小于 1 由服务端归一到第 1 页）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 100，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 客户报告包预览结果（ERP-122，只读、有界）：
/// 销售订单分区与发票 / 收款分摊证据分区各自独立、各自携带独立的列 / 行 / 总数 / 分页信息，
/// 绝不相互推导；金额按原币呈现，剩余证据状态保留 unknown / over_allocated，不轧成假余额。
/// </summary>
public sealed record CustomerReportPacketDto(
    long CustomerId,
    DynamicSalesOrderReportPageDto SalesOrders,
    DynamicReceivableReportPageDto ReceivableEvidence,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
