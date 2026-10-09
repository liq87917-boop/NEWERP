using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 用户管理控制器：角色关联辅助方法
/// </summary>
public partial class SysUserController
{
    /// <summary>
    /// 写入用户的角色关联（ERP-453：任何角色关联写入前都必须把角色 Id 解析为<b>已知的非删除</b>角色，
    /// 否则整批按受控参数错误拒绝，绝不接受任意 <c>roleId</c>、也不留下任何半写关联）。
    /// </summary>
    private async Task SaveRolesAsync(long userId, List<long> roleIds)
    {
        await SysUserAuthorizationRules.EnsureRoleIdsResolvedAsync(_db, roleIds);

        var existing = await _db.SysUserRoles.Where(ur => ur.UserId == userId).ToListAsync();
        _db.SysUserRoles.RemoveRange(existing);
        foreach (var roleId in roleIds.Distinct())
            _db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId, CreatedAt = DateTime.Now });
        await _db.SaveChangesAsync();
    }

    private async Task<Dictionary<long, List<(long, string)>>> GetRoleMapAsync(List<long> userIds)
    {
        var result = new Dictionary<long, List<(long, string)>>();
        if (userIds.Count == 0) return result;
        var links = await _db.SysUserRoles.Where(ur => userIds.Contains(ur.UserId) && !ur.IsDeleted).ToListAsync();
        var roleIds = links.Select(l => l.RoleId).Distinct().ToList();
        var roles = await _db.SysRoles.Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.RoleName);
        foreach (var g in links.GroupBy(l => l.UserId))
            result[g.Key] = g.Select(l => (l.RoleId, roles.TryGetValue(l.RoleId, out var n) ? n : "")).ToList();
        return result;
    }
}
