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
/// ERP-443 其他资料数据字典（<c>api/base/other-infos</c>）实时身份 / 既有菜单 / 字段有界校验护栏单元测试。
/// <para>覆盖：分页 / 全部 / 详情 / 按类型查询 / 新增 / 修改 / 删除 / 批量删除在读取任何字典行之前对缺失 /
/// 已删除 / 禁用身份与无既有「其他资料」菜单身份 fail closed；撤销菜单后立即收敛；特权账号保留既有访问；
/// 已授予既有菜单的非特权账号可读可写；新增 / 修改对未知资料类型、空或有界的编码 / 名称、超长英文名称 /
/// 备注与非法状态返回既有受控校验错误，且被拒绝时不新增 / 不改写任何字典行。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。</para>
/// </summary>
public class OtherInfoAuthorizationTests
{
    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 全部路由_无身份_一律未认证且不读取或改写任何字典行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedOtherInfo(db, "Forwarder", "FD-ANON", "无身份货代");
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetByType("Forwarder"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewRow("Port", "PT-ANON", "无身份港口")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewRow("Forwarder", "FD-ANON", "改名")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.BatchDelete(new List<long> { row.Id }));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var row = SeedOtherInfo(db, "Port", "PT-ID", "身份港口");
        var before = Snapshot(db);
        var disabled = SeedRoleUser(db, UserStatus.Disabled, deleted: false, "other-info");
        var deleted = SeedRoleUser(db, UserStatus.Enabled, deleted: true, "other-info");

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetByType("Port"));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewRow("Port", "PT-D", "禁用港口")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 非特权_无既有其他资料菜单_全部路由拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var row = SeedOtherInfo(db, "Forwarder", "FD-NOMENU", "无菜单货代");
        var before = Snapshot(db);
        var noMenu = SeedRoleUser(db, UserStatus.Enabled, deleted: false);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetByType("Forwarder"));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewRow("Forwarder", "FD-NEW", "无菜单新增")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewRow("Forwarder", "FD-NOMENU", "无菜单改名")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { row.Id }));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 非特权_撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedOtherInfo(db, "Port", "PT-REVOKE", "收敛港口");
        var user = SeedRoleUser(db, UserStatus.Enabled, deleted: false, "other-info");
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery()));

        RevokeMenus(db, user);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetByType("Port"));
    }

    // ==================== 2. 放行路径（特权 / 已授予既有菜单） ====================

    [Fact]
    public async Task 特权账号_保留既有全部访问_可读可写()
    {
        using var db = TestDbFactory.Create();
        var row = SeedOtherInfo(db, "Forwarder", "FD-PRIV", "特权货代");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var ctl = NewController(db, privileged);

        var paged = AssertOk<PagedResult<BaseOtherInfo>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, o => o.Id == row.Id);
        Assert.Single(AssertOk<List<BaseOtherInfo>>(await ctl.GetAll()));
        Assert.Equal(row.Id, AssertOk<BaseOtherInfo>(await ctl.GetById(row.Id)).Id);
        Assert.Single(AssertOk<List<BaseOtherInfo>>(await ctl.GetByType("Forwarder")));

        var created = AssertOk<BaseOtherInfo>(await ctl.Create(NewRow("Port", "PT-PRIV", "特权新增港口")));
        Assert.True(created.Id > 0);
        Assert.True(db.BaseOtherInfos.Any(o => o.Id == created.Id));

        var updated = AssertOk<BaseOtherInfo>(await ctl.Update(created.Id, NewRow("Port", "PT-PRIV", "特权改名港口")));
        Assert.Equal("特权改名港口", updated.InfoName);

        Assert.IsType<OkObjectResult>(await ctl.Delete(created.Id));
        Assert.True(db.BaseOtherInfos.Single(o => o.Id == created.Id).IsDeleted);
    }

    [Fact]
    public async Task 非特权_已授予其他资料菜单_可读可写且按类型查询只返回启用行()
    {
        using var db = TestDbFactory.Create();
        SeedOtherInfo(db, "Forwarder", "FD-ON", "启用货代");
        SeedOtherInfo(db, "Forwarder", "FD-OFF", "停用货代", status: 0);
        SeedOtherInfo(db, "Port", "PT-ON", "启用港口");
        var user = SeedRoleUser(db, UserStatus.Enabled, deleted: false, "other-info");
        var ctl = NewController(db, user);

        var options = AssertOk<List<BaseOtherInfo>>(await ctl.GetByType("Forwarder"));
        var only = Assert.Single(options);
        Assert.Equal("FD-ON", only.InfoCode);

        var created = AssertOk<BaseOtherInfo>(await ctl.Create(NewRow("Currency", "CNY", "人民币")));
        var updated = AssertOk<BaseOtherInfo>(await ctl.Update(created.Id, NewRow("Currency", "CNY", "人民币（改）")));
        Assert.Equal("人民币（改）", updated.InfoName);

        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { created.Id }));
        Assert.True(db.BaseOtherInfos.Single(o => o.Id == created.Id).IsDeleted);
    }

    // ==================== 3. 字段有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task 新增_未知资料类型_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var before = Snapshot(db);
        var ctl = NewController(db, privileged);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Create(NewRow("NotAKnownType", "X-1", "未知类型")));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已知的有界字典类型", ex.Message);

        AssertUnchanged(db, before);
    }

    [Theory]
    [InlineData("", "C-1", "名称")]
    [InlineData("   ", "C-1", "名称")]
    [InlineData("Port", "", "名称")]
    [InlineData("Port", "   ", "名称")]
    [InlineData("Port", "C-1", "")]
    [InlineData("Port", "C-1", "   ")]
    public async Task 新增_类型或编码或名称为空_拒绝且不落库(string infoType, string infoCode, string infoName)
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var before = Snapshot(db);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRow(infoType, infoCode, infoName)));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 新增_超长字段_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var before = Snapshot(db);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewRow("Port", new string('C', 51), "名称")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewRow("Port", "C-2", new string('名', 101))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewRow("Port", "C-3", "名称", englishName: new string('E', 101))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewRow("Port", "C-4", "名称", remark: new string('R', 501))));

        AssertUnchanged(db, before);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task 新增_非法状态_拒绝且不落库(int status)
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var before = Snapshot(db);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewRow("Port", "ST-1", "状态港口", status: status)));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_非法字段_拒绝且不改写原行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedOtherInfo(db, "Port", "PT-KEEP", "原名港口");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var before = Snapshot(db);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRow("NotAKnownType", "PT-KEEP", "改名")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRow("Port", "PT-KEEP", new string('名', 101))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRow("Port", "PT-KEEP", "改名", status: 7)));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_合法字段_写入成功()
    {
        using var db = TestDbFactory.Create();
        var row = SeedOtherInfo(db, "Port", "PT-EDIT", "原港口");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var ctl = NewController(db, privileged);

        var updated = AssertOk<BaseOtherInfo>(
            await ctl.Update(row.Id, NewRow("Port", "PT-EDIT", "新港口", status: 0)));

        Assert.Equal("新港口", updated.InfoName);
        Assert.Equal(0, updated.Status);
        var stored = db.BaseOtherInfos.Single(o => o.Id == row.Id);
        Assert.Equal("新港口", stored.InfoName);
        Assert.Equal(0, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public void 已知资料类型_与既有页面取值同源且忽略大小写与首尾空白()
    {
        foreach (var type in OtherInfoAuthorizationRules.KnownInfoTypes)
            Assert.True(OtherInfoAuthorizationRules.IsKnownInfoType(type));

        Assert.True(OtherInfoAuthorizationRules.IsKnownInfoType(" forwarder "));
        Assert.True(OtherInfoAuthorizationRules.IsKnownInfoType("CUSTOMSBROKER"));
        Assert.False(OtherInfoAuthorizationRules.IsKnownInfoType("NotAKnownType"));
        Assert.False(OtherInfoAuthorizationRules.IsKnownInfoType(null));
        Assert.False(OtherInfoAuthorizationRules.IsKnownInfoType("   "));
    }

    // ==================== 4. 测试辅助 ====================

    /// <summary>构造其他资料控制器（内存库 + 通用 CRUD 服务）并注入指定登录身份（可空 = 无身份）。</summary>
    private static OtherInfoController NewController(ErpDbContext db, long? userId)
    {
        var controller = new OtherInfoController(new GenericService<BaseOtherInfo>(db), db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>待写入的其他资料字典行（默认启用）。</summary>
    private static BaseOtherInfo NewRow(string infoType, string infoCode, string infoName,
        string englishName = "", int status = 1, string remark = "")
        => new()
        {
            InfoType = infoType,
            InfoCode = infoCode,
            InfoName = infoName,
            EnglishName = englishName,
            Status = status,
            Remark = remark
        };

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

    /// <summary>字典行快照（用于断言拒绝路径不新增 / 不改写任何行）。</summary>
    private static List<string> Snapshot(ErpDbContext db)
        => db.BaseOtherInfos.AsNoTracking().OrderBy(o => o.Id).ToList()
            .Select(o =>
                $"{o.Id}|{o.InfoType}|{o.InfoCode}|{o.InfoName}|{o.EnglishName}|{o.Status}|{o.Remark}|{o.IsDeleted}")
            .ToList();

    private static void AssertUnchanged(ErpDbContext db, List<string> before)
        => Assert.Equal(before, Snapshot(db));

    /// <summary>造一条「其他资料」字典项。</summary>
    private static BaseOtherInfo SeedOtherInfo(ErpDbContext db, string infoType, string infoCode, string infoName,
        int status = 1, bool deleted = false)
    {
        var entry = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = infoCode,
            InfoName = infoName,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseOtherInfos.Add(entry);
        db.SaveChanges();
        return entry;
    }

    /// <summary>普通账号（非特权）：可选授予既有菜单编码，默认无任何菜单授权。</summary>
    private static long SeedRoleUser(ErpDbContext db, UserStatus status, bool deleted, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"oi-user-{Guid.NewGuid():N}",
            DisplayName = "其他资料账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "其他资料操作员", RoleCode = $"OiRole-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        GrantMenus(db, role.Id, menuCodes);
        return user.Id;
    }

    /// <summary>复用 / 新建既有菜单并授予角色（不新增任何权限模型，菜单编码与 SeedData 同源）。</summary>
    private static void GrantMenus(ErpDbContext db, long roleId, IEnumerable<string> menuCodes)
    {
        foreach (var code in menuCodes)
        {
            var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuCode = code, MenuName = code, MenuType = MenuType.Menu, Path = $"/base/{code}" };
                db.SysMenus.Add(menu);
                db.SaveChanges();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    /// <summary>撤销账号当前角色下的全部菜单授权（模拟授权撤销，验证下一次请求立即收敛）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }
}
