using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-457 客户端限制（<c>api/sys/client-limits</c>）实时身份 / 既有 client-limit 菜单 / 限制形状有界校验护栏单元测试。
/// <para>覆盖：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除在读取或写入任何限制行之前，对缺失 / 已删除 /
/// 禁用身份与无既有「客户端限制」菜单身份 fail closed（无管理员兜底）；撤销菜单后立即收敛；已授予既有菜单的账号
/// 可读可写；新增 / 修改对未知限制类型、空 / 超长限制值与超长备注返回既有受控错误，且被拒绝时不落任何行、
/// 批量删除被拒时不删除任何行。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。</para>
/// </summary>
public class ClientLimitAuthorizationTests
{
    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 全部路由_无身份_一律未认证且不读取或改写任何限制行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.0.1");
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewLimit("10.0.0.2")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewLimit("10.0.0.3")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.BatchDelete(new List<long> { row.Id }));

        AssertUnchanged(db, before);
        Assert.False(db.SysClientLimits.Single(l => l.Id == row.Id).IsDeleted);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.0.4");
        var before = Snapshot(db);
        var disabled = SeedUser(db, UserStatus.Disabled, deleted: false, grantMenu: true);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true, grantMenu: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewLimit("10.0.0.5")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 无既有客户端限制菜单_全部路由拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.0.6");
        var before = Snapshot(db);
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewLimit("10.0.0.7")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewLimit("10.0.0.8")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { row.Id }));

        AssertUnchanged(db, before);
        Assert.False(db.SysClientLimits.Single(l => l.Id == row.Id).IsDeleted);
    }

    [Fact]
    public async Task 撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedLimit(db, "10.0.0.9");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery()));

        RevokeMenus(db, user);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
    }

    [Fact]
    public async Task 特权账号_缺少既有菜单_仍按权限不足拒绝_无管理员兜底()
    {
        using var db = TestDbFactory.Create();
        SeedLimit(db, "10.0.0.10");
        // 系统内置角色（特权）但不授予 client-limit 菜单：不得因特权而绕过既有功能菜单。
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false, systemRole: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetAll());

        // 显式授予既有 client-limit 菜单后立即放行，证明拒绝只因缺少既有功能菜单。
        GrantMenu(db, PrimaryRoleId(db, privileged), ClientLimitAuthorizationRules.RequiredMenuCode,
            ClientLimitAuthorizationRules.RequiredMenuText);
        Assert.IsType<OkObjectResult>(await NewController(db, privileged).GetPaged(new PageQuery()));
    }


    // ==================== 2. 放行路径（已授予既有菜单的账号） ====================

    [Fact]
    public async Task 已授予既有菜单的账号_可读可写且保留既有响应契约()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.0.11");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var paged = AssertOk<PagedResult<SysClientLimit>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, l => l.Id == row.Id);
        Assert.Contains(AssertOk<List<SysClientLimit>>(await ctl.GetAll()), l => l.Id == row.Id);
        Assert.Equal(row.Id, AssertOk<SysClientLimit>(await ctl.GetById(row.Id)).Id);

        var created = AssertOk<SysClientLimit>(await ctl.Create(NewLimit("10.0.0.12")));
        Assert.True(created.Id > 0);
        Assert.True(db.SysClientLimits.Any(l => l.Id == created.Id));

        var updated = AssertOk<SysClientLimit>(
            await ctl.Update(created.Id, NewLimit("10.0.0.13", ClientLimitType.MachineCode, "机器码调整")));
        Assert.Equal("10.0.0.13", updated.LimitValue);
        Assert.Equal(ClientLimitType.MachineCode, updated.LimitType);

        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { created.Id }));
        Assert.True(db.SysClientLimits.Single(l => l.Id == created.Id).IsDeleted);
    }

    [Fact]
    public async Task 特权账号_具备既有菜单_保留既有读_写访问()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.0.14");
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true, systemRole: true);
        var ctl = NewController(db, privileged);

        Assert.Contains(AssertOk<List<SysClientLimit>>(await ctl.GetAll()), l => l.Id == row.Id);
        var created = AssertOk<SysClientLimit>(await ctl.Create(NewLimit("10.0.0.15")));
        Assert.IsType<OkObjectResult>(await ctl.Delete(created.Id));
        Assert.True(db.SysClientLimits.Single(l => l.Id == created.Id).IsDeleted);
    }

    // ==================== 3. 限制形状有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task 新增_非法限制形状_拒绝且不落任何限制行()
    {
        using var db = TestDbFactory.Create();
        SeedLimit(db, "10.0.1.1");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewLimit("10.0.1.2", limitType: (ClientLimitType)999)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewLimit(string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewLimit("   ")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewLimit(new string('9', 201))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewLimit("10.0.1.3", remark: new string('R', 501))));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_非法字段_拒绝且不改写原行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.2.1");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewLimit("10.0.2.2", limitType: (ClientLimitType)0)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(row.Id, NewLimit(string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewLimit(new string('8', 201))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewLimit("10.0.2.3", remark: new string('R', 501))));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_合法字段_去空白写入成功且不复活软删除语义不变()
    {
        using var db = TestDbFactory.Create();
        var row = SeedLimit(db, "10.0.3.1");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var updated = AssertOk<SysClientLimit>(await ctl.Update(row.Id,
            NewLimit("  10.0.3.9  ", ClientLimitType.MachineCode, "  机器码  ")));

        Assert.Equal("10.0.3.9", updated.LimitValue);   // 写入前规范化去首尾空白
        Assert.Equal("机器码", updated.Remark);
        Assert.Equal(ClientLimitType.MachineCode, updated.LimitType);

        var stored = db.SysClientLimits.Single(l => l.Id == row.Id);
        Assert.Equal("10.0.3.9", stored.LimitValue);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public void 限制类型判定_复用既有枚举且拒绝未知取值()
    {
        Assert.True(Enum.IsDefined(typeof(ClientLimitType), ClientLimitType.IP));
        Assert.True(Enum.IsDefined(typeof(ClientLimitType), ClientLimitType.MachineCode));
        Assert.False(Enum.IsDefined(typeof(ClientLimitType), (ClientLimitType)0));
        Assert.False(Enum.IsDefined(typeof(ClientLimitType), (ClientLimitType)999));
    }

    // ==================== 4. 源码与菜单契约 ====================

    [Fact]
    public void 控制器源码契约_全部路由先授权_复用既有客户端限制菜单()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysSimpleControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("ClientLimitAuthorizationRules.EnsureAuthorizedAsync", source);
        var clientLimitSection = source[..source.IndexOf(
            "DocumentNumberRuleController", StringComparison.Ordinal)];
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            clientLimitSection, @"await EnsureClientLimitAuthorizedAsync\(\);").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            clientLimitSection, @"ClientLimitAuthorizationRules\.Validate\(entity\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);

        Assert.Equal("client-limit", ClientLimitAuthorizationRules.RequiredMenuCode);
        Assert.Equal("客户端限制", ClientLimitAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{ClientLimitAuthorizationRules.RequiredMenuCode}\", "
            + $"\"{ClientLimitAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>基类通用软删除契约未被改写（其它派生控制器仍走 BaseCrudController）。</summary>
    [Fact]
    public void 通用软删除契约保持不变_其它派生控制器不受影响()
    {
        var baseSource = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseCrudController.cs"));
        Assert.Contains("public virtual async Task<IActionResult> BatchDelete", baseSource);
        Assert.Contains("await Service.BatchDeleteAsync(ids);", baseSource);

        // 操作日志控制器（同一文件）行为保持不变：无新增授权覆盖。
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysSimpleControllers.cs"));
        Assert.Contains("public class OperationLogController : ControllerBase", source);
    }

    // ==================== 5. 测试辅助 ====================

    /// <summary>构造客户端限制控制器（内存库 + 通用 CRUD 服务）并注入指定登录身份（可空 = 无身份）。</summary>
    private static ClientLimitController NewController(ErpDbContext db, long? userId)
    {
        var controller = new ClientLimitController(new GenericService<SysClientLimit>(db), db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>待写入的客户端限制（默认合法形状）。</summary>
    private static SysClientLimit NewLimit(
        string limitValue,
        ClientLimitType limitType = ClientLimitType.IP,
        string remark = "测试客户端限制")
        => new()
        {
            LimitType = limitType,
            LimitValue = limitValue,
            IsEnabled = true,
            Remark = remark
        };

    /// <summary>播种一条既有客户端限制（用于读取 / 校验 / 软删除语义场景）。</summary>
    private static SysClientLimit SeedLimit(ErpDbContext db, string limitValue,
        ClientLimitType limitType = ClientLimitType.IP)
    {
        var limit = NewLimit(limitValue, limitType);
        db.SysClientLimits.Add(limit);
        db.SaveChanges();
        return limit;
    }

    /// <summary>播种账号（可选启用 / 删除 / 系统内置角色 / 既有 client-limit 菜单授权），返回用户 Id。</summary>
    private static long SeedUser(
        ErpDbContext db, UserStatus status, bool deleted, bool grantMenu, bool systemRole = false)
    {
        var user = new SysUser
        {
            UserName = $"client-limit-{Guid.NewGuid():N}",
            DisplayName = "客户端限制授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "客户端限制操作员",
            RoleCode = $"ClientLimitRole-{Guid.NewGuid():N}",
            IsSystem = systemRole
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, ClientLimitAuthorizationRules.RequiredMenuCode,
                ClientLimitAuthorizationRules.RequiredMenuText);
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

    /// <summary>客户端限制行快照（用于断言拒绝路径不新增 / 不改写 / 不软删除任何限制行）。</summary>
    private static List<string> Snapshot(ErpDbContext db)
        => db.SysClientLimits.AsNoTracking().OrderBy(l => l.Id).ToList()
            .Select(l => $"{l.Id}|{l.LimitType}|{l.LimitValue}|{l.IsEnabled}|{l.Remark}|{l.IsDeleted}")
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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
