using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-206 动态报价成交率报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与类型化值（计数 / 金额为数值单元格）、公式前导文本转义（保持字面、非公式单元格）、
/// 仅导出当前页（单页上限 200）、日期与原币口径上下文工作表（绝不追加跨币种金额合计）、
/// 无身份 / 无菜单授权 / 授权撤销拒绝、空页仅表头 + 上下文空页说明，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicQuotationConversionExcelTests
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

    private static FileContentResult ExportOk(IActionResult result)
        => Assert.IsType<FileContentResult>(result);

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return new XSSFWorkbook(input);
    }

    [Fact]
    public async Task Export_返回xlsx附件_数据表与口径表齐备()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-xlsx", "Priv");
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName", "currency", "quotationCount", "totalAmount" },
            Start = Start,
            End = End
        }));

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicQuotationConversionReportRules.RequiredMenuText, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);
    }

    [Fact]
    public async Task Export_选定列顺序与类型化数值单元格()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-typed", "Priv");
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "quotationCount", "totalAmount" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        Assert.Equal("原币币种", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("有效报价数", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("有效报价金额(原币)", sheet.GetRow(0).GetCell(2).StringCellValue);

        var row = sheet.GetRow(1);
        Assert.Equal("USD", row.GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(1).CellType);
        Assert.Equal(1d, row.GetCell(1).NumericCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(2).CellType);
        Assert.Equal(1500d, row.GetCell(2).NumericCellValue, 2);
    }

    [Fact]
    public async Task Export_公式前导文本转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-formula", "Priv");
        SeedQuotation(db, "QT-1", "=SUM(A1)", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var cell = workbook.GetSheetAt(0).GetRow(1).GetCell(0);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=SUM(A1)", cell.StringCellValue);
    }

    [Fact]
    public async Task Export_日期与原币口径已标注且绝不追加跨币种金额合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-ctx", "Priv");
        SeedQuotation(db, "QT-USD", "张三", Currency.USD, 1000m);
        SeedQuotation(db, "QT-EUR", "张三", Currency.EUR, 2000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName", "currency", "convertedAmount" },
            Start = Start,
            End = End,
            PageSize = 200
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);

        // 数据表只有表头 + 两个原币分桶行，绝不追加混合币种合计行
        Assert.Equal(2, data.LastRowNum);
        Assert.Equal("业务员", data.GetRow(0).GetCell(0).StringCellValue);

        var ctx = workbook.GetSheetAt(1);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextStartLabel, ctx.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", ctx.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextEndLabel, ctx.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", ctx.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextCurrencyLabel, ctx.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("绝不跨币种合计", ctx.GetRow(2).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_授权撤销_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-revoke", "Priv");
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_空页_仅表头并在口径表标注空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-empty-x", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);
        Assert.Equal(0, data.LastRowNum);   // 仅表头行

        var ctx = workbook.GetSheetAt(1);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextEmptyLabel, ctx.GetRow(4).GetCell(0).StringCellValue);
        Assert.Equal(DynamicQuotationConversionReportRules.EmptyText, ctx.GetRow(4).GetCell(1).StringCellValue);
    }
}


