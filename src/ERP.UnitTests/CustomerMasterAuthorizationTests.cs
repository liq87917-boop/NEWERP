using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// 客户资料（<c>api/base/customers</c>）实时授权与有界字段校验单元测试（ERP-451）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「客户资料」菜单 / 无菜单的身份在
/// <b>全部 8 条路由</b>（分页 / 全部 / 按主键 / 指定货代下拉 / 新增 / 修改 / 删除 / 批量删除）fail closed，
/// 且 <c>BaseCustomers</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「客户资料」菜单的授权身份下，既有读 / 写契约与分页 / 响应契约保持；</item>
/// <item><b>ERP-097 读取范围不变</b>：授权身份下的受限业务员仍只看到自己被分配的客户；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段校验</b>：编码 / 名称空值、文本长度越界、数值超 <c>DECIMAL(18,4)</c>、
/// 负数业务员 Id / 账期天数、状态非 0 / 1 一律按受控参数错误拒绝且不落任何行 / 不改写任何行；</item>
/// <item><b>源码契约</b>：控制器 8 条路由全部先授权再读写，且只复用既有 <c>customer</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class CustomerMasterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static CustomerController Controller(ErpDbContext db, long? userId) =>
        new(new GenericService<BaseCustomer>(db), db) { ControllerContext = ContextWithUser(userId) };

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
        http.Request.Path = "/api/base/customers";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>进程内直调上下文（无请求路径、可带身份）：用于断言历史单元测试口径保持不变。</summary>
    private static ControllerContext InternalContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 客户菜单 / 特权角色），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantCustomerMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"customer-auth-{Guid.NewGuid():N}",
            DisplayName = "客户授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "客户授权用例角色",
            RoleCode = $"CustomerAuthCase-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantCustomerMenu)
            GrantMenu(db, role.Id, CustomerAuthorizationRules.RequiredMenuCode, CustomerAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>授予既有功能菜单（幂等；菜单缺失时按既有种子口径补建一条功能菜单）</summary>
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

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code = "C001", string name = "义乌客户", int status = 1, bool deleted = false,
        long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseCustomer NewCustomer(
        string code = "C-NEW", string name = "新客户", int status = 1, string remark = "",
        decimal creditLimit = 0m, decimal depositRatio = 0m, decimal commissionRatio = 0m,
        long? empId = null, int? creditDays = null)
        => new()
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            Remark = remark,
            CreditLimit = creditLimit,
            DepositRatio = depositRatio,
            CommissionRatio = commissionRatio,
            EmpId = empId,
            CreditDays = creditDays
        };

    /// <summary>客户主表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseCustomers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.CustomerCode}:{x.CustomerName}:{x.Remark}:" +
                         $"{x.CreditLimit}:{x.EmpId}:{x.CreditDays}")
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

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有客户菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「客户资料」菜单 / 无任何菜单的身份：全部 8 条路由
    /// 在读取或写入任何客户之前 fail closed，且客户主表逐字节不变（拒绝既不读取也不改写任何行）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何客户(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantCustomerMenu: false)
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
        await AssertCode(expectedCode, () => ctl.GetById(customer.Id));
        await AssertCode(expectedCode, () => ctl.GetForwarderOptions());
        await AssertCode(expectedCode, () => ctl.Create(NewCustomer(code: "C-DENIED", name: "被拒客户")));
        await AssertCode(expectedCode, () => ctl.Update(customer.Id, NewCustomer(code: customer.CustomerCode, name: "被拒改名")));
        await AssertCode(expectedCode, () => ctl.Delete(customer.Id));
        await AssertCode(expectedCode, () => ctl.BatchDelete(new List<long> { customer.Id }));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == customer.Id);
        Assert.Equal(customer.CustomerCode, stored.CustomerCode);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Auth_授权身份_既有读写契约放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var ctl = Controller(db, userId);

        var created = Data<BaseCustomer>(await ctl.Create(NewCustomer(code: "C-OK", name: "授权客户")));
        Assert.True(created.Id > 0);

        var page = Data<PagedResult<BaseCustomer>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, c => c.Id == created.Id && c.CustomerCode == "C-OK");
        Assert.Contains(Data<List<BaseCustomer>>(await ctl.GetAll()), c => c.Id == created.Id);
        Assert.Equal("授权客户", Data<BaseCustomer>(await ctl.GetById(created.Id)).CustomerName);

        // 指定货代下拉（无货代字典项时返回空集合）放行
        var options = Data<List<OtherInfoOptionDto>>(await ctl.GetForwarderOptions());
        Assert.Empty(options);

        var updated = Data<BaseCustomer>(await ctl.Update(created.Id, NewCustomer(code: "C-OK", name: "授权客户改名")));
        Assert.Equal("授权客户改名", updated.CustomerName);

        var second = Data<BaseCustomer>(await ctl.Create(NewCustomer(code: "C-OK2", name: "授权客户2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }


    /// <summary>
    /// 授权身份下的受限业务员（登录账号 == 员工编码）仍沿用 ERP-097 读取范围：分页只返回自己被分配的客户，
    /// 越界客户按「不存在」fail closed —— 既有菜单门不改变也不放宽 ERP-097 读取范围。
    /// </summary>
    [Fact]
    public async Task Auth_授权身份_保留ERP097受限业务员读取范围()
    {
        using var db = TestDbFactory.Create();

        var user = new SysUser
        {
            UserName = "alice",
            DisplayName = "alice",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "Sales", RoleCode = $"Sales-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        GrantMenu(db, role.Id, CustomerAuthorizationRules.RequiredMenuCode, CustomerAuthorizationRules.RequiredMenuText);

        var employee = new BaseEmployee { EmployeeCode = "alice", EmployeeName = "Alice", IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var mine = SeedCustomer(db, "C-MINE", "我的客户", empId: employee.Id);
        var others = SeedCustomer(db, "C-OTHER", "别人的客户", empId: employee.Id + 1000);
        var ctl = Controller(db, user.Id);

        var page = Data<PagedResult<BaseCustomer>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Single(page.Items);
        Assert.Equal(mine.Id, page.Items[0].Id);
        Assert.Equal(1, page.Total);

        Assert.Equal(mine.Id, Data<BaseCustomer>(await ctl.GetById(mine.Id)).Id);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(others.Id));
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<BaseCustomer>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 })); // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForwarderOptions());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(customer.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewCustomer(code: "C-REVOKED")));
        Assert.False((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == customer.Id)).IsDeleted);
        Assert.Equal(1, await db.BaseCustomers.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// 进程内直调边界（与仓库既有口径同源）：未进入 HTTP 请求管线（<c>Request.Path</c> 为空）的历史单元测试 /
    /// 内部派生读取沿用既有语义；真实 HTTP 请求（<c>Request.Path</c> 已赋值）一律实时授权并 fail closed。
    /// </summary>
    [Fact]
    public async Task Auth_进程内直调无请求路径_沿用既有语义()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C-INT-1", "直调客户");
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        var ctl = new CustomerController(new GenericService<BaseCustomer>(db), db)
        {
            ControllerContext = InternalContextWithUser(privilegedId)
        };

        var page = Data<PagedResult<BaseCustomer>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Contains(page.Items, c => c.Id == mine.Id);
    }


    // ==================== 3. 有界字段校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>
    /// 授权身份下的非法载荷（编码 / 名称空值、文本长度越界、数值超 <c>DECIMAL(18,4)</c> 可存储范围、
    /// 负数业务员 Id / 账期天数、状态非已知值）：新增与修改都按受控参数错误拒绝，
    /// 且不落任何新行、不改写任何既有行。
    /// </summary>
    [Theory]
    [InlineData("empty-code")]
    [InlineData("blank-name")]
    [InlineData("code-too-long")]
    [InlineData("name-too-long")]
    [InlineData("text-too-long")]
    [InlineData("credit-overflow")]
    [InlineData("deposit-overflow")]
    [InlineData("commission-overflow")]
    [InlineData("negative-emp")]
    [InlineData("negative-creditdays")]
    [InlineData("status-unknown")]
    public async Task Validation_非法载荷_新增与修改均拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var existing = SeedCustomer(db, code: "C-EXIST", name: "既有客户");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        BaseCustomer CreatePayload() => scenario switch
        {
            "empty-code" => NewCustomer(code: "", name: "名称"),
            "blank-name" => NewCustomer(code: "C-1", name: "   "),
            "code-too-long" => NewCustomer(code: new string('C', CustomerAuthorizationRules.MaxCustomerCodeLength + 1)),
            "name-too-long" => NewCustomer(code: "C-1", name: new string('N', CustomerAuthorizationRules.MaxCustomerNameLength + 1)),
            "text-too-long" => NewCustomer(code: "C-1", name: "名称",
                remark: new string('R', CustomerAuthorizationRules.MaxRemarkLength + 1)),
            "credit-overflow" => NewCustomer(code: "C-1", name: "名称", creditLimit: 100000000000000m),
            "deposit-overflow" => NewCustomer(code: "C-1", name: "名称", depositRatio: -100000000000000m),
            "commission-overflow" => NewCustomer(code: "C-1", name: "名称", commissionRatio: 100000000000000m),
            "negative-emp" => NewCustomer(code: "C-1", name: "名称", empId: -1),
            "negative-creditdays" => NewCustomer(code: "C-1", name: "名称", creditDays: -1),
            _ => NewCustomer(code: "C-1", name: "名称", status: 2)
        };

        var before = Snapshot(db);

        // 新增：拒绝且不落任何行
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(CreatePayload()));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.BaseCustomers.AsNoTracking().CountAsync());

        // 修改：拒绝且不改写既有行
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, CreatePayload()));
        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("C-EXIST", stored.CustomerCode);
        Assert.Equal("既有客户", stored.CustomerName);
        Assert.Equal(1, stored.Status);
    }

    /// <summary>边界值必须放行：编码 / 名称恰好等于持久化长度上限、数值等于可存储上限、状态 0 / 1 都接受。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var codeAtLimit = new string('C', CustomerAuthorizationRules.MaxCustomerCodeLength);
        var nameAtLimit = new string('N', CustomerAuthorizationRules.MaxCustomerNameLength);
        var created = Data<BaseCustomer>(await ctl.Create(new BaseCustomer
        {
            CustomerCode = codeAtLimit,
            CustomerName = nameAtLimit,
            CreditLimit = CustomerAuthorizationRules.MaxDecimalMagnitude,
            DepositRatio = -CustomerAuthorizationRules.MaxDecimalMagnitude,
            CommissionRatio = CustomerAuthorizationRules.MaxDecimalMagnitude,
            EmpId = 0,
            CreditDays = 0,
            Status = CustomerAuthorizationRules.DisabledStatus
        }));
        Assert.Equal(codeAtLimit, created.CustomerCode);
        Assert.Equal(nameAtLimit, created.CustomerName);
        Assert.Equal(CustomerAuthorizationRules.DisabledStatus, created.Status);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(new BaseCustomer
        {
            CustomerCode = codeAtLimit + "C",
            CustomerName = "超长编码",
            Status = 1
        }));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(new BaseCustomer
        {
            CustomerCode = "C-2",
            CustomerName = nameAtLimit + "N",
            Status = 1
        }));
        Assert.Equal(1, await db.BaseCustomers.AsNoTracking().CountAsync());
    }

    // ==================== 4. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：8 条路由全部先经实时授权再读写，且无匿名 / 角色回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureCustomerAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>授权口径复用既有「客户资料」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有客户菜单常量_不新增菜单()
    {
        Assert.Equal("customer", CustomerAuthorizationRules.RequiredMenuCode);
        Assert.Equal("客户资料", CustomerAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{CustomerAuthorizationRules.RequiredMenuCode}\", \"{CustomerAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

