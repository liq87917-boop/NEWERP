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
/// ERP-370 销售出库单实时授权与数据范围护栏单元测试（内存库 + 真实 HTTP 身份）。
/// <para>覆盖：缺失身份 / 禁用账号 / 撤销菜单 / 受限未映射账号在九条既有路由上 fail closed 且零副作用；
/// 受限业务员只在本人客户范围内读取（列表 / 详情 / 流水）与写入（创建 / 修改）；
/// 创建被拒绝不消耗单据号；修改同时校验「已存」与「请求」客户，且来源链接失败时单据 / 明细 / 库存 / 流水原样保留；
/// 审核恰好一次过账、重复审核被拒；审核 / 取消 / 修改 / 提交 / 删除共用同一把出库单行锁与可串行化事务；
/// 历史账号（未配置任何菜单授权）沿用 ERP-097 权威数据范围。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收；真实身份通过既有角色 / 菜单 /
/// 员工 / 客户种子构造，不新增任何用户授权。</para>
/// </summary>
public class StockOutAuthorizationTests
{
    private const long CustomerA = 942001L;
    private const long CustomerB = 942002L;
    private const long WarehouseA = 942101L;
    private const long ProductA = 942201L;

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Missing_identity_is_rejected_on_every_route_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var document = SeedStockOut(db, "AUTH-MISSING", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        var ctl = ForUser(db, null);

        await AssertAllRoutesDenied(ctl, document.Id, ErrorCodes.Unauthorized);

        AssertUnchanged(db, document.Id, DocumentStatus.Pending, deleted: false);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task Disabled_account_is_rejected_with_forbidden_on_every_route()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        var role = SeedRoleWithMenus(db, menu);
        var userId = SeedUser(db, role, UserStatus.Disabled);
        var document = SeedStockOut(db, "AUTH-DISABLED", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDenied(ctl, document.Id, ErrorCodes.Forbidden);

        AssertUnchanged(db, document.Id, DocumentStatus.Pending, deleted: false);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task Account_with_other_menu_only_is_denied_on_every_route()
    {
        using var db = TestDbFactory.Create();
        var other = SeedMenu(db, "stock-query", "库存查询");
        var role = SeedRoleWithMenus(db, other);
        var userId = SeedUser(db, role, UserStatus.Enabled);
        var document = SeedStockOut(db, "AUTH-NO-MENU", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDenied(ctl, document.Id, ErrorCodes.Forbidden);

        AssertUnchanged(db, document.Id, DocumentStatus.Pending, deleted: false);
    }

    [Fact]
    public async Task Restricted_account_without_salesman_mapping_is_denied()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        // 非系统角色（受限）但没有员工编码映射：fail closed，绝不降级为全局可见。
        var role = SeedRoleWithMenus(db, menu);
        var userId = SeedUser(db, role, UserStatus.Enabled);
        var document = SeedStockOut(db, "AUTH-UNMAPPED", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDenied(ctl, document.Id, ErrorCodes.Forbidden);

        AssertUnchanged(db, document.Id, DocumentStatus.Pending, deleted: false);
    }

    [Fact]
    public async Task Revoked_menu_authorization_converges_immediately()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        var role = SeedRoleWithMenus(db, menu);
        var (userId, _) = SeedRestrictedSalesman(db, role, CustomerA);
        var own = SeedStockOut(db, "AUTH-REVOKE", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));

        // 撤销既有菜单授权：下一次请求立即收敛（绝不缓存）。
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == role.Id && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        await AssertAllRoutesDenied(ctl, own.Id, ErrorCodes.Forbidden);

        AssertUnchanged(db, own.Id, DocumentStatus.Pending, deleted: false);
    }

    // ==================== 2. 权威客户数据范围（列表 / 详情 / 流水 / 创建 / 修改） ====================

    [Fact]
    public async Task Restricted_salesman_reads_only_own_customer_documents()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        var role = SeedRoleWithMenus(db, menu);
        var (userId, _) = SeedRestrictedSalesman(db, role, CustomerA);
        var own = SeedStockOut(db, "AUTH-OWN", CustomerA, DocumentStatus.Pending, (ProductA, 2m));
        var foreign = SeedStockOut(db, "AUTH-FOREIGN", CustomerB, DocumentStatus.Pending, (ProductA, 3m));
        var ctl = ForUser(db, userId);

        var page = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 50 }, null));
        var data = Assert.IsType<ApiResponse<PagedResult<StockOut>>>(page.Value).Data!;
        Assert.Equal(1, data.Total);
        Assert.Equal(own.Id, Assert.Single(data.Items).Id);

        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));
        Assert.IsType<OkObjectResult>(await ctl.GetMovements(own.Id));

        // 范围外单据按「不存在」拒绝（不泄露 Id / 单号），列表 / 详情 / 流水同口径。
        await AssertDenied(ErrorCodes.NotFound, () => ctl.GetById(foreign.Id));
        await AssertDenied(ErrorCodes.NotFound, () => ctl.GetMovements(foreign.Id));
    }

    [Fact]
    public async Task Create_rejects_foreign_customer_without_consuming_document_number()
    {
        using var db = TestDbFactory.Create();
        SeedStockOutNumberRule(db, currentSequence: 5);
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        var role = SeedRoleWithMenus(db, menu);
        var (userId, _) = SeedRestrictedSalesman(db, role, CustomerA);
        var ctl = ForUser(db, userId);

        await AssertDenied(ErrorCodes.NotFound, () => ctl.Create(NewStockOut(CustomerB)));

        Assert.Empty(db.StockOuts);
        Assert.Equal(5, db.SysDocumentNumberRules.Single().CurrentSequence);

        // 范围内客户照常创建。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewStockOut(CustomerA)));
        Assert.Equal(6, db.SysDocumentNumberRules.Single().CurrentSequence);
    }

    [Fact]
    public async Task Update_checks_stored_and_proposed_customer_before_assignment()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        var role = SeedRoleWithMenus(db, menu);
        var (userId, _) = SeedRestrictedSalesman(db, role, CustomerA);
        var own = SeedStockOut(db, "AUTH-UPDATE-OWN", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        var ctl = ForUser(db, userId);

        // 请求侧越界：拒绝且已存单据字段保持不变。
        await AssertDenied(ErrorCodes.NotFound, () => ctl.Update(own.Id, NewStockOut(CustomerB)));

        var persisted = await db.StockOuts.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == own.Id);
        Assert.Equal(CustomerA, persisted.CustomerId);
        Assert.Equal(5m, persisted.TotalQuantity);
        Assert.Equal(5m, Assert.Single(persisted.Details).Quantity);

        // 范围内客户可以正常改单（授权接入后行为不变）。
        var update = NewStockOut(CustomerA);
        update.Remark = "OWN-UPDATE-OK";
        update.Details[0].Quantity = 2m;
        Assert.IsType<OkObjectResult>(await ctl.Update(own.Id, update));
        var reloaded = await db.StockOuts.AsNoTracking().Include(o => o.Details).SingleAsync(o => o.Id == own.Id);
        Assert.Equal(2m, Assert.Single(reloaded.Details).Quantity);
    }

    [Fact]
    public async Task Update_rejects_foreign_stored_document_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, StockOutAuthorizationRules.RequiredMenuCode, "销售出库");
        var role = SeedRoleWithMenus(db, menu);
        var (userId, _) = SeedRestrictedSalesman(db, role, CustomerA);
        var foreign = SeedStockOut(db, "AUTH-UPDATE-FOREIGN", CustomerB, DocumentStatus.Pending, (ProductA, 4m));
        var ctl = ForUser(db, userId);

        await AssertDenied(ErrorCodes.NotFound, () => ctl.Update(foreign.Id, NewStockOut(CustomerB)));

        var persisted = db.StockOuts.AsNoTracking().Single(o => o.Id == foreign.Id);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.Equal("AUTH-UPDATE-FOREIGN", persisted.StockOutNo);
        Assert.Equal(4m, persisted.TotalQuantity);
    }

    [Fact]
    public async Task Failed_source_link_update_leaves_document_details_stock_and_ledger_unchanged()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedOperator(db);
        var document = SeedStockOut(db, "AUTH-LINK-FAIL", CustomerA, DocumentStatus.Pending, (ProductA, 5m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m);
        var ctl = ForUser(db, userId);

        // 显式链接到不存在的销售订单：ERP-343 fail closed，且不落任何半截写入。
        var error = await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Update(document.Id, new StockOut
        {
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            SalesOrderId = 999999L,
            StockOutDate = DateTime.Today,
            Remark = "SHOULD_NOT_PERSIST",
            Details = new List<StockOutDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Unit = "PCS", Quantity = 99m }
            }
        }));
        Assert.Contains("来源销售订单", error.Message);

        var persisted = await db.StockOuts.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == document.Id);
        Assert.Equal(5m, persisted.TotalQuantity);
        Assert.Equal(5m, Assert.Single(persisted.Details).Quantity);
        Assert.NotEqual("SHOULD_NOT_PERSIST", persisted.Remark);
        Assert.Equal(10m, db.Stocks.Single().Quantity);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 3. 状态流转 / 库存与行锁（ERP-343 保留） ====================

    [Fact]
    public async Task Approval_posts_inventory_exactly_once_and_rejects_duplicate()
    {
        using var db = TestDbFactory.Create();
        SeedBaseProduct(db);
        db.SaveChanges();
        var userId = SeedPrivilegedOperator(db);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m);
        var document = SeedStockOut(db, "AUTH-APPROVE", CustomerA, DocumentStatus.Pending, (ProductA, 4m));
        var ctl = ForUser(db, userId);

        await ctl.Submit(document.Id);
        Assert.IsType<OkObjectResult>(await ctl.Approve(document.Id));

        Assert.Equal(6m, db.Stocks.Single().Quantity);
        var movement = Assert.Single(db.StockMovements);
        Assert.Equal(4m, movement.Quantity);
        Assert.Equal(DocumentStatus.Approved, db.StockOuts.Single().Status);

        // 重复审核：锁内幂等护栏拒绝，库存 / 流水绝不重复过账。
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Approve(document.Id));
        Assert.Equal(6m, db.Stocks.Single().Quantity);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task Submitted_or_approved_document_rejects_edit_and_delete_without_mutation()
    {
        using var db = TestDbFactory.Create();
        SeedBaseProduct(db);
        db.SaveChanges();
        var userId = SeedPrivilegedOperator(db);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m);
        var document = SeedStockOut(db, "AUTH-STATE", CustomerA, DocumentStatus.Pending, (ProductA, 4m));
        var ctl = ForUser(db, userId);

        await ctl.Submit(document.Id);
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Update(document.Id, NewStockOut(CustomerA)));
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Delete(document.Id));

        await ctl.Approve(document.Id);
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Update(document.Id, NewStockOut(CustomerA)));

        var persisted = db.StockOuts.AsNoTracking().Single(o => o.Id == document.Id);
        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.False(persisted.IsDeleted);
        Assert.Single(db.StockMovements);
    }

    [Fact]
    public async Task Restricted_salesman_without_menu_grants_keeps_scoped_access()
    {
        using var db = TestDbFactory.Create();
        // 历史账号：未配置任何「角色 → 菜单」授权，沿用既有 ERP-097 权威数据范围（不新增授权、不降级为全局）。
        var (userId, _) = SeedRestrictedSalesmanWithoutMenus(db, CustomerA);
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewStockOut(CustomerA)));
        await AssertDenied(ErrorCodes.NotFound, () => ctl.Create(NewStockOut(CustomerB)));
        Assert.Single(db.StockOuts);
    }

    // ==================== 4. 规则 / 控制器形状（确定性行锁 + 可串行化事务） ====================

    [Fact]
    public void Controller_applies_deterministic_row_lock_and_serializable_transaction_to_state_routes()
    {
        var controller = ReadSource("src/ERP.Api/Controllers/StockOutController.cs");

        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.Contains("LockStockOutRowAsync", controller);
        Assert.Contains("await LockSourceShipmentRowAsync(id);", controller);
        Assert.Contains("StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync", controller);

        // 审核：本出库单行锁必须先于来源销售订单行锁（确定性锁序，避免死锁）。
        var sheetLock = controller.IndexOf("await LockStockOutRowAsync(entity.Id);", StringComparison.Ordinal);
        var orderLock = controller.IndexOf("await AcquireOrderApprovalLockAsync(entity.SalesOrderId);", StringComparison.Ordinal);
        Assert.True(sheetLock > 0, "审核必须取得本出库单行锁");
        Assert.True(orderLock > sheetLock, "来源销售订单行锁必须在本出库单行锁之内");

        // 取消：来源行锁 → 装柜 / 退货护栏 → 冲销（ERP-359 / ERP-367 既有锁序保持不变）。
        var cancelLock = controller.IndexOf("await LockSourceShipmentRowAsync(id);", StringComparison.Ordinal);
        var loadingGuard = controller.IndexOf("await LoadingStockOutLinkRules.EnsureNoEffectiveApprovedLoadingAsync(Db, id);", StringComparison.Ordinal);
        var reversal = controller.IndexOf("await ReverseStockAsync(entity);", StringComparison.Ordinal);
        Assert.True(cancelLock > 0 && loadingGuard > cancelLock && reversal > loadingGuard);
    }

    [Fact]
    public void Rules_are_documented_and_do_not_require_new_grants()
    {
        Assert.Equal("stock-out", StockOutAuthorizationRules.RequiredMenuCode);
        Assert.Equal("销售出库", StockOutAuthorizationRules.RequiredMenuText);
        Assert.Contains("ERP-097", StockOutAuthorizationRules.RuleText);
        Assert.Contains("绝不新增", StockOutAuthorizationRules.RuleText);
        Assert.Contains("不新增", StockOutAuthorizationRules.BoundaryText);
        Assert.Equal(ReturnSourceCancellationRules.LockStockOutRowSql, StockOutAuthorizationRules.LockStockOutRowSql);
        using var db = TestDbFactory.Create();
        Assert.False(ReturnSourceCancellationRules.IsRelationalProvider(db));
    }

    // ==================== 脚手架 ====================

    /// <summary>断言九条既有路由对当前身份一律 fail closed（错误码一致）。</summary>
    private static async Task AssertAllRoutesDenied(StockOutController ctl, long documentId, int expectedCode)
    {
        await AssertDenied(expectedCode, () => ctl.GetPaged(new PageQuery(), null));
        await AssertDenied(expectedCode, () => ctl.GetById(documentId));
        await AssertDenied(expectedCode, () => ctl.GetMovements(documentId));
        await AssertDenied(expectedCode, () => ctl.Create(NewStockOut(CustomerA)));
        await AssertDenied(expectedCode, () => ctl.Update(documentId, NewStockOut(CustomerA)));
        await AssertDenied(expectedCode, () => ctl.Submit(documentId));
        await AssertDenied(expectedCode, () => ctl.Approve(documentId));
        await AssertDenied(expectedCode, () => ctl.Cancel(documentId));
        await AssertDenied(expectedCode, () => ctl.Delete(documentId));
    }

    private static async Task<BusinessException> AssertDenied(int expectedCode, Func<Task<IActionResult>> action)
    {
        var error = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, error.Code);
        return error;
    }

    private static void AssertUnchanged(ErpDbContext db, long id, DocumentStatus status, bool deleted)
    {
        var persisted = db.StockOuts.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(status, persisted.Status);
        Assert.Equal(deleted, persisted.IsDeleted);
        Assert.NotEmpty(persisted.StockOutNo);
    }

    private static StockOutController ForUser(ErpDbContext db, long? userId)
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
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static StockOut NewStockOut(long customerId)
        => new()
        {
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Unit = "PCS", Quantity = 1m }
            }
        };

    private static SysMenu SeedMenu(ErpDbContext db, string code, string name)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = "/logistics/stock-out",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static SysRole SeedRoleWithMenus(ErpDbContext db, params SysMenu[] menus)
    {
        var role = new SysRole { RoleCode = $"StockOutAuth-{Guid.NewGuid():N}", RoleName = "销售出库测试角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        foreach (var menu in menus)
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return role;
    }

    private static long SeedUser(ErpDbContext db, SysRole role, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"stockout-auth-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销售出库授权测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种特权出库操作员（系统内置角色 = 既有全部菜单继承口径，不新增任何用户授权）。</summary>
    private static long SeedPrivilegedOperator(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleCode = $"StockOutPrivileged-{Guid.NewGuid():N}",
            RoleName = "出库特权角色",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种受限业务员（员工编码映射 + 本人客户 + 既有菜单授权），返回（用户 Id，员工 Id）。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedSalesman(
        ErpDbContext db, SysRole role, params long[] ownedCustomerIds)
        => SeedSalesman(db, $"stockout-sales-{Guid.NewGuid():N}", role, ownedCustomerIds);

    /// <summary>播种历史受限业务员（无任何角色 / 菜单授权，仅员工编码映射 + 本人客户）。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedSalesmanWithoutMenus(
        ErpDbContext db, params long[] ownedCustomerIds)
        => SeedSalesman(db, $"stockout-legacy-{Guid.NewGuid():N}", null, ownedCustomerIds);

    private static (long UserId, long EmployeeId) SeedSalesman(
        ErpDbContext db, string code, SysRole? role, long[] ownedCustomerIds)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "业务员",
            Status = UserStatus.Enabled
        };
        db.BaseEmployees.Add(employee);
        db.SysUsers.Add(user);
        db.SaveChanges();

        if (role is not null)
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            db.SaveChanges();
        }

        foreach (var customerId in ownedCustomerIds)
            db.BaseCustomers.Add(new BaseCustomer
            {
                Id = customerId,
                CustomerCode = $"C-{customerId}",
                CustomerName = $"客户{customerId}",
                EmpId = employee.Id,
                Status = 1
            });
        db.SaveChanges();
        return (user.Id, employee.Id);
    }

    private static void SeedBaseProduct(ErpDbContext db)
        => db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductA,
            ProductCode = "AUTH-P1",
            ProductName = "商品A",
            Spec = "规格A",
            Unit = "PCS"
        });

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var document = new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Status = status
        };
        db.StockOuts.Add(document);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = document.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = quantity
            });
        db.SaveChanges();
        return document;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity
        });
        db.SaveChanges();
    }

    private static void SeedStockOutNumberRule(ErpDbContext db, long currentSequence)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockOut,
            RuleCode = "CK-AUTH",
            RuleName = "销售出库单号",
            Prefix = "CK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = currentSequence
        });
        db.SaveChanges();
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
