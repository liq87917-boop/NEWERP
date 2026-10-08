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
/// ERP-379 供应商付款单提交 / 审核串行化 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「付款单」菜单授权 + 既有业务员数据范围口径驱动真实
/// <see cref="FinancePaymentController"/> 的提交 / 审核（以及取消 / 删除 / 修改的既有护栏），验证实时启用身份、
/// 权威来源客户范围、持久化金额 / 币种 / 来源复核、允许的状态流转与重复流转拒绝，并在失败时保持原始状态 /
/// 字段 / 删除标记 / 审计不变；同时验证 ERP-351 的有效付款引用证据护栏与显式作废释放；</item>
/// <item><b>两个独立连接竞态</b>：并发「提交 vs 修改」「提交 vs 删除」「审核 vs 取消」经同一把付款单行锁
/// （UPDLOCK/HOLDLOCK 语义）串行化后给出唯一一致的串行化结果：绝不出现陈旧写入把已取消 / 已删除的付款单
/// 复活，也不产生任何新的引用行或资金记账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SupplierPaymentTransitionConcurrencySqlServerTests
    : IClassFixture<SupplierPaymentTransitionConcurrencySqlServerFixture>
{
    private readonly SupplierPaymentTransitionConcurrencySqlServerFixture _fixture;

    public SupplierPaymentTransitionConcurrencySqlServerTests(SupplierPaymentTransitionConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SupplierPaymentTransitionConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinancePaymentController NewController(ErpDbContext db, long? userId)
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

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接 / 事务）。</summary>
    private async Task<(bool Success, string Error)> TryControllerAsync(
        long? userId, Func<FinancePaymentController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接登记「付款单 → 采购订单」引用证据（与控制器生命周期动作竞争同一把付款单行锁）。</summary>
    private async Task<(bool Success, string Error)> TryAllocateOrderAsync(long paymentId, long orderId, decimal amount)
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

    // ==================== 种子助手（EF 生成身份主键） ====================

    private static async Task<BaseSupplier> SeedSupplierAsync(ErpDbContext db, string code, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<FinancePaymentApply> SeedPaymentApplyAsync(
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

    private static async Task<PurchaseOrder> SeedOrderAsync(
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

    private static async Task<PurchaseInvoice> SeedInvoiceAsync(
        ErpDbContext db, string number, long supplierId, string currency = "CNY", decimal grossAmount = 5000m)
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
        await db.SaveChangesAsync();
        return invoice;
    }

    private static SupplierPaymentAllocationSaveDto OrderDto(long paymentId, long orderId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseOrderId = orderId, AllocatedAmount = amount, Remark = string.Empty };

    private static SupplierPaymentInvoiceAllocationSaveDto InvoiceDto(long paymentId, long invoiceId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseInvoiceId = invoiceId, AllocatedAmount = amount, Remark = string.Empty };

    // ==================== 真实身份 / 既有菜单授权 / 数据范围种子 ====================

    /// <summary>
    /// 播种一个真实登录账号：既有 <c>payment</c> 菜单授权（<c>SeedData</c> 种子的菜单，不新增权限模型）+
    /// 业务员员工映射（把「范围内客户」分配给它，数据范围恰好覆盖该客户）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// <paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」（<c>SysRole.IsSystem</c>）口径，
    /// 适用于「付款单未关联货款申请单」的历史场景（此时没有可派生的来源客户，受限制账号 fail closed）。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedPaymentOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withMenu = true, UserStatus status = UserStatus.Enabled,
        bool privileged = false)
    {
        var code = $"ppt-op-{Guid.NewGuid():N}";
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
            RoleName = "付款操作角色",
            RoleCode = $"PptOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return (user.Id, role.Id);
    }

    private static async Task RevokePaymentMenuAsync(ErpDbContext db, long roleId)
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

    /// <summary>只读复核：用一条独立连接读取付款单权威状态（不受被测事务 / 跟踪状态影响）。</summary>
    private async Task<FinancePayment> ReloadPaymentAsync(long paymentId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    /// <summary>只读复核权威引用占用：两套有效行的合计金额与行数。</summary>
    private async Task<(decimal Combined, int Orders, int Invoices)> ActiveFundingAsync(long paymentId)
    {
        await using var db = _fixture.CreateDbContext();
        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, paymentId);
        return (funding.CombinedAllocated, funding.OrderCount, funding.InvoiceCount);
    }

    /// <summary>付款单全部引用行（含已作废历史，排除逻辑删除）条数：用于断言没有孤儿 / 半成品写入。</summary>
    private async Task<int> NonDeletedAllocationRowCountAsync(long paymentId)
    {
        await using var db = _fixture.CreateDbContext();
        var orders = await db.SupplierPaymentAllocations.AsNoTracking()
            .CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted);
        var invoices = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .CountAsync(a => a.PaymentId == paymentId && !a.IsDeleted);
        return orders + invoices;
    }

    /// <summary>既有记账 / 结算 / 库存流水表条数合计：付款单生命周期流转绝不产生任何新的资金记账行。</summary>
    private async Task<int> PostingRowCountAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenses.AsNoTracking().CountAsync()
            + await db.FinanceContainerSettlements.AsNoTracking().CountAsync()
            + await db.FinanceBulkSettlements.AsNoTracking().CountAsync()
            + await db.StockMovements.AsNoTracking().CountAsync();
    }

    /// <summary>每条「请求」等价一条独立连接 / DbContext：断言业务异常代码（失败已在事务内整体回滚）。</summary>
    private async Task AssertControllerDeniedAsync(
        long? userId, int expectedCode, Func<FinancePaymentController, Task> action)
    {
        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action(NewController(db, userId)));
        Assert.Equal(expectedCode, ex.Code);
    }

    /// <summary>每条「请求」等价一条独立连接 / DbContext：执行成功路径动作。</summary>
    private async Task RunControllerAsync(long? userId, Func<FinancePaymentController, Task> action)
    {
        await using var db = _fixture.CreateDbContext();
        await action(NewController(db, userId));
    }

    private async Task<SupplierPaymentAllocationDto> CreateOrderAllocationAsync(
        long paymentId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        return await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(paymentId, orderId, amount));
    }

    private async Task<SupplierPaymentInvoiceAllocationDto> CreateInvoiceAllocationAsync(
        long paymentId, long invoiceId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        return await SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(paymentId, invoiceId, amount), "tester");
    }

    /// <summary>用独立连接走既有显式作废服务释放证据（证据保留，不物理删除）。</summary>
    private async Task VoidAllocationAsync(long allocationId, string reason, bool invoiceSide)
    {
        await using var db = _fixture.CreateDbContext();
        if (invoiceSide)
            await SupplierPaymentInvoiceAllocationService.VoidAsync(db, allocationId, reason);
        else
            await SupplierPaymentAllocationService.VoidAsync(db, allocationId, reason);
    }

    // ==================== 1. 真实控制器：状态流转 / 失败方保留状态与审计 ====================

    [Fact]
    public async Task Controller_SubmitApprove_AndLosingTransitionsKeepAudit()
    {
        Guard();
        long userId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S1", "供应商A");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C1", "客户A");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P1", supplier.Id, 900m, Currency.CNY)).Id;
        }

        var postingBaseline = await PostingRowCountAsync();

        // 未提交不能审核（状态不允许），且不改写状态与审计
        var before = await ReloadPaymentAsync(paymentId);
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Approve(paymentId));
        var refused = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Pending, refused.Status);
        Assert.Equal(before.UpdatedAt, refused.UpdatedAt);

        await RunControllerAsync(userId, ctl => ctl.Submit(paymentId));
        var submitted = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Submitted, submitted.Status);
        Assert.Equal(900m, submitted.Amount);
        Assert.Equal("ORIGINAL-BANK", submitted.BankAccount);

        // 重复提交被拒绝，状态与审计时间戳不被改写
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Submit(paymentId));
        var stillSubmitted = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Submitted, stillSubmitted.Status);
        Assert.Equal(submitted.UpdatedAt, stillSubmitted.UpdatedAt);

        await RunControllerAsync(userId, ctl => ctl.Approve(paymentId));
        var approved = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Approved, approved.Status);
        Assert.Equal(900m, approved.Amount);

        // 重复审核被拒绝，状态与审计时间戳不被改写
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Approve(paymentId));
        var after = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Approved, after.Status);
        Assert.Equal(approved.UpdatedAt, after.UpdatedAt);
        Assert.Equal("原始备注", after.Remark);
        Assert.False(after.IsDeleted);

        // 流转不产生任何新的引用行或资金记账
        Assert.Equal(0, await NonDeletedAllocationRowCountAsync(paymentId));
        Assert.Equal(postingBaseline, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Controller_DisabledAccount_SubmitAndApprove_Denied_AndRowUnchanged()
    {
        Guard();
        long userId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S2", "供应商B");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C2", "客户B");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id)).UserId;
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P2", supplier.Id, 600m, Currency.CNY,
                DocumentStatus.Submitted)).Id;
            await DisableUserAsync(seed, userId);
        }

        var before = await ReloadPaymentAsync(paymentId);

        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Submit(paymentId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Approve(paymentId));

        var after = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Submitted, after.Status);
        Assert.Equal(600m, after.Amount);
        Assert.Equal("ORIGINAL-BANK", after.BankAccount);
        Assert.Equal("原始备注", after.Remark);
        Assert.False(after.IsDeleted);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Controller_RevokedMenu_SubmitAndApprove_Denied_AndRowUnchanged()
    {
        Guard();
        long userId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S3", "供应商C");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C3", "客户C");
            var seeded = await SeedPaymentOperatorAsync(seed, customer.Id);
            userId = seeded.UserId;
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P3", supplier.Id, 700m, Currency.CNY)).Id;
            await RevokePaymentMenuAsync(seed, seeded.RoleId);
        }

        var before = await ReloadPaymentAsync(paymentId);

        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Submit(paymentId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Approve(paymentId));

        var after = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(700m, after.Amount);
        Assert.False(after.IsDeleted);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Controller_ForeignSourceCustomer_Submit_Denied_AndRowUnchanged()
    {
        Guard();
        long userId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S4", "供应商D");
            var inScope = await SeedCustomerAsync(seed, "INT_PPT379_C4", "范围内客户");
            // 来源客户不属于该账号的业务员数据范围（fail closed，不泄露归属）
            var foreign = await SeedCustomerAsync(seed, "INT_PPT379_C4F", "范围外客户");
            userId = (await SeedPaymentOperatorAsync(seed, inScope.Id)).UserId;
            var apply = await SeedPaymentApplyAsync(seed, "INT_PPT379_DJ4F", foreign.Id, 1000m, Currency.CNY);
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P4", supplier.Id, 500m, Currency.CNY,
                DocumentStatus.Pending, apply.Id)).Id;
        }

        var before = await ReloadPaymentAsync(paymentId);
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Submit(paymentId));

        var after = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(500m, after.Amount);
        Assert.False(after.IsDeleted);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Controller_InvalidSource_DanglingOrCancelledOrIncompatible_Denied()
    {
        Guard();
        long userId, danglingId, cancelledId, mismatchId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S5", "供应商E");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C5", "客户E");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id, privileged: true)).UserId;

            // 悬空来源（Id 不存在）：绝不按单号文本或金额猜测来源
            danglingId = (await SeedPaymentAsync(seed, "INT_PPT379_P5A", supplier.Id, 500m, Currency.CNY,
                DocumentStatus.Pending, 999999L)).Id;

            var cancelled = await SeedPaymentApplyAsync(seed, "INT_PPT379_DJ5B", customer.Id, 1000m, Currency.CNY,
                DocumentStatus.Cancelled);
            cancelledId = (await SeedPaymentAsync(seed, "INT_PPT379_P5B", supplier.Id, 500m, Currency.CNY,
                DocumentStatus.Submitted, cancelled.Id)).Id;

            var usd = await SeedPaymentApplyAsync(seed, "INT_PPT379_DJ5C", customer.Id, 1000m, Currency.USD);
            mismatchId = (await SeedPaymentAsync(seed, "INT_PPT379_P5C", supplier.Id, 500m, Currency.CNY,
                DocumentStatus.Submitted, usd.Id)).Id;
        }

        await AssertControllerDeniedAsync(userId, ErrorCodes.NotFound, ctl => ctl.Submit(danglingId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Approve(cancelledId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Approve(mismatchId));

        // 失败的提交 / 审核不改写状态、字段、删除标记与审计
        var dangling = await ReloadPaymentAsync(danglingId);
        Assert.Equal(DocumentStatus.Pending, dangling.Status);
        Assert.Equal(500m, dangling.Amount);
        Assert.False(dangling.IsDeleted);

        var cancelledAfter = await ReloadPaymentAsync(cancelledId);
        Assert.Equal(DocumentStatus.Submitted, cancelledAfter.Status);
        Assert.Equal(500m, cancelledAfter.Amount);

        var mismatchAfter = await ReloadPaymentAsync(mismatchId);
        Assert.Equal(DocumentStatus.Submitted, mismatchAfter.Status);
        Assert.Equal(500m, mismatchAfter.Amount);
    }

    // ==================== 2. ERP-351 证据护栏 / 共享额度 / 显式作废（真实 SQL） ====================

    [Fact]
    public async Task Controller_WithActiveEvidence_CancelRefused_ThenVoidReleases()
    {
        Guard();
        long userId, paymentId;
        SupplierPaymentAllocationDto allocation;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S6", "供应商F");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C6", "客户F");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            var order = await SeedOrderAsync(seed, "INT_PPT379_PO6", supplier.Id);
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P6", supplier.Id, 1000m, Currency.CNY,
                DocumentStatus.Submitted)).Id;
            allocation = await CreateOrderAllocationAsync(paymentId, order.Id, 400m);
        }

        // 有效证据存在：取消 / 删除被拒绝，付款单与证据均不变
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Cancel(paymentId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Delete(paymentId));
        var refused = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Submitted, refused.Status);
        Assert.Equal(1000m, refused.Amount);
        Assert.Equal(400m, (await ActiveFundingAsync(paymentId)).Combined);

        await VoidAllocationAsync(allocation.Id, "录错", invoiceSide: false);

        await RunControllerAsync(userId, ctl => ctl.Cancel(paymentId));
        var cancelled = await ReloadPaymentAsync(paymentId);
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(1000m, cancelled.Amount);
        Assert.Equal(0m, (await ActiveFundingAsync(paymentId)).Combined);

        // 作废保留历史：原始引用金额与原因仍在，绝不物理删除
        await using var db = _fixture.CreateDbContext();
        var retained = await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync(a => a.Id == allocation.Id);
        Assert.False(retained.IsDeleted);
        Assert.Equal(400m, retained.AllocatedAmount);
        Assert.Equal(SupplierPaymentAllocationRules.StatusVoided, retained.Status);
        Assert.Equal("录错", retained.VoidReason);
    }

    [Fact]
    public async Task Controller_SharedSameCurrencyBudget_OverBudgetRefused_NoOrphanRows()
    {
        Guard();
        long paymentId, orderId, order2Id, invoiceId, invoice2Id;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S7", "供应商G");
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P7", supplier.Id, 100m, Currency.CNY)).Id;
            orderId = (await SeedOrderAsync(seed, "INT_PPT379_PO7", supplier.Id)).Id;
            order2Id = (await SeedOrderAsync(seed, "INT_PPT379_PO7B", supplier.Id)).Id;
            invoiceId = (await SeedInvoiceAsync(seed, "INT_PPT379_INV7", supplier.Id)).Id;
            invoice2Id = (await SeedInvoiceAsync(seed, "INT_PPT379_INV7B", supplier.Id)).Id;
        }

        // 两套证据共同占用同一同币种额度：订单 80 + 发票 20 = 100（精确）
        await CreateOrderAllocationAsync(paymentId, orderId, 80m);
        await CreateInvoiceAllocationAsync(paymentId, invoiceId, 20m);

        var funding = await ActiveFundingAsync(paymentId);
        Assert.Equal(100m, funding.Combined);
        Assert.Equal(1, funding.Orders);
        Assert.Equal(1, funding.Invoices);
        Assert.Equal(2, await NonDeletedAllocationRowCountAsync(paymentId));

        // 超额：任何维度再加一行都被拒绝，且不落孤儿引用行
        Assert.False((await TryAllocateOrderAsync(paymentId, order2Id, 1m)).Success);
        Assert.False((await TryAllocateInvoiceAsync(paymentId, invoice2Id, 1m)).Success);

        var afterOver = await ActiveFundingAsync(paymentId);
        Assert.Equal(100m, afterOver.Combined);
        Assert.Equal(2, await NonDeletedAllocationRowCountAsync(paymentId));
    }

    private async Task<(bool Success, string Error)> TryAllocateInvoiceAsync(
        long paymentId, long invoiceId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await SupplierPaymentInvoiceAllocationService.CreateAsync(
                db, InvoiceDto(paymentId, invoiceId, amount), "tester");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 3. 两个独立连接竞态（付款单行锁串行化） ====================

    [Fact]
    public async Task Race_SubmitVersusEdit_SerializedConsistentResult_NoStaleWrite()
    {
        Guard();
        long userId, paymentId, supplierId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S8", "供应商H");
            supplierId = supplier.Id;
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C8", "客户H");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P8", supplier.Id, 1000m, Currency.CNY)).Id;
        }

        // 两条独立连接：提交 vs 修改（各自独立 DbContext / 连接 / 事务）
        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.Submit(paymentId)),
            () => TryControllerAsync(userId, ctl => ctl.Update(paymentId, new FinancePayment
            {
                SupplierId = supplierId,
                PaymentDate = DateTime.Today,
                Amount = 500m,
                Currency = Currency.CNY,
                PaymentMethod = PaymentMethod.BankTransfer,
                BankAccount = "EDITED-BANK",
                Remark = "并发修改"
            })));

        var submit = results[0];
        var edit = results[1];
        Assert.True(submit.Success || edit.Success);

        var stored = await ReloadPaymentAsync(paymentId);

        // 无半成品写入：要么整笔修改生效，要么全部保持原值
        if (edit.Success)
        {
            Assert.Equal(500m, stored.Amount);
            Assert.Equal("EDITED-BANK", stored.BankAccount);
            Assert.Equal("并发修改", stored.Remark);
        }
        else
        {
            Assert.Equal(1000m, stored.Amount);
            Assert.Equal("ORIGINAL-BANK", stored.BankAccount);
            Assert.Equal("原始备注", stored.Remark);
        }

        // 提交结果与最终状态严格一致，状态不会停在提交以外的非法值，也不出现陈旧写入
        Assert.Equal(submit.Success, stored.Status == DocumentStatus.Submitted);
        Assert.True(stored.Status is DocumentStatus.Pending or DocumentStatus.Submitted);
        Assert.False(stored.IsDeleted);
        Assert.Equal(0, await NonDeletedAllocationRowCountAsync(paymentId));
    }

    [Fact]
    public async Task Race_SubmitVersusDelete_SerializedConsistentResult_NoStaleWrite()
    {
        Guard();
        long userId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S9", "供应商I");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C9", "客户I");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P9", supplier.Id, 1000m, Currency.CNY)).Id;
        }

        // 两条独立连接：提交 vs 删除（各自独立 DbContext / 连接 / 事务）
        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.Submit(paymentId)),
            () => TryControllerAsync(userId, ctl => ctl.Delete(paymentId)));

        var submit = results[0];
        var delete = results[1];
        Assert.True(submit.Success || delete.Success);

        var stored = await ReloadPaymentAsync(paymentId);

        // 串行化一致性：提交成功 ⇔ 既未删除且已提交；删除成功 ⇔ 已软删除
        Assert.Equal(submit.Success, stored.Status == DocumentStatus.Submitted && !stored.IsDeleted);
        Assert.Equal(delete.Success, stored.IsDeleted);
        Assert.True(stored.Status is DocumentStatus.Pending or DocumentStatus.Submitted);
        // 绝不出现「已删除 + 已提交」的陈旧写入把已删除付款单复活或反过来删掉已提交流转
        Assert.False(stored.IsDeleted && stored.Status == DocumentStatus.Submitted);
        Assert.Equal(1000m, stored.Amount);
        Assert.Equal("ORIGINAL-BANK", stored.BankAccount);
        Assert.Equal(0, await NonDeletedAllocationRowCountAsync(paymentId));
    }

    [Fact]
    public async Task Race_ApproveVersusCancel_SerializedConsistentResult_NoStaleWrite()
    {
        Guard();
        long userId, paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed, "INT_PPT379_S10", "供应商J");
            var customer = await SeedCustomerAsync(seed, "INT_PPT379_C10", "客户J");
            userId = (await SeedPaymentOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            paymentId = (await SeedPaymentAsync(seed, "INT_PPT379_P10", supplier.Id, 1200m, Currency.CNY,
                DocumentStatus.Submitted)).Id;
        }

        var postingBaseline = await PostingRowCountAsync();

        // 两条独立连接：审核 vs 取消（各自独立 DbContext / 连接 / 事务）
        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.Approve(paymentId)),
            () => TryControllerAsync(userId, ctl => ctl.Cancel(paymentId)));

        var approve = results[0];
        var cancel = results[1];
        Assert.True(approve.Success || cancel.Success);

        var stored = await ReloadPaymentAsync(paymentId);
        Assert.NotEqual(DocumentStatus.Submitted, stored.Status);

        if (cancel.Success)
        {
            // 取消胜出（终态）：审核要么先成功、要么被取消阻断，绝不会出现半成品
            Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        }
        else
        {
            // 取消被行锁串行化击败：审核必须已生效
            Assert.True(approve.Success);
            Assert.Equal(DocumentStatus.Approved, stored.Status);
        }

        // 陈旧写入防护：金额 / 银行账户 / 删除标记均未被流转改写
        Assert.Equal(1200m, stored.Amount);
        Assert.Equal("ORIGINAL-BANK", stored.BankAccount);
        Assert.Equal("原始备注", stored.Remark);
        Assert.False(stored.IsDeleted);
        Assert.Equal(0, await NonDeletedAllocationRowCountAsync(paymentId));
        Assert.Equal(postingBaseline, await PostingRowCountAsync());
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-379）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 供真实 SQL Server 集成测试复用；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// <para>安全口径：任何数据库访问之前先校验实例名 / 库名前缀 / 集成安全；连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class SupplierPaymentTransitionConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SUPPLIERPAYMENTTRANSITION_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-379] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-379] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SupplierPaymentTransitionTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => SupplierPaymentTransitionConcurrencySqlServerFixture.AssertDedicatedTarget(connection));
}
