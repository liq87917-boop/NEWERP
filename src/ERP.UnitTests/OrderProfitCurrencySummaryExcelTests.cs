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
/// ERP-225 动态订单利润暂估报表「分币种汇总 Excel 导出」聚焦单元测试（只读、有界、经授权端点）。
/// 覆盖：数据工作表 / 口径表齐备且与明细导出工作表区分、稳定币种行（原币 / 已审核订单数 / 销售额(原币)）、
/// 已知币种计数与金额为数值、未知币种金额显式「未知」（绝不写成数值 0）、绝不跨币种合计、绝不含成本 / 利润 /
/// 当前价估算 / 换算、日期 / 来源上限 / 原币 / 未知成本依据 / 覆盖范围 / 原币证据 / 只读声明上下文、
/// 客户 / 原币筛选上下文、授权撤销 / 无身份 / 无效输入 / 来源超限拒绝且不返回工作簿、空汇总仅表头 + 空汇总说明、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class OrderProfitCurrencySummaryExcelTests
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

    private static FileContentResult ExportOk(IActionResult result)
        => Assert.IsType<FileContentResult>(result);

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return new XSSFWorkbook(input);
    }

    // ==================== 纯规则：分币种汇总导出行 ====================

    [Fact]
    public void 汇总导出行_已知币种为数值_未知币种金额显式未知_不含成本利润()
    {
        var known = DynamicOrderProfitEstimateReportRules.BuildSummaryExportRow(
            new DynamicOrderProfitEstimateCurrencySummaryDto { Currency = "USD", OrderCount = 2, SalesAmount = 300m });
        Assert.Equal("USD", known["currency"]);
        Assert.Equal(2, known["orderCount"]);
        Assert.Equal(300m, known["salesAmount"]);

        var unknown = DynamicOrderProfitEstimateReportRules.BuildSummaryExportRow(
            new DynamicOrderProfitEstimateCurrencySummaryDto { Currency = "未知币种", OrderCount = 3, SalesAmount = null });
        Assert.Equal("未知币种", unknown["currency"]);
        Assert.Equal(3, unknown["orderCount"]);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.UnknownAmountText, unknown["salesAmount"]);

        // 键集合与汇总列白名单一致，绝不出现成本 / 利润 / 当前价估算
        Assert.Equal(new[] { "currency", "orderCount", "salesAmount" }, known.Keys.ToArray());
    }

    [Fact]
    public void 汇总导出行_公式前导币种文本转义为字面文本()
    {
        var row = DynamicOrderProfitEstimateReportRules.BuildSummaryExportRow(
            new DynamicOrderProfitEstimateCurrencySummaryDto { Currency = "=cmd()", OrderCount = 1, SalesAmount = 1m });
        Assert.Equal("'=cmd()", row["currency"]);
    }

    // ==================== 导出端点：工作簿结构与类型化单元格 ====================

    [Fact]
    public async Task ExportSummary_返回xlsx附件_数据表与口径表齐备_区别于明细导出()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-xlsx", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.SummarySheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);

        // 明确区分于「当前页明细导出」的「订单利润暂估表」数据工作表
        Assert.NotEqual(DynamicOrderProfitEstimateReportRules.RequiredMenuText, workbook.GetSheetAt(0).SheetName);
    }

    [Fact]
    public async Task ExportSummary_稳定币种行_已知计数金额为数值_未知金额显式未知()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-typed", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-USD-2", customer.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-UNK", customer.Id, (Currency)99, 999m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        // 表头固定：原币币种 / 已审核订单数 / 销售额(原币)
        Assert.Equal("原币币种", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("已审核订单数", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("销售额(原币)", sheet.GetRow(0).GetCell(2).StringCellValue);

        var rows = Enumerable.Range(1, sheet.LastRowNum)
            .Select(r => sheet.GetRow(r))
            .Where(r => r != null)
            .ToList();

        Assert.Equal(2, rows.Count);

        var usdRow = rows.Single(r => r!.GetCell(0).StringCellValue == "USD")!;
        Assert.Equal(CellType.Numeric, usdRow.GetCell(1).CellType);
        Assert.Equal(2d, usdRow.GetCell(1).NumericCellValue, 0);
        Assert.Equal(CellType.Numeric, usdRow.GetCell(2).CellType);
        Assert.Equal(300d, usdRow.GetCell(2).NumericCellValue, 2);

        var unknownRow = rows.Single(r => r!.GetCell(0).StringCellValue == "未知币种")!;
        Assert.Equal(CellType.Numeric, unknownRow.GetCell(1).CellType);
        Assert.Equal(1d, unknownRow.GetCell(1).NumericCellValue, 0);
        Assert.Equal(CellType.String, unknownRow.GetCell(2).CellType);
        Assert.Equal("未知", unknownRow.GetCell(2).StringCellValue);
    }

    [Fact]
    public async Task ExportSummary_绝不跨币种合计_绝不含成本利润当前价估算换算()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-nosume", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-USD-2", customer.Id, Currency.USD, -30m);
        SeedOrder(db, "SO-EUR", customer.Id, Currency.EUR, 500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        var header = Enumerable.Range(0, sheet.GetRow(0).LastCellNum)
            .Select(c => sheet.GetRow(0).GetCell(c)?.StringCellValue ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();
        Assert.Equal(new[] { "原币币种", "已审核订单数", "销售额(原币)" }, header);

        var currencyCells = Enumerable.Range(1, sheet.LastRowNum)
            .Select(r => sheet.GetRow(r)?.GetCell(0)?.StringCellValue ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();
        Assert.Equal(new[] { "EUR", "USD" }, currencyCells);
        Assert.DoesNotContain("合计", header);
        Assert.DoesNotContain("总计", header);
        Assert.DoesNotContain(currencyCells, c => c.Contains("合计") || c.Contains("总计"));
        Assert.DoesNotContain(header, c => c.Contains("成本") || c.Contains("利润") || c.Contains("当前价估算") || c.Contains("换算"));
    }

    [Fact]
    public async Task ExportSummary_上下文_日期来源上限原币未知成本依据覆盖范围原币证据只读()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-ctx", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(1);

        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextStartLabel, ctx.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", ctx.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextEndLabel, ctx.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", ctx.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextSourceLimitLabel, ctx.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("500 张订单", ctx.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextCurrencyLabel, ctx.GetRow(3).GetCell(0).StringCellValue);
        Assert.Contains("绝不跨币种合计", ctx.GetRow(3).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextMissingCostBasisLabel, ctx.GetRow(4).GetCell(0).StringCellValue);
        Assert.Contains("未知", ctx.GetRow(4).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.SummaryCoverageLabel, ctx.GetRow(5).GetCell(0).StringCellValue);
        Assert.Contains("全部匹配的已审核销售订单", ctx.GetRow(5).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextOriginalCurrencyEvidenceLabel, ctx.GetRow(6).GetCell(0).StringCellValue);
        Assert.Contains("无任何跨币种金额总计", ctx.GetRow(6).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextReadOnlyLabel, ctx.GetRow(7).GetCell(0).StringCellValue);
    }

    [Fact]
    public async Task ExportSummary_筛选上下文_客户与原币已标注()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-filter", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End,
            Filter = new OrderProfitEstimateFilterDto { CustomerId = customer.Id, Currency = "usd" }
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(1);

        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextFilterLabel, ctx.GetRow(8).GetCell(0).StringCellValue);
        Assert.Contains($"客户 Id {customer.Id}", ctx.GetRow(8).GetCell(1).StringCellValue);
        Assert.Contains("原币币种 USD", ctx.GetRow(8).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task ExportSummary_授权撤销_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_无效输入_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-invalid", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
            {
                Start = new DateTime(2026, 9, 30),
                End = new DateTime(2026, 9, 1)
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_超出500订单_拒绝且不返回部分汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-overflow", "Priv");
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
            () => ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_空结果_仅表头并在口径表标注空汇总说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-empty", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);
        Assert.Equal(0, data.LastRowNum);

        var ctx = workbook.GetSheetAt(1);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextEmptyLabel, ctx.GetRow(8).GetCell(0).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.EmptyText, ctx.GetRow(8).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task ExportSummary_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-sum-ro", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = ExportOk(await ctl.ExportSummary(new DynamicOrderProfitEstimateReportRequest
        {
            Start = Start,
            End = End
        }));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
