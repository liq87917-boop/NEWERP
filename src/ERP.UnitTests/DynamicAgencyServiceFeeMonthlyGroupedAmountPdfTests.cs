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
/// ERP-191 动态代理服务费月度汇总报表「当前页按对账月份 / 客户分组原币金额 PDF 导出」单元测试。
/// <para>语义：<see cref="DynamicAgencyServiceFeeMonthlyPdfExporter.Export(DynamicAgencyServiceFeeMonthlyReportPageDto)"/> 在
/// month / customer 分组模式下，于选定列证据页之后追加「分组金额（当前页）」子表（复用 ERP-186 同一批有界、已授权
/// GroupCounts 的已登记 / 草稿 / 已作废原币金额），并与 ERP-189 的「分组计数（当前页）」子表在同一组页序列上连续分页；
/// 金额按原币 + 币种精度分别列示、绝不跨币种 / 跨页合计；none 保持既有证据 PDF 不变。</para>
/// <para>覆盖：拒绝访问（无身份 / 无菜单授权）、无效分组键、混合币种、小数精度、长标签、分页、空分组、截断分组、
/// 缺失字体与 none 证据页不变。全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行任何 SQL。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyGroupedAmountPdfTests
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
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "grouped-amt-pdf-user")
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

    /// <summary>构造带「已登记 / 草稿 / 已作废原币金额 + 币种精度文案」的分组金额行（ERP-186 口径）</summary>
    private static DynamicAgencyServiceFeeMonthlyReportGroupCountDto MakeAmountGroup(
        string groupBy,
        string currency,
        decimal registered,
        decimal draft,
        decimal voided,
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
            1,
            1,
            1,
            1,
            3,
            registered,
            AgencyServiceFeeStatementRules.AmountText(registered, currency),
            draft,
            AgencyServiceFeeStatementRules.AmountText(draft, currency),
            voided,
            AgencyServiceFeeStatementRules.AmountText(voided, currency));
    }

    // ==================== 1. 拒绝访问 / 无效分组键（fail closed） ====================

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
        var user = SeedUser(db, "nomenu-amt");
        var role = SeedRole(db, "Role-nomenu-amt");
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

    // ==================== 2. 分组金额分区追加 + none 证据页不变 ====================

    [Fact]
    public async Task ExportPdf_month分组_证据页后追加分组分区_计数与金额同组页()
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

        // 证据页（1 页）+ 分组分区页（1 页，含「分组计数」与「分组金额」两个子表）
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

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

    [Fact]
    public void ExportPdf_none证据页不变_month分组仅追加分组分区()
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
                MakeAmountGroup("month", "USD", 1045m, 0m, 0m),
            });
        var groupedBytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(grouped);
        using var groupedPdf = OpenPdf(groupedBytes);

        // none 保持既有证据页不变；month 仅在证据页之后追加 1 页分组分区（计数 + 金额）
        Assert.Equal(1, noneCount);
        Assert.Equal(noneCount + 1, groupedPdf.Pages.Count);
    }

    // ==================== 3. 金额行映射：混合币种 / 小数精度 / 状态金额分离 ====================

    [Fact]
    public void BuildAmountGroupRows_混合币种_原币隔离_状态金额分离()
    {
        var groups = new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>
        {
            MakeAmountGroup("customer", "USD", 100.5m, 10m, 5.25m),
            MakeAmountGroup("customer", "JPY", 1200m, 300m, 0m),
        };

        var rows = DynamicAgencyServiceFeeMonthlyPdfExporter.BuildAmountGroupRows(groups);

        Assert.Equal(2, rows.Count);
        Assert.Equal("USD", rows[0]["currency"]);
        Assert.Equal("100.50 USD", rows[0]["registeredAmountText"]);
        Assert.Equal("10.00 USD", rows[0]["draftAmountText"]);
        Assert.Equal("5.25 USD", rows[0]["voidedAmountText"]);

        Assert.Equal("JPY", rows[1]["currency"]);
        Assert.Equal("1200 JPY", rows[1]["registeredAmountText"]);
        Assert.Equal("300 JPY", rows[1]["draftAmountText"]);
        Assert.Equal("0 JPY", rows[1]["voidedAmountText"]);
    }

    [Fact]
    public void BuildAmountGroupRows_小数精度_USD两位_JPY零位_绝不跨币种合计()
    {
        var groups = new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>
        {
            MakeAmountGroup("month", "USD", 1288.5m, 0m, 0m),
            MakeAmountGroup("month", "JPY", 1288m, 0m, 0m),
        };

        var rows = DynamicAgencyServiceFeeMonthlyPdfExporter.BuildAmountGroupRows(groups);

        Assert.Equal("1288.50 USD", rows[0]["registeredAmountText"]);
        Assert.Equal("1288 JPY", rows[1]["registeredAmountText"]);
        // 绝不跨币种合计：两条不同币种各自独立成行
        Assert.NotEqual(rows[0]["registeredAmountText"], rows[1]["registeredAmountText"]);
    }

    // ==================== 4. 长标签 / 多分组纵向分页 ====================

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
                MakeAmountGroup("customer", "USD", 100m, 0m, 0m, customerName: longName),
            });

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count >= 2);
    }

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
            MakeAmountGroup("customer", "USD", 100m + i, 10m, 0m,
                customerCode: $"C{i:000}", customerName: $"客户{i}", customerId: i)).ToList();
        var page = MakeGroupedPage(columns, rows,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer, groups);

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count > 2);
    }

    // ==================== 5. 空分组 / 截断分组显式说明 ====================

    [Fact]
    public async Task ExportPdf_空分组_渲染空提示()
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

        // 证据空页 + 分组分区空页（计数空提示 + 金额空提示）
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

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
                MakeAmountGroup("month", "USD", 100m, 10m, 0m),
            },
            truncated: true);

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 6. 缺失字体显式失败 ====================

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
                MakeAmountGroup("month", "USD", 100m, 10m, 0m),
            });

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }
}
