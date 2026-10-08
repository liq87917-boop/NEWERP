using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-379 供应商付款单提交 / 审核串行化单元测试（内存库）：验证「提交 / 审核」在付款单行锁内用
/// <strong>实时启用身份</strong>、付款单（payment）菜单授权与权威来源客户数据范围复核，并用既有规则复核持久化
/// 金额 / 币种 / 来源（不新增审批要求）；被拒绝 / 撤销授权 / 停用账号 / 来源非法 / 重复流转的请求不改写状态、
/// 原始字段、删除标记与审计。同时保留 ERP-351 的有效付款引用证据护栏（ERP-049 / ERP-066 共同占用同一同币种额度）、
/// 显式作废释放额度并保留历史，且流转本身不新增任何引用行或资金记账。
/// 全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不做浏览器 / UI 验收。
/// <para>真实 SQL 的两个独立连接竞态（提交 vs 修改、提交 vs 删除、审核 vs 取消）见
/// <c>ERP.IntegrationTests/SupplierPaymentTransitionConcurrencySqlServerTests.cs</c>。</para>
/// </summary>
public class SupplierPaymentTransitionConcurrencyTests
{
    // ==================== 0. 测试脚手架 ====================

    private static FinancePaymentController BuildController(ErpDbContext db)
        => new(db, new FakeDocumentNumberService());

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;
        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
            => Task.FromResult($"FK{DateTime.Now:yyyyMMdd}{++_seq:D4}");
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name, int status = 1)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinancePaymentApply SeedPaymentApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount = 1000m,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Pending,
        long? paymentApplyId = null, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 9, 10),
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "ORIGINAL-BANK",
            Status = status,
            Remark = "原始备注",
            UpdatedAt = new DateTime(2026, 9, 11, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal grossAmount = 1000m,
        string currency = "CNY")
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = "普票",
            InvoiceNumber = number,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
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
        return invoice;
    }

    private static SupplierPaymentAllocationSaveDto OrderDto(long paymentId, long orderId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseOrderId = orderId, AllocatedAmount = amount, Remark = string.Empty };

    private static SupplierPaymentInvoiceAllocationSaveDto InvoiceDto(long paymentId, long invoiceId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseInvoiceId = invoiceId, AllocatedAmount = amount, Remark = string.Empty };

    private static Task<SupplierPaymentAllocationDto> CreateOrderAllocationAsync(
        ErpDbContext db, FinancePayment payment, PurchaseOrder order, decimal amount)
        => SupplierPaymentAllocationService.CreateAsync(db, OrderDto(payment.Id, order.Id, amount));

    private static Task<SupplierPaymentInvoiceAllocationDto> CreateInvoiceAllocationAsync(
        ErpDbContext db, FinancePayment payment, PurchaseInvoice invoice, decimal amount)
        => SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(payment.Id, invoice.Id, amount), "tester");

    private static SysMenu EnsurePaymentMenu(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = SupplierPaymentLifecycleRules.RequiredMenuCode,
            MenuName = SupplierPaymentLifecycleRules.RequiredMenuText,
            Path = "/finance/payment",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个具备既有付款单菜单授权、账号启用的操作员，返回 (用户 Id, 角色 Id, 账号名)。
    /// <paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户），
    /// 为 <c>false</c> 时按既有业务员数据范围口径（<c>BaseEmployee.EmployeeCode</c> 映射）限制客户范围。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedPaymentOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true, bool withMenu = true)
    {
        var role = new SysRole
        {
            RoleName = "付款操作角色",
            RoleCode = $"PaymentOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"payment-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "付款操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withMenu)
        {
            var menu = EnsurePaymentMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return (user.Id, role.Id, userName);
    }

    private static void MapOperatorToEmployee(ErpDbContext db, string userName)
    {
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = userName,
            IsSalesman = true,
            Status = 1
        });
        db.SaveChanges();
    }

    private static void RevokePaymentMenu(ErpDbContext db, long roleId)
    {
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableUser(ErpDbContext db, long userId)
    {
        db.SysUsers.Single(u => u.Id == userId).Status = UserStatus.Disabled;
        db.SaveChanges();
    }

    private static void AssertPaymentUnchanged(ErpDbContext db, FinancePayment expected)
    {
        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.SupplierId, stored.SupplierId);
        Assert.Equal(expected.PaymentApplyId, stored.PaymentApplyId);
        Assert.Equal(expected.Currency, stored.Currency);
        Assert.Equal(expected.Amount, stored.Amount);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.BankAccount, stored.BankAccount);
        Assert.Equal(expected.PaymentDate, stored.PaymentDate);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static DocumentStatus StoredStatus(ErpDbContext db, long paymentId)
        => db.FinancePayments.AsNoTracking().Single(p => p.Id == paymentId).Status;

    private static DateTime? StoredUpdatedAt(ErpDbContext db, long paymentId)
        => db.FinancePayments.AsNoTracking().Single(p => p.Id == paymentId).UpdatedAt;

    private static int ActiveOrderAllocationCount(ErpDbContext db, long paymentId)
        => db.SupplierPaymentAllocations.AsNoTracking()
            .Count(a => a.PaymentId == paymentId && !a.IsDeleted
                        && a.Status == SupplierPaymentAllocationRules.StatusActive);

    private static int ActiveInvoiceAllocationCount(ErpDbContext db, long paymentId)
        => db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Count(a => a.PaymentId == paymentId && !a.IsDeleted
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive);

    // ==================== 1. 实时身份 / 菜单授权 / 权威来源客户范围（锁内复核） ====================

    [Fact]
    public async Task DisabledIdentity_SubmitAndApprove_Denied_AndPaymentUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var pending = SeedPayment(db, "FK-DISABLED-1", supplier.Id);
        var submitted = SeedPayment(db, "FK-DISABLED-2", supplier.Id, status: DocumentStatus.Submitted);

        DisableUser(db, userId);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(pending.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(submitted.Id));

        AssertPaymentUnchanged(db, pending);
        AssertPaymentUnchanged(db, submitted);
    }

    [Fact]
    public async Task RevokedMenu_SubmitApproveCancelDelete_Denied_AndPaymentUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-REVOKED", supplier.Id);
        var submitted = SeedPayment(db, "FK-REVOKED-2", supplier.Id, status: DocumentStatus.Submitted);

        RevokePaymentMenu(db, roleId);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(payment.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(submitted.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(payment.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(payment.Id));

        AssertPaymentUnchanged(db, payment);
        AssertPaymentUnchanged(db, submitted);
    }

    [Fact]
    public async Task ForeignSourceCustomer_Submit_Denied_AndPaymentUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedPaymentOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);

        var supplier = SeedSupplier(db, "S001", "义乌档口");
        // 来源客户不属于当前账号数据范围（fail closed，不泄露归属）
        var foreign = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 9999L);
        var apply = SeedPaymentApply(db, "DJ-FOREIGN", foreign.Id);
        var payment = SeedPayment(db, "FK-FOREIGN", supplier.Id, amount: 500m, paymentApplyId: apply.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(payment.Id));
        AssertPaymentUnchanged(db, payment);
    }

    [Fact]
    public async Task InvalidSource_DanglingApply_Submit_Denied_AndPaymentUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-DANGLING", supplier.Id, amount: 500m, paymentApplyId: 999999L);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Submit(payment.Id));
        AssertPaymentUnchanged(db, payment);
    }

    [Fact]
    public async Task InvalidSource_CancelledOrIncompatible_Approve_Denied_AndPaymentUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var customer = SeedCustomer(db, "C001", "客户A");

        var cancelledApply = SeedPaymentApply(db, "DJ-CANCELLED", customer.Id,
            amount: 1000m, currency: Currency.CNY, status: DocumentStatus.Cancelled);
        var usdApply = SeedPaymentApply(db, "DJ-USD", customer.Id, amount: 1000m, currency: Currency.USD);
        var smallApply = SeedPaymentApply(db, "DJ-SMALL", customer.Id, amount: 100m, currency: Currency.CNY);

        var cancelledSource = SeedPayment(db, "FK-SRC-1", supplier.Id, amount: 500m,
            status: DocumentStatus.Submitted, paymentApplyId: cancelledApply.Id);
        var currencyMismatch = SeedPayment(db, "FK-SRC-2", supplier.Id, amount: 500m,
            status: DocumentStatus.Submitted, paymentApplyId: usdApply.Id);
        var overApply = SeedPayment(db, "FK-SRC-3", supplier.Id, amount: 500m,
            status: DocumentStatus.Submitted, paymentApplyId: smallApply.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(cancelledSource.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(currencyMismatch.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(overApply.Id));

        AssertPaymentUnchanged(db, cancelledSource);
        AssertPaymentUnchanged(db, currencyMismatch);
        AssertPaymentUnchanged(db, overApply);
    }

    [Fact]
    public async Task SourceLessPayment_PrivilegedAllowed_AndRestrictedDenied()
    {
        using var db = TestDbFactory.Create();
        var (privilegedUserId, _, _) = SeedPaymentOperator(db);
        var (restrictedUserId, _, restrictedName) = SeedPaymentOperator(db, privileged: false);
        MapOperatorToEmployee(db, restrictedName);

        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var privileged = SeedPayment(db, "FK-UNLINKED-1", supplier.Id, amount: 500m);
        var restricted = SeedPayment(db, "FK-UNLINKED-2", supplier.Id, amount: 500m);

        var privilegedController = BuildController(db);
        TestAuth.SetUser(privilegedController, privilegedUserId);
        await privilegedController.Submit(privileged.Id);
        Assert.Equal(DocumentStatus.Submitted, StoredStatus(db, privileged.Id));
        await privilegedController.Approve(privileged.Id);
        Assert.Equal(DocumentStatus.Approved, StoredStatus(db, privileged.Id));

        var restrictedController = BuildController(db);
        TestAuth.SetUser(restrictedController, restrictedUserId);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => restrictedController.Submit(restricted.Id));
        AssertPaymentUnchanged(db, restricted);
    }

    // ==================== 2. 失败 / 重复流转保留状态、字段、删除标记与审计 ====================

    [Fact]
    public async Task RepeatTransition_SubmitApprove_SecondAttemptDenied_KeepsAudit()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-REPEAT", supplier.Id, amount: 500m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Submit(payment.Id);
        var afterSubmit = StoredUpdatedAt(db, payment.Id);
        Assert.Equal(DocumentStatus.Submitted, StoredStatus(db, payment.Id));

        // 重复提交被拒绝，状态与审计时间戳都不被改写
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Submit(payment.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredStatus(db, payment.Id));
        Assert.Equal(afterSubmit, StoredUpdatedAt(db, payment.Id));

        await controller.Approve(payment.Id);
        var afterApprove = StoredUpdatedAt(db, payment.Id);
        Assert.Equal(DocumentStatus.Approved, StoredStatus(db, payment.Id));

        // 重复审核被拒绝，状态与审计时间戳都不被改写
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(payment.Id));
        Assert.Equal(DocumentStatus.Approved, StoredStatus(db, payment.Id));
        Assert.Equal(afterApprove, StoredUpdatedAt(db, payment.Id));

        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        Assert.Equal(500m, stored.Amount);
        Assert.Equal("原始备注", stored.Remark);
        Assert.Equal("ORIGINAL-BANK", stored.BankAccount);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task FailedTransition_LeavesStatusDeletionAndAuditUntouched()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");

        var pending = SeedPayment(db, "FK-LOSE-1", supplier.Id, amount: 500m);
        var cancelled = SeedPayment(db, "FK-LOSE-2", supplier.Id, amount: 500m, status: DocumentStatus.Cancelled);
        var deleted = SeedPayment(db, "FK-LOSE-3", supplier.Id, amount: 500m, deleted: true);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        // 未提交不能审核：状态 / 字段 / 删除标记 / 审计保持不变
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(pending.Id));
        AssertPaymentUnchanged(db, pending);

        // 已取消不能重复取消 / 不能提交
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(cancelled.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Submit(cancelled.Id));
        AssertPaymentUnchanged(db, cancelled);

        // 已软删除的付款单不可见（按不存在拒绝），删除标记与审计不被改写
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Submit(deleted.Id));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Approve(deleted.Id));
        AssertPaymentUnchanged(db, deleted);
    }

    // ==================== 3. ERP-351 证据护栏 / 共享额度 / 作废历史 ====================

    [Fact]
    public async Task Cancel_WithActiveOrderEvidence_Refused_ThenVoidReleases_AndFieldsUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-EVIDENCE-1", supplier.Id, amount: 1000m);
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var allocation = await CreateOrderAllocationAsync(db, payment, order, 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(payment.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(payment.Id));
        AssertPaymentUnchanged(db, payment);

        var voided = await SupplierPaymentAllocationService.VoidAsync(db, allocation.Id, "录错");
        Assert.True(voided.IsVoided);

        await controller.Cancel(payment.Id);
        Assert.Equal(DocumentStatus.Cancelled, StoredStatus(db, payment.Id));

        // 作废只保留历史证据：原始分摊金额与原因仍在，绝不物理删除
        var retained = db.SupplierPaymentAllocations.AsNoTracking().Single(a => a.Id == allocation.Id);
        Assert.False(retained.IsDeleted);
        Assert.Equal(300m, retained.AllocatedAmount);
        Assert.Equal(SupplierPaymentAllocationRules.StatusVoided, retained.Status);
        Assert.Equal("录错", retained.VoidReason);
    }

    [Fact]
    public async Task Delete_WithActiveInvoiceEvidence_Refused_ThenVoidReleases_SoftDelete()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-EVIDENCE-2", supplier.Id, amount: 1000m);
        var invoice = SeedInvoice(db, "INV-1", supplier.Id);
        var allocation = await CreateInvoiceAllocationAsync(db, payment, invoice, 200m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(payment.Id));
        Assert.False(db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id).IsDeleted);

        await SupplierPaymentInvoiceAllocationService.VoidAsync(db, allocation.Id, "录错");
        await controller.Delete(payment.Id);

        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        Assert.True(stored.IsDeleted);
        Assert.Equal(1000m, stored.Amount);
        Assert.NotNull(stored.UpdatedAt);

        var retained = db.SupplierPaymentInvoiceAllocations.AsNoTracking().Single(a => a.Id == allocation.Id);
        Assert.False(retained.IsDeleted);
        Assert.Equal(200m, retained.AllocatedAmount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusVoided, retained.Status);
    }

    [Fact]
    public async Task SharedBudget_OrderAndInvoice_OverBudgetRefused_ThenVoidReleases_RetainsHistory()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-BUDGET", supplier.Id, amount: 100m);
        var order = SeedOrder(db, "PO-BUDGET", supplier.Id);
        var invoice = SeedInvoice(db, "INV-BUDGET", supplier.Id);

        // 两套证据共同占用同一同币种额度：订单 80 + 发票 20 = 100（精确）
        Assert.Equal(80m, (await CreateOrderAllocationAsync(db, payment, order, 80m)).AllocatedAmount);
        Assert.Equal(20m, (await CreateInvoiceAllocationAsync(db, payment, invoice, 20m)).AllocatedAmount);

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(80m, funding.OrderAllocated);
        Assert.Equal(20m, funding.InvoiceAllocated);
        Assert.Equal(100m, funding.CombinedAllocated);

        // 已达付款单权威额度：任何维度再加分摊都被拒绝，且不落孤儿引用行
        var order2 = SeedOrder(db, "PO-BUDGET-2", supplier.Id);
        var invoice2 = SeedInvoice(db, "INV-BUDGET-2", supplier.Id);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => CreateOrderAllocationAsync(db, payment, order2, 1m));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => CreateInvoiceAllocationAsync(db, payment, invoice2, 1m));
        Assert.Equal(1, ActiveOrderAllocationCount(db, payment.Id));
        Assert.Equal(1, ActiveInvoiceAllocationCount(db, payment.Id));

        // 显式作废释放额度后才允许新的有效引用（历史行保留，不被物理删除）
        var invoiceRow = db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Single(a => a.PaymentId == payment.Id && !a.IsDeleted);
        await SupplierPaymentInvoiceAllocationService.VoidAsync(db, invoiceRow.Id, "录错");

        Assert.Equal(20m, (await CreateOrderAllocationAsync(db, payment, order2, 20m)).AllocatedAmount);

        var afterVoid = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(100m, afterVoid.CombinedAllocated);
        Assert.Equal(2, ActiveOrderAllocationCount(db, payment.Id));
        Assert.Equal(0, ActiveInvoiceAllocationCount(db, payment.Id));
        Assert.Equal(3, db.SupplierPaymentAllocations.AsNoTracking().Count(a => a.PaymentId == payment.Id && !a.IsDeleted)
            + db.SupplierPaymentInvoiceAllocations.AsNoTracking().Count(a => a.PaymentId == payment.Id && !a.IsDeleted));
    }

    [Fact]
    public async Task SubmitApprove_DoNotTouchEvidenceSnapshotsOrCreatePostings()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedPaymentOperator(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-NOPOST", supplier.Id, amount: 500m);
        var order = SeedOrder(db, "PO-NOPOST", supplier.Id);
        var invoice = SeedInvoice(db, "INV-NOPOST", supplier.Id);

        await CreateOrderAllocationAsync(db, payment, order, 300m);
        await CreateInvoiceAllocationAsync(db, payment, invoice, 200m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);
        await controller.Submit(payment.Id);
        await controller.Approve(payment.Id);

        Assert.Equal(DocumentStatus.Approved, StoredStatus(db, payment.Id));

        // 流转不新增任何引用行、不改写既有证据快照
        Assert.Equal(1, ActiveOrderAllocationCount(db, payment.Id));
        Assert.Equal(1, ActiveInvoiceAllocationCount(db, payment.Id));
        var orderRow = db.SupplierPaymentAllocations.AsNoTracking().Single(a => a.PaymentId == payment.Id);
        Assert.Equal(300m, orderRow.AllocatedAmount);
        Assert.Equal(500m, orderRow.PaymentAmount);
        Assert.Equal((int)DocumentStatus.Pending, orderRow.PaymentStatus);
        var invoiceRow = db.SupplierPaymentInvoiceAllocations.AsNoTracking().Single(a => a.PaymentId == payment.Id);
        Assert.Equal(200m, invoiceRow.AllocatedAmount);
        Assert.Equal(500m, invoiceRow.PaymentAmount);
        Assert.Equal((int)DocumentStatus.Pending, invoiceRow.PaymentStatus);

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(500m, funding.CombinedAllocated);

        // 不改写来源单据，也不产生任何新的记账 / 结算 / 库存流水
        var storedOrder = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, storedOrder.Status);
        Assert.Equal("未结算", storedOrder.SettlementProgress);
        var storedInvoice = db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == invoice.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, storedInvoice.Status);

        Assert.Equal(1, db.SupplierPaymentAllocations.Count());
        Assert.Equal(1, db.SupplierPaymentInvoiceAllocations.Count());
        Assert.Equal(1, db.PurchaseOrders.Count());
        Assert.Equal(1, db.PurchaseInvoices.Count());
        Assert.Equal(0, db.FinanceExpenses.Count());
        Assert.Equal(0, db.StockMovements.Count());
    }
}

