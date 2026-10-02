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
/// ERP-218 动态商品销量排名报表「按单位分组」PDF 导出单元测试。
/// <para>语义：<see cref="DynamicProductSalesRankingPdfExporter.Export(DynamicProductSalesRankingReportPageDto)"/> 在
/// unit 分组模式下，于选定明细列证据页之后追加独立的「单位汇总」分区（复用 ERP-216 同一批服务端派生分组桶），
/// 按精确单位稳定呈现排名桶数与同单位签名数量小计（未知单位数量显式「未知」，绝不回落 0），并显式标注日期 / Top / 分组 / 单位口径；
/// none 保持既有证据 PDF 不变。全部使用内存数据库（TestDbFactory）或直接构造页 DTO，不连接 SQL Server、不执行任何 SQL。</para>
/// <para>覆盖：none 证据页不变、unit 分组追加单位汇总分区、空结果空提示、多分组纵向分页、长单位标签不崩溃、缺失字体显式失败、
/// 无效分组键 / 授权撤销拒绝，以及只读不写库。</para>
/// </summary>
public class DynamicProductSalesRankingGroupedPdfTests
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

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
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

    /// <summary>播种一个拥有「商品销量排名榜」菜单授权的登录用户（系统内置角色 → 特权不过滤数据范围）</summary>
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
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10,
        string? groupBy = null, ProductSalesRankingFilterDto? filter = null)
        => new() { Fields = fields, Start = start, End = end, Top = top, GroupBy = groupBy, Filter = filter };

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

    private static List<DynamicProductSalesRankingReportFieldDto> Columns()
        => new()
        {
            new("rank", "排名", "number", false),
            new("unit", "单位", "text", false),
            new("totalQuantity", "发货数量", "number", false),
        };

    private static DynamicProductSalesRankingReportPageDto MakePage(
        List<DynamicProductSalesRankingReportFieldDto> columns,
        List<Dictionary<string, object?>> rows)
        => new(
            columns,
            rows,
            rows.Count,
            200,
            rows.Count >= 200,
            rows.Count == 0 ? DynamicProductSalesRankingReportRules.EmptyText : string.Empty,
            DynamicProductSalesRankingReportRules.ReadOnlyText,
            DynamicProductSalesRankingReportRules.BoundaryText,
            DynamicProductSalesRankingReportRules.DisclaimerText,
            DynamicProductSalesRankingReportRules.UnitContextText,
            DynamicProductSalesRankingReportRules.ApprovedShipmentText,
            Start,
            End);

    private static DynamicProductSalesRankingReportPageDto MakeGroupedPage(
        List<DynamicProductSalesRankingReportFieldDto> columns,
        List<Dictionary<string, object?>> rows,
        string groupBy,
        List<DynamicProductSalesRankingReportGroupDto> groups)
        => new(
            columns,
            rows,
            rows.Count,
            200,
            rows.Count >= 200,
            rows.Count == 0 ? DynamicProductSalesRankingReportRules.EmptyText : string.Empty,
            DynamicProductSalesRankingReportRules.ReadOnlyText,
            DynamicProductSalesRankingReportRules.BoundaryText,
            DynamicProductSalesRankingReportRules.DisclaimerText,
            DynamicProductSalesRankingReportRules.UnitContextText,
            DynamicProductSalesRankingReportRules.ApprovedShipmentText,
            Start,
            End,
            string.Empty,
            groupBy,
            groups,
            groups.Count == 0 ? string.Empty : DynamicProductSalesRankingReportRules.GroupContextText);

    private static DynamicProductSalesRankingReportGroupDto MakeGroup(
        string unit, int bucketCount, decimal? totalQuantity, bool isUnknown = false)
        => new(unit, isUnknown ? DynamicProductSalesRankingReportRules.UnknownUnitLabel : unit, bucketCount, totalQuantity, isUnknown);

    // ==================== 1. none 保持既有证据 PDF 不变 ====================

    [Fact]
    public void ExportPdf_none分组_保持既有证据页_不追加单位汇总()
    {
        var rows = Enumerable.Range(1, 10).Select(i => new Dictionary<string, object?>
        {
            ["rank"] = i, ["unit"] = "PCS", ["totalQuantity"] = 100m - i,
        }).ToList();

        var bytes = DynamicProductSalesRankingPdfExporter.Export(MakePage(Columns(), rows));
        using var pdf = OpenPdf(bytes);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 2. unit 分组追加单位汇总分区 ====================

    [Fact]
    public void ExportPdf_unit分组_追加单位汇总分区()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["rank"] = 1, ["unit"] = "PCS", ["totalQuantity"] = 100m },
            new() { ["rank"] = 2, ["unit"] = "BOX", ["totalQuantity"] = -40m },
            new() { ["rank"] = 3, ["unit"] = " ", ["totalQuantity"] = 5m },
        };
        var groups = new List<DynamicProductSalesRankingReportGroupDto>
        {
            MakeGroup("PCS", 1, 100m),
            MakeGroup("BOX", 1, -40m),
            MakeGroup(string.Empty, 1, null, isUnknown: true),
        };

        var page = MakeGroupedPage(Columns(), rows, DynamicProductSalesRankingReportRules.GroupUnit, groups);
        var bytes = DynamicProductSalesRankingPdfExporter.Export(page);
        using var pdf = OpenPdf(bytes);

        // 明细证据页（1 页）+ 单位汇总分区（1 页）
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public void BuildGroupSummaryRows_精确单位签名数量与未知单位证据()
    {
        var groups = new List<DynamicProductSalesRankingReportGroupDto>
        {
            MakeGroup("PCS", 2, 100m),
            MakeGroup("BOX", 1, -40m),
            MakeGroup(string.Empty, 1, null, isUnknown: true),
        };

        var rows = DynamicProductSalesRankingPdfExporter.BuildGroupSummaryRows(groups);

        Assert.Equal(3, rows.Count);

        Assert.Equal("PCS", rows[0][0]);
        Assert.Equal("2", rows[0][1]);
        Assert.Equal("100", rows[0][2]);

        Assert.Equal("BOX", rows[1][0]);
        Assert.Equal("1", rows[1][1]);
        Assert.Equal("-40", rows[1][2]);

        Assert.Equal(DynamicProductSalesRankingReportRules.UnknownUnitLabel, rows[2][0]);
        Assert.Equal("1", rows[2][1]);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnknownQuantityText, rows[2][2]);
    }

    // ==================== 3. 空结果空提示 ====================

    [Fact]
    public async Task ExportPdf_unit分组_空结果_明细空页与单位汇总空提示()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportPdf(Request(
            fields: new List<string> { "rank", "unit", "totalQuantity" }, start: Start, end: End, groupBy: "unit")));

        // 明细证据空页（1 页）+ 单位汇总空提示（1 页）
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    // ==================== 4. 拒绝访问 / 无效分组键（fail closed） ====================

    [Fact]
    public async Task ExportPdf_unit分组_授权撤销_拒绝且不返回PDF()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request(start: Start, end: End, groupBy: "unit")));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_unit分组_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(Request(start: Start, end: End, groupBy: "quarter")));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 5. 多分组纵向分页避免裁切 ====================

    [Fact]
    public void ExportPdf_多分组_纵向分页_避免裁切()
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["rank"] = 1, ["unit"] = "PCS", ["totalQuantity"] = 100m } };
        var groups = Enumerable.Range(1, 40)
            .Select(i => MakeGroup($"U{i:000}", 1, (100 - i) * 1m))
            .ToList();

        var page = MakeGroupedPage(Columns(), rows, DynamicProductSalesRankingReportRules.GroupUnit, groups);
        var bytes = DynamicProductSalesRankingPdfExporter.Export(page);
        using var pdf = OpenPdf(bytes);

        // 明细证据页（1 页）+ 单位汇总分区多页（纵向分页）
        Assert.True(pdf.Pages.Count > 2);
    }

    // ==================== 6. 长单位标签渲染不崩溃 ====================

    [Fact]
    public void ExportPdf_长单位标签_渲染不崩溃()
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["rank"] = 1, ["unit"] = "PCS", ["totalQuantity"] = 100m } };
        var longUnit = "超长单位" + new string('甲', 300);
        var groups = new List<DynamicProductSalesRankingReportGroupDto> { MakeGroup(longUnit, 1, 10m) };

        var page = MakeGroupedPage(Columns(), rows, DynamicProductSalesRankingReportRules.GroupUnit, groups);
        var bytes = DynamicProductSalesRankingPdfExporter.Export(page);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 7. 缺失字体显式失败 ====================

    [Fact]
    public void ExportPdf_unit分组_字体缺失_显式失败()
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["rank"] = 1, ["unit"] = "PCS", ["totalQuantity"] = 100m } };
        var groups = new List<DynamicProductSalesRankingReportGroupDto> { MakeGroup("PCS", 1, 100m) };
        var page = MakeGroupedPage(Columns(), rows, DynamicProductSalesRankingReportRules.GroupUnit, groups);

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicProductSalesRankingPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 8. 只读 ====================

    [Fact]
    public async Task ExportPdf_unit分组_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "大", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        _ = PdfOk(await ctl.ExportPdf(Request(start: Start, end: End, groupBy: "unit")));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
