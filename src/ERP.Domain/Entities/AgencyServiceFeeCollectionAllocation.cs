using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 客户收款单 → **代理服务费对账单** 的收款分摊证据行（ERP-071）。
/// <para>定位：<b>代理服务费收款分摊登记册</b>——它只记录「某张**既有、未删除且未取消**的客户收款单
/// （<see cref="FinanceReceipt"/>）把多少钱**指向**了哪一条**已登记**的代理服务费对账单证据
/// （<see cref="AgencyServiceFeeStatement"/>，ERP-070）」，让「客户付的这笔钱对应哪几笔代理服务费对账」
/// 可以被显式登记与追溯。</para>
/// <para>命名口径（重要）：本类型名用 <b>Collection</b>（收款）而不是 <c>Receipt</c>，是为了保持 ERP-053 已审计锁定的
/// 「仓库只有一套『收款单 → 销售订单』收款引用模型（DbSet 名同时含 Receipt 与 Allocation 的模型唯一）」契约
/// （见 <c>CustomerReceiptAllocationTests.ERP032审计_收款单只有客户级引用_仓库只有一套收款引用模型</c>）：
/// 本模型虽然同样以客户收款单为来源，但引用对象是**代理服务费对账单**而不是销售订单，属于**另一套独立证据维度**；
/// 因此它与 ERP-053 的 <see cref="CustomerReceiptAllocation"/> 是两个**刻意分离**的模型，命名上也绝不重叠。</para>
/// <para>审计结论（ERP-071 的关键前提）：ERP-070 对账单证据**只有客户、只有服务来源与合计，没有任何收款级
/// 持久化引用**；ERP-053 的 <see cref="CustomerReceiptAllocation"/> 是**收款单 → 销售订单**的引用维度，
/// 与「收款单 → 代理服务费对账单」口径不同（引用对象、金额含义与判定资格都不同）。因此本表是**唯一**的
/// 「收款 → 代理服务费对账单」分摊模型：不替换、不复制、不派生 ERP-053，也**不**在收款单或对账单上加列、
/// 不建第二套收款主数据。</para>
/// <para>证据维度分离（重要）：本表的收款分摊与 ERP-053 的销售订单收款引用、ERP-055 的销项发票分摊
/// 是**互相独立的证据维度**，系统<strong>绝不</strong>把三个维度的金额相加、也不把它们当成「几张不同的收款单」；
/// 本表派生的「未分摊金额」只表示<b>本维度</b>内尚未被本表引用的部分。</para>
/// <para>金额口径（服务端权威校验）：</para>
/// <list type="bullet">
/// <item>一行只引用一张收款单与一条对账单，分摊金额按<b>币种精度</b>取整（见 <c>CurrencyAmountRules</c>）后必须大于 0；</item>
/// <item>同一收款单内**有效行**金额合计不得超过收款单金额（<see cref="ReceiptAmount"/> 快照，写入时取自收款单）；</item>
/// <item>同一对账单内**有效行**金额合计不得超过对账单服务端合计（<see cref="StatementTotalAmount"/> 快照）；</item>
/// <item>币种一律<b>原币存储</b>：不做汇率换算、不跨币种合并、不按金额相近猜测对账单。</item>
/// </list>
/// <para>权威资格：收款单必须存在、未删除且未取消（<c>DocumentStatus.Cancelled</c> 拒绝）；
/// 对账单必须存在、未删除且已**登记**（草稿 / 已作废拒绝）；收款单客户与对账单客户必须一致，
/// 收款单币种与对账单币种必须一致。</para>
/// </summary>
public class AgencyServiceFeeCollectionAllocation : BaseEntity
{
    /// <summary>代理服务费对账单证据 Id（引用 <see cref="AgencyServiceFeeStatement"/>；写入时必须是已登记的未删除对账单）</summary>
    public long StatementId { get; set; }

    /// <summary>对账单号快照（服务端权威写入；对账单被作废后历史证据保持登记当时口径）</summary>
    [MaxLength(50)]
    public string StatementNo { get; set; } = string.Empty;

    /// <summary>对账日期快照</summary>
    public DateTime StatementDate { get; set; }

    /// <summary>对账单状态快照（<c>AgencyServiceFeeStatementRules</c> 取值：0 草稿 / 1 已登记 / 2 已作废）</summary>
    public int StatementStatus { get; set; }

    /// <summary>对账单状态文案快照（服务端权威写入）</summary>
    [MaxLength(30)]
    public string StatementStatusText { get; set; } = string.Empty;

    /// <summary>对账单**服务端合计**快照（原币；写入时取自已登记对账单，是有效行分配合计的上限，后续不回写）</summary>
    public decimal StatementTotalAmount { get; set; }

    /// <summary>对账单币种快照（必须与收款单币种一致才能建立分摊，不做汇率换算）</summary>
    [MaxLength(20)]
    public string StatementCurrency { get; set; } = "CNY";

    /// <summary>对账单关联的 ERP-069 协议 Id 快照（只作只读身份留痕，不参与任何金额派生）</summary>
    public long StatementAgreementId { get; set; }

    /// <summary>对账单关联协议号快照（只作只读身份留痕）</summary>
    [MaxLength(50)]
    public string StatementAgreementNo { get; set; } = string.Empty;

    /// <summary>收款单 Id（必须指向存在、未删除且未取消的收款单；本表刻意不建外键）</summary>
    public long ReceiptId { get; set; }

    /// <summary>收款单号快照（服务端权威写入）</summary>
    [MaxLength(50)]
    public string ReceiptNo { get; set; } = string.Empty;

    /// <summary>收款日期快照</summary>
    public DateTime ReceiptDate { get; set; }

    /// <summary>收款单状态快照（<c>DocumentStatus</c> 取值：0 待提交 / 1 已提交 / 2 已审核 / 3 已驳回 / 4 已完成 / 5 已取消）</summary>
    public int ReceiptStatus { get; set; }

    /// <summary>收款单状态文案快照（服务端权威写入；照实回显，不假定为已到账或已审核）</summary>
    [MaxLength(30)]
    public string ReceiptStatusText { get; set; } = string.Empty;

    /// <summary>收款单金额快照（原币；写入时取自收款单并按币种精度取整，是有效行分配合计的上限）</summary>
    public decimal ReceiptAmount { get; set; }

    /// <summary>客户 Id（必须与收款单客户、对账单客户全部一致才能建立分摊）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照（服务端权威写入，不由客户端提交）</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照（客户改名 / 停用 / 删除后历史证据保持登记当时口径）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>分摊金额（原币，与收款单 / 对账单币种一致；按币种精度取整，必须大于 0 且不超两侧可用金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>币种（与收款单 / 对账单币种一致，冗余保存便于按币种有界检索）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>备注（有界；只作为登记说明，不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>状态（1=有效，2=已作废；常量见 <c>AgencyServiceFeeCollectionAllocationRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记时间（服务端权威写入；作废不改写它）</summary>
    public DateTime AllocatedAt { get; set; }

    /// <summary>登记人（服务端按已认证身份写入，缺失记「未知用户」；作废不改写它）</summary>
    [MaxLength(100)]
    public string AllocatedBy { get; set; } = string.Empty;

    /// <summary>作废时间（作废时由服务端写入）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（作废必填、有界；保留原始值与历史）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>收款单当前是否可用（存在、未删除且未取消；**非持久化列**）</summary>
    [NotMapped]
    public bool ReceiptAvailable { get; set; }

    /// <summary>收款单可用性文案（不可用时照实说明，历史证据照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string ReceiptAvailabilityText { get; set; } = string.Empty;

    /// <summary>对账单当前是否可用（存在、未删除且已登记；**非持久化列**）</summary>
    [NotMapped]
    public bool StatementAvailable { get; set; }

    /// <summary>对账单可用性文案（草稿 / 已作废 / 已删除时照实说明，历史证据照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string StatementAvailabilityText { get; set; } = string.Empty;

    /// <summary>币种小数位（**非持久化列**：显式展示原币精度，不做换算）</summary>
    [NotMapped]
    public int AmountDecimals { get; set; }

    /// <summary>分摊金额文案（**非持久化列**：显式展示原币与币种精度，不做换算）</summary>
    [NotMapped]
    public string AmountText { get; set; } = string.Empty;

    /// <summary>状态文案（**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;
}
