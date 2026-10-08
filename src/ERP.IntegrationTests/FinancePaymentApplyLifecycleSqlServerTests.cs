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
/// ERP-380 货款申请单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「货款申请单」（<c>payment-apply</c>）菜单授权 + 既有业务员数据范围口径驱动真实
/// <see cref="FinancePaymentApplyController"/>，验证实时启用身份、权威客户范围、币种 / 金额 / 汇率与来源销售订单资格、
/// 未删除且未取消引用付款单对取消 / 删除 / 商业改动的拒绝、显式取消付款单释放限制、被拒编辑不改写原始字段；</item>
/// <item><b>分配合格性</b>：真实 SQL 上验证 <see cref="SupplierPaymentAllocationService"/> 在锁内重新复核来源申请单
/// （已取消 / 孤立的来源绝不产生新的付款引用证据）；</item>
/// <item><b>两个独立连接竞态</b>：并发「申请单取消 vs 付款单创建」「申请单金额改动 vs 付款单创建」经同一把
/// 来源申请单行锁（UPDLOCK/HOLDLOCK 语义）串行化后给出唯一一致结果：绝不出现陈旧来源、超额付款、孤儿引用或
/// 半成品写入，也不产生任何资金记账 / 结算 / 库存流水。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class FinancePaymentApplyLifecycleSqlServerTests
    : IClassFixture<FinancePaymentApplyLifecycleSqlServerFixture>
{
    private readonly FinancePaymentApplyLifecycleSqlServerFixture _fixture;

    public FinancePaymentApplyLifecycleSqlServerTests(FinancePaymentApplyLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinancePaymentApplyLifecycleSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinancePaymentApplyController NewApplyController(ErpDbContext db, long? userId)
    {
        var controller = new FinancePaymentApplyController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static FinancePaymentController NewPaymentController(ErpDbContext db, long? userId)
    {
        var controller = new FinancePaymentController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private async Task<(bool Success, string Error)> TryApplyActionAsync(
        long? userId, Func<FinancePaymentApplyController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewApplyController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryPaymentCreateAsync(
        long? userId, FinancePayment request)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewPaymentController(db, userId).Create(request);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryAllocateOrderAsync(long paymentId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await SupplierPaymentAllocationService.CreateAsync(db, new SupplierPaymentAllocationSaveDto
            {
                PaymentId = paymentId,
                PurchaseOrderId = orderId,
                AllocatedAmount = amount,
                Remark = "ERP-380 集成"
            });
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接在同一起点同时发起动作（各自独立 DbContext / 连接 / 事务）。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    // ==================== 真实身份 / 既有菜单授权 / 数据范围种子 ====================

    private static async Task<BaseSupplier> SeedSupplierAsync(ErpDbContext db, string code, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = 10000m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<FinancePaymentApply> SeedApplyAsync(
        ErpDbContext db, string applyNo, long customerId, decimal amount,
        Currency currency = Currency.CNY, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Approved, decimal exchangeRate = 1m)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            BankAccount = "ORIGINAL-BANK",
            Payee = "原始收款方",
            Reason = "原始事由",
            Remark = "原始备注",
            Status = status
        };
        db.FinancePaymentApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    private static async Task<FinancePayment> SeedPaymentAsync(
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
            BankAccount = "ORIGINAL-BANK",
            Remark = "原始备注",
            Status = status
        };
        db.FinancePayments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<PurchaseOrder> SeedPurchaseOrderAsync(
        ErpDbContext db, string orderNo, long supplierId, Currency currency = Currency.CNY)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = 5000m,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static FinancePayment NewPaymentRequest(long supplierId, long? applyId, decimal amount)
        => new()
        {
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            PaymentApplyId = applyId,
            Amount = amount,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "NEW-BANK",
            Remark = "并发新建"
        };

    private static FinancePaymentApply NewApplyRequest(
        long customerId, decimal amount, long? salesOrderId = null, decimal exchangeRate = 1m)
        => new()
        {
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = Currency.CNY,
            SalesOrderId = salesOrderId,
            ExchangeRate = exchangeRate,
            BankAccount = "REQ-BANK",
            Payee = "请求收款方",
            Reason = "请求事由",
            Remark = "请求备注"
        };

    /// <summary>
    /// 播种一个真实登录账号：既有「货款申请单」（与可选「付款单」）菜单授权 + 业务员员工映射
    /// （把范围内客户分配给它，数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withApplyMenu = true, bool withPaymentMenu = false,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp380-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            DisplayName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "货款申请操作角色",
            RoleCode = $"Erp380Op-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withApplyMenu)
            await GrantMenuAsync(db, role.Id, FinancePaymentApplyLifecycleRules.RequiredMenuCode);
        if (withPaymentMenu)
            await GrantMenuAsync(db, role.Id, SupplierPaymentLifecycleRules.RequiredMenuCode);

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return (user.Id, role.Id);
    }

    /// <summary>授予既有种子菜单（不新增菜单 / 权限模型）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    /// <summary>只读复核：用一条独立连接读取申请单权威行（不受被测事务 / 跟踪状态影响）。</summary>
    private async Task<FinancePaymentApply> ReloadApplyAsync(long applyId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == applyId);
    }

    private async Task<int> PaymentRowCountForApplyAsync(long applyId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinancePayments.AsNoTracking()
            .CountAsync(p => p.PaymentApplyId == applyId && !p.IsDeleted);
    }

    private async Task<int> NonDeletedAllocationRowCountAsync(long paymentId)
    {
        await using var db = _fixture.CreateDbContext();
        var orders = await db.SupplierPaymentAllocations.AsNoTracking()
            .CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted);
        var invoices = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted);
        return orders + invoices;
    }

    /// <summary>既有记账 / 结算表条数合计：本护栏绝不产生任何新的资金记账 / 结算行。</summary>
    private async Task<int> PostingRowCountAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenses.AsNoTracking().CountAsync()
            + await db.FinanceContainerSettlements.AsNoTracking().CountAsync()
            + await db.FinanceBulkSettlements.AsNoTracking().CountAsync();
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 1. 真实身份 / 菜单 / 数据范围（own / foreign / revoked / disabled） ====================

    [Fact]
    public async Task Controller_OwnScopeAllowed_AndForeignScopeRevokedDisabledDenied()
    {
        Guard();
        var tag = Tag();
        long ownerId, foreignOperatorId, disabledOperatorId, revokedOperatorId, revokedRoleId;
        long ownerApplyId, foreignApplyId, disabledApplyId, revokedApplyId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var ownerCustomer = await SeedCustomerAsync(seed, $"INT_E380_OWN_{tag}", "本人客户");
            ownerId = (await SeedOperatorAsync(seed, ownerCustomer.Id)).UserId;
            ownerApplyId = (await SeedApplyAsync(seed, $"INT_E380_AOWN_{tag}", ownerCustomer.Id, 1000m)).Id;

            // 范围外操作员：其客户范围不含 ownerCustomer（申请单属于 ownerCustomer）
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E380_FOR_{tag}", "范围外客户");
            foreignOperatorId = (await SeedOperatorAsync(seed, foreignCustomer.Id)).UserId;
            foreignApplyId = (await SeedApplyAsync(seed, $"INT_E380_AFOR_{tag}", ownerCustomer.Id, 1000m)).Id;

            var disabledCustomer = await SeedCustomerAsync(seed, $"INT_E380_DIS_{tag}", "停用账号客户");
            disabledOperatorId = (await SeedOperatorAsync(seed, disabledCustomer.Id)).UserId;
            disabledApplyId = (await SeedApplyAsync(seed, $"INT_E380_ADIS_{tag}", disabledCustomer.Id, 1000m)).Id;
            await DisableUserAsync(seed, disabledOperatorId);

            var revokedCustomer = await SeedCustomerAsync(seed, $"INT_E380_REV_{tag}", "撤销授权客户");
            (revokedOperatorId, revokedRoleId) = await SeedOperatorAsync(seed, revokedCustomer.Id);
            revokedApplyId = (await SeedApplyAsync(seed, $"INT_E380_AREV_{tag}", revokedCustomer.Id, 1000m)).Id;
            await RevokeMenuAsync(seed, revokedRoleId);
        }

        var disabledBefore = await ReloadApplyAsync(disabledApplyId);
        var revokedBefore = await ReloadApplyAsync(revokedApplyId);

        await using (var db = _fixture.CreateDbContext())
        {
            // own：范围内客户可读、可流转
            Assert.IsType<OkObjectResult>(await NewApplyController(db, ownerId).GetById(ownerApplyId));
            Assert.IsType<OkObjectResult>(await NewApplyController(db, ownerId).Cancel(ownerApplyId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewApplyController(db, foreignOperatorId).GetById(foreignApplyId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewApplyController(db, disabledOperatorId).GetById(disabledApplyId));
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewApplyController(db, revokedOperatorId).GetById(revokedApplyId));
        }

        var disabled = await ReloadApplyAsync(disabledApplyId);
        var revoked = await ReloadApplyAsync(revokedApplyId);
        Assert.Equal(DocumentStatus.Approved, disabled.Status);
        Assert.Equal(disabledBefore.UpdatedAt, disabled.UpdatedAt);
        Assert.Equal(DocumentStatus.Approved, revoked.Status);
        Assert.Equal(revokedBefore.UpdatedAt, revoked.UpdatedAt);
    }

    [Fact]
    public async Task Controller_InvalidCurrencyAmountExchangeRateAndSource_Denied_NoWrite()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, cancelledOrderId, foreignOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E380_CINV_{tag}", "校验客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            cancelledOrderId = (await SeedSalesOrderAsync(
                seed, $"INT_E380_SOC_{tag}", customer.Id, status: DocumentStatus.Cancelled)).Id;
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E380_CFOR_{tag}", "他客户");
            foreignOrderId = (await SeedSalesOrderAsync(seed, $"INT_E380_SOF_{tag}", foreignCustomer.Id)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewApplyController(db, userId);

            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(new FinancePaymentApply
            {
                CustomerId = customerId,
                Amount = 100m,
                Currency = (Currency)77,
                ExchangeRate = 1m,
                ApplyDate = DateTime.Today
            }));
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => controller.Create(NewApplyRequest(customerId, 0.004m)));
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => controller.Create(NewApplyRequest(customerId, 100m, exchangeRate: 0m)));
            await AssertBusinessCodeAsync(ErrorCodes.NotFound,
                () => controller.Create(NewApplyRequest(customerId, 100m, salesOrderId: 99999999L)));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
                () => controller.Create(NewApplyRequest(customerId, 100m, salesOrderId: cancelledOrderId)));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
                () => controller.Create(NewApplyRequest(customerId, 100m, salesOrderId: foreignOrderId)));
        }

        await using (var db = _fixture.CreateDbContext())
            Assert.Equal(0, await db.FinancePaymentApplies.AsNoTracking().CountAsync(a => a.CustomerId == customerId));

        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Controller_ReferencingPaymentBlocksCancelAndCommercialEdit_ThenExplicitCancelledReleases()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, supplierId, applyId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E380_CRP_{tag}", "引用护栏客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            supplierId = (await SeedSupplierAsync(seed, $"INT_E380_SRP_{tag}", "引用护栏供应商")).Id;
            applyId = (await SeedApplyAsync(
                seed, $"INT_E380_ARP_{tag}", customer.Id, 1000m, status: DocumentStatus.Pending)).Id;
            paymentId = (await SeedPaymentAsync(
                seed, $"INT_E380_PRP_{tag}", supplierId, 500m, Currency.CNY, paymentApplyId: applyId)).Id;
        }

        var before = await ReloadApplyAsync(applyId);

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewApplyController(db, userId);
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(applyId));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
                () => controller.Update(applyId, NewApplyRequest(customerId, 300m)));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(applyId));
        }

        // 被拒编辑 / 取消 / 删除都不改写原始申请单（含审计时间戳与删除标记）
        var denied = await ReloadApplyAsync(applyId);
        Assert.Equal(DocumentStatus.Pending, denied.Status);
        Assert.Equal(before.Amount, denied.Amount);
        Assert.Equal(before.CustomerId, denied.CustomerId);
        Assert.Equal(before.UpdatedAt, denied.UpdatedAt);
        Assert.Equal(before.BankAccount, denied.BankAccount);
        Assert.False(denied.IsDeleted);

        // 显式取消付款单释放护栏（历史与审计保留，不物理删除付款单）
        await using (var db = _fixture.CreateDbContext())
        {
            var payment = await db.FinancePayments.SingleAsync(p => p.Id == paymentId);
            payment.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(
                await NewApplyController(db, userId).Update(applyId, NewApplyRequest(customerId, 300m)));
        }

        var released = await ReloadApplyAsync(applyId);
        Assert.Equal(300m, released.Amount);
        Assert.Equal(DocumentStatus.Pending, released.Status);
        Assert.Equal(1, await PaymentRowCountForApplyAsync(applyId));
    }

    // ==================== 2. 分配合格性：锁内重新复核来源申请单 ====================

    [Fact]
    public async Task Allocation_RechecksSourceApplyUnderLock_DeniedWhenSourceCancelled_AndAllowedWhenUsable()
    {
        Guard();
        var tag = Tag();
        long cancelledSourcePaymentId, cancelledSourceOrderId, usablePaymentId, usableOrderId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E380_CAL_{tag}", "分摊客户");
            var supplier = await SeedSupplierAsync(seed, $"INT_E380_SAL_{tag}", "分摊供应商");

            // 直接播种「付款单引用已取消申请单」的既有状态（正常流程被护栏阻止，这里证明分配合格性 fail closed）
            var cancelledApply = await SeedApplyAsync(
                seed, $"INT_E380_ACAN_{tag}", customer.Id, 1000m, status: DocumentStatus.Cancelled);
            cancelledSourcePaymentId = (await SeedPaymentAsync(
                seed, $"INT_E380_PCAN_{tag}", supplier.Id, 500m, Currency.CNY, paymentApplyId: cancelledApply.Id)).Id;
            cancelledSourceOrderId = (await SeedPurchaseOrderAsync(seed, $"INT_E380_POC_{tag}", supplier.Id)).Id;

            var usableApply = await SeedApplyAsync(seed, $"INT_E380_AUSE_{tag}", customer.Id, 1000m);
            usablePaymentId = (await SeedPaymentAsync(
                seed, $"INT_E380_PUSE_{tag}", supplier.Id, 500m, Currency.CNY, paymentApplyId: usableApply.Id)).Id;
            usableOrderId = (await SeedPurchaseOrderAsync(seed, $"INT_E380_POU_{tag}", supplier.Id)).Id;
        }

        var denied = await TryAllocateOrderAsync(cancelledSourcePaymentId, cancelledSourceOrderId, 100m);
        Assert.False(denied.Success);
        Assert.Contains("已取消", denied.Error);
        Assert.Equal(0, await NonDeletedAllocationRowCountAsync(cancelledSourcePaymentId));

        var allowed = await TryAllocateOrderAsync(usablePaymentId, usableOrderId, 100m);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(1, await NonDeletedAllocationRowCountAsync(usablePaymentId));
    }

    // ==================== 3. 两个独立连接竞态（来源申请单行锁串行化） ====================

    [Fact]
    public async Task Race_ApplicationCancel_Versus_PaymentCreate_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, supplierId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E380_RC1_{tag}", "并发客户1");
            userId = (await SeedOperatorAsync(seed, customer.Id, withPaymentMenu: true)).UserId;
            supplierId = (await SeedSupplierAsync(seed, $"INT_E380_RS1_{tag}", "并发供应商1")).Id;
            applyId = (await SeedApplyAsync(seed, $"INT_E380_RA1_{tag}", customer.Id, 1000m)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TryApplyActionAsync(userId, ctl => ctl.Cancel(applyId)),
            () => TryPaymentCreateAsync(userId, NewPaymentRequest(supplierId, applyId, 500m)));

        var cancel = results[0];
        var create = results[1];

        // 同一把来源申请单行锁串行化：恰好一个成功；
        // 绝不出现「已取消申请单 + 新付款单」这种陈旧来源写入，也不会两者皆败留下半成品
        Assert.True(cancel.Success ^ create.Success, $"cancel={cancel.Error} create={create.Error}");

        var applied = await ReloadApplyAsync(applyId);
        var paymentCount = await PaymentRowCountForApplyAsync(applyId);

        if (cancel.Success)
        {
            Assert.Equal(DocumentStatus.Cancelled, applied.Status);
            Assert.Equal(0, paymentCount);
        }
        else
        {
            Assert.Equal(DocumentStatus.Approved, applied.Status);
            Assert.Equal(1, paymentCount);
            await using var db = _fixture.CreateDbContext();
            var payment = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.PaymentApplyId == applyId);
            Assert.Equal(DocumentStatus.Pending, payment.Status);
            Assert.Equal(500m, payment.Amount);
            Assert.Equal(Currency.CNY, payment.Currency);
            Assert.Equal(0, await NonDeletedAllocationRowCountAsync(payment.Id));
        }

        Assert.False(applied.IsDeleted);
        Assert.Equal(1000m, applied.Amount);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Race_ApplicationEditAmount_Versus_PaymentCreate_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, supplierId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E380_RC2_{tag}", "并发客户2");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id, withPaymentMenu: true)).UserId;
            supplierId = (await SeedSupplierAsync(seed, $"INT_E380_RS2_{tag}", "并发供应商2")).Id;
            // 仅待提交状态可修改（既有冻结规则）
            applyId = (await SeedApplyAsync(
                seed, $"INT_E380_RA2_{tag}", customer.Id, 1000m, status: DocumentStatus.Pending)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        // 编辑把申请金额降到 300，付款单创建引用 500：两者只可能串行成功其一
        var results = await RaceAsync(
            () => TryApplyActionAsync(userId, ctl => ctl.Update(applyId, NewApplyRequest(customerId, 300m))),
            () => TryPaymentCreateAsync(userId, NewPaymentRequest(supplierId, applyId, 500m)));

        var edit = results[0];
        var create = results[1];
        Assert.True(edit.Success ^ create.Success, $"edit={edit.Error} create={create.Error}");

        var applied = await ReloadApplyAsync(applyId);
        var paymentCount = await PaymentRowCountForApplyAsync(applyId);

        if (edit.Success)
        {
            // 编辑先赢：金额降到 300；付款单创建在锁内重解析来源后因超额被拒，不留半成品付款单
            Assert.Equal(300m, applied.Amount);
            Assert.Equal(0, paymentCount);
        }
        else
        {
            // 付款单先赢：申请单商业字段保持原值；付款金额绝不超过申请金额，也不产生引用证据
            Assert.Equal(1000m, applied.Amount);
            Assert.Equal(1, paymentCount);
            await using var db = _fixture.CreateDbContext();
            var payment = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.PaymentApplyId == applyId);
            Assert.Equal(500m, payment.Amount);
            Assert.True(payment.Amount <= applied.Amount);
            Assert.Equal(DocumentStatus.Pending, payment.Status);
            Assert.Equal(0, await NonDeletedAllocationRowCountAsync(payment.Id));
        }

        Assert.False(applied.IsDeleted);
        Assert.Equal(DocumentStatus.Pending, applied.Status);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }
}

/// <summary>
/// ERP-380 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class FinancePaymentApplyLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP380";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-380] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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
                throw new InvalidOperationException("The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-380] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinancePaymentApplyLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => FinancePaymentApplyLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}
