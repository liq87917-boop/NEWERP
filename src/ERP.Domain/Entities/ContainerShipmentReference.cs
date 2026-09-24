using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 装柜出运引用登记（ERP-057）：为**既有**装柜链路记录（订柜信息 / 预装柜单 / 装柜清单）登记
/// 一条显式的**出运引用证据**（用户记录的操作性证据）。
/// <para>审计结论（为什么是「一套」而不是「第二套」）：仓库中装柜链路已有**唯一**的持久化引用关系 ——
/// <see cref="ContainerPreLoading.BookingId"/>（预装柜单 → 订柜信息）与
/// <see cref="ContainerLoadingList.PreLoadingId"/>（装柜清单 → 预装柜单），而订柜信息
/// <see cref="ContainerBooking"/> 由 ERP-040 承载本套跟踪值的**权威记录**。
/// 因此本表<strong>不</strong>新建出运主数据、<strong>不</strong>复制订柜跟踪列、也<strong>不</strong>按柜号 /
/// 订单号 / 单证号等自由文本匹配任何记录：它只按 <see cref="SourceType"/> + <see cref="SourceId"/>
/// 显式指向一条权威记录，登记用户录入的出运证据快照。</para>
/// <para>数据边界（重要）：</para>
/// <list type="bullet">
/// <item>登记 / 修订 / 作废出运引用都<strong>不</strong>改写订柜信息 / 预装柜单 / 装柜清单的
/// 任何列、状态与工作流（不推进装柜状态、不写单据号）；</item>
/// <item><strong>不</strong>改写销售订单、采购订单、库存与库存成本、库存流水、单证中心（<see cref="TradeDocument"/>）、
/// 发票、费用与分摊、收付款、税务与结算记录；</item>
/// <item>表头字段一律**可选**：未填写保持空串 / <c>null</c>（= 未知），<strong>绝不</strong>由柜型、体积、客户、
/// 航线或任何自由文本推断；出运方式只接受 <c>LCL</c> / <c>FCL</c> / 未指定；</item>
/// <item>源记录只保存**服务端写入的快照**（单号 / 日期 / 状态 / 柜号），刻意<strong>不建任何外键</strong> ——
/// 源记录被软删除或改名后历史证据照常可读，只是显式标注不可用；</item>
/// <item>更正证据走**显式修订**（每次修订先写一条 <see cref="ContainerShipmentReferenceRevision"/>
/// 记录修订前的原值，修订号递增）或**显式作废**（必填原因），不提供硬删除与静默改写；</item>
/// <item>报关行只保存字典项引用与名称快照（由服务端按「其他资料」中 <c>InfoType = CustomsBroker</c> 的字典项
/// 权威写入，客户端自由文本不被采信）。</item>
/// </list>
/// <para>语义边界：它<strong>不是</strong>承运人 / 海关 / 货代的确认，<strong>不是</strong>提单正本，
/// <strong>不是</strong>报关或放行结论，也<strong>不</strong>构成任何法律或清关依据。</para>
/// </summary>
public class ContainerShipmentReference : BaseEntity
{
    /// <summary>
    /// 源记录类型（显式 allowlist：<c>booking</c> 订柜信息 / <c>pre-loading</c> 预装柜单 /
    /// <c>loading-list</c> 装柜清单；取值见 <c>ContainerShipmentReferenceRules</c>）。
    /// </summary>
    [Required, MaxLength(20)]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>源记录 Id（显式指向 <see cref="ContainerBooking"/> / <see cref="ContainerPreLoading"/> / <see cref="ContainerLoadingList"/> 之一）</summary>
    public long SourceId { get; set; }

    /// <summary>源记录单号快照（服务端按源记录权威写入，客户端提交值不被采信）</summary>
    [MaxLength(50)]
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>源记录日期快照（订柜日期 / 装柜日期，服务端写入）</summary>
    public DateTime SourceDate { get; set; }

    /// <summary>源记录状态快照（<c>DocumentStatus</c> 取值；源记录后来改状态不影响历史快照）</summary>
    public int SourceStatus { get; set; }

    /// <summary>源记录状态文案快照（如「待提交」「已审核」）</summary>
    [MaxLength(30)]
    public string SourceStatusText { get; set; } = string.Empty;

    /// <summary>柜号快照（源记录上的柜号；订柜信息无柜号时保持空串 = 未知，不由其它字段推断）</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    // ============ 出运证据（全部可选：未填写 = 未知，绝不推断） ============

    /// <summary>出运方式（<c>LCL</c> 拼箱 / <c>FCL</c> 整箱；空串 = 未指定 / 未知）</summary>
    [MaxLength(10)]
    public string ShipmentMode { get; set; } = string.Empty;

    /// <summary>订舱号 / 托运单号（S/O）引用文本（用户录入，空串 = 未知）</summary>
    [MaxLength(50)]
    public string ShippingOrderNo { get; set; } = string.Empty;

    /// <summary>提单号（B/L）引用文本（用户录入，空串 = 未知；不是提单正本）</summary>
    [MaxLength(50)]
    public string BillOfLadingNo { get; set; } = string.Empty;

    /// <summary>承运人（船公司 / 航空公司等）名称（自由文本，空串 = 未知）</summary>
    [MaxLength(200)]
    public string CarrierName { get; set; } = string.Empty;

    /// <summary>货代名称（自由文本，空串 = 未知）</summary>
    [MaxLength(200)]
    public string ForwarderName { get; set; } = string.Empty;

    /// <summary>起运港（空串 = 未知）</summary>
    [MaxLength(100)]
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>中转港（空串 = 未知）</summary>
    [MaxLength(100)]
    public string TransitPort { get; set; } = string.Empty;

    /// <summary>目的港（空串 = 未知）</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>计划开船 / 起运时间（ETD；<c>null</c> = 未知）</summary>
    public DateTime? PlannedDepartureAt { get; set; }

    /// <summary>计划到港 / 抵达时间（ETA；<c>null</c> = 未知）</summary>
    public DateTime? PlannedArrivalAt { get; set; }

    /// <summary>拖车 / 集卡服务商名称（自由文本，空串 = 未知）</summary>
    [MaxLength(200)]
    public string TruckerName { get; set; } = string.Empty;

    /// <summary>
    /// 报关行 Id（引用 <see cref="BaseOtherInfo"/> 中 <c>InfoType = CustomsBroker</c> 的字典项；可空，不建外键）。
    /// 只接受启用、未删除的报关行字典项。
    /// </summary>
    public long? CustomsBrokerId { get; set; }

    /// <summary>报关行名称快照（服务端按字典项权威写入；字典项后来停用 / 删除时历史快照照常可读）</summary>
    [MaxLength(100)]
    public string CustomsBrokerName { get; set; } = string.Empty;

    /// <summary>备注（用户记录说明，空串 = 无；不参与任何派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 状态与留痕 ============

    /// <summary>状态：<c>1</c> 已登记（有效，可修订 / 可作废）；<c>2</c> 已作废（只读，保留原值）</summary>
    public int Status { get; set; } = 1;

    /// <summary>登记时间（首次登记时间，修订不改写）</summary>
    public DateTime RecordedAt { get; set; } = DateTime.Now;

    /// <summary>当前修订号（首次登记 = 1；每次修订 +1，修订前的原值写入 <see cref="ContainerShipmentReferenceRevision"/>）</summary>
    public int RevisionNo { get; set; } = 1;

    /// <summary>最近一次修订时间（未修订过为 <c>null</c>）</summary>
    public DateTime? LastRevisedAt { get; set; }

    /// <summary>最近一次修订原因（必填于修订动作，有界；未修订过为空串）</summary>
    [MaxLength(200)]
    public string LastRevisionReason { get; set; } = string.Empty;

    /// <summary>作废时间（未作废为 <c>null</c>）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填于作废动作；未作废为空串）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，服务端按当前主数据重算） ============

    /// <summary>源记录当前是否可读（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool SourceAvailable { get; set; } = true;

    /// <summary>源记录可用性文案（被删除 / 不存在时照实说明，历史快照仍可读；**非持久化列**）</summary>
    [NotMapped]
    public string SourceAvailabilityText { get; set; } = string.Empty;

    /// <summary>报关行字典项当前是否仍可选用（**非持久化列**：字典项被停用 / 删除 / 改类型后为 <c>false</c>）</summary>
    [NotMapped]
    public bool CustomsBrokerAvailable { get; set; } = true;

    /// <summary>报关行可用性文案（**非持久化列**）</summary>
    [NotMapped]
    public string CustomsBrokerAvailabilityText { get; set; } = string.Empty;

    /// <summary>状态文案（已登记 / 已作废；**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>出运方式文案（拼箱 LCL / 整箱 FCL / 未知；**非持久化列**）</summary>
    [NotMapped]
    public string ShipmentModeText { get; set; } = string.Empty;

    /// <summary>修订条数（保留的修订前原值条数；**非持久化列**）</summary>
    [NotMapped]
    public int RevisionCount { get; set; }

    /// <summary>模块边界声明（接口与界面同源；**非持久化列**）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}

/// <summary>
/// 出运引用修订留痕（ERP-057，**只追加**）：每次修订出运引用前，先按服务端口径写入一条
/// 「修订前的原值快照」，<see cref="RevisionNo"/> 记录被取代的那一版修订号。
/// <para>口径：本表<strong>不</strong>提供修改与删除接口，也不保存「修订后的值」——
/// 修订后的值就是 <see cref="ContainerShipmentReference"/> 的当前值，历史值永远留在本表，
/// 因此「谁在什么时候因为什么原因把哪个字段从什么改成了什么」始终可查，而不是被静默改写。</para>
/// <para>本表只保存快照字段，刻意不建任何外键与导航属性（引用行只做软删除，源记录可被软删除 / 改名）。</para>
/// </summary>
public class ContainerShipmentReferenceRevision : BaseEntity
{
    /// <summary>所属出运引用 Id（引用 <see cref="ContainerShipmentReference"/>；刻意不建外键）</summary>
    public long ContainerShipmentReferenceId { get; set; }

    /// <summary>被取代的修订号（即本次修订前的 <c>RevisionNo</c>；当前修订号 = 本值 + 1）</summary>
    public int RevisionNo { get; set; }

    /// <summary>源记录类型快照（与引用行一致；源记录类型不允许改派）</summary>
    [MaxLength(20)]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>源记录 Id 快照（不允许改派到其它记录）</summary>
    public long SourceId { get; set; }

    /// <summary>源记录单号快照</summary>
    [MaxLength(50)]
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>取代（修订）时间</summary>
    public DateTime SupersededAt { get; set; } = DateTime.Now;

    /// <summary>修订原因（必填于修订动作，有界）</summary>
    [MaxLength(200)]
    public string Reason { get; set; } = string.Empty;

    // ---- 修订前的原值快照（逐列，不再解释） ----

    /// <summary>修订前的出运方式</summary>
    [MaxLength(10)]
    public string ShipmentMode { get; set; } = string.Empty;

    /// <summary>修订前的订舱号 / 托运单号</summary>
    [MaxLength(50)]
    public string ShippingOrderNo { get; set; } = string.Empty;

    /// <summary>修订前的提单号</summary>
    [MaxLength(50)]
    public string BillOfLadingNo { get; set; } = string.Empty;

    /// <summary>修订前的承运人</summary>
    [MaxLength(200)]
    public string CarrierName { get; set; } = string.Empty;

    /// <summary>修订前的货代</summary>
    [MaxLength(200)]
    public string ForwarderName { get; set; } = string.Empty;

    /// <summary>修订前的起运港</summary>
    [MaxLength(100)]
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>修订前的中转港</summary>
    [MaxLength(100)]
    public string TransitPort { get; set; } = string.Empty;

    /// <summary>修订前的目的港</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>修订前的计划开船时间</summary>
    public DateTime? PlannedDepartureAt { get; set; }

    /// <summary>修订前的计划到港时间</summary>
    public DateTime? PlannedArrivalAt { get; set; }

    /// <summary>修订前的拖车 / 集卡服务商</summary>
    [MaxLength(200)]
    public string TruckerName { get; set; } = string.Empty;

    /// <summary>修订前的报关行 Id</summary>
    public long? CustomsBrokerId { get; set; }

    /// <summary>修订前的报关行名称快照</summary>
    [MaxLength(100)]
    public string CustomsBrokerName { get; set; } = string.Empty;

    /// <summary>修订前的备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
