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
/// ERP-345 采购订单取消护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="PurchaseOrderCancellationRules"/> 做真实 SQL Server 验证：无履约依赖取消成功、
/// 已审核未冲销入库拒绝、已取消入库释放、有效付款 / 发票引用证据拒绝、作废证据释放，
/// 以及同一订单并发「入库审核 vs 来源取消」经 UPDLOCK/HOLDLOCK 串行化后只能成功其一。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class PurchaseOrderCancellationSqlServerTests
    : IClassFixture<PurchaseOrderCancellationSqlServerFixture>
{
    private readonly PurchaseOrderCancellationSqlServerFixture _fixture;

    private const long SupplierA = 943001L;
    private const long WarehouseA = 943101L;
    private const long ProductA = 943201L;

    public PurchaseOrderCancellationSqlServerTests(PurchaseOrderCancellationSqlServerFixture fixture)
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
        var productId = SeedProduct(db, "INT_TEST_POCANCEL_PROD", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_POCANCEL_PO_A", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
        var saved = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Cancelled, saved.Status);
        Assert.Equal(75m, saved.TotalAmount);
        Assert.Single(db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id && !d.IsDeleted));
    }

    [Fact]
    public async Task Cancel_ActiveApprovedStockIn_Refused()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POCANCEL_PROD_B", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_POCANCEL_PO_B", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));
        SeedStockIn(db, "INT_TEST_POCANCEL_SI_B", order.Id, SupplierA, DocumentStatus.Approved, (productId, 6m));

        var result = await TryCancelAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("已审核且未冲销", result.Error);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ReversedStockIn_Released()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POCANCEL_PROD_C", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_POCANCEL_PO_C", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));
        SeedStockIn(db, "INT_TEST_POCANCEL_SI_C", order.Id, SupplierA, DocumentStatus.Cancelled, (productId, 6m));

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Cancel_ActivePaymentAllocation_Refused()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POCANCEL_PROD_D", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_POCANCEL_PO_D", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));
        var payment = SeedPayment(db, "INT_TEST_POCANCEL_PAY_D", SupplierA);
        SeedPaymentAllocation(db, payment, order, SupplierPaymentAllocationRules.StatusActive, 30m);

        var result = await TryCancelAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("付款单 → 采购订单", result.Error);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_VoidedPaymentAllocation_Released()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POCANCEL_PROD_E", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_POCANCEL_PO_E", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));
        var payment = SeedPayment(db, "INT_TEST_POCANCEL_PAY_E", SupplierA);
        SeedPaymentAllocation(db, payment, order, SupplierPaymentAllocationRules.StatusVoided, 30m, voidReason: "录错");

        var result = await TryCancelAsync(order.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Cancel_ActiveInvoicePaymentAllocation_Refused()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POCANCEL_PROD_F", "商品A", "PCS");
        var order = SeedOrder(db, "INT_TEST_POCANCEL_PO_F", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));
        var invoice = SeedInvoice(db, "INT_TEST_POCANCEL_INV_F", SupplierA, PurchaseInvoiceRules.StatusRecorded, 30m);
        SeedInvoiceAllocation(db, invoice, order);
        var payment = SeedPayment(db, "INT_TEST_POCANCEL_PAY_F", SupplierA);
        SeedInvoicePaymentAllocation(db, payment, invoice, SupplierPaymentInvoiceAllocationRules.StatusActive, 30m);

        var result = await TryCancelAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("付款单 → 采购发票", result.Error);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Concurrent_CancelVsStockInApproval_OnlyOneSucceeds()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var productId = SeedProduct(seed, "INT_TEST_POCANCEL_PROD_G", "商品A", "PCS");
        var order = SeedOrder(seed, "INT_TEST_POCANCEL_PO_G", SupplierA, DocumentStatus.Approved,
            (productId, 10m, 7.5m));
        var stockIn = SeedStockIn(seed, "INT_TEST_POCANCEL_SI_G", order.Id, SupplierA, DocumentStatus.Submitted,
            (productId, 6m));

        var results = await Task.WhenAll(
            TryApproveStockInAsync(stockIn.Id, order.Id),
            TryCancelAsync(order.Id));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));
    }


    // ==================== 事务化助手（与控制器 / ERP-342 同源锁定口径） ====================

    private async Task<(bool Success, string Error)> TryCancelAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", orderId)
                .ToListAsync();

            var order = await db.PurchaseOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购订单不存在");

            var adminId = await db.SysUsers.AsNoTracking()
                .Where(u => u.UserName == SeedData.AdminUserName)
                .Select(u => u.Id)
                .FirstAsync();
            await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, order, adminId);

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

    private async Task<(bool Success, string Error)> TryApproveStockInAsync(long stockInId, long orderId)
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

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, DocumentStatus status,
        params (long ProductId, decimal Quantity, decimal UnitPrice)[] lines)
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
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
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
        order.TotalAmount = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).Sum(d => d.Amount);
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long orderId, long supplierId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = DateTime.Today,
            PurchaseOrderId = orderId,
            SupplierId = supplierId,
            WarehouseId = WarehouseA,
            Status = status
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = stockIn.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity
            });
        }
        db.SaveChanges();
        stockIn.TotalQuantity = db.StockInDetails.Where(d => d.StockInId == stockIn.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockIn;
    }

    private static FinancePayment SeedPayment(ErpDbContext db, string paymentNo, long supplierId)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            Amount = 100m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static void SeedPaymentAllocation(ErpDbContext db, FinancePayment payment, PurchaseOrder order,
        int status, decimal amount, string? voidReason = null)
    {
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = payment.Status.ToString(),
            PaymentAmount = payment.Amount,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            SupplierId = payment.SupplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            AllocatedAmount = amount,
            Currency = payment.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == SupplierPaymentAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = voidReason ?? string.Empty
        });
        db.SaveChanges();
    }


    private static PurchaseInvoice SeedInvoice(ErpDbContext db, string invoiceNo, long supplierId, int status, decimal grossAmount)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceNumber = invoiceNo,
            NormalizedInvoiceNumber = invoiceNo,
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            Currency = "CNY",
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = status
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static void SeedInvoiceAllocation(ErpDbContext db, PurchaseInvoice invoice, PurchaseOrder order)
    {
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoice.Id,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderCurrency = order.Currency.ToString(),
            SupplierId = order.SupplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            AllocatedAmount = invoice.GrossAmount,
            Currency = invoice.Currency
        });
        db.SaveChanges();
    }

    private static void SeedInvoicePaymentAllocation(ErpDbContext db, FinancePayment payment, PurchaseInvoice invoice,
        int status, decimal amount)
    {
        db.SupplierPaymentInvoiceAllocations.Add(new SupplierPaymentInvoiceAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = payment.Status.ToString(),
            PaymentAmount = payment.Amount,
            PurchaseInvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceIdentityText = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = invoice.Status.ToString(),
            InvoiceGrossAmount = invoice.GrossAmount,
            SupplierId = invoice.SupplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            Status = status,
            AllocatedAt = DateTime.Now,
            RecordedBy = "测试"
        });
        db.SaveChanges();
    }
}



/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// </summary>
public sealed class PurchaseOrderCancellationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PURCHASEORDERCANCEL_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-345] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-345] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}


public sealed class PurchaseOrderCancellationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PurchaseOrderCancellationSqlServerFixture.AssertDedicatedTarget(connection));
}

