using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-465 规范销售订单（<c>api/sales-orders</c>）读侧入口实时授权测试脚手架：只在隔离的内存测试数据里播种
/// <strong>既有启用账号 / 既有「销售订单」（<c>sales-order</c>）菜单授权 / ERP-097 权威客户范围</strong>，
/// 并以真实 <see cref="DefaultHttpContext"/> 身份驱动真实 <see cref="SalesOrderController"/>。
/// <para>默认<strong>不设置</strong> <c>Request.Path</c>（空路径请求），用于证明授权判定与请求形状完全无关；
/// 空路径与已赋值路径必须给出完全一致的判定。绝不新增任何生产菜单 / 权限 / 用户授权，也不提供任何测试专用绕过开关。</para>
/// </summary>
internal static class SalesOrderEntryTestIdentities
{
    /// <summary>既有「销售订单」功能菜单（与 <c>SeedData.Menus</c> 同源，绝不新增菜单）。</summary>
    internal static SysMenu FindOrCreateRequiredMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == SalesOrderExecutionAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            MenuCode = SalesOrderExecutionAuthorizationRules.RequiredMenuCode,
            MenuName = SalesOrderExecutionAuthorizationRules.RequiredMenuText,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种受限业务员账号（登录账号 == 员工编码，ERP-097 权威映射），返回（用户 Id，角色 Id，员工 Id）。</summary>
    internal static (long UserId, long RoleId, long EmployeeId) SeedOperator(
        ErpDbContext db, bool menu = true, UserStatus status = UserStatus.Enabled, bool privileged = false)
    {
        var code = $"so-entry-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleCode = $"SoEntryOp-{Guid.NewGuid():N}",
            RoleName = "销售订单入口受限角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销售订单入口测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (menu)
        {
            var menuEntity = FindOrCreateRequiredMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuEntity.Id });
            db.SaveChanges();
        }

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
    internal static SalesOrderController ForUser(ErpDbContext db, long? userId, string? requestPath = null)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new SalesOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }
}

/// <summary>
/// ERP-465 规范销售订单读侧入口实时授权单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）。
/// <list type="number">
/// <item><b>与请求形状无关</b>：同一个真实身份在<strong>空 <c>Request.Path</c></strong> 与<strong>已赋值 <c>Request.Path</c></strong>
/// 上必须得到完全一致的判定；缺失 / 零 / 未知 / 已删除身份一律未认证，禁用 / 缺少或撤销既有菜单一律权限不足，
/// 且全部发生在任何订单 / 明细 / 执行证据 / 时间线字节读取之前。</item>
/// <item><b>既有菜单口径不变</b>：列表 / 详情只要求实时身份 + 账号状态 + ERP-097 权威客户范围（<strong>不新增</strong>菜单要求）；
/// 执行证据入口在身份 / 状态 / 范围之外仍要求既有「销售订单」（<c>sales-order</c>）菜单。</item>
/// <item><b>保留既有业务语义</b>：既有菜单授权身份照常完成列表 / 详情 / 时间线 / 财务核对 / 进度 / 退货影响 / 收款与发票证据
/// 单张与批量读取；范围外 / 已删除 / 不存在订单返回同一非披露错误；被拒绝的调用零写入、零授权扩张。</item>
/// <item><b>无匿名 / 管理员兜底</b>：控制器不读 <c>Request.Path</c>、不读环境变量、不使用测试专用开关，也不存在任何匿名 / 管理员回退。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不写生产数据；真实隔离 SQL 授权用例见
/// <c>ERP.IntegrationTests/SalesOrderEntryAuthorizationSqlServerTests.cs</c>。</para>
/// </summary>
public class SalesOrderEntryAuthorizationTests
{
    private const string RoutePath = "/api/sales-orders";
    private const long UnknownUserId = 9_465_999_999L;

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.1m,
            TotalAmount = 1000m,
            Status = DocumentStatus.Pending,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static async Task AssertDeniedAsync(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
    }

    private static async Task AssertOkAsync(Func<Task<IActionResult>> action)
        => Assert.IsType<OkObjectResult>(await action());

    private static async Task AssertNonDisclosingNotFoundAsync(Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.NotFoundText, ex.Message);
    }

    /// <summary>读侧每一条公开入口：先授权、后读取；拒绝时必须返回同一受控错误码。</summary>
    private static async Task AssertAllReadEntriesDeniedAsync(SalesOrderController ctl, long orderId, int code)
    {
        var ids = orderId.ToString();
        await AssertDeniedAsync(code, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertDeniedAsync(code, () => ctl.GetById(orderId));
        await AssertDeniedAsync(code, () => ctl.Timeline(orderId));
        await AssertDeniedAsync(code, () => ctl.FinanceReconciliation(orderId));
        await AssertDeniedAsync(code, () => ctl.Progress(orderId));
        await AssertDeniedAsync(code, () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()));
        await AssertDeniedAsync(code, () => ctl.ReturnImpact(orderId));
        await AssertDeniedAsync(code, () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()));
        await AssertDeniedAsync(code, () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()));
        await AssertDeniedAsync(code, () => ctl.ReceiptEvidence(orderId));
        await AssertDeniedAsync(code, () => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = ids }));
        await AssertDeniedAsync(code, () => ctl.InvoiceEvidence(orderId));
        await AssertDeniedAsync(code, () => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = ids }));
    }

    /// <summary>被拒绝的调用零写入 / 零授权扩张：无待提交的变更。</summary>
    private static void AssertZeroMutation(ErpDbContext db)
        => Assert.False(db.ChangeTracker.HasChanges());


    // ==================== 1. 缺失 / 零 / 未知 / 已删除身份：任何入口、任何请求形状一律未认证 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_identity_is_unauthorized_on_every_read_entry(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "SO465-MISS", null);
        var order = SeedOrder(db, "SO-465-MISS", customer.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, null, requestPath);
        await AssertAllReadEntriesDeniedAsync(ctl, order.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Zero_identity_is_unauthorized_on_every_read_entry(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "SO465-ZERO", null);
        var order = SeedOrder(db, "SO-465-ZERO", customer.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, 0L, requestPath);
        await AssertAllReadEntriesDeniedAsync(ctl, order.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Unknown_identity_is_unauthorized_on_every_read_entry(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "SO465-UNKNOWN", null);
        var order = SeedOrder(db, "SO-465-UNKNOWN", customer.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, UnknownUserId, requestPath);
        await AssertAllReadEntriesDeniedAsync(ctl, order.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Deleted_identity_is_unauthorized_on_every_read_entry(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SalesOrderEntryTestIdentities.SeedOperator(db);
        var own = SeedCustomer(db, "SO465-DEL", employeeId);
        var order = SeedOrder(db, "SO-465-DEL", own.Id);
        SalesOrderEntryTestIdentities.SoftDeleteUser(db, userId);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId, requestPath);
        await AssertAllReadEntriesDeniedAsync(ctl, order.Id, ErrorCodes.Unauthorized);
        AssertZeroMutation(db);
    }


    // ==================== 2. 禁用 / 缺少或撤销既有菜单：权限不足 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Disabled_identity_is_forbidden_on_every_read_entry(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SalesOrderEntryTestIdentities.SeedOperator(
            db, status: UserStatus.Disabled);
        var own = SeedCustomer(db, "SO465-DIS", employeeId);
        var order = SeedOrder(db, "SO-465-DIS", own.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId, requestPath);
        await AssertAllReadEntriesDeniedAsync(ctl, order.Id, ErrorCodes.Forbidden);
        AssertZeroMutation(db);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_menu_denies_evidence_but_not_list_or_detail(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SalesOrderEntryTestIdentities.SeedOperator(db, menu: false);
        var own = SeedCustomer(db, "SO465-NOMENU", employeeId);
        var order = SeedOrder(db, "SO-465-NOMENU", own.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId, requestPath);

        // 列表 / 详情：只要求实时身份 + 账号状态 + ERP-097 范围，**不新增**菜单要求。
        await AssertOkAsync(() => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertOkAsync(() => ctl.GetById(order.Id));

        // 执行证据入口：缺少既有「销售订单」菜单 → 权限不足（先于任何证据读取）。
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.Timeline(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.FinanceReconciliation(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.Progress(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.ReturnImpact(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.ReceiptEvidence(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = order.Id.ToString() }));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.InvoiceEvidence(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = order.Id.ToString() }));
        AssertZeroMutation(db);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Revoked_menu_denies_evidence_but_not_list_or_detail(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, employeeId) = SalesOrderEntryTestIdentities.SeedOperator(db);
        var own = SeedCustomer(db, "SO465-REVOKED", employeeId);
        var order = SeedOrder(db, "SO-465-REVOKED", own.Id);
        SalesOrderEntryTestIdentities.RevokeMenu(db, roleId);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId, requestPath);

        await AssertOkAsync(() => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertOkAsync(() => ctl.GetById(order.Id));

        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.Progress(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.ReceiptEvidence(order.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.InvoiceEvidence(order.Id));
        AssertZeroMutation(db);
    }


    // ==================== 3. 允许身份：既有生命周期与 ERP-097 权威范围 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Authorized_operator_reads_own_order_on_empty_and_populated_paths(string? requestPath)
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SalesOrderEntryTestIdentities.SeedOperator(db);
        var own = SeedCustomer(db, "SO465-OWN", employeeId);
        var foreign = SeedCustomer(db, "SO465-FOR", null);
        var ownOrder = SeedOrder(db, "SO-465-OWN", own.Id);
        var foreignOrder = SeedOrder(db, "SO-465-FOR", foreign.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId, requestPath);

        await AssertOkAsync(() => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertOkAsync(() => ctl.GetById(ownOrder.Id));
        await AssertOkAsync(() => ctl.Timeline(ownOrder.Id));
        await AssertOkAsync(() => ctl.FinanceReconciliation(ownOrder.Id));
        await AssertOkAsync(() => ctl.Progress(ownOrder.Id));
        await AssertOkAsync(() => ctl.ReturnImpact(ownOrder.Id));
        await AssertOkAsync(() => ctl.ReceiptEvidence(ownOrder.Id));
        await AssertOkAsync(() => ctl.InvoiceEvidence(ownOrder.Id));
        await AssertOkAsync(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = ownOrder.Id.ToString() }));
        await AssertOkAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = ownOrder.Id.ToString() }));

        // 非披露：范围外订单与批量混入不可访问订单返回同一受控错误。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = $"{ownOrder.Id},{foreignOrder.Id}" }));
    }

    [Fact]
    public async Task Empty_and_populated_path_yield_identical_authority()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SalesOrderEntryTestIdentities.SeedOperator(db);
        var own = SeedCustomer(db, "SO465-EQ", employeeId);
        var foreign = SeedCustomer(db, "SO465-EQ-FOR", null);
        var ownOrder = SeedOrder(db, "SO-465-EQ-OWN", own.Id);
        var foreignOrder = SeedOrder(db, "SO-465-EQ-FOR", foreign.Id);

        foreach (var ctl in new[]
                 {
                     SalesOrderEntryTestIdentities.ForUser(db, userId, null),
                     SalesOrderEntryTestIdentities.ForUser(db, userId, RoutePath)
                 })
        {
            await AssertOkAsync(() => ctl.Progress(ownOrder.Id));
            await AssertOkAsync(() => ctl.GetById(ownOrder.Id));
            await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(foreignOrder.Id));
            await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(foreignOrder.Id));
        }
    }

    [Fact]
    public async Task Privileged_identity_retains_unrestricted_access()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SalesOrderEntryTestIdentities.SeedOperator(db, menu: false, privileged: true);
        var foreign = SeedCustomer(db, "SO465-PRIV", null);
        var order = SeedOrder(db, "SO-465-PRIV", foreign.Id);

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId);
        await AssertOkAsync(() => ctl.Progress(order.Id));
        await AssertOkAsync(() => ctl.ReceiptEvidence(order.Id));
        await AssertOkAsync(() => ctl.GetById(order.Id));
    }


    // ==================== 4. 拒绝零写入 / 零授权扩张 ====================

    [Fact]
    public async Task Denied_calls_return_no_data_and_persist_zero_mutations()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, roleId) = SalesOrderEntryTestIdentities.SeedOperator(db);
        var own = SeedCustomer(db, "SO465-ZW", employeeId);
        var foreign = SeedCustomer(db, "SO465-ZW-FOR", null);
        var ownOrder = SeedOrder(db, "SO-465-ZW-OWN", own.Id);
        var foreignOrder = SeedOrder(db, "SO-465-ZW-FOR", foreign.Id);

        var beforeOrders = db.SalesOrders.Count();
        var beforeGrants = db.SysRoleMenus.Count();

        var ctl = SalesOrderEntryTestIdentities.ForUser(db, userId);

        // 范围外单张与批量混入：同一非披露错误，零写入。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{ownOrder.Id},{foreignOrder.Id}" }));

        // 撤销既有菜单后：证据入口权限不足，且不扩张任何用户授权。
        SalesOrderEntryTestIdentities.RevokeMenu(db, roleId);
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.InvoiceEvidence(ownOrder.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.Progress(ownOrder.Id));

        Assert.Equal(beforeOrders, db.SalesOrders.Count());
        Assert.Equal(beforeGrants, db.SysRoleMenus.Count());
        AssertZeroMutation(db);

        // 列表 / 详情仍可用（未新增任何菜单要求），证明拒绝路径没有改写任何业务 / 授权记录。
        await AssertOkAsync(() => ctl.GetById(ownOrder.Id));
    }

    // ==================== 5. 源码契约：与请求形状无关且无兜底 ====================

    [Fact]
    public void Authorization_gate_is_request_shape_independent_and_has_no_fallback()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var controller = File.ReadAllText(Path.Combine(directory!.FullName,
            "src", "ERP.Api", "Controllers", "SalesOrderController.cs"));
        Assert.Contains("ControllerContext?.HttpContext is not null", controller);
        Assert.DoesNotContain("Request.Path", controller);
        Assert.DoesNotContain("AllowAnonymous", controller);
        Assert.DoesNotContain("[Authorize(Roles", controller);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller);
        Assert.DoesNotContain("IsInMemory", controller);
        Assert.Contains("SalesOrderExecutionAuthorizationRules.EnsureLiveIdentityAsync", controller);
        Assert.Contains("SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync", controller);
        Assert.Contains("SalesOrderMutationAuthorizationRules.EnsureWriteAuthorizedAsync", controller);
        Assert.Contains("SalesOrderDocumentOutputAuthorizationRules.EnsureDocumentOutputAuthorizedAsync", controller);

        Assert.Contains("请求形状", SalesOrderExecutionAuthorizationRules.PathIndependenceText);
        Assert.Contains("未认证", SalesOrderExecutionAuthorizationRules.PathIndependenceText);
        Assert.Contains("权限不足", SalesOrderExecutionAuthorizationRules.PathIndependenceText);
    }
}

