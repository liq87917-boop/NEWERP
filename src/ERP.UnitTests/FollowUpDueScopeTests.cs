using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 跟进提醒报表（/api/reports/follow-up-due，ERP-192）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「跟进提醒」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤 CustomerFollowUps（特权账号不过滤、受限制业务员仅其被分配客户、空客户对受限制账号不可见），
/// 校验 aheadDays（0~365）、保留既有响应形状与到期排序，且全程只读。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class FollowUpDueScopeTests
{
    private const string FollowUpDueMenuCode = "follow-up-due";

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
        ErpDbContext db, string followNo, long? customerId, string customerName,
        DateTime? nextFollowDate, string subject = "跟进主题", string salesmanName = "张三")
    {
        var follow = new CustomerFollowUp
        {
            FollowNo = followNo,
            FollowDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            Subject = subject,
            SalesmanName = salesmanName,
            Result = "待跟进",
            NextFollowDate = nextFollowDate,
            IsDeleted = false
        };
        db.CustomerFollowUps.Add(follow);
        db.SaveChanges();
        return follow;
    }

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    /// <summary>创建拥有「跟进提醒」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, FollowUpDueMenuCode).Id);
        return user;
    }

    private static ReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new ReportController(new ReportService(db), db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return ctl;
    }

    private static List<ReportDtos.FollowUpDueItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.FollowUpDueItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.FollowUpDueItem>();
    }

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权被回收_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Role");
        var user = SeedUser(db, "revoke-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, FollowUpDueMenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 受限制业务员_只看到被分配客户_他人与空客户不可见()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", new DateTime(2026, 9, 10), subject: "我的跟进");
        SeedFollowUp(db, "FU-OTHER", other.Id, "别人的客户", new DateTime(2026, 9, 10), subject: "他人跟进");
        SeedFollowUp(db, "FU-NULL", null, "匿名客户", new DateTime(2026, 9, 10), subject: "匿名跟进");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));

        var row = Assert.Single(items);
        Assert.Equal("我的客户", row.CustomerName);
        Assert.Equal("我的跟进", row.Subject);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        SeedFollowUp(db, "FU-1", customer.Id, "有客户", new DateTime(2026, 9, 10));

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));

        Assert.Empty(items);
    }

    [Fact]
    public async Task 重新分配客户_下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var otherEmployee = SeedEmployee(db, "bob");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);
        SeedFollowUp(db, "FU-1", customer.Id, "我的客户", new DateTime(2026, 9, 10));

        var ctl = BuildController(db, user.Id);
        Assert.Single(OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7)));

        customer.EmpId = otherEmployee.Id;   // 重新分配给其他业务员
        db.SaveChanges();

        Assert.Empty(OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7)));
    }

    [Fact]
    public async Task 空客户_受限制账号不可见()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);

        SeedFollowUp(db, "FU-NULL", null, "匿名客户", new DateTime(2026, 9, 10), subject: "匿名跟进");
        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", new DateTime(2026, 9, 10), subject: "我的跟进");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));

        var row = Assert.Single(items);
        Assert.Equal("我的客户", row.CustomerName);
        Assert.DoesNotContain(items, i => i.CustomerName == "匿名客户");
    }

    [Fact]
    public async Task 特权账号_保留全部可见_包括空客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedFollowUp(db, "FU-1", c1.Id, "客户一", new DateTime(2026, 9, 10));
        SeedFollowUp(db, "FU-2", c2.Id, "客户二", new DateTime(2026, 9, 10));
        SeedFollowUp(db, "FU-NULL", null, "匿名客户", new DateTime(2026, 9, 10));

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.CustomerName == "匿名客户");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task aheadDays越界_拒绝(int aheadDays)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.FollowUpDue(new DateTime(2026, 9, 15), aheadDays));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(365)]
    public async Task aheadDays边界_0与365_正常通过(int aheadDays)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        SeedFollowUp(db, "FU-1", mine.Id, "我的客户", new DateTime(2026, 9, 10));

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.FollowUpDue(new DateTime(2026, 9, 15), aheadDays));
    }

    [Fact]
    public async Task 日期边界_恰好在截止日计入_超过截止日不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");

        var asOf = new DateTime(2026, 9, 15);
        SeedFollowUp(db, "FU-LIMIT", c.Id, "客户", new DateTime(2026, 9, 22));  // asOf + 7 天，恰好计入
        SeedFollowUp(db, "FU-OVER", c.Id, "客户", new DateTime(2026, 9, 23));   // 超过一天，不计入
        SeedFollowUp(db, "FU-TODAY", c.Id, "客户", new DateTime(2026, 9, 15));  // 今日到期
        SeedFollowUp(db, "FU-OLD", c.Id, "客户", new DateTime(2026, 9, 10));    // 已逾期

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.FollowUpDue(asOf, 7));

        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, i => i.NextFollowDate == new DateTime(2026, 9, 23));
        Assert.Contains(items, i => i.NextFollowDate == new DateTime(2026, 9, 22));
    }

    [Fact]
    public async Task 稳定排序_按逾期天数降序_同天数按客户名升序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");

        var asOf = new DateTime(2026, 9, 15);
        SeedFollowUp(db, "FU-1", c.Id, "客户C", new DateTime(2026, 9, 10), subject: "s5");   // 逾期 5 天
        SeedFollowUp(db, "FU-2", c.Id, "客户A", new DateTime(2026, 9, 13), subject: "s2A");  // 逾期 2 天，客户名更小
        SeedFollowUp(db, "FU-3", c.Id, "客户B", new DateTime(2026, 9, 13), subject: "s2B");  // 逾期 2 天，客户名更大
        SeedFollowUp(db, "FU-4", c.Id, "客户D", new DateTime(2026, 9, 15), subject: "s0");   // 今日到期

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.FollowUpDue(asOf, 7));

        Assert.Equal(new[] { "s5", "s2A", "s2B", "s0" }, items.Select(i => i.Subject).ToArray());
    }

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", new DateTime(2026, 9, 10));

        var before = db.CustomerFollowUps.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkList(await ctl.FollowUpDue(new DateTime(2026, 9, 15), 7));

        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
