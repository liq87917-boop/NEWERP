using System.Reflection;
using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-182 动态代理服务费月度汇总报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与行值、数值金额与公式前导文本转义、原币分别成组、草稿 / 已作废金额与已登记合计分开、
/// 仅导出当前页（单页 200 上限）、空页显式说明、无身份 / 无菜单授权 / 页大小超限 / 未知字段拒绝、业务员数据范围、只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyExcelTests
{
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

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
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "excel-user")
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

    // ==================== 1. 选定列顺序与数值金额 ====================

    [Fact]
    public async Task Export_选定列顺序与数值金额_原币分别成组()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-USD", c1.Id, 100m, currency: "USD");
        SeedStatement(db, "ASF-JPY", c1.Id, 1200m, currency: "JPY");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency", "registeredTotalAmount", "customerName" },
            PageSize = 100
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);

        Assert.Equal("币种", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("已登记原币合计", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("客户名称", sheet.GetRow(0).GetCell(2).StringCellValue);

        Assert.Equal(2, sheet.LastRowNum);
        var usdRow = (string)sheet.GetRow(1).GetCell(0).StringCellValue == "USD" ? 1 : 2;
        var jpyRow = usdRow == 1 ? 2 : 1;

        Assert.Equal("USD", sheet.GetRow(usdRow).GetCell(0).StringCellValue);
        Assert.Equal("JPY", sheet.GetRow(jpyRow).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(usdRow).GetCell(1).CellType);
        Assert.Equal(100.0, sheet.GetRow(usdRow).GetCell(1).NumericCellValue, 6);
        Assert.Equal(CellType.Numeric, sheet.GetRow(jpyRow).GetCell(1).CellType);
        Assert.Equal(1200.0, sheet.GetRow(jpyRow).GetCell(1).NumericCellValue, 6);
    }

    // ==================== 2. 状态金额口径（草稿 / 已作废与已登记合计分开） ====================

    [Fact]
    public async Task Export_状态金额分开_草稿与已作废不并入已登记合计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-R", c1.Id, 100m, status: AgencyServiceFeeStatementRules.StatusRecorded);
        SeedStatement(db, "ASF-D", c1.Id, 30m, status: AgencyServiceFeeStatementRules.StatusDraft);
        SeedStatement(db, "ASF-V", c1.Id, 20m, status: AgencyServiceFeeStatementRules.StatusVoided);

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new()
            {
                "registeredTotalAmount", "draftTotalAmount", "voidedTotalAmount",
                "registeredCount", "draftCount", "voidedCount",
            },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var row = sheet.GetRow(1);

        Assert.Equal(100.0, row.GetCell(0).NumericCellValue, 6);
        Assert.Equal(30.0, row.GetCell(1).NumericCellValue, 6);
        Assert.Equal(20.0, row.GetCell(2).NumericCellValue, 6);
        Assert.Equal(1.0, row.GetCell(3).NumericCellValue, 6);
        Assert.Equal(1.0, row.GetCell(4).NumericCellValue, 6);
        Assert.Equal(1.0, row.GetCell(5).NumericCellValue, 6);
    }

    // ==================== 3. 公式前导文本转义 ====================

    [Fact]
    public async Task Export_公式前导文本_前缀单引号保持字面()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "=1+1");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerName: "=1+1");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);

        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    // ==================== 4. 空页显式说明 ====================

    [Fact]
    public async Task Export_空页_工作簿含显式说明()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(1, sheet.LastRowNum);
        var note = sheet.GetRow(1).GetCell(0).StringCellValue;
        Assert.Contains("没有符合筛选条件", note);
        Assert.Contains("不代表收入或应收", note);
    }

    // ==================== 5. 仅导出当前页 + 页大小上限 ====================

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
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

        var page1 = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerId" }, Page = 1, PageSize = 2
        }));
        Assert.Equal(2, OpenWorkbook(page1.FileContents).GetSheetAt(0).LastRowNum);

        var page2 = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerId" }, Page = 2, PageSize = 2
        }));
        Assert.Equal(1, OpenWorkbook(page2.FileContents).GetSheetAt(0).LastRowNum);
    }

    // ==================== 6. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task Export_无客户资料菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicAgencyServiceFeeMonthlyReportRequest { Fields = new() { "currency", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 业务员数据范围 ====================

    [Fact]
    public async Task Export_受限制业务员_只导出其被分配客户()
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
            Fields = new() { "customerId" },
            PageSize = 100
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal((double)mine.Id, sheet.GetRow(1).GetCell(0).NumericCellValue, 6);
    }

    // ==================== 8. 只读 ====================

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 9. 接口路由与前端接线 ====================

    [Fact]
    public void 接口_导出路由契约()
    {
        var route = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/dynamic-agency-service-fee-monthly-report", route);

        var export = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetMethod(nameof(DynamicAgencyServiceFeeMonthlyReportController.Export))!
            .GetCustomAttributes<HttpPostAttribute>(true).ToList();
        Assert.Contains(export, a => a.Template == "export");
    }

    [Fact]
    public void 前端接线_导出按钮与导出接口路径齐备()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "agency-service-fee-monthly-summary.js"));
        Assert.Contains("/api/dynamic-agency-service-fee-monthly-report/export", js);
        Assert.Contains("function asfmsExportExcel(", js);
        Assert.Contains("asfmsExportExcel", js);
    }
}
