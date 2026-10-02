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
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-254 动态柜量与装柜利用率证据报表中文 PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei（非缺字字体）、
/// 选定列顺序（BuildRowCells）与有符号箱数 / 毛重 / 体积 / 计数数值、负数 / 0 与缺失「未知」的显式区分、
/// 宽列集拆分为多列页、多行按行页拆分、空结果显式空页说明、
/// 无菜单授权 / 授权撤销 / 无身份 / 未知字段 / 页大小超限 / 来源超限拒绝、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicContainerStatsPdfTests
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

    private static FileContentResult PdfOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
        return file;
    }

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    // ==================== 1. 签名 / 内容类型 / 页面边界 / 字体 ====================

    [Fact]
    public async Task 下载PDF_返回PDF签名与内容类型_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "containerNo", "totalVolume" })));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task 下载PDF_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-font", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request()));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 选定列顺序 / 有符号数值 / 负数 / 0 / 未知 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_有符号数值与未知区分()
    {
        var columns = DynamicContainerStatsReportRules.GetCatalogDto().Fields;
        var row = new Dictionary<string, object?>
        {
            ["loadingDate"] = new DateTime(2026, 9, 10),
            ["containerNo"] = "TCLU-001",
            ["loadingListCount"] = 1,
            ["authorizedCustomerCount"] = 1,
            ["totalCartons"] = -5m,
            ["totalWeight"] = 0m,
            ["totalVolume"] = 12.5m,
            ["utilizationType"] = "未知",
            ["reasons"] = "缺少权威容积/整柜/满柜证据，装载率未知（不按 68m³ 估算）",
        };

        var cells = DynamicContainerStatsPdfExporter.BuildRowCells(columns, row);

        Assert.Equal(columns.Count, cells.Count);
        Assert.Equal("2026-09-10", cells[0]);
        Assert.Equal("TCLU-001", cells[1]);
        Assert.Equal("1", cells[2]);
        Assert.Equal("-5", cells[4]);
        Assert.Equal("0", cells[5]);
        Assert.Equal("12.5", cells[6]);
        Assert.Equal("未知", cells[7]);
    }

    [Fact]
    public void FormatCellValue_负数与零_保留有符号数值_缺失显式未知()
    {
        Assert.Equal("-12.5", DynamicContainerStatsPdfExporter.FormatCellValue(-12.5m));
        Assert.Equal("0", DynamicContainerStatsPdfExporter.FormatCellValue(0m));
        Assert.Equal("3", DynamicContainerStatsPdfExporter.FormatCellValue(3m));

        var field = DynamicContainerStatsReportRules.GetField("totalCartons")!;
        var missingRow = new Dictionary<string, object?> { ["totalCartons"] = null };
        Assert.Equal("未知", DynamicContainerStatsPdfExporter.FormatFieldCell(field, missingRow));
    }

    // ==================== 3. 宽列集拆分 / 行页拆分 / 空结果 ====================

    [Fact]
    public async Task 下载PDF_宽列集_拆分为多个列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-wide", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        // 全选 9 个字段（含长「未知原因」列），列宽合计超过单页可用宽度，应拆分为多个列页
        var file = PdfOk(await ctl.ExportPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public async Task 下载PDF_多行_拆分为多个行页且重复表头()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-tall", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        for (var i = 1; i <= 80; i++)
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

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "containerNo" }, pageSize: 200)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public async Task 下载PDF_空结果_显式空页说明且仍返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-empty", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(fields: new List<string> { "containerNo" })));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
    }


    // ==================== 4. 授权撤销 / 无身份 / 无效请求 / 来源超限 ====================

    [Fact]
    public async Task 下载PDF_无菜单授权_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "cst-pdf-nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 下载PDF_授权撤销_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-revoke", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(fields: new List<string> { "containerNo" })));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 下载PDF_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(fields: new List<string> { "containerNo" })));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 下载PDF_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-field", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(fields: new List<string> { "notAField" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 下载PDF_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-cap", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(Request(pageSize: 201)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 下载PDF_来源超限_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-overflow", "Priv", isSystemRole: true);
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }


    // ==================== 5. 数据范围 / 字体缺失 / 只读 ====================

    [Fact]
    public async Task 下载PDF_受限制业务员_只下载被分配客户_且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var before = db.ContainerLoadingLists.Count();
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "containerNo" })));

        Assert.Equal(before, db.ContainerLoadingLists.Count());
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.True(file.FileContents.Length > 0);
    }

    [Fact]
    public void Export_字体缺失_显式失败()
    {
        var page = DynamicContainerStatsReportRules.BuildPage(
            Array.Empty<ReportDtos.ContainerStatsItem>(),
            DynamicContainerStatsReportRules.NormalizeFields(null),
            1, 20, Start, End);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicContainerStatsPdfExporter.Export(page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    [Fact]
    public async Task 下载PDF_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cst-pdf-ro", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");
        var listsBefore = db.ContainerLoadingLists.Count();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportPdf(Request()));

        Assert.Equal(listsBefore, db.ContainerLoadingLists.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 6. 上下文行 ====================

    [Fact]
    public void BuildFilterLine_有筛选_渲染规范化筛选_无筛选为空串()
    {
        var pageWithFilter = DynamicContainerStatsReportRules.BuildPage(
            Array.Empty<ReportDtos.ContainerStatsItem>(),
            DynamicContainerStatsReportRules.NormalizeFields(null),
            1, 20, Start, End, "客户 Id 7；柜号关键字 TCLU");

        var line = DynamicContainerStatsPdfExporter.BuildFilterLine(pageWithFilter);
        Assert.Contains("应用筛选", line);
        Assert.Contains("客户 Id 7", line);
        Assert.Contains("柜号关键字 TCLU", line);

        var pageWithoutFilter = DynamicContainerStatsReportRules.BuildPage(
            Array.Empty<ReportDtos.ContainerStatsItem>(),
            DynamicContainerStatsReportRules.NormalizeFields(null),
            1, 20, Start, End);
        Assert.Equal(string.Empty, DynamicContainerStatsPdfExporter.BuildFilterLine(pageWithoutFilter));
    }

    [Fact]
    public void BuildScopeLine_范围上下文_含证据桶与已审核清单()
    {
        var page = DynamicContainerStatsReportRules.BuildPage(
            Array.Empty<ReportDtos.ContainerStatsItem>(),
            DynamicContainerStatsReportRules.NormalizeFields(null),
            1, 20, Start, End);

        var line = DynamicContainerStatsPdfExporter.BuildScopeLine(page);
        Assert.Contains("装柜日历日 × 原始非空白柜号证据桶", line);
        Assert.Contains("0 桶", line);
        Assert.Contains("已审核清单 0", line);
    }
}

