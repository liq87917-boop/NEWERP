using System.ComponentModel.DataAnnotations;

namespace ERP.Application.DTOs;

/// <summary>
/// 登录请求
/// </summary>
public class LoginRequest
{
    /// <summary>登录账号</summary>
    [Required(ErrorMessage = "用户名不能为空")]
    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>登录密码</summary>
    [Required(ErrorMessage = "密码不能为空")]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 登录响应
/// </summary>
public class LoginResponse
{
    /// <summary>访问令牌</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>令牌有效期（秒）</summary>
    public int ExpiresIn { get; set; }

    /// <summary>用户 Id</summary>
    public long UserId { get; set; }

    /// <summary>登录账号</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>显示姓名</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否首次登录需修改密码</summary>
    public bool MustChangePassword { get; set; }
}

/// <summary>
/// 修改密码请求
/// </summary>
public class ChangePasswordRequest
{
    /// <summary>原密码</summary>
    [Required(ErrorMessage = "原密码不能为空")]
    public string OldPassword { get; set; } = string.Empty;

    /// <summary>新密码</summary>
    [Required(ErrorMessage = "新密码不能为空")]
    [MinLength(6, ErrorMessage = "新密码长度不能少于 6 位")]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// 当前用户信息响应
/// </summary>
public class UserProfileResponse
{
    /// <summary>用户 Id</summary>
    public long Id { get; set; }

    /// <summary>登录账号</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>显示姓名</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>邮箱</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>头像</summary>
    public string Avatar { get; set; } = string.Empty;

    /// <summary>角色编码集合</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>权限编码集合</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>菜单树</summary>
    public List<MenuNode> Menus { get; set; } = new();
}

/// <summary>
/// 菜单节点（用于前端渲染 Sidebar）
/// </summary>
public class MenuNode
{
    public long Id { get; set; }
    public long ParentId { get; set; }
    public string MenuName { get; set; } = string.Empty;
    public string MenuCode { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int MenuType { get; set; }
    public List<MenuNode> Children { get; set; } = new();
}
