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
/// ERP-370 销售出库单实时授权与数据范围护栏 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item>无身份 / 禁用 / 无既有「销售出库」菜单授权：经**真实控制器**在写入前 fail closed，单据 / 库存 / 流水不变；</item>
/// <item>既有种子管理员（<c>SeedData</c> 已授予全部菜单）合法放行；范围外（受限操作员）客户被拒绝；</item>
/// <item><b>两条独立连接竞争</b>：并发「审核 vs 改单」与并发「提交 vs 删除」，经同一把出库单行锁
/// （<c>UPDLOCK, HOLDLOCK</c>）+ 可串行化事务串行化后只出现一种一致结果，库存 / 流水绝不重复过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockOutAuthorizationSqlServerTests : IClassFixture<StockOutAuthorizationSqlServerFixture>
{
    private readonly StockOutAuthorizationSqlServerFixture _fixture;

    private long CustomerA;
    private long CustomerB;
    private long WarehouseA;
    private long ProductA;
    private long RaceWarehouse;
    private long RaceProduct;

    public StockOutAuthorizationSqlServerTests(StockOutAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockOutAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // Each test owns real generated master ids, including separate inventory keys.
    private async Task InitializeMastersAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var key = Guid.NewGuid().ToString("N");
        var a = new BaseCustomer { CustomerCode = $"AUTH-A-{key}", CustomerName = "Owned", Status = 1 };
        var b = new BaseCustomer { CustomerCode = $"AUTH-B-{key}", CustomerName = "Foreign", Status = 1 };
        var warehouse = new BaseWarehouse { WarehouseCode = $"AUTH-W-{key}", WarehouseName = "Authorization", Status = 1 };
        var raceWarehouse = new BaseWarehouse { WarehouseCode = $"AUTH-RW-{key}", WarehouseName = "Race", Status = 1 };
        var product = new BaseProduct { ProductCode = $"AUTH-P-{key}", ProductName = "Product", Spec = "规格A", Unit = "PCS" };
        var raceProduct = new BaseProduct { ProductCode = $"AUTH-RP-{key}", ProductName = "Race product", Spec = "规格A", Unit = "PCS" };
        db.AddRange(a, b, warehouse, raceWarehouse, product, raceProduct);
        await db.SaveChangesAsync();
        CustomerA = a.Id; CustomerB = b.Id; WarehouseA = warehouse.Id;
        RaceWarehouse = raceWarehouse.Id; ProductA = product.Id; RaceProduct = raceProduct.Id;
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("no-menu")]
    public async Task Live_identity_menu_and_status_denials_leave_inventory_unchanged(string scenario)
    {
        Guard();
        await InitializeMastersAsync();
        await using var db = _fixture.CreateDbContext();
        long? userId = null;
        if (scenario != "missing")
        {
            userId = scenario == "disabled"
                ? await SeedUserAsync(db, "stockout-denied-disabled", UserStatus.Disabled, withStockOutMenu: false)
                : await SeedUserAsync(db, "stockout-denied-no-menu", UserStatus.Enabled, withStockOutMenu: false);
        }

        var document = await SeedStockOutAsync(db, $"DG-AUTH-DENIED-{Guid.NewGuid():N}", CustomerA, DocumentStatus.Pending);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m);
        await db.SaveChangesAsync();

        var beforeMovements = await db.StockMovements.CountAsync();
        var beforeStock = await db.Stocks.CountAsync();
        var ctl = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockOut(CustomerA, WarehouseA)));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, error.Code);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery(), null));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(document.Id));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(document.Id));

        Assert.Equal(beforeMovements, await db.StockMovements.CountAsync());
        Assert.Equal(beforeStock, await db.Stocks.CountAsync());
        Assert.Equal(DocumentStatus.Pending, (await db.StockOuts.AsNoTracking()
            .SingleAsync(o => o.Id == document.Id)).Status);
    }

    // ==================== 2. 既有种子账号合法放行 ====================

    [Fact]
    public async Task Seeded_admin_with_existing_stock_out_menu_is_admitted()
    {
        Guard();
        await InitializeMastersAsync();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, adminId);
        Assert.Contains(StockOutAuthorizationRules.RequiredMenuCode, menuCodes, StringComparer.OrdinalIgnoreCase);

        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(db, adminId, CustomerA);
        var controller = ControllerFor(db, adminId);
        Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));

        var created = await CreateAndSubmitAsync(db, adminId);
        Assert.IsType<OkObjectResult>(await controller.Approve(created.Id));
        Assert.Equal(DocumentStatus.Approved, (await db.StockOuts.AsNoTracking()
            .SingleAsync(o => o.Id == created.Id)).Status);
    }

    // ==================== 3. 两条独立连接竞争：审核 vs 改单 ====================

    [Fact]
    public async Task Real_two_connections_racing_approve_and_edit_keep_single_posting()
    {
        Guard();
        await InitializeMastersAsync();
        long adminId;
        long documentId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(seeding);
            documentId = (await SeedStockOutAsync(seeding, $"DG-AUTH-RACE-EDIT-{Guid.NewGuid():N}",
                CustomerA, DocumentStatus.Submitted, RaceWarehouse, RaceProduct, quantity: 4m)).Id;
            SeedStock(seeding, RaceWarehouse, RaceProduct, quantity: 10m);
            await seeding.SaveChangesAsync();
        }

        var race = await RaceAsync(
            () => TryApproveAsync(documentId, adminId),
            () => TryEditAsync(documentId, adminId));

        // 只有审核成功；并发改单在行锁内看到已审核状态 → 明确拒绝，绝不会改变已审核明细。
        Assert.Equal(1, race.Count(r => r.Ok));
        Assert.IsType<OkObjectResult>(race.Single(r => r.Ok).Result);
        var rejected = Assert.IsType<BusinessException>(Assert.Single(race.Where(r => !r.Ok)).Error);
        Assert.Equal(ErrorCodes.RuleConflict, rejected.Code);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.StockOuts.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == documentId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(4m, Assert.Single(stored.Details).Quantity);
        Assert.Equal(6m, (await verify.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == RaceWarehouse && s.ProductId == RaceProduct)).Quantity);
        Assert.Equal(1, await verify.StockMovements.CountAsync(m =>
            m.SourceDocId == documentId && m.MovementType == InventoryMovementType.SalesOut));
    }

    // ==================== 4. 两条独立连接竞争：提交 vs 删除 ====================

    [Fact]
    public async Task Real_two_connections_racing_submit_and_delete_leave_one_consistent_state()
    {
        Guard();
        await InitializeMastersAsync();
        long adminId;
        long documentId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(seeding);
            documentId = (await SeedStockOutAsync(seeding, $"DG-AUTH-RACE-STATE-{Guid.NewGuid():N}",
                CustomerA, DocumentStatus.Pending)).Id;
        }

        var race = await RaceAsync(
            () => TrySubmitAsync(documentId, adminId),
            () => TryDeleteAsync(documentId, adminId));

        // 恰好一方成功：提交赢 → 已提交且未删除；删除赢 → 待提交且已软删除（提交随后按不存在拒绝）。
        Assert.Equal(1, race.Count(r => r.Ok));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.StockOuts.AsNoTracking().SingleAsync(o => o.Id == documentId);
        if (race[0].Ok)
        {
            Assert.Equal(DocumentStatus.Submitted, stored.Status);
            Assert.False(stored.IsDeleted);
            var rejected = Assert.IsType<BusinessException>(race[1].Error);
            Assert.Equal(ErrorCodes.RuleConflict, rejected.Code);
        }
        else
        {
            Assert.True(stored.IsDeleted);
            Assert.Equal(DocumentStatus.Pending, stored.Status);
            Assert.IsType<BusinessException>(race[0].Error);
        }

        Assert.Empty(await verify.StockMovements.Where(m =>
            m.SourceDocId == documentId && m.MovementType == InventoryMovementType.SalesOut).ToListAsync());
    }

    // ==================== 5. 范围外受限操作员被拒绝 ====================

    [Fact]
    public async Task Restricted_operator_cannot_touch_foreign_customer_document()
    {
        Guard();
        await InitializeMastersAsync();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedRestrictedOperatorAsync(db, CustomerA);
        var foreign = await SeedStockOutAsync(db, $"DG-AUTH-FOREIGN-{Guid.NewGuid():N}", CustomerB,
            DocumentStatus.Pending);

        var ctl = ControllerFor(db, operatorId);
        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(foreign.Id));
        Assert.Equal(ErrorCodes.NotFound, error.Code);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(foreign.Id, NewStockOut(CustomerB, WarehouseA)));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockOut(CustomerB, WarehouseA)));

        Assert.Equal(DocumentStatus.Pending, (await db.StockOuts.AsNoTracking()
            .SingleAsync(o => o.Id == foreign.Id)).Status);
    }

    // ==================== 脚手架：真实控制器 / 真实身份 / 两条独立连接 ====================

    /// <summary>一次竞争动作的结果（成功结果或异常，绝不吞掉证据）。</summary>
    private readonly record struct RaceResult(bool Ok, IActionResult? Result, Exception? Error);

    /// <summary>让两个动作尽量同时起跑，并在<b>两条独立连接</b>上并行执行。</summary>
    private static async Task<RaceResult[]> RaceAsync(params Func<Task<RaceResult>>[] actions)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions
            .Select(action => Task.Run(async () =>
            {
                await gate.Task;
                return await action();
            }))
            .ToArray();
        gate.SetResult(true);
        return await Task.WhenAll(tasks);
    }

    private async Task<RaceResult> TryApproveAsync(long documentId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Approve(documentId));

    private async Task<RaceResult> TrySubmitAsync(long documentId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Submit(documentId));

    private async Task<RaceResult> TryDeleteAsync(long documentId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Delete(documentId));

    private async Task<RaceResult> TryEditAsync(long documentId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Update(documentId, NewStockOut(CustomerA, RaceWarehouse)));

    private async Task<RaceResult> RunAsync(Func<ErpDbContext, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            return new RaceResult(true, await action(db), null);
        }
        catch (Exception ex)
        {
            return new RaceResult(false, null, ex);
        }
    }

    private static StockOutController ControllerFor(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new StockOutController(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "IntegrationTest"))
                }
            }
        };
    }

    private StockOut NewStockOut(long customerId, long warehouseId, long? productId = null, decimal quantity = 5m)
        => new()
        {
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = productId ?? ProductA, ProductName = $"商品{productId}", Spec = "规格A", Unit = "PCS", Quantity = quantity }
            }
        };

    /// <summary>既有种子数据里的超级管理员账号（已被授予全部菜单，不新增任何用户授权）。</summary>
    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking().Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

    /// <summary>按既有菜单授权创建出库操作员：非系统角色 + 可选既有 stock-out 菜单。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string name, UserStatus status, bool withStockOutMenu)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var user = new SysUser
        {
            UserName = $"auth-{suffix}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = name,
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleCode = $"StockOutDenied-{suffix}", RoleName = "出库受限角色" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withStockOutMenu)
            await GrantStockOutMenuAsync(db, role.Id);

        return user.Id;
    }

    /// <summary>播种受限出库操作员（员工编码映射 + 既有 stock-out 菜单 + 本人客户）。</summary>
    private static async Task<long> SeedRestrictedOperatorAsync(ErpDbContext db, long ownedCustomerId)
    {
        var code = $"stockout-sql-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var userId = await SeedUserAsync(db, code, UserStatus.Enabled, withStockOutMenu: true);
        var roleId = await db.SysUserRoles.AsNoTracking().Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).FirstAsync();
        Assert.True(roleId > 0);

        employee.EmployeeCode = await db.SysUsers.Where(u => u.Id == userId).Select(u => u.UserName).SingleAsync();
        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == ownedCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();
        return userId;
    }

    /// <summary>把既有 stock-out 菜单授予角色（只读既有菜单，绝不新增菜单 / 用户。）。</summary>
    private static async Task GrantStockOutMenuAsync(ErpDbContext db, long roleId)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == StockOutAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id).FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    /// <summary>播种销售出库单（显式链接为空的历史口径），并确保商品主数据存在。</summary>
    private async Task<StockOut> SeedStockOutAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status, long? warehouseId = null, long? productId = null, decimal quantity = 5m)
    {
        var resolvedProductId = productId ?? ProductA;
        await EnsureProductAsync(db, resolvedProductId);
        var document = new StockOut
        {
            StockOutNo = $"AUTH-{Guid.NewGuid():N}",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId ?? WarehouseA,
            TotalQuantity = quantity,
            Status = status
        };
        db.StockOuts.Add(document);
        await db.SaveChangesAsync();
        db.StockOutDetails.Add(new StockOutDetail
        {
            StockOutId = document.Id,
            ProductId = resolvedProductId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity
        });
        await db.SaveChangesAsync();
        return document;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity)
        => db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity
        });

    private static async Task EnsureProductAsync(ErpDbContext db, long productId)
    {
        Assert.True(await db.BaseProducts.AsNoTracking().AnyAsync(p => p.Id == productId && !p.IsDeleted));
    }

    /// <summary>经真实控制器创建并提交一张出库单（商品与库存就绪），返回已持久化单据。</summary>
    private async Task<StockOut> CreateAndSubmitAsync(ErpDbContext db, long userId)
    {
        await EnsureProductAsync(db, ProductA);
        if (!await db.Stocks.AsNoTracking().AnyAsync(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA))
        {
            SeedStock(db, WarehouseA, ProductA, quantity: 20m);
            await db.SaveChangesAsync();
        }

        var controller = ControllerFor(db, userId);
        var ok = Assert.IsType<OkObjectResult>(await controller.Create(NewStockOut(CustomerA, WarehouseA)));
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        var id = (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        return await db.StockOuts.AsNoTracking().SingleAsync(o => o.Id == id);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 供 ERP-370 销售出库授权 / 两条独立连接竞争集成测试复用；发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取生产设置。
/// </summary>
public sealed class StockOutAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKOUTAUTH";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-370] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
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

        Console.WriteLine("[ERP-370] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class StockOutAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => StockOutAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}


