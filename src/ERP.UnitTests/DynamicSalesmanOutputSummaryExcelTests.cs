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
/// ERP-240 动态业务员产值证据报表「全匹配」汇总 Excel 导出（只读、有界）单元测试。
/// 覆盖：独立重新校验身份 / 菜单授权 / 数据范围 / 字段 / 日期 / 分页 / 应用筛选（无需先预览、绝不含业务员 / 客户明细行）、
/// 已知签名原币金额 / 去重业务员数 / 已分配已审核订单数按类型写入数值、未知金额 / 利润 / 利润率显式「未知」文本（绝不写成 0）、
/// 未知币种原始键 / 订单数与利润依据可见、上下文工作表标注日期 / 来源上限 / 覆盖范围 / 来源证据 / 未知口径 / 利润依据 / 应用筛选，
/// 越界详情页仍导出完整匹配汇总，以及授权撤销 / 只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesmanOutputSummaryExcelTests
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

    private static FileContentResult ExportSummaryOk(IActionResult result)
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

    private static DynamicSalesmanOutputReportRequest Request(
        List<string>? fields = null,
        SalesmanOutputFilterDto? filter = null,
        int page = 1,
        int pageSize = 20)
        => new()
        {
            Fields = fields,
            Filter = filter,
            Start = Start,
            End = End,
            Page = page,
            PageSize = pageSize,
        };

    // ==================== 1. 工作簿结构 ====================

    [Fact]
    public async Task ExportSummary_工作簿仅含汇总与口径工作表_无业务员客户明细行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 选定字段仍被校验，但绝不改变汇总证据（汇总覆盖全部匹配行）
        var file = ExportSummaryOk(await ctl.ExportSummary(Request(fields: new List<string> { "currency" })));
        var workbook = OpenWorkbook(file.FileContents);

        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicSalesmanOutputSummaryRules.SummaryCurrencySheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);

        var sheet = workbook.GetSheetAt(0);
        Assert.Equal(2, sheet.LastRowNum);   // 表头 + 2 个币种汇总行，绝不含业务员 / 客户明细行

        var headers = sheet.GetRow(0).Cells.Select(c => c.StringCellValue).ToList();
        Assert.DoesNotContain("业务员", headers);
        Assert.DoesNotContain("客户", headers);
    }

    // ==================== 2. 类型化值 / 未知显式 ====================

    [Fact]
    public async Task ExportSummary_已知金额订单数业务员数类型化_未知金额利润利润率显式文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, -200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportSummaryOk(await ctl.ExportSummary(Request()));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal("USD", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("USD 美元", sheet.GetRow(1).GetCell(1).StringCellValue);

        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(2).CellType);   // 业务员数
        Assert.Equal(2d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(3).CellType);   // 已分配已审核订单数
        Assert.Equal(2d, sheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(4).CellType);   // 签名金额 1000 + (-200)
        Assert.Equal(800d, sheet.GetRow(1).GetCell(4).NumericCellValue);

        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(5).CellType);    // 利润未知
        Assert.Equal("未知", sheet.GetRow(1).GetCell(5).StringCellValue);
        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(6).CellType);    // 利润率未知
        Assert.Equal("未知", sheet.GetRow(1).GetCell(6).StringCellValue);
    }

    [Fact]
    public async Task ExportSummary_未知币种原币代码订单数与利润依据可见_金额未知绝不写成零()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-U", customer.Id, emp.Id, (Currency)999, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportSummaryOk(await ctl.ExportSummary(Request()));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal("999", sheet.GetRow(1).GetCell(0).StringCellValue);            // 未知原始键可见
        Assert.Equal(SalesmanOutputEvidenceRules.UnknownCurrencyGroup, sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(3).CellType);        // 订单数仍为数值
        Assert.Equal(1d, sheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(4).CellType);         // 未知金额显式文本
        Assert.Equal("未知", sheet.GetRow(1).GetCell(4).StringCellValue);
        Assert.Contains("利润未知", sheet.GetRow(1).GetCell(8).StringCellValue);    // 利润依据可见
    }

    // ==================== 3. 上下文工作表 ====================

    [Fact]
    public async Task ExportSummary_上下文工作表含日期来源上限覆盖来源利润依据与筛选()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var filter = new SalesmanOutputFilterDto { CustomerId = customer.Id };
        var file = ExportSummaryOk(await ctl.ExportSummary(Request(filter: filter)));
        var context = OpenWorkbook(file.FileContents).GetSheetAt(1);

        Assert.Equal(DynamicSalesmanOutputReportRules.ContextStartLabel, context.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", context.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextEndLabel, context.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextSourceLimitLabel, context.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("500 张订单", context.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputSummaryRules.ContextCoverageLabel, context.GetRow(3).GetCell(0).StringCellValue);
        Assert.Contains("已分配业务员", context.GetRow(3).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextSourceLabel, context.GetRow(4).GetCell(0).StringCellValue);
        Assert.Contains("已审核", context.GetRow(4).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextUnknownLabel, context.GetRow(5).GetCell(0).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputSummaryRules.ContextProfitBasisLabel, context.GetRow(6).GetCell(0).StringCellValue);
        Assert.Contains("利润未知", context.GetRow(6).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextFilterLabel, context.GetRow(7).GetCell(0).StringCellValue);
        Assert.Contains("客户 Id", context.GetRow(7).GetCell(1).StringCellValue);
        Assert.Equal(DynamicSalesmanOutputReportRules.ContextReadOnlyLabel, context.GetRow(8).GetCell(0).StringCellValue);
    }

    // ==================== 4. 越界详情页 / 空来源 ====================

    [Fact]
    public async Task ExportSummary_越界详情页仍导出完整匹配汇总_与当前页无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-1", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-2", customer.Id, emp2.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-3", customer.Id, emp1.Id, Currency.CNY, 500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 详情页越界（第 9 页，每页 1 条），汇总仍覆盖全部匹配行
        var file = ExportSummaryOk(await ctl.ExportSummary(Request(
            fields: new List<string> { "currency" }, page: 9, pageSize: 1)));
        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal(2, sheet.LastRowNum);   // 表头 + CNY / USD 两个币种汇总行

        var rows = new List<IRow>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
            rows.Add(sheet.GetRow(r));

        var usd = Assert.Single(rows.Where(r => r.GetCell(0).StringCellValue == "USD"));
        Assert.Equal(800d, usd.GetCell(4).NumericCellValue);
        var cny = Assert.Single(rows.Where(r => r.GetCell(0).StringCellValue == "CNY"));
        Assert.Equal(500d, cny.GetCell(4).NumericCellValue);
    }

    [Fact]
    public async Task ExportSummary_空来源_仍返回汇总工作簿与空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportSummaryOk(await ctl.ExportSummary(Request()));
        var workbook = OpenWorkbook(file.FileContents);

        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(0, workbook.GetSheetAt(0).LastRowNum);   // 仅表头

        var context = workbook.GetSheetAt(1);
        Assert.Contains(DynamicSalesmanOutputReportRules.ContextEmptyLabel,
            context.GetRow(context.LastRowNum - 1).GetCell(0).StringCellValue);
    }

    // ==================== 5. 授权 / 边界 / 只读 ====================

    [Fact]
    public async Task ExportSummary_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummary(Request(fields: new List<string> { "orderNo" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_授权撤销_不返回工作簿()
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
        Assert.IsType<FileContentResult>(await ctl.ExportSummary(Request()));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummary_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = ExportSummaryOk(await ctl.ExportSummary(Request()));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ExportSummary_来源超限_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }
}
