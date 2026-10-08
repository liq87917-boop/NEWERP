using System.ComponentModel.DataAnnotations;

namespace ERP.Application.DTOs;

/// <summary>
/// 预装柜明细可链接的**权威需求计划证据**候选（ERP-368，只读、有界）：
/// 一条候选 = 一条「已审核、未删除」销售订单明细，并显式带出其父销售订单 / 商品 / 客户
/// 与**剩余可链接基础单位数量**，作为预装柜行选择需求来源的完整证据，避免只凭单号猜测归属。
/// </summary>
public class PreLoadingSalesOrderCandidateDto
{
    /// <summary>来源销售订单明细 Id（链接时写入 <c>ContainerPreLoadingDetail.SourceSalesOrderDetailId</c>）</summary>
    public long SalesOrderDetailId { get; set; }

    /// <summary>父销售订单 Id</summary>
    public long SalesOrderId { get; set; }

    /// <summary>父销售订单号（冗余展示）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（商品主数据权威名称）</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>商品基础单位（数量口径；空 = 商品未维护基础单位，链接会被拒绝）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>权威客户 Id（父销售订单客户，必须等于预装柜单权威订柜信息客户）</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称（客户主数据权威名称）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>该销售订单明细的授权数量（基础单位）</summary>
    public decimal OrderBaseQuantity { get; set; }

    /// <summary>该明细已被「其它已审核预装柜单」链接占用的基础单位数量（仅统计显式链接，历史未链接不计入）</summary>
    public decimal LinkedPreLoadingBaseQuantity { get; set; }

    /// <summary>该明细**剩余可链接基础单位数量**（= 订单明细数量 − 已链接占用，下限 0）</summary>
    public decimal RemainingBaseQuantity { get; set; }
}

/// <summary>可链接需求计划证据候选查询（有界；<see cref="Take"/> 会被服务端钳制在合法区间）</summary>
public class PreLoadingSalesOrderCandidateQueryDto
{
    /// <summary>关键字（匹配销售订单号 / 商品名称；为空 = 不过滤）</summary>
    [MaxLength(100)]
    public string? Keyword { get; set; }

    /// <summary>期望返回条数（服务端钳制，避免无界拉取）</summary>
    public int Take { get; set; }
}

/// <summary>单条预装柜明细 → 来源销售订单明细 的链接指派（<see cref="SourceSalesOrderDetailId"/> 为空 = 清除链接）</summary>
public class PreLoadingSalesOrderLinkAssignmentDto
{
    /// <summary>目标预装柜明细 Id（必须属于路径中的预装柜单）</summary>
    public long PreLoadingDetailId { get; set; }

    /// <summary>来源销售订单明细 Id（为空 = 清除该行的显式链接，保持「未链接」语义）</summary>
    public long? SourceSalesOrderDetailId { get; set; }
}

/// <summary>链接指派请求（一次性替换 / 清除指定行的显式需求计划证据链接；缺省行保持不变）</summary>
public class PreLoadingSalesOrderLinkAssignRequest
{
    /// <summary>链接指派行集合（服务端在改写任何一行之前先统一校验全部拟议链接）</summary>
    public List<PreLoadingSalesOrderLinkAssignmentDto> Links { get; set; } = new();
}

/// <summary>链接指派结果（逐行回显最终链接状态，作为显式证据）</summary>
public class PreLoadingSalesOrderLinkAssignResultDto
{
    /// <summary>本次新增 / 变更的链接数</summary>
    public int LinkedCount { get; set; }

    /// <summary>本次清除的链接数</summary>
    public int ClearedCount { get; set; }

    /// <summary>全部预装柜明细的最终链接状态（含历史未链接行，<c>SourceSalesOrderDetailId</c> 为空 = 显式未链接）</summary>
    public List<PreLoadingSalesOrderLinkLineDto> Items { get; set; } = new();
}

/// <summary>预装柜明细的最终链接状态（显式证据回显）</summary>
public class PreLoadingSalesOrderLinkLineDto
{
    /// <summary>预装柜明细 Id</summary>
    public long PreLoadingDetailId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>预装数量（基础单位）</summary>
    public decimal Quantity { get; set; }

    /// <summary>来源销售订单明细 Id（为空 = 显式未链接：历史 / 未登记需求来源，绝不计入已链接需求）</summary>
    public long? SourceSalesOrderDetailId { get; set; }

    /// <summary>来源销售订单 Id（为空 = 未链接或来源已不可解析）</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>来源销售订单号（为空串 = 未链接或来源已不可解析）</summary>
    public string OrderNo { get; set; } = string.Empty;
}
