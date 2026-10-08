using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-371 采购订单实时授权与数据范围护栏单元测试（内存库 + 真实 HTTP 身份）。
/// <para>覆盖：缺失身份 / 禁用账号 / 撤销菜单在核心路由（列表 / 详情 / 导出 / 打印 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除）
/// 上 fail closed 且零副作用；受限业务员只读自有客户、写自有客户，越界（含无归属备货）一律拒绝；
/// 特权账号的合规备货采购保持可用；显式销售链接按权威来源客户范围收敛；
/// 修改失败不改动已存单据 / 明细 / 总额；审核与「来源销售订单失效」竞争时拒绝审核。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收；真实身份通过既有角色 / 菜单 /
/// 员工 / 客户 / 销售订单种子构造，不新增任何用户授权。</para>
/// </summary>
public class PurchaseOrderAuthorizationTests
{
    private const long ProductA = 955201L;
    private const long CustomerA = 955301L;
    private const long CustomerB = 955302L;

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Missing_identity_is_rejected_on_every_core_route_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "AUTH-MISSING", DocumentStatus.Pending, CustomerA, line: true);
        var ctl = ForUser(db, null);

        await AssertAllRoutesDenied(ctl, order.Id, ErrorCodes.Unauthorized);

        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending, deleted: false);
    }

    [Fact]
    public async Task Disabled_account_is_rejected_with_forbidden_on_every_core_route()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var userId = SeedUser(db, role, UserStatus.Disabled);
        var order = SeedOrder(db, "AUTH-DISABLED", DocumentStatus.Pending, CustomerA, line: true);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDenied(ctl, order.Id, ErrorCodes.Forbidden);

        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending, deleted: false);
    }

    [Fact]
    public async Task Account_without_purchase_order_menu_is_denied_on_every_core_route()
    {
        using var db = TestDbFactory.Create();
        var other = SeedMenu(db, "stock-query", "库存查询");
        var role = SeedRole(db, other);
        var userId = SeedUser(db, role, UserStatus.Enabled);
        var order = SeedOrder(db, "AUTH-NO-MENU", DocumentStatus.Pending, CustomerA, line: true);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDenied(ctl, order.Id, ErrorCodes.Forbidden);

        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending, deleted: false);
    }

    [Fact]
    public async Task Revoked_menu_authorization_converges_immediately()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var (userId, _) = SeedSalesman(db, role, CustomerA);
        var own = SeedOrder(db, "AUTH-REVOKE", DocumentStatus.Pending, CustomerA, line: true);
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));

        // 撤销既有菜单授权：下一次请求立即收敛（绝不缓存）。
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == role.Id && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        await AssertAllRoutesDenied(ctl, own.Id, ErrorCodes.Forbidden);

        AssertOrderUnchanged(db, own.Id, DocumentStatus.Pending, deleted: false);
    }

    // ==================== 2. 权威客户数据范围（列表 / 详情 / 导出） ====================

    [Fact]
    public async Task Restricted_salesman_reads_only_own_customer_orders()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var (userId, _) = SeedSalesman(db, role, CustomerA);
        var own = SeedOrder(db, "AUTH-OWN", DocumentStatus.Pending, CustomerA, line: true);
        var foreign = SeedOrder(db, "AUTH-FOREIGN", DocumentStatus.Pending, CustomerB, line: true);
        var unowned = SeedOrder(db, "AUTH-UNOWNED", DocumentStatus.Pending, null, line: true);
        var ctl = ForUser(db, userId);

        var page = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 50 }, null));
        var data = Assert.IsType<ApiResponse<PagedResult<PurchaseOrder>>>(page.Value).Data!;
        Assert.Equal(1, data.Total);
        Assert.Equal(own.Id, Assert.Single(data.Items).Id);

        var export = Assert.IsType<OkObjectResult>(await ctl.Export(null, null));
        var exported = Assert.IsType<ApiResponse<List<PurchaseOrder>>>(export.Value).Data!;
        Assert.Equal(own.Id, Assert.Single(exported).Id);

        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));
        Assert.IsType<OkObjectResult>(await ctl.GetPrint(own.Id));

        // 范围外 / 无归属单据一律拒绝（fail closed），列表 / 详情 / 打印同口径。
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(unowned.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetPrint(foreign.Id));
    }

    [Fact]
    public async Task Restricted_salesman_cannot_create_or_update_unowned_stock_procurement()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var (userId, _) = SeedSalesman(db, role, CustomerA);
        var ctl = ForUser(db, userId);

        // 无归属备货采购：受限账号拒绝，且不消耗单据号、不落库。
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Create(NewOrder(null, line: true)));
        Assert.Empty(db.PurchaseOrders);

        // 越界客户（请求侧）同样拒绝。
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Create(NewOrder(CustomerB, line: true)));
        Assert.Empty(db.PurchaseOrders);

        // 自有客户可正常创建备货采购（显式归属客户在范围内）。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(CustomerA, line: true)));
        var created = db.PurchaseOrders.Single();
        Assert.Equal(CustomerA, created.OwningCustomerId);
    }

    [Fact]
    public async Task Unrestricted_stock_procurement_remains_usable()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var ctl = ForUser(db, userId);

        // 特权账号的无归属备货采购保持可用（既有 procurement-for-stock 行为不变）。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(null, line: true)));
        var created = db.PurchaseOrders.Single();
        Assert.Null(created.OwningCustomerId);
        Assert.Null(created.OwningSalesOrderId);
    }

    [Fact]
    public async Task Linked_create_scopes_by_authoritative_sales_order_customer()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-AUTH-1", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-AUTH-1", "客户A", empId: null);
        var so = SeedApprovedSalesOrder(db, "SO-AUTH-1", customer.Id, (ProductA, "PCS"));

        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);

        // 受限业务员：来源客户未分配 → 拒绝（授权销售订单链接构成权威来源要求）。
        var (restrictedUserId, _) = SeedSalesman(db, role, ownedCustomerIds: Array.Empty<long>());
        var restricted = ForUser(db, restrictedUserId);
        await AssertDenied(ErrorCodes.Forbidden, () => restricted.Create(NewLinkedOrder(so.Id, line: true)));
        Assert.Empty(db.PurchaseOrders);

        // 受限业务员拥有来源客户 → 放行，并按权威来源派生归属快照。
        var (ownerUserId, _) = SeedSalesman(db, role, CustomerA);
        var owner = ForUser(db, ownerUserId);
        Assert.IsType<OkObjectResult>(await owner.Create(NewLinkedOrder(so.Id, line: true)));
        var saved = db.PurchaseOrders.Single();
        Assert.Equal(CustomerA, saved.OwningCustomerId);
        Assert.Equal("SO-AUTH-1", saved.OwningSalesOrderNo);
    }

    // ==================== 3. 已存 / 请求两侧归属与失败原子性 ====================

    [Fact]
    public async Task Update_rejects_foreign_stored_order_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var (userId, _) = SeedSalesman(db, role, CustomerA);
        var foreign = SeedOrder(db, "AUTH-UPD-FOREIGN", DocumentStatus.Pending, CustomerB, line: true);
        var ctl = ForUser(db, userId);

        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Update(foreign.Id, NewOrder(CustomerB, line: true)));

        var persisted = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == foreign.Id);
        Assert.Equal(CustomerB, persisted.OwningCustomerId);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(10m, Assert.Single(persisted.Details).Quantity);
    }

    [Fact]
    public async Task Update_rejects_proposed_foreign_customer_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var (userId, _) = SeedSalesman(db, role, CustomerA);
        var own = SeedOrder(db, "AUTH-UPD-OWN", DocumentStatus.Pending, CustomerA, line: true);
        var ctl = ForUser(db, userId);

        // 请求侧越界：拒绝且已存单据字段 / 明细 / 总额保持不变。
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Update(own.Id, NewOrder(CustomerB, line: true)));

        var persisted = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == own.Id);
        Assert.Equal(CustomerA, persisted.OwningCustomerId);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(10m, Assert.Single(persisted.Details).Quantity);

        // 范围内客户可正常改单（授权接入后行为不变）。
        var update = NewOrder(CustomerA, line: true);
        update.Remark = "AUTH-UPD-OK";
        update.Details[0].Quantity = 3m;
        Assert.IsType<OkObjectResult>(await ctl.Update(own.Id, update));
        var reloaded = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == own.Id);
        Assert.Equal(3m, Assert.Single(reloaded.Details).Quantity);
        Assert.Equal(300m, reloaded.TotalAmount);
    }

    [Fact]
    public async Task Failed_linked_update_leaves_totals_details_and_status_unchanged()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-AUTH-2", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-AUTH-2", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-AUTH-2", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedUser(db);
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewLinkedOrder(so.Id, line: true)));
        var id = db.PurchaseOrders.Single().Id;

        // 来源在编辑与保存之间失效：拒绝且已存单据原样保留。
        so.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(id, NewLinkedOrder(so.Id, line: true, remark: "SHOULD_NOT_PERSIST", quantity: 99m)));
        Assert.Contains("已取消", ex.Message);

        var persisted = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == id);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.NotEqual("SHOULD_NOT_PERSIST", persisted.Remark);
        Assert.Equal(200m, persisted.TotalAmount);
        Assert.Equal(2m, Assert.Single(persisted.Details).Quantity);
        Assert.Single(db.PurchaseOrders);
    }

    // ==================== 4. 审核与失效来源竞争 ====================

    [Fact]
    public async Task Approve_is_refused_when_source_sales_order_was_invalidated()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-AUTH-3", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-AUTH-3", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-AUTH-3", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedUser(db);
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewLinkedOrder(so.Id, line: true)));
        var id = db.PurchaseOrders.Single().Id;
        Assert.IsType<OkObjectResult>(await ctl.Submit(id));

        // 来源在审核前失效：审核必须拒绝（不得先审后失效）。
        so.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseOrders.AsNoTracking().Single(o => o.Id == id).Status);
    }

    [Fact]
    public async Task Approve_succeeds_for_valid_linked_source()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-AUTH-4", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-AUTH-4", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-AUTH-4", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedUser(db);
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewLinkedOrder(so.Id, line: true)));
        var id = db.PurchaseOrders.Single().Id;

        Assert.IsType<OkObjectResult>(await ctl.Submit(id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.AsNoTracking().Single(o => o.Id == id).Status);
    }

    // ==================== 5. 规则 / 控制器形状 ====================

    [Fact]
    public void Controller_applies_row_locks_serializable_transactions_and_source_revalidation()
    {
        var controller = ReadSource("src/ERP.Api/Controllers/PurchaseOrderController.cs");

        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.Contains("AcquireOrderStateLocksAsync", controller);
        Assert.Contains("AcquirePurchaseOrderLockAsync", controller);
        Assert.Contains("UPDLOCK, HOLDLOCK", controller);
        Assert.Contains("EnsureSourceLinkStillValidAsync", controller);
        Assert.Contains("ApplyProcurementScopeAsync", controller);
        Assert.Contains("EnsureProposedAuthorizedAsync", controller);
    }

    [Fact]
    public void RuleText_contains_key_contract()
    {
        Assert.Contains("权威", PurchaseOrderAuthorizationRules.RuleText);
        Assert.Contains("fail closed", PurchaseOrderAuthorizationRules.RuleText);
        Assert.Contains("无归属", PurchaseOrderAuthorizationRules.RuleText);
        Assert.Contains("范围", PurchaseOrderAuthorizationRules.RuleText);
    }

    // ==================== 脚手架 ====================

    /// <summary>
    /// 构造绑定到真实 HTTP 请求管线（<c>Request.Path</c> 已赋值）的控制器，使实时授权按真实请求口径生效；
    /// userId 为 null 时模拟匿名但仍在请求管线内的调用（应 fail closed）。
    /// </summary>
    private static PurchaseOrderController ForUser(ErpDbContext db, long? userId, bool httpBound = true)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (httpBound) http.Request.Path = "/api/purchase-orders";
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task AssertAllRoutesDenied(PurchaseOrderController ctl, long id, int code)
    {
        await AssertDenied(code, () => ctl.GetPaged(new PageQuery { PageSize = 10 }, null));
        await AssertDenied(code, () => ctl.GetById(id));
        await AssertDenied(code, () => ctl.GetPrint(id));
        await AssertDenied(code, () => ctl.Export(null, null));
        await AssertDenied(code, () => ctl.ExportExcel(null, null, null, null));
        await AssertDenied(code, () => ctl.Timeline(id));
        await AssertDenied(code, () => ctl.Progress(id));
        await AssertDenied(code, () => ctl.Create(NewOrder(CustomerA, line: true)));
        await AssertDenied(code, () => ctl.Update(id, NewOrder(CustomerA, line: true)));
        await AssertDenied(code, () => ctl.Submit(id));
        await AssertDenied(code, () => ctl.Approve(id));
        await AssertDenied(code, () => ctl.Cancel(id));
        await AssertDenied(code, () => ctl.Delete(id));
    }

    private static async Task AssertDenied(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
    }

    private static void AssertOrderUnchanged(ErpDbContext db, long id, DocumentStatus status, bool deleted)
    {
        var order = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(status, order.Status);
        Assert.Equal(deleted, order.IsDeleted);
        Assert.Single(db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == id && !d.IsDeleted));
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

    private static void SeedProduct(ErpDbContext db, long id, string code, string name, string unit)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { Id = id, CustomerCode = code, CustomerName = name, EmpId = empId, Status = 1 };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedApprovedSalesOrder(ErpDbContext db, string orderNo, long customerId,
        params (long ProductId, string Unit)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            Details = lines.Select(l => new SalesOrderDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = 10m,
                UnitPrice = 20m,
                Amount = 200m
            }).ToList()
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code, string name)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static SysRole SeedRole(ErpDbContext db, params SysMenu[] menus)
    {
        var role = new SysRole { RoleCode = $"PoAuth-{Guid.NewGuid():N}", RoleName = "采购授权测试角色" };
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
            UserName = $"po-auth-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购授权测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有采购订单菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = new SysRole { RoleCode = $"PoPriv-{Guid.NewGuid():N}", RoleName = "采购特权角色", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种受限业务员（员工编码映射 + 指定角色 / 菜单 + 本人客户），返回（用户 Id，员工 Id）。</summary>
    private static (long UserId, long EmployeeId) SeedSalesman(ErpDbContext db, SysRole? role, params long[] ownedCustomerIds)
    {
        var code = $"po-sales-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购业务员",
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
        {
            var existing = db.BaseCustomers.FirstOrDefault(c => c.Id == customerId);
            if (existing is null)
                db.BaseCustomers.Add(new BaseCustomer
                {
                    Id = customerId, CustomerCode = $"C-{customerId}", CustomerName = $"客户{customerId}",
                    EmpId = employee.Id, Status = 1
                });
            else
                existing.EmpId = employee.Id;
            db.SaveChanges();
        }

        return (user.Id, employee.Id);
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string no, DocumentStatus status,
        long? owningCustomerId, bool line)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            Status = status
        };
        if (line)
        {
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
            order.TotalAmount = 1000m;
        }
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>未链接销售订单的采购单（显式归属客户可为空 = 备货采购）。</summary>
    private static PurchaseOrder NewOrder(long? owningCustomerId, bool line)
    {
        var order = new PurchaseOrder
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId
        };
        if (line)
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
        return order;
    }

    /// <summary>显式链接已审核销售订单的采购单（权威来源客户由来源销售订单派生）。</summary>
    private static PurchaseOrder NewLinkedOrder(long salesOrderId, bool line,
        string remark = "", decimal quantity = 2m)
    {
        var order = new PurchaseOrder
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningSalesOrderId = salesOrderId,
            Remark = remark
        };
        if (line)
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                Quantity = quantity, UnitPrice = 100m, Amount = quantity * 100m
            });
        return order;
    }
}
