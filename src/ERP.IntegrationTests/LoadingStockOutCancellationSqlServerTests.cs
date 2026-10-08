using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-367 来源销售出库单取消 / 冲销前的「已审核装柜清单显式引用」护栏 —— 真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>拒绝不改台账</b>：存在「已审核、未删除」装柜清单明细显式链接（<c>SourceStockOutDetailId</c>）到本出库单明细时，
/// 真实 <see cref="StockOutController.Cancel"/> 在取得来源出库单行锁的可串行化事务内 fail closed 拒绝，
/// 单据 / 明细 / 状态 / 库存 / 流水 / 装柜证据全部保持原样；</item>
/// <item><b>释放</b>：装柜清单取消后护栏即时释放，来源出库单按既有流程恢复库存并写红字冲销流水，装柜链接证据原样保留，重复取消幂等拒绝；</item>
/// <item><b>不误判</b>：历史未链接（<c>null</c>）明细、未审核 / 已删除装柜清单、链接到其它出库单明细的行均不阻断；</item>
/// <item><b>既有护栏不被绕过</b>：装柜释放后仍存在已审核销售退货时，ERP-359 退货引用护栏继续拒绝；</item>
/// <item><b>两条独立连接竞争</b>：①「装柜审核」与「来源取消」只出现一种一致结果；②释放后两条并发取消只成功一次（库存与冲销流水恰好一次）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class LoadingStockOutCancellationSqlServerTests
    : IClassFixture<LoadingStockOutCancellationSqlServerFixture>
{
    private readonly LoadingStockOutCancellationSqlServerFixture _fixture;

    public LoadingStockOutCancellationSqlServerTests(LoadingStockOutCancellationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(LoadingStockOutCancellationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 真实控制器脚手架 ====================

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static StockOutController NewStockOutController(ErpDbContext db, long? userId)
    {
        var ctl = new StockOutController(db, new DocumentNumberService(db), new InventoryService(db));
        SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerLoadingListController NewLoadingController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
        SetUser(ctl, userId);
        return ctl;
    }

    private static async Task<StockOut> ReloadStockOutAsync(ErpDbContext db, long id)
        => await db.StockOuts.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<ContainerLoadingList> ReloadLoadingAsync(ErpDbContext db, long id)
        => await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<decimal> StockQuantityAsync(ErpDbContext db, long warehouseId, long productId)
        => await db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
            .SumAsync(s => s.Quantity);

    /// <summary>两个独立连接 / DbContext / 事务在同一闸门后同时发起操作。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    private async Task<(bool Success, string Error)> TryCancelStockOutAsync(long stockOutId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewStockOutController(db, userId).Cancel(stockOutId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryApproveLoadingAsync(long loadingListId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewLoadingController(db, userId).Approve(loadingListId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 播种助手（真实主数据 + 真实授权身份） ====================

    private static async Task<SysMenu> SeedMenuAsync(ErpDbContext db, string code, string name, string path)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            Icon = "box", SortOrder = 20, MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>
    /// 播种真实授权操作员（启用账号 + 既有菜单授权：装柜清单 / 销售出库 / 销售退货），
    /// 不使用匿名 / 管理员兜底，也不新增任何权限模型。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole { RoleCode = $"LSC-OP-{Tag()}", RoleName = "装柜出运证据操作员", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"lsc-op-{Tag()}";
        var user = new SysUser
        {
            UserName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "装柜出运证据操作员", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var code in menuCodes)
        {
            var menu = await SeedMenuAsync(db, code, code, $"/logistics/{code}");
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"LSCW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit = "PCS")
    {
        var product = new BaseProduct
        {
            ProductCode = $"LSCP-{Tag()}", ProductName = name, Spec = "规格A", Unit = unit, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer { CustomerCode = $"LSCC-{Tag()}", CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId,
        decimal quantity, decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId, ProductId = productId, Quantity = quantity,
            AvailableQuantity = quantity, TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        await db.SaveChangesAsync();
    }

    private static async Task<StockOutDetail> SeedApprovedStockOutAsync(ErpDbContext db, long customerId,
        long warehouseId, long productId, decimal quantity)
    {
        var stockOut = new StockOut
        {
            StockOutNo = $"CK-LSC-SQL-{Tag()}", StockOutDate = DateTime.Today, CustomerId = customerId,
            WarehouseId = warehouseId, Status = DocumentStatus.Approved,
            TotalQuantity = quantity, Remark = "ERP-367_INT",
            Details = new List<StockOutDetail>
            {
                new() { ProductId = productId, ProductName = $"商品{productId}", Unit = "PCS", Quantity = quantity }
            }
        };
        db.StockOuts.Add(stockOut);
        await db.SaveChangesAsync();
        return stockOut.Details.Single();
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(ErpDbContext db, long customerId,
        DocumentStatus status, bool deleted,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = $"ZQ-LSC-{Tag()}", LoadingDate = DateTime.Today, CustomerId = customerId,
            Status = status, IsDeleted = deleted, Remark = "ERP-367_INT"
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceStockOutDetailId = sourceDetailId
            });
        }
        await db.SaveChangesAsync();
        return list;
    }

    private static async Task<SalesReturn> SeedApprovedSalesReturnAsync(ErpDbContext db, long stockOutId,
        string stockOutNo, long customerId, long warehouseId, long productId, decimal quantity)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-LSC-SQL-{Tag()}", ReturnDate = DateTime.Today, CustomerId = customerId,
            CustomerName = "装柜出运客户", WarehouseId = warehouseId,
            SourceStockOutId = stockOutId, SourceStockOutNo = stockOutNo, ReturnReason = "质量",
            Status = DocumentStatus.Approved, TotalQuantity = quantity,
            Details = new List<SalesReturnDetail>
            {
                new() { ProductId = productId, ProductName = $"商品{productId}", Spec = "规格A", Unit = "PCS",
                    Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m, UnitCost = 8m }
            }
        };
        db.SalesReturns.Add(ret);
        await db.SaveChangesAsync();
        return ret;
    }

    // ==================== 1. 已审核装柜清单显式引用 → 拒绝且台账不变 ====================

    [Fact]
    public async Task Live_cancel_rejected_while_approved_loading_links_source_detail()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "装柜出运仓");
        var productId = await SeedProductAsync(seed, "装柜出运商品");
        var customerId = await SeedCustomerAsync(seed, "装柜出运客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var sourceDetail = await SeedApprovedStockOutAsync(seed, customerId, warehouseId, productId, quantity: 5m);
        var list = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Approved, deleted: false,
            (productId, 5m, sourceDetail.Id));

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewStockOutController(db, userId).Cancel(sourceDetail.StockOutId));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains(LoadingStockOutLinkRules.LoadingReversalRequirementText, ex.Message);
            Assert.Contains(list.LoadingListNo, ex.Message);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await ReloadStockOutAsync(db, sourceDetail.StockOutId);
            Assert.Equal(DocumentStatus.Approved, stored.Status);
            Assert.Equal(5m, stored.Details.Single().Quantity);
            Assert.Equal(10m, await StockQuantityAsync(db, warehouseId, productId));
            Assert.Equal(0, await db.StockMovements.AsNoTracking()
                .CountAsync(m => m.WarehouseId == warehouseId && !m.IsDeleted));
            var loading = await ReloadLoadingAsync(db, list.Id);
            Assert.Equal(DocumentStatus.Approved, loading.Status);
            Assert.Equal(sourceDetail.Id, loading.Details.Single().SourceStockOutDetailId);
            Assert.Equal(5m, loading.Details.Single().Quantity);
        }
    }

    // ==================== 2. 装柜取消释放护栏（证据保留 + 重复取消幂等） ====================

    [Fact]
    public async Task Live_cancel_released_after_loading_cancelled_preserves_loading_evidence()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "装柜释放仓");
        var productId = await SeedProductAsync(seed, "装柜释放商品");
        var customerId = await SeedCustomerAsync(seed, "装柜释放客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var sourceDetail = await SeedApprovedStockOutAsync(seed, customerId, warehouseId, productId, quantity: 5m);
        var list = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Approved, deleted: false,
            (productId, 5m, sourceDetail.Id));

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await NewLoadingController(db, userId).Cancel(list.Id));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var released = await ReloadLoadingAsync(db, list.Id);
            Assert.Equal(DocumentStatus.Cancelled, released.Status);
            Assert.Equal(sourceDetail.Id, released.Details.Single().SourceStockOutDetailId);
            Assert.Equal(5m, released.Details.Single().Quantity);
            Assert.IsType<OkObjectResult>(await NewStockOutController(db, userId).Cancel(sourceDetail.StockOutId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Cancelled,
                (await ReloadStockOutAsync(db, sourceDetail.StockOutId)).Status);
            // 历史无流水单据按基础单位原路恢复：10 + 5
            Assert.Equal(15m, await StockQuantityAsync(db, warehouseId, productId));
            // 重复取消按既有规则幂等拒绝，不重复恢复库存
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewStockOutController(db, userId).Cancel(sourceDetail.StockOutId));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("已取消", ex.Message);
            Assert.Equal(15m, await StockQuantityAsync(db, warehouseId, productId));
            Assert.Equal(sourceDetail.Id,
                (await ReloadLoadingAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
        }
    }

    // ==================== 3. 非生效装柜清单 / 历史未链接 → 不阻断 ====================

    [Fact]
    public async Task Live_null_source_unapproved_and_deleted_loading_do_not_block()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "非生效装柜仓");
        var productId = await SeedProductAsync(seed, "非生效装柜商品");
        var customerId = await SeedCustomerAsync(seed, "非生效装柜客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var sourceDetail = await SeedApprovedStockOutAsync(seed, customerId, warehouseId, productId, quantity: 5m);

        // 历史未链接（null = 显式无证据）：即使同商品、数量巨大也绝不按商品猜测来源
        var legacy = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Approved, deleted: false,
            (productId, 100m, null));
        var submitted = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Submitted, deleted: false,
            (productId, 5m, sourceDetail.Id));
        var cancelled = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Cancelled, deleted: false,
            (productId, 5m, sourceDetail.Id));
        var deleted = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Approved, deleted: true,
            (productId, 5m, sourceDetail.Id));

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await NewStockOutController(db, userId).Cancel(sourceDetail.StockOutId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Cancelled,
                (await ReloadStockOutAsync(db, sourceDetail.StockOutId)).Status);
            Assert.Equal(15m, await StockQuantityAsync(db, warehouseId, productId));
            var legacyStored = await ReloadLoadingAsync(db, legacy.Id);
            Assert.Equal(DocumentStatus.Approved, legacyStored.Status);
            Assert.Null(legacyStored.Details.Single().SourceStockOutDetailId);
            Assert.Equal(100m, legacyStored.Details.Single().Quantity);
            Assert.Equal(DocumentStatus.Submitted, (await ReloadLoadingAsync(db, submitted.Id)).Status);
            Assert.Equal(DocumentStatus.Cancelled, (await ReloadLoadingAsync(db, cancelled.Id)).Status);
            Assert.True((await ReloadLoadingAsync(db, deleted.Id)).IsDeleted);
        }
    }

    // ==================== 4. 既有 ERP-359 退货护栏不被绕过 ====================

    [Fact]
    public async Task Live_existing_sales_return_guard_still_rejects_after_loading_release()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货护栏仓");
        var productId = await SeedProductAsync(seed, "退货护栏商品");
        var customerId = await SeedCustomerAsync(seed, "退货护栏客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var sourceDetail = await SeedApprovedStockOutAsync(seed, customerId, warehouseId, productId, quantity: 5m);
        var stockOutNo = await seed.StockOuts.Where(o => o.Id == sourceDetail.StockOutId)
            .Select(o => o.StockOutNo).SingleAsync();
        var list = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Approved, deleted: false,
            (productId, 5m, sourceDetail.Id));

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await NewLoadingController(db, userId).Cancel(list.Id));
        }
        await SeedApprovedSalesReturnAsync(seed, sourceDetail.StockOutId, stockOutNo, customerId, warehouseId,
            productId, quantity: 5m);

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewStockOutController(db, userId).Cancel(sourceDetail.StockOutId));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("销审", ex.Message);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Approved,
                (await ReloadStockOutAsync(db, sourceDetail.StockOutId)).Status);
            Assert.Equal(10m, await StockQuantityAsync(db, warehouseId, productId));
            Assert.Equal(0, await db.StockMovements.AsNoTracking()
                .CountAsync(m => m.WarehouseId == warehouseId && !m.IsDeleted));
        }
    }

    // ==================== 5. 两条独立连接竞争：装柜审核 vs 来源取消 ====================

    [Fact]
    public async Task Two_connections_loading_approval_races_stock_out_cancel_single_consistent_outcome()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "竞争仓");
        var productId = await SeedProductAsync(seed, "竞争商品");
        var customerId = await SeedCustomerAsync(seed, "竞争客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        // 不播种库存：历史无流水已审核出库单取消时按基础单位原路恢复（10），便于区分两种一致结果。
        var sourceDetail = await SeedApprovedStockOutAsync(seed, customerId, warehouseId, productId, quantity: 10m);
        var list = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Submitted, deleted: false,
            (productId, 10m, sourceDetail.Id));

        var results = await RaceAsync(
            () => TryApproveLoadingAsync(list.Id, userId),
            () => TryCancelStockOutAsync(sourceDetail.StockOutId, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            var loadingStatus = await db.ContainerLoadingLists.AsNoTracking()
                .Where(l => l.Id == list.Id).Select(l => l.Status).SingleAsync();
            var stockOutStatus = await db.StockOuts.AsNoTracking()
                .Where(o => o.Id == sourceDetail.StockOutId).Select(o => o.Status).SingleAsync();
            var stock = await StockQuantityAsync(db, warehouseId, productId);

            // 只允许一种一致结果：装柜已审核（来源保持已审核）异或 来源已取消（装柜保持已提交）
            var loadingApproved = loadingStatus == DocumentStatus.Approved;
            var stockOutCancelled = stockOutStatus == DocumentStatus.Cancelled;
            Assert.True(loadingApproved ^ stockOutCancelled);

            if (loadingApproved)
            {
                Assert.Equal(DocumentStatus.Approved, stockOutStatus);
                Assert.Equal(0m, stock);
                Assert.Equal(sourceDetail.Id,
                    (await ReloadLoadingAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
            }
            else
            {
                Assert.Equal(DocumentStatus.Submitted, loadingStatus);
                Assert.Equal(10m, stock);
            }
        }
    }

    // ==================== 6. 两条独立连接竞争：释放后并发取消只成功一次 ====================

    [Fact]
    public async Task Two_connections_concurrent_stock_out_cancels_serialize_once()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "并发取消仓");
        var productId = await SeedProductAsync(seed, "并发取消商品");
        var customerId = await SeedCustomerAsync(seed, "并发取消客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var sourceDetail = await SeedApprovedStockOutAsync(seed, customerId, warehouseId, productId, quantity: 5m);
        // 装柜清单已取消：护栏已释放，但链接证据原样保留
        var list = await SeedLoadingListAsync(seed, customerId, DocumentStatus.Cancelled, deleted: false,
            (productId, 5m, sourceDetail.Id));

        var results = await RaceAsync(
            () => TryCancelStockOutAsync(sourceDetail.StockOutId, userId),
            () => TryCancelStockOutAsync(sourceDetail.StockOutId, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));
        Assert.Contains("已取消", results.Single(r => !r.Success).Error);

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Cancelled,
                (await ReloadStockOutAsync(db, sourceDetail.StockOutId)).Status);
            // 恰好恢复一次：10 + 5
            Assert.Equal(15m, await StockQuantityAsync(db, warehouseId, productId));
            Assert.Equal(sourceDetail.Id,
                (await ReloadLoadingAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
        }
    }

    // ==================== 7. 后续库存冲销失败 → 整体回滚 ====================

    [Fact]
    public async Task Live_late_inventory_reversal_failure_rolls_back_all_changes()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "回滚仓");
        var productId = await SeedProductAsync(seed, "回滚商品");
        var customerId = await SeedCustomerAsync(seed, "回滚客户");
        var userId = await SeedOperatorAsync(seed, LoadingStockOutLinkRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);

        long stockOutId;
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockOutController(db, userId);
            stockOutId = CreatedId(await ctl.Create(new StockOut
            {
                StockOutDate = DateTime.Today, CustomerId = customerId, WarehouseId = warehouseId,
                Remark = "ERP-367_INT",
                Details = new List<StockOutDetail>
                {
                    new() { ProductId = productId, ProductName = $"商品{productId}", Unit = "PCS", Quantity = 5m }
                }
            }));
            await ctl.Submit(stockOutId);
            await ctl.Approve(stockOutId);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(5m, await StockQuantityAsync(db, warehouseId, productId));
            var movementId = await db.StockMovements.AsNoTracking()
                .Where(m => m.SourceDocId == stockOutId && !m.IsReversal)
                .Select(m => m.Id).SingleAsync();

            // 人为让后续库存冲销失败：软删除库存行 → ReverseAsync 在锁内 fail closed 抛业务冲突
            var stock = await db.Stocks.SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
            stock.IsDeleted = true;
            stock.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewStockOutController(db, userId).Cancel(stockOutId));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("无法销审冲销", ex.Message);

            // 整体回滚：状态未被改写为已取消，正向流水未被标记已冲销，也没有红字流水
            db.ChangeTracker.Clear();
            Assert.Equal(DocumentStatus.Approved, await db.StockOuts.AsNoTracking()
                .Where(o => o.Id == stockOutId).Select(o => o.Status).SingleAsync());
            Assert.False(await db.StockMovements.AsNoTracking()
                .Where(m => m.Id == movementId).Select(m => m.IsReversed).SingleAsync());
            Assert.Equal(0, await db.StockMovements.AsNoTracking()
                .CountAsync(m => m.SourceDocId == stockOutId && m.IsReversal));
        }
    }

    private static long CreatedId(IActionResult result)
    {
        Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }
}

/// <summary>
/// 专用 localdb 夹具：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class LoadingStockOutCancellationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_LOADINGSTOCKOUTCANCEL_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine(
            $"[ERP-367] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-367] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class LoadingStockOutCancellationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => LoadingStockOutCancellationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => LoadingStockOutCancellationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
