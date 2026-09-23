using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 数据库种子数据：初始化管理员、角色、菜单授权
/// </summary>
public static partial class SeedData
{
    /// <summary>超级管理员角色编码</summary>
    public const string AdminRoleCode = "SuperAdmin";

    /// <summary>默认管理员账号</summary>
    public const string AdminUserName = "admin";

    /// <summary>默认管理员初始密码</summary>
    public const string AdminPassword = "Admin@123";

    /// <summary>初始化种子数据（幂等，可重复执行）</summary>
    public static async Task InitializeAsync(ErpDbContext db)
    {
        var adminRole = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == AdminRoleCode);
        if (adminRole is null)
        {
            adminRole = new SysRole
            {
                RoleName = "超级管理员",
                RoleCode = AdminRoleCode,
                Description = "系统内置超级管理员角色，拥有全部权限",
                IsSystem = true,
                CreatedAt = DateTime.Now
            };
            db.SysRoles.Add(adminRole);
            await db.SaveChangesAsync();
        }

        var adminUser = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == AdminUserName);
        if (adminUser is null)
        {
            var salt = PasswordHasher.GenerateSalt();
            adminUser = new SysUser
            {
                UserName = AdminUserName,
                PasswordSalt = salt,
                PasswordHash = PasswordHasher.HashPassword(AdminPassword, salt),
                DisplayName = "系统管理员",
                Email = "admin@erp.local",
                Status = UserStatus.Enabled,
                MustChangePassword = false,
                CreatedAt = DateTime.Now
            };
            db.SysUsers.Add(adminUser);
            await db.SaveChangesAsync();
        }

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == adminUser.Id && ur.RoleId == adminRole.Id))
        {
            db.SysUserRoles.Add(new SysUserRole
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                CreatedAt = DateTime.Now
            });
        }

        await SeedMenusAsync(db);
        await GrantAllMenusToAdminAsync(db, adminRole.Id);
        await SeedParametersAsync(db);
        await SeedDocumentNumberRulesAsync(db);

        await db.SaveChangesAsync();
    }

    /// <summary>为超级管理员授予全部菜单权限</summary>
    private static async Task GrantAllMenusToAdminAsync(ErpDbContext db, long roleId)
    {
        var allMenuIds = await db.SysMenus.Where(m => !m.IsDeleted).Select(m => m.Id).ToListAsync();
        var existingMenuIds = await db.SysRoleMenus
            .Where(rm => rm.RoleId == roleId)
            .Select(rm => rm.MenuId)
            .ToListAsync();

        foreach (var menuId in allMenuIds.Except(existingMenuIds))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId, CreatedAt = DateTime.Now });
        }
    }
}
