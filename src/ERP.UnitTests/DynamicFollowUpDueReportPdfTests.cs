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
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-196 动态跟进提醒报表 PDF 导出（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（行列页边界）、选定字段顺序（BuildRowCells）与到期证据保留、
/// 嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、宽列集拆分为多列页、多行按行页拆分、
/// 无身份 / 无菜单授权 / 授权撤销 / 页大小超限 / 未知字段拒绝、空页，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicFollowUpDueReportPdfTests
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

    // ==================== 1. 签名 / 内容类型 / 页面边界 ====================

    [Fact]
    public async Task ExportPdf_导出当前页_返回PDF签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo", "customerName", "dueDays" },
            AsOfDate = AsOf,
            AheadDays = 7,
            PageSize = 20
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
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo", "customerName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            PageSize = 20
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 选定列顺序 / 到期证据 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_到期证据与空值保留()
    {
        var columns = new List<DynamicFollowUpDueReportFieldDto>
        {
            new("followNo", "跟进编号", "text", false),
            new("customerName", "客户名称", "text", false),
            new("nextFollowDate", "下次跟进日期", "date", false),
            new("dueDays", "到期天数", "number", false),
            new("dueStatus", "到期状态", "text", true),
            new("remark", "备注", "text", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["followNo"] = "FU-1",
            ["customerName"] = "客户",
            ["nextFollowDate"] = new DateTime(2026, 9, 15),
            ["dueDays"] = 0,
            ["dueStatus"] = "今日到期",
            ["remark"] = null,
        };

        var cells = DynamicFollowUpDuePdfExporter.BuildRowCells(columns, row);

        Assert.Equal(new[] { "FU-1", "客户", "2026-09-15", "0", "今日到期", "" }, cells);
    }

    [Fact]
    public void FormatCellValue_布尔日期数值按口径格式化()
    {
        Assert.Equal("是", DynamicFollowUpDuePdfExporter.FormatCellValue(true));
        Assert.Equal("否", DynamicFollowUpDuePdfExporter.FormatCellValue(false));
        Assert.Equal("2026-09-15", DynamicFollowUpDuePdfExporter.FormatCellValue(new DateTime(2026, 9, 15)));
        Assert.Equal("3", DynamicFollowUpDuePdfExporter.FormatCellValue(3));
        Assert.Equal("-5", DynamicFollowUpDuePdfExporter.FormatCellValue(-5));
        Assert.Equal(string.Empty, DynamicFollowUpDuePdfExporter.FormatCellValue(null));
    }

    // ==================== 3. 宽列集拆分多列页 ====================

    [Fact]
    public async Task ExportPdf_宽列集_拆分为多列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 留空字段 = 返回全部白名单字段（16 列），宽列集应拆成多个列页（每个列页总宽不超页宽，列不裁切）
        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            AsOfDate = AsOf,
            AheadDays = 7,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
        foreach (var page in pdf.Pages)
        {
            Assert.InRange(page.Width.Point, 594, 596);
            Assert.InRange(page.Height.Point, 841, 843);
        }
    }

    // ==================== 4. 多行按行页拆分 ====================

    [Fact]
    public async Task ExportPdf_多行_按行页拆分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 40; i++)
            SeedFollowUp(db, $"FU-{i:D3}", customer.Id, "客户", AsOf.AddDays(-i));

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            Page = 1,
            PageSize = 40
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }


    // ==================== 5. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = new DynamicFollowUpDueReportPageDto(
            new List<DynamicFollowUpDueReportFieldDto> { new("followNo", "跟进编号", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0, false,
            DynamicFollowUpDueReportRules.EmptyText,
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicFollowUpDuePdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 6. 未授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicFollowUpDueReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无跟进提醒菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nomenu-user");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicFollowUpDueReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_授权撤销_菜单授权移除后立即拒绝()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicFollowUpDueReportRequest { AsOfDate = AsOf, AheadDays = 7 }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicFollowUpDueReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicFollowUpDueReportRequest { Fields = new List<string> { "customerName", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 空页 / 只读 ====================

    [Fact]
    public async Task ExportPdf_空页_生成带空提示的PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 8. 分组 PDF 导出（ERP-199） ====================

    [Fact]
    public async Task ExportPdf_不分组_仅明细页_无分组计数区块()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupNone
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_分组_dueStatus_明细后追加分组计数页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo", "dueStatus" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupDueStatus
        }));

        using var pdf = OpenPdf(file.FileContents);
        // 1 页明细 + 1 页分组计数
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_分组_salesman_明细后追加分组计数页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var follow = SeedFollowUp(db, "FU-1", customer.Id, "客户", AsOf);
        follow.SalesmanName = "销售甲";
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo", "salesmanName" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupSalesman
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public void BuildGroupCountRows_标签与数量_照实映射()
    {
        var groups = new List<DynamicFollowUpDueReportGroupDto>
        {
            new("dueStatus:overdue", "已逾期", 2),
            new("dueStatus:today", "今日到期", 1),
            new("dueStatus:upcoming", "即将到期", 0),
        };

        var rows = DynamicFollowUpDuePdfExporter.BuildGroupCountRows(groups);

        Assert.Equal(3, rows.Count);
        Assert.Equal("已逾期", rows[0].Label);
        Assert.Equal("2", rows[0].Count);
        Assert.Equal("今日到期", rows[1].Label);
        Assert.Equal("1", rows[1].Count);
        Assert.Equal("即将到期", rows[2].Label);
        Assert.Equal("0", rows[2].Count);
    }

    [Fact]
    public async Task ExportPdf_分组_dueStatus_空页_含零计数与空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupDueStatus,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        // 1 页空明细 + 1 页分组计数（含零计数与空页说明）
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_分组_salesman_空页_仅空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupSalesman,
            PageSize = 20
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_分组_salesman_多分组_分组计数跨页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 80; i++)
        {
            var follow = SeedFollowUp(db, $"FU-{i:D3}", customer.Id, "客户", AsOf.AddDays(-(i % 5)));
            follow.SalesmanName = $"销售员{i:D3}";
        }
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var noneFile = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupNone,
            Page = 1,
            PageSize = 200
        }));
        var groupFile = PdfOk(await ctl.ExportPdf(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" },
            AsOfDate = AsOf,
            AheadDays = 7,
            GroupBy = DynamicFollowUpDueReportRules.GroupSalesman,
            Page = 1,
            PageSize = 200
        }));

        using var nonePdf = OpenPdf(noneFile.FileContents);
        using var groupPdf = OpenPdf(groupFile.FileContents);

        // 分组计数自身跨多页（明细页之外至少再新增 2 页），标签 / 数量保持可读
        Assert.True(groupPdf.Pages.Count > nonePdf.Pages.Count + 1);
    }
}

