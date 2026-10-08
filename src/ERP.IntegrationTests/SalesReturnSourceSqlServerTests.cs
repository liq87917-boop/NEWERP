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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-357 销售退货显式来源与可退容量的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="SalesReturnController"/> + <see cref="SalesReturnSourceRules"/> +
/// <see cref="InventoryService"/>），不复制一份测试专用实现：</para>
/// <list type="number">
/// <item>实时授权 fail closed（无身份 / 无菜单 / 禁用账号）时库存、流水与状态不变；</item>
/// <item>权威来源审核恰好一次入库、来源单号回填、精确边界允许、跨单据超退拒绝；</item>
/// <item>来源被外部改动（source edit）与授权撤销后立即收敛（permission revocation）；</item>
/// <item>后续行超限时整单回滚，不产生部分库存；</item>
/// <item><b>两条独立连接竞争</b>：并发退货对同一来源出库单只允许累计不超过出库数量的一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一张退货单只允许一方过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SalesReturnSourceSqlServerTests
    : IClassFixture<SalesReturnSourceSqlServerFixture>
{
    private readonly SalesReturnSourceSqlServerFixture _fixture;

    public SalesReturnSourceSqlServerTests(SalesReturnSourceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesReturnSourceSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static SalesReturnController NewController(ErpDbContext db, long? userId)
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

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    // ==================== 1. 授权 fail closed（库存 / 流水 / 状态不变） ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-menu")]
    [InlineData("disabled")]
    public async Task Denied_identities_post_no_inventory_and_leave_document_untouched(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "退货仓");
        var productId = await SeedProductAsync(db, "退货商品");
        var customerId = await SeedCustomerAsync(db, "退货客户");
        var shipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedReturnAsync(db, shipment.Id, customerId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 4m, 5m));

        long? userId = scenario switch
        {
            "no-menu" => await SeedSalesmanAsync(db, customerId, withMenu: false, enabled: true),
            "disabled" => await SeedSalesmanAsync(db, customerId, withMenu: true, enabled: false),
            _ => null
        };

        var beforeMovements = await db.StockMovements.CountAsync();
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesReturnSourceRules.EnsureMenuAuthorizedAsync(db, userId));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, error.Code);

        Assert.Equal(beforeMovements, await db.StockMovements.CountAsync());
        Assert.Equal(DocumentStatus.Submitted, await db.SalesReturns.Where(r => r.Id == ret.Id)
            .Select(r => r.Status).SingleAsync());
        Assert.Equal(0m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
            .Select(s => s.Quantity).DefaultIfEmpty(0m).SumAsync());
    }

    // ==================== 2. 权威来源审核 + 精确边界 + 超退拒绝 ====================

    [Fact]
    public async Task Approved_source_posts_once_then_exact_boundary_allowed_and_over_return_rejected()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productId = await SeedProductAsync(seed, "退货商品");
        var customerId = await SeedCustomerAsync(seed, "退货客户");
        var userId = await SeedSalesmanAsync(seed, customerId, withMenu: true, enabled: true);
        var shipment = await SeedShipmentAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var first = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 4m, 5m));
        var second = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 6m, 5m));
        var over = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 1m, 5m));

        // 第一张 4：审核入库恰好一次并回填权威来源单号
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            await ctl.Submit(first.Id);
            await ctl.Approve(first.Id);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(4m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(shipment.StockOutNo, await db.SalesReturns.Where(r => r.Id == first.Id)
                .Select(r => r.SourceStockOutNo).SingleAsync());
            Assert.Equal(DocumentStatus.Approved, await db.SalesReturns.Where(r => r.Id == first.Id)
                .Select(r => r.Status).SingleAsync());
        }

        // 第二张 6：精确边界（4 + 6 == 10）允许
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            await ctl.Submit(second.Id);
            await ctl.Approve(second.Id);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(10m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
        }

        // 第三张 1：超退拒绝，库存 / 流水 / 状态不变
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            await ctl.Submit(over.Id);
            var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(over.Id));
            Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(10m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(2, await db.StockMovements.CountAsync(m => !m.IsDeleted && !m.IsReversal));
            Assert.Equal(DocumentStatus.Submitted, await db.SalesReturns.Where(r => r.Id == over.Id)
                .Select(r => r.Status).SingleAsync());
        }
    }

    // ==================== 3. 来源改动 + 授权撤销 fail closed ====================

    [Fact]
    public async Task Source_edit_and_permission_revocation_fail_closed_without_posting()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productId = await SeedProductAsync(seed, "退货商品");
        var customerA = await SeedCustomerAsync(seed, "客户A");
        var customerB = await SeedCustomerAsync(seed, "客户B");
        var userId = await SeedSalesmanAsync(seed, customerA, withMenu: true, enabled: true);
        var shipment = await SeedShipmentAsync(seed, customerA, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedReturnAsync(seed, shipment.Id, customerA, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 3m, 5m));

        // 来源出库单客户被外部改动 → 审核 fail closed
        shipment.CustomerId = customerB;
        await seed.SaveChangesAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(ret.Id));
            Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.StockMovements.CountAsync());
            Assert.Equal(DocumentStatus.Submitted, await db.SalesReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
        }

        // 还原来源客户并撤销菜单授权 → 下一次审核立即收敛为权限不足
        shipment.CustomerId = customerA;
        await seed.SaveChangesAsync();
        foreach (var grant in await seed.SysRoleMenus.Where(g => !g.IsDeleted).ToListAsync())
        {
            grant.IsDeleted = true;
        }
        await seed.SaveChangesAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(ret.Id));
            Assert.Equal(ErrorCodes.Forbidden, error.Code);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.StockMovements.CountAsync());
        }
    }


    // ==================== 4. 后续行超限：整单回滚 ====================

    [Fact]
    public async Task Later_line_over_return_rolls_back_whole_document()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productA = await SeedProductAsync(seed, "商品A");
        var productB = await SeedProductAsync(seed, "商品B");
        var customerId = await SeedCustomerAsync(seed, "退货客户");
        var userId = await SeedSalesmanAsync(seed, customerId, withMenu: true, enabled: true);
        var shipment = await SeedShipmentAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 5m), (productB, "PCS", 5m));
        var ret = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Pending,
            (productA, "PCS", 4m, 5m), (productB, "PCS", 9m, 5m));

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            await ctl.Submit(ret.Id);
            var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(ret.Id));
            Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.StockMovements.CountAsync());   // 第一行也不得入库
            Assert.Equal(0m, await db.Stocks.Where(s => s.WarehouseId == warehouseId
                && (s.ProductId == productA || s.ProductId == productB)).SumAsync(s => s.Quantity));
            Assert.Equal(DocumentStatus.Submitted, await db.SalesReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
        }
    }

    // ==================== 5. 两条独立连接竞争：并发退货累计不超来源出库数量 ====================

    [Fact]
    public async Task Two_connections_concurrent_returns_never_exceed_shipped_quantity()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productId = await SeedProductAsync(seed, "退货商品");
        var customerId = await SeedCustomerAsync(seed, "退货客户");
        var userId = await SeedSalesmanAsync(seed, customerId, withMenu: true, enabled: true);
        var shipment = await SeedShipmentAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        // 两张各 6 的退货：单独都 ≤ 10，合计 12 > 10 → 只能成功其一
        var first = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 6m, 5m));
        var second = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 6m, 5m));

        var results = await Task.WhenAll(
            TryApproveAsync(first.Id, userId),
            TryApproveAsync(second.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            var approvedReturned = await (from r in db.SalesReturns
                                          join d in db.SalesReturnDetails on r.Id equals d.SalesReturnId
                                          where r.SourceStockOutId == shipment.Id && !r.IsDeleted
                                                && r.Status == DocumentStatus.Approved && !d.IsDeleted
                                          select d.Quantity).SumAsync();
            Assert.True(approvedReturned <= 10m);
            Assert.Equal(6m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1, await db.StockMovements.CountAsync(m => !m.IsDeleted && !m.IsReversal));
        }
    }


    // ==================== 6. 两条独立连接竞争：并发审核同一张退货单 ====================

    [Fact]
    public async Task Two_connections_approve_same_return_only_one_posts_inventory()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productId = await SeedProductAsync(seed, "退货商品");
        var customerId = await SeedCustomerAsync(seed, "退货客户");
        var userId = await SeedSalesmanAsync(seed, customerId, withMenu: true, enabled: true);
        var shipment = await SeedShipmentAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedReturnAsync(seed, shipment.Id, customerId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 4m, 5m));

        var results = await Task.WhenAll(
            TryApproveAsync(ret.Id, userId),
            TryApproveAsync(ret.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(4m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1, await db.StockMovements.CountAsync(m => !m.IsDeleted && !m.IsReversal));
            Assert.Equal(DocumentStatus.Approved, await db.SalesReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
        }
    }

    /// <summary>独立连接执行一次真实 <see cref="SalesReturnController.Approve"/>（含锁 / 事务 / 业务规则）。</summary>
    private async Task<(bool Success, string Error)> TryApproveAsync(long returnId, long? userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewController(db, userId).Approve(returnId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }


    // ==================== 播种助手（真实主数据 + 真实授权身份） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse
        {
            WarehouseCode = $"SRW-{Tag()}", WarehouseName = name, Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit = "PCS",
        string packageUnit = "", int unitsPerPackage = 0)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SRP-{Tag()}", ProductName = name, Spec = "规格A",
            Unit = unit, PackageUnit = packageUnit, UnitsPerPackage = unitsPerPackage, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"SRC-{Tag()}", CustomerName = name, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>播种真实业务员账号（非特权）：既有「销售退货」菜单（可选）+ 客户数据范围（仅分配客户）。</summary>
    private static async Task<long> SeedSalesmanAsync(ErpDbContext db, long customerId, bool withMenu, bool enabled)
    {
        var role = new SysRole { RoleCode = $"SR-SQL-{Tag()}", RoleName = "销售退货操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"sr-sql-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销售退货操作员",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "销售退货操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m =>
                m.MenuCode == SalesReturnSourceRules.RequiredMenuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu
                {
                    ParentId = 0,
                    MenuCode = SalesReturnSourceRules.RequiredMenuCode,
                    MenuName = SalesReturnSourceRules.RequiredMenuText,
                    Path = "/logistics/sales-return",
                    Icon = "package-minus",
                    SortOrder = 60,
                    MenuType = MenuType.Menu
                };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }


    private static async Task<StockOut> SeedShipmentAsync(ErpDbContext db, long customerId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            StockOutNo = $"CK-SR-SQL-{Tag()}",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockOuts.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private static async Task<SalesReturn> SeedReturnAsync(ErpDbContext db, long sourceStockOutId,
        long customerId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitCost)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-SR-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "退货客户",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = await db.StockOuts.Where(o => o.Id == sourceStockOutId)
                .Select(o => o.StockOutNo).SingleAsync(),
            ReturnReason = "质量",
            Status = status
        };
        db.SalesReturns.Add(ret);
        await db.SaveChangesAsync();

        var sortNo = 0;
        foreach (var (productId, unit, quantity, unitCost) in lines)
        {
            db.SalesReturnDetails.Add(new SalesReturnDetail
            {
                SalesReturnId = ret.Id,
                ReturnNo = ret.ReturnNo,
                SortNo = ++sortNo,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity,
                UnitPrice = 10m,
                Amount = Math.Round(quantity * 10m, 4),
                UnitCost = unitCost
            });
        }
        await db.SaveChangesAsync();

        ret.TotalQuantity = lines.Sum(l => l.Quantity);
        ret.TotalAmount = Math.Round(lines.Sum(l => l.Quantity * 10m), 4);
        await db.SaveChangesAsync();
        return ret;
    }
}

/// <summary>
/// 专用 localdb 夹具：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesReturnSourceSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SALESRETURNSOURCE_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-357] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-357] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 sales-return 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SalesReturnSourceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => SalesReturnSourceSqlServerFixture.AssertDedicatedTarget(connection));
}

