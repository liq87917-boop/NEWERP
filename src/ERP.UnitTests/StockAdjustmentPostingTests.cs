using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 库存盘点测试身份 / 主数据脚手架（ERP-355）：为直接实例化 <see cref="StockAdjustmentController"/> 的单元测试
/// 注入**真实 HTTP 身份**，并播种既有「库存查询」（<c>stock-query</c>）菜单授权与在用仓库 / 商品。
/// 不新增权限模型、不绕过鉴权：拒绝场景一律不播种菜单或播种禁用账号。
/// </summary>
internal static class StockAdjustmentTestAuthorization
{
    /// <summary>播种既有 stock-query 菜单（幂等），供角色 → 菜单授权复用。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == "stock-query" && !m.IsDeleted);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = "stock-query",
            MenuName = "库存查询",
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

    /// <summary>播种一个已授权仓库操作员（既有菜单 + 启用账号）并返回其用户 Id。</summary>
    public static long SeedAuthorizedOperator(ErpDbContext db, bool enabled = true)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = $"PD-{Guid.NewGuid():N}", RoleName = "盘点操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"pd-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "盘点操作员",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种一个已启用但**没有**任何菜单授权的账号（fail closed 场景）。</summary>
    public static long SeedUnauthorizedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"pd-no-menu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>创建已注入授权身份的 StockAdjustmentController。</summary>
    public static StockAdjustmentController CreateAuthorized(ErpDbContext db)
        => ForUser(db, SeedAuthorizedOperator(db));

    /// <summary>把指定登录用户 Id（可空 = 无身份）写入控制器 HttpContext。</summary>
    public static StockAdjustmentController ForUser(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>()))
                }
            }
        };

    /// <summary>播种一张盘点单所需的在用主数据（仓库 + 商品，幂等）。</summary>
    public static void SeedMasterData(ErpDbContext db, long warehouseId, params long[] productIds)
    {
        SeedWarehouse(db, warehouseId);
        foreach (var productId in productIds) SeedProduct(db, productId);
    }

    /// <summary>播种启用 / 停用仓库（幂等；显式 Id 便于测试直接引用）。</summary>
    public static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, bool enabled = true)
    {
        var warehouse = db.BaseWarehouses.FirstOrDefault(w => w.Id == id && !w.IsDeleted);
        if (warehouse is not null) return warehouse;

        warehouse = new BaseWarehouse
        {
            Id = id,
            WarehouseCode = $"PDW-{id}",
            WarehouseName = $"盘点仓-{id}",
            Status = enabled ? 1 : 0
        };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        return warehouse;
    }

    /// <summary>播种启用 / 停用商品（幂等；显式 Id 便于测试直接引用）。</summary>
    public static BaseProduct SeedProduct(ErpDbContext db, long id, bool enabled = true)
    {
        var product = db.BaseProducts.FirstOrDefault(p => p.Id == id && !p.IsDeleted);
        if (product is not null) return product;

        product = new BaseProduct
        {
            Id = id,
            ProductCode = $"PDP-{id}",
            ProductName = $"盘点商品-{id}",
            Spec = "规格A",
            Unit = "PCS",
            Status = enabled ? 1 : 0
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }
}

/// <summary>
/// ERP-355 库存盘点单过账 / 冲销原子化单元测试（内存库 + 真实 HTTP 身份）：
/// 1) 授权操作员按**已验证的账面数量基线**审核恰好一次（数量 / 成本语义不变）；
/// 2) 账面数量与权威当前库存不一致（基线过期）→ 拒绝过账且不静默更正，需在待提交状态重新盘点 / 修改；
/// 3) 非负数量 / 成本、商品不重复、仓库与商品在用等校验先于任何库存写入；
/// 4) 无身份 / 无菜单 / 禁用账号 fail closed，创建不消耗单号；
/// 5) 销审按既有红字流水冲销（重复销审拒绝、被占用时全部不变）、已审核不可取消 / 删除；
/// 6) 确定性库存行锁定顺序与控制器 / 规则源代码契约。
/// 说明：全部使用内存数据库，不连接 SQL Server、不触碰任何业务库数据。
/// </summary>
public class StockAdjustmentPostingTests
{
    private const long WarehouseA = 920001L;
    private const long WarehouseB = 920002L;
    private const long Product1 = 820001L;
    private const long Product2 = 820002L;

    // ==================== 1. 已验证账面基线 + 恰好一次 ====================

    [Fact]
    public async Task 授权盘点操作员_按已验证账面基线审核_恰好一次()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);      // 均价 10
        var ctl = NewController(db);
        var id = await CreateAndSubmitAsync(ctl, (Product1, "P1", 10m, 7m, 10m));

        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        Assert.Equal(7m, stock.Quantity);                                        // 账面 10 == 当前库存 10 → 差异 -3
        Assert.Equal(70m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Equal(1, db.StockMovements.Count());

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(1, db.StockMovements.Count());                              // 不重复过账
        Assert.Equal(7m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
    }

    [Fact]
    public async Task 账面基线过期_审核拒绝且不静默更正()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateAndSubmitAsync(ctl, (Product1, "P1", 8m, 7m, 10m));  // 账面 8 ≠ 权威库存 10

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("账面", ex.Message);

        Assert.Equal(10m, db.Stocks.Single().Quantity);                          // 库存不变
        Assert.Equal(100m, db.Stocks.Single().TotalCost);
        Assert.Empty(db.StockMovements);                                         // 不产生虚构差异流水
        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single().Status);
        Assert.Equal(8m, db.StockAdjustmentDetails.Single().BookQuantity);       // 不自动改写账面数量
    }

    [Fact]
    public async Task 待提交状态修改账面数量后重新提交可审核()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateAsync(ctl, (Product1, "P1", 8m, 7m, 10m));          // 待提交（尚未提交）

        // 显式重新盘点 / 修改：账面改回权威库存 10
        var update = await ctl.Update(id, new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Remark = "ERP-355_TEST",
            Details = new List<StockAdjustmentDetail>
            {
                new()
                {
                    ProductId = Product1, ProductName = "P1", Spec = "规格A", Unit = "PCS",
                    BookQuantity = 10m, ActualQuantity = 7m, UnitCost = 10m
                }
            }
        });
        Assert.IsType<OkObjectResult>(update);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        Assert.Equal(7m, stock.Quantity);
        Assert.Equal(70m, stock.TotalCost);
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Single(db.StockMovements);
    }

    // ==================== 2. 明细与主数据校验（先于任何库存写入） ====================

    [Theory]
    [InlineData(-1, 5, 1)]      // 账面数量为负
    [InlineData(5, -1, 1)]      // 实盘数量为负
    [InlineData(5, 5, -1)]      // 成本单价为负
    public async Task 非法数量或成本_创建即拒绝(decimal book, decimal actual, decimal unitCost)
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = book, ActualQuantity = actual, UnitCost = unitCost }
            }
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.StockAdjustments);
        Assert.Empty(db.SysDocumentNumberRules);
    }

    [Fact]
    public async Task 重复商品明细_创建即拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = 1m, ActualQuantity = 2m, UnitCost = 1m },
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = 3m, ActualQuantity = 4m, UnitCost = 1m }
            }
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.StockAdjustments);
    }

    [Fact]
    public async Task 缺少商品_创建即拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductName = "无商品Id", BookQuantity = 1m, ActualQuantity = 2m, UnitCost = 1m }
            }
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.StockAdjustments);
    }

    [Fact]
    public async Task 商品不存在_创建即拒绝()
    {
        using var db = TestDbFactory.Create();
        StockAdjustmentTestAuthorization.SeedWarehouse(db, WarehouseA);
        var ctl = StockAdjustmentTestAuthorization.CreateAuthorized(db);   // 不播种任何商品

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = 1m, ActualQuantity = 2m, UnitCost = 1m }
            }
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.StockAdjustments);
    }

    [Theory]
    [InlineData("warehouse-disabled")]
    [InlineData("product-disabled")]
    public async Task 主数据停用_审核拒绝且库存不变(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var ctl = NewController(db);
        var id = await CreateAndSubmitAsync(ctl, (Product1, "P1", 5m, 4m, 10m));

        // 提交后主数据停用：审核在锁内复查权威主数据，必须 fail closed
        if (scenario == "warehouse-disabled")
            db.BaseWarehouses.Single(w => w.Id == WarehouseA).Status = 0;
        else
            db.BaseProducts.Single(p => p.Id == Product1).Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(5m, db.Stocks.Single().Quantity);
        Assert.Equal(50m, db.Stocks.Single().TotalCost);
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single().Status);
    }

    [Fact]
    public async Task 缺仓库_创建即拒绝()
    {
        using var db = TestDbFactory.Create();
        StockAdjustmentTestAuthorization.SeedProduct(db, Product1);
        var ctl = StockAdjustmentTestAuthorization.CreateAuthorized(db);   // 不播种仓库

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseB,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = 1m, ActualQuantity = 2m, UnitCost = 1m }
            }
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.StockAdjustments);
    }

    // ==================== 3. 身份 / 授权 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-menu")]
    [InlineData("disabled")]
    public async Task 未认证或未授权_审核拒绝且不落库存(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var authorized = NewController(db);
        var id = await CreateAndSubmitAsync(authorized, (Product1, "P1", 10m, 7m, 10m));

        long? userId = scenario switch
        {
            "no-menu" => StockAdjustmentTestAuthorization.SeedUnauthorizedUser(db),
            "disabled" => StockAdjustmentTestAuthorization.SeedAuthorizedOperator(db, enabled: false),
            _ => null
        };
        var denied = StockAdjustmentTestAuthorization.ForUser(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => denied.Approve(id));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);

        Assert.Equal(10m, db.Stocks.Single().Quantity);
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single().Status);
    }

    [Fact]
    public async Task 无身份_提交取消删除一律拒绝且不改状态()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var authorized = NewController(db);
        var id = await CreateAndSubmitAsync(authorized, (Product1, "P1", 10m, 7m, 10m));

        var denied = StockAdjustmentTestAuthorization.ForUser(db, null);

        Assert.Equal(ErrorCodes.Unauthorized,
            (await Assert.ThrowsAsync<BusinessException>(() => denied.Submit(id))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            (await Assert.ThrowsAsync<BusinessException>(() => denied.Cancel(id))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            (await Assert.ThrowsAsync<BusinessException>(() => denied.Delete(id))).Code);

        var doc = db.StockAdjustments.Single();
        Assert.Equal(DocumentStatus.Submitted, doc.Status);
        Assert.False(doc.IsDeleted);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 无身份创建_拒绝且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        StockAdjustmentTestAuthorization.SeedMasterData(db, WarehouseA, Product1);
        var ctl = StockAdjustmentTestAuthorization.ForUser(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Details = new List<StockAdjustmentDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", BookQuantity = 1m, ActualQuantity = 2m, UnitCost = 1m }
            }
        }));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.StockAdjustments);
        Assert.Empty(db.SysDocumentNumberRules);
    }

    // ==================== 4. 销审 / 取消 / 删除 ====================

    [Fact]
    public async Task 销审_红字冲销还原_重复销审拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateAndSubmitAsync(ctl, (Product1, "P1", 10m, 12m, 10m));
        await ctl.Approve(id);
        Assert.Equal(12m, db.Stocks.Single().Quantity);

        await ctl.Unaudit(id);

        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);
        Assert.Equal(DocumentStatus.Pending, db.StockAdjustments.Single().Status);

        // 原流水标记为已冲销并追加红字流水（不删除历史、可审计）
        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].IsReversed);
        Assert.True(movements[1].IsReversal);
        Assert.Equal(-1, movements[1].Direction);
        Assert.Equal(movements[0].Id, movements[1].ReversalOfMovementId);

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(2, db.StockMovements.Count());
    }

    [Fact]
    public async Task 销审_货物已被后续占用_拒绝且全部不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 0m, totalCost: 0m);
        var ctl = NewController(db);
        var id = await CreateAndSubmitAsync(ctl, (Product1, "P1", 0m, 5m, 10m));
        await ctl.Approve(id);
        Assert.Equal(5m, db.Stocks.Single().Quantity);

        // 模拟后续业务把盘盈货物领走（只剩 1）
        var stock = db.Stocks.Single();
        stock.Quantity = 1m;
        stock.TotalCost = 10m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 冲销失败整体回滚：余额 / 流水 / 状态全部不变
        Assert.Equal(1m, db.Stocks.Single().Quantity);
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single().Status);
        Assert.False(db.StockMovements.Single().IsReversed);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task 已审核不可取消删除_待提交可取消删除()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 5m, totalCost: 50m);
        var ctl = NewController(db);
        var approvedId = await CreateAndSubmitAsync(ctl, (Product1, "P1", 5m, 4m, 10m));
        await ctl.Approve(approvedId);

        var cancelEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(approvedId));
        Assert.Equal(ErrorCodes.RuleConflict, cancelEx.Code);
        var deleteEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(approvedId));
        Assert.Equal(ErrorCodes.RuleConflict, deleteEx.Code);
        Assert.False(db.StockAdjustments.Single(a => a.Id == approvedId).IsDeleted);

        var pendingForCancel = await CreateAsync(ctl, (Product1, "P1", 0m, 0m, 1m));
        await ctl.Cancel(pendingForCancel);
        Assert.Equal(DocumentStatus.Cancelled, db.StockAdjustments.Single(a => a.Id == pendingForCancel).Status);

        var pendingForDelete = await CreateAsync(ctl, (Product1, "P1", 0m, 0m, 1m));
        await ctl.Delete(pendingForDelete);
        Assert.True(db.StockAdjustments.Single(a => a.Id == pendingForDelete).IsDeleted);
    }

    // ==================== 5. 确定性锁定顺序 ====================

    [Fact]
    public void 库存行锁定顺序_去重且仓库商品升序()
    {
        var ordered = InventoryService.OrderStockIdentities(new[]
        {
            new StockIdentity(9, 2), new StockIdentity(3, 5), new StockIdentity(9, 1),
            new StockIdentity(3, 1), new StockIdentity(9, 2), new StockIdentity(0, 7)
        });

        Assert.Equal(new[] { (3L, 1L), (3L, 5L), (9L, 1L), (9L, 2L) },
            ordered.Select(k => (k.WarehouseId, k.ProductId)).ToArray());

        var details = new List<StockAdjustmentDetail>
        {
            new() { ProductId = 2 }, new() { ProductId = 1 }, new() { ProductId = 2 }
        };
        var keys = InventoryService.OrderStockIdentities(StockAdjustmentPostingRules.StockKeys(7, details));

        Assert.Equal(new[] { (7L, 1L), (7L, 2L) },                  // 同一仓库 + 去重升序
            keys.Select(k => (k.WarehouseId, k.ProductId)).ToArray());
    }

    // ==================== 6. 控制器 / 规则源代码契约 ====================

    [Fact]
    public void 控制器与规则_遵循过账契约()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockAdjustmentController.cs");
        Assert.Contains("StockAdjustmentPostingRules.EnsureMenuAuthorizedAsync", controller);
        Assert.Contains("StockAdjustments WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("Stocks WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("InventoryService.OrderStockIdentities", controller);
        Assert.Contains("StockAdjustmentPostingRules.StockKeys", controller);
        Assert.Contains("StockAdjustmentPostingRules.EnsureLineShape", controller);
        Assert.Contains("StockAdjustmentPostingRules.EnsureLiveMasterDataAsync", controller);
        Assert.Contains("EnsureFreshBookQuantity", controller);
        Assert.Contains("DiscardTrackedChanges", controller);

        var approveStart = controller.IndexOf("public override async Task<IActionResult> Approve", StringComparison.Ordinal);
        Assert.True(approveStart >= 0);
        var approve = controller[approveStart..];
        var lockRow = approve.IndexOf("LockAdjustmentRowAsync", StringComparison.Ordinal);
        var movements = approve.IndexOf("CountActiveMovementsAsync", StringComparison.Ordinal);
        var fresh = approve.IndexOf("EnsureFreshBookQuantity", StringComparison.Ordinal);
        var increase = approve.IndexOf("IncreaseAsync(context", StringComparison.Ordinal);
        var decrease = approve.IndexOf("DecreaseAsync(context", StringComparison.Ordinal);
        Assert.True(lockRow >= 0 && movements > lockRow && fresh > movements);
        Assert.True(increase > fresh && decrease > fresh);           // 账面基线核验先于任何库存写入

        var unauditStart = controller.IndexOf("public async Task<IActionResult> Unaudit", StringComparison.Ordinal);
        Assert.True(unauditStart >= 0);
        var unaudit = controller[unauditStart..];
        Assert.True(unaudit.IndexOf("LockAdjustmentRowAsync", StringComparison.Ordinal)
                    < unaudit.IndexOf("ReverseAsync", StringComparison.Ordinal));

        var rules = ReadSource("ERP.Application/Services/StockAdjustmentPostingRules.cs");
        Assert.DoesNotContain("SaveChanges", rules);                 // 纯判定 + 只读查询，不落库
        Assert.Contains("AsNoTracking", rules);
        Assert.Equal("stock-query", StockAdjustmentPostingRules.RequiredMenuCode);
    }

    // ==================== 测试辅助 ====================

    /// <summary>播种在用主数据（WarehouseA + Product1/2）并返回已授权控制器。</summary>
    private static StockAdjustmentController NewController(ErpDbContext db)
    {
        StockAdjustmentTestAuthorization.SeedMasterData(db, WarehouseA, Product1, Product2);
        return StockAdjustmentTestAuthorization.CreateAuthorized(db);
    }

    private static async Task<long> CreateAsync(StockAdjustmentController ctl,
        params (long ProductId, string Name, decimal BookQuantity, decimal ActualQuantity, decimal UnitCost)[] lines)
    {
        var result = await ctl.Create(new StockAdjustment
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Remark = "ERP-355_TEST",
            Details = lines.Select(l => new StockAdjustmentDetail
            {
                ProductId = l.ProductId,
                ProductName = l.Name,
                Spec = "规格A",
                Unit = "PCS",
                BookQuantity = l.BookQuantity,
                ActualQuantity = l.ActualQuantity,
                UnitCost = l.UnitCost
            }).ToList()
        });
        return CreatedId(result);
    }

    private static async Task<long> CreateAndSubmitAsync(StockAdjustmentController ctl,
        params (long ProductId, string Name, decimal BookQuantity, decimal ActualQuantity, decimal UnitCost)[] lines)
    {
        var id = await CreateAsync(ctl, lines);
        await ctl.Submit(id);
        return id;
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        db.SaveChanges();
    }

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
