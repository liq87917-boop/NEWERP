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
/// ERP-457 客户端限制（<c>api/sys/client-limits</c>）实时身份 / 既有 client-limit 菜单 / 限制形状有界校验的真实
/// SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item>真实控制器：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除在读取 / 计数 / 写入任何限制行之前，解析实时身份与既有
/// <c>client-limit</c> 功能菜单，缺失 / 已删除按未认证，禁用 / 无菜单按权限不足，一律 fail closed 且不新增 / 不改写任何限制行；</item>
/// <item>真实身份 / 菜单：撤销既有菜单后下一次请求立即收敛为拒绝；</item>
/// <item>放行路径：种子管理员（具备既有全部菜单）与显式授予既有 <c>client-limit</c> 菜单的操作员均可持续读写；</item>
/// <item>限制形状校验：未知限制类型、空 / 超长限制值与超长备注按既有受控错误拒绝，且被拒绝时零写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ClientLimitAuthorizationSqlServerTests : IClassFixture<ClientLimitAuthorizationSqlServerFixture>
{
    private readonly ClientLimitAuthorizationSqlServerFixture _fixture;

    public ClientLimitAuthorizationSqlServerTests(ClientLimitAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ClientLimitAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_every_route_without_mutating_any_limit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedLimitAsync(db, "203.0.113.10");
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewLimit("203.0.113.11")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewLimit("203.0.113.12")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.BatchDelete(new List<long> { row.Id }));

        await AssertUnchangedAsync(db, before);
        Assert.False(await db.SysClientLimits.AsNoTracking().AnyAsync(l => l.Id == row.Id && l.IsDeleted));
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedLimitAsync(db, "203.0.113.20");
        var disabled = await SeedOperatorAsync(db, UserStatus.Disabled, deleted: false);
        var deleted = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: true);
        var before = await SnapshotAsync(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewLimit("203.0.113.21")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_identity_without_client_limit_menu_is_forbidden_on_every_route()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedLimitAsync(db, "203.0.113.30");
        var noMenu = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewLimit("203.0.113.31")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewLimit("203.0.113.32")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { row.Id }));

        await AssertUnchangedAsync(db, before);
        Assert.False(await db.SysClientLimits.AsNoTracking().AnyAsync(l => l.Id == row.Id && l.IsDeleted));
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedLimitAsync(db, "203.0.113.40");
        var operatorId = await SeedGrantedOperatorAsync(db);
        var ctl = NewController(db, operatorId);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery()));

        RevokeMenus(db, operatorId);
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
    }

    // ==================== 2. 放行路径（种子管理员 / 已授予既有菜单） ====================

    [Fact]
    public async Task Live_seeded_admin_with_client_limit_menu_can_read_and_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var seeded = await SeedLimitAsync(db, "203.0.113.50");
        var ctl = NewController(db, adminId);

        var paged = AssertOk<PagedResult<SysClientLimit>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, l => l.Id == seeded.Id);

        var created = AssertOk<SysClientLimit>(await ctl.Create(NewLimit("203.0.113.51")));
        Assert.True(created.Id > 0);

        var updated = AssertOk<SysClientLimit>(
            await ctl.Update(created.Id, NewLimit("203.0.113.52", ClientLimitType.MachineCode, "机器码")));
        Assert.Equal(ClientLimitType.MachineCode, updated.LimitType);

        Assert.IsType<OkObjectResult>(await ctl.Delete(created.Id));
        Assert.True(await db.SysClientLimits.AsNoTracking().AnyAsync(l => l.Id == created.Id && l.IsDeleted));
    }

    [Fact]
    public async Task Live_menu_granted_operator_can_read_and_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var seeded = await SeedLimitAsync(db, "203.0.113.60");
        var operatorId = await SeedGrantedOperatorAsync(db);
        var ctl = NewController(db, operatorId);

        Assert.Contains(AssertOk<List<SysClientLimit>>(await ctl.GetAll()), l => l.Id == seeded.Id);

        var created = AssertOk<SysClientLimit>(await ctl.Create(NewLimit("203.0.113.61")));
        Assert.True(created.Id > 0);

        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { created.Id }));
        Assert.True(await db.SysClientLimits.AsNoTracking().AnyAsync(l => l.Id == created.Id && l.IsDeleted));
    }

    // ==================== 3. 限制形状有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task Live_invalid_payloads_are_rejected_without_mutating_any_limit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var seeded = await SeedLimitAsync(db, "203.0.113.70");
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewLimit("203.0.113.71", limitType: (ClientLimitType)999)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewLimit(string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewLimit(new string('9', 201))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewLimit("203.0.113.72", remark: new string('R', 501))));

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(seeded.Id, NewLimit("203.0.113.73", limitType: (ClientLimitType)0)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewLimit(new string('8', 201))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(seeded.Id, NewLimit("203.0.113.74", remark: new string('R', 501))));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 4. 脚手架 ====================

    private static ClientLimitController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ClientLimitController(new GenericService<SysClientLimit>(db), db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return ctl;
    }

    private static SysClientLimit NewLimit(
        string limitValue,
        ClientLimitType limitType = ClientLimitType.IP,
        string remark = "集成客户端限制")
        => new()
        {
            LimitType = limitType,
            LimitValue = limitValue,
            IsEnabled = true,
            Remark = remark
        };

    private static async Task<SysClientLimit> SeedLimitAsync(
        ErpDbContext db, string limitValue, ClientLimitType limitType = ClientLimitType.IP)
    {
        var limit = NewLimit(limitValue, limitType);
        db.SysClientLimits.Add(limit);
        await db.SaveChangesAsync();
        return limit;
    }

    private static async Task<long> SeedOperatorAsync(ErpDbContext db, UserStatus status, bool deleted)
    {
        var user = new SysUser
        {
            UserName = $"client-limit-op-{Guid.NewGuid():N}",
            DisplayName = "客户端限制操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>非特权操作员 + 既有「客户端限制」菜单授权（复用 SeedData 已建菜单，不新增任何菜单）。</summary>
    private static async Task<long> SeedGrantedOperatorAsync(ErpDbContext db)
    {
        var userId = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);

        var role = new SysRole
        {
            RoleName = "客户端限制操作员",
            RoleCode = $"ClientLimitOp-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == ClientLimitAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
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
        var rows = await db.SysClientLimits.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        return rows
            .Select(l => $"{l.Id}|{l.LimitType}|{l.LimitValue}|{l.IsEnabled}|{l.Remark}|{l.IsDeleted}")
            .ToList();
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

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-457）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供客户端限制授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class ClientLimitAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_CLIENTLIMIT_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-457] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-457] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ClientLimitAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ClientLimitAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ClientLimitAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

