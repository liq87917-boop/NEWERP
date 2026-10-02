using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态业务员产值证据报表（ERP-237）聚焦单元测试：字段目录 / 字段校验、日期与分页边界、
/// 可选应用筛选（客户 / 业务员 / 原币）、业务员数据范围、稳定分页、服务端上下文派生、只读操作。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesmanOutputReportTests
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved)
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

    // ==================== 1. 字段目录 / 校验（纯规则） ====================

    [Fact]
    public void 目录_有限白名单_包含业务员原币订单数金额利润与来源依据字段()
    {
        var catalog = DynamicSalesmanOutputReportRules.GetCatalogDto();

        Assert.Equal("salesman-output", catalog.RequiredMenuCode);
        Assert.Equal("业务员产值报表", catalog.RequiredMenuText);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(20, catalog.DefaultPageSize);

        var keys = catalog.Fields.Select(f => f.Key).ToList();
        Assert.Contains("salesmanId", keys);
        Assert.Contains("salesmanName", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("currencyLabel", keys);
        Assert.Contains("orderCount", keys);
        Assert.Contains("totalAmount", keys);
        Assert.Contains("totalProfit", keys);
        Assert.Contains("amountLabel", keys);
        Assert.Contains("currencyEvidence", keys);
        Assert.Contains("profitEvidence", keys);
        Assert.Contains("salesmanIdentityEvidence", keys);
        Assert.Contains("sourceEvidence", keys);
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        var catalog = DynamicSalesmanOutputReportRules.GetCatalogDto();
        var keys = DynamicSalesmanOutputReportRules.NormalizeFields(null);
        Assert.Equal(catalog.Fields.Select(f => f.Key).ToList(), keys);
    }

    [Fact]
    public void NormalizeFields_保持请求顺序()
    {
        var keys = DynamicSalesmanOutputReportRules.NormalizeFields(
            new List<string> { "currency", "salesmanName", "orderCount" });
        Assert.Equal(new[] { "currency", "salesmanName", "orderCount" }, keys);
    }

    [Fact]
    public void NormalizeFields_未知字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicSalesmanOutputReportRules.NormalizeFields(new List<string> { "salesmanName", "notAField" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_重复字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicSalesmanOutputReportRules.NormalizeFields(new List<string> { "salesmanName", "salesmanName" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void 日期与分页_超出边界拒绝()
    {
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.ValidateDateRange(End, Start));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.ValidateDateRange(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.ValidatePageBounds(0, 20));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.ValidatePageBounds(1, 0));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.ValidatePageBounds(1, 201));
    }

    // ==================== 2. 可选应用筛选（纯规则） ====================

    [Fact]
    public void NormalizeFilter_留空返回null_非法Ids与币种拒绝()
    {
        Assert.Null(DynamicSalesmanOutputReportRules.NormalizeFilter(null));
        Assert.Null(DynamicSalesmanOutputReportRules.NormalizeFilter(new SalesmanOutputFilterDto()));

        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { CustomerId = 0 }));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { SalesmanId = -1 }));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { Currency = "999" }));
        Assert.Throws<BusinessException>(() => DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { Currency = "XXX" }));
    }

    [Fact]
    public void NormalizeFilter_已知币种大小写归一化()
    {
        var filter = DynamicSalesmanOutputReportRules.NormalizeFilter(
            new SalesmanOutputFilterDto { CustomerId = 1, SalesmanId = 2, Currency = " usd " });
        Assert.Equal(1, filter!.CustomerId);
        Assert.Equal(2, filter.SalesmanId);
        Assert.Equal("USD", filter.Currency);
        Assert.Equal("客户 Id 1；业务员 Id 2；原币币种 USD",
            DynamicSalesmanOutputReportRules.BuildFilterContext(filter));
    }

    // ==================== 3. 预览：上下文 / 分页 / 筛选 ====================

    [Fact]
    public async Task 预览_上下文描述业务员原币行与已分配已审核订单_绝不统计全部ERP订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp1.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-3", customer.Id, emp2.Id, Currency.CNY, 50m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(pageSize: 20)));

        Assert.Equal(2, page.Total);           // 业务员 × 原币证据行总数（非订单总数）
        Assert.Equal(1, page.TotalPages);
        Assert.Equal(2, page.Context.SalesmanCurrencyRows);
        Assert.Equal(2, page.Context.UniqueSalesmen);
        Assert.Equal(3, page.Context.AssignedApprovedOrders);   // 已分配已审核订单数
        Assert.False(page.PageOnly);
    }

    [Fact]
    public async Task 预览_稳定分页_只返回当前页行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, emp.Id, Currency.CNY, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var fields = new List<string> { "salesmanName", "currency", "totalAmount" };
        var page1 = OkPage(await ctl.Preview(Request(fields: fields, page: 1, pageSize: 1)));
        var page2 = OkPage(await ctl.Preview(Request(fields: fields, page: 2, pageSize: 1)));

        Assert.Equal(2, page1.Total);
        Assert.Equal(2, page1.TotalPages);
        Assert.True(page1.PageOnly);
        Assert.Single(page1.Rows);
        Assert.Single(page2.Rows);
        Assert.NotEqual(page1.Rows[0]["currency"], page2.Rows[0]["currency"]);
    }

    [Fact]
    public async Task 预览_客户业务员币种筛选在聚合前收窄()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", c1.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", c1.Id, emp1.Id, Currency.CNY, 200m);
        SeedOrder(db, "SO-3", c2.Id, emp2.Id, Currency.USD, 300m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(
            filter: new SalesmanOutputFilterDto { CustomerId = c1.Id, SalesmanId = emp1.Id, Currency = "USD" })));

        Assert.Equal(1, page.Total);
        Assert.Equal(100m, Assert.IsType<decimal>(page.Rows[0]["totalAmount"]));
        Assert.Equal("客户 Id " + c1.Id + "；业务员 Id " + emp1.Id + "；原币币种 USD", page.FilterText);
    }

    [Fact]
    public async Task 预览_未知币种金额与未知利润为null_不回落为0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-U", customer.Id, emp.Id, (Currency)999, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(fields: new List<string> { "currency", "totalAmount", "totalProfit" })));

        var row = Assert.Single(page.Rows);
        Assert.Equal("999", row["currency"]);
        Assert.Null(row["totalAmount"]);
        Assert.Null(row["totalProfit"]);
    }

    [Fact]
    public async Task 受限制业务员_只看到被分配客户订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice", "业务员甲");
        var otherEmp = SeedEmployee(db, "bob", "业务员乙");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmp.Id);

        SeedOrder(db, "SO-MINE", mine.Id, employee.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, otherEmp.Id, Currency.USD, 9000m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request()));

        var row = Assert.Single(page.Rows);
        Assert.Equal(employee.Id, row["salesmanId"]);
        Assert.DoesNotContain(page.Rows, r => (long)r["salesmanId"]! == otherEmp.Id);
    }

    [Fact]
    public async Task 缺少菜单授权_目录与预览均拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var catalogEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, catalogEx.Code);

        var previewEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
        Assert.Equal(ErrorCodes.Forbidden, previewEx.Code);
    }

    [Fact]
    public async Task 预览_全匹配汇总覆盖全部匹配行_与分页和选定列无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        // 越界页 + 隐藏字段（仅选定 currency）时汇总仍覆盖全部匹配行
        var page = OkPage(await ctl.Preview(Request(fields: new List<string> { "currency" }, page: 9, pageSize: 1)));

        Assert.NotNull(page.Summary);
        Assert.Single(page.Columns);
        Assert.Empty(page.Rows);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);

        var usd = Assert.Single(page.Summary.CurrencyRows.Where(r => r.Currency == "USD"));
        Assert.Equal(800m, usd.TotalAmount);       // 1000 + (-200)，覆盖全部匹配行
        Assert.Equal(2, usd.SalesmanCount);
        Assert.Equal(2, usd.OrderCount);
        Assert.Null(usd.TotalProfit);
        Assert.Null(usd.ProfitRate);
    }
}




