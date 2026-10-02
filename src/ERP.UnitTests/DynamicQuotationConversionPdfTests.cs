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
/// ERP-207 动态报价成交率报表 PDF 导出（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（行列页边界）、选定列顺序（BuildRowCells）与金额 / 计数格式化、
/// 嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、宽列集拆分为多列页、多行按行页拆分、
/// 无身份 / 无菜单授权 / 授权撤销 / 页大小超限 / 未知字段拒绝、空页、多币种分桶绝不跨币种合计，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicQuotationConversionPdfTests
{
    private const string QuotationMenuCode = "quotation";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 0. 测试脚手架 ====================

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
            CustomerName = "报价成交率导出客户",
            SalesmanName = salesmanName,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
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

    // ==================== 1. 签名 / 内容类型 / 页面边界 ====================

    [Fact]
    public async Task ExportPdf_导出当前页_返回PDF签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-sign", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName", "currency", "quotationCount" },
            Start = Start,
            End = End,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-font", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName", "currency" },
            Start = Start,
            End = End,
            PageSize = 20
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 选定列顺序 / 金额与计数格式化 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_原币金额与计数格式化()
    {
        var columns = new List<DynamicQuotationConversionReportFieldDto>
        {
            new("salesmanName", "业务员", "text", false),
            new("currency", "原币币种", "text", false),
            new("quotationCount", "有效报价数", "number", false),
            new("totalAmount", "有效报价金额(原币)", "number", false),
            new("conversionRate", "成交率%", "number", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["salesmanName"] = "张三",
            ["currency"] = "USD",
            ["quotationCount"] = 3,
            ["totalAmount"] = 1500.5m,
            ["conversionRate"] = 66.67m,
        };

        var cells = DynamicQuotationConversionPdfExporter.BuildRowCells(columns, row);

        Assert.Equal(new[] { "张三", "USD", "3", "1500.5", "66.67" }, cells);
    }

    [Fact]
    public void FormatCellValue_数值日期布尔空值按口径格式化()
    {
        Assert.Equal("3", DynamicQuotationConversionPdfExporter.FormatCellValue(3));
        Assert.Equal("1500.5", DynamicQuotationConversionPdfExporter.FormatCellValue(1500.5m));
        Assert.Equal("2026-09-15", DynamicQuotationConversionPdfExporter.FormatCellValue(new DateTime(2026, 9, 15)));
        Assert.Equal("是", DynamicQuotationConversionPdfExporter.FormatCellValue(true));
        Assert.Equal("否", DynamicQuotationConversionPdfExporter.FormatCellValue(false));
        Assert.Equal(string.Empty, DynamicQuotationConversionPdfExporter.FormatCellValue(null));
    }

    // ==================== 3. 宽列集 / 多行分页 ====================

    [Fact]
    public async Task ExportPdf_宽列集_拆分为多列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-wide", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 留空字段 = 返回全部白名单字段（10 列），宽列集应拆成多个列页（每个列页总宽不超页宽，列不裁切）
        var file = PdfOk(await ctl.ExportPdf(new DynamicQuotationConversionReportRequest
        {
            Start = Start,
            End = End,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
        foreach (var page in pdf.Pages)
        {
            Assert.InRange(page.Width.Point, 594, 596);
            Assert.InRange(page.Height.Point, 841, 843);
        }
    }

    [Fact]
    public async Task ExportPdf_多行_按行页拆分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-rows", "Priv", isSystemRole: true);
        for (var i = 0; i < 40; i++)
            SeedQuotation(db, $"QT-{i:D3}", $"业务员{i:D3}", Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 40
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 4. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = new DynamicQuotationConversionReportPageDto(
            new List<DynamicQuotationConversionReportFieldDto> { new("salesmanName", "业务员", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0, false,
            DynamicQuotationConversionReportRules.EmptyText,
            "只读", "边界", "免责",
            Start, End);

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicQuotationConversionPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 未授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无报价单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu", isSystem: true);
        var user = SeedUser(db, "qcd-pdf-nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_授权撤销_菜单授权移除后立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-revoke", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-pagesize", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicQuotationConversionReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-unknown", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicQuotationConversionReportRequest { Fields = new List<string> { "salesmanName", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 空页 / 多币种 / 只读 ====================

    private static DynamicQuotationConversionReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicQuotationConversionReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    [Fact]
    public async Task ExportPdf_空页_生成带空提示的PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-empty", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_多币种分桶_原币分开且无跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-multi", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-USD", "张三", Currency.USD, 1000m);
        SeedQuotation(db, "QT-EUR", "张三", Currency.EUR, 2000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var request = new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName", "currency", "totalAmount" },
            Start = Start,
            End = End,
            PageSize = 200
        };

        // 预览口径：两个原币分桶行（USD / EUR），绝不产生混合币种合计行
        var page = OkPage(await ctl.Preview(request));
        Assert.Equal(2, page.Rows.Count);
        Assert.Contains(page.Rows, r => Equals(r["currency"], "USD"));
        Assert.Contains(page.Rows, r => Equals(r["currency"], "EUR"));
        Assert.DoesNotContain(page.Rows, r => string.IsNullOrEmpty(r["currency"] as string));

        // PDF 导出对同一有界授权预览照实呈现，成功返回
        var file = PdfOk(await ctl.ExportPdf(request));
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-pdf-readonly", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var before = db.Quotations.Count();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End,
            PageSize = 20
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(before, db.Quotations.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
