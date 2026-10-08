using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 库存查询测试身份 / 库存夹具（ERP-356）：为直接实例化 <see cref="StockController"/> 的单元测试
/// 注入**真实 HTTP 身份**并播种既有「库存查询」菜单、特权 / 受限角色与库存行 / 流水。
/// <para>不新增权限模型、不绕过鉴权：拒绝场景一律不播种菜单、播种禁用 / 已删除账号，
/// 或播种未映射为业务员的受限账号；库存数据一律显式播种，便于断言「拒绝时不泄露任何计数」。</para>
/// </summary>
public static class StockQueryTestAuthorization
{
    public const long WarehouseA = 930001L;
    public const long WarehouseB = 930002L;
    public const long Product1 = 770001L;

    /// <summary>播种既有 stock-query 菜单（幂等），供角色 → 菜单授权复用。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == StockQueryAuthorizationRules.RequiredMenuCode);
        if (existing is not null)
            return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = StockQueryAuthorizationRules.RequiredMenuCode,
            MenuName = StockQueryAuthorizationRules.RequiredMenuText,
            Path = "/logistics/stock",
            Icon = "Boxes",
            SortOrder = 0,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    public static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name)
    {
        var warehouse = new BaseWarehouse
        {
            Id = id,
            WarehouseCode = $"WH-{id}",
            WarehouseName = name,
            Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        return warehouse;
    }

    public static BaseProduct SeedProduct(ErpDbContext db, long id, string code)
    {
        var product = new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = $"商品{code}",
            Spec = "规格A",
            Unit = "PCS",
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    public static Stock SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
    {
        var stock = new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            LockedQuantity = 0m,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        };
        db.Stocks.Add(stock);
        db.SaveChanges();
        return stock;
    }

    public static StockMovement SeedMovement(ErpDbContext db, long warehouseId, long productId,
        string sourceDocNo, string sourceDocType = "StockAdjustment")
    {
        var movement = new StockMovement
        {
            MovementDate = DateTime.Today,
            MovementType = InventoryMovementType.Adjustment,
            SourceDocType = sourceDocType,
            SourceDocId = 1L,
            SourceDocNo = sourceDocNo,
            WarehouseId = warehouseId,
            WarehouseName = $"仓{warehouseId}",
            ProductId = productId,
            ProductCode = $"P{productId}",
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Direction = 1,
            Quantity = 5m,
            UnitCost = 10m,
            Amount = 50m,
            BalanceQuantity = 5m,
            BalanceAmount = 50m,
            BalanceAverageCost = 10m
        };
        db.StockMovements.Add(movement);
        db.SaveChanges();
        return movement;
    }

    /// <summary>播种特权登录用户（系统内置角色 <c>IsSystem == true</c>）并授予既有 stock-query 菜单。</summary>
    public static long SeedPrivilegedReader(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var user = SeedUser(db, enabled: true, deleted: false);
        var role = new SysRole { RoleCode = "SuperAdmin", RoleName = "测试超级管理员", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// 播种受限登录用户（非系统内置角色、未映射为业务员）：可选择是否授予既有 stock-query 菜单。
    /// 受限账号在库存维度没有权威数据范围，因此无论是否具备菜单授权都必须 fail closed。
    /// </summary>
    public static long SeedRestrictedReader(ErpDbContext db, bool withMenu)
    {
        var user = SeedUser(db, enabled: true, deleted: false);
        var role = new SysRole
        {
            RoleCode = $"Restricted-{Guid.NewGuid():N}",
            RoleName = "受限库存查询员",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withMenu)
        {
            var menu = SeedMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return user.Id;
    }

    public static long SeedDisabledUser(ErpDbContext db, bool withMenu)
    {
        var user = SeedUser(db, enabled: false, deleted: false);
        var role = new SysRole
        {
            RoleCode = $"Disabled-{Guid.NewGuid():N}",
            RoleName = "禁用库存查询员",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withMenu)
        {
            var menu = SeedMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return user.Id;
    }

    public static long SeedDeletedUser(ErpDbContext db, bool withMenu)
    {
        var user = SeedUser(db, enabled: true, deleted: true);
        var role = new SysRole
        {
            RoleCode = $"Deleted-{Guid.NewGuid():N}",
            RoleName = "已删除库存查询员",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withMenu)
        {
            var menu = SeedMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return user.Id;
    }

    private static SysUser SeedUser(ErpDbContext db, bool enabled, bool deleted)
    {
        var user = new SysUser
        {
            UserName = $"stock-query-{Guid.NewGuid():N}",
            DisplayName = "库存查询测试账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    /// <summary>撤销当前全部「角色 → 菜单」授权（模拟请求之间回收权限）。</summary>
    public static void RevokeMenuGrants(ErpDbContext db)
    {
        foreach (var grant in db.SysRoleMenus.ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>创建已注入特权身份的 StockController。</summary>
    public static StockController Create(ErpDbContext db)
        => ForUser(db, SeedPrivilegedReader(db));

    /// <summary>把指定登录用户 Id（可空 = 无身份）写入控制器 HttpContext。</summary>
    public static StockController ForUser(ErpDbContext db, long? userId)
    {
        var controller = new StockController(db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }
}

/// <summary>
/// ERP-356 库存查询实时授权 / 数据范围与「无泄露」单元测试（内存库，不连接 SQL Server、不触碰业务库）。
/// <para>覆盖：身份缺失、禁用、已删除、无菜单、受限身份（无权威数据范围）一律 fail closed；
/// 特权库存查询用户的列表 / 流水证据 / 汇总放行并保留既有数量与成本语义；请求筛选只能收窄；
/// 撤销授权后下一次请求立即收敛；拒绝读取不改变库存 / 流水。</para>
/// </summary>
public class StockQueryAuthorizationTests
{
    private const long WarehouseA = StockQueryTestAuthorization.WarehouseA;
    private const long WarehouseB = StockQueryTestAuthorization.WarehouseB;
    private const long Product1 = StockQueryTestAuthorization.Product1;

    /// <summary>播种两个仓库的库存行与一条可审计库存流水（来源单据证据）。</summary>
    private static void SeedInventory(ErpDbContext db)
    {
        StockQueryTestAuthorization.SeedWarehouse(db, WarehouseA, "一号仓");
        StockQueryTestAuthorization.SeedWarehouse(db, WarehouseB, "二号仓");
        StockQueryTestAuthorization.SeedProduct(db, Product1, "P-001");
        StockQueryTestAuthorization.SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        StockQueryTestAuthorization.SeedStock(db, WarehouseB, Product1, quantity: 4m, totalCost: 60m);
        StockQueryTestAuthorization.SeedMovement(db, WarehouseA, Product1, "PD-AUT-0001");
    }

    private static PageQuery Page() => new() { Page = 1, PageSize = 20 };

    // ==================== 1. 拒绝矩阵（先于任何计数 / 成本 / 来源单据读取） ====================

    [Fact]
    public async Task 缺失身份_列表_流水_汇总一律未认证且不返回数据()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var ctl = StockQueryTestAuthorization.ForUser(db, null);

        Assert.Equal(ErrorCodes.Unauthorized,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(Page(), null))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(Page(), null, null, null, null))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSummary())).Code);
    }

    [Theory]
    [InlineData("disabled", ErrorCodes.Forbidden)]
    [InlineData("deleted", ErrorCodes.Unauthorized)]
    [InlineData("no-menu", ErrorCodes.Forbidden)]
    [InlineData("restricted", ErrorCodes.Forbidden)]
    public async Task 非授权身份_三个只读端点都先于任何计数失败(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);

        var userId = scenario switch
        {
            "disabled" => StockQueryTestAuthorization.SeedDisabledUser(db, withMenu: true),
            "deleted" => StockQueryTestAuthorization.SeedDeletedUser(db, withMenu: true),
            "no-menu" => StockQueryTestAuthorization.SeedRestrictedReader(db, withMenu: false),
            _ => StockQueryTestAuthorization.SeedRestrictedReader(db, withMenu: true)
        };
        var ctl = StockQueryTestAuthorization.ForUser(db, userId);

        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(Page(), null))).Code);
        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(Page(), null, null, null, null))).Code);
        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSummary())).Code);
    }

    [Fact]
    public async Task 未映射受限身份_即使有库存查询菜单也拒绝读取全局库存()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        // 具备既有 stock-query 菜单，但不是特权账号且未映射为业务员 → 无权威仓库级数据范围。
        var userId = StockQueryTestAuthorization.SeedRestrictedReader(db, withMenu: true);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => StockQueryAuthorizationRules.EnsureAuthorizedAsync(db, userId));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Contains("权威", error.Message);
    }

    [Fact]
    public async Task 请求仓库筛选不能为受限账号扩大范围()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var userId = StockQueryTestAuthorization.SeedRestrictedReader(db, withMenu: true);
        var ctl = StockQueryTestAuthorization.ForUser(db, userId);

        // 显式指定单一仓库仍是「无权威范围」的全局库存读取 → 同样拒绝。
        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(Page(), WarehouseA))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(Page(), WarehouseA, null, null, null))).Code);
    }

    [Fact]
    public async Task 撤销菜单授权后_下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var ctl = StockQueryTestAuthorization.Create(db);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(Page(), null));

        StockQueryTestAuthorization.RevokeMenuGrants(db);

        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(Page(), null))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSummary())).Code);
    }

    // ==================== 2. 特权库存查询用户放行（保留既有契约与语义） ====================

    [Fact]
    public async Task 特权库存查询用户_列表保留数量与成本语义()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var ctl = StockQueryTestAuthorization.Create(db);

        var response = Assert.IsType<ApiResponse<PagedResult<StockView>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(Page(), null)).Value);

        Assert.Equal(2, response.Data!.Total);
        var row = Assert.Single(response.Data.Items, i => i.WarehouseId == WarehouseA);
        Assert.Equal("一号仓", row.WarehouseName);
        Assert.Equal("P-001", row.ProductCode);
        Assert.Equal(10m, row.Quantity);
        Assert.Equal(10m, row.AvailableQuantity);
        Assert.Equal(10m, row.AverageCost);      // 100 / 10
        Assert.Equal(100m, row.TotalCost);
    }

    [Fact]
    public async Task 特权库存查询用户_流水证据保留来源单据审计字段()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var ctl = StockQueryTestAuthorization.Create(db);

        var response = Assert.IsType<ApiResponse<PagedResult<StockMovement>>>(
            Assert.IsType<OkObjectResult>(
                await ctl.GetMovements(Page(), null, null, "PD-AUT-0001", null)).Value);

        var movement = Assert.Single(response.Data!.Items);
        Assert.Equal("PD-AUT-0001", movement.SourceDocNo);
        Assert.Equal("StockAdjustment", movement.SourceDocType);
        Assert.Equal(InventoryMovementType.Adjustment, movement.MovementType);
    }

    [Fact]
    public async Task 特权库存查询用户_汇总返回既有聚合字段()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var ctl = StockQueryTestAuthorization.Create(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetSummary());
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        var type = data.GetType();
        Assert.Equal(14m, (decimal)type.GetProperty("totalQuantity")!.GetValue(data)!);
        Assert.Equal(14m, (decimal)type.GetProperty("totalAvailable")!.GetValue(data)!);
        Assert.Equal(2, (int)type.GetProperty("warehouseCount")!.GetValue(data)!);
    }

    [Fact]
    public async Task 特权库存查询用户_仓库筛选只能收窄不能扩大()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var ctl = StockQueryTestAuthorization.Create(db);

        var all = Assert.IsType<ApiResponse<PagedResult<StockView>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(Page(), null)).Value);
        var filtered = Assert.IsType<ApiResponse<PagedResult<StockView>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(Page(), WarehouseB)).Value);

        Assert.Equal(2, all.Data!.Total);
        Assert.Equal(1, filtered.Data!.Total);
        Assert.Equal(WarehouseB, Assert.Single(filtered.Data.Items).WarehouseId);
    }

    // ==================== 3. 拒绝读取无副作用 ====================

    [Fact]
    public async Task 拒绝读取_库存与流水不发生变化()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var userId = StockQueryTestAuthorization.SeedRestrictedReader(db, withMenu: true);
        var ctl = StockQueryTestAuthorization.ForUser(db, userId);

        var stocksBefore = db.Stocks.Count();
        var movementsBefore = db.StockMovements.Count();
        var quantityBefore = db.Stocks.Sum(s => s.Quantity);

        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(Page(), null));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(Page(), null, null, null, null));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSummary());

        Assert.Equal(stocksBefore, db.Stocks.Count());
        Assert.Equal(movementsBefore, db.StockMovements.Count());
        Assert.Equal(quantityBefore, db.Stocks.Sum(s => s.Quantity));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 4. 规则层直测 ====================

    [Fact]
    public async Task 规则_上下文为空_抛参数异常()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => StockQueryAuthorizationRules.EnsureAuthorizedAsync(null!, 1L));
    }

    [Fact]
    public void 规则_受限范围应用到库存查询一律拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var restricted = new StockQueryAuthorizationRules.StockQueryScope
        {
            UserId = 42L,
            UserName = "restricted",
            IsPrivileged = false
        };

        Assert.False(restricted.AllowsGlobalStock);
        Assert.Equal(ErrorCodes.Forbidden, Assert.Throws<BusinessException>(
            () => StockQueryAuthorizationRules.ApplyScope(db.Stocks, restricted)).Code);
        Assert.Equal(ErrorCodes.Forbidden, Assert.Throws<BusinessException>(
            () => StockQueryAuthorizationRules.ApplyScope(db.StockMovements, restricted)).Code);
    }

    [Fact]
    public async Task 规则_特权范围原样返回库存查询()
    {
        using var db = TestDbFactory.Create();
        SeedInventory(db);
        var privileged = new StockQueryAuthorizationRules.StockQueryScope
        {
            UserId = 1L,
            UserName = "admin",
            IsPrivileged = true
        };

        Assert.True(privileged.AllowsGlobalStock);
        Assert.Equal(db.Stocks.Count(),
            StockQueryAuthorizationRules.ApplyScope(db.Stocks, privileged).Count());
        Assert.Equal(db.StockMovements.Count(),
            StockQueryAuthorizationRules.ApplyScope(db.StockMovements, privileged).Count());

        var resolved = await StockQueryAuthorizationRules.EnsureAuthorizedAsync(
            db, StockQueryTestAuthorization.SeedPrivilegedReader(db));
        Assert.True(resolved.IsPrivileged);
        Assert.True(resolved.AllowsGlobalStock);
    }
}
