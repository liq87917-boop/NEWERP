using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-112 动态销售订单报表预览（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、日期 / 客户 / 状态 / 币种筛选、稳定分页与 200 上限、
/// 无身份（未认证）、无销售订单菜单授权（权限不足）、越界业务员（数据范围 fail closed）、
/// 无效字段 / 无效日期区间 / 无效状态与币种 / 页大小超限、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesOrderReportTests
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
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending, Currency currency = Currency.USD)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = status,
            Currency = currency,
            TotalAmount = 100
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种一个「特权 + 销售订单菜单授权」用户（系统内置角色，不过滤客户）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        return user.Id;
    }

    /// <summary>播种一个「受限制业务员 + 销售订单菜单授权」用户（登录账号 == 员工编码），返回其客户 Id</summary>
    private static (long UserId, long MineCustomerId, long OtherCustomerId) SeedRestrictedUser(
        ErpDbContext db, string userName)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var emp = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"C-{userName}-mine", "我的客户", emp.Id);
        var other = SeedCustomer(db, $"C-{userName}-other", "别人的客户", emp.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static DynamicSalesOrderReportController NewController(IErpDbContext db)
        => new(new DynamicSalesOrderReportQuery(db));

    private static DynamicSalesOrderReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSalesOrderReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 字段目录 ====================

    [Fact]
    public async Task Catalog_返回有限白名单字段目录()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ok = Assert.IsType<OkObjectResult>(await ctl.Catalog());
        var resp = Assert.IsType<ApiResponse<DynamicSalesOrderReportCatalogDto>>(ok.Value);
        var catalog = resp.Data!;

        Assert.Equal(DynamicSalesOrderReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(31, catalog.Fields.Count);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet();
        Assert.Contains("orderNo", keys);
        Assert.Contains("orderDate", keys);
        Assert.Contains("customerId", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("status", keys);
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public async Task Catalog_无销售订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Catalog_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 2. 选定列 ====================

    [Fact]
    public async Task Preview_选中列_只返回选定字段并保持请求顺序()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "SO-1", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "totalAmount", "orderNo", "status" }
        }));

        Assert.Equal(new[] { "totalAmount", "orderNo", "status" }, page.Columns.Select(c => c.Key));
        Assert.Single(page.Rows);
        Assert.Equal(new[] { "totalAmount", "orderNo", "status" }, page.Rows[0].Keys.ToArray());
        Assert.Equal("SO-1", page.Rows[0]["orderNo"]);
        Assert.Equal("Pending", page.Rows[0]["status"]);
    }

    [Fact]
    public async Task Preview_未指定字段_默认返回全部白名单字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "SO-1", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest()));

        Assert.Equal(31, page.Columns.Count);
        Assert.Single(page.Rows);
        Assert.Equal(31, page.Rows[0].Count);
    }

    // ==================== 3. 筛选 ====================

    [Fact]
    public async Task Preview_按日期_客户_状态_币种过滤()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "A", 1L, new DateTime(2026, 1, 10), DocumentStatus.Approved, Currency.USD);
        SeedOrder(db, "B", 2L, new DateTime(2026, 2, 10), DocumentStatus.Pending, Currency.CNY);
        SeedOrder(db, "C", 1L, new DateTime(2026, 3, 10), DocumentStatus.Approved, Currency.EUR);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 3, 31),
            CustomerId = 1L,
            Status = "approved",
            Currency = "usd"
        }));

        Assert.Equal(1, page.Total);
        Assert.Single(page.Rows);
        Assert.Equal("A", page.Rows[0]["orderNo"]);
    }

    // ==================== 4. 分页 ====================

    [Fact]
    public async Task Preview_分页稳定且单页上限200()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        for (var i = 1; i <= 250; i++)
            SeedOrder(db, $"SO-{i}", 1L, new DateTime(2026, 1, 1).AddDays(i % 30));
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var p1 = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" }, Page = 1, PageSize = 200
        }));
        Assert.Equal(250, p1.Total);
        Assert.Equal(200, p1.Rows.Count);
        Assert.Equal(2, p1.TotalPages);

        var p2 = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" }, Page = 2, PageSize = 200
        }));
        Assert.Equal(50, p2.Rows.Count);
    }

    // ==================== 5. 无效输入（查询前拒绝） ====================

    [Fact]
    public async Task Preview_页大小超限或非法_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest { PageSize = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task Preview_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest { Fields = new() { "orderNo", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_日期区间无效_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest
            {
                StartDate = new DateTime(2026, 2, 1),
                EndDate = new DateTime(2026, 1, 1)
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效状态或币种_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest { Status = "Bogus" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest { Currency = "BOGUS" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    // ==================== 6. 未授权 / 越界 ====================

    [Fact]
    public async Task Preview_无销售订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Preview_越界业务员_只返回自己客户_越界筛选返回空()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedUser(db, "alice");
        SeedOrder(db, "SO-MINE", mine);
        SeedOrder(db, "SO-OTHER", other);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" }, PageSize = 100
        }));
        Assert.Equal(1, page.Total);
        Assert.Equal("SO-MINE", page.Rows[0]["orderNo"]);

        var outOfScope = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" }, CustomerId = other, PageSize = 100
        }));
        Assert.Equal(0, outOfScope.Total);
        Assert.Empty(outOfScope.Rows);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Preview_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "SO-R", 1L);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSalesOrderReportController(new DynamicSalesOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" }, PageSize = 10
        }));
        Assert.Equal(1, page.Total);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 8. 只读计数上下文（断言不写库） ====================

    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int WriteCalls { get; private set; }

        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}



