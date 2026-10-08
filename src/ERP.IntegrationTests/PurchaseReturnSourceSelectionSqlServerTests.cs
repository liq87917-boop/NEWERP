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
/// ERP-377 采购退货来源入库候选 / 详情与净可退容量的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="PurchaseReturnSourceSqlServerFixture"/>）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="PurchaseReturnController"/> + <see cref="PurchaseReturnSourceRules"/> +
/// <see cref="InventoryService"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item>真实权限：无身份 / 缺既有「采购退货」/ 缺既有「采购入库」/ 禁用账号一律 fail closed，且不产生库存 / 流水；</item>
/// <item>候选只含采购入库归属范围内「未删除、已审核」来源，且按「来源入库单 + 商品」聚合重复行、扣减已生效退货；</item>
/// <item>零容量 / 单位未知 / 负数量证据显式不可用；来源被取消（stale cancellation）后候选消失、审核 fail closed；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一来源的两张退货只允许累计不超过入库数量的一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一张退货单只允许一方过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全，在访问数据库之前校验；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PurchaseReturnSourceSelectionSqlServerTests
    : IClassFixture<PurchaseReturnSourceSqlServerFixture>
{
    private readonly PurchaseReturnSourceSqlServerFixture _fixture;

    public PurchaseReturnSourceSelectionSqlServerTests(PurchaseReturnSourceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        PurchaseReturnSourceSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);
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

    private static List<PurchaseReturnSourceCandidateDto> Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<List<PurchaseReturnSourceCandidateDto>>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static PurchaseReturnSourceDetailDto Detail(IActionResult result)
        => Assert.IsType<ApiResponse<PurchaseReturnSourceDetailDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    // ==================== 1. 真实授权：既有「采购退货」+「采购入库」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-purchase-return")]
    [InlineData("no-stock-in")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "候选权限仓");
        var productId = await SeedProductAsync(db, "候选权限商品");
        var supplierId = await SeedSupplierAsync(db, "候选权限供应商");
        var userId = await SeedOperatorAsync(db, scenario);
        await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved, (productId, "PCS", 5m));

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
        var supplierId = await SeedSupplierAsync(db, "候选聚合供应商");
        var userId = await SeedOperatorAsync(db, "ok");

        // 同商品重复行 6 + 4 = 10；另有 Z 4；已审核退货 A 6（部分退货）→ A 净可退 4
        var receipt = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 6m), (productA, "PCS", 4m), (productZ, "PCS", 4m));
        await SeedReturnAsync(db, receipt.Id, supplierId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 6m, 5m));
        // 零容量来源：Z 全部退完
        var zeroReceipt = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productZ, "PCS", 2m));
        await SeedReturnAsync(db, zeroReceipt.Id, supplierId, warehouseId, DocumentStatus.Approved,
            (productZ, "PCS", 2m, 5m));
        // 未审核 / 已删除来源：一律不得出现
        var pendingReceipt = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Pending,
            (productA, "PCS", 9m));
        var deletedReceipt = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productA, "PCS", 9m));
        deletedReceipt.IsDeleted = true;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var rows = Candidates(await ctl.GetSourceCandidates(null, warehouseId, null, 0));

        Assert.DoesNotContain(rows, r => r.SourceStockInId == pendingReceipt.Id);
        Assert.DoesNotContain(rows, r => r.SourceStockInId == deletedReceipt.Id);

        var lineA = rows.Single(r => r.SourceStockInId == receipt.Id && r.ProductId == productA);
        Assert.Equal(10m, lineA.SourceBaseQuantity);              // 重复行合计，绝不重复相乘
        Assert.Equal(6m, lineA.EffectiveReturnedBaseQuantity);
        Assert.Equal(4m, lineA.RemainingBaseQuantity);
        Assert.True(lineA.Available);
        Assert.Equal(supplierId, lineA.SupplierId);
        Assert.Equal(warehouseId, lineA.WarehouseId);
        Assert.Equal("PCS", lineA.BaseUnit);

        var lineZero = rows.Single(r => r.SourceStockInId == zeroReceipt.Id);
        Assert.False(lineZero.Available);
        Assert.Equal(PurchaseReturnSourceRules.ZeroCapacityText, lineZero.UnavailableReason);

        // 只读投影：候选查询不产生库存 / 流水
        Assert.Empty(await db.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted).ToListAsync());
        Assert.Empty(await db.StockMovements.Where(m => m.WarehouseId == warehouseId && !m.IsDeleted).ToListAsync());
    }

    // ==================== 3. 来源详情 fail closed 与单位未知显式不可用 ====================

    [Fact]
    public async Task Source_detail_fail_closed_and_unknown_unit_is_unavailable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "候选详情仓");
        var productId = await SeedProductAsync(db, "候选详情商品");
        var supplierId = await SeedSupplierAsync(db, "候选详情供应商");
        var userId = await SeedOperatorAsync(db, "ok");

        var approved = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        await SeedReturnAsync(db, approved.Id, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 3m, 5m));
        var unknownUnit = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "袋", 5m));
        var pending = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Pending,
            (productId, "PCS", 5m));
        var deleted = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 5m));
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var detail = Detail(await ctl.GetSourceCandidateDetail(approved.Id));
        Assert.Equal(approved.Id, detail.SourceStockInId);
        Assert.Equal(supplierId, detail.SupplierId);
        Assert.Equal(warehouseId, detail.WarehouseId);
        Assert.True(detail.Available);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(7m, line.RemainingBaseQuantity);

        var unknown = Detail(await ctl.GetSourceCandidateDetail(unknownUnit.Id));
        Assert.False(unknown.Available);
        Assert.Equal(PurchaseReturnSourceRules.UnknownUnitText,
            Assert.Single(unknown.Lines).UnavailableReason);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(pending.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 4. 来源被取消（stale cancellation）后候选收敛且审核 fail closed ====================

    [Fact]
    public async Task Stale_cancelled_source_disappears_from_candidates_and_approval_fails_closed()
    {
        Guard();
        long warehouseId, supplierId, userId, receiptId, returnId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "候选取消仓");
            var productId = await SeedProductAsync(db, "候选取消商品");
            supplierId = await SeedSupplierAsync(db, "候选取消供应商");
            userId = await SeedOperatorAsync(db, "ok");
            await SeedStockAsync(db, warehouseId, productId, 50m);
            receiptId = (await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
                (productId, "PCS", 5m))).Id;
            returnId = (await SeedReturnAsync(db, receiptId, supplierId, warehouseId, DocumentStatus.Submitted,
                (productId, "PCS", 2m, 5m))).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            Assert.Single(Candidates(await ctl.GetSourceCandidates(null, warehouseId, null, 0)));

            // 来源被取消（stale candidate）：候选立即收敛
            var receipt = await db.StockIns.SingleAsync(s => s.Id == receiptId);
            receipt.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();
            Assert.Empty(Candidates(await ctl.GetSourceCandidates(null, warehouseId, null, 0)));

            // 审核 fail closed：不产生库存扣减 / 流水，单据状态不变
            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(returnId));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        }

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(50m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(0, await verify.StockMovements.CountAsync(m =>
            m.WarehouseId == warehouseId && m.MovementType == InventoryMovementType.PurchaseReturn && !m.IsDeleted));
        Assert.Equal(DocumentStatus.Submitted,
            await verify.PurchaseReturns.Where(r => r.Id == returnId).Select(r => r.Status).SingleAsync());
    }

    // ==================== 5. 归属范围外来源与授权撤销（真实 SQL） ====================

    [Fact]
    public async Task Foreign_owned_source_is_hidden_and_revoked_permission_converges()
    {
        Guard();
        long warehouseId, supplierId, userId, foreignReceiptId, ownReceiptId, roleId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "候选范围仓");
            var productId = await SeedProductAsync(db, "候选范围商品");
            supplierId = await SeedSupplierAsync(db, "候选范围供应商");
            var customerId = await SeedCustomerAsync(db, "范围外客户");
            userId = await SeedOperatorAsync(db, "ok");
            roleId = await db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
                .Select(ur => ur.RoleId).SingleAsync();

            var order = await SeedPurchaseOrderAsync(db, supplierId, customerId);
            foreignReceiptId = (await SeedLinkedReceiptAsync(db, supplierId, warehouseId, order,
                (productId, "PCS", 10m))).Id;
            ownReceiptId = (await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
                (productId, "PCS", 4m))).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            var rows = Candidates(await ctl.GetSourceCandidates(null, warehouseId, null, 0));
            Assert.Equal(new[] { ownReceiptId }, rows.Select(r => r.SourceStockInId).ToArray());

            // 归属范围外来源按「不存在」拒绝（不泄露归属）
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                ctl.GetSourceCandidateDetail(foreignReceiptId));
            Assert.Equal(ErrorCodes.NotFound, ex.Code);

            // 撤销本账号角色 → 菜单授权后下一次请求立即收敛（不缓存授权）
            foreach (var grant in await db.SysRoleMenus
                         .Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            {
                grant.IsDeleted = true;
            }
            await db.SaveChangesAsync();
            ex = await Assert.ThrowsAsync<BusinessException>(() =>
                ctl.GetSourceCandidates(null, warehouseId, null, 0));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
    }

    // ==================== 6. 两条独立连接竞争（真实 SQL，串行化上游来源行锁） ====================

    [Fact]
    public async Task Concurrent_returns_on_same_source_cannot_over_return()
    {
        Guard();
        long warehouseId, supplierId, userId, receiptId, firstReturnId, secondReturnId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "并发容量仓");
            var productId = await SeedProductAsync(db, "并发容量商品");
            supplierId = await SeedSupplierAsync(db, "并发容量供应商");
            userId = await SeedOperatorAsync(db, "ok");
            await SeedStockAsync(db, warehouseId, productId, 100m);

            receiptId = (await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
                (productId, "PCS", 10m))).Id;
            // 两张各退 6 的已提交退货：6 + 6 > 10，只允许一方过账
            firstReturnId = (await SeedReturnAsync(db, receiptId, supplierId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 6m, 5m))).Id;
            secondReturnId = (await SeedReturnAsync(db, receiptId, supplierId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 6m, 5m))).Id;
        }

        // 两条独立连接同时审核
        var results = await Task.WhenAll(
            TryApproveAsync(userId, firstReturnId),
            TryApproveAsync(userId, secondReturnId));

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.IsType<BusinessException>(Assert.Single(results.Where(r => !r.Ok)).Error);

        await using var verify = _fixture.CreateDbContext();
        var approvedTotal = (await (from r in verify.PurchaseReturns
                                    join d in verify.PurchaseReturnDetails on r.Id equals d.PurchaseReturnId
                                    where r.SourceStockInId == receiptId
                                          && r.Status == DocumentStatus.Approved && !r.IsDeleted && !d.IsDeleted
                                    select d.Quantity).ToListAsync()).Sum();
        Assert.True(approvedTotal <= 10m, "已审核退货累计不得超过来源入库数量");
        Assert.Equal(94m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted)
            .Select(s => s.Quantity).SingleAsync());                    // 恰好一次出库：100 − 6
        Assert.Equal(1, await verify.StockMovements.CountAsync(m =>
            m.WarehouseId == warehouseId && m.MovementType == InventoryMovementType.PurchaseReturn && !m.IsDeleted));
    }

    [Fact]
    public async Task Concurrent_approve_of_same_return_posts_once()
    {
        Guard();
        long warehouseId, supplierId, userId, receiptId, returnId;
        await using (var db = _fixture.CreateDbContext())
        {
            warehouseId = await SeedWarehouseAsync(db, "并发同单仓");
            var productId = await SeedProductAsync(db, "并发同单商品");
            supplierId = await SeedSupplierAsync(db, "并发同单供应商");
            userId = await SeedOperatorAsync(db, "ok");
            await SeedStockAsync(db, warehouseId, productId, 40m);

            receiptId = (await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved,
                (productId, "PCS", 10m))).Id;
            returnId = (await SeedReturnAsync(db, receiptId, supplierId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 4m, 5m))).Id;
        }

        var results = await Task.WhenAll(
            TryApproveAsync(userId, returnId),
            TryApproveAsync(userId, returnId));

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.IsType<BusinessException>(Assert.Single(results.Where(r => !r.Ok)).Error);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(36m, await verify.Stocks.Where(s => s.WarehouseId == warehouseId && !s.IsDeleted)
            .Select(s => s.Quantity).SingleAsync());                    // 恰好一次出库：40 − 4
        Assert.Equal(1, await verify.StockMovements.CountAsync(m =>
            m.WarehouseId == warehouseId && m.MovementType == InventoryMovementType.PurchaseReturn && !m.IsDeleted));
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
        var warehouse = new BaseWarehouse { WarehouseCode = $"WH-PRSEL-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-PRSEL-{Tag()}", ProductName = name, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = $"S-PRSEL-{Tag()}", SupplierName = name, Status = 1 };
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
    /// 播种一个真实业务员账号（既有「采购退货」+ 既有「采购入库」菜单；<c>ok</c> 场景两者齐备）：
    /// <c>no-purchase-return</c> = 缺「采购退货」；<c>no-stock-in</c> = 缺「采购入库」；
    /// <c>disabled</c> = 已禁用；<c>missing</c> = 仅播种（控制器不注入身份）。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, string scenario)
    {
        var role = new SysRole { RoleCode = $"PRSEL-{Tag()}", RoleName = "采购退货来源操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"prsel-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购退货来源操作员",
            Status = scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        // 映射为业务员（IsSalesman + 员工编码 = 登录账号）：采购入库归属范围按已映射入库操作员判定。
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "采购退货来源操作员", IsSalesman = true, Status = 1
        });
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (scenario is not ("no-purchase-return" or "missing"))
        {
            var menu = await EnsureMenuAsync(db, PurchaseReturnSourceRules.RequiredMenuCode,
                PurchaseReturnSourceRules.RequiredMenuText, "/logistics/purchase-return", 70);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        if (scenario is not ("no-stock-in" or "missing"))
        {
            var menu = await EnsureMenuAsync(db, PurchaseReturnSourceRules.SourceRequiredMenuCode,
                PurchaseReturnSourceRules.SourceRequiredMenuText, "/logistics/stock-in", 40);
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
            StockInNo = $"RK-PRSEL-SQL-{Tag()}",
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity)
        };
        db.StockIns.Add(receipt);
        await db.SaveChangesAsync();

        foreach (var (productId, unit, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = receipt.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity
            });
        }
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<PurchaseReturn> SeedReturnAsync(ErpDbContext db, long sourceStockInId,
        long supplierId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitCost)[] lines)
    {
        var ret = new PurchaseReturn
        {
            ReturnNo = $"CTH-PRSEL-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierName = "退货供应商",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = await db.StockIns.Where(o => o.Id == sourceStockInId)
                .Select(o => o.StockInNo).SingleAsync(),
            ReturnReason = "质量",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            TotalAmount = Math.Round(lines.Sum(l => l.Quantity * 10m), 4)
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
        return ret;
    }

    /// <summary>播种一个未被当前操作员可见的客户（归属范围外证据）。</summary>
    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer { CustomerCode = $"C-PRSEL-{Tag()}", CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<PurchaseOrder> SeedPurchaseOrderAsync(ErpDbContext db, long supplierId,
        long owningCustomerId)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO-PRSEL-{Tag()}",
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved,
            OwningCustomerId = owningCustomerId,
            OwningCustomerName = "范围外客户"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>播种一张链接到指定采购订单的已审核入库单（用于采购入库归属范围证据）。</summary>
    private static async Task<StockIn> SeedLinkedReceiptAsync(ErpDbContext db, long supplierId, long warehouseId,
        PurchaseOrder order, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = await SeedReceiptAsync(db, supplierId, warehouseId, DocumentStatus.Approved, lines);
        receipt.PurchaseOrderId = order.Id;
        await db.SaveChangesAsync();
        return receipt;
    }
}

/// <summary>
/// ERP-377 目标库护栏单元级校验：非专用 localdb 目标必须在<b>访问数据库之前</b>被拒绝
/// （实例名 / 库名前缀 / 集成安全）。本类不打开任何连接。
/// </summary>
public sealed class PurchaseReturnSourceSelectionTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            PurchaseReturnSourceSqlServerFixture.AssertDedicatedTarget(connection));
}
