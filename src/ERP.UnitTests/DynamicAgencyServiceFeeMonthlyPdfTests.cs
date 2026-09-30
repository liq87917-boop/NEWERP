using System.Reflection;
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
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-183 动态代理服务费月度汇总报表 PDF 导出（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、选定字段顺序（BuildRowCells）、原币分行与状态口径（已登记 / 草稿 / 已作废分开列示）、
/// 嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、宽列集拆分为多列页、长行集拆分为多行页、空页显式说明、
/// 无身份 / 无菜单授权 / 页大小超限 / 未知字段拒绝、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyPdfTests
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
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "pdf-user")
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
        string customerCode = "C001", string customerName = "义乌云进出口")
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

    // ==================== 1. 签名 / 内容类型 / 页面边界 ====================

    [Fact]
    public async Task ExportPdf_导出当前页_返回PDF签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency", "registeredTotalAmount", "customerName" },
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName", "currency" },
            PageSize = 10
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 宽列集拆分多列页 ====================

    [Fact]
    public async Task ExportPdf_宽列集_拆分为多列页_页面边界一致()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);

        // 留空字段 = 返回全部白名单字段，宽列集应拆成多个列页（每个列页总宽不超页宽，列不裁切）
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
        foreach (var page in pdf.Pages)
        {
            Assert.InRange(page.Width.Point, 594, 596);
            Assert.InRange(page.Height.Point, 841, 843);
        }
    }

    // ==================== 3. 字段顺序 / 原币分行 / 状态口径 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_原币与状态值保留()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("customerName", "客户名称", "text", false),
            new("currency", "币种", "text", false),
            new("registeredTotalAmount", "已登记原币合计", "number", false),
            new("draftCount", "草稿张数", "number", false),
            new("voidedCount", "已作废张数", "number", false),
        };

        var usd = new Dictionary<string, object?>
        {
            ["customerName"] = "甲客户",
            ["currency"] = "USD",
            ["registeredTotalAmount"] = 100m,
            ["draftCount"] = 1,
            ["voidedCount"] = 0,
        };
        var jpy = new Dictionary<string, object?>
        {
            ["customerName"] = "甲客户",
            ["currency"] = "JPY",
            ["registeredTotalAmount"] = 1200m,
            ["draftCount"] = 0,
            ["voidedCount"] = 1,
        };

        Assert.Equal(new[] { "甲客户", "USD", "100", "1", "0" },
            DynamicAgencyServiceFeeMonthlyPdfExporter.BuildRowCells(columns, usd));
        Assert.Equal(new[] { "甲客户", "JPY", "1200", "0", "1" },
            DynamicAgencyServiceFeeMonthlyPdfExporter.BuildRowCells(columns, jpy));
    }

    // ==================== 4. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = MakePage(
            new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
            {
                new("currency", "币种", "text", false),
            },
            new List<Dictionary<string, object?>>());

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 长行集拆分为多行页 ====================

    [Fact]
    public void ExportPdf_长行集_按行页拆分_避免裁切()
    {
        var columns = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>
        {
            new("currency", "币种", "text", false),
            new("registeredTotalAmount", "已登记原币合计", "number", false),
        };
        var rows = Enumerable.Range(1, 40).Select(i => new Dictionary<string, object?>
        {
            ["currency"] = "USD",
            ["registeredTotalAmount"] = 100m + i,
        }).ToList();

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(MakePage(columns, rows));
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 6. 未授权 / 校验拒绝（fail closed） ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicAgencyServiceFeeMonthlyReportRequest()));
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
            new DynamicAgencyServiceFeeMonthlyReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicAgencyServiceFeeMonthlyReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicAgencyServiceFeeMonthlyReportRequest { Fields = new() { "currency", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 空页 ====================

    [Fact]
    public async Task ExportPdf_空页_生成带空提示的PDF()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 9. 接口路由与前端接线 ====================

    [Fact]
    public void 接口_导出PDF路由契约()
    {
        var route = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/dynamic-agency-service-fee-monthly-report", route);

        var export = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetMethod(nameof(DynamicAgencyServiceFeeMonthlyReportController.ExportPdf))!
            .GetCustomAttributes<HttpPostAttribute>(true).ToList();
        Assert.Contains(export, a => a.Template == "pdf");
    }

    [Fact]
    public void 前端接线_导出PDF按钮与导出接口路径齐备()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "agency-service-fee-monthly-summary.js"));
        Assert.Contains("/api/dynamic-agency-service-fee-monthly-report/pdf", js);
        Assert.Contains("function asfmsExportPdf(", js);
        Assert.Contains("asfmsExportPdf", js);
    }
}
