using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
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
/// ERP-499 系统日志（<c>api/sys/logs</c>）实时身份 / 账号状态 / 既有 <c>sys-log</c> 菜单授权护栏的真实
/// SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item>真实控制器：分页查询在读取 / 计数任何 <c>SysOperationLogs</c> 行之前，解析实时身份与既有
/// <c>sys-log</c> 功能菜单，缺失 / 已删除按未认证，禁用 / 无菜单按权限不足，一律 fail closed 且不返回任何日志行；</item>
/// <item>真实身份 / 菜单：撤销既有菜单后下一次请求立即收敛为拒绝；</item>
/// <item>放行路径：种子管理员（具备既有全部菜单）与显式授予既有 <c>sys-log</c> 菜单的操作员均可持续分页读取，
/// 且保留既有筛选与 <c>ApiResponse&lt;PagedResult&lt;SysOperationLog&gt;&gt;</c> 响应契约；</item>
/// <item>调用形状无关：未绑定 <c>HttpContext</c> 的进程内直调同样先授权并 fail closed。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class OperationLogAuthorizationSqlServerTests : IClassFixture<OperationLogAuthorizationSqlServerFixture>
{
    private readonly OperationLogAuthorizationSqlServerFixture _fixture;

    public OperationLogAuthorizationSqlServerTests(OperationLogAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(OperationLogAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_without_returning_any_log()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedLogAsync(db);
        var before = await SnapshotAsync(db);

        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, userId: null).GetPaged(new PageQuery(), null, null, null, null, null));
        await AssertCode(ErrorCodes.Unauthorized,
            () => new OperationLogController(db).GetPaged(new PageQuery(), null, null, null, null, null));

        // 被拒绝的读取零写入：日志行数不变。
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedLogAsync(db);
        var before = await SnapshotAsync(db);
        var disabled = await SeedOperatorAsync(db, UserStatus.Disabled, deleted: false);
        var deleted = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: true);

        var forbidden = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, disabled).GetPaged(new PageQuery(), null, null, null, null, null));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Code);

        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, deleted).GetPaged(new PageQuery(), null, null, null, null, null));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Live_identity_without_sys_log_menu_is_forbidden()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedLogAsync(db);
        var before = await SnapshotAsync(db);
        var noMenu = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, noMenu).GetPaged(new PageQuery(), null, null, null, null, null));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        // 特权（系统内置角色）但不具备既有 sys-log 菜单：同样拒绝，无管理员兜底。
        var privileged = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false, system: true);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(db, privileged).GetPaged(new PageQuery(), null, null, null, null, null));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedLogAsync(db);
        var operatorId = await SeedGrantedOperatorAsync(db);

        Assert.IsType<OkObjectResult>(
            await NewController(db, operatorId).GetPaged(new PageQuery(), null, null, null, null, null));

        await RevokeMenusAsync(db, operatorId);
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(db, operatorId).GetPaged(new PageQuery(), null, null, null, null, null));
    }
    // ==================== 2. 放行路径（种子管理员 / 已授予既有菜单） ====================

    [Fact]
    public async Task Live_seeded_admin_with_sys_log_menu_can_read_logs()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var billNo = await SeedLogAsync(db);
        var adminId = await ResolveSeededAdminIdAsync(db);
        var ctl = NewController(db, adminId);

        var paged = AssertOk<PagedResult<SysOperationLog>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null, null, null));
        Assert.Contains(paged.Items, l => l.BillNo == billNo);

        var byBillNo = AssertOk<PagedResult<SysOperationLog>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, billNo, null, null));
        Assert.Equal(1, byBillNo.Total);
    }

    [Fact]
    public async Task Live_menu_granted_operator_keeps_existing_read_contract()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        // 共享夹具库：按本用例独有的用户名 / 模块 / 单据号断言精确命中（避免跨用例累计行）。
        var userName = $"erp499-{Guid.NewGuid():N}";
        var module = $"集成-{Guid.NewGuid():N}";
        var billNo = await SeedLogAsync(db, module: module, userName: userName);
        var operatorId = await SeedGrantedOperatorAsync(db);
        var ctl = NewController(db, operatorId);

        var all = AssertOk<PagedResult<SysOperationLog>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null, null, null));
        Assert.Contains(all.Items, l => l.BillNo == billNo);
        Assert.All(all.Items, l => Assert.False(l.IsDeleted));

        var byModule = AssertOk<PagedResult<SysOperationLog>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, module, null, null, null, null));
        Assert.Equal(1, byModule.Total);
        Assert.Equal(billNo, byModule.Items.Single().BillNo);

        var byUser = AssertOk<PagedResult<SysOperationLog>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, userName, null, null, null));
        Assert.Equal(1, byUser.Total);
        Assert.Equal(billNo, byUser.Items.Single().BillNo);

        var byKeyword = AssertOk<PagedResult<SysOperationLog>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50, Keyword = userName }, null, null, null, null, null));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal(billNo, byKeyword.Items.Single().BillNo);
    }

    [Fact]
    public async Task Live_request_path_and_filter_shape_do_not_change_the_decision()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedLogAsync(db);
        var operatorId = await SeedGrantedOperatorAsync(db);

        // 同一身份：空请求路径 / 已赋值请求路径、空筛选 / 已赋值筛选给出完全相同的放行。
        Assert.IsType<OkObjectResult>(await NewController(db, operatorId, requestPath: string.Empty)
            .GetPaged(new PageQuery(), null, null, null, null, null));
        Assert.IsType<OkObjectResult>(await NewController(db, operatorId, requestPath: "/api/sys/logs")
            .GetPaged(new PageQuery { Keyword = "erp499" }, "集成销售订单", "erp499", null, null, null));

        // 无身份：空请求路径 / 已赋值请求路径都按同一口径拒绝。
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, userId: null, requestPath: string.Empty)
            .GetPaged(new PageQuery(), null, null, null, null, null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, userId: null, requestPath: "/api/sys/logs")
            .GetPaged(new PageQuery { Keyword = "erp499" }, null, null, null, null, null));
    }


    // ==================== 3. 脚手架 ====================

    /// <summary>构造日志控制器并注入指定登录身份（可空 = 无身份）；Request.Path 由参数显式控制。</summary>
    private static OperationLogController NewController(
        IErpDbContext db, long? userId, string requestPath = "/api/sys/logs")
    {
        var controller = new OperationLogController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = requestPath;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>写入一条有界测试日志（GUID 单据号 / 默认 GUID 用户名，便于精准断言），返回其单据号。</summary>
    private static async Task<string> SeedLogAsync(ErpDbContext db, string module = "集成模块", string? userName = null)
    {
        var billNo = $"ERP499-{Guid.NewGuid():N}";
        db.SysOperationLogs.Add(new SysOperationLog
        {
            UserName = userName ?? $"erp499-{Guid.NewGuid():N}",
            Module = module,
            Action = "分页读取",
            Method = "GET",
            Path = "/api/sys/logs",
            BillNo = billNo,
            RequestBody = "{\"module\":null}",
            IpAddress = "203.0.113.199",
            StatusCode = 200,
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();
        return billNo;
    }

    /// <summary>播种账号（可选启用 / 删除 / 系统内置角色），返回用户 Id。</summary>
    private static async Task<long> SeedOperatorAsync(
        ErpDbContext db, UserStatus status, bool deleted, bool system = false)
    {
        var user = new SysUser
        {
            UserName = $"sys-log-op-{Guid.NewGuid():N}",
            DisplayName = "系统日志操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "系统日志操作员",
            RoleCode = $"SysLogOp-{Guid.NewGuid():N}",
            IsSystem = system
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>非特权操作员 + 既有「系统日志」菜单授权（复用 SeedData 已建菜单，不新增任何菜单）。</summary>
    private static async Task<long> SeedGrantedOperatorAsync(ErpDbContext db)
    {
        var userId = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);

        var roleId = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .FirstAsync();
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == OperationLogAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
        return userId;
    }

    /// <summary>撤销账号当前角色下的全部菜单授权（模拟授权撤销，验证下一次请求立即收敛）。</summary>
    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToListAsync();
        foreach (var grant in await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync())
        {
            grant.IsDeleted = true;
        }
    }

    /// <summary>日志行数（用于断言拒绝路径零写入 / 零新增）。</summary>
    private static Task<int> SnapshotAsync(ErpDbContext db)
        => db.SysOperationLogs.AsNoTracking().CountAsync();

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
/// 专用 localdb 目标 Fixture（ERP-499）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供系统日志授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class OperationLogAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SYSLOG_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-499] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-499] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class OperationLogAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => OperationLogAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => OperationLogAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

