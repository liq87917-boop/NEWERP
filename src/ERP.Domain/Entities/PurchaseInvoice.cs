using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 供应商采购发票登记（ERP-043，ERP-065 扩展）：登记**普通发票（普票）/ 增值税专用发票（专票）/
/// 进口发票（海关进口增值税专用缴款书）**的运营证据，并可（可选、显式）把含税总额按权威口径关联到既有采购订单；
/// 可选登记**到期日**与**付款条件**作为显式证据（留空 = 未知 / 未提供，系统绝不推算）。
/// <para>定位：<b>操作性的发票证据台账</b>——它<strong>不是</strong>应付账款台账、<strong>不是</strong>税务申报系统、
/// <strong>不是</strong>付款授权机制，也不引入第二套账务引擎（不记账、不生成凭证 / 收款 / 付款 / 结算单）。</para>
/// <para>金额口径（服务端权威校验）：<c>含税总额（价税合计）= 不含税金额（净额）+ 税额</c>，
/// 三者均按<b>币种精度</b>四舍五入后必须严格相等；金额一律以原币存储，不做汇率换算、不跨币种合并。</para>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>登记 / 记录 / 作废发票都<strong>不</strong>改写采购订单状态、到货进度、已收数量、采购订单金额与明细；</item>
/// <item><strong>不</strong>改写库存与库存成本、库存流水、退税记录、供应商余额 / 结算方式、付款状态；</item>
/// <item>供应商侧只保存**编码 / 名称快照**（服务端写入）：供应商停用 / 删除后历史证据仍可读，绝不回填；</item>
/// <item>关联（分摊）行只保存**采购订单快照**（单号 / 日期 / 币种 / 供应商）与关联金额，不建到采购订单的外键；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 2 已作废 + 作废原因），不物理删除、不静默改写已登记证据。</item>
/// </list>
/// </summary>
public class PurchaseInvoice : BaseEntity
{
    /// <summary>发票类型（普票 / 专票 / 进口；取值见 <c>PurchaseInvoiceRules.InvoiceTypeOrdinary|InvoiceTypeSpecial|InvoiceTypeImport</c>）</summary>
    [Required, MaxLength(20)]
    public string InvoiceType { get; set; } = "普票";

    /// <summary>发票代码（专票必填、普票可选；客户端原样提交，服务端只做去空白与长度校验）</summary>
    [MaxLength(50)]
    public string InvoiceCode { get; set; } = string.Empty;

    /// <summary>发票号码（必填；供应商发票上的号码，不是本系统生成的编号）</summary>
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

    /// <summary>供应商 Id（新增 / 修改时必须是存在、未删除且启用的供应商）</summary>
    public long SupplierId { get; set; }

    /// <summary>供应商编码快照（服务端权威写入，不由客户端提交）</summary>
    [MaxLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    /// <summary>供应商名称快照（服务端权威写入；供应商改名 / 停用后历史证据保持登记当时口径）</summary>
    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>币种（系统支持的币种代码，如 CNY / USD / JPY；关联采购订单时要求币种一致）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>不含税金额（净额，原币；按币种精度取整后参与金额等式校验）</summary>
    public decimal NetAmount { get; set; }

    /// <summary>税额（原币；按币种精度取整后参与金额等式校验；允许为 0 = 不征税 / 免税票）</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>含税总额（价税合计，原币；必须大于 0 且等于净额 + 税额）</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// 到期日（ERP-065：**可选、显式**证据；<c>null</c> = 未知）。
    /// <para>只有授权用户显式提交时才落库；服务端**绝不**按供应商默认账期、付款条件、发票备注、
    /// 历史发票或采购订单推算到期日，也不据此推导逾期 / 账龄。</para>
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// 付款条件（ERP-065：**可选、显式**文本快照，长度 ≤ 200；空串 = 未提供）。
    /// <para>原样保存用户提交的文本证据，服务端不解析、不折算、不回填供应商默认账期。</para>
    /// </summary>
    [MaxLength(200)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>状态（0=草稿，1=已登记，2=已作废；常量见 <c>PurchaseInvoiceRules</c>）</summary>
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

    /// <summary>关联（分摊）到既有采购订单的证据行（草稿期可整体替换；登记后冻结，作废后保留）</summary>
    public List<PurchaseInvoiceAllocation> Allocations { get; set; } = new();

    // ============ 读取侧标注（**非持久化列**：由服务端在读取时计算，绝不落库、绝不回填） ============

    /// <summary>状态文案（草稿 / 已登记 / 已作废；**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>已关联金额（= 关联行金额合计，原币；**非持久化列**，读取时按持久化关联行计算）</summary>
    [NotMapped]
    public decimal LinkedAmount { get; set; }

    /// <summary>未关联金额（= 含税总额 − 已关联金额，下限 0；**非持久化列**）</summary>
    [NotMapped]
    public decimal UnlinkedAmount { get; set; }

    /// <summary>关联状态（linked 已全额关联 / partial 部分关联 / unlinked 未关联；**非持久化列**）</summary>
    [NotMapped]
    public string LinkageStatus { get; set; } = string.Empty;

    /// <summary>关联状态文案（显式说明已关联 / 未关联金额；**非持久化列**）</summary>
    [NotMapped]
    public string LinkageText { get; set; } = string.Empty;

    /// <summary>关联行条数（**非持久化列**）</summary>
    [NotMapped]
    public int AllocationCount { get; set; }

    /// <summary>登记时的供应商当前是否可用（存在且未删除且启用；**非持久化列**）</summary>
    [NotMapped]
    public bool SupplierAvailable { get; set; }

    /// <summary>供应商可用性文案（**非持久化列**：不可用时照实说明，历史快照照常可读）</summary>
    [NotMapped]
    public string SupplierAvailabilityText { get; set; } = string.Empty;

    /// <summary>到期日是否已知（**非持久化列**：只按持久化 <see cref="DueDate"/> 判定；未填写 = 未知，绝不视为当天 / 开票日期）</summary>
    [NotMapped]
    public bool DueDateKnown { get; set; }

    /// <summary>到期日展示文案（**非持久化列**：未知时显式说明「未知」并声明不推算）</summary>
    [NotMapped]
    public string DueDateText { get; set; } = string.Empty;

    /// <summary>付款条件展示文案（**非持久化列**：未提供时显式说明「未提供」，绝不回填默认账期）</summary>
    [NotMapped]
    public string PaymentTermsText { get; set; } = string.Empty;

    /// <summary>模块边界声明（**非持久化列**：接口与界面同源，声明这不是应付账款台账 / 税务申报 / 付款授权）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}

/// <summary>
/// 供应商采购发票 → 采购订单的关联（分摊）证据行（ERP-043）。
/// <para>口径：一条发票可以把含税总额的**全部或一部分**关联到**一张或多张**既有、未取消的采购订单；
/// 关联的前提是**权威一致**——供应商 Id 与币种都必须与发票一致，系统<strong>不</strong>按单号相似度、
/// 金额相近或开票日期接近猜测采购订单。</para>
/// <para>快照：采购订单 Id 之外的字段（单号 / 日期 / 币种 / 供应商编码与名称）都是写入时的**服务端快照**，
/// 采购订单后续改名 / 改币种 / 被取消都不影响历史关联的可读性；本表<strong>不建</strong>到采购订单与供应商的外键，
/// 也不参与采购订单的收货、结算或库存计算。</para>
/// </summary>
public class PurchaseInvoiceAllocation : BaseEntity
{
    /// <summary>所属发票 Id（外键：发票被物理删除时一并清理；发票只做软删除）</summary>
    public long PurchaseInvoiceId { get; set; }

    /// <summary>所属发票（导航属性：仅用于写入时由 EF 回填 <see cref="PurchaseInvoiceId"/>）</summary>
    public PurchaseInvoice? Invoice { get; set; }

    /// <summary>采购订单 Id（引用 <see cref="PurchaseOrder"/>；刻意不建外键，订单软删除 / 取消后历史关联仍可读）</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>采购单号快照</summary>
    [MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; set; }

    /// <summary>订单币种快照（币种名，如 CNY / USD；必须与发票币种一致才能建立关联）</summary>
    [MaxLength(20)]
    public string OrderCurrency { get; set; } = "CNY";

    /// <summary>订单供应商 Id 快照（必须与发票供应商一致才能建立关联）</summary>
    public long SupplierId { get; set; }

    /// <summary>供应商编码快照</summary>
    [MaxLength(50)]
    public string SupplierCode { get; set; } = string.Empty;

    /// <summary>供应商名称快照</summary>
    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>关联金额（原币，与发票币种一致；按币种精度取整，必须大于 0 且合计不超过发票含税总额）</summary>
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

    /// <summary>关联的采购订单当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool OrderAvailable { get; set; }

    /// <summary>采购订单可用性 / 状态文案（不可用或已取消时照实说明，历史关联仍保持可读；**非持久化列**）</summary>
    [NotMapped]
    public string OrderAvailabilityText { get; set; } = string.Empty;
}
