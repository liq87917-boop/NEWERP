using System.Security.Claims;
using System.Text;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-215 动态商品销量排名可选客户 / 商品 / 单位筛选（控制器 + 规则，只读）聚焦单元测试。
/// 覆盖：筛选校验与规范化（非法 Id / 超长单位 / 控制字符 / 全部留空 / 去首尾空白）、规范化筛选上下文、
/// 客户筛选在 Top 之前应用、范围外客户不泄露名称与行、授权撤销、Excel 上下文工作表写入筛选、PDF 筛选请求成功。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicProductSalesRankingFilterTests
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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
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

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = false
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string unit, decimal quantity)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = "大",
            Unit = unit,
            Quantity = quantity,
            IsDeleted = false
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicProductSalesRankingReportRules.RequiredMenuCode).Id);
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
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10,
        ProductSalesRankingFilterDto? filter = null)
        => new() { Fields = fields, Start = start, End = end, Top = top, Filter = filter };

    // ==================== 1. 规则：筛选校验与规范化 ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void NormalizeFilter_客户Id非正整数_拒绝(long customerId)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicProductSalesRankingReportRules.NormalizeFilter(new ProductSalesRankingFilterDto { CustomerId = customerId }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-2L)]
    public void NormalizeFilter_商品Id非正整数_拒绝(long productId)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicProductSalesRankingReportRules.NormalizeFilter(new ProductSalesRankingFilterDto { ProductId = productId }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_单位超长_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicProductSalesRankingReportRules.NormalizeFilter(new ProductSalesRankingFilterDto { Unit = new string('A', 31) }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_单位含控制字符_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicProductSalesRankingReportRules.NormalizeFilter(new ProductSalesRankingFilterDto { Unit = "PCS\u0000" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_全部留空_返回null_保持既有行为()
    {
        Assert.Null(DynamicProductSalesRankingReportRules.NormalizeFilter(new ProductSalesRankingFilterDto()));
        Assert.Null(DynamicProductSalesRankingReportRules.NormalizeFilter(null));
    }

    [Fact]
    public void NormalizeFilter_合法_规范化单位去首尾空白()
    {
        var filter = DynamicProductSalesRankingReportRules.NormalizeFilter(
            new ProductSalesRankingFilterDto { CustomerId = 3, ProductId = 5, Unit = "  PCS  " });

        Assert.NotNull(filter);
        Assert.Equal(3L, filter.CustomerId);
        Assert.Equal(5L, filter.ProductId);
        Assert.Equal("PCS", filter.Unit);
    }

    [Fact]
    public void BuildFilterContext_显示规范化上下文()
    {
        var text = DynamicProductSalesRankingReportRules.BuildFilterContext(
            new ProductSalesRankingFilterDto { CustomerId = 3, ProductId = 5, Unit = "PCS" });

        Assert.Contains("客户 Id 3", text);
        Assert.Contains("商品 Id 5", text);
        Assert.Contains("单位 PCS", text);
    }

    // ==================== 2. 控制器：客户 / 商品 / 单位筛选在 Top 之前应用 ====================

    [Fact]
    public async Task 预览_客户筛选在Top前_只返回指定客户商品()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        var o1 = SeedStockOut(db, "OUT-1", c1.Id, DocumentStatus.Approved);
        var o2 = SeedStockOut(db, "OUT-2", c2.Id, DocumentStatus.Approved);
        SeedDetail(db, o1.Id, 1, "商品一", "PCS", 100m);
        SeedDetail(db, o2.Id, 2, "商品二", "PCS", 999m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity" },
            start: Start, end: End, top: 1,
            filter: new ProductSalesRankingFilterDto { CustomerId = c1.Id })));

        var row = Assert.Single(page.Rows);
        Assert.Equal("商品一", row["productName"]);
        Assert.Contains("客户 Id " + c1.Id, page.FilterText);
    }

    [Fact]
    public async Task 预览_客户筛选超出业务员范围_空且不泄露名称()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        var o1 = SeedStockOut(db, "OUT-MINE", mine.Id, DocumentStatus.Approved);
        var o2 = SeedStockOut(db, "OUT-OTHER", other.Id, DocumentStatus.Approved);
        SeedDetail(db, o1.Id, 1, "商品一", "PCS", 100m);
        SeedDetail(db, o2.Id, 2, "商品二", "PCS", 999m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity" },
            start: Start, end: End,
            filter: new ProductSalesRankingFilterDto { CustomerId = other.Id })));

        Assert.Empty(page.Rows);
        Assert.DoesNotContain("别人的客户", page.FilterText);
        Assert.Contains("客户 Id " + other.Id, page.FilterText);
    }

    [Fact]
    public async Task 预览_商品筛选在Top前_只返回指定商品()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "低排名商品", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "高排名商品", "PCS", 999m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity" },
            start: Start, end: End, top: 1,
            filter: new ProductSalesRankingFilterDto { ProductId = 1 })));

        var row = Assert.Single(page.Rows);
        Assert.Equal("低排名商品", row["productName"]);
    }

    [Fact]
    public async Task 预览_单位筛选在Top前_只返回指定单位()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品一", "CTN", 999m);
        SeedDetail(db, out1.Id, 1, "商品一", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity", "unit" },
            start: Start, end: End, top: 1,
            filter: new ProductSalesRankingFilterDto { Unit = "PCS" })));

        var row = Assert.Single(page.Rows);
        Assert.Equal("PCS", row["unit"]);
        Assert.Equal(100m, row["totalQuantity"]);
    }

    // ==================== 3. 授权撤销 ====================

    [Fact]
    public async Task 预览_授权撤销_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var ctl = BuildController(db, user.Id);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request(
            start: Start, end: End,
            filter: new ProductSalesRankingFilterDto { CustomerId = 1 })));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 4. Excel / PDF 保留筛选上下文 ====================

    [Fact]
    public async Task 导出Excel_筛选上下文_写入上下文工作表()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品一", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        var file = Assert.IsType<FileContentResult>(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity" },
            start: Start, end: End,
            filter: new ProductSalesRankingFilterDto { CustomerId = customer.Id, Unit = "PCS" })));

        using var ms = new MemoryStream(file.FileContents);
        using var workbook = new XSSFWorkbook(ms);
        var context = workbook.GetSheetAt(1);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextFilterLabel, context.GetRow(6).GetCell(0).StringCellValue);
        Assert.Contains("客户 Id " + customer.Id, context.GetRow(6).GetCell(1).StringCellValue);
        Assert.Contains("单位 PCS", context.GetRow(6).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task 导出PDF_筛选请求_返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品一", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        var file = Assert.IsType<FileContentResult>(await ctl.ExportPdf(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity" },
            start: Start, end: End,
            filter: new ProductSalesRankingFilterDto { ProductId = 1 })));

        Assert.Equal("application/pdf", file.ContentType);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
    }
}
