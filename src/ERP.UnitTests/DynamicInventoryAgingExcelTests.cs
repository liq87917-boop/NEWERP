using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-138 动态库存库龄与成本估值报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与行值、基础单位与 CNY 币种标注、固定 5 格库龄分层数量 / 金额、
/// 未知成本金额显式保留为未知（null → 空单元格、绝不回落为 0）、公式前导文本转义（保持字面、非公式单元格）、
/// 仅导出当前页（单页 200 上限）、无身份 / 无菜单授权 / 页大小超限 / 未知字段拒绝、空页仅表头、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicInventoryAgingExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long WarehouseA = 920001L;
    private const long Product1 = 720001L;
    private const long Product2 = 720002L;

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

    /// <summary>播种一个「库存查询菜单授权」登录用户并返回其用户 Id</summary>
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "stock-user")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicInventoryAgingReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name)
    {
        var warehouse = new BaseWarehouse { Id = id, WarehouseCode = $"WH{id}", WarehouseName = name };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        return warehouse;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name, string unit = "PCS")
    {
        var product = new BaseProduct { Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = unit };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static Stock SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal averageCost = 0m, decimal totalCost = 0m)
    {
        var stock = new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            AverageCost = averageCost,
            TotalCost = totalCost
        };
        db.Stocks.Add(stock);
        db.SaveChanges();
        return stock;
    }

    /// <summary>写入一条库存流水（方向 1=入库 / -1=出库；基础单位数量）</summary>
    private static StockMovement SeedMovement(ErpDbContext db, long warehouseId, long productId,
        DateTime movementDate, int direction, decimal quantity, long id = 0)
    {
        var movement = new StockMovement
        {
            Id = id,
            MovementDate = movementDate,
            MovementType = direction > 0 ? InventoryMovementType.PurchaseIn : InventoryMovementType.SalesOut,
            SourceDocType = direction > 0 ? "StockIn" : "StockOut",
            SourceDocId = 1,
            SourceDocNo = direction > 0 ? "SI-0001" : "SO-0001",
            WarehouseId = warehouseId,
            WarehouseName = "主仓",
            ProductId = productId,
            ProductCode = "P001",
            ProductName = "商品一",
            Spec = "标准",
            Unit = "PCS",
            Direction = direction,
            Quantity = quantity,
            UnitCost = 0m,
            Amount = 0m,
            IsReversal = false,
            IsReversed = false
        };
        db.StockMovements.Add(movement);
        db.SaveChanges();
        return movement;
    }

    private static DynamicInventoryAgingReportController NewController(ErpDbContext db)
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

    // ==================== 1. 选定列顺序与行值 ====================

    [Fact]
    public async Task Export_选定列顺序与行值_基础单位与CNY保留()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一", unit: "KG");
        SeedStock(db, WarehouseA, Product1, 12m, averageCost: 10m, totalCost: 120m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 12m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productCode", "unit", "currentQuantity", "costCurrency" },
            AsOfDate = AsOf,
            Page = 1,
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("商品编码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("基础单位", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("当前现存量", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("成本币种", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal(1, sheet.LastRowNum);

        Assert.Equal("P001", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("KG", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(12d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, sheet.GetRow(1).GetCell(3).StringCellValue);
    }

    // ==================== 2. 固定 5 格库龄分层 ====================

    [Fact]
    public async Task Export_固定库龄分层数量与金额()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");

        var ages = new[] { 0, 30, 31, 60, 61, 90, 91, 180, 181 };
        long movementId = 1;
        foreach (var age in ages)
            SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-age), 1, 1m, id: movementId++);
        SeedStock(db, WarehouseA, Product1, ages.Length, averageCost: 10m, totalCost: 90m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new()
            {
                "bucket0To30Quantity", "bucket0To30Amount",
                "bucket31To60Quantity", "bucket31To60Amount",
                "bucket61To90Quantity", "bucket61To90Amount",
                "bucket91To180Quantity", "bucket91To180Amount",
                "bucketOver180Quantity", "bucketOver180Amount"
            },
            AsOfDate = AsOf,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        var row = sheet.GetRow(1);
        Assert.Equal(new[] { 2d, 2d, 2d, 2d, 1d }, new[]
        {
            row.GetCell(0).NumericCellValue,
            row.GetCell(2).NumericCellValue,
            row.GetCell(4).NumericCellValue,
            row.GetCell(6).NumericCellValue,
            row.GetCell(8).NumericCellValue,
        });
        Assert.Equal(new[] { 20d, 20d, 20d, 20d, 10d }, new[]
        {
            row.GetCell(1).NumericCellValue,
            row.GetCell(3).NumericCellValue,
            row.GetCell(5).NumericCellValue,
            row.GetCell(7).NumericCellValue,
            row.GetCell(9).NumericCellValue,
        });
    }

    // ==================== 3. 未知成本金额（不回落为 0） ====================

    [Fact]
    public async Task Export_未知成本金额_显式保留为未知不回落为0()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 5m, averageCost: 0m, totalCost: 0m);   // 无成本依据
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 5m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new()
            {
                "costStatus", "authoritativeAmount", "bucket0To30Amount", "unknownCostQuantity", "costCurrency"
            },
            AsOfDate = AsOf,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        var row = sheet.GetRow(1);
        Assert.Equal(InventoryAgingSemantics.CostUnknown, row.GetCell(0).StringCellValue);
        // 未知成本金额显式保留为未知（空单元格），绝不回落为 0
        Assert.Equal(string.Empty, row.GetCell(1).StringCellValue);
        Assert.Equal(string.Empty, row.GetCell(2).StringCellValue);
        Assert.Equal(5d, row.GetCell(3).NumericCellValue);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, row.GetCell(4).StringCellValue);
    }

    // ==================== 4. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "=1+1", "商品一");
        SeedStock(db, WarehouseA, Product1, 5m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productCode" },
            AsOfDate = AsOf,
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.NotEqual(CellType.Formula, cell.CellType);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    [Fact]
    public void EscapeFormulaLeading_危险字符转义_普通值原样()
    {
        Assert.False(DynamicInventoryAgingReportRules.IsFormulaLeading(null));
        Assert.False(DynamicInventoryAgingReportRules.IsFormulaLeading(""));
        Assert.False(DynamicInventoryAgingReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicInventoryAgingReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicInventoryAgingReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicInventoryAgingReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicInventoryAgingReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicInventoryAgingReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicInventoryAgingReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicInventoryAgingReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicInventoryAgingReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicInventoryAgingReportRules.EscapeFormulaLeading(123));
    }

    // ==================== 5. 仅导出当前页 + 页大小上限 ====================

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 1m);
        SeedStock(db, WarehouseA, Product2, 1m);
        SeedStock(db, WarehouseA, 720003L, 1m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productId" }, AsOfDate = AsOf, Page = 1, PageSize = 2
        }));
        Assert.Equal(2, OpenWorkbook(page1.FileContents).GetSheetAt(0).LastRowNum);

        var page2 = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productId" }, AsOfDate = AsOf, Page = 2, PageSize = 2
        }));
        Assert.Equal(1, OpenWorkbook(page2.FileContents).GetSheetAt(0).LastRowNum);
    }

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicInventoryAgingReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicInventoryAgingReportRequest { Fields = new() { "productName", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 空页仅表头 ====================

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productCode" }, AsOfDate = AsOf, PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("商品编码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }

    // ==================== 7. 未授权 / 只读 ====================

    [Fact]
    public async Task Export_无库存查询菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicInventoryAgingReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicInventoryAgingReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 12m, averageCost: 10m, totalCost: 120m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicInventoryAgingReportController(counting.Proxy, new ReportService(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productName" }, AsOfDate = AsOf, PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 8. 只读计数上下文（断言不写库） ====================

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



