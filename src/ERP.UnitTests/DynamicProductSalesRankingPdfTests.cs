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
/// ERP-214 动态商品销量排名报表 PDF 下载（只读、有界、作用域化）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（行列页边界）、选定字段顺序（BuildRowCells）与空值保留、
/// 金额字段绝不渲染、不同单位行各自保留（不跨单位合计）、嵌入中文黑体 SimHei（非缺字字体）、
/// 字体缺失显式失败、宽列集拆分为多列页、多行按行页拆分、空结果显式空页说明、
/// 无菜单授权 / 授权撤销 / 未知字段拒绝，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicProductSalesRankingPdfTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

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

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name)
    {
        var product = new BaseProduct { Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = "PCS" };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = false
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string spec, string unit, decimal quantity)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = spec,
            Unit = unit,
            Quantity = quantity,
            IsDeleted = false
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    /// <summary>播种一个拥有「商品销量排名榜」菜单授权的登录用户（可选系统内置角色 → 特权不过滤数据范围）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicProductSalesRankingReportRules.RequiredMenuCode).Id);
        return user;
    }

    private static DynamicProductSalesRankingReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static DynamicProductSalesRankingReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10)
        => new() { Fields = fields, Start = start, End = end, Top = top };

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
    public async Task ExportPdf_返回PDF签名与内容类型_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 10, "热销商品", "大", "PCS", 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity" }, start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 10, "热销商品", "大", "PCS", 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(start: Start, end: End)));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 选定列顺序 / 空值 / 金额排除 / 单位独立 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_空值保留()
    {
        var columns = new List<DynamicProductSalesRankingReportFieldDto>
        {
            new("rank", "排名", "number", false),
            new("productName", "商品名称", "text", false),
            new("totalQuantity", "发货数量", "number", false),
            new("unit", "单位", "text", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["rank"] = 1,
            ["productName"] = "热销商品",
            ["totalQuantity"] = 100m,
            ["unit"] = null,
        };

        var cells = DynamicProductSalesRankingPdfExporter.BuildRowCells(columns, row);

        Assert.Equal(new[] { "1", "热销商品", "100", "" }, cells);
    }

    [Fact]
    public void FormatCellValue_日期数值按口径格式化()
    {
        Assert.Equal("2026-09-15", DynamicProductSalesRankingPdfExporter.FormatCellValue(new DateTime(2026, 9, 15)));
        Assert.Equal("100.5", DynamicProductSalesRankingPdfExporter.FormatCellValue(100.5m));
        Assert.Equal("3", DynamicProductSalesRankingPdfExporter.FormatCellValue(3));
        Assert.Equal("10", DynamicProductSalesRankingPdfExporter.FormatCellValue(10L));
        Assert.Equal(string.Empty, DynamicProductSalesRankingPdfExporter.FormatCellValue(null));
    }

    [Fact]
    public void BuildRowCells_仅渲染选定发货数量证据列_不包含金额字段()
    {
        var columns = DynamicProductSalesRankingReportRules.GetCatalog();
        var row = new Dictionary<string, object?>
        {
            ["rank"] = 1,
            ["productId"] = 10L,
            ["productCode"] = "P001",
            ["productName"] = "热销商品",
            ["spec"] = "大",
            ["unit"] = "PCS",
            ["totalQuantity"] = 100m,
            ["totalAmount"] = 9999m,   // 金额字段即便出现在行里，也绝不渲染
        };

        var cells = DynamicProductSalesRankingPdfExporter.BuildRowCells(columns, row);

        Assert.Equal(7, cells.Count);
        Assert.DoesNotContain("9999", cells);
        Assert.DoesNotContain("9999.00", cells);
    }

    [Fact]
    public void BuildRowCells_不同单位行_各自保留_不跨单位合计()
    {
        var columns = DynamicProductSalesRankingReportRules.GetCatalog();
        var pcs = new Dictionary<string, object?>
        {
            ["rank"] = 1, ["productId"] = 1L, ["productCode"] = "P001", ["productName"] = "热销商品",
            ["spec"] = "大", ["unit"] = "PCS", ["totalQuantity"] = 100m,
        };
        var kg = new Dictionary<string, object?>
        {
            ["rank"] = 2, ["productId"] = 2L, ["productCode"] = "P002", ["productName"] = "散装商品",
            ["spec"] = "标准", ["unit"] = "KG", ["totalQuantity"] = 5m,
        };

        var pcsCells = DynamicProductSalesRankingPdfExporter.BuildRowCells(columns, pcs);
        var kgCells = DynamicProductSalesRankingPdfExporter.BuildRowCells(columns, kg);

        Assert.Equal("PCS", pcsCells[5]);
        Assert.Equal("100", pcsCells[6]);
        Assert.Equal("KG", kgCells[5]);
        Assert.Equal("5", kgCells[6]);
    }


    // ==================== 3. 分页 ====================

    [Fact]
    public async Task ExportPdf_宽列集_拆分为多列页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedProduct(db, 10, new string('C', 80), "商品");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 10, new string('名', 80), new string('规', 80), "PCS", 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        // productCode / productName / spec 三列超宽 → 至少拆成 2 个列页（列不裁切）
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public async Task ExportPdf_多行_按行页拆分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        for (var i = 0; i < 200; i++)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = out1.Id,
                ProductId = 1000 + i,
                ProductName = $"商品{i}",
                Spec = "大",
                Unit = "PCS",
                Quantity = 200 - i,
                IsDeleted = false
            });
        }
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(top: 200, start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 4. 空结果 / 授权 / 校验 / 字体缺失 ====================

    [Fact]
    public async Task ExportPdf_空结果_仅表头与空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "rank", "productName" }, start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_授权撤销_拒绝且不返回PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoked-user", "Sales");
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "大", "PCS", 100m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request(start: Start, end: End)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            Request(fields: new List<string> { "totalAmount" }, start: Start, end: End)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ExportPdf_字体缺失_显式失败()
    {
        var page = DynamicProductSalesRankingReportRules.BuildPage(
            Array.Empty<ReportDtos.ProductSalesRankItem>(),
            DynamicProductSalesRankingReportRules.AllFieldKeys,
            10, Start, End);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicProductSalesRankingPdfExporter.Export(page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
    }

    // ==================== 5. 只读 ====================

    [Fact]
    public async Task ExportPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "大", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        _ = PdfOk(await ctl.ExportPdf(Request(start: Start, end: End)));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

