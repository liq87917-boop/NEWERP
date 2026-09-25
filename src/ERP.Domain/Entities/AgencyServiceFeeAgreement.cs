using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 代理服务费协议证据登记（ERP-069）：把**授权用户显式提供的**客户代理服务费商业条款登记为一条可审计的协议证据
/// （协议号 / 权威客户 / 生效日期区间 / 币种 / 披露的计费方式与显式费率或固定金额 / 有界的计费依据说明 / 有界备注）。
/// <para>审计结论（ERP-069 新增前的既有模型，见 <c>docs/代理服务费协议证据说明.md</c> §2）：本仓库此前<strong>没有</strong>
/// 任何「代理服务费协议」权威模型 —— 既有的 <see cref="BaseCustomer.CommissionRatio"/>（客户佣金 / 回佣比例）、
/// <see cref="BaseSupplier.RebateRatio"/>（供应商返点比例）、<see cref="SalesOrder.CommissionRatio"/>（订单佣金比例快照）
/// 都只是**主数据 / 单据上的比例设置**，而 `业务员提成表`（<c>/api/reports/sales-commission</c>，比例取自系统参数
/// <c>SalesCommissionRate</c>、按毛利计算）是**内部业务员提成报表**。三者与「客户代理服务费协议」口径不同
/// （计费主体 / 计费对象 / 计费依据都不同），因此本实体是**新增的独立证据模型**：既不替换、也不复制、也不派生上述任一模型，
/// 且与业务员提成报表<strong>刻意分离</strong>。</para>
/// <para>定位：<b>仓库内商业条款证据册</b>——它<strong>不是</strong>税务发票、<strong>不是</strong>会计凭证或记账分录、
/// <strong>不是</strong>付款授权或资金指令、<strong>不是</strong>法律意见，也<strong>不</strong>是服务已交付或已收付款的证明。</para>
/// <para>费用条款口径（服务端权威校验）：</para>
/// <list type="number">
/// <item><b>用户显式提供</b>：费率 / 固定金额 / 计费依据说明都只来自授权用户提交的值，服务端<strong>不</strong>从
/// 业务员提成设置（系统参数 <c>SalesCommissionRate</c>）、客户 / 供应商主数据比例、历史订单、自由文本、
/// 客户默认值或金额相似度推断、补齐或改写任何商业含义；</item>
/// <item><b>计费方式与必填字段显式对应</b>：比例费率必须填写 (0, 100] 的费率且不得同时填写固定金额；
/// 固定金额必须填写大于 0 的金额（按币种精度取整）且不得同时填写费率 —— 不兼容组合一律拒绝，
/// 不做静默归一化；</item>
/// <item><b>币种权威</b>：币种必须来自系统币种口径，金额一律以原币记录，不做汇率换算、不跨币种合并。</item>
/// </list>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>登记 / 记录 / 作废协议证据都<strong>不</strong>改写客户主数据（含佣金比例与信用状态）、销售订单（含佣金比例与金额）、
/// 装柜与单证、客户收款单及其引用行、客户销项发票证据、库存与库存成本、费用与退税记录；</item>
/// <item><strong>不</strong>开具或作废税务发票、<strong>不</strong>记账或生成凭证 / 收款 / 付款 / 结算单、
/// <strong>不</strong>授权或发起任何付款、<strong>不</strong>提供法律意见，也<strong>不</strong>构成服务已交付 / 已收付款的结论；</item>
/// <item>业务员提成报表（<c>SalesCommissionRate</c> + 毛利）保持**独立的模型与口径**，本实体与它无任何派生关系；</item>
/// <item><strong>不提供</strong>任何「不披露 / 账外 / 隐匿佣金」的字段或流程：协议条款一律以显式、可读、可审计的方式记录；</item>
/// <item>客户侧只保存**编码 / 名称快照**（服务端写入）：客户停用 / 删除 / 改名后历史证据仍按登记当时口径可读，绝不回填；</item>
/// <item>本表<strong>不建</strong>到客户的外键、也没有导航属性（客户可能被停用 / 软删除，历史证据必须始终可读）；</item>
/// <item>更正走**显式作废**（<see cref="VoidReason"/> 必填）：保留原始条款、客户快照、登记人与时间戳，
/// 不提供硬删除、不提供静默改写。</item>
/// </list>
/// </summary>
public class AgencyServiceFeeAgreement : BaseEntity
{
    /// <summary>协议号（必填；由授权用户按真实协议填写，不是本系统生成的编号，也不做自动发号）</summary>
    [Required, MaxLength(50)]
    public string AgreementNo { get; set; } = string.Empty;

    /// <summary>
    /// 规范化协议号（唯一身份判定用：去空白、去连字符 / 下划线并大写；服务端写入）。
    /// 仅用于「同一客户 + 同一规范化协议号」的**精确**判定，不做模糊匹配、不静默合并。
    /// </summary>
    [MaxLength(50)]
    public string NormalizedAgreementNo { get; set; } = string.Empty;

    /// <summary>客户 Id（新增 / 修改时必须是存在、未删除且启用的客户）</summary>
    public long CustomerId { get; set; }

    /// <summary>客户编码快照（服务端权威写入，不由客户端提交）</summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>客户名称快照（服务端权威写入；客户改名 / 停用 / 删除后历史证据保持登记当时口径）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>生效起始日期（必填；由用户显式填写，服务端不按今天或历史订单推算）</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>生效结束日期（可空 = **无固定结束日 / 长期有效**；填写时不得早于生效起始日期）</summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>币种（系统支持的币种代码，如 CNY / USD / JPY；固定金额以该币种原币记录，不做汇率换算）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "CNY";

    /// <summary>披露的计费方式（取值见 <c>AgencyServiceFeeAgreementRules</c>：比例费率 / 固定金额；未知取值一律拒绝）</summary>
    [MaxLength(20)]
    public string FeeMethod { get; set; } = "比例费率";

    /// <summary>费率百分比（比例费率时必填且必须大于 0、不超过 100；固定金额时留 0；保留 4 位小数）</summary>
    public decimal RatePercent { get; set; }

    /// <summary>固定金额（固定金额时必填且按币种精度取整后必须大于 0；比例费率时留 0；原币）</summary>
    public decimal FixedAmount { get; set; }

    /// <summary>计费依据说明（必填、有界人工文本：说明按什么口径计费；服务端绝不据此推断或改写金额）</summary>
    [MaxLength(200)]
    public string FeeBasis { get; set; } = string.Empty;

    /// <summary>状态（0 草稿 / 1 已登记 / 2 已作废；取值见 <c>AgencyServiceFeeAgreementRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>登记时间（登记时由服务端写入；作废不改写它）</summary>
    public DateTime? RecordedAt { get; set; }

    /// <summary>登记人（服务端按已认证身份写入，缺失记「未知用户」；作废不改写它）</summary>
    [MaxLength(100)]
    public string RecordedBy { get; set; } = string.Empty;

    /// <summary>作废时间（作废时由服务端写入）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（作废必填、有界；保留原始条款与历史）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**） ============

    /// <summary>登记时的客户当前是否可用（存在、未删除且启用；**非持久化列**）</summary>
    [NotMapped]
    public bool CustomerAvailable { get; set; }

    /// <summary>客户可用性文案（**非持久化列**：不可用时照实说明，历史快照照常可读）</summary>
    [NotMapped]
    public string CustomerAvailabilityText { get; set; } = string.Empty;

    /// <summary>费用条款文案（**非持久化列**：服务端按持久化值生成，展示费率 / 固定金额与币种口径）</summary>
    [NotMapped]
    public string FeeTermsText { get; set; } = string.Empty;

    /// <summary>生效区间文案（**非持久化列**：无结束日时显式显示「无固定结束日」而不是猜测日期）</summary>
    [NotMapped]
    public string EffectiveRangeText { get; set; } = string.Empty;

    /// <summary>模块边界声明（**非持久化列**：接口与界面同源，声明这不是发票 / 记账 / 付款授权 / 法律意见 / 交付或收付款证明）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;

    /// <summary>与业务员提成报表的分离声明（**非持久化列**：与提成设置无任何派生关系）</summary>
    [NotMapped]
    public string CommissionSeparationText { get; set; } = string.Empty;
}
