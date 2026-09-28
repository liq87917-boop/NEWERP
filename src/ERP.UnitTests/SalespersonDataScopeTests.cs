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
/// ERP-097 业务员数据范围（客户 / 询价 / 报价 / PI / 销售订单 / 销售出库 / 客户销项发票）单元测试。
/// 覆盖：特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤、受限制业务员按「登录账号 == 员工编码」映射到
/// 其被分配客户、未映射业务员时 fail closed（空范围）、缺失身份未认证拒绝、查询过滤与详情越界 fail closed、
/// 以及控制器层（客户资料 / 销售订单）的列表过滤与详情 fail closed。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class SalespersonDataScopeTests
{
    // ==================== 0. 测试脚手架 ====================

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = isSalesman, Status = 1 };
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
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder { OrderNo = orderNo, CustomerId = customerId, OrderDate = DateTime.Today };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, long? customerId)
    {
        var quotation = new Quotation { QuotationNo = no, CustomerId = customerId, CustomerName = no };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    // ==================== 1. 特权判定 ====================

    [Fact]
    public async Task ResolveAsync_超级管理员_不过滤()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "admin");
        var role = SeedRole(db, SalespersonDataScopeService.SuperAdminRoleCode);
        SeedUserRole(db, user.Id, role.Id);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, user.Id);

        Assert.True(scope.IsPrivileged);
        Assert.Null(scope.AllowedCustomerIds);
    }

    [Fact]
    public async Task ResolveAsync_系统内置角色_不过滤()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "sysop");
        var role = SeedRole(db, "SysOperator", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, user.Id);

        Assert.True(scope.IsPrivileged);
        Assert.Null(scope.AllowedCustomerIds);
    }

    [Fact]
    public async Task ResolveAsync_显式特权角色_不过滤()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "mgr");
        var role = SeedRole(db, "SalesManager");
        SeedUserRole(db, user.Id, role.Id);
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = SalespersonDataScopeService.PrivilegedRolesParameterKey,
            ParamValue = "SalesManager, FinanceManager",
            ParamName = "数据范围特权角色"
        });
        db.SaveChanges();

        var scope = await SalespersonDataScopeService.ResolveAsync(db, user.Id);

        Assert.True(scope.IsPrivileged);
        Assert.Null(scope.AllowedCustomerIds);
    }

    // ==================== 2. 业务员映射与可见客户 ====================

    [Fact]
    public async Task ResolveAsync_受限制业务员_只解析到其被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);

        var alice = SeedEmployee(db, "alice", isSalesman: true);
        var bob = SeedEmployee(db, "bob", isSalesman: true);

        var mine1 = SeedCustomer(db, "C001", "我的客户一", alice.Id);
        var mine2 = SeedCustomer(db, "C002", "我的客户二", alice.Id);
        var others = SeedCustomer(db, "C003", "别人的客户", bob.Id);
        var unassigned = SeedCustomer(db, "C004", "未分配客户", null);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, user.Id);

        Assert.False(scope.IsPrivileged);
        Assert.Equal(alice.Id, scope.SalesmanId);
        Assert.NotNull(scope.AllowedCustomerIds);
        Assert.Equal(2, scope.AllowedCustomerIds!.Count);
        Assert.Contains(mine1.Id, scope.AllowedCustomerIds);
        Assert.Contains(mine2.Id, scope.AllowedCustomerIds);
        Assert.DoesNotContain(others.Id, scope.AllowedCustomerIds);
        Assert.DoesNotContain(unassigned.Id, scope.AllowedCustomerIds);
    }

    [Fact]
    public async Task ResolveAsync_非业务员员工不映射_空范围()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "carol");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedEmployee(db, "carol", isSalesman: false);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, user.Id);

        Assert.False(scope.IsPrivileged);
        Assert.Null(scope.SalesmanId);
        Assert.NotNull(scope.AllowedCustomerIds);
        Assert.Empty(scope.AllowedCustomerIds);
    }

    [Fact]
    public async Task ResolveAsync_无员工映射_fail_closed空范围()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "ghost");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, user.Id);

        Assert.False(scope.IsPrivileged);
        Assert.Null(scope.SalesmanId);
        Assert.NotNull(scope.AllowedCustomerIds);
        Assert.Empty(scope.AllowedCustomerIds);
    }

    [Fact]
    public async Task ResolveAsync_缺失身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SalespersonDataScopeService.ResolveAsync(db, null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 3. 查询过滤与越界判定 ====================

    [Fact]
    public async Task FilterByCustomer_受限制业务员_只返回其客户订单()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var others = SeedCustomer(db, "C002", "别人的客户");
        var myOrder = SeedOrder(db, "SO-MY", mine.Id);
        SeedOrder(db, "SO-OTHER", others.Id);

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };

        var filtered = await SalespersonDataScopeService
            .FilterByCustomer(db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId)
            .ToListAsync();

        Assert.Single(filtered);
        Assert.Equal(myOrder.Id, filtered[0].Id);
    }

    [Fact]
    public async Task FilterByCustomer_特权账号_不过滤()
    {
        using var db = TestDbFactory.Create();
        var c1 = SeedCustomer(db, "C001", "一");
        var c2 = SeedCustomer(db, "C002", "二");
        SeedOrder(db, "SO-1", c1.Id);
        SeedOrder(db, "SO-2", c2.Id);

        var scope = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };

        var result = await SalespersonDataScopeService
            .FilterByCustomer(db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId)
            .ToListAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task FilterByCustomer_可空客户报价单_空客户被排除()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        SeedQuotation(db, "QT-MY", mine.Id);
        SeedQuotation(db, "QT-NULL", null);

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };

        var result = await SalespersonDataScopeService
            .FilterByCustomer(db.Quotations.AsNoTracking().Where(q => !q.IsDeleted), scope, q => q.CustomerId)
            .ToListAsync();

        Assert.Single(result);
        Assert.Equal("QT-MY", result[0].QuotationNo);
    }

    [Fact]
    public void AllowsCustomer_特权恒true_受限制仅允许已分配客户()
    {
        var privileged = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };
        Assert.True(privileged.AllowsCustomer(123L));
        Assert.True(privileged.AllowsCustomer(null));

        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false,
            AllowedCustomerIds = new HashSet<long> { 1L, 2L }
        };
        Assert.True(restricted.AllowsCustomer(1L));
        Assert.False(restricted.AllowsCustomer(3L));
        Assert.False(restricted.AllowsCustomer(null));
    }

    // ==================== 4. 控制器层：列表过滤与详情 fail closed ====================

    [Fact]
    public async Task CustomerController_GetPaged_受限制业务员_只返回其客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", alice.Id);
        SeedCustomer(db, "C002", "别人的客户", alice.Id + 1000);

        var ctl = new CustomerController(new GenericService<BaseCustomer>(db), db);
        SetUser(ctl, user.Id);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 100 }));
        var resp = Assert.IsType<ApiResponse<PagedResult<BaseCustomer>>>(ok.Value);

        Assert.Single(resp.Data!.Items);
        Assert.Equal(mine.Id, resp.Data.Items[0].Id);
        Assert.Equal(1, resp.Data.Total);
    }

    [Fact]
    public async Task CustomerController_GetById_越界客户_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        var alice = SeedEmployee(db, "alice");
        SeedCustomer(db, "C001", "我的客户", alice.Id);
        var others = SeedCustomer(db, "C002", "别人的客户", alice.Id + 1000);

        var ctl = new CustomerController(new GenericService<BaseCustomer>(db), db);
        SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(others.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task SalesOrderController_GetById_越界订单_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", alice.Id);
        var others = SeedCustomer(db, "C002", "别人的客户", alice.Id + 1000);
        SeedOrder(db, "SO-MY", mine.Id);
        var otherOrder = SeedOrder(db, "SO-OTHER", others.Id);

        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(otherOrder.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }
}
