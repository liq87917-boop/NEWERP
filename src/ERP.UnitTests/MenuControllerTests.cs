using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// MenuController 单元测试：菜单编码唯一性、存在子菜单不能删、GetTree 三层菜单树构建
/// </summary>
public class MenuControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_菜单编码重复_抛Duplicate()
    {
        using var db = TestDbFactory.Create();
        SeedMenu(db, "sys", "系统", MenuType.Directory, parentId: 0);
        var ctl = new MenuController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(new SysMenu
            {
                MenuCode = "sys",
                MenuName = "重复",
                MenuType = MenuType.Directory,
                ParentId = 0
            }));
        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.SysMenus);
    }

    [Fact]
    public async Task Create_正常创建_返回Ok_数据库可查()
    {
        using var db = TestDbFactory.Create();
        var ctl = new MenuController(db);

        var result = await ctl.Create(new SysMenu
        {
            MenuCode = "sys",
            MenuName = "系统",
            MenuType = MenuType.Directory,
            ParentId = 0,
            SortOrder = 1
        });
        Assert.IsType<OkObjectResult>(result);
        Assert.Single(db.SysMenus);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_菜单不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new MenuController(db);

        await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(999, new SysMenu { MenuName = "X" }));
    }

    [Fact]
    public async Task Update_正常更新_所有字段被覆盖_路径与图标也变更()
    {
        using var db = TestDbFactory.Create();
        var m = SeedMenu(db, "sys", "系统", MenuType.Directory, parentId: 0, path: "/old", icon: "old-icon");
        var ctl = new MenuController(db);

        await ctl.Update(m.Id, new SysMenu
        {
            ParentId = 0,
            MenuName = "系统改名",
            Path = "/new",
            Icon = "new-icon",
            SortOrder = 5,
            MenuType = MenuType.Menu,
            PermissionCode = "sys:edit"
        });

        var dbMenu = db.SysMenus.Single();
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
        var ctl = new MenuController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(parent.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.SysMenus.Single(m => m.Id == parent.Id).IsDeleted);
    }

    [Fact]
    public async Task Delete_无子菜单_软删除成功()
    {
        using var db = TestDbFactory.Create();
        var leaf = SeedMenu(db, "user-mgmt", "用户管理", MenuType.Menu, parentId: 0);
        var ctl = new MenuController(db);

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

        var ctl = new MenuController(db);
        var result = await ctl.GetTree();

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<MenuTreeNode>>>(ok.Value);
        var tree = resp.Data!;

        Assert.Equal(2, tree.Count);                              // 两个根：系统 / 销售
        Assert.Equal("sys", tree[0].MenuCode);
        Assert.Equal("sales", tree[1].MenuCode);

        // 系统下两个子菜单：用户管理、客户管理（按 SortOrder）
        Assert.Equal(2, tree[0].Children.Count);
        Assert.Equal("user-mgmt", tree[0].Children[0].MenuCode);
        Assert.Equal("cust-mgmt", tree[0].Children[1].MenuCode);

        // 销售下只有一个子菜单：询价
        Assert.Single(tree[1].Children);
        Assert.Equal("inquiry", tree[1].Children[0].MenuCode);

        // 三级不应再有 Children（叶子）
        Assert.Empty(tree[0].Children[0].Children);
        Assert.Empty(tree[0].Children[1].Children);
        Assert.Empty(tree[1].Children[0].Children);
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
}