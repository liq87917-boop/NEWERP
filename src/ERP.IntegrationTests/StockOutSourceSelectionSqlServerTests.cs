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
/// ERP-376 销售出库来源候选 / 详情与显式来源剩余可发数量的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="StockOutSourceSelectionSqlServerFixture"/>）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="StockOutController"/> + <see cref="StockOutOrderFulfillmentRules"/> +
/// <see cref="InventoryService"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item>候选只含当前账号客户范围内「未删除、已审核」来源，且按「来源订单 + 商品」给出 ERP-343 剩余可发数量；</item>
/// <item>重复 / 歧义订单明细、单位未知、已发货满额、已取消 / 已删除来源显式不可用或直接不返回；</item>
/// <item>无身份 / 无既有菜单授权（销售出库或销售订单）一律 fail closed 且不产生库存 / 流水；</item>
/// <item>来源在候选后被取消（stale cancellation）时审核 fail closed，单据 / 库存 / 流水保持不变；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一来源的两张出库单只允许累计不超过授权数量的一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一张出库单只允许一方过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全，在访问数据库之前校验；每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockOutSourceSelectionSqlServerTests
    : IClassFixture<StockOutSourceSelectionSqlServerFixture>
{
    private readonly StockOutSourceSelectionSqlServerFixture _fixture;

    public StockOutSourceSelectionSqlServerTests(StockOutSourceSelectionSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        StockOutSourceSelectionSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockOutSourceSelectionSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static StockOutController NewController(ErpDbContext db, long? userId)
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

    private static List<StockOutSourceCandidateDto> Candidates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<List<StockOutSourceCandidateDto>>>(ok.Value).Data!;
    }

    private static StockOutSourceDetailDto Detail(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<StockOutSourceDetailDto>>(ok.Value).Data!;
    }

    // ==================== 1. 真实授权：既有「销售出库」+「销售订单」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-stock-out")]
    [InlineData("no-sales-order")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "候选权限客户");
        var productId = await SeedProductAsync(db, "候选权限商品", "PCS", string.Empty, 0);
        var userId = await SeedOperatorAsync(db, customerId, scenario);
        await SeedOrderAsync(db, $"SO-SEL-AUTH-{Tag()}", DocumentStatus.Approved, customerId,
            (productId, "PCS", 5m));

        var ctl = NewController(db, scenario == "missing" ? null : userId);
        var movementsBefore = await db.StockMovements.CountAsync();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);

        // 只读投影：被拒绝的候选读取绝不新增任何库存流水。
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
    }

    // ==================== 2. 候选投影：ERP-343 剩余可发数量（真实 SQL） ====================

    [Fact]
    public async Task Candidates_project_authorized_shipped_and_remaining_base_quantities()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "候选投影客户");
        var productA = await SeedProductAsync(db, "候选投影商品A", "PCS", string.Empty, 0);
        var productPack = await SeedProductAsync(db, "候选投影商品P", "PCS", "BOX", 12);
        var userId = await SeedPrivilegedOperatorAsync(db);

        var order = await SeedOrderAsync(db, $"SO-SEL-PROJ-{Tag()}", DocumentStatus.Approved, customerId,
            (productA, "PCS", 10m), (productPack, "BOX", 2m));
        await SeedShipmentAsync(db, order.Id, customerId, DocumentStatus.Approved, false, (productA, "PCS", 3m));
        await SeedShipmentAsync(db, order.Id, customerId, DocumentStatus.Pending, false, (productPack, "PCS", 24m));
        await SeedShipmentAsync(db, order.Id, customerId, DocumentStatus.Approved, true, (productA, "PCS", 100m));

        var pending = await SeedOrderAsync(db, $"SO-SEL-PEND-{Tag()}", DocumentStatus.Pending, customerId,
            (productA, "PCS", 9m));
        var deleted = await SeedOrderAsync(db, $"SO-SEL-DEL-{Tag()}", DocumentStatus.Approved, customerId,
            (productA, "PCS", 9m));
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));

        Assert.DoesNotContain(rows, r => r.SalesOrderId == pending.Id);
        Assert.DoesNotContain(rows, r => r.SalesOrderId == deleted.Id);

        var lineA = rows.Single(r => r.SalesOrderId == order.Id && r.ProductId == productA);
        Assert.Equal(10m, lineA.AuthorizedBaseQuantity);
        Assert.Equal(3m, lineA.ShippedBaseQuantity);   // 已删除 / 待提交出库不计入
        Assert.Equal(7m, lineA.RemainingBaseQuantity);
        Assert.True(lineA.Available);
        Assert.Equal("PCS", lineA.BaseUnit);
        Assert.Equal(customerId, lineA.CustomerId);
        Assert.Equal("候选投影客户", lineA.CustomerName);

        // 装箱单位折算：2 箱 × 12 = 24；待提交出库不计入已发货
        var linePack = rows.Single(r => r.SalesOrderId == order.Id && r.ProductId == productPack);
        Assert.Equal(24m, linePack.AuthorizedBaseQuantity);
        Assert.Equal(0m, linePack.ShippedBaseQuantity);
        Assert.Equal(24m, linePack.RemainingBaseQuantity);
        Assert.True(linePack.Available);

        // 详情：权威表头 + 逐商品剩余可发
        var detail = Detail(await ctl.GetSourceCandidateDetail(order.Id));
        Assert.Equal(order.Id, detail.SalesOrderId);
        Assert.Equal(customerId, detail.CustomerId);
        Assert.True(detail.Available);
        Assert.Equal(2, detail.Lines.Count);
    }

    // ==================== 3. 重复 / 单位未知 / 商品缺失 / 满额显式不可用 ====================

    [Fact]
    public async Task Candidates_mark_ambiguous_unknown_unit_missing_and_full_as_unavailable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "候选不可用客户");
        var productA = await SeedProductAsync(db, "候选不可用商品A", "PCS", string.Empty, 0);
        const long missingProduct = 999999999L;
        var userId = await SeedPrivilegedOperatorAsync(db);

        var ambiguous = await SeedOrderAsync(db, $"SO-SEL-AMB-{Tag()}", DocumentStatus.Approved, customerId,
            (productA, "PCS", 4m), (productA, "PCS", 6m));
        var unknown = await SeedOrderAsync(db, $"SO-SEL-UNK-{Tag()}", DocumentStatus.Approved, customerId,
            (productA, "袋", 5m));
        var missing = await SeedOrderAsync(db, $"SO-SEL-MISS-{Tag()}", DocumentStatus.Approved, customerId,
            (missingProduct, "PCS", 5m));
        var full = await SeedOrderAsync(db, $"SO-SEL-FULL-{Tag()}", DocumentStatus.Approved, customerId,
            (productA, "PCS", 5m));
        await SeedShipmentAsync(db, full.Id, customerId, DocumentStatus.Approved, false, (productA, "PCS", 5m));

        var ctl = NewController(db, userId);
        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));

        var ambiguousLine = rows.Single(r => r.SalesOrderId == ambiguous.Id);
        Assert.False(ambiguousLine.Available);
        Assert.Equal(StockOutOrderFulfillmentRules.AmbiguousLineText, ambiguousLine.UnavailableReason);

        var unknownLine = rows.Single(r => r.SalesOrderId == unknown.Id);
        Assert.False(unknownLine.Available);
        Assert.Equal(StockOutOrderFulfillmentRules.UnknownUnitText, unknownLine.UnavailableReason);

        var missingLine = rows.Single(r => r.SalesOrderId == missing.Id);
        Assert.False(missingLine.Available);
        Assert.Equal(StockOutOrderFulfillmentRules.CorruptSourceText, missingLine.UnavailableReason);

        var fullLine = rows.Single(r => r.SalesOrderId == full.Id);
        Assert.False(fullLine.Available);
        Assert.Equal(0m, fullLine.RemainingBaseQuantity);
        Assert.Equal(StockOutOrderFulfillmentRules.ZeroCapacityText, fullLine.UnavailableReason);

        var detail = Detail(await ctl.GetSourceCandidateDetail(full.Id));
        Assert.False(detail.Available);
        Assert.False(string.IsNullOrWhiteSpace(detail.UnavailableReason));
    }
    // ==================== 4. 详情 fail closed 与范围外来源 ====================

    [Fact]
    public async Task Detail_rejects_unapproved_deleted_foreign_and_invalid_sources()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ownCustomer = await SeedCustomerAsync(db, "候选详情客户");
        var foreignCustomer = await SeedCustomerAsync(db, "候选详情范围外客户");
        var productId = await SeedProductAsync(db, "候选详情商品", "PCS", string.Empty, 0);
        var userId = await SeedOperatorAsync(db, ownCustomer, "ok");

        var own = await SeedOrderAsync(db, $"SO-SEL-DET-{Tag()}", DocumentStatus.Approved, ownCustomer,
            (productId, "PCS", 5m));
        var pending = await SeedOrderAsync(db, $"SO-SEL-DETP-{Tag()}", DocumentStatus.Pending, ownCustomer,
            (productId, "PCS", 5m));
        var deleted = await SeedOrderAsync(db, $"SO-SEL-DETD-{Tag()}", DocumentStatus.Approved, ownCustomer,
            (productId, "PCS", 5m));
        deleted.IsDeleted = true;
        var foreign = await SeedOrderAsync(db, $"SO-SEL-DETF-{Tag()}", DocumentStatus.Approved, foreignCustomer,
            (productId, "PCS", 5m));
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        Assert.Equal(own.Id, Detail(await ctl.GetSourceCandidateDetail(own.Id)).SalesOrderId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(pending.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(foreign.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.DoesNotContain(rows, r => r.SalesOrderId == foreign.Id);
    }

    // ==================== 5. 陈旧来源（候选后被取消）审核 fail closed ====================

    [Fact]
    public async Task Approval_of_stale_cancelled_source_fails_closed_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "陈旧来源客户");
        var warehouseId = await EnsureDefaultWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "陈旧来源商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(db);
        await SeedStockAsync(db, warehouseId, productId, 100m);

        var order = await SeedOrderAsync(db, $"SO-SEL-STALE-{Tag()}", DocumentStatus.Approved, customerId,
            (productId, "PCS", 10m));
        var shipment = await SeedShipmentAsync(db, order.Id, customerId, DocumentStatus.Submitted, false,
            (productId, "PCS", 5m));
        await db.SaveChangesAsync();

        // 候选窗口内来源被取消 → 审核必须 fail closed
        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(shipment.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 单据状态 / 库存 / 流水保持不变（保留用户输入，不产生半成品写入）
        await using var verify = _fixture.CreateDbContext();
        var reloaded = await verify.StockOuts.AsNoTracking().SingleAsync(s => s.Id == shipment.Id);
        Assert.Equal(DocumentStatus.Submitted, reloaded.Status);
        Assert.Empty(await verify.StockMovements.Where(m => m.SourceDocId == shipment.Id).ToListAsync());
        Assert.Equal(100m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync());
    }
    // ==================== 6. 两条独立连接竞争：同来源两张出库单不超发 ====================

    [Fact]
    public async Task Concurrent_approvals_of_two_shipments_against_one_order_do_not_overship()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(seed, "并发来源客户");
        var warehouseId = await EnsureDefaultWarehouseAsync(seed);
        var productId = await SeedProductAsync(seed, "并发来源商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(seed);
        await SeedStockAsync(seed, warehouseId, productId, 100m);

        var order = await SeedOrderAsync(seed, $"SO-SEL-RACE-{Tag()}", DocumentStatus.Approved, customerId,
            (productId, "PCS", 10m));
        var docA = await SeedShipmentAsync(seed, order.Id, customerId, DocumentStatus.Submitted, false,
            (productId, "PCS", 6m));
        var docB = await SeedShipmentAsync(seed, order.Id, customerId, DocumentStatus.Submitted, false,
            (productId, "PCS", 6m));
        await seed.SaveChangesAsync();

        var results = await Task.WhenAll(TryApproveAsync(docA.Id, userId), TryApproveAsync(docB.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.Error.Contains("超过来源销售订单授权数量")));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(6m, await verify.StockMovements
            .Where(m => !m.IsDeleted && (m.SourceDocId == docA.Id || m.SourceDocId == docB.Id))
            .SumAsync(m => m.Quantity));
    }

    // ==================== 7. 两条独立连接竞争：同一张出库单只过账一次 ====================

    [Fact]
    public async Task Concurrent_approvals_of_same_shipment_post_exactly_once()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(seed, "并发同单客户");
        var warehouseId = await EnsureDefaultWarehouseAsync(seed);
        var productId = await SeedProductAsync(seed, "并发同单商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(seed);
        await SeedStockAsync(seed, warehouseId, productId, 100m);

        var order = await SeedOrderAsync(seed, $"SO-SEL-SAME-{Tag()}", DocumentStatus.Approved, customerId,
            (productId, "PCS", 10m));
        var shipment = await SeedShipmentAsync(seed, order.Id, customerId, DocumentStatus.Submitted, false,
            (productId, "PCS", 4m));
        await seed.SaveChangesAsync();

        var results = await Task.WhenAll(TryApproveAsync(shipment.Id, userId), TryApproveAsync(shipment.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var reloaded = await verify.StockOuts.AsNoTracking().SingleAsync(s => s.Id == shipment.Id);
        Assert.Equal(DocumentStatus.Approved, reloaded.Status);
        Assert.Equal(1, await verify.StockMovements.CountAsync(m => !m.IsDeleted && m.SourceDocId == shipment.Id));
        Assert.Equal(4m, await verify.StockMovements
            .Where(m => !m.IsDeleted && m.SourceDocId == shipment.Id)
            .SumAsync(m => m.Quantity));
    }

    private async Task<(bool Success, string Error)> TryApproveAsync(long stockOutId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, userId);
        try
        {
            var result = await ctl.Approve(stockOutId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
        catch (DbUpdateException ex)
        {
            return (false, ex.Message);
        }
    }
    // ==================== 8. 部分发货累计：先到者过账，后到者 fail closed ====================

    [Fact]
    public async Task Sequential_partial_shipments_cannot_overship_source_order()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "部分发货客户");
        var warehouseId = await EnsureDefaultWarehouseAsync(db);
        var productId = await SeedProductAsync(db, "部分发货商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(db);
        await SeedStockAsync(db, warehouseId, productId, 100m);

        var order = await SeedOrderAsync(db, $"SO-SEL-PART-{Tag()}", DocumentStatus.Approved, customerId,
            (productId, "PCS", 10m));
        var docA = await SeedShipmentAsync(db, order.Id, customerId, DocumentStatus.Submitted, false,
            (productId, "PCS", 6m));
        var docB = await SeedShipmentAsync(db, order.Id, customerId, DocumentStatus.Submitted, false,
            (productId, "PCS", 6m));
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        Assert.IsType<OkObjectResult>(await ctl.Approve(docA.Id));

        // 候选在审核后立即收敛：剩余可发 4
        var remaining = Candidates(await ctl.GetSourceCandidates(null, null, 0))
            .Single(r => r.SalesOrderId == order.Id && r.ProductId == productId);
        Assert.Equal(10m, remaining.AuthorizedBaseQuantity);
        Assert.Equal(6m, remaining.ShippedBaseQuantity);
        Assert.Equal(4m, remaining.RemainingBaseQuantity);

        // 第二张 6 超过剩余可发 4 → fail closed，且不产生第二条流水
        await using (var ctl2 = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(ctl2, userId).Approve(docB.Id));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        }

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.StockOuts.AsNoTracking().SingleAsync(s => s.Id == docB.Id)).Status);
        Assert.Equal(6m, await verify.StockMovements
            .Where(m => !m.IsDeleted && m.SourceDocId == docA.Id)
            .SumAsync(m => m.Quantity));
    }

    // ==================== 9. 撤销既有「销售订单」菜单授权后下一次请求立即收敛 ====================

    [Fact]
    public async Task Revoked_source_menu_authorization_converges_immediately()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "撤销授权客户");
        var productId = await SeedProductAsync(db, "撤销授权商品", "PCS", string.Empty, 0);
        var userId = await SeedOperatorAsync(db, customerId, "ok");
        await SeedOrderAsync(db, $"SO-SEL-REVOKE-{Tag()}", DocumentStatus.Approved, customerId,
            (productId, "PCS", 5m));

        var ctl = NewController(db, userId);
        Assert.NotEmpty(Candidates(await ctl.GetSourceCandidates(null, null, 0)));

        // 撤销既有「销售订单」菜单授权：下一次请求立即收敛（绝不缓存）
        var roleId = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).SingleAsync();
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 播种辅助 ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer { CustomerCode = $"C-SEL-{Tag()}", CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit, string packageUnit,
        int unitsPerPackage)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-SEL-{Tag()}",
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage,
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code, string name,
        string path, int sortOrder)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            SortOrder = sortOrder, MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>确保存在一个供出库审核扣减使用的默认仓库（幂等）。</summary>
    private static async Task<long> EnsureDefaultWarehouseAsync(ErpDbContext db)
    {
        var warehouse = await db.BaseWarehouses.FirstOrDefaultAsync(w => w.WarehouseCode == "WH-SO-SEL-DEFAULT");
        if (warehouse is not null) return warehouse.Id;

        warehouse = new BaseWarehouse
        {
            WarehouseCode = "WH-SO-SEL-DEFAULT", WarehouseName = "出库来源默认仓", Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
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
    /// <summary>
    /// 播种一个真实业务员账号：<c>ok</c> = 既有「销售出库」+「销售订单」菜单；
    /// <c>no-stock-out</c> = 缺「销售出库」；<c>no-sales-order</c> = 缺「销售订单」；
    /// <c>disabled</c> = 已禁用；<c>missing</c> = 仅播种（控制器不注入身份）。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, long customerId, string scenario)
    {
        var role = new SysRole { RoleCode = $"SO-SEL-{Tag()}", RoleName = "出库来源操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"so-sel-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "出库来源操作员",
            Status = scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "出库来源操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (scenario is not ("no-stock-out" or "missing"))
        {
            var stockOutMenu = await EnsureMenuAsync(db, StockOutAuthorizationRules.RequiredMenuCode,
                StockOutAuthorizationRules.RequiredMenuText, "/logistics/stock-out", 41);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = stockOutMenu.Id });
        }
        if (scenario is not ("no-sales-order" or "missing"))
        {
            var salesOrderMenu = await EnsureMenuAsync(db, StockOutAuthorizationRules.SourceRequiredMenuCode,
                StockOutAuthorizationRules.SourceRequiredMenuText, "/order/sales", 39);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = salesOrderMenu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>播种特权出库操作员（系统内置角色 = 不受数据范围限制）+ 既有「销售出库」/「销售订单」菜单。</summary>
    private static async Task<long> SeedPrivilegedOperatorAsync(ErpDbContext db)
    {
        var stockOutMenu = await EnsureMenuAsync(db, StockOutAuthorizationRules.RequiredMenuCode,
            StockOutAuthorizationRules.RequiredMenuText, "/logistics/stock-out", 41);
        var salesOrderMenu = await EnsureMenuAsync(db, StockOutAuthorizationRules.SourceRequiredMenuCode,
            StockOutAuthorizationRules.SourceRequiredMenuText, "/order/sales", 39);

        var role = new SysRole { RoleCode = $"SO-SEL-{Tag()}", RoleName = "出库来源管理员", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"so-sel-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "出库来源管理员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = stockOutMenu.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = salesOrderMenu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<SalesOrder> SeedOrderAsync(ErpDbContext db, string orderNo, DocumentStatus status,
        long customerId, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        foreach (var (productId, unit, quantity) in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity,
                UnitPrice = 5m,
                Amount = quantity * 5m
            });
        }
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<StockOut> SeedShipmentAsync(ErpDbContext db, long orderId, long customerId,
        DocumentStatus status, bool deleted,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var warehouseId = await EnsureDefaultWarehouseAsync(db);
        var shipment = new StockOut
        {
            StockOutNo = $"CK-SEL-SQL-{Tag()}",
            StockOutDate = DateTime.Today,
            SalesOrderId = orderId,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            IsDeleted = deleted,
            TotalQuantity = lines.Sum(l => l.Quantity)
        };
        db.StockOuts.Add(shipment);
        await db.SaveChangesAsync();

        foreach (var (productId, unit, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = shipment.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity
            });
        }
        await db.SaveChangesAsync();
        return shipment;
    }
}

/// <summary>
/// 专用 localdb 夹具：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class StockOutSourceSelectionSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKOUTSOURCE_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-376] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-376] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 stock-out / sales-order 菜单）。");
    }
}

/// <summary>
/// ERP-376 目标库护栏单元级校验：非专用 localdb 目标必须在<b>访问数据库之前</b>被拒绝
/// （实例名 / 库名前缀 / 集成安全）。本类不打开任何连接。
/// </summary>
public sealed class StockOutSourceSelectionTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockOutSourceSelectionSqlServerFixture.AssertDedicatedTarget(connection));
}
