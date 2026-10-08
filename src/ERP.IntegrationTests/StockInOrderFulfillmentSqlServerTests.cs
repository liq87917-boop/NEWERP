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
/// ERP-342 采购入库衔接来源订单并防止累计超收 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="StockInOrderFulfillmentRules"/> 做真实 SQL Server 验证：非权威链接不做累计校验、
/// 部分 / 满量批次累计、累计超限拒绝、取消释放额度、包装单位折算、以及同一订单并发审核经
/// UPDLOCK/HOLDLOCK 串行化后不会超收。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockInOrderFulfillmentSqlServerTests
    : IClassFixture<StockInOrderFulfillmentSqlServerFixture>
{
    private readonly StockInOrderFulfillmentSqlServerFixture _fixture;

    private const long SupplierA = 942001L;
    private const long SupplierB = 942002L;
    private const long WarehouseA = 942101L;
    private const long ProductA = 942201L;

    public StockInOrderFulfillmentSqlServerTests(StockInOrderFulfillmentSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    [Fact]
    public async Task Approval_NonAuthoritativeSupplier_NoCumulativeEnforcement()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_FULFILL_LINK_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "INT_TEST_FULFILL_LINK_PO", SupplierA, DocumentStatus.Approved,
            (productId, "PCS", 10m, 7.5m));

        var entity = SeedStockIn(db, "INT_TEST_FULFILL_LINK_SI", order.Id, SupplierB, DocumentStatus.Submitted,
            (productId, "PCS", 6m));

        // 供应商不一致 → 非权威链接：不做累计校验，不抛异常（与 ERP-033 成本回退同一口径）
        await StockInOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id));
    }

    [Fact]
    public async Task Approval_PartialThenFull_Allowed_ThenOverrun_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_FULFILL_CUM_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "INT_TEST_FULFILL_CUM_PO", SupplierA, DocumentStatus.Approved,
            (productId, "PCS", 10m, 7.5m));

        SeedStockIn(db, "INT_TEST_FULFILL_CUM_SI_A", order.Id, SupplierA, DocumentStatus.Approved,
            (productId, "PCS", 6m));
        var partial = SeedStockIn(db, "INT_TEST_FULFILL_CUM_SI_B", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 4m));

        await StockInOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, partial.Id));
        partial.Status = DocumentStatus.Approved;
        await db.SaveChangesAsync();

        var over = SeedStockIn(db, "INT_TEST_FULFILL_CUM_SI_C", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 1m));
        var overEntity = await ReloadAsync(db, over.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            StockInOrderFulfillmentRules.ValidateApprovalAsync(db, overEntity));
        Assert.Contains("超过来源采购订单授权数量 10", ex.Message);
    }

    [Fact]
    public async Task Approval_CancelledDocument_ReleasesRemainingCapacity()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_FULFILL_CANCEL_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "INT_TEST_FULFILL_CANCEL_PO", SupplierA, DocumentStatus.Approved,
            (productId, "PCS", 10m, 7.5m));

        var cancelled = SeedStockIn(db, "INT_TEST_FULFILL_CANCEL_SI_A", order.Id, SupplierA,
            DocumentStatus.Approved, (productId, "PCS", 6m));
        cancelled.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var full = SeedStockIn(db, "INT_TEST_FULFILL_CANCEL_SI_B", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 10m));

        // 已取消的单据不计入已审核数量，10 ≤ 10 通过
        await StockInOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, full.Id));
    }

    [Fact]
    public async Task Approval_PackagingOrderLine_ConvertsToBaseUnit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_FULFILL_PKG_PROD", "商品A", "PCS", "CTN", 12);
        var order = SeedOrder(db, "INT_TEST_FULFILL_PKG_PO", SupplierA, DocumentStatus.Approved,
            (productId, "CTN", 1m, 90m));

        var full = SeedStockIn(db, "INT_TEST_FULFILL_PKG_SI_A", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 12m));
        await StockInOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, full.Id));
        full.Status = DocumentStatus.Approved;
        await db.SaveChangesAsync();

        var over = SeedStockIn(db, "INT_TEST_FULFILL_PKG_SI_B", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 1m));
        var overEntity = await ReloadAsync(db, over.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            StockInOrderFulfillmentRules.ValidateApprovalAsync(db, overEntity));
        Assert.Contains("超过来源采购订单授权数量 12", ex.Message);
    }

    [Fact]
    public async Task ConcurrentApprovals_SerializedByOrderLock_OneOverrunBlocked()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var productId = SeedProduct(seed, "INT_TEST_FULFILL_CONC_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(seed, "INT_TEST_FULFILL_CONC_PO", SupplierA, DocumentStatus.Approved,
            (productId, "PCS", 10m, 7.5m));
        var docA = SeedStockIn(seed, "INT_TEST_FULFILL_CONC_SI_A", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 6m));
        var docB = SeedStockIn(seed, "INT_TEST_FULFILL_CONC_SI_B", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, "PCS", 6m));

        var results = await Task.WhenAll(TryApproveAsync(docA.Id, order.Id), TryApproveAsync(docB.Id, order.Id));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.Error.Contains("超过来源采购订单授权数量")));
    }

    private async Task<(bool Success, string Error)> TryApproveAsync(long stockInId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", orderId)
                .ToListAsync();
            if (ids.Count == 0)
                throw BusinessException.RuleConflict("来源采购订单不存在或已删除");

            var entity = await db.StockIns.Include(o => o.Details).SingleAsync(s => s.Id == stockInId);
            await StockInOrderFulfillmentRules.ValidateApprovalAsync(db, entity);

            entity.Status = DocumentStatus.Approved;
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

    private static async Task<StockIn> ReloadAsync(ErpDbContext db, long id)
        => await db.StockIns.Include(o => o.Details).AsNoTracking().SingleAsync(s => s.Id == id);


    private static long SeedProduct(ErpDbContext db, string code, string name, string unit,
        string packageUnit, int unitsPerPackage)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = status
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, quantity, unitPrice) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity,
                UnitPrice = unitPrice,
                Amount = quantity * unitPrice
            });
        }
        db.SaveChanges();
        order.TotalAmount = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).Sum(d => d.Amount);
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId, long supplierId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplierId,
            WarehouseId = WarehouseA,
            Status = status
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        foreach (var (productId, unit, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = stockIn.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = unit,
                Quantity = quantity
            });
        }
        db.SaveChanges();
        stockIn.TotalQuantity = db.StockInDetails.Where(d => d.StockInId == stockIn.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockIn;
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// </summary>
public sealed class StockInOrderFulfillmentSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKINFULFILLMENT_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-342] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

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
                throw new InvalidOperationException("The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-342] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }

    private static string QuoteSqlString(string value) => value.Replace("'", "''");

    private static string QuoteSqlIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";
}


public sealed class StockInOrderFulfillmentTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => StockInOrderFulfillmentSqlServerFixture.AssertDedicatedTarget(connection));
}
