using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 装柜出运里程碑证据（ERP-058）：挂在 ERP-057 出运引用记录
/// （<see cref="ContainerShipmentReference"/>）之下的**只追加**操作性事件登记册。
/// <para>审计结论（为什么挂在出运引用之下，而不是新建第二套跟踪模型）：ERP-057 已经建立了
/// 「既有装柜链路记录（订柜信息 / 预装柜单 / 装柜清单）→ 出运引用证据」的唯一权威登记册，
/// 因此本表只按 <see cref="ContainerShipmentReferenceId"/> 显式指向该登记册中的一条出运引用，
/// <strong>不</strong>新建出运 / 跟踪主数据、<strong>不</strong>复制订柜跟踪列，
/// 也<strong>不</strong>按柜号、S/O、B/L 或任何自由文本匹配记录 —— 缺失父记录时证据可读，
/// 但<strong>绝不</strong>被静默改派到另一条出运引用。</para>
/// <para>字段边界：</para>
/// <list type="bullet">
/// <item>事件类型只接受显式 allowlist（实际开船 / 实际到港 / 查验 / 放行），其余取值一律拒绝；</item>
/// <item>事件发生时间由用户录入且<strong>必填</strong>：<strong>不</strong>由计划开船 / 计划到港时间、
/// 单据状态或任何自由文本推断，缺失的选项（来源说明 / 备注 / 记录人）保持空串 = 未知；</item>
/// <item>记录人由用户录入，登记时间由服务端写入；一条记录 = 一次操作性事件，
/// 同一父记录 + 事件类型 + 事件时间不允许重复有效登记（不静默合并）；</item>
/// <item>登记后只提供**显式作废**（必填原因）：作废只改状态与作废留痕，原始事件类型 / 事件时间 /
/// 来源说明 / 备注 / 记录人照常可读，<strong>不</strong>提供硬删除与改写；</item>
/// <item>本表<strong>不</strong>保存任何「权威结论」字段：查验与放行只是用户记录的操作性证据。</item>
/// </list>
/// <para>语义边界（重要）：本登记册<strong>不是</strong>承运人 / 海关 / 货代的确认或回执，
/// <strong>不是</strong>报关或放行结论，<strong>不</strong>构成清关许可、交付承诺或法律依据，
/// 也<strong>不</strong>代表「允许出运」。</para>
/// <para>数据边界（重要）：登记 / 作废里程碑<strong>不</strong>改写所属出运引用、订柜信息 / 预装柜单 /
/// 装柜清单的任何列、状态与工作流，<strong>不</strong>自动推进业务单据，也<strong>不</strong>改写销售订单、
/// 采购订单、库存与库存成本、库存流水、单证中心（<see cref="TradeDocument"/>）、发票、费用与分摊、
/// 收付款、税务与结算记录，<strong>不</strong>轮询任何外部服务。</para>
/// </summary>
public class ContainerShipmentMilestone : BaseEntity
{
    /// <summary>
    /// 所属出运引用 Id（引用 ERP-057 <see cref="ContainerShipmentReference"/>；刻意不建外键）：
    /// 只按显式的父记录 Id 关联，绝不按柜号 / S/O / B/L 等自由文本匹配。
    /// </summary>
    public long ContainerShipmentReferenceId { get; set; }

    /// <summary>
    /// 事件类型（显式 allowlist：<c>actual-departure</c> 实际开船 / <c>actual-arrival</c> 实际到港 /
    /// <c>inspection</c> 查验 / <c>customs-release</c> 放行；取值见 <c>ContainerShipmentMilestoneRules</c>）。
    /// </summary>
    [Required, MaxLength(20)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// 事件发生时间（用户录入，必填且必须有界）：绝不按计划开船 / 计划到港时间、单据状态或自由文本推断；
    /// 同一父记录 + 类型 + 时间构成「重复有效证据」的判定键。
    /// </summary>
    public DateTime EventAt { get; set; }

    /// <summary>
    /// 来源说明（用户录入的来源描述，空串 = 未知，如「船公司网站截图」「货代邮件」「报关行通知」）：
    /// 只作文字来源说明，<strong>不是</strong>承运人 / 海关 / 货代回执，也不参与任何派生。
    /// </summary>
    [MaxLength(200)]
    public string SourceDescription { get; set; } = string.Empty;

    /// <summary>备注（用户记录说明，空串 = 无；不参与任何派生）</summary>
    [MaxLength(500)]
    public string Notes { get; set; } = string.Empty;

    /// <summary>记录人（用户录入的操作人标识，空串 = 未知；由用户填写，不按登录用户强行推断）</summary>
    [MaxLength(100)]
    public string RecordedBy { get; set; } = string.Empty;

    /// <summary>登记时间（服务端写入；只追加，登记后不因修订或作废而改写）</summary>
    public DateTime RecordedAt { get; set; } = DateTime.Now;

    /// <summary>状态：<c>1</c> 已登记（有效证据，可作废）；<c>2</c> 已作废（只读，保留原始与作废留痕）</summary>
    public int Status { get; set; } = 1;

    /// <summary>作废时间（未作废为 <c>null</c>）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填于作废动作；未作废为空串）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，服务端按当前父记录重算） ============

    /// <summary>事件类型文案（实际开船 / 实际到港 / 查验 / 放行；未知取值照实回显；**非持久化列**）</summary>
    [NotMapped]
    public string EventTypeText { get; set; } = string.Empty;

    /// <summary>状态文案（已登记 / 已作废；**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>父出运引用当前是否可读（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool ParentAvailable { get; set; } = true;

    /// <summary>父记录可用性文案（被删除 / 不存在时照实说明，历史里程碑仍可读；**非持久化列**）</summary>
    [NotMapped]
    public string ParentAvailabilityText { get; set; } = string.Empty;

    /// <summary>父出运引用的源记录类型快照（读取时由父记录带入，只读派生；**非持久化列**）</summary>
    [NotMapped]
    public string ParentSourceType { get; set; } = string.Empty;

    /// <summary>父出运引用的源记录类型文案（订柜信息 / 预装柜单 / 装柜清单；**非持久化列**）</summary>
    [NotMapped]
    public string ParentSourceTypeText { get; set; } = string.Empty;

    /// <summary>父出运引用的源记录单号快照（读取时由父记录带入；**非持久化列**）</summary>
    [NotMapped]
    public string ParentSourceNo { get; set; } = string.Empty;

    /// <summary>父出运引用的柜号快照（读取时由父记录带入，可能为空 = 未知；**非持久化列**）</summary>
    [NotMapped]
    public string ParentContainerNo { get; set; } = string.Empty;

    /// <summary>父出运引用的状态文案快照（已登记 / 已作废；**非持久化列**）</summary>
    [NotMapped]
    public string ParentStatusText { get; set; } = string.Empty;

    /// <summary>证据性质文案（仓库操作性证据 vs 权威结论的显式说明；**非持久化列**）</summary>
    [NotMapped]
    public string EvidenceCategoryText { get; set; } = string.Empty;

    /// <summary>模块边界声明（接口与界面同源；**非持久化列**）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}
