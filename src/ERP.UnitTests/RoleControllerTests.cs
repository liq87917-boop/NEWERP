using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// RoleController 单元测试：角色编码唯一性、系统内置不可删、菜单全删全建、GetRoleMenus
/// </summary>
public class RoleControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_角色编码重复_抛Duplicate()
    {
        using var db = TestDbFactory.Create();
        SeedRole(db, "admin", "管理员", isSystem: false);
        var ctl = new RoleController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(new RoleRequest { RoleCode = "admin", RoleName = "重复", MenuIds = new() }));
        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.SysRoles);
    }

    [Fact]
    public async Task Create_正常创建_分配菜单_菜单关联全部建立()
    {
        using var db = TestDbFactory.Create();
        var (m1, m2, m3) = (SeedMenu(db), SeedMenu(db), SeedMenu(db));
        var ctl = new RoleController(db);

        var result = await ctl.Create(new RoleRequest
        {
            RoleCode = "sales",
            RoleName = "业务员",
            MenuIds = new List<long> { m1.Id, m2.Id, m3.Id }
        });

        Assert.IsType<OkObjectResult>(result);
        var role = db.SysRoles.Single();
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
        var ctl = new RoleController(db);

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

        var ctl = new RoleController(db);
        await ctl.Update(r.Id, new RoleRequest
        {
            RoleCode = "sales-changed",   // 注意：Update 不允许改 RoleCode
            RoleName = "业务员改名",
            MenuIds = new List<long> { m2.Id, m3.Id }    // 移除 m1、新增 m3
        });

        var role = db.SysRoles.Single();
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
        var ctl = new RoleController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(adminRole.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.SysRoles.Single().IsDeleted);
    }

    [Fact]
    public async Task Delete_普通角色_软删除_GetById找不到()
    {
        using var db = TestDbFactory.Create();
        var r = SeedRole(db, "sales", "业务员", isSystem: false);
        var ctl = new RoleController(db);

        await ctl.Delete(r.Id);
        Assert.True(db.SysRoles.Single().IsDeleted);

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

        var ctl = new RoleController(db);
        var result = await ctl.GetRoleMenus(r.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<long>>>(ok.Value);
        Assert.Equal(2, resp.Data!.Count);
        Assert.Contains(m1.Id, resp.Data);
        Assert.Contains(m2.Id, resp.Data);
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