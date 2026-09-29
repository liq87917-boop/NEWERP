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
/// ERP-132 动态库存移动报表「授权页面按仓库 / 呆滞分类 / 台账状态的行数分布」单元测试。
/// <para>页面行数语义：<see cref="DynamicInventoryMovementReportRules.BuildGroupCounts"/> 只对「当前授权预览页」的库存行计数，
/// 绝不跨不同商品 / 基础单位求和任何数量；固定分类（classification / history）的空分类与未知历史分类始终保留（计数可为 0），
/// warehouse 为动态分组（只出现本页存在的仓库）。</para>
/// 覆盖：仓库分组（含不同基础单位只计数）、分类与未知历史、台账状态、空页、无效分组键（fail closed）、分页重算、
/// 无库存查询菜单授权（权限不足）、无身份（未认证）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicInventoryMovementGroupingTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long WarehouseA = 900001L;
    private const long WarehouseB = 900002L;
    private const long Product1 = 700001L;
    private const long Product2 = 700002L;
    private const long Product3 = 700003L;

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

    private static DynamicInventoryMovementReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicInventoryMovementReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 分组键规范化 ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("warehouse", "warehouse")]
    [InlineData("Warehouse", "warehouse")]
    [InlineData("classification", "classification")]
    [InlineData("Classification", "classification")]
    [InlineData("history", "history")]
    [InlineData("HISTORY", "history")]
    public void NormalizeGroupBy_合法取值_规范化(string? input, string expected)
    {
        Assert.Equal(expected, DynamicInventoryMovementReportRules.NormalizeGroupBy(input));
    }

    [Theory]
    [InlineData("supplier")]
    [InlineData("quarter")]
    [InlineData("未知")]
    [InlineData("warehouses")]
    public void NormalizeGroupBy_无效取值_拒绝(string input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicInventoryMovementReportRules.NormalizeGroupBy(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 仓库分组（不同基础单位只计数，绝不求和数量） ====================

    [Fact]
    public async Task GroupBy_仓库_只计数不求和不同单位数量()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "副仓");
        SeedProduct(db, Product1, "P001", "件装商品", "PCS");
        SeedProduct(db, Product2, "P002", "称重商品", "KG");
        SeedStock(db, WarehouseA, Product1, 10m);
        SeedStock(db, WarehouseA, Product2, 5m);
        SeedStock(db, WarehouseB, Product1, 7m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        {
            GroupBy = "warehouse",
            AsOfDate = AsOf,
            PageSize = 50
        }));

        Assert.Equal("warehouse", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(2, page.Groups.Count);

        var main = Assert.Single(page.Groups, g => g.Key == $"warehouse:{WarehouseA}");
        Assert.Equal("主仓", main.Label);
        Assert.Equal(2, main.Count); // PCS 10 + KG 5 只计 2 行，绝不求和为 15

        var secondary = Assert.Single(page.Groups, g => g.Key == $"warehouse:{WarehouseB}");
        Assert.Equal("副仓", secondary.Label);
        Assert.Equal(1, secondary.Count);
    }

    // ==================== 3. 分类与台账状态分组 ====================

    [Fact]
    public async Task GroupBy_分类_覆盖正常流动呆滞与未知历史()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "副仓");
        SeedProduct(db, Product1, "P001", "正常流动商品");
        SeedProduct(db, Product2, "P002", "呆滞商品");
        SeedProduct(db, Product3, "P003", "无台账商品");

        SeedStock(db, WarehouseA, Product1, 10m);
        SeedStock(db, WarehouseA, Product2, 5m);
        SeedStock(db, WarehouseB, Product3, 3m);

        // 窗口内移动 → 正常流动；90 天前（窗口外）移动 → 呆滞；无移动 → 未知
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);
        SeedMovement(db, WarehouseA, Product2, AsOf.AddDays(-100), 1, 5m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        {
            GroupBy = "classification",
            AsOfDate = AsOf,
            InactiveDays = 90,
            PageSize = 50
        }));

        Assert.Equal("classification", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(3, page.Groups.Count);

        Assert.Equal(1, page.Groups.Single(g => g.Key == "classification:active").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "classification:stagnant").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "classification:unknown").Count);
        Assert.Equal("正常流动", page.Groups.Single(g => g.Key == "classification:active").Label);
        Assert.Equal("无法判定", page.Groups.Single(g => g.Key == "classification:unknown").Label);
    }

    [Fact]
    public async Task GroupBy_台账状态_覆盖有台账与无台账历史()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "副仓");
        SeedProduct(db, Product1, "P001", "窗口内移动商品");
        SeedProduct(db, Product2, "P002", "窗口外移动商品");
        SeedProduct(db, Product3, "P003", "无台账商品");

        SeedStock(db, WarehouseA, Product1, 10m);
        SeedStock(db, WarehouseA, Product2, 5m);
        SeedStock(db, WarehouseB, Product3, 3m);

        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), 1, 10m);   // 窗口内 → ledger
        SeedMovement(db, WarehouseA, Product2, AsOf.AddDays(-100), 1, 5m);   // 窗口外 → window_empty
        // Product3 无流水 → no_history

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        {
            GroupBy = "history",
            AsOfDate = AsOf,
            InactiveDays = 90,
            PageSize = 50
        }));

        Assert.Equal("history", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(3, page.Groups.Count);

        Assert.Equal(1, page.Groups.Single(g => g.Key == "history:ledger").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "history:window_empty").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "history:no_history").Count);
        Assert.Equal("无台账（历史库存 · 未知）", page.Groups.Single(g => g.Key == "history:no_history").Label);
    }

    // ==================== 4. 分页重算（仅当前页） ====================

    [Fact]
    public async Task GroupBy_仓库_分页变化仅统计当前页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedProduct(db, Product3, "P003", "商品三");
        SeedStock(db, WarehouseA, Product1, 10m);
        SeedStock(db, WarehouseA, Product2, 5m);
        SeedStock(db, WarehouseA, Product3, 3m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        { GroupBy = "warehouse", AsOfDate = AsOf, PageSize = 2, Page = 1 }));
        var page2 = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        { GroupBy = "warehouse", AsOfDate = AsOf, PageSize = 2, Page = 2 }));

        var g1 = Assert.Single(page1.Groups!);
        var g2 = Assert.Single(page2.Groups!);
        Assert.Equal(2, g1.Count); // 第 1 页两行
        Assert.Equal(1, g2.Count); // 第 2 页一行（非全量合计）
        Assert.Equal(3, page1.Total);
    }

    // ==================== 5. 空页 ====================

    [Fact]
    public async Task GroupBy_仓库_空页_分组为空()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        { GroupBy = "warehouse", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal(0, page.Total);
        Assert.Equal("warehouse", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
    }

    [Fact]
    public async Task GroupBy_固定分类_空页_保留零计数()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var classPage = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        { GroupBy = "classification", AsOfDate = AsOf, PageSize = 50 }));
        Assert.Equal(3, classPage.Groups!.Count);
        Assert.All(classPage.Groups, g => Assert.Equal(0, g.Count));

        var historyPage = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        { GroupBy = "history", AsOfDate = AsOf, PageSize = 50 }));
        Assert.Equal(3, historyPage.Groups!.Count);
        Assert.All(historyPage.Groups, g => Assert.Equal(0, g.Count));
    }

    // ==================== 6. 无效分组键（控制器入口 fail closed） ====================

    [Fact]
    public async Task Preview_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryMovementReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 权限与只读 ====================

    [Fact]
    public async Task GroupBy_无库存查询菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryMovementReportRequest { GroupBy = "warehouse" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task GroupBy_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicInventoryMovementReportRequest { GroupBy = "warehouse" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task GroupBy_只读不写库()
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

        var page = PreviewOk(await ctl.Preview(new DynamicInventoryMovementReportRequest
        { GroupBy = "warehouse", AsOfDate = AsOf, PageSize = 10 }));

        Assert.Equal(1, page.Total);
        Assert.NotEmpty(page.Groups!);
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
