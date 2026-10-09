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
/// ERP-454 角色管理（<c>api/sys/roles</c> 分页 / 全部 / 按主键读取 / 角色菜单 / 新增 / 修改 / 删除）
/// 实时授权、有界字段校验与受控菜单分配的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「角色管理」（<c>role</c>）菜单与既有「角色 → 菜单」口径
/// 驱动真实 <see cref="RoleController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部 7 条路由</b> fail closed，
/// 且 <c>SysRoles</c> + <c>SysRoleMenus</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有角色菜单（含特权种子管理员）时既有读 / 写契约放行；请求之间撤销菜单立即收敛。</item>
/// <item><b>有界字段 / 菜单校验</b>：名称 / 编码空值越界、描述越界、菜单 Id 负数 / 未知 / 已删除
/// 在授权后仍 fail closed 且零写入（不落任何新行、不改写任何既有行）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class RoleMasterAuthorizationSqlServerTests
    : IClassFixture<RoleMasterAuthorizationSqlServerFixture>
{
    private readonly RoleMasterAuthorizationSqlServerFixture _fixture;

    public RoleMasterAuthorizationSqlServerTests(RoleMasterAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(RoleMasterAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static RoleController Controller(ErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextFor(userId) };

    /// <summary>
    /// 注入真实 HTTP 身份（可空 = 无 <c>NameIdentifier</c>）；<c>Request.Path</c> 已赋值以标记真实请求，
    /// 因此缺失身份也一律实时授权并 fail closed。
    /// </summary>
    private static ControllerContext ContextFor(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/sys/roles";
        return new ControllerContext { HttpContext = http };
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
        string name = "集成授权用例角色", string? code = null, string description = "集成描述",
        List<long>? menuIds = null)
        => new()
        {
            RoleName = name,
            RoleCode = code ?? $"int-role-{Tag()}",
            Description = description,
            MenuIds = menuIds ?? new List<long>()
        };

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

    /// <summary>播种被管理目标角色（分页 / 详情 / 角色菜单 / 修改 / 删除对象）。</summary>
    private static async Task<SysRole> SeedTargetRoleAsync(ErpDbContext db, bool isSystem = false)
    {
        var role = new SysRole
        {
            RoleName = "集成被管理角色",
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

    /// <summary>播种授权身份（可选状态 / 删除 / 既有角色菜单 / 系统内置角色）。</summary>
    private static async Task<long> SeedAuthUserAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool roleMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"rma-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "角色授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "角色授权集成测试角色",
            RoleCode = $"RMA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (roleMenu)
            await GrantMenuAsync(db, role.Id, RoleAuthorizationRules.RequiredMenuCode);
        return user.Id;
    }

    /// <summary>授予既有功能菜单（菜单由种子数据提供；缺失时按既有种子口径补建）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
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
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }
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

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 无菜单的身份：全部 7 条路由在读取或写入任何角色之前 fail closed，
    /// 且 <c>SysRoles</c> + <c>SysRoleMenus</c> 行逐字节不变（拒绝既不读取也不改写任何行）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Live_denies_every_route_without_reading_or_mutating_any_role_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetRoleAsync(db);
        await SeedMenuAsync(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedAuthUserAsync(db, UserStatus.Disabled, deleted: false, roleMenu: true),
            "deleted" => await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: true, roleMenu: true),
            _ => await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false, roleMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
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
        Assert.Equal("集成被管理角色", (await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).RoleName);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    /// <summary>具备既有「角色管理」菜单的身份：全部既有读 / 写契约（含全删全建菜单替换）放行。</summary>
    [Fact]
    public async Task Authorized_menu_granted_identity_permits_all_contracts()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);
        var target = await SeedTargetRoleAsync(db);
        var m1 = await SeedMenuAsync(db);
        var m2 = await SeedMenuAsync(db);
        var ctl = Controller(db, userId);

        var page = Data<PagedResult<SysRole>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Contains(page.Items, r => r.Id == target.Id);
        Assert.Contains(Data<List<SysRole>>(await ctl.GetAll()), r => r.Id == target.Id);
        Assert.Equal(target.Id, Data<SysRole>(await ctl.GetById(target.Id)).Id);
        Assert.Empty(Data<List<long>>(await ctl.GetRoleMenus(target.Id)));

        await ctl.Create(NewRequest(code: $"int-new-{Tag()}", menuIds: new List<long> { m1.Id, m2.Id }));
        var created = await db.SysRoles.AsNoTracking().SingleAsync(r => r.RoleCode.StartsWith("int-new-"));
        Assert.Equal(2, await db.SysRoleMenus.AsNoTracking().CountAsync(x => x.RoleId == created.Id));

        await ctl.Update(target.Id, NewRequest(name: "集成改名", code: target.RoleCode, menuIds: new List<long> { m2.Id }));
        var updated = await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal("集成改名", updated.RoleName);
        var links = await db.SysRoleMenus.AsNoTracking().Where(x => x.RoleId == target.Id).ToListAsync();
        Assert.Single(links);
        Assert.Equal(m2.Id, links[0].MenuId);

        await ctl.Delete(target.Id);
        Assert.True((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

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
            await Controller(db, adminId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page.Items);
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetRoleAsync(db);
        await SeedMenuAsync(db);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);

        Data<PagedResult<SysRole>>(await Controller(db, userId)
            .GetPaged(new PageQuery { Page = 1, PageSize = 10 }));   // 授权读取成功

        await RevokeRoleMenuForUserAsync(db, userId);                  // 撤销该账号的既有「角色 → 菜单」授权

        var before = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Controller(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).GetAll());
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).GetById(target.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).GetRoleMenus(target.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Create(NewRequest()));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Controller(db, userId).Update(target.Id, NewRequest(code: target.RoleCode)));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False((await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    // ==================== 4. 授权后有界字段 / 菜单校验仍 fail closed 且零写入 ====================

    [Fact]
    public async Task Authorized_identity_with_invalid_payloads_fails_closed_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetRoleAsync(db);
        var existingMenu = await SeedMenuAsync(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = target.Id, MenuId = existingMenu.Id });
        await db.SaveChangesAsync();
        var deletedMenu = await SeedMenuAsync(db, deleted: true);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);
        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(name: "")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(name: "   ")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            name: new string('N', RoleAuthorizationRules.MaxRoleNameLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(code: "")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            code: new string('C', RoleAuthorizationRules.MaxRoleCodeLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            description: new string('D', RoleAuthorizationRules.MaxDescriptionLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            menuIds: new List<long> { -1 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            menuIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewRequest(
            menuIds: new List<long> { deletedMenu.Id })));

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewRequest(
            name: new string('N', RoleAuthorizationRules.MaxRoleNameLength + 1), code: target.RoleCode)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewRequest(
            code: target.RoleCode, description: new string('D', RoleAuthorizationRules.MaxDescriptionLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewRequest(
            code: target.RoleCode, menuIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewRequest(
            code: target.RoleCode, menuIds: new List<long> { deletedMenu.Id })));

        Assert.Equal(before, await SnapshotAsync(db));

        // 既有菜单关联未被「全删全建」清空，角色字段未被改写。
        Assert.Single(db.SysRoleMenus.Where(x => x.RoleId == target.Id && x.MenuId == existingMenu.Id));
        Assert.Equal("集成被管理角色", (await db.SysRoles.AsNoTracking().SingleAsync(x => x.Id == target.Id)).RoleName);
    }
}

/// <summary>
/// ERP-454 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供角色管理实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class RoleMasterAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_ROLEMASTERAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-454] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-454] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 role 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class RoleMasterAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => RoleMasterAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
