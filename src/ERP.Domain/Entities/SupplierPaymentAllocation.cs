using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 供应商付款单 → 采购订单 的**付款引用（分摊）证据行**（ERP-049）。
/// <para>定位：<b>付款引用证据登记册</b>——它只记录「某张**既有、未删除**的供应商付款单（<see cref="FinancePayment"/>）
/// 把多少钱**指向**了哪几张既有、未取消的采购订单」，让「这笔付款到底对应哪几张订单」可被显式登记与追溯。</para>
/// <para>它<strong>不是</strong>银行付款凭证、<strong>不是</strong>应付账款核销、<strong>不是</strong>发票核销、
/// <strong>不是</strong>税务（进项）抵扣判断，也<strong>不是</strong>供应商余额：登记 / 作废一行都不会真的付款、
/// 不会移动资金、不会把发票或订单标记为已结清。</para>
/// <para>金额口径（服务端权威校验）：</para>
/// <list type="bullet">
/// <item>一行只引用一张采购订单，金额按<b>币种精度</b>取整后必须大于 0（口径见 <c>CurrencyAmountRules</c>）；</item>
/// <item>同一张付款单内**有效行**金额合计不得超过付款单金额（<see cref="PaymentAmount"/> 快照，写入时取自付款单）；</item>
/// <item>币种一律<b>原币存储</b>：不做汇率换算、不跨币种合并、不按金额相近猜测订单。</item>
/// </list>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>登记 / 作废分摊行都<strong>不</strong>改写付款单的审批与执行状态、金额、币种、付款方式、银行账户、
/// 供应商或备注，也<strong>不</strong>改写采购订单状态、到货进度、已收数量、金额与明细、结算进度；</item>
/// <item><strong>不</strong>改写发票记录与发票关联、库存与库存成本、库存流水、退税记录、费用与供应商余额；</item>
/// <item>供应商侧只保存**编码 / 名称快照**（服务端写入）：供应商停用 / 删除后历史证据仍可读，绝不回填；</item>
/// <item>采购订单侧只保存**订单快照**（单号 / 日期 / 状态 / 币种），本表<strong>不建</strong>到采购订单与供应商的外键；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 2 已作废 + 作废原因）：保留原始值，
/// 不提供硬删除、不提供静默替换、不重写已作废证据。</item>
/// </list>
/// </summary>
public class SupplierPaymentAllocation : BaseEntity
{
    /// <summary>付款单 Id（必须指向存在且未删除的付款单；本表刻意不建外键，付款单软删除后历史证据仍可读）</summary>
    public long PaymentId { get; set; }

    /// <summary>付款单号快照（服务端权威写入；付款单改名后历史证据保持登记当时口径）</summary>
    [MaxLength(50)]
    public string PaymentNo { get; set; } = string.Empty;

    /// <summary>付款日期快照</summary>
    public DateTime PaymentDate { get; set; }

    /// <summary>付款单状态快照（<c>DocumentStatus</c> 取值：0 待提交 / 1 已提交 / 2 已审核 / 3 已驳回 / 4 已完成 / 5 已取消）</summary>
    public int PaymentStatus { get; set; }

    /// <summary>付款单状态文案快照（服务端权威写入）</summary>
    [MaxLength(30)]
    public string PaymentStatusText { get; set; } = string.Empty;

    /// <summary>付款单金额快照（原币；写入时取自付款单，是**有效行金额合计的上限**，后续不随付款单变动回写）</summary>
    public decimal PaymentAmount { get; set; }

    /// <summary>采购订单 Id（引用 <see cref="PurchaseOrder"/>；刻意不建外键，订单软删除 / 取消后历史证据仍可读）</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>采购单号快照</summary>
    [MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>订单状态快照（<c>DocumentStatus</c> 取值；取消后历史证据仍保留登记当时口径）</summary>
    public int OrderStatus { get; set; }

    /// <summary>订单币种快照（币种名，如 CNY / USD；必须与付款单币种一致才能建立引用）</summary>
    [MaxLength(20)]
    public string OrderCurrency { get; set; } = "CNY";

    /// <summary>供应商 Id 快照（必须与付款单供应商一致才能建立引用）</summary>
    public long SupplierId { get; set; }

    /// <summary>供应商编码快照</summary>
    [MaxLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    /// <summary>供应商名称快照</summary>
    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>引用（分摊）金额（原币，与付款单币种一致；按币种精度取整，必须大于 0 且合计不超过付款单金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>币种（与付款单币种一致，冗余保存便于按币种有界检索）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>备注（有界；只作为登记说明，不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>状态（1=有效，2=已作废；常量见 <c>SupplierPaymentAllocationRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记时间（服务端权威写入）</summary>
    public DateTime AllocatedAt { get; set; }

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填：作废保留原始值，必须记录更正原因而不是静默覆盖或删除）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>付款单当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool PaymentAvailable { get; set; }

    /// <summary>付款单可用性 / 状态文案（不可用时照实说明，历史证据照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string PaymentAvailabilityText { get; set; } = string.Empty;

    /// <summary>采购订单当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool OrderAvailable { get; set; }

    /// <summary>采购订单可用性 / 状态文案（不可用或已取消时照实说明，历史证据仍保持可读；**非持久化列**）</summary>
    [NotMapped]
    public string OrderAvailabilityText { get; set; } = string.Empty;
}
