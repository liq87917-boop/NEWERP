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
/// ERP-195 动态跟进提醒报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与行值（数字 / 日期保留）、公式前导文本转义（保持字面、非公式单元格）、
/// 仅导出当前页（单页上限 200）、数据范围（受限制业务员看不到他人 / 匿名客户）、
/// 无身份 / 无菜单授权 / 授权撤销 / 页大小超限 / 未知字段拒绝、空页仅表头，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicFollowUpDueReportExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 15);

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
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static CustomerFollowUp SeedFollowUp(
        ErpDbContext db, string followNo, long? customerId, string customerName,
        DateTime? nextFollowDate, string subject = "跟进主题")
    {
        var follow = new CustomerFollowUp
        {
            FollowNo = followNo,
            FollowDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            Subject = subject,
            SalesmanName = "张三",
            Result = "待跟进",
            NextFollowDate = nextFollowDate,
            IsDeleted = false
        };
        db.CustomerFollowUps.Add(follow);
        db.SaveChanges();
        return follow;
    }

    /// <summary>播种一个拥有「跟进提醒」菜单授权的登录用户（可选系统内置角色 → 特权不过滤数据范围）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicFollowUpDueReportRules.RequiredMenuCode).Id);
        return user;
    }

    private static DynamicFollowUpDueReportController NewController(ErpDbContext db)
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


    // ==================== 1. 选定列顺序与行值 ====================

    [Fact]
    public async Task Export_选定列顺序与行值_数字与日期保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo", "followDate", "dueDays", "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            Page = 1,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("跟进编号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("跟进日期", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("到期天数", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("客户名称", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal(1, sheet.LastRowNum);

        Assert.Equal("FU-1", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01 00:00", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(2).CellType);
        Assert.Equal(0d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal("客户", sheet.GetRow(1).GetCell(3).StringCellValue);
    }

    // ==================== 2. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf, subject: "=1+1");

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" },
            AsOfDate = AsOf,
            AheadDays = 7
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.NotEqual(CellType.Formula, cell.CellType);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    [Fact]
    public void EscapeFormulaLeading_危险字符转义_普通值原样()
    {
        Assert.False(DynamicFollowUpDueReportRules.IsFormulaLeading(null));
        Assert.False(DynamicFollowUpDueReportRules.IsFormulaLeading(""));
        Assert.False(DynamicFollowUpDueReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicFollowUpDueReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicFollowUpDueReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicFollowUpDueReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicFollowUpDueReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicFollowUpDueReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicFollowUpDueReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicFollowUpDueReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicFollowUpDueReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicFollowUpDueReportRules.EscapeFormulaLeading(123));

        var exportRow = DynamicFollowUpDueReportRules.BuildExportRow(
            new Dictionary<string, object?> { ["subject"] = "=1+1", ["dueDays"] = 3 });
        Assert.Equal("'=1+1", exportRow["subject"]);
        Assert.Equal(3, exportRow["dueDays"]);
    }

    // ==================== 3. 仅导出当前页 + 页大小上限 ====================

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 5; i++)
            SeedFollowUp(db, $"FU-{i}", customer.Id, "客户", AsOf.AddDays(-i));

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            Page = 1,
            PageSize = 2
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(2, sheet.LastRowNum);
    }

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("跟进编号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }


    // ==================== 4. 数据范围（受限制业务员） ====================

    [Fact]
    public async Task Export_受限制业务员_只导出被分配客户_他人与空客户不可见()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", AsOf, subject: "我的");
        SeedFollowUp(db, "FU-OTHER", other.Id, "别人的客户", AsOf, subject: "他人");
        SeedFollowUp(db, "FU-NULL", null, "匿名客户", AsOf, subject: "匿名");

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal("我的客户", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 5. 无效输入（读取前拒绝） ====================

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicFollowUpDueReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicFollowUpDueReportRequest { Fields = new List<string> { "customerName", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 未授权 / 撤销授权 / 只读 ====================

    [Fact]
    public async Task Export_无跟进提醒菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nomenu-user");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicFollowUpDueReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_授权撤销_菜单授权移除后立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoked-user", "Sales");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicFollowUpDueReportRequest { AsOfDate = AsOf, AheadDays = 7 }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicFollowUpDueReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

