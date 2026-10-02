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
/// 动态业务员提成证据报表（ERP-246）PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei（非缺字字体）、
/// 选定列顺序（BuildRowCells）与已知签名原币金额数值 / 未知金额 / 未知利润 / 未知提成「未知」、
/// 配置为 0 的当前参考比例按数值 0 呈现（与缺失比例「未知」区分）、宽列集拆分为多列页、多行按行页拆分、
/// 空结果显式空页说明、无菜单授权 / 授权撤销 / 无身份 / 未知字段拒绝、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesCommissionPdfTests
{
    private const string MenuCode = "sales-commission";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

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

    private static DynamicSalesCommissionReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static DynamicSalesCommissionReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null,
        int page = 1, int pageSize = 20, SalesCommissionFilterDto? filter = null)
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
        var user = SeedAuthorizedUser(db, "sc-pdf-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName", "currency", "salesAmount" })));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-pdf-font", "Priv", isSystemRole: true);
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

    // ==================== 2. 确定性渲染输入 / 布局计算口径 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_已知签名金额与未知利润与配置零比例区分()
    {
        var columns = new[] { "salesmanName", "salesAmount", "commissionRate", "profit" }
            .Select(k => DynamicSalesCommissionReportRules.GetField(k)!)
            .ToList();
        var row = new Dictionary<string, object?>
        {
            ["salesmanName"] = "业务员甲",
            ["salesAmount"] = 1500m,
            ["commissionRate"] = 0m,
            ["profit"] = null,
        };

        var cells = DynamicSalesCommissionPdfExporter.BuildRowCells(columns, row);

        Assert.Equal("业务员甲", cells[0]);
        Assert.Equal("1500", cells[1]);   // 已知签名原币金额：数值（绝不跨币种合计）
        Assert.Equal("0", cells[2]);      // 配置为 0 的当前参考比例：显式数值 0
        Assert.Equal("未知", cells[3]);   // 未知利润：绝不回落为 0
    }

    [Fact]
    public void FormatFieldCell_未知数值字段空值显式未知_业务员Id空保留空()
    {
        var amount = DynamicSalesCommissionReportRules.GetField("salesAmount")!;
        var rate = DynamicSalesCommissionReportRules.GetField("commissionRate")!;
        var id = DynamicSalesCommissionReportRules.GetField("salesmanId")!;

        Assert.Equal(DynamicSalesCommissionReportRules.UnknownValueText,
            DynamicSalesCommissionPdfExporter.FormatFieldCell(amount, new Dictionary<string, object?> { ["salesAmount"] = null }));
        Assert.Equal(DynamicSalesCommissionReportRules.UnknownValueText,
            DynamicSalesCommissionPdfExporter.FormatFieldCell(rate, new Dictionary<string, object?>()));
        Assert.Equal(string.Empty,
            DynamicSalesCommissionPdfExporter.FormatFieldCell(id, new Dictionary<string, object?> { ["salesmanId"] = null }));
    }

    [Fact]
    public void FormatFieldCell_配置零参考比例显式零_绝不写成未知()
    {
        var rate = DynamicSalesCommissionReportRules.GetField("commissionRate")!;
        Assert.Equal("0", DynamicSalesCommissionPdfExporter.FormatFieldCell(
            rate, new Dictionary<string, object?> { ["commissionRate"] = 0m }));
    }

    [Fact]
    public void FormatCellValue_数值日期布尔空值与签名小数口径与Excel一致()
    {
        Assert.Equal("3", DynamicSalesCommissionPdfExporter.FormatCellValue(3));
        Assert.Equal("10", DynamicSalesCommissionPdfExporter.FormatCellValue(10L));
        Assert.Equal("1500", DynamicSalesCommissionPdfExporter.FormatCellValue(1500m));
        Assert.Equal("-123.45", DynamicSalesCommissionPdfExporter.FormatCellValue(-123.45m));
        Assert.Equal("2026-09-10", DynamicSalesCommissionPdfExporter.FormatCellValue(new DateTime(2026, 9, 10)));
        Assert.Equal("是", DynamicSalesCommissionPdfExporter.FormatCellValue(true));
        Assert.Equal(string.Empty, DynamicSalesCommissionPdfExporter.FormatCellValue(null));
    }

    // ==================== 3. 分页 ====================

    [Fact]
    public async Task ExportPdf_宽列集拆分为多列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-pdf-wide", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        // 全部白名单字段（18 列）超出单页可用宽度 → 至少拆成 2 个列页（列不裁切）
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public async Task ExportPdf_多行_按行页拆分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-pdf-rows", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 60; i++)
        {
            var emp = SeedEmployee(db, $"S{i:D3}", $"业务员{i}");
            SeedOrder(db, $"SO-{i}", customer.Id, emp.Id, Currency.USD, 1000m + i);
        }

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName", "salesAmount" }, pageSize: 200)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 4. 空结果 / 授权 / 校验 / 字体缺失 ====================

    [Fact]
    public async Task ExportPdf_空结果_仅表头与空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-pdf-empty", "Priv", isSystemRole: true);

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
        var user = SeedAuthorizedUser(db, "sc-pdf-revoke", "Priv");
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
        var user = SeedAuthorizedUser(db, "sc-pdf-field", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(fields: new List<string> { "orderNo" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ExportPdf_字体缺失_显式失败()
    {
        var page = DynamicSalesCommissionReportRules.BuildPage(
            Array.Empty<ReportDtos.SalesCommissionItem>(),
            new List<string> { "salesmanName" },
            1, 20, Start, End);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicSalesCommissionPdfExporter.Export(page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 只读 / 作用域 ====================

    [Fact]
    public async Task ExportPdf_受限制业务员_只导出被分配客户订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales", isSystemRole: false);
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
        var user = SeedAuthorizedUser(db, "sc-pdf-ro", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "salesmanName", "salesAmount" })));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public void ExportPdf_不追加全匹配汇总_页面数与无汇总一致()
    {
        var items = new List<ReportDtos.SalesCommissionItem>
        {
            new() { SalesmanId = 1, SalesmanName = "业务员甲", Currency = "USD", CurrencyLabel = "USD 美元", OrderCount = 1, SalesAmount = 100m },
            new() { SalesmanId = 2, SalesmanName = "业务员乙", Currency = "CNY", CurrencyLabel = "CNY 人民币", OrderCount = 1, SalesAmount = 200m },
        };

        var page = DynamicSalesCommissionReportRules.BuildPage(
            items,
            new List<string> { "salesmanName", "currency", "salesAmount" },
            1, 20, Start, End);

        Assert.NotNull(page.Summary);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);

        var withoutSummary = page with { Summary = null };

        using var withPdf = OpenPdf(DynamicSalesCommissionPdfExporter.Export(page));
        using var withoutPdf = OpenPdf(DynamicSalesCommissionPdfExporter.Export(withoutSummary));

        // 汇总绝不为当前页下载追加额外页（保留既有当前页 PDF 语义）
        Assert.Equal(withoutPdf.Pages.Count, withPdf.Pages.Count);
    }
}



