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
/// ERP-375 采购入库来源候选 / 详情与显式来源剩余可收数量的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="StockInSourceSelectionSqlServerFixture"/>）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="StockInController"/> + <see cref="StockInOrderFulfillmentRules"/> +
/// <see cref="InventoryService"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item>候选只含当前账号客户范围内「未删除、已审核」来源，且按「来源订单 + 商品」给出 ERP-342 剩余可收数量；</item>
/// <item>重复 / 歧义订单明细、单位未知、已收货满额、已取消 / 已删除来源显式不可用或直接不返回；</item>
/// <item>无身份 / 无既有菜单授权（采购入库或采购订单）一律 fail closed 且不产生库存 / 流水；</item>
/// <item>来源在候选后被取消（stale cancellation）时审核 fail closed，单据 / 库存 / 流水保持不变；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一来源的两张入库单只允许累计不超过授权数量的一方过账；</item>
/// <item><b>两条独立连接竞争</b>：并发审核同一张入库单只允许一方过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全，在访问数据库之前校验；每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockInSourceSelectionSqlServerTests
    : IClassFixture<StockInSourceSelectionSqlServerFixture>
{
    private readonly StockInSourceSelectionSqlServerFixture _fixture;

    public StockInSourceSelectionSqlServerTests(StockInSourceSelectionSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        StockInSourceSelectionSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockInSourceSelectionSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static StockInController NewController(ErpDbContext db, long? userId)
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

    private static List<StockInSourceCandidateDto> Candidates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<List<StockInSourceCandidateDto>>>(ok.Value).Data!;
    }

    private static StockInSourceDetailDto Detail(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<StockInSourceDetailDto>>(ok.Value).Data!;
    }

    // ==================== 1. 真实授权：既有「采购入库」+「采购订单」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-stock-in")]
    [InlineData("no-purchase-order")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "候选权限供应商");
        var productId = await SeedProductAsync(db, "候选权限商品", "PCS", string.Empty, 0);
        var customerId = await SeedCustomerAsync(db, "候选权限客户");
        var userId = await SeedOperatorAsync(db, customerId, scenario);
        await SeedOrderAsync(db, $"PO-SEL-AUTH-{Tag()}", supplierId, DocumentStatus.Approved, customerId,
            (productId, "PCS", 5m));

        var stocksBefore = await db.Stocks.CountAsync();
        var movementsBefore = await db.StockMovements.CountAsync();
        var ctl = NewController(db, scenario == "missing" ? null : userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);

        Assert.Equal(stocksBefore, await db.Stocks.CountAsync());
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
    }

    // ==================== 2. 候选投影：ERP-342 剩余可收数量（真实 SQL） ====================

    [Fact]
    public async Task Candidates_project_authorized_received_and_remaining_base_quantities()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "候选投影供应商");
        var productA = await SeedProductAsync(db, "候选投影商品A", "PCS", string.Empty, 0);
        var productPack = await SeedProductAsync(db, "候选投影商品P", "PCS", "BOX", 12);
        var warehouseId = await SeedWarehouseAsync(db, "候选投影仓");
        var userId = await SeedPrivilegedOperatorAsync(db);

        var order = await SeedOrderAsync(db, $"PO-SEL-PROJ-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productA, "PCS", 10m), (productPack, "BOX", 2m));
        await SeedReceiptAsync(db, order.Id, supplierId, warehouseId, DocumentStatus.Approved, false, (productA, "PCS", 3m));
        await SeedReceiptAsync(db, order.Id, supplierId, warehouseId, DocumentStatus.Pending, false, (productPack, "PCS", 24m));
        await SeedReceiptAsync(db, order.Id, supplierId, warehouseId, DocumentStatus.Approved, true, (productA, "PCS", 100m));

        var pending = await SeedOrderAsync(db, $"PO-SEL-PEND-{Tag()}", supplierId, DocumentStatus.Pending, null,
            (productA, "PCS", 9m));
        var deleted = await SeedOrderAsync(db, $"PO-SEL-DEL-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productA, "PCS", 9m));
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));

        Assert.DoesNotContain(rows, r => r.PurchaseOrderId == pending.Id);
        Assert.DoesNotContain(rows, r => r.PurchaseOrderId == deleted.Id);

        var lineA = rows.Single(r => r.PurchaseOrderId == order.Id && r.ProductId == productA);
        Assert.Equal(10m, lineA.AuthorizedBaseQuantity);
        Assert.Equal(3m, lineA.ReceivedBaseQuantity);   // 已删除的已审核入库不计入
        Assert.Equal(7m, lineA.RemainingBaseQuantity);
        Assert.True(lineA.Available);
        Assert.Equal("PCS", lineA.BaseUnit);
        Assert.Equal(supplierId, lineA.SupplierId);
        Assert.Equal("候选投影供应商", lineA.SupplierName);
        Assert.Equal(Currency.CNY, lineA.Currency);

        // 装箱单位折算：2 箱 × 12 = 24；未审核入库不计入已收
        var linePack = rows.Single(r => r.PurchaseOrderId == order.Id && r.ProductId == productPack);
        Assert.Equal(24m, linePack.AuthorizedBaseQuantity);
        Assert.Equal(0m, linePack.ReceivedBaseQuantity);
        Assert.Equal(24m, linePack.RemainingBaseQuantity);
        Assert.True(linePack.Available);

        // 详情：权威表头 + 逐商品剩余可收
        var detail = Detail(await ctl.GetSourceCandidateDetail(order.Id));
        Assert.Equal(order.Id, detail.PurchaseOrderId);
        Assert.True(detail.Available);
        Assert.Equal(2, detail.Lines.Count);
    }

    // ==================== 3. 重复 / 单位未知 / 满额显式不可用 ====================

    [Fact]
    public async Task Candidates_mark_ambiguous_unknown_unit_and_full_as_unavailable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "候选不可用供应商");
        var productA = await SeedProductAsync(db, "候选不可用商品A", "PCS", string.Empty, 0);
        var noBaseUnitProduct = await SeedProductAsync(db, "候选不可用商品N", string.Empty, string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(db);

        var ambiguous = await SeedOrderAsync(db, $"PO-SEL-AMB-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productA, "PCS", 4m), (productA, "PCS", 6m));
        var unknown = await SeedOrderAsync(db, $"PO-SEL-UNK-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productA, "袋", 5m));
        var noBaseUnit = await SeedOrderAsync(db, $"PO-SEL-NOBASE-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (noBaseUnitProduct, "PCS", 5m));
        var full = await SeedOrderAsync(db, $"PO-SEL-FULL-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productA, "PCS", 5m));
        await SeedReceiptAsync(db, full.Id, supplierId, await SeedWarehouseAsync(db, "候选满额仓"),
            DocumentStatus.Approved, false, (productA, "PCS", 5m));

        var ctl = NewController(db, userId);
        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));

        var ambiguousLine = rows.Single(r => r.PurchaseOrderId == ambiguous.Id);
        Assert.False(ambiguousLine.Available);
        Assert.Equal(StockInOrderFulfillmentRules.AmbiguousLineText, ambiguousLine.UnavailableReason);

        var unknownLine = rows.Single(r => r.PurchaseOrderId == unknown.Id);
        Assert.False(unknownLine.Available);
        Assert.Equal(StockInOrderFulfillmentRules.UnknownUnitText, unknownLine.UnavailableReason);

        var noBaseUnitLine = rows.Single(r => r.PurchaseOrderId == noBaseUnit.Id);
        Assert.False(noBaseUnitLine.Available);
        Assert.Equal(StockInOrderFulfillmentRules.UnknownUnitText, noBaseUnitLine.UnavailableReason);

        var fullLine = rows.Single(r => r.PurchaseOrderId == full.Id);
        Assert.False(fullLine.Available);
        Assert.Equal(0m, fullLine.RemainingBaseQuantity);
        Assert.Equal(StockInOrderFulfillmentRules.ZeroCapacityText, fullLine.UnavailableReason);

        // 详情整单不可用（无可用行）
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
        var supplierId = await SeedSupplierAsync(db, "候选详情供应商");
        var productId = await SeedProductAsync(db, "候选详情商品", "PCS", string.Empty, 0);
        var ownCustomer = await SeedCustomerAsync(db, "候选详情客户");
        var foreignCustomer = await SeedCustomerAsync(db, "候选详情范围外客户");
        var userId = await SeedOperatorAsync(db, ownCustomer, "ok");

        var own = await SeedOrderAsync(db, $"PO-SEL-DET-{Tag()}", supplierId, DocumentStatus.Approved, ownCustomer,
            (productId, "PCS", 5m));
        var pending = await SeedOrderAsync(db, $"PO-SEL-DETP-{Tag()}", supplierId, DocumentStatus.Pending, ownCustomer,
            (productId, "PCS", 5m));
        var deleted = await SeedOrderAsync(db, $"PO-SEL-DETD-{Tag()}", supplierId, DocumentStatus.Approved, ownCustomer,
            (productId, "PCS", 5m));
        deleted.IsDeleted = true;
        var foreign = await SeedOrderAsync(db, $"PO-SEL-DETF-{Tag()}", supplierId, DocumentStatus.Approved, foreignCustomer,
            (productId, "PCS", 5m));
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        Assert.Equal(own.Id, Detail(await ctl.GetSourceCandidateDetail(own.Id)).PurchaseOrderId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(pending.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(foreign.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.DoesNotContain(rows, r => r.PurchaseOrderId == foreign.Id);
    }

    // ==================== 5. 陈旧来源（候选后被取消）审核 fail closed ====================

    [Fact]
    public async Task Approval_of_stale_cancelled_source_fails_closed_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "陈旧来源供应商");
        var warehouseId = await SeedWarehouseAsync(db, "陈旧来源仓");
        var productId = await SeedProductAsync(db, "陈旧来源商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(db);

        var order = await SeedOrderAsync(db, $"PO-SEL-STALE-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productId, "PCS", 10m));
        var receipt = await SeedReceiptAsync(db, order.Id, supplierId, warehouseId, DocumentStatus.Submitted, false,
            (productId, "PCS", 5m));

        // 候选窗口内来源被取消 → 审核必须 fail closed
        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var ctl = NewController(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(receipt.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 单据状态 / 库存 / 流水保持不变（保留用户输入，不产生半成品写入）
        var reloaded = await db.StockIns.AsNoTracking().SingleAsync(s => s.Id == receipt.Id);
        Assert.Equal(DocumentStatus.Submitted, reloaded.Status);
        Assert.Empty(await db.StockMovements.Where(m => m.SourceDocId == receipt.Id && m.MovementType == InventoryMovementType.PurchaseIn).ToListAsync());
        Assert.Empty(await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId).ToListAsync());
    }

    // ==================== 6. 两条独立连接竞争：同来源两张入库单不超收 ====================

    [Fact]
    public async Task Concurrent_approvals_of_two_receipts_against_one_order_do_not_overreceive()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(seed, "并发来源供应商");
        var warehouseId = await SeedWarehouseAsync(seed, "并发来源仓");
        var productId = await SeedProductAsync(seed, "并发来源商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(seed);

        var order = await SeedOrderAsync(seed, $"PO-SEL-RACE-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productId, "PCS", 10m));
        var docA = await SeedReceiptAsync(seed, order.Id, supplierId, warehouseId, DocumentStatus.Submitted, false,
            (productId, "PCS", 6m));
        var docB = await SeedReceiptAsync(seed, order.Id, supplierId, warehouseId, DocumentStatus.Submitted, false,
            (productId, "PCS", 6m));

        var results = await Task.WhenAll(TryApproveAsync(docA.Id, userId), TryApproveAsync(docB.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.Error.Contains("超过来源采购订单授权数量")));

        await using var verify = _fixture.CreateDbContext();
        var posted = await verify.StockMovements.Where(m => !m.IsDeleted && m.WarehouseId == warehouseId && m.ProductId == productId && m.MovementType == InventoryMovementType.PurchaseIn).SumAsync(m => m.Quantity);
        Assert.Equal(6m, posted);
    }

    // ==================== 7. 两条独立连接竞争：同一张入库单只过账一次 ====================

    [Fact]
    public async Task Concurrent_approvals_of_same_receipt_post_exactly_once()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(seed, "并发同单供应商");
        var warehouseId = await SeedWarehouseAsync(seed, "并发同单仓");
        var productId = await SeedProductAsync(seed, "并发同单商品", "PCS", string.Empty, 0);
        var userId = await SeedPrivilegedOperatorAsync(seed);

        var order = await SeedOrderAsync(seed, $"PO-SEL-SAME-{Tag()}", supplierId, DocumentStatus.Approved, null,
            (productId, "PCS", 10m));
        var receipt = await SeedReceiptAsync(seed, order.Id, supplierId, warehouseId, DocumentStatus.Submitted, false,
            (productId, "PCS", 4m));

        var results = await Task.WhenAll(TryApproveAsync(receipt.Id, userId), TryApproveAsync(receipt.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var reloaded = await verify.StockIns.AsNoTracking().SingleAsync(s => s.Id == receipt.Id);
        Assert.Equal(DocumentStatus.Approved, reloaded.Status);
        Assert.Equal(1, await verify.StockMovements.CountAsync(m => !m.IsDeleted && m.SourceDocId == receipt.Id && m.MovementType == InventoryMovementType.PurchaseIn));
        Assert.Equal(4m, await verify.StockMovements.Where(m => !m.IsDeleted && m.WarehouseId == warehouseId && m.ProductId == productId && m.MovementType == InventoryMovementType.PurchaseIn).SumAsync(m => m.Quantity));
    }

    private async Task<(bool Success, string Error)> TryApproveAsync(long stockInId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, userId);
        try
        {
            var result = await ctl.Approve(stockInId);
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

    // ==================== 播种辅助 ====================

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = $"SUP-SEL-{Tag()}", SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"WH-SEL-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
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
            SortOrder = sortOrder, MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>
    /// 播种一个真实业务员账号：<c>ok</c> = 既有「采购入库」+「采购订单」菜单；
    /// <c>no-stock-in</c> = 缺「采购入库」；<c>no-purchase-order</c> = 缺「采购订单」；
    /// <c>disabled</c> = 已禁用；<c>missing</c> = 仅播种（控制器不注入身份）。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, long customerId, string scenario)
    {
        var role = new SysRole { RoleCode = $"SI-SEL-{Tag()}", RoleName = "入库来源操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"si-sel-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "入库来源操作员",
            Status = scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "入库来源操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (scenario is not ("no-stock-in" or "missing"))
        {
            var stockInMenu = await EnsureMenuAsync(db, StockInAuthorizationRules.RequiredMenuCode,
                StockInAuthorizationRules.RequiredMenuText, "/logistics/stock-in", 40);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = stockInMenu.Id });
        }
        if (scenario is not ("no-purchase-order" or "missing"))
        {
            var purchaseOrderMenu = await EnsureMenuAsync(db, StockInAuthorizationRules.SourceRequiredMenuCode,
                StockInAuthorizationRules.SourceRequiredMenuText, "/purchase/order", 10);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = purchaseOrderMenu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>播种特权入库操作员（系统内置角色 = 不受数据范围限制）+ 既有「采购入库」菜单。</summary>
    private static async Task<long> SeedPrivilegedOperatorAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted && u.Status == UserStatus.Enabled)
            .Select(u => u.Id).SingleAsync();

    private static async Task<PurchaseOrder> SeedOrderAsync(ErpDbContext db, string orderNo, long supplierId,
        DocumentStatus status, long? owningCustomerId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = status,
            OwningCustomerId = owningCustomerId
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        foreach (var (productId, unit, quantity) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
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

    private static async Task<StockIn> SeedReceiptAsync(ErpDbContext db, long orderId, long supplierId,
        long warehouseId, DocumentStatus status, bool deleted,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            StockInNo = $"RK-SEL-SQL-{Tag()}",
            StockInDate = DateTime.Today,
            PurchaseOrderId = orderId,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = status,
            IsDeleted = deleted,
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
}

/// <summary>
/// 专用 localdb 夹具：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class StockInSourceSelectionSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKINSOURCE_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-375] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-375] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 stock-in / purchase-order 菜单）。");
    }
}

/// <summary>
/// ERP-375 目标库护栏单元级校验：非专用 localdb 目标必须在<b>访问数据库之前</b>被拒绝
/// （实例名 / 库名前缀 / 集成安全）。本类不打开任何连接。
/// </summary>
public sealed class StockInSourceSelectionTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockInSourceSelectionSqlServerFixture.AssertDedicatedTarget(connection));
}
