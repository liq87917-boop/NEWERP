using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 客户销项发票证据登记（ERP-055）：登记**普通发票 / 增值税专用发票 / 出口发票**的运营证据，
/// 并可（可选、显式）把含税总额按权威口径分摊到既有销售订单。
/// <para>定位：<b>操作性的销项发票证据台账</b>——它<strong>不是</strong>发票开具系统（不连税务局、不调用任何开票服务）、
/// <strong>不是</strong>税务申报 / 销项税金计算、<strong>不是</strong>应收账款台账或余额、<strong>不是</strong>收款核销，
/// 也不引入第二套账务引擎（不记账、不生成凭证 / 收款 / 付款 / 结算单）。</para>
/// <para>与单证中心商业发票（<see cref="TradeDocument"/> 中 <c>DocType = 商业发票</c>，权威常量见
/// <c>TradeDocumentItemRules.CommercialInvoiceDocType</c>）的关系：两者<strong>刻意分离</strong>——
/// 单证中心的商业发票是<b>出口报关用的单证快照</b>，本表登记的是<b>账务 / 税务口径的发票证据</b>；
/// 本登记册<strong>不</strong>转换、<strong>不</strong>替换、<strong>不</strong>自动链接任何单证，只接受<b>显式</b>的有界交叉引用
/// （<see cref="TradeDocumentId"/> + 服务端快照 + <see cref="CommercialInvoiceReference"/> 文本），
/// 且交叉引用只作为证据存在：不参与金额、不推断开票、不改写单证。</para>
/// <para>金额口径（服务端权威校验）：<c>含税总额（价税合计）= 不含税金额（净额）+ 税额</c>，
/// 三者均按<b>币种精度</b>四舍五入后必须严格相等；金额一律以原币存储，不做汇率换算、不跨币种合并。</para>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>登记 / 记录 / 作废发票都<strong>不</strong>改写销售订单状态、出货进度、金额与明细、交期与合同字段；</item>
/// <item><strong>不</strong>改写客户信用状态与信用额度、客户收款单及其引用行、库存与库存成本、库存流水、
/// 装柜与单证记录、佣金 / 回佣、费用与退税记录，也<strong>不</strong>写入任何财务 / 税务记录；</item>
/// <item>记录 / 作废<strong>不</strong>开具或作废真实发票、<strong>不</strong>调用任何开票 / 税务平台接口；</item>
/// <item>客户侧只保存**编码 / 名称快照**（服务端写入）：客户停用 / 删除后历史证据仍可读，绝不回填；</item>
/// <item>分摊行只保存**销售订单快照**（单号 / 日期 / 状态 / 币种），本表<strong>不建</strong>到销售订单与客户的外键；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 2 已作废 + 作废原因）：保留原始值，不提供硬删除、不提供静默替换。</item>
/// </list>
/// </summary>
public class CustomerSalesInvoiceEvidence : BaseEntity
{
    /// <summary>发票类型（普票 / 专票 / 出口发票；取值见 <c>CustomerSalesInvoiceEvidenceRules</c>）</summary>
    [Required, MaxLength(20)]
    public string InvoiceType { get; set; } = "普票";

    /// <summary>发票代码（专票必填，普票 / 出口发票可选；客户端原样提交，服务端只做去空白与长度校验）</summary>
    [MaxLength(50)]
    public string InvoiceCode { get; set; } = string.Empty;

    /// <summary>发票号码（必填；发票上的号码，不是本系统生成的编号）</summary>
    [Required, MaxLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>规范化发票代码（唯一身份判定用：去空白、去连字符后大写；不做任何模糊匹配）</summary>
    [MaxLength(50)]
    public string NormalizedInvoiceCode { get; set; } = string.Empty;

    /// <summary>规范化发票号码（唯一身份判定用：去空白、去连字符后大写）</summary>
    [MaxLength(50)]
    public string NormalizedInvoiceNumber { get; set; } = string.Empty;

    /// <summary>开票日期</summary>
    public DateTime InvoiceDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id（新增 / 修改时必须是存在、未删除且启用的客户）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照（服务端权威写入，不由客户端提交）</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照（服务端权威写入；客户改名 / 停用后历史证据保持登记当时口径）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>币种（系统支持的币种代码，如 CNY / USD / JPY；分摊销售订单时要求币种一致）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>不含税金额（净额，原币；按币种精度取整后参与金额等式校验）</summary>
    public decimal NetAmount { get; set; }

    /// <summary>税额（原币；按币种精度取整后参与金额等式校验；允许为 0 = 不征税 / 出口免税票）</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>含税总额（价税合计，原币；必须大于 0 且等于净额 + 税额）</summary>
    public decimal GrossAmount { get; set; }

    // ============ 与单证中心商业发票的**显式、有界**交叉引用（仅证据，绝不自动链接） ============

    /// <summary>
    /// 显式引用的单证中心单证 Id（可选；提供时服务端校验该单证存在、未删除且类型为商业发票，
    /// 并写入单证号 / 类型快照）。
    /// <para>该引用<strong>只作为证据</strong>：不读取单证金额、不参与金额计算、不把单证转换成本登记册记录，
    /// 也不改写单证；本表<strong>不建</strong>到单证表的外键。</para>
    /// </summary>
    public long? TradeDocumentId { get; set; }

    /// <summary>单证号快照（服务端按被引用单证权威写入；单证改名 / 删除后历史证据保持登记当时口径）</summary>
    [MaxLength(50)]
    public string TradeDocumentNo { get; set; } = string.Empty;

    /// <summary>单证类型快照（服务端写入；显式引用时固定为商业发票口径的文本）</summary>
    [MaxLength(30)]
    public string TradeDocumentDocType { get; set; } = string.Empty;

    /// <summary>
    /// 显式填写的商业发票号 / 单证引用说明（可选、有界文本）。
    /// <para>它只是**人工留痕**：系统绝不据此匹配、转换或链接任何单证与订单，也不参与金额派生。</para>
    /// </summary>
    [MaxLength(100)]
    public string CommercialInvoiceReference { get; set; } = string.Empty;

    /// <summary>状态（0=草稿，1=已登记，2=已作废；常量见 <c>CustomerSalesInvoiceEvidenceRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记（记录为已登记）时间</summary>
    public DateTime? RecordedAt { get; set; }

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填：作废保留历史，必须记录更正原因而不是静默覆盖）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>已分摊金额（= 有效分摊行金额合计，原币；**非持久化列**）</summary>
    [NotMapped]
    public decimal LinkedAmount { get; set; }

    /// <summary>未分摊金额（= 含税总额 − 已分摊金额，下限 0；**非持久化列**）</summary>
    [NotMapped]
    public decimal UnlinkedAmount { get; set; }

    /// <summary>分摊状态（linked 已全额分摊 / partial 部分分摊 / unlinked 未分摊；**非持久化列**）</summary>
    [NotMapped]
    public string LinkageStatus { get; set; } = string.Empty;

    /// <summary>分摊状态文案（显式说明已分摊 / 未分摊金额；**非持久化列**）</summary>
    [NotMapped]
    public string LinkageText { get; set; } = string.Empty;

    /// <summary>分摊行条数（**非持久化列**）</summary>
    [NotMapped]
    public int AllocationCount { get; set; }

    /// <summary>登记时的客户当前是否可用（存在、未删除且启用；**非持久化列**）</summary>
    [NotMapped]
    public bool CustomerAvailable { get; set; }

    /// <summary>客户可用性文案（**非持久化列**：不可用时照实说明，历史快照照常可读）</summary>
    [NotMapped]
    public string CustomerAvailabilityText { get; set; } = string.Empty;

    /// <summary>显式交叉引用的单证当前是否可用（未引用，或单证仍存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool TradeDocumentAvailable { get; set; } = true;

    /// <summary>交叉引用可用性文案（**非持久化列**：单证被删除时照实说明，历史证据照常可读）</summary>
    [NotMapped]
    public string TradeDocumentAvailabilityText { get; set; } = string.Empty;

    /// <summary>模块边界声明（**非持久化列**：接口与界面同源，声明这不是开票系统 / 税务申报 / 应收账款台账）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}

/// <summary>
/// 客户销项发票证据 → 销售订单 的分摊证据行（ERP-055）。
/// <para>口径：一张发票可以把含税总额的**全部或一部分**分摊到**一张或多张**既有、未取消的销售订单；
/// 分摊的前提是**权威一致**——客户 Id 与币种都必须与发票一致，系统<strong>不</strong>按单号相似度、
/// 金额相近或开票 / 订单日期接近猜测销售订单。</para>
/// <para>快照：销售订单 Id 之外的字段（单号 / 日期 / 状态 / 币种 / 客户编码与名称）都是写入时的**服务端快照**，
/// 订单后续改名 / 改币种 / 被取消 / 软删除都不影响历史分摊的可读性；本表<strong>刻意不建任何外键、也不建导航属性</strong>
/// （发票证据只做软删除，订单与客户可能被取消 / 停用 / 软删除 / 改名 —— 历史证据必须始终可读，
/// 与 ERP-049 / ERP-053 的登记册口径一致），也不参与销售订单的出货、收款引用或库存计算。</para>
/// </summary>
public class CustomerSalesInvoiceAllocation : BaseEntity
{
    /// <summary>所属发票证据 Id（引用 <see cref="CustomerSalesInvoiceEvidence"/>；刻意不建外键，历史证据始终可读）</summary>
    public long CustomerSalesInvoiceEvidenceId { get; set; }

    /// <summary>销售订单 Id（引用 <see cref="SalesOrder"/>；刻意不建外键，订单软删除 / 取消后历史分摊仍可读）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>销售订单号快照</summary>
    [MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>订单状态快照（<c>DocumentStatus</c> 取值；订单取消后历史分摊仍保留登记当时口径）</summary>
    public int OrderStatus { get; set; }

    /// <summary>订单币种快照（币种名，如 CNY / USD；必须与发票币种一致才能建立分摊）</summary>
    [MaxLength(20)]
    public string OrderCurrency { get; set; } = "CNY";

    /// <summary>订单客户 Id 快照（必须与发票客户一致才能建立分摊）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>分摊金额（原币，与发票币种一致；按币种精度取整，必须大于 0 且合计不超过含税总额）</summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>币种（与发票币种一致，冗余保存便于按币种有界检索）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>排序号（仅影响展示顺序，不影响任何金额口径）</summary>
    public int SortOrder { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**） ============

    /// <summary>分摊的销售订单当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool OrderAvailable { get; set; }

    /// <summary>销售订单可用性 / 状态文案（不可用或已取消时照实说明，历史分摊仍保持可读；**非持久化列**）</summary>
    [NotMapped]
    public string OrderAvailabilityText { get; set; } = string.Empty;
}
