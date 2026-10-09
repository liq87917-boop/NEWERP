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
/// 供应商资料（<c>api/base/suppliers</c>）实时授权与有界字段校验单元测试（ERP-447）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「供应商资料」菜单 / 无菜单的身份在
/// <b>全部 7 条路由</b>（分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除）fail closed，
/// 且 <c>BaseSuppliers</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「供应商资料」菜单的授权身份下，既有读 / 写契约与分页 / 响应契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段校验</b>：编码 / 名称空值、文本长度越界、税率 / 返点比例超 <c>DECIMAL(18,4)</c>、
/// 状态非 0 / 1 一律按受控参数错误拒绝且不落任何行 / 不改写任何行；</item>
/// <item><b>源码契约</b>：控制器 7 条路由全部先授权再读写，且只复用既有 <c>supplier</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class SupplierMasterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static SupplierController Controller(ErpDbContext db, long? userId) =>
        new(new GenericService<BaseSupplier>(db), db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>
    /// 带（可空）<c>NameIdentifier</c> 的真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：
    /// null = 无身份，真实请求因处于请求管线内一律 fail closed 拒绝。
    /// </summary>
    private static ControllerContext ContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        // 标记为真实 HTTP 路由（Request.Path 已赋值）：缺失身份也必须实时授权并 fail closed。
        http.Request.Path = "/api/base/suppliers";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 供应商菜单），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantSupplierMenu = true)
    {
        var user = new SysUser
        {
            UserName = $"supplier-auth-{Guid.NewGuid():N}",
            DisplayName = "供应商授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "供应商授权用例角色",
            RoleCode = $"SupplierAuthCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantSupplierMenu)
            GrantMenu(db, role.Id, SupplierAuthorizationRules.RequiredMenuCode, SupplierAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单）</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        if (!db.SysRoleMenus.Any(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static BaseSupplier SeedSupplier(
        ErpDbContext db, string code = "S001", string name = "义乌工厂", int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status, IsDeleted = deleted };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseSupplier NewSupplier(
        string code = "S-NEW", string name = "新供应商", int status = 1,
        string remark = "", decimal taxRate = 0m, decimal rebateRatio = 0m)
        => new()
        {
            SupplierCode = code,
            SupplierName = name,
            Status = status,
            Remark = remark,
            TaxRate = taxRate,
            RebateRatio = rebateRatio
        };

    /// <summary>供应商主表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseSuppliers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.SupplierCode}:{x.SupplierName}:{x.Remark}")
            .ToList());

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有供应商菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「供应商资料」菜单 / 无任何菜单的身份：全部 7 条路由
    /// 在读取或写入任何供应商之前 fail closed，且供应商主表逐字节不变（拒绝既不读取也不改写任何行）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何供应商(string scenario)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantSupplierMenu: false)
        };
        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetAll());
        await AssertCode(expectedCode, () => ctl.GetById(supplier.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewSupplier(code: "S-DENIED", name: "被拒供应商")));
        await AssertCode(expectedCode, () => ctl.Update(supplier.Id, NewSupplier(code: "S001", name: "被拒改名")));
        await AssertCode(expectedCode, () => ctl.Delete(supplier.Id));
        await AssertCode(expectedCode, () => ctl.BatchDelete(new List<long> { supplier.Id }));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseSuppliers.AsNoTracking().SingleAsync();
        Assert.Equal("S001", stored.SupplierCode);
        Assert.Equal("义乌工厂", stored.SupplierName);
        Assert.Equal(1, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行与撤销收敛 ====================

    /// <summary>具备既有「供应商资料」菜单的授权身份：既有供应商读 / 写契约与响应契约照常放行。</summary>
    [Fact]
    public async Task Auth_具备既有供应商菜单_放行既有读写契约()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var created = Data<BaseSupplier>(await ctl.Create(NewSupplier(code: "S-OK", name: "授权供应商")));
        Assert.True(created.Id > 0);
        Assert.Equal("S-OK", created.SupplierCode);

        var page = Data<PagedResult<BaseSupplier>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Equal(1, page.Total);
        Assert.Single(Data<List<BaseSupplier>>(await ctl.GetAll()));
        Assert.Equal("授权供应商", Data<BaseSupplier>(await ctl.GetById(created.Id)).SupplierName);

        var updated = Data<BaseSupplier>(await ctl.Update(created.Id, NewSupplier(code: "S-OK", name: "授权供应商改名")));
        Assert.Equal("授权供应商改名", updated.SupplierName);

        var second = Data<BaseSupplier>(await ctl.Create(NewSupplier(code: "S-OK2", name: "授权供应商2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseSuppliers.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseSuppliers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<BaseSupplier>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 })); // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(supplier.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewSupplier(code: "S-REVOKED")));
        Assert.False((await db.BaseSuppliers.AsNoTracking().SingleAsync(x => x.Id == supplier.Id)).IsDeleted);
        Assert.Equal(1, await db.BaseSuppliers.AsNoTracking().CountAsync());
    }

    // ==================== 3. 有界字段校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>
    /// 授权身份下的非法载荷（编码 / 名称空值、文本长度越界、税率 / 返点比例超 <c>DECIMAL(18,4)</c> 可存储范围、
    /// 状态非已知值）：新增与修改都按受控参数错误拒绝，且不落任何新行、不改写任何既有行。
    /// </summary>
    [Theory]
    [InlineData("empty-code")]
    [InlineData("blank-name")]
    [InlineData("code-too-long")]
    [InlineData("name-too-long")]
    [InlineData("remark-too-long")]
    [InlineData("taxrate-overflow")]
    [InlineData("rebate-overflow")]
    [InlineData("status-unknown")]
    public async Task Validation_非法载荷_新增与修改均拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var existing = SeedSupplier(db, code: "S-EXIST", name: "既有供应商");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        BaseSupplier CreatePayload() => scenario switch
        {
            "empty-code" => NewSupplier(code: "", name: "名称"),
            "blank-name" => NewSupplier(code: "S-1", name: "   "),
            "code-too-long" => NewSupplier(code: new string('C', SupplierAuthorizationRules.MaxSupplierCodeLength + 1)),
            "name-too-long" => NewSupplier(code: "S-1", name: new string('N', SupplierAuthorizationRules.MaxSupplierNameLength + 1)),
            "remark-too-long" => NewSupplier(code: "S-1", name: "名称", remark: new string('R', SupplierAuthorizationRules.MaxRemarkLength + 1)),
            "taxrate-overflow" => NewSupplier(code: "S-1", name: "名称", taxRate: 100000000000000m),
            "rebate-overflow" => NewSupplier(code: "S-1", name: "名称", rebateRatio: -100000000000000m),
            _ => NewSupplier(code: "S-1", name: "名称", status: 2)
        };

        var before = Snapshot(db);

        // 新增：拒绝且不落任何行
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(CreatePayload()));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.BaseSuppliers.AsNoTracking().CountAsync());

        // 修改：拒绝且不改写既有行
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, CreatePayload()));
        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseSuppliers.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("S-EXIST", stored.SupplierCode);
        Assert.Equal("既有供应商", stored.SupplierName);
        Assert.Equal(1, stored.Status);
    }

    /// <summary>边界值必须放行：编码 / 名称恰好等于持久化长度上限、税率 / 返点等于可存储上限、状态 0 / 1 都接受。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var codeAtLimit = new string('C', SupplierAuthorizationRules.MaxSupplierCodeLength);
        var nameAtLimit = new string('N', SupplierAuthorizationRules.MaxSupplierNameLength);
        var created = Data<BaseSupplier>(await ctl.Create(new BaseSupplier
        {
            SupplierCode = codeAtLimit,
            SupplierName = nameAtLimit,
            TaxRate = SupplierAuthorizationRules.MaxRateMagnitude,
            RebateRatio = -SupplierAuthorizationRules.MaxRateMagnitude,
            Status = SupplierAuthorizationRules.DisabledStatus
        }));
        Assert.Equal(codeAtLimit, created.SupplierCode);
        Assert.Equal(nameAtLimit, created.SupplierName);
        Assert.Equal(SupplierAuthorizationRules.DisabledStatus, created.Status);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(new BaseSupplier
        {
            SupplierCode = codeAtLimit + "C",
            SupplierName = "超长编码",
            Status = 1
        }));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(new BaseSupplier
        {
            SupplierCode = "S-2",
            SupplierName = nameAtLimit + "N",
            Status = 1
        }));
        Assert.Equal(1, await db.BaseSuppliers.AsNoTracking().CountAsync());
    }

    // ==================== 4. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：7 条路由全部先经实时授权再读写，且无匿名 / 角色回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>授权口径复用既有「供应商资料」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有供应商菜单常量_不新增菜单()
    {
        Assert.Equal("supplier", SupplierAuthorizationRules.RequiredMenuCode);
        Assert.Equal("供应商资料", SupplierAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{SupplierAuthorizationRules.RequiredMenuCode}\", \"{SupplierAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

