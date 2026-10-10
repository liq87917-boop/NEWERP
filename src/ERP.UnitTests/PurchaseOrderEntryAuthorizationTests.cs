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
/// ERP-466 规范采购订单（<c>api/purchase-orders</c>）入口实时授权「与请求路径无关」单元测试。
/// <para>以真实 <see cref="DefaultHttpContext"/> 身份驱动真实 <see cref="PurchaseOrderController"/>：空路径与已赋值路径
/// 必须给出完全一致的判定；绑定到请求管线但缺失 / 零 / 未知 / 已删除身份一律未认证（2000），
/// 禁用 / 缺少或撤销既有「采购订单」菜单一律权限不足（2002），且全部先于任何订单 / 明细 / 派生读取与写入；
/// 授权身份完成既有生命周期、拒绝调用零写入且不消耗单据号；受限业务员仍按 ERP-097 权威客户范围收口。</para>
/// <para>默认<strong>不设置</strong> <c>Request.Path</c>（空路径请求）用于证明授权判定与请求形状无关；
/// 全部使用隔离内存测试数据，绝不新增生产菜单 / 角色 / 用户授权，也不提供任何测试专用开关。</para>
/// </summary>
public class PurchaseOrderEntryAuthorizationTests
{
    private const long ProductA = 966201L;
    private const long CustomerA = 966301L;
    private const long CustomerB = 966302L;
    private const string PopulatedPath = "/api/purchase-orders";

    // ==================== 1. 绑定到请求管线但缺少身份：空路径与已赋值路径判决完全一致 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(PopulatedPath)]
    public async Task Bound_request_without_identity_is_unauthorized_on_every_core_route(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "ERP466-NOIDENT", DocumentStatus.Pending, CustomerA);
        var ctl = NewController(db, userId: null, requestPath);

        await AssertAllCoreRoutesDenied(ctl, order.Id, ErrorCodes.Unauthorized);

        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending);
        Assert.Single(db.PurchaseOrders);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PopulatedPath)]
    public async Task Disabled_account_is_forbidden_on_every_core_route(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, status: UserStatus.Disabled);
        var order = SeedOrder(db, "ERP466-DISABLED", DocumentStatus.Pending, CustomerA);
        var ctl = NewController(db, userId, requestPath);

        await AssertAllCoreRoutesDenied(ctl, order.Id, ErrorCodes.Forbidden);

        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PopulatedPath)]
    public async Task Account_without_purchase_order_menu_is_forbidden_on_every_core_route(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, menu: false);
        var order = SeedOrder(db, "ERP466-NOMENU", DocumentStatus.Pending, CustomerA);
        var ctl = NewController(db, userId, requestPath);

        await AssertAllCoreRoutesDenied(ctl, order.Id, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PopulatedPath)]
    public async Task Revoked_menu_converges_immediately_on_both_paths(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId) = SeedOperator(db);
        var order = SeedOrder(db, "ERP466-REVOKE", DocumentStatus.Pending, CustomerA);
        var ctl = NewController(db, userId, requestPath);

        Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id));

        // 撤销既有菜单授权：下一次请求立即收敛（绝不缓存）。
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        await AssertAllCoreRoutesDenied(ctl, order.Id, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PopulatedPath)]
    public async Task Zero_unknown_and_deleted_identity_are_unauthorized_on_both_paths(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (deletedUserId, _) = SeedOperator(db);
        var deleted = db.SysUsers.Single(u => u.Id == deletedUserId);
        deleted.IsDeleted = true;
        db.SaveChanges();
        var order = SeedOrder(db, "ERP466-DELETED", DocumentStatus.Pending, CustomerA);

        foreach (var userId in new long?[] { 0L, 9_466_999_999L, deletedUserId })
        {
            var ctl = NewController(db, userId, requestPath);
            await AssertAllCoreRoutesDenied(ctl, order.Id, ErrorCodes.Unauthorized);
        }

        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending);
    }

    // ==================== 2. 空路径与已赋值路径对同一身份判定完全一致 ====================

    [Fact]
    public async Task Authorized_identity_reads_are_identical_on_empty_and_populated_paths()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var (userId, _) = SeedOperator(db);
        var order = SeedOrder(db, "ERP466-PATH", DocumentStatus.Pending, CustomerA);

        foreach (var requestPath in new[] { null, PopulatedPath })
        {
            var ctl = NewController(db, userId, requestPath);
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 10 }, null));
            Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.Export(null, null));
            Assert.IsType<OkObjectResult>(await ctl.Timeline(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.Progress(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.FinanceReconciliation(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.ReturnImpact(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.InvoiceEvidence(order.Id));
            Assert.IsType<OkObjectResult>(await ctl.PaymentEvidence(order.Id));
        }
    }

    [Fact]
    public async Task Restricted_salesman_scope_is_identical_on_empty_and_populated_paths()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var userId = SeedRestrictedOperator(db, CustomerA);
        var own = SeedOrder(db, "ERP466-OWN", DocumentStatus.Pending, CustomerA);
        var foreign = SeedOrder(db, "ERP466-FOREIGN", DocumentStatus.Pending, CustomerB);

        foreach (var requestPath in new[] { null, PopulatedPath })
        {
            var ctl = NewController(db, userId, requestPath);

            var page = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 50 }, null));
            var data = Assert.IsType<ApiResponse<PagedResult<PurchaseOrder>>>(page.Value).Data!;
            Assert.Equal(own.Id, Assert.Single(data.Items).Id);

            Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));
            await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
            await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetPrint(foreign.Id));
        }
    }

    // ==================== 3. 授权身份在空路径请求上完成既有生命周期 ====================

    [Fact]
    public async Task Authorized_identity_completes_lifecycle_on_empty_request_path()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var (userId, _) = SeedOperator(db);
        var ctl = NewController(db, userId, requestPath: null);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(CustomerA)));
        var created = db.PurchaseOrders.Single();
        Assert.Equal(CustomerA, created.OwningCustomerId);
        Assert.Equal(DocumentStatus.Pending, created.Status);

        Assert.IsType<OkObjectResult>(await ctl.Update(created.Id, NewOrder(CustomerA)));
        Assert.IsType<OkObjectResult>(await ctl.Submit(created.Id));
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseOrders.Single().Status);
        Assert.IsType<OkObjectResult>(await ctl.Approve(created.Id));
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.Single().Status);
    }

    // ==================== 4. 拒绝调用零写入且不消耗单据号 ====================

    [Fact]
    public async Task Denied_calls_persist_zero_mutation_and_consume_no_document_number()
    {
        using var db = TestDbFactory.Create();
        SeedDocumentNumberRule(db);
        var order = SeedOrder(db, "ERP466-ZERO", DocumentStatus.Pending, CustomerA);

        foreach (var requestPath in new[] { null, PopulatedPath })
        {
            var ctl = NewController(db, userId: null, requestPath);
            await AssertAllCoreRoutesDenied(ctl, order.Id, ErrorCodes.Unauthorized);
        }

        Assert.Single(db.PurchaseOrders);
        AssertOrderUnchanged(db, order.Id, DocumentStatus.Pending);
        Assert.Equal(0L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.PurchaseOrder).CurrentSequence);
    }

    // ==================== 5. 控制器源码契约：授权门与请求路径 / 身份形状无关且无兜底 ====================

    [Fact]
    public void Controller_authorization_probe_is_path_and_identity_independent()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseOrderController.cs");

        // 授权门只依据「是否绑定到请求管线」，不再依据 Request.Path 或身份是否可解析。
        Assert.Contains("ControllerContext?.HttpContext is not null", controller);
        Assert.DoesNotContain("http.Request.Path.HasValue ||", controller);
        Assert.DoesNotContain("RequiresLiveAuthorization()\n    {\n        var http", controller);

        // 无匿名 / 角色白名单 / 环境变量 / 内存库开关兜底。
        Assert.DoesNotContain("AllowAnonymous", controller);
        Assert.DoesNotContain("[Authorize(Roles", controller);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller);
        Assert.DoesNotContain("UseInMemoryDatabase", controller);

        // 每一条入口仍在读写之前调用实时授权 / 范围下推。
        Assert.Contains("EnsureMenuAuthorizedAsync", controller);
        Assert.Contains("EnsureOrderAuthorizedAsync", controller);
        Assert.Contains("EnsureProposedAuthorizedAsync", controller);
        Assert.Contains("EnsureOrderIdAuthorizedAsync", controller);
        Assert.Contains("EnsureOrderIdsAuthorizedAsync", controller);
        Assert.Contains("ApplyProcurementScopeAsync", controller);

        // 口径文案与实现同源。
        Assert.Contains("Request.Path", PurchaseOrderAuthorizationRules.PathIndependenceText);
        Assert.Contains("HTTP 请求管线", PurchaseOrderAuthorizationRules.PathIndependenceText);
        Assert.Contains("未认证", PurchaseOrderAuthorizationRules.PathIndependenceText);
        Assert.Contains("权限不足", PurchaseOrderAuthorizationRules.PathIndependenceText);
    }

    // ==================== 脚手架（隔离内存测试数据，绝不新增生产授权） ====================

    /// <summary>绑定真实登录身份（<paramref name="requestPath"/> 为 null = 空路径请求）的真实控制器。</summary>
    private static PurchaseOrderController NewController(ErpDbContext db, long? userId, string? requestPath)
    {
        var claims = userId is > 0
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>播种既有「采购订单」菜单授权的启用账号（默认特权系统角色 → 不受数据范围限制）。</summary>
    private static (long UserId, long RoleId) SeedOperator(
        ErpDbContext db, bool menu = true, UserStatus status = UserStatus.Enabled, bool privileged = true)
    {
        var role = new SysRole
        {
            RoleCode = $"PoEntry-{Guid.NewGuid():N}", RoleName = "采购入口测试角色", IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"po-entry-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购入口测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (menu)
        {
            var requiredMenu = EnsureRequiredMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = requiredMenu.Id });
            db.SaveChanges();
        }

        return (user.Id, role.Id);
    }

    /// <summary>播种受限业务员账号（登录账号 == 员工编码，ERP-097 权威映射）+ 既有「采购订单」菜单授权。</summary>
    private static long SeedRestrictedOperator(ErpDbContext db, params long[] ownedCustomerIds)
    {
        var code = $"po-entry-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var menu = EnsureRequiredMenu(db);
        var role = new SysRole { RoleCode = $"PoEntryOp-{Guid.NewGuid():N}", RoleName = "采购入口受限角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
        {
            var customer = db.BaseCustomers.FirstOrDefault(c => c.Id == customerId);
            if (customer is null)
                db.BaseCustomers.Add(new BaseCustomer
                {
                    Id = customerId, CustomerCode = $"C-466-{customerId}", CustomerName = $"客户{customerId}",
                    EmpId = employee.Id, Status = 1
                });
            else
                customer.EmpId = employee.Id;
        }
        db.SaveChanges();
        return user.Id;
    }

    private static SysMenu EnsureRequiredMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string no, DocumentStatus status, long? owningCustomerId)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            Status = status,
            TotalAmount = 1000m
        };
        order.Details.Add(new PurchaseOrderDetail
        {
            ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
            Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder NewOrder(long? owningCustomerId)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };

    /// <summary>只补缺失行的既有合法主数据（供应商 Id=1 与商品 <see cref="ProductA"/>，单位 PCS），绝不改写生产校验口径。</summary>
    private static void SeedMasterFixtures(ErpDbContext db)
    {
        if (!db.BaseSuppliers.Any(s => s.Id == 1L))
            db.BaseSuppliers.Add(new BaseSupplier
            {
                Id = 1L, SupplierCode = "S-466-1", SupplierName = "ERP466 供应商", Status = 1
            });
        if (!db.BaseProducts.Any(p => p.Id == ProductA))
            db.BaseProducts.Add(new BaseProduct
            {
                Id = ProductA, ProductCode = "P-466-1", ProductName = "商品A", Spec = "规格A",
                Unit = "PCS", Status = 1
            });
        db.SaveChanges();
    }

    /// <summary>播种采购订单号规则（流水从 0 开始），用于断言被拒绝的调用绝不消耗单据号。</summary>
    private static void SeedDocumentNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseOrder,
            RuleCode = "PO-466",
            RuleName = "采购订单号规则",
            Prefix = "PO",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    /// <summary>每一条公开核心入口：先授权、后读写；拒绝时返回同一受控错误码。</summary>
    private static async Task AssertAllCoreRoutesDenied(PurchaseOrderController ctl, long id, int code)
    {
        await AssertDenied(code, () => ctl.GetPaged(new PageQuery { PageSize = 10 }, null));
        await AssertDenied(code, () => ctl.GetById(id));
        await AssertDenied(code, () => ctl.GetPrint(id));
        await AssertDenied(code, () => ctl.Export(null, null));
        await AssertDenied(code, () => ctl.ExportExcel(null, null, null, null));
        await AssertDenied(code, () => ctl.Timeline(id));
        await AssertDenied(code, () => ctl.Progress(id));
        await AssertDenied(code, () => ctl.FinanceReconciliation(id));
        await AssertDenied(code, () => ctl.ReturnImpact(id));
        await AssertDenied(code, () => ctl.InvoiceEvidence(id));
        await AssertDenied(code, () => ctl.PaymentEvidence(id));
        await AssertDenied(code, () => ctl.Create(NewOrder(null)));
        await AssertDenied(code, () => ctl.Update(id, NewOrder(null)));
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

    private static void AssertOrderUnchanged(ErpDbContext db, long id, DocumentStatus status)
    {
        var order = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(status, order.Status);
        Assert.False(order.IsDeleted);
        Assert.Single(db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == id && !d.IsDeleted));
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray()));
    }
}
