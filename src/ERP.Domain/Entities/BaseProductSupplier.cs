using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 商品 / SKU 货源关系（ERP-038）：把一个既有商品（或它的一个启用规格）与多个既有供应商关联起来。
/// <para>定位：**货源指引性的主数据关系**——它只描述「这个商品 / 规格可以从哪些供应商采购、
/// 供应商货号 / 采购单位 / 最小起订量 / 交期是多少、哪一家是首选」，用于人工比价与下单前的参考。</para>
/// <para>边界（重要）：本表<strong>不参与任何自动决策</strong>——
/// <list type="bullet">
/// <item>不会自动为采购报价（<c>PurchaseQuotes</c>）选供应商或选价；</item>
/// <item>不会自动改写采购订单（<c>PurchaseOrders</c> / <c>PurchaseOrderDetails</c>）的供应商、单价或金额；</item>
/// <item>不会改写库存 <c>Stocks</c> 成本与库存流水 <c>StockMovements</c>，也不改任何历史单据；</item>
/// <item>不建外键：商品 / 规格 / 供应商软删除或停用后，历史货源关系必须继续可读。</item>
/// </list>
/// </para>
/// <para>首选口径：同一「商品 + 可选规格」范围内最多一条**启用中的**首选关系
/// （由过滤唯一索引 <c>UX_BaseProductSuppliers_ScopePreferred</c> 在数据库层兜底），
/// 且切换首选只能通过显式的「设为首选」操作完成，不依赖列表顺序。</para>
/// </summary>
public class BaseProductSupplier : BaseEntity
{
    /// <summary>归属商品 Id（引用 <see cref="BaseProduct"/>；刻意不建外键，避免商品软删除时连带影响历史货源）</summary>
    public long ProductId { get; set; }

    /// <summary>
    /// 可选的归属规格 Id（引用 <see cref="BaseProductVariant"/>）：
    /// <c>null</c> = 商品级货源关系（整品通用）；有值 = 仅该启用规格可用的 SKU 级货源关系。
    /// </summary>
    public long? VariantId { get; set; }

    /// <summary>
    /// 「商品级 / 规格级」作用域键（服务端写入，客户端提交值一律不被采信）：
    /// 商品级固定为 <c>P</c>；规格级为 <c>V{规格Id}</c>。
    /// <para>用途：SQL Server 的唯一索引对 <c>NULL</c> 不去重，因此用本列把「商品级」与「规格级」
    /// 收敛成可唯一约束的键，使「同一范围 + 同一供应商不重复」「同一范围只有一个启用首选」可以在数据库层判定。</para>
    /// </summary>
    [Required, MaxLength(30)]
    public string ScopeKey { get; set; } = string.Empty;

    /// <summary>关联的供应商 Id（引用 <see cref="BaseSupplier"/>；同样刻意不建外键）</summary>
    public long SupplierId { get; set; }

    /// <summary>供应商货号 / 款号（可选；服务端去首尾空白并压缩连续空白后落库，保留原大小写）</summary>
    [MaxLength(100)]
    public string SupplierItemCode { get; set; } = string.Empty;

    /// <summary>采购单位（可选，如 箱 / 打 / 个；仅作货源指引，不改写商品自身的计量单位）</summary>
    [MaxLength(20)]
    public string PurchaseUnit { get; set; } = string.Empty;

    /// <summary>最小起订量 MOQ（0 = 未指定；仅作货源指引，不参与采购订单校验）</summary>
    public decimal MinOrderQty { get; set; }

    /// <summary>交期天数（0 = 未指定；仅作货源指引）</summary>
    public int LeadTimeDays { get; set; }

    /// <summary>
    /// 是否为该「商品 + 可选规格」范围内的首选货源（同一范围内最多一条**启用中**的首选关系）。
    /// 停用 / 删除时该标记会被释放（置 0），重新启用后需再次显式设为「首选」。
    /// </summary>
    public bool IsPreferred { get; set; }

    /// <summary>状态（1=启用，可作为启用货源被参考；0=停用，历史可读但不再作为启用货源）</summary>
    public int Status { get; set; } = 1;

    /// <summary>排序号（同范围内展示顺序；首选判定与列表顺序无关）</summary>
    public int SortOrder { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
