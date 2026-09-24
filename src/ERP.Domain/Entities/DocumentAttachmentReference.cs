using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 业务单据附件引用登记（ERP-045）：为**既有**销售订单 / 采购订单 / 装柜清单 / 出口单证登记
/// <b>仅元数据</b>的附件引用（分类、安全显示名、不透明引用标识、可选内容类型、字节数、校验和、备注）。
/// <para>定位：<b>引用登记册</b>——它只记录「哪张单据的哪类附件由谁登记了什么不透明引用标识」，
/// <strong>不</strong>上传 / 下载 / 预览 / 抓取 / 覆盖 / 删除任何 OSS 对象，也不校验文件是否存在、
/// 内容是否安全、是否真实或是否已获下载授权。真正的对象存储集成属于另行人工审批的活动。</para>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>父单据只保存**服务端权威写入**的号码 / 类型快照（<see cref="ParentNo"/> / <see cref="ParentTypeText"/>），
/// 父单据改名、停用、软删除都不影响历史登记的可读性；本表<strong>不做</strong>任何回填、不写入父单据；</item>
/// <item>登记 / 作废附件引用都<strong>不</strong>改写父单据状态、金额、明细、库存与库存成本、财务、出运或审批状态；</item>
/// <item>引用标识 <see cref="ReferenceId"/> 按**不透明标识**原样保存：服务端拒绝链接 / HTML / 脚本 /
/// <c>data:</c> 方案 / 文件系统穿越等取值，读取时对历史不安全值只输出「不可用」文本，绝不发起服务端请求、不嵌入不可信标记；</item>
/// <item>登记的必填口径是**显式来源授权确认**（<see cref="SourceAuthorizationAcknowledged"/> 必须为真 +
/// <see cref="SourceAuthorizationNote"/>），该确认只表示登记人声明其有权引用，<strong>不</strong>授予存储访问权，
/// 也<strong>不</strong>代表系统已认定生产数据合规；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 1 已作废 + 作废原因）：保留原始元数据与历史，
/// 不提供硬删除、不提供远端对象删除、不提供静默替换；</item>
/// <item>既有 <see cref="TradeDocument.FileNote"/> 附件说明与商品图片位（<c>BaseProduct.Image1~3</c>）
/// 保持原样，<strong>不</strong>自动导入、<strong>不</strong>改写、<strong>不</strong>当作已授权附件。</item>
/// </list>
/// </summary>
public class DocumentAttachmentReference : BaseEntity
{
    /// <summary>父单据类型（白名单：SalesOrder / PurchaseOrder / ContainerLoadingList / TradeDocument；常量见 <c>DocumentAttachmentReferenceRules</c>）</summary>
    [Required, MaxLength(30)]
    public string ParentType { get; set; } = string.Empty;

    /// <summary>父单据 Id（必须指向存在且未删除的权威父单据；本表刻意不建外键，父单据软删除后历史仍可读）</summary>
    public long ParentId { get; set; }

    /// <summary>父单据号码快照（服务端权威写入：销售订单号 / 采购单号 / 装柜清单号 / 单证编号）</summary>
    [MaxLength(50)]
    public string ParentNo { get; set; } = string.Empty;

    /// <summary>父单据类型文案快照（服务端权威写入，如「销售订单」；类型改名后历史登记保持登记当时口径）</summary>
    [MaxLength(30)]
    public string ParentTypeText { get; set; } = string.Empty;

    /// <summary>附件分类（白名单，取值见 <c>DocumentAttachmentReferenceRules.SupportedCategories</c>；未知分类一律拒绝）</summary>
    [Required, MaxLength(30)]
    public string Category { get; set; } = string.Empty;

    /// <summary>安全显示名（必填、有界：拒绝控制字符、HTML / 脚本标记与链接形态，只作为纯文本标签显示）</summary>
    [Required, MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 不透明引用标识（必填、有界）：只允许字母 / 数字 / 点 / 下划线 / 加号 / 连字符组成的**不透明令牌**
    /// （不允许链接、路径、空白、标记字符或 <c>..</c> 穿越），服务端绝不用它发起任何请求。
    /// </summary>
    [Required, MaxLength(200)]
    public string ReferenceId { get; set; } = string.Empty;

    /// <summary>内容类型（可选，如 <c>application/pdf</c>；只接受 <c>type/subtype</c> 形态，不接受参数与标记）</summary>
    [MaxLength(120)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>字节大小（有界，0 = 未提供 / 未知；上限见 <c>DocumentAttachmentReferenceRules.MaxSizeBytes</c>）</summary>
    public long SizeBytes { get; set; }

    /// <summary>校验和（可选，只接受 16~128 位十六进制摘要；不接受其它任意文本）</summary>
    [MaxLength(128)]
    public string Checksum { get; set; } = string.Empty;

    /// <summary>备注（有界）</summary>
    [MaxLength(500)]
    public string Notes { get; set; } = string.Empty;

    /// <summary>来源授权确认（创建时必须显式为真；仅表示登记人声明其有权引用该来源，不授予存储访问权）</summary>
    public bool SourceAuthorizationAcknowledged { get; set; }

    /// <summary>来源授权确认说明（必填、有界：记录确认依据 / 范围；与确认标记一起构成登记留痕）</summary>
    [MaxLength(300)]
    public string SourceAuthorizationNote { get; set; } = string.Empty;

    /// <summary>确认人 / 确认来源（可选，服务端原样记录，最长 100 字符）</summary>
    [MaxLength(100)]
    public string AuthorizedBy { get; set; } = string.Empty;

    /// <summary>授权确认时间（服务端权威写入）</summary>
    public DateTime AuthorizedAt { get; set; }

    /// <summary>登记时间（服务端权威写入）</summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>状态（0=有效，1=已作废；常量见 <c>DocumentAttachmentReferenceRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填：作废保留原始元数据，必须记录更正原因而不是静默覆盖或删除）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>状态文案（有效 / 已作废；**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>分类文案（**非持久化列**）</summary>
    [NotMapped]
    public string CategoryText { get; set; } = string.Empty;

    /// <summary>引用标识是否符合不透明标识口径（历史 / 外部写入的不安全值 → false；**非持久化列**）</summary>
    [NotMapped]
    public bool ReferenceAvailable { get; set; }

    /// <summary>引用不可用时的说明文案（安全值 → 空串；**非持久化列**，界面只显示该文本，绝不把原值当链接 / 标记渲染）</summary>
    [NotMapped]
    public string ReferenceUnavailableText { get; set; } = string.Empty;

    /// <summary>父单据当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool ParentAvailable { get; set; }

    /// <summary>父单据可用性文案（不可用时照实说明，历史快照照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string ParentAvailabilityText { get; set; } = string.Empty;

    /// <summary>模块边界声明（**非持久化列**：接口与界面同源，声明这不是文件存储 / 预览 / 下载通道）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}
