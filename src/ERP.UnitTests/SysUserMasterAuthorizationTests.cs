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
/// 系统用户管理（<c>api/sys/users</c>）实时授权、有界字段校验与受控角色分配单元测试（ERP-453）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「用户管理」（<c>user</c>）菜单 / 无菜单的身份在
/// <b>全部 7 条路由</b>（分页 / 按主键 / 新增 / 修改 / 切换状态 / 重置密码 / 删除）fail closed，
/// 且 <c>SysUsers</c> + <c>SysUserRoles</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「用户管理」菜单的特权（系统内置角色）与普通菜单授权身份下，既有读 / 写契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段 / 角色校验</b>：用户名空值 / 越界、显示姓名 / 邮箱 / 手机号越界、状态非 0 / 1、
/// 重置密码越界、角色 Id 为负数 / 未知 / 已删除一律按受控参数错误拒绝且不落任何行 / 不改写任何行；</item>
/// <item><b>既有契约不变</b>：内置管理员（<c>SeedData.AdminUserName</c>）禁用 / 删除保护与 PBKDF2 哈希语义不变；</item>
/// <item><b>与请求路径无关</b>（ERP-463）：空路径与已赋值路径口径完全一致，完全未绑定 <c>HttpContext</c>
/// 的纯进程内直调同样执行实时授权（无法解析身份即未认证），绝无匿名 / 管理员回退；</item>
/// <item><b>源码契约</b>：控制器 7 条路由全部先授权再读写，且只复用既有 <c>user</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class SysUserMasterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static SysUserController Controller(ErpDbContext db, long? userId) =>
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
        http.Request.Path = "/api/sys/users";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>
    /// 空路径身份上下文（<b>不设置</b> <c>Request.Path</c>，可带身份）：用于断言空路径 / 未绑定请求管线的调用
    /// 与已赋值路径口径完全一致（ERP-463）。
    /// </summary>
    private static ControllerContext InternalContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 用户管理菜单 / 系统内置角色），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantUserMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"user-auth-{Guid.NewGuid():N}",
            DisplayName = "用户授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "用户授权用例角色",
            RoleCode = $"UserAuthCase-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantUserMenu)
            GrantMenu(db, role.Id, SysUserAuthorizationRules.RequiredMenuCode, SysUserAuthorizationRules.RequiredMenuText);
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

    /// <summary>播种一个被管理目标用户（分页 / 详情 / 修改 / 切换 / 重置 / 删除的对象）</summary>
    private static SysUser SeedTarget(ErpDbContext db, string userName = "target", UserStatus status = UserStatus.Enabled)
    {
        var salt = PasswordHasher.GenerateSalt();
        var user = new SysUser
        {
            UserName = userName,
            DisplayName = "被管理用户",
            Email = "target@x.com",
            Phone = "13800000000",
            PasswordSalt = salt,
            PasswordHash = PasswordHasher.HashPassword("OldPass123", salt),
            Status = status,
            MustChangePassword = false
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysUserCreateRequest NewCreate(
        string userName = "new-user", string password = "NewPass123",
        string displayName = "新用户", string email = "new@x.com", string phone = "13900000000",
        List<long>? roleIds = null)
        => new()
        {
            UserName = userName,
            Password = password,
            DisplayName = displayName,
            Email = email,
            Phone = phone,
            RoleIds = roleIds ?? new List<long>()
        };

    private static SysUserUpdateRequest NewUpdate(
        string displayName = "改名", string email = "upd@x.com", string phone = "13700000000",
        UserStatus status = UserStatus.Enabled, List<long>? roleIds = null)
        => new()
        {
            DisplayName = displayName,
            Email = email,
            Phone = phone,
            Status = status,
            RoleIds = roleIds ?? new List<long>()
        };

    /// <summary>用户主表 + 角色关联表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) =>
        string.Join("|", db.SysUsers.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"{x.Id}:{x.UserName}:{x.DisplayName}:{x.Email}:{x.Phone}:{x.Status}:" +
                             $"{x.IsDeleted}:{x.MustChangePassword}:{x.PasswordHash}").ToList())
        + "##" + string.Join("|", db.SysUserRoles.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"{x.Id}:{x.UserId}:{x.RoleId}:{x.IsDeleted}").ToList());

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

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有用户菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「用户管理」菜单的身份：全部 7 条路由
    /// 在读取或写入任何用户之前 fail closed，且 <c>SysUsers</c> + <c>SysUserRoles</c> 逐字节不变。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何用户(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantUserMenu: false)
        };
        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetById(target.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewCreate()));
        await AssertCode(expectedCode, () => ctl.Update(target.Id, NewUpdate()));
        await AssertCode(expectedCode, () => ctl.ToggleStatus(target.Id));
        await AssertCode(expectedCode, () => ctl.ResetPassword(target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));
        await AssertCode(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    /// <summary>具备既有「用户管理」菜单的普通授权身份：既有读 / 写契约（含角色绑定）放行。</summary>
    [Fact]
    public async Task Auth_菜单授权身份_既有读写契约放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var role = new SysRole { RoleName = "业务角色", RoleCode = "sales", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var ctl = Controller(db, userId);

        // 新增（含角色绑定）
        await ctl.Create(NewCreate(userName: "alice", roleIds: new List<long> { role.Id }));
        var alice = await db.SysUsers.AsNoTracking().SingleAsync(x => x.UserName == "alice");
        Assert.True(alice.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("NewPass123", alice.PasswordSalt, alice.PasswordHash));
        Assert.Single(db.SysUserRoles.Where(x => x.UserId == alice.Id && x.RoleId == role.Id));

        // 分页 / 详情
        var page = Data<PagedResult<SysUserView>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Contains(page.Items, x => x.UserName == "alice");
        var detail = Data<SysUserView>(await ctl.GetById(alice.Id));
        Assert.Contains(role.Id, detail.RoleIds);

        // 修改（字段 + 状态 + 角色）
        await ctl.Update(alice.Id, NewUpdate(displayName: "Alice", status: UserStatus.Disabled));
        var updated = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == alice.Id);
        Assert.Equal("Alice", updated.DisplayName);
        Assert.Equal(UserStatus.Disabled, updated.Status);
        Assert.Empty(db.SysUserRoles.Where(x => x.UserId == alice.Id));

        // 切换状态
        await ctl.ToggleStatus(alice.Id);
        Assert.Equal(UserStatus.Enabled, (await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == alice.Id)).Status);

        // 重置密码
        await ctl.ResetPassword(alice.Id, new ResetPasswordRequest { NewPassword = "ResetPass123" });
        var reset = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == alice.Id);
        Assert.True(reset.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("ResetPass123", reset.PasswordSalt, reset.PasswordHash));

        // 软件删除
        await ctl.Delete(alice.Id);
        Assert.True((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == alice.Id)).IsDeleted);
    }

    /// <summary>特权（系统内置角色）身份若同样具备既有「用户管理」菜单，则既有只读契约放行 —— 不因特权跳过菜单检查。</summary>
    [Fact]
    public async Task Auth_特权菜单身份_只读契约放行()
    {
        using var db = TestDbFactory.Create();
        SeedTarget(db);
        var userId = SeedUser(db, privileged: true);
        var ctl = Controller(db, userId);

        var page = Data<PagedResult<SysUserView>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotEmpty(page.Items);
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<SysUserView>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 })); // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(target.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewCreate()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(target.Id));

        Assert.False((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    /// <summary>
    /// ERP-463：授权判定与请求路径 / 请求形状无关 —— 空路径（未设置 <c>Request.Path</c>）与已赋值路径
    /// 对同一身份给出完全一致的判定；完全未绑定 <c>HttpContext</c> 的纯进程内直调同样执行本护栏，
    /// 因无法解析身份一律按未认证拒绝（绝不因缺少路径 / 上下文而放行）。
    /// </summary>
    [Fact]
    public async Task Auth_空路径与已赋值路径口径一致_未绑定上下文一律未认证()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var userId = SeedUser(db);

        // 1) 空路径 + 既有启用身份 + 既有 user 菜单 → 放行（与已赋值路径完全一致）
        var emptyPath = new SysUserController(db) { ControllerContext = InternalContextWithUser(userId) };
        var page = Data<PagedResult<SysUserView>>(await emptyPath.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Contains(page.Items, x => x.Id == target.Id);

        // 2) 已赋值路径 + 同一身份 → 判定完全一致
        var realRoute = Controller(db, userId);
        var page2 = Data<PagedResult<SysUserView>>(await realRoute.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Contains(page2.Items, x => x.Id == target.Id);

        // 3) 空路径 + 无身份 → 未认证（绝不因空路径放行）
        var emptyPathAnonymous = new SysUserController(db) { ControllerContext = InternalContextWithUser(null) };
        await AssertCode(ErrorCodes.Unauthorized, () =>
            emptyPathAnonymous.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Unauthorized, () => emptyPathAnonymous.Create(NewCreate()));
        await AssertCode(ErrorCodes.Unauthorized, () => emptyPathAnonymous.Delete(target.Id));

        // 4) 完全未绑定 HttpContext（纯进程内直调）→ 无法解析身份，一律未认证
        var unbound = new SysUserController(db);
        await AssertCode(ErrorCodes.Unauthorized, () => unbound.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Unauthorized, () => unbound.Update(target.Id, NewUpdate()));
        await AssertCode(ErrorCodes.Unauthorized, () => unbound.ToggleStatus(target.Id));
        await AssertCode(ErrorCodes.Unauthorized, () =>
            unbound.ResetPassword(target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));

        // 拒绝后目标用户零改写
        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.True(PasswordHasher.VerifyPassword("OldPass123", stored.PasswordSalt, stored.PasswordHash));
    }

    /// <summary>空路径（未设置 <c>Request.Path</c>）下，禁用 / 已删除 / 缺菜单身份与已赋值路径一样 fail closed。</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_空路径_拒绝身份与已赋值路径一致(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        long? userId = scenario switch
        {
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantUserMenu: false)
        };
        var expectedCode = scenario == "deleted" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden;

        var ctl = new SysUserController(db) { ControllerContext = InternalContextWithUser(userId) };
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetById(target.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewCreate()));
        await AssertCode(expectedCode, () => ctl.Update(target.Id, NewUpdate()));
        await AssertCode(expectedCode, () => ctl.ToggleStatus(target.Id));
        await AssertCode(expectedCode, () =>
            ctl.ResetPassword(target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));
        await AssertCode(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 3. 有界字段 / 角色校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>授权身份下的非法新增载荷（用户名空值 / 越界、显示姓名 / 邮箱 / 手机号越界、密码越界、角色非法）拒绝且零写入。</summary>
    [Theory]
    [InlineData("empty-username")]
    [InlineData("blank-username")]
    [InlineData("username-too-long")]
    [InlineData("display-too-long")]
    [InlineData("email-too-long")]
    [InlineData("phone-too-long")]
    [InlineData("password-too-short")]
    [InlineData("password-too-long")]
    [InlineData("negative-role")]
    [InlineData("unknown-role")]
    public async Task Validation_新增非法载荷_拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedTarget(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        SysUserCreateRequest Payload() => scenario switch
        {
            "empty-username" => NewCreate(userName: ""),
            "blank-username" => NewCreate(userName: "   "),
            "username-too-long" => NewCreate(userName: new string('U', SysUserAuthorizationRules.MaxUserNameLength + 1)),
            "display-too-long" => NewCreate(displayName: new string('D', SysUserAuthorizationRules.MaxDisplayNameLength + 1)),
            "email-too-long" => NewCreate(email: new string('E', SysUserAuthorizationRules.MaxEmailLength + 1)),
            "phone-too-long" => NewCreate(phone: new string('P', SysUserAuthorizationRules.MaxPhoneLength + 1)),
            "password-too-short" => NewCreate(password: "12345"),
            "password-too-long" => NewCreate(password: new string('p', SysUserAuthorizationRules.MaxPasswordLength + 1)),
            "negative-role" => NewCreate(roleIds: new List<long> { -1 }),
            _ => NewCreate(roleIds: new List<long> { 999_999 })
        };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(2, await db.SysUsers.AsNoTracking().CountAsync()); // 仅 target 与授权身份
    }

    /// <summary>授权身份下的非法修改载荷（显示姓名 / 邮箱 / 手机号越界、状态非 0 / 1、角色非法）拒绝且不改写任何既有行。</summary>
    [Theory]
    [InlineData("display-too-long")]
    [InlineData("email-too-long")]
    [InlineData("phone-too-long")]
    [InlineData("status-unknown")]
    [InlineData("negative-role")]
    [InlineData("unknown-role")]
    public async Task Validation_修改非法载荷_拒绝且不改写任何行(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        SysUserUpdateRequest Payload() => scenario switch
        {
            "display-too-long" => NewUpdate(displayName: new string('D', SysUserAuthorizationRules.MaxDisplayNameLength + 1)),
            "email-too-long" => NewUpdate(email: new string('E', SysUserAuthorizationRules.MaxEmailLength + 1)),
            "phone-too-long" => NewUpdate(phone: new string('P', SysUserAuthorizationRules.MaxPhoneLength + 1)),
            "status-unknown" => NewUpdate(status: (UserStatus)2),
            "negative-role" => NewUpdate(roleIds: new List<long> { -1 }),
            _ => NewUpdate(roleIds: new List<long> { 999_999 })
        };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, Payload()));
        Assert.Equal(before, Snapshot(db));
    }

    /// <summary>授权身份下的非法重置密码载荷（过短 / 过长）拒绝且不改写任何既有行（哈希 / 盐语义不变）。</summary>
    [Theory]
    [InlineData("too-short")]
    [InlineData("too-long")]
    public async Task Validation_重置密码非法载荷_拒绝且不改写任何行(string scenario)
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var request = scenario == "too-short"
            ? new ResetPasswordRequest { NewPassword = "12345" }
            : new ResetPasswordRequest { NewPassword = new string('p', SysUserAuthorizationRules.MaxPasswordLength + 1) };

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.ResetPassword(target.Id, request));
        Assert.Equal(before, Snapshot(db));

        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.True(PasswordHasher.VerifyPassword("OldPass123", stored.PasswordSalt, stored.PasswordHash));
    }

    /// <summary>已删除角色不可分配：新增与修改都拒绝且零写入（不新增任何用户 / 角色关联）。</summary>
    [Fact]
    public async Task Validation_角色已删除_新增与修改均拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var target = SeedTarget(db);
        var userId = SeedUser(db);
        var deletedRole = new SysRole { RoleName = "已删除角色", RoleCode = "gone", IsDeleted = true };
        db.SysRoles.Add(deletedRole);
        db.SaveChanges();
        var ctl = Controller(db, userId);

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(
            NewCreate(userName: "withrole", roleIds: new List<long> { deletedRole.Id })));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(
            target.Id, NewUpdate(roleIds: new List<long> { deletedRole.Id })));

        Assert.Equal(before, Snapshot(db));
        Assert.DoesNotContain(db.SysUsers, u => u.UserName == "withrole");
    }

    /// <summary>边界值必须放行：用户名 / 显示姓名 / 邮箱 / 手机号恰好等于持久化长度上限、密码等于最小 / 最大长度、状态 0 / 1 都接受。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var role = new SysRole { RoleName = "边界角色", RoleCode = "boundary", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var ctl = Controller(db, userId);

        var nameAtLimit = new string('U', SysUserAuthorizationRules.MaxUserNameLength);
        var displayAtLimit = new string('D', SysUserAuthorizationRules.MaxDisplayNameLength);
        var emailAtLimit = new string('E', SysUserAuthorizationRules.MaxEmailLength);
        var phoneAtLimit = new string('P', SysUserAuthorizationRules.MaxPhoneLength);

        await ctl.Create(NewCreate(
            userName: nameAtLimit,
            password: new string('p', SysUserAuthorizationRules.MinPasswordLength),
            displayName: displayAtLimit,
            email: emailAtLimit,
            phone: phoneAtLimit,
            roleIds: new List<long> { role.Id }));

        var created = await db.SysUsers.AsNoTracking().SingleAsync(x => x.UserName == nameAtLimit);
        Assert.Equal(displayAtLimit, created.DisplayName);
        Assert.Equal(emailAtLimit, created.Email);
        Assert.Equal(phoneAtLimit, created.Phone);
        Assert.Single(db.SysUserRoles.Where(x => x.UserId == created.Id && x.RoleId == role.Id));

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(userName: nameAtLimit + "U")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            userName: "u2", password: new string('p', SysUserAuthorizationRules.MaxPasswordLength + 1))));

        // 状态 0 / 1 都接受
        await ctl.Update(created.Id, NewUpdate(status: UserStatus.Disabled));
        Assert.Equal(UserStatus.Disabled, (await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Status);
    }

    // ==================== 4. 既有契约与源码 / 菜单契约 ====================

    /// <summary>内置管理员（<c>SeedData.AdminUserName</c>）保护不变：授权身份下禁用 / 删除仍被拒绝且零写入。</summary>
    [Fact]
    public async Task Contract_内置管理员保护不变()
    {
        using var db = TestDbFactory.Create();
        var admin = SeedTarget(db, userName: SeedData.AdminUserName);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.ToggleStatus(admin.Id));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Delete(admin.Id));

        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == admin.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    /// <summary>控制器源码契约：7 条路由全部先经实时授权再读写，且无匿名 / 角色回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysUserController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureUserAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>
    /// ERP-463 源码契约：授权入口不以 <c>Request.Path</c>、环境、上下文存在性或任何测试开关决定是否执行；
    /// 角色关联写入入口同样先授权。
    /// </summary>
    [Fact]
    public void Auth_控制器源码契约_授权不依赖请求路径()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysUserController.cs"));
        var helpers = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysUserController.Helpers.cs"));

        foreach (var text in new[] { source, helpers })
        {
            Assert.DoesNotContain("Request.Path", text);
            Assert.DoesNotContain("RequiresLiveAuthorization", text);
            Assert.DoesNotContain("Environment.GetEnvironmentVariable", text);
        }

        // 角色关联批量写入入口不因调用方已授权而省略实时护栏
        Assert.Contains("await EnsureUserAuthorizedAsync();", helpers);
        Assert.Contains("EnsureRoleIdsResolvedAsync", helpers);
    }

    /// <summary>授权口径复用既有「用户管理」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有用户菜单常量_不新增菜单()
    {
        Assert.Equal("user", SysUserAuthorizationRules.RequiredMenuCode);
        Assert.Equal("用户管理", SysUserAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{SysUserAuthorizationRules.RequiredMenuCode}\", \"{SysUserAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
