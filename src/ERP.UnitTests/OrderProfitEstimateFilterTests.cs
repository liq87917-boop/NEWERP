using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-223 订单利润暂估表「客户 Id / 原币币种」筛选的聚焦单元测试：省略筛选保持既有行为、
/// 客户 / 原币 / 组合筛选、业务员数据范围与筛选叠加（不泄露范围外数据）、筛选先于 500 订单头上限探测，
/// 以及经动态预览端点校验非法 token 并返回规范化筛选上下文。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class OrderProfitEstimateFilterTests
{
    private const string OrderProfitMenuCode = "order-profit";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount = 1000m, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Priv-{userName}", isSystem: true);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, OrderProfitMenuCode).Id);
        return user;
    }

    private static DynamicOrderProfitEstimateReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicOrderProfitEstimateReportController(db, new ReportService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static DynamicOrderProfitEstimateReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicOrderProfitEstimateReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 服务层：省略筛选 / 客户 / 原币 / 组合 ====================

    [Fact]
    public async Task 服务_省略筛选_保持既有行为_返回全部范围内订单()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedOrder(db, "SO-A", a.Id, Currency.USD);
        SeedOrder(db, "SO-B", b.Id, Currency.EUR);

        var rows = await new ReportService(db).GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.OrderNo == "SO-A");
        Assert.Contains(rows, r => r.OrderNo == "SO-B");
    }

    [Fact]
    public async Task 服务_客户筛选_只返回该客户订单()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedOrder(db, "SO-A", a.Id, Currency.USD);
        SeedOrder(db, "SO-B", b.Id, Currency.USD);

        var rows = await new ReportService(db).GetOrderProfitEstimateAsync(Start, End, PrivilegedScope,
            new OrderProfitEstimateFilterDto { CustomerId = a.Id });

        var row = Assert.Single(rows);
        Assert.Equal("SO-A", row.OrderNo);
    }

    [Fact]
    public async Task 服务_原币筛选_只返回该币种订单()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        SeedOrder(db, "SO-USD", a.Id, Currency.USD);
        SeedOrder(db, "SO-EUR", a.Id, Currency.EUR);

        var rows = await new ReportService(db).GetOrderProfitEstimateAsync(Start, End, PrivilegedScope,
            new OrderProfitEstimateFilterDto { Currency = "USD" });

        var row = Assert.Single(rows);
        Assert.Equal("SO-USD", row.OrderNo);
        Assert.Equal("USD", row.Currency);
    }

    [Fact]
    public async Task 服务_组合筛选_客户与原币同时生效()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedOrder(db, "SO-A-USD", a.Id, Currency.USD);
        SeedOrder(db, "SO-A-EUR", a.Id, Currency.EUR);
        SeedOrder(db, "SO-B-USD", b.Id, Currency.USD);

        var rows = await new ReportService(db).GetOrderProfitEstimateAsync(Start, End, PrivilegedScope,
            new OrderProfitEstimateFilterDto { CustomerId = a.Id, Currency = "USD" });

        var row = Assert.Single(rows);
        Assert.Equal("SO-A-USD", row.OrderNo);
    }


    [Fact]
    public async Task 服务_受限制范围与筛选叠加_不泄露范围外数据()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C-MINE", "我的客户");
        var other = SeedCustomer(db, "C-OTHER", "他人客户");
        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.USD);

        var restricted = new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { mine.Id } };
        var service = new ReportService(db);

        var none = await service.GetOrderProfitEstimateAsync(Start, End, restricted,
            new OrderProfitEstimateFilterDto { CustomerId = other.Id });
        Assert.Empty(none);

        var mineRows = await service.GetOrderProfitEstimateAsync(Start, End, restricted,
            new OrderProfitEstimateFilterDto { CustomerId = mine.Id });
        var row = Assert.Single(mineRows);
        Assert.Equal("SO-MINE", row.OrderNo);
    }

    [Fact]
    public async Task 服务_筛选先于500订单上限_过滤后不再溢出()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");

        var many = new List<SalesOrder>();
        for (var i = 1; i <= 501; i++)
        {
            many.Add(new SalesOrder
            {
                OrderNo = $"SO-A-{i:0000}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = a.Id,
                Status = DocumentStatus.Approved,
                Currency = Currency.USD,
                TotalAmount = 100m
            });
        }
        many.Add(new SalesOrder
        {
            OrderNo = "SO-B-1",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = b.Id,
            Status = DocumentStatus.Approved,
            Currency = Currency.USD,
            TotalAmount = 100m
        });
        db.SalesOrders.AddRange(many);
        db.SaveChanges();

        var rows = await new ReportService(db).GetOrderProfitEstimateAsync(Start, End, PrivilegedScope,
            new OrderProfitEstimateFilterDto { CustomerId = b.Id });

        var row = Assert.Single(rows);
        Assert.Equal("SO-B-1", row.OrderNo);
    }


    // ==================== 2. 动态预览端点：非法 token 拒绝 + 规范化筛选上下文 ====================

    [Fact]
    public async Task 预览_非法客户Id_校验先于读取并拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opf-cid-invalid");
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicOrderProfitEstimateReportRequest
            {
                Start = Start,
                End = End,
                Filter = new OrderProfitEstimateFilterDto { CustomerId = 0 }
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_非法原币_校验先于读取并拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opf-cur-invalid");
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicOrderProfitEstimateReportRequest
            {
                Start = Start,
                End = End,
                Filter = new OrderProfitEstimateFilterDto { Currency = "ABC" }
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_应用筛选_结果与规范化筛选上下文一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opf-preview");
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedOrder(db, "SO-A", a.Id, Currency.USD);
        SeedOrder(db, "SO-B", b.Id, Currency.USD);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End,
            Filter = new OrderProfitEstimateFilterDto { CustomerId = a.Id }
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("SO-A", row["orderNo"]);
        Assert.Contains("客户 Id " + a.Id, page.FilterText);
    }
}

