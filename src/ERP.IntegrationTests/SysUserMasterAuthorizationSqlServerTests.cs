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
/// ERP-453 系统用户管理（<c>api/sys/users</c> 分页 / 按主键读取 / 新增 / 修改 / 切换状态 / 重置密码 / 删除）
/// 实时授权、有界字段校验与受控角色分配的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「用户管理」（<c>user</c>）菜单与既有「角色 → 菜单」口径
/// 驱动真实 <see cref="SysUserController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部 7 条路由</b> fail closed，
/// 且 <c>SysUsers</c> + <c>SysUserRoles</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有用户菜单（含特权种子管理员）时既有读 / 写契约放行；请求之间撤销菜单立即收敛。</item>
/// <item><b>有界字段 / 角色校验</b>：用户名 / 名称空值越界、状态越界、重置密码越界、角色负数 / 未知 / 已删除
/// 在授权后仍 fail closed 且零写入（不落任何新行、不改写任何既有行）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SysUserMasterAuthorizationSqlServerTests
    : IClassFixture<SysUserMasterAuthorizationSqlServerFixture>
{
    private readonly SysUserMasterAuthorizationSqlServerFixture _fixture;

    public SysUserMasterAuthorizationSqlServerTests(SysUserMasterAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SysUserMasterAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static SysUserController Controller(ErpDbContext db, long? userId)
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
        http.Request.Path = "/api/sys/users";
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

    private static SysUserCreateRequest NewCreate(
        string? userName = null, string password = "NewPass123",
        string displayName = "集成新用户", string email = "int-new@x.com", string phone = "13900000000",
        List<long>? roleIds = null)
        => new()
        {
            UserName = userName ?? $"int-{Tag()}",
            Password = password,
            DisplayName = displayName,
            Email = email,
            Phone = phone,
            RoleIds = roleIds ?? new List<long>()
        };

    private static SysUserUpdateRequest NewUpdate(
        string displayName = "集成改名", string email = "int-upd@x.com", string phone = "13700000000",
        UserStatus status = UserStatus.Enabled, List<long>? roleIds = null)
        => new()
        {
            DisplayName = displayName,
            Email = email,
            Phone = phone,
            Status = status,
            RoleIds = roleIds ?? new List<long>()
        };

    private static async Task<string> SnapshotAsync(ErpDbContext db)
    {
        var users = await db.SysUsers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.UserName}:{x.DisplayName}:{x.Email}:{x.Phone}:{x.Status}:" +
                         $"{x.IsDeleted}:{x.MustChangePassword}:{x.PasswordHash}")
            .ToListAsync();
        var links = await db.SysUserRoles.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.UserId}:{x.RoleId}:{x.IsDeleted}")
            .ToListAsync();
        return string.Join("|", users) + "##" + string.Join("|", links);
    }

    // ==================== 0. 播种辅助 ====================

    /// <summary>播种被管理目标用户（分页 / 详情 / 修改 / 切换 / 重置 / 删除对象）。</summary>
    private static async Task<SysUser> SeedTargetAsync(ErpDbContext db, string? userName = null)
    {
        var salt = PasswordHasher.GenerateSalt();
        var user = new SysUser
        {
            UserName = userName ?? $"int-target-{Tag()}",
            DisplayName = "集成被管理用户",
            Email = "int-target@x.com",
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

    /// <summary>播种授权身份（可选状态 / 删除 / 既有用户菜单 / 系统内置角色）。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool userMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"sua-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "用户授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "用户授权集成测试角色",
            RoleCode = $"SUA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (userMenu)
            await GrantMenuAsync(db, role.Id, SysUserAuthorizationRules.RequiredMenuCode);
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

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 无菜单的身份：全部 7 条路由在读取或写入任何用户之前 fail closed，
    /// 且 <c>SysUsers</c> + <c>SysUserRoles</c> 行逐字节不变（拒绝既不读取也不改写任何行）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Live_denies_every_route_without_reading_or_mutating_any_user_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, userMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, userMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, userMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetById(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewCreate()));
        await AssertCodeAsync(expectedCode, () => ctl.Update(target.Id, NewUpdate()));
        await AssertCodeAsync(expectedCode, () => ctl.ToggleStatus(target.Id));
        await AssertCodeAsync(expectedCode, () => ctl.ResetPassword(
            target.Id, new ResetPasswordRequest { NewPassword = "NewPass123" }));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(target.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(UserStatus.Enabled, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Authorized_identity_allows_existing_contracts()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false);
        var ctl = Controller(db, userId);

        var role = new SysRole { RoleName = "集成业务角色", RoleCode = $"SUA-BIZ-{Tag()}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var name = $"int-{Tag()}";
        await ctl.Create(NewCreate(userName: name, roleIds: new List<long> { role.Id }));
        var created = await db.SysUsers.AsNoTracking().SingleAsync(x => x.UserName == name);
        Assert.True(created.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("NewPass123", created.PasswordSalt, created.PasswordHash));
        Assert.True(await db.SysUserRoles.AsNoTracking().AnyAsync(x => x.UserId == created.Id && x.RoleId == role.Id));

        var page = Data<PagedResult<SysUserView>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Contains(page.Items, x => x.UserName == name);
        Assert.Contains(role.Id, Data<SysUserView>(await ctl.GetById(created.Id)).RoleIds);

        await ctl.Update(created.Id, NewUpdate(displayName: "集成改名", status: UserStatus.Disabled));
        var updated = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("集成改名", updated.DisplayName);
        Assert.Equal(UserStatus.Disabled, updated.Status);

        await ctl.ToggleStatus(created.Id);
        Assert.Equal(UserStatus.Enabled, (await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Status);

        await ctl.ResetPassword(created.Id, new ResetPasswordRequest { NewPassword = "ResetPass123" });
        var reset = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.True(reset.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("ResetPass123", reset.PasswordSalt, reset.PasswordHash));

        await ctl.Delete(created.Id);
        Assert.True((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

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
            await Controller(db, adminId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page.Items);
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false);

        Data<PagedResult<SysUserView>>(await Controller(db, userId)
            .GetPaged(new PageQuery { Page = 1, PageSize = 10 }));  // 授权读取成功

        await RevokeUserMenuForUserAsync(db, userId);                 // 撤销该账号的既有「角色 → 菜单」授权

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Controller(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).GetById(target.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Create(NewCreate()));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(target.Id));

        Assert.False((await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id)).IsDeleted);
    }

    // ==================== 4. 授权后有界字段 / 角色校验仍 fail closed 且零写入 ====================

    [Fact]
    public async Task Authorized_identity_with_invalid_payloads_fails_closed_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var target = await SeedTargetAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false);
        var deletedRole = new SysRole { RoleName = "集成已删除角色", RoleCode = $"SUA-GONE-{Tag()}", IsDeleted = true };
        db.SysRoles.Add(deletedRole);
        await db.SaveChangesAsync();
        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(userName: "")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(userName: "   ")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            userName: new string('U', SysUserAuthorizationRules.MaxUserNameLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            displayName: new string('D', SysUserAuthorizationRules.MaxDisplayNameLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            email: new string('E', SysUserAuthorizationRules.MaxEmailLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            phone: new string('P', SysUserAuthorizationRules.MaxPhoneLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(password: "12345")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            password: new string('p', SysUserAuthorizationRules.MaxPasswordLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            roleIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            roleIds: new List<long> { -1 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewCreate(
            roleIds: new List<long> { deletedRole.Id })));

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewUpdate(
            displayName: new string('D', SysUserAuthorizationRules.MaxDisplayNameLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewUpdate(
            status: (UserStatus)2)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewUpdate(
            roleIds: new List<long> { 999_999 })));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(target.Id, NewUpdate(
            roleIds: new List<long> { deletedRole.Id })));

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.ResetPassword(
            target.Id, new ResetPasswordRequest { NewPassword = "12345" }));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.ResetPassword(
            target.Id, new ResetPasswordRequest { NewPassword = new string('p', SysUserAuthorizationRules.MaxPasswordLength + 1) }));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.SysUsers.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.True(PasswordHasher.VerifyPassword("OldPass123", stored.PasswordSalt, stored.PasswordHash));
    }
}

/// <summary>
/// ERP-453 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供系统用户管理实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class SysUserMasterAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_SYSUSERMASTERAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-453] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-453] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 user 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SysUserMasterAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SysUserMasterAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
