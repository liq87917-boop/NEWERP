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
/// 动态业务员提成证据报表（ERP-245）Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与类型化值（已知签名原币金额 / 计数为数值，null 金额 / 利润 / 利润率 / 提成比例 / 提成额显式「未知」，绝不写成 0）、
/// 配置为 0 的当前参考比例写入数值 0、负数金额签名保留、公式前导文本转义为字面文本、仅导出当前页、绝不跨币种合计 / 追加未选定列、
/// 上下文工作表始终标注规范化日期 / 应用筛选 / 页面覆盖 / 来源计数与上限 / 当前用户受限已审核订单来源 / 当前参考比例 / 未知历史利润与提成口径
/// （即使对应列被取消选择也始终包含）、数据范围（受限制业务员看不到他人）、授权撤销不返回工作簿、来源超限 / 非法输入 fail closed，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesCommissionExcelTests
{
    private const string MenuCode = "sales-commission";
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

    private static FileContentResult ExportOk(IActionResult result)
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

    // ==================== 1. 选定列顺序与类型化值 ====================

    [Fact]
    public async Task Export_选定列顺序与类型化值_金额订单数数值_未知利润提成显式未知()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string>
            { "salesmanName", "currency", "orderCount", "salesAmount", "profit", "profitRate", "commissionRate", "commissionAmount" })));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(DynamicSalesCommissionReportRules.DataSheetName, sheet.SheetName);

        Assert.Equal("业务员", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("原币币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("已审核订单数", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("已知原币金额小计", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal("利润(未知)", sheet.GetRow(0).GetCell(4).StringCellValue);
        Assert.Equal("利润率%(未知)", sheet.GetRow(0).GetCell(5).StringCellValue);
        Assert.Equal("提成比例%(当前参考)", sheet.GetRow(0).GetCell(6).StringCellValue);
        Assert.Equal("提成额(未知)", sheet.GetRow(0).GetCell(7).StringCellValue);

        Assert.Equal("业务员甲", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("USD", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(2).CellType);
        Assert.Equal(1d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(3).CellType);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(4).CellType);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(4).StringCellValue);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(5).StringCellValue);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(6).StringCellValue);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(7).StringCellValue);
    }

    [Fact]
    public async Task Export_未知币种金额显式未知_绝不写成0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, (Currency)999, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "currency", "salesAmount" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(1).CellType);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_配置为零的当前参考比例_写入数值0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedRate(db, "0");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "commissionRate", "commissionRateEvidence" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(0).CellType);
        Assert.Equal(0d, sheet.GetRow(1).GetCell(0).NumericCellValue);
        Assert.Contains("当前参考比例", sheet.GetRow(1).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_负数金额签名保留_绝不取绝对值()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, -50m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesAmount" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(0).CellType);
        Assert.Equal(-50d, sheet.GetRow(1).GetCell(0).NumericCellValue);
    }

    [Fact]
    public async Task Export_公式前导业务员名_转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "=HYPERLINK(\"http://evil\")");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.StartsWith("'", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 2. 上下文工作表（即使对应列未选定也始终包含） ====================

    [Fact]
    public async Task Export_上下文工作表_始终包含日期筛选页面覆盖来源计数上限当前参考与未知利润提成()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        // 只选定「原币币种」一列：上下文仍应始终包含，且不追加未选定列
        var file = ExportOk(await ctl.Export(Request(new List<string> { "currency" })));
        var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicSalesCommissionReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);

        var context = workbook.GetSheetAt(1);
        Assert.Equal(DynamicSalesCommissionReportRules.ContextStartLabel, context.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", context.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesCommissionReportRules.ContextEndLabel, context.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", context.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesCommissionReportRules.ContextPageLabel, context.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("业务员桶×原币行总数 1", context.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesCommissionReportRules.ContextSourceLimitLabel, context.GetRow(3).GetCell(0).StringCellValue);
        Assert.Contains("500 张订单", context.GetRow(3).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesCommissionReportRules.ContextSourceCountLabel, context.GetRow(4).GetCell(0).StringCellValue);
        Assert.Contains("已审核订单 1", context.GetRow(4).GetCell(1).StringCellValue);

        var labels = Enumerable.Range(0, context.LastRowNum + 1)
            .Select(r => context.GetRow(r)?.GetCell(0)?.StringCellValue ?? string.Empty)
            .ToList();
        Assert.Contains(DynamicSalesCommissionReportRules.ContextRateLabel, labels);
        Assert.Contains(DynamicSalesCommissionReportRules.ContextProfitLabel, labels);
        Assert.Contains(DynamicSalesCommissionReportRules.ContextCommissionLabel, labels);
        Assert.Contains(DynamicSalesCommissionReportRules.ContextFilterLabel, labels);
        Assert.Contains(DynamicSalesCommissionReportRules.ContextSourceLabel, labels);
        Assert.Contains(DynamicSalesCommissionReportRules.ContextPageOnlyLabel, labels);
    }

    [Fact]
    public async Task Export_仅导出当前页_绝不跨币种合计_绝不追加未选定列()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, emp.Id, Currency.CNY, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var req = Request(new List<string> { "salesmanName", "currency", "salesAmount" });
        req.PageSize = 1;
        var file = ExportOk(await ctl.Export(req));
        var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        Assert.Equal(1, sheet.LastRowNum);   // 仅表头 + 当前页 1 行
        Assert.DoesNotContain("合计", sheet.GetRow(0).Cells.Select(c => c.StringCellValue));
        Assert.Equal(3, (int)sheet.GetRow(0).LastCellNum);   // 绝不追加未选定列
        Assert.Equal(2, workbook.NumberOfSheets);            // 数据 + 报表口径，绝不追加全匹配汇总工作表
    }

    // ==================== 3. 授权 / 范围 / 边界 ====================

    [Fact]
    public async Task Export_授权撤销_不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke");
        var user = SeedUser(db, "revoke");
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
        Assert.IsType<FileContentResult>(await ctl.Export(Request(new List<string> { "salesmanName" })));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(Request(new List<string> { "salesmanName" })));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_来源超限_拒绝()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Export_非法筛选_在读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(Request(filter: new SalesCommissionFilterDto { Currency = "999" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_受限制业务员_只导出被分配客户订单()
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

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName", "salesAmount" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(1, sheet.LastRowNum);   // 仅表头 + 1 行证据
        Assert.Equal("业务员甲", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 4. 纯规则：导出投影 ====================

    [Fact]
    public void BuildExportRow_未知数值字段null转未知_业务员Id保留空()
    {
        var row = new Dictionary<string, object?>
        {
            ["salesmanId"] = null,
            ["salesAmount"] = null,
            ["profit"] = null,
            ["profitRate"] = null,
            ["commissionRate"] = null,
            ["commissionAmount"] = null,
            ["orderCount"] = 3,
        };
        var keys = new List<string>
        {
            "salesmanId", "salesAmount", "profit", "profitRate", "commissionRate", "commissionAmount", "orderCount",
        };
        var export = DynamicSalesCommissionReportRules.BuildExportRow(row, keys);

        Assert.Null(export["salesmanId"]);
        Assert.Equal("未知", export["salesAmount"]);
        Assert.Equal("未知", export["profit"]);
        Assert.Equal("未知", export["profitRate"]);
        Assert.Equal("未知", export["commissionRate"]);
        Assert.Equal("未知", export["commissionAmount"]);
        Assert.Equal(3, Assert.IsType<int>(export["orderCount"]));
    }

    [Fact]
    public void EscapeFormulaLeading_公式前导转义为字面文本_数值原样()
    {
        Assert.Equal("'=HYPERLINK(\"http://evil\")",
            DynamicSalesCommissionReportRules.EscapeFormulaLeading("=HYPERLINK(\"http://evil\")"));
        Assert.Equal("'+1+2", DynamicSalesCommissionReportRules.EscapeFormulaLeading("+1+2"));
        Assert.Equal("'-cmd", DynamicSalesCommissionReportRules.EscapeFormulaLeading("-cmd"));
        Assert.Equal("'@SUM(A1)", DynamicSalesCommissionReportRules.EscapeFormulaLeading("@SUM(A1)"));
        Assert.Equal("业务员甲", DynamicSalesCommissionReportRules.EscapeFormulaLeading("业务员甲"));
        Assert.Equal(100m, Assert.IsType<decimal>(DynamicSalesCommissionReportRules.EscapeFormulaLeading(100m)));
        Assert.Null(DynamicSalesCommissionReportRules.EscapeFormulaLeading(null));
    }

    [Fact]
    public async Task Export_不追加全匹配汇总_仅当前页数据与口径工作表()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.CNY, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            new List<string> { "salesmanName", "currency", "salesAmount" })));

        var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicSalesCommissionReportRules.DataSheetName, workbook.GetSheetName(0));
        Assert.Equal(DynamicSalesCommissionReportRules.ContextSheetName, workbook.GetSheetName(1));

        for (var i = 0; i < workbook.NumberOfSheets; i++)
        {
            var sheet = workbook.GetSheetAt(i);
            Assert.DoesNotContain("全匹配原币汇总", sheet.SheetName);
            for (var r = 0; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r);
                if (row == null) continue;
                foreach (var cell in row.Cells)
                {
                    var text = cell.CellType == CellType.String ? cell.StringCellValue : string.Empty;
                    Assert.DoesNotContain("全匹配原币汇总", text);
                    Assert.DoesNotContain("globalUniqueSalesmanBuckets", text);
                }
            }
        }
    }

    [Fact]
    public async Task Export_姓名关键字_上下文表保留关键字_即使业务员列隐藏()
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

        // 只选币种列（隐藏业务员列），规范化关键字上下文仍保留在「报表口径」工作表
        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "currency" },
            filter: new SalesCommissionFilterDto { SalesmanName = "  张三  " })));

        var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheet(DynamicSalesCommissionReportRules.ContextSheetName);
        string? filterValue = null;
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row?.GetCell(0)?.StringCellValue == DynamicSalesCommissionReportRules.ContextFilterLabel)
            {
                filterValue = row.GetCell(1)?.StringCellValue;
                break;
            }
        }
        Assert.Equal("业务员姓名关键字 张三", filterValue);
    }
}



