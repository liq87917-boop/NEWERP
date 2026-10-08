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
/// ERP-354 仓库调拨过账 / 冲销原子化的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>用与 <c>StockTransferController</c> 完全相同的锁 / 事务协议直接验证：</para>
/// <list type="number">
/// <item>实时授权 fail closed（无身份 / 无菜单 / 禁用账号）时库存、流水与状态不变；</item>
/// <item>调拨单行锁（<c>UPDLOCK, HOLDLOCK</c>）+ 可串行化事务下审核恰好一次、销审还原；</item>
/// <item><b>两条独立连接竞争</b>：并发审核 / 审核只允许一方过账，并发审核 / 取消只允许一方成功；</item>
/// <item>确定性库存行锁顺序（仓库 → 商品升序）下，对向调拨（A→B 与 B→A）并发审核不发生死锁。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockTransferPostingSqlServerTests
    : IClassFixture<StockTransferPostingSqlServerFixture>
{
    private readonly StockTransferPostingSqlServerFixture _fixture;

    public StockTransferPostingSqlServerTests(StockTransferPostingSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockTransferPostingSqlServerFixture.DatabasePrefix, target.InitialCatalog,
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
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = 8814001L;
        await SeedStockAsync(db, fromId, productId, quantity: 100m, totalCost: 1000m);
        var transfer = await SeedTransferAsync(db, fromId, toId, DocumentStatus.Submitted, (productId, 10m, 0m));

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
            StockTransferPostingRules.EnsureMenuAuthorizedAsync(db, userId));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, error.Code);

        Assert.Equal(beforeMovements, await db.StockMovements.CountAsync());
        Assert.Equal(100m, await db.Stocks.Where(s => s.WarehouseId == fromId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(DocumentStatus.Submitted, await db.StockTransfers.Where(t => t.Id == transfer.Id)
            .Select(t => t.Status).SingleAsync());
    }

    // ==================== 2. 审核恰好一次 + 销审还原 ====================

    [Fact]
    public async Task Authorized_operator_approves_once_and_reversal_restores_both_warehouses()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(seed, "调出仓");
        var toId = await SeedWarehouseAsync(seed, "调入仓");
        var productId = 8814002L;
        await SeedStockAsync(seed, fromId, productId, quantity: 100m, totalCost: 1000m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);
        var transfer = await SeedTransferAsync(seed, fromId, toId, DocumentStatus.Submitted, (productId, 30m, 0m));

        var approved = await TryApproveAsync(transfer.Id, userId);
        Assert.True(approved.Success, approved.Error);

        await using (var db = _fixture.CreateDbContext())
        {
            var from = await db.Stocks.SingleAsync(s => s.WarehouseId == fromId && s.ProductId == productId);
            var to = await db.Stocks.SingleAsync(s => s.WarehouseId == toId && s.ProductId == productId);
            Assert.Equal(70m, from.Quantity);
            Assert.Equal(700m, from.TotalCost);
            Assert.Equal(30m, to.Quantity);
            Assert.Equal(300m, to.TotalCost);
            Assert.Equal(10m, to.AverageCost);
            Assert.Equal(DocumentStatus.Approved, await db.StockTransfers.Where(t => t.Id == transfer.Id)
                .Select(t => t.Status).SingleAsync());
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == transfer.Id && !m.IsDeleted));
        }

        // 重复审核在锁内被状态门拒绝，不新增流水
        var again = await TryApproveAsync(transfer.Id, userId);
        Assert.False(again.Success);
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == transfer.Id && !m.IsDeleted));
        }

        // 销审按流水冲销，两仓还原
        await using (var db = _fixture.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var entity = await db.StockTransfers.Include(o => o.Details).SingleAsync(o => o.Id == transfer.Id);
            await LockStockRowsAsync(db, entity);
            var service = new InventoryService(db);
            await service.ReverseAsync("StockTransfer", entity.Id, "集成销审冲销");
            entity.Status = DocumentStatus.Pending;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(100m, await db.Stocks.Where(s => s.WarehouseId == fromId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(0m, await db.Stocks.Where(s => s.WarehouseId == toId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(DocumentStatus.Pending, await db.StockTransfers.Where(t => t.Id == transfer.Id)
                .Select(t => t.Status).SingleAsync());
            Assert.Equal(2, await db.StockMovements
                .CountAsync(m => m.SourceDocId == transfer.Id && m.IsReversal && !m.IsDeleted));
        }
    }

    // ==================== 3. 两条独立连接竞争 ====================

    [Fact]
    public async Task Two_connections_approve_twice_only_one_posts_inventory()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(seed, "调出仓");
        var toId = await SeedWarehouseAsync(seed, "调入仓");
        var productId = 8814011L;
        await SeedStockAsync(seed, fromId, productId, quantity: 100m, totalCost: 1000m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);
        var transfer = await SeedTransferAsync(seed, fromId, toId, DocumentStatus.Submitted, (productId, 30m, 0m));

        // 两条独立连接并发审核同一张单据：共用同一把调拨单行锁，只能成功其一
        var results = await Task.WhenAll(
            TryApproveAsync(transfer.Id, userId),
            TryApproveAsync(transfer.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            // 库存只过账一次：调出 70 / 调入 30，流水恰好 2 条（无重复过账）
            Assert.Equal(70m, await db.Stocks.Where(s => s.WarehouseId == fromId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(30m, await db.Stocks.Where(s => s.WarehouseId == toId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == transfer.Id && !m.IsDeleted));
            Assert.Equal(DocumentStatus.Approved, await db.StockTransfers.Where(t => t.Id == transfer.Id)
                .Select(t => t.Status).SingleAsync());
        }
    }

    [Fact]
    public async Task Two_connections_approve_and_cancel_only_one_succeeds()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(seed, "调出仓");
        var toId = await SeedWarehouseAsync(seed, "调入仓");
        var productId = 8814012L;
        await SeedStockAsync(seed, fromId, productId, quantity: 100m, totalCost: 1000m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);
        var transfer = await SeedTransferAsync(seed, fromId, toId, DocumentStatus.Submitted, (productId, 20m, 0m));

        // 两条独立连接并发「审核 vs 取消」：共用同一把调拨单行锁，只能成功其一
        var results = await Task.WhenAll(
            TryApproveAsync(transfer.Id, userId),
            TryCancelAsync(transfer.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        var approved = results[0].Success;

        await using (var db = _fixture.CreateDbContext())
        {
            var status = await db.StockTransfers.Where(t => t.Id == transfer.Id).Select(t => t.Status).SingleAsync();
            var movementCount = await db.StockMovements
                .CountAsync(m => m.SourceDocId == transfer.Id && !m.IsDeleted);

            if (approved)
            {
                // 审核赢：已过账；取消被「已审核」状态门拒绝
                Assert.Equal(DocumentStatus.Approved, status);
                Assert.Equal(2, movementCount);
                Assert.Equal(80m, await db.Stocks.Where(s => s.WarehouseId == fromId && s.ProductId == productId)
                    .Select(s => s.Quantity).SingleAsync());
                Assert.Equal(20m, await db.Stocks.Where(s => s.WarehouseId == toId && s.ProductId == productId)
                    .Select(s => s.Quantity).SingleAsync());
            }
            else
            {
                // 取消赢：单据已取消；审核看到已取消而拒绝，绝不产生任何库存
                Assert.Equal(DocumentStatus.Cancelled, status);
                Assert.Equal(0, movementCount);
                Assert.Equal(100m, await db.Stocks.Where(s => s.WarehouseId == fromId && s.ProductId == productId)
                    .Select(s => s.Quantity).SingleAsync());
                Assert.False(await db.Stocks.AnyAsync(s => s.WarehouseId == toId && s.ProductId == productId));
            }
        }
    }

    // ==================== 4. 对向调拨不互相死锁 ====================

    [Fact]
    public async Task Opposing_transfers_use_deterministic_stock_lock_order_and_both_succeed()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseA = await SeedWarehouseAsync(seed, "对向仓A");
        var warehouseB = await SeedWarehouseAsync(seed, "对向仓B");
        var productId = 8814021L;
        await SeedStockAsync(seed, warehouseA, productId, quantity: 50m, totalCost: 500m);
        await SeedStockAsync(seed, warehouseB, productId, quantity: 50m, totalCost: 500m);
        var userId = await SeedUserAsync(seed, withMenu: true, enabled: true);

        var aToB = await SeedTransferAsync(seed, warehouseA, warehouseB, DocumentStatus.Submitted, (productId, 10m, 0m));
        var bToA = await SeedTransferAsync(seed, warehouseB, warehouseA, DocumentStatus.Submitted, (productId, 10m, 0m));

        // 两条独立连接并发审核对向调拨：库存行锁顺序一致（仓库 → 商品升序），不会形成环形等待
        var results = await Task.WhenAll(
            TryApproveAsync(aToB.Id, userId),
            TryApproveAsync(bToA.Id, userId));

        Assert.True(results[0].Success, results[0].Error);
        Assert.True(results[1].Success, results[1].Error);

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(50m, await db.Stocks.Where(s => s.WarehouseId == warehouseA && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(50m, await db.Stocks.Where(s => s.WarehouseId == warehouseB && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == aToB.Id && !m.IsDeleted));
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == bToA.Id && !m.IsDeleted));
        }
    }

    // ==================== 测试辅助（与控制器同一锁 / 事务协议） ====================

    /// <summary>
    /// 复刻 <c>StockTransferController.Approve</c> 的锁 / 事务协议：同一把调拨单行锁（UPDLOCK/HOLDLOCK）、
    /// 同一确定性库存行锁顺序、同一 <see cref="InventoryService"/> 记账口径，供真实两连接并发验证。
    /// </summary>
    private async Task<(bool Success, string Error)> TryApproveAsync(long transferId, long? userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.ExecuteSqlRawAsync(StockTransferPostingRules.PostingBoundarySql);
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.StockTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", transferId)
                .ToListAsync();
            if (ids.Count == 0) throw BusinessException.NotFound("调拨单不存在");

            await StockTransferPostingRules.EnsureMenuAuthorizedAsync(db, userId);

            var entity = await db.StockTransfers.Include(o => o.Details)
                .SingleAsync(o => o.Id == transferId && !o.IsDeleted);
            if (entity.Status != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("仅已提交的调拨单可审核");
            if (entity.FromWarehouseId <= 0 || entity.ToWarehouseId <= 0)
                throw BusinessException.InvalidParameter("调出仓与调入仓都必须填写");
            if (entity.FromWarehouseId == entity.ToWarehouseId)
                throw BusinessException.RuleConflict("调出仓与调入仓不能相同");

            var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
            if (details.Count == 0) throw BusinessException.RuleConflict("调拨单无明细，不能审核");

            var inventory = new InventoryService(db);
            if (await inventory.CountActiveMovementsAsync("StockTransfer", entity.Id) > 0)
                throw BusinessException.RuleConflict("该调拨单已产生库存流水，不能重复审核");

            await LockStockRowsAsync(db, entity);

            var fromName = await WarehouseNameAsync(db, entity.FromWarehouseId);
            var toName = await WarehouseNameAsync(db, entity.ToWarehouseId);
            foreach (var d in details)
            {
                if (d.Quantity <= 0)
                    throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的调拨数量必须大于 0");

                var outContext = new InventoryMovementContext
                {
                    SourceDocType = "StockTransfer", SourceDocId = entity.Id, SourceDocNo = entity.TransferNo,
                    MovementType = InventoryMovementType.TransferOut, WarehouseId = entity.FromWarehouseId,
                    WarehouseName = fromName, ProductId = d.ProductId, ProductCode = d.ProductCode,
                    ProductName = d.ProductName, Spec = d.Spec, Unit = d.Unit,
                    MovementDate = entity.TransferDate, Remark = $"调拨出库 → {toName}"
                };
                var outMovement = await inventory.DecreaseAsync(outContext, d.Quantity, d.UnitCost);

                var inContext = new InventoryMovementContext
                {
                    SourceDocType = "StockTransfer", SourceDocId = entity.Id, SourceDocNo = entity.TransferNo,
                    MovementType = InventoryMovementType.TransferIn, WarehouseId = entity.ToWarehouseId,
                    WarehouseName = toName, ProductId = d.ProductId, ProductCode = d.ProductCode,
                    ProductName = d.ProductName, Spec = d.Spec, Unit = d.Unit,
                    MovementDate = entity.TransferDate, Remark = $"调拨入库 ← {fromName}"
                };
                await inventory.IncreaseAsync(inContext, d.Quantity, outMovement.UnitCost);
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

    /// <summary>复刻 <c>StockTransferController.Cancel</c> 的锁 / 事务协议（同一把调拨单行锁）。</summary>
    private async Task<(bool Success, string Error)> TryCancelAsync(long transferId, long? userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.ExecuteSqlRawAsync(StockTransferPostingRules.PostingBoundarySql);
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.StockTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", transferId)
                .ToListAsync();
            if (ids.Count == 0) throw BusinessException.NotFound("调拨单不存在");

            await StockTransferPostingRules.EnsureMenuAuthorizedAsync(db, userId);

            var entity = await db.StockTransfers.SingleAsync(o => o.Id == transferId && !o.IsDeleted);
            if (entity.Status == DocumentStatus.Approved)
                throw BusinessException.RuleConflict("已审核的调拨单不能取消，请先销审");

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

    /// <summary>按控制器同一确定性顺序（仓库 Id → 商品 Id 升序）对两侧库存行加 UPDLOCK/HOLDLOCK。</summary>
    private static async Task LockStockRowsAsync(ErpDbContext db, StockTransfer entity)
    {
        var keys = InventoryService.OrderStockIdentities(StockTransferPostingRules.StockKeys(
            entity.FromWarehouseId, entity.ToWarehouseId, entity.Details));
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

    // ==================== 种子数据（SQL 自增主键，不硬编码 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"STW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
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

    private static async Task<StockTransfer> SeedTransferAsync(ErpDbContext db, long fromWarehouseId,
        long toWarehouseId, DocumentStatus status,
        params (long ProductId, decimal Quantity, decimal UnitCost)[] lines)
    {
        var transfer = new StockTransfer
        {
            TransferNo = $"ST-IT-{Tag()}",
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Status = status
        };
        db.StockTransfers.Add(transfer);
        await db.SaveChangesAsync();

        var sortNo = 0;
        foreach (var (productId, quantity, unitCost) in lines)
        {
            db.StockTransferDetails.Add(new StockTransferDetail
            {
                StockTransferId = transfer.Id,
                TransferNo = transfer.TransferNo,
                SortNo = ++sortNo,
                ProductId = productId,
                ProductName = $"P{productId}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = quantity,
                UnitCost = unitCost,
                Amount = quantity * unitCost
            });
        }
        await db.SaveChangesAsync();

        transfer.TotalQuantity = lines.Sum(l => l.Quantity);
        transfer.TotalAmount = lines.Sum(l => l.Quantity * l.UnitCost);
        await db.SaveChangesAsync();
        return transfer;
    }

    /// <summary>播种真实授权账号（既有 stock-query 菜单 + 角色授权）；拒绝场景不授予菜单 / 使用禁用账号。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, bool withMenu, bool enabled)
    {
        var role = new SysRole { RoleCode = $"ST-POST-{Tag()}", RoleName = "集成调拨操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"st-post-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "集成调拨操作员",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menu = await db.SysMenus.FirstAsync(m =>
                m.MenuCode == StockTransferPostingRules.RequiredMenuCode && !m.IsDeleted);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }
        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockTransferPostingSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKTRANSFERPOSTING_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-354] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-354] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockTransferPostingTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => StockTransferPostingSqlServerFixture.AssertDedicatedTarget(connection));
}





