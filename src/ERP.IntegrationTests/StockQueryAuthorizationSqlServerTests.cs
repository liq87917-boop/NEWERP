using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-356 库存查询实时授权与数据范围的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>身份 / 账号状态 / 既有 stock-query 菜单授权 / 无权威范围的受限身份 fail closed 时，库存与流水计数不变；</item>
/// <item>真实种子特权库存查询用户可读取列表 / 流水证据 / 汇总，且请求仓库筛选只能收窄；</item>
/// <item>请求之间撤销授权立即收敛；</item>
/// <item><b>两条独立连接竞争</b>：并发授权读取结果一致且不产生任何写入；撤销与读取分别发生在两条独立连接时，
///       后续读取收敛为拒绝（fail closed）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockQueryAuthorizationSqlServerTests
    : IClassFixture<StockQueryAuthorizationSqlServerFixture>
{
    private readonly StockQueryAuthorizationSqlServerFixture _fixture;

    public StockQueryAuthorizationSqlServerTests(StockQueryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockQueryAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    /// <summary>
    /// 与 <c>StockController</c> 同一入口：解析并校验当前账号的库存查询范围（fail closed）。
    /// 列表 / 流水证据 / 汇总在控制器中共用此入口，且先于任何计数、成本与来源单据读取。
    /// </summary>
    private static Task<StockQueryAuthorizationRules.StockQueryScope> AuthorizeAsync(ErpDbContext db, long? userId)
        => StockQueryAuthorizationRules.EnsureAuthorizedAsync(db, userId);

    // ==================== 1. 拒绝身份 fail closed 且库存 / 流水不变 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    [InlineData("restricted")]
    public async Task Denied_identities_read_no_inventory_and_leave_counts_unchanged(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "库存查询拒绝仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseId, productId, quantity: 25m, totalCost: 250m);
        await SeedMovementAsync(db, warehouseId, productId, "PD-DENY-0001");

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedDisabledUserAsync(db, withMenu: true),
            "deleted" => await SeedDeletedUserAsync(db, withMenu: true),
            "no-menu" => await SeedRestrictedUserAsync(db, withMenu: false),
            _ => await SeedRestrictedUserAsync(db, withMenu: true)
        };

        var stocksBefore = await db.Stocks.CountAsync();
        var movementsBefore = await db.StockMovements.CountAsync();

        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        // 列表 / 流水证据 / 汇总 / 显式仓库筛选在控制器中共用同一入口：
        // 缺失身份 / 禁用 / 已删除 / 无菜单 / 无权威范围的受限身份一律在读取任何计数或成本之前拒绝。
        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => AuthorizeAsync(db, userId))).Code);

        Assert.Equal(25m, await db.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(stocksBefore, await db.Stocks.CountAsync());
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
    }

    // ==================== 2. 真实种子特权库存查询用户放行并保留既有语义 ====================

    [Fact]
    public async Task Seeded_privileged_stock_query_user_reads_list_movements_and_summary()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseA = await SeedWarehouseAsync(db, "库存查询一号仓");
        var warehouseB = await SeedWarehouseAsync(db, "库存查询二号仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseA, productId, quantity: 10m, totalCost: 100m);
        await SeedStockAsync(db, warehouseB, productId, quantity: 4m, totalCost: 60m);
        var movementNo = $"PD-IT-{Guid.NewGuid():N}"[..24];
        await SeedMovementAsync(db, warehouseA, productId, movementNo);

        var userId = await SeedPrivilegedReaderAsync(db);
        var scope = await AuthorizeAsync(db, userId);

        // 列表 / 流水证据 / 汇总共用同一已授权范围（特权 = 既有全量可见性）。
        var stocks = StockQueryAuthorizationRules.ApplyScope(
            db.Stocks.AsNoTracking().Where(s => !s.IsDeleted), scope);
        Assert.Equal(2, await stocks.CountAsync());
        var row = await stocks.SingleAsync(s => s.WarehouseId == warehouseA);
        Assert.Equal(10m, row.Quantity);
        Assert.Equal(10m, row.AverageCost);
        Assert.Equal(100m, row.TotalCost);

        // 请求仓库筛选只能收窄，不能扩大。
        Assert.Equal(1, await stocks.CountAsync(s => s.WarehouseId == warehouseB));

        var movements = StockQueryAuthorizationRules.ApplyScope(
            db.StockMovements.AsNoTracking().Where(m => !m.IsDeleted), scope);
        var evidence = await movements.SingleAsync(m => m.SourceDocNo == movementNo);
        Assert.Equal("StockAdjustment", evidence.SourceDocType);     // 来源单据审计保留
        Assert.Equal(5m, evidence.Quantity);
        Assert.Equal(50m, evidence.Amount);

        // 汇总（与控制器同一口径）：数量 / 可用数量 / 仓库数。
        Assert.Equal(14m, await stocks.SumAsync(s => s.Quantity));
        Assert.Equal(14m, await stocks.SumAsync(s => s.AvailableQuantity));
        Assert.Equal(2, await stocks.Select(s => s.WarehouseId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "库存查询撤销仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseId, productId, quantity: 7m, totalCost: 70m);

        var userId = await SeedPrivilegedReaderAsync(db);
        await AuthorizeAsync(db, userId);                  // 授权读取成功

        // 请求之间回收「角色 → 菜单」授权：下一次请求立即收敛为拒绝（每次请求重新解析，绝不缓存）。
        await RevokeMenuGrantsAsync(db);

        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => AuthorizeAsync(db, userId))).Code);

        Assert.Equal(7m, await db.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
    }

    // ==================== 3. 两条独立连接竞争（授权读取 / 撤销收敛） ====================

    [Fact]
    public async Task Two_connections_concurrent_authorized_reads_are_consistent_and_non_mutating()
    {
        Guard();
        long warehouseId, productId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(seed, "库存查询并发仓");
            productId = await SeedProductAsync(seed);
            await SeedStockAsync(seed, warehouseId, productId, quantity: 12m, totalCost: 144m);
            await SeedMovementAsync(seed, warehouseId, productId, $"PD-RACE-{Guid.NewGuid():N}"[..24]);
            userId = await SeedPrivilegedReaderAsync(seed);
        }

        // 两条独立连接（各自 DbContext / 连接）并发执行同一套授权读取。
        var results = await Task.WhenAll(
            ReadSnapshotAsync(userId, warehouseId),
            ReadSnapshotAsync(userId, warehouseId));

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, results[0].Total);
        Assert.Equal(12m, results[0].Quantity);
        Assert.Equal(12m, results[0].Available);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(1, await verify.Stocks.CountAsync(s => s.WarehouseId == warehouseId));
        Assert.Equal(12m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(1, await verify.StockMovements.CountAsync(m => m.WarehouseId == warehouseId));
    }

    [Fact]
    public async Task Two_connections_revocation_between_reads_converges_to_denial()
    {
        Guard();
        long warehouseId, productId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(seed, "库存查询撤销收敛仓");
            productId = await SeedProductAsync(seed);
            await SeedStockAsync(seed, warehouseId, productId, quantity: 9m, totalCost: 90m);
            userId = await SeedPrivilegedReaderAsync(seed);
        }

        // 连接 A：授权读取成功（独立连接 / 独立请求）。
        var allowed = await ReadSnapshotAsync(userId, warehouseId);
        Assert.Equal(1, allowed.Total);

        // 连接 B：在另一条独立连接上回收授权并提交。
        await using (var revoke = _fixture.CreateDbContext())
        {
            await RevokeMenuGrantsAsync(revoke);
        }

        // 连接 A 的下一次读取必须收敛为拒绝（每次请求重新解析，绝不缓存），且库存不变。
        await using (var denied = _fixture.CreateDbContext())
        {
            Assert.Equal(ErrorCodes.Forbidden,
                (await Assert.ThrowsAsync<BusinessException>(() => AuthorizeAsync(denied, userId))).Code);
            Assert.Equal(1, await denied.Stocks.CountAsync(s => s.WarehouseId == warehouseId));
            Assert.Equal(9m, await denied.Stocks.Where(s => s.WarehouseId == warehouseId)
                .Select(s => s.Quantity).SingleAsync());
        }
    }

    /// <summary>在**独立连接**上执行一次授权读取（列表 + 汇总），返回权威快照用于跨连接比对。</summary>
    private async Task<(long Total, decimal Quantity, decimal Available, int WarehouseCount)> ReadSnapshotAsync(
        long userId, long warehouseId)
    {
        await using var db = _fixture.CreateDbContext();
        var scope = await AuthorizeAsync(db, userId);
        var stocks = StockQueryAuthorizationRules.ApplyScope(
            db.Stocks.AsNoTracking().Where(s => !s.IsDeleted), scope);
        return (await stocks.CountAsync(s => s.WarehouseId == warehouseId),
            await stocks.Where(s => s.WarehouseId == warehouseId).SumAsync(s => s.Quantity),
            await stocks.SumAsync(s => s.AvailableQuantity),
            await stocks.Select(s => s.WarehouseId).Distinct().CountAsync());
    }

    // ==================== 种子助手（SQL 自增主键，不硬编码 Id） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"SQ-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SQ-P-{Tag()}",
            ProductName = "库存查询测试商品",
            Spec = "规格A",
            Unit = "PCS",
            Status = 1
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
            LockedQuantity = 0m,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedMovementAsync(ErpDbContext db, long warehouseId, long productId, string sourceDocNo)
    {
        db.StockMovements.Add(new StockMovement
        {
            MovementDate = DateTime.Today,
            MovementType = InventoryMovementType.Adjustment,
            SourceDocType = "StockAdjustment",
            SourceDocId = 1L,
            SourceDocNo = sourceDocNo,
            WarehouseId = warehouseId,
            WarehouseName = $"仓{warehouseId}",
            ProductId = productId,
            ProductCode = $"P{productId}",
            ProductName = "库存查询测试商品",
            Spec = "规格A",
            Unit = "PCS",
            Direction = 1,
            Quantity = 5m,
            UnitCost = 10m,
            Amount = 50m,
            BalanceQuantity = 5m,
            BalanceAmount = 50m,
            BalanceAverageCost = 10m
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种特权库存查询账号（系统内置角色 + 既有 stock-query 菜单授权）。</summary>
    private static async Task<long> SeedPrivilegedReaderAsync(ErpDbContext db)
    {
        var user = await SeedAccountAsync(db, UserStatus.Enabled, deleted: false);
        var role = new SysRole { RoleCode = $"SQ-PRIV-{Tag()}", RoleName = "库存查询特权账号", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user, RoleId = role.Id });
        await GrantMenuAsync(db, role.Id, StockQueryAuthorizationRules.RequiredMenuCode);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>播种受限账号（非系统内置角色、未映射业务员）：可选是否授予既有 stock-query 菜单。</summary>
    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, bool withMenu)
    {
        var user = await SeedAccountAsync(db, UserStatus.Enabled, deleted: false);
        var role = new SysRole { RoleCode = $"SQ-RESTRICTED-{Tag()}", RoleName = "受限库存查询账号", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user, RoleId = role.Id });
        if (withMenu)
            await GrantMenuAsync(db, role.Id, StockQueryAuthorizationRules.RequiredMenuCode);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<long> SeedDisabledUserAsync(ErpDbContext db, bool withMenu)
    {
        var user = await SeedAccountAsync(db, UserStatus.Disabled, deleted: false);
        var role = new SysRole { RoleCode = $"SQ-DISABLED-{Tag()}", RoleName = "禁用库存查询账号", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user, RoleId = role.Id });
        if (withMenu)
            await GrantMenuAsync(db, role.Id, StockQueryAuthorizationRules.RequiredMenuCode);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<long> SeedDeletedUserAsync(ErpDbContext db, bool withMenu)
    {
        var user = await SeedAccountAsync(db, UserStatus.Enabled, deleted: true);
        var role = new SysRole { RoleCode = $"SQ-DELETED-{Tag()}", RoleName = "已删除库存查询账号", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user, RoleId = role.Id });
        if (withMenu)
            await GrantMenuAsync(db, role.Id, StockQueryAuthorizationRules.RequiredMenuCode);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<long> SeedAccountAsync(ErpDbContext db, UserStatus status, bool deleted)
    {
        var user = new SysUser
        {
            UserName = $"sq-{Tag()}",
            DisplayName = "库存查询集成测试账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    /// <summary>回收当前全部「角色 → 菜单」授权（模拟请求之间撤销权限）。</summary>
    private static async Task RevokeMenuGrantsAsync(ErpDbContext db)
    {
        var grants = await db.SysRoleMenus.ToListAsync();
        foreach (var grant in grants)
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// ERP-356 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供库存查询授权 / 数据范围集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class StockQueryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_STOCKQUERYAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-356] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await InitialiseFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};" +
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

    private async Task InitialiseFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-356] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 stock-query 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockQueryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => StockQueryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
