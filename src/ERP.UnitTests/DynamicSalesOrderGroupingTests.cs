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
/// ERP-114 动态销售订单报表「货币安全分组与小计」单元测试。
/// <para>页面小计语义：<see cref="DynamicSalesOrderReportRules.BuildGroupSubtotals"/> 只对「当前预览页」的已授权行聚合，
/// 分组内按币种分开统计条数与金额（金额只对同币种求和、绝不跨币种相加），并非全量合计。</para>
/// 覆盖：混合币种不合并、月份边界、范围受限行、空页、无效分组键（fail closed）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesOrderGroupingTests
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
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending,
        Currency currency = Currency.USD, decimal totalAmount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = status,
            Currency = currency,
            TotalAmount = totalAmount
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

    // ==================== 1. 分组键规范化（fail closed） ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("customer", "customer")]
    [InlineData("CUSTOMER", "customer")]
    [InlineData("month", "month")]
    [InlineData("Month", "month")]
    public void NormalizeGroupBy_仅接受none_customer_month(string? input, string expected)
    {
        Assert.Equal(expected, DynamicSalesOrderReportRules.NormalizeGroupBy(input));
    }

    [Fact]
    public void NormalizeGroupBy_无效分组键_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicSalesOrderReportRules.NormalizeGroupBy("bogus"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 按客户分组：混合币种绝不合并 ====================

    [Fact]
    public async Task GroupBy_按客户分组_混合币种_币种分开不合并()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        var c2 = SeedCustomer(db, "C2", "客户二").Id;

        SeedOrder(db, "SO-1", c1, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 100m);
        SeedOrder(db, "SO-2", c1, new DateTime(2026, 9, 2), DocumentStatus.Pending, Currency.USD, 50m);
        SeedOrder(db, "SO-3", c1, new DateTime(2026, 9, 3), DocumentStatus.Pending, Currency.CNY, 200m);
        SeedOrder(db, "SO-4", c2, new DateTime(2026, 9, 4), DocumentStatus.Pending, Currency.USD, 300m);
        SeedOrder(db, "SO-5", c2, new DateTime(2026, 9, 5), DocumentStatus.Pending, Currency.EUR, 40m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            GroupBy = "customer",
            PageSize = 100
        }));

        Assert.Equal("customer", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);

        // 组序确定性：客户 Id 升序
        Assert.Equal($"customer:{c1}", page.Groups[0].Key);
        Assert.Equal($"customer:{c2}", page.Groups[1].Key);

        // 客户一：USD（2 条 / 150）与 CNY（1 条 / 200）分开，绝不合并成单一金额
        var g1 = page.Groups[0];
        Assert.Equal(2, g1.Subtotals.Count);
        Assert.Equal("CNY", g1.Subtotals[0].Currency);
        Assert.Equal(1, g1.Subtotals[0].Count);
        Assert.Equal(200m, g1.Subtotals[0].Amount);
        Assert.Equal("USD", g1.Subtotals[1].Currency);
        Assert.Equal(2, g1.Subtotals[1].Count);
        Assert.Equal(150m, g1.Subtotals[1].Amount);

        // 客户二：USD（1 条 / 300）与 EUR（1 条 / 40）分开
        var g2 = page.Groups[1];
        Assert.Equal(2, g2.Subtotals.Count);
        Assert.Equal("USD", g2.Subtotals[0].Currency);
        Assert.Equal(1, g2.Subtotals[0].Count);
        Assert.Equal(300m, g2.Subtotals[0].Amount);
        Assert.Equal("EUR", g2.Subtotals[1].Currency);
        Assert.Equal(1, g2.Subtotals[1].Count);
        Assert.Equal(40m, g2.Subtotals[1].Amount);
    }

    // ==================== 3. 按月份分组：月边界 ====================

    [Fact]
    public async Task GroupBy_按月份分组_月边界分开且升序()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;

        SeedOrder(db, "SO-JAN-A", c1, new DateTime(2026, 1, 31), DocumentStatus.Pending, Currency.USD, 10m);
        SeedOrder(db, "SO-JAN-B", c1, new DateTime(2026, 1, 15), DocumentStatus.Pending, Currency.CNY, 20m);
        SeedOrder(db, "SO-FEB-A", c1, new DateTime(2026, 2, 1), DocumentStatus.Pending, Currency.USD, 30m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            GroupBy = "month",
            PageSize = 100
        }));

        Assert.Equal("month", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);

        // 月边界：2026-01（1/31）与 2026-02（2/1）各成一组，且按年月升序
        Assert.Equal("month:2026-01", page.Groups[0].Key);
        Assert.Equal("2026年1月", page.Groups[0].Label);
        Assert.Equal("month:2026-02", page.Groups[1].Key);
        Assert.Equal("2026年2月", page.Groups[1].Label);

        Assert.Equal(2, page.Groups[0].Subtotals.Count); // CNY + USD，分开
        Assert.Equal("CNY", page.Groups[0].Subtotals[0].Currency);
        Assert.Equal(1, page.Groups[0].Subtotals[0].Count);
        Assert.Equal(20m, page.Groups[0].Subtotals[0].Amount);
        Assert.Equal("USD", page.Groups[0].Subtotals[1].Currency);
        Assert.Equal(1, page.Groups[0].Subtotals[1].Count);
        Assert.Equal(10m, page.Groups[0].Subtotals[1].Amount);

        Assert.Single(page.Groups[1].Subtotals);
        Assert.Equal("USD", page.Groups[1].Subtotals[0].Currency);
        Assert.Equal(1, page.Groups[1].Subtotals[0].Count);
        Assert.Equal(30m, page.Groups[1].Subtotals[0].Amount);
    }

    // ==================== 4. 页面小计：仅当前页、随分页变化 ====================

    [Fact]
    public async Task GroupBy_页面小计_仅统计当前页_随分页变化()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;

        SeedOrder(db, "SO-1", c1, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 100m);
        SeedOrder(db, "SO-2", c1, new DateTime(2026, 9, 2), DocumentStatus.Pending, Currency.USD, 200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var p1 = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            GroupBy = "customer",
            Page = 1,
            PageSize = 1
        }));
        var g1 = Assert.Single(p1.Groups!);
        var s1 = Assert.Single(g1.Subtotals);
        Assert.Equal("USD", s1.Currency);
        Assert.Equal(1, s1.Count);
        Assert.Equal(100m, s1.Amount);

        var p2 = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            GroupBy = "customer",
            Page = 2,
            PageSize = 1
        }));
        var g2 = Assert.Single(p2.Groups!);
        var s2 = Assert.Single(g2.Subtotals);
        Assert.Equal(1, s2.Count);
        Assert.Equal(200m, s2.Amount);
    }

    // ==================== 5. 范围受限：只统计当前业务员范围内行 ====================

    [Fact]
    public async Task GroupBy_受限制业务员_只统计范围内客户行()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedUser(db, "alice");

        SeedOrder(db, "SO-MINE", mine, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other, new DateTime(2026, 9, 2), DocumentStatus.Pending, Currency.USD, 999m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            GroupBy = "customer",
            PageSize = 100
        }));

        Assert.Equal(1, page.Total);
        var g = Assert.Single(page.Groups!);
        Assert.Equal($"customer:{mine}", g.Key);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal("USD", s.Currency);
        Assert.Equal(1, s.Count);
        Assert.Equal(100m, s.Amount);
    }

    // ==================== 6. 空页 ====================

    [Fact]
    public async Task GroupBy_空页_无分组小计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            GroupBy = "customer",
            PageSize = 100
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Equal("customer", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
    }

    // ==================== 7. 无效分组键（控制器入口 fail closed） ====================

    [Fact]
    public async Task Preview_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSalesOrderReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task GroupBy_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-1", c1, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 100m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSalesOrderReportController(new DynamicSalesOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSalesOrderReportRequest
        {
            GroupBy = "customer",
            PageSize = 10
        }));

        Assert.Equal(1, page.Total);
        Assert.NotEmpty(page.Groups!);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 9. 只读计数上下文（断言不写库） ====================

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


