namespace ERP.Application.DTOs;

/// <summary>
/// 销售退货来源候选行（ERP-374，只读、有界）：一条候选 = 「一张已审核、未删除的销售出库单 + 一个商品」的聚合证据。
/// <para>口径：同商品重复出库行按<b>基础单位</b>合计（绝不重复相乘），并给出「来源数量 − 已生效（已审核、未删除）退货数量」
/// 的<b>净可退容量</b>；候选 / 详情不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 权限。</para>
/// </summary>
public class SalesReturnSourceCandidateDto
{
    /// <summary>来源销售出库单 Id（保存时写入 <c>SalesReturn.SourceStockOutId</c>，绝不臆造）</summary>
    public long SourceStockOutId { get; set; }

    /// <summary>来源销售出库单号（权威快照，保存时原样回填）</summary>
    public string SourceStockOutNo { get; set; } = string.Empty;

    /// <summary>来源出库日期</summary>
    public DateTime SourceStockOutDate { get; set; }

    /// <summary>权威客户 Id（来源出库单客户，保存时回填）</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称（客户主数据权威名称）</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>权威仓库 Id（来源出库仓库 = 退货入库仓库，保存时回填）</summary>
    public long WarehouseId { get; set; }

    /// <summary>权威仓库名称</summary>
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（商品主数据权威名称）</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>商品基础单位（数量口径；空 = 来源单位未知，候选不可用）</summary>
    public string BaseUnit { get; set; } = string.Empty;

    /// <summary>该「来源出库单 + 商品」组的来源出库基础单位数量（同商品重复行已聚合）</summary>
    public decimal SourceBaseQuantity { get; set; }

    /// <summary>该组已生效（已审核、未删除）退货的基础单位数量（退货无来源明细 Id，按商品保守聚合）</summary>
    public decimal EffectiveReturnedBaseQuantity { get; set; }

    /// <summary>该组净可退基础单位数量（= 来源数量 − 已生效退货，下限 0；<see cref="Available"/> 为 false 时恒为 0）</summary>
    public decimal RemainingBaseQuantity { get; set; }

    /// <summary>
    /// 该候选是否可被显式选择：来源数量 &gt; 0、净可退容量 &gt; 0、来源单位可折算为基础单位且证据未损坏。
    /// <para>零容量 / 负数量 / 单位未知一律 <c>false</c>，界面不得将其作为可选项。</para>
    /// </summary>
    public bool Available { get; set; }

    /// <summary>不可用原因（<see cref="Available"/> 为 true 时为空串；服务端权威文案，界面原样展示）</summary>
    public string UnavailableReason { get; set; } = string.Empty;
}

/// <summary>
/// 销售退货来源详情（ERP-374，只读、有界）：一张权威来源销售出库单的完整可退证据（表头 + 逐商品可退容量行）。
/// <para>范围外 / 不存在 / 已删除的来源按「不存在」拒绝，未审核 / 已取消 / 已驳回的来源按冲突拒绝：
/// 界面必须重新选择来源，绝不静默改写已保存的来源链接。</para>
/// </summary>
public class SalesReturnSourceDetailDto
{
    /// <summary>来源销售出库单 Id</summary>
    public long SourceStockOutId { get; set; }

    /// <summary>来源销售出库单号</summary>
    public string SourceStockOutNo { get; set; } = string.Empty;

    /// <summary>来源出库日期</summary>
    public DateTime SourceStockOutDate { get; set; }

    /// <summary>权威客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>权威客户名称</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>权威仓库 Id</summary>
    public long WarehouseId { get; set; }

    /// <summary>权威仓库名称</summary>
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>该来源是否至少存在一条「可退容量 &gt; 0」的商品行</summary>
    public bool Available { get; set; }

    /// <summary>整单不可用原因（<see cref="Available"/> 为 true 时为空串）</summary>
    public string UnavailableReason { get; set; } = string.Empty;

    /// <summary>逐商品可退容量行（含不可用行与原因，可选项仅 <c>Available = true</c>）</summary>
    public List<SalesReturnSourceCandidateDto> Lines { get; set; } = new();
}
