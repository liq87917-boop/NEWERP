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
/// 动态商品销量排名报表（ERP-213）聚焦单元测试：发货数量证据字段目录 / 字段校验（未知 / 重复 / 空 / 顺序）、
/// 日期与 Top 边界校验、业务员数据范围（撤销授权 / 空客户 / 受限制业务员）、Top 限定标注、空结果、只读操作。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicProductSalesRankingReportTests
{
    private const string MenuCode = "product-sales-ranking";

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

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status, bool deleted = false)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = "大",
            Unit = unit,
            Quantity = quantity,
            IsDeleted = deleted
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    /// <summary>创建拥有「商品销量排名榜」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static DynamicProductSalesRankingReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicProductSalesRankingReportController(db, new ReportService(db));
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

    private static DynamicProductSalesRankingReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicProductSalesRankingReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicProductSalesRankingReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10)
        => new() { Fields = fields, Start = start, End = end, Top = top };

    // ==================== 1. 目录与字段校验（纯规则） ====================

    [Fact]
    public void 目录_有限白名单_仅发货数量证据_排除金额估算()
    {
        var catalog = DynamicProductSalesRankingReportRules.GetCatalog();

        Assert.Equal(DynamicProductSalesRankingReportRules.AllFieldKeys.Count, catalog.Count);
        Assert.Equal(7, catalog.Count);
        Assert.Contains(catalog, f => f.Key == "rank");
        Assert.Contains(catalog, f => f.Key == "productId");
        Assert.Contains(catalog, f => f.Key == "productCode");
        Assert.Contains(catalog, f => f.Key == "productName");
        Assert.Contains(catalog, f => f.Key == "spec");
        Assert.Contains(catalog, f => f.Key == "unit");
        Assert.Contains(catalog, f => f.Key == "totalQuantity");
        Assert.DoesNotContain(catalog, f => f.Key == "totalAmount");
        Assert.DoesNotContain(catalog, f => f.Key == "amountLabel");
        Assert.All(catalog, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        var keys = DynamicProductSalesRankingReportRules.NormalizeFields(null);
        Assert.Equal(DynamicProductSalesRankingReportRules.AllFieldKeys, keys);
    }

    [Fact]
    public void NormalizeFields_保持请求顺序()
    {
        var keys = DynamicProductSalesRankingReportRules.NormalizeFields(
            new[] { "rank", "productName", "totalQuantity", "unit" });
        Assert.Equal(new[] { "rank", "productName", "totalQuantity", "unit" }, keys);
    }

    [Fact]
    public void NormalizeFields_未知字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicProductSalesRankingReportRules.NormalizeFields(new[] { "rank", "notAField" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_重复字段拒绝_大小写不敏感()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicProductSalesRankingReportRules.NormalizeFields(new[] { "rank", "RANK" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_空键拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicProductSalesRankingReportRules.NormalizeFields(new[] { "rank", "  " }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 日期 / Top 边界（先于源读取） ====================

    [Fact]
    public void ValidateDateRange_结束早于开始_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicProductSalesRankingReportRules.ValidateDateRange(new DateTime(2026, 9, 10), new DateTime(2026, 9, 1)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_超过366天_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicProductSalesRankingReportRules.ValidateDateRange(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_合法_返回日期部分()
    {
        var (start, end) = DynamicProductSalesRankingReportRules.ValidateDateRange(
            new DateTime(2026, 9, 1, 12, 30, 0), new DateTime(2026, 9, 30));
        Assert.Equal(new DateTime(2026, 9, 1), start);
        Assert.Equal(new DateTime(2026, 9, 30), end);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void ValidateTop_越界_拒绝(int top)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicProductSalesRankingReportRules.ValidateTop(top));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void ValidateTop_边界_通过(int top)
        => Assert.Equal(top, DynamicProductSalesRankingReportRules.ValidateTop(top));

    // ==================== 3. 行投影与 Top 限定（纯规则） ====================

    [Fact]
    public void BuildPage_保持选定顺序_TopLimited_不泄漏金额字段()
    {
        var items = new List<ReportDtos.ProductSalesRankItem>
        {
            new() { Rank = 1, ProductId = 1, ProductCode = "P001", ProductName = "热销商品", Spec = "大", Unit = "PCS", TotalQuantity = 100m, TotalAmount = 9999m, AmountLabel = "金额" },
            new() { Rank = 2, ProductId = 2, ProductCode = "P002", ProductName = "平销商品", Spec = "中", Unit = "BOX", TotalQuantity = 50m, TotalAmount = 8888m, AmountLabel = "金额" },
        };
        var keys = new[] { "rank", "productName", "totalQuantity", "unit" };

        var page = DynamicProductSalesRankingReportRules.BuildPage(items, keys, top: 2, start: Start, end: End);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Top);
        Assert.True(page.TopLimited);
        Assert.Equal(new[] { "rank", "productName", "totalQuantity", "unit" },
            page.Columns.Select(c => c.Key).ToArray());
        Assert.Equal(1, page.Rows[0]["rank"]);
        Assert.Equal("热销商品", page.Rows[0]["productName"]);
        Assert.Equal(100m, page.Rows[0]["totalQuantity"]);
        Assert.All(page.Rows, r =>
        {
            Assert.False(r.ContainsKey("totalAmount"));
            Assert.False(r.ContainsKey("amountLabel"));
        });
    }

    [Fact]
    public void BuildPage_不足Top_TopLimited为假_空结果带空文案()
    {
        var keys = new[] { "rank" };
        var page = DynamicProductSalesRankingReportRules.BuildPage(
            new List<ReportDtos.ProductSalesRankItem>(), keys, top: 10, start: Start, end: End);

        Assert.Equal(0, page.Total);
        Assert.False(page.TopLimited);
        Assert.Equal(DynamicProductSalesRankingReportRules.EmptyText, page.EmptyText);
    }

    // ==================== 4. 身份与菜单授权 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
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
        Assert.IsType<OkObjectResult>(await ctl.Preview(Request()));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 5. 业务员数据范围 ====================

    [Fact]
    public async Task 受限制业务员_只看到被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        var mineOut = SeedStockOut(db, "OUT-MINE", mine.Id, DocumentStatus.Approved);
        var otherOut = SeedStockOut(db, "OUT-OTHER", other.Id, DocumentStatus.Approved);
        SeedDetail(db, mineOut.Id, 1, "热销商品", "PCS", 100m);
        SeedDetail(db, otherOut.Id, 2, "别家商品", "PCS", 999m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity", "unit" },
            start: Start, end: End)));

        var row = Assert.Single(page.Rows);
        Assert.Equal("热销商品", row["productName"]);
        Assert.Equal(100m, row["totalQuantity"]);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(start: Start, end: End)));

        Assert.Empty(page.Rows);
        Assert.Equal(DynamicProductSalesRankingReportRules.EmptyText, page.EmptyText);
    }

    // ==================== 6. 只读 ====================

    [Fact]
    public async Task 预览_只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkPage(await ctl.Preview(Request(start: Start, end: End)));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

