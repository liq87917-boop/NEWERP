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
/// 动态柜量与装柜利用率证据报表（ERP-253）Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与类型化值（有符号箱数 / 毛重 / 体积与授权范围计数为数值单元格，装载率 / 柜型恒为字面「未知」、
/// 日期规范化为 yyyy-MM-dd、负数 / 0 保留数值）、公式前导原始柜号转义为字面文本、仅导出当前页、
/// 绝不追加全匹配 / 跨单位合计 / 实体柜数量声称 / 未选定列、上下文工作表始终标注规范化日期 / 应用筛选 / 页面覆盖 /
/// 来源计数与上限 / 客户范围 / 分组身份 / 数量单位 / 未知实际容积 / 柜型 / 出运证据（即使对应列被取消选择也始终包含）、
/// 数据范围（受限制业务员看不到他人）、授权撤销 / 无效输入 / 来源超限不返回工作簿、空页仍返回仅表头 + 口径的工作簿，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicContainerStatsExcelTests
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
    public async Task Export_选定列顺序与类型化值_有符号数值_未知装载率柜型_日期规范化()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001", cartons: -5m, weight: 0m, volume: 3.5m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string>
            { "totalVolume", "loadingDate", "containerNo", "totalCartons", "totalWeight", "loadingListCount", "authorizedCustomerCount", "utilizationType", "reasons" })));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        var sheet = workbook.GetSheet(DynamicContainerStatsReportRules.DataSheetName);
        Assert.Equal(DynamicContainerStatsReportRules.DataSheetName, sheet.SheetName);

        Assert.Equal(new[]
        {
            "体积(m³)", "装柜日历日", "原始柜号", "箱数(cartons)", "毛重(kg)", "装柜清单数", "授权范围客户数", "装载率/柜型(未知)", "未知原因"
        }, Enumerable.Range(0, 9).Select(i => sheet.GetRow(0).GetCell(i).StringCellValue).ToArray());

        var row = sheet.GetRow(1);
        Assert.Equal(CellType.Numeric, row.GetCell(0).CellType);
        Assert.Equal(3.5d, row.GetCell(0).NumericCellValue);
        Assert.Equal(CellType.String, row.GetCell(1).CellType);
        Assert.Equal("2026-09-10", row.GetCell(1).StringCellValue);
        Assert.Equal("TCLU-001", row.GetCell(2).StringCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(3).CellType);
        Assert.Equal(-5d, row.GetCell(3).NumericCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(4).CellType);
        Assert.Equal(0d, row.GetCell(4).NumericCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(5).CellType);
        Assert.Equal(1d, row.GetCell(5).NumericCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(6).CellType);
        Assert.Equal(1d, row.GetCell(6).NumericCellValue);
        Assert.Equal(CellType.String, row.GetCell(7).CellType);
        Assert.Equal("未知", row.GetCell(7).StringCellValue);
        Assert.Equal(CellType.String, row.GetCell(8).CellType);
        Assert.Contains("缺少权威容积", row.GetCell(8).StringCellValue);
        Assert.Contains("范围客户数", row.GetCell(8).StringCellValue);
    }

    // ==================== 2. 上下文工作表始终包含口径（即使列被隐藏） ====================

    [Fact]
    public async Task Export_上下文表始终包含日期筛选分页来源范围分组单位未知口径_即使列被隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "containerNo" },
            filter: new DynamicContainerStatsReportFilterDto { ContainerNo = "TCLU" })));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheet(DynamicContainerStatsReportRules.ContextSheetName);
        var labels = new List<string?>();
        var values = new List<string?>();
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            labels.Add(row?.GetCell(0)?.StringCellValue);
            values.Add(row?.GetCell(1)?.StringCellValue);
        }

        Assert.Contains(DynamicContainerStatsReportRules.ContextStartLabel, labels);
        Assert.Contains("2026-09-01", values);
        Assert.Contains(DynamicContainerStatsReportRules.ContextEndLabel, labels);
        Assert.Contains("2026-09-30", values);
        Assert.Contains(DynamicContainerStatsReportRules.ContextFilterLabel, labels);
        Assert.Contains("柜号关键字 TCLU", values);
        Assert.Contains(DynamicContainerStatsReportRules.ContextPageLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextPageOnlyLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextSourceLimitLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextSourceCountLabel, labels);
        Assert.Contains(values, v => v != null && v.Contains("装柜日历日 × 原始非空白柜号证据桶"));
        Assert.Contains(DynamicContainerStatsReportRules.ContextCustomerScopeLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextGroupingLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextUnitLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextUnknownCapacityLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextTypeLabel, labels);
        Assert.Contains(DynamicContainerStatsReportRules.ContextShippingLabel, labels);
    }

    // ==================== 3. 公式注入转义 ====================

    [Fact]
    public async Task Export_公式前导原始柜号_转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "=SUM(A1)");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "containerNo" })));
        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheet(DynamicContainerStatsReportRules.DataSheetName);

        Assert.Equal(CellType.String, sheet.GetRow(1).GetCell(0).CellType);
        Assert.Equal("'=SUM(A1)", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    [Fact]
    public void 规则_公式前导转义_仅字符串生效_其余原样()
    {
        Assert.False(DynamicContainerStatsReportRules.IsFormulaLeading(null));
        Assert.False(DynamicContainerStatsReportRules.IsFormulaLeading(""));
        Assert.True(DynamicContainerStatsReportRules.IsFormulaLeading("=SUM(A1)"));
        Assert.True(DynamicContainerStatsReportRules.IsFormulaLeading("+1+2"));
        Assert.True(DynamicContainerStatsReportRules.IsFormulaLeading("-1+2"));
        Assert.True(DynamicContainerStatsReportRules.IsFormulaLeading("@SUM"));

        Assert.Equal("'=SUM(A1)", DynamicContainerStatsReportRules.EscapeFormulaLeading("=SUM(A1)"));
        Assert.Equal("柜号", DynamicContainerStatsReportRules.EscapeFormulaLeading("柜号"));
        Assert.Equal(100m, DynamicContainerStatsReportRules.EscapeFormulaLeading(100m));
        Assert.Null(DynamicContainerStatsReportRules.EscapeFormulaLeading(null));
    }

    // ==================== 4. 仅当前页 / 无未选定列 / 无合计 ====================

    [Fact]
    public async Task Export_仅当前页_不追加未选定列与合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "AAA", new DateTime(2026, 9, 10));
        SeedList(db, "LL-2", customer.Id, "BBB", new DateTime(2026, 9, 11));
        SeedList(db, "LL-3", customer.Id, "CCC", new DateTime(2026, 9, 12));

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "containerNo", "loadingDate" }, page: 1, pageSize: 2)));
        using var workbook = OpenWorkbook(file.FileContents);

        var data = workbook.GetSheet(DynamicContainerStatsReportRules.DataSheetName);
        Assert.Equal(2, data.GetRow(0).LastCellNum);
        Assert.Equal(new[] { "原始柜号", "装柜日历日" },
            new[] { data.GetRow(0).GetCell(0).StringCellValue, data.GetRow(0).GetCell(1).StringCellValue });
        Assert.Equal("AAA", data.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("BBB", data.GetRow(2).GetCell(0).StringCellValue);
        Assert.Null(data.GetRow(3));

        for (var r = 0; r <= data.LastRowNum; r++)
        {
            var row = data.GetRow(r);
            if (row == null) continue;
            foreach (var cell in row.Cells)
            {
                if (cell.CellType != CellType.String) continue;
                Assert.DoesNotContain("合计", cell.StringCellValue);
                Assert.DoesNotContain("总计", cell.StringCellValue);
                Assert.DoesNotContain("全匹配", cell.StringCellValue);
                Assert.DoesNotContain("实体柜数量", cell.StringCellValue);
            }
        }
    }

    // ==================== 5. 空页 ====================

    [Fact]
    public async Task Export_空页_返回仅表头与口径工作表_不返回数据行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(new List<string> { "containerNo" })));
        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);

        var data = workbook.GetSheet(DynamicContainerStatsReportRules.DataSheetName);
        Assert.Equal(1, data.GetRow(0).LastCellNum);
        Assert.Equal("原始柜号", data.GetRow(0).GetCell(0).StringCellValue);
        Assert.Null(data.GetRow(1));

        var context = workbook.GetSheet(DynamicContainerStatsReportRules.ContextSheetName);
        var labels = new List<string?>();
        for (var r = 0; r <= context.LastRowNum; r++)
            labels.Add(context.GetRow(r)?.GetCell(0)?.StringCellValue);
        Assert.Contains(DynamicContainerStatsReportRules.ContextEmptyLabel, labels);
    }

    // ==================== 6. 授权撤销 / 无效输入 / 来源超限 ====================

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(Request()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无效字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(Request(fields: new List<string> { "nope" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_分页超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(Request(pageSize: 201)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_来源超限_拒绝且不返回工作簿()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    // ==================== 7. 数据范围 ====================

    [Fact]
    public async Task Export_受限制业务员_只导出被分配客户_不泄露他人()
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

        var file = ExportOk(await ctl.Export(Request(new List<string> { "containerNo" })));
        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheet(DynamicContainerStatsReportRules.DataSheetName);

        Assert.Equal("TCLU-001", data.GetRow(1).GetCell(0).StringCellValue);
        Assert.Null(data.GetRow(2));
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task Export_只读_不新增任何记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");
        var listsBefore = db.ContainerLoadingLists.Count();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        _ = ExportOk(await ctl.Export(Request()));

        Assert.Equal(listsBefore, db.ContainerLoadingLists.Count());
    }
}

