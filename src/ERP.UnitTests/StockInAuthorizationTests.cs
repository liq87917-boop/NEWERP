using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 采购入库测试身份 / 主数据脚手架（ERP-352）：为直接实例化 <see cref="StockInController"/> 的单元测试
/// 注入「特权 / 受限制入库操作员」的**真实 HTTP 身份**，并播种 stock-in 菜单与真实可用供应商 / 仓库。
/// </summary>
public static class StockInTestAuthorization
{
    /// <summary>播种 stock-in 菜单（幂等），供角色 → 菜单授权复用。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == "stock-in" && !m.IsDeleted);
        if (existing is not null)
            return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = "stock-in",
            MenuName = "采购入库",
            Path = "/logistics/stock-in",
            Icon = "PackagePlus",
            SortOrder = 0,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种真实可用供应商（存在、未删除、已启用）。</summary>
    public static BaseSupplier SeedSupplier(ErpDbContext db, long id, string name = "测试供应商")
    {
        var supplier = new BaseSupplier
        {
            Id = id,
            SupplierCode = $"SUP-{id}",
            SupplierName = name,
            Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    /// <summary>播种真实可用仓库（存在、未删除、已启用）。</summary>
    public static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name = "测试仓库")
    {
        var warehouse = new BaseWarehouse
        {
            Id = id,
            WarehouseCode = $"WH-{id}",
            WarehouseName = name,
            Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        return warehouse;
    }

    /// <summary>
    /// ERP-504：播种真实可用商品（存在、未删除、已启用；幂等）。
    /// 创建 / 修改的草稿护栏要求明细商品真实在用，既有夹具据此补齐主数据（不新增任何授权与业务断言）。
    /// </summary>
    public static BaseProduct SeedProduct(ErpDbContext db, long id, string unit = "PCS",
        string packageUnit = "", int unitsPerPackage = 0, string name = "测试商品")
    {
        var existing = db.BaseProducts.FirstOrDefault(p => p.Id == id);
        if (existing is not null)
            return existing;

        var product = new BaseProduct
        {
            Id = id,
            ProductCode = $"P-{id}",
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage,
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    /// <summary>播种一个特权入库操作员（SuperAdmin 角色 + stock-in 菜单）并返回其用户 Id。</summary>
    public static long SeedPrivilegedInboundOperator(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = "SuperAdmin", RoleName = "测试超级管理员", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"in-auth-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "入库测试管理员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>创建已注入特权身份的 StockInController。</summary>
    public static StockInController Create(ErpDbContext db)
        => ForUser(db, SeedPrivilegedInboundOperator(db));

    /// <summary>把指定登录用户 Id（可空 = 无身份）写入控制器 HttpContext。</summary>
    public static StockInController ForUser(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new StockInController(db, new DocumentNumberService(db), new InventoryService(db))
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

    /// <summary>
    /// 播种一个受限制的入库操作员（业务员映射 + stock-in 菜单，无特权角色），
    /// 并分配一个归属客户；返回（用户 Id, 归属客户 Id）。
    /// </summary>
    public static (long UserId, long CustomerId) SeedRestrictedInboundOperator(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var employee = new BaseEmployee
        {
            EmployeeCode = $"op-{Guid.NewGuid():N}",
            EmployeeName = "入库操作员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "入库操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"StockInOp-{Guid.NewGuid():N}", RoleName = "入库操作员角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "归属客户",
            EmpId = employee.Id,
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return (user.Id, customer.Id);
    }
}

/// <summary>
/// ERP-352 采购入库单实时授权与数据范围场景测试（内存库 + 真实 HTTP 身份，不连接 SQL Server、不启动 API）。
/// <para>覆盖：缺失 / 禁用 / 未映射身份与缺失 stock-in 菜单授权全部 fail closed；全部路由对拒绝方零副作用；
/// 已授权入库操作员（含合法未链接入库单）放行；链接采购订单归属客户越界不泄露；编辑把来源改到范围外被拒绝
/// 且已存来源不变；读取与审核 / 取消之间撤销授权立即收敛；以及授权路径的基础单位数量 / 订单成本 / 冲销口径。</para>
/// </summary>
public class StockInAuthorizationTests
{
    private const long SupplierA = 960001L;
    private const long WarehouseA = 960101L;
    private const long ProductA = 960201L;
    private const long MissingSupplier = 960999L;

    // ==================== 身份 / 菜单 / 主数据 fail closed ====================

    [Fact]
    public async Task Create_missing_identity_is_unauthorized_and_consumes_no_document_number()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, null);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockIn(null)));

        Assert.Equal(ErrorCodes.Unauthorized, error.Code);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, DocumentSequence(db));
    }

    [Fact]
    public async Task Create_user_without_stock_in_menu_permission_is_forbidden()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var userId = SeedOperator(db, withStockInMenu: false, UserStatus.Enabled);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockIn(null)));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Empty(db.StockIns);
        Assert.Equal(0L, DocumentSequence(db));
    }

    [Fact]
    public async Task Create_disabled_account_is_forbidden_even_with_menu_permission()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var userId = SeedOperator(db, withStockInMenu: true, UserStatus.Disabled);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockIn(null)));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Empty(db.StockIns);
        Assert.Equal(0L, DocumentSequence(db));
    }

    [Fact]
    public async Task Create_menu_user_without_salesman_mapping_cannot_take_unlinked_receipt()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var userId = SeedOperator(db, withStockInMenu: true, UserStatus.Enabled);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockIn(null)));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Empty(db.StockIns);
        Assert.Equal(0L, DocumentSequence(db));
    }

    [Fact]
    public async Task Create_missing_supplier_or_disabled_warehouse_is_rejected_without_write()
    {
        using var db = TestDbFactory.Create();
        StockInTestAuthorization.SeedSupplier(db, SupplierA);
        db.BaseWarehouses.Add(new BaseWarehouse
        {
            Id = WarehouseA,
            WarehouseCode = "WH-AUTH-OFF",
            WarehouseName = "已停用仓库",
            Status = 0
        });
        db.SaveChanges();
        SeedStockInNumberRule(db);
        var ctl = StockInTestAuthorization.Create(db);

        var supplierError = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Create(NewStockIn(null, supplierId: MissingSupplier)));
        Assert.Equal(ErrorCodes.NotFound, supplierError.Code);

        var warehouseError = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Create(NewStockIn(null, warehouseId: WarehouseA)));
        Assert.Equal(ErrorCodes.RuleConflict, warehouseError.Code);

        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, DocumentSequence(db));
    }

    // ==================== 全部路由对拒绝方零副作用 ====================

    [Fact]
    public async Task All_routes_fail_closed_for_unauthenticated_caller_without_any_mutation()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var (_, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var order = SeedApprovedOrder(db, "PO-AUTH-ALL", customerId);
        var pending = SeedReceipt(db, "RK-AUTH-ALL-1", DocumentStatus.Pending, order.Id);
        var submitted = SeedReceipt(db, "RK-AUTH-ALL-2", DocumentStatus.Submitted, order.Id);
        // 拒绝方身份缺失：即使单据链接的是本人客户订单也不能放宽。
        var ctl = StockInTestAuthorization.ForUser(db, null);

        await AssertUnauthorized(() => ctl.GetPaged(new PageQuery(), null));
        await AssertUnauthorized(() => ctl.GetById(pending.Id));
        await AssertUnauthorized(() => ctl.GetMovements(pending.Id));
        await AssertUnauthorized(() => ctl.Create(NewStockIn(order.Id)));
        await AssertUnauthorized(() => ctl.Update(pending.Id, NewStockIn(order.Id)));
        await AssertUnauthorized(() => ctl.Submit(pending.Id));
        await AssertUnauthorized(() => ctl.Approve(submitted.Id));
        await AssertUnauthorized(() => ctl.Cancel(submitted.Id));
        await AssertUnauthorized(() => ctl.Delete(pending.Id));

        var stored = db.StockIns.AsNoTracking().OrderBy(o => o.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(DocumentStatus.Pending, stored[0].Status);
        Assert.Equal(DocumentStatus.Submitted, stored[1].Status);
        Assert.All(stored, o => Assert.False(o.IsDeleted));
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
        Assert.Equal(0L, DocumentSequence(db));
    }

    // ==================== 已授权入库操作员放行 ====================

    [Fact]
    public async Task Authorized_privileged_operator_may_take_unlinked_stocking_receipt()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var ctl = StockInTestAuthorization.Create(db);

        var result = await ctl.Create(NewStockIn(null));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Single();
        Assert.Null(saved.PurchaseOrderId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(10m, saved.TotalQuantity);
        Assert.StartsWith("RK", saved.StockInNo);
        Assert.Equal(1L, DocumentSequence(db));
    }

    [Fact]
    public async Task Restricted_operator_may_take_receipt_linked_to_own_customer_order()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var order = SeedApprovedOrder(db, "PO-AUTH-OWN", customerId);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var result = await ctl.Create(NewStockIn(order.Id));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(order.Id, db.StockIns.Single().PurchaseOrderId);
    }

    [Fact]
    public async Task Restricted_operator_cannot_take_receipt_linked_to_foreign_customer_order()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var (userId, _) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var foreignOrder = SeedApprovedOrder(db, "PO-AUTH-FOREIGN", SeedForeignCustomer(db));
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        // 范围外来源与不存在的来源共用同一非披露错误（绝不透露范围外来源归属客户）。
        var foreignError = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockIn(foreignOrder.Id)));
        var unknownError = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewStockIn(969999L)));

        Assert.Equal(ErrorCodes.NotFound, foreignError.Code);
        Assert.Equal(ErrorCodes.NotFound, unknownError.Code);
        Assert.Equal(StockInAuthorizationRules.SourceScopeDeniedText, foreignError.Message);
        Assert.Equal(StockInAuthorizationRules.SourceScopeDeniedText, unknownError.Message);
        Assert.DoesNotContain("范围外客户", foreignError.Message);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, DocumentSequence(db));
    }

    // ==================== 已存 / 请求来源归属与撤销收敛 ====================

    [Fact]
    public async Task Update_changing_source_to_foreign_customer_order_is_rejected_and_stored_source_kept()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var ownOrder = SeedApprovedOrder(db, "PO-AUTH-UPD-OWN", customerId);
        var foreignOrder = SeedApprovedOrder(db, "PO-AUTH-UPD-FOREIGN", SeedForeignCustomer(db));
        var existing = SeedReceipt(db, "RK-AUTH-UPD", DocumentStatus.Pending, ownOrder.Id);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Update(existing.Id, NewStockIn(foreignOrder.Id)));

        // 范围外新来源与不存在的新来源共用同一非披露错误，且已存来源 / 状态 / 库存 / 流水全部保持不变。
        Assert.Equal(ErrorCodes.NotFound, error.Code);
        Assert.Equal(StockInAuthorizationRules.SourceScopeDeniedText, error.Message);
        var stored = await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == existing.Id);
        Assert.Equal(ownOrder.Id, stored.PurchaseOrderId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    [Fact]
    public async Task Approve_after_menu_revocation_between_read_and_approval_is_forbidden_and_unchanged()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var order = SeedApprovedOrder(db, "PO-AUTH-REVOKE", customerId);
        var receipt = SeedReceipt(db, "RK-AUTH-REVOKE", DocumentStatus.Pending, order.Id);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.GetById(receipt.Id));
        Assert.IsType<OkObjectResult>(await ctl.Submit(receipt.Id));
        Assert.Equal(DocumentStatus.Submitted, receipt.Status);

        // 读取 / 提交之后撤销 stock-in 菜单授权：审核必须立即收敛（fail closed）。
        RevokeStockInMenu(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(receipt.Id));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Equal(DocumentStatus.Submitted, receipt.Status);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task Cancel_after_account_disabled_between_read_and_cancel_is_forbidden_and_unchanged()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var order = SeedApprovedOrder(db, "PO-AUTH-CANCEL", customerId);
        var receipt = SeedReceipt(db, "RK-AUTH-CANCEL", DocumentStatus.Approved, order.Id);
        db.Stocks.Add(new Stock { WarehouseId = WarehouseA, ProductId = ProductA, Quantity = 20m, AvailableQuantity = 20m });
        db.SaveChanges();
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.GetById(receipt.Id));

        db.SysUsers.Single(u => u.Id == userId).Status = UserStatus.Disabled;
        db.SaveChanges();

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(receipt.Id));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Equal(DocumentStatus.Approved, receipt.Status);
        Assert.Equal(20m, db.Stocks.Single().Quantity);
        Assert.Equal(20m, db.Stocks.Single().AvailableQuantity);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 列表 / 详情 / 流水作用域一致 ====================

    [Fact]
    public async Task Paged_list_detail_and_movements_scope_consistently_without_leaking_foreign_receipts()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var ownOrder = SeedApprovedOrder(db, "PO-AUTH-SCOPE-OWN", customerId);
        var foreignOrder = SeedApprovedOrder(db, "PO-AUTH-SCOPE-FOREIGN", SeedForeignCustomer(db));
        var own = SeedReceipt(db, "RK-AUTH-SCOPE-OWN", DocumentStatus.Pending, ownOrder.Id);
        var foreign = SeedReceipt(db, "RK-AUTH-SCOPE-FOREIGN", DocumentStatus.Pending, foreignOrder.Id);
        var unlinked = SeedReceipt(db, "RK-AUTH-SCOPE-NONE", DocumentStatus.Pending, null);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var page = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<StockIn>>>(page.Value);
        var items = payload.Data!.Items.ToList();
        Assert.Equal(2, payload.Data!.Total);
        Assert.Contains(items, o => o.Id == own.Id);
        Assert.Contains(items, o => o.Id == unlinked.Id);
        Assert.DoesNotContain(items, o => o.Id == foreign.Id);

        Assert.IsType<OkObjectResult>(await ctl.GetById(unlinked.Id));
        Assert.IsType<OkObjectResult>(await ctl.GetMovements(unlinked.Id));

        // 范围外与不存在共用同一非披露错误：详情 / 流水都不能透露范围外单据的存在性。
        var detailError = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(foreign.Id));
        var unknownDetailError = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(969999L));
        var movementError = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(foreign.Id));
        var unknownMovementError = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(969999L));

        Assert.Equal(ErrorCodes.NotFound, detailError.Code);
        Assert.Equal(ErrorCodes.NotFound, movementError.Code);
        Assert.Equal(StockInAuthorizationRules.NotFoundText, detailError.Message);
        Assert.Equal(detailError.Message, unknownDetailError.Message);
        Assert.Equal(detailError.Code, unknownDetailError.Code);
        Assert.Equal(StockInAuthorizationRules.NotFoundText, movementError.Message);
        Assert.Equal(movementError.Message, unknownMovementError.Message);
        Assert.Equal(movementError.Code, unknownMovementError.Code);
    }

    // ==================== 非披露：存在与不存在的 Id 共用同一受控错误 ====================

    [Fact]
    public async Task Denied_callers_get_identical_controlled_error_for_existing_and_unknown_receipt_ids()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var (_, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var order = SeedApprovedOrder(db, "PO-AUTH-NONDISCLOSE", customerId);
        var existing = SeedReceipt(db, "RK-AUTH-NONDISCLOSE", DocumentStatus.Pending, order.Id);

        // 缺失身份：已存在与不存在的 Id 都得到受控未认证（绝不因 Id 是否存在而差异化）。
        var anonymous = StockInTestAuthorization.ForUser(db, null);
        Assert.Equal(ErrorCodes.Unauthorized, (await Assert.ThrowsAsync<BusinessException>(() => anonymous.GetById(existing.Id))).Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await Assert.ThrowsAsync<BusinessException>(() => anonymous.GetById(969999L))).Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await Assert.ThrowsAsync<BusinessException>(() => anonymous.GetMovements(existing.Id))).Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await Assert.ThrowsAsync<BusinessException>(() => anonymous.GetMovements(969999L))).Code);

        // 禁用账号 / 无 stock-in 菜单 / 未映射业务员：已存在与不存在的 Id 都得到权限不足。
        foreach (var userId in new[]
                 {
                     SeedOperator(db, withStockInMenu: true, UserStatus.Disabled),
                     SeedOperator(db, withStockInMenu: false, UserStatus.Enabled),
                     SeedOperator(db, withStockInMenu: true, UserStatus.Enabled)
                 })
        {
            var ctl = StockInTestAuthorization.ForUser(db, userId);
            Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(existing.Id))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(969999L))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(existing.Id))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<BusinessException>(() => ctl.GetMovements(969999L))).Code);
        }

        // 零副作用：无写入 / 无状态变化 / 不消耗单据号。
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
        Assert.Equal(DocumentStatus.Pending, db.StockIns.AsNoTracking().Single().Status);
        Assert.Equal(0L, DocumentSequence(db));
    }

    [Fact]
    public async Task Foreign_and_unknown_receipts_share_the_non_disclosing_error_on_every_lifecycle_route()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStockInNumberRule(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var ownOrder = SeedApprovedOrder(db, "PO-AUTH-NONDISC-LC-OWN", customerId);
        var foreignOrder = SeedApprovedOrder(db, "PO-AUTH-NONDISC-LC-FOREIGN", SeedForeignCustomer(db));
        var own = SeedReceipt(db, "RK-AUTH-NONDISC-OWN", DocumentStatus.Submitted, ownOrder.Id);
        var foreign = SeedReceipt(db, "RK-AUTH-NONDISC-FOREIGN", DocumentStatus.Submitted, foreignOrder.Id);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        // 已授权范围内单据仍可读（保留既有读取能力）。
        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));

        // 范围外（foreign）与不存在（unknown）在每一条生命周期路由共用同一非披露错误。
        foreach (var action in new Func<long, Task<IActionResult>>[] { ctl.Submit, ctl.Approve, ctl.Cancel })
        {
            var foreignError = await Assert.ThrowsAsync<BusinessException>(() => action(foreign.Id));
            var unknownError = await Assert.ThrowsAsync<BusinessException>(() => action(969999L));
            Assert.Equal(ErrorCodes.NotFound, foreignError.Code);
            Assert.Equal(StockInAuthorizationRules.NotFoundText, foreignError.Message);
            Assert.Equal(foreignError.Code, unknownError.Code);
            Assert.Equal(foreignError.Message, unknownError.Message);
        }

        var foreignDelete = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(foreign.Id));
        var unknownDelete = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(969999L));
        Assert.Equal(ErrorCodes.NotFound, foreignDelete.Code);
        Assert.Equal(foreignDelete.Code, unknownDelete.Code);

        var foreignUpdate = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Update(foreign.Id, NewStockIn(ownOrder.Id)));
        var unknownUpdate = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Update(969999L, NewStockIn(ownOrder.Id)));
        Assert.Equal(ErrorCodes.NotFound, foreignUpdate.Code);
        Assert.Equal(foreignUpdate.Code, unknownUpdate.Code);

        // 拒绝方零副作用：状态 / 删除态 / 库存 / 流水 / 单据号全部保持原样。
        var stored = db.StockIns.AsNoTracking().OrderBy(o => o.Id).ToList();
        Assert.Equal(DocumentStatus.Submitted, stored.Single(o => o.Id == own.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, stored.Single(o => o.Id == foreign.Id).Status);
        Assert.All(stored, o => Assert.False(o.IsDeleted));
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
        Assert.Equal(0L, DocumentSequence(db));
    }

    // ==================== 授权路径的数量 / 成本 / 冲销口径 ====================

    [Fact]
    public async Task Authorized_operator_posts_base_quantity_and_order_cost_then_reversal_matches()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        var order = SeedApprovedOrder(db, "PO-AUTH-LEDGER", customerId);
        var ctl = StockInTestAuthorization.ForUser(db, userId);
        var id = await CreateAsync(ctl, order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(10m, stock.AvailableQuantity);
        Assert.Equal(75m, stock.TotalCost);
        Assert.Equal(7.5m, stock.AverageCost);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.PurchaseIn, movement.MovementType);
        Assert.Equal(InventoryDocumentHelper.StockInType, movement.SourceDocType);
        Assert.Equal(id, movement.SourceDocId);
        Assert.Equal(1, movement.Direction);
        Assert.Equal(10m, movement.Quantity);
        Assert.Equal(7.5m, movement.UnitCost);
        Assert.Equal(75m, movement.Amount);
        Assert.Contains($"采购订单 Id {order.Id}", movement.Remark);

        await ctl.Cancel(id);

        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(0m, db.Stocks.Single().AvailableQuantity);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single().Status);
        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].IsReversed);
        Assert.True(movements[1].IsReversal);
        Assert.Equal(-1, movements[1].Direction);
        Assert.Equal(0m, db.StockMovements.Sum(m => m.Amount));
    }

    // ==================== 辅助 ====================

    private static void SeedMasterData(ErpDbContext db)
    {
        StockInTestAuthorization.SeedSupplier(db, SupplierA);
        StockInTestAuthorization.SeedWarehouse(db, WarehouseA);
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductA,
            ProductCode = "AUTH-P1",
            ProductName = "商品A",
            Spec = "规格A",
            Unit = "PCS"
        });
        db.SaveChanges();
    }

    private static void SeedStockInNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockIn,
            RuleCode = "RK-AUTH",
            RuleName = "采购入库单号",
            Prefix = "RK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static long SeedOperator(ErpDbContext db, bool withStockInMenu, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"auth-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "授权测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"AuthRole-{Guid.NewGuid():N}", RoleName = "授权测试角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withStockInMenu)
        {
            var menu = StockInTestAuthorization.SeedMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }
        return user.Id;
    }

    private static long SeedForeignCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-FOREIGN-{Guid.NewGuid():N}",
            CustomerName = "范围外客户",
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static PurchaseOrder SeedApprovedOrder(ErpDbContext db, string orderNo, long? owningCustomerId)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved,
            OwningCustomerId = owningCustomerId
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = 100m,
            UnitPrice = 7.5m,
            Amount = 750m
        });
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedReceipt(ErpDbContext db, string no, DocumentStatus status, long? purchaseOrderId)
    {
        var receipt = new StockIn
        {
            StockInNo = no,
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            TotalQuantity = 10m,
            Status = status
        };
        db.StockIns.Add(receipt);
        db.SaveChanges();
        db.StockInDetails.Add(new StockInDetail
        {
            StockInId = receipt.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = 10m
        });
        db.SaveChanges();
        return receipt;
    }

    private static StockIn NewStockIn(long? purchaseOrderId, long supplierId = SupplierA, long warehouseId = WarehouseA)
        => new()
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Remark = "ERP-352_AUTH_TEST",
            Details = new List<StockInDetail>
            {
                new StockInDetail
                {
                    ProductId = ProductA,
                    ProductName = "商品A",
                    Spec = "规格A",
                    Unit = "PCS",
                    Quantity = 10m
                }
            }
        };

    private static async Task<long> CreateAsync(StockInController ctl, long? purchaseOrderId)
    {
        var ok = Assert.IsType<OkObjectResult>(await ctl.Create(NewStockIn(purchaseOrderId)));
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task AssertUnauthorized(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(ErrorCodes.Unauthorized, error.Code);
    }

    private static void RevokeStockInMenu(ErpDbContext db)
    {
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static long DocumentSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.Single().CurrentSequence;
}
