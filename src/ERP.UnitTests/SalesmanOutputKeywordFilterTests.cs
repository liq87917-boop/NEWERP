using System.Security.Claims;
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
/// ERP-242 业务员产值证据报表「业务员姓名关键字」筛选的聚焦单元测试。
/// <para>覆盖：关键字规范化（留空 / 去首尾空白 / 80 字符上限 / 控制字符拒绝 / 字面 % _）、目录能力说明、
/// 服务端在客户范围与既有谓词之后、501 订单头上限探测之前用关联 BaseEmployees 谓词相交、缺失 / 已删除员工不匹配非空关键字、
/// 省略关键字保留既有未知身份行、与客户范围相交、字面通配符不被当作 SQL 通配符、预览 / 全匹配汇总上下文传播。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。
/// SQL Server 生成 SQL 的离线翻译验收为环境阻断（无数据库连接），本文件仅以内存 EF 验证谓词形状与行为，绝不声称真实数据库通过。</para>
/// </summary>
public class SalesmanOutputKeywordFilterTests
{
    private const string MenuCode = "salesman-output";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name, bool isSalesman = true, bool isDeleted = false)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = isSalesman,
            Status = 1,
            IsDeleted = isDeleted
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId,
        Currency currency = Currency.USD, decimal totalAmount = 100m,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            Status = status,
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static DynamicSalesmanOutputReportController BuildController(ErpDbContext db, long? userId)
        => new(db, new ReportService(db));

    private static DynamicSalesmanOutputReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSalesmanOutputReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicSalesmanOutputReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        SalesmanOutputFilterDto? filter = null)
        => new()
        {
            Fields = fields,
            Start = Start,
            End = End,
            Page = page,
            PageSize = pageSize,
            Filter = filter,
        };

    // ==================== 1. 关键字规范化（纯规则） ====================

    [Fact]
    public void Normalize关键字_留空空白返回null_去首尾空白_超长拒绝()
    {
        Assert.Null(DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword(null));
        Assert.Null(DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword(""));
        Assert.Null(DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword("   "));

        Assert.Equal("张三", DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword("  张三  "));

        var over = new string('张', DynamicSalesmanOutputReportRules.MaxFilterKeywordLength + 1);
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword(over));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Normalize关键字_控制字符拒绝_字面百分号下划线保留()
    {
        Assert.Throws<BusinessException>(() =>
            DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword("张\u0000三"));
        Assert.Throws<BusinessException>(() =>
            DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword("张\t三"));

        Assert.Equal("100%_", DynamicSalesmanOutputReportRules.NormalizeSalesmanNameKeyword(" 100%_ "));
    }

    [Fact]
    public void NormalizeFilter_姓名关键字进入规范化与上下文_全空返回null()
    {
        var filter = DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { SalesmanName = "  张三  " });
        Assert.NotNull(filter);
        Assert.Equal("张三", filter.SalesmanName);
        Assert.Equal("业务员姓名关键字 张三",
            DynamicSalesmanOutputReportRules.BuildFilterContext(filter));

        Assert.Null(DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { SalesmanName = "   " }));
    }

    [Fact]
    public void 目录口径_描述姓名关键字能力()
    {
        var catalog = DynamicSalesmanOutputReportRules.GetCatalogDto();
        Assert.Contains("业务员姓名关键字", catalog.FilterText);
        Assert.Contains("80", catalog.FilterText);
    }

    // ==================== 2. 服务端关键字过滤（内存 EF 验证谓词形状与行为） ====================

    [Fact]
    public async Task 服务_姓名关键字_字面包含匹配未删除员工()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "张三丰");
        var emp2 = SeedEmployee(db, "S002", "李四");
        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, 200m);

        var rows = await new ReportService(db).GetSalesmanOutputAsync(
            Start, End, PrivilegedScope, new SalesmanOutputFilterDto { SalesmanName = "张三" });

        var row = Assert.Single(rows);
        Assert.Equal("张三丰", row.SalesmanName);
        Assert.Equal(100m, row.TotalAmount);
    }

    [Fact]
    public async Task 服务_姓名关键字_缺失员工不匹配_省略关键字保留未知身份行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-GHOST", customer.Id, 999L, Currency.USD, 100m);

        var service = new ReportService(db);

        var noFilter = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);
        var unknown = Assert.Single(noFilter);
        Assert.Equal(SalesmanOutputEvidenceRules.UnknownSalesmanName, unknown.SalesmanName);

        var filtered = await service.GetSalesmanOutputAsync(
            Start, End, PrivilegedScope, new SalesmanOutputFilterDto { SalesmanName = "未知" });
        Assert.Empty(filtered);
    }

    [Fact]
    public async Task 服务_姓名关键字_已删除员工不匹配()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var deleted = SeedEmployee(db, "S001", "张三丰", isDeleted: true);
        SeedOrder(db, "SO-DEL", customer.Id, deleted.Id, Currency.USD, 100m);

        var rows = await new ReportService(db).GetSalesmanOutputAsync(
            Start, End, PrivilegedScope, new SalesmanOutputFilterDto { SalesmanName = "张三" });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task 服务_姓名关键字_与客户范围相交_不扩展权限()
    {
        using var db = TestDbFactory.Create();
        var emp = SeedEmployee(db, "S001", "张三丰");
        var otherEmp = SeedEmployee(db, "S002", "张三丰");
        var mine = SeedCustomer(db, "C001", "我的客户", emp.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmp.Id);
        SeedOrder(db, "SO-MINE", mine.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, otherEmp.Id, Currency.USD, 200m);

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = emp.Id,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };

        var rows = await new ReportService(db).GetSalesmanOutputAsync(
            Start, End, scope, new SalesmanOutputFilterDto { SalesmanName = "张三" });

        var row = Assert.Single(rows);
        Assert.Equal(emp.Id, row.SalesmanId);
        Assert.Equal(100m, row.TotalAmount);
    }

    [Fact]
    public async Task 服务_姓名关键字_百分号下划线按字面文本_不通配()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var e1 = SeedEmployee(db, "S001", "A_B");
        var e2 = SeedEmployee(db, "S002", "AB");
        var e3 = SeedEmployee(db, "S003", "100%棉");
        var e4 = SeedEmployee(db, "S004", "100棉");
        SeedOrder(db, "SO-1", customer.Id, e1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, e2.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-3", customer.Id, e3.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-4", customer.Id, e4.Id, Currency.USD, 100m);

        var service = new ReportService(db);

        var underscore = await service.GetSalesmanOutputAsync(
            Start, End, PrivilegedScope, new SalesmanOutputFilterDto { SalesmanName = "_" });
        Assert.Single(underscore);
        Assert.Equal("A_B", underscore[0].SalesmanName);

        var percent = await service.GetSalesmanOutputAsync(
            Start, End, PrivilegedScope, new SalesmanOutputFilterDto { SalesmanName = "%" });
        Assert.Single(percent);
        Assert.Equal("100%棉", percent[0].SalesmanName);
    }

    [Fact]
    public async Task 服务_姓名关键字_在500上限之前过滤_过滤后不超限()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var matchEmp = SeedEmployee(db, "S001", "张三丰");
        var otherEmp = SeedEmployee(db, "S002", "李四");

        var orders = new List<SalesOrder>
        {
            new()
            {
                OrderNo = "SO-MATCH",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                SalesmanId = matchEmp.Id,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
                TotalAmount = 100m
            }
        };
        for (var i = 0; i < 500; i++)
        {
            orders.Add(new SalesOrder
            {
                OrderNo = $"SO-OTHER-{i:D4}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                SalesmanId = otherEmp.Id,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
                TotalAmount = 100m
            });
        }
        db.SalesOrders.AddRange(orders);
        db.SaveChanges();

        var service = new ReportService(db);

        var overflow = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetSalesmanOutputAsync(Start, End, PrivilegedScope));
        Assert.Equal(ErrorCodes.RuleConflict, overflow.Code);

        var rows = await service.GetSalesmanOutputAsync(
            Start, End, PrivilegedScope, new SalesmanOutputFilterDto { SalesmanName = "张三" });
        var row = Assert.Single(rows);
        Assert.Equal("张三丰", row.SalesmanName);
        Assert.Equal(1, row.OrderCount);
    }

    // ==================== 3. 预览 / 全匹配汇总上下文传播 ====================

    [Fact]
    public async Task 预览_姓名关键字上下文与全匹配汇总覆盖过滤后集合_即使业务员字段隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "张三丰");
        var emp2 = SeedEmployee(db, "S002", "李四");
        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.CNY, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "currency" },
            filter: new SalesmanOutputFilterDto { SalesmanName = "  张三  " })));

        Assert.Contains("业务员姓名关键字 张三", page.FilterText);
        Assert.Equal(1, page.Total);
        Assert.Single(page.Rows);

        Assert.NotNull(page.Summary);
        var currency = Assert.Single(page.Summary.CurrencyRows);
        Assert.Equal("USD", currency.Currency);
        Assert.Equal(100m, currency.TotalAmount);
    }
}
