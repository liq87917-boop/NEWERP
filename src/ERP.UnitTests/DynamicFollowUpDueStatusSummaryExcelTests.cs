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
/// ERP-203 动态跟进提醒报表「筛选集状态汇总」Excel（只读、有界）单元测试。
/// 覆盖：汇总工作表只含 as-of 日期 / 筛选条件 / 范围口径标签与三项状态计数 + 合计（数值单元格），
/// 不含任何明细行或范围外数据；计数覆盖「分页前全量」匹配行（与分页无关）、尊重客户 Id / 到期状态筛选；
/// 受限制业务员看不到他人 / 匿名客户；文本公式注入转义（无公式单元格）；无菜单授权 / 授权撤销 /
/// 无效请求拒绝且不返回文件；全程只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicFollowUpDueStatusSummaryExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 15);

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

    private static ICell LabelCell(XSSFWorkbook wb, int row) => wb.GetSheetAt(0).GetRow(row).GetCell(0);
    private static ICell ValueCell(XSSFWorkbook wb, int row) => wb.GetSheetAt(0).GetRow(row).GetCell(1);

    private static void AssertNumericCount(XSSFWorkbook wb, int row, string label, double expected)
    {
        Assert.Equal(label, LabelCell(wb, row).StringCellValue);
        var cell = ValueCell(wb, row);
        Assert.Equal(CellType.Numeric, cell.CellType);
        Assert.Equal(expected, cell.NumericCellValue);
    }

    // ==================== 1. 汇总工作表结构与无明细行 ====================

    [Fact]
    public async Task ExportStatusSummary_含asOf筛选范围标签与数值计数_不含明细行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf.AddDays(-1));   // 已逾期
        SeedFollowUp(db, "FU-2", customer.Id, "客户", AsOf);               // 今日到期
        SeedFollowUp(db, "FU-3", customer.Id, "客户", AsOf.AddDays(1));    // 即将到期

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(1, workbook.NumberOfSheets);
        var sheet = workbook.GetSheetAt(0);
        Assert.Equal(DynamicFollowUpDueReportRules.SummarySheetName, sheet.SheetName);

        // 表头：项目 / 值
        Assert.Equal("项目", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("值", sheet.GetRow(0).GetCell(1).StringCellValue);

        // as-of 日期 / 筛选条件 / 范围口径标签
        Assert.Equal(DynamicFollowUpDueReportRules.SummaryAsOfLabel, LabelCell(workbook, 1).StringCellValue);
        Assert.Equal("2026-09-15", ValueCell(workbook, 1).StringCellValue);

        Assert.Equal(DynamicFollowUpDueReportRules.SummaryFilterLabel, LabelCell(workbook, 2).StringCellValue);
        var filter = ValueCell(workbook, 2).StringCellValue;
        Assert.Contains("as-of 2026-09-15", filter);
        Assert.Contains("提前天数 7", filter);
        Assert.Contains("全部状态", filter);

        Assert.Equal(DynamicFollowUpDueReportRules.SummaryScopeLabel, LabelCell(workbook, 3).StringCellValue);
        Assert.Equal(DynamicFollowUpDueReportRules.SummaryScopeText, ValueCell(workbook, 3).StringCellValue);

        // 三项状态计数 + 合计（数值单元格）
        AssertNumericCount(workbook, 4, DynamicFollowUpDueReportRules.DueOverdueText, 1);
        AssertNumericCount(workbook, 5, DynamicFollowUpDueReportRules.DueTodayText, 1);
        AssertNumericCount(workbook, 6, DynamicFollowUpDueReportRules.DueUpcomingText, 1);
        AssertNumericCount(workbook, 7, DynamicFollowUpDueReportRules.SummaryTotalLabel, 3);

        // 不含任何明细行
        Assert.Equal(7, sheet.LastRowNum);
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            for (var c = 0; c < row.LastCellNum; c++)
            {
                var cell = row.GetCell(c);
                Assert.NotEqual(CellType.Formula, cell.CellType);
                if (cell.CellType == CellType.String)
                    Assert.DoesNotContain("FU-", cell.StringCellValue);
            }
        }
    }

    // ==================== 2. 计数覆盖全量匹配行（与分页无关） ====================

    [Fact]
    public async Task ExportStatusSummary_计数覆盖分页前全量_与页码页大小无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 1; i <= 5; i++)
            SeedFollowUp(db, $"FU-{i}", customer.Id, "客户", AsOf.AddDays(-1));   // 全部已逾期

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            Page = 1,
            PageSize = 2
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        AssertNumericCount(workbook, 4, DynamicFollowUpDueReportRules.DueOverdueText, 5);
        AssertNumericCount(workbook, 5, DynamicFollowUpDueReportRules.DueTodayText, 0);
        AssertNumericCount(workbook, 6, DynamicFollowUpDueReportRules.DueUpcomingText, 0);
        AssertNumericCount(workbook, 7, DynamicFollowUpDueReportRules.SummaryTotalLabel, 5);
    }


    // ==================== 3. 尊重客户 Id / 到期状态筛选 ====================

    [Fact]
    public async Task ExportStatusSummary_尊重客户Id与到期状态筛选()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var a = SeedCustomer(db, "C001", "客户A");
        var b = SeedCustomer(db, "C002", "客户B");

        SeedFollowUp(db, "FU-A1", a.Id, "客户A", AsOf.AddDays(-1));   // A：已逾期
        SeedFollowUp(db, "FU-A2", a.Id, "客户A", AsOf);               // A：今日到期
        SeedFollowUp(db, "FU-B1", b.Id, "客户B", AsOf.AddDays(1));    // B：即将到期

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var byCustomer = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            CustomerId = a.Id
        }));
        using (var wb = OpenWorkbook(byCustomer.FileContents))
        {
            AssertNumericCount(wb, 4, DynamicFollowUpDueReportRules.DueOverdueText, 1);
            AssertNumericCount(wb, 5, DynamicFollowUpDueReportRules.DueTodayText, 1);
            AssertNumericCount(wb, 6, DynamicFollowUpDueReportRules.DueUpcomingText, 0);
            AssertNumericCount(wb, 7, DynamicFollowUpDueReportRules.SummaryTotalLabel, 2);
        }

        var byStatus = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            DueStatus = DynamicFollowUpDueReportRules.DueOverdue
        }));
        using (var wb = OpenWorkbook(byStatus.FileContents))
        {
            AssertNumericCount(wb, 4, DynamicFollowUpDueReportRules.DueOverdueText, 1);
            AssertNumericCount(wb, 5, DynamicFollowUpDueReportRules.DueTodayText, 0);
            AssertNumericCount(wb, 6, DynamicFollowUpDueReportRules.DueUpcomingText, 0);
            AssertNumericCount(wb, 7, DynamicFollowUpDueReportRules.SummaryTotalLabel, 1);
        }
    }

    // ==================== 4. 数据范围（受限制业务员） ====================

    [Fact]
    public async Task ExportStatusSummary_受限制业务员_看不到他人或匿名客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sales1", "Sales");
        var employee = SeedEmployee(db, "sales1");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "他人客户", empId: null);

        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", AsOf.AddDays(-1));   // 我的：已逾期
        SeedFollowUp(db, "FU-OTHER", other.Id, "他人客户", AsOf);             // 他人：今日到期

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        AssertNumericCount(workbook, 4, DynamicFollowUpDueReportRules.DueOverdueText, 1);
        AssertNumericCount(workbook, 5, DynamicFollowUpDueReportRules.DueTodayText, 0);
        AssertNumericCount(workbook, 6, DynamicFollowUpDueReportRules.DueUpcomingText, 0);
        AssertNumericCount(workbook, 7, DynamicFollowUpDueReportRules.SummaryTotalLabel, 1);
    }


    // ==================== 5. 拒绝且不返回文件 ====================

    [Fact]
    public async Task ExportStatusSummary_授权撤销_拒绝且不返回文件()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportStatusSummary(
            new DynamicFollowUpDueReportRequest
            {
                Fields = new List<string> { "customerName" },
                AsOfDate = AsOf,
                AheadDays = 7
            }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportStatusSummary_无效请求_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportStatusSummary(
            new DynamicFollowUpDueReportRequest
            {
                Fields = new List<string> { "customerName" },
                AsOfDate = AsOf,
                AheadDays = 7,
                PageSize = 201
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    // ==================== 6. 公式安全文本 ====================

    [Fact]
    public async Task ExportStatusSummary_公式前导关键字_转义为字面文本非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf, subject: "=SUM(A1)");

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            Keyword = "=SUM(A1)"
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            for (var c = 0; c < row.LastCellNum; c++)
                Assert.NotEqual(CellType.Formula, row.GetCell(c).CellType);
        }

        var filter = ValueCell(workbook, 2).StringCellValue;
        Assert.StartsWith("as-of ", filter);
        Assert.Contains("=SUM(A1)", filter);
    }

    [Fact]
    public void BuildSummaryFilterContext_渲染规范化筛选上下文_且不泄露SQL()
    {
        var context = DynamicFollowUpDueReportRules.BuildSummaryFilterContext(
            new DynamicFollowUpDueReportRequest
            {
                AsOfDate = AsOf,
                AheadDays = 7,
                DueStatus = "overdue",
                CustomerId = 42,
                Keyword = " 义乌小商品 "
            });

        Assert.Contains("as-of 2026-09-15", context);
        Assert.Contains("提前天数 7", context);
        Assert.Contains("已逾期", context);
        Assert.Contains("客户 Id 42", context);
        Assert.Contains("关键字 义乌小商品", context);
        Assert.DoesNotContain("FromSql", context);
        Assert.DoesNotContain("SELECT", context);
    }

    [Fact]
    public async Task ExportStatusSummary_只读_不写库不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = ExportOk(await ctl.ExportStatusSummary(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7
        }));

        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

