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
/// ERP-501 仓库调拨草稿写入护栏的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockTransferController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建对「仓库 Id 非正 / 调出调入相同 / 不存在 / 已软删除 / 已停用」与
/// 「数量非正或不可表示、成本为负或不可表示、商品缺失 / 不存在 / 已删除 / 已停用」一律拒绝，
/// 且零单据、零明细、零库存流水、零单号消耗；</item>
/// <item>修改在明细整体替换之前收敛同样的非法输入，既有单据与明细逐字段不变、不消耗新单号；</item>
/// <item>重复商品行与空草稿头保持既有语义（累计数量合并、空头可保存但提交要求有效明细），
/// 合法草稿创建 / 修改成功且单号语义不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockTransferDraftValidationSqlServerTests
    : IClassFixture<StockTransferDraftValidationSqlServerFixture>
{
    private readonly StockTransferDraftValidationSqlServerFixture _fixture;

    public StockTransferDraftValidationSqlServerTests(StockTransferDraftValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockTransferDraftValidationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 创建：非法仓库（零写入 / 零单号） ====================

    [Theory]
    [InlineData("from-zero", ErrorCodes.InvalidParameter)]
    [InlineData("to-zero", ErrorCodes.InvalidParameter)]
    [InlineData("from-negative", ErrorCodes.InvalidParameter)]
    [InlineData("same", ErrorCodes.RuleConflict)]
    [InlineData("missing-from", ErrorCodes.InvalidParameter)]
    [InlineData("missing-to", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-to", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-from", ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_warehouse_before_any_write(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "调拨商品");
        var userId = await SeedUserAsync(db);

        if (scenario == "deleted-to") await SetWarehouseFlagsAsync(db, toId, isDeleted: true, status: null);
        if (scenario == "disabled-from") await SetWarehouseFlagsAsync(db, fromId, isDeleted: null, status: 0);

        var (from, to) = scenario switch
        {
            "from-zero" => (0L, toId),
            "to-zero" => (fromId, 0L),
            "from-negative" => (-3L, toId),
            "same" => (fromId, fromId),
            "missing-from" => (999_999_999L, toId),
            "missing-to" => (fromId, 999_999_999L),
            _ => (fromId, toId)
        };

        var before = await SnapshotAsync(db);

        var controller = CreateController(db, userId);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(from, to, (productId, 5m, 1m))));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoDocumentOrNumberChangeAsync(db, before);

        // 本用例自己播种的两侧仓库不得出现任何单据（拒绝方不落任何行）
        Assert.Equal(0, await db.StockTransfers.CountAsync(t =>
            t.FromWarehouseId == fromId || t.ToWarehouseId == toId));
    }

    // ==================== 2. 创建：非法明细（数量 / 成本 / 商品） ====================

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-cost", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cost", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_lines_before_any_write(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "调拨商品");
        var userId = await SeedUserAsync(db);

        if (scenario == "deleted-product") await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product") await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);

        var (lineProductId, quantity, unitCost) = scenario switch
        {
            "zero-quantity" => ((long?)productId, 0m, 1m),
            "negative-quantity" => ((long?)productId, -1m, 1m),
            "unrepresentable-quantity" => ((long?)productId, 1.00001m, 1m),
            "negative-cost" => ((long?)productId, 1m, -1m),
            "unrepresentable-cost" => ((long?)productId, 1m, 0.0000001m),
            "missing-product-id" => ((long?)0L, 1m, 1m),
            "missing-product" => ((long?)999_999_999L, 1m, 1m),
            _ => ((long?)productId, 1m, 1m)
        };

        var before = await SnapshotAsync(db);

        var controller = CreateController(db, userId);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(fromId, toId, (lineProductId, quantity, unitCost))));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoDocumentOrNumberChangeAsync(db, before);

        // 本用例自己播种的两侧仓库不得出现任何单据 / 库存流水（拒绝方不落任何行）
        Assert.Equal(0, await db.StockTransfers.CountAsync(t =>
            t.FromWarehouseId == fromId || t.ToWarehouseId == toId));
        Assert.Equal(0, await db.StockMovements.CountAsync(m =>
            m.WarehouseId == fromId || m.WarehouseId == toId));
    }

    // ==================== 3. 修改：非法草稿零改动 ====================

    [Theory]
    [InlineData("same-warehouse", ErrorCodes.RuleConflict)]
    [InlineData("missing-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-warehouse", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cost", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Update_rejects_invalid_draft_and_keeps_existing_rows(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "调拨商品");
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);

        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, 5m, 2m))));
        var transferNo = await db.StockTransfers.Where(t => t.Id == id)
            .Select(t => t.TransferNo).SingleAsync();
        var before = await SnapshotAsync(db);

        // 场景性主数据变更发生在合法草稿创建之后：修改路径必须在同一套护栏下收敛
        if (scenario == "disabled-warehouse") await SetWarehouseFlagsAsync(db, toId, null, 0);
        if (scenario == "disabled-product") await SetProductFlagsAsync(db, productId, null, 0);

        var (from, to) = scenario switch
        {
            "same-warehouse" => (fromId, fromId),
            "missing-warehouse" => (fromId, 999_999_999L),
            _ => (fromId, toId)
        };
        var (lineProductId, quantity, unitCost) = scenario switch
        {
            "zero-quantity" => ((long?)productId, 0m, 1m),
            "unrepresentable-cost" => ((long?)productId, 1m, 0.0000001m),
            "missing-product" => ((long?)999_999_999L, 1m, 1m),
            _ => ((long?)productId, 7m, 9m)
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Update(id, Draft(from, to, (lineProductId, quantity, unitCost))));

        Assert.Equal(expectedCode, error.Code);

        // 既有单据 / 明细逐字段不变，未消耗新单号，未产生流水
        var after = await db.StockTransfers.SingleAsync(t => t.Id == id);
        Assert.Equal(transferNo, after.TransferNo);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(fromId, after.FromWarehouseId);
        Assert.Equal(toId, after.ToWarehouseId);
        Assert.Equal(5m, after.TotalQuantity);
        Assert.Equal(10m, after.TotalAmount);

        var detail = await db.StockTransferDetails.SingleAsync(d => d.StockTransferId == id);
        Assert.Equal(productId, detail.ProductId);
        Assert.Equal(5m, detail.Quantity);
        Assert.Equal(2m, detail.UnitCost);
        Assert.Equal(10m, detail.Amount);

        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.RuleSequence, await CurrentTransferSequenceAsync(db));
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
    }

    // ==================== 4. 既有支持语义：重复商品行与空草稿头 ====================

    [Fact]
    public async Task Repeated_lines_and_empty_header_keep_existing_semantics()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "调拨商品");
        var userId = await SeedUserAsync(db);
        var controller = CreateController(db, userId);
        var before = await SnapshotAsync(db);

        // 重复商品行受支持：数量按累计口径合并，明细保留两行
        var repeatedId = CreatedId(await controller.Create(Draft(fromId, toId,
            (productId, 3m, 2m), (productId, 2m, 2m))));

        var repeated = await db.StockTransfers.SingleAsync(t => t.Id == repeatedId);
        Assert.Equal(5m, repeated.TotalQuantity);
        Assert.Equal(10m, repeated.TotalAmount);
        Assert.Equal(2, await db.StockTransferDetails.CountAsync(d => d.StockTransferId == repeatedId));

        // 空草稿头仍可保存，但提交要求至少一行有效明细
        var emptyId = CreatedId(await controller.Create(Draft(fromId, toId)));
        var submit = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(emptyId));
        Assert.Equal(ErrorCodes.RuleConflict, submit.Code);
        Assert.Equal(DocumentStatus.Pending,
            await db.StockTransfers.Where(t => t.Id == emptyId).Select(t => t.Status).SingleAsync());

        // 合法草稿可提交，状态按既有流转推进，且审核前不产生任何库存流水
        await controller.Submit(repeatedId);
        Assert.Equal(DocumentStatus.Submitted,
            await db.StockTransfers.Where(t => t.Id == repeatedId).Select(t => t.Status).SingleAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());

        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        var sequence = await CurrentTransferSequenceAsync(db);
        Assert.True(sequence == (before.RuleSequence ?? 0L) + 2L);   // 两次创建恰好消耗两个单号（重复行合并、空草稿头各算一次）
    }

    // ==================== 测试辅助 ====================

    private static StockTransferController CreateController(ErpDbContext db, long? userId)
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

    private static StockTransfer Draft(long fromWarehouseId, long toWarehouseId,
        params (long? ProductId, decimal Quantity, decimal UnitCost)[] lines)
        => new()
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Remark = "ERP-501_SQL_TEST",
            Details = lines.Select(l => new StockTransferDetail
            {
                ProductId = l.ProductId,
                ProductName = $"P{l.ProductId}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = l.Quantity,
                UnitCost = l.UnitCost
            }).ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    /// <summary>调用前快照：单据 / 明细 / 流水 / 单号规则与调拨流水号的基线（拒绝场景必须逐项不变）。</summary>
    private static async Task<DraftSnapshot> SnapshotAsync(ErpDbContext db)
        => new(
            await db.StockTransfers.CountAsync(),
            await db.StockTransferDetails.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.SysDocumentNumberRules.CountAsync(),
            await CurrentTransferSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：单据 / 明细 / 流水 / 单号规则与调拨流水号相对快照逐项不变。</summary>
    private static async Task AssertNoDocumentOrNumberChangeAsync(ErpDbContext db, DraftSnapshot before)
    {
        Assert.Equal(before.Transfers, await db.StockTransfers.CountAsync());
        Assert.Equal(before.Details, await db.StockTransferDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.RuleSequence, await CurrentTransferSequenceAsync(db));   // 拒绝方绝不消耗单据号
    }

    /// <summary>调用前基线快照（同一夹具库被多个用例复用，因此拒绝断言一律相对快照比较）。</summary>
    private sealed record DraftSnapshot(int Transfers, int Details, int Movements, int NumberRules,
        long? RuleSequence);

    private static async Task<long?> CurrentTransferSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockTransfer)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"STDW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"STDP-{Tag()}", ProductName = name, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

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

    /// <summary>播种真实授权账号：既有 stock-query 菜单（种子数据已含）+ 角色授权 + 启用账号。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db)
    {
        var role = new SysRole { RoleCode = $"ST-DRAFT-{Tag()}", RoleName = "调拨草稿操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"st-draft-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "调拨草稿操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m =>
            m.MenuCode == StockTransferPostingRules.RequiredMenuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-501）：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockTransferDraftValidationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKTRANSFERDRAFT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-501] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-501] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockTransferDraftValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockTransferDraftValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
