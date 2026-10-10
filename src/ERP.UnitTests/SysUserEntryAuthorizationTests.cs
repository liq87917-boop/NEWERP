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
/// ERP-463 系统用户管理（<c>api/sys/users</c>）<b>入口授权</b>单元测试：证明实时身份 / 账号状态 / 既有
/// 「用户管理」（<c>user</c>）菜单判定在<b>每一条入口</b>（分页 / 按主键读取 / 新增 / 修改 / 切换状态 /
/// 重置密码 / 删除 / 角色关联写入）都无条件执行，且与请求路径是否赋值、请求形状、
/// 以及控制器是否绑定 <c>HttpContext</c> <b>完全无关</b>。
/// <list type="number">
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>fail closed</b>：缺失 / 零 / 已删除身份按未认证，禁用 / 缺菜单 / 被撤销菜单按权限不足，
/// 且发生在任何敏感读取 / 写入<b>之前</b>；</item>
/// <item><b>零写入</b>：被拒绝的调用不新增 / 不改写任何 <c>SysUsers</c> / <c>SysUserRoles</c> 行，
/// 也不留下任何角色授权；</item>
/// <item><b>既有契约不变</b>：授权身份下既有读 / 写 / 软删除 / 重置密码 / 角色绑定契约与
/// 内置管理员保护、PBKDF2 哈希语义全部保持。</item>
/// </list>
/// 全部使用隔离的内存库（<see cref="TestDbFactory"/>），授权只播种在测试数据里；
/// 不新增任何生产菜单 / 权限 / 用户授权，也没有任何测试专用放行开关。
/// </summary>
public class SysUserEntryAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private const string PopulatedPath = "/api/sys/users";

    /// <summary>
    /// 按请求形状绑定控制器：<c>empty</c> = 不设置 <c>Request.Path</c>；<c>populated</c> = 赋值真实路由；
    /// <c>no-context</c> = 完全不绑定 <c>HttpContext</c>（纯进程内直调）。三种形状必须给出完全一致的判定。
    /// </summary>
    private static SysUserController Bind(ErpDbContext db, long? userId, string pathMode)
    {
        if (pathMode == "no-context") return new SysUserController(db);

        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (pathMode == "populated") http.Request.Path = PopulatedPath;
        return new SysUserController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    /// <summary>
    /// 播种一个既有授权身份（启用 / 禁用、未删除 / 已删除、是否具备既有「用户管理」菜单、是否系统内置角色、
    /// 是否已撤销菜单），返回其用户 Id。菜单与 <c>SeedData</c> 同码同源，不新增任何生产授权。
    /// </summary>
    private static long SeedActor(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool withMenu = true, bool privileged = false, bool revoked = false)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == SysUserAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = SysUserAuthorizationRules.RequiredMenuCode,
                MenuName = SysUserAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        var role = new SysRole
        {
            RoleCode = $"user-entry-{Guid.NewGuid():N}",
            RoleName = "用户管理入口授权角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"user-entry-{Guid.NewGuid():N}",
            DisplayName = "用户管理入口授权账号",
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

    /// <summary>按身份场景播种（或返回）对应身份 Id；<c>null</c> = 无身份，<c>0</c> = 非法零身份。</summary>
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

    // ==================== 0b. 断言与播种辅助 ====================

    /// <summary>播种被管理的目标用户（分页 / 详情 / 修改 / 切换 / 重置 / 删除对象）。</summary>
    private static SysUser SeedTarget(ErpDbContext db)
    {
        var salt = PasswordHasher.GenerateSalt();
        var user = new SysUser
        {
            UserName = $"entry-target-{Guid.NewGuid():N}",
            DisplayName = "入口授权被管理用户",
            Email = "entry-target@x.com",
            Phone = "13800000000",
            PasswordSalt = salt,
            PasswordHash = PasswordHasher.HashPassword("OldPass123", salt),
            Status = UserStatus.Enabled,
            MustChangePassword = false
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    /// <summary>播种一个既有业务角色（可选已删除），返回其角色 Id。</summary>
    private static async Task<long> SeedBusinessRoleAsync(ErpDbContext db, bool deleted = false)
    {
        var role = new SysRole
        {
            RoleCode = $"entry-biz-{Guid.NewGuid():N}",
            RoleName = "入口授权业务角色",
            IsSystem = false,
            IsDeleted = deleted
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private static SysUserCreateRequest NewCreate(string userName, List<long>? roleIds = null)
        => new()
        {
            UserName = userName,
            Password = "NewPass123",
            DisplayName = "入口新建用户",
            Email = "entry-new@x.com",
            Phone = "13900000000",
            RoleIds = roleIds ?? new List<long>()
        };

    private static SysUserUpdateRequest NewUpdate(string displayName = "入口改名",
        UserStatus status = UserStatus.Enabled, List<long>? roleIds = null)
        => new()
        {
            DisplayName = displayName,
            Email = "entry-upd@x.com",
            Phone = "13700000000",
            Status = status,
            RoleIds = roleIds ?? new List<long>()
        };

    /// <summary>授权相关行的完整快照（用户行 + 角色关联行 + 角色 → 菜单授权行）：被拒绝的调用必须逐字节不变。</summary>
    private static async Task<string> SnapshotAsync(ErpDbContext db)
    {
        var users = await db.SysUsers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.UserName}:{x.DisplayName}:{x.Email}:{x.Phone}:{x.Status}:" +
                         $"{x.IsDeleted}:{x.MustChangePassword}:{x.PasswordHash}")
            .ToListAsync();
        var links = await db.SysUserRoles.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.UserId}:{x.RoleId}:{x.IsDeleted}")
            .ToListAsync();
        var grants = await db.SysRoleMenus.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.RoleId}:{x.MenuId}:{x.IsDeleted}")
            .ToListAsync();
        return string.Join("|", users) + "##" + string.Join("|", links) + "##" + string.Join("|", grants);
    }

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

    /// <summary>读取入口（分页）在空路径 / 已赋值路径下的允许与拒绝矩阵。</summary>
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
    public async Task Entry_分页入口_授权与请求路径无关(string pathMode, string identity, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var ctl = Bind(db, SeedActorFor(db, identity), pathMode);

        if (expectedCode == 0)
        {
            var page = Data<PagedResult<SysUserView>>(
                await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
            Assert.Contains(page.Items, x => x.Id == target.Id);
        }
        else
        {
            await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        }
    }

    // ==================== 2. 完全未绑定 HttpContext：一律 fail closed ====================

    /// <summary>
    /// 纯进程内直调（完全没有 <c>HttpContext</c>，外部请求无法到达）：无法解析任何身份，
    /// 读取 / 写入入口一律按未认证拒绝 —— 绝不因「没有请求上下文 / 没有路径」而放行。
    /// </summary>
    [Fact]
    public async Task Entry_未绑定HTTP上下文_一律未认证且零写入()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        SeedActor(db);
        var ctl = Bind(db, userId: null, "no-context");
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.Unauthorized, () =>
            ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetById(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Create(NewCreate("entry-none")));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Update(target.Id, NewUpdate()));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.ToggleStatus(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () =>
            ctl.ResetPassword(target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 3. 拒绝矩阵：全部 7 条入口零写入 ====================

    /// <summary>拒绝身份在空路径 / 已赋值路径下的七入口矩阵。</summary>
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
    /// 缺失 / 已删除 / 禁用 / 缺菜单 / 撤销菜单身份：全部分页 / 按主键 / 新增 / 修改 / 切换状态 / 重置密码 /
    /// 删除入口在读取或写入任何用户之前 fail closed，且用户行 / 角色关联行 / 菜单授权行逐字节不变。
    /// 空路径与已赋值路径给出完全一致的判定。
    /// </summary>
    [Theory]
    [MemberData(nameof(DeniedMutationMatrix))]
    public async Task Entry_拒绝身份_七条入口零写入且与请求路径无关(
        string pathMode, string identity, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var ctl = Bind(db, SeedActorFor(db, identity), pathMode);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewCreate("entry-denied")));
        await AssertCodeAsync(expectedCode, () => ctl.Update(target.Id, NewUpdate()));
        await AssertCodeAsync(expectedCode, () => ctl.ToggleStatus(target.Id));
        await AssertCodeAsync(expectedCode, () =>
            ctl.ResetPassword(target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.True(PasswordHasher.VerifyPassword("OldPass123", stored.PasswordSalt, stored.PasswordHash));
    }

    // ==================== 4. 授权身份：既有读 / 写契约在两种路径形状下完全一致 ====================

    /// <summary>
    /// 既有启用身份 + 既有 <c>user</c> 菜单：既有读 / 写 / 软删除 / 重置密码契约在空路径与已赋值路径下
    /// 给出完全一致的结果（授权不因请求形状改变任何既有业务结果）。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Entry_授权身份_既有读写契约与请求路径无关(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var actorId = SeedActor(db);
        var businessRoleId = await SeedBusinessRoleAsync(db);
        var target = SeedTarget(db);
        var ctl = Bind(db, actorId, pathMode);

        // 分页 / 详情（读取入口）
        var page = Data<PagedResult<SysUserView>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }));
        Assert.Contains(page.Items, x => x.Id == target.Id);
        Assert.Equal(target.Email, Data<SysUserView>(await ctl.GetById(target.Id)).Email);

        // 新增（含既有角色绑定）
        var name = $"entry-{Guid.NewGuid():N}";
        await ctl.Create(NewCreate(name, roleIds: new List<long> { businessRoleId }));
        var created = await db.SysUsers.AsNoTracking().SingleAsync(x => x.UserName == name);
        Assert.True(created.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("NewPass123", created.PasswordSalt, created.PasswordHash));
        Assert.True(await db.SysUserRoles.AsNoTracking()
            .AnyAsync(x => x.UserId == created.Id && x.RoleId == businessRoleId && !x.IsDeleted));

        // 修改（字段 / 状态 / 角色重新分配）
        await ctl.Update(created.Id, NewUpdate(displayName: "入口改名2", status: UserStatus.Disabled));
        var updated = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("入口改名2", updated.DisplayName);
        Assert.Equal(UserStatus.Disabled, updated.Status);
        Assert.Empty(await db.SysUserRoles.AsNoTracking()
            .Where(x => x.UserId == created.Id && !x.IsDeleted).ToListAsync());

        // 切换状态
        await ctl.ToggleStatus(created.Id);
        Assert.Equal(UserStatus.Enabled,
            (await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Status);

        // 重置密码
        await ctl.ResetPassword(created.Id, new ResetPasswordRequest { NewPassword = "ResetPass123" });
        var reset = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.True(reset.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("ResetPass123", reset.PasswordSalt, reset.PasswordHash));

        // 软删除
        await ctl.Delete(created.Id);
        Assert.True((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    /// <summary>授权身份下内置管理员保护不变（禁用 / 删除仍按 RuleConflict 拒绝且零写入），与请求路径无关。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Entry_授权身份_内置管理员保护不变(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var admin = SeedTarget(db);
        admin.UserName = SeedData.AdminUserName;
        await db.SaveChangesAsync();
        var ctl = Bind(db, SeedActor(db), pathMode);

        await AssertCodeAsync(ErrorCodes.RuleConflict, () => ctl.ToggleStatus(admin.Id));
        await AssertCodeAsync(ErrorCodes.RuleConflict, () => ctl.Delete(admin.Id));

        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == admin.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 5. 角色关联写入入口受同一护栏约束 ====================

    /// <summary>
    /// 角色关联写入（<c>SysUserRoles</c> 行）受同一实时护栏约束：被拒绝身份无论载荷如何都不落任何行 / 授权；
    /// 授权身份下未知 / 已删除角色仍按受控参数错误整批拒绝（零写入）。空路径与已赋值路径口径一致。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Entry_角色关联写入_拒绝身份零授权_授权身份下非法角色整批拒绝(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var businessRoleId = await SeedBusinessRoleAsync(db);
        var deletedRoleId = await SeedBusinessRoleAsync(db, deleted: true);
        var deniedActorId = SeedActor(db, withMenu: false);
        var denied = Bind(db, deniedActorId, pathMode);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Create(NewCreate("entry-role-denied", roleIds: new List<long> { businessRoleId })));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Update(deniedActorId, NewUpdate(roleIds: new List<long> { businessRoleId })));
        Assert.Equal(before, await SnapshotAsync(db));

        var granted = Bind(db, SeedActor(db), pathMode);
        var afterDenied = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewCreate("entry-role-unknown", roleIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewCreate("entry-role-deleted", roleIds: new List<long> { deletedRoleId })));
        Assert.Equal(afterDenied, await SnapshotAsync(db));
    }

    // ==================== 6. 授权口径契约 ====================

    /// <summary>授权口径只复用既有「用户管理」菜单，且契约明确声明与请求路径 / 上下文无关。</summary>
    [Fact]
    public void Entry_授权口径复用既有用户菜单且声明路径无关()
    {
        Assert.Equal("user", SysUserAuthorizationRules.RequiredMenuCode);
        Assert.Equal("用户管理", SysUserAuthorizationRules.RequiredMenuText);
        Assert.Contains("与请求路径", SysUserAuthorizationRules.PathIndependenceText);
        Assert.Contains("未绑定任何请求上下文", SysUserAuthorizationRules.PathIndependenceText);
    }
}
