using ERP.Domain.Enums;

namespace ERP.Application.DTOs;

/// <summary>
/// 采购入库来源候选行（ERP-375，只读、有界）：一条候选 = 「一张已审核、未删除的采购订单 + 一个商品」的收货证据。
/// <para>口径（与 <c>StockInOrderFulfillmentRules</c> / ERP-342 同源）：给出「订单授权基础单位数量 −
/// 已审核入库基础单位数量（以本订单为来源、未删除、已审核）」的<b>剩余可收数量</b>；重复 / 歧义订单明细、
/// 单位无法折算、商品主数据缺失、已收货满额一律标记不可用，绝不猜授权数量。</para>
/// <para>候选 / 详情不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 权限或用户授权，
/// 也绝不用候选选择臆造成本 / 价格（成本仍由服务端 ERP-033 口径兜底）。</para>
/// </summary>
public class StockInSourceCandidateDto
{
    /// <summary>来源采购订单 Id（保存时写入 <c>StockIn.PurchaseOrderId</c>，绝不臆造）</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>来源采购订单号（权威快照，保存时原样回填）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源采购订单日期</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>权威供应商 Id（来源采购订单供应商，保存时回填）</summary>
    public long SupplierId { get; set; }

    /// <summary>权威供应商名称（供应商主数据权威名称）</summary>
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>来源采购订单币种（只读展示，绝不改写入库成本口径）</summary>
    public Currency Currency { get; set; } = Currency.CNY;

    /// <summary>来源采购订单汇率（只读展示，绝不改写入库成本口径）</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（商品主数据权威名称）</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>商品基础单位（数量口径；空 = 单位未知，候选不可用）</summary>
    public string BaseUnit { get; set; } = string.Empty;

    /// <summary>该「来源采购订单 + 商品」组的订单授权基础单位数量（唯一兼容明细行，重复行不可用）</summary>
    public decimal AuthorizedBaseQuantity { get; set; }

    /// <summary>该组已审核入库基础单位数量（以本订单为来源、未删除、已审核）</summary>
    public decimal ReceivedBaseQuantity { get; set; }

    /// <summary>该组剩余可收基础单位数量（= 授权数量 − 已审核入库，下限 0；<see cref="Available"/> 为 false 时恒为 0）</summary>
    public decimal RemainingBaseQuantity { get; set; }

    /// <summary>
    /// 该候选是否可被显式选择：授权数量 &gt; 0、剩余可收数量 &gt; 0、商品 / 单位可折算为基础单位且明细唯一无歧义。
    /// <para>重复明细 / 零容量 / 单位未知 / 商品缺失一律 <c>false</c>，界面不得将其作为可选项。</para>
    /// </summary>
    public bool Available { get; set; }

    /// <summary>不可用原因（<see cref="Available"/> 为 true 时为空串；服务端权威文案，界面原样展示）</summary>
    public string UnavailableReason { get; set; } = string.Empty;
}

/// <summary>
/// 采购入库来源详情（ERP-375，只读、有界）：一张权威来源采购订单的完整收货证据（表头 + 逐商品剩余可收行）。
/// <para>范围外 / 不存在 / 已删除的来源按「不存在」拒绝，未审核 / 已取消 / 已驳回的来源按冲突拒绝：
/// 界面必须重新选择来源，绝不静默改写已保存的来源链接。</para>
/// </summary>
public class StockInSourceDetailDto
{
    /// <summary>来源采购订单 Id</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>来源采购订单号</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源采购订单日期</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>权威供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>权威供应商名称</summary>
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>来源采购订单币种</summary>
    public Currency Currency { get; set; } = Currency.CNY;

    /// <summary>来源采购订单汇率</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    /// <summary>该来源是否至少存在一条「剩余可收数量 &gt; 0」的商品行</summary>
    public bool Available { get; set; }

    /// <summary>整单不可用原因（<see cref="Available"/> 为 true 时为空串）</summary>
    public string UnavailableReason { get; set; } = string.Empty;

    /// <summary>逐商品剩余可收行（含不可用行与原因，可选项仅 <c>Available = true</c>）</summary>
    public List<StockInSourceCandidateDto> Lines { get; set; } = new();
}
