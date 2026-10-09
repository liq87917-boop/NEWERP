using ERP.Api.Controllers;
using ERP.Api.Services;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-460 钉钉通知（<c>api/sys/dingtalk</c>）实时身份 / 既有 dingtalk-config / dingtalk-log 菜单 /
/// 白名单配置取值护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <para>覆盖：配置读取 / 保存 / 测试发送 / 手动推送提醒与发送记录列表 / 重发 / 删除在读取配置、发送消息或
/// 读取 / 重发 / 软删除任何发送记录之前解析实时身份与既有功能菜单（缺失 / 已删除按未认证，禁用 / 无菜单按
/// 权限不足，撤销后立即收敛），种子管理员与显式授予既有菜单的操作员可读写 / 重发 / 软删除，未知配置键与
/// 超长取值按既有受控错误拒绝且零写入，被拒绝的读取不返回任何 Webhook / 访问令牌 / 加签密钥。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；
/// 绝不 drop / reset / 复用任何数据库，连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class DingTalkAuthorizationSqlServerTests : IClassFixture<DingTalkAuthorizationSqlServerFixture>
{
    private readonly DingTalkAuthorizationSqlServerFixture _fixture;

    /// <summary>控制器路由前缀（模拟真实 HTTP 请求管线的 <c>Request.Path</c>，缺失身份的真实请求一律 fail closed）。</summary>
    private const string BasePath = "/api/sys/dingtalk";

    public DingTalkAuthorizationSqlServerTests(DingTalkAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(DingTalkAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_every_route_without_mutating_any_row()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedConfigAsync(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=abc");
        var log = await SeedLogAsync(db, "SO-ANON");
        var paramsBefore = await SnapshotParamsAsync(db);
        var logsBefore = await SnapshotLogsAsync(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetConfig());
        await AssertCode(ErrorCodes.Unauthorized,
            () => ctl.SaveConfig(new Dictionary<string, string> { [DingTalkService.KeyEnabled] = "true" }));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Test(null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.SendFollowUpReminder());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetLogs());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Resend(log.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.DeleteLog(log.Id));

        await AssertUnchangedAsync(db, paramsBefore, logsBefore);
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedConfigAsync(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=abc");
        var log = await SeedLogAsync(db, "SO-ID");
        var disabled = await SeedUserAsync(db, UserStatus.Disabled, deleted: false, configMenu: true, logMenu: true);
        var deleted = await SeedUserAsync(db, UserStatus.Enabled, deleted: true, configMenu: true, logMenu: true);
        var paramsBefore = await SnapshotParamsAsync(db);
        var logsBefore = await SnapshotLogsAsync(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetConfig());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetLogs());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Resend(log.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetConfig());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).DeleteLog(log.Id));

        await AssertUnchangedAsync(db, paramsBefore, logsBefore);
    }

    [Fact]
    public async Task Live_identity_without_required_menus_is_forbidden_on_every_route()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedConfigAsync(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=abc");
        var log = await SeedLogAsync(db, "SO-NOMENU");
        var noMenu = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: false);
        var paramsBefore = await SnapshotParamsAsync(db);
        var logsBefore = await SnapshotLogsAsync(db);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetConfig());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetLogs());
        await AssertCode(ErrorCodes.Forbidden,
            () => ctl.SaveConfig(new Dictionary<string, string> { [DingTalkService.KeyEnabled] = "true" }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Test(null));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.SendFollowUpReminder());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Resend(log.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.DeleteLog(log.Id));

        await AssertUnchangedAsync(db, paramsBefore, logsBefore);
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedConfigAsync(db, DingTalkService.KeyEnabled, "true");
        var log = await SeedLogAsync(db, "SO-REVOKE");
        var operatorId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: true);
        var ctl = NewController(db, operatorId);

        Assert.IsType<OkObjectResult>(await ctl.GetConfig());
        Assert.IsType<OkObjectResult>(await ctl.GetLogs());

        await RevokeMenusAsync(db, operatorId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetConfig());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetLogs());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.DeleteLog(log.Id));
    }

    // ==================== 2. 放行路径（种子管理员 / 已授予既有菜单） ====================

    [Fact]
    public async Task Live_seeded_admin_with_dingtalk_menus_can_read_write_resend_and_delete()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        await EnsureNoWebhookConfiguredAsync(db);
        var resendTarget = await SeedLogAsync(db, "SO-ADMIN-RESEND");
        var deleteTarget = await SeedLogAsync(db, "SO-ADMIN-DELETE");
        var ctl = NewController(db, adminId);

        var config = AssertOk<Dictionary<string, string>>(await ctl.GetConfig());
        Assert.True(config.ContainsKey(DingTalkService.KeyEnabled));

        AssertOkResult(await ctl.SaveConfig(new Dictionary<string, string>
        {
            [DingTalkService.KeyEnabled] = "true",
            [DingTalkService.KeyMsgType] = "text"
        }));
        var stored = await db.SysParameters.AsNoTracking()
            .SingleAsync(p => p.ParamKey == DingTalkService.KeyEnabled && !p.IsDeleted);
        Assert.Equal("true", stored.ParamValue);

        Assert.IsType<OkObjectResult>(await ctl.GetLogs());
        Assert.IsType<OkObjectResult>(await ctl.Resend(resendTarget.Id));
        Assert.Equal(1, (await db.SysDingTalkLogs.AsNoTracking()
            .SingleAsync(l => l.Id == resendTarget.Id)).RetryCount);

        AssertOkResult(await ctl.DeleteLog(deleteTarget.Id));
        Assert.True((await db.SysDingTalkLogs.AsNoTracking()
            .SingleAsync(l => l.Id == deleteTarget.Id)).IsDeleted);
    }

    [Fact]
    public async Task Live_granted_config_operator_can_read_and_save_and_log_operator_can_list_resend_and_delete()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await EnsureNoWebhookConfiguredAsync(db);
        var configOnly = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var logOnly = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);

        // 配置侧已授予 dingtalk-config：可读可保存；记录侧仍拒绝。
        var configCtl = NewController(db, configOnly);
        Assert.IsType<OkObjectResult>(await configCtl.GetConfig());
        AssertOkResult(await configCtl.SaveConfig(new Dictionary<string, string> { [DingTalkService.KeyEnabled] = "false" }));
        Assert.IsType<OkObjectResult>(await configCtl.Test(null));
        Assert.IsType<OkObjectResult>(await configCtl.SendFollowUpReminder());
        await AssertCode(ErrorCodes.Forbidden, () => configCtl.GetLogs());

        // 记录侧已授予 dingtalk-log：可读可重发可软删除；配置侧仍拒绝。
        var resendTarget = await SeedLogAsync(db, "SO-OP-RESEND");
        var deleteTarget = await SeedLogAsync(db, "SO-OP-DELETE");
        var logCtl = NewController(db, logOnly);
        Assert.IsType<OkObjectResult>(await logCtl.GetLogs());
        Assert.IsType<OkObjectResult>(await logCtl.Resend(resendTarget.Id));
        Assert.Equal(1, (await db.SysDingTalkLogs.AsNoTracking()
            .SingleAsync(l => l.Id == resendTarget.Id)).RetryCount);
        AssertOkResult(await logCtl.DeleteLog(deleteTarget.Id));
        Assert.True((await db.SysDingTalkLogs.AsNoTracking()
            .SingleAsync(l => l.Id == deleteTarget.Id)).IsDeleted);
        await AssertCode(ErrorCodes.Forbidden, () => logCtl.GetConfig());
    }

    // ==================== 3. 白名单取值校验与记录存在性 ====================

    [Fact]
    public async Task Live_unknown_config_key_and_over_long_value_are_rejected_without_persisting()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var ctl = NewController(db, operatorId);
        var before = await SnapshotParamsAsync(db);
        var logsBefore = await SnapshotLogsAsync(db);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.SaveConfig(
            new Dictionary<string, string> { ["DingTalk_BadKey"] = "v" }));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.SaveConfig(
            new Dictionary<string, string>
            {
                [DingTalkService.KeyWebhook] = "https://oapi.dingtalk.com/robot/send?access_token=abcdef123456",
                ["DingTalk_Extra"] = "x"
            }));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.SaveConfig(
            new Dictionary<string, string>
            {
                [DingTalkService.KeyEnabled] = new string('x', DingTalkAuthorizationRules.MaxParamValueLength + 1)
            }));

        await AssertUnchangedAsync(db, before, logsBefore);
        Assert.False(await db.SysParameters.AsNoTracking().AnyAsync(p => p.ParamKey == DingTalkService.KeyWebhook));
    }

    [Fact]
    public async Task Live_delete_unknown_or_deleted_record_is_not_found_without_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);
        var softDeleted = await SeedLogAsync(db, "SO-ALREADY-DELETED");
        softDeleted.IsDeleted = true;
        await db.SaveChangesAsync();
        var ctl = NewController(db, operatorId);
        var before = await SnapshotLogsAsync(db);

        await AssertCode(ErrorCodes.NotFound, () => ctl.DeleteLog(987654321));
        await AssertCode(ErrorCodes.NotFound, () => ctl.DeleteLog(softDeleted.Id));

        Assert.Equal(before, await SnapshotLogsAsync(db));
    }

    // ==================== 4. 源码 / 白名单 / 菜单契约 ====================

    [Fact]
    public void Source_controller_authorizes_before_every_route_and_reuses_existing_menus()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "DingTalkController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("DingTalkAuthorizationRules.EnsureConfigAuthorizedAsync", source);
        Assert.Contains("DingTalkAuthorizationRules.EnsureLogAuthorizedAsync", source);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureConfigAuthorizedAsync\(\);").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureLogAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);
        Assert.DoesNotContain("Request.Path", source);

        Assert.Equal(
            DingTalkService.AllKeys.OrderBy(x => x, StringComparer.Ordinal),
            DingTalkAuthorizationRules.AllowedConfigKeys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("dingtalk-config", DingTalkAuthorizationRules.ConfigMenuCode);
        Assert.Equal("钉钉通知配置", DingTalkAuthorizationRules.ConfigMenuText);
        Assert.Equal("dingtalk-log", DingTalkAuthorizationRules.LogMenuCode);
        Assert.Equal("钉钉发送记录", DingTalkAuthorizationRules.LogMenuText);

        var upgrader = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.Contains("dingtalk-config", upgrader);
        Assert.Contains("dingtalk-log", upgrader);
    }


    // ==================== 5. 脚手架 ====================

    /// <summary>构造钉钉通知控制器（真实 SQL 上下文）并注入指定登录身份（可空 = 无身份）。</summary>
    private static DingTalkController NewController(ErpDbContext db, long? userId)
    {
        var dingTalk = new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance);
        var controller = new DingTalkController(db, dingTalk, NewReminder(db));
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = BasePath;   // 模拟真实 HTTP 请求管线：缺失身份的真实请求一律 fail closed
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>
    /// 构造提醒服务（内部作用域解析同一个 SQL 上下文与钉钉服务）：无 Webhook / 无到期跟进时不会发起外部请求。
    /// </summary>
    private static FollowUpReminderService NewReminder(ErpDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IErpDbContext>(db);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
        services.AddSingleton(sp => new DingTalkService(
            sp.GetRequiredService<IErpDbContext>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<DingTalkService>.Instance));
        var provider = services.BuildServiceProvider();
        return new FollowUpReminderService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FollowUpReminderService>.Instance);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>播种一条钉钉配置（落在既有 SysParameter 表）。</summary>
    private static async Task<SysParameter> SeedConfigAsync(ErpDbContext db, string key, string value)
    {
        var p = new SysParameter { ParamKey = key, ParamName = key, ParamValue = value, Description = "" };
        db.SysParameters.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>清空已配置的 Webhook（测试前置，确保重发 / 测试发送不会真的发起外部请求）。</summary>
    private static async Task EnsureNoWebhookConfiguredAsync(ErpDbContext db)
    {
        var rows = await db.SysParameters
            .Where(p => p.ParamKey == DingTalkService.KeyWebhook && !p.IsDeleted).ToListAsync();
        foreach (var row in rows) row.ParamValue = string.Empty;
        if (rows.Count > 0) await db.SaveChangesAsync();
    }

    /// <summary>播种一条钉钉发送记录（落在既有 SysDingTalkLog 表）。</summary>
    private static async Task<SysDingTalkLog> SeedLogAsync(ErpDbContext db, string billNo)
    {
        var log = new SysDingTalkLog
        {
            BillType = "sales-order",
            BillTypeName = "销售订单",
            BillNo = billNo,
            ActionCode = "audit",
            ActionName = "审核",
            Operator = "tester",
            Title = "标题",
            Content = "内容",
            MsgType = "text",
            Webhook = "oapi.dingtalk.com/robot/send?access_token=****",
            Success = true
        };
        db.SysDingTalkLogs.Add(log);
        await db.SaveChangesAsync();
        return log;
    }

    /// <summary>播种账号（可选启用 / 删除 / 既有 dingtalk-config 与 dingtalk-log 菜单授权），返回用户 Id。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool configMenu, bool logMenu)
    {
        var user = new SysUser
        {
            UserName = $"dingtalk-{Guid.NewGuid():N}",
            DisplayName = "钉钉通知操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "钉钉通知操作员",
            RoleCode = $"DingTalkRole-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (configMenu)
            await GrantMenuAsync(db, role.Id, DingTalkAuthorizationRules.ConfigMenuCode);
        if (logMenu)
            await GrantMenuAsync(db, role.Id, DingTalkAuthorizationRules.LogMenuCode);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（复用 SchemaUpgrader 已建菜单，不新增任何菜单）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        if (grants.Count > 0) await db.SaveChangesAsync();
    }

    private static async Task<List<string>> SnapshotParamsAsync(ErpDbContext db)
    {
        var rows = await db.SysParameters.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        return rows.Select(p => $"{p.Id}|{p.ParamKey}|{p.ParamValue}|{p.IsDeleted}").ToList();
    }

    private static async Task<List<string>> SnapshotLogsAsync(ErpDbContext db)
    {
        var rows = await db.SysDingTalkLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        return rows.Select(l => $"{l.Id}|{l.BillNo}|{l.RetryCount}|{l.Success}|{l.Webhook}|{l.IsDeleted}").ToList();
    }

    private static async Task AssertUnchangedAsync(ErpDbContext db, List<string> paramsBefore, List<string> logsBefore)
    {
        Assert.Equal(paramsBefore, await SnapshotParamsAsync(db));
        Assert.Equal(logsBefore, await SnapshotLogsAsync(db));
    }

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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-460）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供钉钉通知授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class DingTalkAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_DINGTALK_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-460] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-460] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class DingTalkAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => DingTalkAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => DingTalkAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

