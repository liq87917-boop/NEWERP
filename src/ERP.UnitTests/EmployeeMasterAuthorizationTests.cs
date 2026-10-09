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
/// 员工资料（<c>api/base/employees</c>）实时授权与有界字段校验单元测试（ERP-449）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「员工资料」菜单 / 无菜单的身份在
/// <b>全部 8 条路由</b>（分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除 / 业务员下拉）fail closed，
/// 且 <c>BaseEmployees</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「员工资料」菜单的授权身份下，既有读 / 写契约与分页 / 响应契约保持，
/// 业务员下拉仍只返回在职（未删除）业务员；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段校验</b>：编码 / 姓名空值、文本长度越界、入职日期越界、状态非 0 / 1 一律按受控参数错误
/// 拒绝且不落任何行 / 不改写任何行，被拒绝的写入后业务员下拉仍只返回在职业务员；</item>
/// <item><b>源码契约</b>：控制器 8 条路由全部先授权再读写，且只复用既有 <c>employee</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class EmployeeMasterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static EmployeeController Controller(ErpDbContext db, long? userId) =>
        new(new GenericService<BaseEmployee>(db), db) { ControllerContext = ContextWithUser(userId) };

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
        http.Request.Path = "/api/base/employees";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 员工菜单），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantEmployeeMenu = true)
    {
        var user = new SysUser
        {
            UserName = $"employee-auth-{Guid.NewGuid():N}",
            DisplayName = "员工授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "员工授权用例角色",
            RoleCode = $"EmployeeAuthCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantEmployeeMenu)
            GrantMenu(db, role.Id, EmployeeAuthorizationRules.RequiredMenuCode, EmployeeAuthorizationRules.RequiredMenuText);
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

    private static BaseEmployee SeedEmployee(
        ErpDbContext db, string code = "E001", string name = "张三", int status = 1,
        bool isSalesman = false, bool deleted = false)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            Status = status,
            IsSalesman = isSalesman,
            IsDeleted = deleted
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseEmployee NewEmployee(
        string code = "E-NEW", string name = "新员工", int status = 1, bool isSalesman = false,
        string department = "", string position = "", string phone = "", string email = "",
        DateTime? hireDate = null)
        => new()
        {
            EmployeeCode = code,
            EmployeeName = name,
            Status = status,
            IsSalesman = isSalesman,
            Department = department,
            Position = position,
            Phone = phone,
            Email = email,
            HireDate = hireDate
        };

    /// <summary>员工主表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseEmployees.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.EmployeeCode}:{x.EmployeeName}:{x.Department}:{x.IsSalesman}")
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

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有员工菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「员工资料」菜单 / 无任何菜单的身份：全部 8 条路由
    /// 在读取或写入任何员工之前 fail closed，且员工主表逐字节不变（拒绝既不读取也不改写任何行）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何员工(string scenario)
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db, code: "E-EXIST", name: "既有员工", isSalesman: true);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantEmployeeMenu: false)
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
        await AssertCode(expectedCode, () => ctl.GetById(employee.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewEmployee(code: "E-DENIED", name: "被拒员工")));
        await AssertCode(expectedCode, () => ctl.Update(employee.Id, NewEmployee(code: employee.EmployeeCode, name: "被拒改名")));
        await AssertCode(expectedCode, () => ctl.Delete(employee.Id));
        await AssertCode(expectedCode, () => ctl.BatchDelete(new List<long> { employee.Id }));
        await AssertCode(expectedCode, () => ctl.GetSalesmen());

        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == employee.Id);
        Assert.Equal("E-EXIST", stored.EmployeeCode);
        Assert.False(stored.IsDeleted);
        Assert.Equal(1, await db.BaseEmployees.AsNoTracking().CountAsync());
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    /// <summary>具备既有「员工资料」菜单的授权身份下，分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除全部放行。</summary>
    [Fact]
    public async Task Auth_具备员工菜单身份_既有读写契约放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var created = Data<BaseEmployee>(await ctl.Create(NewEmployee(code: "E-NEW", name: "新员工", isSalesman: true)));
        Assert.True(created.Id > 0);

        var page = Data<PagedResult<BaseEmployee>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Contains(page.Items, e => e.Id == created.Id);
        Assert.Contains(Data<List<BaseEmployee>>(await ctl.GetAll()), e => e.Id == created.Id);
        Assert.Equal("新员工", Data<BaseEmployee>(await ctl.GetById(created.Id)).EmployeeName);

        var updated = Data<BaseEmployee>(await ctl.Update(created.Id, NewEmployee(code: "E-NEW", name: "改名员工", isSalesman: true)));
        Assert.Equal("改名员工", updated.EmployeeName);

        var second = Data<BaseEmployee>(await ctl.Create(NewEmployee(code: "E-NEW2", name: "员工2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseEmployees.AsNoTracking().SingleAsync(e => e.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseEmployees.AsNoTracking().SingleAsync(e => e.Id == created.Id)).IsDeleted);
    }

    /// <summary>业务员下拉仍只返回未删除、在职（Status=1）且 IsSalesman 的员工，且被软删除的业务员不出现。</summary>
    [Fact]
    public async Task Auth_业务员下拉_只返回在职未删除业务员()
    {
        using var db = TestDbFactory.Create();
        var onDuty = SeedEmployee(db, code: "S-ON", name: "在职业务员", status: 1, isSalesman: true);
        var offDuty = SeedEmployee(db, code: "S-OFF", name: "离职业务员", status: 0, isSalesman: true);
        var deleted = SeedEmployee(db, code: "S-DEL", name: "已删除业务员", status: 1, isSalesman: true, deleted: true);
        var nonSalesman = SeedEmployee(db, code: "N-1", name: "非业务员", status: 1, isSalesman: false);

        var userId = SeedUser(db);
        var ctl = Controller(db, userId);
        var items = Data<List<BaseEmployee>>(await ctl.GetSalesmen());

        Assert.Contains(items, e => e.Id == onDuty.Id);
        Assert.DoesNotContain(items, e => e.Id == offDuty.Id);
        Assert.DoesNotContain(items, e => e.Id == deleted.Id);
        Assert.DoesNotContain(items, e => e.Id == nonSalesman.Id);
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db, code: "E-REVOKE", name: "待改名员工", isSalesman: true);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<BaseEmployee>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 })); // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(employee.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewEmployee(code: "E-REVOKED")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetSalesmen());
        Assert.False((await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == employee.Id)).IsDeleted);
        Assert.Equal(1, await db.BaseEmployees.AsNoTracking().CountAsync());
    }

    // ==================== 3. 有界字段校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>
    /// 授权身份下的非法载荷（编码 / 姓名空值、文本长度越界、入职日期越界、状态非已知值）：新增与修改都按
    /// 受控参数错误拒绝，且不落任何新行、不改写任何既有行。
    /// </summary>
    [Theory]
    [InlineData("empty-code")]
    [InlineData("blank-name")]
    [InlineData("code-too-long")]
    [InlineData("name-too-long")]
    [InlineData("department-too-long")]
    [InlineData("position-too-long")]
    [InlineData("phone-too-long")]
    [InlineData("email-too-long")]
    [InlineData("hire-date-out-of-shape")]
    [InlineData("status-unknown")]
    public async Task Validation_非法载荷_新增与修改均拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var existing = SeedEmployee(db, code: "E-EXIST", name: "既有员工");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        BaseEmployee CreatePayload() => scenario switch
        {
            "empty-code" => NewEmployee(code: "", name: "姓名"),
            "blank-name" => NewEmployee(code: "E-1", name: "   "),
            "code-too-long" => NewEmployee(code: new string('C', EmployeeAuthorizationRules.MaxEmployeeCodeLength + 1)),
            "name-too-long" => NewEmployee(code: "E-1", name: new string('N', EmployeeAuthorizationRules.MaxEmployeeNameLength + 1)),
            "department-too-long" => NewEmployee(code: "E-1", name: "姓名", department: new string('D', EmployeeAuthorizationRules.MaxDepartmentLength + 1)),
            "position-too-long" => NewEmployee(code: "E-1", name: "姓名", position: new string('P', EmployeeAuthorizationRules.MaxPositionLength + 1)),
            "phone-too-long" => NewEmployee(code: "E-1", name: "姓名", phone: new string('H', EmployeeAuthorizationRules.MaxPhoneLength + 1)),
            "email-too-long" => NewEmployee(code: "E-1", name: "姓名", email: new string('M', EmployeeAuthorizationRules.MaxEmailLength + 1)),
            "hire-date-out-of-shape" => NewEmployee(code: "E-1", name: "姓名", hireDate: default(DateTime)),
            _ => NewEmployee(code: "E-1", name: "姓名", status: 2)
        };

        var before = Snapshot(db);

        // 新增：拒绝且不落任何行
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(CreatePayload()));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.BaseEmployees.AsNoTracking().CountAsync());

        // 修改：拒绝且不改写既有行
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, CreatePayload()));
        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("E-EXIST", stored.EmployeeCode);
        Assert.Equal("既有员工", stored.EmployeeName);
        Assert.Equal(1, stored.Status);
    }

    /// <summary>
    /// 被拒绝的写入不落任何行且不影响业务员下拉：非法新增后下拉仍只返回在职未删除业务员。
    /// </summary>
    [Fact]
    public async Task Validation_拒绝写入后_业务员下拉仍只返回在职业务员()
    {
        using var db = TestDbFactory.Create();
        var onDuty = SeedEmployee(db, code: "S-ON", name: "在职业务员", status: 1, isSalesman: true);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewEmployee(code: "S-BAD", name: "非法业务员", isSalesman: true, status: 7)));

        var items = Data<List<BaseEmployee>>(await ctl.GetSalesmen());
        Assert.Contains(items, e => e.Id == onDuty.Id);
        Assert.DoesNotContain(items, e => e.EmployeeCode == "S-BAD");
    }

    /// <summary>边界值必须放行：编码 / 姓名恰好等于持久化长度上限、可选文本等于上限、入职日期在范围内、状态 0 / 1 都接受。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var codeAtLimit = new string('C', EmployeeAuthorizationRules.MaxEmployeeCodeLength);
        var nameAtLimit = new string('N', EmployeeAuthorizationRules.MaxEmployeeNameLength);
        var created = Data<BaseEmployee>(await ctl.Create(NewEmployee(
            code: codeAtLimit,
            name: nameAtLimit,
            status: EmployeeAuthorizationRules.OffDutyStatus,
            department: new string('D', EmployeeAuthorizationRules.MaxDepartmentLength),
            position: new string('P', EmployeeAuthorizationRules.MaxPositionLength),
            phone: new string('H', EmployeeAuthorizationRules.MaxPhoneLength),
            email: new string('M', EmployeeAuthorizationRules.MaxEmailLength),
            hireDate: EmployeeAuthorizationRules.MaxHireDate)));
        Assert.Equal(codeAtLimit, created.EmployeeCode);
        Assert.Equal(nameAtLimit, created.EmployeeName);
        Assert.Equal(EmployeeAuthorizationRules.OffDutyStatus, created.Status);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            code: codeAtLimit + "C", name: "超长编码")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            code: "E-2", name: nameAtLimit + "N")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            code: "E-3", name: "姓名", hireDate: EmployeeAuthorizationRules.MinHireDate.AddDays(-1))));
        Assert.Equal(1, await db.BaseEmployees.AsNoTracking().CountAsync());
    }

    // ==================== 4. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：8 条路由全部先经实时授权再读写，且无匿名 / 角色回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureEmployeeAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>授权口径复用既有「员工资料」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有员工菜单常量_不新增菜单()
    {
        Assert.Equal("employee", EmployeeAuthorizationRules.RequiredMenuCode);
        Assert.Equal("员工资料", EmployeeAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{EmployeeAuthorizationRules.RequiredMenuCode}\", \"{EmployeeAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

