using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-211 动态报价成交率报表「分币种汇总 PDF」单元测试（只读、有界、作用域化）。
/// 覆盖：专用汇总 PDF 端点复用已授权预览管线（身份 / 报价单菜单 / 业务员数据范围 / 字段 / 日期 / 分页 / 应用筛选）、
/// 仅导出选定汇总指标（选定顺序、未知币种独立分桶、无跨币种金额合计）、字体缺失显式失败、
/// 空汇总显式说明，以及无效 / 授权撤销 / 来源超限时不返回任何文件。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class QuotationConversionCurrencySummaryPdfTests
{
    private const string QuotationMenuCode = "quotation";
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

    private static SysRoleMenu SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        var roleMenu = new SysRoleMenu { RoleId = roleId, MenuId = menuId };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();
        return roleMenu;
    }

    private static Quotation SeedQuotation(
        ErpDbContext db, string no, string salesmanName, Currency currency, decimal totalAmount)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = Start.AddDays(1),
            CustomerId = 1L,
            CustomerName = "报价成交率汇总 PDF 客户",
            SalesmanName = salesmanName,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static void SeedManyQuotations(ErpDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.Quotations.Add(new Quotation
            {
                QuotationNo = $"QT-{i}",
                QuotationDate = Start.AddDays(1),
                CustomerId = 1L,
                CustomerName = "报价成交率汇总 PDF 超限客户",
                SalesmanName = "张三",
                Currency = Currency.USD,
                TotalAmount = 100m,
                Status = DocumentStatus.Approved
            });
        }
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, QuotationMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);
        return user;
    }

    private static DynamicQuotationConversionReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

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

    private static DynamicQuotationConversionReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicQuotationConversionReportPageDto>>(ok.Value);
        return resp.Data!;
    }


    // ==================== 1. 汇总导出器：选定顺序 / 金额格式化 / 字体缺失 ====================

    [Fact]
    public void BuildRowCells_按选定汇总列顺序映射_未知币种独立_金额与计数格式化()
    {
        var columns = new List<DynamicQuotationConversionReportFieldDto>
        {
            new("currency", "原币币种", "text", false),
            new("quotationCount", "有效报价数", "number", false),
            new("totalAmount", "有效报价金额(原币)", "number", false),
            new("conversionRate", "成交率%", "number", false),
        };
        var row = new DynamicQuotationConversionCurrencySummaryDto
        {
            Currency = "未知币种",
            QuotationCount = 3,
            TotalAmount = 1500.5m,
            ConversionRate = 66.67m,
        };

        var cells = QuotationConversionCurrencySummaryPdfExporter.BuildRowCells(columns, row);

        // 选定顺序 + 未知币种显式保留 + 原币金额 / 计数格式化；未选金额 / 明细字段绝不出现
        Assert.Equal(new[] { "未知币种", "3", "1500.5", "66.67" }, cells);
    }

    [Fact]
    public void FormatCellValue_数值日期布尔空值按口径格式化()
    {
        Assert.Equal("3", QuotationConversionCurrencySummaryPdfExporter.FormatCellValue(3));
        Assert.Equal("1500.5", QuotationConversionCurrencySummaryPdfExporter.FormatCellValue(1500.5m));
        Assert.Equal("2026-09-15", QuotationConversionCurrencySummaryPdfExporter.FormatCellValue(new DateTime(2026, 9, 15)));
        Assert.Equal("是", QuotationConversionCurrencySummaryPdfExporter.FormatCellValue(true));
        Assert.Equal("否", QuotationConversionCurrencySummaryPdfExporter.FormatCellValue(false));
        Assert.Equal(string.Empty, QuotationConversionCurrencySummaryPdfExporter.FormatCellValue(null));
    }

    [Fact]
    public void Export_字体缺失_显式失败_不产出PDF()
    {
        var summary = new DynamicQuotationConversionSummaryDto(
            new List<DynamicQuotationConversionReportFieldDto> { new("currency", "原币币种", "text", false) },
            new List<DynamicQuotationConversionCurrencySummaryDto>(),
            0,
            DynamicQuotationConversionReportRules.CurrencySummaryCoverageText);
        var page = new DynamicQuotationConversionReportPageDto(
            new List<DynamicQuotationConversionReportFieldDto>(),
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0, false,
            DynamicQuotationConversionReportRules.EmptyText,
            "只读", "边界", "免责",
            Start, End);

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationConversionCurrencySummaryPdfExporter.Export(summary, page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }


    // ==================== 2. 汇总 PDF 端点：成功导出 ====================

    [Fact]
    public async Task ExportSummaryPdf_导出分币种汇总_返回PDF签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-pdf-sign", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "quotationCount", "totalAmount" },
            Start = Start,
            End = End
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportSummaryPdf_仅选定汇总指标_未知币种独立_无跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-pdf-typed", "Priv");
        SeedQuotation(db, "QT-USD1", "张三", Currency.USD, 1000m);
        SeedQuotation(db, "QT-USD2", "李四", Currency.USD, 500m);
        SeedQuotation(db, "QT-EUR", "王五", Currency.EUR, 2000m);
        SeedQuotation(db, "QT-UNK", "赵六", (Currency)999, 300m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var request = new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "quotationCount", "totalAmount" },
            Start = Start,
            End = End
        };

        var page = OkPage(await ctl.Preview(request));
        var summary = page.Summary!;

        // 仅「币种 + 选定汇总指标」列，未选金额（convertedAmount）与明细字段（salesmanName）绝不出现
        Assert.Equal(new[] { "currency", "quotationCount", "totalAmount" }, summary.Columns.Select(c => c.Key).ToArray());
        Assert.DoesNotContain(summary.Columns, c => c.Key is "convertedAmount" or "salesmanName");

        // 3 个币种桶（EUR / USD / 未知币种），无混合币种合计行
        Assert.Equal(3, summary.CurrencyCount);
        Assert.Equal(new[] { "EUR", "USD", "未知币种" }, summary.Rows.Select(r => r.Currency).ToArray());
        Assert.DoesNotContain(summary.Rows, r => string.IsNullOrWhiteSpace(r.Currency));

        var usd = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(2, usd.QuotationCount);
        Assert.Equal(1500m, usd.TotalAmount);
        Assert.Null(usd.ConvertedAmount);

        var file = PdfOk(await ctl.ExportSummaryPdf(request));
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task ExportSummaryPdf_空汇总_生成带空提示的PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-pdf-empty", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "quotationCount" },
            Start = Start,
            End = End
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 3. 拒绝路径（不返回文件） ====================

    [Fact]
    public async Task ExportSummaryPdf_授权撤销_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-pdf-revoke", "Priv");
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_来源超限_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-pdf-overflow", "Priv");
        SeedManyQuotations(db, 2001);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }
}

