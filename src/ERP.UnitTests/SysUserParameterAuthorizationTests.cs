using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-456 用户参数（<c>api/sys/user-parameters</c>）实时身份 / 既有 user-parameter 菜单 / 持久化列与
/// 用户解析有界校验护栏单元测试。
/// <list type="number">
/// <item><b>拒绝矩阵</b>：分页 / 主键详情 / 新增 / 修改 / 删除在读取或写入任何用户参数行<b>之前</b>
/// 对缺失 / 非法 / 已删除（未认证）、禁用（权限不足）、缺少既有「用户参数」菜单（权限不足）身份 fail closed，
/// 且 <c>SysUserParameters</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>不依赖 Request.Path</b>：进程内直调（无请求路径）与真实 HTTP 路由一律实时授权，
/// 绝不因缺少请求路径 / 环境 / 空请求而绕过检查；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>放行</b>：具备既有「用户参数」菜单的特权（系统内置角色）与普通菜单授权身份下既有读 / 写 / 软删除契约保持；</item>
/// <item><b>有界校验</b>：新增 / 修改对空 / 超长参数键、超长参数值、未知 / 已删除用户 Id 按既有受控错误拒绝且不落 / 不改写任何行，
/// 既有按用户参数键唯一检查与软删除语义不变；</item>
/// <item><b>源码契约</b>：控制器 5 条路由全部先授权再读写，且只复用既有 <c>user-parameter</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。
/// </summary>
public class SysUserParameterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>真实 HTTP 路由上下文（<c>Request.Path</c> 已赋值）下的用户参数控制器。</summary>
    private static UserParameterController NewController(ErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextWithUser(userId, realHttpRoute: true) };

    /// <summary>进程内直调上下文（无请求路径、可带身份）。</summary>
    private static UserParameterController NewInternalController(ErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextWithUser(userId, realHttpRoute: false) };

    /// <summary>带（可空）<c>NameIdentifier</c> 的身份上下文；<paramref name="realHttpRoute"/> 为真时赋值请求路径。</summary>
    private static ControllerContext ContextWithUser(long? userId, bool realHttpRoute)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (realHttpRoute) http.Request.Path = "/api/sys/user-parameters";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 用户参数菜单 / 系统内置角色），返回用户 Id。</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantMenu = true, bool systemRole = false)
    {
        var user = new SysUser
        {
            UserName = $"user-param-auth-{Guid.NewGuid():N}",
            DisplayName = "用户参数授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "用户参数授权角色",
            RoleCode = $"UserParamAuthCase-{Guid.NewGuid():N}",
            IsSystem = systemRole
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, SysUserParameterAuthorizationRules.RequiredMenuCode,
                SysUserParameterAuthorizationRules.RequiredMenuText);
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

    /// <summary>待写入的用户参数（默认合法值）。</summary>
    private static SysUserParameter NewParam(long userId, string paramKey, string paramValue = "v")
        => new() { UserId = userId, ParamKey = paramKey, ParamValue = paramValue };

    /// <summary>播种一条既有用户参数行（用于读取 / 唯一性 / 软删除场景）。</summary>
    private static SysUserParameter SeedParam(ErpDbContext db, long userId, string key, string value)
    {
        var p = NewParam(userId, key, value);
        db.SysUserParameters.Add(p);
        db.SaveChanges();
        return p;
    }

    /// <summary>用户参数行快照（用于断言拒绝路径不新增 / 不改写任何用户参数行）。</summary>
    private static List<string> Snapshot(ErpDbContext db)
        => db.SysUserParameters.AsNoTracking().OrderBy(p => p.Id).ToList()
            .Select(p => $"{p.Id}|{p.UserId}|{p.ParamKey}|{p.ParamValue}|{p.IsDeleted}")
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

    /// <summary>断言成功响应（新增 / 修改 / 删除返回无数据体的既有契约）。</summary>
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

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 全部路由_无身份_一律未认证且不读取或改写任何用户参数行()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-ANON", "v");
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewParam(owner, "UP-ANON-NEW")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 内部直调_无请求路径_无身份_仍按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-INTERNAL", "v");
        var before = Snapshot(db);
        var ctl = NewInternalController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 内部直调_已授权身份_仍放行既有读契约()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-INTERNAL-OK", "v");
        var ctl = NewInternalController(db, owner);

        var paged = AssertOk<PagedResult<UserParameterView>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, p => p.Id == row.Id);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-ID", "v");
        var before = Snapshot(db);
        var disabled = SeedUser(db, UserStatus.Disabled, deleted: false);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewParam(owner, "UP-D")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 无既有用户参数菜单_全部路由拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-NOMENU", "v");
        var before = Snapshot(db);
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewParam(owner, "UP-NOMENU-NEW")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewParam(owner, row.ParamKey)));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        SeedParam(db, owner, "UP-REVOKE", "v");
        var ctl = NewController(db, owner);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery()));

        RevokeMenus(db, owner);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(1));
    }

    [Fact]
    public async Task 特权账号_缺少既有菜单_仍按权限不足拒绝_无管理员兜底()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-PRIV-NOMENU", "v");
        // 系统内置角色（特权）但不授予 user-parameter 菜单：不得因特权而绕过既有功能菜单。
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false, systemRole: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetById(row.Id));

        // 显式授予既有 user-parameter 菜单后立即放行，证明拒绝只因缺少既有功能菜单。
        GrantMenu(db, PrimaryRoleId(db, privileged), SysUserParameterAuthorizationRules.RequiredMenuCode,
            SysUserParameterAuthorizationRules.RequiredMenuText);
        Assert.IsType<OkObjectResult>(await NewController(db, privileged).GetPaged(new PageQuery()));
    }

    // ==================== 2. 放行路径（已授予既有菜单的账号） ====================

    [Fact]
    public async Task 已授予既有菜单的账号_可读可写并保留既有响应契约与软删除语义()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var other = SeedUser(db);
        var row = SeedParam(db, owner, "UP-OK", "v");
        var ctl = NewController(db, owner);

        var paged = AssertOk<PagedResult<UserParameterView>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, p => p.Id == row.Id && p.UserName == "用户参数授权账号");
        Assert.Equal(row.Id, AssertOk<SysUserParameter>(await ctl.GetById(row.Id)).Id);

        AssertOkResult(await ctl.Create(NewParam(owner, "UP-OK-NEW", "42")));
        var created = db.SysUserParameters.Single(p => p.ParamKey == "UP-OK-NEW");
        Assert.Equal(owner, created.UserId);

        AssertOkResult(await ctl.Update(created.Id, NewParam(owner, "UP-OK-NEW", "43")));
        var stored = db.SysUserParameters.Single(p => p.Id == created.Id);
        Assert.Equal("43", stored.ParamValue);
        Assert.NotNull(stored.UpdatedAt);

        // 既有软删除语义：标记 IsDeleted、不再返回，且删除后同键可再建。
        AssertOkResult(await ctl.Delete(created.Id));
        Assert.True(db.SysUserParameters.Single(p => p.Id == created.Id).IsDeleted);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(created.Id));
        AssertOkResult(await ctl.Create(NewParam(owner, "UP-OK-NEW", "44")));

        // 菜单授权即可管理其它已知用户的参数行（不改变既有契约）。
        AssertOkResult(await ctl.Create(NewParam(other, "UP-OK-OTHER")));
    }

    [Fact]
    public async Task 特权账号_具备既有菜单_保留既有读_写访问()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-PRIV", "v");
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true, systemRole: true);
        var ctl = NewController(db, privileged);

        Assert.Contains(AssertOk<PagedResult<UserParameterView>>(await ctl.GetPaged(new PageQuery())).Items,
            p => p.Id == row.Id);
        AssertOkResult(await ctl.Create(NewParam(owner, "UP-PRIV-NEW")));
        Assert.True(db.SysUserParameters.Any(p => p.ParamKey == "UP-PRIV-NEW"));
    }

    // ==================== 3. 持久化列 + 用户解析有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task 新增_非法持久化字段或未知用户_拒绝且不落任何用户参数行()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var deletedOwner = SeedUser(db, deleted: true);
        SeedParam(db, owner, "UP-KEEP", "keep");
        SeedParam(db, owner, "UP-DUP", "v");
        var before = Snapshot(db);
        var ctl = NewController(db, owner);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(owner, string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(owner, new string('K', 101))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(owner, "UP-LONGV", new string('v', 501))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(0, "UP-NOUSER")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(999999, "UP-UNKNOWN")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewParam(deletedOwner, "UP-DELUSER")));
        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewParam(owner, "UP-DUP")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 新增_边界长度_接受且落库()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var ctl = NewController(db, owner);
        var keyAtLimit = new string('K', SysUserParameterAuthorizationRules.MaxParamKeyLength);
        var valueAtLimit = new string('v', SysUserParameterAuthorizationRules.MaxParamValueLength);

        AssertOkResult(await ctl.Create(NewParam(owner, keyAtLimit, valueAtLimit)));
        var created = db.SysUserParameters.Single(p => p.ParamKey == keyAtLimit);
        Assert.Equal(valueAtLimit, created.ParamValue);
    }

    [Fact]
    public async Task 修改_非法持久化字段或未知用户_拒绝且不改写任何用户参数行()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var other = SeedUser(db);
        var deletedOwner = SeedUser(db, deleted: true);
        var seeded = SeedParam(db, owner, "UP-EDIT", "keep");
        var before = Snapshot(db);
        var ctl = NewController(db, owner);

        // 全部被拒路径：行必须逐字节不变。
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(owner, string.Empty, "x")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(owner, new string('K', 101), "x")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(owner, "UP-EDIT", new string('v', 501))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(999999, "UP-EDIT", "x")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(seeded.Id, NewParam(deletedOwner, "UP-EDIT", "x")));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Update(999999, NewParam(owner, "UP-EDIT", "x")));
        AssertUnchanged(db, before);

        // 提交体未指定 UserId：按既有行所属用户校验（仍以合法身份放行并改写）。
        AssertOkResult(await ctl.Update(seeded.Id, new SysUserParameter { ParamKey = "UP-EDIT", ParamValue = "updated" }));
        var stored = db.SysUserParameters.Single(p => p.Id == seeded.Id);
        Assert.Equal("updated", stored.ParamValue);
        Assert.Equal(owner, stored.UserId);
        Assert.False(stored.IsDeleted);

        // 指定其它已知用户不改变行归属（既有语义：不写 UserId）。
        AssertOkResult(await ctl.Update(seeded.Id, NewParam(other, "UP-EDIT", "updated-2")));
        Assert.Equal(owner, db.SysUserParameters.Single(p => p.Id == seeded.Id).UserId);
    }

    // ==================== 4. 既有软删除语义（标记而不物理删除） ====================

    [Fact]
    public async Task 软删除语义不变_删除标记不物理删除且被拒删除不改写()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUser(db);
        var row = SeedParam(db, owner, "UP-SOFT", "v");
        var ctl = NewController(db, owner);

        // 无身份删除不改变任何行。
        var before = Snapshot(db);
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).Delete(row.Id));
        AssertUnchanged(db, before);

        AssertOkResult(await ctl.Delete(row.Id));
        var stored = db.SysUserParameters.AsNoTracking().Single(p => p.Id == row.Id);
        Assert.True(stored.IsDeleted);
        Assert.Equal(1, db.SysUserParameters.Count(p => p.Id == row.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Delete(row.Id));
    }

    // ==================== 5. 源码与菜单契约 ====================

    [Fact]
    public void 控制器源码契约_全部路由先授权_复用既有用户参数菜单()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "UserParameterController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("SysUserParameterAuthorizationRules.EnsureAuthorizedAsync", source);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);
        // 绝不按请求路径 / 环境 / 空请求绕过检查。
        Assert.DoesNotContain("Request.Path", source);

        Assert.Equal("user-parameter", SysUserParameterAuthorizationRules.RequiredMenuCode);
        Assert.Equal("用户参数", SysUserParameterAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{SysUserParameterAuthorizationRules.RequiredMenuCode}\", "
            + $"\"{SysUserParameterAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    [Fact]
    public void 规则常量与实体持久化上界同源()
    {
        Assert.Equal(100, SysUserParameterAuthorizationRules.MaxParamKeyLength);
        Assert.Equal(500, SysUserParameterAuthorizationRules.MaxParamValueLength);
        Assert.Equal("该用户下参数键已存在", SysUserParameterAuthorizationRules.ParamKeyDuplicatedText);
        Assert.Contains("软删除", SysUserParameterAuthorizationRules.BoundaryText);
        Assert.Contains("UserId", SysUserParameterAuthorizationRules.RuleText);
    }
}
