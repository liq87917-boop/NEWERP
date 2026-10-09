using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-446 系统参数（<c>api/sys/parameters</c>）实时身份 / 既有 sys-parameter 菜单 / 持久化列与运营取值
/// 有界校验护栏单元测试。
/// <para>覆盖：分页 / 主键详情 / 按键详情 / 新增 / 修改在读取或写入任何参数行之前，对缺失 / 已删除 /
/// 禁用身份与无既有「系统参数」菜单身份 fail closed（无管理员兜底）；撤销菜单后立即收敛；已授予既有菜单的账号
/// 可读可写；新增 / 修改对空 / 超长 / 重复参数键、空 / 超长参数名称、超长参数值与说明返回既有受控错误；
/// 被运营单据默认值消费的 DefaultCurrency 只接受受支持币种代码、ExchangeRate 只接受正的可解析十进制数，
/// 被拒写入不落任何参数行且不改变既有默认币种 / 汇率参数与既有系统参数。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。</para>
/// </summary>
public class SystemParameterAuthorizationTests
{
    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 全部路由_无身份_一律未认证且不读取或改写任何参数行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-ANON", "n", "v");
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetAll(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetByKey(row.ParamKey));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewParam("PT-ANON")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewParam("SO-ANON")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-ID", "n", "v");
        var before = Snapshot(db);
        var disabled = SeedUser(db, UserStatus.Disabled, deleted: false, grantMenu: true);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true, grantMenu: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetAll(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewParam("PT-D")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetByKey(row.ParamKey));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Update(row.Id, NewParam("SO-ID")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 无既有系统参数菜单_全部路由拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-NOMENU", "n", "v");
        var before = Snapshot(db);
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetAll(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetByKey(row.ParamKey));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewParam("PT-NEW")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewParam("SO-NOMENU")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "SO-REVOKE", "n", "v");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetAll(new PageQuery()));

        RevokeMenus(db, user);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetByKey("SO-REVOKE"));
    }

    [Fact]
    public async Task 特权账号_缺少既有菜单_仍按权限不足拒绝_无管理员兜底()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-PRIV-NOMENU", "n", "v");
        // 系统内置角色（特权）但不授予 sys-parameter 菜单：不得因特权而绕过既有功能菜单。
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false, systemRole: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetAll(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetById(row.Id));

        // 显式授予既有 sys-parameter 菜单后立即放行，证明拒绝只因缺少既有功能菜单。
        GrantMenu(db, PrimaryRoleId(db, privileged), SystemParameterAuthorizationRules.RequiredMenuCode,
            SystemParameterAuthorizationRules.RequiredMenuText);
        Assert.IsType<OkObjectResult>(await NewController(db, privileged).GetAll(new PageQuery()));
    }

    // ==================== 2. 放行路径（已授予既有菜单的账号） ====================

    [Fact]
    public async Task 已授予既有菜单的账号_可读可写且保留既有响应契约()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-OK", "n", "v");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var paged = AssertOk<PagedResult<SysParameter>>(await ctl.GetAll(new PageQuery()));
        Assert.Contains(paged.Items, p => p.Id == row.Id);
        Assert.Equal(row.Id, AssertOk<SysParameter>(await ctl.GetById(row.Id)).Id);
        Assert.Equal("v", AssertOk<SysParameter>(await ctl.GetByKey("SO-OK")).ParamValue);

        AssertOkResult(await ctl.Create(NewParam("PT-OK", value: "42")));
        var created = db.SysParameters.Single(p => p.ParamKey == "PT-OK");
        AssertOkResult(await ctl.Update(created.Id, NewParam("PT-OK", name: "n-new", value: "43")));
        var stored = db.SysParameters.Single(p => p.Id == created.Id);
        Assert.Equal("43", stored.ParamValue);
        Assert.Equal("n-new", stored.ParamName);
        Assert.NotNull(stored.UpdatedAt);
    }

    [Fact]
    public async Task 特权账号_具备既有菜单_保留既有读_写访问()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-PRIV", "n", "v");
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true, systemRole: true);
        var ctl = NewController(db, privileged);

        Assert.Contains(AssertOk<PagedResult<SysParameter>>(await ctl.GetAll(new PageQuery())).Items,
            p => p.Id == row.Id);
        AssertOkResult(await ctl.Create(NewParam("PT-PRIV")));
        Assert.True(db.SysParameters.Any(p => p.ParamKey == "PT-PRIV"));
    }

    // ==================== 3. 持久化列有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task 新增_非法持久化字段_拒绝且不落任何参数行()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "SO-KEEP", "n", "v");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam("   ")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(new string('K', 101))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam("PT-N", name: string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam("PT-N", name: new string('N', 101))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam("PT-V", value: new string('v', 501))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("PT-D", description: new string('d', 501))));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 新增_重复参数键_按既有唯一语义拒绝且原行不变()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "SO-DUP", "n", "v");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewParam("SO-DUP")));
        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewParam("  SO-DUP  ")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_非法字段_拒绝且不改写原行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedParam(db, "SO-EDIT", "n", "keep");
        var other = SeedParam(db, "PT-OTHER", "n", "v");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewParam(string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewParam("SO-EDIT", name: string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewParam("SO-EDIT", value: new string('v', 501))));
        await AssertCode(ErrorCodes.Duplicate,
            () => ctl.Update(row.Id, NewParam(other.ParamKey)));

        AssertUnchanged(db, before);
    }

    // ==================== 4. 运营取值护栏（DefaultCurrency / ExchangeRate） ====================

    [Fact]
    public async Task 新增_运营键非法取值_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "SO-KEEP", "n", "v");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        // DefaultCurrency：未知币种代码 / 数字 / 空都拒绝。
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("DefaultCurrency", value: "XYZ")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("DefaultCurrency", value: "1")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("DefaultCurrency", value: string.Empty)));

        // ExchangeRate：零 / 负数 / 不可解析都拒绝。
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("ExchangeRate", value: "0")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("ExchangeRate", value: "-1.5")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewParam("ExchangeRate", value: "abc")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 新增_运营键合法取值_可写入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        AssertOkResult(await ctl.Create(NewParam("DefaultCurrency", name: "默认币种", value: "eur")));
        AssertOkResult(await ctl.Create(NewParam("ExchangeRate", name: "默认汇率", value: "7.35")));

        Assert.Equal("eur", db.SysParameters.Single(p => p.ParamKey == "DefaultCurrency").ParamValue);
        Assert.Equal("7.35", db.SysParameters.Single(p => p.ParamKey == "ExchangeRate").ParamValue);
    }

    [Fact]
    public async Task 修改_运营键非法取值_拒绝且既有默认币种与汇率不变()
    {
        using var db = TestDbFactory.Create();
        var currency = SeedParam(db, "DefaultCurrency", "默认币种", "CNY");
        var rate = SeedParam(db, "ExchangeRate", "默认汇率", "7.2");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(currency.Id, NewParam("DefaultCurrency", value: "XYZ")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(rate.Id, NewParam("ExchangeRate", value: "-7.2")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(rate.Id, NewParam("ExchangeRate", value: "not-a-number")));

        AssertUnchanged(db, before);

        // 既有默认币种 / 汇率参数保持原值，BillProcController 的参数驱动回退口径不变。
        Assert.Equal("CNY", db.SysParameters.Single(p => p.Id == currency.Id).ParamValue);
        Assert.Equal("7.2", db.SysParameters.Single(p => p.Id == rate.Id).ParamValue);
    }

    [Fact]
    public void 运营取值判定_受支持币种与正十进制_纯函数()
    {
        // 与 BillProcController.MapCurrencyCode 同源的六个币种代码（大小写无关）。
        Assert.True(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("CNY"));
        Assert.True(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("usd"));
        Assert.True(SystemParameterAuthorizationRules.IsSupportedCurrencyCode(" Eur "));
        Assert.True(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("HKD"));
        Assert.True(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("GBP"));
        Assert.True(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("JPY"));

        Assert.False(SystemParameterAuthorizationRules.IsSupportedCurrencyCode(null));
        Assert.False(SystemParameterAuthorizationRules.IsSupportedCurrencyCode(string.Empty));
        Assert.False(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("XYZ"));
        Assert.False(SystemParameterAuthorizationRules.IsSupportedCurrencyCode("1"));

        Assert.True(SystemParameterAuthorizationRules.IsPositiveExchangeRate("1"));
        Assert.True(SystemParameterAuthorizationRules.IsPositiveExchangeRate("7.2"));
        Assert.True(SystemParameterAuthorizationRules.IsPositiveExchangeRate(" 0.01 "));

        Assert.False(SystemParameterAuthorizationRules.IsPositiveExchangeRate("0"));
        Assert.False(SystemParameterAuthorizationRules.IsPositiveExchangeRate("-1.5"));
        Assert.False(SystemParameterAuthorizationRules.IsPositiveExchangeRate("abc"));
        Assert.False(SystemParameterAuthorizationRules.IsPositiveExchangeRate(string.Empty));

        Assert.Equal(
            new[] { "CNY", "USD", "EUR", "HKD", "GBP", "JPY" },
            SystemParameterAuthorizationRules.SupportedCurrencyCodes);
    }

    // ==================== 5. 源码与菜单契约 ====================

    [Fact]
    public void 控制器源码契约_全部路由先授权_复用既有系统参数菜单()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "ParameterController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("SystemParameterAuthorizationRules.EnsureAuthorizedAsync", source);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);

        Assert.Equal("sys-parameter", SystemParameterAuthorizationRules.RequiredMenuCode);
        Assert.Equal("系统参数", SystemParameterAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{SystemParameterAuthorizationRules.RequiredMenuCode}\", "
            + $"\"{SystemParameterAuthorizationRules.RequiredMenuText}\"",
            menus);

        // BillProcController 的既有参数驱动默认值读取口径（键名与回退来源）保持不变。
        var billProc = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BillProcController.cs"));
        Assert.Contains("ParamKey IN ('DefaultCurrency','ExchangeRate')", billProc);
        Assert.Contains("LoadCurrencyDefaultsAsync", billProc);
        Assert.Contains("ApplyCurrencyDefaultsAsync", billProc);
    }

    // ==================== 6. 测试辅助 ====================

    /// <summary>构造系统参数控制器（内存库）并注入指定登录身份（可空 = 无身份）。</summary>
    private static ParameterController NewController(ErpDbContext db, long? userId)
    {
        var controller = new ParameterController(db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>待写入的系统参数（默认合法值）。</summary>
    private static SysParameter NewParam(
        string paramKey, string name = "测试参数", string value = "v", string description = "")
        => new() { ParamKey = paramKey, ParamName = name, ParamValue = value, Description = description };

    /// <summary>播种一条既有系统参数（用于读取 / 唯一性 / 运营默认值场景）。</summary>
    private static SysParameter SeedParam(ErpDbContext db, string key, string name, string value)
    {
        var p = NewParam(key, name, value);
        db.SysParameters.Add(p);
        db.SaveChanges();
        return p;
    }

    /// <summary>播种账号（可选启用 / 删除 / 系统内置角色 / 既有 sys-parameter 菜单授权），返回用户 Id。</summary>
    private static long SeedUser(
        ErpDbContext db, UserStatus status, bool deleted, bool grantMenu, bool systemRole = false)
    {
        var user = new SysUser
        {
            UserName = $"sys-param-{Guid.NewGuid():N}",
            DisplayName = "系统参数授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "系统参数操作员",
            RoleCode = $"SysParamRole-{Guid.NewGuid():N}",
            IsSystem = systemRole
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, SystemParameterAuthorizationRules.RequiredMenuCode,
                SystemParameterAuthorizationRules.RequiredMenuText);
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
                Path = $"/system/{menuCode}"
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

    /// <summary>系统参数行快照（用于断言拒绝路径不新增 / 不改写任何参数行）。</summary>
    private static List<string> Snapshot(ErpDbContext db)
        => db.SysParameters.AsNoTracking().OrderBy(p => p.Id).ToList()
            .Select(p => $"{p.Id}|{p.ParamKey}|{p.ParamValue}|{p.ParamName}|{p.Description}|{p.IsDeleted}")
            .ToList();

    private static void AssertUnchanged(ErpDbContext db, List<string> before)
        => Assert.Equal(before, Snapshot(db));

    /// <summary>断言抛出指定业务错误码。</summary>
    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>断言成功响应并取出数据（业务码必须为 0）。</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言成功响应（新增 / 修改返回无数据体的既有契约）。</summary>
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
