using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-499 系统日志（<c>api/sys/logs</c>）实时身份 / 账号状态 / 既有「系统日志」（<c>sys-log</c>）菜单授权护栏单元测试。
/// <para>覆盖：分页查询在读取任何 <c>SysOperationLogs</c> 计数或行之前，对缺失 / 零 / 非法 / 已删除身份按未认证拒绝，
/// 对禁用身份与缺少 / 撤销既有 sys-log 菜单身份按权限不足拒绝（含特权账号缺菜单仍拒绝，无管理员兜底）；
/// 与请求路径 / 请求形状 / 是否绑定 <c>HttpContext</c> 完全无关；被拒绝时不读取、不计数、不返回任何日志行、
/// 用户名、路径、单据号或请求体；已授予既有菜单的账号保留既有分页筛选与
/// <c>ApiResponse&lt;PagedResult&lt;SysOperationLog&gt;&gt;</c> 响应契约。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。</para>
/// </summary>
public class OperationLogAuthorizationTests
{
    /// <summary>系统日志路由（仅用于构造已赋值请求路径，不参与任何授权分支）。</summary>
    private const string LogPath = "/api/sys/logs";

    // ==================== 1. 身份 / 账号状态 fail closed（零日志读取） ====================

    [Fact]
    public async Task 无身份_未认证_且不读取任何日志行或计数()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var counting = LogReadCounter.Wrap(db);
        var ctl = NewController(counting.Proxy, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery(), null, null, null, null, null));

        AssertZeroLogReads(counting);
    }

    [Fact]
    public async Task 零身份_非数字身份_负数身份_一律未认证且零日志读取()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);

        foreach (var raw in new[] { "0", "-1", "abc", " " })
        {
            var counting = LogReadCounter.Wrap(db);
            var ctl = NewControllerWithRawIdentity(counting.Proxy, raw, LogPath);
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery(), null, null, null, null, null));
            AssertZeroLogReads(counting);
        }
    }

    [Fact]
    public async Task 账号不存在或已删除_按未认证拒绝_且零日志读取()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true, grantMenu: true);

        var counting = LogReadCounter.Wrap(db);
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(counting.Proxy, deleted).GetPaged(new PageQuery(), null, null, null, null, null));
        AssertZeroLogReads(counting);

        // 未知（不存在）账号同样是未认证，不因数据库里没有该 Id 而放行。
        var missing = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true) + 1000;
        var missingCounting = LogReadCounter.Wrap(db);
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(missingCounting.Proxy, missing).GetPaged(new PageQuery(), null, null, null, null, null));
        AssertZeroLogReads(missingCounting);
    }

    [Fact]
    public async Task 禁用账号_权限不足_且零日志读取()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var disabled = SeedUser(db, UserStatus.Disabled, deleted: false, grantMenu: true);

        var counting = LogReadCounter.Wrap(db);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(counting.Proxy, disabled).GetPaged(new PageQuery(), null, null, null, null, null));
        AssertZeroLogReads(counting);
    }
    [Fact]
    public async Task 无既有系统日志菜单_权限不足_文案指出模块授权_且零日志读取()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);

        var counting = LogReadCounter.Wrap(db);
        var ctl = NewController(counting.Proxy, noMenu);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GetPaged(new PageQuery(), null, null, null, null, null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        AssertZeroLogReads(counting);
    }

    [Fact]
    public async Task 拒绝文案_不泄露任何日志数据()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true, grantMenu: true);

        var denied = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, noMenu).GetPaged(new PageQuery(), null, null, null, null, null));
        var anonymous = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, deleted).GetPaged(new PageQuery(), null, null, null, null, null));

        foreach (var message in new[] { denied.Message, anonymous.Message })
        {
            Assert.DoesNotContain("admin", message, StringComparison.Ordinal);
            Assert.DoesNotContain("tom", message, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/base/customers", message, StringComparison.Ordinal);
            Assert.DoesNotContain("SO202609120001", message, StringComparison.Ordinal);
            Assert.DoesNotContain("RequestBody", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task 缺少既有菜单_特权账号_仍按权限不足拒绝_无管理员兜底()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        // 系统内置角色（特权）但不授予 sys-log 菜单：不得因特权而绕过既有功能菜单。
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false, systemRole: true);

        var counting = LogReadCounter.Wrap(db);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(counting.Proxy, privileged).GetPaged(new PageQuery(), null, null, null, null, null));
        AssertZeroLogReads(counting);

        // 显式授予既有 sys-log 菜单后立即放行，证明拒绝只因缺少既有功能菜单。
        GrantMenu(db, PrimaryRoleId(db, privileged),
            OperationLogAuthorizationRules.RequiredMenuCode, OperationLogAuthorizationRules.RequiredMenuText);
        Assert.IsType<OkObjectResult>(
            await NewController(db, privileged).GetPaged(new PageQuery(), null, null, null, null, null));
    }

    [Fact]
    public async Task 撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery(), null, null, null, null, null));

        RevokeMenus(db, user);

        var counting = LogReadCounter.Wrap(db);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(counting.Proxy, user).GetPaged(new PageQuery(), null, null, null, null, null));
        AssertZeroLogReads(counting);
    }


    // ==================== 2. 与请求路径 / 请求形状 / 请求上下文无关 ====================

    [Fact]
    public async Task 未绑定HttpContext的纯进程内直调_空与已赋值筛选路径_一律先授权()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);

        // 完全没有 ControllerContext（无请求上下文 / 无身份 / 无请求路径）：不得因此放行。
        var counting = LogReadCounter.Wrap(db);
        var bare = new OperationLogController(counting.Proxy);
        await AssertCode(ErrorCodes.Unauthorized, () => bare.GetPaged(new PageQuery(), null, null, null, null, null));
        await AssertCode(ErrorCodes.Unauthorized,
            () => bare.GetPaged(new PageQuery { Keyword = "审核" }, "销售订单", "admin", "SO202609120001",
                DateTime.Today.AddDays(-1), DateTime.Today));
        AssertZeroLogReads(counting);
    }

    [Fact]
    public async Task 空请求路径与已赋值请求路径_对同一身份给出完全相同的判定()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);

        // 空路径 + 空筛选：放行
        Assert.IsType<OkObjectResult>(
            await NewControllerWithPath(db, user, requestPath: string.Empty)
                .GetPaged(new PageQuery(), null, null, null, null, null));

        // 已赋值路径 + 已赋值筛选：同一账号同样放行
        Assert.IsType<OkObjectResult>(
            await NewControllerWithPath(db, user, requestPath: LogPath)
                .GetPaged(new PageQuery { Keyword = "审核" }, "销售订单", "admin", "SO202609120001", null, null));

        // 无身份：空路径与已赋值路径都按同一口径拒绝为未认证
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewControllerWithPath(db, userId: null, requestPath: string.Empty)
                .GetPaged(new PageQuery(), null, null, null, null, null));
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewControllerWithPath(db, userId: null, requestPath: LogPath)
                .GetPaged(new PageQuery { Keyword = "审核" }, "销售订单", null, null, null, null));

        // 缺菜单：空路径与已赋值路径都按同一口径拒绝为权限不足
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewControllerWithPath(db, noMenu, requestPath: string.Empty)
                .GetPaged(new PageQuery(), null, null, null, null, null));
        await AssertCode(ErrorCodes.Forbidden,
            () => NewControllerWithPath(db, noMenu, requestPath: LogPath)
                .GetPaged(new PageQuery { Keyword = "审核" }, null, null, null, null, null));
    }

    // ==================== 3. 放行路径（既有菜单授权账号保留既有读契约） ====================

    [Fact]
    public async Task 已授予既有菜单的账号_保留既有分页筛选与响应契约()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var all = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null, null, null));
        Assert.Equal(3, all.Total);
        Assert.All(all.Items, l => Assert.False(l.IsDeleted));

        var byModule = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, "基础资料", null, null, null, null));
        Assert.Equal(1, byModule.Total);

        var byUser = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, "tom", null, null, null));
        Assert.Equal(1, byUser.Total);

        var byBillNo = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, "SO202609120001", null, null));
        Assert.Equal(2, byBillNo.Total);

        // 单据号筛选同时匹配 Path（既有口径）。
        var byPath = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, "/api/base/customers", null, null));
        Assert.Equal(1, byPath.Total);

        var byKeyword = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50, Keyword = "审核" }, null, null, null, null, null));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("审核", byKeyword.Items.Single().Action);
    }


    [Fact]
    public async Task 特权账号_具备既有菜单_保留既有读访问()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true, systemRole: true);

        var data = GetPaged(await NewController(db, privileged)
            .GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null, null, null));

        Assert.Equal(3, data.Total);
    }

    [Fact]
    public async Task 时间范围_起始包含_次日排他_且软删除排除()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        // 明日 00:00 的日志：以「今天」为结束日时必须被排除（既有的次日排他口径）。
        db.SysOperationLogs.Add(new SysOperationLog
        {
            UserName = "admin",
            Module = "销售订单",
            Action = "新增",
            Path = "/api/v2/bills/sales-order/2",
            BillNo = "SO202609120002",
            CreatedAt = DateTime.Today.AddDays(1)
        });
        db.SaveChanges();

        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var range = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null,
            DateTime.Today.AddHours(-3), DateTime.Today));

        Assert.Equal(3, range.Total);
        Assert.DoesNotContain(range.Items, l => l.BillNo == "SO202609120002");

        var noRange = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null, null, null));
        Assert.Equal(4, noRange.Total);
    }

    [Fact]
    public async Task 分页边界_保留既有归一语义()
    {
        using var db = TestDbFactory.Create();
        SeedLogs(db);
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var normalized = GetPaged(await ctl.GetPaged(new PageQuery { Page = 0, PageSize = 0 }, null, null, null, null, null));
        Assert.Equal(1, normalized.Page);
        Assert.Equal(20, normalized.PageSize);

        var single = GetPaged(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 1 }, null, null, null, null, null));
        Assert.Single(single.Items);
        Assert.Equal(3, single.Total);
    }

    // ==================== 4. 源码与菜单契约 ====================

    [Fact]
    public void 控制器源码契约_分页先授权_复用既有系统日志菜单()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysSimpleControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("OperationLogAuthorizationRules.EnsureAuthorizedAsync", source);

        // 类声明逐字节不变（既有 ClientLimitAuthorizationTests 契约）。
        Assert.Contains("public class OperationLogController : ControllerBase", source);

        // 授权调用严格先于任何日志数据集读取。
        var controllerSection = source[source.IndexOf(
            "public class OperationLogController", StringComparison.Ordinal)..];
        var authorizeIndex = controllerSection.IndexOf(
            "await EnsureOperationLogAuthorizedAsync();", StringComparison.Ordinal);
        var logReadIndex = controllerSection.IndexOf("_db.SysOperationLogs", StringComparison.Ordinal);
        Assert.True(authorizeIndex >= 0, "分页入口必须先执行系统日志实时授权");
        Assert.True(logReadIndex > authorizeIndex, "授权必须严格先于任何 SysOperationLogs 读取");

        // 不读取 Request.Path、不读取环境变量、不区分提供程序、无测试专用开关。
        Assert.DoesNotContain("Request.Path", controllerSection);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", controllerSection);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);

        Assert.Equal("sys-log", OperationLogAuthorizationRules.RequiredMenuCode);
        Assert.Equal("系统日志", OperationLogAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{OperationLogAuthorizationRules.RequiredMenuCode}\", "
            + $"\"{OperationLogAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    [Fact]
    public void 规则文案_路径无关与非披露边界同源()
    {
        Assert.Contains("完全无关", OperationLogAuthorizationRules.PathIndependenceText, StringComparison.Ordinal);
        Assert.Contains("HttpContext", OperationLogAuthorizationRules.PathIndependenceText, StringComparison.Ordinal);
        Assert.Contains("sys-log", OperationLogAuthorizationRules.RuleText, StringComparison.Ordinal);
        Assert.Contains("未认证", OperationLogAuthorizationRules.RuleText, StringComparison.Ordinal);
        Assert.Contains("权限不足", OperationLogAuthorizationRules.RuleText, StringComparison.Ordinal);
        Assert.Contains("不新增菜单", OperationLogAuthorizationRules.BoundaryText, StringComparison.Ordinal);
    }


    // ==================== 5. 测试辅助 ====================

    /// <summary>构造日志控制器并注入指定登录身份（可空 = 无身份），请求路径默认赋值。</summary>
    private static OperationLogController NewController(IErpDbContext db, long? userId)
        => NewControllerWithPath(db, userId, LogPath);

    /// <summary>构造日志控制器：<c>Request.Path</c> 由参数显式控制（空串 = 空请求路径）。</summary>
    private static OperationLogController NewControllerWithPath(IErpDbContext db, long? userId, string requestPath)
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

    /// <summary>构造日志控制器：身份声明为任意原始文本（含非数字 / 零 / 负数），请求路径赋值。</summary>
    private static OperationLogController NewControllerWithRawIdentity(IErpDbContext db, string rawIdentity, string path)
    {
        var controller = new OperationLogController(db);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, rawIdentity) }, "Test"))
        };
        http.Request.Path = path;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>播种三条未删除日志 + 一条已软删除日志（与既有 OperationLogTests 口径一致）。</summary>
    private static void SeedLogs(ErpDbContext db)
    {
        db.SysOperationLogs.AddRange(
            new SysOperationLog { UserName = "admin", Module = "销售订单", Action = "新增", Path = "/api/v2/bills/sales-order/1", BillNo = "SO202609120001", Method = "POST", RequestBody = "{\"id\":1}", IpAddress = "10.0.0.1", StatusCode = 200, CreatedAt = DateTime.Now.AddHours(-2) },
            new SysOperationLog { UserName = "admin", Module = "销售订单", Action = "审核", Path = "/api/v2/bills/sales-order/1", BillNo = "SO202609120001", Method = "POST", RequestBody = "{\"id\":1}", IpAddress = "10.0.0.1", StatusCode = 200, CreatedAt = DateTime.Now.AddHours(-1) },
            new SysOperationLog { UserName = "tom", Module = "基础资料", Action = "新增", Path = "/api/base/customers", BillNo = string.Empty, Method = "POST", RequestBody = "{\"code\":\"C1\"}", IpAddress = "10.0.0.2", StatusCode = 200, CreatedAt = DateTime.Now },
            new SysOperationLog { UserName = "tom", Module = "基础资料", Action = "删除", Path = "/api/base/customers/9", BillNo = string.Empty, Method = "DELETE", RequestBody = "{\"id\":9}", IpAddress = "10.0.0.2", StatusCode = 200, CreatedAt = DateTime.Now, IsDeleted = true });
        db.SaveChanges();
    }

    /// <summary>断言抛出指定业务错误码。</summary>
    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>断言成功响应并取出数据（业务码必须为 0）。</summary>
    private static PagedResult<SysOperationLog> GetPaged(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<PagedResult<SysOperationLog>>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));


    /// <summary>播种账号（可选启用 / 删除 / 系统内置角色 / 既有 sys-log 菜单授权），返回用户 Id。</summary>
    private static long SeedUser(
        ErpDbContext db, UserStatus status, bool deleted, bool grantMenu, bool systemRole = false)
    {
        var user = new SysUser
        {
            UserName = $"sys-log-{Guid.NewGuid():N}",
            DisplayName = "系统日志授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "系统日志操作员",
            RoleCode = $"SysLogRole-{Guid.NewGuid():N}",
            IsSystem = systemRole
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, OperationLogAuthorizationRules.RequiredMenuCode,
                OperationLogAuthorizationRules.RequiredMenuText);
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
                Path = "/system/log"
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

    /// <summary>断言本次调用从未访问 <c>SysOperationLogs</c> 数据集（拒绝路径零日志读取 / 零计数）。</summary>
    private static void AssertZeroLogReads(LogReadCounter counter)
    {
        Assert.DoesNotContain(nameof(IErpDbContext.SysOperationLogs), counter.ReadProperties);
        Assert.Equal(0, counter.WriteCalls);
    }

    /// <summary>
    /// <see cref="IErpDbContext"/> 读取计数器（与既有测试同口径的 <see cref="DispatchProxy"/>）：
    /// 记录被访问的数据集属性名与 <c>SaveChangesAsync</c> 次数，用于证明拒绝路径从未触碰 <c>SysOperationLogs</c>。
    /// </summary>
    private class LogReadCounter : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>被访问的数据集属性名</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读入口恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static LogReadCounter Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, LogReadCounter>();
            var counting = (LogReadCounter)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(
                    args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
                ReadProperties.Add(targetMethod.Name[4..]);

            return targetMethod.Invoke(_inner, args);
        }
    }
}
