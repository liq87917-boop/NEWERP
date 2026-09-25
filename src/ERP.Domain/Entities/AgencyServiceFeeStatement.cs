using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 代理服务费**对账单证据**表头（ERP-070）：把授权用户**显式提供的**客户代理服务费对账证据登记为一条可审计证据 ——
/// 对账单号 / 权威客户 / 币种 / 对账日期 / **可选**到期日 / 服务期间 / 显式关联的 ERP-069 协议证据 /
/// 有界备注 / **一条或多条显式服务来源引用行**。
/// <para>与 ERP-069 的关系：本表<strong>必须显式关联</strong>一条既有的
/// <see cref="AgencyServiceFeeAgreement"/>（协议证据登记册），并对服务端校验「对账单客户 = 协议客户」、
/// 「对账单币种 = 协议币种」；协议号 / 协议费用条款只作为**只读快照**写入，不在读取时回写、也不派生任何金额。</para>
/// <para>定位：<b>仓库内操作性费用证据册</b>——它<strong>不是</strong>税务发票、<strong>不是</strong>具有法律效力的
/// 客户对账单确认、<strong>不是</strong>收入确认、<strong>不是</strong>付款通知或催款、<strong>不是</strong>结算或核销确认，
/// 也<strong>不是</strong>会计凭证或总账记账分录。</para>
/// <para>金额口径（服务端权威）：</para>
/// <list type="number">
/// <item><b>行金额只来自授权用户显式提交</b>：服务端按币种精度（见 <c>CurrencyAmountRules</c>）取整并要求大于 0；
/// 当前协议费率 / 固定金额 / 客户默认值 / 来源单据金额 / 自由文本 / 金额相似度都<strong>不会</strong>被转换成费用；</item>
/// <item><b>合计由服务端按已校验行计算</b>：<see cref="TotalAmount"/> 是服务端按币种精度对有效行金额求和的结果，
/// 客户端提交的同名字段（若有）一律不被采信；</item>
/// <item><b>原币口径</b>：金额一律以对账单币种原币记录，不做汇率换算、不跨币种合并。</item>
/// </list>
/// <para>到期日口径：<see cref="DueDate"/> 是**可选**的显式持久化证据；留空 = <b>未知</b>，
/// 服务端<strong>不</strong>按客户账期、协议文字、对账日期或历史单据推算 / 补齐任何到期日。</para>
/// <para>唯一性：同一「客户 + 规范化对账单号」在**未作废**记录内唯一（草稿同样占用身份）；重复被拒绝而不是合并。</para>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>新增 / 修改 / 登记 / 作废都<strong>不</strong>改写 ERP-069 协议证据（含费率 / 固定金额 / 状态）、
/// 客户主数据、销售订单、装柜与装柜清单、单证、发票（销项 / 进项）、收款单及其引用行、库存与库存成本、
/// 库存流水、费用与退税、结算与余额记录；</item>
/// <item><strong>不</strong>开具或作废税务发票、<strong>不</strong>记账或生成凭证、<strong>不</strong>催收或联系客户、
/// <strong>不</strong>收款或付款、<strong>不</strong>调用任何外部服务；</item>
/// <item>客户侧只保存**编码 / 名称快照**（服务端写入）：客户停用 / 删除 / 改名后历史证据仍按登记当时口径可读，绝不回填；</item>
/// <item>本表<strong>不建</strong>到客户与协议的**任何外键与导航属性**（客户或协议软删除后历史证据必须始终可读）；</item>
/// <item>更正走**显式作废**（<see cref="VoidReason"/> 必填）：保留原始行、来源快照、客户与协议快照、登记人与时间戳，
/// 不提供硬删除、不提供静默改写。</item>
/// </list>
/// </summary>
public class AgencyServiceFeeStatement : BaseEntity
{
    /// <summary>对账单号（必填；由授权用户按真实对账单填写，不是本系统生成的编号，也不做自动发号）</summary>
    [Required, MaxLength(50)]
    public string StatementNo { get; set; } = string.Empty;

    /// <summary>
    /// 规范化对账单号（唯一身份判定用：去空白、去连字符 / 下划线并大写；服务端写入）。
    /// 仅用于「同一客户 + 同一规范化对账单号」的**精确**判定，不做模糊匹配、不静默合并。
    /// </summary>
    [MaxLength(50)]
    public string NormalizedStatementNo { get; set; } = string.Empty;

    /// <summary>客户 Id（新增 / 修改时必须是存在、未删除且启用的客户，且必须与关联协议客户一致）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照（服务端权威写入，不由客户端提交）</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照（服务端权威写入；客户改名 / 停用 / 删除后历史证据保持登记当时口径）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>币种（必须受支持，且必须与关联协议币种一致；所有行金额与合计都以该币种原币记录）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>对账日期（必填；由用户显式填写，服务端不按当天或单据日期推算）</summary>
    public DateTime StatementDate { get; set; }

    /// <summary>
    /// **可选**到期日（显式持久化证据）。留空 = <b>未知</b>：服务端不会按客户账期、协议文字、
    /// 对账日期或历史单据推算 / 补齐任何到期日；填写时不得早于对账日期。
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>服务期间起始日期（必填；由用户显式填写，不按对账日期或行来源日期推算）</summary>
    public DateTime ServicePeriodFrom { get; set; }

    /// <summary>服务期间结束日期（必填；不得早于服务期间起始日期）</summary>
    public DateTime ServicePeriodTo { get; set; }

    /// <summary>显式关联的 ERP-069 协议证据 Id（必须存在、未删除且为「已登记」状态）</summary>
    public long AgreementId { get; set; }

    /// <summary>协议号快照（服务端按关联协议写入）</summary>
    [MaxLength(50)]
    public string AgreementNo { get; set; } = string.Empty;

    /// <summary>协议币种快照（服务端写入；必须与对账单币种一致才能登记）</summary>
    [MaxLength(20)]
    public string AgreementCurrency { get; set; } = string.Empty;

    /// <summary>协议客户 Id 快照（服务端写入；必须与对账单客户一致才能登记）</summary>
    public long AgreementCustomerId { get; set; }

    /// <summary>协议披露的计费方式快照（比例费率 / 固定金额；只作只读回显，不当成任何行的费用）</summary>
    [MaxLength(20)]
    public string AgreementFeeMethod { get; set; } = string.Empty;

    /// <summary>
    /// 协议费用条款文案快照（有界；服务端按 ERP-069 口径生成，只用于人工核对协议身份，
    /// **不会**被折算、分摊或转换成任何行金额）。
    /// </summary>
    [MaxLength(200)]
    public string AgreementTermsText { get; set; } = string.Empty;

    /// <summary>有效行金额合计（**服务端计算**：按币种精度对有效行金额求和；客户端提交的合计不被采信）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>状态（0 草稿 / 1 已登记 / 2 已作废；取值见 <c>AgencyServiceFeeStatementRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记时间（登记时由服务端写入；作废不改写它）</summary>
    public DateTime? RecordedAt { get; set; }

    /// <summary>登记人（服务端按已认证身份写入，缺失记「未知用户」；作废不改写它）</summary>
    [MaxLength(100)]
    public string RecordedBy { get; set; } = string.Empty;

    /// <summary>作废时间（作废时由服务端写入）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（作废必填、有界；保留原始行与历史）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    /// <summary>备注（有界人工文本；不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**） ============

    /// <summary>登记时的客户当前是否可用（存在、未删除且启用；**非持久化列**）</summary>
    [NotMapped]
    public bool CustomerAvailable { get; set; }

    /// <summary>客户可用性文案（**非持久化列**：不可用时照实说明，历史快照照常可读）</summary>
    [NotMapped]
    public string CustomerAvailabilityText { get; set; } = string.Empty;

    /// <summary>关联协议当前是否可用（存在、未删除且仍为已登记；**非持久化列**）</summary>
    [NotMapped]
    public bool AgreementAvailable { get; set; }

    /// <summary>关联协议可用性文案（**非持久化列**：不可用时照实说明，历史快照照常可读）</summary>
    [NotMapped]
    public string AgreementAvailabilityText { get; set; } = string.Empty;

    /// <summary>到期日文案（**非持久化列**：留空时显式显示「未提供（到期日未知）」，绝不推算）</summary>
    [NotMapped]
    public string DueDateText { get; set; } = string.Empty;

    /// <summary>服务期间文案（**非持久化列**）</summary>
    [NotMapped]
    public string ServicePeriodText { get; set; } = string.Empty;

    /// <summary>币种兼容文案（**非持久化列**：显式说明客户 / 协议 / 行来源的币种口径）</summary>
    [NotMapped]
    public string CurrencyCompatibilityText { get; set; } = string.Empty;

    /// <summary>行数（**非持久化列**：读取时按有界查询计数，不落库、不作为费用依据）</summary>
    [NotMapped]
    public int LineCount { get; set; }

    /// <summary>服务来源链接口径声明（**非持久化列**：接口与界面同源）</summary>
    [NotMapped]
    public string SourceLinkRuleText { get; set; } = string.Empty;

    /// <summary>金额口径声明（**非持久化列**：接口与界面同源）</summary>
    [NotMapped]
    public string AmountRuleText { get; set; } = string.Empty;

    /// <summary>唯一性口径声明（**非持久化列**：接口与界面同源）</summary>
    [NotMapped]
    public string UniquenessRuleText { get; set; } = string.Empty;

    /// <summary>与发票 / 收款 / 记账 / 提成的分离声明（**非持久化列**：接口与界面同源）</summary>
    [NotMapped]
    public string SeparationText { get; set; } = string.Empty;

    /// <summary>模块边界声明（**非持久化列**：接口与界面同源）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}

/// <summary>
/// 代理服务费对账单**服务来源引用行**（ERP-070）：对账单下的一条显式费用证据，
/// 用**持久化标识符**指向一条既有、可核对的**服务来源记录**，并保存服务端写入的来源快照与用户显式填写的行证据。
/// <para>支持的来源类型（显式 allowlist，见 <c>AgencyServiceFeeStatementRules</c>）：</para>
/// <list type="bullet">
/// <item><c>sales-order</c> 销售订单（<see cref="SalesOrder"/>）：来源记录自带客户与币种，因此
/// **客户与币种兼容性都会被服务端校验**；</item>
/// <item><c>loading-list</c> 装柜清单（<see cref="ContainerLoadingList"/>）：来源记录**不携带币种列**，
/// 因此币种兼容性对该来源**不适用**（照实标注「来源不携带币种」，绝不由客户 / 订单 / 自由文本补一个币种），
/// 客户兼容性仍按装柜清单的**兼容主客户字段**校验（不按参与方行推断归属）。</item>
/// </list>
/// <para>链接口径（关键）：行只按 <see cref="SourceType"/> + <see cref="SourceId"/> 的**持久化标识符**引用来源，
/// <strong>绝不</strong>按单号文本、金额、日期或相似度猜测链接；服务端不会替用户「找一条看起来像的」来源。</para>
/// <para>金额口径：<see cref="Amount"/> 与 <see cref="BasisQuantity"/> 都是**用户显式提交的持久化证据**，
/// 服务端只做币种精度取整与范围校验；当前协议费率 / 协议固定金额 / 客户默认值 / 来源单据金额 /
/// 自由文本 / 相似度都<strong>不会</strong>被折算或转换成费用。</para>
/// <para>唯一性（防「重复计费证据」）：同一来源（<see cref="SourceType"/> + <see cref="SourceId"/>）
/// 在**未作废、未删除**的行内全局唯一（过滤唯一索引 <c>UX_AgencyServiceFeeStatementLines_ActiveSource</c>）——
/// 草稿行同样占用来源身份，作废后释放；重复引用一律拒绝而不是合并。</para>
/// <para>边界：行<strong>不</strong>改写任何来源记录（订单 / 装柜清单）与协议、客户、发票、收款、库存、费用、结算记录；
/// <strong>不</strong>开票、<strong>不</strong>记账、<strong>不</strong>收款或催收、<strong>不</strong>调用外部服务；
/// 来源被删除 / 改名 / 取消后历史行仍按登记当时快照可读，只显式标注不可用，绝不改派到别的记录。</para>
/// </summary>
public class AgencyServiceFeeStatementLine : BaseEntity
{
    /// <summary>所属对账单 Id（引用 <see cref="AgencyServiceFeeStatement"/>；**刻意不建外键与导航属性**）</summary>
    public long StatementId { get; set; }

    /// <summary>行号（服务端按提交顺序从 1 起连续编号，用于人工核对；不允许跳号或重复）</summary>
    public int LineNo { get; set; }

    /// <summary>来源类型（显式 allowlist：<c>sales-order</c> 销售订单 / <c>loading-list</c> 装柜清单）</summary>
    [Required, MaxLength(20)]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源记录 Id（显式指向 <see cref="SalesOrder"/> 或 <see cref="ContainerLoadingList"/> 之一）</summary>
    public long SourceId { get; set; }

    /// <summary>来源单号快照（服务端按来源记录权威写入：订单号 / 装柜清单号）</summary>
    [MaxLength(50)]
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>来源日期快照（服务端写入：订单日期 / 装柜日期）</summary>
    public DateTime SourceDate { get; set; }

    /// <summary>来源状态码快照（<c>DocumentStatus</c> 取值；来源后来改状态不影响历史快照）</summary>
    public int SourceStatus { get; set; }

    /// <summary>来源状态文案快照（服务端写入）</summary>
    [MaxLength(30)]
    public string SourceStatusText { get; set; } = string.Empty;

    /// <summary>来源客户 Id 快照（必须与对账单客户一致才能建立引用）</summary>
    public long SourceCustomerId { get; set; }

    /// <summary>来源客户编码快照（与对账单客户快照一致）</summary>
    [MaxLength(50)]
    public string SourceCustomerCode { get; set; } = string.Empty;

    /// <summary>来源客户名称快照（与对账单客户快照一致）</summary>
    [MaxLength(200)]
    public string SourceCustomerName { get; set; } = string.Empty;

    /// <summary>
    /// 来源币种快照：**空串 = 该来源记录本身不携带币种**（如装柜清单），
    /// 不是「未知但可以猜」。非空时必须与对账单币种一致（不做汇率换算）。
    /// </summary>
    [MaxLength(20)]
    public string SourceCurrency { get; set; } = string.Empty;

    /// <summary>行说明（必填、有界人工文本：说明这一行对应什么服务；服务端不据此推断金额）</summary>
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 计费基础数量快照（**可选**显式持久化证据，4 位小数）：由用户显式填写或按权威字段抄录；
    /// 留空 = <b>未知</b>，服务端不会从来源单据数量、协议费率或自由文本推算。
    /// </summary>
    public decimal? BasisQuantity { get; set; }

    /// <summary>计费基础说明（必填、有界人工文本：说明数量 / 口径来自哪里；服务端不据此计算金额）</summary>
    [MaxLength(200)]
    public string BasisNote { get; set; } = string.Empty;

    /// <summary>行金额（**用户显式提交**，按对账单币种精度取整后必须大于 0；原币，不做汇率换算）</summary>
    public decimal Amount { get; set; }

    /// <summary>币种（与对账单币种一致，冗余保存便于按币种有界检索）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>状态（0 草稿 / 1 已登记 / 2 已作废；与所属对账单的状态同步，见 <c>AgencyServiceFeeStatementRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记时间（服务端随对账单登记写入；作废不改写它）</summary>
    public DateTime? RecordedAt { get; set; }

    /// <summary>登记人（服务端按已认证身份写入，缺失记「未知用户」；作废不改写它）</summary>
    [MaxLength(100)]
    public string RecordedBy { get; set; } = string.Empty;

    /// <summary>作废时间（随对账单作废写入；单独作废不开放的字段保留可空口径）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（随所属对账单的作废原因写入，保留历史）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    /// <summary>行备注（有界人工文本；不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**） ============

    /// <summary>来源记录当前是否可读（存在且未删除；**非持久化列**：不可用时照实说明，历史快照照常可读）</summary>
    [NotMapped]
    public bool SourceAvailable { get; set; }

    /// <summary>来源可用性文案（**非持久化列**）</summary>
    [NotMapped]
    public string SourceAvailabilityText { get; set; } = string.Empty;

    /// <summary>来源币种兼容文案（**非持久化列**：显式说明「一致」/「来源不携带币种，币种校验不适用」）</summary>
    [NotMapped]
    public string SourceCurrencyCompatibilityText { get; set; } = string.Empty;

    /// <summary>来源类型文案（**非持久化列**：销售订单 / 装柜清单）</summary>
    [NotMapped]
    public string SourceTypeText { get; set; } = string.Empty;

    /// <summary>来源身份文案（**非持久化列**：类型 + 单号，便于人工核对）</summary>
    [NotMapped]
    public string SourceIdentityText { get; set; } = string.Empty;

    /// <summary>状态文案（**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>行金额文案（**非持久化列**：显式展示原币与币种精度，不做换算）</summary>
    [NotMapped]
    public string AmountText { get; set; } = string.Empty;

    /// <summary>金额口径声明（**非持久化列**：接口与界面同源）</summary>
    [NotMapped]
    public string AmountRuleText { get; set; } = string.Empty;
}
