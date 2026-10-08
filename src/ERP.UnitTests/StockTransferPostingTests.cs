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
/// 仓库调拨测试身份 / 主数据脚手架（ERP-354）：为直接实例化 <see cref="StockTransferController"/> 的单元测试
/// 注入**真实 HTTP 身份**，并播种既有「库存查询」（<c>stock-query</c>）菜单授权。
/// 不新增权限模型、不绕过鉴权：拒绝场景一律不播种菜单或播种禁用账号。
/// </summary>
internal static class StockTransferTestAuthorization
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
        var role = new SysRole { RoleCode = $"ST-{Guid.NewGuid():N}", RoleName = "调拨操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"st-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "调拨操作员",
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
            UserName = $"st-no-menu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>创建已注入授权身份的 StockTransferController。</summary>
    public static StockTransferController CreateAuthorized(ErpDbContext db)
        => ForUser(db, SeedAuthorizedOperator(db));

    /// <summary>把指定登录用户 Id（可空 = 无身份）写入控制器 HttpContext。</summary>
    public static StockTransferController ForUser(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };
}

/// <summary>
/// ERP-354 仓库调拨过账 / 冲销原子化单元测试（内存库，不连接 SQL Server）：
/// 1) 授权操作员审核恰好一次（两仓数量 / 金额守恒、成本单价一致），重复审核被拒绝；
/// 2) 审核 / 销审 / 取消 / 删除共用单据行锁边界，授权 fail closed（无身份 / 无菜单 / 禁用账号）；
/// 3) 审核后续明细失败、销审调入被消耗时，两侧库存余额、库存流水与单据状态全部不变；
/// 4) 库存行锁定顺序确定（仓库 → 商品升序），对向调拨共用同一顺序。
/// 说明：内存库无事务回滚与行锁语义，控制器在失败路径显式丢弃跟踪中的半成品变更以保证「全不变」。
/// </summary>
public class StockTransferPostingTests
{
    private const long WarehouseA = 910001L;
    private const long WarehouseB = 910002L;
    private const long Product1 = 810001L;
    private const long Product2 = 810002L;

    // ==================== 1. 授权操作员审核恰好一次 ====================

    [Fact]
    public async Task 调拨单_授权操作员审核_两仓守恒恰好一次_重复审核拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);   // 均价 10
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 30m, 0m));

        await ctl.Approve(id);

        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        var to = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1);
        Assert.Equal(70m, from.Quantity);
        Assert.Equal(700m, from.TotalCost);
        Assert.Equal(30m, to.Quantity);
        Assert.Equal(300m, to.TotalCost);
        Assert.Equal(100m, from.Quantity + to.Quantity);            // 数量守恒
        Assert.Equal(1000m, from.TotalCost + to.TotalCost);         // 金额守恒
        Assert.Equal(10m, to.AverageCost);                          // 两侧同一成本单价
        Assert.Equal(DocumentStatus.Approved, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Equal(2, db.StockMovements.Count());

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(2, db.StockMovements.Count());                 // 重复审核不新增流水
        Assert.Equal(70m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
    }

    [Fact]
    public async Task 调拨单_销审_两仓还原_重复销审拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 40m, 10m));
        await ctl.Approve(id);

        await ctl.Unaudit(id);

        Assert.Equal(100m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(1000m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).TotalCost);
        Assert.Equal(0m, db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1).Quantity);
        Assert.Equal(DocumentStatus.Pending, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Equal(4, db.StockMovements.Count());                 // 2 条原流水 + 2 条红字冲销
        Assert.Equal(2, db.StockMovements.Count(m => m.IsReversal));

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(4, db.StockMovements.Count());
    }

    // ==================== 2. 授权 fail closed ====================

    [Fact]
    public async Task 调拨单_无身份_全部路由拒绝且不落任何库存()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        var authorized = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(authorized, WarehouseA, WarehouseB, (Product1, "P1", 10m, 10m));

        var ctl = StockTransferTestAuthorization.ForUser(db, null);
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.Unauthorized, approveEx.Code);
        var readEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(id));
        Assert.Equal(ErrorCodes.Unauthorized, readEx.Code);

        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Equal(100m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
    }

    [Fact]
    public async Task 调拨单_无菜单授权或禁用账号_审核拒绝且不落库存()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        var authorized = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(authorized, WarehouseA, WarehouseB, (Product1, "P1", 10m, 10m));

        var noMenu = StockTransferTestAuthorization.ForUser(db, StockTransferTestAuthorization.SeedUnauthorizedUser(db));
        var menuEx = await Assert.ThrowsAsync<BusinessException>(() => noMenu.Approve(id));
        Assert.Equal(ErrorCodes.Forbidden, menuEx.Code);

        var disabled = StockTransferTestAuthorization.ForUser(db,
            StockTransferTestAuthorization.SeedAuthorizedOperator(db, enabled: false));
        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() => disabled.Approve(id));
        Assert.Equal(ErrorCodes.Forbidden, disabledEx.Code);

        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
    }

    [Fact]
    public async Task 调拨单_创建_未授权不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockTransferTestAuthorization.ForUser(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockTransfer
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = WarehouseA,
            ToWarehouseId = WarehouseB,
            Details = new List<StockTransferDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Quantity = 1m, UnitCost = 1m }
            }
        }));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.StockTransfers);
        Assert.Empty(db.SysDocumentNumberRules);                    // 拒绝方绝不消耗单据号
    }

    // ==================== 3. 失败不留下半成品 ====================

    [Fact]
    public async Task 调拨单_后续明细失败_两侧库存流水与状态全部不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        SeedStock(db, WarehouseA, Product2, quantity: 1m, totalCost: 10m);       // 第二行不足
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(ctl, WarehouseA, WarehouseB,
            (Product1, "P1", 5m, 0m), (Product2, "P2", 10m, 0m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 第一行已扣减、第二行不足 → 两侧余额、流水与状态必须与审核前完全一致
        Assert.Equal(100m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(1000m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).TotalCost);
        Assert.Equal(1m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product2).Quantity);
        Assert.Empty(db.Stocks.Where(s => s.WarehouseId == WarehouseB));
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
    }

    [Fact]
    public async Task 调拨单_销审_调入被消耗_两侧余额流水与状态全部不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 40m, 10m));
        await ctl.Approve(id);

        // 调入仓的货已被后续业务领走 → 冲销必然失败
        var dest = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1);
        dest.Quantity = 0m;
        dest.TotalCost = 0m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Equal(60m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).Quantity);
        Assert.Equal(600m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1).TotalCost);
        Assert.Equal(0m, db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1).Quantity);
        Assert.Equal(2, db.StockMovements.Count());                 // 未追加红字流水
        Assert.All(db.StockMovements, m => Assert.False(m.IsReversed));
        Assert.Equal(DocumentStatus.Approved, db.StockTransfers.Single(t => t.Id == id).Status);
    }

    // ==================== 4. 同单边界：取消 / 删除 / 修改 ====================

    [Fact]
    public async Task 调拨单_已审核_取消删除修改被拒绝_需先销审()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);
        var id = await CreateAndSubmitAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 10m, 10m));
        await ctl.Approve(id);

        var cancelEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(id));
        Assert.Equal(ErrorCodes.RuleConflict, cancelEx.Code);
        var deleteEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(id));
        Assert.Equal(ErrorCodes.RuleConflict, deleteEx.Code);
        var updateEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id, new StockTransfer
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = WarehouseA,
            ToWarehouseId = WarehouseB,
            Details = new List<StockTransferDetail>
            {
                new() { ProductId = Product1, ProductName = "P1", Quantity = 1m, UnitCost = 1m }
            }
        }));
        Assert.Equal(ErrorCodes.RuleConflict, updateEx.Code);

        Assert.Equal(DocumentStatus.Approved, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.False(db.StockTransfers.Single(t => t.Id == id).IsDeleted);
        Assert.Equal(2, db.StockMovements.Count());
    }

    [Fact]
    public async Task 调拨单_待提交_取消与删除保持既有规则()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockTransferTestAuthorization.CreateAuthorized(db);

        var pendingForCancel = await CreateAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 1m, 1m));
        await ctl.Cancel(pendingForCancel);
        Assert.Equal(DocumentStatus.Cancelled, db.StockTransfers.Single(t => t.Id == pendingForCancel).Status);

        var pendingForDelete = await CreateAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 1m, 1m));
        await ctl.Delete(pendingForDelete);
        Assert.True(db.StockTransfers.Single(t => t.Id == pendingForDelete).IsDeleted);

        // 已提交（非待提交）仍不可删除，保持既有编辑规则
        var submitted = await CreateAndSubmitAsync(ctl, WarehouseA, WarehouseB, (Product1, "P1", 1m, 1m));
        var deleteEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(submitted));
        Assert.Equal(ErrorCodes.RuleConflict, deleteEx.Code);
        Assert.False(db.StockTransfers.Single(t => t.Id == submitted).IsDeleted);
    }

    // ==================== 5. 确定性库存锁定顺序 ====================

    [Fact]
    public void 库存锁定顺序_按仓库与商品升序_去重且对向调拨同序()
    {
        var ordered = InventoryService.OrderStockIdentities(new[]
        {
            new StockIdentity(9, 2), new StockIdentity(3, 5), new StockIdentity(9, 1),
            new StockIdentity(3, 1), new StockIdentity(9, 2), new StockIdentity(0, 7)
        });

        Assert.Equal(new[] { (3L, 1L), (3L, 5L), (9L, 1L), (9L, 2L) },
            ordered.Select(k => (k.WarehouseId, k.ProductId)).ToArray());

        var details = new List<StockTransferDetail>
        {
            new() { ProductId = 1 }, new() { ProductId = 2 }, new() { ProductId = 1 }
        };
        var aToB = InventoryService.OrderStockIdentities(StockTransferPostingRules.StockKeys(3, 9, details));
        var bToA = InventoryService.OrderStockIdentities(StockTransferPostingRules.StockKeys(9, 3, details));

        Assert.Equal(4, aToB.Count);                                 // 两侧仓库 × 去重商品
        Assert.Equal(aToB, bToA);                                    // 对向调拨共用同一锁定顺序
    }

    // ==================== 6. 源代码契约 ====================

    [Fact]
    public void 调拨过账契约_先锁单据行再读状态流水_并按确定性顺序锁库存行()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockTransferController.cs");
        Assert.Contains("StockTransferPostingRules.EnsureMenuAuthorizedAsync", controller);
        Assert.Contains("StockTransfers WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("Stocks WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("InventoryService.OrderStockIdentities", controller);
        Assert.Contains("StockTransferPostingRules.StockKeys", controller);
        Assert.Contains("DiscardTrackedChanges", controller);

        var approveStart = controller.IndexOf("public override async Task<IActionResult> Approve", StringComparison.Ordinal);
        Assert.True(approveStart >= 0);
        var approve = controller[approveStart..];
        var lockRow = approve.IndexOf("LockTransferRowAsync", StringComparison.Ordinal);
        var movements = approve.IndexOf("CountActiveMovementsAsync", StringComparison.Ordinal);
        var decrease = approve.IndexOf("DecreaseAsync(outContext", StringComparison.Ordinal);
        Assert.True(lockRow >= 0 && movements > lockRow && decrease > movements);

        var unauditStart = controller.IndexOf("public async Task<IActionResult> Unaudit", StringComparison.Ordinal);
        Assert.True(unauditStart >= 0);
        var unaudit = controller[unauditStart..];
        Assert.True(unaudit.IndexOf("LockTransferRowAsync", StringComparison.Ordinal)
                    < unaudit.IndexOf("ReverseAsync", StringComparison.Ordinal));

        var rules = ReadSource("ERP.Application/Services/StockTransferPostingRules.cs");
        Assert.DoesNotContain("SaveChanges", rules);                 // 纯判定，不落库
        Assert.Contains("AsNoTracking", rules);
        Assert.Equal("stock-query", StockTransferPostingRules.RequiredMenuCode);
    }

    // ==================== 测试辅助 ====================

    private static async Task<long> CreateAsync(StockTransferController ctl, long fromWarehouseId,
        long toWarehouseId, params (long ProductId, string Name, decimal Quantity, decimal UnitCost)[] lines)
    {
        var result = await ctl.Create(new StockTransfer
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Remark = "ERP-354_TEST",
            Details = lines.Select(l => new StockTransferDetail
            {
                ProductId = l.ProductId,
                ProductName = l.Name,
                Spec = "规格A",
                Unit = "PCS",
                Quantity = l.Quantity,
                UnitCost = l.UnitCost
            }).ToList()
        });
        return CreatedId(result);
    }

    private static async Task<long> CreateAndSubmitAsync(StockTransferController ctl, long fromWarehouseId,
        long toWarehouseId, params (long ProductId, string Name, decimal Quantity, decimal UnitCost)[] lines)
    {
        var id = await CreateAsync(ctl, fromWarehouseId, toWarehouseId, lines);
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

