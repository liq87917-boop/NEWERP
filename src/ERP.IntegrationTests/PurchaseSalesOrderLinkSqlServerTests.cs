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
/// ERP-346 采购订单显式归属销售订单链接 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="PurchaseSalesOrderLinkRules"/> 做真实 SQL Server 验证：授权链接派生权威快照、
/// 有界按 Id 精确解析、无身份 / 范围外 fail closed、伪造快照以权威为准、商品 / 单位不兼容拒绝、
/// 未关联不臆造链接、以及来源在编辑与保存之间变更被重新解析拒绝。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class PurchaseSalesOrderLinkSqlServerTests
    : IClassFixture<PurchaseSalesOrderLinkSqlServerFixture>
{
    private readonly PurchaseSalesOrderLinkSqlServerFixture _fixture;

    public PurchaseSalesOrderLinkSqlServerTests(PurchaseSalesOrderLinkSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 种子助手（EF 生成身份键） ====================

    private static long SeedProduct(ErpDbContext db, string code, string name, string unit)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = name, Spec = "规格A", Unit = unit };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private static long SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static long SeedSalesOrder(ErpDbContext db, string orderNo, long customerId, DocumentStatus status,
        params (long ProductId, string Unit)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = status,
            Details = lines.Select(l => new SalesOrderDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = 10m,
                UnitPrice = 20m,
                Amount = 200m
            }).ToList()
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order.Id;
    }

    private static PurchaseOrder NewPurchaseOrder(long? owningSalesOrderId,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            OwningSalesOrderId = owningSalesOrderId,
            Details = lines.Select(l => new PurchaseOrderDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };

    private static async Task<long> AdminUserIdAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = code, RoleCode = $"Role-{code}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == PurchaseSalesOrderLinkRules.RequiredMenuCode)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();
        return user.Id;
    }

    // ==================== 真实 SQL 场景 ====================

    [Fact]
    public async Task AuthorizedLink_DerivesAuthoritativeSnapshot()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POSL_PROD_A", "商品A", "PCS");
        var customerId = SeedCustomer(db, "INT_TEST_POSL_CUST_A", "ACME IMPORT");
        var soId = SeedSalesOrder(db, "INT_TEST_POSL_SO_A", customerId, DocumentStatus.Approved, (productId, "PCS"));
        var adminId = await AdminUserIdAsync(db);

        var entity = NewPurchaseOrder(soId, (productId, "PCS", 2m, 5m));
        entity.OwningSalesOrderNo = "FORGED-NO";
        entity.OwningCustomerName = "FORGED-NAME";

        await PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, entity, adminId);

        Assert.Equal("INT_TEST_POSL_SO_A", entity.OwningSalesOrderNo);
        Assert.Equal(customerId, entity.OwningCustomerId);
        Assert.Equal("ACME IMPORT", entity.OwningCustomerName);
        Assert.Equal(Currency.CNY, entity.Currency);
        Assert.Equal(5m, entity.Details.Single().UnitPrice);
    }

    [Fact]
    public async Task BoundedLookup_ResolvesExactSalesOrderById()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productA = SeedProduct(db, "INT_TEST_POSL_PROD_B1", "商品A", "PCS");
        var productB = SeedProduct(db, "INT_TEST_POSL_PROD_B2", "商品B", "PCS");
        var custA = SeedCustomer(db, "INT_TEST_POSL_CUST_B1", "客户A");
        var custB = SeedCustomer(db, "INT_TEST_POSL_CUST_B2", "客户B");
        var so1 = SeedSalesOrder(db, "INT_TEST_POSL_SO_B1", custA, DocumentStatus.Approved, (productA, "PCS"));
        SeedSalesOrder(db, "INT_TEST_POSL_SO_B2", custB, DocumentStatus.Approved, (productB, "PCS"));
        var adminId = await AdminUserIdAsync(db);

        var entity = NewPurchaseOrder(so1, (productA, "PCS", 1m, 1m));
        await PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, entity, adminId);

        Assert.Equal("INT_TEST_POSL_SO_B1", entity.OwningSalesOrderNo);
        Assert.Equal(custA, entity.OwningCustomerId);
    }

    [Fact]
    public async Task UnlinkedProcurement_NoFabricatedLink()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var entity = NewPurchaseOrder(null);
        entity.OwningSalesOrderNo = "FREE-TEXT";

        await PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, entity, null);

        Assert.Null(entity.OwningSalesOrderId);
        Assert.Null(entity.OwningCustomerId);
        Assert.Equal("FREE-TEXT", entity.OwningSalesOrderNo);
    }

    [Fact]
    public async Task Denied_NoIdentity_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POSL_PROD_D", "商品A", "PCS");
        var customerId = SeedCustomer(db, "INT_TEST_POSL_CUST_D", "客户A");
        var soId = SeedSalesOrder(db, "INT_TEST_POSL_SO_D", customerId, DocumentStatus.Approved, (productId, "PCS"));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, NewPurchaseOrder(soId), null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Denied_OutOfScope_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POSL_PROD_E", "商品A", "PCS");
        var customerId = SeedCustomer(db, "INT_TEST_POSL_CUST_E", "客户A", empId: null); // 未分配给受限业务员
        var soId = SeedSalesOrder(db, "INT_TEST_POSL_SO_E", customerId, DocumentStatus.Approved, (productId, "PCS"));
        var userId = await SeedRestrictedUserAsync(db, "int-test-posl-scope");

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, NewPurchaseOrder(soId), userId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("数据范围", ex.Message);
    }

    [Fact]
    public async Task InvalidProduct_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productA = SeedProduct(db, "INT_TEST_POSL_PROD_F1", "商品A", "PCS");
        var productB = SeedProduct(db, "INT_TEST_POSL_PROD_F2", "商品B", "PCS");
        var customerId = SeedCustomer(db, "INT_TEST_POSL_CUST_F", "客户A");
        var soId = SeedSalesOrder(db, "INT_TEST_POSL_SO_F", customerId, DocumentStatus.Approved, (productA, "PCS"));
        var adminId = await AdminUserIdAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, NewPurchaseOrder(soId, (productB, "PCS", 1m, 1m)), adminId));
        Assert.Contains("不在归属销售订单明细中", ex.Message);
    }

    [Fact]
    public async Task InvalidUnit_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POSL_PROD_G", "商品A", "PCS");
        var customerId = SeedCustomer(db, "INT_TEST_POSL_CUST_G", "客户A");
        var soId = SeedSalesOrder(db, "INT_TEST_POSL_SO_G", customerId, DocumentStatus.Approved, (productId, "PCS"));
        var adminId = await AdminUserIdAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, NewPurchaseOrder(soId, (productId, "BOX", 1m, 1m)), adminId));
        Assert.Contains("单位", ex.Message);
    }

    [Fact]
    public async Task SourceChangedBetweenEditAndSave_FailsClosed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var productId = SeedProduct(db, "INT_TEST_POSL_PROD_H", "商品A", "PCS");
        var customerId = SeedCustomer(db, "INT_TEST_POSL_CUST_H", "客户A");
        var soId = SeedSalesOrder(db, "INT_TEST_POSL_SO_H", customerId, DocumentStatus.Approved, (productId, "PCS"));
        var adminId = await AdminUserIdAsync(db);

        var editEntity = NewPurchaseOrder(soId, (productId, "PCS", 2m, 5m));
        await PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, editEntity, adminId);   // 编辑时通过

        var so = await db.SalesOrders.SingleAsync(s => s.Id == soId);
        so.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseSalesOrderLinkRules.ApplyLinkAsync(db, NewPurchaseOrder(soId, (productId, "PCS", 2m, 5m)), adminId));
        Assert.Contains("已取消", ex.Message);
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// </summary>
public sealed class PurchaseSalesOrderLinkSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_POSALESLINK_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-346] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-346] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }

    private static string QuoteSqlString(string value) => value.Replace("'", "''");

    private static string QuoteSqlIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";
}


public sealed class PurchaseSalesOrderLinkTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PurchaseSalesOrderLinkSqlServerFixture.AssertDedicatedTarget(connection));
}

