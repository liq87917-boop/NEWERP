using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-217 动态商品销量排名报表「按单位分组」Excel 导出单元测试。
/// <para>覆盖：unit 分组时在同一工作簿追加「单位汇总」工作表（排名桶数 / 同单位签名数量小计为数值、未知单位显式「未知」绝不回落 0、
/// 单位文本公式注入转义）、隐藏单位字段仍按服务端精确单位派生、Top/日期/分组口径上下文、none 分组保持既有单工作表明细行为、
/// 空结果仅表头 + 空说明、授权撤销拒绝，以及只读不写库。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicProductSalesRankingGroupedExcelTests
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

    private static FileContentResult ExportOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        return file;
    }

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new XSSFWorkbook(ms);
    }

    private static DynamicProductSalesRankingReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10,
        string? groupBy = null, ProductSalesRankingFilterDto? filter = null)
        => new()
        {
            Fields = fields,
            Start = start,
            End = end,
            Top = top,
            GroupBy = groupBy,
            Filter = filter
        };

    // ==================== 1. unit 分组 → 单位汇总工作表 ====================

    [Fact]
    public async Task Export_unit分组_追加单位汇总工作表_桶数与签名数量数值_未知单位显式未知()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "大", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "商品B", "中", "BOX", -30m);
        SeedDetail(db, out1.Id, 3, "商品C", "小", "", 20m);
        SeedDetail(db, out1.Id, 4, "商品D", "大", "PCS", 50m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity", "unit" },
            start: Start, end: End, groupBy: "unit")));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(3, workbook.NumberOfSheets);
        Assert.Equal(DynamicProductSalesRankingReportRules.RequiredMenuText, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);

        var summary = workbook.GetSheetAt(2);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummarySheetName, summary.SheetName);

        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryUnitColumn, summary.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryBucketCountColumn, summary.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryQuantityColumn, summary.GetRow(0).GetCell(2).StringCellValue);

        // PCS：2 桶、签名数量小计 150（100 + 50，数值单元格）
        Assert.Equal("PCS", summary.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, summary.GetRow(1).GetCell(1).CellType);
        Assert.Equal(2d, summary.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal(CellType.Numeric, summary.GetRow(1).GetCell(2).CellType);
        Assert.Equal(150d, summary.GetRow(1).GetCell(2).NumericCellValue);

        // BOX：1 桶、负数签名数量小计 -30（负数保留符号，数值单元格）
        Assert.Equal("BOX", summary.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, summary.GetRow(2).GetCell(1).CellType);
        Assert.Equal(1d, summary.GetRow(2).GetCell(1).NumericCellValue);
        Assert.Equal(CellType.Numeric, summary.GetRow(2).GetCell(2).CellType);
        Assert.Equal(-30d, summary.GetRow(2).GetCell(2).NumericCellValue);

        // 空白单位：独立「未知单位」桶、1 桶、数量显式「未知」且绝不回落 0
        Assert.Equal(DynamicProductSalesRankingReportRules.UnknownUnitLabel, summary.GetRow(3).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, summary.GetRow(3).GetCell(1).CellType);
        Assert.Equal(1d, summary.GetRow(3).GetCell(1).NumericCellValue);
        Assert.NotEqual(CellType.Numeric, summary.GetRow(3).GetCell(2).CellType);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnknownQuantityText, summary.GetRow(3).GetCell(2).StringCellValue);

        // 分组口径 / Top / 日期上下文（仅当前 Top 结果）
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryGroupContextLabel, summary.GetRow(5).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.GroupContextText, summary.GetRow(5).GetCell(1).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryTopLabel, summary.GetRow(6).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryStartLabel, summary.GetRow(7).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", summary.GetRow(7).GetCell(1).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryEndLabel, summary.GetRow(8).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", summary.GetRow(8).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_none分组_保持既有单工作表明细行为_不追加单位汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "大", "PCS", 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName", "totalQuantity", "unit" },
            start: Start, end: End)));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicProductSalesRankingReportRules.RequiredMenuText, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);
        Assert.DoesNotContain(DynamicProductSalesRankingReportRules.UnitSummarySheetName,
            Enumerable.Range(0, workbook.NumberOfSheets).Select(i => workbook.GetSheetAt(i).SheetName));
    }

    [Fact]
    public async Task Export_unit分组_隐藏单位字段_仍按服务端精确单位派生汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "大", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "商品B", "中", "BOX", 40m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 明细列故意不含 unit：汇总仍由服务端从同一批 Top 排名项派生（绝不采纳客户端行 / 聚合）
        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName" },
            start: Start, end: End, groupBy: "unit")));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = workbook.GetSheetAt(2);
        Assert.Equal("PCS", summary.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(100d, summary.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal("BOX", summary.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal(40d, summary.GetRow(2).GetCell(2).NumericCellValue);
    }

    [Fact]
    public async Task Export_unit分组_公式前导单位标签_转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "大", "=EVIL", 5m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "unit" }, start: Start, end: End, groupBy: "unit")));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = workbook.GetSheetAt(2);
        var cell = summary.GetRow(1).GetCell(0);
        Assert.NotEqual(CellType.Formula, cell.CellType);
        Assert.Equal("'=EVIL", cell.StringCellValue);
    }

    [Fact]
    public async Task Export_unit分组_含筛选_单位汇总表标注筛选上下文()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        var out1 = SeedStockOut(db, "OUT-1", c1.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "大", "PCS", 100m);
        var out2 = SeedStockOut(db, "OUT-2", c2.Id, DocumentStatus.Approved);
        SeedDetail(db, out2.Id, 2, "商品B", "中", "BOX", 40m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName", "unit" },
            start: Start, end: End, groupBy: "unit",
            filter: new ProductSalesRankingFilterDto { CustomerId = c1.Id })));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = workbook.GetSheetAt(2);
        // 行 0 表头、1 PCS、2 空、3 分组口径、4 Top、5 开始、6 结束、7 筛选
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryFilterLabel, summary.GetRow(7).GetCell(0).StringCellValue);
        Assert.Equal($"客户 Id {c1.Id}", summary.GetRow(7).GetCell(1).StringCellValue);
    }

    // ==================== 2. 空结果 / 授权撤销 / 只读 ====================

    [Fact]
    public async Task Export_unit分组_空结果_单位汇总表仅表头与空说明_仍含上下文()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName" }, start: Start, end: End, groupBy: "unit")));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(3, workbook.NumberOfSheets);

        var summary = workbook.GetSheetAt(2);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryUnitColumn, summary.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryEmptyNote, summary.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitSummaryGroupContextLabel, summary.GetRow(3).GetCell(0).StringCellValue);
    }

    [Fact]
    public async Task Export_unit分组_授权撤销_拒绝且不返回工作簿()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            Request(start: Start, end: End, groupBy: "unit")));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_unit分组_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "大", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        _ = ExportOk(await ctl.Export(Request(start: Start, end: End, groupBy: "unit")));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
