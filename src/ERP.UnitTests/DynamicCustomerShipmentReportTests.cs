using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// 动态客户出货量证据报表（ERP-229）聚焦单元测试：字段目录 / 字段校验（未知 / 重复 / 顺序）、
/// 日期与分页边界校验、业务员数据范围（撤销授权 / 空客户）、按客户 × 原币稳定分页、未知币种 / 未知单位 null 证据、
/// 服务端派生 Total / TotalPages / PageOnly / 去重客户与订单上下文、只读操作。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicCustomerShipmentReportTests
{
    private const string MenuCode = "customer-shipment";

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

    /// <summary>创建拥有「客户出货量统计表」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(
        ErpDbContext db, long orderId, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = 1,
            ProductName = "商品",
            Quantity = quantity,
            Unit = unit,
            IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static DynamicCustomerShipmentReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicCustomerShipmentReportController(db, new ReportService(db));
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

    private static DynamicCustomerShipmentReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicCustomerShipmentReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 字段目录 / 校验（纯规则） ====================

    [Fact]
    public void 目录_有限白名单_包含客户原币订单数金额与证据字段()
    {
        var catalog = DynamicCustomerShipmentReportRules.GetCatalogDto();

        Assert.Equal("customer-shipment", catalog.RequiredMenuCode);
        Assert.Equal("客户出货量统计表", catalog.RequiredMenuText);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(20, catalog.DefaultPageSize);

        var keys = catalog.Fields.Select(f => f.Key).ToList();
        Assert.Contains("customerName", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("orderCount", keys);
        Assert.Contains("totalAmount", keys);
        Assert.Contains("totalQuantity", keys);
        Assert.Contains("currencyEvidence", keys);
        Assert.Contains("amountLabel", keys);
        Assert.Contains("quantityCompletenessReason", keys);
        Assert.Contains("quantityLabel", keys);
        Assert.Contains("unitGroups", keys);
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        var catalog = DynamicCustomerShipmentReportRules.GetCatalogDto();
        var keys = DynamicCustomerShipmentReportRules.NormalizeFields(null);
        Assert.Equal(catalog.Fields.Select(f => f.Key).ToList(), keys);
    }

    [Fact]
    public void NormalizeFields_保持请求顺序()
    {
        var keys = DynamicCustomerShipmentReportRules.NormalizeFields(
            new List<string> { "currency", "customerName", "orderCount" });
        Assert.Equal(new[] { "currency", "customerName", "orderCount" }, keys);
    }

    [Fact]
    public void NormalizeFields_未知字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentReportRules.NormalizeFields(new List<string> { "customerName", "notAField" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_重复字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentReportRules.NormalizeFields(new List<string> { "customerName", "customerName" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_结束早于开始拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentReportRules.ValidateDateRange(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_超过366天拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentReportRules.ValidateDateRange(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_合法日期窗口通过()
    {
        var (start, end) = DynamicCustomerShipmentReportRules.ValidateDateRange(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        Assert.Equal(new DateTime(2026, 9, 1), start);
        Assert.Equal(new DateTime(2026, 9, 30), end);
    }

    [Fact]
    public void ValidatePageBounds_页码小于1拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentReportRules.ValidatePageBounds(0, 20));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void ValidatePageBounds_每页条数越界拒绝(int pageSize)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentReportRules.ValidatePageBounds(1, pageSize));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void FormatUnitGroups_已知单位签名数量_未知单位显示未知_不重复金额()
    {
        var groups = new List<ReportDtos.CustomerShipmentUnitGroup>
        {
            new() { Unit = "PCS", Quantity = 15m, DetailCount = 2, QuantityLabel = CustomerShipmentEvidenceRules.KnownUnitQuantityLabel },
            new() { Unit = "未知单位", Quantity = null, DetailCount = 1, QuantityLabel = CustomerShipmentEvidenceRules.UnknownUnitQuantityLabel },
        };

        var text = DynamicCustomerShipmentReportRules.FormatUnitGroups(groups);

        Assert.Contains("PCS=15(2条)", text);
        Assert.Contains("未知单位=未知(1条)", text);
        Assert.DoesNotContain("100", text);
        Assert.DoesNotContain("USD", text);
    }

    [Fact]
    public void FormatUnitGroups_空分组返回显式无证据文本()
    {
        Assert.Equal(DynamicCustomerShipmentReportRules.NoUnitGroupsText,
            DynamicCustomerShipmentReportRules.FormatUnitGroups(new List<ReportDtos.CustomerShipmentUnitGroup>()));
        Assert.Equal(DynamicCustomerShipmentReportRules.NoUnitGroupsText,
            DynamicCustomerShipmentReportRules.FormatUnitGroups(null));
    }

    // ==================== 2. 预览：客户 × 原币稳定分页与上下文 ====================

    [Fact]
    public async Task 预览_按客户原币稳定分页_Total与去重客户订单上下文正确()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户A");

        SeedOrder(db, "SO-USD1", customer.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-USD2", customer.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName", "currency", "orderCount", "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 1
        }));

        Assert.Equal(2, page.Total);            // USD 行 + CNY 行
        Assert.Equal(1, page.PageSize);
        Assert.Single(page.Rows);
        Assert.Equal(2, page.TotalPages);
        Assert.True(page.Truncated);
        Assert.True(page.PageOnly);
        Assert.Equal(DynamicCustomerShipmentReportRules.PageOnlyText, page.PageOnlyText);

        Assert.Equal(1, page.Context.UniqueCustomers);
        Assert.Equal(3, page.Context.UniqueOrders);   // 3 张已审核订单
        Assert.Equal(2, page.Context.CustomerCurrencyRows);
        Assert.Contains("已审核销售订单证据", page.Context.EvidenceBasis);

        // 稳定排序：同一客户内按币种编码升序（CNY 在 USD 之前）
        var first = page.Rows[0];
        Assert.Equal("CNY", (string)first["currency"]!);
        Assert.Equal(1, first["orderCount"]);
        Assert.Equal(500m, (decimal)first["totalAmount"]!);
    }

    [Fact]
    public async Task 预览_未知币种金额为null_来源证据非实际出库收款()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        SeedOrder(db, "SO-BAD", customer.Id, (Currency)999, 777m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "currency", "orderCount", "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("未知币种", (string)row["currency"]!);
        Assert.Equal(1, row["orderCount"]);
        Assert.Null(row["totalAmount"]);          // 未知币种金额未知，绝不回落为 0
        Assert.Contains("非实际出库", page.SourceContextText);
        Assert.Contains("非实际收款", page.DisclaimerText);
        Assert.Contains("非实际出库", page.Context.EvidenceBasis);
    }

    [Fact]
    public async Task 预览_选定列顺序_未知单位数量为null_单位分组安全文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        SeedDetail(db, order.Id, "PCS", 10m);
        SeedDetail(db, order.Id, "", 2m);    // 空白单位 → 旧口径数量未知

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "totalQuantity", "unitGroups", "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        Assert.Equal(new[] { "totalQuantity", "unitGroups", "customerName" },
            page.Columns.Select(c => c.Key).ToArray());

        var row = Assert.Single(page.Rows);
        Assert.Null(row["totalQuantity"]);                       // 未知单位 → null
        Assert.Equal("客户", (string)row["customerName"]!);

        var unitText = (string)row["unitGroups"]!;
        Assert.Contains("PCS=10(1条)", unitText);
        Assert.Contains("未知单位=未知(1条)", unitText);
        Assert.DoesNotContain("100", unitText);                  // 绝不重复金额
    }

    [Fact]
    public async Task 预览_空页_TotalPages为0且空页说明显式()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.TotalPages);
        Assert.False(page.Truncated);
        Assert.False(page.PageOnly);
        Assert.Empty(page.Rows);
        Assert.NotEmpty(page.EmptyText);
        Assert.Equal(0, page.Context.UniqueCustomers);
        Assert.Equal(0, page.Context.UniqueOrders);
    }

    // ==================== 3. 身份 / 菜单授权 / 数据范围 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
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
        Assert.IsType<OkObjectResult>(await ctl.Preview(new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 受限制业务员_只看到被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.USD, 999m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName", "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("我的客户", (string)row["customerName"]!);
        Assert.Equal(100m, (decimal)row["totalAmount"]!);
        Assert.Equal(1, page.Context.UniqueCustomers);
        Assert.Equal(1, page.Context.UniqueOrders);
    }

    // ==================== 4. 只读 ====================

    [Fact]
    public async Task 只读_预览后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 5. ERP-231 可选应用筛选 ====================

    [Fact]
    public async Task 预览_应用客户Id筛选_结果与规范化筛选上下文一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedOrder(db, "SO-A", a.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-B", b.Id, Currency.USD, 999m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Filter = new CustomerShipmentFilterDto { CustomerId = a.Id }
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("客户A", (string)row["customerName"]!);
        Assert.Contains("客户 Id " + a.Id, page.FilterText);
    }

    [Fact]
    public async Task 预览_应用原币币种筛选_结果与规范化筛选上下文一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "currency", "totalAmount" },
            Start = Start,
            End = End,
            Filter = new CustomerShipmentFilterDto { Currency = "usd" }
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("USD", (string)row["currency"]!);
        Assert.Equal(100m, (decimal)row["totalAmount"]!);
        Assert.Contains("原币币种 USD", page.FilterText);
    }

    [Fact]
    public async Task 预览_非法筛选_校验先于读取并拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicCustomerShipmentReportRequest
            {
                Start = Start,
                End = End,
                Filter = new CustomerShipmentFilterDto { CustomerId = 0 }
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicCustomerShipmentReportRequest
            {
                Start = Start,
                End = End,
                Filter = new CustomerShipmentFilterDto { Currency = "ABC" }
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    // ==================== 6. ERP-232 全匹配汇总（服务端派生） ====================

    [Fact]
    public async Task 预览_全匹配汇总_按原币签名金额与去重客户订单数_不跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        SeedOrder(db, "SO-USD-A", a.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-USD-B", b.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-CNY-A", a.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "currency", "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 1
        }));

        Assert.NotNull(page.Summary);
        Assert.Single(page.Rows);                   // 当前页仅 1 行

        var usd = Assert.Single(page.Summary.CurrencyRows.Where(r => r.Currency == "USD"));
        Assert.Equal(800m, usd.TotalAmount);        // 1000 + (-200)
        Assert.Equal(2, usd.CustomerCount);         // 去重客户数
        Assert.Equal(2, usd.OrderCount);            // 不相交订单数
        Assert.Equal(CustomerShipmentEvidenceRules.KnownCurrencyEvidence, usd.Evidence);

        var cny = Assert.Single(page.Summary.CurrencyRows.Where(r => r.Currency == "CNY"));
        Assert.Equal(500m, cny.TotalAmount);
        Assert.Equal(1, cny.CustomerCount);

        // 绝不出现跨币种合计
        Assert.DoesNotContain(page.Summary.CurrencyRows, r => r.TotalAmount == 1300m);
        Assert.NotEmpty(page.Summary.CoverageText);
    }

    [Fact]
    public async Task 预览_空来源_全匹配汇总仍可用且与选定列无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string>(),            // 隐藏全部选定列
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        Assert.NotNull(page.Summary);               // 空页上汇总仍可用
        Assert.Empty(page.Summary.CurrencyRows);
        Assert.Empty(page.Summary.UnitRows);
        Assert.Equal(0, page.Summary.IncompleteBucketCount);
        Assert.NotEmpty(page.Summary.CoverageText);
        Assert.NotEmpty(page.EmptyText);            // 空页说明与汇总并存
    }

}
