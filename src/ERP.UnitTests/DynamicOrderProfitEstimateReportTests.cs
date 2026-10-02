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
/// 动态订单利润暂估报表（ERP-221）聚焦单元测试：字段目录 / 字段校验（未知 / 重复 / 空键 / 顺序）、
/// 日期与分页边界校验、稳定分页切片与 Total / PageOnly 元数据、行投影保留原币与未知成本证据、
/// 业务员数据范围（撤销授权 / 空客户）、空页与只读。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicOrderProfitEstimateReportTests
{
    private const string OrderProfitMenuCode = "order-profit";
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

    private static BaseProduct SeedProduct(ErpDbContext db, string code, decimal costPrice)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = code,
            SalePrice = 100m,
            CostPrice = costPrice,
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount,
        DocumentStatus status = DocumentStatus.Approved, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(ErpDbContext db, long orderId, long productId, decimal quantity)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = productId,
            ProductName = "商品",
            Quantity = quantity,
            Unit = "PCS"
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, OrderProfitMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);
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

    // ==================== 字段目录与校验（纯规则） ====================

    [Fact]
    public void 目录_有限白名单_包含身份日期客户原币销售额与未知成本利润证据_当前价估算独立标注()
    {
        var catalog = DynamicOrderProfitEstimateReportRules.GetCatalog();

        Assert.Equal(DynamicOrderProfitEstimateReportRules.AllFieldKeys.Count, catalog.Count);
        Assert.Contains(catalog, f => f.Key == "orderId");
        Assert.Contains(catalog, f => f.Key == "customerId");
        Assert.Contains(catalog, f => f.Key == "orderDate");
        Assert.Contains(catalog, f => f.Key == "customerName");
        Assert.Contains(catalog, f => f.Key == "currency");
        Assert.Contains(catalog, f => f.Key == "salesAmount");
        Assert.Contains(catalog, f => f.Key == "costAmount");
        Assert.Contains(catalog, f => f.Key == "profit");
        Assert.Contains(catalog, f => f.Key == "profitRate");
        Assert.Contains(catalog, f => f.Key == "currentPriceEstimate");
        Assert.Contains(catalog, f => f.Key == "currentPriceEstimateLabel");
        Assert.All(catalog, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        Assert.Equal(DynamicOrderProfitEstimateReportRules.AllFieldKeys,
            DynamicOrderProfitEstimateReportRules.NormalizeFields(null));
        Assert.Equal(DynamicOrderProfitEstimateReportRules.AllFieldKeys,
            DynamicOrderProfitEstimateReportRules.NormalizeFields(new List<string>()));
    }

    [Fact]
    public void NormalizeFields_保持请求顺序()
    {
        var keys = DynamicOrderProfitEstimateReportRules.NormalizeFields(
            new[] { "currencyLabel", "orderNo", "salesAmount" });

        Assert.Equal(new[] { "currencyLabel", "orderNo", "salesAmount" }, keys);
    }

    [Fact]
    public void NormalizeFields_未知字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicOrderProfitEstimateReportRules.NormalizeFields(new[] { "orderNo", "notAField" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_重复字段拒绝_大小写不敏感()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicOrderProfitEstimateReportRules.NormalizeFields(new[] { "orderNo", "ORDERNO" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_空键拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicOrderProfitEstimateReportRules.NormalizeFields(new[] { "orderNo", "  " }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_结束早于开始拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicOrderProfitEstimateReportRules.ValidateDateRange(End, Start));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_超过366天拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicOrderProfitEstimateReportRules.ValidateDateRange(
                new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_边界366天通过()
    {
        var (s, e) = DynamicOrderProfitEstimateReportRules.ValidateDateRange(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));
        Assert.Equal(new DateTime(2026, 1, 1), s);
        Assert.Equal(new DateTime(2027, 1, 1), e);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public void ValidatePageBounds_非法页码或页大小拒绝(int page, int pageSize)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicOrderProfitEstimateReportRules.ValidatePageBounds(page, pageSize));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 稳定分页与行投影（纯规则） ====================

    private static ReportDtos.OrderProfitItem Item(long orderId, string orderNo, string currency)
        => new()
        {
            OrderId = orderId,
            CustomerId = 1,
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerName = "客户",
            Currency = currency,
            CurrencyLabel = currency + " 币",
            SalesAmount = 100m,
            SalesAmountLabel = ReportService.OrderProfitSalesAmountLabel,
            CostAmount = null,
            Profit = null,
            ProfitRate = null,
            CostEvidence = ReportService.OrderProfitCostEvidence,
            ProfitEvidence = ReportService.OrderProfitProfitEvidence,
            CurrentPriceEstimate = 60m,
            CurrentPriceEstimateLabel = ReportService.OrderProfitCurrentPriceEstimateLabel,
            CurrentPriceEstimateReason = string.Empty,
        };

    [Fact]
    public void BuildPage_稳定分页与Total_PageOnly元数据()
    {
        var items = new List<ReportDtos.OrderProfitItem>
        {
            Item(1, "SO-1", "USD"),
            Item(2, "SO-2", "CNY"),
            Item(3, "SO-3", "EUR"),
        };

        var page = DynamicOrderProfitEstimateReportRules.BuildPage(
            items, new[] { "orderNo", "currency" }, page: 1, pageSize: 2, Start, End);

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(2, page.TotalPages);
        Assert.True(page.Truncated);
        Assert.Equal(new[] { "orderNo", "currency" }, page.Columns.Select(c => c.Key).ToArray());
        Assert.Equal("SO-1", page.Rows[0]["orderNo"]);
        Assert.Equal("SO-2", page.Rows[1]["orderNo"]);
        Assert.False(string.IsNullOrWhiteSpace(page.PageOnlyText));
    }

    [Fact]
    public void BuildPage_第二页切片_已覆盖全部匹配行_不截断()
    {
        var items = new List<ReportDtos.OrderProfitItem>
        {
            Item(1, "SO-1", "USD"),
            Item(2, "SO-2", "CNY"),
            Item(3, "SO-3", "EUR"),
        };

        var page = DynamicOrderProfitEstimateReportRules.BuildPage(
            items, new[] { "orderNo" }, page: 2, pageSize: 2, Start, End);

        Assert.Single(page.Rows);
        Assert.Equal("SO-3", page.Rows[0]["orderNo"]);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void BuildPage_行投影保留原币与未知成本证据()
    {
        var items = new List<ReportDtos.OrderProfitItem> { Item(1, "SO-USD", "USD") };

        var page = DynamicOrderProfitEstimateReportRules.BuildPage(
            items, new[] { "orderNo", "currency", "costAmount", "profit", "profitRate", "costEvidence" },
            page: 1, pageSize: 20, Start, End);

        var row = Assert.Single(page.Rows);
        Assert.Equal("USD", row["currency"]);
        Assert.Null(row["costAmount"]);
        Assert.Null(row["profit"]);
        Assert.Null(row["profitRate"]);
        Assert.Equal(ReportService.OrderProfitCostEvidence, row["costEvidence"]);
    }

    [Fact]
    public void BuildPage_原币与未知依据上下文_即使对应列被取消选择也始终返回()
    {
        var page = DynamicOrderProfitEstimateReportRules.BuildPage(
            new List<ReportDtos.OrderProfitItem> { Item(1, "SO-1", "USD") },
            new[] { "orderNo" }, page: 1, pageSize: 20, Start, End);

        Assert.False(string.IsNullOrWhiteSpace(page.CurrencyContextText));
        Assert.False(string.IsNullOrWhiteSpace(page.UnknownBasisText));
        Assert.False(string.IsNullOrWhiteSpace(page.SourceLimitText));
        Assert.Contains("绝不跨币种合计", page.CurrencyContextText);
    }

    // ==================== 预览：授权 + 范围 + 投影 + 分页 ====================

    [Fact]
    public async Task 预览_授权后按选定字段顺序投影并分页_保留原币与未知成本证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var product = SeedProduct(db, "P001", 60m);
        var order = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 1500m);
        SeedDetail(db, order.Id, product.Id, 10m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo", "currency", "salesAmount", "costAmount", "currentPriceEstimate" },
            Start = Start,
            End = End
        }));

        Assert.Equal(1, page.Total);
        Assert.Single(page.Rows);
        Assert.Equal(new[] { "orderNo", "currency", "salesAmount", "costAmount", "currentPriceEstimate" },
            page.Columns.Select(c => c.Key).ToArray());

        var row = page.Rows[0];
        Assert.Equal("SO-USD", row["orderNo"]);
        Assert.Equal("USD", row["currency"]);
        Assert.Equal(1500m, row["salesAmount"]);
        Assert.Null(row["costAmount"]);              // 成本未知：null，绝不回落为 0
        Assert.Equal(600m, row["currentPriceEstimate"]); // 当前价估算独立口径：10 × 60
    }

    [Fact]
    public async Task 预览_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 预览_无订单利润菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu", isSystem: true);
        var user = SeedUser(db, "opd-nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 预览_受限制业务员仅见其被分配客户的订单()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Sales", isSystem: false);
        var user = SeedUser(db, "sales-1");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, OrderProfitMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);

        var employee = SeedEmployee(db, "sales-1");
        var mine = SeedCustomer(db, "C-MINE", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C-OTHER", "他人客户");

        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.EUR, 2000m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("SO-MINE", row["orderNo"]);
    }

    [Fact]
    public async Task 预览_空结果_显式给出空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-empty", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);

        var page = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.EmptyText, page.EmptyText);
    }

    [Fact]
    public async Task 只读_预览后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-ro", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1000m);

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
