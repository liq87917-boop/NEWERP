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
/// ERP-348 装柜清单衔接已审核预装柜单并防止累计超装 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="ContainerLoadingFulfillmentRules"/> 做真实 SQL Server 验证：无效显式链接 fail closed 拒绝、
/// 部分 / 满量批次累计、重复行聚合、累计超限拒绝、取消释放额度、箱数不当作件数、以及同一来源并发审核与来源取消经
/// UPDLOCK/HOLDLOCK 串行化后不会超发 / 不会越过取消护栏。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class ContainerLoadingFulfillmentSqlServerTests
    : IClassFixture<ContainerLoadingFulfillmentSqlServerFixture>
{
    private readonly ContainerLoadingFulfillmentSqlServerFixture _fixture;

    private const long CustomerA = 944001L;
    private const long CustomerB = 944002L;

    public ContainerLoadingFulfillmentSqlServerTests(ContainerLoadingFulfillmentSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    private static (long ProductId, decimal Quantity, decimal Cartons, decimal Weight, decimal Volume) L(long productId, decimal qty)
        => (productId, qty, 0m, 0m, 0m);

    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "集成特权角色",
            RoleCode = $"IT-Priv-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"it-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "集成特权用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static long SeedProduct(ErpDbContext db, string code, string name, string unit)
    {
        var product = new BaseProduct
        {
            ProductCode = code, ProductName = name, Spec = "规格A", Unit = unit
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, DocumentStatus status,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var pre = new ContainerPreLoading { PreLoadingNo = no, LoadingDate = DateTime.Today, Status = status };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Quantity = quantity
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static ContainerLoadingList SeedLoading(ErpDbContext db, string no, long? preLoadingId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity, decimal Cartons, decimal Weight, decimal Volume)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, PreLoadingId = preLoadingId, LoadingDate = DateTime.Today,
            CustomerId = customerId, Status = status
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        foreach (var (productId, quantity, cartons, weight, volume) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Quantity = quantity,
                Cartons = cartons,
                Weight = weight,
                Volume = volume
            });
        }
        db.SaveChanges();
        return list;
    }

    private static async Task<ContainerLoadingList> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking().SingleAsync(l => l.Id == id);

    [Fact]
    public async Task Approval_InvalidSource_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(db);
        var productId = SeedProduct(db, "INT_TEST_CLF_PROD_A", "商品A", "PCS");
        var pre = SeedPreLoading(db, "INT_TEST_CLF_SRC_PENDING", DocumentStatus.Pending, (productId, 10m));
        var entity = SeedLoading(db, "INT_TEST_CLF_LIST_PENDING", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 6m));

        var ex = await Assert.ThrowsAsync<BusinessException>(async () =>
            await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id), userId));

        Assert.Contains("未审核", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.ContainerLoadingLists.Single(l => l.Id == entity.Id).Status);
    }

    [Fact]
    public async Task Approval_PartialThenFull_Allowed_ThenOverrun_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(db);
        var productId = SeedProduct(db, "INT_TEST_CLF_PROD_CUM", "商品A", "PCS");
        var pre = SeedPreLoading(db, "INT_TEST_CLF_SRC_CUM", DocumentStatus.Approved, (productId, 10m));

        SeedLoading(db, "INT_TEST_CLF_LIST_CUM_A", pre.Id, CustomerA, DocumentStatus.Approved, L(productId, 6m));
        var partial = SeedLoading(db, "INT_TEST_CLF_LIST_CUM_B", pre.Id, CustomerA, DocumentStatus.Submitted, L(productId, 4m));

        await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, partial.Id), userId);
        partial.Status = DocumentStatus.Approved;
        await db.SaveChangesAsync();

        var over = SeedLoading(db, "INT_TEST_CLF_LIST_CUM_C", pre.Id, CustomerA, DocumentStatus.Submitted, L(productId, 1m));
        var overEntity = await ReloadAsync(db, over.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, overEntity, userId));
        Assert.Contains("超过预装柜单授权数量 10", ex.Message);
    }

    [Fact]
    public async Task Approval_DuplicateLines_Aggregate_Overflow()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(db);
        var productId = SeedProduct(db, "INT_TEST_CLF_PROD_DUP", "商品A", "PCS");
        var pre = SeedPreLoading(db, "INT_TEST_CLF_SRC_DUP", DocumentStatus.Approved, (productId, 10m));
        var entity = SeedLoading(db, "INT_TEST_CLF_LIST_DUP", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 6m), L(productId, 6m));

        var ex = await Assert.ThrowsAsync<BusinessException>(async () =>
            await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id), userId));

        Assert.Contains("本次 12", ex.Message);
    }

    [Fact]
    public async Task Approval_DuplicateSourceLines_Aggregate()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(db);
        var productId = SeedProduct(db, "INT_TEST_CLF_PROD_SRCDUP", "商品A", "PCS");
        var pre = SeedPreLoading(db, "INT_TEST_CLF_SRC_SRCDUP", DocumentStatus.Approved,
            (productId, 6m), (productId, 4m));
        var entity = SeedLoading(db, "INT_TEST_CLF_LIST_SRCDUP", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 10m));

        await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id), userId);
    }

    [Fact]
    public async Task Approval_CancelledDocument_ReleasesRemainingCapacity()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(db);
        var productId = SeedProduct(db, "INT_TEST_CLF_PROD_CANCEL", "商品A", "PCS");
        var pre = SeedPreLoading(db, "INT_TEST_CLF_SRC_CANCEL", DocumentStatus.Approved, (productId, 10m));

        var cancelled = SeedLoading(db, "INT_TEST_CLF_LIST_CANCEL_A", pre.Id, CustomerA, DocumentStatus.Approved,
            L(productId, 6m));
        cancelled.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var full = SeedLoading(db, "INT_TEST_CLF_LIST_CANCEL_B", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 10m));
        await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, full.Id), userId);
    }

    [Fact]
    public async Task Approval_CartonsNotEquatedToPieces()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(db);
        var productId = SeedProduct(db, "INT_TEST_CLF_PROD_CARTON", "商品A", "PCS");
        var pre = SeedPreLoading(db, "INT_TEST_CLF_SRC_CARTON", DocumentStatus.Approved, (productId, 10m));
        var entity = SeedLoading(db, "INT_TEST_CLF_LIST_CARTON", pre.Id, CustomerA, DocumentStatus.Submitted,
            (productId, 6m, 100m, 0m, 0m));

        await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id), userId);
    }


    [Fact]
    public async Task ConcurrentApprovals_SerializedBySourceLock_OneOverrunBlocked()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(seed);
        var productId = SeedProduct(seed, "INT_TEST_CLF_PROD_CONC", "商品A", "PCS");
        var pre = SeedPreLoading(seed, "INT_TEST_CLF_SRC_CONC", DocumentStatus.Approved, (productId, 10m));
        var docA = SeedLoading(seed, "INT_TEST_CLF_LIST_CONC_A", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 6m));
        var docB = SeedLoading(seed, "INT_TEST_CLF_LIST_CONC_B", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 6m));

        var results = await Task.WhenAll(
            TryApproveAsync(docA.Id, pre.Id, userId),
            TryApproveAsync(docB.Id, pre.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.Error.Contains("超过预装柜单授权数量")));
    }

    [Fact]
    public async Task ConcurrentSourceCancelVersusApproval_OnlyOneWins()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var userId = SeedPrivilegedUser(seed);
        var productId = SeedProduct(seed, "INT_TEST_CLF_PROD_RACE", "商品A", "PCS");
        var pre = SeedPreLoading(seed, "INT_TEST_CLF_SRC_RACE", DocumentStatus.Approved, (productId, 10m));
        var doc = SeedLoading(seed, "INT_TEST_CLF_LIST_RACE", pre.Id, CustomerA, DocumentStatus.Submitted,
            L(productId, 6m));

        var results = await Task.WhenAll(
            TryApproveAsync(doc.Id, pre.Id, userId),
            TryCancelSourceAsync(pre.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
    }

    private async Task<(bool Success, string Error)> TryApproveAsync(long loadingId, long? preLoadingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (preLoadingId is > 0)
            {
                var ids = await db.Database.SqlQueryRaw<long>(
                    "SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                    preLoadingId.Value).ToListAsync();
                if (ids.Count == 0)
                    throw BusinessException.RuleConflict("预装柜单不存在或已删除");
            }

            var entity = await db.ContainerLoadingLists.Include(o => o.Details).SingleAsync(l => l.Id == loadingId);
            if (entity.Status != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(db, entity, userId);

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

    private async Task<(bool Success, string Error)> TryCancelSourceAsync(long preLoadingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                preLoadingId).ToListAsync();
            if (ids.Count == 0)
                throw BusinessException.NotFound("预装柜单不存在");

            var pre = await db.ContainerPreLoadings.Include(o => o.Details).SingleAsync(p => p.Id == preLoadingId);
            await ContainerLoadingFulfillmentRules.ValidateSourceCancellationAsync(db, pre, userId);

            pre.Status = DocumentStatus.Cancelled;
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
}



/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// </summary>
public sealed class ContainerLoadingFulfillmentSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_CONTAINERLOADINGFULFILLMENT_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-348] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-348] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class ContainerLoadingFulfillmentTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => ContainerLoadingFulfillmentSqlServerFixture.AssertDedicatedTarget(connection));
}

