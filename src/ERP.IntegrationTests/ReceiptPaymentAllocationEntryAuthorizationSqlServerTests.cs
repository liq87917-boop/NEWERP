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
/// ERP-468 收款分摊证据 / 付款引用证据入口实时授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「收款单」（receipt）/「付款单」（payment）菜单授权与既有
/// <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实 <see cref="CustomerReceiptAllocationController"/> /
/// <see cref="SupplierPaymentAllocationController"/>。</item>
/// <item><b>与请求形状 / 请求上下文无关</b>：<strong>空 <c>Request.Path</c></strong>、<strong>已赋值 <c>Request.Path</c></strong>
/// 与<strong>完全未绑定 <c>HttpContext</c></strong> 的纯进程内直调必须给出完全一致的判定；缺失 / 零 / 未知 / 已删除身份一律未认证，
/// 禁用 / 撤销菜单一律权限不足，且都在任何分摊金额、快照或候选读取与写入之前。</item>
/// <item><b>保留既有业务生命周期与零写入拒绝</b>：既有授权账号照常完成 台账 / 候选 / 汇总 / 详情 / 登记 / 作废 全生命周期；
/// 被拒绝的请求不新增 / 不改写任何分摊行或权威收款单 / 付款单，也不新增任何用户授权。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ReceiptPaymentAllocationEntryAuthorizationSqlServerTests
    : IClassFixture<ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture>
{
    private readonly ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture _fixture;

    public ReceiptPaymentAllocationEntryAuthorizationSqlServerTests(
        ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const string ReceiptPath = "/api/customer-receipt-allocations";
    private const string PaymentPath = "/api/supplier-payment-allocations";

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器脚手架 ====================

    private static DefaultHttpContext NewHttp(long? userId, string? requestPath)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "IntTest")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return http;
    }

    private static CustomerReceiptAllocationController ReceiptController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    private static SupplierPaymentAllocationController PaymentController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    /// <summary>用一条独立连接执行控制器动作，返回（是否成功、受控错误码、错误文案）。</summary>
    private async Task<(bool Success, int Code, string Error)> TryReceiptAsync(
        long? userId, string? requestPath, Func<CustomerReceiptAllocationController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(ReceiptController(db, userId, requestPath));
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.ToString());
        }
    }

    private async Task<(bool Success, int Code, string Error)> TryPaymentAsync(
        long? userId, string? requestPath, Func<SupplierPaymentAllocationController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(PaymentController(db, userId, requestPath));
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.ToString());
        }
    }

    /// <summary>完全未绑定 HttpContext 的纯进程内直调（<c>ControllerContext.HttpContext</c> 为 null）。</summary>
    private async Task<(bool Success, int Code, string Error)> TryReceiptWithoutContextAsync(
        Func<CustomerReceiptAllocationController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(new CustomerReceiptAllocationController(db));
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.ToString());
        }
    }

    private async Task<(bool Success, int Code, string Error)> TryPaymentWithoutContextAsync(
        Func<SupplierPaymentAllocationController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(new SupplierPaymentAllocationController(db));
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.ToString());
        }
    }

    // ==================== 路由判决助手（收款 / 付款各 8 条入口） ====================

    private static CustomerReceiptAllocationSaveDto ReceiptDto(long receiptId, long salesOrderId, decimal amount)
        => new() { ReceiptId = receiptId, SalesOrderId = salesOrderId, AllocatedAmount = amount, Remark = "INT468" };

    private static SupplierPaymentAllocationSaveDto PaymentDto(long paymentId, long purchaseOrderId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseOrderId = purchaseOrderId, AllocatedAmount = amount, Remark = "INT468" };

    private async Task AssertReceiptRoutesDeniedAsync(
        long? userId, string? requestPath, long receiptId, long salesOrderId, int code)
    {
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.GetPaged(new CustomerReceiptAllocationQuery()))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.ReceiptCandidates(null, null, 10))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.ReceiptSummary(receiptId))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.AllocationsForReceipt(receiptId))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.OrderCandidates(receiptId, null, 10))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.GetById(1L))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.Create(ReceiptDto(receiptId, salesOrderId, 10m)))).Code);
        Assert.Equal(code, (await TryReceiptAsync(userId, requestPath,
            c => c.Void(1L, new CustomerReceiptAllocationVoidRequest { Reason = "作废" }))).Code);
    }

    private async Task AssertPaymentRoutesDeniedAsync(
        long? userId, string? requestPath, long paymentId, long purchaseOrderId, int code)
    {
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.GetPaged(new SupplierPaymentAllocationQuery()))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.PaymentCandidates(null, null, 10))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.PaymentSummary(paymentId))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.AllocationsForPayment(paymentId))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.OrderCandidates(paymentId, null, 10))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.GetById(1L))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.Create(PaymentDto(paymentId, purchaseOrderId, 10m)))).Code);
        Assert.Equal(code, (await TryPaymentAsync(userId, requestPath,
            c => c.Void(1L, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }))).Code);
    }

    private async Task AssertReceiptRoutesWithoutContextDeniedAsync(
        long receiptId, long salesOrderId, int code)
    {
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.GetPaged(new CustomerReceiptAllocationQuery()))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.ReceiptCandidates(null, null, 10))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.ReceiptSummary(receiptId))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.AllocationsForReceipt(receiptId))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.OrderCandidates(receiptId, null, 10))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.GetById(1L))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.Create(ReceiptDto(receiptId, salesOrderId, 10m)))).Code);
        Assert.Equal(code, (await TryReceiptWithoutContextAsync(
            c => c.Void(1L, new CustomerReceiptAllocationVoidRequest { Reason = "作废" }))).Code);
    }

    private async Task AssertPaymentRoutesWithoutContextDeniedAsync(
        long paymentId, long purchaseOrderId, int code)
    {
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.GetPaged(new SupplierPaymentAllocationQuery()))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.PaymentCandidates(null, null, 10))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.PaymentSummary(paymentId))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.AllocationsForPayment(paymentId))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.OrderCandidates(paymentId, null, 10))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.GetById(1L))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.Create(PaymentDto(paymentId, purchaseOrderId, 10m)))).Code);
        Assert.Equal(code, (await TryPaymentWithoutContextAsync(
            c => c.Void(1L, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }))).Code);
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string tag)
    {
        // BaseCustomers.CustomerCode 上限 50：标签 + 32 位 GUID 长度恒 ≤ 45，绝不越界。
        var code = $"IR468C{tag}{Guid.NewGuid():N}";
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db)
    {
        var code = $"IR468S{Guid.NewGuid():N}";
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = code, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<FinanceReceipt> SeedReceiptAsync(ErpDbContext db, long customerId)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = $"IR468-{Guid.NewGuid():N}"[..24],
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.USD,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Pending
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = $"IS468-{Guid.NewGuid():N}"[..24],
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<FinancePayment> SeedPaymentAsync(ErpDbContext db, long supplierId)
    {
        var payment = new FinancePayment
        {
            PaymentNo = $"IP468-{Guid.NewGuid():N}"[..24],
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            Amount = 1000m,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Pending
        };
        db.FinancePayments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<PurchaseOrder> SeedPurchaseOrderAsync(ErpDbContext db, long supplierId)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"IO468-{Guid.NewGuid():N}"[..24],
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>
    /// 播种真实操作账号并授予既有「收款单」（receipt）与「付款单」（payment）菜单：<paramref name="privileged"/> 为 true 时
    /// 复用系统内置角色口径（既有不受客户范围限制）；否则为映射到 <paramref name="ownedCustomerId"/> 的受限业务员。
    /// 绝不新增任何菜单 / 角色 / 用户授权，只复用既有菜单编码。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long? ownedCustomerId = null, bool receiptMenu = true, bool paymentMenu = true,
        bool privileged = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"rpa468-op-{Guid.NewGuid():N}";
        long? employeeId = null;
        if (ownedCustomerId is not null)
        {
            var employee = new BaseEmployee
            {
                EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
            };
            db.BaseEmployees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
        }

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt", Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "分摊入口集成角色", RoleCode = $"Rpa468Op-{Guid.NewGuid():N}", IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (receiptMenu)
        {
            var receiptMenuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == CustomerReceiptLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = receiptMenuId });
        }
        if (paymentMenu)
        {
            var paymentMenuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = paymentMenuId });
        }
        await db.SaveChangesAsync();

        if (ownedCustomerId is not null && employeeId is not null)
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == ownedCustomerId.Value);
            customer.EmpId = employeeId;
            await db.SaveChangesAsync();
        }

        return (user.Id, role.Id);
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode).Select(m => m.Id).ToListAsync();
        foreach (var grant in await db.SysRoleMenus
                     .Where(g => !g.IsDeleted && g.RoleId == roleId && menuIds.Contains(g.MenuId)).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task SoftDeleteUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    // ==================== 1. 与请求形状无关：空路径与已赋值路径判定一致 ====================

    [Fact]
    public async Task Empty_and_populated_request_paths_enforce_identical_authority()
    {
        Guard();
        long receiptId, salesOrderA, salesOrderB, paymentId, purchaseOrderA, purchaseOrderB, privilegedId, disabledId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "PARITY");
            var supplierId = await SeedSupplierAsync(seed);
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            salesOrderA = (await SeedSalesOrderAsync(seed, customerId)).Id;
            salesOrderB = (await SeedSalesOrderAsync(seed, customerId)).Id;
            paymentId = (await SeedPaymentAsync(seed, supplierId)).Id;
            purchaseOrderA = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            purchaseOrderB = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            privilegedId = (await SeedOperatorAsync(seed)).UserId;
            disabledId = (await SeedOperatorAsync(seed, status: UserStatus.Disabled)).UserId;
        }

        // 匿名：两种请求形状都未认证；禁用身份：两种请求形状都权限不足。
        foreach (var path in new[] { null, ReceiptPath })
        {
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryReceiptAsync(null, path, c => c.ReceiptSummary(receiptId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,
                (await TryReceiptAsync(disabledId, path, c => c.ReceiptSummary(receiptId))).Code);
        }
        foreach (var path in new[] { null, PaymentPath })
        {
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryPaymentAsync(null, path, c => c.PaymentSummary(paymentId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,
                (await TryPaymentAsync(disabledId, path, c => c.PaymentSummary(paymentId))).Code);
        }

        // 授权身份：两种请求形状都能读取并登记（每形状使用不同订单，避免重复有效行）。
        foreach (var path in new[] { null, ReceiptPath })
        {
            var summary = await TryReceiptAsync(privilegedId, path, c => c.ReceiptSummary(receiptId));
            Assert.True(summary.Success, summary.Error);
            var orderId = path is null ? salesOrderA : salesOrderB;
            var created = await TryReceiptAsync(privilegedId, path,
                c => c.Create(ReceiptDto(receiptId, orderId, 100m)));
            Assert.True(created.Success, created.Error);
        }
        foreach (var path in new[] { null, PaymentPath })
        {
            var summary = await TryPaymentAsync(privilegedId, path, c => c.PaymentSummary(paymentId));
            Assert.True(summary.Success, summary.Error);
            var orderId = path is null ? purchaseOrderA : purchaseOrderB;
            var created = await TryPaymentAsync(privilegedId, path,
                c => c.Create(PaymentDto(paymentId, orderId, 100m)));
            Assert.True(created.Success, created.Error);
        }
    }

    // ==================== 2. 零 / 未知 / 已删除身份：任何请求形状一律未认证 ====================

    [Fact]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized_on_every_route()
    {
        Guard();
        long receiptId, salesOrderId, paymentId, purchaseOrderId, deletedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "NOID");
            var supplierId = await SeedSupplierAsync(seed);
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            salesOrderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            paymentId = (await SeedPaymentAsync(seed, supplierId)).Id;
            purchaseOrderId = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            deletedId = (await SeedOperatorAsync(seed)).UserId;
            await SoftDeleteUserAsync(seed, deletedId);
        }

        foreach (var userId in new long?[] { null, 0L, 9_468_000_001L, deletedId })
        {
            await AssertReceiptRoutesDeniedAsync(userId, null, receiptId, salesOrderId, ErrorCodes.Unauthorized);
            await AssertReceiptRoutesDeniedAsync(userId, ReceiptPath, receiptId, salesOrderId, ErrorCodes.Unauthorized);
            await AssertPaymentRoutesDeniedAsync(userId, null, paymentId, purchaseOrderId, ErrorCodes.Unauthorized);
            await AssertPaymentRoutesDeniedAsync(userId, PaymentPath, paymentId, purchaseOrderId, ErrorCodes.Unauthorized);
        }

        await using var check = _fixture.CreateDbContext();
        Assert.Equal(0, await check.CustomerReceiptAllocations.CountAsync(a => a.ReceiptId == receiptId));
        Assert.Equal(0, await check.SupplierPaymentAllocations.CountAsync(a => a.PaymentId == paymentId));
    }

    // ==================== 3. 禁用身份 / 撤销既有菜单：任何请求形状一律权限不足 ====================

    [Fact]
    public async Task Disabled_identity_and_revoked_menu_are_forbidden_on_every_route()
    {
        Guard();
        long receiptId, salesOrderId, paymentId, purchaseOrderId, disabledId, menuUserId, menuRoleId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "FORB");
            var supplierId = await SeedSupplierAsync(seed);
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            salesOrderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            paymentId = (await SeedPaymentAsync(seed, supplierId)).Id;
            purchaseOrderId = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            disabledId = (await SeedOperatorAsync(seed, status: UserStatus.Disabled)).UserId;
            (menuUserId, menuRoleId) = await SeedOperatorAsync(seed);
        }

        // 禁用身份：两种请求形状都权限不足。
        foreach (var path in new[] { null, ReceiptPath })
            await AssertReceiptRoutesDeniedAsync(disabledId, path, receiptId, salesOrderId, ErrorCodes.Forbidden);
        foreach (var path in new[] { null, PaymentPath })
            await AssertPaymentRoutesDeniedAsync(disabledId, path, paymentId, purchaseOrderId, ErrorCodes.Forbidden);

        // 撤销前：既有授权身份在两种请求形状上都可读（证明判定与请求形状无关，而不是「一律拒绝」）。
        foreach (var path in new[] { null, ReceiptPath })
            Assert.True((await TryReceiptAsync(menuUserId, path,
                c => c.GetPaged(new CustomerReceiptAllocationQuery()))).Success);

        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, menuRoleId, CustomerReceiptLifecycleRules.RequiredMenuCode);

        // 撤销「收款单」菜单：收款入口立即收敛；付款菜单未被撤销，付款入口保持可用（不误伤其它模块）。
        foreach (var path in new[] { null, ReceiptPath })
            await AssertReceiptRoutesDeniedAsync(menuUserId, path, receiptId, salesOrderId, ErrorCodes.Forbidden);
        foreach (var path in new[] { null, PaymentPath })
            Assert.True((await TryPaymentAsync(menuUserId, path,
                c => c.GetPaged(new SupplierPaymentAllocationQuery()))).Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.CustomerReceiptAllocations.CountAsync(a => a.ReceiptId == receiptId));
    }

    // ==================== 4. 授权身份在两种请求形状上完成既有生命周期 ====================

    [Fact]
    public async Task Permitted_genuine_identity_completes_the_lifecycle_on_both_request_shapes()
    {
        Guard();
        long receiptId, salesOrderNull, salesOrderPath, paymentId, purchaseOrderNull, purchaseOrderPath, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "LIFE");
            var supplierId = await SeedSupplierAsync(seed);
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            salesOrderNull = (await SeedSalesOrderAsync(seed, customerId)).Id;
            salesOrderPath = (await SeedSalesOrderAsync(seed, customerId)).Id;
            paymentId = (await SeedPaymentAsync(seed, supplierId)).Id;
            purchaseOrderNull = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            purchaseOrderPath = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            userId = (await SeedOperatorAsync(seed)).UserId;
        }

        var receiptNull = await TryReceiptAsync(userId, null,
            c => c.Create(ReceiptDto(receiptId, salesOrderNull, 250m)));
        Assert.True(receiptNull.Success, receiptNull.Error);
        var receiptPath = await TryReceiptAsync(userId, ReceiptPath,
            c => c.Create(ReceiptDto(receiptId, salesOrderPath, 250m)));
        Assert.True(receiptPath.Success, receiptPath.Error);

        var paymentNull = await TryPaymentAsync(userId, null,
            c => c.Create(PaymentDto(paymentId, purchaseOrderNull, 250m)));
        Assert.True(paymentNull.Success, paymentNull.Error);
        var paymentPath = await TryPaymentAsync(userId, PaymentPath,
            c => c.Create(PaymentDto(paymentId, purchaseOrderPath, 250m)));
        Assert.True(paymentPath.Success, paymentPath.Error);

        // 显式作废（保留原始金额与原因，绝不物理删除）：空路径请求同样可用。
        List<long> receiptRowIds;
        await using (var read = _fixture.CreateDbContext())
        {
            receiptRowIds = await read.CustomerReceiptAllocations.AsNoTracking()
                .Where(a => a.ReceiptId == receiptId && !a.IsDeleted)
                .Select(a => a.Id).ToListAsync();
        }
        Assert.Equal(2, receiptRowIds.Count);
        foreach (var rowId in receiptRowIds)
        {
            var voided = await TryReceiptAsync(userId, null,
                c => c.Void(rowId, new CustomerReceiptAllocationVoidRequest { Reason = "集成录错" }));
            Assert.True(voided.Success, voided.Error);
        }

        await using var verify = _fixture.CreateDbContext();
        var receiptRows = await verify.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receiptId).ToListAsync();
        Assert.Equal(2, receiptRows.Count);
        Assert.All(receiptRows, r =>
        {
            Assert.Equal(CustomerReceiptAllocationRules.StatusVoided, r.Status);
            Assert.Equal(250m, r.AllocatedAmount);
            Assert.Equal("集成录错", r.VoidReason);
        });
        Assert.Equal(2, await verify.SupplierPaymentAllocations
            .CountAsync(a => a.PaymentId == paymentId && a.Status == SupplierPaymentAllocationRules.StatusActive));
    }

    // ==================== 5. 被拒绝调用零写入且不新增任何授权 ====================

    [Fact]
    public async Task Denied_operations_persist_zero_rows_and_grant_nothing()
    {
        Guard();
        long receiptId, salesOrderId, paymentId, purchaseOrderId, privilegedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "ZERO");
            var supplierId = await SeedSupplierAsync(seed);
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            salesOrderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            paymentId = (await SeedPaymentAsync(seed, supplierId)).Id;
            purchaseOrderId = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
            privilegedId = (await SeedOperatorAsync(seed)).UserId;
        }

        // 先用真实授权身份建立一条有效证据，再以匿名身份尝试全部入口。
        var seeded = await TryReceiptAsync(privilegedId, null,
            c => c.Create(ReceiptDto(receiptId, salesOrderId, 250m)));
        Assert.True(seeded.Success, seeded.Error);

        int receiptRows, paymentRows, grants;
        await using (var before = _fixture.CreateDbContext())
        {
            receiptRows = await before.CustomerReceiptAllocations
                .CountAsync(a => a.ReceiptId == receiptId && !a.IsDeleted);
            paymentRows = await before.SupplierPaymentAllocations
                .CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted);
            grants = await before.SysRoleMenus.CountAsync(g => !g.IsDeleted);
        }

        await AssertReceiptRoutesDeniedAsync(null, null, receiptId, salesOrderId, ErrorCodes.Unauthorized);
        await AssertReceiptRoutesDeniedAsync(null, ReceiptPath, receiptId, salesOrderId, ErrorCodes.Unauthorized);
        await AssertPaymentRoutesDeniedAsync(null, null, paymentId, purchaseOrderId, ErrorCodes.Unauthorized);
        await AssertPaymentRoutesDeniedAsync(null, PaymentPath, paymentId, purchaseOrderId, ErrorCodes.Unauthorized);

        await using var after = _fixture.CreateDbContext();
        Assert.Equal(receiptRows, await after.CustomerReceiptAllocations
            .CountAsync(a => a.ReceiptId == receiptId && !a.IsDeleted));
        Assert.Equal(paymentRows, await after.SupplierPaymentAllocations
            .CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted));
        Assert.Equal(grants, await after.SysRoleMenus.CountAsync(g => !g.IsDeleted));

        var stored = await after.CustomerReceiptAllocations.AsNoTracking()
            .SingleAsync(a => a.ReceiptId == receiptId && !a.IsDeleted);
        Assert.Equal(CustomerReceiptAllocationRules.StatusActive, stored.Status);
        Assert.Null(stored.VoidedAt);
    }

    // ==================== 6. 完全未绑定 HttpContext：同样无条件未认证且零写入 ====================

    [Fact]
    public async Task Absent_Http_Context_fails_closed_on_every_route_and_persists_nothing()
    {
        Guard();
        long receiptId, salesOrderId, paymentId, purchaseOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "NOCTX");
            var supplierId = await SeedSupplierAsync(seed);
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            salesOrderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            paymentId = (await SeedPaymentAsync(seed, supplierId)).Id;
            purchaseOrderId = (await SeedPurchaseOrderAsync(seed, supplierId)).Id;
        }

        // 未绑定任何请求上下文（HttpContext 为 null）的纯进程内直调：身份不可解析 → 一律未认证。
        await AssertReceiptRoutesWithoutContextDeniedAsync(receiptId, salesOrderId, ErrorCodes.Unauthorized);
        await AssertPaymentRoutesWithoutContextDeniedAsync(paymentId, purchaseOrderId, ErrorCodes.Unauthorized);

        await using var check = _fixture.CreateDbContext();
        Assert.Equal(0, await check.CustomerReceiptAllocations.CountAsync(a => a.ReceiptId == receiptId));
        Assert.Equal(0, await check.SupplierPaymentAllocations.CountAsync(a => a.PaymentId == paymentId));
    }
}

/// <summary>
/// ERP-468 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RECEIPTPAYMENTALLOCATIONENTRYAUTHORIZATION";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-468] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
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

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-468] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ReceiptPaymentAllocationEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ReceiptPaymentAllocationEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
