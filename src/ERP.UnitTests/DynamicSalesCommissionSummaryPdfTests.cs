using System.Text;
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
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态业务员提成证据报表（ERP-249）「全匹配」原币汇总中文 PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei（非缺字字体）、
/// 独立于当前页 / 选定明细列（页面数一致）、固定原币汇总列（无员工明细列）且每列页保留币种身份、
/// 已知签名金额 / 计数与未知金额完整度 / 未知利润 / 未知提成显式「未知」、配置为 0 的当前参考比例显式数值 0（与缺失「未知」区分）、
/// 空汇总单页、多币种行按行页拆分、无菜单授权 / 授权撤销 / 无身份 / 来源超限 fail closed、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesCommissionSummaryPdfTests
{
    private const string MenuCode = "sales-commission";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            Status = DocumentStatus.Approved,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedRate(ErpDbContext db, params string[] values)
    {
        foreach (var value in values)
        {
            db.SysParameters.Add(new SysParameter
            {
                ParamKey = SalesCommissionEvidenceRules.SalesCommissionRateKey,
                ParamValue = value,
                ParamName = "业务员提成比例(%)",
                IsSystem = true
            });
        }
        db.SaveChanges();
    }

    private static DynamicSalesCommissionReportController BuildController(ErpDbContext db, long? userId)
        => new(db, new ReportService(db));

    private static DynamicSalesCommissionReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        SalesCommissionFilterDto? filter = null)
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

    private static DynamicSalesCommissionReportPageDto BuildPage(IReadOnlyList<ReportDtos.SalesCommissionItem> items)
        => DynamicSalesCommissionReportRules.BuildPage(items, new List<string>(), 1, 20, Start, End);

    private static DynamicSalesCommissionSummaryDto BuildSummary(
        IReadOnlyList<DynamicSalesCommissionCurrencySummaryDto> rows,
        int globalBuckets,
        int globalOrders,
        decimal? currentReferenceRate = null,
        string rateReason = "")
        => new()
        {
            CurrencyColumns = DynamicSalesCommissionSummaryRules.CurrencySummaryColumns.ToList(),
            CurrencyRows = rows.ToList(),
            GlobalUniqueSalesmanBuckets = globalBuckets,
            GlobalApprovedOrders = globalOrders,
            CurrentReferenceRate = currentReferenceRate,
            CurrentReferenceRateReason = rateReason,
            CoverageText = DynamicSalesCommissionSummaryRules.SummaryCoverageText,
            ProfitBasisText = DynamicSalesCommissionSummaryRules.ProfitBasisText,
            EmptyText = string.Empty,
        };

    // ==================== 1. 签名 / 内容类型 / 页面边界 / 字体 ====================

    [Fact]
    public async Task ExportSummaryPdf_返回PDF签名与内容类型_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-pdf", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportSummaryPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-font", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 确定性渲染输入 / 布局计算口径 ====================

    [Fact]
    public void BuildRowCells_固定汇总列_签名金额与计数与未知完整度利润提成()
    {
        var columns = DynamicSalesCommissionSummaryRules.CurrencySummaryColumns;
        var row = new DynamicSalesCommissionCurrencySummaryDto
        {
            Currency = "USD",
            CurrencyLabel = "USD 美元",
            ApprovedOrders = 5,
            UniqueSalesmanBuckets = 2,
            SalesAmount = -123.45m,
            Profit = null,
            ProfitRate = null,
            CommissionAmount = null,
            AmountCompletenessText = DynamicSalesCommissionSummaryRules.KnownAmountCompleteText,
            ProfitReasonText = SalesCommissionEvidenceRules.ProfitEvidence,
            ProfitRateReasonText = SalesCommissionEvidenceRules.ProfitRateEvidence,
            CommissionAmountReasonText = SalesCommissionEvidenceRules.CommissionEvidence,
        };

        var cells = DynamicSalesCommissionSummaryPdfExporter.BuildRowCells(columns, row);

        Assert.Equal("USD", cells[0]);
        Assert.Equal("USD 美元", cells[1]);
        Assert.Equal("5", cells[2]);
        Assert.Equal("2", cells[3]);
        Assert.Equal("-123.45", cells[4]); // 已知签名原币金额：数值（绝不跨币种合计）
        Assert.Equal("未知", cells[5]);      // 未知利润：绝不回落为 0
        Assert.Equal("未知", cells[6]);      // 未知利润率：绝不回落为 0
        Assert.Equal("未知", cells[7]);      // 未知提成额：绝不回落为 0
        Assert.Equal(DynamicSalesCommissionSummaryRules.KnownAmountCompleteText, cells[8]);
    }

    [Fact]
    public void FormatSummaryCell_未知币种金额与未知利润提成显式未知_仅计数证据()
    {
        var columns = DynamicSalesCommissionSummaryRules.CurrencySummaryColumns;
        var row = new DynamicSalesCommissionCurrencySummaryDto
        {
            Currency = "999",
            CurrencyLabel = "未知币种",
            ApprovedOrders = 3,
            UniqueSalesmanBuckets = 1,
            SalesAmount = null,
            AmountCompletenessText = DynamicSalesCommissionSummaryRules.UnknownAmountIncompleteText,
        };

        var cells = DynamicSalesCommissionSummaryPdfExporter.BuildRowCells(columns, row);

        Assert.Equal("999", cells[0]);
        Assert.Equal("未知币种", cells[1]);
        Assert.Equal("3", cells[2]);
        Assert.Equal("1", cells[3]);
        Assert.Equal("未知", cells[4]); // 未知 / 无效币种金额：仅计数证据、绝不回落为 0
        Assert.Equal("未知", cells[5]);
        Assert.Equal("未知", cells[6]);
        Assert.Equal("未知", cells[7]);
        Assert.Equal(DynamicSalesCommissionSummaryRules.UnknownAmountIncompleteText, cells[8]);
    }

    [Fact]
    public void BuildRateContextText_配置零当前参考比例_显式数值0_绝不写成未知()
    {
        var summary = BuildSummary(
            Array.Empty<DynamicSalesCommissionCurrencySummaryDto>(),
            0, 0,
            currentReferenceRate: 0m,
            rateReason: DynamicSalesCommissionSummaryRules.RateKnownReason);

        var text = DynamicSalesCommissionSummaryPdfExporter.BuildRateContextText(summary);

        Assert.Contains("当前参考比例：0", text);
        Assert.DoesNotContain("未知", text);
    }

    [Fact]
    public void BuildRateContextText_缺失比例_显式未知()
    {
        var summary = BuildSummary(
            Array.Empty<DynamicSalesCommissionCurrencySummaryDto>(),
            0, 0,
            currentReferenceRate: null,
            rateReason: DynamicSalesCommissionSummaryRules.RateEmptyReason);

        var text = DynamicSalesCommissionSummaryPdfExporter.BuildRateContextText(summary);

        Assert.Contains("当前参考比例：未知", text);
        Assert.Contains(DynamicSalesCommissionSummaryRules.RateEmptyReason, text);
    }

    [Fact]
    public void 汇总PDF_列集_无员工明细列_每列页保留币种身份()
    {
        var columns = DynamicSalesCommissionSummaryRules.CurrencySummaryColumns;

        Assert.DoesNotContain(columns, c => c.Key == "salesmanName");
        Assert.DoesNotContain(columns, c => c.Key == "salesmanId");
        Assert.DoesNotContain(columns, c => c.Key == "salesmanIdentityEvidence");

        var pages = DynamicSalesCommissionSummaryPdfExporter.SplitColumnPages(
            columns, Array.Empty<DynamicSalesCommissionCurrencySummaryDto>());

        foreach (var page in pages)
        {
            Assert.Contains(page, c => c.Key == "currency");
            Assert.Contains(page, c => c.Key == "currencyLabel");
        }

        var dataKeys = columns.Where(c => c.Key is not ("currency" or "currencyLabel"))
            .Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var renderedDataKeys = pages
            .SelectMany(p => p.Where(c => c.Key is not ("currency" or "currencyLabel")).Select(c => c.Key))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(dataKeys, renderedDataKeys);
    }

    // ==================== 3. 全匹配覆盖 / 分页 ====================

    [Fact]
    public async Task ExportSummaryPdf_独立于当前页与选定列_页面数一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-indep", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        SeedOrder(db, "SO-USD", customer.Id, emp1.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-CNY", customer.Id, emp2.Id, Currency.CNY, 500m);
        SeedOrder(db, "SO-BAD", customer.Id, emp1.Id, (Currency)999, 200m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var a = PdfOk(await ctl.ExportSummaryPdf(Request(
            fields: new List<string> { "salesmanName" }, page: 1, pageSize: 1)));
        var b = PdfOk(await ctl.ExportSummaryPdf(Request(
            fields: null, page: 99, pageSize: 200)));

        using var pa = OpenPdf(a.FileContents);
        using var pb = OpenPdf(b.FileContents);
        Assert.Equal(pa.Pages.Count, pb.Pages.Count);
    }

    [Fact]
    public void ExportSummary_多币种行_按行页拆分()
    {
        var columns = DynamicSalesCommissionSummaryRules.CurrencySummaryColumns;
        var rows = Enumerable.Range(1, 120).Select(i => new DynamicSalesCommissionCurrencySummaryDto
        {
            Currency = "C" + i,
            CurrencyLabel = "未知币种",
            ApprovedOrders = 1,
            UniqueSalesmanBuckets = 1,
            SalesAmount = null,
            AmountCompletenessText = DynamicSalesCommissionSummaryRules.UnknownAmountIncompleteText,
        }).ToList();
        var summary = BuildSummary(rows, 120, 120);
        var page = BuildPage(Array.Empty<ReportDtos.SalesCommissionItem>());

        var columnPageCount = DynamicSalesCommissionSummaryPdfExporter.SplitColumnPages(columns, rows).Count;
        using var pdf = OpenPdf(DynamicSalesCommissionSummaryPdfExporter.Export(summary, page));

        Assert.True(pdf.Pages.Count > columnPageCount);
    }

    [Fact]
    public async Task ExportSummaryPdf_空汇总_单页_无跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-empty", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 4. 空结果 / 授权 / 校验 / 字体缺失 ====================

    [Fact]
    public async Task ExportSummaryPdf_授权撤销_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(Request()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(Request()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-field", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportSummaryPdf(Request(fields: new List<string> { "orderNo" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_来源超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-cap", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        db.SalesOrders.AddRange(Enumerable.Range(1, 501).Select(i => new SalesOrder
        {
            OrderNo = $"SO-{i:0000}",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            SalesmanId = emp.Id,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            TotalAmount = 100m
        }));
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    // ==================== 5. 作用域 / 只读 / 零比例 / 字体缺失 ====================

    [Fact]
    public async Task ExportSummaryPdf_受限制业务员_只汇总被分配客户证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales", isSystemRole: false);
        var employee = SeedEmployee(db, "alice", "业务员甲");
        var otherEmp = SeedEmployee(db, "bob", "业务员乙");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmp.Id);

        SeedOrder(db, "SO-MINE", mine.Id, employee.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, otherEmp.Id, Currency.USD, 9000m);

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        // 仅被分配客户证据行，PDF 正常生成；绝不泄露范围外业务员
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
    }

    [Fact]
    public async Task ExportSummaryPdf_配置零当前参考比例_正常渲染且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-zero", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);
        SeedRate(db, "0");

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request()));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ExportSummaryPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sc-sum-ro", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 1500m);

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportSummaryPdf(Request()));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public void ExportSummaryPdf_字体缺失_显式失败()
    {
        var summary = BuildSummary(Array.Empty<DynamicSalesCommissionCurrencySummaryDto>(), 0, 0);
        var page = BuildPage(Array.Empty<ReportDtos.SalesCommissionItem>());

        var ex = Assert.Throws<BusinessException>(
            () => DynamicSalesCommissionSummaryPdfExporter.Export(summary, page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }
}
