using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.UnitTests;

/// <summary>
/// 测试身份脚手架（ERP-097）：为「直接实例化控制器」的单元测试注入一个**特权**登录身份，
/// 避免 [Authorize] 边界之外的数据范围解析因缺少身份而 fail closed。
/// <para>特权身份通过系统内置角色（<c>IsSystem == true</c>）实现，与生产端超级管理员 / 系统内置角色同一口径；</para>
/// <para>受限制业务员的身份（登录账号 → 员工编码映射）在 <see cref="SalespersonDataScopeTests"/> 中单独覆盖。</para>
/// </summary>
public static class TestAuth
{
    /// <summary>播种一个特权登录用户（系统内置角色）并返回其用户 Id。</summary>
    public static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "测试特权角色",
            RoleCode = $"Privileged-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "特权测试用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>把指定登录用户 Id（可空 = 无身份）写入控制器的 HttpContext。</summary>
    public static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }
}
