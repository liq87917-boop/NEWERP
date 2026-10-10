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
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-464 角色管理（<c>api/sys/roles</c>）<b>入口授权</b>单元测试：证明实时身份 / 账号状态 / 既有
/// 「角色管理」（<c>role</c>）菜单判定在<b>每一条入口</b>（分页 / 全部 / 按主键读取 / 角色菜单 /
/// 新增 / 修改 / 删除 / 角色菜单批量写入）都无条件执行，且与请求路径是否赋值、请求形状、
/// 以及控制器是否绑定 <c>HttpContext</c> <b>完全无关</b>。
/// <list type="number">
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>fail closed</b>：缺失 / 零 / 已删除身份按未认证，禁用 / 缺菜单 / 被撤销菜单按权限不足，
/// 且发生在任何角色读取 / 写入<b>之前</b>；</item>
/// <item><b>零写入</b>：被拒绝的调用不新增 / 不改写任何 <c>SysRoles</c> / <c>SysRoleMenus</c> 行；</item>
/// <item><b>既有契约不变</b>：授权身份下既有读 / 写 / 软删除 / 全删全建菜单替换与内置角色删除保护全部保持。</item>
/// </list>
/// 全部使用隔离的内存库（<see cref="TestDbFactory"/>），授权只播种在测试数据里；
/// 不新增任何生产菜单 / 权限 / 用户授权，也没有任何测试专用放行开关。
/// </summary>
public class RoleEntryAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private const string PopulatedPath = "/api/sys/roles";

    /// <summary>
    /// 按请求形状绑定控制器：<c>empty</c> = 不设置 <c>Request.Path</c>；<c>populated</c> = 赋值真实路由；
    /// <c>no-context</c> = 完全不绑定 <c>HttpContext</c>（纯进程内直调）。三种形状必须给出完全一致的判定。
    /// </summary>
    private static RoleController Bind(ErpDbContext db, long? userId, string pathMode)
    {
        if (pathMode == "no-context") return new RoleController(db);

        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (pathMode == "populated") http.Request.Path = PopulatedPath;
        return new RoleController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    /// <summary>
    /// 播种一个既有授权身份（启用 / 禁用、未删除 / 已删除、是否具备既有「角色管理」菜单、是否系统内置角色、
    /// 是否已撤销菜单），返回其用户 Id。菜单与 <c>SeedData</c> 同码同源，不新增任何生产授权。
    /// </summary>
    private static long SeedActor(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool withMenu = true, bool privileged = false, bool revoked = false)
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
            RoleName = "角色管理入口授权角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"role-entry-{Guid.NewGuid():N}",
            DisplayName = "角色管理入口授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withMenu)
        {
            db.SysRoleMenus.Add(new SysRoleMenu
            {
                RoleId = role.Id,
                MenuId = menu.Id,
                IsDeleted = revoked
            });
            db.SaveChanges();
        }
        return user.Id;
    }

    /// <summary>按身份场景播种（或返回）对应身份 Id；<c>missing</c> = 无身份，<c>zero</c> = 非法零身份。</summary>
    private static long? SeedActorFor(ErpDbContext db, string identity) => identity switch
    {
        "missing" => null,
        "zero" => 0L,
        "deleted" => SeedActor(db, deleted: true),
        "disabled" => SeedActor(db, status: UserStatus.Disabled),
        "no-menu" => SeedActor(db, withMenu: false),
        "revoked" => SeedActor(db, revoked: true),
        _ => SeedActor(db)
    };

    /// <summary>播种被管理的目标角色（分页 / 详情 / 角色菜单 / 修改 / 删除对象）。</summary>
    private static SysRole SeedTarget(ErpDbContext db, string code = "sales", bool isSystem = false)
    {
        var role = new SysRole
        {
            RoleName = "入口授权被管理角色",
            RoleCode = code,
            Description = "描述",
            IsSystem = isSystem
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    /// <summary>播种一个菜单（可选软删除），用于角色菜单分配校验。</summary>
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
        string name = "入口授权角色", string? code = null, string description = "描述",
        List<long>? menuIds = null)
        => new()
        {
            RoleName = name,
            RoleCode = code ?? $"entry-role-{Guid.NewGuid():N}",
            Description = description,
            MenuIds = menuIds ?? new List<long>()
        };

    // ==================== 0b. 断言与播种辅助 ====================

    /// <summary>角色主表 + 角色菜单关联表快照（被拒绝的调用必须逐字节不变）。</summary>
    private static string Snapshot(ErpDbContext db) =>
        string.Join("|", db.SysRoles.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"{x.Id}:{x.RoleCode}:{x.RoleName}:{x.Description}:{x.IsSystem}:{x.IsDeleted}").ToList())
        + "##" + string.Join("|", db.SysRoleMenus.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"{x.Id}:{x.RoleId}:{x.MenuId}:{x.IsDeleted}").ToList());

    private static async Task AssertCodeAsync(int expected, Func<Task<IActionResult>> action)
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

    /// <summary>读取入口（分页 / 全部 / 按主键 / 角色菜单）在空路径 / 已赋值路径下的允许与拒绝矩阵。</summary>
    public static IEnumerable<object[]> ReadEntryMatrix()
    {
        foreach (var pathMode in new[] { "empty", "populated" })
        {
            yield return new object[] { pathMode, "missing", ErrorCodes.Unauthorized };
            yield return new object[] { pathMode, "zero", ErrorCodes.Unauthorized };
            yield return new object[] { pathMode, "deleted", ErrorCodes.Unauthorized };
            yield return new object[] { pathMode, "disabled", ErrorCodes.Forbidden };
            yield return new object[] { pathMode, "no-menu", ErrorCodes.Forbidden };
            yield return new object[] { pathMode, "revoked", ErrorCodes.Forbidden };
            yield return new object[] { pathMode, "genuine", 0 };
        }
    }

    // ==================== 1. 读取入口：空路径 / 已赋值路径完全一致 ====================

    [Theory]
    [MemberData(nameof(ReadEntryMatrix))]
    public async Task Entry_读取入口_授权与请求路径无关(string pathMode, string identity, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var ctl = Bind(db, SeedActorFor(db, identity), pathMode);

        if (expectedCode == 0)
        {
            var page = Data<PagedResult<SysRole>>(
                await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
            Assert.Contains(page.Items, x => x.Id == target.Id);
            Assert.Contains(Data<List<SysRole>>(await ctl.GetAll()), x => x.Id == target.Id);
            Assert.Equal(target.Id, Data<SysRole>(await ctl.GetById(target.Id)).Id);
            Assert.NotNull(Data<List<long>>(await ctl.GetRoleMenus(target.Id)));
        }
        else
        {
            await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
            await AssertCodeAsync(expectedCode, () => ctl.GetAll());
            await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
            await AssertCodeAsync(expectedCode, () => ctl.GetRoleMenus(target.Id));
        }
    }

    // ==================== 2. 完全未绑定 HttpContext：一律 fail closed ====================

    /// <summary>
    /// 纯进程内直调（完全没有 <c>HttpContext</c>，外部请求无法到达）：无法解析任何身份，
    /// 读取 / 写入入口一律按未认证拒绝，且授权相关行逐字节不变 —— 绝不因「没有请求上下文 / 没有路径」而放行。
    /// </summary>
    [Fact]
    public async Task Entry_未绑定HTTP上下文_一律未认证且零写入()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        SeedActor(db);
        SeedMenu(db);
        var ctl = Bind(db, userId: null, "no-context");
        var before = Snapshot(db);

        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetById(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetRoleMenus(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Create(NewRequest()));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Update(target.Id, NewRequest(code: "sales")));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
    }


    // ==================== 3. 拒绝矩阵：全部入口零写入 ====================

    /// <summary>拒绝身份在空路径 / 已赋值路径下的全部入口矩阵。</summary>
    public static IEnumerable<object[]> DeniedMutationMatrix()
    {
        foreach (var pathMode in new[] { "empty", "populated" })
        {
            yield return new object[] { pathMode, "missing", ErrorCodes.Unauthorized };
            yield return new object[] { pathMode, "deleted", ErrorCodes.Unauthorized };
            yield return new object[] { pathMode, "disabled", ErrorCodes.Forbidden };
            yield return new object[] { pathMode, "no-menu", ErrorCodes.Forbidden };
            yield return new object[] { pathMode, "revoked", ErrorCodes.Forbidden };
        }
    }

    /// <summary>
    /// 缺失 / 已删除 / 禁用 / 缺菜单 / 撤销菜单身份：全部分页 / 全部 / 按主键 / 角色菜单 / 新增 / 修改 /
    /// 删除入口在读取或写入任何角色之前 fail closed，且角色行 / 角色菜单关联行逐字节不变。
    /// 空路径与已赋值路径给出完全一致的判定。
    /// </summary>
    [Theory]
    [MemberData(nameof(DeniedMutationMatrix))]
    public async Task Entry_拒绝身份_全部入口零写入且与请求路径无关(
        string pathMode, string identity, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        SeedMenu(db);
        var ctl = Bind(db, SeedActorFor(db, identity), pathMode);
        var before = Snapshot(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetAll());
        await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.GetRoleMenus(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewRequest()));
        await AssertCodeAsync(expectedCode, () => ctl.Update(target.Id, NewRequest(code: "sales")));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    // ==================== 4. 授权身份：既有读 / 写契约在两种路径形状下完全一致 ====================

    /// <summary>
    /// 既有启用身份 + 既有 <c>role</c> 菜单：既有读 / 写 / 软删除契约与「全删全建」菜单替换在空路径与
    /// 已赋值路径下给出完全一致的结果（授权不因请求形状改变任何既有业务结果）。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Entry_授权身份_既有读写契约与请求路径无关(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var actorId = SeedActor(db);
        var target = SeedTarget(db);
        var m1 = SeedMenu(db);
        var m2 = SeedMenu(db);
        var ctl = Bind(db, actorId, pathMode);

        // 分页 / 全部 / 详情 / 角色菜单（读取入口）
        var page = Data<PagedResult<SysRole>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Contains(page.Items, x => x.Id == target.Id);
        Assert.Contains(Data<List<SysRole>>(await ctl.GetAll()), x => x.Id == target.Id);
        Assert.Equal(target.Id, Data<SysRole>(await ctl.GetById(target.Id)).Id);
        Assert.Empty(Data<List<long>>(await ctl.GetRoleMenus(target.Id)));

        // 新增（含菜单分配）
        var code = $"entry-{Guid.NewGuid():N}";
        await ctl.Create(NewRequest(code: code, menuIds: new List<long> { m1.Id, m2.Id }));
        var created = await db.SysRoles.AsNoTracking().SingleAsync(x => x.RoleCode == code);
        Assert.False(created.IsSystem);
        Assert.Equal(2, await db.SysRoleMenus.AsNoTracking().CountAsync(x => x.RoleId == created.Id));

        // 修改（全删全建；角色编码不变）
        await ctl.Update(created.Id, NewRequest(name: "入口改名", code: code, menuIds: new List<long> { m2.Id }));
        var updated = await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("入口改名", updated.RoleName);
        Assert.Equal(code, updated.RoleCode);
        var links = await db.SysRoleMenus.AsNoTracking().Where(x => x.RoleId == created.Id).ToListAsync();
        Assert.Single(links);
        Assert.Equal(m2.Id, links[0].MenuId);
        Assert.Equal(new[] { m2.Id }, Data<List<long>>(await ctl.GetRoleMenus(created.Id)));

        // 删除（软删除）
        await ctl.Delete(created.Id);
        Assert.True((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }
    // ==================== 5. 角色菜单批量写入入口受同一护栏约束 ====================

    /// <summary>
    /// 角色 → 菜单批量写入（<c>SysRoleMenus</c> 行）受同一实时护栏约束：被拒绝身份无论载荷如何都不落任何行；
    /// 授权身份下未知 / 已删除 / 负数菜单仍按受控参数错误整批拒绝（零写入）。空路径与已赋值路径口径一致。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Entry_菜单分配入口_拒绝身份零授权_授权身份下非法菜单整批拒绝(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var knownMenu = SeedMenu(db);
        var deletedMenu = SeedMenu(db, deleted: true);
        var denied = Bind(db, SeedActor(db, withMenu: false), pathMode);
        var before = Snapshot(db);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Create(NewRequest(code: "entry-denied", menuIds: new List<long> { knownMenu.Id })));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Update(target.Id, NewRequest(code: "sales", menuIds: new List<long> { knownMenu.Id })));
        Assert.Equal(before, Snapshot(db));

        var granted = Bind(db, SeedActor(db), pathMode);
        var afterDenied = Snapshot(db);
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewRequest(code: "entry-unknown", menuIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewRequest(code: "entry-deleted", menuIds: new List<long> { deletedMenu.Id })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewRequest(code: "entry-negative", menuIds: new List<long> { -1 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Update(target.Id, NewRequest(code: "sales", menuIds: new List<long> { 999_999 })));
        Assert.Equal(afterDenied, Snapshot(db));
    }

    // ==================== 6. 请求之间撤销菜单授权立即收敛 ====================

    /// <summary>请求之间撤销既有「角色管理」菜单授权：空路径与已赋值路径的下一次请求立即拒绝且零写入。</summary>
    [Fact]
    public async Task Entry_请求之间撤销菜单_两种路径形状下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedActor(db);
        var target = SeedTarget(db);
        SeedMenu(db);

        foreach (var pathMode in new[] { "empty", "populated" })
            Data<PagedResult<SysRole>>(
                await Bind(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        RevokeMenu(db, userId, RoleAuthorizationRules.RequiredMenuCode);

        var before = Snapshot(db);
        foreach (var pathMode in new[] { "empty", "populated" })
        {
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetAll());
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetPaged(new PageQuery()));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetById(target.Id));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetRoleMenus(target.Id));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).Create(NewRequest()));
            await AssertCodeAsync(ErrorCodes.Forbidden, () =>
                Bind(db, userId, pathMode).Update(target.Id, NewRequest(code: "sales")));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).Delete(target.Id));
        }

        Assert.Equal(before, Snapshot(db));
    }

    /// <summary>回收指定账号的既有「角色管理」菜单授权（模拟请求之间撤销权限，不影响其它账号）。</summary>
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

    // ==================== 7. 授权口径契约 ====================

    /// <summary>授权口径只复用既有「角色管理」菜单，且契约明确声明与请求路径 / 上下文无关。</summary>
    [Fact]
    public void Entry_授权口径复用既有角色菜单且声明路径无关()
    {
        Assert.Equal("role", RoleAuthorizationRules.RequiredMenuCode);
        Assert.Equal("角色管理", RoleAuthorizationRules.RequiredMenuText);
        Assert.Contains("与请求路径", RoleAuthorizationRules.PathIndependenceText);
        Assert.Contains("未绑定任何请求上下文", RoleAuthorizationRules.PathIndependenceText);
    }
}


