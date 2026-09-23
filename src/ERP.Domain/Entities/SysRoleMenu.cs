using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 角色
/// </summary>
public class SysRole : BaseEntity
{
    /// <summary>角色名称</summary>
    [Required, MaxLength(50)]
    public string RoleName { get; set; } = string.Empty;

    /// <summary>角色编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string RoleCode { get; set; } = string.Empty;

    /// <summary>角色描述</summary>
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>是否系统内置角色（内置不可删除）</summary>
    public bool IsSystem { get; set; }
}

/// <summary>
/// 用户-角色关联
/// </summary>
public class SysUserRole : BaseEntity
{
    /// <summary>用户 Id</summary>
    public long UserId { get; set; }

    /// <summary>角色 Id</summary>
    public long RoleId { get; set; }
}

/// <summary>
/// 菜单/权限项
/// </summary>
public class SysMenu : BaseEntity
{
    /// <summary>父级菜单 Id（根为 0）</summary>
    public long ParentId { get; set; }

    /// <summary>菜单名称</summary>
    [Required, MaxLength(50)]
    public string MenuName { get; set; } = string.Empty;

    /// <summary>菜单编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string MenuCode { get; set; } = string.Empty;

    /// <summary>前端路由路径</summary>
    [MaxLength(200)]
    public string Path { get; set; } = string.Empty;

    /// <summary>图标名称（Lucide 风格）</summary>
    [MaxLength(50)]
    public string Icon { get; set; } = string.Empty;

    /// <summary>排序号</summary>
    public int SortOrder { get; set; }

    /// <summary>菜单类型</summary>
    public MenuType MenuType { get; set; } = MenuType.Menu;

    /// <summary>权限编码（如 sys:user:create）</summary>
    [MaxLength(100)]
    public string PermissionCode { get; set; } = string.Empty;
}

/// <summary>
/// 角色-菜单关联
/// </summary>
public class SysRoleMenu : BaseEntity
{
    /// <summary>角色 Id</summary>
    public long RoleId { get; set; }

    /// <summary>菜单 Id</summary>
    public long MenuId { get; set; }
}
