using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// RoleController 单元测试：角色编码唯一性、系统内置不可删、菜单全删全建、GetRoleMenus。
/// <para>ERP-464：夹具全部以<b>真实的既有启用身份 + 既有「角色管理」（<c>role</c>）菜单授权</b>驱动控制器，
/// 且<b>不设置</b> <c>Request.Path</c>（空路径）—— 证明这些既有业务契约只有在同一套实时授权通过之后才成立，
/// 授权判定与请求路径 / 请求形状完全无关。授权数据只播种在隔离的内存测试库中，
/// 绝不新增任何生产菜单 / 权限 / 用户授权，也没有任何测试专用放行开关。</para>
/// </summary>
public class RoleControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_角色编码重复_抛Duplicate()
    {
        using var db = TestDbFactory.Create();
        SeedRole(db, "admin", "管理员", isSystem: false);
        var ctl = BoundController(db, SeedAuthorizedActor(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(new RoleRequest { RoleCode = "admin", RoleName = "重复", MenuIds = new() }));
        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.SysRoles.Where(r => r.RoleCode == "admin"));
    }

    [Fact]
    public async Task Create_正常创建_分配菜单_菜单关联全部建立()
    {
        using var db = TestDbFactory.Create();
        var (m1, m2, m3) = (SeedMenu(db), SeedMenu(db), SeedMenu(db));
        var ctl = BoundController(db, SeedAuthorizedActor(db));

        var result = await ctl.Create(new RoleRequest
        {
            RoleCode = "sales",
            RoleName = "业务员",
            MenuIds = new List<long> { m1.Id, m2.Id, m3.Id }
        });

        Assert.IsType<OkObjectResult>(result);
        var role = db.SysRoles.Single(r => r.RoleCode == "sales");
        Assert.Equal("sales", role.RoleCode);
        Assert.False(role.IsSystem);

        var links = db.SysRoleMenus.Where(x => x.RoleId == role.Id).ToList();
        Assert.Equal(3, links.Count);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_角色不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = BoundController(db, SeedAuthorizedActor(db));

        await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(999, new RoleRequest { RoleCode = "x", RoleName = "X", MenuIds = new() }));
    }

    [Fact]
    public async Task Update_重新分配菜单_旧菜单关联被全删全建_且不更新RoleCode()
    {
        using var db = TestDbFactory.Create();
        var r = SeedRole(db, "sales", "业务员", isSystem: false);
        var m1 = SeedMenu(db);
        var m2 = SeedMenu(db);
        var m3 = SeedMenu(db);

        // 初始分配 m1、m2
        db.SysRoleMenus.AddRange(
            new SysRoleMenu { RoleId = r.Id, MenuId = m1.Id },
            new SysRoleMenu { RoleId = r.Id, MenuId = m2.Id }
        );
        await db.SaveChangesAsync();

        var ctl = BoundController(db, SeedAuthorizedActor(db));
        await ctl.Update(r.Id, new RoleRequest
        {
            RoleCode = "sales-changed",   // 注意：Update 不允许改 RoleCode
            RoleName = "业务员改名",
            MenuIds = new List<long> { m2.Id, m3.Id }    // 移除 m1、新增 m3
        });

        var role = db.SysRoles.Single(x => x.Id == r.Id);
        Assert.Equal("sales", role.RoleCode);    // 未变
        Assert.Equal("业务员改名", role.RoleName);

        var links = db.SysRoleMenus.Where(x => x.RoleId == r.Id).ToList();
        Assert.Equal(2, links.Count);
        Assert.DoesNotContain(links, x => x.MenuId == m1.Id);
        Assert.Contains(links, x => x.MenuId == m2.Id);
        Assert.Contains(links, x => x.MenuId == m3.Id);
    }

    // ==================== Delete ====================

    [Fact]
    public async Task Delete_系统内置角色_抛RuleConflict_未软删()
    {
        using var db = TestDbFactory.Create();
        var adminRole = SeedRole(db, "admin", "管理员", isSystem: true);
        var ctl = BoundController(db, SeedAuthorizedActor(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(adminRole.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.SysRoles.Single(x => x.Id == adminRole.Id).IsDeleted);
    }

    [Fact]
    public async Task Delete_普通角色_软删除_GetById找不到()
    {
        using var db = TestDbFactory.Create();
        var r = SeedRole(db, "sales", "业务员", isSystem: false);
        var ctl = BoundController(db, SeedAuthorizedActor(db));

        await ctl.Delete(r.Id);
        Assert.True(db.SysRoles.Single(x => x.Id == r.Id).IsDeleted);

        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(r.Id));
    }

    // ==================== GetRoleMenus ====================

    [Fact]
    public async Task GetRoleMenus_返回当前角色已分配的菜单ID集合()
    {
        using var db = TestDbFactory.Create();
        var r = SeedRole(db, "sales", "业务员", isSystem: false);
        var m1 = SeedMenu(db);
        var m2 = SeedMenu(db);
        db.SysRoleMenus.AddRange(
            new SysRoleMenu { RoleId = r.Id, MenuId = m1.Id },
            new SysRoleMenu { RoleId = r.Id, MenuId = m2.Id }
        );
        await db.SaveChangesAsync();

        var ctl = BoundController(db, SeedAuthorizedActor(db));
        var result = await ctl.GetRoleMenus(r.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<long>>>(ok.Value);
        Assert.Equal(2, resp.Data!.Count);
        Assert.Contains(m1.Id, resp.Data);
        Assert.Contains(m2.Id, resp.Data);
    }

    /// <summary>
    /// ERP-464：在隔离测试数据里播种一个<b>既有启用身份 + 既有「角色管理」（<c>role</c>）菜单授权</b>
    /// （真实角色 → 菜单口径，菜单与 <c>SeedData</c> 同码同源，不新增任何生产权限），返回其用户 Id。
    /// </summary>
    private static long SeedAuthorizedActor(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == RoleAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = RoleAuthorizationRules.RequiredMenuCode,
                MenuName = RoleAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        var role = new SysRole
        {
            RoleCode = $"role-entry-{Guid.NewGuid():N}",
            RoleName = "角色管理入口测试角色",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });

        var actor = new SysUser
        {
            UserName = $"role-entry-{Guid.NewGuid():N}",
            DisplayName = "角色管理入口测试账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled,
            MustChangePassword = false
        };
        db.SysUsers.Add(actor);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = actor.Id, RoleId = role.Id });
        db.SaveChanges();
        return actor.Id;
    }

    /// <summary>
    /// ERP-464：把控制器绑定到已播种的既有启用身份。这里<b>刻意不设置</b> <c>Request.Path</c>（空路径），
    /// 证明既有业务契约的放行同样必须经过实时授权，授权判定与请求路径 / 请求形状完全无关。
    /// </summary>
    private static RoleController BoundController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
        };
        return new RoleController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }


    // ==================== 种子辅助 ====================

    private static SysRole SeedRole(ErpDbContext db, string code, string name, bool isSystem)
    {
        var r = new SysRole { RoleCode = code, RoleName = name, IsSystem = isSystem };
        db.SysRoles.Add(r);
        db.SaveChanges();
        return r;
    }

    private static SysMenu SeedMenu(ErpDbContext db)
    {
        var m = new SysMenu
        {
            ParentId = 0,
            MenuCode = $"m-{Guid.NewGuid():N}".Substring(0, 10),
            MenuName = "测试菜单",
            MenuType = MenuType.Menu,
            SortOrder = 0
        };
        db.SysMenus.Add(m);
        db.SaveChanges();
        return m;
    }
}