using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 客户收款单 → 销售订单 的**收款引用（分摊）证据行**（ERP-053）。
/// <para>定位：<b>收款引用证据登记册</b>——它只记录「某张**既有、未删除**的客户收款单（<see cref="FinanceReceipt"/>）
/// 把多少钱**指向**了哪几张既有、未取消的销售订单」，让「这笔收款到底对应哪几张订单」可被显式登记与追溯。</para>
/// <para>为什么必须新建本表（ERP-053 审计结论）：ERP-032 / ERP-046 的权威口径里，收款单
/// （<c>FinanceReceipt</c>，引用字段 <c>SalesOrderProgress.ReferenceReceipt = FinanceReceipt.CustomerId</c>）
/// **只记录客户、没有任何订单级持久化引用**，因此历史收款证据在订单侧只能作为「未关联证据」列出，
/// 系统绝不按客户名、订单号文本、日期或金额相似度猜测链接。仓库里**不存在**可复用的收款单 → 订单权威关系，
/// 因此本表是**唯一**的收款引用（分摊）登记模型：不在收款单或销售订单上新增任何列，也不建立第二套链接表。</para>
/// <para>它<strong>不是</strong>银行入账 / 到账凭证、<strong>不是</strong>应收账款台账或余额、<strong>不是</strong>货款核销、
/// <strong>不是</strong>客户对账单（statement of account）、<strong>不是</strong>税务（销项）判断，也<strong>不构成</strong>任何法律上的债务清偿；
/// 登记 / 作废一行都<strong>不会</strong>真的收款、不会移动资金、不会把订单标记为已收款或已结清。</para>
/// <para>金额口径（服务端权威校验）：</para>
/// <list type="bullet">
/// <item>一行只引用一张销售订单，金额按<b>币种精度</b>取整后必须大于 0（口径见 <c>CurrencyAmountRules</c>）；</item>
/// <item>同一张收款单内**有效行**金额合计不得超过收款单金额（<see cref="ReceiptAmount"/> 快照，写入时取自收款单）；</item>
/// <item>币种一律<b>原币存储</b>：不做汇率换算、不跨币种合并、不按金额相近猜测订单。</item>
/// </list>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>登记 / 作废引用行都<strong>不</strong>改写收款单的审批与执行状态、金额、币种、付款方式、银行账户、客户或备注，
/// 也<strong>不</strong>改写销售订单状态、出货进度、金额与明细、交期与合同字段；</item>
/// <item><strong>不</strong>改写客户信用状态与信用额度、发票记录、库存与库存成本、库存流水、装柜与单证记录、
/// 佣金 / 回佣、费用与退税记录；</item>
/// <item>客户侧只保存**编码 / 名称快照**（服务端写入）：客户停用 / 删除后历史证据仍可读，绝不回填；</item>
/// <item>销售订单侧只保存**订单快照**（单号 / 日期 / 状态 / 币种），本表<strong>不建</strong>到销售订单与客户的外键；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 2 已作废 + 作废原因）：保留原始值，
/// 不提供硬删除、不提供静默替换、不重写已作废证据。</item>
/// </list>
/// </summary>
public class CustomerReceiptAllocation : BaseEntity
{
    /// <summary>收款单 Id（必须指向存在且未删除的收款单；本表刻意不建外键，收款单软删除后历史证据仍可读）</summary>
    public long ReceiptId { get; set; }

    /// <summary>收款单号快照（服务端权威写入；收款单改名后历史证据保持登记当时口径）</summary>
    [MaxLength(50)]
    public string ReceiptNo { get; set; } = string.Empty;

    /// <summary>收款日期快照</summary>
    public DateTime ReceiptDate { get; set; }

    /// <summary>收款单状态快照（<c>DocumentStatus</c> 取值：0 待提交 / 1 已提交 / 2 已审核 / 3 已驳回 / 4 已完成 / 5 已取消）</summary>
    public int ReceiptStatus { get; set; }

    /// <summary>收款单状态文案快照（服务端权威写入）</summary>
    [MaxLength(30)]
    public string ReceiptStatusText { get; set; } = string.Empty;

    /// <summary>收款单金额快照（原币；写入时取自收款单，是**有效行金额合计的上限**，后续不随收款单变动回写）</summary>
    public decimal ReceiptAmount { get; set; }

    /// <summary>销售订单 Id（引用 <see cref="SalesOrder"/>；刻意不建外键，订单软删除 / 取消后历史证据仍可读）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>销售订单号快照</summary>
    [MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>订单状态快照（<c>DocumentStatus</c> 取值；取消后历史证据仍保留登记当时口径）</summary>
    public int OrderStatus { get; set; }

    /// <summary>订单币种快照（币种名，如 CNY / USD；必须与收款单币种一致才能建立引用）</summary>
    [MaxLength(20)]
    public string OrderCurrency { get; set; } = "CNY";

    /// <summary>客户 Id 快照（必须与收款单客户一致才能建立引用）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>引用（分摊）金额（原币，与收款单币种一致；按币种精度取整，必须大于 0 且合计不超过收款单金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>币种（与收款单币种一致，冗余保存便于按币种有界检索）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>备注（有界；只作为登记说明，不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>状态（1=有效，2=已作废；常量见 <c>CustomerReceiptAllocationRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记时间（服务端权威写入）</summary>
    public DateTime AllocatedAt { get; set; }

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填：作废保留原始值，必须记录更正原因而不是静默覆盖或删除）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>收款单当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool ReceiptAvailable { get; set; }

    /// <summary>收款单可用性 / 状态文案（不可用时照实说明，历史证据照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string ReceiptAvailabilityText { get; set; } = string.Empty;

    /// <summary>销售订单当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool OrderAvailable { get; set; }

    /// <summary>销售订单可用性 / 状态文案（不可用或已取消时照实说明，历史证据仍保持可读；**非持久化列**）</summary>
    [NotMapped]
    public string OrderAvailabilityText { get; set; } = string.Empty;
}
