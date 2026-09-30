using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-188 动态代理服务费月度汇总报表「当前页按对账月份 / 客户分组计数 Excel 导出」单元测试。
/// <para>语义：<see cref="DynamicAgencyServiceFeeMonthlyReportController.Export"/> 复用同一有界、已授权预览，
/// none 模式保持既有单工作表不变；month / customer 分组模式在选定列证据工作表之后追加「分组计数」工作表
/// （复用 ERP-184 同一批有界、已授权分组计数），分组标签 + 原币 + 月度行数与已登记 / 草稿 / 已作废 / 总计张数
/// 全部为数值单元格，文本单元格做公式注入转义，并显式标注空页 / 截断 / 仅本页；不含金额列、不声明跨页合计。</para>
/// <para>覆盖：按月份 / 按客户分组、none 不变、混合币种、隐藏维度列、公式前导标签、空页、截断页、不含金额列、
/// 无身份 / 无菜单授权 / 无效分组键拒绝、业务员数据范围、只读不写库。全部使用内存数据库（TestDbFactory），
/// 不连接 SQL Server、不执行任何 SQL。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyGroupedExcelTests
{
    private const string GroupCountSheetName = "分组计数";

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

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    /// <summary>播种一个「客户资料菜单授权 + 系统内置角色（特权）」的登录用户并返回其用户 Id（特权 → 不过滤客户）</summary>
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "grouped-excel-user")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount,
        string currency = "USD", int status = AgencyServiceFeeStatementRules.StatusRecorded,
        DateTime? statementDate = null, bool deleted = false,
        string customerCode = "C001", string customerName = "义乌进出口")
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = customerCode,
            CustomerName = customerName,
            Currency = currency,
            StatementDate = statementDate ?? new DateTime(2026, 9, 1),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            TotalAmount = totalAmount,
            Status = status,
            RecordedAt = status == AgencyServiceFeeStatementRules.StatusRecorded
                ? new DateTime(2026, 9, 2)
                : null,
            RecordedBy = status == AgencyServiceFeeStatementRules.StatusRecorded ? "张三" : string.Empty,
            IsDeleted = deleted
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private static DynamicAgencyServiceFeeMonthlyReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicAgencyServiceFeeMonthlyReportController(db);
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

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

    private static ISheet RequireSheet(XSSFWorkbook workbook, string name)
    {
        var sheet = workbook.GetSheet(name);
        Assert.NotNull(sheet);
        return sheet!;
    }

    /// <summary>分组计数工作表表头：分组标签 + 原币 + 月度行数 + 已登记 / 草稿 / 已作废 / 总计张数（不含金额列）</summary>
    private static void AssertHeader(ISheet sheet)
    {
        var header = sheet.GetRow(0);
        Assert.Equal("分组标签", header.GetCell(0).StringCellValue);
        Assert.Equal("原币", header.GetCell(1).StringCellValue);
        Assert.Equal("月度行数", header.GetCell(2).StringCellValue);
        Assert.Equal("已登记张数", header.GetCell(3).StringCellValue);
        Assert.Equal("草稿张数", header.GetCell(4).StringCellValue);
        Assert.Equal("已作废张数", header.GetCell(5).StringCellValue);
        Assert.Equal("对账单总张数", header.GetCell(6).StringCellValue);
    }

    // ==================== 1. none 保持既有工作簿 ====================

    [Fact]
    public async Task Export_none_保持既有单工作表不变()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName" },
            PageSize = 10,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(1, workbook.NumberOfSheets);
        Assert.Equal("代理服务费月度汇总", workbook.GetSheetAt(0).SheetName);
        Assert.Equal("客户名称", workbook.GetSheetAt(0).GetRow(0).GetCell(0).StringCellValue);
        Assert.Null(workbook.GetSheet(GroupCountSheetName));
    }

    // ==================== 2. 按对账月份分组 ====================

    [Fact]
    public async Task Export_按对账月份分组_追加分组计数工作表()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一",
            statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-2", c1.Id, 30m, status: AgencyServiceFeeStatementRules.StatusDraft,
            customerCode: "C001", customerName: "客户一", statementDate: new DateTime(2026, 8, 20));
        SeedStatement(db, "ASF-3", c2.Id, 50m, customerCode: "C002", customerName: "客户二",
            statementDate: new DateTime(2026, 8, 25));
        SeedStatement(db, "ASF-4", c1.Id, 20m, customerCode: "C001", customerName: "客户一",
            statementDate: new DateTime(2026, 9, 1));

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal("代理服务费月度汇总", workbook.GetSheetAt(0).SheetName);

        var sheet = RequireSheet(workbook, GroupCountSheetName);
        AssertHeader(sheet);

        var aug = sheet.GetRow(1);
        Assert.Equal("2026-08", aug.GetCell(0).StringCellValue);
        Assert.Equal("USD", aug.GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, aug.GetCell(2).CellType);
        Assert.Equal(2.0, aug.GetCell(2).NumericCellValue, 6);
        Assert.Equal(2.0, aug.GetCell(3).NumericCellValue, 6);
        Assert.Equal(1.0, aug.GetCell(4).NumericCellValue, 6);
        Assert.Equal(0.0, aug.GetCell(5).NumericCellValue, 6);
        Assert.Equal(3.0, aug.GetCell(6).NumericCellValue, 6);

        var sep = sheet.GetRow(2);
        Assert.Equal("2026-09", sep.GetCell(0).StringCellValue);
        Assert.Equal("USD", sep.GetCell(1).StringCellValue);
        Assert.Equal(1.0, sep.GetCell(2).NumericCellValue, 6);
        Assert.Equal(1.0, sep.GetCell(3).NumericCellValue, 6);
        Assert.Equal(0.0, sep.GetCell(4).NumericCellValue, 6);
        Assert.Equal(0.0, sep.GetCell(5).NumericCellValue, 6);
        Assert.Equal(1.0, sep.GetCell(6).NumericCellValue, 6);

        Assert.StartsWith("本工作表只统计当前授权预览页", sheet.GetRow(3).GetCell(0).StringCellValue);
    }

    // ==================== 3. 按客户分组 ====================

    [Fact]
    public async Task Export_按客户分组_追加分组计数工作表()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一",
            statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-2", c1.Id, 30m, status: AgencyServiceFeeStatementRules.StatusDraft,
            customerCode: "C001", customerName: "客户一", statementDate: new DateTime(2026, 9, 1));
        SeedStatement(db, "ASF-3", c2.Id, 20m, status: AgencyServiceFeeStatementRules.StatusVoided,
            customerCode: "C002", customerName: "客户二", statementDate: new DateTime(2026, 8, 15));

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "statementMonthText" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        AssertHeader(sheet);

        var row1 = sheet.GetRow(1);
        Assert.Equal("客户一", row1.GetCell(0).StringCellValue);
        Assert.Equal("USD", row1.GetCell(1).StringCellValue);
        Assert.Equal(2.0, row1.GetCell(2).NumericCellValue, 6);
        Assert.Equal(1.0, row1.GetCell(3).NumericCellValue, 6);
        Assert.Equal(1.0, row1.GetCell(4).NumericCellValue, 6);
        Assert.Equal(0.0, row1.GetCell(5).NumericCellValue, 6);
        Assert.Equal(2.0, row1.GetCell(6).NumericCellValue, 6);

        var row2 = sheet.GetRow(2);
        Assert.Equal("客户二", row2.GetCell(0).StringCellValue);
        Assert.Equal(1.0, row2.GetCell(2).NumericCellValue, 6);
        Assert.Equal(0.0, row2.GetCell(3).NumericCellValue, 6);
        Assert.Equal(0.0, row2.GetCell(4).NumericCellValue, 6);
        Assert.Equal(1.0, row2.GetCell(5).NumericCellValue, 6);
        Assert.Equal(1.0, row2.GetCell(6).NumericCellValue, 6);
    }

    // ==================== 4. 混合币种分别成组 ====================

    [Fact]
    public async Task Export_混合币种_分组计数分别成组()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-USD", c1.Id, 100m, currency: "USD",
            customerCode: "C001", customerName: "客户一", statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-JPY", c1.Id, 1200m, currency: "JPY",
            customerCode: "C001", customerName: "客户一", statementDate: new DateTime(2026, 8, 15));

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        AssertHeader(sheet);

        var currencies = new[]
        {
            sheet.GetRow(1).GetCell(1).StringCellValue,
            sheet.GetRow(2).GetCell(1).StringCellValue,
        };
        Assert.Contains("USD", currencies);
        Assert.Contains("JPY", currencies);
        Assert.Equal(1.0, sheet.GetRow(1).GetCell(2).NumericCellValue, 6);
        Assert.Equal(1.0, sheet.GetRow(2).GetCell(2).NumericCellValue, 6);
    }

    // ==================== 5. 隐藏维度列 ====================

    [Fact]
    public async Task Export_隐藏维度列_分组计数仍来自当前页源行()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一");
        SeedStatement(db, "ASF-2", c2.Id, 200m, customerCode: "C002", customerName: "客户二");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" }, // 不选择客户维度列，分组计数仍应来自同一当前页源行
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        Assert.Equal("客户一", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("客户二", sheet.GetRow(2).GetCell(0).StringCellValue);
    }

    // ==================== 6. 公式前导标签转义 ====================

    [Fact]
    public async Task Export_分组标签公式前导_转义()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "=1+1");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "=1+1");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    // ==================== 7. 空页 ====================

    [Fact]
    public async Task Export_空页_分组计数含空页说明()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 10,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        AssertHeader(sheet);
        Assert.Contains("空页", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.StartsWith("本工作表只统计当前授权预览页", sheet.GetRow(2).GetCell(0).StringCellValue);
    }

    // ==================== 8. 截断页 ====================

    [Fact]
    public async Task Export_截断页_分组计数含截断说明()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        var c3 = SeedCustomer(db, "C003", "客户三");
        SeedStatement(db, "ASF-1", c1.Id, 10m, customerCode: "C001", customerName: "客户一");
        SeedStatement(db, "ASF-2", c2.Id, 20m, customerCode: "C002", customerName: "客户二");
        SeedStatement(db, "ASF-3", c3.Id, 30m, customerCode: "C003", customerName: "客户三");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 2,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        AssertHeader(sheet);

        // 表头 + 两个分组行 + 截断说明 + 仅本页说明
        Assert.Equal(4, sheet.LastRowNum);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(2).CellType);
        Assert.Equal(CellType.Numeric, sheet.GetRow(2).GetCell(2).CellType);
        Assert.Contains("截断", sheet.GetRow(3).GetCell(0).StringCellValue);
        Assert.StartsWith("本工作表只统计当前授权预览页", sheet.GetRow(4).GetCell(0).StringCellValue);
    }

    // ==================== 9. 不含金额列 ====================

    [Fact]
    public async Task Export_分组工作表不含金额列()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        var header = sheet.GetRow(0);
        Assert.Equal(7, header.LastCellNum);

        var headers = Enumerable.Range(0, header.LastCellNum)
            .Select(i => header.GetCell(i).StringCellValue)
            .ToList();
        Assert.DoesNotContain(headers, h => h.Contains("金额") || h.Contains("合计"));
    }

    // ==================== 10. 拒绝（fail closed） ====================

    [Fact]
    public async Task Export_无效分组键_读取源数据前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_无菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer
            }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth
            }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 11. 业务员数据范围 ====================

    [Fact]
    public async Task Export_受限制业务员_分组计数只含被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Role-alice");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode).Id);

        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedStatement(db, "ASF-MY", mine.Id, 100m, customerCode: "C001", customerName: "我的客户");
        SeedStatement(db, "ASF-OTHER", other.Id, 999m, customerCode: "C002", customerName: "别人的客户");

        var ctl = BuildController(db, user.Id);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = RequireSheet(workbook, GroupCountSheetName);
        Assert.Equal("我的客户", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(1.0, sheet.GetRow(1).GetCell(2).NumericCellValue, 6);
        Assert.StartsWith("本工作表只统计当前授权预览页", sheet.GetRow(2).GetCell(0).StringCellValue);
    }

    // ==================== 12. 只读 ====================

    [Fact]
    public async Task Export_分组导出_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 10,
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }
}





