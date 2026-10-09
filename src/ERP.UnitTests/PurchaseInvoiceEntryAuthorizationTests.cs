using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// ERP-462 供应商采购发票授权入口测试脚手架：只在隔离的内存测试数据里播种<strong>既有启用账号 + 既有「采购订单」菜单授权</strong>，
/// 并以真实 <see cref="DefaultHttpContext"/> 身份驱动真实 <see cref="PurchaseInvoiceController"/>。
/// <para>默认<strong>不设置</strong> <c>Request.Path</c>，用于证明授权判定与请求路径完全无关；空路径与已赋值路径必须给出完全一致的判定。
/// 绝不新增任何角色 / 菜单 / 用户授权，也不提供任何测试专用绕过开关（不读环境变量、不按数据库提供程序放行）。</para>
/// </summary>
internal static class PurchaseInvoiceTestIdentities
{
    /// <summary>播种启用账号并授予既有「采购订单」（<c>purchase-order</c>）菜单；<paramref name="privileged"/> = 系统内置角色口径（真正不受限的既有授权账号）。</summary>
    internal static long SeedAuthorizedUser(
        ErpDbContext db, bool privileged = false, UserStatus status = UserStatus.Enabled, bool withMenu = true)
    {
        var menu = FindOrCreateRequiredMenu(db);

        var role = new SysRole
        {
            RoleCode = $"PiEntry-{Guid.NewGuid():N}",
            RoleName = "发票入口授权测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"pi-entry-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "发票入口授权测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withMenu)
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种受限业务员账号（员工编码映射 + 既有菜单 + 本人客户范围），返回（用户 Id，角色 Id，员工 Id）。</summary>
    internal static (long UserId, long RoleId, long EmployeeId) SeedRestrictedOperator(
        ErpDbContext db, params long[] ownedCustomerIds)
    {
        var code = $"pi-entry-sales-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var menu = FindOrCreateRequiredMenu(db);
        var role = new SysRole
        {
            RoleCode = $"PiEntryOp-{Guid.NewGuid():N}",
            RoleName = "发票入口受限角色",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "发票入口受限账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
        {
            var customer = db.BaseCustomers.Single(c => c.Id == customerId);
            customer.EmpId = employee.Id;
        }
        db.SaveChanges();
        return (user.Id, role.Id, employee.Id);
    }

    /// <summary>撤销账号既有最后一个菜单授权（真实「权限撤销」路径）。</summary>
    internal static void RevokeMenu(ErpDbContext db, long roleId)
    {
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>按业务「账号已删除」软删除账号（缺失 / 已删除一律按未认证拒绝）。</summary>
    internal static void SoftDeleteUser(ErpDbContext db, long userId)
    {
        var user = db.SysUsers.Single(u => u.Id == userId);
        user.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>
    /// 以真实登录身份绑定控制器：<paramref name="requestPath"/> 为 null 时<strong>不设置</strong> <c>Request.Path</c>（空路径请求），
    /// 非 null 时按真实路由赋值；两种情况都必须执行完全一致的实时授权。
    /// </summary>
    internal static PurchaseInvoiceController Bind(
        PurchaseInvoiceController controller, long? userId, string? requestPath = null)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>构造绑定真实身份的控制器（默认空路径，证明授权不依赖请求形状）。</summary>
    internal static PurchaseInvoiceController ForUser(
        IErpDbContext db, long? userId, string? requestPath = null)
        => Bind(new PurchaseInvoiceController(db), userId, requestPath);

    private static SysMenu FindOrCreateRequiredMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == PurchaseInvoiceAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            MenuName = PurchaseInvoiceAuthorizationRules.RequiredMenuText,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }
}

/// <summary>
/// ERP-462 供应商采购发票证据入口实时授权单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）。
/// <list type="number">
/// <item><b>与请求形状无关</b>：同一个真实身份在<strong>空 <c>Request.Path</c></strong> 与<strong>已赋值 <c>Request.Path</c></strong>
/// 上必须得到完全一致的判定；缺失 / 零 / 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，且全部发生在任何敏感读取 / 写入之前。</item>
/// <item><b>保留既有业务生命周期与数据范围</b>：既有特权账号与既有菜单授权账号照常完成 台账 / 新增 / 修改 / 关联 / 登记 / 作废
/// 全生命周期；受限业务员对「无来源采购订单」或「来源订单归属客户在本人范围外」的发票一律 fail closed，且被拒绝的请求零写入。</item>
/// <item><b>无匿名 / 管理员兜底</b>：控制器不读 <c>Request.Path</c>、不读环境变量、不使用测试专用开关，也不存在任何匿名 / 管理员回退。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不写生产数据；真实隔离 SQL 授权用例见
/// <c>ERP.IntegrationTests/PurchaseInvoiceEntryAuthorizationSqlServerTests.cs</c>。</para>
/// </summary>
public class PurchaseInvoiceEntryAuthorizationTests
{
    private const string RoutePath = "/api/purchase-invoices";

    // ==================== 0. 脚手架 ====================

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = $"供应商-{code}", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = $"客户-{code}", Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, long? owningCustomerId = null, decimal totalAmount = 1000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算",
            OwningCustomerId = owningCustomerId
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal gross = 100m,
        int status = PurchaseInvoiceRules.StatusDraft)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(string.Empty),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            SupplierCode = "S001",
            SupplierName = "供应商-S001",
            Currency = "CNY",
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            Status = status
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static void SeedAllocation(ErpDbContext db, long invoiceId, PurchaseOrder order, decimal amount)
    {
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoiceId,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderCurrency = "CNY",
            SupplierId = order.SupplierId,
            SupplierCode = "S001",
            SupplierName = "供应商-S001",
            AllocatedAmount = amount,
            Currency = "CNY",
            SortOrder = 1
        });
        db.SaveChanges();
    }

    private static PurchaseInvoiceSaveDto InvoiceDto(long supplierId, string number = "0001", decimal gross = 100m)
        => new()
        {
            InvoiceType = "普票",
            InvoiceNumber = number,
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            Currency = "CNY",
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross
        };

    private static PurchaseInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new PurchaseInvoiceAllocationSaveDto
            {
                PurchaseOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    private static async Task AssertDeniedAsync(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Contains("供应商采购发票", ex.Message);
    }

    private static async Task<int> DeniedCodeAsync(Func<Task<IActionResult>> action)
        => (await Assert.ThrowsAsync<BusinessException>(action)).Code;

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>被拒绝 / 未授权请求的证据快照：发票状态与关联行都必须零改动。</summary>
    private static void AssertZeroMutation(ErpDbContext db, long invoiceId)
    {
        Assert.Equal(PurchaseInvoiceRules.StatusDraft,
            db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == invoiceId).Status);
        Assert.Equal(0, db.PurchaseInvoiceAllocations.Count());
        Assert.Equal(0, db.SupplierPaymentInvoiceAllocations.Count());
    }

    /// <summary>供应商采购发票的每一条公开路由都必须先授权后读写（覆盖 12 条入口）。</summary>
    private static async Task AssertAllRoutesDeniedAsync(
        PurchaseInvoiceController ctl, long invoiceId, long supplierId, int code)
    {
        await AssertDeniedAsync(code, () => ctl.GetPaged(new PurchaseInvoiceQuery()));
        await AssertDeniedAsync(code, () => ctl.GetById(invoiceId));
        await AssertDeniedAsync(code, () => ctl.Reconciliation(new SupplierInvoiceReconciliationQuery()));
        await AssertDeniedAsync(code, () => ctl.ReconciliationAging(new SupplierReconciliationAgingQuery()));
        await AssertDeniedAsync(code, () => ctl.ReconciliationAgingInvoiceDetail(invoiceId));
        await AssertDeniedAsync(code, () => ctl.OrderCandidates(invoiceId, null, 10));
        await AssertDeniedAsync(code, () => ctl.PreviewAllocations(invoiceId, Lines((1L, 10m))));
        await AssertDeniedAsync(code, () => ctl.SaveAllocations(invoiceId, Lines((1L, 10m))));
        await AssertDeniedAsync(code, () => ctl.Create(InvoiceDto(supplierId, "INV-NEW")));
        await AssertDeniedAsync(code, () => ctl.Update(invoiceId, InvoiceDto(supplierId, "INV-UPD")));
        await AssertDeniedAsync(code, () => ctl.Record(invoiceId));
        await AssertDeniedAsync(code, () => ctl.Void(invoiceId, new PurchaseInvoiceVoidRequest { Reason = "重开" }));
    }

    // ==================== 1. 缺失 / 零 / 未知 / 已删除身份：任何路由、任何请求形状一律未认证 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_identity_is_unauthorized_on_every_route_for_empty_and_populated_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-MISS", supplier.Id);
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, null, path);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db, invoice.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Zero_identity_is_unauthorized_on_every_route_for_empty_and_populated_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-ZERO", supplier.Id);
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, 0L, path);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db, invoice.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Unknown_identity_is_unauthorized_on_every_route_for_empty_and_populated_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-UNKNOWN", supplier.Id);
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, 987654321L, path);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db, invoice.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Deleted_identity_is_unauthorized_on_every_route_for_empty_and_populated_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-DELETED", supplier.Id);
        var userId = PurchaseInvoiceTestIdentities.SeedAuthorizedUser(db, privileged: true);
        PurchaseInvoiceTestIdentities.SoftDeleteUser(db, userId);
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, userId, path);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db, invoice.Id);
    }

    // ==================== 2. 禁用 / 撤销菜单：任何路由、任何请求形状一律权限不足 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Disabled_identity_is_forbidden_on_every_route_for_empty_and_populated_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-DISABLED", supplier.Id);
        var userId = PurchaseInvoiceTestIdentities.SeedAuthorizedUser(db, status: UserStatus.Disabled);
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, userId, path);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Forbidden);
        AssertZeroMutation(db, invoice.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Revoked_menu_is_forbidden_on_every_route_for_empty_and_populated_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-REVOKED", supplier.Id);
        var customer = SeedCustomer(db, "C001");
        var (userId, roleId, _) = PurchaseInvoiceTestIdentities.SeedRestrictedOperator(db, customer.Id);
        var order = SeedOrder(db, "PO-REVOKED", supplier.Id, customer.Id);
        SeedAllocation(db, invoice.Id, order, 40m);

        // 授权仍在：读取成功（证明判定与请求路径无关，而不是「一律拒绝」）
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, userId, path);
        AssertOk<PurchaseInvoiceDto>(await ctl.GetById(invoice.Id));

        PurchaseInvoiceTestIdentities.RevokeMenu(db, roleId);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Forbidden);

        // 撤销后的被拒绝请求零改动：发票仍为草稿、既有 40m 关联行原样保留（不新增、不改写）
        Assert.Equal(PurchaseInvoiceRules.StatusDraft,
            db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == invoice.Id).Status);
        Assert.Equal(1, db.PurchaseInvoiceAllocations.Count());
        Assert.Equal(40m, db.PurchaseInvoiceAllocations.AsNoTracking().Single().AllocatedAmount);
    }

    // ==================== 3. 空路径与已赋值路径：同一身份、同一数据必须得到完全一致的判定 ====================

    [Fact]
    public async Task Empty_and_populated_request_paths_enforce_identical_authority()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var invoice = SeedInvoice(db, "INV-PARITY", supplier.Id);
        var privileged = PurchaseInvoiceTestIdentities.SeedAuthorizedUser(db, privileged: true);
        var disabled = PurchaseInvoiceTestIdentities.SeedAuthorizedUser(db, status: UserStatus.Disabled);

        var emptyPath = PurchaseInvoiceTestIdentities.ForUser(db, privileged, requestPath: null);
        var populatedPath = PurchaseInvoiceTestIdentities.ForUser(db, privileged, RoutePath);
        var emptyPathDisabled = PurchaseInvoiceTestIdentities.ForUser(db, disabled, requestPath: null);
        var populatedPathDisabled = PurchaseInvoiceTestIdentities.ForUser(db, disabled, RoutePath);
        var emptyPathAnonymous = PurchaseInvoiceTestIdentities.ForUser(db, null, requestPath: null);
        var populatedPathAnonymous = PurchaseInvoiceTestIdentities.ForUser(db, null, RoutePath);

        // 授权账号：两种请求形状都能读到同一张发票、同一分页数
        Assert.Equal(
            AssertOk<PurchaseInvoiceDto>(await emptyPath.GetById(invoice.Id)).Id,
            AssertOk<PurchaseInvoiceDto>(await populatedPath.GetById(invoice.Id)).Id);
        Assert.Equal(
            AssertOk<PagedResult<PurchaseInvoiceDto>>(await emptyPath.GetPaged(new PurchaseInvoiceQuery())).Total,
            AssertOk<PagedResult<PurchaseInvoiceDto>>(await populatedPath.GetPaged(new PurchaseInvoiceQuery())).Total);

        // 禁用账号 / 匿名：两种请求形状都必须落到同一个受控错误码
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => emptyPathDisabled.GetById(invoice.Id)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => populatedPathDisabled.GetById(invoice.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(() => emptyPathAnonymous.GetById(invoice.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(() => populatedPathAnonymous.GetById(invoice.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(() => emptyPathAnonymous.GetPaged(new PurchaseInvoiceQuery())));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(() => populatedPathAnonymous.GetPaged(new PurchaseInvoiceQuery())));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => emptyPathDisabled.Create(InvoiceDto(supplier.Id, "INV-PARITY-C"))));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => populatedPathDisabled.Create(InvoiceDto(supplier.Id, "INV-PARITY-D"))));

        AssertZeroMutation(db, invoice.Id);
    }

    // ==================== 4. 保留既有业务生命周期：既有授权账号在空路径请求上照常完成全流程 ====================

    [Fact]
    public async Task Privileged_identity_completes_the_lifecycle_on_an_empty_path_request()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var order = SeedOrder(db, "PO-LIFECYCLE", supplier.Id);
        var userId = PurchaseInvoiceTestIdentities.SeedAuthorizedUser(db, privileged: true);
        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, userId, requestPath: null);

        var created = AssertOk<PurchaseInvoiceDto>(await ctl.Create(InvoiceDto(supplier.Id, "INV-LIFE", 200m)));
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, created.Status);

        var allocated = AssertOk<PurchaseInvoiceDto>(await ctl.SaveAllocations(created.Id, Lines((order.Id, 120m))));
        Assert.Single(allocated.Allocations);

        var recorded = AssertOk<PurchaseInvoiceDto>(await ctl.Record(created.Id));
        Assert.NotNull(recorded.RecordedAt);

        var voided = AssertOk<PurchaseInvoiceDto>(await ctl.Void(created.Id,
            new PurchaseInvoiceVoidRequest { Reason = "录错单价" }));
        Assert.Equal(PurchaseInvoiceRules.StatusVoided, voided.Status);
        Assert.Equal("录错单价", voided.VoidReason);

        // 既有商业口径不变：作废后证据保留、关联行保留、含税总额不变（授权改动不改变金额语义）
        var stored = db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == created.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusVoided, stored.Status);
        Assert.Equal(200m, stored.GrossAmount);
        Assert.Equal(1, db.PurchaseInvoiceAllocations.Count());
        Assert.Empty(db.SupplierPaymentInvoiceAllocations);
    }

    // ==================== 5. 既有权威来源范围：受限业务员 fail closed 且零写入 ====================

    [Fact]
    public async Task Restricted_operator_cannot_read_or_mutate_unlinked_or_foreign_invoices()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001");
        var ownCustomer = SeedCustomer(db, "C-OWN");
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN");
        var (userId, _, _) = PurchaseInvoiceTestIdentities.SeedRestrictedOperator(db, ownCustomer.Id);

        var ownOrder = SeedOrder(db, "PO-OWN", supplier.Id, ownCustomer.Id);
        var foreignOrder = SeedOrder(db, "PO-FOREIGN", supplier.Id, foreignCustomer.Id);
        var ownInvoice = SeedInvoice(db, "INV-OWN", supplier.Id);
        SeedAllocation(db, ownInvoice.Id, ownOrder, 40m);
        var foreignInvoice = SeedInvoice(db, "INV-FOREIGN", supplier.Id, status: PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, foreignInvoice.Id, foreignOrder, 40m);
        var unlinkedInvoice = SeedInvoice(db, "INV-UNLINKED", supplier.Id);

        var ctl = PurchaseInvoiceTestIdentities.ForUser(db, userId, requestPath: null);

        // 本人客户来源：可读；范围外来源 / 无任何来源：fail closed（非披露，一律权限不足）
        AssertOk<PurchaseInvoiceDto>(await ctl.GetById(ownInvoice.Id));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => ctl.GetById(foreignInvoice.Id)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => ctl.GetById(unlinkedInvoice.Id)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => ctl.Record(foreignInvoice.Id)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(() => ctl.Void(foreignInvoice.Id,
            new PurchaseInvoiceVoidRequest { Reason = "重开" })));

        // 被拒绝的写入零改动：范围外发票仍为已登记、无作废原因，关联行原样保留
        var foreignStored = db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == foreignInvoice.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, foreignStored.Status);
        Assert.True(string.IsNullOrEmpty(foreignStored.VoidReason));
        Assert.Equal(1, db.PurchaseInvoiceAllocations.Count(a => a.PurchaseInvoiceId == foreignInvoice.Id));

        // 台账范围下推：只返回本人客户来源的发票（同一严格范围，绝不先查全量再内存过滤）
        var page = AssertOk<PagedResult<PurchaseInvoiceDto>>(await ctl.GetPaged(new PurchaseInvoiceQuery()));
        var ids = page.Items.Select(i => i.Id).ToList();
        Assert.Contains(ownInvoice.Id, ids);
        Assert.DoesNotContain(foreignInvoice.Id, ids);
        Assert.DoesNotContain(unlinkedInvoice.Id, ids);
    }

    // ==================== 6. 与请求形状无关 / 无兜底：源码契约 ====================

    [Fact]
    public void Authorization_gate_is_request_path_independent_and_has_no_fallback()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseInvoiceController.cs");
        Assert.Contains("ControllerContext?.HttpContext is not null", controller);
        Assert.DoesNotContain("Request.Path", controller);
        Assert.DoesNotContain("AllowAnonymous", controller);
        Assert.DoesNotContain("[Authorize(Roles", controller);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller);
        Assert.DoesNotContain("IsInMemory", controller);
        Assert.Contains("PurchaseInvoiceAuthorizationRules.EnsureMenuAuthorizedAsync", controller);
        Assert.Contains("PurchaseInvoiceAuthorizationRules.EnsureInvoiceAuthorizedAsync", controller);
        Assert.Contains("PurchaseInvoiceAuthorizationRules.EnsureOrderIdsAuthorizedAsync", controller);
        Assert.Contains("PurchaseInvoiceAuthorizationRules.BuildScopePredicateAsync", controller);

        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseInvoiceAuthorizationRules.cs");
        Assert.Contains("PathIndependenceText", rules);
        Assert.Contains("ErrorCodes.Unauthorized", rules);
        Assert.Contains("ErrorCodes.Forbidden", rules);
        Assert.Contains("fail closed", rules);
        Assert.Contains("未认证", PurchaseInvoiceAuthorizationRules.PathIndependenceText);
        Assert.Contains("权限不足", PurchaseInvoiceAuthorizationRules.PathIndependenceText);
        Assert.Contains("请求形状", PurchaseInvoiceAuthorizationRules.PathIndependenceText);
        Assert.Contains("绝不新增", PurchaseInvoiceAuthorizationRules.RuleText);
        Assert.Contains("请求形状无关", PurchaseInvoiceAuthorizationRules.RuleText);
    }
}


