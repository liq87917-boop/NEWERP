using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// MenuController 单元测试：菜单编码唯一性、存在子菜单不能删、GetTree 三层菜单树构建。
/// <para>ERP-492：授权在每一个入口<b>无条件</b>执行（与 <c>Request.Path</c> 是否赋值、请求形状、
/// 是否绑定 <c>HttpContext</c> 完全无关），因此进程内直调夹具一律绑定隔离内存测试库中的
/// <b>真实启用身份</b>，且该身份持有既有「用户权限」（<c>user-permission</c>）菜单授权
/// （授予口径与生产端「角色 → 菜单」完全同源）。菜单 / 角色 / 账号只播种在隔离测试数据里，
/// 绝不新增或修改任何生产授权，也不使用任何测试专用开关。</para>
/// </summary>
public class MenuControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_菜单编码重复_抛Duplicate()
    {
        using var db = TestDbFactory.Create();
        SeedMenu(db, "sys", "系统", MenuType.Directory, parentId: 0);
        var ctl = Controller(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(new SysMenu
            {
                MenuCode = "sys",
                MenuName = "重复",
                MenuType = MenuType.Directory,
                ParentId = 0
            }));
        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.SysMenus.Where(m => m.MenuCode == "sys"));
    }

    [Fact]
    public async Task Create_正常创建_返回Ok_数据库可查()
    {
        using var db = TestDbFactory.Create();
        var ctl = Controller(db);

        var result = await ctl.Create(new SysMenu
        {
            MenuCode = "sys",
            MenuName = "系统",
            MenuType = MenuType.Directory,
            ParentId = 0,
            SortOrder = 1
        });
        Assert.IsType<OkObjectResult>(result);
        Assert.Single(db.SysMenus.Where(m => m.MenuCode == "sys"));
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_菜单不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = Controller(db);

        await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(999, new SysMenu { MenuName = "X" }));
    }

    [Fact]
    public async Task Update_正常更新_所有字段被覆盖_路径与图标也变更()
    {
        using var db = TestDbFactory.Create();
        var m = SeedMenu(db, "sys", "系统", MenuType.Directory, parentId: 0, path: "/old", icon: "old-icon");
        var ctl = Controller(db);

        await ctl.Update(m.Id, new SysMenu
        {
            // ERP-455：修改载荷仍须携带非空且受长度约束的菜单编码（Update 不改写 MenuCode 本身）。
            MenuCode = m.MenuCode,
            ParentId = 0,
            MenuName = "系统改名",
            Path = "/new",
            Icon = "new-icon",
            SortOrder = 5,
            MenuType = MenuType.Menu,
            PermissionCode = "sys:edit"
        });

        var dbMenu = db.SysMenus.Single(x => x.Id == m.Id);
        Assert.Equal("系统改名", dbMenu.MenuName);
        Assert.Equal("/new", dbMenu.Path);
        Assert.Equal("new-icon", dbMenu.Icon);
        Assert.Equal(5, dbMenu.SortOrder);
        Assert.Equal(MenuType.Menu, dbMenu.MenuType);
        Assert.Equal("sys:edit", dbMenu.PermissionCode);
    }

    // ==================== Delete ====================

    [Fact]
    public async Task Delete_存在子菜单_抛RuleConflict_未删除()
    {
        using var db = TestDbFactory.Create();
        var parent = SeedMenu(db, "sys", "系统", MenuType.Directory, parentId: 0);
        SeedMenu(db, "user-mgmt", "用户管理", MenuType.Menu, parentId: parent.Id);
        var ctl = Controller(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(parent.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.SysMenus.Single(m => m.Id == parent.Id).IsDeleted);
    }

    [Fact]
    public async Task Delete_无子菜单_软删除成功()
    {
        using var db = TestDbFactory.Create();
        var leaf = SeedMenu(db, "user-mgmt", "用户管理", MenuType.Menu, parentId: 0);
        var ctl = Controller(db);

        var result = await ctl.Delete(leaf.Id);
        Assert.IsType<OkObjectResult>(result);
        Assert.True(db.SysMenus.Single(m => m.Id == leaf.Id).IsDeleted);
    }

    // ==================== GetTree ====================

    [Fact]
    public async Task GetTree_三层菜单_返回正确层级_按SortOrder排序()
    {
        using var db = TestDbFactory.Create();
        // 根：目录
        var sys = SeedMenu(db, "sys", "系统", MenuType.Directory, parentId: 0, sortOrder: 1);
        var sales = SeedMenu(db, "sales", "销售", MenuType.Directory, parentId: 0, sortOrder: 2);
        // 二级：菜单
        var userMgmt = SeedMenu(db, "user-mgmt", "用户管理", MenuType.Menu, parentId: sys.Id, sortOrder: 1);
        var custMgmt = SeedMenu(db, "cust-mgmt", "客户管理", MenuType.Menu, parentId: sys.Id, sortOrder: 2);
        // 三级：菜单（在 sales 下）
        SeedMenu(db, "inquiry", "询价", MenuType.Menu, parentId: sales.Id, sortOrder: 1);

        var ctl = Controller(db);
        var result = await ctl.GetTree();

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<MenuTreeNode>>>(ok.Value);
        var tree = resp.Data!;

        // ERP-492：既有授权夹具另播种既有「用户权限」菜单（真实菜单表中本就有该行），按编码定位被测层级。
        var sysNode = tree.Single(n => n.MenuCode == "sys");
        var salesNode = tree.Single(n => n.MenuCode == "sales");

        // 根层仍按 SortOrder 排序（系统 → 销售）
        var orderedRoots = tree.Where(n => n.MenuCode is "sys" or "sales").ToList();
        Assert.Equal("sys", orderedRoots[0].MenuCode);
        Assert.Equal("sales", orderedRoots[1].MenuCode);

        // 系统下两个子菜单：用户管理、客户管理（按 SortOrder）
        Assert.Equal(2, sysNode.Children.Count);
        Assert.Equal("user-mgmt", sysNode.Children[0].MenuCode);
        Assert.Equal("cust-mgmt", sysNode.Children[1].MenuCode);

        // 销售下只有一个子菜单：询价
        var inquiry = Assert.Single(salesNode.Children);
        Assert.Equal("inquiry", inquiry.MenuCode);

        // 三级不应再有 Children（叶子）
        Assert.Empty(sysNode.Children[0].Children);
        Assert.Empty(sysNode.Children[1].Children);
        Assert.Empty(inquiry.Children);

        // 层级归属与排序仍与既有语义一致（被授权夹具绑定的既有「用户权限」为独立根节点，不影响以上断言）
        Assert.Equal(sys.Id, sysNode.Id);
        Assert.Equal(sales.Id, salesNode.Id);
        Assert.Equal(userMgmt.Id, sysNode.Children[0].Id);
        Assert.Equal(custMgmt.Id, sysNode.Children[1].Id);
    }

    // ==================== 种子辅助 ====================

    private static SysMenu SeedMenu(ErpDbContext db, string code, string name, MenuType type,
        long parentId = 0, int sortOrder = 0, string path = "/x", string icon = "")
    {
        var m = new SysMenu
        {
            ParentId = parentId,
            MenuCode = code,
            MenuName = name,
            MenuType = type,
            SortOrder = sortOrder,
            Path = path,
            Icon = icon
        };
        db.SysMenus.Add(m);
        db.SaveChanges();
        return m;
    }

    /// <summary>
    /// ERP-492：为进程内直调夹具绑定一个真实启用身份 —— 该身份持有隔离内存测试库中的既有「用户权限」
    /// （<c>user-permission</c>）菜单授权。夹具<b>不设置 <c>Request.Path</c></b>（默认空路径），
    /// 以证明授权与请求路径 / 请求形状 / 是否绑定请求上下文完全无关。
    /// 菜单 / 角色 / 账号只播种在隔离测试数据里，绝不新增或修改任何生产授权。
    /// </summary>
    private static MenuController Controller(ErpDbContext db)
    {
        var ctl = new MenuController(db);
        TestAuth.SetUser(ctl, SeedMenuOperator(db));
        return ctl;
    }

    /// <summary>播种启用账号 + 既有「用户权限」菜单授权的角色（与生产端「角色 → 菜单」授予口径同源）。</summary>
    private static long SeedMenuOperator(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == SysMenuAuthorizationRules.WriteRequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuCode = SysMenuAuthorizationRules.WriteRequiredMenuCode,
                MenuName = SysMenuAuthorizationRules.WriteRequiredMenuText,
                Path = "/system/user-permission",
                SortOrder = 99,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        var role = new SysRole
        {
            RoleCode = $"MenuController-{Guid.NewGuid():N}",
            RoleName = "菜单管理单元测试角色"
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"menu-ctl-{Guid.NewGuid():N}",
            DisplayName = "菜单管理单元测试账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }
}