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
/// ERP-350 收款单唯一分摊额度 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="CustomerReceiptLifecycleRules"/> 与两套收款分摊证据服务做真实 SQL Server 验证：
/// 先「客户 80 → 代理 30」与反向写入顺序的合计超额拒绝、精确 100 双写成功、显式作废释放额度、
/// 币种 / 客户护栏失败关闭，以及两个独立连接上的跨消费者并发竞态（同单收款单行锁串行化后只能成功其一）。
/// 失败时原始收款单与分摊证据保持不变。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class CustomerReceiptSharedFundingSqlServerTests
    : IClassFixture<CustomerReceiptSharedFundingSqlServerFixture>
{
    private readonly CustomerReceiptSharedFundingSqlServerFixture _fixture;

    public CustomerReceiptSharedFundingSqlServerTests(CustomerReceiptSharedFundingSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 种子助手（EF 生成身份主键） ====================

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

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, Currency currency, decimal totalAmount = 5000m)
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            Currency = currency.ToString(),
            StatementDate = DateTime.Today.AddDays(-2),
            ServicePeriodFrom = DateTime.Today.AddMonths(-1),
            ServicePeriodTo = DateTime.Today,
            AgreementId = 1L,
            AgreementNo = "AG-1",
            AgreementCurrency = currency.ToString(),
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = AgencyServiceFeeStatementRules.StatusRecorded,
            RecordedAt = DateTime.Today.AddDays(-1),
            RecordedBy = "tester"
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private async Task<(bool Success, string Error)> TryCustomerAllocateAsync(
        long receiptId, long orderId, decimal amount)
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

    private async Task<(bool Success, string Error)> TryAgencyAllocateAsync(
        long receiptId, long statementId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await AgencyServiceFeeCollectionAllocationService.CreateAsync(db,
                new AgencyServiceFeeCollectionAllocationSaveDto
                {
                    StatementId = statementId,
                    ReceiptId = receiptId,
                    AllocatedAmount = amount
                }, "tester");
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 真实 SQL 场景：跨消费者额度 ====================

    [Fact]
    public async Task 客户80后代理30_合计110_拒绝代理且两表不变()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_350_CUST_A", "客户A");
        var order = SeedOrder(seed, "INT_TEST_350_SO_A", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_A", customerId, 100m, Currency.CNY);
        var statement = SeedStatement(seed, "INT_TEST_350_STMT_A", customerId, Currency.CNY);

        var customerOk = await TryCustomerAllocateAsync(receipt.Id, order.Id, 80m);
        Assert.True(customerOk.Success, customerOk.Error);

        var agencyRefused = await TryAgencyAllocateAsync(receipt.Id, statement.Id, 30m);
        Assert.False(agencyRefused.Success);

        await using var check = _fixture.CreateDbContext();
        var customerAllocated = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = check.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        Assert.Equal(80m, customerAllocated);
        Assert.Equal(0m, agencyAllocated);
    }

    [Fact]
    public async Task 代理80后客户30_合计110_拒绝客户且两表不变()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_350_CUST_B", "客户B");
        var order = SeedOrder(seed, "INT_TEST_350_SO_B", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_B", customerId, 100m, Currency.CNY);
        var statement = SeedStatement(seed, "INT_TEST_350_STMT_B", customerId, Currency.CNY);

        var agencyOk = await TryAgencyAllocateAsync(receipt.Id, statement.Id, 80m);
        Assert.True(agencyOk.Success, agencyOk.Error);

        var customerRefused = await TryCustomerAllocateAsync(receipt.Id, order.Id, 30m);
        Assert.False(customerRefused.Success);

        await using var check = _fixture.CreateDbContext();
        var customerAllocated = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = check.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        Assert.Equal(0m, customerAllocated);
        Assert.Equal(80m, agencyAllocated);
    }

    [Fact]
    public async Task 客户80代理20_合计精确100_双写成功()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_350_CUST_C", "客户C");
        var order = SeedOrder(seed, "INT_TEST_350_SO_C", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_C", customerId, 100m, Currency.CNY);
        var statement = SeedStatement(seed, "INT_TEST_350_STMT_C", customerId, Currency.CNY);

        var customerOk = await TryCustomerAllocateAsync(receipt.Id, order.Id, 80m);
        var agencyOk = await TryAgencyAllocateAsync(receipt.Id, statement.Id, 20m);
        Assert.True(customerOk.Success, customerOk.Error);
        Assert.True(agencyOk.Success, agencyOk.Error);

        await using var check = _fixture.CreateDbContext();
        var customerAllocated = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = check.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        Assert.Equal(80m, customerAllocated);
        Assert.Equal(20m, agencyAllocated);
        Assert.Equal(100m, customerAllocated + agencyAllocated);
    }

    [Fact]
    public async Task 占满后作废客户维度_释放额度后代理可再分摊()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_350_CUST_D", "客户D");
        var order = SeedOrder(seed, "INT_TEST_350_SO_D", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_D", customerId, 100m, Currency.CNY);
        var statement = SeedStatement(seed, "INT_TEST_350_STMT_D1", customerId, Currency.CNY);
        var statement2 = SeedStatement(seed, "INT_TEST_350_STMT_D2", customerId, Currency.CNY);

        Assert.True((await TryCustomerAllocateAsync(receipt.Id, order.Id, 80m)).Success);
        Assert.True((await TryAgencyAllocateAsync(receipt.Id, statement.Id, 20m)).Success);
        Assert.False((await TryAgencyAllocateAsync(receipt.Id, statement2.Id, 10m)).Success);

        await using (var release = _fixture.CreateDbContext())
        {
            var row = release.CustomerReceiptAllocations.Single(a => a.ReceiptId == receipt.Id && !a.IsDeleted);
            await CustomerReceiptAllocationService.VoidAsync(release, row.Id, "录错");
        }

        var created = await TryAgencyAllocateAsync(receipt.Id, statement2.Id, 10m);
        Assert.True(created.Success, created.Error);

        await using var check = _fixture.CreateDbContext();
        var customerAllocated = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = check.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        Assert.Equal(0m, customerAllocated);
        Assert.Equal(30m, agencyAllocated);
    }

    [Fact]
    public async Task 币种不一致_代理分摊失败关闭()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_350_CUST_E", "客户E");
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_E", customerId, 100m, Currency.USD);
        var statementCny = SeedStatement(seed, "INT_TEST_350_STMT_E", customerId, Currency.CNY);

        var refused = await TryAgencyAllocateAsync(receipt.Id, statementCny.Id, 20m);
        Assert.False(refused.Success);

        await using var check = _fixture.CreateDbContext();
        Assert.Empty(check.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted));
    }

    [Fact]
    public async Task 客户不一致_客户引用失败关闭()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerA = SeedCustomer(seed, "INT_TEST_350_CUST_F1", "客户F1");
        var customerB = SeedCustomer(seed, "INT_TEST_350_CUST_F2", "客户F2");
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_F", customerA, 100m, Currency.CNY);
        var orderB = SeedOrder(seed, "INT_TEST_350_SO_F", customerB, Currency.CNY);

        var refused = await TryCustomerAllocateAsync(receipt.Id, orderB.Id, 20m);
        Assert.False(refused.Success);

        await using var check = _fixture.CreateDbContext();
        Assert.Empty(check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted));
    }

    [Fact]
    public async Task 两个连接_跨消费者并发竞态_只能成功其一()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_350_CUST_G", "客户G");
        var order = SeedOrder(seed, "INT_TEST_350_SO_G", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_350_REC_G", customerId, 100m, Currency.CNY);
        var statement = SeedStatement(seed, "INT_TEST_350_STMT_G", customerId, Currency.CNY);

        var results = await Task.WhenAll(
            TryCustomerAllocateAsync(receipt.Id, order.Id, 80m),
            TryAgencyAllocateAsync(receipt.Id, statement.Id, 80m));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using var check = _fixture.CreateDbContext();
        var customerAllocated = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = check.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        Assert.Equal(80m, customerAllocated + agencyAllocated);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并重置为完整 NEWERP 结构 + 种子数据，
/// 供真实 SQL Server 集成测试复用；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class CustomerReceiptSharedFundingSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP350";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-350] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-350] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class CustomerReceiptSharedFundingTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => CustomerReceiptSharedFundingSqlServerFixture.AssertDedicatedTarget(connection));
}
