using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 钉钉通知发送记录：单据业务动作（保存 / 审核 / 销审 / 作废 / 还原 / 删除）推送到钉钉的结果留痕
/// 配置项（Webhook / 加签密钥 / 触发规则 / 消息模板）统一存放于系统参数表 SysParameters（键前缀 DingTalk_）
/// </summary>
public class SysDingTalkLog : BaseEntity
{
    /// <summary>单据类型（菜单编码，如 sales-order）</summary>
    [MaxLength(50)]
    public string BillType { get; set; } = string.Empty;

    /// <summary>单据类型中文名</summary>
    [MaxLength(50)]
    public string BillTypeName { get; set; } = string.Empty;

    /// <summary>单据号</summary>
    [MaxLength(100)]
    public string BillNo { get; set; } = string.Empty;

    /// <summary>动作编码（save / audit / unaudit / void / restore / delete）</summary>
    [MaxLength(30)]
    public string ActionCode { get; set; } = string.Empty;

    /// <summary>动作名称（新增 / 修改 / 审核 / 销审 / 作废 / 还原 / 删除）</summary>
    [MaxLength(30)]
    public string ActionName { get; set; } = string.Empty;

    /// <summary>操作人</summary>
    [MaxLength(50)]
    public string Operator { get; set; } = string.Empty;

    /// <summary>消息标题</summary>
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>消息正文</summary>
    [MaxLength(2000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>消息类型（text / markdown）</summary>
    [MaxLength(20)]
    public string MsgType { get; set; } = "text";

    /// <summary>发送目标 Webhook（脱敏保存）</summary>
    [MaxLength(300)]
    public string Webhook { get; set; } = string.Empty;

    /// <summary>是否发送成功</summary>
    public bool Success { get; set; }

    /// <summary>失败原因</summary>
    [MaxLength(500)]
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>重试次数（手动重发累加）</summary>
    public int RetryCount { get; set; }

    /// <summary>发送时间</summary>
    public DateTime? SentAt { get; set; }
}
