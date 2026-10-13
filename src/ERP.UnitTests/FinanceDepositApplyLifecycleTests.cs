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
/// 定金申请单生命周期护栏（ERP-381）单元测试：覆盖实时启用身份 / 定金申请单（deposit-apply）菜单授权 / 客户数据范围、
/// 实时可用客户 / 受支持币种 / 精度取整后的正金额 / 正汇率、可空来源销售订单的精确资格（悬空 / 已取消 / 跨客户 /
/// 币种不兼容一律拒绝，空来源保留历史语义）、被拒创建不消耗单号、被拒编辑 / 流转不改写原始字段（含审计时间戳）、
/// 提交 / 审核的持久化商业字段与来源复核，以及与销售订单取消证据护栏的协同（存在未删除且未取消的定金申请单即拒绝
/// 取消来源销售订单，取消 / 删除后释放）。全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本。
/// <para>真实 SQL 的身份 / 来源 / 金额护栏与两个独立连接竞态见
/// <c>ERP.IntegrationTests/FinanceDepositApplyLifecycleSqlServerTests.cs</c>。</para>
/// </summary>
public class FinanceDepositApplyLifecycleTests
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
            return Task.FromResult($"DJ{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
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
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceDepositApply SeedDepositApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.CNY, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Pending, decimal exchangeRate = 1m, bool deleted = false)
    {
        var apply = new FinanceDepositApply
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
        db.FinanceDepositApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string code, string name, string path)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }
    /// <summary>
    /// 播种一个操作员：<paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户），
    /// 为 <c>false</c> 时按既有业务员数据范围口径（<c>BaseEmployee.EmployeeCode</c> 映射）限制客户范围。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true,
        bool withDepositMenu = true, bool withSalesOrderMenu = false)
    {
        var role = new SysRole
        {
            RoleName = "定金申请操作角色",
            RoleCode = $"DepositOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"deposit-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "定金申请操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withDepositMenu)
            GrantMenu(db, role.Id, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
                FinanceDepositApplyLifecycleRules.RequiredMenuText, "/finance/deposit-apply");
        if (withSalesOrderMenu)
            GrantMenu(db, role.Id, SalesOrderCancellationRules.RequiredMenuCode,
                SalesOrderCancellationRules.RequiredMenuText, "/sales/orders");

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

    private static void AssertApplyUnchanged(ErpDbContext db, FinanceDepositApply expected)
    {
        var stored = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == expected.Id);
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

    private static FinanceDepositApply StoredApply(ErpDbContext db, long applyId)
        => db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == applyId);

    private static FinanceDepositApplyController BuildController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static FinanceDepositApply NewApplyRequest(
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
            Payee = "收款方",
            Reason = "定金",
            Remark = "请求备注"
        };


    // ==================== 1. 实时身份 / 菜单授权 / 客户数据范围 ====================

    [Fact]
    public async Task 未认证_列表详情创建全部拒绝_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(NewApplyRequest(customer.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Single(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 账号停用_列表详情创建修改全部拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);
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
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);
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
        var own = SeedDepositApply(db, "DJ-OWN", ownCustomer.Id);
        SeedDepositApply(db, "DJ-FOREIGN", foreignCustomer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceDepositApply>>>(result.Value);
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
        var apply = SeedDepositApply(db, "DJ-FOREIGN", foreignCustomer.Id);

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
        // 按币种精度取整后为 0（CNY 2 位 → 0.004 → 0.00）→ 拒绝
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewApplyRequest(customer.Id, amount: 0.004m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewApplyRequest(customer.Id, exchangeRate: 0m)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_不可用客户_与越界客户_拒绝且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var deleted = SeedCustomer(db, "C-DEL", "已删除客户", deleted: true);
        var disabled = SeedCustomer(db, "C-DIS", "停用客户", status: 0);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewApplyRequest(deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(disabled.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewApplyRequest(999_999L)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_来源订单_悬空已取消跨客户币种不兼容拒绝_空来源放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var foreign = SeedCustomer(db, "C002", "他客户");
        var cancelled = SeedSalesOrder(db, "SO-CAN", customer.Id, status: DocumentStatus.Cancelled);
        var foreignOrder = SeedSalesOrder(db, "SO-FOR", foreign.Id);
        var usdOrder = SeedSalesOrder(db, "SO-USD", customer.Id, Currency.USD);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: 999_999L)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: cancelled.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: foreignOrder.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewApplyRequest(customer.Id, salesOrderId: usdOrder.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceDepositApplies.AsNoTracking().ToList());

        // 历史「未关联来源」语义原样保留：空来源可创建
        var result = Assert.IsType<OkObjectResult>(
            await controller.Create(NewApplyRequest(customer.Id, salesOrderId: null)));
        Assert.NotNull(result.Value);
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 创建_成功_金额按币种精度取整_并保存()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // CNY 2 位：100.456 → 100.46
        var result = Assert.IsType<OkObjectResult>(await controller.Create(
            NewApplyRequest(customer.Id, amount: 100.456m, salesOrderId: order.Id, exchangeRate: 1.23456789m)));
        Assert.NotNull(result.Value);

        var stored = Assert.Single(db.FinanceDepositApplies.AsNoTracking().ToList());
        Assert.Equal(100.46m, stored.Amount);
        Assert.Equal(1.234568m, stored.ExchangeRate);
        Assert.Equal(Currency.CNY, stored.Currency);
        Assert.Equal(order.Id, stored.SalesOrderId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(1, no.Calls);
        Assert.False(string.IsNullOrWhiteSpace(stored.ApplyNo));

        // JPY 0 位：100.5 → 101（0.5 进位）
        await controller.Create(NewApplyRequest(customer.Id, amount: 100.5m, currency: Currency.JPY));
        var jpy = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Currency == Currency.JPY);
        Assert.Equal(101m, jpy.Amount);
    }


    // ==================== 3. 修改：仅待提交可改 + 锁内既有 / 请求范围复核 ====================

    [Fact]
    public async Task 修改_仅待提交可改_并按币种精度取整()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedDepositApply(db, "DJ-1", customer.Id, amount: 1000m);
        var approved = SeedDepositApply(db, "DJ-APP", customer.Id, status: DocumentStatus.Approved);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(approved.Id, NewApplyRequest(customer.Id, amount: 200m)));
        AssertApplyUnchanged(db, approved);

        Assert.IsType<OkObjectResult>(
            await controller.Update(pending.Id, NewApplyRequest(customer.Id, amount: 123.456m, exchangeRate: 2.5m)));
        var stored = StoredApply(db, pending.Id);
        Assert.Equal(123.46m, stored.Amount);
        Assert.Equal(2.5m, stored.ExchangeRate);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 修改_请求客户越界拒绝_被拒编辑不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 7777L);
        var apply = SeedDepositApply(db, "DJ-OWN", ownCustomer.Id, amount: 1000m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(apply.Id, NewApplyRequest(foreignCustomer.Id, amount: 300m)));
        AssertApplyUnchanged(db, apply);
    }

    [Fact]
    public async Task 修改_来源订单不兼容或已取消拒绝_被拒编辑不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var usdOrder = SeedSalesOrder(db, "SO-USD", customer.Id, Currency.USD);
        var sourceOrder = SeedSalesOrder(db, "SO-1", customer.Id);
        var apply = SeedDepositApply(db, "DJ-1", customer.Id, amount: 1000m, salesOrderId: sourceOrder.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        // 币种不兼容的来源订单：拒绝
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(apply.Id, NewApplyRequest(customer.Id, amount: 300m, salesOrderId: usdOrder.Id)));
        AssertApplyUnchanged(db, apply);

        // 既有来源被并发取消：锁内复核拒绝（绝不基于陈旧来源放行）
        sourceOrder.Status = DocumentStatus.Cancelled;
        db.SaveChanges();
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(apply.Id, NewApplyRequest(customer.Id, amount: 300m, salesOrderId: sourceOrder.Id)));
        AssertApplyUnchanged(db, apply);
    }


    // ==================== 4. 提交 / 审核 / 取消 / 删除 ====================

    [Fact]
    public async Task 提交审核_正常流转_并保留商业字段()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id);
        var apply = SeedDepositApply(db, "DJ-1", customer.Id, amount: 1000m, salesOrderId: order.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(apply.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredApply(db, apply.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(apply.Id));
        var stored = StoredApply(db, apply.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(1000m, stored.Amount);
        Assert.Equal(order.Id, stored.SalesOrderId);
        Assert.Equal(customer.Id, stored.CustomerId);
    }

    [Fact]
    public async Task 提交审核_非法持久化数据拒绝_不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var cancelledOrder = SeedSalesOrder(db, "SO-CAN", customer.Id, status: DocumentStatus.Cancelled);
        var cancelledSource = SeedDepositApply(db, "DJ-SRC", customer.Id, amount: 1000m, salesOrderId: cancelledOrder.Id);
        var zeroAmount = SeedDepositApply(db, "DJ-ZERO", customer.Id, amount: 0m);
        var submittedZeroAmount = SeedDepositApply(
            db, "DJ-ZERO-2", customer.Id, amount: 0m, status: DocumentStatus.Submitted);

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
    public async Task 取消_重复取消拒绝_删除_仅待提交可删()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedDepositApply(db, "DJ-1", customer.Id);
        var approved = SeedDepositApply(db, "DJ-APP", customer.Id, status: DocumentStatus.Approved);
        var deletable = SeedDepositApply(db, "DJ-DEL", customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Cancel(pending.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredApply(db, pending.Id).Status);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(pending.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredApply(db, pending.Id).Status);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(approved.Id));
        AssertApplyUnchanged(db, approved);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(pending.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredApply(db, pending.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Delete(deletable.Id));
        Assert.True(StoredApply(db, deletable.Id).IsDeleted);

        // 已删除的申请单不再可见
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(deletable.Id));
    }

    // ==================== 5. 与销售订单取消证据护栏协同 ====================

    [Fact]
    public async Task 销售订单取消_存在未取消定金申请单拒绝_取消后释放()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db, withSalesOrderMenu: true);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id);
        var apply = SeedDepositApply(db, "DJ-1", customer.Id, salesOrderId: order.Id);

        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(order.Id));
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);

        // 显式取消定金申请单释放护栏，历史与审计原样保留（不物理删除）
        apply.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        Assert.IsType<OkObjectResult>(await controller.Cancel(order.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
        Assert.Single(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 销售订单取消_已删除或非本单定金的申请单不阻断()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db, withSalesOrderMenu: true);
        var customer = SeedCustomer(db, "C001", "客户A");
        var other = SeedCustomer(db, "C002", "他客户");
        var order = SeedSalesOrder(db, "SO-1", customer.Id);
        var otherOrder = SeedSalesOrder(db, "SO-2", other.Id);
        SeedDepositApply(db, "DJ-DEL", customer.Id, salesOrderId: order.Id, deleted: true);
        SeedDepositApply(db, "DJ-OTHER", other.Id, salesOrderId: otherOrder.Id);

        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Cancel(order.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    // ==================== 6. 调用方闸门先于任何业务 / 来源 / 主数据读取与行锁（ERP-381 恢复） ====================

    /// <summary>不存在的申请单 Id（未知 Id 绝不泄露「不存在」，也绝不先读行再判权）。</summary>
    private const long UnknownApplyId = 987654321L;

    private static void MarkUserDeleted(ErpDbContext db, long userId)
    {
        db.SysUsers.Single(u => u.Id == userId).IsDeleted = true;
        db.SaveChanges();
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NEWERP.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    /// <summary>截取 <paramref name="from"/> 签名之后、<paramref name="to"/> 签名之前的源码片段（源码契约断言用）。</summary>
    private static string Segment(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到片段起点：{from}");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"未找到片段终点：{to}");
        return source[start..end];
    }

    /// <summary>
    /// 被拒调用方在<strong>全部</strong>定金申请单路由（列表 / 详情 / 已存储来源 / 来源候选 / 创建 / 修改 / 提交 /
    /// 审核 / 取消 / 删除）上必须得到<strong>完全相同</strong>的受控拒绝码，且未知 Id 与既有 Id 无差异
    /// （绝不先读行 / 锁行再判权、绝不因 Id 是否存在而差异化），被拒请求零单号消耗。
    /// </summary>
    private static async Task AssertAllRoutesRejectedAsync(
        FinanceDepositApplyController controller, FakeDocumentNumberService no,
        long customerId, long existingApplyId, int expectedCode)
    {
        await AssertBusinessCodeAsync(expectedCode, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(expectedCode, () => controller.GetById(existingApplyId));
        await AssertBusinessCodeAsync(expectedCode, () => controller.GetById(UnknownApplyId));
        await AssertBusinessCodeAsync(expectedCode, () => controller.GetStoredSalesOrderSource(existingApplyId));
        await AssertBusinessCodeAsync(expectedCode, () => controller.GetStoredSalesOrderSource(UnknownApplyId));
        await AssertBusinessCodeAsync(expectedCode,
            () => controller.GetSalesOrderCandidates(customerId, null, null, 1, 20));
        await AssertBusinessCodeAsync(expectedCode, () => controller.Create(NewApplyRequest(customerId)));
        await AssertBusinessCodeAsync(expectedCode,
            () => controller.Update(existingApplyId, NewApplyRequest(customerId, amount: 200m)));
        await AssertBusinessCodeAsync(expectedCode,
            () => controller.Update(UnknownApplyId, NewApplyRequest(customerId, amount: 200m)));
        await AssertBusinessCodeAsync(expectedCode, () => controller.Submit(existingApplyId));
        await AssertBusinessCodeAsync(expectedCode, () => controller.Approve(existingApplyId));
        await AssertBusinessCodeAsync(expectedCode, () => controller.Cancel(existingApplyId));
        await AssertBusinessCodeAsync(expectedCode, () => controller.Delete(existingApplyId));

        Assert.Equal(0, no.Calls);
    }

    [Fact]
    public async Task 未认证_全部路由与未知Id_受控未认证_零业务查询零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id);
        var apply = SeedDepositApply(db, "DJ-1", customer.Id, salesOrderId: order.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertAllRoutesRejectedAsync(controller, no, customer.Id, apply.Id, ErrorCodes.Unauthorized);

        AssertApplyUnchanged(db, apply);
        Assert.Single(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 账号已删除_全部路由与未知Id_受控未认证_零业务查询()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);
        MarkUserDeleted(db, userId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertAllRoutesRejectedAsync(controller, no, customer.Id, apply.Id, ErrorCodes.Unauthorized);

        AssertApplyUnchanged(db, apply);
    }

    [Fact]
    public async Task 账号停用_全部路由与未知Id_受控权限不足_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);
        DisableUser(db, userId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertAllRoutesRejectedAsync(controller, no, customer.Id, apply.Id, ErrorCodes.Forbidden);

        AssertApplyUnchanged(db, apply);
        Assert.Single(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 撤销菜单授权_全部路由与未知Id_受控权限不足_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);
        RevokeApplyMenu(db, roleId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertAllRoutesRejectedAsync(controller, no, customer.Id, apply.Id, ErrorCodes.Forbidden);

        AssertApplyUnchanged(db, apply);
        Assert.Single(db.FinanceDepositApplies.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 未绑定请求上下文_纯进程内直调_安全解析为null_受控未认证()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var apply = SeedDepositApply(db, "DJ-1", customer.Id);

        var no = new FakeDocumentNumberService();

        // (a) 完全没有 HttpContext（未进入请求管线）：身份安全解析为 null → 受控未认证，绝无免授权 / 测试兼容豁免
        var withoutHttpContext = BuildController(db, no);
        withoutHttpContext.ControllerContext = new ControllerContext { HttpContext = null! };
        await AssertAllRoutesRejectedAsync(withoutHttpContext, no, customer.Id, apply.Id, ErrorCodes.Unauthorized);
        AssertApplyUnchanged(db, apply);

        // (b) 从未绑定任何 ControllerContext：同一套实时授权，同样受控未认证，绝不抛出基类 User 的空引用
        var untouched = BuildController(db, no);
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => untouched.GetById(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => untouched.GetById(UnknownApplyId));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => untouched.Cancel(apply.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => untouched.Create(NewApplyRequest(customer.Id)));

        Assert.Equal(0, no.Calls);
        AssertApplyUnchanged(db, apply);
    }

    [Fact]
    public void 源码契约_定金控制器调用方闸门先于任何读取与行锁_且不改货款控制器()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceApplyControllers.cs"));
        var paymentClassIndex = controller.IndexOf(
            "class FinancePaymentApplyController", StringComparison.Ordinal);
        Assert.True(paymentClassIndex > 0);

        var deposit = controller[..paymentClassIndex];
        const string gate = "FinanceDepositApplyLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());";

        // 身份只来自已认证请求主体，缺失 / 无 HttpContext 一律安全解析为 null；无请求形状 / 环境变量 / 测试开关探针
        Assert.Contains("private new long? CurrentUserId()", deposit);
        Assert.Contains(
            "ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value", deposit);
        Assert.DoesNotContain("Request.Path", deposit);
        Assert.DoesNotContain("Environment.", deposit);
        Assert.DoesNotContain("IsInMemory", deposit);

        // 详情：闸门先于申请单读取（未知 Id 绝不泄露「不存在」）
        var getById = Segment(deposit,
            "public async Task<IActionResult> GetById(long id)",
            "public async Task<IActionResult> GetSalesOrderCandidates(");
        Assert.Contains(gate, getById);
        Assert.True(getById.IndexOf(gate, StringComparison.Ordinal)
            < getById.IndexOf("Set.AsNoTracking()", StringComparison.Ordinal),
            "详情必须先复核调用方闸门（身份 + 定金申请单菜单）再读取申请单行");

        // 创建：闸门先于客户 / 来源主数据读取与单号生成
        var create = Segment(deposit,
            "public async Task<IActionResult> Create([FromBody] FinanceDepositApply entity)",
            "public async Task<IActionResult> Update(long id, [FromBody] FinanceDepositApply entity)");
        Assert.Contains(gate, create);
        Assert.True(create.IndexOf(gate, StringComparison.Ordinal)
            < create.IndexOf("Db.BaseCustomers.AsNoTracking()", StringComparison.Ordinal),
            "创建必须先复核调用方闸门再读取客户主数据");
        Assert.True(create.IndexOf(gate, StringComparison.Ordinal)
            < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal),
            "创建必须先复核调用方闸门再生成申请单号");

        // 修改 / 取消 / 删除 / 提交审核：闸门先于来源读取、申请单行锁与主数据查询
        var update = Segment(deposit,
            "public async Task<IActionResult> Update(long id, [FromBody] FinanceDepositApply entity)",
            "public override Task<IActionResult> Submit(long id)");
        Assert.Contains(gate, update);
        Assert.True(update.IndexOf(gate, StringComparison.Ordinal)
            < update.IndexOf("ReadSourceSalesOrderIdAsync", StringComparison.Ordinal),
            "修改必须先复核调用方闸门再读取既有来源");
        Assert.True(update.IndexOf(gate, StringComparison.Ordinal)
            < update.IndexOf("Db.BaseCustomers.AsNoTracking()", StringComparison.Ordinal),
            "修改必须先复核调用方闸门再读取请求客户主数据");

        var cancel = Segment(deposit,
            "public override async Task<IActionResult> Cancel(long id)",
            "public override async Task<IActionResult> Delete(long id)");
        Assert.Contains(gate, cancel);
        Assert.True(cancel.IndexOf(gate, StringComparison.Ordinal)
            < cancel.IndexOf("ReadSourceSalesOrderIdAsync", StringComparison.Ordinal),
            "取消必须先复核调用方闸门再读取既有来源");
        Assert.True(cancel.IndexOf(gate, StringComparison.Ordinal)
            < cancel.IndexOf("LockSourceOrderRowsAsync", StringComparison.Ordinal),
            "取消必须先复核调用方闸门再取来源行锁");

        var delete = Segment(deposit,
            "public override async Task<IActionResult> Delete(long id)",
            "private async Task<IActionResult> TransitionAsync(");
        Assert.Contains(gate, delete);
        Assert.True(delete.IndexOf(gate, StringComparison.Ordinal)
            < delete.IndexOf("ReadSourceSalesOrderIdAsync", StringComparison.Ordinal),
            "删除必须先复核调用方闸门再读取既有来源");

        var transition = Segment(deposit,
            "private async Task<IActionResult> TransitionAsync(",
            "private async Task LockSourceOrderRowsAsync(");
        Assert.Contains(gate, transition);
        Assert.True(transition.IndexOf(gate, StringComparison.Ordinal)
            < transition.IndexOf("ReadSourceSalesOrderIdAsync", StringComparison.Ordinal),
            "提交 / 审核必须先复核调用方闸门再读取既有来源");

        // 定金仍保留来源销售订单行锁执行（锁序：先来源订单行、后申请单行）
        Assert.Contains("FinanceDepositApplyLifecycleRules.LockApplyRowAsync", deposit);
        Assert.Contains("LockSourceOrderRowsAsync", deposit);

        // 货款申请单控制器（同一文件）的调用方闸门与「先判权后读取」顺序保持不变
        var payment = controller[paymentClassIndex..];
        Assert.Contains("private new long? CurrentUserId()", payment);
        var paymentGetById = Segment(payment,
            "public async Task<IActionResult> GetById(long id)",
            "public async Task<IActionResult> GetSalesOrderCandidates(");
        Assert.Contains("FinancePaymentApplyLifecycleRules.EnsureMenuAuthorizedAsync", paymentGetById);
        Assert.True(paymentGetById.IndexOf(
                "FinancePaymentApplyLifecycleRules.EnsureMenuAuthorizedAsync", StringComparison.Ordinal)
            < paymentGetById.IndexOf("Set.AsNoTracking()", StringComparison.Ordinal),
            "货款详情仍然先复核调用方闸门再读取申请单行（本次未改动）");
    }
}
