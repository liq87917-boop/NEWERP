using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// 动态业务员提成证据报表（ERP-248）「全匹配」原币汇总 Excel 导出（只读、有界）单元测试。
/// 覆盖：独立于当前页 / 选定明细列的全匹配覆盖、已知签名金额 / 计数 / 显式 0 当前参考比例为数值、
/// 未知金额 / 利润 / 利润率 / 提成额显式「未知」、未知 / 无效币种保留原始键仅计数、
/// 无员工明细行 / 跨币种合计金额、公式前导转义、上下文表完整口径、
/// 以及授权撤销 / 数据范围 / 来源超限 fail closed 与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesCommissionSummaryExcelTests
{
    private const string MenuCode = "sales-commission";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = isSalesman,
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            Status = status,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedRate(ErpDbContext db, params string[] values)
    {
        foreach (var value in values)
        {
            db.SysParameters.Add(new SysParameter
            {
                ParamKey = SalesCommissionEvidenceRules.SalesCommissionRateKey,
                ParamValue = value,
                ParamName = "业务员提成比例(%)",
                IsSystem = true
            });
        }
        db.SaveChanges();
    }

    private static DynamicSalesCommissionReportController BuildController(ErpDbContext db, long? userId)
        => new(db, new ReportService(db));

    private static DynamicSalesCommissionReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        SalesCommissionFilterDto? filter = null)
        => new()
        {
            Fields = fields,
            Start = Start,
            End = End,
            Page = page,
            PageSize = pageSize,
            Filter = filter,
        };

    private static FileContentResult SummaryOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        return file;
    }

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new XSSFWorkbook(ms);
    }

    private static IRow? CurrencyRow(XSSFWorkbook workbook, string currency)
    {
        var sheet = workbook.GetSheet(DynamicSalesCommissionSummaryRules.SummarySheetName);
        for (var r = 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row?.GetCell(0)?.StringCellValue == currency)
                return row;
        }
        return null;
    }

    private static ICell? ContextValue(XSSFWorkbook workbook, string label)
    {
        var sheet = workbook.GetSheet(DynamicSalesCommissionReportRules.ContextSheetName);
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row?.GetCell(0)?.StringCellValue == label)
                return row.GetCell(1);
        }
        return null;
    }

    // ==================== 1. 纯规则：汇总导出投影 ====================

    [Fact]
    public void BuildExportRow_已知金额计数保留数值_未知金额利润提成显式未知()
    {
        var row = new DynamicSalesCommissionCurrencySummaryDto
        {
            Currency = "USD",
            CurrencyLabel = "USD 美元",
            ApprovedOrders = 5,
            UniqueSalesmanBuckets = 2,
            SalesAmount = 800m,
            Profit = null,
            ProfitRate = null,
            CommissionAmount = null,
            AmountCompletenessText = DynamicSalesCommissionSummaryRules.KnownAmountCompleteText,
        };

        var export = DynamicSalesCommissionSummaryRules.BuildExportRow(row);

        Assert.Equal("USD", export["currency"]);
        Assert.Equal(5, Assert.IsType<int>(export["approvedOrders"]));
        Assert.Equal(2, Assert.IsType<int>(export["uniqueSalesmanBuckets"]));
        Assert.Equal(800m, Assert.IsType<decimal>(export["salesAmount"]));
        Assert.Equal("未知", export["profit"]);
        Assert.Equal("未知", export["profitRate"]);
        Assert.Equal("未知", export["commissionAmount"]);
    }

    [Fact]
    public void BuildExportRow_公式前导转义为字面文本_未知币种金额未知仅计数()
    {
        var row = new DynamicSalesCommissionCurrencySummaryDto
        {
            Currency = "=HYPERLINK(\"http://evil\")",
            CurrencyLabel = "@SUM(A1)",
            ApprovedOrders = 3,
            UniqueSalesmanBuckets = 1,
            SalesAmount = null,
            AmountCompletenessText = "-cmd",
        };

        var export = DynamicSalesCommissionSummaryRules.BuildExportRow(row);

        Assert.Equal("'=HYPERLINK(\"http://evil\")", export["currency"]);
        Assert.Equal("'@SUM(A1)", export["currencyLabel"]);
        Assert.Equal("'-cmd", export["amountCompletenessText"]);
        Assert.Equal(3, Assert.IsType<int>(export["approvedOrders"]));
        Assert.Equal("未知", export["salesAmount"]);
    }

    // ==================== 2. 全匹配汇总 Excel（控制器 + 工作簿） ====================

    [Fact]
    public async Task 导出汇总_独立于当前页与选定列_全匹配覆盖全部币种()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request(
            new List<string> { "currency" }, page: 1, pageSize: 1)));

        var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicSalesCommissionSummaryRules.SummarySheetName, workbook.GetSheetName(0));
        Assert.Equal(DynamicSalesCommissionReportRules.ContextSheetName, workbook.GetSheetName(1));

        var data = workbook.GetSheetAt(0);
        Assert.Equal(2, data.LastRowNum);

        Assert.NotNull(CurrencyRow(workbook, "USD"));
        Assert.NotNull(CurrencyRow(workbook, "CNY"));

        Assert.Equal(2.0, ContextValue(workbook, DynamicSalesCommissionSummaryRules.ContextGlobalBucketsLabel)!.NumericCellValue);
        Assert.Equal(3.0, ContextValue(workbook, DynamicSalesCommissionSummaryRules.ContextGlobalOrdersLabel)!.NumericCellValue);
    }

    [Fact]
    public async Task 导出汇总_已知签名金额与计数为数值_未知利润提成显式未知_无跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);

        Assert.Equal(2, data.LastRowNum);

        var usd = CurrencyRow(workbook, "USD");
        Assert.NotNull(usd);
        Assert.Equal(2.0, usd!.GetCell(2).NumericCellValue);
        Assert.Equal(2.0, usd!.GetCell(3).NumericCellValue);
        Assert.Equal(800.0, usd!.GetCell(4).NumericCellValue);
        Assert.Equal("未知", usd!.GetCell(5).StringCellValue);
        Assert.Equal("未知", usd!.GetCell(6).StringCellValue);
        Assert.Equal("未知", usd!.GetCell(7).StringCellValue);

        var cny = CurrencyRow(workbook, "CNY");
        Assert.NotNull(cny);
        Assert.Equal(500.0, cny!.GetCell(4).NumericCellValue);

        for (var r = 1; r <= data.LastRowNum; r++)
        {
            var row = data.GetRow(r);
            if (row == null) continue;
            var currencyKey = row.GetCell(0)?.StringCellValue ?? string.Empty;
            Assert.DoesNotContain("合计", currencyKey);
            Assert.DoesNotContain("总计", currencyKey);
        }
    }

    [Fact]
    public async Task 导出汇总_显式零参考比例_上下文写入数值零()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);
        SeedRate(db, "0");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        var workbook = OpenWorkbook(file.FileContents);

        var rateCell = ContextValue(workbook, DynamicSalesCommissionReportRules.ContextRateLabel);
        Assert.NotNull(rateCell);
        Assert.Equal(CellType.Numeric, rateCell!.CellType);
        Assert.Equal(0.0, rateCell!.NumericCellValue);

        var reasonCell = ContextValue(workbook, DynamicSalesCommissionSummaryRules.ContextRateReasonLabel);
        Assert.NotNull(reasonCell);
        Assert.Equal(SalesCommissionEvidenceRules.CommissionRateEvidence, reasonCell!.StringCellValue);
    }

    [Fact]
    public async Task 导出汇总_未知币种保留原始键_金额未知仅计数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp.Id, (Currency)999, 0m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        var workbook = OpenWorkbook(file.FileContents);

        var unknown = CurrencyRow(workbook, "999");
        Assert.NotNull(unknown);
        Assert.Equal("未知币种", unknown!.GetCell(1).StringCellValue);
        Assert.Equal(1.0, unknown!.GetCell(2).NumericCellValue);
        Assert.Equal("未知", unknown!.GetCell(4).StringCellValue);
        Assert.Equal(DynamicSalesCommissionSummaryRules.UnknownAmountIncompleteText, unknown!.GetCell(8).StringCellValue);
    }

    [Fact]
    public async Task 导出汇总_授权撤销_不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Priv", isSystem: true);
        var user = SeedUser(db, "priv");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, MenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);
        Assert.IsType<FileContentResult>(await ctl.ExportSummary(Request()));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 导出汇总_来源超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        db.SalesOrders.AddRange(Enumerable.Range(1, 501).Select(i => new SalesOrder
        {
            OrderNo = $"SO-{i:0000}",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            SalesmanId = emp.Id,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            TotalAmount = 100m
        }));
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task 导出汇总_受限制业务员_只汇总被分配客户证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice", "业务员甲");
        var otherEmp = SeedEmployee(db, "bob", "业务员乙");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmp.Id);

        SeedOrder(db, "SO-MINE", mine.Id, employee.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, otherEmp.Id, Currency.USD, 9000m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);

        Assert.Equal(1, data.LastRowNum);
        var usd = CurrencyRow(workbook, "USD");
        Assert.NotNull(usd);
        Assert.Equal(100.0, usd!.GetCell(4).NumericCellValue);
        Assert.Equal(1.0, ContextValue(workbook, DynamicSalesCommissionSummaryRules.ContextGlobalOrdersLabel)!.NumericCellValue);
    }

    [Fact]
    public async Task 导出汇总_上下文表_含日期筛选覆盖来源上限全局计数比例依据与未知历史利润提成说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request(filter: new SalesCommissionFilterDto { Currency = "USD" })));
        var workbook = OpenWorkbook(file.FileContents);

        Assert.Equal("2026-09-01", ContextValue(workbook, DynamicSalesCommissionReportRules.ContextStartLabel)!.StringCellValue);
        Assert.Equal("2026-09-30", ContextValue(workbook, DynamicSalesCommissionReportRules.ContextEndLabel)!.StringCellValue);
        Assert.Equal("原币币种 USD", ContextValue(workbook, DynamicSalesCommissionReportRules.ContextFilterLabel)!.StringCellValue);
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextSourceLimitLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionSummaryRules.ContextCoverageLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextCurrencyLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextUnknownLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextProfitLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextCommissionLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextSourceLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionReportRules.ContextReadOnlyLabel));
        Assert.NotNull(ContextValue(workbook, DynamicSalesCommissionSummaryRules.ContextProfitBasisLabel));

        Assert.Equal("未知", ContextValue(workbook, DynamicSalesCommissionReportRules.ContextRateLabel)!.StringCellValue);
    }

    [Fact]
    public async Task 导出汇总_无员工明细行_仅固定原币汇总列()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);

        Assert.Equal(DynamicSalesCommissionSummaryRules.SummarySheetName, data.SheetName);
        Assert.NotEqual(DynamicSalesCommissionReportRules.DataSheetName, data.SheetName);

        var header = data.GetRow(0);
        Assert.Equal(12, (int)header.LastCellNum);
        for (var c = 0; c < header.LastCellNum; c++)
        {
            var label = header.GetCell(c)?.StringCellValue ?? string.Empty;
            Assert.NotEqual("业务员", label);
            Assert.NotEqual("业务员 Id", label);
            Assert.NotEqual("业务员身份依据", label);
        }
    }

    [Fact]
    public async Task 导出汇总_姓名关键字_上下文表保留关键字_即使业务员列隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "张三丰");
        var emp2 = SeedEmployee(db, "S002", "李四");
        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.CNY, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        // 汇总本身无员工明细列，关键字上下文仍保留在「报表口径」工作表
        var file = SummaryOk(await ctl.ExportSummary(Request(
            fields: new List<string> { "currency" },
            filter: new SalesCommissionFilterDto { SalesmanName = "  张三  " })));

        var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal("业务员姓名关键字 张三",
            ContextValue(workbook, DynamicSalesCommissionReportRules.ContextFilterLabel)!.StringCellValue);
    }
}
