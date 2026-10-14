using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-521 代理服务费「对账单共享分摊额度 + 对账单作废 ↔ 收款分摊证据」并发序列化单元测试（内存库）：
/// <list type="number">
/// <item><b>护栏契约</b>：<see cref="AgencyFeeEvidenceConcurrencyRules"/> 的分摊证据锁序文案（对账单证据行 →
/// 收款单行 → 分摊证据行）、收款单行锁 T-SQL 契约、「存在有效分摊证据时禁止作废对账单」的受控错误文案同源，
/// 且不泄露数据库内部细节；非关系型提供程序的对账单行锁等价无操作。</item>
/// <item><b>共享对账单未分摊额度</b>：同一对账单被不同收款单分摊时共用同一额度，登记 / 作废都先取同一把对账单行锁；
/// 超出剩余额度一律受控拒绝且零写入（不静默截断、不改派）。</item>
/// <item><b>对账单作废互斥</b>：对账单仍存在任一有效收款分摊证据时不能作废（表头逐字段不变）；
/// 先走既有作废服务显式作废分摊证据后，对账单才可按既有规则作废，且作废证据的原始金额 / 快照 / 审计全部保留。</item>
/// <item><b>授权与范围</b>：控制器每条路由仍先做实时身份 / 菜单 / 权威客户范围解析；服务在锁内复核权威状态、
/// 客户归属与币种一致性，越权 / 跨客户 / 跨币种一律受控拒绝且不改变任何既有证据。</item>
/// </list>
/// 全部使用内存库（<see cref="TestDbFactory"/>）；不连接 SQL Server、不执行 DDL / 部署脚本，
/// 也不做浏览器 / UI 验收（真实双连接竞态由 ERP-521 的 SQL Server 集成测试覆盖）。
/// </summary>
public class AgencyStatementCollectionConcurrencyTests
{
    // ==================== 1. 护栏契约（纯规则） ====================

    [Fact]
    public void 分摊证据锁序文案_对账单行先于收款单行_且绝不反向获取()
    {
        var order = AgencyFeeEvidenceConcurrencyRules.CollectionAllocationLockOrderText;
        Assert.Contains("ERP-521", order);
        Assert.Contains("AgencyServiceFeeStatements", order);
        Assert.Contains("FinanceReceipts", order);
        Assert.Contains("AgencyServiceFeeCollectionAllocations", order);
        Assert.Contains("绝不反向获取", order);

        // 同一把对账单行锁必须先于收款单行锁（跨收款单共享对账单额度）。
        Assert.True(order.IndexOf("AgencyServiceFeeStatements", StringComparison.Ordinal)
                    < order.IndexOf("FinanceReceipts", StringComparison.Ordinal));

        // 全局锁序文案同源：协议行 → 对账单行 → 收款单行 → 分摊证据行。
        var global = AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText;
        Assert.Contains("ERP-521", global);
        Assert.Contains("FinanceReceipts", global);
        Assert.Contains("绝不反向获取", global);
        Assert.True(global.IndexOf("AgencyServiceFeeStatements", StringComparison.Ordinal)
                    < global.IndexOf("FinanceReceipts", StringComparison.Ordinal));

        // 收款单行锁的等价 T-SQL 契约（与既有收款单行锁实现同源）。
        Assert.Contains("UPDLOCK, HOLDLOCK", AgencyFeeEvidenceConcurrencyRules.ReceiptRowLockSql);
        Assert.Contains("FinanceReceipts", AgencyFeeEvidenceConcurrencyRules.ReceiptRowLockSql);
    }

    [Fact]
    public void 作废拒绝文案_不含数据库内部细节()
    {
        var text = AgencyFeeEvidenceConcurrencyRules.StatementVoidBlockedByActiveAllocationsText;
        Assert.Contains("收款分摊", text);
        Assert.Contains("作废", text);
        Assert.DoesNotContain("SqlException", text);
        Assert.DoesNotContain("UNIQUE", text);
        Assert.DoesNotContain("UX_", text);
        Assert.DoesNotContain("2601", text);
    }

    [Fact]
    public void 纯判定_有效分摊证据存在时受控拒绝_为零时放行()
    {
        // 零条有效分摊证据：放行（不抛错）。
        AgencyFeeEvidenceConcurrencyRules.EnsureNoActiveCollectionAllocations(0, "ASFS-521-NONE");

        var ex = Assert.Throws<BusinessException>(() =>
            AgencyFeeEvidenceConcurrencyRules.EnsureNoActiveCollectionAllocations(2, "ASFS-521-HAS"));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("ASFS-521-HAS", ex.Message);
        Assert.Contains("2 条有效收款分摊证据", ex.Message);
        Assert.Contains("显式作废", ex.Message);
    }

    [Fact]
    public async Task 非关系型_对账单行锁等价无操作_且计数可用()
    {
        using var db = TestDbFactory.Create();

        // 内存库没有行锁语义：对账单行锁帮助方法等价无操作（返回 null）。
        Assert.Null(await AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, 123));
        Assert.Equal(0, await AgencyFeeEvidenceConcurrencyRules.CountActiveCollectionAllocationsAsync(db, 123));
        Assert.Equal(0, await AgencyFeeEvidenceConcurrencyRules.CountActiveCollectionAllocationsAsync(db, 0));

        // 无有效证据时作废前护栏放行。
        await AgencyFeeEvidenceConcurrencyRules.EnsureNoActiveCollectionAllocationsAsync(db, 123, "ASFS-X");
    }

    // ==================== 2. 共享额度与作废互斥（内存库语义） ====================

    [Fact]
    public async Task 对账单存在有效分摊_作废被受控拒绝_且表头与分摊证据逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-521-A", "并发客户A");
        var statement = SeedStatement(db, "ASFS-521-A", customer.Id, totalAmount: 100m);
        var receipt = SeedReceipt(db, "RC-521-A", customer.Id, amount: 200m);

        var created = await AgencyServiceFeeCollectionAllocationService.CreateAsync(
            db, SaveDto(statement.Id, receipt.Id, 60m), "张三");
        Assert.True(created.IsActive);

        var before = Reload(db, statement.Id);
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeStatementService.VoidAsync(db, statement.Id, "对账口径更正"));

        Assert.Contains("ASFS-521-A", ex.Message);
        Assert.Contains("有效收款分摊证据", ex.Message);
        Assert.Contains("显式作废", ex.Message);

        // 被拒请求零变更：对账单表头逐字段不变、有效分摊证据仍为有效且金额未改写。
        var after = Reload(db, statement.Id);
        AssertStatementUnchanged(before, after);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, after.Status);

        var stored = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == created.Id);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusActive, stored.Status);
        Assert.Equal(60m, stored.AllocatedAmount);
        Assert.Null(stored.VoidedAt);
    }

    [Fact]
    public async Task 显式作废分摊后_对账单可作废_作废证据保留原始金额与快照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-521-B", "并发客户B");
        var statement = SeedStatement(db, "ASFS-521-B", customer.Id, totalAmount: 100m);
        var receipt = SeedReceipt(db, "RC-521-B", customer.Id, amount: 200m);

        var created = await AgencyServiceFeeCollectionAllocationService.CreateAsync(
            db, SaveDto(statement.Id, receipt.Id, 60m), "李四");

        // 显式作废分摊证据（保留原始金额 / 快照 / 审计，只改状态）。
        var voided = await AgencyServiceFeeCollectionAllocationService.VoidAsync(db, created.Id, "录错对账单");
        Assert.True(voided.IsVoided);
        Assert.Equal(60m, voided.AllocatedAmount);
        Assert.Equal("ASFS-521-B", voided.StatementNo);
        Assert.Equal("RC-521-B", voided.ReceiptNo);
        Assert.Equal("李四", voided.AllocatedBy);

        // 释放额度后，对账单按既有规则可作废。
        var statementVoided = await AgencyServiceFeeStatementService.VoidAsync(db, statement.Id, "服务范围调整");
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, statementVoided.Status);
        Assert.Equal("服务范围调整", statementVoided.VoidReason);

        // 作废证据不物理删除、原始金额与快照保留可读。
        var stored = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == created.Id);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusVoided, stored.Status);
        Assert.Equal(60m, stored.AllocatedAmount);
        Assert.Equal("ASFS-521-B", stored.StatementNo);
        Assert.Equal("录错对账单", stored.VoidReason);
        Assert.Single(db.AgencyServiceFeeCollectionAllocations);
    }

    [Fact]
    public async Task 同一对账单共享未分摊额度_跨两张收款单扣减_超额被拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-521-C", "共享额度客户");
        var statement = SeedStatement(db, "ASFS-521-C", customer.Id, totalAmount: 100m);
        var first = SeedReceipt(db, "RC-521-C1", customer.Id, amount: 200m);
        var second = SeedReceipt(db, "RC-521-C2", customer.Id, amount: 200m);

        await AgencyServiceFeeCollectionAllocationService.CreateAsync(
            db, SaveDto(statement.Id, first.Id, 60m), "王五");

        // 第二张收款单仍有自己的可分摊余额，但对账单未分摊额只剩 40：必须被受控拒绝。
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeCollectionAllocationService.CreateAsync(
                db, SaveDto(statement.Id, second.Id, 60m), "王五"));
        Assert.Contains("未分摊额", ex.Message);
        Assert.Contains("ASFS-521-C", ex.Message);

        Assert.Single(db.AgencyServiceFeeCollectionAllocations);   // 零部分写入：输家没有落任何分摊行

        // 剩余额度仍可被第二张收款单正常使用（共享额度守恒）。
        var ok = await AgencyServiceFeeCollectionAllocationService.CreateAsync(
            db, SaveDto(statement.Id, second.Id, 40m), "王五");
        Assert.Equal(40m, ok.AllocatedAmount);

        var activeSum = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.StatementId == statement.Id
                        && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .SumAsync(a => a.AllocatedAmount);
        Assert.Equal(100m, activeSum);
    }

    [Fact]
    public async Task 跨客户与跨币种_分摊一律受控拒绝_零写入()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedCustomer(db, "C-521-D", "归属客户");
        var foreign = SeedCustomer(db, "C-521-E", "他人客户");
        var statement = SeedStatement(db, "ASFS-521-D", owner.Id, totalAmount: 100m);
        var foreignReceipt = SeedReceipt(db, "RC-521-D1", foreign.Id, amount: 200m);
        var eurReceipt = SeedReceipt(db, "RC-521-D2", owner.Id, amount: 200m, currency: Currency.EUR);

        var crossCustomer = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeCollectionAllocationService.CreateAsync(
                db, SaveDto(statement.Id, foreignReceipt.Id, 10m), "赵六"));
        Assert.Contains("客户", crossCustomer.Message);
        Assert.Contains("不一致", crossCustomer.Message);

        var crossCurrency = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeCollectionAllocationService.CreateAsync(
                db, SaveDto(statement.Id, eurReceipt.Id, 10m), "赵六"));
        Assert.Contains("币种", crossCurrency.Message);
        Assert.Contains("不一致", crossCurrency.Message);

        Assert.Empty(db.AgencyServiceFeeCollectionAllocations);   // 所有权 / 币种校验先于任何写入
    }

    // ==================== 3. 源码契约（锁序与拒绝顺序） ====================

    [Fact]
    public void 分摊写路由_先取对账单行锁再取收款单行锁_并在同一事务内()
    {
        var service = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "AgencyServiceFeeCollectionAllocationService.cs"));

        // 登记 / 作废都经由共用护栏：同一事务 + 对账单行锁 + 收款单行锁 + 拒绝清理。
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, dto.StatementId)", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, target.StatementId)", service);
        Assert.Contains("CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, dto.ReceiptId)", service);
        Assert.Contains("CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, target.ReceiptId)", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.CommitAsync(transaction)", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync(transaction, db)", service);

        // 旧路径（脚本自带事务 / 只锁收款单行）必须消失。
        Assert.DoesNotContain("db.Database.BeginTransactionAsync", service);
        Assert.Contains("ERP-383", service);   // 既有 ERP-383 锁序审计口径仍保留

        // 固定锁序：对账单行锁必须先于收款单行锁，且两处（登记 / 作废）都成立。
        var statementLockIndex = service.IndexOf(
            "AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, dto.StatementId)", StringComparison.Ordinal);
        var receiptLockIndex = service.IndexOf(
            "CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, dto.ReceiptId)", StringComparison.Ordinal);
        Assert.True(statementLockIndex >= 0 && receiptLockIndex > statementLockIndex);

        var voidStatementLockIndex = service.IndexOf(
            "AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, target.StatementId)", StringComparison.Ordinal);
        var voidReceiptLockIndex = service.IndexOf(
            "CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, target.ReceiptId)", StringComparison.Ordinal);
        Assert.True(voidStatementLockIndex >= 0 && voidReceiptLockIndex > voidStatementLockIndex);
    }

    [Fact]
    public void 对账单作废_在同一把对账单行锁内先判定无有效分摊证据_再改写状态()
    {
        var service = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "AgencyServiceFeeStatementService.cs"));

        var guardIndex = service.IndexOf(
            "AgencyFeeEvidenceConcurrencyRules.EnsureNoActiveCollectionAllocationsAsync", StringComparison.Ordinal);
        var voidStatusIndex = service.IndexOf(
            "statement.Status = AgencyServiceFeeStatementRules.StatusVoided", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "对账单作废必须先判定不存在有效收款分摊证据");
        Assert.True(voidStatusIndex > guardIndex, "拒绝必须发生在任何状态改写之前");

        // 对账单作废只取对账单行锁，不取收款单行锁（避免新锁序反转）。
        Assert.DoesNotContain("CustomerReceiptLifecycleRules.LockReceiptRowAsync", service);
    }

    [Fact]
    public void 控制器写路由_仍先做实时身份菜单与客户范围授权()
    {
        var statements = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers",
            "AgencyServiceFeeStatementController.cs"));
        Assert.Contains("AgencyServiceFeeStatementService.VoidAsync(_db, id, request?.Reason, scope)", statements);
        Assert.DoesNotContain("HttpContext.Items", statements);

        var allocations = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers",
            "AgencyServiceFeeCollectionAllocationController.cs"));
        Assert.Contains("AgencyServiceFeeCollectionAllocationService.CreateAsync(_db, dto, CurrentUserName(), scope)",
            allocations);
        Assert.Contains("AgencyServiceFeeCollectionAllocationService.VoidAsync(_db, id, request?.Reason, scope)",
            allocations);
        Assert.DoesNotContain("HttpContext.Items", allocations);
    }

    // ==================== 4. 测试脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount)
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            Currency = "USD",
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = 1L,
            AgreementNo = "AG-521",
            AgreementCurrency = "USD",
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = AgencyServiceFeeStatementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 9, 21),
            RecordedBy = "张三"
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency = Currency.USD)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static AgencyServiceFeeCollectionAllocationSaveDto SaveDto(
        long statementId, long receiptId, decimal amount)
        => new() { StatementId = statementId, ReceiptId = receiptId, AllocatedAmount = amount, Remark = string.Empty };

    private static AgencyServiceFeeStatement Reload(ErpDbContext db, long id)
        => db.AgencyServiceFeeStatements.AsNoTracking().Single(x => x.Id == id);

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static void AssertStatementUnchanged(
        AgencyServiceFeeStatement expected, AgencyServiceFeeStatement actual)
    {
        Assert.Equal(expected.StatementNo, actual.StatementNo);
        Assert.Equal(expected.NormalizedStatementNo, actual.NormalizedStatementNo);
        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.TotalAmount, actual.TotalAmount);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RecordedAt, actual.RecordedAt);
        Assert.Equal(expected.RecordedBy, actual.RecordedBy);
        Assert.Equal(expected.VoidedAt, actual.VoidedAt);
        Assert.Equal(expected.VoidReason, actual.VoidReason);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.False(actual.IsDeleted);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
