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
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-351 供应商付款单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="SupplierPaymentLifecycleRules"/> 与两套付款引用证据服务做真实 SQL Server 验证：
/// 两套引用类型（ERP-049 / ERP-066）、精确合计 / 跨消费者超额、作废释放、未关联付款、不兼容申请单 / 供应商 / 币种、
/// 拒绝 / 撤销调用方、付款取消 vs 引用、以及两个独立连接上的跨消费者并发引用（付款单行锁串行化后只能成功其一）。
/// 失败时原始付款单与引用证据保持不变。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SupplierPaymentLifecycleSqlServerTests
    : IClassFixture<SupplierPaymentLifecycleSqlServerFixture>
{
    private readonly SupplierPaymentLifecycleSqlServerFixture _fixture;

    public SupplierPaymentLifecycleSqlServerTests(SupplierPaymentLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 种子助手（EF 生成身份主键） ====================

    private static long SeedSupplier(ErpDbContext db, string code, string name, int status = 1)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier.Id;
    }

    private static long SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static long SeedPaymentApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount, Currency currency,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply.Id;
    }

    private static long SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount, Currency currency,
        DocumentStatus status = DocumentStatus.Pending, long? paymentApplyId = null)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment.Id;
    }

    private static long SeedOrder(ErpDbContext db, string orderNo, long supplierId, Currency currency, decimal totalAmount = 5000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order.Id;
    }

    private static long SeedInvoice(
        ErpDbContext db, string number, long supplierId, string currency, decimal grossAmount = 5000m)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = "普票",
            InvoiceNumber = number,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = DateTime.Today.AddDays(-3),
            SupplierId = supplierId,
            SupplierCode = "S" + supplierId,
            SupplierName = "供应商" + supplierId,
            Currency = currency,
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = PurchaseInvoiceRules.StatusRecorded
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice.Id;
    }

    private static SupplierPaymentAllocationSaveDto OrderDto(long paymentId, long orderId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseOrderId = orderId, AllocatedAmount = amount, Remark = string.Empty };

    private static SupplierPaymentInvoiceAllocationSaveDto InvoiceDto(long paymentId, long invoiceId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseInvoiceId = invoiceId, AllocatedAmount = amount, Remark = string.Empty };

    // ==================== 1. 两套引用类型 / 精确合计 / 超额 / 作废释放 ====================

    [Fact]
    public async Task 两套引用类型_订单80_发票20_精确合计100_成功()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP_S1", "供应商A");
        var orderId = SeedOrder(db, "INT_SP_PO1", supplierId, Currency.CNY);
        var invoiceId = SeedInvoice(db, "INT_SP_INV1", supplierId, "CNY");
        var paymentId = SeedPayment(db, "INT_SP_PAY1", supplierId, 100m, Currency.CNY);

        await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(paymentId, orderId, 80m));
        await SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(paymentId, invoiceId, 20m), "tester");

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, paymentId);
        Assert.Equal(80m, funding.OrderAllocated);
        Assert.Equal(20m, funding.InvoiceAllocated);
        Assert.Equal(100m, funding.CombinedAllocated);
    }

    [Fact]
    public async Task 跨消费者_超额_拒绝且两表不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP_S2", "供应商B");
        var orderId = SeedOrder(db, "INT_SP_PO2", supplierId, Currency.CNY);
        var invoiceId = SeedInvoice(db, "INT_SP_INV2", supplierId, "CNY");
        var paymentId = SeedPayment(db, "INT_SP_PAY2", supplierId, 100m, Currency.CNY);

        await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(paymentId, orderId, 80m));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(paymentId, invoiceId, 21m), "tester"));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Equal(1, await db.SupplierPaymentAllocations.AsNoTracking().CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted));
        Assert.Equal(0, await db.SupplierPaymentInvoiceAllocations.AsNoTracking().CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted));
    }

    [Fact]
    public async Task 作废释放后_可继续引用()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP_S3", "供应商C");
        var orderId = SeedOrder(db, "INT_SP_PO3", supplierId, Currency.CNY);
        var invoiceId = SeedInvoice(db, "INT_SP_INV3", supplierId, "CNY");
        var paymentId = SeedPayment(db, "INT_SP_PAY3", supplierId, 100m, Currency.CNY);

        var orderRow = await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(paymentId, orderId, 80m));
        await SupplierPaymentAllocationService.VoidAsync(db, orderRow.Id, "录错");
        await SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(paymentId, invoiceId, 100m), "tester");

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, paymentId);
        Assert.Equal(0m, funding.OrderAllocated);
        Assert.Equal(100m, funding.InvoiceAllocated);
    }

    [Fact]
    public async Task 未关联付款_登记订单引用_成功()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP_S4", "供应商D");
        var orderId = SeedOrder(db, "INT_SP_PO4", supplierId, Currency.CNY);
        var paymentId = SeedPayment(db, "INT_SP_PAY4", supplierId, 100m, Currency.CNY, paymentApplyId: null);

        await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(paymentId, orderId, 60m));

        var payment = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        Assert.Null(payment.PaymentApplyId);
        Assert.Equal(60m, (await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.PaymentId == paymentId)).AllocatedAmount);
    }

    [Fact]
    public async Task 悬空申请单_按Id拒绝_不从标签猜测()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(db, 999999L, "CNY", 100m));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 申请单已取消或币种不兼容_拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = SeedCustomer(db, "INT_SP_C6", "客户F");
        var applyUsd = SeedPaymentApply(db, "INT_SP_DJ6", customerId, 1000m, Currency.USD, status: DocumentStatus.Approved);
        var applyCancelled = SeedPaymentApply(db, "INT_SP_DJ6C", customerId, 1000m, Currency.CNY, status: DocumentStatus.Cancelled);

        await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(db, applyCancelled, "CNY", 100m));
        await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(db, applyUsd, "CNY", 100m));
        await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(db, applyUsd, "USD", 1001m));
    }

    // ==================== 2. 拒绝 / 撤销调用方与供应商护栏 ====================

    [Fact]
    public async Task 无付款菜单_权限不足()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = SeedCustomer(db, "INT_SP_C7", "客户G");

        var role = new SysRole { RoleName = "无菜单角色", RoleCode = $"INT_SP_NOMENU_{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser { UserName = $"nomenu-{Guid.NewGuid():N}", PasswordHash = "h", PasswordSalt = "s", DisplayName = "nomenu", Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(db, user.Id, customerId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 越界客户_权限不足()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userName = $"sales-{Guid.NewGuid():N}";

        var role = new SysRole { RoleName = "业务员", RoleCode = $"INT_SP_SALES_{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser { UserName = userName, PasswordHash = "h", PasswordSalt = "s", DisplayName = userName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = await db.SysMenus.SingleAsync(m => m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        var employee = new BaseEmployee { EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var outOfScope = SeedCustomer(db, "INT_SP_C8", "范围外", empId: 999999L);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(db, user.Id, outOfScope));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 停用供应商_拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP_S9", "停用供应商", status: 0);
        var supplier = await db.BaseSuppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);

        Assert.Throws<BusinessException>(() => SupplierPaymentLifecycleRules.EnsureSupplierAvailable(supplier, supplierId));
    }

    // ==================== 3. 付款取消 vs 有效引用（事务 + 行锁） ====================

    [Fact]
    public async Task 取消付款单_存在有效引用_拒绝且原状态与证据不变()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(seed, "INT_SP_S10", "供应商J");
        var orderId = SeedOrder(seed, "INT_SP_PO10", supplierId, Currency.CNY);
        var paymentId = SeedPayment(seed, "INT_SP_PAY10", supplierId, 100m, Currency.CNY);
        await SupplierPaymentAllocationService.CreateAsync(seed, OrderDto(paymentId, orderId, 40m));

        var result = await TryCancelAsync(paymentId);

        Assert.False(result.Success);
        await using var db = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending, (await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Status);
        Assert.Equal(40m, (await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.PaymentId == paymentId)).AllocatedAmount);
    }

    private async Task<(bool Success, string Error)> TryCancelAsync(long paymentId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await SupplierPaymentLifecycleRules.LockPaymentRowAsync(db, paymentId);
            var payment = await db.FinancePayments.FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted)
                ?? throw BusinessException.NotFound("付款单不存在");
            if (payment.Status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("付款单已取消，不能重复取消");
            await SupplierPaymentLifecycleRules.EnsureNoActiveAllocationAsync(db, paymentId, "取消");
            payment.Status = DocumentStatus.Cancelled;
            payment.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return (false, ex.Message);
        }
    }

    // ==================== 4. 并发竞态（付款单行锁串行化） ====================

    [Fact]
    public async Task 并发_登记证据vs取消_只能成功其一()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(seed, "INT_SP_S11", "供应商K");
        var orderId = SeedOrder(seed, "INT_SP_PO11", supplierId, Currency.CNY);
        var paymentId = SeedPayment(seed, "INT_SP_PAY11", supplierId, 100m, Currency.CNY);

        var results = await Task.WhenAll(
            TryAllocateOrderAsync(paymentId, orderId, 40m),
            TryCancelAsync(paymentId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));
    }

    [Fact]
    public async Task 并发_跨消费者登记_只能成功其一()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(seed, "INT_SP_S12", "供应商L");
        var orderId = SeedOrder(seed, "INT_SP_PO12", supplierId, Currency.CNY);
        var invoiceId = SeedInvoice(seed, "INT_SP_INV12", supplierId, "CNY");
        var paymentId = SeedPayment(seed, "INT_SP_PAY12", supplierId, 100m, Currency.CNY);

        var results = await Task.WhenAll(
            TryAllocateOrderAsync(paymentId, orderId, 80m),
            TryAllocateInvoiceAsync(paymentId, invoiceId, 80m));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using var db = _fixture.CreateDbContext();
        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, paymentId);
        Assert.True(funding.CombinedAllocated <= 100m);
    }

    private async Task<(bool Success, string Error)> TryAllocateOrderAsync(
        long paymentId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(paymentId, orderId, amount));
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryAllocateInvoiceAsync(
        long paymentId, long invoiceId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(paymentId, invoiceId, amount), "tester");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 5. 认证控制器（ERP-433：实时身份 + 付款菜单 + 客户范围） ====================

    private static long SeedPrivilegedUserWithMenu(ErpDbContext db)
    {
        var role = new SysRole { RoleName = "集成特权角色", RoleCode = $"INT_SP433_PRIV_{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = $"int-sp433-priv-{Guid.NewGuid():N}",
            PasswordHash = "h", PasswordSalt = "s", DisplayName = "特权", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = db.SysMenus.Single(m => m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static long SeedSalesmanWithMenu(ErpDbContext db, string userName, bool grantMenu = true)
    {
        var role = new SysRole { RoleName = "集成业务员", RoleCode = $"INT_SP433_SALES_{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = userName, PasswordHash = "h", PasswordSalt = "s", DisplayName = userName, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = db.SysMenus.Single(m => m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode && !m.IsDeleted);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.BaseEmployees.Add(new BaseEmployee { EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1 });
        db.SaveChanges();
        return user.Id;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static T BuildAuthController<T>(T controller, long? userId) where T : ControllerBase
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "IntTest"))
        };
        // 标记为真实 HTTP 路由（Request.Path 已赋值）：缺失身份也必须实时授权并 fail closed。
        http.Request.Path = "/api/supplier-payment-allocations";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static SupplierPaymentAllocationController OrderController(ErpDbContext db, long? userId)
        => BuildAuthController(new SupplierPaymentAllocationController(db), userId);

    private static SupplierPaymentInvoiceAllocationController InvoiceController(ErpDbContext db, long? userId)
        => BuildAuthController(new SupplierPaymentInvoiceAllocationController(db), userId);

    private static async Task<BusinessException> AssertCodeAsync(int code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    private static T AssertOkDto<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    [Fact]
    public async Task 认证控制器_缺失停用删除撤权身份与无菜单一律拒绝且不落行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP433_S1", "供应商433A");
        var orderId = SeedOrder(db, "INT_SP433_PO1", supplierId, Currency.CNY);
        var invoiceId = SeedInvoice(db, "INT_SP433_INV1", supplierId, "CNY");
        var customerId = SeedCustomer(db, "INT_SP433_C1", "客户433A");
        var applyId = SeedPaymentApply(db, "INT_SP433_DJ1", customerId, 100m, Currency.CNY);
        var paymentId = SeedPayment(db, "INT_SP433_PAY1", supplierId, 100m, Currency.CNY, DocumentStatus.Approved, applyId);

        var disabledId = SeedPrivilegedUserWithMenu(db);
        (await db.SysUsers.SingleAsync(u => u.Id == disabledId)).Status = UserStatus.Disabled;

        var deletedId = SeedPrivilegedUserWithMenu(db);
        (await db.SysUsers.SingleAsync(u => u.Id == deletedId)).IsDeleted = true;

        var revokedId = SeedPrivilegedUserWithMenu(db);
        RevokeMenus(db, revokedId);
        await db.SaveChangesAsync();

        var noMenuId = SeedSalesmanWithMenu(db, $"int-sp433-nomenu-{Guid.NewGuid():N}", grantMenu: false);

        // 缺失 / 非法 / 已删除身份 → 未认证
        foreach (long? userId in new long?[] { null, 0, deletedId })
        {
            var controller = OrderController(db, userId);
            await AssertCodeAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new SupplierPaymentAllocationQuery()));
            await AssertCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(OrderDto(paymentId, orderId, 10m)));
            await AssertCodeAsync(ErrorCodes.Unauthorized,
                () => controller.Void(1, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }));
            var invoiceController = InvoiceController(db, userId);
            await AssertCodeAsync(ErrorCodes.Unauthorized, () => invoiceController.Create(InvoiceDto(paymentId, invoiceId, 10m)));
        }

        // 禁用 / 删除菜单 / 无菜单 → 权限不足
        foreach (var userId in new[] { disabledId, revokedId, noMenuId })
        {
            var controller = OrderController(db, userId);
            await AssertCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new SupplierPaymentAllocationQuery()));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => controller.Create(OrderDto(paymentId, orderId, 10m)));
            await AssertCodeAsync(ErrorCodes.Forbidden,
                () => controller.Void(1, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }));
            var invoiceController = InvoiceController(db, userId);
            await AssertCodeAsync(ErrorCodes.Forbidden, () => invoiceController.Create(InvoiceDto(paymentId, invoiceId, 10m)));
        }

        // 被拒请求一律不落任何引用行
        Assert.Equal(0, await db.SupplierPaymentAllocations.AsNoTracking().CountAsync(a => a.PaymentId == paymentId));
        Assert.Equal(0, await db.SupplierPaymentInvoiceAllocations.AsNoTracking().CountAsync(a => a.PaymentId == paymentId));
    }

    [Fact]
    public async Task 认证控制器_自有范围登记与作废成功_越范围与已删除付款单同一条错误且零变更()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP433_S2", "供应商433B");
        var ownName = $"int-sp433-own-{Guid.NewGuid():N}";
        var salesmanId = SeedSalesmanWithMenu(db, ownName);
        var employeeId = (await db.BaseEmployees.SingleAsync(e => e.EmployeeCode == ownName)).Id;
        var ownCustomerId = SeedCustomer(db, "INT_SP433_OWN", "自有客户", employeeId);
        var foreignCustomerId = SeedCustomer(db, "INT_SP433_FGN", "他人客户");
        var ownApplyId = SeedPaymentApply(db, "INT_SP433_DJ_OWN", ownCustomerId, 200m, Currency.CNY);
        var foreignApplyId = SeedPaymentApply(db, "INT_SP433_DJ_FGN", foreignCustomerId, 200m, Currency.CNY);
        var deletionApplyId = SeedPaymentApply(db, "INT_SP433_DJ_DEL", ownCustomerId, 200m, Currency.CNY);
        var ownPaymentId = SeedPayment(db, "INT_SP433_PAY_OWN", supplierId, 200m, Currency.CNY, DocumentStatus.Approved, ownApplyId);
        var foreignPaymentId = SeedPayment(db, "INT_SP433_PAY_FGN", supplierId, 200m, Currency.CNY, DocumentStatus.Approved, foreignApplyId);
        var deletionPaymentId = SeedPayment(db, "INT_SP433_PAY_DEL", supplierId, 200m, Currency.CNY, DocumentStatus.Approved, deletionApplyId);
        var ownOrderId = SeedOrder(db, "INT_SP433_PO_OWN", supplierId, Currency.CNY);
        var ownOrder2Id = SeedOrder(db, "INT_SP433_PO_OWN2", supplierId, Currency.CNY);
        var foreignOrderId = SeedOrder(db, "INT_SP433_PO_FGN", supplierId, Currency.CNY);
        var deletionOrderId = SeedOrder(db, "INT_SP433_PO_DEL", supplierId, Currency.CNY);

        // 特权账号先建立两条有效引用行（含越范围与即将软删除的付款单）
        var privileged = OrderController(db, SeedPrivilegedUserWithMenu(db));
        var ownRow = AssertOkDto<SupplierPaymentAllocationDto>(await privileged.Create(OrderDto(ownPaymentId, ownOrderId, 50m)));
        var foreignRow = AssertOkDto<SupplierPaymentAllocationDto>(await privileged.Create(OrderDto(foreignPaymentId, foreignOrderId, 50m)));
        var deletionRow = AssertOkDto<SupplierPaymentAllocationDto>(await privileged.Create(OrderDto(deletionPaymentId, deletionOrderId, 50m)));

        (await db.FinancePayments.SingleAsync(p => p.Id == deletionPaymentId)).IsDeleted = true;
        await db.SaveChangesAsync();

        var controller = OrderController(db, salesmanId);

        // 自有范围内：登记与作废均被许可（被许可的生命周期）
        var ownNew = AssertOkDto<SupplierPaymentAllocationDto>(await controller.Create(OrderDto(ownPaymentId, ownOrder2Id, 20m)));
        Assert.True(ownNew.IsActive);
        var voided = AssertOkDto<SupplierPaymentAllocationDto>(
            await controller.Void(ownRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "录错" }));
        Assert.True(voided.IsVoided);

        // 越范围付款单：与不存在付款单同一条不披露错误
        var foreignDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.PaymentSummary(foreignPaymentId));
        var missingDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.PaymentSummary(987_654_321L));
        Assert.Equal(missingDenied.Message, foreignDenied.Message);
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.Create(OrderDto(foreignPaymentId, ownOrder2Id, 10m)));
        await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Void(foreignRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "越权作废" }));

        // 已删除付款单：登记与作废一律按不存在拒绝
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.Create(OrderDto(deletionPaymentId, deletionOrderId, 10m)));
        await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Void(deletionRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "已删除作废" }));

        // 证据零变更：被拒行仍为有效、无作废时间
        var storedForeign = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.Id == foreignRow.Id);
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive, storedForeign.Status);
        Assert.Null(storedForeign.VoidedAt);
        var storedDeletion = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.Id == deletionRow.Id);
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive, storedDeletion.Status);
        Assert.Null(storedDeletion.VoidedAt);

        // 台账按客户范围过滤：受限业务员只看到自有范围内付款单的引用行
        var ledger = AssertOkDto<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery()));
        Assert.Equal(2, ledger.Total);
        Assert.All(ledger.Items, i => Assert.Equal(ownPaymentId, i.PaymentId));
    }

    [Fact]
    public async Task 认证控制器_越范围付款单与发票侧读取同一条不披露错误_且范围外证据不泄露()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = SeedSupplier(db, "INT_SP433_S3", "供应商433C");
        var ownName = $"int-sp433-inv-{Guid.NewGuid():N}";
        var salesmanId = SeedSalesmanWithMenu(db, ownName);
        var employeeId = (await db.BaseEmployees.SingleAsync(e => e.EmployeeCode == ownName)).Id;
        var ownCustomerId = SeedCustomer(db, "INT_SP433I_OWN", "自有客户", employeeId);
        var foreignCustomerId = SeedCustomer(db, "INT_SP433I_FGN", "他人客户");
        var ownApplyId = SeedPaymentApply(db, "INT_SP433I_DJ_OWN", ownCustomerId, 200m, Currency.CNY);
        var foreignApplyId = SeedPaymentApply(db, "INT_SP433I_DJ_FGN", foreignCustomerId, 200m, Currency.CNY);
        var ownPaymentId = SeedPayment(db, "INT_SP433I_PAY_OWN", supplierId, 200m, Currency.CNY, DocumentStatus.Approved, ownApplyId);
        var foreignPaymentId = SeedPayment(db, "INT_SP433I_PAY_FGN", supplierId, 200m, Currency.CNY, DocumentStatus.Approved, foreignApplyId);
        var ownInvoiceId = SeedInvoice(db, "INT_SP433I_INV_OWN", supplierId, "CNY");
        var ownInvoice2Id = SeedInvoice(db, "INT_SP433I_INV_OWN2", supplierId, "CNY");
        var foreignInvoiceId = SeedInvoice(db, "INT_SP433I_INV_FGN", supplierId, "CNY");

        var privileged = InvoiceController(db, SeedPrivilegedUserWithMenu(db));
        AssertOkDto<SupplierPaymentInvoiceAllocationDto>(await privileged.Create(InvoiceDto(ownPaymentId, ownInvoiceId, 50m)));
        AssertOkDto<SupplierPaymentInvoiceAllocationDto>(await privileged.Create(InvoiceDto(foreignPaymentId, foreignInvoiceId, 50m)));

        var controller = InvoiceController(db, salesmanId);

        // 自有范围内：登记与发票侧汇总被许可
        var ownNew = AssertOkDto<SupplierPaymentInvoiceAllocationDto>(await controller.Create(InvoiceDto(ownPaymentId, ownInvoice2Id, 20m)));
        Assert.True(ownNew.IsActive);
        Assert.NotNull(AssertOkDto<SupplierPaymentInvoiceAllocationInvoiceSummaryDto>(await controller.InvoiceSummary(ownInvoiceId)));

        // 越范围付款单：与不存在付款单同一条不披露错误
        var foreignDenied = await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(InvoiceDto(foreignPaymentId, foreignInvoiceId, 10m)));
        var missingDenied = await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(InvoiceDto(987_654_321L, foreignInvoiceId, 10m)));
        Assert.Equal(missingDenied.Message, foreignDenied.Message);
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.PaymentSummary(foreignPaymentId));
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.InvoiceCandidates(foreignPaymentId, null));

        // 发票侧：仅被范围内付款单引用的发票对受限账号可见；越范围发票与不存在发票同一错误
        var foreignInvoiceDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.InvoiceSummary(foreignInvoiceId));
        var missingInvoiceDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.InvoiceSummary(987_654_321L));
        Assert.Equal(missingInvoiceDenied.Message, foreignInvoiceDenied.Message);
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.AllocationsForInvoice(foreignInvoiceId));
    }

}

/// <summary>
/// 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并重置为完整 NEWERP 结构 + 种子数据，
/// 供真实 SQL Server 集成测试复用；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class SupplierPaymentLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SUPPLIERPAYMENT_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-351] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

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
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
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
                throw new InvalidOperationException("The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-351] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class SupplierPaymentLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => SupplierPaymentLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}




