using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 装柜费用分摊批次（ERP-042）：把一个**既有柜级费用单**（来源费用 <see cref="SourceExpenseId"/>）按
/// 装柜清单（一柜）**启用中的多客户参与方**（ERP-041）分摊一次，并为这次分摊留下一份可追溯的批次留痕。
/// <para>定位：<b>既有 <see cref="FinanceExpense"/> 费用单与既有分摊口径之上的「留痕层」</b>——
/// 分摊结果仍然是 <see cref="FinanceExpense"/> 行（一参与方一行，沿用原有金额 / 币种 / 汇率 / 归属 /
/// 分摊基数 / 比例 / 分摊金额字段），本表只额外记录「这次分摊是谁按什么口径生成的」，
/// <strong>不引入第二套账务引擎</strong>（不记账、不生成凭证、不生成收款 / 付款 / 结算单）。</para>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>不改写来源费用单（金额 / 归属 / 付款状态一律不变）——来源费用始终是「柜级总额」，批次只是它的分摊结果；</item>
/// <item>不改写装柜清单与装柜明细（数量 / 箱数 / 重量 / 体积）、参与方身份、订柜外贸与物流跟踪值、
/// 单证 <c>TradeDocuments</c>、库存 <c>Stocks</c> / 库存流水 <c>StockMovements</c>、采购订单、销售订单；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 0 已作废 + 作废原因），不物理删除、不静默改写已生成行；</item>
/// <item>同一「来源费用 + 装柜清单」最多只有一条**有效**批次（业务口径**不区分分摊方法**，
/// 避免同一笔柜级费用被重复分摊）；数据库过滤唯一索引 <c>UX_FinanceExpenseAllocationBatches_SourceLive</c>
/// （含方法）作为并发兜底；作废后可重新生成。</item>
/// </list>
/// </summary>
public class FinanceExpenseAllocationBatch : BaseEntity
{
    /// <summary>批次号（唯一，形如 EAB-yyyyMMdd-001）</summary>
    [Required, MaxLength(50)]
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>来源费用单 Id（引用 <see cref="FinanceExpense"/>；本批次分摊的就是它的柜级总额）</summary>
    public long SourceExpenseId { get; set; }

    /// <summary>来源费用单号快照（生成时服务端写入，便于批次台账直接可读）</summary>
    [MaxLength(50)]
    public string SourceExpenseNo { get; set; } = string.Empty;

    /// <summary>装柜清单 Id（引用 <see cref="ContainerLoadingList"/>；分摊范围是它启用中的参与方）</summary>
    public long LoadingListId { get; set; }

    /// <summary>装柜清单号快照</summary>
    [MaxLength(50)]
    public string LoadingListNo { get; set; } = string.Empty;

    /// <summary>柜号快照（来源费用的归属单号必须与它一致才可分摊）</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>分摊方法（按体积 / 按重量 / 按箱数 / 按金额 / 整柜）</summary>
    [MaxLength(30)]
    public string AllocationMethod { get; set; } = string.Empty;

    /// <summary>基数种类（体积 / 重量 / 箱数 / 金额 / 整柜；整柜法的持久化基数种类在此留痕）</summary>
    [MaxLength(30)]
    public string BasisKind { get; set; } = string.Empty;

    /// <summary>币种（沿用来源费用单币种，不换算、不合并币种）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>汇率（沿用来源费用单汇率，仅用于折人民币展示）</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>来源费用的柜级总额（原币，取自来源费用单；分摊行合计必须等于它）</summary>
    public decimal SourceAmount { get; set; }

    /// <summary>分摊行合计（原币，恒等于 <see cref="SourceAmount"/>，便于读侧校验而不必回表求和）</summary>
    public decimal AllocatedTotal { get; set; }

    /// <summary>分摊行条数</summary>
    public int LineCount { get; set; }

    /// <summary>批次状态（1=有效，0=已作废；作废保留历史，见 <c>FinanceExpenseAllocationRules.BatchActive</c>）</summary>
    public int Status { get; set; } = 1;

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填，用于解释更正原因而不是静默覆盖）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>状态文案（**非持久化列**，读取时由服务端标注：有效 / 已作废）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>分摊明细行（批次 → 参与方，一参与方一行）</summary>
    public List<FinanceExpenseAllocationLine> Lines { get; set; } = new();
}


/// <summary>
/// 装柜费用分摊行（ERP-042）：批次下**每个参与方一行**的完整留痕 ——
/// 来源费用、装柜清单 / 柜号、参与方客户、分摊方法、基数种类、基数来源、基数值、比例、分摊金额，
/// 以及由本行生成的那条 <see cref="FinanceExpense"/> 费用单（可空：只留痕不落单的历史场景）。
/// <para>基线口径：分摊基数要么来自**持久化装柜证据**（整柜法用装柜清单持久化的总箱数 / 总毛重 / 总体积，
/// 缺失或为 0 一律拒绝），要么来自**用户确认的请求值**（服务端校验非负且合计大于 0，缺失即拒绝）——
/// 任何情况下都<strong>不按经验推断</strong>缺失基数。</para>
/// </summary>
public class FinanceExpenseAllocationLine : BaseEntity
{
    /// <summary>归属批次 Id（引用 <see cref="FinanceExpenseAllocationBatch"/>）</summary>
    public long BatchId { get; set; }

    /// <summary>批次号快照</summary>
    [MaxLength(50)]
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>来源费用单 Id（与批次一致，便于按来源费用有界检索）</summary>
    public long SourceExpenseId { get; set; }

    /// <summary>来源费用单号快照</summary>
    [MaxLength(50)]
    public string SourceExpenseNo { get; set; } = string.Empty;

    /// <summary>装柜清单 Id</summary>
    public long LoadingListId { get; set; }

    /// <summary>装柜清单号快照</summary>
    [MaxLength(50)]
    public string LoadingListNo { get; set; } = string.Empty;

    /// <summary>柜号快照</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>参与方 Id（引用 <see cref="ContainerLoadingListParticipant"/>；刻意不建外键，参与方停用 / 删除后历史留痕仍可读）</summary>
    public long ParticipantId { get; set; }

    /// <summary>参与方客户 Id（引用 <see cref="BaseCustomer"/>；不建外键，客户停用 / 删除后留痕仍可读）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照（生成时取自参与方快照，不由客户端提交）</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照（生成时取自参与方快照）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>生成时该参与方是否为该柜的主参与方（留痕，便于复盘；主参与方不参与比例计算）</summary>
    public bool ParticipantPrimary { get; set; }

    /// <summary>分摊方法（按体积 / 按重量 / 按箱数 / 按金额 / 整柜）</summary>
    [MaxLength(30)]
    public string AllocationMethod { get; set; } = string.Empty;

    /// <summary>
    /// 基数种类：逐行基准法与方法同名（按体积 / 按重量 / 按箱数 / 按金额）；
    /// 整柜法为请求显式指定的**持久化装柜证据**种类（箱数 / 毛重 / 体积）。
    /// </summary>
    [MaxLength(30)]
    public string BasisKind { get; set; } = string.Empty;

    /// <summary>基数来源（持久化装柜证据 / 用户确认请求值）</summary>
    [MaxLength(30)]
    public string BasisSource { get; set; } = string.Empty;

    /// <summary>该参与方本行采用的基数值（服务端校验后的权威值）</summary>
    public decimal BasisValue { get; set; }

    /// <summary>分摊比例（%，最多 4 位小数；合计 100%，余差归基准值最大的参与方）</summary>
    public decimal Ratio { get; set; }

    /// <summary>分摊金额（原币，按币种精度取整；行合计恒等于批次来源金额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>分摊金额折人民币（按来源费用汇率换算，2 位小数）</summary>
    public decimal AllocatedAmountCny { get; set; }

    /// <summary>币种（沿用来源费用单币种）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>由本行生成的费用单 Id（可空：只留痕不落单的场景；由 EF 关系回填）</summary>
    public long? ExpenseId { get; set; }

    /// <summary>
    /// 由本行生成的费用单（导航属性：**仅用于写入时由 EF 回填 <see cref="ExpenseId"/>**，
    /// 不参与任何读取口径；留痕对外展示一律使用 <see cref="ExpenseNo"/> 快照）。
    /// </summary>
    public FinanceExpense? Expense { get; set; }

    /// <summary>由本行生成的费用单号快照</summary>
    [MaxLength(50)]
    public string ExpenseNo { get; set; } = string.Empty;

    /// <summary>排序号（仅影响展示顺序，与余差归属判定无关）</summary>
    public int SortOrder { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
