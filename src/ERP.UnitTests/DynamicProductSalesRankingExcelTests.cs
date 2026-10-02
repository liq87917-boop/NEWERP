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
/// ERP-213 动态商品销量排名报表 Excel 导出（只读、Top 有界）单元测试。
/// 覆盖：选定列顺序与行值（排名 / 商品Id / 发货数量为数值单元格、文本保持文本）、公式前导文本转义（保持字面、非公式单元格）、
/// 仅导出选定 Top 结果字段（排除金额估算）、上下文工作表标注日期 / Top / 发货证据 / 单位口径（绝不追加金额合计）、
/// 授权撤销 / 未知字段拒绝、空结果仅表头 + 空说明，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicProductSalesRankingExcelTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

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
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10)
        => new() { Fields = fields, Start = start, End = end, Top = top };

    // ==================== 1. 选定列顺序与行值 ====================

    [Fact]
    public async Task Export_选定列顺序与行值_数值与文本保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 10, "热销商品", "大", "PCS", 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productId", "productName", "totalQuantity", "unit" },
            start: Start, end: End)));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("排名", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("商品Id", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("商品名称", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("发货数量", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal("单位", sheet.GetRow(0).GetCell(4).StringCellValue);

        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(0).CellType);
        Assert.Equal(1d, sheet.GetRow(1).GetCell(0).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(1).CellType);
        Assert.Equal(10d, sheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("热销商品", sheet.GetRow(1).GetCell(2).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(3).CellType);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal("PCS", sheet.GetRow(1).GetCell(4).StringCellValue);
    }

    // ==================== 2. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "=1+1", "大", "PCS", 5m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "productName" }, start: Start, end: End)));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.NotEqual(CellType.Formula, cell.CellType);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    [Fact]
    public void EscapeFormulaLeading_危险字符转义_普通值原样()
    {
        Assert.False(DynamicProductSalesRankingReportRules.IsFormulaLeading(null));
        Assert.False(DynamicProductSalesRankingReportRules.IsFormulaLeading(""));
        Assert.False(DynamicProductSalesRankingReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicProductSalesRankingReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicProductSalesRankingReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicProductSalesRankingReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicProductSalesRankingReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicProductSalesRankingReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicProductSalesRankingReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicProductSalesRankingReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicProductSalesRankingReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicProductSalesRankingReportRules.EscapeFormulaLeading(123));

        var exportRow = DynamicProductSalesRankingReportRules.BuildExportRow(
            new Dictionary<string, object?> { ["productName"] = "=1+1", ["totalQuantity"] = 3m });
        Assert.Equal("'=1+1", exportRow["productName"]);
        Assert.Equal(3m, exportRow["totalQuantity"]);
    }

    // ==================== 3. 上下文工作表 ====================

    [Fact]
    public async Task Export_上下文工作表_标注日期Top发货证据单位口径_无金额合计()
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

        var context = workbook.GetSheetAt(1);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextSheetName, context.SheetName);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextStartLabel, context.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", context.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextEndLabel, context.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", context.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextTopLabel, context.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("Top 10", context.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextApprovedShipmentLabel, context.GetRow(3).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextUnitLabel, context.GetRow(4).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnitContextText, context.GetRow(4).GetCell(1).StringCellValue);
    }

    // ==================== 4. 授权 / 校验 / 空结果 ====================

    [Fact]
    public async Task Export_授权撤销_拒绝且不返回工作簿()
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(Request(start: Start, end: End)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            Request(fields: new List<string> { "totalAmount" }, start: Start, end: End)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_空结果_数据表仅表头_上下文表含空说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(Request(
            fields: new List<string> { "rank", "productName" }, start: Start, end: End)));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);

        var data = workbook.GetSheetAt(0);
        Assert.Equal("排名", data.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("商品名称", data.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(0, data.LastRowNum);

        var context = workbook.GetSheetAt(1);
        Assert.Equal(DynamicProductSalesRankingReportRules.ContextEmptyLabel, context.GetRow(6).GetCell(0).StringCellValue);
        Assert.Equal(DynamicProductSalesRankingReportRules.EmptyText, context.GetRow(6).GetCell(1).StringCellValue);
    }

    // ==================== 5. 只读 ====================

    [Fact]
    public async Task Export_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "大", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        _ = ExportOk(await ctl.Export(Request(start: Start, end: End)));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
