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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-124 客户报告包 PDF 导出（只读、有界、双授权、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸、嵌入中文黑体 SimHei 字体（非缺字字体）、字体缺失显式失败、
/// 两个独立分页章节（销售订单 + 发票 / 收款分摊证据，各自独立分页）、原币文本原样与显式剩余证据状态标签、
/// 双菜单授权任一缺失 / 无身份 / 页大小超限拒绝、越界业务员仅本人范围、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class CustomerReportPacketPdfTests
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
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        Currency currency = Currency.USD, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = currency,
            TotalAmount = 100
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        DateTime? invoiceDate = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = invoiceDate ?? new DateTime(2026, 8, 20),
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

    /// <summary>播种同时具备「销售订单 + 客户资料」两个菜单授权的特权账号（系统内置角色），返回用户 Id。</summary>
    private static long SeedPrivilegedBothMenus(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种只具备「客户资料」菜单授权的特权账号（缺少销售订单菜单）。</summary>
    private static long SeedPrivilegedCustomerMenuOnly(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种只具备「销售订单」菜单授权的特权账号（缺少客户资料菜单）。</summary>
    private static long SeedPrivilegedOrderMenuOnly(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种同时具备两个菜单授权的受限业务员账号，返回（用户 Id、本人客户、他人客户）。</summary>
    private static (long UserId, long Mine, long Other) SeedRestrictedBothMenus(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Sales-{userName}");
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);

        var employee = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"{userName}-C1", "我的客户", employee.Id);
        var other = SeedCustomer(db, $"{userName}-C2", "别人的客户", employee.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static CustomerReportPacketController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new CustomerReportPacketController(
            new DynamicSalesOrderReportQuery(db),
            new DynamicReceivableReportQuery(db),
            db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
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
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        SeedInvoice(db, "INV-1", customer.Id, 100m);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 2. 嵌入中文黑体字体 ====================

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        SeedInvoice(db, "INV-1", customer.Id, 100m);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 3. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var packet = new CustomerReportPacketDto(
            1L,
            new DynamicSalesOrderReportPageDto(
                new List<DynamicSalesOrderReportFieldDto> { new("orderNo", "订单号", "text", false) },
                new List<Dictionary<string, object?>>(),
                0, 1, 20, 0, "只读", "边界", "免责"),
            new DynamicReceivableReportPageDto(
                new List<DynamicReceivableReportFieldDto> { new("invoiceNumber", "发票号码", "text", false) },
                new List<Dictionary<string, object?>>(),
                0, 1, 20, 0, "只读", "边界", "免责"),
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            CustomerReportPacketPdfExporter.Export(packet, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 4. 两个独立分页章节 ====================

    [Fact]
    public async Task ExportPdf_两个独立分页章节_销售订单与应收证据各自独立分页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD);
        SeedInvoice(db, "INV-1", customer.Id, 100m, "USD");
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        using var pdf = OpenPdf(file.FileContents);
        // 销售订单章节与应收证据章节各自独立分页：两章至少各占一页。
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 5. 分页（页大小上限 100） ====================

    [Fact]
    public async Task ExportPdf_多行_分页_且仅导出当前页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        for (var i = 0; i < 100; i++)
            SeedOrder(db, $"SO-{i:D3}", customer.Id);
        for (var i = 0; i < 100; i++)
            SeedInvoice(db, $"INV-{i:D3}", customer.Id, 10m);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 100 }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 2);
    }

    // ==================== 6. A4 页面尺寸 ====================

    [Fact]
    public async Task ExportPdf_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    // ==================== 7. 单元格格式化（中文布尔 / 日期 / 数值 / 原币文本） ====================

    [Fact]
    public void FormatCellValue_中文布尔日期数值_原币文本原样()
    {
        Assert.Equal("是", CustomerReportPacketPdfExporter.FormatCellValue(true));
        Assert.Equal("否", CustomerReportPacketPdfExporter.FormatCellValue(false));
        Assert.Equal("2026-09-01", CustomerReportPacketPdfExporter.FormatCellValue(new DateTime(2026, 9, 1)));
        Assert.Equal("100", CustomerReportPacketPdfExporter.FormatCellValue(100m));
        Assert.Equal("", CustomerReportPacketPdfExporter.FormatCellValue(null));
        Assert.Equal("USD", CustomerReportPacketPdfExporter.FormatCellValue("USD"));
        Assert.Equal("CNY", CustomerReportPacketPdfExporter.FormatCellValue("CNY"));
    }

    // ==================== 8. 显式剩余证据状态标签 ====================

    [Fact]
    public void FormatRemainingState_known_unknown_over_allocated_显式区分()
    {
        Assert.Equal("剩余可确认", CustomerReportPacketPdfExporter.FormatRemainingState(
            CustomerReceivableReconciliationRules.RemainingKnown));
        Assert.Equal("剩余未知", CustomerReportPacketPdfExporter.FormatRemainingState(
            CustomerReceivableReconciliationRules.RemainingUnknown));
        Assert.Equal("剩余超额分摊（无效）", CustomerReportPacketPdfExporter.FormatRemainingState(
            CustomerReceivableReconciliationRules.RemainingOverAllocated));
        // 未知取值原样返回，绝不猜测
        Assert.Equal("weird", CustomerReportPacketPdfExporter.FormatRemainingState("weird"));
    }

    // ==================== 9. 双菜单授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_缺销售订单菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedCustomerMenuOnly(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new CustomerReportPacketRequest { CustomerId = 1 }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_缺客户资料菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedOrderMenuOnly(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new CustomerReportPacketRequest { CustomerId = 1 }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new CustomerReportPacketRequest { CustomerId = 1 }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new CustomerReportPacketRequest { CustomerId = 1, PageSize = 101 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 10. 越界业务员仅本人范围 ====================

    [Fact]
    public async Task ExportPdf_越界业务员_仅本人范围_不泄露他人数据()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedBothMenus(db, "pdf-scope");
        SeedInvoice(db, "INV-MINE", mine, 1000m);
        SeedInvoice(db, "INV-OTHER", other, 2000m);
        var ctl = BuildController(db, uid);

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = mine, PageSize = 100 }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 11. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        SeedInvoice(db, "INV-1", customer.Id, 1000m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new CustomerReportPacketController(
            new DynamicSalesOrderReportQuery(counting.Proxy),
            new DynamicReceivableReportQuery(counting.Proxy),
            counting.Proxy);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, uid.ToString()) };
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var file = PdfOk(await ctl.ExportPdf(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 12. 只读计数上下文（断言不写库） ====================

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



