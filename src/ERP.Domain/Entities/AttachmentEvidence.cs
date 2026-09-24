using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 业务单据附件**内容证据**（ERP-061）：把用户提供的 PDF / PNG / JPEG 证据文件显式挂到**既有**
/// 销售订单 / 采购订单（后续任务扩展单证）上，并保存服务端权威生成的内容元数据
/// （净化后的原始文件名快照、媒体类型、字节长度、SHA-256 摘要、上传人、登记时间）。
/// <para>定位：这是仓库内**唯一**的附件（二进制内容）证据模型 —— ERP-045 的
/// <see cref="DocumentAttachmentReference"/> 仍是「仅元数据引用册」，本表在其之外负责
/// **内容本身**的托管与安全下载；本模块**不**引入第二套文件存储，而是通过
/// <c>IAttachmentContentStore</c> 这一唯一内容访问接缝复用既有对象存储约定
/// （生产对象存储客户端 <c>OssStorageService</c> 是唯一既有实现；其在本阶段**未激活**）。</para>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>只保存**服务端权威值**：<see cref="StorageKey"/>（服务端生成的不透明键）、
/// <see cref="Sha256"/>、<see cref="SizeBytes"/>、<see cref="MediaType"/>、
/// <see cref="OwnerNo"/> / <see cref="OwnerTypeText"/> 快照、<see cref="UploadedBy"/>、
/// <see cref="RecordedAt"/> 一律不接受客户端提交；</item>
/// <item>文件内容一律按**不可信文件**处理：只允许 PDF / PNG / JPEG（按文件签名判定），
/// 拒绝可执行 / 脚本 / 标记类格式，下载只以「附件」方式流式返回并附带防御性响应头，
/// <strong>不</strong>在浏览器中内联渲染、<strong>不</strong>转成标记、<strong>不</strong>暴露
/// 可直接猜测的文件系统 / 对象存储路径；</item>
/// <item>上挂 / 读取 / 作废都<strong>不</strong>改写父单据（订单状态、金额、明细、备注）、库存与库存成本、
/// 库存流水、出库 / 装柜 / 单证 / 发票 / 费用与分摊 / 收付款 / 税务与结算记录；</item>
/// <item>更正走**显式作废**（<see cref="Status"/> = 1 已作废 + 作废原因）：保留原始文件名、摘要、
/// 媒体类型、字节长度、上传人与登记时间，<strong>不</strong>提供硬删除、<strong>不</strong>静默替换二进制内容、
/// <strong>不</strong>改派到别的单据；已作废证据仍在历史中可读，但不再提供下载；</item>
/// <item>刻意不建任何外键与导航属性：父单据软删除 / 改名后历史证据必须始终可读，只是由服务端
/// 显式标注可用性，绝不改派；</item>
/// <item>同一摘要的重复上传**不做内容寻址去重**：两次上传是两条各自独立的证据（各有自己的 Id、
/// 存储键与登记时间），系统绝不静默合并或覆盖。</item>
/// </list>
/// </summary>
public class AttachmentEvidence : BaseEntity
{
    /// <summary>归属单据类型（白名单：SalesOrder / PurchaseOrder；常量见 <c>AttachmentEvidenceRules</c>）</summary>
    [Required, MaxLength(30)]
    public string OwnerType { get; set; } = string.Empty;

    /// <summary>归属单据 Id（必须指向存在且未删除的权威单据；本表刻意不建外键，软删除后历史仍可读）</summary>
    public long OwnerId { get; set; }

    /// <summary>归属单据号码快照（服务端权威写入：销售订单号 / 采购单号）</summary>
    [MaxLength(50)]
    public string OwnerNo { get; set; } = string.Empty;

    /// <summary>归属单据类型文案快照（服务端权威写入，如「销售订单」；类型改名后历史保持登记当时口径）</summary>
    [MaxLength(30)]
    public string OwnerTypeText { get; set; } = string.Empty;

    /// <summary>净化后的原始文件名快照（服务端写入：客户端路径一律忽略，控制字符与不安全字符已替换、长度有界）</summary>
    [Required, MaxLength(255)]
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>媒体类型（服务端按文件签名判定，只可能是 application/pdf / image/png / image/jpeg）</summary>
    [Required, MaxLength(120)]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>字节长度（服务端实测内容长度，客户端声明值不落库）</summary>
    public long SizeBytes { get; set; }

    /// <summary>内容 SHA-256 摘要（服务端计算，64 位小写十六进制；只用于内容比对，不作为证据身份）</summary>
    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>证据说明（有界、可选；只作为纯文本标签显示，不做标记渲染）</summary>
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 内容存储键（**服务端生成的不透明键**，不接受客户端提交，也<strong>不</strong>经接口返回）：
    /// 形如 <c>yyyyMMdd/{guid}{ext}</c>，因此不存在「可直接猜测的路径」。
    /// </summary>
    [Required, MaxLength(200)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>内容存储提供程序编码（本阶段只能是隔离的非生产本地存储；生产 OSS 未激活）</summary>
    [MaxLength(30)]
    public string StorageProvider { get; set; } = string.Empty;

    /// <summary>上传人（服务端从当前登录身份写入；缺失时记为「未知用户」，绝不猜测）</summary>
    [MaxLength(100)]
    public string UploadedBy { get; set; } = string.Empty;

    /// <summary>登记时间（服务端权威写入）</summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>状态（0=有效，1=已作废；常量见 <c>AttachmentEvidenceRules</c>）</summary>
    public int Status { get; set; }

    /// <summary>作废时间（服务端权威写入）</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>作废原因（必填：保留原始元数据与内容，必须记录更正原因而不是静默覆盖或删除）</summary>
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>状态文案（有效 / 已作废；**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>媒体类型文案（**非持久化列**）</summary>
    [NotMapped]
    public string MediaTypeText { get; set; } = string.Empty;

    /// <summary>字节长度文案（**非持久化列**）</summary>
    [NotMapped]
    public string SizeText { get; set; } = string.Empty;

    /// <summary>归属单据当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool OwnerAvailable { get; set; }

    /// <summary>归属单据不可用时的说明文案（历史证据照常可读，但不能用于新的上挂 / 下载；**非持久化列**）</summary>
    [NotMapped]
    public string OwnerAvailabilityText { get; set; } = string.Empty;

    /// <summary>证据内容下载接口的相对地址（**非持久化列**：只给出 API 路径，绝不给出存储键或存储路径）</summary>
    [NotMapped]
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>模块边界声明（**非持久化列**：接口与界面同源，声明这不是报关 / 报税 / 银行 / 承运人确认）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}
