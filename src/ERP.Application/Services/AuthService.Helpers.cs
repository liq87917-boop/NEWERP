using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 认证服务辅助方法：构建用户上下文与菜单树
/// </summary>
public partial class AuthService
{
    /// <summary>构建当前用户上下文（含角色、权限）</summary>
    private async Task<CurrentUser> BuildCurrentUserAsync(SysUser user)
    {
        var roleIds = await _db.SysUserRoles
            .Where(ur => ur.UserId == user.Id && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();

        var roles = await _db.SysRoles
            .Where(r => roleIds.Contains(r.Id) && !r.IsDeleted)
            .Select(r => r.RoleCode)
            .ToListAsync();

        var permissionCodes = new List<string>();
        if (roles.Count > 0)
        {
            var roleIdSet = roleIds.ToHashSet();
            var menuIds = await _db.SysRoleMenus
                .Where(rm => roleIdSet.Contains(rm.RoleId) && !rm.IsDeleted)
                .Select(rm => rm.MenuId)
                .ToListAsync();

            permissionCodes = await _db.SysMenus
                .Where(m => menuIds.Contains(m.Id) && !m.IsDeleted && m.MenuType == MenuType.Button)
                .Select(m => m.PermissionCode)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToListAsync();
        }

        return new CurrentUser
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Roles = roles,
            Permissions = permissionCodes
        };
    }

    /// <summary>构建用户可见菜单树</summary>
    private async Task<List<MenuNode>> BuildMenuTreeAsync(long userId)
    {
        var roleIds = await _db.SysUserRoles
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();

        if (roleIds.Count == 0)
            return new List<MenuNode>();

        var roleIdSet = roleIds.ToHashSet();
        var menuIds = await _db.SysRoleMenus
            .Where(rm => roleIdSet.Contains(rm.RoleId) && !rm.IsDeleted)
            .Select(rm => rm.MenuId)
            .ToListAsync();

        var menus = await _db.SysMenus
            .Where(m => menuIds.Contains(m.Id) && !m.IsDeleted && m.MenuType != MenuType.Button)
            .OrderBy(m => m.SortOrder)
            .ToListAsync();

        var nodes = menus.Select(m => new MenuNode
        {
            Id = m.Id,
            ParentId = m.ParentId,
            MenuName = m.MenuName,
            MenuCode = m.MenuCode,
            Path = m.Path,
            Icon = m.Icon,
            SortOrder = m.SortOrder,
            MenuType = (int)m.MenuType
        }).ToList();

        return BuildTree(nodes, 0);
    }

    /// <summary>递归构建菜单树</summary>
    private static List<MenuNode> BuildTree(List<MenuNode> all, long parentId)
    {
        return all
            .Where(n => n.ParentId == parentId)
            .OrderBy(n => n.SortOrder)
            .Select(n =>
            {
                n.Children = BuildTree(all, n.Id);
                return n;
            })
            .ToList();
    }
}
