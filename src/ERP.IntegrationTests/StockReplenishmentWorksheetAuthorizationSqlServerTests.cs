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
/// ERP-438 只读补货工作台的实时授权真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>以<b>真实控制器</b>（注入真实 HTTP 身份）复用既有 <see cref="StockQueryAuthorizationRules"/>（ERP-356）：
/// 实时身份 → 账号状态 → 既有「库存查询」（<c>stock-query</c>）菜单授权 → 权威数据范围，任一缺失即 fail closed。</para>
/// <list type="number">
/// <item>缺失 / 禁用 / 已删除 / 无菜单 / 无权威范围的受限身份：拒绝且不返回任何行，库存 / 商品 / 供应商 / 货源行不变；</item>
/// <item>真实种子特权身份：按既有 DTO / 建议 / 边界文案语义读取工作台，零写入；</item>
/// <item>空仓 / 不存在（foreign）的仓库输入：特权身份返回空行集，受限身份仍 fail closed；</item>
/// <item>请求之间撤销菜单授权：下一次请求立即收敛为拒绝。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockReplenishmentWorksheetAuthorizationSqlServerTests
    : IClassFixture<StockReplenishmentWorksheetAuthorizationSqlServerFixture>
{
    private readonly StockReplenishmentWorksheetAuthorizationSqlServerFixture _fixture;

    public StockReplenishmentWorksheetAuthorizationSqlServerTests(
        StockReplenishmentWorksheetAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockReplenishmentWorksheetAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    /// <summary>构建注入真实 HTTP 身份的补货工作台控制器（可空身份 = 无 <c>NameIdentifier</c>，由授权 fail closed 拒绝）。</summary>
    private static StockReplenishmentWorksheetController CreateController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new StockReplenishmentWorksheetController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static StockReplenishmentWorksheetDto WorksheetData(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<StockReplenishmentWorksheetDto>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private sealed record RowCounts(int Stocks, int Products, int Suppliers, int Relations);

    private static async Task<RowCounts> CountRowsAsync(ErpDbContext db)
        => new(
            await db.Stocks.CountAsync(),
            await db.BaseProducts.CountAsync(),
            await db.BaseSuppliers.CountAsync(),
            await db.BaseProductSuppliers.CountAsync());

    // ==================== 1. 拒绝矩阵：fail closed 且零写入 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    [InlineData("restricted")]
    public async Task Denied_identities_read_no_worksheet_and_mutate_no_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "补货工作台拒绝仓");
        var productId = await SeedProductAsync(db, minStock: 10m, maxStock: 100m);
        await SeedStockAsync(db, warehouseId, productId, quantity: 5m);
        var supplierId = await SeedSupplierAsync(db);
        await SeedRelationAsync(db, productId, supplierId, preferred: true);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedNonPrivilegedUserAsync(db, UserStatus.Disabled, deleted: false, withMenu: true),
            "deleted" => await SeedNonPrivilegedUserAsync(db, UserStatus.Enabled, deleted: true, withMenu: true),
            "no-menu" => await SeedNonPrivilegedUserAsync(db, UserStatus.Enabled, deleted: false, withMenu: false),
            _ => await SeedNonPrivilegedUserAsync(db, UserStatus.Enabled, deleted: false, withMenu: true)
        };

        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var before = await CountRowsAsync(db);

        // 先于任何库存 / 阈值 / 货源字段读取拒绝：不返回任何行。
        var error = await Assert.ThrowsAsync<BusinessException>(() => CreateController(db, userId)
            .GetWorksheet(new StockReplenishmentWorksheetQuery { WarehouseId = warehouseId }));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(before, await CountRowsAsync(db));
        Assert.Equal(5m, await db.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
    }

    // ==================== 2. 特权身份：既有契约放行且零写入 ====================

    [Fact]
    public async Task Privileged_identity_reads_worksheet_with_existing_contract_and_no_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "补货工作台特权仓");
        var productId = await SeedProductAsync(db, minStock: 10m, maxStock: 100m);
        await SeedStockAsync(db, warehouseId, productId, quantity: 5m);
        var supplierId = await SeedSupplierAsync(db);
        await SeedRelationAsync(db, productId, supplierId, preferred: true);
        var userId = await SeedPrivilegedReaderAsync(db);

        var before = await CountRowsAsync(db);

        var data = WorksheetData(await CreateController(db, userId).GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = warehouseId }));

        var row = Assert.Single(data.Items);
        Assert.Equal(warehouseId, row.WarehouseId);
        Assert.Equal(5m, row.Quantity);
        Assert.Equal(5m, row.AvailableQuantity);
        Assert.Equal(10m, row.MinStock);
        Assert.Equal(100m, row.MaxStock);
        Assert.True(row.BelowMinimum);
        Assert.Equal(95m, row.SuggestedTopUp);                       // 100 − 5
        Assert.Equal(StockReplenishmentRules.StateReplenish, row.Recommendation);
        var sourcing = Assert.Single(row.Sourcing);
        Assert.Equal(supplierId, sourcing.SupplierId);
        Assert.True(sourcing.SupplierAvailable);

        // 既有 StockReplenishmentWorksheetDto / 只读 / 边界 / 免责文案契约保持不变。
        Assert.Equal(StockReplenishmentRules.ReadOnlyText, data.ReadOnlyText);
        Assert.Equal(StockReplenishmentRules.BoundaryText, data.BoundaryText);
        Assert.Equal(StockReplenishmentRules.DisclaimerText, data.DisclaimerText);

        Assert.Equal(before, await CountRowsAsync(db));
    }

    // ==================== 3. 空仓 / 外部仓库输入 ====================

    [Fact]
    public async Task Empty_or_foreign_warehouse_inputs_return_no_rows_and_deny_restricted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var emptyWarehouseId = await SeedWarehouseAsync(db, "补货工作台空仓");
        var privileged = await SeedPrivilegedReaderAsync(db);
        var controller = CreateController(db, privileged);

        // 存在但无库存行：返回空行集，不猜任何数量。
        var empty = WorksheetData(await controller.GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = emptyWarehouseId }));
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Total);

        // 不存在（foreign）的仓库 Id：不猜归属、不返回任何行。
        var foreign = WorksheetData(await controller.GetWorksheet(
            new StockReplenishmentWorksheetQuery { WarehouseId = 987654321L }));
        Assert.Empty(foreign.Items);
        Assert.Equal(0, foreign.Total);

        // 受限账号（非特权）即使显式指定单一仓库仍是「全局库存读取」→ 拒绝，不返回任何行。
        var restricted = await SeedNonPrivilegedUserAsync(db, UserStatus.Enabled, deleted: false, withMenu: true);
        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => CreateController(db, restricted)
                .GetWorksheet(new StockReplenishmentWorksheetQuery { WarehouseId = emptyWarehouseId }))).Code);
    }

    // ==================== 4. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "补货工作台撤销仓");
        var productId = await SeedProductAsync(db, minStock: 10m, maxStock: 100m);
        await SeedStockAsync(db, warehouseId, productId, quantity: 7m);
        var userId = await SeedPrivilegedReaderAsync(db);

        WorksheetData(await CreateController(db, userId).GetWorksheet(       // 授权读取成功
            new StockReplenishmentWorksheetQuery { WarehouseId = warehouseId }));

        // 请求之间回收「角色 → 菜单」授权：下一次请求立即收敛为拒绝（每次请求重新解析，绝不缓存）。
        await RevokeMenuGrantsAsync(db);

        Assert.Equal(ErrorCodes.Forbidden,
            (await Assert.ThrowsAsync<BusinessException>(() => CreateController(db, userId)
                .GetWorksheet(new StockReplenishmentWorksheetQuery { WarehouseId = warehouseId }))).Code);

        Assert.Equal(7m, await db.Stocks.Where(s => s.WarehouseId == warehouseId)
            .Select(s => s.Quantity).SingleAsync());
    }

    // ==================== 5. 自包含播种（真实既有授权模型） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"RW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, decimal minStock, decimal maxStock)
    {
        var product = new BaseProduct
        {
            ProductCode = $"RW-P-{Tag()}",
            ProductName = "补货工作台测试商品",
            Spec = "规格A",
            Unit = "PCS",
            Status = 1,
            MinStock = minStock,
            MaxStock = maxStock
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity
        });
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"RW-S-{Tag()}",
            SupplierName = "补货工作台测试供应商",
            Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task SeedRelationAsync(ErpDbContext db, long productId, long supplierId, bool preferred)
    {
        db.BaseProductSuppliers.Add(new BaseProductSupplier
        {
            ProductId = productId,
            SupplierId = supplierId,
            ScopeKey = ProductSupplierRules.BuildScopeKey(null),
            SupplierItemCode = "RW-ITEM",
            PurchaseUnit = "箱",
            MinOrderQty = 10m,
            LeadTimeDays = 7,
            IsPreferred = preferred,
            Status = 1
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种特权补货工作台账号（系统内置角色 <c>IsSystem == true</c> + 既有 stock-query 菜单授权）。</summary>
    private static async Task<long> SeedPrivilegedReaderAsync(ErpDbContext db)
    {
        var user = await SeedAccountAsync(db, UserStatus.Enabled, deleted: false);
        var role = new SysRole { RoleCode = $"RW-PRIV-{Tag()}", RoleName = "补货工作台特权账号", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user, RoleId = role.Id });
        await GrantMenuAsync(db, role.Id, StockQueryAuthorizationRules.RequiredMenuCode);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>播种非特权账号（非系统内置角色、未映射业务员）：可选是否授予既有 stock-query 菜单。</summary>
    private static async Task<long> SeedNonPrivilegedUserAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool withMenu)
    {
        var user = await SeedAccountAsync(db, status, deleted);
        var role = new SysRole { RoleCode = $"RW-USER-{Tag()}", RoleName = "补货工作台受限账号", IsSystem = false };
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
            UserName = $"rw-{Tag()}",
            DisplayName = "补货工作台集成测试账号",
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
/// ERP-438 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供补货工作台授权 / 数据范围集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class StockReplenishmentWorksheetAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_REPLENISHWORKAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-438] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-438] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 stock-query 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockReplenishmentWorksheetAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockReplenishmentWorksheetAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

