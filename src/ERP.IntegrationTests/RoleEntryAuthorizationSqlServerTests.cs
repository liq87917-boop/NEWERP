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
/// ERP-464 角色管理（<c>api/sys/roles</c>）<b>入口授权</b>的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，绝不 drop / reset / 复用）。
/// <list type="number">
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>实时收敛</b>：禁用 / 已删除 / 撤销菜单的身份在请求之间立即收敛为拒绝（每次实时解析，绝不缓存）；</item>
/// <item><b>零写入</b>：被拒绝的调用不新增 / 不改写任何 <c>SysRoles</c> / <c>SysRoleMenus</c> 行；</item>
/// <item><b>既有契约不变</b>：真实既有启用身份 + 既有「角色管理」（<c>role</c>）菜单下，既有新增 / 分页 /
/// 全部 / 详情 / 角色菜单 / 修改 / 软删除与「全删全建」菜单替换语义全部保持。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class RoleEntryAuthorizationSqlServerTests
    : IClassFixture<RoleEntryAuthorizationSqlServerFixture>
{
    private readonly RoleEntryAuthorizationSqlServerFixture _fixture;

    public RoleEntryAuthorizationSqlServerTests(RoleEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(RoleEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

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

    private static RoleRequest NewRequest(
        string name = "集成入口授权角色", string? code = null, string description = "集成描述",
        List<long>? menuIds = null)
        => new()
        {
            RoleName = name,
            RoleCode = code ?? $"int-entry-{Tag()}",
            Description = description,
            MenuIds = menuIds ?? new List<long>()
        };

    /// <summary>角色主表 + 角色菜单关联表快照（被拒绝的调用必须逐字节不变）。</summary>
    private static async Task<string> SnapshotAsync(ErpDbContext db)
    {
        var roles = await db.SysRoles.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.RoleCode}:{x.RoleName}:{x.Description}:{x.IsSystem}:{x.IsDeleted}")
            .ToListAsync();
        var links = await db.SysRoleMenus.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.RoleId}:{x.MenuId}:{x.IsDeleted}")
            .ToListAsync();
        return string.Join("|", roles) + "##" + string.Join("|", links);
    }

    // ==================== 0. 播种辅助 ====================

    /// <summary>播种被管理的目标角色（分页 / 详情 / 角色菜单 / 修改 / 删除对象）。</summary>
    private static async Task<SysRole> SeedTargetRoleAsync(ErpDbContext db, bool isSystem = false)
    {
        var role = new SysRole
        {
            RoleName = "集成入口被管理角色",
            RoleCode = $"int-target-{Tag()}",
            Description = "集成描述",
            IsSystem = isSystem
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    /// <summary>播种一个菜单（可选软删除），用于角色菜单分配校验。</summary>
    private static async Task<SysMenu> SeedMenuAsync(ErpDbContext db, bool deleted = false)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = $"int-m-{Tag()}",
            MenuName = "集成测试菜单",
            MenuType = MenuType.Menu,
            SortOrder = 0,
            IsDeleted = deleted
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>播种一个既有授权身份（可选状态 / 删除 / 既有角色菜单 / 已撤销菜单 / 系统内置角色）。</summary>
    private static async Task<long> SeedActorAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool roleMenu = true, bool revoked = false, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"rea-{Tag()}",
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
            RoleCode = $"REA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (roleMenu)
            await GrantMenuAsync(db, role.Id, RoleAuthorizationRules.RequiredMenuCode, revoked);
        return user.Id;
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
        if (identity == "no-menu") return await SeedActorAsync(db, UserStatus.Enabled, deleted: false, roleMenu: false);
        if (identity == "revoked") return await SeedActorAsync(db, UserStatus.Enabled, deleted: false, revoked: true);
        return await SeedActorAsync(db, UserStatus.Enabled, deleted: false);
    }


    // ==================== 1. 读取入口：空路径 / 已赋值路径完全一致 ====================

    /// <summary>读取入口（分页 / 全部 / 按主键 / 角色菜单）在空路径与已赋值路径下的允许与拒绝矩阵。</summary>
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
        var target = await SeedTargetRoleAsync(db);
        var ctl = Bind(db, await SeedActorForAsync(db, identity), pathMode);

        if (expectedCode == 0)
        {
            var page = Data<PagedResult<SysRole>>(
                await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }));
            Assert.Contains(page.Items, x => x.Id == target.Id);
            Assert.Contains(Data<List<SysRole>>(await ctl.GetAll()), x => x.Id == target.Id);
            Assert.Equal(target.Id, Data<SysRole>(await ctl.GetById(target.Id)).Id);
            Assert.NotNull(Data<List<long>>(await ctl.GetRoleMenus(target.Id)));
        }
        else
        {
            await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }));
            await AssertCodeAsync(expectedCode, () => ctl.GetAll());
            await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
            await AssertCodeAsync(expectedCode, () => ctl.GetRoleMenus(target.Id));
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
        var target = await SeedTargetRoleAsync(db);
        await SeedMenuAsync(db);
        await SeedActorAsync(db, UserStatus.Enabled, deleted: false);
        var ctl = Bind(db, userId: null, "no-context");
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetById(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.GetRoleMenus(target.Id));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Create(NewRequest()));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => ctl.Update(target.Id, NewRequest(code: target.RoleCode)));
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
        var target = await SeedTargetRoleAsync(db);
        await SeedMenuAsync(db);
        var userId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false);

        foreach (var pathMode in new[] { "empty", "populated" })
            Data<PagedResult<SysRole>>(
                await Bind(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        await RevokeRoleMenuForUserAsync(db, userId);

        foreach (var pathMode in new[] { "empty", "populated" })
        {
            await AssertCodeAsync(ErrorCodes.Forbidden, () =>
                Bind(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetAll());
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetById(target.Id));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).GetRoleMenus(target.Id));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).Create(NewRequest()));
            await AssertCodeAsync(ErrorCodes.Forbidden, () =>
                Bind(db, userId, pathMode).Update(target.Id, NewRequest(code: target.RoleCode)));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, pathMode).Delete(target.Id));
        }

        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    /// <summary>账号在请求之间被禁用 / 删除：下一次请求立即分别按权限不足 / 未认证收敛，且不产生任何写入。</summary>
    [Fact]
    public async Task Disabled_and_deleted_identities_converge_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetRoleAsync(db);
        var userId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false);

        Data<PagedResult<SysRole>>(
            await Bind(db, userId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        var actor = await db.SysUsers.SingleAsync(x => x.Id == userId);
        actor.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Bind(db, userId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, "populated").Create(NewRequest()));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Bind(db, userId, "populated").Delete(target.Id));

        actor.Status = UserStatus.Enabled;
        actor.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertCodeAsync(ErrorCodes.Unauthorized, () =>
            Bind(db, userId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Unauthorized, () => Bind(db, userId, "populated").Delete(target.Id));

        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    // ==================== 4. 拒绝矩阵：全部入口零写入 ====================

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
    public async Task Denied_identities_write_nothing_on_all_paths(
        string pathMode, string identity, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetRoleAsync(db);
        await SeedMenuAsync(db);
        var ctl = Bind(db, await SeedActorForAsync(db, identity), pathMode);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetAll());
        await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.GetRoleMenus(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewRequest()));
        await AssertCodeAsync(expectedCode, () => ctl.Update(target.Id, NewRequest(code: target.RoleCode)));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }


    // ==================== 5. 授权身份：既有读写契约与请求路径无关 ====================

    /// <summary>
    /// 既有启用身份 + 既有 <c>role</c> 菜单：既有读 / 写 / 软删除契约与「全删全建」菜单替换在空路径与
    /// 已赋值路径下给出完全一致的结果（授权不因请求形状改变任何既有业务结果）。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Permitted_identity_lifecycle_is_path_independent(string pathMode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var actorId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false);
        var target = await SeedTargetRoleAsync(db);
        var m1 = await SeedMenuAsync(db);
        var m2 = await SeedMenuAsync(db);
        var ctl = Bind(db, actorId, pathMode);

        var page = Data<PagedResult<SysRole>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Contains(page.Items, x => x.Id == target.Id);
        Assert.Contains(Data<List<SysRole>>(await ctl.GetAll()), x => x.Id == target.Id);
        Assert.Equal(target.Id, Data<SysRole>(await ctl.GetById(target.Id)).Id);
        Assert.Empty(Data<List<long>>(await ctl.GetRoleMenus(target.Id)));

        var code = $"int-entry-{Tag()}";
        await ctl.Create(NewRequest(code: code, menuIds: new List<long> { m1.Id, m2.Id }));
        var created = await db.SysRoles.AsNoTracking().SingleAsync(x => x.RoleCode == code);
        Assert.False(created.IsSystem);
        Assert.Equal(2, await db.SysRoleMenus.AsNoTracking().CountAsync(x => x.RoleId == created.Id));

        await ctl.Update(created.Id, NewRequest(name: "集成入口改名", code: code, menuIds: new List<long> { m2.Id }));
        var updated = await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("集成入口改名", updated.RoleName);
        Assert.Equal(code, updated.RoleCode);
        var links = await db.SysRoleMenus.AsNoTracking().Where(x => x.RoleId == created.Id).ToListAsync();
        Assert.Single(links);
        Assert.Equal(m2.Id, links[0].MenuId);
        Assert.Equal(new[] { m2.Id }, Data<List<long>>(await ctl.GetRoleMenus(created.Id)));

        await ctl.Delete(created.Id);
        Assert.True((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }


    // ==================== 6. 角色菜单批量写入入口受同一护栏约束 ====================

    /// <summary>
    /// 角色 → 菜单批量写入（<c>SysRoleMenus</c> 行）受同一实时护栏约束：被拒绝身份无论载荷如何都不落任何行；
    /// 授权身份下未知 / 已删除 / 负数菜单仍按受控参数错误整批拒绝（零写入）。空路径与已赋值路径口径一致。
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Menu_assignment_is_guarded_on_all_paths(string pathMode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetRoleAsync(db);
        var knownMenu = await SeedMenuAsync(db);
        var deletedMenu = await SeedMenuAsync(db, deleted: true);
        var deniedActorId = await SeedActorAsync(db, UserStatus.Enabled, deleted: false, roleMenu: false);
        var denied = Bind(db, deniedActorId, pathMode);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Create(NewRequest(code: $"int-denied-{Tag()}", menuIds: new List<long> { knownMenu.Id })));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            denied.Update(target.Id, NewRequest(code: target.RoleCode, menuIds: new List<long> { knownMenu.Id })));
        Assert.Equal(before, await SnapshotAsync(db));

        var granted = Bind(db, await SeedActorAsync(db, UserStatus.Enabled, deleted: false), pathMode);
        var afterDenied = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewRequest(code: $"int-unknown-{Tag()}", menuIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewRequest(code: $"int-deleted-{Tag()}", menuIds: new List<long> { deletedMenu.Id })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Create(NewRequest(code: $"int-negative-{Tag()}", menuIds: new List<long> { -1 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            granted.Update(target.Id, NewRequest(code: target.RoleCode, menuIds: new List<long> { 999_999 })));
        Assert.Equal(afterDenied, await SnapshotAsync(db));
    }

    // ==================== 7. 既有特权种子管理员（具备既有 role 菜单） ====================

    /// <summary>
    /// 特权（种子管理员，既有种子已授予全部菜单含 <c>role</c>）：既有只读契约同样放行
    /// —— 授权口径不因特权而跳过菜单检查，也不新增任何用户授权。
    /// </summary>
    [Fact]
    public async Task Privileged_seed_admin_with_role_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

        var page = Data<PagedResult<SysRole>>(
            await Bind(db, adminId, "empty").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page.Items);
        var page2 = Data<PagedResult<SysRole>>(
            await Bind(db, adminId, "populated").GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page2.Items);
    }

    /// <summary>回收指定账号的既有「角色管理」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeRoleMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == RoleAuthorizationRules.RequiredMenuCode)
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
/// ERP-464 集成测试夹具：只创建一次性 GUID 独占的 <c>NEWERP_AUTOTEST</c> 库，绝不 drop / reset / 复用；
/// 访问数据库之前先复核目标必须为 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝，绝不读取 appsettings / .env / 生产凭据或生产数据。
/// </summary>
public sealed class RoleEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_ROLEENTRYAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-464] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-464] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 role 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class RoleEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => RoleEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

