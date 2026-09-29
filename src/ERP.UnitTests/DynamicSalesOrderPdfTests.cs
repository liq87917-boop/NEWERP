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
/// ERP-116 动态销售订单报表 PDF 导出（只读、有界）单元测试。
/// 覆盖：PDF 签名与内容类型、分页（单页 / 多页）、嵌入中文黑体 SimHei 字体（不替换为缺字字体）、
/// 字体缺失显式失败、无身份 / 无菜单授权 / 页大小超限拒绝、币种分开小计不跨币种合计、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesOrderPdfTests
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

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending,
        Currency currency = Currency.USD, decimal totalAmount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = status,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        return user.Id;
    }

    private static (long UserId, long MineCustomerId, long OtherCustomerId) SeedRestrictedUser(
        ErpDbContext db, string userName)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var emp = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"C-{userName}-mine", "我的客户", emp.Id);
        var other = SeedCustomer(db, $"C-{userName}-other", "别人的客户", emp.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static DynamicSalesOrderReportController NewController(IErpDbContext db)
        => new(new DynamicSalesOrderReportQuery(db));

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
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-1", c1);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo", "totalAmount" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 2. 分页 ====================

    [Fact]
    public async Task ExportPdf_少行_单页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-1", c1);
        SeedOrder(db, "SO-2", c1);
        SeedOrder(db, "SO-3", c1);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 100
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_多行_分页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        for (var i = 0; i < 100; i++)
            SeedOrder(db, $"SO-{i:D3}", c1);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 200
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 3. 嵌入中文黑体字体 ====================

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-1", c1);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo", "currency", "totalAmount" },
            PageSize = 10
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 4. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = new DynamicSalesOrderReportPageDto(
            new List<DynamicSalesOrderReportFieldDto> { new("orderNo", "订单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0,
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesOrderPdfExporter.Export(page, null, "none", @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 单元格格式化（中文布尔 / 日期 / 数值） ====================

    [Fact]
    public void FormatCellValue_中文布尔日期数值()
    {
        Assert.Equal("是", DynamicSalesOrderPdfExporter.FormatCellValue(true));
        Assert.Equal("否", DynamicSalesOrderPdfExporter.FormatCellValue(false));
        Assert.Equal("2026-09-01", DynamicSalesOrderPdfExporter.FormatCellValue(new DateTime(2026, 9, 1)));
        Assert.Equal("100", DynamicSalesOrderPdfExporter.FormatCellValue(100m));
        Assert.Equal("", DynamicSalesOrderPdfExporter.FormatCellValue(null));
    }

    // ==================== 6. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_无销售订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicSalesOrderReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicSalesOrderReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicSalesOrderReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 币种分开小计（不跨币种合计） ====================

    [Fact]
    public void 分组小计_币种分开_绝不跨币种合计()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customerId"] = 1L, ["currency"] = "USD", ["totalAmount"] = 100m },
            new() { ["customerId"] = 1L, ["currency"] = "CNY", ["totalAmount"] = 200m },
        };

        var groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(rows, "customer");
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Subtotals.Count);
        Assert.Contains(group.Subtotals, s => s.Currency == "USD" && s.Amount == 100m);
        Assert.Contains(group.Subtotals, s => s.Currency == "CNY" && s.Amount == 200m);
        Assert.DoesNotContain(group.Subtotals, s => string.IsNullOrEmpty(s.Currency));
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-R", c1);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSalesOrderReportController(new DynamicSalesOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 9. 只读计数上下文（断言不写库） ====================

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
