using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// 货款申请单生命周期护栏（ERP-380）单元测试：覆盖实时启用身份 / 货款申请单（payment-apply）菜单授权 / 客户数据范围、
/// 真实可用客户 / 受支持币种 / 精度取整后的正金额 / 正汇率、可空来源销售订单的精确资格（悬空 / 已取消 / 跨客户 /
/// 币种不兼容一律拒绝，空来源保留历史语义）、未删除且未取消的引用付款单对取消 / 删除 / 商业改动（客户 / 来源 / 币种 /
/// 金额 / 汇率）的拒绝与显式取消付款单后的释放、非商业改动放行、被拒编辑不改写原始字段、以及提交 / 审核的持久化
/// 商业字段与来源复核。全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// <para>真实 SQL 的四类护栏与两个独立连接竞态见
/// <c>ERP.IntegrationTests/FinancePaymentApplyLifecycleSqlServerTests.cs</c>。</para>
/// </summary>
public class FinancePaymentApplyLifecycleTests
{
    // ==================== 0. 测试脚手架 ====================

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗申请单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            return Task.FromResult($"HK{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name, int status = 1)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinancePaymentApply SeedApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.CNY, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Pending, decimal exchangeRate = 1m, bool deleted = false)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 5),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            BankAccount = "ORIGINAL-BANK",
            Payee = "原始收款方",
            Reason = "原始事由",
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 6, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, long? paymentApplyId,
        decimal amount = 500m, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
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
            BankAccount = "BANK",
            Status = status,
            Remark = "付款备注",
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static SysMenu EnsureApplyMenu(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            MenuName = FinancePaymentApplyLifecycleRules.RequiredMenuText,
            Path = "/finance/payment-apply",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个具备既有货款申请单菜单授权、账号启用的操作员，返回 (用户 Id, 角色 Id, 账号名)。
    /// <paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户），
    /// 为 <c>false</c> 时按既有业务员数据范围口径（<c>BaseEmployee.EmployeeCode</c> 映射）限制客户范围。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true, bool withMenu = true)
    {
        var role = new SysRole
        {
            RoleName = "货款申请操作角色",
            RoleCode = $"ApplyOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"apply-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "货款申请操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withMenu)
        {
            var menu = EnsureApplyMenu(db);
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

    private static void RevokeApplyMenu(ErpDbContext db, long roleId)
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

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static void AssertApplyUnchanged(ErpDbContext db, FinancePaymentApply expected)
    {
        var stored = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.SalesOrderId, stored.SalesOrderId);
        Assert.Equal(expected.Currency, stored.Currency);
        Assert.Equal(expected.Amount, stored.Amount);
        Assert.Equal(expected.ExchangeRate, stored.ExchangeRate);
        Assert.Equal(expected.BankAccount, stored.BankAccount);
        Assert.Equal(expected.Payee, stored.Payee);
        Assert.Equal(expected.Reason, stored.Reason);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static FinancePaymentApply StoredApply(ErpDbContext db, long applyId)
        => db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == applyId);

    private static FinancePaymentApplyController BuildController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static FinancePaymentApply NewApplyRequest(
        long customerId, decimal amount = 1000m, Currency currency = Currency.CNY,
        long? salesOrderId = null, decimal exchangeRate = 1m)
        => new()
        {
            ApplyDate = new DateTime(2026, 9, 5),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            SalesOrderId = salesOrderId,
            ExchangeRate = exchangeRate,
            BankAccount = "6222...",
            Payee = "义乌档口",
            Reason = "货款",
            Remark = "请求备注"
        };

    // ==================== 1. 实时身份 / 菜单授权 / 客户数据范围 ====================

    [Fact]
    public async Task 未认证_列表详情创建全部拒绝_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedApply(db, "HK-1", customer.Id);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(NewApplyRequest(customer.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Single(db.FinancePaymentApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 账号停用_列表详情创建修改全部拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedApply(db, "HK-1", customer.Id);
        DisableUser(db, userId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewApplyRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Update(apply.Id, NewApplyRequest(customer.Id, amount: 200m)));

        Assert.Equal(0, no.Calls);
        AssertApplyUnchanged(db, apply);
    }

    [Fact]
    public async Task 撤销菜单授权_列表详情创建取消删除全部拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedApply(db, "HK-1", customer.Id);
        RevokeApplyMenu(db, roleId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewApplyRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(apply.Id));

        Assert.Equal(0, no.Calls);
        AssertApplyUnchanged(db, apply);
    }

    [Fact]
    public async Task 受限制业务员_列表仅返回自己客户_且计数在范围过滤之后()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 9999L);
        var own = SeedApply(db, "HK-OWN", ownCustomer.Id);
        SeedApply(db, "HK-FOREIGN", foreignCustomer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinancePaymentApply>>>(result.Value);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(own.Id, Assert.Single(payload.Data.Items).Id);
    }

    [Fact]
    public async Task 受限制业务员_范围外详情与取消_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 8888L);
        var apply = SeedApply(db, "HK-FOREIGN", foreignCustomer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(apply.Id));
        AssertApplyUnchanged(db, apply);
    }

    // ==================== 2. 创建：客户 / 币种 / 金额 / 汇率 / 来源 ====================

    [Fact]
    public async Task 创建_拒绝不受支持币种_非正金额_非正汇率_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewApplyRequest(customer.Id, currency: (Currency)99)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewApplyRequest(customer.Id, amount: 0m)));
        // 按币种精度取整后为 0（CNY 2 位）→ 拒绝
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewApplyRequest(customer.Id, amount: 0.004m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewApplyRequest(customer.Id, exchangeRate: 0m)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinancePaymentApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_拒绝不可用客户与范围外客户_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 7777L);
        var disabledCustomer = SeedCustomer(db, "C-DISABLED", "已停用客户", empId: 7777L, status: 0);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(NewApplyRequest(999999L)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(NewApplyRequest(disabledCustomer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewApplyRequest(foreignCustomer.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinancePaymentApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_来源销售订单_悬空已删除已取消跨客户币种不兼容一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var otherCustomer = SeedCustomer(db, "C002", "客户B");
        var foreignOrder = SeedSalesOrder(db, "SO-FOREIGN", otherCustomer.Id, Currency.CNY);
        var cancelledOrder = SeedSalesOrder(db, "SO-CANCELLED", customer.Id, Currency.CNY, DocumentStatus.Cancelled);
        var deletedOrder = SeedSalesOrder(db, "SO-DELETED", customer.Id, Currency.CNY, deleted: true);
        var usdOrder = SeedSalesOrder(db, "SO-USD", customer.Id, Currency.USD);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: 999999L)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: deletedOrder.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: cancelledOrder.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: foreignOrder.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: usdOrder.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinancePaymentApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_空来源保留历史语义_匹配来源放行_并按币种精度取整()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id, Currency.CNY);
        var jpyOrder = SeedSalesOrder(db, "SO-JPY", customer.Id, Currency.JPY);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(
            await controller.Create(NewApplyRequest(customer.Id, amount: 123.456m)));
        Assert.IsType<OkObjectResult>(
            await controller.Create(NewApplyRequest(customer.Id, amount: 500m, salesOrderId: order.Id)));
        Assert.IsType<OkObjectResult>(
            await controller.Create(NewApplyRequest(customer.Id, amount: 100.4m, currency: Currency.JPY, salesOrderId: jpyOrder.Id)));

        var stored = db.FinancePaymentApplies.AsNoTracking().OrderBy(a => a.Id).ToList();
        Assert.Equal(3, stored.Count);
        Assert.Null(stored[0].SalesOrderId);
        Assert.Equal(123.46m, stored[0].Amount);
        Assert.Equal(order.Id, stored[1].SalesOrderId);
        Assert.Equal(100m, stored[2].Amount);
        Assert.All(stored, a => Assert.Equal(DocumentStatus.Pending, a.Status));
        Assert.Equal(3, no.Calls);
    }

    // ==================== 3. 修改：商业改动护栏与来源复核 ====================

    [Fact]
    public async Task 修改_仅待提交可改_非待提交拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedApply(db, "HK-1", customer.Id, status: DocumentStatus.Approved);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(apply.Id, NewApplyRequest(customer.Id, amount: 200m)));
        AssertApplyUnchanged(db, apply);
    }

    [Fact]
    public async Task 修改_存在未取消引用付款单_商业改动拒绝且不改写_显式取消付款单后释放()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedApply(db, "HK-1", customer.Id, amount: 1000m);
        var payment = SeedPayment(db, "FK-1", supplier.Id, apply.Id, amount: 500m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        // 金额 / 币种商业改动被拒绝，且原始申请单与付款单引用保持不变
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(apply.Id, NewApplyRequest(customer.Id, amount: 500m)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(apply.Id, NewApplyRequest(customer.Id, currency: Currency.USD, amount: 1000m)));
        AssertApplyUnchanged(db, apply);
        Assert.Equal(apply.Id, db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id).PaymentApplyId);

        // 显式取消付款单释放护栏（历史与审计保留），商业改动随后放行
        payment.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        Assert.IsType<OkObjectResult>(
            await controller.Update(apply.Id, NewApplyRequest(customer.Id, amount: 700m)));
        Assert.Equal(700m, StoredApply(db, apply.Id).Amount);
    }

    [Fact]
    public async Task 修改_已驳回付款单仍视为引用_软删除付款单释放引用()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var applyWithRejected = SeedApply(db, "HK-REJ", customer.Id, amount: 1000m);
        var applyWithDeleted = SeedApply(db, "HK-DEL", customer.Id, amount: 1000m);
        SeedPayment(db, "FK-REJ", supplier.Id, applyWithRejected.Id, amount: 100m, status: DocumentStatus.Rejected);
        SeedPayment(db, "FK-DEL", supplier.Id, applyWithDeleted.Id, amount: 100m, deleted: true);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        // 已驳回 = 未取消 → 仍受护栏保护
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(applyWithRejected.Id, NewApplyRequest(customer.Id, amount: 200m)));
        AssertApplyUnchanged(db, applyWithRejected);

        // 软删除的付款单不再引用本申请单 → 释放护栏
        Assert.IsType<OkObjectResult>(
            await controller.Update(applyWithDeleted.Id, NewApplyRequest(customer.Id, amount: 200m)));
        Assert.Equal(200m, StoredApply(db, applyWithDeleted.Id).Amount);
    }

    [Fact]
    public async Task 修改_非商业改动在存在引用付款单时仍放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedApply(db, "HK-1", customer.Id, amount: 1000m);
        SeedPayment(db, "FK-1", supplier.Id, apply.Id, amount: 500m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var request = NewApplyRequest(customer.Id, amount: 1000m);
        request.ApplyDate = new DateTime(2026, 9, 20);
        request.BankAccount = "NEW-BANK";
        request.Payee = "新收款方";
        request.Reason = "新事由";
        request.Remark = "新备注";

        Assert.IsType<OkObjectResult>(await controller.Update(apply.Id, request));

        var stored = StoredApply(db, apply.Id);
        Assert.Equal("NEW-BANK", stored.BankAccount);
        Assert.Equal("新收款方", stored.Payee);
        Assert.Equal("新事由", stored.Reason);
        Assert.Equal("新备注", stored.Remark);
        Assert.Equal(new DateTime(2026, 9, 20), stored.ApplyDate);
        Assert.Equal(1000m, stored.Amount);
        Assert.Equal(apply.Id, db.FinancePayments.AsNoTracking().Single(p => p.PaymentNo == "FK-1").PaymentApplyId);
    }

    [Fact]
    public async Task 修改_拒绝悬空或跨客户来源与范围外请求客户_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);
        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var otherCustomer = SeedCustomer(db, "C-OTHER", "范围外客户", empId: 7777L);
        var foreignOrder = SeedSalesOrder(db, "SO-FOREIGN", otherCustomer.Id, Currency.CNY);
        var ownOrder = SeedSalesOrder(db, "SO-OWN", ownCustomer.Id, Currency.CNY);
        var apply = SeedApply(db, "HK-1", ownCustomer.Id, amount: 1000m, salesOrderId: ownOrder.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Update(apply.Id, NewApplyRequest(ownCustomer.Id, amount: 1000m, salesOrderId: 999999L)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(apply.Id, NewApplyRequest(ownCustomer.Id, amount: 1000m, salesOrderId: foreignOrder.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(apply.Id, NewApplyRequest(otherCustomer.Id, amount: 1000m, salesOrderId: ownOrder.Id)));

        AssertApplyUnchanged(db, apply);
    }

    // ==================== 4. 提交 / 审核 / 取消 / 删除 ====================

    [Fact]
    public async Task 提交审核_状态流转放行_并在锁内复核持久化商业字段()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id, Currency.CNY);
        var apply = SeedApply(db, "HK-1", customer.Id, amount: 1000m, salesOrderId: order.Id, exchangeRate: 7.1m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(apply.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredApply(db, apply.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(apply.Id));
        Assert.Equal(DocumentStatus.Approved, StoredApply(db, apply.Id).Status);
    }

    [Fact]
    public async Task 提交审核_持久化来源已取消或金额非法_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var cancelledOrder = SeedSalesOrder(db, "SO-CANCELLED", customer.Id, Currency.CNY, DocumentStatus.Cancelled);
        var cancelledSource = SeedApply(db, "HK-SRC", customer.Id, amount: 1000m, salesOrderId: cancelledOrder.Id);
        var zeroAmount = SeedApply(db, "HK-ZERO", customer.Id, amount: 0m);
        var submittedZeroAmount = SeedApply(db, "HK-ZERO-2", customer.Id, amount: 0m, status: DocumentStatus.Submitted);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Submit(cancelledSource.Id));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Submit(zeroAmount.Id));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Approve(submittedZeroAmount.Id));

        AssertApplyUnchanged(db, cancelledSource);
        AssertApplyUnchanged(db, zeroAmount);
        AssertApplyUnchanged(db, submittedZeroAmount);
    }

    [Fact]
    public async Task 取消_有效引用付款单拒绝_显式取消付款单后放行_重复取消拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedApply(db, "HK-1", customer.Id, amount: 1000m);
        var payment = SeedPayment(db, "FK-1", supplier.Id, apply.Id, amount: 500m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(apply.Id));
        AssertApplyUnchanged(db, apply);

        // 显式取消付款单释放护栏，历史与审计保留
        payment.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        Assert.IsType<OkObjectResult>(await controller.Cancel(apply.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredApply(db, apply.Id).Status);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(apply.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredApply(db, apply.Id).Status);
    }

    [Fact]
    public async Task 删除_仅待提交可删_且有效引用付款单拒绝_释放后软删除()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedApply(db, "HK-1", customer.Id, amount: 1000m);
        var approved = SeedApply(db, "HK-APPROVED", customer.Id, status: DocumentStatus.Approved);
        var payment = SeedPayment(db, "FK-1", supplier.Id, apply.Id, amount: 500m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(approved.Id));
        AssertApplyUnchanged(db, apply);
        AssertApplyUnchanged(db, approved);

        payment.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        Assert.IsType<OkObjectResult>(await controller.Delete(apply.Id));
        Assert.True(StoredApply(db, apply.Id).IsDeleted);

        // 已删除的申请单不再可见
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(apply.Id));
    }
}
