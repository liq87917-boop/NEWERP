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
/// ERP-129 动态采购订单报表 PDF 导出（只读、有界）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（分页边界）、选定字段顺序（BuildRowCells）、分页（单页 / 多页）、
/// 嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、分组小计币种分开（绝不跨币种合计）、
/// 无身份 / 无菜单授权 / 无效字段 / 页大小超限拒绝、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicPurchaseOrderPdfTests
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

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending, Currency currency = Currency.CNY,
        decimal totalAmount = 100, bool isDeleted = false, string remark = "")
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            SupplierId = supplierId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = status,
            Currency = currency,
            TotalAmount = totalAmount,
            IsDeleted = isDeleted,
            Remark = remark
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种一个「特权 + 采购订单菜单授权」用户（系统内置角色）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "purchase-order").Id);
        return user.Id;
    }

    private static DynamicPurchaseOrderReportController NewController(IErpDbContext db)
        => new(new DynamicPurchaseOrderReportQuery(db));

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
    public async Task ExportPdf_签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-PDF", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 2. A4 页面边界 / 分页 ====================

    [Fact]
    public async Task ExportPdf_少行_单页_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-1", 1L);
        SeedOrder(db, "PO-2", 1L);
        SeedOrder(db, "PO-3", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" },
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
        var uid = SeedPrivilegedUser(db);
        for (var i = 0; i < 100; i++)
            SeedOrder(db, $"PO-{i:D3}", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 100
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 3. 选定字段顺序 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射值()
    {
        var columns = new List<DynamicPurchaseOrderReportFieldDto>
        {
            new("orderNo", "采购单号", "text", false),
            new("currency", "币种", "enum", true),
            new("totalAmount", "订单总额", "number", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["orderNo"] = "PO-1001",
            ["currency"] = "USD",
            ["totalAmount"] = 1234.5m,
        };

        var cells = DynamicPurchaseOrderPdfExporter.BuildRowCells(columns, row);

        Assert.Equal(new[] { "PO-1001", "USD", "1234.5" }, cells);
    }

    // ==================== 4. 嵌入中文黑体字体 ====================

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-1", 1L, currency: Currency.USD, totalAmount: 100m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo", "currency", "totalAmount" },
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
        var page = new DynamicPurchaseOrderReportPageDto(
            new List<DynamicPurchaseOrderReportFieldDto> { new("orderNo", "采购单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0,
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicPurchaseOrderPdfExporter.Export(page, null, "none", @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 6. 币种分开小计（不跨币种合计） ====================

    [Fact]
    public void 分组小计_币种分开_绝不跨币种合计()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["supplierId"] = 1L, ["currency"] = "USD", ["totalAmount"] = 100m },
            new() { ["supplierId"] = 1L, ["currency"] = "CNY", ["totalAmount"] = 200m },
        };

        var groups = DynamicPurchaseOrderReportRules.BuildGroupSubtotals(rows, "supplier");
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Subtotals.Count);
        Assert.Contains(group.Subtotals, s => s.Currency == "USD" && s.Amount == 100m);
        Assert.Contains(group.Subtotals, s => s.Currency == "CNY" && s.Amount == 200m);
        Assert.DoesNotContain(group.Subtotals, s => string.IsNullOrEmpty(s.Currency));
    }

    [Fact]
    public async Task ExportPdf_分组时_复用页面小计_币种分开()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-CNY", 1L, currency: Currency.CNY, totalAmount: 100m);
        SeedOrder(db, "PO-USD", 1L, currency: Currency.USD, totalAmount: 50m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" },
            GroupBy = "supplier",
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
    }

    // ==================== 7. 授权 / 无效输入拒绝 ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicPurchaseOrderReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicPurchaseOrderReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicPurchaseOrderReportRequest { PageSize = 101 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicPurchaseOrderReportRequest { Fields = new() { "orderNo", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-RO", 1L);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicPurchaseOrderReportController(new DynamicPurchaseOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicPurchaseOrderReportRequest
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
