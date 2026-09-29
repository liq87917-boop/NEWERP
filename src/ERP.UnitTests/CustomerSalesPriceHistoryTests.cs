using System.Reflection;
using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户销售订单价格历史（ERP-108）单元测试：口径分组、原始单价与贸易条款保留、状态（仅已审核）、
/// 软删除（订单 / 明细）、业务员数据范围、分页有界与截断、无写入语义、参数校验与接口路由契约。
/// 全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API。
/// </summary>
public class CustomerSalesPriceHistoryTests
{
    // ==================== 分组 ====================

    [Fact]
    public async Task 相同商品_按客户商品规格单位币种分组_不同口径分单列()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820001L;
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");

        SeedLine(db, "SO-108-A1", a.Id, productId, "大号", "PCS", Currency.USD, 2m);
        SeedLine(db, "SO-108-A2", a.Id, productId, "大号", "PCS", Currency.USD, 3m);
        SeedLine(db, "SO-108-B1", a.Id, productId, "大号", "PCS", Currency.CNY, 10m); // 币种不同
        SeedLine(db, "SO-108-C1", a.Id, productId, "小号", "PCS", Currency.USD, 4m); // 规格不同
        SeedLine(db, "SO-108-D1", b.Id, productId, "大号", "PCS", Currency.USD, 5m); // 客户不同

        var view = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, PageSize = 100 });

        Assert.Equal(4, view.GroupCount);
        var usdGroup = view.Groups.Single(g => g.CustomerId == a.Id && g.Spec == "大号" && g.Currency == "USD");
        Assert.Equal(2, usdGroup.RowCount);
        Assert.All(usdGroup.Rows, r => Assert.Equal("PCS", r.Unit));
    }

    [Fact]
    public async Task 保留原始单价与贸易条款_不重定价()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820002L;
        var c = SeedCustomer(db, "C-1", "客户");
        SeedLine(db, "SO-108-T1", c.Id, productId, "大号", "PCS", Currency.USD, 12.34m, tradeTerms: "FOB");
        SeedLine(db, "SO-108-T2", c.Id, productId, "大号", "PCS", Currency.USD, 56.78m, tradeTerms: "CIF");

        var view = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, PageSize = 100 });

        var rows = view.Groups.Single().Rows;
        Assert.Equal(new[] { 12.34m, 56.78m }, rows.Select(r => r.UnitPrice).ToArray());
        Assert.Equal(new[] { "FOB", "CIF" }, rows.Select(r => r.TradeTerms).ToArray());
    }

    // ==================== 状态与软删除 ====================

    [Fact]
    public async Task 仅已审核订单明细计入_其余状态排除()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820003L;
        var c = SeedCustomer(db, "C-1", "客户");
        SeedLine(db, "SO-108-P", c.Id, productId, status: DocumentStatus.Pending);
        SeedLine(db, "SO-108-S", c.Id, productId, status: DocumentStatus.Submitted);
        SeedLine(db, "SO-108-A", c.Id, productId, status: DocumentStatus.Approved);
        SeedLine(db, "SO-108-X", c.Id, productId, status: DocumentStatus.Cancelled);

        var view = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, PageSize = 100 });

        Assert.Equal(1, view.TotalCount);
        Assert.Equal("SO-108-A", view.Groups.Single().Rows.Single().OrderNo);
    }

    [Fact]
    public async Task 排除软删除订单与软删除明细()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820004L;
        var c = SeedCustomer(db, "C-1", "客户");
        SeedLine(db, "SO-108-K1", c.Id, productId);
        var deletedOrder = SeedOrder(db, "SO-108-K2", c.Id, DocumentStatus.Approved);
        SeedDetail(db, deletedOrder.Id, productId, "大号", "PCS", Currency.USD, 1m);
        deletedOrder.IsDeleted = true;
        db.SaveChanges();

        var deletedDetailOrder = SeedOrder(db, "SO-108-K3", c.Id, DocumentStatus.Approved);
        var dd = SeedDetail(db, deletedDetailOrder.Id, productId, "大号", "PCS", Currency.USD, 1m);
        dd.IsDeleted = true;
        db.SaveChanges();

        var view = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, PageSize = 100 });

        Assert.Equal(1, view.TotalCount);
        Assert.Equal("SO-108-K1", view.Groups.Single().Rows.Single().OrderNo);
    }
    [Fact]
    public async Task 客户与订单日期区间筛选()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820005L;
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedLine(db, "SO-108-F1", a.Id, productId, orderDate: new DateTime(2026, 9, 1));
        SeedLine(db, "SO-108-F2", a.Id, productId, orderDate: new DateTime(2026, 9, 5));
        SeedLine(db, "SO-108-F3", b.Id, productId, orderDate: new DateTime(2026, 9, 1));

        var byCustomer = await QueryAsync(db, new CustomerSalesPriceHistoryQuery
        { ProductId = productId, CustomerId = a.Id, PageSize = 100 });
        Assert.Equal(2, byCustomer.TotalCount);
        Assert.All(byCustomer.Groups.SelectMany(g => g.Rows), r => Assert.Equal(a.Id, r.CustomerId));

        var byDate = await QueryAsync(db, new CustomerSalesPriceHistoryQuery
        { ProductId = productId, DateFrom = new DateTime(2026, 9, 2), DateTo = new DateTime(2026, 9, 5), PageSize = 100 });
        Assert.Equal(1, byDate.TotalCount);
        Assert.Equal("SO-108-F2", byDate.Groups.Single().Rows.Single().OrderNo);
    }

    [Fact]
    public async Task 业务员数据范围_只返回其客户订单()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820006L;
        var mine = SeedCustomer(db, "C-M", "我的客户");
        var others = SeedCustomer(db, "C-O", "别人的客户");
        SeedLine(db, "SO-108-M1", mine.Id, productId);
        SeedLine(db, "SO-108-O1", others.Id, productId);

        var view = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, PageSize = 100 },
            allowedCustomerIds: new HashSet<long> { mine.Id });

        Assert.Equal(1, view.TotalCount);
        Assert.Equal("SO-108-M1", view.Groups.Single().Rows.Single().OrderNo);
    }

    [Fact]
    public async Task 分页有界_并显式标记截断()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820007L;
        var c = SeedCustomer(db, "C-1", "客户");
        for (var i = 0; i < 3; i++)
            SeedLine(db, $"SO-108-PG{i}", c.Id, productId, orderDate: new DateTime(2026, 9, 1 + i));

        var view = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, Page = 1, PageSize = 2 });

        Assert.Equal(3, view.TotalCount);
        Assert.True(view.Truncated);
        Assert.Equal(2, view.Groups.Single().RowCount);

        var last = await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, Page = 2, PageSize = 2 });
        Assert.False(last.Truncated);
        Assert.Equal(1, last.Groups.Single().RowCount);
    }

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820008L;
        var c = SeedCustomer(db, "C-1", "客户");
        SeedLine(db, "SO-108-R1", c.Id, productId);

        var beforeOrders = db.SalesOrders.Count();
        var beforeDetails = db.SalesOrderDetails.Count();
        await QueryAsync(db, new CustomerSalesPriceHistoryQuery { ProductId = productId, PageSize = 100 });

        Assert.Equal(beforeOrders, db.SalesOrders.Count());
        Assert.Equal(beforeDetails, db.SalesOrderDetails.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
    [Fact]
    public async Task 缺商品Id_或日期区间倒置_抛参数错误()
    {
        using var db = TestDbFactory.Create();
        var noProduct = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new CustomerSalesPriceHistoryQuery { ProductId = null }));
        Assert.Equal(ErrorCodes.InvalidParameter, noProduct.Code);

        var badDate = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new CustomerSalesPriceHistoryQuery
            { ProductId = 1L, DateFrom = new DateTime(2026, 9, 10), DateTo = new DateTime(2026, 9, 1) }));
        Assert.Equal(ErrorCodes.InvalidParameter, badDate.Code);
    }

    [Fact]
    public async Task 控制器_受限制业务员_价格历史只返回其客户()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820009L;
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C-M", "我的客户", alice.Id);
        var others = SeedCustomer(db, "C-O", "别人的客户", alice.Id + 1000);
        SeedLine(db, "SO-108-SC1", mine.Id, productId);
        SeedLine(db, "SO-108-SC2", others.Id, productId);

        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        SetUser(ctl, user.Id);

        var ok = Assert.IsType<OkObjectResult>(await ctl.PriceHistory(new CustomerSalesPriceHistoryQuery
        { ProductId = productId, PageSize = 100 }));
        var resp = Assert.IsType<ApiResponse<CustomerSalesPriceHistoryView>>(ok.Value);

        Assert.Equal(1, resp.Data!.TotalCount);
        Assert.Equal("SO-108-SC1", resp.Data.Groups.Single().Rows.Single().OrderNo);
    }

    [Fact]
    public void 接口路由_与前端调用路径一致()
    {
        var route = typeof(SalesOrderController).GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/sales-orders", route);
        var method = typeof(SalesOrderController).GetMethod(nameof(SalesOrderController.PriceHistory),
            BindingFlags.Public | BindingFlags.Instance)!;
        var templates = method.GetCustomAttributes<HttpMethodAttribute>(true).Select(a => a.Template ?? string.Empty).ToList();
        Assert.Contains("price-history", templates);
    }

    [Fact]
    public void 前端接线_工具栏入口_接口路径与脚本加载()
    {
        var jsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
        var modules = File.ReadAllText(Path.Combine(jsDir, "modules-doc.js"));
        var js = File.ReadAllText(Path.Combine(jsDir, "customer-sales-price-history.js"));
        var indexHtml = File.ReadAllText(Path.Combine(jsDir, "..", "index.html"));

        Assert.Contains("onclick: 'openCustomerSalesPriceHistory'", modules);
        Assert.Contains("function openCustomerSalesPriceHistory()", js);
        Assert.Contains("function loadCustomerSalesPriceHistory(", js);
        Assert.Contains("`/api/sales-orders/price-history?${qs}`", js);
        Assert.Contains("openForm(${r.orderId})", js);
        Assert.Contains("/js/customer-sales-price-history.js", indexHtml);
    }
    // ==================== 助手 ====================

    private static async Task<CustomerSalesPriceHistoryView> QueryAsync(ErpDbContext db,
        CustomerSalesPriceHistoryQuery query, HashSet<long>? allowedCustomerIds = null)
        => await CustomerSalesPriceHistoryService.QueryAsync(db, query, allowedCustomerIds);

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        DocumentStatus status, DateTime? orderDate = null, string tradeTerms = "", Currency currency = Currency.USD)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            Status = status,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            TradeTerms = tradeTerms,
            Currency = currency,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(ErpDbContext db, long salesOrderId, long productId,
        string spec, string unit, Currency currency, decimal unitPrice, decimal quantity = 1m)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = spec,
            Unit = unit,
            UnitPrice = unitPrice,
            Quantity = quantity,
            Amount = unitPrice * quantity,
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static SalesOrderDetail SeedLine(ErpDbContext db, string orderNo, long customerId, long productId,
        string spec = "大号", string unit = "PCS", Currency currency = Currency.USD, decimal unitPrice = 2m,
        DocumentStatus status = DocumentStatus.Approved, DateTime? orderDate = null, string tradeTerms = "")
    {
        var order = SeedOrder(db, orderNo, customerId, status, orderDate, tradeTerms, currency);
        return SeedDetail(db, order.Id, productId, spec, unit, currency, unitPrice);
    }

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        { UserName = userName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = userName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var role = new SysRole { RoleName = code, RoleCode = code };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
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
}
