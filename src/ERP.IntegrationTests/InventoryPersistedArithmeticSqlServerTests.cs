using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-527 库存增减 / 冲销「持久化算术」边界的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认库存行数量 / 可用数量
/// <c>decimal(18,2)</c>（EF 默认精度）、金额 <c>decimal(18,4)</c>、成本 <c>decimal(18,6)</c>，
/// 库存流水数量 / 金额 / 结存 <c>decimal(18,4)</c>、单价 / 结存均价 <c>decimal(18,6)</c>；</item>
/// <item>结存数量精确上限在真实列上原样往返；「上限 + 一个最小单位（0.01）」与更细小数位 / 越界数量
/// 在写入之前受控拒绝（零库存变更、零新增流水，用全新上下文复核）；</item>
/// <item>调用方事务下的多步 / 多行记账：后一行越界时**整体回滚**，先前已写入的库存与流水都不落库；</item>
/// <item>冲销溢出受控拒绝且原流水 <c>IsReversed</c>、库存与红字流水全部不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class InventoryPersistedArithmeticSqlServerTests
    : IClassFixture<InventoryPersistedArithmeticSqlServerFixture>
{
    private readonly InventoryPersistedArithmeticSqlServerFixture _fixture;

    public InventoryPersistedArithmeticSqlServerTests(InventoryPersistedArithmeticSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(InventoryPersistedArithmeticSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static readonly decimal MaxStockQuantity = InventoryPersistedArithmeticRules.MaxStockQuantityValue;

    /// <summary>
    /// 结存数量的实际可落库上界：库存行数量列 <c>decimal(18,2)</c> 与库存流水结存数量快照列
    /// <c>decimal(18,4)</c> 的交集（小数位 2 位）。
    /// </summary>
    private static readonly decimal MaxBalanceQuantity = 99_999_999_999_999.99m;

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,2)", ColumnType(db, typeof(Stock), nameof(Stock.Quantity)));
        Assert.Equal("decimal(18,2)", ColumnType(db, typeof(Stock), nameof(Stock.AvailableQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(Stock), nameof(Stock.TotalCost)));
        Assert.Equal("decimal(18,6)", ColumnType(db, typeof(Stock), nameof(Stock.AverageCost)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(StockMovement), nameof(StockMovement.Quantity)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(StockMovement), nameof(StockMovement.Amount)));
        Assert.Equal("decimal(18,6)", ColumnType(db, typeof(StockMovement), nameof(StockMovement.UnitCost)));

        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "Stocks", "Quantity"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "Stocks", "AvailableQuantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "Stocks", "TotalCost"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "Stocks", "AverageCost"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockMovements", "Quantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockMovements", "Amount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockMovements", "BalanceQuantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockMovements", "BalanceAmount"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "StockMovements", "UnitCost"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "StockMovements", "BalanceAverageCost"));

        Assert.Equal(2, InventoryPersistedArithmeticRules.StockQuantityScale);
        Assert.Equal(4, InventoryPersistedArithmeticRules.MovementScale);
        Assert.Equal(6, InventoryPersistedArithmeticRules.CostScale);
        Assert.Equal(9_999_999_999_999_999.99m, InventoryPersistedArithmeticRules.MaxStockQuantityValue);

        Console.WriteLine("[ERP-527] 实际列精度与规则常量一致（Stocks.Quantity decimal(18,2) 等）。");
    }

    // ==================== 2. 结存数量边界：精确上限原样往返 + 最小单位拒绝 ====================

    [Fact]
    public async Task Increase_to_exact_max_round_trips_and_max_plus_quantum_is_rejected()
    {
        Guard();
        long warehouseId, productId;
        await using (var seed = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(seed, "边界仓");
            productId = await SeedProductAsync(seed, "边界商品");
            await SeedStockAsync(seed, warehouseId, productId, MaxBalanceQuantity - 100m, 0m);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var service = new InventoryService(db);
            await service.IncreaseAsync(Context(warehouseId, productId, "边界商品"), 100m, 0m);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stock = await db.Stocks.AsNoTracking()
                .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
            Assert.Equal(MaxBalanceQuantity, stock.Quantity);
            var movement = await db.StockMovements.AsNoTracking()
                .SingleAsync(m => m.WarehouseId == warehouseId && m.ProductId == productId);
            Assert.Equal(MaxBalanceQuantity, movement.BalanceQuantity);

            // 上限 + 一个最小单位（0.01）：在真实 SQL 写入之前受控拒绝
            var service = new InventoryService(db);
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                service.IncreaseAsync(Context(warehouseId, productId, "边界商品"), 0.01m, 0m));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(MaxBalanceQuantity, await db.Stocks
                .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1, await db.StockMovements
                .CountAsync(m => m.WarehouseId == warehouseId && m.ProductId == productId));
        }

        Console.WriteLine("[ERP-527] 结存数量精确上限原样往返、上限 + 最小单位零写入。");
    }

    // ==================== 3. 入参不可表示：零库存 / 零流水 ====================

    [Theory]
    [InlineData("quantity-over-scale")]   // 1.00001：DECIMAL(18,4) 过精度
    [InlineData("quantity-over-max")]     // 1e14：超过 DECIMAL(18,4) 上限
    [InlineData("cost-over-max")]         // 1e12：超过 DECIMAL(18,6) 上限
    public async Task Unrepresentable_input_is_rejected_before_any_write(string scenario)
    {
        Guard();
        long warehouseId, productId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "输入仓");
            productId = await SeedProductAsync(db, "输入商品");
        }

        var (quantity, cost) = scenario switch
        {
            "quantity-over-scale" => (1.00001m, 1m),
            "quantity-over-max" => (100_000_000_000_000m, 1m),
            _ => (1m, 1_000_000_000_000m)
        };

        await using (var db = _fixture.CreateDbContext())
        {
            var service = new InventoryService(db);
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                service.IncreaseAsync(Context(warehouseId, productId, "输入商品"), quantity, cost));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.Stocks
                .CountAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId));
            Assert.Equal(0, await verify.StockMovements
                .CountAsync(m => m.WarehouseId == warehouseId && m.ProductId == productId));
        }
    }

    // ==================== 4. 多行记账：后续行越界时先前已落库的行一并回滚 ====================

    [Fact]
    public async Task Multirow_posting_rolls_back_previous_rows_when_a_later_balance_overflows()
    {
        Guard();
        long fromId, toId, productId;
        await using (var seed = _fixture.CreateDbContext())
        {
            fromId = await SeedWarehouseAsync(seed, "整单来源仓");
            toId = await SeedWarehouseAsync(seed, "整单目标仓");
            productId = await SeedProductAsync(seed, "整单商品");
            await SeedStockAsync(seed, fromId, productId, quantity: 100m, totalCost: 1000m);
            await SeedStockAsync(seed, toId, productId, quantity: MaxBalanceQuantity - 20m, totalCost: 0m);
        }

        // 与调用方（调拨 / 盘点 / 退货控制器）相同的协议：可串行化事务内逐行记账，
        // 第 1 行先落库（SaveChanges），第 2 行把目标仓结存推过流水结存快照列上限 → 整体回滚。
        await using (var db = _fixture.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var service = new InventoryService(db);
            var rejected = false;
            try
            {
                await service.DecreaseAsync(
                    Context(fromId, productId, "整单商品", InventoryMovementType.TransferOut), 10m, 10m);
                await service.IncreaseAsync(
                    Context(toId, productId, "整单商品", InventoryMovementType.TransferIn), 10m, 10m);
                await db.SaveChangesAsync();     // 第 1 行已真实落入 SQL（随后必须被回滚）

                var ex = await Assert.ThrowsAsync<BusinessException>(() => service.IncreaseAsync(
                    Context(toId, productId, "整单商品", InventoryMovementType.TransferIn), 20m, 10m));
                Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
                rejected = true;
            }
            finally
            {
                // 与调用方一致：任一行失败即整体回滚（先前已落库的行一并撤销，绝不提交半截单据）。
                await transaction.RollbackAsync();
            }

            Assert.True(rejected);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(100m, await verify.Stocks
                .Where(s => s.WarehouseId == fromId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1000m, await verify.Stocks
                .Where(s => s.WarehouseId == fromId && s.ProductId == productId)
                .Select(s => s.TotalCost).SingleAsync());
            Assert.Equal(MaxBalanceQuantity - 20m, await verify.Stocks
                .Where(s => s.WarehouseId == toId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(0, await verify.StockMovements
                .CountAsync(m => m.WarehouseId == fromId || m.WarehouseId == toId));
        }

        Console.WriteLine("[ERP-527] 多行记账在后续行越界时整体回滚（先前已落库的行一并撤销）。");
    }

    // ==================== 5. 冲销溢出：原流水 / 库存 / 红字流水全部不变 ====================

    [Fact]
    public async Task Reverse_overflow_is_rejected_and_leaves_movement_and_stock_untouched()
    {
        Guard();
        long warehouseId, productId;
        await using (var seed = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(seed, "冲销仓");
            productId = await SeedProductAsync(seed, "冲销商品");
            await SeedStockAsync(seed, warehouseId, productId, MaxStockQuantity, 0m);

            // 历史出库流水（真实列 decimal(18,4) / decimal(18,6) 内可表示）：冲销要把 0.01 退回库存 →
            // 结存越过 DECIMAL(18,2) 上限，必须在写入红字流水之前受控拒绝。
            seed.StockMovements.Add(new StockMovement
            {
                MovementDate = DateTime.Today,
                MovementType = InventoryMovementType.Adjustment,
                SourceDocType = "StockAdjustment",
                SourceDocId = 527L,
                SourceDocNo = "PD-ERP527-SQL",
                WarehouseId = warehouseId,
                WarehouseName = "冲销仓",
                ProductId = productId,
                ProductCode = "ERP527-SQL",
                ProductName = "冲销商品",
                Spec = "规格A",
                Unit = "PCS",
                Direction = -1,
                Quantity = 0.01m,
                UnitCost = 0m,
                Amount = -0.01m,
                BalanceQuantity = 0m,
                BalanceAmount = 0m,
                BalanceAverageCost = 0m,
                CreatedAt = DateTime.Now
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var service = new InventoryService(db);
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                service.ReverseAsync("StockAdjustment", 527L, "ERP-527 集成冲销"));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Equal(0, await db.StockMovements.CountAsync(m => m.IsReversal));
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(MaxStockQuantity, await verify.Stocks
                .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.False(await verify.StockMovements
                .Where(m => m.SourceDocType == "StockAdjustment" && m.SourceDocId == 527L)
                .Select(m => m.IsReversed).SingleAsync());
            Assert.Equal(1, await verify.StockMovements.CountAsync(m => !m.IsReversal));
            Assert.Equal(0, await verify.StockMovements.CountAsync(m => m.IsReversal));
        }

        Console.WriteLine("[ERP-527] 冲销溢出受控拒绝：库存、原流水标记与红字流水全部不变。");
    }

    // ==================== 测试辅助 ====================

    private static InventoryMovementContext Context(long warehouseId, long productId, string productName,
        InventoryMovementType movementType = InventoryMovementType.Adjustment) => new()
    {
        SourceDocType = "StockAdjustment",
        SourceDocId = 527L,
        SourceDocNo = "PD-ERP527-SQL",
        MovementType = movementType,
        WarehouseId = warehouseId,
        WarehouseName = "集成仓",
        ProductId = productId,
        ProductCode = "ERP527-SQL",
        ProductName = productName,
        Spec = "规格A",
        Unit = "PCS",
        MovementDate = DateTime.Today,
        Remark = "ERP-527_SQL"
    };

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse
        {
            WarehouseCode = $"ERP527-{Tag()}",
            WarehouseName = $"{name}-{Tag()}",
            Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"ERP527-{Tag()}",
            ProductName = $"{name}-{Tag()}",
            Unit = "PCS",
            Status = 1
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
            AverageCost = quantity > 0 ? InventoryService.RoundCost(totalCost / quantity) : 0m
        });
        await db.SaveChangesAsync();
    }

    private static string ColumnType(ErpDbContext db, Type entityType, string propertyName)
        => db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}


// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-527）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class InventoryPersistedArithmeticSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_INVENTORYARITH_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-527] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
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

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

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

        Console.WriteLine("[ERP-527] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}



/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class InventoryPersistedArithmeticTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            InventoryPersistedArithmeticSqlServerFixture.AssertDedicatedTarget(connection));
}


