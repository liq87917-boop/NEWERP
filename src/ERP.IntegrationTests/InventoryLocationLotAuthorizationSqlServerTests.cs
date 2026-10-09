using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-436 库位 + 批次库存基础（<c>api/inventory/location-lot</c> 的 <c>GET balances</c> / <c>POST validate</c>）
/// 实时授权与数据范围的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>缺失 / 禁用 / 已删除 / 无「库存查询」菜单 / 受限身份（无权威仓库级范围）：余额与校验端点一律
/// 在读取任何数量之前 fail closed，且库存 / 流水计数与数量不变；</item>
/// <item>真实种子特权库存查询账号（系统内置角色 + 既有 stock-query 菜单）可读库位 / 批次余额并校验移动身份，
/// 请求仓库 / 商品筛选只能收窄，且不产生任何写入；</item>
/// <item>请求之间撤销菜单授权立即收敛为拒绝；</item>
/// <item>不存在 / 跨仓（foreign）仓库、商品、库位与批次输入被拒绝或返回空，且不改动任何库存 / 流水行。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class InventoryLocationLotAuthorizationSqlServerTests
    : IClassFixture<InventoryLocationLotAuthorizationSqlServerFixture>
{
    private readonly InventoryLocationLotAuthorizationSqlServerFixture _fixture;

    public InventoryLocationLotAuthorizationSqlServerTests(InventoryLocationLotAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(InventoryLocationLotAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 拒绝身份 fail closed 且库存 / 流水不变 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    [InlineData("restricted")]
    public async Task Denied_identities_read_no_balances_validate_nothing_and_mutate_no_rows(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "库位批次拒绝仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseId, productId, quantity: 25m, totalCost: 250m);
        await SeedMovementAsync(db, warehouseId, productId, $"PD-INVLOT-{Tag()}");

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
        var quantityBefore = await db.Stocks.SumAsync(s => s.Quantity);

        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var ctl = NewController(db, userId);

        // 全量 / 显式仓库筛选 / 移动校验三个入口共用同一授权入口，先于任何数量读取。
        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetBalances(null, null))).Code);
        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetBalances(warehouseId, productId))).Code);
        Assert.Equal(expectedCode,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Validate(StockIn(warehouseId, productId)))).Code);

        Assert.Equal(25m, await db.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(stocksBefore, await db.Stocks.CountAsync());
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
        Assert.Equal(quantityBefore, await db.Stocks.SumAsync(s => s.Quantity));
    }


    // ==================== 2. 真实种子特权账号放行并保留既有语义 ====================

    [Fact]
    public async Task Seeded_privileged_stock_query_user_reads_balances_and_validates_movement_without_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "库位批次放行仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseId, productId, quantity: 14m, totalCost: 140m);
        await SeedMovementAsync(db, warehouseId, productId, $"PD-INVLOT-{Tag()}");

        var stocksBefore = await db.Stocks.CountAsync();
        var movementsBefore = await db.StockMovements.CountAsync();
        var quantityBefore = await db.Stocks.SumAsync(s => s.Quantity);

        var userId = await SeedPrivilegedReaderAsync(db);
        var ctl = NewController(db, userId);

        // 库位级 + 商品级余额（既有响应契约：数量 / 成本口径不变）。
        var report = AssertOk<LocationLotBalanceReport>(await ctl.GetBalances(warehouseId, productId));
        Assert.True(report.IsReconciled);
        var line = Assert.Single(report.LocationLines);
        Assert.Equal(warehouseId, line.WarehouseId);
        Assert.Equal(productId, line.ProductId);
        Assert.Equal(14m, line.Quantity);
        Assert.Equal(14m, Assert.Single(report.ProductTotals).Quantity);

        // 显式仓库 / 商品筛选只能收窄：范围外 Id 返回空，不泄露其它仓库 / 商品数量。
        var foreign = AssertOk<LocationLotBalanceReport>(await ctl.GetBalances(warehouseId + 987_654L, productId));
        Assert.Empty(foreign.LocationLines);
        Assert.Empty(foreign.ProductTotals);

        // 移动校验保留既有归一化语义（库位 / 批次去空白），且不写任何库存 / 流水。
        var validated = AssertOk<ValidatedMovementLocationLot>(await ctl.Validate(StockIn(warehouseId, productId)));
        Assert.Equal(warehouseId, validated.WarehouseId);
        Assert.Equal("A-01", validated.LocationCode);
        Assert.Equal("L-AUTH", validated.LotNo);

        Assert.Equal(stocksBefore, await db.Stocks.CountAsync());
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
        Assert.Equal(quantityBefore, await db.Stocks.SumAsync(s => s.Quantity));
    }

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "库位批次撤销仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseId, productId, quantity: 7m, totalCost: 70m);

        var userId = await SeedPrivilegedReaderAsync(db);
        AssertOk<LocationLotBalanceReport>(
            await NewController(db, userId).GetBalances(warehouseId, productId));   // 授权读取成功

        // 请求之间回收「角色 → 菜单」授权：下一次请求立即收敛为拒绝（每次请求重新解析，绝不缓存）。
        await RevokeMenuGrantsAsync(db);

        var revoked = NewController(db, userId);
        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => revoked.GetBalances(warehouseId, productId))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => revoked.Validate(StockIn(warehouseId, productId)))).Code);

        Assert.Equal(7m, await db.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
    }

    // ==================== 3. 非法 / 跨仓输入拒绝且零变更 ====================

    [Fact]
    public async Task Invalid_and_foreign_inputs_are_rejected_or_empty_without_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "库位批次非法输入仓");
        var otherWarehouseId = await SeedWarehouseAsync(db, "库位批次非法输入邻仓");
        var productId = await SeedProductAsync(db);
        await SeedStockAsync(db, warehouseId, productId, quantity: 5m, totalCost: 50m);

        var stocksBefore = await db.Stocks.CountAsync();
        var movementsBefore = await db.StockMovements.CountAsync();
        var quantityBefore = await db.Stocks.SumAsync(s => s.Quantity);

        var ctl = NewController(db, await SeedPrivilegedReaderAsync(db));

        // 不存在 / 跨仓仓库：校验 fail closed（不泄露仓库或库存是否存在）。
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() =>
                ctl.Validate(StockIn(999_000_001L, productId)))).Code);

        // 非正数量。
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Validate(new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.StockOut,
                WarehouseId = warehouseId,
                ProductId = productId,
                ProductName = "集成测试商品",
                Quantity = 0m
            }))).Code);

        // 库位 / 批次超长。
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Validate(new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.StockIn,
                WarehouseId = warehouseId,
                ProductId = productId,
                ProductName = "集成测试商品",
                LocationCode = new string('X', InventoryLocationLotRules.IdentityMaxLength + 1),
                Quantity = 1m
            }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Validate(new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.StockIn,
                WarehouseId = warehouseId,
                ProductId = productId,
                ProductName = "集成测试商品",
                LotNo = new string('Y', InventoryLocationLotRules.IdentityMaxLength + 1),
                Quantity = 1m
            }))).Code);

        // 调拨两仓相同 / 调出仓现存量不足（拒绝负库存）。
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Validate(new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.Transfer,
                WarehouseId = warehouseId,
                ToWarehouseId = warehouseId,
                ProductId = productId,
                ProductName = "集成测试商品",
                Quantity = 1m
            }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Validate(new MovementLocationLotInput
            {
                Kind = InventoryLocationLotMovementKind.Transfer,
                WarehouseId = warehouseId,
                ToWarehouseId = otherWarehouseId,
                ProductId = productId,
                ProductName = "集成测试商品",
                Quantity = 999m
            }))).Code);

        // 跨仓余额筛选返回空，绝不泄露他仓数量。
        var foreign = AssertOk<LocationLotBalanceReport>(await ctl.GetBalances(otherWarehouseId, productId));
        Assert.Empty(foreign.LocationLines);

        Assert.Equal(stocksBefore, await db.Stocks.CountAsync());
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
        Assert.Equal(quantityBefore, await db.Stocks.SumAsync(s => s.Quantity));
    }

    // ==================== 控制器工厂 / 身份注入 ====================

    private static InventoryLocationLotController NewController(ErpDbContext db, long? userId)
    {
        var controller = new InventoryLocationLotController(new InventoryLocationLotService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static T AssertOk<T>(IActionResult result)
        => Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static MovementLocationLotInput StockIn(long warehouseId, long productId)
        => new()
        {
            Kind = InventoryLocationLotMovementKind.StockIn,
            WarehouseId = warehouseId,
            LocationCode = " A-01 ",
            LotNo = " L-AUTH ",
            ProductId = productId,
            ProductName = "集成测试商品",
            Quantity = 1m
        };


    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"IL-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"IL-P-{Tag()}",
            ProductName = "库位批次测试商品",
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
            ProductName = "库位批次测试商品",
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
        var role = new SysRole { RoleCode = $"IL-PRIV-{Tag()}", RoleName = "库位批次特权账号", IsSystem = true };
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
        var role = new SysRole { RoleCode = $"IL-RESTRICTED-{Tag()}", RoleName = "受限库位批次账号", IsSystem = false };
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
        var role = new SysRole { RoleCode = $"IL-DISABLED-{Tag()}", RoleName = "禁用库位批次账号", IsSystem = false };
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
        var role = new SysRole { RoleCode = $"IL-DELETED-{Tag()}", RoleName = "已删除库位批次账号", IsSystem = false };
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
            UserName = $"il-{Tag()}",
            DisplayName = "库位批次集成测试账号",
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
/// ERP-436 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供库位 / 批次授权与数据范围集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class InventoryLocationLotAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_INVLOCLOTAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-436] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-436] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 stock-query 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class InventoryLocationLotAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            InventoryLocationLotAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

