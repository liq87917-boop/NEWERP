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
/// 业务员产值报表（/api/reports/salesman-output，ERP-235）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「业务员产值报表」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤销售订单头（特权账号不过滤、受限制业务员仅其被分配客户、未映射业务员看不到任何数据），
/// 且只统计已审核、未删除、已分配业务员的销售订单；未分配业务员的订单保持既有排除口径。
/// 业务员身份仅作为订单属性参与分组，绝不扩展为更宽泛的权限边界。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class SalesmanOutputScopeTests
{
    private const string MenuCode = "salesman-output";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
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

    /// <summary>创建拥有「业务员产值报表」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, DocumentStatus status,
        DateTime? orderDate = null, bool deleted = false, decimal totalAmount = 0m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Status = status,
            TotalAmount = totalAmount,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
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

    private static List<ReportDtos.SalesmanOutputItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.SalesmanOutputItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.SalesmanOutputItem>();
    }

    // ==================== 身份 / 数据库 / 菜单授权 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesmanOutput(Start, End));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 无数据库上下文_内部错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = new ReportController(new ReportService(db), null);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "Test"))
            }
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesmanOutput(Start, End));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesmanOutput(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权被回收_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Role");
        var user = SeedUser(db, "revoke-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, MenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.SalesmanOutput(Start, End));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesmanOutput(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 业务员数据范围 ====================

    [Fact]
    public async Task 受限制业务员_只看到被分配客户的已分配业务员订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice", "业务员甲");
        var otherEmp = SeedEmployee(db, "bob", "业务员乙");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmp.Id);

        SeedOrder(db, "SO-MINE", mine.Id, employee.Id, DocumentStatus.Approved, totalAmount: 1000m);
        SeedOrder(db, "SO-OTHER", other.Id, otherEmp.Id, DocumentStatus.Approved, totalAmount: 9000m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.SalesmanOutput(Start, End));

        var row = Assert.Single(items);
        Assert.Equal(employee.Id, row.SalesmanId);
        Assert.Equal("业务员甲", row.SalesmanName);
        Assert.Equal(1, row.OrderCount);
        Assert.Equal(1000m, row.TotalAmount);
        Assert.DoesNotContain(items, x => x.SalesmanName == "业务员乙");
    }

    [Fact]
    public async Task 未映射业务员_看不到任何数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "ghost", "Sales");
        var employee = SeedEmployee(db, "someone-else", "其他业务员");
        var customer = SeedCustomer(db, "C001", "客户", employee.Id);
        SeedOrder(db, "SO-1", customer.Id, employee.Id, DocumentStatus.Approved, totalAmount: 1000m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.SalesmanOutput(Start, End));

        Assert.Empty(items);
    }

    // ==================== 状态 / 删除 / 未分配业务员 ====================

    [Fact]
    public async Task 仅已审核未删除且已分配业务员的订单计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-OK", customer.Id, emp.Id, DocumentStatus.Approved, totalAmount: 1000m);
        SeedOrder(db, "SO-DEL", customer.Id, emp.Id, DocumentStatus.Approved, deleted: true, totalAmount: 2000m);
        SeedOrder(db, "SO-PENDING", customer.Id, emp.Id, DocumentStatus.Pending, totalAmount: 3000m);
        SeedOrder(db, "SO-NO-SALESMAN", customer.Id, null, DocumentStatus.Approved, totalAmount: 4000m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.SalesmanOutput(Start, End));

        var row = Assert.Single(items);
        Assert.Equal(1000m, row.TotalAmount);
        Assert.Equal(1, row.OrderCount);
        Assert.Equal("业务员甲", row.SalesmanName);
    }
}
