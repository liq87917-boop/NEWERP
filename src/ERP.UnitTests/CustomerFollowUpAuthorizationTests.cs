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
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户跟进记录（<c>api/crm/follow-ups</c>，客户跟进记录页面维护）实时授权、ERP-097 业务员数据范围与
/// 有界字段校验单元测试（ERP-458）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「客户跟进记录」菜单 / 无菜单的身份在
/// <b>全部 7 条路由</b>（分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除）fail closed，
/// 且 <c>CustomerFollowUps</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「客户跟进记录」菜单的授权身份下，既有读 / 写契约与分页 / 响应契约保持；</item>
/// <item><b>ERP-097 数据范围</b>：授权身份下的受限业务员仍只看到自己被分配客户的跟进记录，
/// 越界读取按「不存在」fail closed，写入的客户必须落在范围内；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段校验</b>：跟进编号空值 / 文本长度越界 / 客户与跟进人引用非法一律按受控参数错误拒绝且零写入；</item>
/// <item><b>源码契约</b>：控制器 7 条路由全部先授权再读写，且只复用既有 <c>customer-follow</c> 菜单，
/// 不按请求路径 / 环境 / 空请求降级（无 <c>AllowAnonymous</c> / 角色回退）。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class CustomerFollowUpAuthorizationTests
{
    private const string RequiredMenuCode = "customer-follow";

    // ==================== 0. 测试脚手架 ====================

    private static CustomerFollowUpController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<CustomerFollowUp>(db), db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>
    /// 真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：null = 无身份。
    /// 控制器<b>不</b>按请求路径降级，因此缺失身份一律实时授权并 fail closed。
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
        http.Request.Path = "/api/crm/follow-ups";
        return new ControllerContext { HttpContext = http };
    }

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

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 跟进菜单 / 特权角色），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"follow-auth-{Guid.NewGuid():N}",
            DisplayName = "跟进授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "跟进授权用例角色",
            RoleCode = $"FollowAuthCase-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, RequiredMenuCode, CustomerFollowUpAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>播种受限业务员（登录账号 == 员工编码，非特权角色），返回账号 / 员工</summary>
    private static (long UserId, BaseEmployee Employee) SeedRestrictedSalesman(ErpDbContext db, bool grantMenu = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"sales-{Guid.NewGuid():N}",
            EmployeeName = "受限业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            DisplayName = "受限业务员账号",
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

        if (grantMenu)
            GrantMenu(db, role.Id, RequiredMenuCode, CustomerFollowUpAuthorizationRules.RequiredMenuText);
        return (user.Id, employee);
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static CustomerFollowUp SeedFollowUp(
        ErpDbContext db, string followNo, long? customerId, string customerName = "客户", string subject = "跟进主题")
    {
        var follow = new CustomerFollowUp
        {
            FollowNo = followNo,
            FollowDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            FollowType = "电话",
            Subject = subject,
            Result = "待跟进",
            IsDeleted = false
        };
        db.CustomerFollowUps.Add(follow);
        db.SaveChanges();
        return follow;
    }

    private static CustomerFollowUp NewFollowUp(
        string followNo = "FU-NEW", long? customerId = null, string customerName = "客户",
        string followType = "电话", string contactPerson = "张三", long? salesmanId = null,
        string salesmanName = "张三", string subject = "跟进主题", string content = "跟进内容",
        string result = "待跟进", string remark = "")
        => new()
        {
            FollowNo = followNo,
            FollowDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            FollowType = followType,
            ContactPerson = contactPerson,
            SalesmanId = salesmanId,
            SalesmanName = salesmanName,
            Subject = subject,
            Content = content,
            Result = result,
            Remark = remark
        };

    /// <summary>跟进记录快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.CustomerFollowUps.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.IsDeleted}:{x.FollowNo}:{x.CustomerId}:{x.CustomerName}:{x.Subject}:" +
                         $"{x.Content}:{x.Remark}:{x.SalesmanId}")
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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有跟进菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「客户跟进记录」菜单 / 无任何菜单的身份：全部 7 条路由
    /// 在读取或写入任何跟进记录之前 fail closed，且跟进记录快照逐字节不变。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何跟进记录(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var follow = SeedFollowUp(db, "FU-1", customer.Id);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetAll());
        await AssertCode(expectedCode, () => ctl.GetById(follow.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewFollowUp("FU-DENIED", customer.Id)));
        await AssertCode(expectedCode, () => ctl.Update(follow.Id, NewFollowUp("FU-1", customer.Id, customerName: "被拒改名")));
        await AssertCode(expectedCode, () => ctl.Delete(follow.Id));
        await AssertCode(expectedCode, () => ctl.BatchDelete(new List<long> { follow.Id }));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.CustomerFollowUps.AsNoTracking().SingleAsync(x => x.Id == follow.Id);
        Assert.Equal("FU-1", stored.FollowNo);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Auth_授权身份_既有读写契约放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var employee = SeedEmployee(db, "emp-1");
        var customer = SeedCustomer(db, "C001", "客户", employee.Id);
        var ctl = Controller(db, userId);

        var created = Data<CustomerFollowUp>(await ctl.Create(NewFollowUp("FU-OK", customer.Id, salesmanId: employee.Id)));
        Assert.True(created.Id > 0);

        var page = Data<PagedResult<CustomerFollowUp>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, f => f.Id == created.Id && f.FollowNo == "FU-OK");
        Assert.Contains(Data<List<CustomerFollowUp>>(await ctl.GetAll()), f => f.Id == created.Id);
        Assert.Equal("FU-OK", Data<CustomerFollowUp>(await ctl.GetById(created.Id)).FollowNo);

        var updated = Data<CustomerFollowUp>(
            await ctl.Update(created.Id, NewFollowUp("FU-OK", customer.Id, customerName: "改名客户")));
        Assert.Equal("改名客户", updated.CustomerName);

        var second = Data<CustomerFollowUp>(await ctl.Create(NewFollowUp("FU-OK2", customer.Id)));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.CustomerFollowUps.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.CustomerFollowUps.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    // ==================== 3. ERP-097 受限业务员读取 / 写入范围 ====================

    /// <summary>
    /// 授权身份下的受限业务员（登录账号 == 员工编码）沿用 ERP-097 数据范围：分页 / 全部只返回自己被分配客户的
    /// 跟进记录，越界读取按「不存在」fail closed；新增 / 修改必须写入范围内客户，范围外请求拒绝且零写入。
    /// </summary>
    [Fact]
    public async Task Auth_授权身份_保留ERP097受限业务员范围()
    {
        using var db = TestDbFactory.Create();
        var (userId, employee) = SeedRestrictedSalesman(db);
        var mine = SeedCustomer(db, "C-MINE", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C-OTHER", "别人的客户", employee.Id + 1000);
        var mineFollow = SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户");
        var otherFollow = SeedFollowUp(db, "FU-OTHER", other.Id, "别人的客户");
        SeedFollowUp(db, "FU-NULL", null, "匿名客户");

        var ctl = Controller(db, userId);

        var page = Data<PagedResult<CustomerFollowUp>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Single(page.Items);
        Assert.Equal(mineFollow.Id, page.Items[0].Id);
        Assert.Equal(1, page.Total);

        var all = Data<List<CustomerFollowUp>>(await ctl.GetAll());
        Assert.Single(all);
        Assert.Equal(mineFollow.Id, all[0].Id);

        Assert.Equal(mineFollow.Id, Data<CustomerFollowUp>(await ctl.GetById(mineFollow.Id)).Id);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(otherFollow.Id));

        var created = Data<CustomerFollowUp>(await ctl.Create(NewFollowUp("FU-MINE-NEW", mine.Id, "我的客户")));
        Assert.True(created.Id > 0);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewFollowUp("FU-OTHER-NEW", other.Id, "别人的客户")));
        await AssertCode(ErrorCodes.Forbidden,
            () => ctl.Update(mineFollow.Id, NewFollowUp("FU-MINE", other.Id, "别人的客户")));

        var after = Snapshot(db);
        Assert.Contains("FU-MINE-NEW", after);
        Assert.DoesNotContain("FU-OTHER-NEW", after);
        Assert.Equal(1, await db.CustomerFollowUps.AsNoTracking().CountAsync(f => f.CustomerId == other.Id));
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var follow = SeedFollowUp(db, "FU-1", customer.Id);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<CustomerFollowUp>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewFollowUp("FU-REVOKED", customer.Id)));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(follow.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { follow.Id }));

        Assert.False((await db.CustomerFollowUps.AsNoTracking().SingleAsync(x => x.Id == follow.Id)).IsDeleted);
        Assert.Equal(1, await db.CustomerFollowUps.AsNoTracking().CountAsync());
    }

    // ==================== 4. 有界字段校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>
    /// 授权身份下的非法载荷（跟进编号空值 / 文本长度越界 / 客户与跟进人引用非法）：新增与修改都按受控参数错误拒绝，
    /// 且不落任何新行、不改写任何既有行。
    /// </summary>
    [Theory]
    [InlineData("empty-followno")]
    [InlineData("followno-too-long")]
    [InlineData("followtype-too-long")]
    [InlineData("contact-too-long")]
    [InlineData("salesmanname-too-long")]
    [InlineData("subject-too-long")]
    [InlineData("customername-too-long")]
    [InlineData("content-too-long")]
    [InlineData("result-too-long")]
    [InlineData("remark-too-long")]
    [InlineData("customer-missing")]
    [InlineData("customer-unknown")]
    [InlineData("salesman-unknown")]
    public async Task Validation_非法载荷_新增与修改均拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var existing = SeedFollowUp(db, "FU-EXIST", customer.Id, "既有客户");
        var ctl = Controller(db, userId);

        CustomerFollowUp Payload() => scenario switch
        {
            "empty-followno" => NewFollowUp("", customer.Id),
            "followno-too-long" => NewFollowUp(
                new string('F', CustomerFollowUpAuthorizationRules.MaxFollowNoLength + 1), customer.Id),
            "followtype-too-long" => NewFollowUp("FU-1", customer.Id,
                followType: new string('T', CustomerFollowUpAuthorizationRules.MaxFollowTypeLength + 1)),
            "contact-too-long" => NewFollowUp("FU-1", customer.Id,
                contactPerson: new string('P', CustomerFollowUpAuthorizationRules.MaxContactPersonLength + 1)),
            "salesmanname-too-long" => NewFollowUp("FU-1", customer.Id,
                salesmanName: new string('S', CustomerFollowUpAuthorizationRules.MaxSalesmanNameLength + 1)),
            "subject-too-long" => NewFollowUp("FU-1", customer.Id,
                subject: new string('S', CustomerFollowUpAuthorizationRules.MaxSubjectLength + 1)),
            "customername-too-long" => NewFollowUp("FU-1", customer.Id,
                customerName: new string('N', CustomerFollowUpAuthorizationRules.MaxCustomerNameLength + 1)),
            "content-too-long" => NewFollowUp("FU-1", customer.Id,
                content: new string('C', CustomerFollowUpAuthorizationRules.MaxContentLength + 1)),
            "result-too-long" => NewFollowUp("FU-1", customer.Id,
                result: new string('R', CustomerFollowUpAuthorizationRules.MaxResultLength + 1)),
            "remark-too-long" => NewFollowUp("FU-1", customer.Id,
                remark: new string('R', CustomerFollowUpAuthorizationRules.MaxRemarkLength + 1)),
            "customer-missing" => NewFollowUp("FU-1", null),
            "customer-unknown" => NewFollowUp("FU-1", 999999),
            _ => NewFollowUp("FU-1", customer.Id, salesmanId: 999999)
        };

        var before = Snapshot(db);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.CustomerFollowUps.AsNoTracking().CountAsync());

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, Payload()));
        Assert.Equal(before, Snapshot(db));
        var stored = await db.CustomerFollowUps.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("FU-EXIST", stored.FollowNo);
    }

    /// <summary>边界值必须放行：各文本字段恰好等于持久化长度上限时接受，超一位即拒绝。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var employee = SeedEmployee(db, "emp-1");
        var ctl = Controller(db, userId);

        var followNoAtLimit = new string('F', CustomerFollowUpAuthorizationRules.MaxFollowNoLength);
        var created = Data<CustomerFollowUp>(await ctl.Create(NewFollowUp(
            followNoAtLimit, customer.Id,
            customerName: new string('N', CustomerFollowUpAuthorizationRules.MaxCustomerNameLength),
            followType: new string('T', CustomerFollowUpAuthorizationRules.MaxFollowTypeLength),
            contactPerson: new string('P', CustomerFollowUpAuthorizationRules.MaxContactPersonLength),
            salesmanId: employee.Id,
            salesmanName: new string('S', CustomerFollowUpAuthorizationRules.MaxSalesmanNameLength),
            subject: new string('S', CustomerFollowUpAuthorizationRules.MaxSubjectLength),
            content: new string('C', CustomerFollowUpAuthorizationRules.MaxContentLength),
            result: new string('R', CustomerFollowUpAuthorizationRules.MaxResultLength),
            remark: new string('R', CustomerFollowUpAuthorizationRules.MaxRemarkLength))));
        Assert.Equal(followNoAtLimit, created.FollowNo);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewFollowUp(followNoAtLimit + "F", customer.Id)));
        Assert.Equal(1, await db.CustomerFollowUps.AsNoTracking().CountAsync());
    }

    // ==================== 5. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：7 条路由全部先经实时授权再读写，且无匿名 / 角色 / 请求路径 / 环境回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "CustomerFollowUpController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(7, Regex.Matches(source, @"await EnsureAuthorizedScopeAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
        Assert.DoesNotContain("Request.Path", source);
        Assert.DoesNotContain("Environment.", source);
    }

    /// <summary>授权口径复用既有「客户跟进记录」菜单（与 SchemaUpgrader 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有跟进菜单常量_不新增菜单()
    {
        Assert.Equal("customer-follow", CustomerFollowUpAuthorizationRules.RequiredMenuCode);
        Assert.Equal("客户跟进记录", CustomerFollowUpAuthorizationRules.RequiredMenuText);

        var upgrader = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.Contains("N'customer-follow'", upgrader);
        Assert.Contains("N'客户跟进记录'", upgrader);
    }

}
