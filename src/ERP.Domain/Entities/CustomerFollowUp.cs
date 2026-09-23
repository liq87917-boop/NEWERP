using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 客户跟进记录（CRM）
/// 用途：记录与客户的每一次接触（电话 / 微信 / 邮件 / 拜访 / 展会 / 寄样），
///       含跟进结果与**下次跟进日期**，解决"客户跟到哪一步说不清、该回访的没人管"的问题。
/// 说明：单表结构（不涉及主子表与存储过程），使用通用 CRUD 维护，可按客户/跟进人/日期检索。
/// </summary>
public class CustomerFollowUp : BaseEntity
{
    /// <summary>跟进编号（可自行编号，如 FU20260918001）</summary>
    [Required, MaxLength(50)]
    public string FollowNo { get; set; } = string.Empty;

    /// <summary>跟进日期</summary>
    public DateTime FollowDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余，便于直接查看与导出）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>跟进方式（电话 / 微信 / 邮件 / 面谈拜访 / 展会 / 寄样 / 其他）</summary>
    [MaxLength(30)]
    public string FollowType { get; set; } = string.Empty;

    /// <summary>对接人（客户方联系人）</summary>
    [MaxLength(100)]
    public string ContactPerson { get; set; } = string.Empty;

    /// <summary>跟进人 Id（业务员）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>跟进人姓名（冗余）</summary>
    [MaxLength(50)]
    public string SalesmanName { get; set; } = string.Empty;

    /// <summary>跟进主题（如「圣诞饰品 5000 个报价跟进」）</summary>
    [MaxLength(100)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>跟进内容 / 会谈纪要</summary>
    [MaxLength(1000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>跟进结果（有意向 / 待跟进 / 已报价 / 已成交 / 已放弃）</summary>
    [MaxLength(30)]
    public string Result { get; set; } = string.Empty;

    /// <summary>下次跟进日期（到期可用「库存/跟进提醒」类报表筛出）</summary>
    public DateTime? NextFollowDate { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
