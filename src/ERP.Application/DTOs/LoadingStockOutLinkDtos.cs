using System.ComponentModel.DataAnnotations;

namespace ERP.Application.DTOs;

/// <summary>
/// 装柜明细可链接的**权威出运证据**候选（ERP-366，只读、有界）：
/// 一条候选 = 一条「已审核、未删除」的销售出库明细，并显式带出其父出库单 / 销售订单 / 商品 / 客户
/// 与**剩余可链接基础单位数量**，作为装柜明细选择出运依据的完整证据，避免只凭单号猜测归属。
/// </summary>
public class LoadingStockOutCandidateDto
{
    /// <summary>来源销售出库明细 Id（链接时写入 <c>ContainerLoadingDetail.SourceStockOutDetailId</c>）</summary>
    public long StockOutDetailId { get; set; }

    /// <summary>父销售出库单 Id</summary>
    public long StockOutId { get; set; }

    /// <summary>父销售出库单号（冗余展示）</summary>
    public string StockOutNo { get; set; } = string.Empty;

    /// <summary>出库日期</summary>
    public DateTime StockOutDate { get; set; }

    /// <summary>来源销售订单 Id（可为空 = 该出库单未关联销售订单）</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>来源销售订单号（可为空串 = 无来源订单，绝不臆造）</summary>
    public string SalesOrderNo { get; set; } = string.Empty;

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（商品主数据权威名称）</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>商品基础单位（数量口径；空 = 商品未维护基础单位，链接会被拒绝）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>权威客户 Id（父出库单客户）</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称（客户主数据权威名称）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>该「出库单 + 商品」组的来源出库基础单位数量（同商品重复行已聚合）</summary>
    public decimal SourceBaseQuantity { get; set; }

    /// <summary>该组已生效（已审核、未删除）销售退货的基础单位数量（退货缺明细 Id，按商品保守聚合）</summary>
    public decimal EffectiveReturnedBaseQuantity { get; set; }

    /// <summary>该组已被「其它已审核装柜清单」链接占用的基础单位数量（仅统计显式链接，历史未链接不计入）</summary>
    public decimal LinkedLoadingBaseQuantity { get; set; }

    /// <summary>
    /// 该组**剩余可链接基础单位数量**（= 来源数量 − 已生效退货 − 已链接占用，下限 0）：
    /// 退货缺明细 Id，因此以「出库单 + 商品」组为保守聚合口径，不猜测退货归属到哪一条出库明细。
    /// </summary>
    public decimal RemainingBaseQuantity { get; set; }
}

/// <summary>可链接出运证据候选查询（有界；<see cref="Take"/> 会被服务端钳制在合法区间）</summary>
public class LoadingStockOutCandidateQueryDto
{
    /// <summary>关键字（匹配出库单号 / 商品名称；为空 = 不过滤）</summary>
    [MaxLength(100)]
    public string? Keyword { get; set; }

    /// <summary>期望返回条数（服务端钳制，避免无界拉取）</summary>
    public int Take { get; set; }
}

/// <summary>单条装柜明细 → 来源出库明细 的链接指派（<see cref="SourceStockOutDetailId"/> 为空 = 清除链接）</summary>
public class LoadingStockOutLinkAssignmentDto
{
    /// <summary>目标装柜明细 Id（必须属于路径中的装柜清单）</summary>
    public long LoadingDetailId { get; set; }

    /// <summary>来源销售出库明细 Id（为空 = 清除该行的显式链接，保持「未链接」语义）</summary>
    public long? SourceStockOutDetailId { get; set; }
}

/// <summary>链接指派请求（一次性替换/清除指定行的显式出运证据链接；缺省行保持不变）</summary>
public class LoadingStockOutLinkAssignRequest
{
    /// <summary>链接指派行集合（服务端在改写任何一行之前先统一校验全部拟议链接）</summary>
    public List<LoadingStockOutLinkAssignmentDto> Links { get; set; } = new();
}

/// <summary>链接指派结果（逐行回显最终链接状态，作为显式证据）</summary>
public class LoadingStockOutLinkAssignResultDto
{
    /// <summary>本次新增 / 变更的链接数</summary>
    public int LinkedCount { get; set; }

    /// <summary>本次清除的链接数</summary>
    public int ClearedCount { get; set; }

    /// <summary>全部装柜明细的最终链接状态（含历史未链接行，<c>SourceStockOutDetailId</c> 为空 = 显式未链接）</summary>
    public List<LoadingStockOutLinkLineDto> Items { get; set; } = new();
}

/// <summary>装柜明细的最终链接状态（显式证据回显）</summary>
public class LoadingStockOutLinkLineDto
{
    /// <summary>装柜明细 Id</summary>
    public long LoadingDetailId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>装柜数量（基础单位）</summary>
    public decimal Quantity { get; set; }

    /// <summary>来源销售出库明细 Id（为空 = 显式未链接：历史 / 未登记证据，绝不计入已证明出运）</summary>
    public long? SourceStockOutDetailId { get; set; }

    /// <summary>来源销售出库单 Id（为空 = 未链接或来源已不可解析）</summary>
    public long? StockOutId { get; set; }

    /// <summary>来源销售出库单号（为空串 = 未链接或来源已不可解析）</summary>
    public string StockOutNo { get; set; } = string.Empty;
}
