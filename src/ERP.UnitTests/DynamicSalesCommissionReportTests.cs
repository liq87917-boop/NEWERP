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
/// 动态业务员提成证据报表（ERP-244）聚焦单元测试：字段目录 / 字段校验、日期与分页边界、
/// 可选应用筛选（客户 / 业务员 / 原币）、业务员数据范围、稳定分页、服务端上下文派生、只读操作。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesCommissionReportTests
{
    private const string MenuCode = "sales-commission";

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

    private static DynamicSalesCommissionReportController BuildController(ErpDbContext db, long? userId)
        => new(db, new ReportService(db));

    private static DynamicSalesCommissionReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSalesCommissionReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicSalesCommissionReportCatalogDto OkCatalog(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSalesCommissionReportCatalogDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicSalesCommissionReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        SalesCommissionFilterDto? filter = null)
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
    public void 目录_有限白名单_包含业务员桶原币订单数金额利润提成与来源依据字段()
    {
        var catalog = DynamicSalesCommissionReportRules.GetCatalogDto();

        Assert.Equal("sales-commission", catalog.RequiredMenuCode);
        Assert.Equal("业务员提成表", catalog.RequiredMenuText);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(20, catalog.DefaultPageSize);

        var keys = catalog.Fields.Select(f => f.Key).ToList();
        Assert.Contains("salesmanId", keys);
        Assert.Contains("salesmanName", keys);
        Assert.Contains("salesmanIdentityEvidence", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("currencyLabel", keys);
        Assert.Contains("orderCount", keys);
        Assert.Contains("salesAmount", keys);
        Assert.Contains("profit", keys);
        Assert.Contains("profitRate", keys);
        Assert.Contains("commissionRate", keys);
        Assert.Contains("commissionAmount", keys);
        Assert.Contains("sourceLabel", keys);
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        var catalog = DynamicSalesCommissionReportRules.GetCatalogDto();
        var keys = DynamicSalesCommissionReportRules.NormalizeFields(null);
        Assert.Equal(catalog.Fields.Select(f => f.Key).ToList(), keys);
    }

    [Fact]
    public void NormalizeFields_未知重复空键拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFields(new List<string> { "notAField" }));
        Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFields(new List<string> { "currency", "currency" }));
        Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFields(new List<string> { "" }));
    }

    [Fact]
    public void ValidatePageBounds_拒绝页码小于1与每页越界()
    {
        Assert.Throws<BusinessException>(() => DynamicSalesCommissionReportRules.ValidatePageBounds(0, 20));
        Assert.Throws<BusinessException>(() => DynamicSalesCommissionReportRules.ValidatePageBounds(1, 0));
        Assert.Throws<BusinessException>(() => DynamicSalesCommissionReportRules.ValidatePageBounds(1, 201));
    }

    [Fact]
    public void ValidateDateRange_拒绝非法日期()
    {
        Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.ValidateDateRange(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1)));
        Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.ValidateDateRange(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
    }

    // ==================== 2. 预览（内存数据库） ====================

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
            filter: new SalesCommissionFilterDto { CustomerId = c1.Id, SalesmanId = emp1.Id, Currency = "USD" })));

        Assert.Equal(1, page.Total);
        Assert.Equal(100m, Assert.IsType<decimal>(page.Rows[0]["salesAmount"]));
        Assert.Equal("客户 Id " + c1.Id + "；业务员 Id " + emp1.Id + "；原币币种 USD", page.FilterText);
    }

    [Fact]
    public async Task 预览_缺少筛选_保留未分配与未知业务员桶()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var employee = SeedEmployee(db, "S001", "业务员甲");
        employee.IsDeleted = true;
        db.SaveChanges();

        SeedOrder(db, "SO-NONE", customer.Id, null, Currency.USD, 100m);
        SeedOrder(db, "SO-GONE", customer.Id, employee.Id, Currency.USD, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request()));

        Assert.Equal(2, page.Total);
        Assert.Contains(page.Rows, r => r["salesmanId"] == null);
        Assert.Contains(page.Rows, r => r["salesmanId"] is long id && id == employee.Id);
    }

    [Fact]
    public async Task 预览_稳定分页_总数与当前页覆盖()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, emp.Id, Currency.CNY, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var fields = new List<string> { "salesmanName", "currency", "salesAmount" };
        var page1 = OkPage(await ctl.Preview(Request(fields: fields, page: 1, pageSize: 1)));
        var page2 = OkPage(await ctl.Preview(Request(fields: fields, page: 2, pageSize: 1)));

        Assert.Equal(2, page1.Total);
        Assert.Equal(2, page1.TotalPages);
        Assert.True(page1.PageOnly);
        Assert.Single(page1.Rows);
        Assert.Single(page2.Rows);
        Assert.NotEqual(page1.Rows[0]["currency"], page2.Rows[0]["currency"]);
        Assert.Equal(2, page1.Context.SalesmanCurrencyRows);
        Assert.Equal(1, page1.Context.UniqueSalesmanBuckets);
        Assert.Equal(2, page1.Context.ApprovedOrders);
    }

    [Fact]
    public async Task 预览_隐藏证据列_日期筛选来源上限与提成上下文仍可见()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(fields: new List<string> { "currency" })));

        Assert.Single(page.Columns);
        Assert.All(page.Rows, r => Assert.Single(r));
        Assert.NotNull(page.Context);
        Assert.NotEqual(string.Empty, page.SourceLimitText);
        Assert.NotEqual(string.Empty, page.SourceContextText);
        Assert.NotEqual(string.Empty, page.CurrencyContextText);
        Assert.NotEqual(string.Empty, page.CommissionContextText);
        Assert.NotEqual(string.Empty, page.RateContextText);
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
    public async Task 预览_非法筛选_在读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(Request(filter: new SalesCommissionFilterDto { Currency = "999" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_来源超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        db.SalesOrders.AddRange(Enumerable.Range(1, 501).Select(i => new SalesOrder
        {
            OrderNo = $"SO-{i:0000}",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            SalesmanId = emp.Id,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            TotalAmount = 100m
        }));
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task 预览_全匹配汇总_独立于当前页与选定列()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "currency" }, page: 1, pageSize: 1)));

        Assert.Single(page.Rows);                              // 当前页仅 1 行
        Assert.NotNull(page.Summary);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);      // 汇总覆盖全部匹配行（USD + CNY）
        Assert.Equal(2, page.Summary.GlobalUniqueSalesmanBuckets);
        Assert.Equal(3, page.Summary.GlobalApprovedOrders);
    }

    [Fact]
    public async Task 预览_汇总_受限制业务员_只汇总被分配客户证据()
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

        Assert.NotNull(page.Summary);
        Assert.Equal(1, page.Summary.GlobalApprovedOrders);
        Assert.Equal(1, page.Summary.GlobalUniqueSalesmanBuckets);
        var usd = Assert.Single(page.Summary.CurrencyRows);
        Assert.Equal(100m, usd.SalesAmount);
    }
}
