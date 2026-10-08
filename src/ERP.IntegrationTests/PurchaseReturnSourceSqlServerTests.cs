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
/// ERP-358 采购退货显式来源与可退容量的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="PurchaseReturnController"/> + <see cref="PurchaseReturnSourceRules"/> +
/// <see cref="InventoryService"/>），不复制一份测试专用实现：</para>
/// <list type="number">
/// <item>实时授权 fail closed（无身份 / 无菜单 / 禁用账号）时库存、流水与状态不变；</item>
/// <item>权威来源审核恰好一次出库、来源单号回填、精确边界允许、跨单据超退拒绝；</item>
/// <item>来源被外部改动（source edit）与授权撤销后立即收敛（permission revocation）；</item>
/// <item>后续行超限时整单回滚，不产生部分出库；</item>
/// <item><b>两条独立连接竞争</b>：并发退货对同一来源入库单只允许累计不超过入库数量的一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一张退货单只允许一方过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PurchaseReturnSourceSqlServerTests
    : IClassFixture<PurchaseReturnSourceSqlServerFixture>
{
    private readonly PurchaseReturnSourceSqlServerFixture _fixture;

    public PurchaseReturnSourceSqlServerTests(PurchaseReturnSourceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseReturnSourceSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static PurchaseReturnController NewController(ErpDbContext db, long? userId)
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
        var supplierId = await SeedSupplierAsync(db, "退货供应商");
        await SeedStockAsync(db, warehouseId, productId, 50m);
        var receipt = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedReturnAsync(db, receipt.Id, supplierId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 4m, 5m));

        long? userId = scenario switch
        {
            "no-menu" => await SeedOperatorAsync(db, withMenu: false, enabled: true),
            "disabled" => await SeedOperatorAsync(db, withMenu: true, enabled: false),
            _ => null
        };

        var beforeMovements = await db.StockMovements.CountAsync();
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseReturnSourceRules.EnsureMenuAuthorizedAsync(db, userId));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, error.Code);

        Assert.Equal(beforeMovements, await db.StockMovements.CountAsync());
        Assert.Equal(DocumentStatus.Submitted, await db.PurchaseReturns.Where(r => r.Id == ret.Id)
            .Select(r => r.Status).SingleAsync());
        Assert.Equal(50m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync());
    }

    // ==================== 2. 权威来源审核 + 精确边界 + 超退拒绝 ====================

    [Fact]
    public async Task Approved_source_posts_once_then_exact_boundary_allowed_and_over_return_rejected()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productId = await SeedProductAsync(seed, "退货商品");
        var supplierId = await SeedSupplierAsync(seed, "退货供应商");
        await SeedStockAsync(seed, warehouseId, productId, 50m);
        var userId = await SeedOperatorAsync(seed, withMenu: true, enabled: true);
        var receipt = await SeedReceiptAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var first = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 4m, 5m));
        var second = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 6m, 5m));
        var over = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 1m, 5m));

        // 第一张 4：审核出库恰好一次并回填权威来源单号
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            await ctl.Submit(first.Id);
            await ctl.Approve(first.Id);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(46m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(receipt.StockInNo, await db.PurchaseReturns.Where(r => r.Id == first.Id)
                .Select(r => r.SourceStockInNo).SingleAsync());
            Assert.Equal(DocumentStatus.Approved, await db.PurchaseReturns.Where(r => r.Id == first.Id)
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
            Assert.Equal(40m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
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
            Assert.Equal(40m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId
                && !m.IsDeleted && !m.IsReversal));
            Assert.Equal(DocumentStatus.Submitted, await db.PurchaseReturns.Where(r => r.Id == over.Id)
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
        var supplierId = await SeedSupplierAsync(seed, "退货供应商");
        var otherSupplierId = await SeedSupplierAsync(seed, "其它供应商");
        await SeedStockAsync(seed, warehouseId, productId, 50m);
        var userId = await SeedOperatorAsync(seed, withMenu: true, enabled: true);
        var receipt = await SeedReceiptAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 3m, 5m));

        // 来源入库单供应商被外部改动 → 审核 fail closed
        receipt.SupplierId = otherSupplierId;
        await seed.SaveChangesAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(ret.Id));
            Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        }
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId));
            Assert.Equal(DocumentStatus.Submitted, await db.PurchaseReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
        }

        // 还原来源供应商并撤销菜单授权 → 下一次审核立即收敛为权限不足
        receipt.SupplierId = supplierId;
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
            Assert.Equal(0, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId));
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
        var supplierId = await SeedSupplierAsync(seed, "退货供应商");
        await SeedStockAsync(seed, warehouseId, productA, 50m);
        await SeedStockAsync(seed, warehouseId, productB, 50m);
        var userId = await SeedOperatorAsync(seed, withMenu: true, enabled: true);
        var receipt = await SeedReceiptAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 5m), (productB, "PCS", 5m));
        var ret = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Pending,
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
            // 第一行也不得出库
            Assert.Equal(0, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId));
            Assert.Equal(100m, await db.Stocks.Where(s => s.WarehouseId == warehouseId
                && (s.ProductId == productA || s.ProductId == productB)).SumAsync(s => s.Quantity));
            Assert.Equal(DocumentStatus.Submitted, await db.PurchaseReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
        }
    }

    /// <summary>独立连接执行一次真实 <see cref="PurchaseReturnController.Approve"/>（含锁 / 事务 / 业务规则）。</summary>
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

    // ==================== 5. 两条独立连接竞争：并发退货累计不超来源入库数量 ====================

    [Fact]
    public async Task Two_connections_concurrent_returns_never_exceed_received_quantity()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "退货仓");
        var productId = await SeedProductAsync(seed, "退货商品");
        var supplierId = await SeedSupplierAsync(seed, "退货供应商");
        await SeedStockAsync(seed, warehouseId, productId, 50m);
        var userId = await SeedOperatorAsync(seed, withMenu: true, enabled: true);
        var receipt = await SeedReceiptAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        // 两张各 6 的退货：单独都 ≤ 10，合计 12 > 10 → 只能成功其一
        var first = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 6m, 5m));
        var second = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 6m, 5m));

        var results = await Task.WhenAll(
            TryApproveAsync(first.Id, userId),
            TryApproveAsync(second.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            var approvedReturned = await (from r in db.PurchaseReturns
                                          join d in db.PurchaseReturnDetails on r.Id equals d.PurchaseReturnId
                                          where r.SourceStockInId == receipt.Id && !r.IsDeleted
                                                && r.Status == DocumentStatus.Approved && !d.IsDeleted
                                          select d.Quantity).SumAsync();
            Assert.True(approvedReturned <= 10m);
            Assert.Equal(44m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId
                && !m.IsDeleted && !m.IsReversal));
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
        var supplierId = await SeedSupplierAsync(seed, "退货供应商");
        await SeedStockAsync(seed, warehouseId, productId, 50m);
        var userId = await SeedOperatorAsync(seed, withMenu: true, enabled: true);
        var receipt = await SeedReceiptAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedReturnAsync(seed, receipt.Id, supplierId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 4m, 5m));

        var results = await Task.WhenAll(
            TryApproveAsync(ret.Id, userId),
            TryApproveAsync(ret.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(46m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(1, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId
                && !m.IsDeleted && !m.IsReversal));
            Assert.Equal(DocumentStatus.Approved, await db.PurchaseReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
        }
    }

    // ==================== 播种助手（真实主数据 + 真实授权身份） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse
        {
            WarehouseCode = $"PRW-{Tag()}", WarehouseName = name, Status = 1
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
            ProductCode = $"PRP-{Tag()}", ProductName = name, Spec = "规格A",
            Unit = unit, PackageUnit = packageUnit, UnitsPerPackage = unitsPerPackage, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"PRS-{Tag()}", SupplierName = name, Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = Math.Round(quantity * 6m, 4),
            AverageCost = 6m
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种真实操作员账号（非特权）：既有「采购退货」菜单（可选）+ 员工映射。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, bool withMenu, bool enabled)
    {
        var role = new SysRole { RoleCode = $"PR-SQL-{Tag()}", RoleName = "采购退货操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"pr-sql-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购退货操作员",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "采购退货操作员", IsSalesman = true, Status = 1
        });
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m =>
                m.MenuCode == PurchaseReturnSourceRules.RequiredMenuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu
                {
                    ParentId = 0,
                    MenuCode = PurchaseReturnSourceRules.RequiredMenuCode,
                    MenuName = PurchaseReturnSourceRules.RequiredMenuText,
                    Path = "/logistics/purchase-return",
                    Icon = "package-plus",
                    SortOrder = 70,
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

    private static async Task<StockIn> SeedReceiptAsync(ErpDbContext db, long supplierId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            StockInNo = $"RK-PR-SQL-{Tag()}",
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockInDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockIns.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<PurchaseReturn> SeedReturnAsync(ErpDbContext db, long sourceStockInId,
        long supplierId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitCost)[] lines)
    {
        var ret = new PurchaseReturn
        {
            ReturnNo = $"CTH-PR-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierName = "退货供应商",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = await db.StockIns.Where(o => o.Id == sourceStockInId)
                .Select(o => o.StockInNo).SingleAsync(),
            ReturnReason = "规格不符",
            Status = status
        };
        db.PurchaseReturns.Add(ret);
        await db.SaveChangesAsync();

        var sortNo = 0;
        foreach (var (productId, unit, quantity, unitCost) in lines)
        {
            db.PurchaseReturnDetails.Add(new PurchaseReturnDetail
            {
                PurchaseReturnId = ret.Id,
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
public sealed class PurchaseReturnSourceSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PURCHASERETURNSOURCE_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-358] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-358] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 purchase-return 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseReturnSourceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PurchaseReturnSourceSqlServerFixture.AssertDedicatedTarget(connection));
}
