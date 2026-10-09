using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 菜单管理（<c>api/sys/menus</c> 读树 / 新增 / 修改 / 删除）实时授权、有界字段校验与父级完整性单元测试（ERP-455）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无任何菜单授权的身份在<b>全部 4 条路由</b> fail closed，
/// 且 <c>SysMenus</c> 行逐字节不变（拒绝在路由体读取 / 改写任何菜单之前抛错）；</item>
/// <item><b>读 / 写授权分离</b>：读树接受既有「用户权限」（<c>user-permission</c>）或既有「角色管理」（<c>role</c>）任一；
/// 新增 / 修改 / 删除只接受既有「用户权限」；</item>
/// <item><b>放行</b>：具备既有「用户权限」菜单的普通与特权（系统内置角色）身份下，既有读 / 写契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段 / 父级校验</b>：菜单名称 / 编码空值 / 越界、路径 / 图标 / 权限编码越界、未知菜单类型、
/// 父级负数 / 未知 / 已删除一律按受控参数错误拒绝且不落任何行 / 不改写任何行；</item>
/// <item><b>既有契约不变</b>：菜单编码重复仍为 <c>Duplicate</c>、存在子菜单删除仍为 <c>RuleConflict</c>；</item>
/// <item><b>源码契约</b>：控制器 4 条路由全部先授权再读写，且只复用既有菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class SysMenuAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static MenuController Controller(ErpDbContext db, long? userId) =>
        new(db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>
    /// 带（可空）<c>NameIdentifier</c> 的真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：
    /// null = 无身份，真实请求因处于请求管线内一律 fail closed 拒绝。
    /// </summary>
    private static ControllerContext ContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        // 标记为真实 HTTP 路由（Request.Path 已赋值）：缺失身份也必须实时授权并 fail closed。
        http.Request.Path = "/api/sys/menus/tree";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>进程内直调上下文（无请求路径、可带身份）：用于断言历史单元测试口径保持不变。</summary>
    private static ControllerContext InternalContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>
    /// 播种一个独立授权身份，返回用户 Id（每个用例独立）。可选状态 / 删除 / 既有「用户权限」菜单 /
    /// 既有「角色管理」菜单（读树替代授权） / 系统内置角色。
    /// </summary>
    private static long SeedAuthUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantWriteMenu = true, bool grantReadAlternateMenu = false, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"menu-auth-{Guid.NewGuid():N}",
            DisplayName = "菜单授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "菜单授权用例角色",
            RoleCode = $"MenuAuthCase-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantWriteMenu)
            GrantMenu(db, role.Id, SysMenuAuthorizationRules.WriteRequiredMenuCode,
                SysMenuAuthorizationRules.WriteRequiredMenuText);
        if (grantReadAlternateMenu)
            GrantMenu(db, role.Id, SysMenuAuthorizationRules.ReadAlternateMenuCode,
                SysMenuAuthorizationRules.ReadAlternateMenuText);
        return user.Id;
    }

    /// <summary>授予既有功能菜单（幂等；菜单缺失时按既有种子口径补建一条功能菜单）</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        if (!db.SysRoleMenus.Any(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    /// <summary>回收指定账号的既有菜单授权（模拟请求之间撤销权限，不影响其它账号）</summary>
    private static void RevokeMenu(ErpDbContext db, long userId, string menuCode)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        var menuIds = db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode).Select(m => m.Id).ToList();
        foreach (var grant in db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && menuIds.Contains(rm.MenuId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>播种被管理目标菜单（读树 / 修改 / 删除对象）</summary>
    private static SysMenu SeedTargetMenu(ErpDbContext db, string code = "target-menu",
        MenuType type = MenuType.Menu, long parentId = 0, bool deleted = false)
    {
        var menu = new SysMenu
        {
            ParentId = parentId,
            MenuCode = code,
            MenuName = "被管理菜单",
            Path = "/target",
            Icon = "Target",
            SortOrder = 1,
            MenuType = type,
            IsDeleted = deleted
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>菜单主表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) =>
        string.Join("|", db.SysMenus.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => $"{m.Id}:{m.ParentId}:{m.MenuName}:{m.MenuCode}:{m.Path}:{m.Icon}:{m.SortOrder}:{(int)m.MenuType}:{m.PermissionCode}:{m.IsDeleted}")
            .ToList());

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static SysMenu NewMenu(
        string code = "new-menu", string name = "新菜单", long parentId = 0,
        string path = "/new", string icon = "Plus", MenuType type = MenuType.Menu,
        string permissionCode = "sys:menu:create")
        => new()
        {
            MenuCode = code,
            MenuName = name,
            ParentId = parentId,
            Path = path,
            Icon = icon,
            MenuType = type,
            PermissionCode = permissionCode
        };

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 无任何菜单授权的身份：全部 4 条路由在读取或写入任何菜单之前 fail closed，
    /// 且 <c>SysMenus</c> 行逐字节不变（拒绝在路由体读取 / 改写任何菜单之前抛错）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何菜单(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedAuthUser(db, UserStatus.Disabled),
            "deleted" => SeedAuthUser(db, deleted: true),
            _ => SeedAuthUser(db, grantWriteMenu: false)
        };
        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetTree());
        await AssertCode(expectedCode, () => ctl.Create(NewMenu()));
        await AssertCode(expectedCode, () => ctl.Update(target.Id, NewMenu(code: target.MenuCode, name: target.MenuName)));
        await AssertCode(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
        Assert.DoesNotContain(db.SysMenus, m => m.MenuCode == "new-menu");
    }

    /// <summary>缺失身份的真实匿名请求（<c>Request.Path</c> 已赋值）一律 fail closed，绝不把空身份当作管理员。</summary>
    [Fact]
    public async Task Auth_真实匿名请求_全部路由未认证()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var ctl = Controller(db, null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetTree());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewMenu()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(target.Id, NewMenu(code: target.MenuCode)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(target.Id));
    }

    // ==================== 2. 读 / 写授权分离与放行 ====================

    /// <summary>
    /// 既有「用户权限」（<c>user-permission</c>）菜单：读树与新增 / 修改 / 删除既有契约全部放行。
    /// </summary>
    [Fact]
    public async Task Auth_具备用户权限菜单_读写全部放行()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var userId = SeedAuthUser(db);
        var ctl = Controller(db, userId);

        // 读树
        var tree = Data<List<MenuTreeNode>>(await ctl.GetTree());
        Assert.Contains(tree, n => n.MenuCode == target.MenuCode);

        // 新增
        await ctl.Create(NewMenu(code: "menu-created", name: "新建菜单"));
        Assert.Contains(db.SysMenus, m => m.MenuCode == "menu-created");

        // 修改（父级为零）
        await ctl.Update(target.Id, NewMenu(code: target.MenuCode, name: "改名菜单", path: "/renamed"));
        var updated = await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal("改名菜单", updated.MenuName);
        Assert.Equal("/renamed", updated.Path);

        // 删除
        await ctl.Delete(target.Id);
        Assert.True((await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    /// <summary>
    /// 只具备既有「角色管理」（<c>role</c>）菜单：读树放行，但新增 / 修改 / 删除仍 fail closed 且零写入。
    /// </summary>
    [Fact]
    public async Task Auth_仅角色管理菜单_读树放行_写操作拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var userId = SeedAuthUser(db, grantWriteMenu: false, grantReadAlternateMenu: true);
        var ctl = Controller(db, userId);

        var tree = Data<List<MenuTreeNode>>(await ctl.GetTree());
        Assert.Contains(tree, n => n.MenuCode == target.MenuCode);

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewMenu()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(target.Id, NewMenu(code: target.MenuCode, name: target.MenuName)));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(target.Id));
        Assert.Equal(before, Snapshot(db));
    }


    /// <summary>
    /// 特权（系统内置角色）身份：具备既有「用户权限」菜单时放行；缺菜单时读 / 写仍 fail closed
    /// —— 授权口径不因特权跳过菜单检查，也不新增任何用户授权。
    /// </summary>
    [Fact]
    public async Task Auth_特权内置角色_具备菜单放行_缺菜单仍拒绝()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var privilegedUserId = SeedAuthUser(db, privileged: true);
        var privilegedNoMenuId = SeedAuthUser(db, grantWriteMenu: false, privileged: true);

        var tree = Data<List<MenuTreeNode>>(await Controller(db, privilegedUserId).GetTree());
        Assert.Contains(tree, n => n.MenuCode == target.MenuCode);

        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, privilegedNoMenuId).GetTree());
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, privilegedNoMenuId).Create(NewMenu()));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, privilegedNoMenuId).Delete(target.Id));
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    /// <summary>请求之间撤销既有「用户权限」菜单授权：全部 4 条路由下一次请求立即拒绝且零写入。</summary>
    [Fact]
    public async Task Auth_请求之间撤销菜单_下一次请求立即拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var userId = SeedAuthUser(db);

        Data<List<MenuTreeNode>>(await Controller(db, userId).GetTree());          // 授权读取成功
        RevokeMenu(db, userId, SysMenuAuthorizationRules.WriteRequiredMenuCode);    // 撤销授权

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetTree());
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Create(NewMenu()));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Update(target.Id, NewMenu(code: target.MenuCode)));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    /// <summary>读树替代授权（既有「角色管理」菜单）被撤销后，读树下一次请求立即拒绝。</summary>
    [Fact]
    public async Task Auth_撤销读树替代授权_读树立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedTargetMenu(db);
        var userId = SeedAuthUser(db, grantWriteMenu: false, grantReadAlternateMenu: true);

        Data<List<MenuTreeNode>>(await Controller(db, userId).GetTree());          // 替代授权读取成功
        RevokeMenu(db, userId, SysMenuAuthorizationRules.ReadAlternateMenuCode);    // 撤销替代授权

        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetTree());
    }


    // ==================== 4. 有界字段 / 父级校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>授权身份下的非法新增载荷（编码 / 名称空值 / 越界、路径 / 图标 / 权限编码越界、未知菜单类型、父级负数 / 未知 / 已删除）拒绝且零写入。</summary>
    [Theory]
    [InlineData("empty-code")]
    [InlineData("blank-code")]
    [InlineData("code-too-long")]
    [InlineData("empty-name")]
    [InlineData("blank-name")]
    [InlineData("name-too-long")]
    [InlineData("path-too-long")]
    [InlineData("icon-too-long")]
    [InlineData("permission-too-long")]
    [InlineData("unknown-type")]
    [InlineData("negative-parent")]
    [InlineData("unknown-parent")]
    [InlineData("deleted-parent")]
    public async Task Validation_新增非法载荷_拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var deletedMenu = SeedTargetMenu(db, code: "deleted-parent", deleted: true);
        var ctl = Controller(db, userId);

        SysMenu Payload() => scenario switch
        {
            "empty-code" => NewMenu(code: ""),
            "blank-code" => NewMenu(code: "   "),
            "code-too-long" => NewMenu(code: new string('C', SysMenuAuthorizationRules.MaxMenuCodeLength + 1)),
            "empty-name" => NewMenu(name: ""),
            "blank-name" => NewMenu(name: "   "),
            "name-too-long" => NewMenu(name: new string('N', SysMenuAuthorizationRules.MaxMenuNameLength + 1)),
            "path-too-long" => NewMenu(path: "/" + new string('p', SysMenuAuthorizationRules.MaxPathLength)),
            "icon-too-long" => NewMenu(icon: new string('I', SysMenuAuthorizationRules.MaxIconLength + 1)),
            "permission-too-long" => NewMenu(permissionCode: new string('P', SysMenuAuthorizationRules.MaxPermissionCodeLength + 1)),
            "unknown-type" => NewMenu(type: (MenuType)0),
            "negative-parent" => NewMenu(parentId: -1),
            "unknown-parent" => NewMenu(parentId: 999_999),
            _ => NewMenu(parentId: deletedMenu.Id)
        };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, Snapshot(db));
        Assert.DoesNotContain(db.SysMenus, m => m.MenuCode == "new-menu");
    }

    /// <summary>授权身份下的非法修改载荷（编码 / 名称空值 / 越界、路径 / 图标 / 权限编码越界、未知菜单类型、父级负数 / 未知 / 已删除）拒绝且不改写任何既有行。</summary>
    [Theory]
    [InlineData("empty-code")]
    [InlineData("code-too-long")]
    [InlineData("empty-name")]
    [InlineData("name-too-long")]
    [InlineData("path-too-long")]
    [InlineData("icon-too-long")]
    [InlineData("permission-too-long")]
    [InlineData("unknown-type")]
    [InlineData("negative-parent")]
    [InlineData("unknown-parent")]
    [InlineData("deleted-parent")]
    public async Task Validation_修改非法载荷_拒绝且不改写任何行(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var userId = SeedAuthUser(db);
        var deletedMenu = SeedTargetMenu(db, code: "deleted-parent", deleted: true);
        var ctl = Controller(db, userId);

        SysMenu Payload() => scenario switch
        {
            "empty-code" => NewMenu(code: ""),
            "code-too-long" => NewMenu(code: new string('C', SysMenuAuthorizationRules.MaxMenuCodeLength + 1)),
            "empty-name" => NewMenu(code: target.MenuCode, name: ""),
            "name-too-long" => NewMenu(code: target.MenuCode, name: new string('N', SysMenuAuthorizationRules.MaxMenuNameLength + 1)),
            "path-too-long" => NewMenu(code: target.MenuCode, path: "/" + new string('p', SysMenuAuthorizationRules.MaxPathLength)),
            "icon-too-long" => NewMenu(code: target.MenuCode, icon: new string('I', SysMenuAuthorizationRules.MaxIconLength + 1)),
            "permission-too-long" => NewMenu(code: target.MenuCode, permissionCode: new string('P', SysMenuAuthorizationRules.MaxPermissionCodeLength + 1)),
            "unknown-type" => NewMenu(code: target.MenuCode, type: (MenuType)0),
            "negative-parent" => NewMenu(code: target.MenuCode, parentId: -1),
            "unknown-parent" => NewMenu(code: target.MenuCode, parentId: 999_999),
            _ => NewMenu(code: target.MenuCode, parentId: deletedMenu.Id)
        };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, Payload()));
        Assert.Equal(before, Snapshot(db));

        var stored = await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal("被管理菜单", stored.MenuName);
        Assert.Equal("/target", stored.Path);
        Assert.Equal(0, stored.ParentId);
    }


    // ==================== 5. 边界值与父级完整性 ====================

    /// <summary>边界值必须放行：编码 / 名称恰好等于持久化长度上限、路径 / 图标 / 权限编码恰好等于上限；超一位即拒绝。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var ctl = Controller(db, userId);

        var codeAtLimit = new string('C', SysMenuAuthorizationRules.MaxMenuCodeLength);
        var nameAtLimit = new string('N', SysMenuAuthorizationRules.MaxMenuNameLength);
        var pathAtLimit = "/" + new string('p', SysMenuAuthorizationRules.MaxPathLength - 1);
        var iconAtLimit = new string('I', SysMenuAuthorizationRules.MaxIconLength);
        var permissionAtLimit = new string('P', SysMenuAuthorizationRules.MaxPermissionCodeLength);

        await ctl.Create(NewMenu(code: codeAtLimit, name: nameAtLimit, path: pathAtLimit,
            icon: iconAtLimit, permissionCode: permissionAtLimit));

        var created = await db.SysMenus.AsNoTracking().SingleAsync(m => m.MenuCode == codeAtLimit);
        Assert.Equal(nameAtLimit, created.MenuName);
        Assert.Equal(pathAtLimit, created.Path);
        Assert.Equal(iconAtLimit, created.Icon);
        Assert.Equal(permissionAtLimit, created.PermissionCode);
        Assert.Equal(MenuType.Menu, created.MenuType);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewMenu(
            code: new string('C', SysMenuAuthorizationRules.MaxMenuCodeLength + 1))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewMenu(
            code: "over-name", name: new string('N', SysMenuAuthorizationRules.MaxMenuNameLength + 1))));
    }

    /// <summary>父级完整性：零与非零已知非删除父级放行；负数 / 未知 / 已删除父级一律拒绝且零写入。</summary>
    [Fact]
    public async Task Validation_父级完整性_零与已知父级放行_伪造父级拒绝()
    {
        using var db = TestDbFactory.Create();
        var parent = SeedTargetMenu(db, code: "known-parent", type: MenuType.Directory);
        var deletedParent = SeedTargetMenu(db, code: "gone-parent", deleted: true);
        var userId = SeedAuthUser(db);
        var ctl = Controller(db, userId);

        // 根菜单与已知非删除父级放行
        await ctl.Create(NewMenu(code: "root-child", name: "根子菜单", parentId: 0));
        await ctl.Create(NewMenu(code: "nested-child", name: "子菜单", parentId: parent.Id));

        var nested = await db.SysMenus.AsNoTracking().SingleAsync(m => m.MenuCode == "nested-child");
        Assert.Equal(parent.Id, nested.ParentId);

        // 伪造父级：负数 / 未知 / 已删除，一律拒绝且零写入
        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewMenu(code: "bad-1", parentId: -1)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewMenu(code: "bad-2", parentId: 999_999)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewMenu(code: "bad-3", parentId: deletedParent.Id)));
        Assert.Equal(before, Snapshot(db));

        // 修改也不接受伪造父级
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(parent.Id,
            NewMenu(code: parent.MenuCode, name: parent.MenuName, parentId: deletedParent.Id)));
        Assert.Equal(0, (await db.SysMenus.AsNoTracking().SingleAsync(m => m.Id == parent.Id)).ParentId);
    }

    // ==================== 6. 既有契约不变 ====================

    /// <summary>授权身份下的既有契约不变：重复菜单编码仍为 <c>Duplicate</c>；存在子菜单删除仍为 <c>RuleConflict</c>，且零写入。</summary>
    [Fact]
    public async Task Contract_重复编码与子菜单删除保护不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var parent = SeedTargetMenu(db, code: "parent", type: MenuType.Directory);
        SeedTargetMenu(db, code: "child", parentId: parent.Id);
        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewMenu(code: "parent", name: "重复")));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Delete(parent.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysMenus.AsNoTracking().SingleAsync(m => m.Id == parent.Id)).IsDeleted);
    }

    /// <summary>软删除的编码不占用唯一性（与既有「非删除集合」判定同源）：同码可再建一条，软删除行保持不变。</summary>
    [Fact]
    public async Task Contract_软删除编码不占用唯一性()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var deleted = SeedTargetMenu(db, code: "reuse-me", deleted: true);
        var ctl = Controller(db, userId);

        await ctl.Create(NewMenu(code: "reuse-me", name: "复用编码"));

        Assert.True((await db.SysMenus.AsNoTracking().SingleAsync(m => m.Id == deleted.Id)).IsDeleted);
        Assert.Single(db.SysMenus.Where(m => m.MenuCode == "reuse-me" && !m.IsDeleted));
    }


    // ==================== 7. 进程内直调边界与源码 / 菜单契约 ====================

    /// <summary>
    /// 进程内直调边界（与仓库既有口径同源）：未进入 HTTP 请求管线（<c>Request.Path</c> 为空）的历史单元测试 /
    /// 内部派生读取沿用既有语义；真实 HTTP 请求（<c>Request.Path</c> 已赋值）一律实时授权并 fail closed。
    /// </summary>
    [Fact]
    public async Task Auth_进程内直调无请求路径_沿用既有语义()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetMenu(db);
        var ctl = new MenuController(db) { ControllerContext = InternalContextWithUser(null) };

        var tree = Data<List<MenuTreeNode>>(await ctl.GetTree());
        Assert.Contains(tree, n => n.MenuCode == target.MenuCode);
    }

    /// <summary>控制器源码契约：4 条路由全部先经实时授权再读写，且无匿名 / 角色回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "MenuController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureMenuReadAuthorizedAsync\(\);"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureMenuWriteAuthorizedAsync\(\);").Count);
        Assert.Contains("SysMenuAuthorizationRules.Validate", source);
        Assert.Contains("SysMenuAuthorizationRules.EnsureParentResolvedAsync", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>授权口径复用既有菜单（与 SeedData.Menus 同源），不新增任何菜单 / 权限。</summary>
    [Fact]
    public void Auth_复用既有菜单常量_不新增菜单()
    {
        Assert.Equal("user-permission", SysMenuAuthorizationRules.WriteRequiredMenuCode);
        Assert.Equal("用户权限", SysMenuAuthorizationRules.WriteRequiredMenuText);
        Assert.Equal("role", SysMenuAuthorizationRules.ReadAlternateMenuCode);
        Assert.Equal("角色管理", SysMenuAuthorizationRules.ReadAlternateMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{SysMenuAuthorizationRules.WriteRequiredMenuCode}\", \"{SysMenuAuthorizationRules.WriteRequiredMenuText}\"",
            menus);
        Assert.Contains(
            $"(\"system\", \"{SysMenuAuthorizationRules.ReadAlternateMenuCode}\", \"{SysMenuAuthorizationRules.ReadAlternateMenuText}\"",
            menus);
    }

    /// <summary>持久化长度上限与实体 <c>[MaxLength]</c> 同源，不新增 / 不放宽任何列约束。</summary>
    [Fact]
    public void Auth_长度上限与实体注释同源()
    {
        Assert.Equal(50, SysMenuAuthorizationRules.MaxMenuNameLength);
        Assert.Equal(50, SysMenuAuthorizationRules.MaxMenuCodeLength);
        Assert.Equal(200, SysMenuAuthorizationRules.MaxPathLength);
        Assert.Equal(50, SysMenuAuthorizationRules.MaxIconLength);
        Assert.Equal(100, SysMenuAuthorizationRules.MaxPermissionCodeLength);
    }
}
