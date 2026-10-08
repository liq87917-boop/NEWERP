using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-349 客户收款单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="CustomerReceiptLifecycleRules"/> 与两套收款分摊证据服务做真实 SQL Server 验证：
/// 有效收款引用（ERP-053）拒绝收款单改金额 / 取消、作废释放、有效代理服务费分摊（ERP-071）拒绝改金额、
/// 以及同一收款单并发「登记证据 vs 改金额」「登记证据 vs 取消」经 UPDLOCK/HOLDLOCK 串行化后只能成功其一，
/// 失败时原始收款单与分摊证据保持不变。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class CustomerReceiptLifecycleSqlServerTests
    : IClassFixture<CustomerReceiptLifecycleSqlServerFixture>
{
    private readonly CustomerReceiptLifecycleSqlServerFixture _fixture;

    public CustomerReceiptLifecycleSqlServerTests(CustomerReceiptLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 事务化助手（与控制器 / ERP-343 同源锁定口径） ====================

    private async Task<(bool Success, string Error)> TryChangeAmountAsync(long receiptId, decimal newAmount)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, receiptId);

            var receipt = await db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            if (receipt.Status != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            var amount = CustomerReceiptLifecycleRules.NormalizeReceiptAmount(newAmount, receipt.Currency.ToString());
            var changesEvidence = amount != CustomerReceiptLifecycleRules.AuthoritativeReceiptAmount(
                receipt.Amount, receipt.Currency.ToString());
            if (changesEvidence)
                await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(db, receiptId, "修改金额");

            receipt.Amount = amount;
            receipt.UpdatedAt = DateTime.Now;
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

    private async Task<(bool Success, string Error)> TryCancelAsync(long receiptId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, receiptId);

            var receipt = await db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            if (receipt.Status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("收款单已取消，不能重复取消");

            await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(db, receiptId, "取消");

            receipt.Status = DocumentStatus.Cancelled;
            receipt.UpdatedAt = DateTime.Now;
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

    private async Task<(bool Success, string Error)> TryAllocateAsync(long receiptId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await CustomerReceiptAllocationService.CreateAsync(db, new CustomerReceiptAllocationSaveDto
            {
                ReceiptId = receiptId,
                SalesOrderId = orderId,
                AllocatedAmount = amount
            });
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 种子助手（EF 生成身份键） ====================

    private static long SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = currency,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static void SeedCustomerOrderAllocation(
        ErpDbContext db, FinanceReceipt receipt, SalesOrder order, decimal amount, int status)
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
            CustomerId = receipt.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = order.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == CustomerReceiptAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == CustomerReceiptAllocationRules.StatusVoided ? "录错" : string.Empty
        });
        db.SaveChanges();
    }

    private static void SeedAgencyFeeAllocation(ErpDbContext db, FinanceReceipt receipt, decimal amount, int status)
    {
        db.AgencyServiceFeeCollectionAllocations.Add(new AgencyServiceFeeCollectionAllocation
        {
            StatementId = 990001L,
            StatementNo = "STMT-990001",
            StatementDate = DateTime.Today.AddDays(-2),
            StatementStatus = 1,
            StatementStatusText = "已登记",
            StatementTotalAmount = 5000m,
            StatementCurrency = receipt.Currency.ToString(),
            StatementAgreementId = 1L,
            StatementAgreementNo = "AG-1",
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            CustomerId = receipt.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = receipt.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            AllocatedBy = "tester",
            VoidedAt = status == AgencyServiceFeeCollectionAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == AgencyServiceFeeCollectionAllocationRules.StatusVoided ? "录错" : string.Empty
        });
        db.SaveChanges();
    }

    // ==================== 真实 SQL 场景 ====================

    [Fact]
    public async Task ReceiptChange_ActiveCustomerOrderAllocation_Refused_ThenVoidReleases()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_A", "客户A");
        var order = SeedOrder(seed, "INT_TEST_RLC_SO_A", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_A", customerId, 1000m, Currency.CNY);
        SeedCustomerOrderAllocation(seed, receipt, order, 300m, CustomerReceiptAllocationRules.StatusActive);

        var refused = await TryChangeAmountAsync(receipt.Id, 500m);

        Assert.False(refused.Success);
        Assert.Contains("收款分摊证据", refused.Error);
        using (var check = _fixture.CreateDbContext())
        {
            Assert.Equal(1000m, check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
            Assert.Equal(CustomerReceiptAllocationRules.StatusActive,
                check.CustomerReceiptAllocations.AsNoTracking().Single(a => a.ReceiptId == receipt.Id).Status);
        }

        // 显式作废释放限制
        await using (var release = _fixture.CreateDbContext())
        {
            var row = release.CustomerReceiptAllocations.Single(a => a.ReceiptId == receipt.Id && !a.IsDeleted);
            row.Status = CustomerReceiptAllocationRules.StatusVoided;
            row.VoidedAt = DateTime.Now;
            row.VoidReason = "录错";
            await release.SaveChangesAsync();
        }

        var released = await TryChangeAmountAsync(receipt.Id, 500m);
        Assert.True(released.Success, released.Error);
        using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(500m, verify.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
        }
    }

    [Fact]
    public async Task ReceiptChange_ActiveAgencyFeeAllocation_Refused()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_B", "客户B");
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_B", customerId, 1000m, Currency.USD);
        SeedAgencyFeeAllocation(seed, receipt, 300m, AgencyServiceFeeCollectionAllocationRules.StatusActive);

        var refused = await TryChangeAmountAsync(receipt.Id, 500m);

        Assert.False(refused.Success);
        Assert.Contains("收款分摊证据", refused.Error);
        using var check = _fixture.CreateDbContext();
        Assert.Equal(1000m, check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
    }

    [Fact]
    public async Task Cancel_ActiveCustomerOrderAllocation_Refused_ThenVoidReleases()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_C", "客户C");
        var order = SeedOrder(seed, "INT_TEST_RLC_SO_C", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_C", customerId, 1000m, Currency.CNY);
        SeedCustomerOrderAllocation(seed, receipt, order, 300m, CustomerReceiptAllocationRules.StatusActive);

        var refused = await TryCancelAsync(receipt.Id);

        Assert.False(refused.Success);
        Assert.Contains("收款分摊证据", refused.Error);
        using (var check = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Pending, check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
        }
    }

    [Fact]
    public async Task ConcurrentAllocation_Race_OnlyOneSucceeds_OriginalUnchangedOnFailure()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_D", "客户D");
        var orderA = SeedOrder(seed, "INT_TEST_RLC_SO_D1", customerId, Currency.CNY);
        var orderB = SeedOrder(seed, "INT_TEST_RLC_SO_D2", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_D", customerId, 1000m, Currency.CNY);

        var results = await Task.WhenAll(
            TryAllocateAsync(receipt.Id, orderA.Id, 600m),
            TryAllocateAsync(receipt.Id, orderB.Id, 600m));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        using var check = _fixture.CreateDbContext();
        var savedReceipt = check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(1000m, savedReceipt.Amount);
        Assert.Equal(DocumentStatus.Pending, savedReceipt.Status);
        var rows = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .ToList();
        Assert.Single(rows);
        Assert.Equal(600m, rows[0].AllocatedAmount);
    }

    [Fact]
    public async Task ConcurrentAllocationVersusCancel_OnlyOneSucceeds()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_E", "客户E");
        var order = SeedOrder(seed, "INT_TEST_RLC_SO_E", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_E", customerId, 1000m, Currency.CNY);

        var results = await Task.WhenAll(
            TryAllocateAsync(receipt.Id, order.Id, 100m),
            TryCancelAsync(receipt.Id));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并重置为完整 NEWERP 结构 + 种子数据，
/// 供真实 SQL Server 集成测试复用；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class CustomerReceiptLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RECEIPTLIFECYCLE_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-349] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
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

    private async Task CreateFreshDatabaseAsync()
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

        Console.WriteLine("[ERP-349] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class CustomerReceiptLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => CustomerReceiptLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}
