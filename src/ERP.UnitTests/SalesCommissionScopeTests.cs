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
/// 业务员提成表（/api/reports/sales-commission，ERP-243）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「业务员提成表」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤销售订单头（特权账号不过滤、受限制业务员仅其被分配客户、空客户对受限制账号不可见），
/// 且只统计已审核、未删除订单头；服务层强制要求数据范围，不提供无范围绕过。
/// 本表口径为「已审核销售订单证据」，非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class SalesCommissionScopeTests
{
    private const string MenuCode = "sales-commission";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId = null,
        Currency currency = Currency.USD, decimal totalAmount = 100m,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
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

    private static List<ReportDtos.SalesCommissionItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.SalesCommissionItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.SalesCommissionItem>();
    }

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesCommission(Start, End));
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesCommission(Start, End));
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesCommission(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权被回收_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoke-user", "Sales", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id);

        var ctl = BuildController(db, user.Id);
        _ = OkList(await ctl.SalesCommission(Start, End));

        db.SysRoleMenus.Single().IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.SalesCommission(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 受限制业务员_只覆盖被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedOrder(db, "SO-MINE", mine.Id, employee.Id);
        SeedOrder(db, "SO-OTHER", other.Id, employee.Id);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.SalesCommission(Start, End));

        var row = Assert.Single(items);
        Assert.Equal(employee.Id, row.SalesmanId);
        Assert.Equal(1, row.OrderCount);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        SeedOrder(db, "SO-1", customer.Id);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.SalesCommission(Start, End));

        Assert.Empty(items);
    }

    [Fact]
    public async Task 服务层_受限范围_只返回范围内订单()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        var emp1 = SeedEmployee(db, "S001");
        var emp2 = SeedEmployee(db, "S002");

        SeedOrder(db, "SO-MINE", mine.Id, emp1.Id, totalAmount: 100m);
        SeedOrder(db, "SO-OTHER", other.Id, emp2.Id, totalAmount: 200m);

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = emp1.Id,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };
        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, scope);

        var row = Assert.Single(result);
        Assert.Equal(emp1.Id, row.SalesmanId);
        Assert.Equal(100m, row.SalesAmount);
    }

    [Fact]
    public async Task 服务层_空数据范围_拒绝_不提供无范围绕过()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GetSalesCommissionAsync(Start, End, null!));
    }

    [Fact]
    public async Task 草稿取消删除订单不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001");

        SeedOrder(db, "SO-DRAFT", customer.Id, emp.Id, status: DocumentStatus.Pending);
        SeedOrder(db, "SO-CANCEL", customer.Id, emp.Id, status: DocumentStatus.Cancelled);
        SeedOrder(db, "SO-DEL", customer.Id, emp.Id, deleted: true);
        SeedOrder(db, "SO-OK", customer.Id, emp.Id, totalAmount: 100m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.SalesCommission(Start, End));

        var row = Assert.Single(items);
        Assert.Equal(1, row.OrderCount);
        Assert.Equal(100m, row.SalesAmount);
    }

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id);

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkList(await ctl.SalesCommission(Start, End));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
