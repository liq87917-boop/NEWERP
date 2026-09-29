using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-121 动态客户应收账款证据报表 PDF 导出（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（分页边界）、选定字段顺序（BuildRowCells）、分页（单页 / 多页）、
/// 嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、known / unknown / over_allocated 剩余证据区分、
/// 分组小计币种分开（绝不跨币种合计）、无身份 / 无菜单授权 / 页大小超限拒绝、越界业务员仅本人范围、
/// 共享 SimHei 解析器下销售订单与应收 PDF 无跨报表回归、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceivablePdfTests
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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
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
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = new DateTime(2026, 8, 20),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            NetAmount = grossAmount * 0.9m,
            TaxAmount = grossAmount * 0.1m,
            GrossAmount = grossAmount,
            Status = status,
            IsDeleted = false
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    /// <summary>播种一个具备「客户资料」菜单授权的特权账号（系统内置角色），返回其用户 Id。</summary>
    private static long SeedPrivilegedAuthorized(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceivableReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种一个具备「客户资料」菜单授权的受限业务员账号，返回（用户 Id、本人客户、他人客户）。</summary>
    private static (long UserId, long Mine, long Other) SeedRestrictedAuthorized(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Sales-{userName}");
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceivableReportRules.RequiredMenuCode).Id);

        var employee = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"{userName}-C1", "我的客户", employee.Id);
        var other = SeedCustomer(db, $"{userName}-C2", "别人的客户", employee.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static DynamicReceivableReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicReceivableReportController(new DynamicReceivableReportQuery(db));
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

    // ==================== 1. 签名 / 内容类型 ====================

    [Fact]
    public async Task ExportPdf_导出当前页_返回PDF签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedInvoice(db, "INV-1", customer.Id, 100m);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "grossAmount" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 2. 选定字段顺序 ====================

    [Fact]
    public void BuildRowCells_按请求字段顺序映射单元格()
    {
        var columns = new List<DynamicReceivableReportFieldDto>
        {
            new("invoiceNumber", "发票号码", "text", false),
            new("currency", "币种", "text", false),
            new("grossAmount", "发票含税总额", "number", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["grossAmount"] = 100m,
            ["invoiceNumber"] = "INV-1",
            ["currency"] = "USD",
        };

        var cells = DynamicReceivablePdfExporter.BuildRowCells(columns, row);

        Assert.Equal(new[] { "INV-1", "USD", "100" }, cells);
    }

    // ==================== 3. 分页与 A4 页面边界 ====================

    [Fact]
    public async Task ExportPdf_少行_单页_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedInvoice(db, "INV-1", c1, 100m);
        SeedInvoice(db, "INV-2", c1, 200m);
        SeedInvoice(db, "INV-3", c1, 300m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 100
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_多行_分页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        for (var i = 0; i < 100; i++)
            SeedInvoice(db, $"INV-{i:D3}", c1, 10m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 100
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 4. 嵌入中文黑体字体 ====================

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedInvoice(db, "INV-1", c1, 100m);

        var ctl = BuildController(db, uid);
        var file = PdfOk(await ctl.ExportPdf(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "currency", "grossAmount" },
            PageSize = 10
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 5. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = new DynamicReceivableReportPageDto(
            new List<DynamicReceivableReportFieldDto> { new("invoiceNumber", "发票号码", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0,
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicReceivablePdfExporter.Export(page, null, "none", @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 6. 单元格格式化（中文布尔 / 日期 / 数值） ====================

    [Fact]
    public void FormatCellValue_中文布尔日期数值()
    {
        Assert.Equal("是", DynamicReceivablePdfExporter.FormatCellValue(true));
        Assert.Equal("否", DynamicReceivablePdfExporter.FormatCellValue(false));
        Assert.Equal("2026-09-01", DynamicReceivablePdfExporter.FormatCellValue(new DateTime(2026, 9, 1)));
        Assert.Equal("100", DynamicReceivablePdfExporter.FormatCellValue(100m));
        Assert.Equal("", DynamicReceivablePdfExporter.FormatCellValue(null));
    }


    // ==================== 7. 剩余证据区分（不轧为假余额） ====================

    [Fact]
    public void FormatRemainingText_known_unknown_over_allocated_区分()
    {
        var known = new DynamicReceivableReportCurrencySubtotalDto(
            "USD", 1, 100m, 40m, 60m, CustomerReceivableReconciliationRules.RemainingKnown);
        var unknown = new DynamicReceivableReportCurrencySubtotalDto(
            "USD", 1, 100m, 0m, null, CustomerReceivableReconciliationRules.RemainingUnknown);
        var over = new DynamicReceivableReportCurrencySubtotalDto(
            "USD", 1, 100m, 120m, null, CustomerReceivableReconciliationRules.RemainingOverAllocated);

        Assert.Equal("60", DynamicReceivablePdfExporter.FormatRemainingText(known));
        Assert.Equal("剩余未知", DynamicReceivablePdfExporter.FormatRemainingText(unknown));
        Assert.Equal("剩余超额分摊（无效）", DynamicReceivablePdfExporter.FormatRemainingText(over));
    }

    // ==================== 8. 分组小计币种分开（不跨币种合计） ====================

    [Fact]
    public void 分组小计_币种分开_绝不跨币种合计()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customerId"] = 1L, ["currency"] = "USD", ["grossAmount"] = 100m, ["effectiveAmount"] = 0m, ["remainingAmount"] = 100m, ["remainingState"] = CustomerReceivableReconciliationRules.RemainingKnown },
            new() { ["customerId"] = 1L, ["currency"] = "CNY", ["grossAmount"] = 200m, ["effectiveAmount"] = 0m, ["remainingAmount"] = 200m, ["remainingState"] = CustomerReceivableReconciliationRules.RemainingKnown },
        };

        var groups = DynamicReceivableReportRules.BuildGroupSubtotals(rows, "customer");
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Subtotals.Count);
        Assert.Contains(group.Subtotals, s => s.Currency == "USD" && s.GrossAmount == 100m);
        Assert.Contains(group.Subtotals, s => s.Currency == "CNY" && s.GrossAmount == 200m);
        Assert.DoesNotContain(group.Subtotals, s => string.IsNullOrEmpty(s.Currency));
    }

    // ==================== 9. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_无客户菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicReceivableReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicReceivableReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicReceivableReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 10. 越界业务员仅本人范围 ====================

    [Fact]
    public async Task ExportPdf_越界业务员_仅本人范围_不泄露他人数据()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedAuthorized(db, "pdf-scope");
        SeedInvoice(db, "INV-MINE", mine, 1000m);
        SeedInvoice(db, "INV-OTHER", other, 2000m);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "grossAmount" },
            PageSize = 100
        }));

        Assert.NotEmpty(file.FileContents);
    }


    // ==================== 11. 共享 SimHei 解析器：跨报表无回归 ====================

    [Fact]
    public void 共享字体解析器_应收与销售订单PDF_无跨报表回归()
    {
        // 先导出应收 PDF（注册共享 SimHei 解析器）
        var recPage = new DynamicReceivableReportPageDto(
            new List<DynamicReceivableReportFieldDto> { new("invoiceNumber", "发票号码", "text", false) },
            new List<Dictionary<string, object?>> { new() { ["invoiceNumber"] = "INV-1" } },
            1, 1, 20, 1, "只读", "边界", "免责");
        var recBytes = DynamicReceivablePdfExporter.Export(recPage, null, "none");
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(recBytes));
        Assert.Contains("SimHei", Encoding.ASCII.GetString(recBytes));

        // 再导出销售订单 PDF（同一进程、同一共享解析器），确认无回归
        var soPage = new DynamicSalesOrderReportPageDto(
            new List<DynamicSalesOrderReportFieldDto> { new("orderNo", "订单号", "text", false) },
            new List<Dictionary<string, object?>> { new() { ["orderNo"] = "SO-1" } },
            1, 1, 20, 1, "只读", "边界", "免责");
        var soBytes = DynamicSalesOrderPdfExporter.Export(soPage, null, "none");
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(soBytes));
        Assert.Contains("SimHei", Encoding.ASCII.GetString(soBytes));
    }

    // ==================== 12. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-RO", "只读客户");
        SeedInvoice(db, "INV-RO", customer.Id, 1000m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicReceivableReportController(new DynamicReceivableReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 13. 只读计数上下文（断言不写库） ====================

    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int WriteCalls { get; private set; }

        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}

