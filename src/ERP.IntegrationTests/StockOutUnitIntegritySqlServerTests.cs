using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-523 销售出库单位完整性护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockOutController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>手工未链接与显式链接来源草稿对「显式非空且不在商品当前基础 / 装箱单位内」的单位一律拒绝，
/// 且零单据、零明细、零库存、零流水、零单号消耗；</item>
/// <item>空 / 空白单位保持既有回退；大小写 / 首尾空白不敏感的基础 / 装箱单位放行，装箱单位恰好折算一次；</item>
/// <item>保存后商品单位主数据被改写（清空 / 改名）时，提交与审核在承诺边界内拒绝，零库存、零流水、状态不变；</item>
/// <item>链接来源按折算后的基础单位累计容量、取消释放，且审核不重复折算。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，
/// 每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockOutUnitIntegritySqlServerTests : IClassFixture<StockOutUnitIntegritySqlServerFixture>
{
    private readonly StockOutUnitIntegritySqlServerFixture _fixture;

    public StockOutUnitIntegritySqlServerTests(StockOutUnitIntegritySqlServerFixture fixture) => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockOutUnitIntegritySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 显式越界单位：创建即拒绝（未链接与链接同口径） ====================

    [Theory]
    [InlineData("KG", false)]
    [InlineData("kg", false)]
    [InlineData(" SET ", false)]
    [InlineData("KG", true)]
    public async Task Create_rejects_explicit_unsupported_unit_before_any_write(string unit, bool linked)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "箱装商品", "PCS", "CTN", 12);
        long? orderId = linked ? await SeedApprovedOrderAsync(db, customerId, productId, 24m) : null;
        var userId = await SeedOperatorAsync(db);

        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(customerId, warehouseId, orderId, Line(productId, 1m, unit))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        await AssertNoWriteAsync(db, before);
    }

    // ==================== 2. 支持口径：空白回退 / 基础单位 / 装箱折算恰好一次 ====================

    [Theory]
    [InlineData("", 3.5, null, 3.5)]
    [InlineData("   ", 2, null, 2)]
    [InlineData("PCS", 2, "PCS", 2)]
    [InlineData("pcs", 2, "pcs", 2)]
    [InlineData(" Pcs ", 2, " Pcs ", 2)]
    [InlineData("CTN", 2, "PCS", 24)]
    [InlineData("ctn", 2, "PCS", 24)]
    public async Task Create_accepts_supported_units_and_converts_package_exactly_once(string unit, decimal quantity,
        string? expectedUnit, decimal expectedQuantity)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "箱装商品", "PCS", "CTN", 12);
        var userId = await SeedOperatorAsync(db);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(
            Draft(customerId, warehouseId, null, Line(productId, quantity, unit))));

        var detail = await db.StockOutDetails.AsNoTracking().SingleAsync(d => d.StockOutId == createdId);
        Assert.Equal(expectedQuantity, detail.Quantity);
        if (expectedUnit is not null) Assert.Equal(expectedUnit, detail.Unit);

        var header = await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId);
        Assert.Equal(expectedQuantity, header.TotalQuantity);
    }

    [Fact]
    public async Task Create_rejects_package_conversion_overflow_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "箱装商品", "PCS", "CTN", 12);
        var userId = await SeedOperatorAsync(db);

        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(customerId, warehouseId, null,
                Line(productId, 10_000_000_000_000_000_000_000_000_000m, "CTN"))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出可表示范围", error.Message);
        await AssertNoWriteAsync(db, before);
    }


    // ==================== 3. 提交 / 审核承诺边界：主数据被改写后拒绝 ====================

    [Theory]
    [InlineData("cleared")]
    [InlineData("mutated")]
    public async Task Submit_and_approve_reject_when_master_unit_changed_after_draft(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "箱装商品", "PCS", "CTN", 12);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var createdId = CreatedId(await controller.Create(
            Draft(customerId, warehouseId, null, Line(productId, 2m, "PCS"))));

        if (scenario == "cleared")
            await SetProductUnitAsync(db, productId, string.Empty, string.Empty);
        else
            await SetProductUnitAsync(db, productId, "BOX", "CTN");

        var beforeSubmit = await SnapshotAsync(db);
        var submitError = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(createdId));
        Assert.Equal(ErrorCodes.RuleConflict, submitError.Code);
        Assert.Contains("不在当前有效单位口径内", submitError.Message);
        Assert.Equal(DocumentStatus.Pending,
            (await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);
        await AssertNoWriteAsync(db, beforeSubmit);

        await SetDocumentStatusAsync(db, createdId, DocumentStatus.Submitted);
        var beforeApprove = await SnapshotAsync(db);
        var approveError = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(createdId));
        Assert.Equal(ErrorCodes.RuleConflict, approveError.Code);
        Assert.Contains("不在当前有效单位口径内", approveError.Message);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);
        await AssertNoWriteAsync(db, beforeApprove);
    }

    // ==================== 4. 链接来源：折算后按基础单位累计容量且不重复折算 ====================

    [Fact]
    public async Task Approve_uses_converted_base_quantity_for_source_capacity_without_double_conversion()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "箱装商品", "PCS", "CTN", 12);
        var orderId = await SeedApprovedOrderAsync(db, customerId, productId, 24m);
        await SeedStockAsync(db, warehouseId, productId, 100m);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var firstId = CreatedId(await controller.Create(
            Draft(customerId, warehouseId, orderId, Line(productId, 2m, "CTN"))));
        await controller.Submit(firstId);
        await controller.Approve(firstId);

        var firstDetail = await db.StockOutDetails.AsNoTracking().SingleAsync(d => d.StockOutId == firstId);
        Assert.Equal(24m, firstDetail.Quantity);                 // 2 CTN → 24 PCS，恰好一次
        Assert.Equal("PCS", firstDetail.Unit);

        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.SourceDocId == firstId);
        Assert.Equal(24m, movement.Quantity);                    // 承诺边界不重复折算（不是 288）
        Assert.Equal(76m, (await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId)).Quantity);

        // 第二张：订单授权 24 已被第一张占满，再出 1 CTN（12 PCS）在审核时按累计容量拒绝。
        var secondId = CreatedId(await controller.Create(
            Draft(customerId, warehouseId, orderId, Line(productId, 1m, "CTN"))));
        await controller.Submit(secondId);
        var over = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(secondId));
        Assert.Equal(ErrorCodes.RuleConflict, over.Code);
        Assert.Equal(76m, (await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId)).Quantity);

        await controller.Cancel(firstId);                        // 取消释放剩余容量
        await controller.Approve(secondId);                      // 1 CTN = 12 PCS → 88
        Assert.Equal(88m, (await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId)).Quantity);
    }


    // ==================== 辅助 ====================

    private static StockOutController ControllerFor(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
                }
            }
        };

    private static StockOut Draft(long customerId, long warehouseId, long? salesOrderId,
        params StockOutDetail[] lines)
        => new()
        {
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            SalesOrderId = salesOrderId,
            Remark = "ERP-523_SQL",
            Details = lines.ToList()
        };

    private static StockOutDetail Line(long productId, decimal quantity, string unit)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static async Task<Snapshot> SnapshotAsync(ErpDbContext db)
        => new(await db.StockOuts.CountAsync(), await db.StockOutDetails.CountAsync(),
            await db.StockMovements.CountAsync(), await db.Stocks.CountAsync(), await NumberSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存、零流水、零单号消耗。</summary>
    private static async Task AssertNoWriteAsync(ErpDbContext db, Snapshot before)
    {
        Assert.Equal(before.Documents, await db.StockOuts.CountAsync());
        Assert.Equal(before.Details, await db.StockOutDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.Stocks, await db.Stocks.CountAsync());
        Assert.Equal(before.Sequence, await NumberSequenceAsync(db));
    }

    private static async Task<long?> NumberSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockOut)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    private sealed record Snapshot(long Documents, long Details, long Movements, long Stocks, long? Sequence);

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"SIU-{Tag()}", CustomerName = "单位完整性客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db)
    {
        var warehouse = new BaseWarehouse
        {
            WarehouseCode = $"SIU-{Tag()}", WarehouseName = "单位完整性仓", Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit, string packageUnit,
        int unitsPerPackage)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SIU-{Tag()}", ProductName = name, Spec = "规格A",
            Unit = unit, PackageUnit = packageUnit, UnitsPerPackage = unitsPerPackage, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedApprovedOrderAsync(ErpDbContext db, long customerId, long productId,
        decimal capacity)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SIU-SO-{Tag()}", OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, ExchangeRate = 1m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = productId, ProductName = "箱装商品", Spec = "规格A",
            Unit = "PCS", Quantity = capacity, UnitPrice = 5m, Amount = capacity * 5m
        });
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId, ProductId = productId,
            Quantity = quantity, AvailableQuantity = quantity
        });
        await db.SaveChangesAsync();
    }

    /// <summary>复用既有「admin」种子账号（系统内置特权身份 + 既有全部菜单），不新增任何用户授权。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task SetProductUnitAsync(ErpDbContext db, long productId, string unit, string packageUnit)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Unit = unit;
        product.PackageUnit = packageUnit;
        await db.SaveChangesAsync();
    }

    private static async Task SetDocumentStatusAsync(ErpDbContext db, long id, DocumentStatus status)
    {
        var document = await db.StockOuts.SingleAsync(o => o.Id == id);
        document.Status = status;
        await db.SaveChangesAsync();
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture（ERP-523）：全新 GUID 后缀库 → 完整 NEWERP 结构 + 种子数据。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 也绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockOutUnitIntegritySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKOUTUNIT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-523] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        // 破坏性初始化前再次护栏：绝不使用生产回退，也绝不复用已存在的库。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
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

        Console.WriteLine("[ERP-523] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockOutUnitIntegrityTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockOutUnitIntegritySqlServerFixture.AssertDedicatedTarget(connection));
}

