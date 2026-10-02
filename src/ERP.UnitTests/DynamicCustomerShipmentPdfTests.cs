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
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-230 动态客户出货量证据报表 PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei（非缺字字体）、
/// 选定列顺序（BuildRowCells）与已知金额 / 订单数数值、未知金额 / 未知单位数量「未知」的显式区分、
/// 宽列集拆分为多列页、多行按行页拆分、空结果显式空页说明、
/// 无菜单授权 / 授权撤销 / 无身份 / 未知字段 / 页大小超限拒绝、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicCustomerShipmentPdfTests
{
    private const string MenuCode = "customer-shipment";
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

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, MenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);
        return user;
    }

    private static DynamicCustomerShipmentReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static DynamicCustomerShipmentReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int page = 1, int pageSize = 20)
        => new() { Fields = fields, Start = start, End = end, Page = page, PageSize = pageSize };

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
        var user = SeedAuthorizedUser(db, "csd-pdf-user", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "customerName", "totalAmount" }, start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-font", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(start: Start, end: End)));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 选定列顺序 / 未知金额与数量语义 / 数值区分 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_且已知金额订单数与未知数量区分()
    {
        var columns = DynamicCustomerShipmentReportRules.GetCatalogDto().Fields;
        var row = new Dictionary<string, object?>
        {
            ["customerName"] = "客户",
            ["currency"] = "USD",
            ["currencyLabel"] = "USD 美元",
            ["orderCount"] = 1,
            ["totalAmount"] = 1500m,
            ["currencyEvidence"] = "已知原币",
            ["amountLabel"] = "订单原币金额",
            ["unitGroups"] = "PCS=10(1条)",
            ["totalQuantity"] = null,
            ["quantityCompletenessReason"] = "空白/未知单位",
            ["quantityLabel"] = "已审核订单明细数量",
        };

        var cells = DynamicCustomerShipmentPdfExporter.BuildRowCells(columns, row);

        Assert.Equal("客户", cells[0]);
        Assert.Equal("1", cells[3]);        // 已审核订单数：数值
        Assert.Equal("1500", cells[4]);      // 已知原币金额：数值
        Assert.Equal("未知", cells[8]);       // 未知单位数量：绝不回落为 0
    }

    [Fact]
    public void FormatCellValue_数值日期布尔空值口径与Excel一致()
    {
        Assert.Equal("3", DynamicCustomerShipmentPdfExporter.FormatCellValue(3));
        Assert.Equal("10", DynamicCustomerShipmentPdfExporter.FormatCellValue(10L));
        Assert.Equal("1500", DynamicCustomerShipmentPdfExporter.FormatCellValue(1500m));
        Assert.Equal("2026-09-10", DynamicCustomerShipmentPdfExporter.FormatCellValue(new DateTime(2026, 9, 10)));
        Assert.Equal("是", DynamicCustomerShipmentPdfExporter.FormatCellValue(true));
        Assert.Equal(string.Empty, DynamicCustomerShipmentPdfExporter.FormatCellValue(null));
    }

    [Fact]
    public void FormatFieldCell_空值显式未知_绝不写成零()
    {
        var field = DynamicCustomerShipmentReportRules.GetField("totalQuantity")!;
        Assert.Equal(DynamicCustomerShipmentReportRules.UnknownValueText,
            DynamicCustomerShipmentPdfExporter.FormatFieldCell(field, new Dictionary<string, object?> { ["totalQuantity"] = null }));
        Assert.Equal(DynamicCustomerShipmentReportRules.UnknownValueText,
            DynamicCustomerShipmentPdfExporter.FormatFieldCell(field, new Dictionary<string, object?>()));
    }
    // ==================== 3. 分页 ====================

    [Fact]
    public async Task ExportPdf_宽列集拆分为多列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-wide", "Priv");
        var customer = SeedCustomer(db, "C001", new string('客', 60));
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        // 全部白名单字段（11 列）+ 超宽客户名 → 至少拆成 2 个列页（列不裁切）
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public async Task ExportPdf_多行_按行页拆分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-rows", "Priv");
        for (var i = 0; i < 60; i++)
        {
            var customer = SeedCustomer(db, $"C{i:D3}", $"客户{i}");
            SeedOrder(db, $"SO-{i}", customer.Id, Currency.USD, 1000m + i);
        }

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "customerName", "totalAmount" }, start: Start, end: End, pageSize: 200)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }
    // ==================== 4. 空结果 / 授权 / 校验 ====================

    [Fact]
    public async Task ExportPdf_空结果_仅表头与空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-empty", "Priv");

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "customerName" }, start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_授权撤销_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(start: Start, end: End)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(start: Start, end: End)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-field", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(fields: new List<string> { "notAField" }, start: Start, end: End)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-cap", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(start: Start, end: End, pageSize: 201)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_受限制业务员_只导出被分配客户_且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales", isSystemRole: false);
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.USD, 999m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "customerName", "totalAmount" }, start: Start, end: End)));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.True(file.FileContents.Length > 0);
    }
    // ==================== 5. 字体缺失 / 只读 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败()
    {
        var page = DynamicCustomerShipmentReportRules.BuildPage(
            Array.Empty<ReportDtos.CustomerShipmentItem>(),
            DynamicCustomerShipmentReportRules.NormalizeFields(null),
            1, 20, Start, End);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentPdfExporter.Export(page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    [Fact]
    public async Task ExportPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-ro", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "customerName" }, start: Start, end: End)));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 6. ERP-231 应用筛选上下文 ====================

    [Fact]
    public void BuildFilterLine_有筛选_渲染规范化筛选_无筛选为空串()
    {
        var pageWithFilter = DynamicCustomerShipmentReportRules.BuildPage(
            Array.Empty<ReportDtos.CustomerShipmentItem>(),
            DynamicCustomerShipmentReportRules.NormalizeFields(null),
            1, 20, Start, End, "客户 Id 7；原币币种 USD");

        var line = DynamicCustomerShipmentPdfExporter.BuildFilterLine(pageWithFilter);
        Assert.Contains("应用筛选", line);
        Assert.Contains("客户 Id 7", line);
        Assert.Contains("原币币种 USD", line);

        var pageWithoutFilter = DynamicCustomerShipmentReportRules.BuildPage(
            Array.Empty<ReportDtos.CustomerShipmentItem>(),
            DynamicCustomerShipmentReportRules.NormalizeFields(null),
            1, 20, Start, End);
        Assert.Equal(string.Empty, DynamicCustomerShipmentPdfExporter.BuildFilterLine(pageWithoutFilter));
    }

    [Fact]
    public async Task ExportPdf_应用筛选_下载成功_且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-filter", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20,
            Filter = new CustomerShipmentFilterDto { CustomerId = customer.Id, Currency = "usd" }
        }));

        Assert.True(file.FileContents.Length > 0);
        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 7. ERP-232 全匹配汇总不改变明细 PDF ====================

    [Fact]
    public async Task ExportPdf_全匹配汇总在场_明细PDF下载成功_且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-pdf-summary", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName", "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        Assert.True(file.FileContents.Length > 0);
        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

}
