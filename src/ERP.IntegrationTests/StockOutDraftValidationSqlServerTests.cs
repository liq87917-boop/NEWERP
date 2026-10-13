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
/// ERP-502 销售出库草稿写入护栏的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockOutController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建对「仓库 Id 非正 / 不存在 / 已软删除 / 已停用」与「数量非正 / 不可表示、毛重与体积为负 / 不可表示、
/// 商品 Id 缺失 / 不存在 / 已删除 / 已停用」一律拒绝，且零单据、零明细、零库存流水、零单号消耗；</item>
/// <item>修改在明细整体替换之前收敛同样的非法输入，既有单据与明细逐字段不变、不消耗新单号；</item>
/// <item>重复商品行与空草稿头保持既有语义（累计数量合并、空头可保存但提交 / 审核要求有效明细），合法草稿创建 / 修改成功。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockOutDraftValidationSqlServerTests : IClassFixture<StockOutDraftValidationSqlServerFixture>
{
    private readonly StockOutDraftValidationSqlServerFixture _fixture;

    public StockOutDraftValidationSqlServerTests(StockOutDraftValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockOutDraftValidationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 创建：非法仓库（零写入 / 零单号） ====================

    [Theory]
    [InlineData("zero-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("negative-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("missing-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-warehouse", ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_warehouse_before_any_write(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var productId = await SeedProductAsync(db, "出库商品");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);

        if (scenario == "deleted-warehouse") await SetWarehouseFlagsAsync(db, warehouseId, isDeleted: true, status: null);
        if (scenario == "disabled-warehouse") await SetWarehouseFlagsAsync(db, warehouseId, isDeleted: null, status: 0);

        var requestWarehouseId = scenario switch
        {
            "zero-warehouse" => 0L,
            "negative-warehouse" => -7L,
            "missing-warehouse" => 999_999_999L,
            _ => warehouseId
        };

        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(requestWarehouseId, customerId, Line(productId, 5m))));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);
        Assert.Equal(0, await db.StockOuts.CountAsync(o => o.WarehouseId == warehouseId));
    }

    // ==================== 2. 创建：非法明细（数量 / 毛重 / 体积 / 商品） ====================

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-weight", ErrorCodes.InvalidParameter)]
    [InlineData("negative-volume", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_lines_before_any_write(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var productId = await SeedProductAsync(db, "出库商品");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);

        if (scenario == "deleted-product") await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product") await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);

        var (lineProductId, quantity, weight, volume) = scenario switch
        {
            "zero-quantity" => ((long?)productId, 0m, 0m, 0m),
            "negative-quantity" => ((long?)productId, -1m, 0m, 0m),
            "unrepresentable-quantity" => ((long?)productId, 1.005m, 0m, 0m),
            "overflow-quantity" => ((long?)productId, 10_000_000_000_000_000m, 0m, 0m),
            "negative-weight" => ((long?)productId, 1m, -1m, 0m),
            "unrepresentable-weight" => ((long?)productId, 1m, 0.005m, 0m),
            "negative-volume" => ((long?)productId, 1m, 0m, -1m),
            "unrepresentable-volume" => ((long?)productId, 1m, 0m, 0.005m),
            "missing-product-id" => ((long?)0L, 1m, 0m, 0m),
            "missing-product" => ((long?)999_999_999L, 1m, 0m, 0m),
            _ => ((long?)productId, 1m, 0m, 0m)
        };

        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(warehouseId, customerId, Line(lineProductId, quantity, weight, volume))));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);
        Assert.Equal(0, await db.StockOuts.CountAsync(o => o.WarehouseId == warehouseId));
    }

    [Fact]
    public async Task Create_rejects_overflowing_header_total_before_any_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var productId = await SeedProductAsync(db, "出库商品");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);

        // 单行数量各自可表示（= DECIMAL(18,2) 上界），但两行合计溢出 —— 必须校验计算后的表头合计。
        var max = StockOutAuthorizationRules.MaxDecimal18Scale2;
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ControllerFor(db, userId).Create(
            Draft(warehouseId, customerId, Line(productId, max), Line(productId, max))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        await AssertNoWriteAsync(db, before);
    }


    // ==================== 3. 创建：重复商品行 / 空草稿头 / 合法流程 ====================

    [Fact]
    public async Task Create_keeps_repeated_product_lines_and_permits_valid_flow()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var productId = await SeedProductAsync(db, "重复行商品");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);
        await SeedStockAsync(db, warehouseId, productId, 100m);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(
            Draft(warehouseId, customerId, Line(productId, 2m), Line(productId, 3m))));

        var stored = await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId);
        Assert.StartsWith("CK", stored.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(5m, stored.TotalQuantity);                        // 重复商品行按累计数量保留
        Assert.Equal(2, await db.StockOutDetails.CountAsync(d => d.StockOutId == createdId));

        await controller.Submit(createdId);
        Assert.IsType<OkObjectResult>(await controller.Approve(createdId));
        Assert.Equal(95m, (await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId)).Quantity);
        Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    [Fact]
    public async Task Create_allows_empty_draft_header_but_submit_requires_lines()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(Draft(warehouseId, customerId)));

        Assert.Empty(await db.StockOutDetails.Where(d => d.StockOutId == createdId).ToListAsync());

        var submitError = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(createdId));
        Assert.Equal(ErrorCodes.RuleConflict, submitError.Code);
        Assert.Equal(DocumentStatus.Pending,
            (await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    // ==================== 4. 修改：非法输入零改动，合法输入保留服务端身份 ====================

    [Theory]
    [InlineData("zero-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("missing-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-warehouse", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Update_rejects_invalid_draft_and_leaves_document_unchanged(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var productId = await SeedProductAsync(db, "出库商品");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(
            Draft(warehouseId, customerId, Line(productId, 4m, 1m))));

        var created = await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId);
        var createdDetail = await db.StockOutDetails.AsNoTracking().SingleAsync(d => d.StockOutId == createdId);
        var before = await SnapshotAsync(db);

        if (scenario == "deleted-warehouse") await SetWarehouseFlagsAsync(db, warehouseId, isDeleted: true, status: null);
        if (scenario == "disabled-warehouse") await SetWarehouseFlagsAsync(db, warehouseId, isDeleted: null, status: 0);
        if (scenario == "deleted-product") await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product") await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);

        var requestWarehouseId = scenario switch
        {
            "zero-warehouse" => 0L,
            "missing-warehouse" => 999_999_999L,
            _ => warehouseId
        };
        var (lineProductId, quantity) = scenario switch
        {
            "zero-quantity" => (productId, 0m),
            "negative-quantity" => (productId, -2m),
            "missing-product" => (999_999_999L, 3m),
            _ => (productId, 3m)
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(createdId,
            Draft(requestWarehouseId, customerId, Line(lineProductId, quantity, 1m))));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);

        var stored = await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId);
        Assert.Equal(created.StockOutNo, stored.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(warehouseId, stored.WarehouseId);
        Assert.Equal(4m, stored.TotalQuantity);
        var storedDetail = await db.StockOutDetails.AsNoTracking().SingleAsync(d => d.StockOutId == createdId);
        Assert.Equal(createdDetail.Id, storedDetail.Id);
        Assert.Equal(productId, storedDetail.ProductId);
        Assert.Equal(4m, storedDetail.Quantity);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }


    [Fact]
    public async Task Update_permits_valid_draft_and_keeps_server_number()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "出库仓");
        var productId = await SeedProductAsync(db, "出库商品");
        var customerId = await SeedCustomerAsync(db);
        var userId = await SeedOperatorAsync(db);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(
            Draft(warehouseId, customerId, Line(productId, 4m, 1m))));
        var number = (await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId)).StockOutNo;
        var before = await SnapshotAsync(db);

        var result = await controller.Update(createdId,
            Draft(warehouseId, customerId, Line(productId, 7m, 0.25m)));

        Assert.IsType<OkObjectResult>(result);
        var stored = await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == createdId);
        Assert.Equal(number, stored.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(7m, stored.TotalQuantity);
        Assert.Equal(0.25m, stored.TotalWeight);
        Assert.Equal(before.Sequence, await NumberSequenceAsync(db));       // 不消耗新单号
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    // ==================== 测试辅助 ====================

    private sealed record Snapshot(long Documents, long Details, long Movements, long? Sequence);

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

    private static StockOut Draft(long warehouseId, long customerId, params StockOutDetail[] lines)
        => new()
        {
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Remark = "ERP-502_SQL",
            Details = lines.ToList()
        };

    private static StockOutDetail Line(long? productId, decimal quantity, decimal weight = 0m, decimal volume = 0m)
        => new()
        {
            ProductId = productId ?? 0L,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            Weight = weight,
            Volume = volume
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static async Task<Snapshot> SnapshotAsync(ErpDbContext db)
        => new(await db.StockOuts.CountAsync(), await db.StockOutDetails.CountAsync(),
            await db.StockMovements.CountAsync(), await NumberSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存流水、零单号消耗。</summary>
    private static async Task AssertNoWriteAsync(ErpDbContext db, Snapshot before)
    {
        Assert.Equal(before.Documents, await db.StockOuts.CountAsync());
        Assert.Equal(before.Details, await db.StockOutDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.Sequence, await NumberSequenceAsync(db));
    }

    private static async Task<long?> NumberSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockOut)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"SODW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SODP-{Tag()}", ProductName = name, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"SODC-{Tag()}", CustomerName = "出库客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
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

    /// <summary>复用既有「admin」种子账号（SuperAdmin 特权身份 + 既有全部菜单），不新增任何用户授权。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task SetWarehouseFlagsAsync(ErpDbContext db, long id, bool? isDeleted, int? status)
    {
        var warehouse = await db.BaseWarehouses.SingleAsync(w => w.Id == id);
        if (isDeleted.HasValue) warehouse.IsDeleted = isDeleted.Value;
        if (status.HasValue) warehouse.Status = status.Value;
        await db.SaveChangesAsync();
    }

    private static async Task SetProductFlagsAsync(ErpDbContext db, long id, bool? isDeleted, int? status)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == id);
        if (isDeleted.HasValue) product.IsDeleted = isDeleted.Value;
        if (status.HasValue) product.Status = status.Value;
        await db.SaveChangesAsync();
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture（ERP-502）：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockOutDraftValidationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKOUTDRAFT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-502] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-502] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockOutDraftValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockOutDraftValidationSqlServerFixture.AssertDedicatedTarget(connection));
}

