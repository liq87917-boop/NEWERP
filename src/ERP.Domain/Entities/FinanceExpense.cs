using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 费用单（出口业务杂费台账）
/// 用途：记录报关费、拖车费、THC、文件费、港杂费、仓储费、快递费、查验费等出口环节费用，
///       并标记其归属对象（整柜 / 拼柜 / 散货 / 订单 / 客户）与分摊口径。
/// 设计说明：分摊规则**不写死**——AllocationBase 可选「体积 / 重量 / 金额 / 箱数 / 手工 / 不分摊」，
///           并保留 AllocationRatio（手工比例）与 AllocatedAmount（分摊到本归属对象的金额），
///           以适配"整柜、拼柜、散货都有，分摊按体积或重量不固定"的实际业务。
/// </summary>
public class FinanceExpense : BaseEntity
{
    /// <summary>费用单号（唯一）</summary>
    [Required, MaxLength(50)]
    public string ExpenseNo { get; set; } = string.Empty;

    /// <summary>费用日期</summary>
    public DateTime ExpenseDate { get; set; } = DateTime.Today;

    /// <summary>费用类型（报关费 / 拖车费 / THC / 文件费 / 港杂费 / 仓储费 / 快递费 / 查验费 / 其他）</summary>
    [MaxLength(30)]
    public string ExpenseType { get; set; } = string.Empty;

    /// <summary>金额（原币）</summary>
    public decimal Amount { get; set; }

    /// <summary>币种（CNY / USD 等）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>折合人民币金额</summary>
    public decimal AmountCny { get; set; }

    /// <summary>税率（%）—— 预留，当前业务可不填</summary>
    public decimal TaxRate { get; set; }

    /// <summary>税额 —— 预留</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>收款方（报关行 / 货代 / 车队 / 仓库等）</summary>
    [MaxLength(200)]
    public string Payee { get; set; } = string.Empty;

    /// <summary>归属类型（整柜 / 拼柜 / 散货 / 订单 / 客户 / 不分摊）</summary>
    [MaxLength(30)]
    public string RefType { get; set; } = string.Empty;

    /// <summary>归属单号（柜号 / 订舱号 / 销售订单号等）</summary>
    [MaxLength(50)]
    public string RefNo { get; set; } = string.Empty;

    /// <summary>归属客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>归属客户名称（冗余，便于查询与导出）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>分摊基数（体积 Volume / 重量 Weight / 金额 Amount / 箱数 Carton / 手工 Manual / 不分摊 None）</summary>
    [MaxLength(30)]
    public string AllocationBase { get; set; } = "不分摊";

    /// <summary>手工分摊比例（%）—— 当分摊基数为「手工」时填写</summary>
    public decimal AllocationRatio { get; set; }

    /// <summary>分摊到本归属对象的金额（人民币）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>付款状态（未付 / 部分 / 已付）</summary>
    [MaxLength(20)]
    public string PaymentStatus { get; set; } = "未付";

    /// <summary>付款日期</summary>
    public DateTime? PayDate { get; set; }

    /// <summary>付款方式</summary>
    [MaxLength(30)]
    public string PaymentMethod { get; set; } = string.Empty;

    /// <summary>关联付款单号（可选）</summary>
    [MaxLength(50)]
    public string BillNo { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ ERP-042：分摊批次与来源留痕（可空 = 历史 / 未跟踪行，绝不做回填） ============
    // 口径：
    //   1. 由「分摊批次」生成的费用单行会写入 AllocationBatchNo（批次号留痕，批次号在批次表唯一）
    //      以及 AllocationSourceExpenseId / AllocationSourceExpenseNo（它分摊的是哪条柜级来源费用）；
    //   2. 历史费用单（含旧「拼柜分摊」接口生成的行）这些列为 NULL / 空串：**仍然完全可读**，
    //      读取侧按 AllocationLineage 显式标注为「历史分摊（无批次留痕）」或「未分摊」，
    //      并且在任何正常请求中都不会被自动回填写入；
    //   3. 来源费用单本身不被改写：分摊只新增行，金额 / 归属 / 付款状态保持不变；
    //   4. 刻意**不加导航属性 / 不建到批次表的数据库外键**：费用单可被软删除、可被通用编辑接口改写，
    //      留痕只以快照字符串保存，避免编辑时把批次留痕静默清空。

    /// <summary>分摊批次号快照（空串 = 未参与任何分摊批次；非空表示本行由该批次生成，可回溯批次与来源费用）</summary>
    [MaxLength(50)]
    public string AllocationBatchNo { get; set; } = string.Empty;

    /// <summary>本行分摊的**来源费用单** Id（可空；来源费用是该柜的柜级总额费用）</summary>
    public long? AllocationSourceExpenseId { get; set; }

    /// <summary>来源费用单号快照（可空）</summary>
    [MaxLength(50)]
    public string AllocationSourceExpenseNo { get; set; } = string.Empty;

    /// <summary>
    /// 分摊留痕分类（**非持久化列**，读取时由服务端标注，不落库）：
    /// <c>Batch</c>=批次留痕、<c>Legacy</c>=历史分摊（无批次留痕）、<c>None</c>=未分摊。
    /// 仅用于界面与报表的显式说明，不改变任何金额口径。
    /// </summary>
    [NotMapped]
    public string AllocationLineage { get; set; } = string.Empty;

    /// <summary>分摊留痕文案（**非持久化列**，含批次号与「有效 / 已作废」或历史说明）</summary>
    [NotMapped]
    public string AllocationLineageText { get; set; } = string.Empty;
}
