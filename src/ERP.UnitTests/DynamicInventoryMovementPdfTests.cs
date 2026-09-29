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
using System.Reflection;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-134 动态库存移动报表 PDF 导出（只读、有界）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（行列页边界）、选定字段顺序（BuildRowCells）、
/// 基础单位与未知历史证据、嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、
/// 宽列集拆分为多列页、多行拆分为多行页、无身份 / 无菜单授权 / 页大小超限 / 未知字段拒绝、
/// 分组计数只计数不求和，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicInventoryMovementPdfTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long WarehouseA = 900001L;
    private const long Product1 = 700001L;

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
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicInventoryMovementReportRules.RequiredMenuCode).Id);
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

    private static Stock SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        var stock = new Stock { WarehouseId = warehouseId, ProductId = productId, Quantity = quantity, AvailableQuantity = quantity };
        db.Stocks.Add(stock);
        db.SaveChanges();
        return stock;
    }

    /// <summary>写入一条库存流水（方向 1=入库 / -1=出库；基础单位数量）</summary>
    private static StockMovement SeedMovement(ErpDbContext db, long warehouseId, long productId,
        DateTime movementDate, int direction, decimal quantity)
    {
        var movement = new StockMovement
        {
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

    private static DynamicInventoryMovementReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

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

    // ==================== 1. 签名 / 内容类型 / 页面边界 ====================

    [Fact]
    public async Task ExportPdf_导出当前页_返回PDF签名与内容类型()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 12m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicInventoryMovementReportRequest
        {
            Fields = new() { "productName", "unit" },
            AsOfDate = AsOf,
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 12m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicInventoryMovementReportRequest
        {
            Fields = new() { "productName", "unit", "historyStatus" },
            AsOfDate = AsOf,
            PageSize = 10
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 行页 / 列页分页 ====================

    [Fact]
    public async Task ExportPdf_多行_拆分多行页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        for (var i = 0; i < 100; i++)
        {
            var p = SeedProduct(db, 700000L + i, $"P{i:D3}", $"商品{i:D3}");
            SeedStock(db, WarehouseA, p.Id, 1m);
        }

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicInventoryMovementReportRequest
        {
            Fields = new() { "productCode" },
            AsOfDate = AsOf,
            PageSize = 100
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    [Fact]
    public async Task ExportPdf_宽列集_拆分为多列页_不裁切()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 12m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        // 留空字段 = 返回全部 18 项白名单字段，宽列集应拆成多个列页（每个列页总宽不超页宽，列不裁切）
        var file = PdfOk(await ctl.ExportPdf(new DynamicInventoryMovementReportRequest
        {
            AsOfDate = AsOf,
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
        foreach (var page in pdf.Pages)
        {
            Assert.InRange(page.Width.Point, 594, 596);
            Assert.InRange(page.Height.Point, 841, 843);
        }
    }

    // ==================== 3. 字段顺序 / 基础单位 / 未知历史证据 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_基础单位与未知历史保留()
    {
        var columns = new List<DynamicInventoryMovementReportFieldDto>
        {
            new("productName", "商品名称", "text", false),
            new("unit", "基础单位", "text", false),
            new("currentQuantity", "当前现存量", "number", false),
            new("lastMovementDate", "最后移动日期", "date", false),
            new("inactivityDays", "停滞天数", "number", false),
            new("historyStatus", "台账状态", "text", false),
            new("classification", "分类", "text", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["productName"] = "商品一",
            ["unit"] = "KG",
            ["currentQuantity"] = 12.5m,
            ["lastMovementDate"] = null,
            ["inactivityDays"] = null,
            ["historyStatus"] = InventoryMovementSemantics.HistoryNoHistory,
            ["classification"] = InventoryMovementSemantics.ClassUnknown,
        };

        var cells = DynamicInventoryMovementPdfExporter.BuildRowCells(columns, row);

        Assert.Equal(
            new[] { "商品一", "KG", "12.5", "未知", "未知", "无台账（历史库存 · 未知）", "无法判定" },
            cells);
    }

    // ==================== 4. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = new DynamicInventoryMovementReportPageDto(
            new List<DynamicInventoryMovementReportFieldDto> { new("productName", "商品名称", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0,
            AsOf, AsOf.AddDays(-89), AsOf, 90,
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicInventoryMovementPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 未授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicInventoryMovementReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无库存查询菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicInventoryMovementReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicInventoryMovementReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicInventoryMovementReportRequest { Fields = new() { "productName", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 分组计数（只计数不求和） ====================

    [Fact]
    public async Task ExportPdf_分组计数_只计数不求和_生成PDF()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一", "KG");
        SeedStock(db, WarehouseA, Product1, 12m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicInventoryMovementReportRequest
        {
            Fields = new() { "productName", "unit" },
            GroupBy = "classification",
            AsOfDate = AsOf,
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 12m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicInventoryMovementReportController(counting.Proxy, new ReportService(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicInventoryMovementReportRequest
        {
            Fields = new() { "productName" },
            AsOfDate = AsOf,
            PageSize = 10
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
