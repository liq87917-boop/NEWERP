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
/// ERP-347 销售订单取消护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="SalesOrderCancellationRules"/> 做真实 SQL Server 验证：无履约依赖取消成功、
/// 已审核未冲销出库拒绝、已取消出库释放、未删除未取消采购履约拒绝、已取消采购释放、有效收款引用证据拒绝、
/// 作废证据释放，以及同一订单并发「出库审核 vs 来源取消」「收款引用登记 vs 来源取消」经 UPDLOCK/HOLDLOCK 串行化后只能成功其一。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class SalesOrderCancellationSqlServerTests
    : IClassFixture<SalesOrderCancellationSqlServerFixture>
{
    private readonly SalesOrderCancellationSqlServerFixture _fixture;

    private const long SupplierA = 943001L;
    private const long WarehouseA = 943101L;
    private const long ProductA = 943201L;
    private const long CustomerA = 943301L;

    public SalesOrderCancellationSqlServerTests(SalesOrderCancellationSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    [Fact]
    public async Task Cancel_NoDependent_Succeeds()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_A", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
        var saved = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Cancelled, saved.Status);
        Assert.Equal(75m, saved.TotalAmount);
        Assert.Single(db.SalesOrderDetails.Where(d => d.SalesOrderId == order.Id && !d.IsDeleted));
    }

    [Fact]
    public async Task Cancel_ActiveApprovedStockOut_Refused()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD_B", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_B", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        SeedStockOut(db, "INT_TEST_SOCANCEL_OUT_B", order.Id, CustomerA, DocumentStatus.Approved, (productId, 6m));

        var result = await TryCancelAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("已审核且未冲销", result.Error);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ReversedStockOut_Released()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD_C", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_C", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        SeedStockOut(db, "INT_TEST_SOCANCEL_OUT_C", order.Id, CustomerA, DocumentStatus.Cancelled, (productId, 6m));

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ActiveProcurement_Refused()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD_D", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_D", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        SeedPurchaseOrder(db, "INT_TEST_SOCANCEL_PO_D", order.Id, DocumentStatus.Approved);

        var result = await TryCancelAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("采购订单", result.Error);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_CancelledProcurement_Released()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD_E", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_E", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        SeedPurchaseOrder(db, "INT_TEST_SOCANCEL_PO_E", order.Id, DocumentStatus.Cancelled);

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Cancel_ActiveReceiptAllocation_Refused()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD_F", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_F", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        var receipt = SeedFinanceReceipt(db, "INT_TEST_SOCANCEL_REC_F", CustomerA, 1000m, Currency.CNY);
        SeedReceiptAllocation(db, receipt, order, CustomerReceiptAllocationRules.StatusActive);

        var result = await TryCancelAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("收款引用", result.Error);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_VoidedReceiptAllocation_Released()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_SOCANCEL_PROD_G", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_SOCANCEL_SO_G", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        var receipt = SeedFinanceReceipt(db, "INT_TEST_SOCANCEL_REC_G", CustomerA, 1000m, Currency.CNY);
        SeedReceiptAllocation(db, receipt, order, CustomerReceiptAllocationRules.StatusVoided);

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Cancel_ApprovalVersusCancellation_Race_OnlyOneSucceeds()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var productId = SeedProduct(seed, "INT_TEST_SOCANCEL_PROD_H", "商品A", "PCS");
        var order = SeedOrder(seed, "INT_TEST_SOCANCEL_SO_H", CustomerA, DocumentStatus.Approved, Currency.CNY,
            (productId, 10m, 7.5m));
        var stockOut = SeedStockOut(seed, "INT_TEST_SOCANCEL_OUT_H", order.Id, CustomerA,
            DocumentStatus.Submitted, (productId, 6m));

        var results = await Task.WhenAll(
            TryApproveStockOutAsync(stockOut.Id, order.Id),
            TryCancelAsync(order.Id));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));
    }

    // ==================== 事务化助手（与控制器 / ERP-343 同源锁定口径） ====================

    private async Task<(bool Success, string Error)> TryCancelAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", orderId)
                .ToListAsync();

            var order = await db.SalesOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted)
                ?? throw BusinessException.NotFound("销售订单不存在");

            var adminId = await db.SysUsers.AsNoTracking()
                .Where(u => u.UserName == SeedData.AdminUserName)
                .Select(u => u.Id)
                .FirstAsync();
            await SalesOrderCancellationRules.ValidateCancellationAsync(db, order, adminId);

            order.Status = DocumentStatus.Cancelled;
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

    private async Task<(bool Success, string Error)> TryApproveStockOutAsync(long stockOutId, long orderId)
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

    // ==================== 种子助手（EF 生成身份键） ====================

    private static long SeedProduct(ErpDbContext db, string code, string name, string unit)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = string.Empty,
            UnitsPerPackage = 0
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, DocumentStatus status,
        Currency currency, params (long ProductId, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = currency,
            ExchangeRate = 1m,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = "PCS",
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

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long orderId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = DateTime.Today,
            SalesOrderId = orderId,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            Status = status
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity
            });
        }
        db.SaveChanges();
        stockOut.TotalQuantity = db.StockOutDetails.Where(d => d.StockOutId == stockOut.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockOut;
    }

    private static PurchaseOrder SeedPurchaseOrder(ErpDbContext db, string orderNo, long owningSalesOrderId,
        DocumentStatus status, bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = orderNo,
            Status = status,
            IsDeleted = deleted
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedFinanceReceipt(ErpDbContext db, string receiptNo, long customerId,
        decimal amount, Currency currency)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static void SeedReceiptAllocation(ErpDbContext db, FinanceReceipt receipt, SalesOrder order, int status)
    {
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = order.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = 100m,
            Currency = order.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == CustomerReceiptAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == CustomerReceiptAllocationRules.StatusVoided ? "录错" : string.Empty
        });
        db.SaveChanges();
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// </summary>
public sealed class SalesOrderCancellationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SALESORDERCANCEL_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-347] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-347] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class SalesOrderCancellationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => SalesOrderCancellationSqlServerFixture.AssertDedicatedTarget(connection));
}

