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
/// ERP-468 收款分摊证据（<c>api/customer-receipt-allocations</c>）与付款引用证据（<c>api/supplier-payment-allocations</c>）
/// 入口实时授权「与请求路径无关」单元测试。
/// <para>以真实 <see cref="DefaultHttpContext"/> 身份驱动真实 <see cref="CustomerReceiptAllocationController"/> /
/// <see cref="SupplierPaymentAllocationController"/>：空路径与已赋值路径必须给出完全一致的判定；
/// 绑定到请求管线但缺失 / 零 / 未知 / 已删除身份一律未认证（2000），禁用 / 缺少或撤销既有「收款单」/「付款单」菜单
/// 一律权限不足（2002），且全部先于任何分摊金额、快照或候选读取与写入；授权身份在<strong>空路径</strong>请求上完成
/// 既有「登记 → 作废 → 重新登记」生命周期，被拒绝调用零写入且不新增任何授权。</para>
/// <para>默认<strong>不设置</strong> <c>Request.Path</c>（空路径请求）用于证明授权判定与请求形状无关；
/// 并覆盖「完全未绑定 <c>HttpContext</c>」的纯进程内直调 —— 同样无条件实时授权、身份缺失一律未认证。
/// 全部使用隔离内存测试数据（<see cref="TestDbFactory"/>），绝不新增生产菜单 / 角色 / 用户授权，
/// 也不提供任何测试专用开关。真实隔离 SQL 用例见
/// <c>ERP.IntegrationTests/ReceiptPaymentAllocationEntryAuthorizationSqlServerTests.cs</c>。</para>
/// </summary>
public class ReceiptPaymentAllocationEntryAuthorizationTests
{
    private const string ReceiptPath = "/api/customer-receipt-allocations";
    private const string PaymentPath = "/api/supplier-payment-allocations";

    // ==================== 0. 测试脚手架（隔离测试数据，绝不新增生产授权） ====================

    /// <summary>补缺失的既有菜单行（与 <c>SeedData.Menus</c> 同源编码 / 文案），绝不改写既有菜单语义。</summary>
    private static SysMenu EnsureMenu(ErpDbContext db, string code, string name, string path)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted && m.MenuCode == code);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种既有菜单授权的启用账号：<paramref name="privileged"/> = 系统内置角色口径（既有不受客户范围限制的账号）。
    /// <paramref name="receiptMenu"/> / <paramref name="paymentMenu"/> 控制是否授予既有「收款单」/「付款单」菜单。
    /// </summary>
    private static (long UserId, long RoleId) SeedOperator(
        ErpDbContext db, bool receiptMenu = true, bool paymentMenu = true,
        UserStatus status = UserStatus.Enabled, bool privileged = true)
    {
        var role = new SysRole
        {
            RoleCode = $"AllocEntry-{Guid.NewGuid():N}",
            RoleName = "分摊入口测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"alloc-entry-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "分摊入口测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (receiptMenu)
            db.SysRoleMenus.Add(new SysRoleMenu
            {
                RoleId = role.Id,
                MenuId = EnsureMenu(db, CustomerReceiptLifecycleRules.RequiredMenuCode,
                    CustomerReceiptLifecycleRules.RequiredMenuText, "/finance/receipt").Id
            });
        if (paymentMenu)
            db.SysRoleMenus.Add(new SysRoleMenu
            {
                RoleId = role.Id,
                MenuId = EnsureMenu(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
                    SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment").Id
            });
        db.SaveChanges();

        return (user.Id, role.Id);
    }

    /// <summary>播种受限业务员账号（登录账号 == 员工编码，ERP-097 权威映射）+ 既有两套菜单授权。</summary>
    private static (long UserId, long RoleId) SeedRestrictedOperator(ErpDbContext db, params long[] ownedCustomerIds)
    {
        var code = $"alloc-entry-sales-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var (userId, roleId) = SeedOperator(db, privileged: false);
        db.SysUsers.Single(u => u.Id == userId).UserName = code;
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
            db.BaseCustomers.Single(c => c.Id == customerId).EmpId = employee.Id;
        db.SaveChanges();

        return (userId, roleId);
    }

    /// <summary>撤销指定既有菜单的角色授权（软删除既有授权行，下一次请求立即收敛）。</summary>
    private static void RevokeMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menuIds = db.SysMenus.Where(m => m.MenuCode == menuCode).Select(m => m.Id).ToList();
        foreach (var grant in db.SysRoleMenus
                     .Where(g => !g.IsDeleted && g.RoleId == roleId && menuIds.Contains(g.MenuId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static void SoftDeleteUser(ErpDbContext db, long userId)
    {
        db.SysUsers.Single(u => u.Id == userId).IsDeleted = true;
        db.SaveChanges();
    }

    // ==================== 0.1 隔离主数据 / 单据播种 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = $"客户-{code}",
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 1_000_000m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = $"供应商-{code}", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo, long customerId,
        Currency currency = Currency.USD, decimal totalAmount = 1000m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId,
        decimal amount = 1000m, Currency currency = Currency.USD)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static PurchaseOrder SeedPurchaseOrder(
        ErpDbContext db, string orderNo, long supplierId, decimal totalAmount = 1000m)
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
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount = 1000m)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 9, 10),
            SupplierId = supplierId,
            Amount = amount,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static CustomerReceiptAllocationSaveDto ReceiptDto(long receiptId, long orderId, decimal amount)
        => new() { ReceiptId = receiptId, SalesOrderId = orderId, AllocatedAmount = amount, Remark = "ERP468" };

    private static SupplierPaymentAllocationSaveDto PaymentDto(long paymentId, long orderId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseOrderId = orderId, AllocatedAmount = amount, Remark = "ERP468" };

    // ==================== 0.2 控制器（空路径 = 不设置 Request.Path） ====================

    /// <summary>绑定真实请求上下文 + 指定身份；<paramref name="requestPath"/> 为 null = 空路径请求。</summary>
    private static DefaultHttpContext NewHttp(long? userId, string? requestPath)
    {
        var claims = userId is > 0
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return http;
    }

    private static CustomerReceiptAllocationController NewReceiptController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    private static SupplierPaymentAllocationController NewPaymentController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    // ==================== 0.3 判定助手 ====================

    /// <summary>收款分摊证据全部 8 条入口在同一身份下必须给出同一受控判定。</summary>
    private static async Task AssertReceiptRoutesDenied(
        CustomerReceiptAllocationController ctl, long receiptId, long orderId, int expectedCode)
    {
        await AssertDenied(expectedCode, () => ctl.GetPaged(new CustomerReceiptAllocationQuery()));
        await AssertDenied(expectedCode, () => ctl.ReceiptCandidates(null, null, 50));
        await AssertDenied(expectedCode, () => ctl.ReceiptSummary(receiptId));
        await AssertDenied(expectedCode, () => ctl.AllocationsForReceipt(receiptId));
        await AssertDenied(expectedCode, () => ctl.OrderCandidates(receiptId, null, 50));
        await AssertDenied(expectedCode, () => ctl.GetById(1L));
        await AssertDenied(expectedCode, () => ctl.Create(ReceiptDto(receiptId, orderId, 10m)));
        await AssertDenied(expectedCode, () =>
            ctl.Void(1L, new CustomerReceiptAllocationVoidRequest { Reason = "作废" }));
    }

    /// <summary>付款引用证据全部 8 条入口在同一身份下必须给出同一受控判定。</summary>
    private static async Task AssertPaymentRoutesDenied(
        SupplierPaymentAllocationController ctl, long paymentId, long orderId, int expectedCode)
    {
        await AssertDenied(expectedCode, () => ctl.GetPaged(new SupplierPaymentAllocationQuery()));
        await AssertDenied(expectedCode, () => ctl.PaymentCandidates(null, null, 50));
        await AssertDenied(expectedCode, () => ctl.PaymentSummary(paymentId));
        await AssertDenied(expectedCode, () => ctl.AllocationsForPayment(paymentId));
        await AssertDenied(expectedCode, () => ctl.OrderCandidates(paymentId, null, 50));
        await AssertDenied(expectedCode, () => ctl.GetById(1L));
        await AssertDenied(expectedCode, () => ctl.Create(PaymentDto(paymentId, orderId, 10m)));
        await AssertDenied(expectedCode, () =>
            ctl.Void(1L, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }));
    }

    private static async Task<BusinessException> AssertDenied(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
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

    // ==================== 1. 绑定请求管线但缺少身份：空路径与已赋值路径判决完全一致 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(ReceiptPath)]
    public async Task Receipt_routes_without_identity_are_unauthorized_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-NOID");
        var receipt = SeedReceipt(db, "SK-R468-NOID", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-NOID", customer.Id);
        var ctl = NewReceiptController(db, userId: null, path);

        await AssertReceiptRoutesDenied(ctl, receipt.Id, order.Id, ErrorCodes.Unauthorized);

        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PaymentPath)]
    public async Task Payment_routes_without_identity_are_unauthorized_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-NOID");
        var payment = SeedPayment(db, "FK-R468-NOID", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-NOID", supplier.Id);
        var ctl = NewPaymentController(db, userId: null, path);

        await AssertPaymentRoutesDenied(ctl, payment.Id, order.Id, ErrorCodes.Unauthorized);

        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }

    // ==================== 2. 零 / 未知 / 已删除身份：一律未认证 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(ReceiptPath)]
    public async Task Receipt_zero_unknown_and_deleted_identity_are_unauthorized_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-DEL");
        var receipt = SeedReceipt(db, "SK-R468-DEL", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-DEL", customer.Id);

        var (deletedUserId, _) = SeedOperator(db);
        SoftDeleteUser(db, deletedUserId);

        foreach (var userId in new long?[] { 0L, 9_468_999_999L, deletedUserId })
        {
            var ctl = NewReceiptController(db, userId, path);
            await AssertReceiptRoutesDenied(ctl, receipt.Id, order.Id, ErrorCodes.Unauthorized);
        }

        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PaymentPath)]
    public async Task Payment_zero_unknown_and_deleted_identity_are_unauthorized_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-DEL");
        var payment = SeedPayment(db, "FK-R468-DEL", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-DEL", supplier.Id);

        var (deletedUserId, _) = SeedOperator(db);
        SoftDeleteUser(db, deletedUserId);

        foreach (var userId in new long?[] { 0L, 9_468_999_998L, deletedUserId })
        {
            var ctl = NewPaymentController(db, userId, path);
            await AssertPaymentRoutesDenied(ctl, payment.Id, order.Id, ErrorCodes.Unauthorized);
        }

        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }

    // ==================== 3. 禁用 / 缺少或撤销菜单：一律权限不足 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(ReceiptPath)]
    public async Task Receipt_disabled_and_menu_less_identity_are_forbidden_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-FORB");
        var receipt = SeedReceipt(db, "SK-R468-FORB", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-FORB", customer.Id);

        var (disabledId, _) = SeedOperator(db, status: UserStatus.Disabled);
        var (noMenuId, _) = SeedOperator(db, receiptMenu: false, paymentMenu: false);

        foreach (var userId in new[] { disabledId, noMenuId })
        {
            var ctl = NewReceiptController(db, userId, path);
            await AssertReceiptRoutesDenied(ctl, receipt.Id, order.Id, ErrorCodes.Forbidden);
        }

        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PaymentPath)]
    public async Task Payment_disabled_and_menu_less_identity_are_forbidden_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-FORB");
        var payment = SeedPayment(db, "FK-R468-FORB", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-FORB", supplier.Id);

        var (disabledId, _) = SeedOperator(db, status: UserStatus.Disabled);
        var (noMenuId, _) = SeedOperator(db, receiptMenu: true, paymentMenu: false);

        foreach (var userId in new[] { disabledId, noMenuId })
        {
            var ctl = NewPaymentController(db, userId, path);
            await AssertPaymentRoutesDenied(ctl, payment.Id, order.Id, ErrorCodes.Forbidden);
        }

        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReceiptPath)]
    public async Task Receipt_revoked_menu_converges_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-REVOKE");
        var receipt = SeedReceipt(db, "SK-R468-REVOKE", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-REVOKE", customer.Id);
        var (userId, roleId) = SeedOperator(db);
        var ctl = NewReceiptController(db, userId, path);

        // 授权仍在：读取成功（证明判定与请求路径无关，而不是「一律拒绝」）。
        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new CustomerReceiptAllocationQuery()));

        RevokeMenu(db, roleId, CustomerReceiptLifecycleRules.RequiredMenuCode);

        await AssertReceiptRoutesDenied(ctl, receipt.Id, order.Id, ErrorCodes.Forbidden);
        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PaymentPath)]
    public async Task Payment_revoked_menu_converges_on_both_paths(string? path)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-REVOKE");
        var payment = SeedPayment(db, "FK-R468-REVOKE", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-REVOKE", supplier.Id);
        var (userId, roleId) = SeedOperator(db);
        var ctl = NewPaymentController(db, userId, path);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new SupplierPaymentAllocationQuery()));

        RevokeMenu(db, roleId, SupplierPaymentLifecycleRules.RequiredMenuCode);

        await AssertPaymentRoutesDenied(ctl, payment.Id, order.Id, ErrorCodes.Forbidden);
        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }

    // ==================== 4. 空路径与已赋值路径：同一身份必须得到完全一致的判定 ====================

    [Fact]
    public async Task Receipt_authorized_reads_are_identical_on_empty_and_populated_paths()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-PARITY");
        var receipt = SeedReceipt(db, "SK-R468-PARITY", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-PARITY", customer.Id);
        var (userId, _) = SeedOperator(db);

        var created = AssertOk<CustomerReceiptAllocationDto>(
            await NewReceiptController(db, userId, requestPath: null)
                .Create(ReceiptDto(receipt.Id, order.Id, 250m)));
        Assert.True(created.IsActive);

        var seen = new List<(long Id, decimal Amount, int Count)>();
        foreach (var path in new[] { null, ReceiptPath })
        {
            var ctl = NewReceiptController(db, userId, path);

            var page = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
                await ctl.GetPaged(new CustomerReceiptAllocationQuery { PageSize = 20 }));
            Assert.Equal(1, page.Total);
            var only = Assert.Single(page.Items);
            Assert.Equal(created.Id, only.Id);
            Assert.Equal(250m, only.AllocatedAmount);

            var detail = AssertOk<CustomerReceiptAllocationDto>(await ctl.GetById(created.Id));
            Assert.Equal(250m, detail.AllocatedAmount);
            Assert.Equal("SK-R468-PARITY", detail.ReceiptNo);

            var summary = AssertOk<CustomerReceiptAllocationReceiptSummaryDto>(await ctl.ReceiptSummary(receipt.Id));
            Assert.Equal(250m, summary.AllocatedAmount);
            Assert.Equal(750m, summary.UnallocatedAmount);
            Assert.Equal(1, summary.AllocationCount);
            Assert.Equal(0, summary.VoidedCount);

            Assert.Single(AssertOk<List<CustomerReceiptAllocationDto>>(
                await ctl.AllocationsForReceipt(receipt.Id)));
            Assert.Single(AssertOk<List<CustomerReceiptAllocationReceiptCandidateDto>>(
                await ctl.ReceiptCandidates(null, null, 50)));
            Assert.Single(AssertOk<List<CustomerReceiptAllocationOrderCandidateDto>>(
                await ctl.OrderCandidates(receipt.Id, null, 50)));

            seen.Add((only.Id, summary.AllocatedAmount, summary.AllocationCount));
        }

        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0], seen[1]);
        Assert.Equal((created.Id, 250m, 1), seen[0]);
    }

    [Fact]
    public async Task Payment_authorized_reads_are_identical_on_empty_and_populated_paths()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-PARITY");
        var payment = SeedPayment(db, "FK-R468-PARITY", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-PARITY", supplier.Id);
        var (userId, _) = SeedOperator(db);

        var created = AssertOk<SupplierPaymentAllocationDto>(
            await NewPaymentController(db, userId, requestPath: null)
                .Create(PaymentDto(payment.Id, order.Id, 250m)));
        Assert.True(created.IsActive);

        var seen = new List<(long Id, decimal Amount, int Count)>();
        foreach (var path in new[] { null, PaymentPath })
        {
            var ctl = NewPaymentController(db, userId, path);

            var page = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
                await ctl.GetPaged(new SupplierPaymentAllocationQuery { PageSize = 20 }));
            Assert.Equal(1, page.Total);
            var only = Assert.Single(page.Items);
            Assert.Equal(created.Id, only.Id);
            Assert.Equal(250m, only.AllocatedAmount);

            var detail = AssertOk<SupplierPaymentAllocationDto>(await ctl.GetById(created.Id));
            Assert.Equal(250m, detail.AllocatedAmount);
            Assert.Equal("FK-R468-PARITY", detail.PaymentNo);

            var summary = AssertOk<SupplierPaymentAllocationPaymentSummaryDto>(await ctl.PaymentSummary(payment.Id));
            Assert.Equal(250m, summary.AllocatedAmount);
            Assert.Equal(750m, summary.UnallocatedAmount);
            Assert.Equal(1, summary.AllocationCount);
            Assert.Equal(0, summary.VoidedCount);

            Assert.Single(AssertOk<List<SupplierPaymentAllocationDto>>(
                await ctl.AllocationsForPayment(payment.Id)));
            Assert.Single(AssertOk<List<SupplierPaymentAllocationPaymentCandidateDto>>(
                await ctl.PaymentCandidates(null, null, 50)));
            Assert.Single(AssertOk<List<SupplierPaymentAllocationOrderCandidateDto>>(
                await ctl.OrderCandidates(payment.Id, null, 50)));

            seen.Add((only.Id, summary.AllocatedAmount, summary.AllocationCount));
        }

        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0], seen[1]);
        Assert.Equal((created.Id, 250m, 1), seen[0]);
    }

    // ==================== 5. 授权身份在空路径请求上完成既有生命周期 ====================

    [Fact]
    public async Task Receipt_authorized_identity_completes_lifecycle_on_empty_request_path()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-LIFE");
        var otherCustomer = SeedCustomer(db, "C-R468-LIFE-OTHER");
        var receipt = SeedReceipt(db, "SK-R468-LIFE", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-LIFE", customer.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-R468-LIFE-F", otherCustomer.Id);
        var cnyOrder = SeedSalesOrder(db, "SO-R468-LIFE-CNY", customer.Id, Currency.CNY);
        var (userId, _) = SeedOperator(db);
        var ctl = NewReceiptController(db, userId, requestPath: null);

        var created = AssertOk<CustomerReceiptAllocationDto>(
            await ctl.Create(ReceiptDto(receipt.Id, order.Id, 250m)));
        Assert.True(created.IsActive);
        Assert.Equal(250m, created.AllocatedAmount);
        Assert.Equal("SK-R468-LIFE", created.ReceiptNo);

        // 既有金额精度 / 大于 0、币种一致、客户一致与重复有效行护栏全部保留（授权通过后才进入业务校验）。
        await AssertDenied(ErrorCodes.InvalidParameter, () => ctl.Create(ReceiptDto(receipt.Id, order.Id, 0.004m)));
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Create(ReceiptDto(receipt.Id, cnyOrder.Id, 10m)));
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Create(ReceiptDto(receipt.Id, foreignOrder.Id, 10m)));
        await AssertDenied(ErrorCodes.Duplicate, () => ctl.Create(ReceiptDto(receipt.Id, order.Id, 10m)));

        var voided = AssertOk<CustomerReceiptAllocationDto>(
            await ctl.Void(created.Id, new CustomerReceiptAllocationVoidRequest { Reason = "引用录错" }));
        Assert.True(voided.IsVoided);
        Assert.Equal(250m, voided.AllocatedAmount);
        Assert.Equal("引用录错", voided.VoidReason);

        // 作废释放额度后可重新登记（既有语义不变，历史行保留、绝不物理删除）。
        var again = AssertOk<CustomerReceiptAllocationDto>(
            await ctl.Create(ReceiptDto(receipt.Id, order.Id, 100m)));
        Assert.True(again.IsActive);
        Assert.Equal(2, await db.CustomerReceiptAllocations.CountAsync());
        Assert.Equal(1, await db.CustomerReceiptAllocations
            .CountAsync(a => a.Status == CustomerReceiptAllocationRules.StatusVoided));
    }

    [Fact]
    public async Task Payment_authorized_identity_completes_lifecycle_on_empty_request_path()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-LIFE");
        var otherSupplier = SeedSupplier(db, "S-R468-LIFE-OTHER");
        var payment = SeedPayment(db, "FK-R468-LIFE", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-LIFE", supplier.Id);
        var foreignOrder = SeedPurchaseOrder(db, "PO-R468-LIFE-F", otherSupplier.Id);
        var (userId, _) = SeedOperator(db);
        var ctl = NewPaymentController(db, userId, requestPath: null);

        var created = AssertOk<SupplierPaymentAllocationDto>(
            await ctl.Create(PaymentDto(payment.Id, order.Id, 250m)));
        Assert.True(created.IsActive);
        Assert.Equal(250m, created.AllocatedAmount);
        Assert.Equal("FK-R468-LIFE", created.PaymentNo);

        await AssertDenied(ErrorCodes.InvalidParameter, () => ctl.Create(PaymentDto(payment.Id, order.Id, 0.004m)));
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Create(PaymentDto(payment.Id, foreignOrder.Id, 10m)));
        await AssertDenied(ErrorCodes.Duplicate, () => ctl.Create(PaymentDto(payment.Id, order.Id, 10m)));

        var voided = AssertOk<SupplierPaymentAllocationDto>(
            await ctl.Void(created.Id, new SupplierPaymentAllocationVoidRequest { Reason = "引用录错" }));
        Assert.True(voided.IsVoided);
        Assert.Equal(250m, voided.AllocatedAmount);
        Assert.Equal("引用录错", voided.VoidReason);

        var again = AssertOk<SupplierPaymentAllocationDto>(
            await ctl.Create(PaymentDto(payment.Id, order.Id, 100m)));
        Assert.True(again.IsActive);
        Assert.Equal(2, await db.SupplierPaymentAllocations.CountAsync());
        Assert.Equal(1, await db.SupplierPaymentAllocations
            .CountAsync(a => a.Status == SupplierPaymentAllocationRules.StatusVoided));
    }

    // ==================== 6. 被拒绝调用零写入（不落证据、不改写收款单 / 付款单、不新增授权） ====================

    [Fact]
    public async Task Denied_calls_persist_zero_mutation_snapshots()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-ZERO");
        var receipt = SeedReceipt(db, "SK-R468-ZERO", customer.Id);
        var salesOrder = SeedSalesOrder(db, "SO-R468-ZERO", customer.Id);
        var supplier = SeedSupplier(db, "S-R468-ZERO");
        var purchaseOrder = SeedPurchaseOrder(db, "PO-R468-ZERO", supplier.Id);
        var payment = SeedPayment(db, "FK-R468-ZERO", supplier.Id);

        var (userId, _) = SeedOperator(db);
        Assert.True(AssertOk<CustomerReceiptAllocationDto>(
            await NewReceiptController(db, userId, null)
                .Create(ReceiptDto(receipt.Id, salesOrder.Id, 250m))).IsActive);
        Assert.True(AssertOk<SupplierPaymentAllocationDto>(
            await NewPaymentController(db, userId, null)
                .Create(PaymentDto(payment.Id, purchaseOrder.Id, 250m))).IsActive);

        var before = await SnapshotAsync(db);
        var grantsBefore = await db.SysRoleMenus.CountAsync(g => !g.IsDeleted);

        foreach (var path in new[] { null, ReceiptPath })
            await AssertReceiptRoutesDenied(
                NewReceiptController(db, null, path), receipt.Id, salesOrder.Id, ErrorCodes.Unauthorized);
        foreach (var path in new[] { null, PaymentPath })
            await AssertPaymentRoutesDenied(
                NewPaymentController(db, null, path), payment.Id, purchaseOrder.Id, ErrorCodes.Unauthorized);

        Assert.Equal(before, await SnapshotAsync(db));
        Assert.Equal(grantsBefore, await db.SysRoleMenus.CountAsync(g => !g.IsDeleted));
    }

    /// <summary>证据行 / 分摊金额 / 权威单据状态快照：被拒绝调用前后必须完全一致。</summary>
    private static async Task<(int ReceiptRows, int PaymentRows, decimal ReceiptAllocated, decimal PaymentAllocated,
        int ReceiptStatus, int PaymentStatus)> SnapshotAsync(ErpDbContext db)
        => (await db.CustomerReceiptAllocations.CountAsync(),
            await db.SupplierPaymentAllocations.CountAsync(),
            await db.CustomerReceiptAllocations.SumAsync(a => a.AllocatedAmount),
            await db.SupplierPaymentAllocations.SumAsync(a => a.AllocatedAmount),
            (int)await db.FinanceReceipts.Select(r => r.Status).FirstAsync(),
            (int)await db.FinancePayments.Select(p => p.Status).FirstAsync());

    // ==================== 7. ERP-097 权威客户范围：空路径与已赋值路径一致收敛 ====================

    [Fact]
    public async Task Restricted_receipt_identity_scope_is_identical_on_both_paths()
    {
        using var db = TestDbFactory.Create();
        var ownCustomer = SeedCustomer(db, "C-R468-OWN");
        var foreignCustomer = SeedCustomer(db, "C-R468-FOREIGN");
        var ownReceipt = SeedReceipt(db, "SK-R468-OWN", ownCustomer.Id);
        var foreignReceipt = SeedReceipt(db, "SK-R468-FOREIGN", foreignCustomer.Id);
        var ownOrderA = SeedSalesOrder(db, "SO-R468-OWN-A", ownCustomer.Id);
        var ownOrderB = SeedSalesOrder(db, "SO-R468-OWN-B", ownCustomer.Id);
        var (userId, _) = SeedRestrictedOperator(db, ownCustomer.Id);

        var paths = new[] { null, ReceiptPath };
        for (var i = 0; i < paths.Length; i++)
        {
            var ctl = NewReceiptController(db, userId, paths[i]);

            // 自有范围：读取与登记被许可（与请求形状无关）。
            Assert.IsType<OkObjectResult>(await ctl.ReceiptSummary(ownReceipt.Id));
            var own = AssertOk<CustomerReceiptAllocationDto>(
                await ctl.Create(ReceiptDto(ownReceipt.Id, i == 0 ? ownOrderA.Id : ownOrderB.Id, 50m)));
            Assert.True(own.IsActive);

            // 范围外：与「不存在」同一条不披露错误（不泄露范围外 Id / 金额 / 计数），且零写入。
            var denied = await AssertDenied(ErrorCodes.NotFound, () => ctl.ReceiptSummary(foreignReceipt.Id));
            Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText, denied.Message);
            var createDenied = await AssertDenied(ErrorCodes.NotFound,
                () => ctl.Create(ReceiptDto(foreignReceipt.Id, ownOrderA.Id, 10m)));
            Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText, createDenied.Message);
        }

        Assert.Equal(2, await db.CustomerReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Restricted_payment_identity_fails_closed_identically_on_both_paths()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-RESTRICT");
        var payment = SeedPayment(db, "FK-R468-RESTRICT", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-RESTRICT", supplier.Id);
        var (userId, _) = SeedOperator(db, privileged: false);

        foreach (var path in new[] { null, PaymentPath })
        {
            var ctl = NewPaymentController(db, userId, path);

            var page = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
                await ctl.GetPaged(new SupplierPaymentAllocationQuery()));
            Assert.Empty(page.Items);

            // 付款单未关联客户（未关联货款申请单）：受限账号 fail closed，与请求形状无关。
            var summaryDenied = await AssertDenied(ErrorCodes.NotFound, () => ctl.PaymentSummary(payment.Id));
            Assert.Equal(SupplierPaymentLifecycleRules.AllocationNotFoundText, summaryDenied.Message);
            var createDenied = await AssertDenied(ErrorCodes.NotFound,
                () => ctl.Create(PaymentDto(payment.Id, order.Id, 10m)));
            Assert.Equal(SupplierPaymentLifecycleRules.AllocationNotFoundText, createDenied.Message);
        }

        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }

    // ==================== 8. 控制器源码契约：授权门与请求形状无关且无兜底 ====================

    [Fact]
    public void Controllers_gate_live_authorization_independently_of_request_shape()
    {
        var receipts = ReadSource("src", "ERP.Api", "Controllers", "CustomerReceiptAllocationController.cs");
        var payments = ReadSource("src", "ERP.Api", "Controllers", "SupplierPaymentAllocationController.cs");

        foreach (var controller in new[] { receipts, payments })
        {
            // ERP-468：请求形状门 / 无上下文豁免彻底移除 —— 不再按请求路径、身份或「是否绑定 HttpContext」放行，
            // 登记 / 作废也绝不退回无授权的服务直调。
            Assert.DoesNotContain("RequiresLiveAuthorization", controller);
            Assert.DoesNotContain("ControllerContext?.HttpContext is not null", controller);
            Assert.DoesNotContain("Request.Path", controller);
            Assert.DoesNotContain("AllowAnonymous", controller);
            Assert.DoesNotContain("[Authorize(Roles", controller);
            Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller);
            Assert.DoesNotContain("UseInMemoryDatabase", controller);

            // 登记 / 作废走授权服务重载，绝非无授权的服务直调。
            Assert.Contains("CreateAuthorizedAsync", controller);
            Assert.Contains("VoidAuthorizedAsync", controller);
        }

        // 无授权的服务直调重载绝不被控制器引用（登记 / 作废只走授权重载）。
        Assert.DoesNotContain("CustomerReceiptAllocationService.CreateAsync", receipts);
        Assert.DoesNotContain("CustomerReceiptAllocationService.VoidAsync", receipts);
        Assert.DoesNotContain("SupplierPaymentAllocationService.CreateAsync", payments);
        Assert.DoesNotContain("SupplierPaymentAllocationService.VoidAsync", payments);

        // 每一条入口（台账 / 候选 / 汇总 / 清单 / 订单候选 / 详情）仍在读写之前实时授权。
        Assert.Contains("CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync", receipts);
        Assert.Contains("SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync", payments);

        // 口径文案与实现同源。
        foreach (var text in new[]
                 {
                     CustomerReceiptLifecycleRules.PathIndependenceText,
                     SupplierPaymentLifecycleRules.PathIndependenceText
                 })
        {
            Assert.Contains("HTTP 请求管线", text);
            Assert.Contains("未认证", text);
            Assert.Contains("权限不足", text);
            Assert.Contains("Request.Path", text);
        }
    }

    // ==================== 9. 完全未绑定 HttpContext：同样无条件 fail closed ====================

    [Fact]
    public async Task Receipt_routes_without_any_HttpContext_are_unauthorized()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-NOCTX");
        var receipt = SeedReceipt(db, "SK-R468-NOCTX", customer.Id);
        var order = SeedSalesOrder(db, "SO-R468-NOCTX", customer.Id);

        // 完全未绑定请求上下文（ControllerContext.HttpContext 为 null）的纯进程内直调：
        // ERP-468 起同样无条件实时授权，身份缺失一律未认证，绝不因「无 HttpContext」放行。
        var ctl = new CustomerReceiptAllocationController(db);

        await AssertReceiptRoutesDenied(ctl, receipt.Id, order.Id, ErrorCodes.Unauthorized);
        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Payment_routes_without_any_HttpContext_are_unauthorized()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-R468-NOCTX");
        var payment = SeedPayment(db, "FK-R468-NOCTX", supplier.Id);
        var order = SeedPurchaseOrder(db, "PO-R468-NOCTX", supplier.Id);

        var ctl = new SupplierPaymentAllocationController(db);

        await AssertPaymentRoutesDenied(ctl, payment.Id, order.Id, ErrorCodes.Unauthorized);
        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }

    [Fact]
    public async Task No_Http_Context_never_registers_or_voids_evidence_even_with_valid_data()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-R468-NOCTX-MUT");
        var receipt = SeedReceipt(db, "SK-R468-NOCTX-MUT", customer.Id, amount: 1000m);
        var order = SeedSalesOrder(db, "SO-R468-NOCTX-MUT", customer.Id, totalAmount: 1000m);
        var supplier = SeedSupplier(db, "S-R468-NOCTX-MUT");
        var payment = SeedPayment(db, "FK-R468-NOCTX-MUT", supplier.Id, amount: 1000m);
        var purchaseOrder = SeedPurchaseOrder(db, "PO-R468-NOCTX-MUT", supplier.Id, totalAmount: 1000m);

        // 即使提供完全合法的金额 / 币种 / 关联，未绑定请求上下文同样零写入。
        var receiptCtl = new CustomerReceiptAllocationController(db);
        await AssertDenied(ErrorCodes.Unauthorized, () =>
            receiptCtl.Create(ReceiptDto(receipt.Id, order.Id, 100m)));
        await AssertDenied(ErrorCodes.Unauthorized, () =>
            receiptCtl.Void(1L, new CustomerReceiptAllocationVoidRequest { Reason = "作废" }));

        var paymentCtl = new SupplierPaymentAllocationController(db);
        await AssertDenied(ErrorCodes.Unauthorized, () =>
            paymentCtl.Create(PaymentDto(payment.Id, purchaseOrder.Id, 100m)));
        await AssertDenied(ErrorCodes.Unauthorized, () =>
            paymentCtl.Void(1L, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }));

        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());
    }
}
