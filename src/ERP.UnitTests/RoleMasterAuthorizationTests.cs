using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 角色管理（<c>api/sys/roles</c>）实时授权、有界字段校验与受控菜单分配单元测试（ERP-454）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「角色管理」（<c>role</c>）菜单的身份在
/// <b>全部 7 条路由</b>（分页 / 全部 / 按主键 / 角色菜单 / 新增 / 修改 / 删除）fail closed，
/// 且 <c>SysRoles</c> + <c>SysRoleMenus</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「角色管理」菜单的普通与特权（系统内置角色）身份下，既有读 / 写契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段 / 菜单校验</b>：角色名称 / 编码空值 / 越界、描述越界、菜单 Id 为负数 / 未知 / 已删除
/// 一律按受控参数错误拒绝且不落任何行 / 不改写任何行，伪造菜单 Id 绝不落 <c>SysRoleMenus</c> 关联；</item>
/// <item><b>既有契约不变</b>：角色编码唯一性（<c>Duplicate</c>）、系统内置角色删除保护（<c>RuleConflict</c>）
/// 与「全删全建」菜单替换语义不变；</item>
/// <item><b>源码契约</b>：控制器 7 条路由 + 角色菜单批量写入入口全部先授权再读写，且只复用既有 <c>role</c> 菜单，不新增菜单；</item>
/// <item><b>路径无关</b>（ERP-464）：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class RoleMasterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static RoleController Controller(ErpDbContext db, long? userId) =>
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
        http.Request.Path = "/api/sys/roles";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>空路径上下文（<c>Request.Path</c> 未赋值、可带身份）：ERP-464 断言空路径与已赋值路径口径完全一致。</summary>
    private static ControllerContext InternalContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 角色管理菜单 / 系统内置角色），返回用户 Id（每个用例独立）</summary>
    private static long SeedAuthUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantRoleMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"role-auth-{Guid.NewGuid():N}",
            DisplayName = "角色授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "角色授权用例角色",
            RoleCode = $"RoleAuthCase-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantRoleMenu)
            GrantMenu(db, role.Id, RoleAuthorizationRules.RequiredMenuCode, RoleAuthorizationRules.RequiredMenuText);
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

    /// <summary>回收指定账号的既有「角色管理」菜单授权（模拟请求之间撤销权限，不影响其它账号）</summary>
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

    /// <summary>播种被管理目标角色（分页 / 详情 / 角色菜单 / 修改 / 删除对象）</summary>
    private static SysRole SeedTargetRole(ErpDbContext db, string code = "sales", bool isSystem = false)
    {
        var role = new SysRole { RoleName = "被管理角色", RoleCode = code, Description = "描述", IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    /// <summary>播种一个菜单（可选软删除），用于角色菜单分配校验</summary>
    private static SysMenu SeedMenu(ErpDbContext db, bool deleted = false)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = $"m-{Guid.NewGuid():N}".Substring(0, 12),
            MenuName = "测试菜单",
            MenuType = MenuType.Menu,
            SortOrder = 0,
            IsDeleted = deleted
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static RoleRequest NewRequest(
        string name = "授权用例角色", string code = "role-case", string description = "描述",
        List<long>? menuIds = null)
        => new()
        {
            RoleName = name,
            RoleCode = code,
            Description = description,
            MenuIds = menuIds ?? new List<long>()
        };

    /// <summary>角色主表 + 角色菜单关联表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) =>
        string.Join("|", db.SysRoles.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"{x.Id}:{x.RoleCode}:{x.RoleName}:{x.Description}:{x.IsSystem}:{x.IsDeleted}").ToList())
        + "##" + string.Join("|", db.SysRoleMenus.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"{x.Id}:{x.RoleId}:{x.MenuId}:{x.IsDeleted}").ToList());

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

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有角色菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「角色管理」菜单的身份：全部 7 条路由
    /// 在读取或写入任何角色之前 fail closed，且 <c>SysRoles</c> + <c>SysRoleMenus</c> 逐字节不变。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何角色(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetRole(db);
        SeedMenu(db);   // 供菜单分配使用（授权失败时也绝不落关联）

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedAuthUser(db, UserStatus.Disabled),
            "deleted" => SeedAuthUser(db, deleted: true),
            _ => SeedAuthUser(db, grantRoleMenu: false)
        };
        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetAll());
        await AssertCode(expectedCode, () => ctl.GetById(target.Id));
        await AssertCode(expectedCode, () => ctl.GetRoleMenus(target.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewRequest()));
        await AssertCode(expectedCode, () => ctl.Update(target.Id, NewRequest(code: "sales")));
        await AssertCode(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.False(stored.IsDeleted);
        Assert.DoesNotContain(db.SysRoles, r => r.RoleCode == "role-case");
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    /// <summary>具备既有「角色管理」菜单的普通授权身份：既有读 / 写契约（含全删全建菜单替换）放行。</summary>
    [Fact]
    public async Task Auth_菜单授权身份_既有读写契约放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var target = SeedTargetRole(db);
        var m1 = SeedMenu(db);
        var m2 = SeedMenu(db);
        var ctl = Controller(db, userId);

        // 分页 / 全部 / 详情
        var page = Data<PagedResult<SysRole>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Contains(page.Items, r => r.Id == target.Id);
        Assert.Contains(Data<List<SysRole>>(await ctl.GetAll()), r => r.Id == target.Id);
        Assert.Equal(target.Id, Data<SysRole>(await ctl.GetById(target.Id)).Id);

        // 角色菜单（初始为空）
        Assert.Empty(Data<List<long>>(await ctl.GetRoleMenus(target.Id)));

        // 新增（含菜单分配）
        await ctl.Create(NewRequest(code: "newbie", menuIds: new List<long> { m1.Id, m2.Id }));
        var created = await db.SysRoles.AsNoTracking().SingleAsync(r => r.RoleCode == "newbie");
        Assert.False(created.IsSystem);
        Assert.Equal(2, await db.SysRoleMenus.AsNoTracking().CountAsync(x => x.RoleId == created.Id));

        // 修改（全删全建；角色编码不变）
        await ctl.Update(target.Id, NewRequest(name: "改名", code: "sales-changed", menuIds: new List<long> { m2.Id }));
        var updated = await db.SysRoles.AsNoTracking().SingleAsync(r => r.Id == target.Id);
        Assert.Equal("改名", updated.RoleName);
        Assert.Equal("sales", updated.RoleCode);
        var links = await db.SysRoleMenus.AsNoTracking().Where(x => x.RoleId == target.Id).ToListAsync();
        Assert.Single(links);
        Assert.Equal(m2.Id, links[0].MenuId);

        // 角色菜单可读回
        Assert.Equal(new[] { m2.Id }, Data<List<long>>(await ctl.GetRoleMenus(target.Id)));

        // 删除（软删除）
        await ctl.Delete(target.Id);
        Assert.True((await db.SysRoles.AsNoTracking().SingleAsync(r => r.Id == target.Id)).IsDeleted);
    }

    /// <summary>
    /// 特权（系统内置角色）身份：具备既有「角色管理」菜单时既有只读契约放行；
    /// 授权口径不因特权而跳过菜单检查（特权但缺菜单仍 fail closed），也不新增任何授权。
    /// </summary>
    [Fact]
    public async Task Auth_特权内置角色_具备菜单放行_缺菜单仍拒绝()
    {
        using var db = TestDbFactory.Create();
        var privilegedUserId = SeedAuthUser(db, privileged: true);
        var privilegedNoMenuId = SeedAuthUser(db, grantRoleMenu: false, privileged: true);
        var target = SeedTargetRole(db);

        var all = Data<List<SysRole>>(await Controller(db, privilegedUserId).GetAll());
        Assert.Contains(all, r => r.Id == target.Id);

        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, privilegedNoMenuId).GetAll());
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    /// <summary>请求之间撤销既有「角色管理」菜单授权：所有 7 条路由下一次请求立即拒绝且零写入。</summary>
    [Fact]
    public async Task Auth_请求之间撤销菜单_下一次请求立即拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var target = SeedTargetRole(db);
        SeedMenu(db);

        Data<List<SysRole>>(await Controller(db, userId).GetAll());                      // 授权读取成功
        RevokeMenu(db, userId, RoleAuthorizationRules.RequiredMenuCode);                   // 撤销授权

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetById(target.Id));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetRoleMenus(target.Id));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Create(NewRequest()));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Update(target.Id, NewRequest(code: "sales")));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
        Assert.DoesNotContain(db.SysRoles, r => r.RoleCode == "role-case");
    }

    /// <summary>
    /// ERP-464 路径无关边界：空路径（未设置 <c>Request.Path</c>）与已赋值路径对同一身份给出完全一致的判定 ——
    /// 缺失 / 已删除身份一律未认证，禁用 / 缺菜单身份一律权限不足，且拒绝后零写入。
    /// </summary>
    [Theory]
    [InlineData("empty", "missing", ErrorCodes.Unauthorized)]
    [InlineData("populated", "missing", ErrorCodes.Unauthorized)]
    [InlineData("empty", "deleted", ErrorCodes.Unauthorized)]
    [InlineData("populated", "deleted", ErrorCodes.Unauthorized)]
    [InlineData("empty", "disabled", ErrorCodes.Forbidden)]
    [InlineData("populated", "disabled", ErrorCodes.Forbidden)]
    [InlineData("empty", "no-menu", ErrorCodes.Forbidden)]
    [InlineData("populated", "no-menu", ErrorCodes.Forbidden)]
    public async Task Auth_空路径与已赋值路径口径完全一致(string pathMode, string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetRole(db);
        long? userId = scenario switch
        {
            "missing" => null,
            "deleted" => SeedAuthUser(db, deleted: true),
            "disabled" => SeedAuthUser(db, UserStatus.Disabled),
            _ => SeedAuthUser(db, grantRoleMenu: false)
        };
        var ctl = pathMode == "populated"
            ? Controller(db, userId)
            : new RoleController(db) { ControllerContext = InternalContextWithUser(userId) };
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetAll());
        await AssertCode(expectedCode, () => ctl.GetById(target.Id));
        await AssertCode(expectedCode, () => ctl.GetRoleMenus(target.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewRequest()));
        await AssertCode(expectedCode, () => ctl.Update(target.Id, NewRequest(code: "sales")));
        await AssertCode(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    /// <summary>
    /// 完全未绑定 <c>HttpContext</c>（纯进程内直调，外部请求无法到达）：无法解析任何身份，
    /// 读取 / 写入入口一律按未认证拒绝且零写入 —— 绝不因「没有请求上下文 / 没有路径」而放行。
    /// </summary>
    [Fact]
    public async Task Auth_未绑定HTTP上下文_一律未认证且零写入()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetRole(db);
        SeedAuthUser(db);
        var ctl = new RoleController(db);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(target.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetRoleMenus(target.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewRequest()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(target.Id, NewRequest(code: "sales")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
    }

    /// <summary>空路径（未设置 <c>Request.Path</c>）下，具备既有 <c>role</c> 菜单的真实启用身份照常放行既有读契约。</summary>
    [Fact]
    public async Task Auth_空路径_具备既有菜单身份放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var target = SeedTargetRole(db);
        var ctl = new RoleController(db) { ControllerContext = InternalContextWithUser(userId) };

        var page = Data<PagedResult<SysRole>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Contains(page.Items, r => r.Id == target.Id);
    }

    // ==================== 4. 有界字段 / 菜单校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>授权身份下的非法新增载荷（名称 / 编码空值 / 越界、描述越界、菜单 Id 负数 / 未知 / 已删除）拒绝且零写入。</summary>
    [Theory]
    [InlineData("empty-name")]
    [InlineData("blank-name")]
    [InlineData("name-too-long")]
    [InlineData("empty-code")]
    [InlineData("blank-code")]
    [InlineData("code-too-long")]
    [InlineData("description-too-long")]
    [InlineData("negative-menu")]
    [InlineData("unknown-menu")]
    [InlineData("deleted-menu")]
    public async Task Validation_新增非法载荷_拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedTargetRole(db);
        var userId = SeedAuthUser(db);
        var deletedMenu = SeedMenu(db, deleted: true);
        var ctl = Controller(db, userId);

        RoleRequest Payload() => scenario switch
        {
            "empty-name" => NewRequest(name: ""),
            "blank-name" => NewRequest(name: "   "),
            "name-too-long" => NewRequest(name: new string('N', RoleAuthorizationRules.MaxRoleNameLength + 1)),
            "empty-code" => NewRequest(code: ""),
            "blank-code" => NewRequest(code: "   "),
            "code-too-long" => NewRequest(code: new string('C', RoleAuthorizationRules.MaxRoleCodeLength + 1)),
            "description-too-long" => NewRequest(description: new string('D', RoleAuthorizationRules.MaxDescriptionLength + 1)),
            "negative-menu" => NewRequest(menuIds: new List<long> { -1 }),
            "unknown-menu" => NewRequest(menuIds: new List<long> { 999_999 }),
            _ => NewRequest(menuIds: new List<long> { deletedMenu.Id })
        };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, Snapshot(db));
        Assert.DoesNotContain(db.SysRoles, r => r.RoleCode == "role-case");
    }

    /// <summary>授权身份下的非法修改载荷（名称 / 编码越界、描述越界、菜单 Id 负数 / 未知 / 已删除）拒绝且不改写任何既有行。</summary>
    [Theory]
    [InlineData("name-too-long")]
    [InlineData("code-too-long")]
    [InlineData("description-too-long")]
    [InlineData("negative-menu")]
    [InlineData("unknown-menu")]
    [InlineData("deleted-menu")]
    public async Task Validation_修改非法载荷_拒绝且不改写任何行(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTargetRole(db);
        var existingMenu = SeedMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = target.Id, MenuId = existingMenu.Id });
        db.SaveChanges();
        var userId = SeedAuthUser(db);
        var deletedMenu = SeedMenu(db, deleted: true);
        var ctl = Controller(db, userId);

        RoleRequest Payload() => scenario switch
        {
            "name-too-long" => NewRequest(name: new string('N', RoleAuthorizationRules.MaxRoleNameLength + 1), code: "sales"),
            "code-too-long" => NewRequest(code: new string('C', RoleAuthorizationRules.MaxRoleCodeLength + 1)),
            "description-too-long" => NewRequest(code: "sales", description: new string('D', RoleAuthorizationRules.MaxDescriptionLength + 1)),
            "negative-menu" => NewRequest(code: "sales", menuIds: new List<long> { -1 }),
            "unknown-menu" => NewRequest(code: "sales", menuIds: new List<long> { 999_999 }),
            _ => NewRequest(code: "sales", menuIds: new List<long> { deletedMenu.Id })
        };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, Payload()));
        Assert.Equal(before, Snapshot(db));

        // 既有菜单关联未被「全删全建」清空，角色字段未被改写。
        Assert.Single(db.SysRoleMenus.Where(x => x.RoleId == target.Id && x.MenuId == existingMenu.Id));
        Assert.Equal("被管理角色", (await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).RoleName);
    }

    /// <summary>边界值必须放行：名称 / 编码恰好等于持久化长度上限、描述等于上限；超一位即拒绝。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedTargetRole(db);
        var userId = SeedAuthUser(db);
        var menu = SeedMenu(db);
        var ctl = Controller(db, userId);

        var nameAtLimit = new string('N', RoleAuthorizationRules.MaxRoleNameLength);
        var codeAtLimit = new string('C', RoleAuthorizationRules.MaxRoleCodeLength);
        var descriptionAtLimit = new string('D', RoleAuthorizationRules.MaxDescriptionLength);

        await ctl.Create(NewRequest(name: nameAtLimit, code: codeAtLimit,
            description: descriptionAtLimit, menuIds: new List<long> { menu.Id }));

        var created = await db.SysRoles.AsNoTracking().SingleAsync(x => x.RoleCode == codeAtLimit);
        Assert.Equal(nameAtLimit, created.RoleName);
        Assert.Equal(descriptionAtLimit, created.Description);
        Assert.Single(db.SysRoleMenus.Where(x => x.RoleId == created.Id && x.MenuId == menu.Id));

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            name: nameAtLimit + "N", code: codeAtLimit + "C")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            code: codeAtLimit + "C", description: descriptionAtLimit + "D")));
    }

    // ==================== 5. 既有契约与源码 / 菜单契约 ====================

    /// <summary>授权身份下的既有契约不变：重复角色编码仍为 <c>Duplicate</c> 且零写入；内置角色仍不可删除（<c>RuleConflict</c>）。</summary>
    [Fact]
    public async Task Contract_重复编码与内置角色删除保护不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedAuthUser(db);
        var builtIn = SeedTargetRole(db, code: "admin", isSystem: true);
        var menu = SeedMenu(db);
        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewRequest(code: "admin", menuIds: new List<long> { menu.Id })));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Delete(builtIn.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == builtIn.Id)).IsDeleted);
    }

    /// <summary>控制器源码契约：7 条路由 + 角色菜单批量写入入口全部先经实时授权再读写，且无匿名 / 角色回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "RoleController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(8, Regex.Matches(source, @"await EnsureRoleAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>
    /// ERP-464 源码契约：授权入口不以 <c>Request.Path</c>、环境、上下文存在性或任何测试开关决定是否执行；
    /// 角色 → 菜单批量写入入口同样先授权。
    /// </summary>
    [Fact]
    public void Auth_控制器源码契约_授权不依赖请求路径()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "RoleController.cs"));

        Assert.DoesNotContain("Request.Path", source);
        Assert.DoesNotContain("RequiresLiveAuthorization", source);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source);

        // 角色菜单批量写入入口不因调用方已授权而省略实时护栏
        Assert.Contains("await EnsureRoleAuthorizedAsync();", source);
        Assert.Contains("EnsureMenuIdsResolvedAsync", source);
    }

    /// <summary>授权口径复用既有「角色管理」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有角色菜单常量_不新增菜单()
    {
        Assert.Equal("role", RoleAuthorizationRules.RequiredMenuCode);
        Assert.Equal("角色管理", RoleAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{RoleAuthorizationRules.RequiredMenuCode}\", \"{RoleAuthorizationRules.RequiredMenuText}\"",
            menus);
    }
}
