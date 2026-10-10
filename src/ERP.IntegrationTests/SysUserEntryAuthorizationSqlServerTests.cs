using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-463 系统用户管理（<c>api/sys/users</c>）<b>入口授权</b>的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，绝不 drop / reset / 复用）。
/// <list type="number">
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>实时收敛</b>：禁用 / 已删除 / 撤销菜单的身份在请求之间立即收敛为拒绝（每次实时解析，绝不缓存）；</item>
/// <item><b>零写入</b>：被拒绝的调用不新增 / 不改写任何 <c>SysUsers</c> / <c>SysUserRoles</c> 行，也不留下任何角色授权；</item>
/// <item><b>既有契约不变</b>：真实既有启用身份 + 既有「用户管理」（<c>user</c>）菜单下，既有新增 / 分页 /
/// 详情 / 修改 / 切换状态 / 重置密码 / 软删除与内置管理员保护、PBKDF2 哈希语义全部保持。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SysUserEntryAuthorizationSqlServerTests
    : IClassFixture<SysUserEntryAuthorizationSqlServerFixture>
{
    private readonly SysUserEntryAuthorizationSqlServerFixture _fixture;

    public SysUserEntryAuthorizationSqlServerTests(SysUserEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SysUserEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

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

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static SysUserCreateRequest NewCreate(string? userName = null, List<long>? roleIds = null)
        => new()
        {
            UserName = userName ?? $"int-entry-{Tag()}",
            Password = "NewPass123",
            DisplayName = "集成入口新用户",
            Email = "int-entry@x.com",
            Phone = "13900000000",
            RoleIds = roleIds ?? new List<long>()
        };

    private static SysUserUpdateRequest NewUpdate(string displayName = "集成入口改名",
        UserStatus status = UserStatus.Enabled, List<long>? roleIds = null)
        => new()
        {
            DisplayName = displayName,
            Email = "int-entry-upd@x.com",
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

    // ==================== 0. 播种辅助 ====================

    /// <summary>播种被管理的目标用户（分页 / 详情 / 修改 / 切换 / 重置 / 删除对象）。</summary>
    private static async Task<SysUser> SeedTargetAsync(ErpDbContext db)
    {
        var salt = PasswordHasher.GenerateSalt();
        var user = new SysUser
        {
            UserName = $"int-entry-target-{Tag()}",
            DisplayName = "集成入口被管理用户",
            Email = "int-entry-target@x.com",
            Phone = "13800000000",
            PasswordSalt = salt,
            PasswordHash = PasswordHasher.HashPassword("OldPass123", salt),
            Status = UserStatus.Enabled,
            MustChangePassword = false
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>播种一个既有授权身份（可选状态 / 删除 / 既有用户菜单 / 已撤销菜单 / 系统内置角色）。</summary>
    private static async Task<long> SeedActorAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool userMenu = true, bool revoked = false, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"int-entry-actor-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "集成入口授权账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "集成入口授权角色",
            RoleCode = $"IEA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (userMenu)
            await GrantMenuAsync(db, role.Id, SysUserAuthorizationRules.RequiredMenuCode, revoked);
        return user.Id;
    }

    /// <summary>播种一个既有业务角色（可选已删除），返回其角色 Id。</summary>
    private static async Task<long> SeedBusinessRoleAsync(ErpDbContext db, bool deleted = false)
    {
        var role = new SysRole
        {
            RoleName = "集成入口业务角色",
            RoleCode = $"IEB-{Tag()}",
            IsSystem = false,
            IsDeleted = deleted
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    /// <summary>
    /// 授予既有功能菜单（真实「角色 → 菜单」口径；菜单缺失时按既有种子口径补建），
    /// <paramref name="revoked"/> 为真时按既有软删除语义直接写入一条已撤销授权（模拟请求之间撤销权限）。
    /// </summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode, bool revoked = false)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id, IsDeleted = revoked });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>按身份场景播种（或返回）对应身份 Id；<c>missing</c> = 无身份，<c>zero</c> = 非法零身份。</summary>
    private static async Task<long?> SeedActorForAsync(ErpDbContext db, string identity)
    {
        if (identity == "missing") return null;
        if (identity == "zero") return 0L;
        if (identity == "deleted") return await SeedActorAsync(db, UserStatus.Enabled, deleted: true);
        if (identity == "disabled") return await SeedActorAsync(db, UserStatus.Disabled, deleted: false);
        if (identity == "no-menu") return await SeedActorAsync(db, UserStatus.Enabled, deleted: false, userMenu: false);
        if (identity == "revoked") return await SeedActorAsync(db, UserStatus.Enabled, deleted: false, revoked: true);
        return await SeedActorAsync(db, UserStatus.Enabled, deleted: false);
    }

    // ==================== 1. 读取入口：空路径 / 已赋值路径完全一致 ====================

    /// <summary>读取入口（分页 / 按主键）在空路径与已赋值路径下的允许与拒绝矩阵。</summary>
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

    [Theory]
    [MemberData(nameof(ReadEntryMatrix))]
    public async Task Read_entries_enforce_identical_authority_on_all_paths(
        string pathMode, string identity, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var ctl = Bind(db, await SeedActorForAsync(db, identity), pathMode);

        if (expectedCode == 0)
        {
            var page = Data<PagedResult<SysUserView>>(
                await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }));
            Assert.Contains(page.Items, x => x.Id == target.Id);
            Assert.Equal(target.Email, Data<SysUserView>(await ctl.GetById(target.Id)).Email);
        }
        else
        {
            await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
            await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
        }
    }

    // ==================== 2. 完全未绑定 HttpContext：一律 fail closed ====================

    /// <summary>
    /// 纯进程内直调（完全没有 <c>HttpContext</c>）：无法解析任何身份，读取 / 写入入口一律按未认证拒绝，
    /// 且授权相关行逐字节不变 —— 绝不因「没有请求上下文 / 没有路径」而放行。
    /// </summary>
    [Fact]
    public async Task Unbound_context_is_unauthorized_and_writes_nothing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var ctl = Bind(db, userId: null, "no-context");
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.Unauthorized, () =>
            ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetById(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Create(NewCreate()));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Update(target.Id, NewUpdate()));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.ToggleStatus(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () =>
            ctl.ResetPassword(target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 3. 请求之间实时收敛（撤销菜单 / 禁用 / 删除） ====================

    /// <summary>撤销账号既有最后一个「角色 → 菜单」授权：空路径与已赋值路径的下一次请求立即收敛为权限不足。</summary>
    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial_on_all_paths()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var userId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false);

        foreach (var pathMode in new[] { "empty", "populated" })
            Data<PagedResult<SysUserView>>(
                await Bind(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        await RevokeUserMenuForUserAsync(db, userId);

        foreach (var pathMode in new[] { "empty", "populated" })
        {
            await AssertCodeAsync(ErrorCodes.Forbidden, () =>
                Bind(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetById(target.Id));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).Create(NewCreate()));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).Delete(target.Id));
        }

        Assert.False((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    /// <summary>账号在请求之间被禁用 / 删除：下一次请求立即分别按权限不足 / 未认证收敛，且不产生任何写入。</summary>
    [Fact]
    public async Task Disabled_and_deleted_identities_converge_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var userId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false);

        Data<PagedResult<SysUserView>>(
            await Bind(db, userId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        var actor = await db.SysUsers.SingleAsync(x => x.Id == userId);
        actor.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Bind(db, userId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, "populated").Create(NewCreate()));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Bind(db, userId, "populated").ToggleStatus(target.Id));

        actor.Status = UserStatus.Enabled;
        actor.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertCodeAsync(ErrorCodes.Unauthorized, () =>
            Bind(db, userId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => Bind(db, userId, "populated").Delete(target.Id));

        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 4. 拒绝矩阵：全部 7 条入口零写入 ====================

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
    public async Task Denied_identities_write_nothing_on_all_paths(
        string pathMode, string identity, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var ctl = Bind(db, await SeedActorForAsync(db, identity), pathMode);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewCreate()));
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

    // ==================== 5. 授权身份：既有读写契约与请求路径无关 ====================

    /// <summary>
    /// 既有启用身份 + 既有 <c>user</c> 菜单：既有读 / 写 / 软删除 / 重置密码契约在空路径与已赋值路径下
    /// 给出完全一致的结果（授权不因请求形状改变任何既有业务结果）。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Permitted_identity_lifecycle_is_path_independent(string pathMode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var actorId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false);
        var businessRoleId = await SeedBusinessRoleAsync(db);
        var target = await SeedTargetAsync(db);
        var ctl = Bind(db, actorId, pathMode);

        var page = Data<PagedResult<SysUserView>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }));
        Assert.Contains(page.Items, x => x.Id == target.Id);
        Assert.Equal(target.Email, Data<SysUserView>(await ctl.GetById(target.Id)).Email);

        var name = $"int-entry-{Tag()}";
        await ctl.Create(NewCreate(name, roleIds: new List<long> { businessRoleId }));
        var created = await db.SysUsers.AsNoTracking().SingleAsync(x => x.UserName == name);
        Assert.True(created.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("NewPass123", created.PasswordSalt, created.PasswordHash));
        Assert.True(await db.SysUserRoles.AsNoTracking()
            .AnyAsync(x => x.UserId == created.Id && x.RoleId == businessRoleId && !x.IsDeleted));

        await ctl.Update(created.Id, NewUpdate(displayName: "集成入口改名2", status: UserStatus.Disabled));
        var updated = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("集成入口改名2", updated.DisplayName);
        Assert.Equal(UserStatus.Disabled, updated.Status);
        Assert.Empty(await db.SysUserRoles.AsNoTracking()
            .Where(x => x.UserId == created.Id && !x.IsDeleted).ToListAsync());

        await ctl.ToggleStatus(created.Id);
        Assert.Equal(UserStatus.Enabled,
            (await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Status);

        await ctl.ResetPassword(created.Id, new ResetPasswordRequest { NewPassword = "ResetPass123" });
        var reset = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.True(reset.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("ResetPass123", reset.PasswordSalt, reset.PasswordHash));

        await ctl.Delete(created.Id);
        Assert.True((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    // ==================== 6. 角色关联写入入口受同一护栏约束 ====================

    /// <summary>
    /// 角色关联写入（<c>SysUserRoles</c> 行）受同一实时护栏约束：被拒绝身份无论载荷如何都不落任何行 / 授权；
    /// 授权身份下未知 / 已删除角色仍按受控参数错误整批拒绝（零写入）。空路径与已赋值路径口径一致。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Role_assignment_is_guarded_on_all_paths(string pathMode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var businessRoleId = await SeedBusinessRoleAsync(db);
        var deletedRoleId = await SeedBusinessRoleAsync(db, deleted: true);
        var deniedActorId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false, userMenu: false);
        var denied = Bind(db, deniedActorId, pathMode);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Create(NewCreate(roleIds: new List<long> { businessRoleId })));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Update(deniedActorId, NewUpdate(roleIds: new List<long> { businessRoleId })));
        Assert.Equal(before, await SnapshotAsync(db));

        var granted = Bind(db, await SeedActorAsync(db, UserStatus.Enabled, deleted: false), pathMode);
        var afterDenied = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewCreate(roleIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewCreate(roleIds: new List<long> { deletedRoleId })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewCreate(roleIds: new List<long> { -1 })));
        Assert.Equal(afterDenied, await SnapshotAsync(db));
    }

    // ==================== 7. 既有特权种子管理员（具备既有 user 菜单） ====================

    /// <summary>
    /// 特权（种子管理员，既有种子已授予全部菜单含 <c>user</c>）：既有只读契约同样放行
    /// —— 授权口径不因特权而跳过菜单检查，也不新增任何用户授权。
    /// </summary>
    [Fact]
    public async Task Privileged_seed_admin_with_user_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

        var page = Data<PagedResult<SysUserView>>(
            await Bind(db, adminId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page.Items);
        var page2 = Data<PagedResult<SysUserView>>(
            await Bind(db, adminId, "populated").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page2.Items);
    }

    /// <summary>回收指定账号的既有「用户管理」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeUserMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == SysUserAuthorizationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && menuIds.Contains(rm.MenuId))
            .ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// ERP-463 集成测试夹具：只创建一次性 GUID 独占的 <c>NEWERP_AUTOTEST</c> 库，绝不 drop / reset / 复用；
/// 访问数据库之前先复核目标必须为 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝，绝不读取 appsettings / .env / 生产凭据或生产数据。
/// </summary>
public sealed class SysUserEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_SYSUSERENTRYAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-463] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await InitialiseFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task InitialiseFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-463] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 user 菜单）。");
    }
}
