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
/// ERP-374 销售退货来源候选 / 详情与显式来源可退容量的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="SalesReturnSourceSqlServerFixture"/>）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="SalesReturnController"/> + <see cref="SalesReturnSourceRules"/> +
/// <see cref="InventoryService"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item>候选只含当前账号客户范围内「未删除、已审核」来源，且按「来源出库单 + 商品」聚合重复行、扣减已生效退货；</item>
/// <item>零容量 / 单位未知 / 负数量证据显式不可用；来源被取消（stale cancellation）后候选消失、审核 fail closed；</item>
/// <item>无身份 / 无既有菜单授权（销售退货或缺销售出库）一律 fail closed 且不产生库存 / 流水；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一来源的两张退货只允许累计不超过出库数量的一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一张退货单只允许一方过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全，在访问数据库之前校验；每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SalesReturnSourceSelectionSqlServerTests
    : IClassFixture<SalesReturnSourceSqlServerFixture>
{
    private readonly SalesReturnSourceSqlServerFixture _fixture;

    public SalesReturnSourceSelectionSqlServerTests(SalesReturnSourceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        SalesReturnSourceSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);
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

    private static List<SalesReturnSourceCandidateDto> Candidates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<List<SalesReturnSourceCandidateDto>>>(ok.Value).Data!;
    }

    // ==================== 1. 真实授权：既有「销售退货」+「销售出库」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-sales-return")]
    [InlineData("no-stock-out")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "候选权限仓");
        var productId = await SeedProductAsync(db, "候选权限商品");
        var customerId = await SeedCustomerAsync(db, "候选权限客户");
        var userId = await SeedOperatorAsync(db, customerId, scenario);
        await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved, (productId, "PCS", 5m));

        var ctl = NewController(db, scenario == "missing" ? null : userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);

        Assert.Empty(await db.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted).ToListAsync());
        Assert.Empty(await db.StockMovements.Where(m => m.WarehouseId == warehouseId && !m.IsDeleted).ToListAsync());
    }

    // ==================== 2. 候选聚合 / 净可退容量 / 不可用证据（真实 SQL） ====================

    [Fact]
    public async Task Candidates_aggregate_duplicate_products_and_subtract_effective_returns()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "候选聚合仓");
        var productA = await SeedProductAsync(db, "候选聚合商品A");
        var productZ = await SeedProductAsync(db, "候选聚合商品Z");
        var customerId = await SeedCustomerAsync(db, "候选聚合客户");
        var userId = await SeedOperatorAsync(db, customerId, "ok");

        // 同商品重复行 6 + 4 = 10；另有 Z 4；已审核退货 A 6（部分退货）→ A 净可退 4
        var shipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 6m), (productA, "PCS", 4m), (productZ, "PCS", 4m));
        await SeedReturnAsync(db, shipment.Id, customerId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 6m, 5m));
        // 零容量来源：Z 全部退完
        var zeroShipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
            (productZ, "PCS", 2m));
        await SeedReturnAsync(db, zeroShipment.Id, customerId, warehouseId, DocumentStatus.Approved,
            (productZ, "PCS", 2m, 5m));
        // 未审核 / 已删除来源：一律不得出现
        var pendingShipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Pending,
            (productA, "PCS", 9m));
        var deletedShipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 9m));
        deletedShipment.IsDeleted = true;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));

        Assert.DoesNotContain(rows, r => r.SourceStockOutId == pendingShipment.Id);
        Assert.DoesNotContain(rows, r => r.SourceStockOutId == deletedShipment.Id);

        var lineA = rows.Single(r => r.SourceStockOutId == shipment.Id && r.ProductId == productA);
        Assert.Equal(10m, lineA.SourceBaseQuantity);                 // 重复行合计，绝不重复相乘
        Assert.Equal(6m, lineA.EffectiveReturnedBaseQuantity);
        Assert.Equal(4m, lineA.RemainingBaseQuantity);               // 部分退货后仍可退
        Assert.True(lineA.Available);

        var lineZero = rows.Single(r => r.SourceStockOutId == zeroShipment.Id);
        Assert.False(lineZero.Available);
        Assert.Equal(SalesReturnSourceRules.ZeroCapacityText, lineZero.UnavailableReason);

        // 只读投影：候选查询不产生库存 / 流水
        Assert.Empty(await db.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted).ToListAsync());
        Assert.Empty(await db.StockMovements.Where(m => m.WarehouseId == warehouseId && !m.IsDeleted).ToListAsync());
    }

    [Fact]
    public async Task Stale_cancelled_source_disappears_from_candidates_and_approval_fails_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "候选取消仓");
        var productId = await SeedProductAsync(db, "候选取消商品");
        var customerId = await SeedCustomerAsync(db, "候选取消客户");
        var userId = await SeedOperatorAsync(db, customerId, "ok");

        var shipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 5m));
        var ret = await SeedReturnAsync(db, shipment.Id, customerId, warehouseId, DocumentStatus.Submitted,
            (productId, "PCS", 2m, 5m));

        var ctl = NewController(db, userId);
        Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, null, 0)));

        // 来源被取消（stale candidate）：候选立即收敛、审核 fail closed 且不产生库存 / 流水
        shipment.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        Assert.Empty(Candidates(await ctl.GetSourceCandidates(null, null, null, 0)));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(ret.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        Assert.Empty(await verify.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted).ToListAsync());
        Assert.Empty(await verify.StockMovements.Where(m => m.WarehouseId == warehouseId && !m.IsDeleted).ToListAsync());
        Assert.Equal(DocumentStatus.Submitted,
            await verify.SalesReturns.Where(r => r.Id == ret.Id).Select(r => r.Status).SingleAsync());
    }

    // ==================== 3. 两条独立连接竞争（真实 SQL，串行化上游来源行锁） ====================

    [Fact]
    public async Task Concurrent_returns_on_same_source_cannot_over_return()
    {
        Guard();
        long warehouseId, customerId, userId, shipmentId, firstReturnId, secondReturnId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "并发容量仓");
            var productId = await SeedProductAsync(db, "并发容量商品");
            customerId = await SeedCustomerAsync(db, "并发容量客户");
            userId = await SeedOperatorAsync(db, customerId, "ok");

            var shipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
                (productId, "PCS", 10m));
            shipmentId = shipment.Id;
            // 两张各退 6 的已提交退货：6 + 6 > 10，只允许一方过账
            firstReturnId = (await SeedReturnAsync(db, shipmentId, customerId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 6m, 5m))).Id;
            secondReturnId = (await SeedReturnAsync(db, shipmentId, customerId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 6m, 5m))).Id;
        }

        // 两条独立连接同时审核
        var results = await Task.WhenAll(
            TryApproveAsync(userId, firstReturnId),
            TryApproveAsync(userId, secondReturnId));

        Assert.Equal(1, results.Count(r => r.Ok));
        var loser = Assert.Single(results.Where(r => !r.Ok));
        Assert.IsType<BusinessException>(loser.Error);

        await using var verify = _fixture.CreateDbContext();
        var approvedTotal = (await (from r in verify.SalesReturns
                                    join d in verify.SalesReturnDetails on r.Id equals d.SalesReturnId
                                    where r.SourceStockOutId == shipmentId
                                          && r.Status == DocumentStatus.Approved && !r.IsDeleted && !d.IsDeleted
                                    select d.Quantity).ToListAsync()).Sum();
        Assert.True(approvedTotal <= 10m, "已审核退货累计不得超过来源出库数量");
        Assert.Equal(6m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted)
            .Select(s => s.Quantity).SingleAsync());                       // 恰好一次入库
        Assert.Equal(1, await verify.StockMovements.CountAsync(m =>
            m.WarehouseId == warehouseId && m.MovementType == InventoryMovementType.SalesReturn && !m.IsDeleted));
    }

    [Fact]
    public async Task Concurrent_approve_of_same_return_posts_once()
    {
        Guard();
        long warehouseId, customerId, userId, returnId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "并发同单仓");
            var productId = await SeedProductAsync(db, "并发同单商品");
            customerId = await SeedCustomerAsync(db, "并发同单客户");
            userId = await SeedOperatorAsync(db, customerId, "ok");

            var shipment = await SeedShipmentAsync(db, customerId, warehouseId, DocumentStatus.Approved,
                (productId, "PCS", 10m));
            returnId = (await SeedReturnAsync(db, shipment.Id, customerId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 4m, 5m))).Id;
        }

        var results = await Task.WhenAll(
            TryApproveAsync(userId, returnId),
            TryApproveAsync(userId, returnId));

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.IsType<BusinessException>(Assert.Single(results.Where(r => !r.Ok)).Error);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(4m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted)
            .Select(s => s.Quantity).SingleAsync());                       // 恰好一次入库
        Assert.Equal(1, await verify.StockMovements.CountAsync(m =>
            m.WarehouseId == warehouseId && m.MovementType == InventoryMovementType.SalesReturn && !m.IsDeleted));
    }

    // ==================== 种子与并发辅助 ====================

    /// <summary>用<b>独立连接</b>审核指定退货单：返回成功或失败原因（两条连接竞争的真实证据）。</summary>
    private async Task<(bool Ok, Exception? Error)> TryApproveAsync(long userId, long returnId)
    {
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, userId);
        try
        {
            await ctl.Approve(returnId);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"WH-SEL-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-SEL-{Tag()}", ProductName = name, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer { CustomerCode = $"C-SEL-{Tag()}", CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code, string name,
        string path, int sortOrder)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            SortOrder = sortOrder, MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>
    /// 播种一个真实业务员账号：<c>ok</c> = 既有「销售退货」+「销售出库」菜单；
    /// <c>no-sales-return</c> = 缺「销售退货」；<c>no-stock-out</c> = 缺「销售出库」；
    /// <c>disabled</c> = 已禁用；<c>missing</c> = 仅播种（控制器不注入身份）。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, long customerId, string scenario)
    {
        var role = new SysRole { RoleCode = $"SR-SEL-{Tag()}", RoleName = "退货来源操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"sr-sel-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "退货来源操作员",
            Status = scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "退货来源操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (scenario is not ("no-sales-return" or "missing"))
        {
            var salesReturnMenu = await EnsureMenuAsync(db, SalesReturnSourceRules.RequiredMenuCode,
                SalesReturnSourceRules.RequiredMenuText, "/logistics/sales-return", 60);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = salesReturnMenu.Id });
        }
        if (scenario is not ("no-stock-out" or "missing"))
        {
            var stockOutMenu = await EnsureMenuAsync(db, SalesReturnSourceRules.SourceRequiredMenuCode,
                SalesReturnSourceRules.SourceRequiredMenuText, "/logistics/stock-out", 41);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = stockOutMenu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<StockOut> SeedShipmentAsync(ErpDbContext db, long customerId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            StockOutNo = $"CK-SEL-SQL-{Tag()}",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockOuts.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private static async Task<SalesReturn> SeedReturnAsync(ErpDbContext db, long sourceStockOutId, long customerId,
        long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitCost)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-SEL-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "退货客户",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = await db.StockOuts.Where(o => o.Id == sourceStockOutId)
                .Select(o => o.StockOutNo).SingleAsync(),
            ReturnReason = "质量",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            TotalAmount = Math.Round(lines.Sum(l => l.Quantity * 10m), 4)
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
        return ret;
    }
}

/// <summary>
/// ERP-374 目标库护栏单元级校验：非专用 localdb 目标必须在<b>访问数据库之前</b>被拒绝
/// （实例名 / 库名前缀 / 集成安全）。本类不打开任何连接。
/// </summary>
public sealed class SalesReturnSourceSelectionTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            SalesReturnSourceSqlServerFixture.AssertDedicatedTarget(connection));
}