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
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-507 库存盘点单数值边界的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockAdjustmentController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际 EF 列精度（<c>DECIMAL(18,4)</c> 数量 / 金额、<c>DECIMAL(18,6)</c> 成本）与规则常量同源，
/// 可表示边界值与 4 / 6 位小数原样落库（无截断）；</item>
/// <item>创建 / 修改对不可表示的账面 / 实盘数量、成本单价、有符号差异乘法溢出、隐藏行（<c>IsDeleted=true</c>）
/// 与重复商品一律拒绝，且零单据 / 明细 / 库存 / 流水、单号序列不推进；</item>
/// <item>合法盘点单（盘盈 + / 盘亏 −）可如实审核并写入库存与流水，隐藏有效行被服务端恢复为正常行。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockAdjustmentNumericBoundsSqlServerTests
    : IClassFixture<StockAdjustmentNumericBoundsSqlServerFixture>
{
    private readonly StockAdjustmentNumericBoundsSqlServerFixture _fixture;

    public StockAdjustmentNumericBoundsSqlServerTests(StockAdjustmentNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockAdjustmentNumericBoundsSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 实际列精度 + 可表示边界值原样落库 ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules_and_boundary_values_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,4)", ColumnType(typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.BookQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.ActualQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.DiffQuantity)));
        Assert.Equal("decimal(18,6)", ColumnType(typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.UnitCost)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.DiffAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(StockAdjustment), nameof(StockAdjustment.TotalDiffQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(StockAdjustment), nameof(StockAdjustment.TotalDiffAmount)));

        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockAdjustmentDetails", "BookQuantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockAdjustmentDetails", "ActualQuantity"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "StockAdjustmentDetails", "UnitCost"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockAdjustmentDetails", "DiffAmount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockAdjustments", "TotalDiffQuantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "StockAdjustments", "TotalDiffAmount"));

        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productA = await SeedProductAsync(db, "边界数量商品");
        var productB = await SeedProductAsync(db, "边界成本商品");
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);

        // 边界数量（DECIMAL(18,4) 上界）与 6 位小数成本必须原样落库。
        var id = CreatedId(await controller.Create(Draft(warehouseId,
            Line(productA, 0m, StockAdjustmentPostingRules.MaxDecimal18Scale4, 0m),
            Line(productB, 2.3456m, 1.2345m, 1.000001m))));

        var details = await db.StockAdjustmentDetails.AsNoTracking()
            .Where(d => d.StockAdjustmentId == id).OrderBy(d => d.SortNo).ToListAsync();
        Assert.Equal(2, details.Count);
        Assert.Equal(0m, details[0].BookQuantity);
        Assert.Equal(StockAdjustmentPostingRules.MaxDecimal18Scale4, details[0].ActualQuantity);
        Assert.Equal(StockAdjustmentPostingRules.MaxDecimal18Scale4, details[0].DiffQuantity);
        Assert.Equal(2.3456m, details[1].BookQuantity);
        Assert.Equal(1.2345m, details[1].ActualQuantity);
        Assert.Equal(1.000001m, details[1].UnitCost);
        Assert.Equal(-1.1111m, details[1].DiffQuantity);
        Assert.Equal(-1.1111m, details[1].DiffAmount);

        var document = await db.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(StockAdjustmentPostingRules.MaxDecimal18Scale4 - 1.1111m, document.TotalDiffQuantity);
        Assert.Equal(-1.1111m, document.TotalDiffAmount);
    }

    // ==================== 2. 创建 / 修改：非法数值与隐藏行零写入 ====================

    [Theory]
    [InlineData("unrepresentable-book")]
    [InlineData("unrepresentable-actual")]
    [InlineData("unrepresentable-cost")]
    [InlineData("overflow-book")]
    [InlineData("overflow-cost")]
    [InlineData("negative-book")]
    [InlineData("overflow-product")]
    [InlineData("duplicate-product")]
    [InlineData("hidden-bad-row")]
    public async Task Create_rejects_invalid_numbers_and_hidden_rows_before_any_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productA = await SeedProductAsync(db, "盘点商品A");
        var productB = await SeedProductAsync(db, "盘点商品B");
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);

        var lines = scenario switch
        {
            "unrepresentable-book" => new[] { Line(productA, 1.00001m, 1m, 1m) },
            "unrepresentable-actual" => new[] { Line(productA, 1m, 1.00001m, 1m) },
            "unrepresentable-cost" => new[] { Line(productA, 1m, 1m, 0.0000001m) },
            "overflow-book" => new[] { Line(productA, 100_000_000_000_000m, 1m, 1m) },
            "overflow-cost" => new[] { Line(productA, 1m, 1m, 1_000_000_000_000m) },
            "negative-book" => new[] { Line(productA, -1m, 1m, 1m) },
            "overflow-product" => new[] { Line(productA, 0m, 10_000_000_000m, 10_000_000_000m) },
            "duplicate-product" => new[] { Line(productA, 1m, 2m, 1m), Line(productA, 3m, 4m, 1m) },
            // 客户端把坏行标记为已删除：服务端会恢复为正常行并纳入校验，仍然拒绝
            _ => new[] { Line(productA, 1m, 1m, 1m), Line(productB, 1m, 1.00001m, 1m, isDeleted: true) }
        };

        var before = await SnapshotAsync(db, warehouseId);
        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Draft(warehouseId, lines)));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        await AssertNoWriteAsync(db, before, warehouseId);
    }

    [Fact]
    public async Task Create_revives_hidden_valid_row_and_counts_it_into_totals()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productA = await SeedProductAsync(db, "盘点商品A");
        var productB = await SeedProductAsync(db, "盘点商品B");
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);

        var hidden = Line(productB, 5m, 9m, 4m, isDeleted: true);
        hidden.Id = 424242L;
        var id = CreatedId(await controller.Create(Draft(warehouseId, Line(productA, 10m, 8m, 12.5m), hidden)));

        var document = await db.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(2m, document.TotalDiffQuantity);
        Assert.Equal(-9m, document.TotalDiffAmount);

        var details = await db.StockAdjustmentDetails.AsNoTracking()
            .Where(d => d.StockAdjustmentId == id).OrderBy(d => d.SortNo).ToListAsync();
        Assert.Equal(2, details.Count);
        Assert.All(details, d => Assert.False(d.IsDeleted));
        Assert.All(details, d => Assert.NotEqual(424242L, d.Id));
        Assert.All(details, d => Assert.Equal(id, d.StockAdjustmentId));
    }

    [Fact]
    public async Task Update_rejects_invalid_numbers_and_leaves_stored_document_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productA = await SeedProductAsync(db, "盘点商品A");
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);

        var id = CreatedId(await controller.Create(Draft(warehouseId, Line(productA, 10m, 8m, 12.5m))));
        var before = await db.StockAdjustments.AsNoTracking().Include(a => a.Details).SingleAsync(a => a.Id == id);

        var bad = Draft(warehouseId, Line(productA, 10m, 8m, 12.5m));
        bad.Remark = "SHOULD_NOT_BE_SAVED";
        bad.Details[0].ActualQuantity = 1.00001m;
        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, bad));
        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);

        var after = await db.StockAdjustments.AsNoTracking().Include(a => a.Details).SingleAsync(a => a.Id == id);
        Assert.Equal(before.AdjustmentNo, after.AdjustmentNo);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.TotalDiffQuantity, after.TotalDiffQuantity);
        Assert.Equal(before.TotalDiffAmount, after.TotalDiffAmount);
        Assert.Equal(before.Details.Single().ActualQuantity, after.Details.Single().ActualQuantity);
        Assert.Equal(before.Details.Single().DiffAmount, after.Details.Single().DiffAmount);
    }

    // ==================== 3. 合法盘点：有符号差异过账与冲销 ====================

    [Fact]
    public async Task Valid_signed_count_is_posted_and_reversed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productA = await SeedProductAsync(db, "盘亏商品");
        var productB = await SeedProductAsync(db, "盘盈商品");
        await SeedStockAsync(db, warehouseId, productA, 10m, 100m);      // 均价 10
        await SeedStockAsync(db, warehouseId, productB, 5m, 20m);        // 均价 4
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);

        var id = CreatedId(await controller.Create(Draft(warehouseId,
            Line(productA, 10m, 7m, 10m),      // 盘亏 -3 × 10 = -30
            Line(productB, 5m, 9m, 4m))));     // 盘盈 +4 × 4  = +16

        var document = await db.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(1m, document.TotalDiffQuantity);
        Assert.Equal(-14m, document.TotalDiffAmount);

        await controller.Submit(id);
        await controller.Approve(id);

        var first = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productA);
        Assert.Equal(7m, first.Quantity);
        Assert.Equal(70m, first.TotalCost);
        var second = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productB);
        Assert.Equal(9m, second.Quantity);
        Assert.Equal(36m, second.TotalCost);

        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceDocType == InventoryDocumentHelper.StockAdjustmentType && m.SourceDocId == id)
            .OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(-1, movements[0].Direction);
        Assert.Equal(3m, movements[0].Quantity);
        Assert.Equal(-30m, movements[0].Amount);
        Assert.Equal(1, movements[1].Direction);
        Assert.Equal(4m, movements[1].Quantity);
        Assert.Equal(16m, movements[1].Amount);
        Assert.Equal(DocumentStatus.Approved,
            await db.StockAdjustments.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).SingleAsync());

        // 销审：按红字流水冲销还原，历史流水不删除（可审计）
        await controller.Unaudit(id);

        first = await db.Stocks.AsNoTracking().SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productA);
        Assert.Equal(10m, first.Quantity);
        Assert.Equal(100m, first.TotalCost);
        second = await db.Stocks.AsNoTracking().SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productB);
        Assert.Equal(5m, second.Quantity);
        Assert.Equal(20m, second.TotalCost);
        Assert.Equal(DocumentStatus.Pending,
            await db.StockAdjustments.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).SingleAsync());

        var all = await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceDocType == InventoryDocumentHelper.StockAdjustmentType && m.SourceDocId == id)
            .OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(4, all.Count);
        Assert.Equal(2, all.Count(m => m.IsReversal));
    }

    // ==================== 测试辅助 ====================

    private static StockAdjustmentController CreateController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };

    private static StockAdjustment Draft(long warehouseId, params StockAdjustmentDetail[] lines) => new()
    {
        AdjustmentDate = DateTime.Today,
        WarehouseId = warehouseId,
        AdjustType = "盘点调整",
        Remark = "ERP-507_SQL_TEST",
        Details = lines.ToList()
    };

    private static StockAdjustmentDetail Line(long productId, decimal book, decimal actual, decimal cost,
        bool isDeleted = false) => new()
        {
            ProductId = productId,
            ProductName = $"P{productId}",
            Spec = "规格A",
            Unit = "PCS",
            BookQuantity = book,
            ActualQuantity = actual,
            UnitCost = cost,
            IsDeleted = isDeleted
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    /// <summary>调用前快照：本用例仓库名下的单据 / 明细 / 库存 / 流水与盘点单号序列基线。</summary>
    private static async Task<BoundsSnapshot> SnapshotAsync(ErpDbContext db, long warehouseId)
        => new(
            await db.StockAdjustments.CountAsync(a => a.WarehouseId == warehouseId),
            await DetailCountAsync(db, warehouseId),
            await db.Stocks.CountAsync(s => s.WarehouseId == warehouseId),
            await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId),
            await SequenceAsync(db));

    /// <summary>拒绝场景的统一断言：单据 / 明细 / 库存 / 流水 / 单号序列相对快照逐项不变。</summary>
    private static async Task AssertNoWriteAsync(ErpDbContext db, BoundsSnapshot before, long warehouseId)
    {
        Assert.Equal(before.Documents, await db.StockAdjustments.CountAsync(a => a.WarehouseId == warehouseId));
        Assert.Equal(before.Details, await DetailCountAsync(db, warehouseId));
        Assert.Equal(before.Stocks, await db.Stocks.CountAsync(s => s.WarehouseId == warehouseId));
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId));
        Assert.Equal(before.Sequence, await SequenceAsync(db));            // 拒绝方绝不消耗单据号
    }

    private static Task<int> DetailCountAsync(ErpDbContext db, long warehouseId)
        => (from d in db.StockAdjustmentDetails
            join a in db.StockAdjustments on d.StockAdjustmentId equals a.Id
            where a.WarehouseId == warehouseId
            select d).CountAsync();

    private static async Task<long?> SequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockAdjustment && !r.IsDeleted)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    /// <summary>调用前基线快照（同一夹具库被多个用例复用，因此拒绝断言一律相对快照比较）。</summary>
    private sealed record BoundsSnapshot(int Documents, int Details, int Stocks, int Movements, long? Sequence);

    private static string ColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=(localdb)\\NEWERP_AutoAcceptance;" +
                          "Database=NEWERP_AUTOTEST_ERP507_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>），用于证明实际存储精度与规则常量同源。</summary>
    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"PDW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"PDP-{Tag()}", ProductName = name, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种真实授权账号：既有 stock-query 菜单（种子数据已含）+ 角色授权 + 启用账号。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db)
    {
        var role = new SysRole { RoleCode = $"PD-BOUNDS-{Tag()}", RoleName = "盘点数值边界操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"pd-bounds-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "盘点数值边界操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m =>
            m.MenuCode == StockAdjustmentPostingRules.RequiredMenuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-507）：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockAdjustmentNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKADJUSTNUMERIC_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-507] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-507] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockAdjustmentNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockAdjustmentNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}

