using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-256）「全匹配」每日证据汇总 Excel 导出（只读、有界、作用域化）单元测试。
/// 覆盖：固定每日汇总工作表列与有符号数值 / 零 / 负数（不按金额格式化）、日期规范化、期间合计与口径工作表必含口径、
/// 多页与隐藏字段下仍覆盖全部匹配证据、绝无客户 / 柜号 / 清单明细行或全局去重客户合计、公式前导筛选 / 口径字面转义、
/// 空证据工作簿显式标注、授权撤销 / 无身份 / 无效输入 / 来源超限不返回文件、受限制业务员不泄露他人，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicContainerStatsSummaryExcelTests
{
    private const string MenuCode = "container-stats";
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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static ContainerLoadingList SeedList(
        ErpDbContext db, string loadingListNo, long customerId, string containerNo,
        DateTime? loadingDate = null, DocumentStatus status = DocumentStatus.Approved, bool deleted = false,
        decimal cartons = 1m, decimal weight = 2m, decimal volume = 3m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = loadingDate ?? new DateTime(2026, 9, 10),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
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

    private static DynamicContainerStatsReportController BuildController(ErpDbContext db, long? userId)
        => new(db, new ReportService(db));

    private static DynamicContainerStatsReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        DynamicContainerStatsReportFilterDto? filter = null)
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

    private static ReportDtos.ContainerStatsItem Item(
        DateTime date, string containerNo, int loadingListCount = 1, bool blank = false,
        decimal cartons = 0m, decimal weight = 0m, decimal volume = 0m)
        => new()
        {
            BucketKey = $"{date:yyyyMMdd}|{containerNo}",
            LoadingDate = date,
            ContainerNo = containerNo,
            ContainerNoBlank = blank,
            LoadingListCount = loadingListCount,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume,
        };

    private static double NumericValue(ISheet sheet, string label)
    {
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row?.GetCell(0)?.StringCellValue == label)
                return row.GetCell(1).NumericCellValue;
        }
        throw new Xunit.Sdk.XunitException($"未找到标签：{label}");
    }


    // ==================== 1. 每日汇总工作表 ====================

    [Fact]
    public async Task 导出汇总_每日工作表_固定列_日期规范化_数值保留零与负数_不按金额格式化()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001", new DateTime(2026, 9, 10), cartons: -5m, weight: 0m, volume: 2m);
        SeedList(db, "LL-2", customer.Id, "TCLU-002", new DateTime(2026, 9, 10), cartons: 8m, weight: -3m, volume: -2m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        using var workbook = OpenWorkbook(file.FileContents);

        Assert.Equal(2, workbook.NumberOfSheets);
        var daily = workbook.GetSheet(DynamicContainerStatsSummaryRules.SummarySheetName);
        Assert.Equal(DynamicContainerStatsSummaryRules.SummarySheetName, daily.SheetName);

        Assert.Equal(new[] { "装柜日历日", "证据桶数", "已审核装柜清单数", "缺柜号清单数", "箱数(cartons)", "毛重(kg)", "体积(m³)" },
            Enumerable.Range(0, 7).Select(i => daily.GetRow(0).GetCell(i).StringCellValue).ToArray());

        var row = daily.GetRow(1);
        Assert.Equal(CellType.String, row.GetCell(0).CellType);
        Assert.Equal("2026-09-10", row.GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(1).CellType);
        Assert.Equal(2d, row.GetCell(1).NumericCellValue);   // 证据桶数 2
        Assert.Equal(2d, row.GetCell(2).NumericCellValue);   // 已审核装柜清单数 2
        Assert.Equal(0d, row.GetCell(3).NumericCellValue);   // 缺柜号 0
        Assert.Equal(3d, row.GetCell(4).NumericCellValue);   // 箱数 -5 + 8 = 3
        Assert.Equal(-3d, row.GetCell(5).NumericCellValue);  // 毛重 0 + (-3) = -3（负数保留）
        Assert.Equal(0d, row.GetCell(6).NumericCellValue);   // 体积 2 + (-2) = 0（零保留）
        Assert.Null(daily.GetRow(2));
    }

    // ==================== 2. 期间合计与口径工作表 ====================

    [Fact]
    public async Task 导出汇总_期间合计与口径_数值与必含口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001", new DateTime(2026, 9, 10), cartons: 10m, weight: 100m, volume: 5m);
        SeedList(db, "LL-2", customer.Id, "TCLU-002", new DateTime(2026, 9, 11), cartons: -2m, weight: 8m, volume: -1m);
        SeedList(db, "LL-3", customer.Id, "", new DateTime(2026, 9, 11));

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        using var workbook = OpenWorkbook(file.FileContents);

        var context = workbook.GetSheet(DynamicContainerStatsSummaryRules.PeriodContextSheetName);
        Assert.Equal(DynamicContainerStatsSummaryRules.PeriodContextSheetName, context.SheetName);

        Assert.Equal(3d, NumericValue(context, DynamicContainerStatsSummaryRules.PeriodBucketLabel));
        Assert.Equal(3d, NumericValue(context, DynamicContainerStatsSummaryRules.PeriodApprovedListsLabel));
        Assert.Equal(1d, NumericValue(context, DynamicContainerStatsSummaryRules.PeriodMissingLabel));
        Assert.Equal(9d, NumericValue(context, DynamicContainerStatsSummaryRules.PeriodCartonsLabel));
        Assert.Equal(110d, NumericValue(context, DynamicContainerStatsSummaryRules.PeriodWeightLabel));
        Assert.Equal(7d, NumericValue(context, DynamicContainerStatsSummaryRules.PeriodVolumeLabel));

        var labels = new List<string?>();
        for (var r = 0; r <= context.LastRowNum; r++)
            labels.Add(context.GetRow(r)?.GetCell(0)?.StringCellValue);

        Assert.Contains(DynamicContainerStatsReportRules.ContextStartLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextEndLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextFilterLabel, labels);
        Assert.Contains(DynamicContainerStatsSummaryRules.ContextCoverageLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextCustomerScopeLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextSourceLimitLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextUnitLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextGroupingLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextUnknownCapacityLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextTypeLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextShippingLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextSourceLabel, labels);
    }


    // ==================== 3. 多页 / 隐藏字段下仍覆盖全部匹配证据 ====================

    [Fact]
    public async Task 导出汇总_多页与隐藏字段_仍覆盖全部匹配证据_无明细行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-A", customer.Id, "AAA", new DateTime(2026, 9, 10));
        SeedList(db, "LL-B", customer.Id, "BBB", new DateTime(2026, 9, 11));
        SeedList(db, "LL-C", customer.Id, "CCC", new DateTime(2026, 9, 12));

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request(fields: new List<string> { "containerNo" }, page: 1, pageSize: 1)));
        using var workbook = OpenWorkbook(file.FileContents);

        var daily = workbook.GetSheet(DynamicContainerStatsSummaryRules.SummarySheetName);
        Assert.Equal(3, daily.LastRowNum);   // 表头 + 3 个每日行，绝无当前页明细行
        Assert.Equal("2026-09-10", daily.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-11", daily.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-12", daily.GetRow(3).GetCell(0).StringCellValue);
        Assert.Null(daily.GetRow(4));
    }

    [Fact]
    public async Task 导出汇总_绝不追加客户柜号清单明细或全局去重客户合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001", new DateTime(2026, 9, 10));
        SeedList(db, "LL-2", customer.Id, "TCLU-002", new DateTime(2026, 9, 10));

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        using var workbook = OpenWorkbook(file.FileContents);

        var daily = workbook.GetSheet(DynamicContainerStatsSummaryRules.SummarySheetName);
        Assert.Equal(7, daily.GetRow(0).LastCellNum);

        for (var r = 0; r <= daily.LastRowNum; r++)
        {
            var row = daily.GetRow(r);
            if (row == null) continue;
            foreach (var cell in row.Cells)
            {
                if (cell.CellType != CellType.String) continue;
                Assert.DoesNotContain("TCLU", cell.StringCellValue);
                Assert.DoesNotContain("客户", cell.StringCellValue);
                Assert.DoesNotContain("合计", cell.StringCellValue);
                Assert.DoesNotContain("全匹配", cell.StringCellValue);
                Assert.DoesNotContain("实体柜", cell.StringCellValue);
            }
        }
    }


    // ==================== 4. 公式前导 / 空证据 / 来源超限 ====================

    [Fact]
    public void 导出汇总_公式前导筛选与口径_字面文本()
    {
        var items = new[]
        {
            Item(new DateTime(2026, 9, 10), "A", cartons: 1m),
        };
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(items);
        var page = DynamicContainerStatsReportRules.BuildPage(
            items, DynamicContainerStatsReportRules.NormalizeFields(null), 1, 20, Start, End, filterText: "=1+1");

        var bytes = new DynamicContainerStatsSummaryExcelExporter().Build(summary, page);
        using var workbook = OpenWorkbook(bytes);
        var context = workbook.GetSheet(DynamicContainerStatsSummaryRules.PeriodContextSheetName);

        for (var r = 0; r <= context.LastRowNum; r++)
        {
            var row = context.GetRow(r);
            if (row?.GetCell(0)?.StringCellValue == DynamicContainerStatsReportRules.ContextFilterLabel)
                Assert.Equal("'=1+1", row.GetCell(1).StringCellValue);
        }
    }

    [Fact]
    public async Task 导出汇总_空证据_显式标注且返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        using var workbook = OpenWorkbook(file.FileContents);

        var daily = workbook.GetSheet(DynamicContainerStatsSummaryRules.SummarySheetName);
        Assert.NotNull(daily.GetRow(1));
        Assert.Equal(DynamicContainerStatsSummaryRules.EmptyEvidenceRowText, daily.GetRow(1).GetCell(0).StringCellValue);

        var context = workbook.GetSheet(DynamicContainerStatsSummaryRules.PeriodContextSheetName);
        var labels = new List<string?>();
        for (var r = 0; r <= context.LastRowNum; r++)
            labels.Add(context.GetRow(r)?.GetCell(0)?.StringCellValue);
        Assert.Contains(DynamicContainerStatsReportRules.ContextEmptyLabel, labels);
    }

    [Fact]
    public async Task 导出汇总_来源超限_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        for (var i = 1; i <= 501; i++)
        {
            db.ContainerLoadingLists.Add(new ContainerLoadingList
            {
                LoadingListNo = $"LL-{i:0000}",
                LoadingDate = new DateTime(2026, 9, 10),
                ContainerNo = $"TCLU-{i:0000}",
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved
            });
        }
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }


    // ==================== 5. 授权撤销 / 无效输入 ====================

    [Fact]
    public async Task 导出汇总_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 导出汇总_无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 导出汇总_无效字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.ExportSummary(Request(fields: new List<string> { "nope" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 导出汇总_分页超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.ExportSummary(Request(pageSize: 201)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 数据范围 ====================

    [Fact]
    public async Task 导出汇总_受限制业务员_仅本人客户_不泄露他人()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = SummaryOk(await ctl.ExportSummary(Request()));
        using var workbook = OpenWorkbook(file.FileContents);

        var daily = workbook.GetSheet(DynamicContainerStatsSummaryRules.SummarySheetName);
        Assert.Equal(1, daily.LastRowNum);   // 表头 + 仅 1 个每日行（只有本人客户）
        Assert.Equal(1d, daily.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Null(daily.GetRow(2));
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task 导出汇总_只读_不新增任何记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");
        var listsBefore = db.ContainerLoadingLists.Count();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        _ = SummaryOk(await ctl.ExportSummary(Request()));

        Assert.Equal(listsBefore, db.ContainerLoadingLists.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

