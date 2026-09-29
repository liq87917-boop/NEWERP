using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-135 动态库存库龄与成本估值报表预览（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、仓库 / 商品 / 截止日期筛选、库龄分层边界与金额、
/// 未知库龄（无台账）、未知成本（金额 null 不回落为 0）、CNY 币种、稳定分页与 200 上限、
/// 无效字段 / 非法日期 / 非法仓库 / 商品 / 页大小超限、无身份（未认证）、无库存查询菜单授权（权限不足）、只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicInventoryAgingReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long WarehouseA = 920001L;
    private const long WarehouseB = 920002L;
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

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name)
    {
        var product = new BaseProduct { Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = "PCS" };
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

    private static DynamicInventoryAgingReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicInventoryAgingReportCatalogDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static DynamicInventoryAgingReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicInventoryAgingReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 字段目录 ====================

    [Fact]
    public async Task Catalog_返回有限白名单字段目录()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicInventoryAgingReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal("库存查询", catalog.RequiredMenuText);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(32, catalog.Fields.Count);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet();
        Assert.Contains("warehouseId", keys);
        Assert.Contains("productCode", keys);
        Assert.Contains("unit", keys);
        Assert.Contains("currentQuantity", keys);
        Assert.Contains("unknownAgeQuantity", keys);
        Assert.Contains("evidenceStatus", keys);
        Assert.Contains("costStatus", keys);
        Assert.Contains("costCurrency", keys);
        Assert.Contains("authoritativeAmount", keys);
        Assert.Contains("bucket0To30Quantity", keys);
        Assert.Contains("bucket0To30Amount", keys);
        Assert.Contains("bucketOver180Quantity", keys);
        Assert.Contains("bucketOver180Amount", keys);
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public async Task Catalog_无库存查询菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Catalog_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 2. 选定字段顺序与语义 ====================

    [Fact]
    public async Task Preview_按选定字段顺序投影且只含选定列()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 12m, averageCost: 10m, totalCost: 120m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 12m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productName", "warehouseId", "currentQuantity", "costCurrency" },
            AsOfDate = AsOf,
            PageSize = 20
        }));

        Assert.Equal(new[] { "productName", "warehouseId", "currentQuantity", "costCurrency" },
            page.Columns.Select(c => c.Key).ToArray());
        Assert.Single(page.Rows);
        Assert.Equal(new[] { "productName", "warehouseId", "currentQuantity", "costCurrency" },
            page.Rows[0].Keys.ToArray());
        Assert.Equal("商品一", (string)page.Rows[0]["productName"]!);
        Assert.Equal(WarehouseA, (long)page.Rows[0]["warehouseId"]!);
        Assert.Equal(12m, (decimal)page.Rows[0]["currentQuantity"]!);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, (string)page.Rows[0]["costCurrency"]!);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, page.CostCurrency);
    }


    [Fact]
    public async Task Preview_库龄分层边界与金额()
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

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
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

        var row = Assert.Single(page.Rows);
        Assert.Equal(new[] { 2m, 2m, 2m, 2m, 1m }, new[]
        {
            (decimal)row["bucket0To30Quantity"]!,
            (decimal)row["bucket31To60Quantity"]!,
            (decimal)row["bucket61To90Quantity"]!,
            (decimal)row["bucket91To180Quantity"]!,
            (decimal)row["bucketOver180Quantity"]!,
        });
        Assert.Equal(new[] { 20m, 20m, 20m, 20m, 10m }, new[]
        {
            (decimal)row["bucket0To30Amount"]!,
            (decimal)row["bucket31To60Amount"]!,
            (decimal)row["bucket61To90Amount"]!,
            (decimal)row["bucket91To180Amount"]!,
            (decimal)row["bucketOver180Amount"]!,
        });
    }

    [Fact]
    public async Task Preview_无台账_未知库龄语义()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 5m);   // 无任何台账
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "currentQuantity", "knownAgedQuantity", "unknownAgeQuantity", "evidenceStatus" },
            AsOfDate = AsOf,
            PageSize = 20
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(5m, (decimal)row["currentQuantity"]!);
        Assert.Equal(0m, (decimal)row["knownAgedQuantity"]!);
        Assert.Equal(5m, (decimal)row["unknownAgeQuantity"]!);
        Assert.Equal(InventoryAgingSemantics.EvidenceNone, (string)row["evidenceStatus"]!);
    }

    [Fact]
    public async Task Preview_无成本依据_未知成本金额为null且币种为CNY()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 5m, averageCost: 0m, totalCost: 0m);   // 无成本依据
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 5m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new()
            {
                "costStatus", "authoritativeAmount", "agedAmount", "unknownAgeAmount",
                "unknownCostQuantity", "bucket0To30Amount", "costCurrency"
            },
            AsOfDate = AsOf,
            PageSize = 20
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(InventoryAgingSemantics.CostUnknown, (string)row["costStatus"]!);
        Assert.Null(row["authoritativeAmount"]);
        Assert.Null(row["agedAmount"]);
        Assert.Null(row["unknownAgeAmount"]);
        Assert.Null(row["bucket0To30Amount"]);          // 未知成本金额不回落为 0
        Assert.Equal(5m, (decimal)row["unknownCostQuantity"]!);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, (string)row["costCurrency"]!);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, page.CostCurrency);
    }


    // ==================== 3. 筛选与分页 ====================

    [Fact]
    public async Task Preview_仓库与商品筛选()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "副仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedStock(db, WarehouseA, Product1, 1m, averageCost: 1m, totalCost: 1m);
        SeedStock(db, WarehouseA, Product2, 2m, averageCost: 1m, totalCost: 2m);
        SeedStock(db, WarehouseB, Product1, 3m, averageCost: 1m, totalCost: 3m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var byWarehouse = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "warehouseId" }, WarehouseId = WarehouseA, AsOfDate = AsOf, PageSize = 20
        }));
        Assert.Equal(2, byWarehouse.Total);
        Assert.All(byWarehouse.Rows, r => Assert.Equal(WarehouseA, (long)r["warehouseId"]!));

        var byProduct = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productId" }, ProductId = Product1, AsOfDate = AsOf, PageSize = 20
        }));
        Assert.Equal(2, byProduct.Total);
        Assert.All(byProduct.Rows, r => Assert.Equal(Product1, (long)r["productId"]!));
    }

    [Fact]
    public async Task Preview_截止日期截断_未来移动不计入()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 110m, averageCost: 1m, totalCost: 110m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(5), 1, 100m);   // 截止日期之后，不计入
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "knownAgedQuantity", "unknownAgeQuantity" },
            AsOfDate = AsOf,
            PageSize = 20
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(10m, (decimal)row["knownAgedQuantity"]!);   // 只有 10 计入库龄分层
        Assert.Equal(100m, (decimal)row["unknownAgeQuantity"]!); // 剩余 100 为流水起点之前的历史库存（未知库龄）
    }

    [Fact]
    public async Task Preview_分页稳定且单页上限200()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        for (var i = 1; i <= 250; i++)
            SeedStock(db, WarehouseA, 730000L + i, 1m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var p1 = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productId" }, Page = 1, PageSize = 200, AsOfDate = AsOf
        }));
        Assert.Equal(250, p1.Total);
        Assert.Equal(200, p1.Rows.Count);
        Assert.Equal(2, p1.TotalPages);

        var p2 = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productId" }, Page = 2, PageSize = 200, AsOfDate = AsOf
        }));
        Assert.Equal(50, p2.Rows.Count);
    }


    // ==================== 4. 无效输入（查询前拒绝） ====================

    [Fact]
    public async Task Preview_页大小超限或非法_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { PageSize = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task Preview_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { Fields = new() { "productName", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_非法日期_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { AsOfDate = new DateTime(1800, 1, 1) }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_非法仓库筛选_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { WarehouseId = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { WarehouseId = -1 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task Preview_非法商品筛选_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { ProductId = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest { ProductId = -1 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    // ==================== 5. 未授权 / 只读 ====================

    [Fact]
    public async Task Preview_无库存查询菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryAgingReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Preview_只读不写库()
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

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryAgingReportRequest
        {
            Fields = new() { "productName" }, AsOfDate = AsOf, PageSize = 10
        }));
        Assert.Equal(1, page.Total);
        Assert.Equal(0, counting.WriteCalls);
    }


    // ==================== 6. 只读计数上下文（断言不写库） ====================

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

