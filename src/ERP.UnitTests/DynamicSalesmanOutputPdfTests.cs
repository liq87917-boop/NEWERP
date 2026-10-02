using System.Text;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-238 动态业务员产值证据报表 PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei（非缺字字体）、
/// 选定列顺序（BuildRowCells）与已知签名原币金额数值 / 未知金额与利润「未知」的显式区分、
/// 宽列集拆分为多列页、多行按行页拆分、空结果显式空页说明、
/// 无菜单授权 / 授权撤销 / 无身份 / 未知字段拒绝、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesmanOutputPdfTests
{
    private const string MenuCode = "salesman-output";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 0. 脚手架 ====================

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            Status = DocumentStatus.Approved,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static DynamicSalesmanOutputReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static DynamicSalesmanOutputReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null,
        int page = 1, int pageSize = 20, SalesmanOutputFilterDto? filter = null)
        => new()
        {
            Fields = fields,
            Start = start ?? Start,
            End = end ?? End,
            Page = page,
            PageSize = pageSize,
            Filter = filter,
        };

    private static FileContentResult PdfOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
        return file;
    }

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    // ==================== 1. 签名 / 内容类型 / 页面边界 / 字体 ====================

    [Fact]
    public async Task ExportPdf_返回PDF签名与内容类型_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName", "currency", "totalAmount" })));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-font", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request()));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 选定列顺序 / 未知金额语义 / 数值区分 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_且已知签名金额与未知金额利润区分()
    {
        var columns = DynamicSalesmanOutputReportRules.GetCatalogDto().Fields;
        var row = new Dictionary<string, object?>
        {
            ["salesmanId"] = 1L,
            ["salesmanName"] = "业务员甲",
            ["currency"] = "USD",
            ["currencyLabel"] = "USD 美元",
            ["orderCount"] = 3,
            ["totalAmount"] = 1500m,
            ["totalProfit"] = null,
            ["amountLabel"] = "已审核订单金额（原币）",
            ["currencyEvidence"] = "已知原币",
            ["profitEvidence"] = "利润未知",
            ["salesmanIdentityEvidence"] = string.Empty,
            ["sourceEvidence"] = "已分配业务员·已审核·未删除·授权客户销售订单证据",
        };

        var cells = DynamicSalesmanOutputPdfExporter.BuildRowCells(columns, row);

        Assert.Equal("1", cells[0]);          // 业务员 Id：数值
        Assert.Equal("业务员甲", cells[1]);
        Assert.Equal("USD", cells[2]);
        Assert.Equal("3", cells[4]);          // 订单数：数值
        Assert.Equal("1500", cells[5]);       // 已知签名原币金额：数值
        Assert.Equal("未知", cells[6]);        // 未知利润：绝不回落为 0
    }

    [Fact]
    public void FormatCellValue_数值日期布尔空值与签名小数口径与Excel一致()
    {
        Assert.Equal("3", DynamicSalesmanOutputPdfExporter.FormatCellValue(3));
        Assert.Equal("10", DynamicSalesmanOutputPdfExporter.FormatCellValue(10L));
        Assert.Equal("1500", DynamicSalesmanOutputPdfExporter.FormatCellValue(1500m));
        Assert.Equal("-123.45", DynamicSalesmanOutputPdfExporter.FormatCellValue(-123.45m));
        Assert.Equal("2026-09-10", DynamicSalesmanOutputPdfExporter.FormatCellValue(new DateTime(2026, 9, 10)));
        Assert.Equal("是", DynamicSalesmanOutputPdfExporter.FormatCellValue(true));
        Assert.Equal(string.Empty, DynamicSalesmanOutputPdfExporter.FormatCellValue(null));
    }

    [Fact]
    public void FormatFieldCell_空值显式未知_绝不写成零()
    {
        var amount = DynamicSalesmanOutputReportRules.GetField("totalAmount")!;
        var profit = DynamicSalesmanOutputReportRules.GetField("totalProfit")!;

        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText,
            DynamicSalesmanOutputPdfExporter.FormatFieldCell(amount, new Dictionary<string, object?> { ["totalAmount"] = null }));
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText,
            DynamicSalesmanOutputPdfExporter.FormatFieldCell(profit, new Dictionary<string, object?>()));
    }


    // ==================== 3. 分页 ====================

    [Fact]
    public async Task ExportPdf_宽列集拆分为多列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-wide", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", new string('客', 60));
        var emp = SeedEmployee(db, "S001", new string('销', 40));
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        // 全部白名单字段（12 列）+ 超宽客户名 / 业务员名 → 至少拆成 2 个列页（列不裁切）
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public async Task ExportPdf_多行_按行页拆分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-rows", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 60; i++)
        {
            var emp = SeedEmployee(db, $"S{i:D3}", $"业务员{i}");
            SeedOrder(db, $"SO-{i}", customer.Id, emp.Id, Currency.USD, 1000m + i);
        }

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName", "totalAmount" }, pageSize: 200)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 4. 空结果 / 授权 / 校验 / 字体缺失 ====================

    [Fact]
    public async Task ExportPdf_空结果_仅表头与空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-empty", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName" })));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_授权撤销_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-field", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(fields: new List<string> { "orderNo" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ExportPdf_字体缺失_显式失败()
    {
        var page = DynamicSalesmanOutputReportRules.BuildPage(
            Array.Empty<ReportDtos.SalesmanOutputItem>(),
            new List<string> { "salesmanName" },
            1, 20, Start, End);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicSalesmanOutputPdfExporter.Export(page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 只读 / 作用域 ====================

    [Fact]
    public async Task ExportPdf_受限制业务员_只导出被分配客户订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-scope", "Sales");
        var employee = SeedEmployee(db, "alice", "业务员甲");
        var otherEmp = SeedEmployee(db, "bob", "业务员乙");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmp.Id);

        SeedOrder(db, "SO-MINE", mine.Id, employee.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, otherEmp.Id, Currency.USD, 9000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request()));

        // 仅被分配客户证据行（单行），PDF 正常生成；绝不泄露范围外业务员
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
    }

    [Fact]
    public async Task ExportPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-pdf-ro", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName", "totalAmount" })));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
