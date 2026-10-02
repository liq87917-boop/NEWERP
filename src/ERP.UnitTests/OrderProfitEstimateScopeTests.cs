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
/// 订单利润暂估表（/api/reports/order-profit，ERP-219）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「订单利润暂估表」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤销售订单头（特权账号不过滤、受限制业务员仅其被分配客户、空客户对受限制账号不可见），
/// 且只统计已审核、未删除订单头与未删除明细；服务层强制要求数据范围，不提供无范围绕过。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class OrderProfitEstimateScopeTests
{
    private const string MenuCode = "order-profit";

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

    /// <summary>创建拥有「订单利润暂估表」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, DocumentStatus status,
        DateTime? orderDate = null, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(
        ErpDbContext db, long salesOrderId, long productId, string productName, decimal quantity, bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = productName,
            Quantity = quantity,
            Unit = "PCS",
            IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
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

    private static List<ReportDtos.OrderProfitItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.OrderProfitItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.OrderProfitItem>();
    }

    // ==================== 身份 / 数据库 / 菜单授权 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderProfit(Start, End));
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderProfit(Start, End));
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderProfit(Start, End));
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
        Assert.IsType<OkObjectResult>(await ctl.OrderProfit(Start, End));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderProfit(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 业务员数据范围 ====================

    [Fact]
    public async Task 受限制业务员_只看到被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedOrder(db, "SO-MINE", mine.Id, DocumentStatus.Approved);
        SeedOrder(db, "SO-OTHER", other.Id, DocumentStatus.Approved);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.OrderProfit(Start, End));

        var row = Assert.Single(items);
        Assert.Equal("SO-MINE", row.OrderNo);
        Assert.Equal("我的客户", row.CustomerName);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        SeedOrder(db, "SO-1", customer.Id, DocumentStatus.Approved);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.OrderProfit(Start, End));

        Assert.Empty(items);
    }

    [Fact]
    public async Task 服务层_受限范围_只返回范围内订单()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        SeedOrder(db, "SO-MINE", mine.Id, DocumentStatus.Approved);
        SeedOrder(db, "SO-OTHER", other.Id, DocumentStatus.Approved);

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 1,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };
        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, scope);

        var row = Assert.Single(result);
        Assert.Equal("SO-MINE", row.OrderNo);
    }

    [Fact]
    public async Task 服务层_空数据范围_拒绝_不提供无范围绕过()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GetOrderProfitEstimateAsync(Start, End, null!));
    }

    // ==================== 状态与软删除证据 ====================

    [Fact]
    public async Task 草稿已取消与软删除订单_不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        SeedOrder(db, "SO-DRAFT", customer.Id, DocumentStatus.Pending);
        SeedOrder(db, "SO-CANCEL", customer.Id, DocumentStatus.Cancelled);
        SeedOrder(db, "SO-DEL", customer.Id, DocumentStatus.Approved, deleted: true);
        SeedOrder(db, "SO-OK", customer.Id, DocumentStatus.Approved);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.OrderProfit(Start, End));

        var row = Assert.Single(items);
        Assert.Equal("SO-OK", row.OrderNo);
    }

    [Fact]
    public async Task 软删除明细_不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        db.BaseProducts.Add(new BaseProduct
        {
            ProductCode = "P001",
            ProductName = "热销商品",
            SalePrice = 100m,
            CostPrice = 60m,
            Status = 1
        });
        db.SaveChanges();

        var order = SeedOrder(db, "SO-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, order.Id, 1, "热销商品", 10m);
        SeedDetail(db, order.Id, 1, "热销商品", 999m, deleted: true);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.OrderProfit(Start, End));

        var row = Assert.Single(items);
        Assert.Equal(600m, row.CostAmount);   // 10 × 60；已删除的 999 数量不计入
    }

    // ==================== 只读 ====================

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, DocumentStatus.Approved);

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkList(await ctl.OrderProfit(Start, End));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
