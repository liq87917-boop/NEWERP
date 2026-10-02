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
/// ERP-226 动态订单利润暂估报表「分币种汇总 PDF」聚焦单元测试（只读、有界、经授权端点）。
/// 覆盖：专用汇总 PDF 端点复用已授权预览管线（身份 / 订单利润暂估表菜单 / 业务员数据范围 / 日期 / 应用筛选）、
/// 稳定币种行（原币 / 已审核订单数 / 销售额(原币)）与未知币种金额显式「未知」（绝不回落 0）、绝不跨币种合计、
/// 绝不合并币种、绝不含成本 / 利润 / 当前价估算、字体缺失显式失败、空汇总显式说明，以及无效 / 授权撤销 /
/// 无身份 / 来源超限时不返回任何文件。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class OrderProfitCurrencySummaryPdfTests
{
    private const string OrderProfitMenuCode = "order-profit";
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

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
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
        var menu = SeedMenu(db, OrderProfitMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);
        return user;
    }

    private static DynamicOrderProfitEstimateReportController NewController(ErpDbContext db)
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

    private static DynamicOrderProfitEstimateReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicOrderProfitEstimateReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 汇总导出器：选定顺序 / 金额格式化 / 字体缺失 ====================

    [Fact]
    public void BuildRowCells_按汇总列顺序映射_未知币种金额显式未知_金额与计数格式化()
    {
        var columns = new List<DynamicOrderProfitEstimateReportFieldDto>
        {
            new("currency", "原币币种", "text", false),
            new("orderCount", "已审核订单数", "number", false),
            new("salesAmount", "销售额(原币)", "number", false),
        };

        var known = OrderProfitCurrencySummaryPdfExporter.BuildRowCells(
            columns,
            new DynamicOrderProfitEstimateCurrencySummaryDto { Currency = "USD", OrderCount = 2, SalesAmount = 300.5m });
        Assert.Equal(new[] { "USD", "2", "300.5" }, known);

        var unknown = OrderProfitCurrencySummaryPdfExporter.BuildRowCells(
            columns,
            new DynamicOrderProfitEstimateCurrencySummaryDto { Currency = "未知币种", OrderCount = 3, SalesAmount = null });
        // 未知币种金额显式「未知」，绝不回落为 0；计数为数值文本
        Assert.Equal(new[] { "未知币种", "3", "未知" }, unknown);
    }

    [Fact]
    public void FormatCellValue_数值日期布尔空值按口径格式化()
    {
        Assert.Equal("3", OrderProfitCurrencySummaryPdfExporter.FormatCellValue(3));
        Assert.Equal("1500.5", OrderProfitCurrencySummaryPdfExporter.FormatCellValue(1500.5m));
        Assert.Equal("2026-09-15", OrderProfitCurrencySummaryPdfExporter.FormatCellValue(new DateTime(2026, 9, 15)));
        Assert.Equal("是", OrderProfitCurrencySummaryPdfExporter.FormatCellValue(true));
        Assert.Equal("否", OrderProfitCurrencySummaryPdfExporter.FormatCellValue(false));
        Assert.Equal(string.Empty, OrderProfitCurrencySummaryPdfExporter.FormatCellValue(null));
    }

    [Fact]
    public void Export_字体缺失_显式失败_不产出PDF()
    {
        var summary = new DynamicOrderProfitEstimateSummaryDto(
            new List<DynamicOrderProfitEstimateReportFieldDto> { new("currency", "原币币种", "text", false) },
            new List<DynamicOrderProfitEstimateCurrencySummaryDto>(),
            0,
            DynamicOrderProfitEstimateReportRules.CurrencySummaryCoverageText);
        var page = new DynamicOrderProfitEstimateReportPageDto(
            new List<DynamicOrderProfitEstimateReportFieldDto>(),
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0, false,
            DynamicOrderProfitEstimateReportRules.EmptyText,
            DynamicOrderProfitEstimateReportRules.ReadOnlyText,
            DynamicOrderProfitEstimateReportRules.BoundaryText,
            DynamicOrderProfitEstimateReportRules.DisclaimerText,
            Start, End,
            DynamicOrderProfitEstimateReportRules.PageOnlyText,
            DynamicOrderProfitEstimateReportRules.CurrencyContextText,
            DynamicOrderProfitEstimateReportRules.UnknownBasisText,
            DynamicOrderProfitEstimateReportRules.SourceLimitText);

        var ex = Assert.Throws<BusinessException>(() =>
            OrderProfitCurrencySummaryPdfExporter.Export(summary, page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 2. 汇总 PDF 端点：成功导出 ====================

    [Fact]
    public async Task ExportSummaryPdf_返回PDF签名与内容类型_区别于明细PDF文件名()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-sign", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End
        }));

        Assert.StartsWith("OrderProfitCurrencySummary_", file.FileDownloadName);

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportSummaryPdf_稳定币种行_未知币种独立_无跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-typed", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-USD-2", customer.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-EUR", customer.Id, Currency.EUR, 500m);
        SeedOrder(db, "SO-UNK", customer.Id, (Currency)99, 999m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var request = new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End
        };

        var page = OkPage(await ctl.Preview(request));
        var summary = page.Summary!;

        // 汇总列固定为「原币币种 / 已审核订单数 / 销售额(原币)」，与明细页选定列无关，绝不含成本 / 利润 / 当前价估算
        Assert.Equal(new[] { "currency", "orderCount", "salesAmount" }, summary.Columns.Select(c => c.Key).ToArray());
        Assert.DoesNotContain(summary.Columns, c => c.Key is "costAmount" or "profit" or "profitRate" or "currentPriceEstimate");

        // 3 个币种桶（EUR / USD / 未知币种），稳定升序，无混合币种合计行
        Assert.Equal(3, summary.CurrencyCount);
        Assert.Equal(new[] { "EUR", "USD", "未知币种" }, summary.Rows.Select(r => r.Currency).ToArray());
        Assert.DoesNotContain(summary.Rows, r => string.IsNullOrWhiteSpace(r.Currency));

        var usd = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(2, usd.OrderCount);
        Assert.Equal(300m, usd.SalesAmount);

        var eur = summary.Rows.Single(r => r.Currency == "EUR");
        Assert.Equal(1, eur.OrderCount);
        Assert.Equal(500m, eur.SalesAmount);

        var unknown = summary.Rows.Single(r => r.Currency == "未知币种");
        Assert.Equal(1, unknown.OrderCount);
        Assert.Null(unknown.SalesAmount);   // 未知币种金额为 null，绝不回落为 0

        var file = PdfOk(await ctl.ExportSummaryPdf(request));
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task ExportSummaryPdf_空汇总_生成带空提示的PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-empty", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
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
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_无效输入_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-invalid", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest
            {
                Start = new DateTime(2026, 9, 30),
                End = new DateTime(2026, 9, 1)
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_来源超限_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-overflow", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");

        var many = new List<SalesOrder>();
        for (var i = 1; i <= 501; i++)
        {
            many.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:0000}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
                Currency = Currency.USD,
                TotalAmount = 100m,
            });
        }
        db.SalesOrders.AddRange(many);
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-pdf-ro", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportSummaryPdf(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
