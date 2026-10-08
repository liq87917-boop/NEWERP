using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-355 库存盘点单过账 / 冲销原子化的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>用与 <c>StockAdjustmentController</c> 完全相同的锁 / 事务协议直接验证：</para>
/// <list type="number">
/// <item>实时授权 fail closed（无身份 / 无菜单 / 禁用账号）时库存、流水与状态不变；</item>
/// <item>授权操作员按**已验证的账面数量基线**审核恰好一次，销审按红字流水还原；</item>
/// <item><b>两条独立连接竞争</b>：并发审核 / 审核只允许一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核 / 取消只允许一方成功，绝不产生已取消单据的库存。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockAdjustmentPostingSqlServerTests
    : IClassFixture<StockAdjustmentPostingSqlServerFixture>
{
    private readonly StockAdjustmentPostingSqlServerFixture _fixture;

    public StockAdjustmentPostingSqlServerTests(StockAdjustmentPostingSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockAdjustmentPostingSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 授权 fail closed（库存 / 流水 / 状态不变） ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-menu")]
    [InlineData("disabled")]
    public async Task Denied_identities_post_no_inventory_and_leave_document_untouched(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "盘点商品");
        await SeedStockAsync(db, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var adjustment = await SeedAdjustmentAsync(db, warehouseId, DocumentStatus.Submitted,
            (productId, 10m, 7m, 10m));

        long? userId = null;
        switch (scenario)
        {
            case "no-menu":
                userId = await SeedUserAsync(db, withMenu: false, enabled: true);
                break;
            case "disabled":
                userId = await SeedUserAsync(db, withMenu: true, enabled: false);
                break;
        }

        var beforeMovements = await db.StockMovements.CountAsync();
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            StockAdjustmentPostingRules.EnsureMenuAuthorizedAsync(db, userId));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, error.Code);

        Assert.Equal(beforeMovements, await db.StockMovements.CountAsync());
        Assert.Equal(10m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(DocumentStatus.Submitted, await db.StockAdjustments.Where(a => a.Id == adjustment.Id)
            .Select(a => a.Status).SingleAsync());
    }

    // ==================== 2. 已验证账面基线 + 恰好一次 + 销审还原 ====================

    [Fact]
    public async Task Authorized_operator_approves_once_from_verified_baseline_and_reversal_restores()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "盘点仓");
        var productId = await SeedProductAsync(seed, "盘点商品");
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);
        var adjustment = await SeedAdjustmentAsync(seed, warehouseId, DocumentStatus.Submitted,
            (productId, 10m, 7m, 10m));

        var approved = await TryApproveAsync(adjustment.Id, userId);
        Assert.True(approved.Success, approved.Error);

        await using (var db = _fixture.CreateDbContext())
        {
            var stock = await db.Stocks.SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
            Assert.Equal(7m, stock.Quantity);
            Assert.Equal(70m, stock.TotalCost);
            Assert.Equal(10m, stock.AverageCost);
            Assert.Equal(DocumentStatus.Approved, await db.StockAdjustments.Where(a => a.Id == adjustment.Id)
                .Select(a => a.Status).SingleAsync());
            Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == adjustment.Id && !m.IsDeleted));
        }

        // 重复审核：不重复过账
        var again = await TryApproveAsync(adjustment.Id, userId);
        Assert.False(again.Success);
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == adjustment.Id && !m.IsDeleted));
        }

        // 销审：按既有红字流水冲销并还原
        await using (var db = _fixture.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var entity = await db.StockAdjustments.Include(o => o.Details).SingleAsync(o => o.Id == adjustment.Id);
            await LockStockRowsAsync(db, entity.WarehouseId, entity.Details);
            var service = new InventoryService(db);
            await service.ReverseAsync("StockAdjustment", entity.Id, "盘点单销审冲销");
            entity.Status = DocumentStatus.Pending;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stock = await db.Stocks.SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
            Assert.Equal(10m, stock.Quantity);
            Assert.Equal(100m, stock.TotalCost);
            Assert.Equal(DocumentStatus.Pending, await db.StockAdjustments.Where(a => a.Id == adjustment.Id)
                .Select(a => a.Status).SingleAsync());
            Assert.Equal(1, await db.StockMovements
                .CountAsync(m => m.SourceDocId == adjustment.Id && m.IsReversal && !m.IsDeleted));
        }
    }

    // ==================== 3. 两条独立连接竞争：审核 / 审核 ====================

    [Fact]
    public async Task Two_connections_approve_twice_only_one_posts_inventory()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "盘点仓");
        var productId = await SeedProductAsync(seed, "盘点商品");
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);
        var adjustment = await SeedAdjustmentAsync(seed, warehouseId, DocumentStatus.Submitted,
            (productId, 10m, 7m, 10m));

        // 两条独立连接同时审核同一张单据
        var results = await Task.WhenAll(
            TryApproveAsync(adjustment.Id, userId),
            TryApproveAsync(adjustment.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            // 库存只被调整一次（10 → 7），只产生一笔流水
            Assert.Equal(7m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == adjustment.Id && !m.IsDeleted));
            Assert.Equal(DocumentStatus.Approved, await db.StockAdjustments.Where(a => a.Id == adjustment.Id)
                .Select(a => a.Status).SingleAsync());
        }
    }

    // ==================== 4. 两条独立连接竞争：审核 / 取消 ====================

    [Fact]
    public async Task Two_connections_approve_and_cancel_only_one_succeeds()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "盘点仓");
        var productId = await SeedProductAsync(seed, "盘点商品");
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);
        var adjustment = await SeedAdjustmentAsync(seed, warehouseId, DocumentStatus.Submitted,
            (productId, 10m, 6m, 10m));

        // 一条连接审核、另一条连接取消：共用同一把单据行锁，只能成功其一
        var results = await Task.WhenAll(
            TryApproveAsync(adjustment.Id, userId),
            TryCancelAsync(adjustment.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        var approved = results[0].Success;

        await using (var db = _fixture.CreateDbContext())
        {
            var status = await db.StockAdjustments.Where(a => a.Id == adjustment.Id)
                .Select(a => a.Status).SingleAsync();
            var movementCount = await db.StockMovements
                .CountAsync(m => m.SourceDocId == adjustment.Id && !m.IsDeleted);

            if (approved)
            {
                // 审核先成功：库存按差异调整一次
                Assert.Equal(DocumentStatus.Approved, status);
                Assert.Equal(1, movementCount);
                Assert.Equal(6m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                    .Select(s => s.Quantity).SingleAsync());
            }
            else
            {
                // 取消先成功：不产生任何库存 / 流水
                Assert.Equal(DocumentStatus.Cancelled, status);
                Assert.Equal(0, movementCount);
                Assert.Equal(10m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                    .Select(s => s.Quantity).SingleAsync());
            }
        }
    }

    // ==================== 控制器相同的锁 / 事务协议（帮助方法） ====================

    /// <summary>
    /// 完整复刻 <c>StockAdjustmentController.Approve</c> 的协议：可串行化事务 → 单据行锁（UPDLOCK/HOLDLOCK）→
    /// 锁内授权 → 锁内重读 → 明细 / 主数据校验 → 确定性库存行锁 → 账面基线核验 → <see cref="InventoryService"/> 记账。
    /// </summary>
    private async Task<(bool Success, string Error)> TryApproveAsync(long adjustmentId, long? userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.StockAdjustments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", adjustmentId)
                .ToListAsync();
            if (ids.Count == 0) throw BusinessException.NotFound("盘点单不存在");

            await StockAdjustmentPostingRules.EnsureMenuAuthorizedAsync(db, userId);

            var entity = await db.StockAdjustments.Include(o => o.Details)
                .SingleAsync(o => o.Id == adjustmentId && !o.IsDeleted);
            if (entity.Status != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("仅已提交的盘点单可审核");

            var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
            if (details.Count == 0) throw BusinessException.RuleConflict("盘点单无明细，不能审核");

            var inventory = new InventoryService(db);
            if (await inventory.CountActiveMovementsAsync("StockAdjustment", entity.Id) > 0)
                throw BusinessException.RuleConflict("该盘点单已产生库存流水，不能重复审核");

            StockAdjustmentPostingRules.EnsureLineShape(details);
            await StockAdjustmentPostingRules.EnsureLiveMasterDataAsync(db, entity.WarehouseId, details);

            await LockStockRowsAsync(db, entity.WarehouseId, details);
            foreach (var d in details)
            {
                var current = await inventory.GetCurrentQuantityAsync(entity.WarehouseId, d.ProductId!.Value);
                StockAdjustmentPostingRules.EnsureFreshBookQuantity(d.ProductName, d.BookQuantity, current);
            }

            var warehouseName = await WarehouseNameAsync(db, entity.WarehouseId);
            foreach (var d in details)
            {
                var diff = d.ActualQuantity - d.BookQuantity;
                if (diff == 0) continue;

                var context = new InventoryMovementContext
                {
                    SourceDocType = "StockAdjustment", SourceDocId = entity.Id, SourceDocNo = entity.AdjustmentNo,
                    MovementType = InventoryMovementType.Adjustment, WarehouseId = entity.WarehouseId,
                    WarehouseName = warehouseName, ProductId = d.ProductId, ProductCode = d.ProductCode,
                    ProductName = d.ProductName, Spec = d.Spec, Unit = d.Unit,
                    MovementDate = entity.AdjustmentDate,
                    Remark = $"盘点差异：账面 {d.BookQuantity}，实盘 {d.ActualQuantity}"
                };

                if (diff > 0) await inventory.IncreaseAsync(context, diff, d.UnitCost);
                else await inventory.DecreaseAsync(context, -diff, d.UnitCost);
            }

            entity.Status = DocumentStatus.Approved;
            entity.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            await transaction.RollbackAsync();
            return (false, ex.Message);
        }
    }

    /// <summary>完整复刻 <c>StockAdjustmentController.Cancel</c> 的协议（同一把单据行锁 + 可串行化事务）。</summary>
    private async Task<(bool Success, string Error)> TryCancelAsync(long adjustmentId, long? userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.StockAdjustments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", adjustmentId)
                .ToListAsync();
            if (ids.Count == 0) throw BusinessException.NotFound("盘点单不存在");

            await StockAdjustmentPostingRules.EnsureMenuAuthorizedAsync(db, userId);

            var entity = await db.StockAdjustments.SingleAsync(o => o.Id == adjustmentId && !o.IsDeleted);
            if (entity.Status == DocumentStatus.Approved)
                throw BusinessException.RuleConflict("已审核的盘点单不能取消，请先销审");

            entity.Status = DocumentStatus.Cancelled;
            entity.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            await transaction.RollbackAsync();
            return (false, ex.Message);
        }
    }

    /// <summary>按确定性顺序（仓库 Id 升序 → 商品 Id 升序）对受影响库存行加 UPDLOCK/HOLDLOCK。</summary>
    private static async Task LockStockRowsAsync(ErpDbContext db, long warehouseId,
        IEnumerable<StockAdjustmentDetail> details)
    {
        var keys = InventoryService.OrderStockIdentities(
            StockAdjustmentPostingRules.StockKeys(warehouseId, details));
        foreach (var key in keys)
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.Stocks WITH (UPDLOCK, HOLDLOCK) WHERE WarehouseId = {0} AND ProductId = {1}",
                key.WarehouseId, key.ProductId).ToListAsync();
        }
    }

    private static async Task<string> WarehouseNameAsync(ErpDbContext db, long warehouseId)
        => await db.BaseWarehouses.Where(w => w.Id == warehouseId).Select(w => w.WarehouseName).FirstOrDefaultAsync()
           ?? string.Empty;

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"PDW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"PDP-{Tag()}", ProductName = name, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity,
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
        await db.SaveChangesAsync();
    }

    private static async Task<StockAdjustment> SeedAdjustmentAsync(ErpDbContext db, long warehouseId,
        DocumentStatus status,
        params (long ProductId, decimal BookQuantity, decimal ActualQuantity, decimal UnitCost)[] lines)
    {
        var adjustment = new StockAdjustment
        {
            AdjustmentNo = $"PD-IT-{Tag()}",
            AdjustmentDate = DateTime.Today,
            WarehouseId = warehouseId,
            AdjustType = "盘点调整",
            Status = status
        };
        db.StockAdjustments.Add(adjustment);
        await db.SaveChangesAsync();

        var sortNo = 0;
        foreach (var (productId, bookQuantity, actualQuantity, unitCost) in lines)
        {
            var diff = actualQuantity - bookQuantity;
            db.StockAdjustmentDetails.Add(new StockAdjustmentDetail
            {
                StockAdjustmentId = adjustment.Id,
                AdjustmentNo = adjustment.AdjustmentNo,
                SortNo = ++sortNo,
                ProductId = productId,
                ProductName = $"P{productId}",
                Spec = "规格A",
                Unit = "PCS",
                BookQuantity = bookQuantity,
                ActualQuantity = actualQuantity,
                UnitCost = unitCost,
                DiffQuantity = diff,
                DiffAmount = Math.Round(diff * unitCost, 4)
            });
        }
        await db.SaveChangesAsync();

        adjustment.TotalDiffQuantity = lines.Sum(l => l.ActualQuantity - l.BookQuantity);
        adjustment.TotalDiffAmount = lines.Sum(l => Math.Round((l.ActualQuantity - l.BookQuantity) * l.UnitCost, 4));
        await db.SaveChangesAsync();
        return adjustment;
    }

    /// <summary>播种角色 + 账号，并按需授予既有 stock-query 菜单（种子数据已含该菜单），返回用户 Id。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, bool withMenu, bool enabled)
    {
        var role = new SysRole { RoleCode = $"PD-POST-{Tag()}", RoleName = "盘点操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"pd-post-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "盘点操作员",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menu = await db.SysMenus.FirstAsync(m =>
                m.MenuCode == StockAdjustmentPostingRules.RequiredMenuCode && !m.IsDeleted);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }
        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 夹具：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>
/// 时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，发现同名库已存在立即拒绝，绝不 drop / reset / 复用。
/// </summary>
public sealed class StockAdjustmentPostingSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKADJUSTMENTPOSTING_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-355] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
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

    private async Task ResetToFullDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
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

        Console.WriteLine("[ERP-355] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockAdjustmentPostingTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => StockAdjustmentPostingSqlServerFixture.AssertDedicatedTarget(connection));
}
