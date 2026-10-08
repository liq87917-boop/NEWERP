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
/// ERP-343 销售出库衔接来源订单并防止累计超发 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="StockOutOrderFulfillmentRules"/> 做真实 SQL Server 验证：非权威链接不做累计校验、
/// 部分 / 满量批次累计、累计超限拒绝、取消释放额度、包装单位折算、以及同一订单并发审核经
/// UPDLOCK/HOLDLOCK 串行化后不会超发。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockOutOrderFulfillmentSqlServerTests
    : IClassFixture<StockOutOrderFulfillmentSqlServerFixture>
{
    private readonly StockOutOrderFulfillmentSqlServerFixture _fixture;

    private const long CustomerA = 943001L;
    private const long CustomerB = 943002L;
    private const long WarehouseA = 943101L;
    private const long ProductA = 943201L;

    public StockOutOrderFulfillmentSqlServerTests(StockOutOrderFulfillmentSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    [Fact]
    public async Task Approval_NonAuthoritativeCustomer_NoCumulativeEnforcement()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        SeedProduct(db, ProductA, "INT_TEST_FULFILL_LINK_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "INT_TEST_FULFILL_LINK_SO", CustomerA, DocumentStatus.Approved,
            (ProductA, "PCS", 10m, 7.5m));

        var entity = SeedStockOut(db, "INT_TEST_FULFILL_LINK_SO", order.Id, CustomerB, DocumentStatus.Submitted,
            (ProductA, "PCS", 6m));

        // 客户不一致 → 非权威链接：不做累计校验，不抛异常
        await StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id));
    }

    [Fact]
    public async Task Approval_PartialThenFull_Allowed_ThenOverrun_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        SeedProduct(db, ProductA, "INT_TEST_FULFILL_CUM_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "INT_TEST_FULFILL_CUM_SO", CustomerA, DocumentStatus.Approved,
            (ProductA, "PCS", 10m, 7.5m));

        SeedStockOut(db, "INT_TEST_FULFILL_CUM_SO_A", order.Id, CustomerA, DocumentStatus.Approved,
            (ProductA, "PCS", 6m));
        var partial = SeedStockOut(db, "INT_TEST_FULFILL_CUM_SO_B", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 4m));

        await StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, partial.Id));

        var over = SeedStockOut(db, "INT_TEST_FULFILL_CUM_SO_C", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 1m));
        var overEntity = await ReloadAsync(db, over.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, overEntity));
        Assert.Contains("超过来源销售订单授权数量 10", ex.Message);
    }

    [Fact]
    public async Task Approval_CancelledDocument_ReleasesRemainingCapacity()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        SeedProduct(db, ProductA, "INT_TEST_FULFILL_CANCEL_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "INT_TEST_FULFILL_CANCEL_SO", CustomerA, DocumentStatus.Approved,
            (ProductA, "PCS", 10m, 7.5m));

        var cancelled = SeedStockOut(db, "INT_TEST_FULFILL_CANCEL_SO_A", order.Id, CustomerA,
            DocumentStatus.Approved, (ProductA, "PCS", 6m));
        cancelled.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var full = SeedStockOut(db, "INT_TEST_FULFILL_CANCEL_SO_B", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 10m));

        // 已取消的单据不计入已审核数量，10 ≤ 10 通过
        await StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, full.Id));
    }

    [Fact]
    public async Task Approval_PackagingOrderLine_ConvertsToBaseUnit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        SeedProduct(db, ProductA, "INT_TEST_FULFILL_PKG_PROD", "商品A", "PCS", "CTN", 12);
        var order = SeedOrder(db, "INT_TEST_FULFILL_PKG_SO", CustomerA, DocumentStatus.Approved,
            (ProductA, "CTN", 1m, 90m));

        var full = SeedStockOut(db, "INT_TEST_FULFILL_PKG_SO_A", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 12m));
        await StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, full.Id));

        var over = SeedStockOut(db, "INT_TEST_FULFILL_PKG_SO_B", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 1m));
        var overEntity = await ReloadAsync(db, over.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, overEntity));
        Assert.Contains("超过来源销售订单授权数量 12", ex.Message);
    }

    [Fact]
    public async Task ConcurrentApprovals_SerializedByOrderLock_OneOverrunBlocked()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        SeedProduct(seed, ProductA, "INT_TEST_FULFILL_CONC_PROD", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(seed, "INT_TEST_FULFILL_CONC_SO", CustomerA, DocumentStatus.Approved,
            (ProductA, "PCS", 10m, 7.5m));
        var docA = SeedStockOut(seed, "INT_TEST_FULFILL_CONC_SO_A", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 6m));
        var docB = SeedStockOut(seed, "INT_TEST_FULFILL_CONC_SO_B", order.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, "PCS", 6m));

        var results = await Task.WhenAll(TryApproveAsync(docA.Id, order.Id), TryApproveAsync(docB.Id, order.Id));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.Error.Contains("超过来源销售订单授权数量")));
    }

    private async Task<(bool Success, string Error)> TryApproveAsync(long stockOutId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", orderId)
                .ToListAsync();
            if (ids.Count == 0)
                throw BusinessException.RuleConflict("来源销售订单不存在或已删除");

            var entity = await db.StockOuts.Include(o => o.Details).SingleAsync(s => s.Id == stockOutId);
            await StockOutOrderFulfillmentRules.ValidateApprovalAsync(db, entity);

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

    private static async Task<StockOut> ReloadAsync(ErpDbContext db, long id)
        => await db.StockOuts.Include(o => o.Details).AsNoTracking().SingleAsync(s => s.Id == id);



    private static void SeedProduct(ErpDbContext db, long id, string code, string name, string unit,
        string packageUnit, int unitsPerPackage)
    {
        if (db.BaseProducts.Any(p => p.Id == id)) return;
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage
        });
        db.SaveChanges();
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, quantity, unitPrice) in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
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
        order.TotalAmount = db.SalesOrderDetails.Where(d => d.SalesOrderId == order.Id).Sum(d => d.Amount);
        db.SaveChanges();
        return order;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId, long customerId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = DateTime.Today,
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            Status = status
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, unit, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = unit,
                Quantity = quantity
            });
        }
        db.SaveChanges();
        stockOut.TotalQuantity = db.StockOutDetails.Where(d => d.StockOutId == stockOut.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockOut;
    }
}



/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// </summary>
public sealed class StockOutOrderFulfillmentSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKOUTFULFILLMENT_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-343] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};Integrated Security=true;TrustServerCertificate=true;";

    private static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Contains(InstanceMarker, server, StringComparison.OrdinalIgnoreCase);
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
            cmd.CommandText = $@"
IF DB_ID(N'{QuoteSqlString(database)}') IS NOT NULL
BEGIN
    ALTER DATABASE {QuoteSqlIdentifier(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {QuoteSqlIdentifier(database)};
END";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-343] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }

    private static string QuoteSqlString(string value) => value.Replace("'", "''");

    private static string QuoteSqlIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";
}

