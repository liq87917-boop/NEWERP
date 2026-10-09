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
/// ERP-456 用户参数（<c>api/sys/user-parameters</c>）实时身份 / 既有 user-parameter 菜单 / 持久化列与用户解析
/// 有界校验的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item>真实控制器：分页 / 主键详情 / 新增 / 修改 / 删除在读取 / 计数 / 写入任何用户参数行之前，解析实时身份与既有
/// <c>user-parameter</c> 功能菜单，缺失 / 已删除按未认证，禁用 / 无菜单按权限不足，一律 fail closed 且不新增 / 不改写任何用户参数行；</item>
/// <item>真实身份 / 菜单：撤销既有菜单后下一次请求立即收敛为拒绝；</item>
/// <item>放行路径：种子管理员（具备既有全部菜单）与显式授予既有 <c>user-parameter</c> 菜单的操作员均可持续读写；</item>
/// <item>有界校验：空 / 超长参数键、超长参数值、未知 / 已删除用户 Id、重复 (UserId,ParamKey) 按既有受控错误拒绝，
/// 且被拒绝时零写入 / 零改写；软删除只标记 <c>IsDeleted</c>。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SysUserParameterAuthorizationSqlServerTests
    : IClassFixture<SysUserParameterAuthorizationSqlServerFixture>
{
    private readonly SysUserParameterAuthorizationSqlServerFixture _fixture;

    public SysUserParameterAuthorizationSqlServerTests(SysUserParameterAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SysUserParameterAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_every_route_without_mutating_any_user_parameter()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var owner = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var row = await SeedParamAsync(db, owner, UniqueKey("UP-ANON"), "v");
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewParam(owner, UniqueKey("UP-ANON-NEW"))));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var owner = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var row = await SeedParamAsync(db, owner, UniqueKey("UP-ID"), "v");
        var disabled = await SeedOperatorAsync(db, UserStatus.Disabled, deleted: false);
        var deleted = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: true);
        var before = await SnapshotAsync(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewParam(owner, UniqueKey("UP-D"))));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_identity_without_user_parameter_menu_is_forbidden_on_every_route()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var owner = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var row = await SeedParamAsync(db, owner, UniqueKey("UP-NOMENU"), "v");
        var noMenu = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewParam(owner, UniqueKey("UP-NOMENU-NEW"))));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedGrantedOperatorAsync(db);
        var ctl = NewController(db, operatorId);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery()));

        RevokeMenus(db, operatorId);
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(1));
    }

    // ==================== 2. 放行路径（种子管理员 / 已授予既有菜单） ====================

    [Fact]
    public async Task Live_seeded_admin_with_user_parameter_menu_can_read_and_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var seeded = await SeedParamAsync(db, adminId, UniqueKey("UP-ADMIN"), "v");
        var ctl = NewController(db, adminId);

        var paged = AssertOk<PagedResult<UserParameterView>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, p => p.Id == seeded.Id);

        var createdKey = UniqueKey("UP-ADMIN-NEW");
        AssertOkResult(await ctl.Create(NewParam(adminId, createdKey, "42")));
        var created = await db.SysUserParameters.AsNoTracking().SingleAsync(p => p.ParamKey == createdKey);
        Assert.True(created.Id > 0);
        Assert.Equal(adminId, created.UserId);

        AssertOkResult(await ctl.Update(created.Id, NewParam(adminId, createdKey, "43")));
        var stored = await db.SysUserParameters.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal("43", stored.ParamValue);
    }

    [Fact]
    public async Task Live_menu_granted_operator_can_read_and_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedGrantedOperatorAsync(db);
        var seeded = await SeedParamAsync(db, operatorId, UniqueKey("UP-OP"), "v");
        var ctl = NewController(db, operatorId);

        Assert.Contains(AssertOk<PagedResult<UserParameterView>>(await ctl.GetPaged(new PageQuery())).Items,
            p => p.Id == seeded.Id);
        Assert.Equal("v", AssertOk<SysUserParameter>(await ctl.GetById(seeded.Id)).ParamValue);

        var createdKey = UniqueKey("UP-OP-NEW");
        AssertOkResult(await ctl.Create(NewParam(operatorId, createdKey)));
        Assert.True(await db.SysUserParameters.AsNoTracking().AnyAsync(p => p.ParamKey == createdKey));
    }

    // ==================== 3. 持久化列 + 用户解析有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task Live_invalid_payloads_are_rejected_without_mutating_any_user_parameter()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var seeded = await SeedParamAsync(db, adminId, UniqueKey("UP-KEEP"), "keep");
        var dupKey = UniqueKey("UP-DUP");
        await SeedParamAsync(db, adminId, dupKey, "v");
        var deletedOwner = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: true);
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, adminId);

        // 持久化列 + 用户解析有界（新增）。
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(adminId, string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(adminId, new string('K', 101))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(adminId, UniqueKey("UP-V"), new string('v', 501))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(0, UniqueKey("UP-NOUSER"))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(999999999, UniqueKey("UP-UNKNOWN"))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(deletedOwner, UniqueKey("UP-DELUSER"))));
        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewParam(adminId, dupKey)));

        // 持久化列 + 用户解析有界（修改）。
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(adminId, string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(adminId, new string('K', 101))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(seeded.Id, NewParam(adminId, seeded.ParamKey, new string('v', 501))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(999999999, seeded.ParamKey, "x")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(deletedOwner, seeded.ParamKey, "x")));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_soft_delete_marks_row_without_physical_delete()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var row = await SeedParamAsync(db, adminId, UniqueKey("UP-SOFT"), "v");
        var ctl = NewController(db, adminId);

        AssertOkResult(await ctl.Delete(row.Id));
        var stored = await db.SysUserParameters.AsNoTracking().SingleAsync(p => p.Id == row.Id);
        Assert.True(stored.IsDeleted);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(row.Id));
    }

    // ==================== 4. 脚手架 ====================

    private static UserParameterController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new UserParameterController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = "/api/sys/user-parameters";
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctl;
    }

    private static SysUserParameter NewParam(long userId, string paramKey, string paramValue = "v")
        => new() { UserId = userId, ParamKey = paramKey, ParamValue = paramValue };

    private static async Task<SysUserParameter> SeedParamAsync(ErpDbContext db, long userId, string key, string value)
    {
        var p = NewParam(userId, key, value);
        db.SysUserParameters.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task<long> SeedOperatorAsync(ErpDbContext db, UserStatus status, bool deleted)
    {
        var user = new SysUser
        {
            UserName = $"user-param-op-{Guid.NewGuid():N}",
            DisplayName = "用户参数操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>非特权操作员 + 既有「用户参数」菜单授权（复用 SeedData 已建菜单，不新增任何菜单）。</summary>
    private static async Task<long> SeedGrantedOperatorAsync(ErpDbContext db)
    {
        var userId = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var role = new SysRole
        {
            RoleName = "用户参数操作员",
            RoleCode = $"UserParamOp-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == SysUserParameterAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();
        return userId;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
    }

    private static async Task<List<string>> SnapshotAsync(ErpDbContext db)
    {
        var rows = await db.SysUserParameters.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        return rows.Select(p => $"{p.Id}|{p.UserId}|{p.ParamKey}|{p.ParamValue}|{p.IsDeleted}").ToList();
    }

    private static async Task AssertUnchangedAsync(ErpDbContext db, List<string> before)
        => Assert.Equal(before, await SnapshotAsync(db));

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static void AssertOkResult(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    private static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-456）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供用户参数授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class SysUserParameterAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SYSUSERPARAMETER_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-456] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
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

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁既有夹具或他人的数据库。
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

        Console.WriteLine("[ERP-456] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SysUserParameterAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SysUserParameterAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => SysUserParameterAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
