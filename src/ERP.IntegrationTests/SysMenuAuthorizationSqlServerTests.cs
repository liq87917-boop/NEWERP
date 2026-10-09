using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-455 菜单管理（<c>api/sys/menus</c> 读树 / 新增 / 修改 / 删除）实时授权、有界字段校验与父级完整性
/// 的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「用户权限」（<c>user-permission</c>）/ 既有「角色管理」（<c>role</c>）
/// 菜单与既有「角色 → 菜单」口径驱动真实 <see cref="MenuController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部 4 条路由</b> fail closed，
/// 且 <c>SysMenus</c> 行逐字节不变（拒绝在路由体读取 / 改写任何菜单之前抛错）。</item>
/// <item><b>读 / 写授权分离</b>：读树接受既有「用户权限」或既有「角色管理」任一；新增 / 修改 / 删除只接受
/// 既有「用户权限」；请求之间撤销菜单立即收敛。</item>
/// <item><b>有界字段 / 父级校验</b>：编码 / 名称空值越界、路径 / 图标 / 权限编码越界、未知菜单类型、
/// 父级负数 / 未知 / 已删除在授权后仍 fail closed 且零写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SysMenuAuthorizationSqlServerTests
    : IClassFixture<SysMenuAuthorizationSqlServerFixture>
{
    private readonly SysMenuAuthorizationSqlServerFixture _fixture;

    public SysMenuAuthorizationSqlServerTests(SysMenuAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SysMenuAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static MenuController Controller(ErpDbContext db, long? userId)
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
        http.Request.Path = "/api/sys/menus/tree";
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

    private static SysMenu NewMenu(
        string code = "int-menu", string name = "集成新菜单", long parentId = 0,
        string path = "/int/new", string icon = "Plus", MenuType type = MenuType.Menu,
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

    /// <summary>菜单主表快照（授权 / 校验拒绝后必须逐字节不变）。</summary>
    private static async Task<string> SnapshotAsync(ErpDbContext db) =>
        string.Join("|", await db.SysMenus.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => $"{m.Id}:{m.ParentId}:{m.MenuName}:{m.MenuCode}:{m.Path}:{m.Icon}:{m.SortOrder}:{(int)m.MenuType}:{m.PermissionCode}:{m.IsDeleted}")
            .ToListAsync());

    // ==================== 0. 播种辅助 ====================

    /// <summary>播种被管理目标菜单（读树 / 修改 / 删除对象）。</summary>
    private static async Task<SysMenu> SeedTargetMenuAsync(
        ErpDbContext db, MenuType type = MenuType.Menu, long parentId = 0, bool deleted = false)
    {
        var menu = new SysMenu
        {
            ParentId = parentId,
            MenuCode = $"int-t-{Tag()}",
            MenuName = "集成被管理菜单",
            Path = "/int/target",
            Icon = "Target",
            SortOrder = 1,
            MenuType = type,
            IsDeleted = deleted
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>播种授权身份（可选状态 / 删除 / 既有功能菜单 / 系统内置角色），返回用户 Id。</summary>
    private static async Task<long> SeedAuthUserAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool writeMenu = true, bool readAlternateMenu = false, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"sma-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "菜单授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "菜单授权集成测试角色",
            RoleCode = $"SMA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (writeMenu)
            await GrantMenuAsync(db, role.Id, SysMenuAuthorizationRules.WriteRequiredMenuCode);
        if (readAlternateMenu)
            await GrantMenuAsync(db, role.Id, SysMenuAuthorizationRules.ReadAlternateMenuCode);
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

    /// <summary>回收指定账号的既有菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeMenuForUserAsync(ErpDbContext db, long userId, string menuCode)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode)
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
    /// 缺失 / 禁用 / 已删除 / 无菜单的身份：全部 4 条路由在读取或写入任何菜单之前 fail closed，
    /// 且 <c>SysMenus</c> 行逐字节不变（拒绝在路由体读取 / 改写任何菜单之前抛错）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Live_denies_every_route_without_mutating_any_menu_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetMenuAsync(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedAuthUserAsync(db, UserStatus.Disabled, deleted: false, writeMenu: true),
            "deleted" => await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: true, writeMenu: true),
            _ => await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false, writeMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetTree());
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewMenu(code: $"int-new-{Tag()}")));
        await AssertCodeAsync(expectedCode, () => ctl.Update(target.Id,
            NewMenu(code: target.MenuCode, name: target.MenuName)));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False((await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
        Assert.Equal("集成被管理菜单", (await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id)).MenuName);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    /// <summary>具备既有「用户权限」菜单的身份：读树 / 新增 / 修改 / 删除既有契约全部放行。</summary>
    [Fact]
    public async Task Authorized_identity_with_user_permission_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var parent = await SeedTargetMenuAsync(db, type: MenuType.Directory);
        var target = await SeedTargetMenuAsync(db);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);
        var ctl = Controller(db, userId);

        var tree = Data<List<MenuTreeNode>>(await ctl.GetTree());
        Assert.Contains(tree, n => n.MenuCode == target.MenuCode);

        await ctl.Create(NewMenu(code: $"int-created-{Tag()}", name: "集成新建菜单", parentId: parent.Id));
        var created = await db.SysMenus.AsNoTracking().SingleAsync(m => m.MenuName == "集成新建菜单");
        Assert.Equal(parent.Id, created.ParentId);

        await ctl.Update(target.Id, NewMenu(code: target.MenuCode, name: "集成改名菜单", path: "/int/renamed"));
        var updated = await db.SysMenus.AsNoTracking().SingleAsync(m => m.Id == target.Id);
        Assert.Equal("集成改名菜单", updated.MenuName);
        Assert.Equal("/int/renamed", updated.Path);

        await ctl.Delete(target.Id);
        Assert.True((await db.SysMenus.AsNoTracking().SingleAsync(m => m.Id == target.Id)).IsDeleted);
    }

    /// <summary>只具备既有「角色管理」菜单：读树放行，但新增 / 修改 / 删除仍 fail closed 且零写入。</summary>
    [Fact]
    public async Task Read_alternate_menu_allows_tree_but_denies_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetMenuAsync(db);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false,
            writeMenu: false, readAlternateMenu: true);
        var ctl = Controller(db, userId);

        var tree = Data<List<MenuTreeNode>>(await ctl.GetTree());
        Assert.Contains(tree, n => n.MenuCode == target.MenuCode);

        var before = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Create(NewMenu(code: $"int-new-{Tag()}")));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Update(target.Id,
            NewMenu(code: target.MenuCode, name: target.MenuName)));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Delete(target.Id));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    /// <summary>特权（种子管理员，既有种子已授予全部菜单）：具备既有菜单授权时只读契约放行。</summary>
    [Fact]
    public async Task Privileged_seed_admin_with_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

        var tree = Data<List<MenuTreeNode>>(await Controller(db, adminId).GetTree());
        Assert.NotNull(tree);
        Assert.NotEmpty(tree);
    }


    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    /// <summary>请求之间撤销既有「用户权限」菜单授权：全部 4 条路由下一次请求立即拒绝且零写入。</summary>
    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetMenuAsync(db);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);

        Data<List<MenuTreeNode>>(await Controller(db, userId).GetTree());                  // 授权读取成功
        await RevokeMenuForUserAsync(db, userId, SysMenuAuthorizationRules.WriteRequiredMenuCode);

        var before = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).GetTree());
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Create(NewMenu(code: $"int-new-{Tag()}")));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Update(target.Id,
            NewMenu(code: target.MenuCode, name: target.MenuName)));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False((await db.SysMenus.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    // ==================== 4. 授权后有界字段 / 父级校验仍 fail closed 且零写入 ====================

    /// <summary>授权后非法新增载荷（编码 / 名称空值越界、路径 / 图标 / 权限编码越界、未知菜单类型、父级负数 / 未知 / 已删除）拒绝且零写入。</summary>
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
    public async Task Authorized_identity_with_invalid_create_payload_fails_closed_without_writes(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var deletedParent = await SeedTargetMenuAsync(db, deleted: true);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);
        var ctl = Controller(db, userId);

        SysMenu Payload() => scenario switch
        {
            "empty-code" => NewMenu(code: ""),
            "code-too-long" => NewMenu(code: new string('C', SysMenuAuthorizationRules.MaxMenuCodeLength + 1)),
            "empty-name" => NewMenu(name: ""),
            "name-too-long" => NewMenu(name: new string('N', SysMenuAuthorizationRules.MaxMenuNameLength + 1)),
            "path-too-long" => NewMenu(path: "/" + new string('p', SysMenuAuthorizationRules.MaxPathLength)),
            "icon-too-long" => NewMenu(icon: new string('I', SysMenuAuthorizationRules.MaxIconLength + 1)),
            "permission-too-long" => NewMenu(permissionCode: new string('P', SysMenuAuthorizationRules.MaxPermissionCodeLength + 1)),
            "unknown-type" => NewMenu(type: (MenuType)0),
            "negative-parent" => NewMenu(parentId: -1),
            "unknown-parent" => NewMenu(parentId: 999_999),
            _ => NewMenu(parentId: deletedParent.Id)
        };

        var before = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, await SnapshotAsync(db));
    }


    /// <summary>授权后非法修改载荷（编码 / 名称空值越界、路径 / 图标 / 权限编码越界、未知菜单类型、父级负数 / 未知 / 已删除）拒绝且不改写任何既有行。</summary>
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
    public async Task Authorized_identity_with_invalid_update_payload_fails_closed_without_writes(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetMenuAsync(db);
        var deletedParent = await SeedTargetMenuAsync(db, deleted: true);
        var userId = await SeedAuthUserAsync(db, UserStatus.Enabled, deleted: false);
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
            _ => NewMenu(code: target.MenuCode, parentId: deletedParent.Id)
        };

        var before = await SnapshotAsync(db);
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, Payload()));
        Assert.Equal(before, await SnapshotAsync(db));

        var stored = await db.SysMenus.AsNoTracking().SingleAsync(m => m.Id == target.Id);
        Assert.Equal("集成被管理菜单", stored.MenuName);
        Assert.Equal("/int/target", stored.Path);
        Assert.Equal(0, stored.ParentId);
    }
}

/// <summary>
/// ERP-455 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供菜单管理实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class SysMenuAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_SYSMENUAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-455] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-455] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 user-permission / role 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SysMenuAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SysMenuAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
