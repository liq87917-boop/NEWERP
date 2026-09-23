using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 系统用户
/// </summary>
public class SysUser : BaseEntity
{
    /// <summary>登录账号</summary>
    [Required, MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>密码哈希（PBKDF2）</summary>
    [Required, MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>密码盐值</summary>
    [Required, MaxLength(64)]
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>显示姓名</summary>
    [MaxLength(50)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>邮箱</summary>
    [MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    /// <summary>手机号</summary>
    [MaxLength(30)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>头像地址</summary>
    [MaxLength(500)]
    public string Avatar { get; set; } = string.Empty;

    /// <summary>状态：启用/禁用</summary>
    public UserStatus Status { get; set; } = UserStatus.Enabled;

    /// <summary>最近登录时间</summary>
    public DateTime? LastLoginTime { get; set; }

    /// <summary>最近登录 IP</summary>
    [MaxLength(64)]
    public string LastLoginIp { get; set; } = string.Empty;

    /// <summary>首次登录是否必须修改密码</summary>
    public bool MustChangePassword { get; set; }
}
