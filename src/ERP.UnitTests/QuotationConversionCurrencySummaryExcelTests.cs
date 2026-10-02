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
/// ERP-210 动态报价成交率报表「分币种汇总 Excel」单元测试（只读、有界）。
/// 覆盖：专用汇总端点复用已授权预览管线（身份 / 报价单菜单 / 业务员数据范围 / 字段 / 日期 / 分页 / 应用筛选）、
/// 仅导出选定汇总指标（类型化数值单元格、未知币种独立分桶、无明细行 / 跨币种金额合计）、
/// 上下文工作表显式标注日期 / 筛选 / 覆盖范围 / 原币证据，以及无效 / 授权撤销 / 来源超限时不返回任何工作簿。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class QuotationConversionCurrencySummaryExcelTests
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
            CustomerName = "报价成交率汇总导出客户",
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
                CustomerName = "报价成交率汇总超限客户",
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

    private static FileContentResult SummaryOk(IActionResult result)
        => Assert.IsType<FileContentResult>(result);

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return new XSSFWorkbook(input);
    }
    [Fact]
    public async Task ExportSummary_返回xlsx附件_汇总数据表与口径表齐备()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-xlsx", "Priv");
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "quotationCount", "totalAmount" },
            Start = Start,
            End = End
        }));

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicQuotationConversionReportRules.SummarySheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);
    }

    [Fact]
    public async Task ExportSummary_仅选定汇总指标_类型化数值单元格_未知币种独立_无跨币种金额合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-typed", "Priv");
        SeedQuotation(db, "QT-USD1", "张三", Currency.USD, 1000m);
        SeedQuotation(db, "QT-USD2", "李四", Currency.USD, 500m);
        SeedQuotation(db, "QT-EUR", "王五", Currency.EUR, 2000m);
        SeedQuotation(db, "QT-UNK", "赵六", (Currency)999, 300m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "quotationCount", "totalAmount" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        // 表头仅「币种 + 选定汇总指标」，绝不出现明细字段（业务员）或未选金额指标
        Assert.Equal("原币币种", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("有效报价数", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("有效报价金额(原币)", sheet.GetRow(0).GetCell(2).StringCellValue);

        // 3 个币种桶（EUR / USD / 未知币种），无任何多余行（无明细行、无跨币种合计行）
        Assert.Equal(3, sheet.LastRowNum);
        var rows = Enumerable.Range(1, 3).Select(i => sheet.GetRow(i)).ToList();
        Assert.Equal(new[] { "EUR", "USD", "未知币种" }, rows.Select(r => r.GetCell(0).StringCellValue).ToArray());

        // 类型化数值单元格（计数整数、金额小数）
        var usd = rows.Single(r => r.GetCell(0).StringCellValue == "USD");
        Assert.Equal(CellType.Numeric, usd.GetCell(1).CellType);
        Assert.Equal(2d, usd.GetCell(1).NumericCellValue);
        Assert.Equal(CellType.Numeric, usd.GetCell(2).CellType);
        Assert.Equal(1500d, usd.GetCell(2).NumericCellValue, 2);
    }

    [Fact]
    public async Task ExportSummary_口径表标注日期筛选覆盖范围与原币证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-ctx", "Priv");
        SeedQuotation(db, "QT-USD", "张三", Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "currency", "totalAmount" },
            Start = Start,
            End = End,
            Filter = new QuotationConversionFilterDto { SalespersonName = "张三", Currency = "USD" }
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(1);

        // 日期窗口
        Assert.Equal(DynamicQuotationConversionReportRules.ContextStartLabel, ctx.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", ctx.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextEndLabel, ctx.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", ctx.GetRow(1).GetCell(1).StringCellValue);

        // 原币口径
        Assert.Equal(DynamicQuotationConversionReportRules.ContextCurrencyLabel, ctx.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("绝不跨币种合计", ctx.GetRow(2).GetCell(1).StringCellValue);

        var labels = Enumerable.Range(0, ctx.LastRowNum + 1)
            .Select(i => ctx.GetRow(i).GetCell(0).StringCellValue).ToArray();

        // 筛选条件（业务员关键字 + 原币币种）显式标注
        var filterIdx = Array.IndexOf(labels, DynamicQuotationConversionReportRules.ContextFilterLabel);
        Assert.True(filterIdx >= 0);
        Assert.Contains("业务员关键字 张三", ctx.GetRow(filterIdx).GetCell(1).StringCellValue);
        Assert.Contains("原币币种 USD", ctx.GetRow(filterIdx).GetCell(1).StringCellValue);

        // 覆盖范围（全部匹配分桶）
        var coverageIdx = Array.IndexOf(labels, DynamicQuotationConversionReportRules.ContextCoverageLabel);
        Assert.True(coverageIdx >= 0);
        Assert.Equal(DynamicQuotationConversionReportRules.CurrencySummaryCoverageText, ctx.GetRow(coverageIdx).GetCell(1).StringCellValue);

        // 原币证据（未知币种独立、无跨币种合计）
        var evidenceIdx = Array.IndexOf(labels, DynamicQuotationConversionReportRules.ContextOriginalCurrencyEvidenceLabel);
        Assert.True(evidenceIdx >= 0);
        Assert.Equal(DynamicQuotationConversionReportRules.ContextOriginalCurrencyEvidenceText, ctx.GetRow(evidenceIdx).GetCell(1).StringCellValue);
    }
    // ==================== 拒绝路径（不返回工作簿） ====================

    [Fact]
    public async Task ExportSummary_授权撤销_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-revoke", "Priv");
        SeedQuotation(db, "QT-1", "张三", Currency.USD, 1000m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_来源超限_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-sum-overflow", "Priv");
        SeedManyQuotations(db, 2001);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }
}
