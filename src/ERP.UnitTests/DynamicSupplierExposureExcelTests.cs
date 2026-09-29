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
/// ERP-150 动态供应商采购敞口 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与行值、供应商 / 采购单号标识与原币保留（不做换算）、公式前导文本转义（保持字面、非公式单元格）、
/// 仅导出当前页（单页 200 上限）、链接不唯一 / 无引用未知结算金额保持未知（绝不回落为 0）、无身份 / 无菜单授权 /
/// 页大小超限 / 未知字段拒绝、空页仅表头、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierExposureExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 968001L;
    private const long ProductA = 968101L;

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

    /// <summary>播种一个「系统内置角色 + 采购订单菜单授权」用户</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "purchase-order").Id);
        return user.Id;
    }

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier { Id = id, SupplierCode = $"S{id}", SupplierName = name });

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo)
    {
        var salesOrder = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-30),
            CustomerId = 1,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 8, 25, 8, 0, 0),
        };
        db.SalesOrders.Add(salesOrder);
        db.SaveChanges();
        return salesOrder;
    }

    /// <summary>写入一张采购订单（含 1 行明细；明细数量用于收货派生的冲抵口径）</summary>
    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, Currency currency,
        decimal totalAmount, long? owningSalesOrderId, (long ProductId, decimal Quantity) line,
        DateTime? orderDate = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = owningSalesOrderId.HasValue ? $"SO#{owningSalesOrderId.Value}" : string.Empty,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 9, 14, 8, 0, 0),
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = line.ProductId,
            ProductName = $"商品{line.ProductId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = line.Quantity,
            UnitPrice = line.Quantity == 0 ? 0m : totalAmount / line.Quantity,
            Amount = totalAmount,
        });
        db.SaveChanges();
        return order;
    }

    private static DynamicSupplierExposureReportController NewController(IErpDbContext db)
        => new(db);

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

    // ==================== 1. 列顺序与行值 ====================

    [Fact]
    public async Task Export_导出当前页_列顺序与行值符合选定字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-XLS-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-XLS-2", SupplierA, Currency.CNY, 200m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo", "orderedAmount" },
            Page = 1,
            PageSize = 10,
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("采购单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("订单金额", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(2, sheet.LastRowNum);
        Assert.Equal("PO-XLS-1", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("PO-XLS-2", sheet.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal(200d, sheet.GetRow(2).GetCell(1).NumericCellValue);
    }

    [Fact]
    public async Task Export_保留供应商订单标识与原币_不做换算()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-USD-X", SupplierA, Currency.USD, 123.45m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "supplierId", "orderNo", "currency", "orderedAmount" },
            PageSize = 10,
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("供应商Id", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("采购单号", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("订单金额", sheet.GetRow(0).GetCell(3).StringCellValue);

        Assert.Equal((double)SupplierA, sheet.GetRow(1).GetCell(0).NumericCellValue);
        Assert.Equal("PO-USD-X", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal("USD", sheet.GetRow(1).GetCell(2).StringCellValue);
        Assert.Equal(123.45d, sheet.GetRow(1).GetCell(3).NumericCellValue);
    }

    // ==================== 2. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本_非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "=HYPERLINK(\"http://evil\")", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10,
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("采购单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=HYPERLINK(\"http://evil\")", cell.StringCellValue);
    }

    [Fact]
    public void EscapeFormulaLeading_危险字符转义_普通值原样()
    {
        Assert.False(DynamicSupplierExposureReportRules.IsFormulaLeading(null));
        Assert.False(DynamicSupplierExposureReportRules.IsFormulaLeading(""));
        Assert.False(DynamicSupplierExposureReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicSupplierExposureReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicSupplierExposureReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicSupplierExposureReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicSupplierExposureReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicSupplierExposureReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicSupplierExposureReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicSupplierExposureReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicSupplierExposureReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicSupplierExposureReportRules.EscapeFormulaLeading(123));

        var exportRow = DynamicSupplierExposureReportRules.BuildExportRow(
            new Dictionary<string, object?> { ["orderNo"] = "=1+1", ["orderedAmount"] = 100m });
        Assert.Equal("'=1+1", exportRow["orderNo"]);
        Assert.Equal(100m, exportRow["orderedAmount"]);
    }

    // ==================== 3. 仅导出当前页（单页上限） ====================

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-XLS-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-XLS-2", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-XLS-3", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            Page = 1,
            PageSize = 2,
        }));
        Assert.Equal(2, OpenWorkbook(page1.FileContents).GetSheetAt(0).LastRowNum);

        var page2 = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            Page = 2,
            PageSize = 2,
        }));
        Assert.Equal(1, OpenWorkbook(page2.FileContents).GetSheetAt(0).LastRowNum);
    }

    // ==================== 4. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task Export_无采购订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSupplierExposureReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSupplierExposureReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSupplierExposureReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSupplierExposureReportRequest { Fields = new() { "orderNo", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 5. 空页 ====================

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10,
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("采购单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }

    // ==================== 6. 链接不唯一 / 无引用：未知结算金额保持未知 ====================

    [Fact]
    public async Task Export_链接不唯一与无引用_未知结算金额保持未知()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-XLS");
        SeedOrder(db, "PO-AMB-1", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-AMB-2", SupplierA, Currency.CNY, 200m, salesOrder.Id, (ProductA, 1m)); // 同销售订单两张 → 不唯一
        SeedOrder(db, "PO-UNAV", SupplierA, Currency.CNY, 50m, null, (ProductA, 1m));           // 无归属销售订单
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo", "linkStatus", "settledAmount" },
            PageSize = 10,
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("采购单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("链接状态", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("已结算金额", sheet.GetRow(0).GetCell(2).StringCellValue);

        var rows = new Dictionary<string, (ICell Link, ICell Settled)>(StringComparer.Ordinal);
        for (var r = 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            rows[row.GetCell(0).StringCellValue] = (row.GetCell(1), row.GetCell(2));
        }

        Assert.Equal(DynamicSupplierExposureReportRules.LinkAmbiguous, rows["PO-AMB-1"].Link.StringCellValue);
        Assert.NotEqual(CellType.Numeric, rows["PO-AMB-1"].Settled.CellType);
        Assert.Equal(DynamicSupplierExposureReportRules.LinkUnavailable, rows["PO-UNAV"].Link.StringCellValue);
        Assert.NotEqual(CellType.Numeric, rows["PO-UNAV"].Settled.CellType);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-RO", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierExposureReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10,
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
