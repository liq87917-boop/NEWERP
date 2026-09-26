using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 客户收款单 → **客户销项发票证据** 的收款分摊证据行（ERP-073）。
/// <para>定位：<b>客户收款关联销项发票分摊登记册</b>——它只记录「某张**既有、未删除且未取消**的客户收款单
/// （<see cref="FinanceReceipt"/>）把多少钱**指向**了哪一条**已登记**的客户销项发票证据
/// （<see cref="CustomerSalesInvoiceEvidence"/>，ERP-055）」，让「客户付的这笔钱对应哪几张已登记的销项发票」
/// 可以被显式登记与追溯。</para>
/// <para>命名口径：本类型名与 DbSet 名刻意采用 <b>Collection</b>（收款）而不是 <c>Receipt</c>，与 ERP-071 同源：
/// ERP-053 已审计锁定「仓库只有一套『收款单 → 销售订单』收款引用模型（<c>IErpDbContext</c> 中名字同时含
/// <c>Receipt</c> 与 <c>Allocation</c> 的 DbSet 只能有一套——<c>CustomerReceiptAllocations</c>）」的契约。
/// 本模型虽然同样以客户收款单为来源，但引用对象是**销项发票证据**而不是销售订单，属于**另一套独立证据维度**。</para>
/// <para>审计结论：ERP-055 的销项发票证据只有客户、发票身份与含税总额，其既有的
/// <see cref="CustomerSalesInvoiceAllocation"/> 是**发票 → 销售订单**的分摊维度（引用对象与金额含义都不同），
/// 并不回答「这笔收款对应哪几张发票」。因此本表是**唯一**的「收款 → 销项发票」分摊模型。</para>
/// <para>证据维度分离：本表的收款分摊与 ERP-053 / ERP-055 / ERP-071 是**互相独立的证据维度**，
/// 系统<strong>绝不</strong>把四个维度的金额相加、也不把它们当成「几张不同的收款单」。</para>
/// <para>金额口径：一行只引用一张收款单与一条发票证据，分摊金额按<b>币种精度</b>取整后必须大于 0；</para>
/// <list type="bullet">
/// <item>同一收款单内**有效行**金额合计不得超过收款单金额（<see cref="ReceiptAmount"/> 快照）；</item>
/// <item>同一发票证据内**有效行**金额合计不得超过发票含税总额（<see cref="InvoiceGrossAmount"/> 快照）；</item>
/// <item>币种一律<b>原币存储</b>：不做汇率换算、不跨币种合并、不按金额相近猜测发票。</item>
/// </list>
/// <para>权威资格：收款单必须存在、未删除且未取消；发票证据必须存在、未删除且已**登记**（草稿 / 已作废拒绝）；
/// 收款单客户与发票客户必须一致，收款单币种与发票币种必须一致。</para>
/// </summary>
public class CustomerSalesInvoiceCollectionAllocation : BaseEntity
{
    /// <summary>客户销项发票证据 Id（引用 <see cref="CustomerSalesInvoiceEvidence"/>；写入时必须是已登记的未删除发票）</summary>
    public long CustomerSalesInvoiceEvidenceId { get; set; }

    /// <summary>发票类型快照（普票 / 专票 / 出口发票；服务端权威写入）</summary>
    [MaxLength(20)]
    public string InvoiceType { get; set; } = string.Empty;

    /// <summary>发票代码快照（专票必填，普票 / 出口发票可为空；服务端权威写入）</summary>
    [MaxLength(50)]
    public string InvoiceCode { get; set; } = string.Empty;

    /// <summary>发票号码快照（服务端权威写入）</summary>
    [MaxLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>开票日期快照</summary>
    public DateTime InvoiceDate { get; set; }

    /// <summary>发票状态快照（<c>CustomerSalesInvoiceEvidenceRules</c> 取值：0 草稿 / 1 已登记 / 2 已作废）</summary>
    public int InvoiceStatus { get; set; }

    /// <summary>发票状态文案快照（服务端权威写入）</summary>
    [MaxLength(30)]
    public string InvoiceStatusText { get; set; } = string.Empty;

    /// <summary>发票**含税总额**快照（原币；写入时取自已登记发票，是有效行分配合计的上限，后续不回写）</summary>
    public decimal InvoiceGrossAmount { get; set; }

    /// <summary>发票币种快照（必须与收款单币种一致才能建立分摊，不做汇率换算）</summary>
    [MaxLength(20)]
    public string InvoiceCurrency { get; set; } = "CNY";

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

    /// <summary>客户 Id（必须与收款单客户、发票客户全部一致才能建立分摊）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照（服务端权威写入，不由客户端提交）</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照（客户改名 / 停用 / 删除后历史证据保持登记当时口径）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>分摊金额（原币，与收款单 / 发票币种一致；按币种精度取整，必须大于 0 且不超两侧可用金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>币种（与收款单 / 发票币种一致，冗余保存便于按币种有界检索）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>备注（有界；只作为登记说明，不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>状态（1=有效，2=已作废；常量见 <c>CustomerSalesInvoiceCollectionAllocationRules</c>）</summary>
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

    /// <summary>发票证据当前是否可用（存在、未删除且已登记；**非持久化列**）</summary>
    [NotMapped]
    public bool InvoiceAvailable { get; set; }

    /// <summary>发票可用性文案（草稿 / 已作废 / 已删除时照实说明，历史证据照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string InvoiceAvailabilityText { get; set; } = string.Empty;

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
