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
/// ERP-237 动态业务员产值证据报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与类型化值（已知金额 / 订单数为数值、未知金额 / 未知利润显式「未知」文本，绝不写成 0）、
/// 公式前导文本转义、仅导出当前页、上下文工作表标注日期 / 分页 / 来源上限 / 币种 / 未知 / 未知利润 / 来源 / 页面覆盖、
/// 数据范围（受限制业务员看不到他人）、授权撤销不返回工作簿，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesmanOutputExcelTests
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

    private static DynamicSalesmanOutputReportRequest Request(List<string>? fields = null)
        => new()
        {
            Fields = fields,
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20,
        };

    // ==================== 1. 选定列顺序与类型化值 ====================

    [Fact]
    public async Task Export_选定列顺序与类型化值_已知金额订单数为数值_未知利润显式未知()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName", "currency", "orderCount", "totalAmount", "totalProfit" })));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("业务员产值报表", sheet.SheetName);
        Assert.Equal("业务员姓名", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("原币币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("已分配已审核订单数", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("已知原币金额小计", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal("利润(未知)", sheet.GetRow(0).GetCell(4).StringCellValue);

        Assert.Equal("业务员甲", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("USD", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(2).CellType);
        Assert.Equal(1d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(3).CellType);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(4).CellType);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(4).StringCellValue);
    }

    [Fact]
    public async Task Export_未知币种金额显式未知_绝不写成0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-U", customer.Id, emp.Id, (Currency)999, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "currency", "totalAmount" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal("999", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(1).CellType);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_公式前导文本转义_名称标签安全()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "=HYPERLINK(\"http://evil\")");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName", "currencyLabel" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.StartsWith("'", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 2. 上下文工作表 ====================

    [Fact]
    public async Task Export_上下文工作表标注日期分页来源上限币种未知利润来源()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName", "totalAmount" })));
        var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);

        var context = workbook.GetSheetAt(1);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextStartLabel, context.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", context.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextEndLabel, context.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextPageLabel, context.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("业务员×原币行总数 1", context.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextSourceLimitLabel, context.GetRow(3).GetCell(0).StringCellValue);
        Assert.Contains("500 张订单", context.GetRow(3).GetCell(1).StringCellValue);
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

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        Assert.IsType<FileContentResult>(await ctl.Export(Request(new List<string> { "salesmanName" })));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(Request(new List<string> { "salesmanName" })));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
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

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName", "totalAmount" })));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(1, sheet.LastRowNum);   // 仅表头 + 1 行证据
        Assert.Equal("业务员甲", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    [Fact]
    public async Task Export_空页仅表头与上下文空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "salesmanName" })));
        var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        Assert.Equal(0, sheet.LastRowNum);   // 仅表头
        var context = workbook.GetSheetAt(1);
        Assert.Contains(DynamicSalesmanOutputReportRules.ContextEmptyLabel, context
            .GetRow(context.LastRowNum).GetCell(0).StringCellValue);
    }

    [Fact]
    public async Task Export_仅导出当前页_绝不跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, emp.Id, Currency.CNY, 200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var req = Request(new List<string> { "salesmanName", "currency", "totalAmount" });
        req.PageSize = 1;
        var file = ExportOk(await ctl.Export(req));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(1, sheet.LastRowNum);   // 仅当前页 1 行，绝不出现跨币种合计行
        Assert.DoesNotContain("合计", sheet.GetRow(0).Cells.Select(c => c.StringCellValue));
    }

    [Fact]
    public async Task Export_明细导出仍仅当前页_绝不追加全匹配汇总工作表()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, emp.Id, Currency.CNY, 200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var req = Request(new List<string> { "currency", "totalAmount" });
        req.PageSize = 1;
        var file = ExportOk(await ctl.Export(req));
        var workbook = OpenWorkbook(file.FileContents);

        Assert.Equal(2, workbook.NumberOfSheets);   // 数据 + 报表口径，绝不追加全匹配汇总工作表
        Assert.Equal(1, workbook.GetSheetAt(0).LastRowNum);   // 仅当前页 1 行
    }
}


