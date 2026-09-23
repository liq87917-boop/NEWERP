using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Application.DTOs;

/// <summary>用户列表项（不含密码信息）</summary>
public class SysUserView
{
    public long Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;
    public UserStatus Status { get; set; }
    public DateTime? LastLoginTime { get; set; }
    public string LastLoginIp { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<long> RoleIds { get; set; } = new();
    public List<string> RoleNames { get; set; } = new();
}

/// <summary>创建用户请求</summary>
public class SysUserCreateRequest
{
    [Required(ErrorMessage = "用户名不能为空")]
    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "初始密码不能为空")]
    [MinLength(6, ErrorMessage = "密码长度不能少于 6 位")]
    public string Password { get; set; } = string.Empty;

    [MaxLength(50)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(30)]
    public string Phone { get; set; } = string.Empty;

    public List<long> RoleIds { get; set; } = new();
}

/// <summary>更新用户请求</summary>
public class SysUserUpdateRequest
{
    [MaxLength(50)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(30)]
    public string Phone { get; set; } = string.Empty;

    public UserStatus Status { get; set; } = UserStatus.Enabled;

    public List<long> RoleIds { get; set; } = new();
}

/// <summary>重置密码请求</summary>
public class ResetPasswordRequest
{
    [Required(ErrorMessage = "新密码不能为空")]
    [MinLength(6, ErrorMessage = "密码长度不能少于 6 位")]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>角色创建/更新请求</summary>
public class RoleRequest
{
    [Required(ErrorMessage = "角色名称不能为空")]
    [MaxLength(50)]
    public string RoleName { get; set; } = string.Empty;

    [Required(ErrorMessage = "角色编码不能为空")]
    [MaxLength(50)]
    public string RoleCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public List<long> MenuIds { get; set; } = new();
}
