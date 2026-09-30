using System.Text;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-189 动态代理服务费月度汇总报表「当前页按对账月份 / 客户分组计数 PDF 导出」单元测试。
/// <para>语义：<see cref="DynamicAgencyServiceFeeMonthlyPdfExporter.Export(DynamicAgencyServiceFeeMonthlyReportPageDto)"/> 在
/// month / customer 分组模式下，于选定列证据页之后追加独立的「分组计数」分区（复用 ERP-184 同一批有界、已授权分组计数），
/// 展示分组标签 + 原币 + 月度行数与已登记 / 草稿 / 已作废 / 总计张数，并显式标注仅本页 / 空页 / 截断；none 保持既有证据 PDF 不变。</para>
/// <para>覆盖：拒绝访问（无身份 / 无菜单授权）、无效分组键、混合币种、隐藏维度列、长标签、空分组、截断分组、缺失字体、
/// none 证据页不变与多分组纵向分页。全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行任何 SQL。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyGroupedPdfTests
{
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
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "grouped-pdf-user")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode).Id);
        return user.Id;
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

    private static FileContentResult PdfOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
        return file;
    }

    private static DynamicAgencyServiceFeeMonthlyReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicAgencyServiceFeeMonthlyReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    private static DynamicAgencyServiceFeeMonthlyReportPageDto MakePage(
        List<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns,
        List<Dictionary<string, object?>> rows)
        => new(
            columns,
            rows,
            rows.Count,
            1,
            200,
            1,
            rows.Count > 200,
            rows.Count,
            "没有符合条件的代理服务费对账单证据",
            "只读声明",
            "模块边界",
            "证据口径说明",
            "币种隔离说明",
            "服务期间跨月不分摊");

    private static DynamicAgencyServiceFeeMonthlyReportPageDto MakeGroupedPage(
        List<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns,
        List<Dictionary<string, object?>> rows,
        string groupBy,
        List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto> groups,
        bool truncated = false)
        => new(
            columns,
            rows,
            rows.Count,
            1,
            200,
            1,
            truncated,
            rows.Count,
            "没有符合条件的代理服务费对账单证据",
            "只读声明",
            "模块边界",
            "证据口径说明",
            "币种隔离说明",
            "服务期间跨月不分摊",
            groupBy,
            groups,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupCountScopeText);

    private static DynamicAgencyServiceFeeMonthlyReportGroupCountDto MakeGroup(
        string groupBy,
        string currency,
        int rowCount,
        int registered,
        int draft,
        int voided,
        int total,
        string customerCode = "C001",
        string customerName = "客户一",
        long? customerId = 1,
        int? year = 2026,
        int? month = 9,
        string monthText = "2026-09")
    {
        var isMonth = string.Equals(groupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, StringComparison.OrdinalIgnoreCase);
        return new DynamicAgencyServiceFeeMonthlyReportGroupCountDto(
            groupBy,
            isMonth ? year : null,
            isMonth ? month : null,
            isMonth ? monthText : string.Empty,
            isMonth ? null : customerId,
            customerCode,
            customerName,
            currency,
            rowCount,
            registered,
            draft,
            voided,
            total,
            0m, "", 0m, "", 0m, "");
    }


    // ==================== 1. none 保持既有证据 PDF 不变 ====================

    [Fact]
    public async Task ExportPdf_none分组_保持既有证据页_不追加分区()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByNone,
            PageSize = 10,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 2. month / customer 分组追加分组计数分区 ====================

    [Fact]
    public async Task ExportPdf_month分组_追加分组计数分区()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 10,
        }));

        // 证据页（1 页）+ 分组计数分区（1 页）
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_customer分组_混合币种_原币隔离()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-USD", c1.Id, 100m, currency: "USD");
        SeedStatement(db, "ASF-JPY", c1.Id, 1200m, currency: "JPY");

        var ctl = BuildController(db, uid);
        var preview = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 10,
        }));

        // 原币严格隔离：同客户两个币种 → 两个分组，绝不合并
        Assert.Equal(2, preview.GroupCounts.Count);
        Assert.Contains("USD", preview.GroupCounts.Select(g => g.Currency));
        Assert.Contains("JPY", preview.GroupCounts.Select(g => g.Currency));

        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 10,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_隐藏分组维度列_仍渲染分组分区()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" }, // 不选择客户 / 月份维度列
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 10,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    // ==================== 3. 空页分组显式说明 ====================

    [Fact]
    public async Task ExportPdf_空页分组_渲染空提示()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 10,
        }));

        // 证据空页 + 分组计数空页（空提示）
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    // ==================== 4. 拒绝访问 / 无效分组键（fail closed） ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth
            }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无客户资料菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer
            }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = "quarter"
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    // ==================== 5. none 证据页不变 / 分组仅追加分区（直接导出） ====================

    [Fact]
    public void ExportPdf_none证据页不变_month分组仅追加分区()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("currency", "币种", "text", false),
            new("registeredTotalAmount", "已登记原币合计", "number", false),
        };
        var rows = Enumerable.Range(1, 10).Select(i => new Dictionary<string, object?>
        {
            ["currency"] = "USD",
            ["registeredTotalAmount"] = 100m + i,
        }).ToList();

        var noneBytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(MakePage(columns, rows));
        using var nonePdf = OpenPdf(noneBytes);
        var noneCount = nonePdf.Pages.Count;

        var grouped = MakeGroupedPage(columns, rows,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>
            {
                MakeGroup("month", "USD", 10, 10, 0, 0, 10),
            });
        var groupedBytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(grouped);
        using var groupedPdf = OpenPdf(groupedBytes);

        // none 保持既有证据页不变；month 仅在证据页之后追加 1 页分组计数分区
        Assert.Equal(1, noneCount);
        Assert.Equal(noneCount + 1, groupedPdf.Pages.Count);
    }

    // ==================== 6. 长标签正常渲染不崩溃 ====================

    [Fact]
    public void ExportPdf_长客户名称_渲染不崩溃()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("currency", "币种", "text", false),
        };
        var rows = new List<Dictionary<string, object?>> { new() { ["currency"] = "USD" } };
        var longName = "超长客户名称" + new string('甲', 300);
        var page = MakeGroupedPage(columns, rows,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>
            {
                MakeGroup("customer", "USD", 1, 1, 0, 0, 1, customerName: longName),
            });

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 7. 多分组纵向分页避免裁切 ====================

    [Fact]
    public void ExportPdf_多分组_纵向分页_避免裁切()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("currency", "币种", "text", false),
        };
        var rows = Enumerable.Range(1, 40).Select(i => new Dictionary<string, object?>
        {
            ["currency"] = "USD",
        }).ToList();
        var groups = Enumerable.Range(1, 40).Select(i =>
            MakeGroup("customer", "USD", 1, 1, 0, 0, 1,
                customerCode: $"C{i:000}", customerName: $"客户{i}", customerId: i)).ToList();
        var page = MakeGroupedPage(columns, rows,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer, groups);

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count > 2);
    }

    // ==================== 8. 截断分组显式说明 ====================

    [Fact]
    public void ExportPdf_截断分组_渲染截断提示()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("currency", "币种", "text", false),
        };
        var rows = new List<Dictionary<string, object?>> { new() { ["currency"] = "USD" } };
        var page = MakeGroupedPage(columns, rows,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>
            {
                MakeGroup("month", "USD", 1, 1, 0, 0, 1),
            },
            truncated: true);

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 9. 缺失字体显式失败 ====================

    [Fact]
    public void ExportPdf_分组页_字体缺失_显式失败()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("currency", "币种", "text", false),
        };
        var rows = new List<Dictionary<string, object?>> { new() { ["currency"] = "USD" } };
        var page = MakeGroupedPage(columns, rows,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>
            {
                MakeGroup("month", "USD", 1, 1, 0, 0, 1),
            });

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }
}

