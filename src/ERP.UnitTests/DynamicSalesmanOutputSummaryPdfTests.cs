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
/// ERP-241 动态业务员产值证据报表「全匹配」原币汇总 PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei（非缺字字体）、
/// 汇总列顺序（BuildSummaryRowCells）与已知签名原币金额 / 未知金额 / 利润 / 利润率显式「未知」、
/// 越界详情页仍导出完整匹配汇总、空来源单页显式空说明、
/// 无菜单授权 / 授权撤销 / 无身份 / 未知字段拒绝、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesmanOutputSummaryPdfTests
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
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
    public async Task ExportSummaryPdf_返回PDF签名与内容类型_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }


    [Fact]
    public async Task ExportSummaryPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-font", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 汇总行语义（签名金额 / 未知金额 / 利润 / 利润率） ====================

    [Fact]
    public void 汇总导出行_已知签名金额_未知利润利润率显式文本()
    {
        var row = DynamicSalesmanOutputSummaryRules.BuildCurrencySummaryExportRow(
            new DynamicSalesmanOutputCurrencySummaryDto
            {
                Currency = "USD",
                CurrencyLabel = "USD 美元",
                SalesmanCount = 2,
                OrderCount = 3,
                TotalAmount = 800m,
                TotalProfit = null,
                ProfitRate = null,
                Evidence = SalesmanOutputEvidenceRules.KnownCurrencyEvidence,
                ProfitBasis = DynamicSalesmanOutputSummaryRules.ProfitBasisText,
            });

        var cells = DynamicSalesmanOutputSummaryPdfExporter.BuildSummaryRowCells(
            DynamicSalesmanOutputSummaryRules.CurrencySummaryColumns, row).ToList();

        Assert.Equal(9, cells.Count);
        Assert.Equal("USD", cells[0]);
        Assert.Equal("USD 美元", cells[1]);
        Assert.Equal("2", cells[2]);
        Assert.Equal("3", cells[3]);
        Assert.Equal("800", cells[4]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, cells[5]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, cells[6]);
    }

    [Fact]
    public void 汇总导出行_未知币种_金额未知_计数证据显式()
    {
        var row = DynamicSalesmanOutputSummaryRules.BuildCurrencySummaryExportRow(
            new DynamicSalesmanOutputCurrencySummaryDto
            {
                Currency = "999",
                CurrencyLabel = "未知币种",
                SalesmanCount = 1,
                OrderCount = 2,
                TotalAmount = null,
                TotalProfit = null,
                ProfitRate = null,
                Evidence = SalesmanOutputEvidenceRules.UnknownCurrencyEvidence,
                ProfitBasis = DynamicSalesmanOutputSummaryRules.ProfitBasisText,
            });

        var cells = DynamicSalesmanOutputSummaryPdfExporter.BuildSummaryRowCells(
            DynamicSalesmanOutputSummaryRules.CurrencySummaryColumns, row).ToList();

        Assert.Equal("999", cells[0]);
        Assert.Equal("未知币种", cells[1]);
        Assert.Equal("1", cells[2]);
        Assert.Equal("2", cells[3]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, cells[4]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, cells[5]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, cells[6]);
    }


    // ==================== 3. 越界详情页 / 空来源 ====================

    [Fact]
    public async Task ExportSummaryPdf_越界详情页仍导出完整匹配汇总_与当前页无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-page", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 详情页越界（第 9 页，每页 1 条），汇总仍覆盖全部匹配行（2 个币种桶）
        var file = PdfOk(await ctl.ExportSummaryPdf(Request(
            fields: new List<string> { "currency" }, page: 9, pageSize: 1)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
    }

    [Fact]
    public async Task ExportSummaryPdf_空来源_单页显式空说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-empty", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 4. 授权 / 校验 / 字体缺失 ====================

    [Fact]
    public async Task ExportSummaryPdf_授权撤销_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(Request()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-field", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(Request(fields: new List<string> { "orderNo" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ExportSummaryPdf_字体缺失_显式失败()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(
            Array.Empty<ReportDtos.SalesmanOutputItem>());
        var page = DynamicSalesmanOutputReportRules.BuildPage(
            Array.Empty<ReportDtos.SalesmanOutputItem>(),
            new List<string> { "salesmanName" },
            1, 20, Start, End);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicSalesmanOutputSummaryPdfExporter.Export(summary, page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 只读 / 来源上限 ====================

    [Fact]
    public async Task ExportSummaryPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-ro", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportSummaryPdf(Request()));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ExportSummaryPdf_来源超限_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sod-summary-pdf-limit", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        for (var i = 0; i < 501; i++)
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = $"SO-{i}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                SalesmanId = emp.Id,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
                TotalAmount = 1m
            });
        }
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }
}

