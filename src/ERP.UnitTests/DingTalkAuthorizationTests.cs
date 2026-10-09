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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-460 钉钉通知（<c>api/sys/dingtalk</c>）实时身份 / 既有 dingtalk-config / dingtalk-log 菜单 /
/// 白名单配置取值护栏单元测试。
/// <para>覆盖：配置读取 / 保存 / 测试发送 / 手动推送提醒与发送记录列表 / 重发 / 删除在读取配置、发送消息或
/// 读取 / 重发 / 软删除任何发送记录之前，对缺失 / 已删除 / 禁用身份与无既有功能菜单身份 fail closed
/// （无匿名 / 管理员兜底）；撤销菜单后立即收敛；已授予既有菜单的账号可读可写；保存对未知配置键与超长取值
/// 返回既有受控错误且不落任何配置行；被拒绝的读取不泄露 Webhook / 访问令牌 / 加签密钥；删除不存在 /
/// 已软删除记录不产生任何变更。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。</para>
/// </summary>
public class DingTalkAuthorizationTests
{
    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 全部路由_无身份_一律未认证且不读取或改写任何行()
    {
        using var db = TestDbFactory.Create();
        SeedConfig(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=abc");
        var log = SeedLog(db, "SO-ANON");
        var paramsBefore = SnapshotParams(db);
        var logsBefore = SnapshotLogs(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetConfig());
        await AssertCode(ErrorCodes.Unauthorized,
            () => ctl.SaveConfig(new Dictionary<string, string> { [DingTalkService.KeyEnabled] = "true" }));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Test(null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.SendFollowUpReminder());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetLogs());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Resend(log.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.DeleteLog(log.Id));

        AssertUnchanged(db, paramsBefore, logsBefore);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedConfig(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=abc");
        var log = SeedLog(db, "SO-ID");
        var disabled = SeedUser(db, UserStatus.Disabled, deleted: false, configMenu: true, logMenu: true);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true, configMenu: true, logMenu: true);
        var paramsBefore = SnapshotParams(db);
        var logsBefore = SnapshotLogs(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetConfig());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetLogs());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Resend(log.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetConfig());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).DeleteLog(log.Id));

        AssertUnchanged(db, paramsBefore, logsBefore);
    }

    [Fact]
    public async Task 缺少配置菜单_配置侧路由拒绝_缺少记录菜单_记录侧路由拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedConfig(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=abc");
        var log = SeedLog(db, "SO-NOMENU");
        var configOnly = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var logOnly = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);
        var paramsBefore = SnapshotParams(db);
        var logsBefore = SnapshotLogs(db);

        // 只有 dingtalk-config：记录侧三个路由拒绝。
        var configCtl = NewController(db, configOnly);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => configCtl.GetLogs());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => configCtl.Resend(log.Id));
        await AssertCode(ErrorCodes.Forbidden, () => configCtl.DeleteLog(log.Id));

        // 只有 dingtalk-log：配置侧四个路由拒绝。
        var logCtl = NewController(db, logOnly);
        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => logCtl.GetConfig());
        Assert.Equal(ErrorCodes.Forbidden, ex2.Code);
        Assert.Contains("模块授权", ex2.Message);
        await AssertCode(ErrorCodes.Forbidden,
            () => logCtl.SaveConfig(new Dictionary<string, string> { [DingTalkService.KeyEnabled] = "true" }));
        await AssertCode(ErrorCodes.Forbidden, () => logCtl.Test(null));
        await AssertCode(ErrorCodes.Forbidden, () => logCtl.SendFollowUpReminder());

        AssertUnchanged(db, paramsBefore, logsBefore);
    }

    [Fact]
    public async Task 撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedConfig(db, DingTalkService.KeyEnabled, "true");
        var log = SeedLog(db, "SO-REVOKE");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: true);
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetConfig());
        Assert.IsType<OkObjectResult>(await ctl.GetLogs());

        RevokeMenus(db, user);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetConfig());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetLogs());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.DeleteLog(log.Id));
    }

    [Fact]
    public async Task 特权账号_缺少既有菜单_仍按权限不足拒绝_无管理员兜底()
    {
        using var db = TestDbFactory.Create();
        SeedConfig(db, DingTalkService.KeyEnabled, "true");
        var log = SeedLog(db, "SO-PRIV-NOMENU");
        // 系统内置角色（特权）但不授予任何钉钉菜单：不得因特权而绕过既有功能菜单。
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false,
            configMenu: false, logMenu: false, systemRole: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetConfig());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetLogs());

        // 显式授予既有 dingtalk-config 菜单后配置侧立即放行（记录侧仍需 dingtalk-log）。
        GrantMenu(db, PrimaryRoleId(db, privileged),
            DingTalkAuthorizationRules.ConfigMenuCode, DingTalkAuthorizationRules.ConfigMenuText);
        Assert.IsType<OkObjectResult>(await NewController(db, privileged).GetConfig());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetLogs());

        GrantMenu(db, PrimaryRoleId(db, privileged),
            DingTalkAuthorizationRules.LogMenuCode, DingTalkAuthorizationRules.LogMenuText);
        Assert.IsType<OkObjectResult>(await NewController(db, privileged).GetLogs());
        Assert.NotNull(log);
    }


    // ==================== 2. 放行路径（已授予既有菜单的账号） ====================

    [Fact]
    public async Task 已授予配置菜单_可读配置_可保存白名单键且保留既有保存语义()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        SeedConfig(db, DingTalkService.KeyEnabled, "true");
        var ctl = NewController(db, user);

        // 读取：既有契约返回全部配置键。
        var ok = Assert.IsType<OkObjectResult>(await ctl.GetConfig());
        var cfg = Assert.IsType<ApiResponse<Dictionary<string, string>>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, cfg.Code);
        Assert.True(cfg.Data!.ContainsKey(DingTalkService.KeyWebhook));
        Assert.Equal("true", cfg.Data[DingTalkService.KeyEnabled]);

        // 保存：白名单键缺失即建、已有即改写（既有保存语义）。
        AssertOkResult(await ctl.SaveConfig(new Dictionary<string, string>
        {
            [DingTalkService.KeyWebhook] = "https://oapi.dingtalk.com/robot/send?access_token=abcdef123456",
            [DingTalkService.KeyEnabled] = "false"
        }));

        var webhook = db.SysParameters.Single(p => p.ParamKey == DingTalkService.KeyWebhook);
        Assert.Equal("https://oapi.dingtalk.com/robot/send?access_token=abcdef123456", webhook.ParamValue);
        var enabled = db.SysParameters.Single(p => p.ParamKey == DingTalkService.KeyEnabled);
        Assert.Equal("false", enabled.ParamValue);
        Assert.NotNull(enabled.UpdatedAt);
    }

    [Fact]
    public async Task 已授予记录菜单_可读记录_可重发_可软删除_且无配置菜单不能读配置()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);
        var resendTarget = SeedLog(db, "SO-RESEND");
        var deleteTarget = SeedLog(db, "SO-DELETE");
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetLogs());

        // 重发（未配置 Webhook：按既有语义发送失败但记录重试次数累加，证明已进入既有重发路径）。
        Assert.IsType<OkObjectResult>(await ctl.Resend(resendTarget.Id));
        Assert.Equal(1, db.SysDingTalkLogs.Single(l => l.Id == resendTarget.Id).RetryCount);

        // 软删除。
        AssertOkResult(await ctl.DeleteLog(deleteTarget.Id));
        Assert.True(db.SysDingTalkLogs.Single(l => l.Id == deleteTarget.Id).IsDeleted);

        // 无 dingtalk-config 菜单：配置侧读取拒绝。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetConfig());
    }

    [Fact]
    public async Task 已授予配置菜单_可测试发送与手动推送提醒_且无记录菜单不能读记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var log = SeedLog(db, "SO-TEST");
        var ctl = NewController(db, user);

        // 未配置 Webhook：测试发送按既有语义返回失败响应体（不抛出），授权已放行。
        Assert.IsType<OkObjectResult>(await ctl.Test(null));

        // 手动推送提醒：无到期跟进时按既有语义返回失败响应体（不抛出），授权已放行。
        Assert.IsType<OkObjectResult>(await ctl.SendFollowUpReminder());

        // 无 dingtalk-log 菜单：记录侧读取拒绝。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetLogs());
        Assert.NotNull(log);
    }


    // ==================== 3. 白名单配置取值有界校验（拒绝且不落库） ====================

    [Fact]
    public async Task 保存配置_未知键_拒绝且不落任何配置行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var ctl = NewController(db, user);
        var before = SnapshotParams(db);
        var logsBefore = SnapshotLogs(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SaveConfig(
            new Dictionary<string, string> { ["DingTalk_BadKey"] = "v", ["OtherKey"] = "v2" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.DoesNotContain("DingTalk_BadKey", ex.Message);
        Assert.DoesNotContain("OtherKey", ex.Message);

        AssertUnchanged(db, before, logsBefore);
    }

    [Fact]
    public async Task 保存配置_白名单键与未知键混合_整体拒绝且不落任何配置行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var ctl = NewController(db, user);
        var before = SnapshotParams(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SaveConfig(new Dictionary<string, string>
        {
            [DingTalkService.KeyWebhook] = "https://oapi.dingtalk.com/robot/send?access_token=abcdef123456",
            ["DingTalk_Extra"] = "x"
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertUnchanged(db, before, SnapshotLogs(db));
        Assert.False(db.SysParameters.Any(p => p.ParamKey == DingTalkService.KeyWebhook));
    }

    [Fact]
    public async Task 保存配置_超长取值_拒绝且不落任何配置行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: true, logMenu: false);
        var ctl = NewController(db, user);
        var before = SnapshotParams(db);
        var tooLong = new string('x', DingTalkAuthorizationRules.MaxParamValueLength + 1);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SaveConfig(
            new Dictionary<string, string> { [DingTalkService.KeyWebhook] = tooLong }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("500", ex.Message);

        AssertUnchanged(db, before, SnapshotLogs(db));
        Assert.False(db.SysParameters.Any(p => p.ParamKey == DingTalkService.KeyWebhook));
    }

    // ==================== 4. 发送记录删除的存在性与非删除性 ====================

    [Fact]
    public async Task 删除记录_不存在_抛NotFound且不改变任何记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);
        var ctl = NewController(db, user);
        var before = SnapshotLogs(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.DeleteLog(987654321));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(before, SnapshotLogs(db));
    }

    [Fact]
    public async Task 删除记录_已软删除_抛NotFound且不改变任何记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);
        var log = SeedLog(db, "SO-DELETED");
        log.IsDeleted = true;
        db.SaveChanges();
        var ctl = NewController(db, user);
        var before = SnapshotLogs(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.DeleteLog(log.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(before, SnapshotLogs(db));
    }

    // ==================== 5. 被拒绝的读取不泄露任何敏感取值 ====================

    [Fact]
    public async Task 缺少配置菜单_读取配置不泄露Webhook与加签密钥()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, configMenu: false, logMenu: true);
        SeedConfig(db, DingTalkService.KeyWebhook, "https://oapi.dingtalk.com/robot/send?access_token=TOPSECRETTOKEN");
        SeedConfig(db, DingTalkService.KeySecret, "SIGN-SECRET-XYZ");
        var before = SnapshotParams(db);
        var ctl = NewController(db, user);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetConfig());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.DoesNotContain("TOPSECRETTOKEN", ex.Message);
        Assert.DoesNotContain("SIGN-SECRET-XYZ", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Test(null));
        await AssertCode(ErrorCodes.Forbidden,
            () => ctl.SaveConfig(new Dictionary<string, string> { [DingTalkService.KeyEnabled] = "true" }));

        AssertUnchanged(db, before, SnapshotLogs(db));
    }


    // ==================== 6. 源码 / 白名单 / 菜单契约 ====================

    [Fact]
    public void 控制器源码契约_全部路由先授权_复用既有钉钉菜单()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "DingTalkController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("DingTalkAuthorizationRules.EnsureConfigAuthorizedAsync", source);
        Assert.Contains("DingTalkAuthorizationRules.EnsureLogAuthorizedAsync", source);
        Assert.Equal(4, Regex.Matches(source, @"await EnsureConfigAuthorizedAsync\(\);").Count);
        Assert.Equal(3, Regex.Matches(source, @"await EnsureLogAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);
        // 绝不依据请求路径 / 环境 / 空请求绕过检查。
        Assert.DoesNotContain("Request.Path", source);
        Assert.DoesNotContain("IsDevelopment", source);
    }

    [Fact]
    public void 白名单与菜单契约_与既有DingTalkService和SchemaUpgrader同源()
    {
        Assert.Equal(
            DingTalkService.AllKeys.OrderBy(x => x, StringComparer.Ordinal),
            DingTalkAuthorizationRules.AllowedConfigKeys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(8, DingTalkAuthorizationRules.AllowedConfigKeys.Length);

        Assert.Equal("dingtalk-config", DingTalkAuthorizationRules.ConfigMenuCode);
        Assert.Equal("钉钉通知配置", DingTalkAuthorizationRules.ConfigMenuText);
        Assert.Equal("dingtalk-log", DingTalkAuthorizationRules.LogMenuCode);
        Assert.Equal("钉钉发送记录", DingTalkAuthorizationRules.LogMenuText);

        var upgrader = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.Contains("dingtalk-config", upgrader);
        Assert.Contains("dingtalk-log", upgrader);
        Assert.Contains("钉钉通知配置", upgrader);
        Assert.Contains("钉钉发送记录", upgrader);
    }

    [Fact]
    public void 取值护栏纯函数_白名单精确匹配与长度上界()
    {
        foreach (var key in DingTalkService.AllKeys)
            Assert.True(DingTalkAuthorizationRules.IsAllowedConfigKey(key));

        Assert.False(DingTalkAuthorizationRules.IsAllowedConfigKey("dingtalk_webhook"));
        Assert.False(DingTalkAuthorizationRules.IsAllowedConfigKey("DingTalk_Other"));
        Assert.False(DingTalkAuthorizationRules.IsAllowedConfigKey(""));
        Assert.False(DingTalkAuthorizationRules.IsAllowedConfigKey(null));
        Assert.Equal(500, DingTalkAuthorizationRules.MaxParamValueLength);
        Assert.Equal(100, DingTalkAuthorizationRules.MaxParamKeyLength);
    }


    // ==================== 7. 测试辅助 ====================

    /// <summary>构造钉钉通知控制器（内存库）并注入指定登录身份（可空 = 无身份）。</summary>
    private static DingTalkController NewController(ErpDbContext db, long? userId)
    {
        var dingTalk = new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance);
        var controller = new DingTalkController(db, dingTalk, NewReminder(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>
    /// 构造提醒服务（内部作用域解析同一个内存库与钉钉服务）：无 Webhook / 无到期跟进时不会发起外部请求。
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
    private static SysParameter SeedConfig(ErpDbContext db, string key, string value)
    {
        var p = new SysParameter { ParamKey = key, ParamName = key, ParamValue = value, Description = "" };
        db.SysParameters.Add(p);
        db.SaveChanges();
        return p;
    }

    /// <summary>播种一条钉钉发送记录（落在既有 SysDingTalkLog 表）。</summary>
    private static SysDingTalkLog SeedLog(ErpDbContext db, string billNo)
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
        db.SaveChanges();
        return log;
    }

    /// <summary>播种账号（可选启用 / 删除 / 系统内置角色 / 既有 dingtalk-config 与 dingtalk-log 菜单授权），返回用户 Id。</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status, bool deleted,
        bool configMenu, bool logMenu, bool systemRole = false)
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
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "钉钉通知操作员",
            RoleCode = $"DingTalkRole-{Guid.NewGuid():N}",
            IsSystem = systemRole
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (configMenu)
            GrantMenu(db, role.Id, DingTalkAuthorizationRules.ConfigMenuCode, DingTalkAuthorizationRules.ConfigMenuText);
        if (logMenu)
            GrantMenu(db, role.Id, DingTalkAuthorizationRules.LogMenuCode, DingTalkAuthorizationRules.LogMenuText);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单）。</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = menuCode,
                MenuName = menuName,
                MenuType = MenuType.Menu,
                Path = $"/{menuCode}"
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>账号当前主角色 Id（仅用于「授予既有菜单后放行」等场景）。</summary>
    private static long PrimaryRoleId(ErpDbContext db, long userId)
        => db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).First();

    /// <summary>撤销账号当前角色下的全部菜单授权（模拟授权撤销，验证下一次请求立即收敛）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>钉钉配置行快照（用于断言拒绝路径不新增 / 不改写任何参数行）。</summary>
    private static List<string> SnapshotParams(ErpDbContext db)
        => db.SysParameters.AsNoTracking().OrderBy(p => p.Id).ToList()
            .Select(p => $"{p.Id}|{p.ParamKey}|{p.ParamValue}|{p.IsDeleted}")
            .ToList();

    /// <summary>钉钉发送记录快照（用于断言拒绝路径不新增 / 不改写 / 不软删除任何记录）。</summary>
    private static List<string> SnapshotLogs(ErpDbContext db)
        => db.SysDingTalkLogs.AsNoTracking().OrderBy(l => l.Id).ToList()
            .Select(l => $"{l.Id}|{l.BillNo}|{l.RetryCount}|{l.Success}|{l.Webhook}|{l.IsDeleted}")
            .ToList();

    /// <summary>断言配置与发送记录都未发生任何变化。</summary>
    private static void AssertUnchanged(ErpDbContext db, List<string> paramsBefore, List<string> logsBefore)
    {
        Assert.Equal(paramsBefore, SnapshotParams(db));
        Assert.Equal(logsBefore, SnapshotLogs(db));
    }

    /// <summary>断言抛出指定业务错误码。</summary>
    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>断言成功响应（保存 / 删除返回无数据体的既有契约）。</summary>
    private static void AssertOkResult(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

}
