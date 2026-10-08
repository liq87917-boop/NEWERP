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
/// ERP-378 收款单生命周期串行化单元测试（内存库）：验证「修改 / 提交 / 审核 / 取消 / 删除」五个动作在锁内用
/// <strong>实时启用身份</strong>、收款单菜单授权与客户数据范围复核，被拒绝 / 撤销授权 / 停用账号的请求与被并发
/// 抢先的「失败方」不改变状态、原始字段与审计；有效收款分摊证据（ERP-053 / ERP-071）保护取消 / 删除与
/// 客户 / 币种 / 金额变更，显式作废释放限制并保留历史；同一收款单的唯一同币种分摊额度（ERP-350）跨两套消费者
/// 共享，不产生孤儿分摊或超额资金。全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、
/// 不做浏览器 / UI 验收。
/// <para>真实 SQL 的两个独立连接竞态（登记 vs 取消、修改 vs 提交、审核 vs 取消）见
/// <c>ERP.IntegrationTests/ReceiptLifecycleConcurrencySqlServerTests.cs</c>。</para>
/// </summary>
public class ReceiptLifecycleConcurrencyTests
{
    // ==================== 0. 测试脚手架 ====================

    private static FinanceReceiptController BuildController(ErpDbContext db)
        => new(db, new FakeDocumentNumberService());

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;
        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
            => Task.FromResult($"RC{DateTime.Now:yyyyMMdd}{++_seq:D4}");
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, int status = 1, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Pending)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Remark = "原始备注",
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, string currency = "USD", decimal totalAmount = 10000m)
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "客户A",
            Currency = currency,
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = 1,
            AgreementNo = "ASF-2026-001",
            AgreementCurrency = currency,
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = AgencyServiceFeeStatementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 9, 21),
            RecordedBy = "tester"
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private static async Task<CustomerReceiptAllocationDto> CreateCustomerOrderAsync(
        ErpDbContext db, FinanceReceipt receipt, SalesOrder order, decimal amount)
        => await CustomerReceiptAllocationService.CreateAsync(db, new CustomerReceiptAllocationSaveDto
        {
            ReceiptId = receipt.Id,
            SalesOrderId = order.Id,
            AllocatedAmount = amount
        });

    private static async Task<AgencyServiceFeeCollectionAllocationDto> CreateAgencyAsync(
        ErpDbContext db, AgencyServiceFeeStatement statement, FinanceReceipt receipt, decimal amount)
        => await AgencyServiceFeeCollectionAllocationService.CreateAsync(db,
            new AgencyServiceFeeCollectionAllocationSaveDto
            {
                StatementId = statement.Id,
                ReceiptId = receipt.Id,
                AllocatedAmount = amount
            }, "tester");

    private static SysMenu EnsureReceiptMenu(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = CustomerReceiptLifecycleRules.RequiredMenuCode,
            MenuName = CustomerReceiptLifecycleRules.RequiredMenuText,
            Path = "/finance/receipt",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个具备既有收款单菜单授权、账号启用的操作员，返回 (用户 Id, 角色 Id)。
    /// <paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户），
    /// 为 <c>false</c> 时按既有业务员数据范围口径（<c>BaseEmployee.EmployeeCode</c> 映射）限制客户范围。
    /// </summary>
    private static (long UserId, long RoleId) SeedReceiptOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true)
    {
        var role = new SysRole
        {
            RoleName = "收款操作角色",
            RoleCode = $"ReceiptOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"receipt-op-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "收款操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsureReceiptMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return (user.Id, role.Id);
    }

    private static void RevokeReceiptMenu(ErpDbContext db, long roleId)
    {
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static void AssertReceiptUnchanged(ErpDbContext db, FinanceReceipt expected)
    {
        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.Currency, stored.Currency);
        Assert.Equal(expected.Amount, stored.Amount);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.ReceiptDate, stored.ReceiptDate);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. 实时身份 / 菜单授权 / 客户范围（锁内复核） ====================

    [Fact]
    public async Task DisabledIdentity_AllLifecycleActions_Denied_AndReceiptUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedReceipt(db, "RC-DISABLED-1", customer.Id, amount: 1000m);
        var submitted = SeedReceipt(db, "RC-DISABLED-2", customer.Id, amount: 1000m, status: DocumentStatus.Submitted);

        db.SysUsers.Single(u => u.Id == userId).Status = UserStatus.Disabled;
        db.SaveChanges();

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Update(pending.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 500m,
            Currency = Currency.USD,
            Remark = "试图改写"
        }));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(pending.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(submitted.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(pending.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(pending.Id));

        AssertReceiptUnchanged(db, pending);
        AssertReceiptUnchanged(db, submitted);
    }

    [Fact]
    public async Task RevokedMenuGrant_AllLifecycleActions_Denied_AndReceiptUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-REVOKED", customer.Id, amount: 1000m);

        RevokeReceiptMenu(db, roleId);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(receipt.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(receipt.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(receipt.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(receipt.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 1000m,
            Currency = Currency.USD
        }));

        AssertReceiptUnchanged(db, receipt);
    }

    [Fact]
    public async Task OutOfScopeCustomer_Submit_Denied_AndReceiptUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db, privileged: false);
        var foreign = SeedCustomer(db, "C-OUT", "范围外客户", empId: 9999L);
        var receipt = SeedReceipt(db, "RC-OUT", foreign.Id, amount: 1000m);

        // 既有账号名映射到业务员员工：受限制身份且没有任何在范围客户（fail closed，不泄露归属）
        var userName = db.SysUsers.AsNoTracking().Single(u => u.Id == userId).UserName;
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = userName,
            IsSalesman = true,
            Status = 1
        });
        db.SaveChanges();

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(receipt.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(receipt.Id));
        AssertReceiptUnchanged(db, receipt);
    }

    // ==================== 2. 失败方（被抢先 / 状态不允许）保留状态、字段与审计 ====================

    [Fact]
    public async Task LosingMutation_ApproveFromPending_KeepsStatusFieldsAndAudit()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-LOSE", customer.Id, amount: 1000m, status: DocumentStatus.Pending);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        var originalUpdatedAt = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).UpdatedAt;

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(receipt.Id));

        AssertReceiptUnchanged(db, receipt);
        Assert.Equal(originalUpdatedAt, db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).UpdatedAt);
    }

    [Fact]
    public async Task LosingMutation_CancelAfterAlreadyCancelled_KeepsOriginalAudit()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-CANCELLED", customer.Id, amount: 1000m, status: DocumentStatus.Cancelled);
        var originalUpdatedAt = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).UpdatedAt;

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(receipt.Id));

        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        Assert.Equal(originalUpdatedAt, stored.UpdatedAt);
    }

    // ==================== 3. 有效证据保护 + 显式作废释放（证据保留） ====================

    [Fact]
    public async Task Cancel_ActiveCustomerOrderEvidence_Refused_ThenVoidReleasesAndRetainsHistory()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-EVIDENCE-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        var allocation = await CreateCustomerOrderAsync(db, receipt, order, 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(receipt.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(receipt.Id));
        AssertReceiptUnchanged(db, receipt);

        var voided = await CustomerReceiptAllocationService.VoidAsync(db, allocation.Id, "录错");
        Assert.True(voided.IsVoided);

        await controller.Cancel(receipt.Id);
        Assert.Equal(DocumentStatus.Cancelled,
            db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);

        // 作废只保留历史证据：原始分摊金额与原因仍在，绝不物理删除
        var retained = db.CustomerReceiptAllocations.AsNoTracking().Single(a => a.Id == allocation.Id);
        Assert.False(retained.IsDeleted);
        Assert.Equal(300m, retained.AllocatedAmount);
        Assert.Equal(CustomerReceiptAllocationRules.StatusVoided, retained.Status);
        Assert.Equal("录错", retained.VoidReason);
    }

    [Fact]
    public async Task Update_EvidenceBreakingAmount_Refused_ThenAgencyVoidReleases()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-EVIDENCE-2", customer.Id, amount: 1000m);
        var statement = SeedStatement(db, "ASF-1", customer.Id);
        var allocation = await CreateAgencyAsync(db, statement, receipt, 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 500m,
            Currency = Currency.USD,
            Remark = "试图改金额"
        }));
        AssertReceiptUnchanged(db, receipt);

        await AgencyServiceFeeCollectionAllocationService.VoidAsync(db, allocation.Id, "录错");

        await controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 500m,
            Currency = Currency.USD,
            Remark = "作废后修改"
        });

        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(500m, stored.Amount);
        Assert.Equal("作废后修改", stored.Remark);
        Assert.NotNull(stored.UpdatedAt);

        var retained = db.AgencyServiceFeeCollectionAllocations.AsNoTracking().Single(a => a.Id == allocation.Id);
        Assert.False(retained.IsDeleted);
        Assert.Equal(300m, retained.AllocatedAmount);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusVoided, retained.Status);
    }

    [Fact]
    public async Task Update_EvidenceBreakingCustomerOrCurrency_Refused_AndRowUnchanged()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var receipt = SeedReceipt(db, "RC-EVIDENCE-3", customerA.Id, amount: 1000m, currency: Currency.USD);
        var order = SeedOrder(db, "SO-3", customerA.Id, Currency.USD);
        await CreateCustomerOrderAsync(db, receipt, order, 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customerB.Id,
            Amount = 1000m,
            Currency = Currency.USD
        }));
        AssertReceiptUnchanged(db, receipt);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customerA.Id,
            Amount = 1000m,
            Currency = Currency.CNY
        }));
        AssertReceiptUnchanged(db, receipt);

        // 无害元数据修改（不改客户 / 币种 / 金额）仍放行：金额与证据都不变
        await controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customerA.Id,
            Amount = 1000m,
            Currency = Currency.USD,
            Remark = "仅改备注"
        });
        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(1000m, stored.Amount);
        Assert.Equal("仅改备注", stored.Remark);
    }

    [Fact]
    public async Task Delete_ActiveEvidence_Refused_ThenVoidReleasesAndSoftDeletes()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-EVIDENCE-4", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-4", customer.Id);
        var allocation = await CreateCustomerOrderAsync(db, receipt, order, 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(receipt.Id));
        Assert.False(db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).IsDeleted);

        await CustomerReceiptAllocationService.VoidAsync(db, allocation.Id, "录错");
        await controller.Delete(receipt.Id);

        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.True(stored.IsDeleted);
        Assert.Equal(1000m, stored.Amount);
        Assert.NotNull(stored.UpdatedAt);
    }

    // ==================== 4. ERP-350 同一收款单唯一同币种额度（跨两套消费者） ====================

    [Fact]
    public async Task SharedSameCurrencyBudget_TwoConsumers_OverBudgetRefused_ThenVoidReleases()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-BUDGET", customer.Id, amount: 100m, currency: Currency.USD);
        var order = SeedOrder(db, "SO-BUDGET", customer.Id, Currency.USD);
        var statementA = SeedStatement(db, "ASF-A", customer.Id, "USD");
        var statementB = SeedStatement(db, "ASF-B", customer.Id, "USD");

        Assert.Equal(80m, (await CreateCustomerOrderAsync(db, receipt, order, 80m)).AllocatedAmount);
        Assert.Equal(20m, (await CreateAgencyAsync(db, statementA, receipt, 20m)).AllocatedAmount);

        // 已达收款单权威额度：跨对账单再分摊被拒绝，且不落任何孤儿分摊行
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => CreateAgencyAsync(db, statementB, receipt, 10m));

        var funding = await CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db, receipt.Id);
        Assert.Equal(100m, funding.CombinedAllocated);
        Assert.Equal(1, funding.CustomerOrderCount);
        Assert.Equal(1, funding.AgencyCount);

        // 显式作废释放额度后才允许新的有效分摊（历史行保留，不被物理删除）
        var agencyRow = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Single(a => a.ReceiptId == receipt.Id && !a.IsDeleted);
        await AgencyServiceFeeCollectionAllocationService.VoidAsync(db, agencyRow.Id, "录错");

        var released = await CreateAgencyAsync(db, statementB, receipt, 10m);
        Assert.Equal(10m, released.AllocatedAmount);

        var afterVoid = await CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db, receipt.Id);
        Assert.Equal(90m, afterVoid.CombinedAllocated);
        Assert.Equal(1, afterVoid.AgencyCount);
        Assert.Equal(2, db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Count(a => a.ReceiptId == receipt.Id && !a.IsDeleted));
    }

    // ==================== 5. 允许的状态流转与证据阻断 ====================

    [Fact]
    public async Task AllowedTransitions_SubmitApprove_AndCancelBlockedByEvidence()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedReceiptOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-FLOW", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-FLOW", customer.Id);
        var allocation = await CreateCustomerOrderAsync(db, receipt, order, 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Submit(receipt.Id);
        Assert.Equal(DocumentStatus.Submitted,
            db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);

        // 提交后不可再修改；有效证据存在时不可取消
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 1000m,
            Currency = Currency.USD
        }));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(receipt.Id));

        await controller.Approve(receipt.Id);
        Assert.Equal(DocumentStatus.Approved,
            db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);

        // 重复提交（已审核）被拒绝且状态 / 审计不被改写
        var updatedAt = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).UpdatedAt;
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Submit(receipt.Id));
        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(updatedAt, stored.UpdatedAt);

        await CustomerReceiptAllocationService.VoidAsync(db, allocation.Id, "录错");
        await controller.Cancel(receipt.Id);
        Assert.Equal(DocumentStatus.Cancelled,
            db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
    }
}
