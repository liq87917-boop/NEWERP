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
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-508 收款单 / 定金申请单「标量金额存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：<see cref="FinancePersistedAmountRules"/> 的 <c>DECIMAL(18,2)</c> 边界与实际 EF 列精度同源；
/// 取整**进位后越过单列上限**、超过上限的极大十进制、最小正数、小数位 / 零小数币种（<c>JPY</c>）取整口径
/// 全部保持既有语义（<c>AwayFromZero</c>）；</item>
/// <item>全局 <see cref="CurrencyAmountRules.RoundAmount"/> 与只读权威金额 / 聚合求和口径**不加**单列上限；</item>
/// <item>控制器创建 / 修改：受权账号提交超出单列精度的正金额在**单号预约与任何写入之前**被受控拒绝
/// （零表头写入、零单号消耗），恰好等于上限的金额照常落库；</item>
/// <item>提交 / 审核在既有行锁内复核**已持久化**金额（收款单直接复核，定金申请单经
/// <see cref="FinanceDepositApplyLifecycleRules.EnsurePersistedApplyConsistentAsync"/>），非法历史值被拒绝且
/// 状态 / 字段 / 审计不变；取消等撤销动作**不新增**草稿有效性闸门。</item>
/// </list>
/// 说明：除「实际 EF 列精度」用例以 SQL Server 提供程序**仅构建模型**（不打开连接）外，全部使用内存数据库。
/// </summary>
public class ReceiptDepositPersistedAmountTests
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal HalfUpToMax = 9_999_999_999_999_999.985m;      // USD 半进位后恰好等于上限
    private const decimal JustOver = 9_999_999_999_999_999.999m;         // 取整进位后越过上限
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）

    // ==================== 1. 纯规则：标量边界与全局取整口径 ====================

    [Fact]
    public void 标量金额规则_常量与可表示判定同源()
    {
        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal(9_999_999_999_999_999.99m, FinancePersistedAmountRules.MaxDecimal18Scale2);
        Assert.Equal(-9_999_999_999_999_999.99m, FinancePersistedAmountRules.MinDecimal18Scale2);

        Assert.True(FinancePersistedAmountRules.IsRepresentable(MaxAmount));
        Assert.True(FinancePersistedAmountRules.IsRepresentable(0m));
        Assert.True(FinancePersistedAmountRules.IsRepresentable(-MaxAmount));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(MaxAmount + 0.01m));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(OneE16));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(-OneE16));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(1.234m));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(decimal.MaxValue));

        Assert.Equal(MaxAmount,
            FinancePersistedAmountRules.EnsureRepresentable(MaxAmount, "收款金额按币种精度取整后"));

        var ex = Assert.Throws<BusinessException>(
            () => FinancePersistedAmountRules.EnsureRepresentable(OneE16, "收款金额按币种精度取整后"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Fact]
    public void 全局取整与只读权威金额口径_不施加单列上限()
    {
        // 全局币种取整与只读权威金额（用于历史比对 / 聚合）保持无上限：单列边界只存在于标量守卫。
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(RoundUpCrossing, "USD"));
        Assert.Equal(OneE16, CustomerReceiptLifecycleRules.AuthoritativeReceiptAmount(RoundUpCrossing, "USD"));
        Assert.Equal(OneE16, FinanceDepositApplyLifecycleRules.AuthoritativeApplyAmount(RoundUpCrossing, "CNY"));
        Assert.Equal(decimal.MaxValue, CurrencyAmountRules.RoundAmount(decimal.MaxValue, "USD"));

        // 两个合法上限金额的只读求和（聚合报告口径）合法地超过单个存储列：不得被单列边界限制。
        Assert.Equal(19_999_999_999_999_999.98m, MaxAmount + MaxAmount);
    }

    [Fact]
    public void 收款金额_精确上限与半进位进位_允许()
    {
        Assert.Equal(MaxAmount, CustomerReceiptLifecycleRules.NormalizeReceiptAmount(MaxAmount, "USD"));
        Assert.Equal(MaxAmount, CustomerReceiptLifecycleRules.NormalizeReceiptAmount(HalfUpToMax, "USD"));
        Assert.Equal(100.01m, CustomerReceiptLifecycleRules.NormalizeReceiptAmount(100.005m, "USD"));
        Assert.Equal(0.01m, CustomerReceiptLifecycleRules.NormalizeReceiptAmount(0.005m, "USD"));
    }

    [Fact]
    public void 收款金额_零小数币种取整与最小正数_保持既有口径()
    {
        Assert.Equal(100m, CustomerReceiptLifecycleRules.NormalizeReceiptAmount(100.4m, "JPY"));
        Assert.Equal(101m, CustomerReceiptLifecycleRules.NormalizeReceiptAmount(100.5m, "JPY"));
        Assert.Equal(9_999_999_999_999_999m,
            CustomerReceiptLifecycleRules.NormalizeReceiptAmount(9_999_999_999_999_999.4m, "JPY"));

        var minPositive = Assert.Throws<BusinessException>(
            () => CustomerReceiptLifecycleRules.NormalizeReceiptAmount(0.4m, "JPY"));
        Assert.Equal(ErrorCodes.InvalidParameter, minPositive.Code);

        // 零小数币种取整进位后越过单列上限：同样受控拒绝。
        var crossing = Assert.Throws<BusinessException>(
            () => CustomerReceiptLifecycleRules.NormalizeReceiptAmount(9_999_999_999_999_999.6m, "JPY"));
        Assert.Equal(ErrorCodes.InvalidParameter, crossing.Code);
        Assert.Contains("DECIMAL(18,2)", crossing.Message);
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 收款金额_超出单列上限_受控拒绝(string scenario)
    {
        var ex = Assert.Throws<BusinessException>(
            () => CustomerReceiptLifecycleRules.NormalizeReceiptAmount(OverLimit(scenario), "USD"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 申请金额_超出单列上限_受控拒绝(string scenario)
    {
        var ex = Assert.Throws<BusinessException>(
            () => FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(OverLimit(scenario), "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Fact]
    public void 申请金额_精确上限与零小数币种_同源口径()
    {
        Assert.Equal(MaxAmount, FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(MaxAmount, "CNY"));
        Assert.Equal(MaxAmount, FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(HalfUpToMax, "USD"));
        Assert.Equal(100m, FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(100.4m, "JPY"));
        Assert.Equal(101m, FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(100.5m, "JPY"));
        Assert.Equal(0.01m, FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(0.005m, "USD"));

        var minPositive = Assert.Throws<BusinessException>(
            () => FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(0.004m, "USD"));
        Assert.Equal(ErrorCodes.InvalidParameter, minPositive.Code);
    }

    private static decimal OverLimit(string scenario) => scenario switch
    {
        "just-over" => JustOver,
        "round-up-cross" => RoundUpCrossing,
        "1e16" => OneE16,
        _ => decimal.MaxValue
    };

    // ==================== 2. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP508_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        AssertScalarAmountColumn(db.Model, typeof(FinanceReceipt), nameof(FinanceReceipt.Amount));
        AssertScalarAmountColumn(db.Model, typeof(FinanceDepositApply), nameof(FinanceDepositApply.Amount));
    }

    private static void AssertScalarAmountColumn(IModel model, Type entityType, string propertyName)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        // 金额列**未**显式 HasPrecision：EF SQL Server 提供程序按默认精度给出 decimal(18,2)；
        // 规则常量必须与该实际列类型逐字同源（若将来显式声明精度，本断言会立即失败并要求同步规则常量）。
        Assert.Null(property.GetPrecision());
        Assert.Null(property.GetScale());
        Assert.Equal(
            $"decimal({FinancePersistedAmountRules.DecimalPrecision},{FinancePersistedAmountRules.DecimalScale})",
            property.GetColumnType());
        Assert.Equal("decimal(18,2)", property.GetColumnType());
    }

    // ==================== 3. 控制器：创建 / 修改拒绝（零写入 / 零占号）与精确上限放行 ====================

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建收款单_金额超出存储精度_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-508", "客户508");
        var userId = SeedReceiptOperator(db);
        var numbers = new CountingNumberService();
        var controller = new FinanceReceiptController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(ReceiptDraft(customer.Id, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.FinanceReceipts);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 创建收款单_恰好等于存储上限_照常落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-MAX", "客户MAX");
        var userId = SeedReceiptOperator(db);
        var numbers = new CountingNumberService();
        var controller = new FinanceReceiptController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(ReceiptDraft(customer.Id, MaxAmount)));

        Assert.Equal(1, numbers.Calls);
        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 修改收款单_金额超出存储精度_拒绝且原始字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-UPD", "客户UPD");
        var userId = SeedReceiptOperator(db);
        var receipt = SeedReceipt(db, "RC-UPD", customer.Id, amount: 1000m);
        var before = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        var controller = new FinanceReceiptController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var request = ReceiptDraft(customer.Id, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(receipt.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task 修改收款单_恰好等于存储上限_允许()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-UPD-MAX", "客户UPDMAX");
        var userId = SeedReceiptOperator(db);
        var receipt = SeedReceipt(db, "RC-UPD-MAX", customer.Id, amount: 1000m);
        var controller = new FinanceReceiptController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(
            await controller.Update(receipt.Id, ReceiptDraft(customer.Id, MaxAmount)));
        Assert.Equal(MaxAmount, db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
    }

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建定金申请单_金额超出存储精度_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-DEP-508", "客户定金508");
        var userId = SeedDepositOperator(db);
        var numbers = new CountingNumberService();
        var controller = new FinanceDepositApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(DepositDraft(customer.Id, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.FinanceDepositApplies);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 创建定金申请单_恰好等于存储上限_照常落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-DEP-MAX", "客户定金MAX");
        var userId = SeedDepositOperator(db);
        var numbers = new CountingNumberService();
        var controller = new FinanceDepositApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(DepositDraft(customer.Id, MaxAmount)));

        Assert.Equal(1, numbers.Calls);
        var stored = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == id);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Null(stored.SalesOrderId);
    }

    [Fact]
    public async Task 修改定金申请单_金额超出存储精度_拒绝且原始字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-DEP-UPD", "客户定金UPD");
        var userId = SeedDepositOperator(db);
        var apply = SeedDepositApply(db, "DJ-UPD", customer.Id, amount: 1000m);
        var before = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == apply.Id);
        var controller = new FinanceDepositApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var request = DepositDraft(customer.Id, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(apply.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertApplyUnchanged(db, before);
    }

    // ==================== 4. 提交 / 审核在既有行锁内复核已持久化金额（撤销语义不变） ====================

    [Fact]
    public async Task 收款单提交_已持久化金额超出存储精度_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-SUB", "客户SUB");
        var userId = SeedReceiptOperator(db);
        var receipt = SeedReceipt(db, "RC-SUB", customer.Id, amount: OneE16);
        var before = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        var controller = new FinanceReceiptController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(receipt.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertReceiptUnchanged(db, before);
        Assert.Equal(DocumentStatus.Pending, before.Status);
    }

    [Fact]
    public async Task 收款单审核_已持久化金额超出存储精度_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-APP", "客户APP");
        var userId = SeedReceiptOperator(db);
        var receipt = SeedReceipt(
            db, "RC-APP", customer.Id, amount: RoundUpCrossing, status: DocumentStatus.Submitted);
        var before = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        var controller = new FinanceReceiptController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(receipt.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertReceiptUnchanged(db, before);
        Assert.Equal(DocumentStatus.Submitted, before.Status);
    }

    [Fact]
    public async Task 收款单提交审核_恰好等于存储上限_放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-FLOW", "客户FLOW");
        var userId = SeedReceiptOperator(db);
        var receipt = SeedReceipt(db, "RC-FLOW", customer.Id, amount: MaxAmount);
        var controller = new FinanceReceiptController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(receipt.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(receipt.Id));
        Assert.Equal(DocumentStatus.Approved,
            db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
    }

    [Fact]
    public async Task 收款单取消_已持久化金额超出存储精度_仍按既有撤销语义执行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-CAN", "客户CAN");
        var userId = SeedReceiptOperator(db);
        var receipt = SeedReceipt(db, "RC-CAN", customer.Id, amount: OneE16);
        var controller = new FinanceReceiptController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        // 取消 / 删除等撤销路径不新增草稿有效性闸门：历史（或异常）金额不阻止既有撤销语义。
        Assert.IsType<OkObjectResult>(await controller.Cancel(receipt.Id));
        Assert.Equal(DocumentStatus.Cancelled,
            db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
    }

    [Fact]
    public async Task 定金申请单提交审核_已持久化金额超出存储精度_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-DEP-FLOW", "客户定金FLOW");
        var userId = SeedDepositOperator(db);
        var pending = SeedDepositApply(db, "DJ-SUB", customer.Id, amount: OneE16);
        var submitted = SeedDepositApply(
            db, "DJ-APP", customer.Id, amount: RoundUpCrossing, status: DocumentStatus.Submitted);
        var pendingBefore = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == pending.Id);
        var submittedBefore = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == submitted.Id);
        var controller = new FinanceDepositApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(pending.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, submitEx.Code);
        AssertApplyUnchanged(db, pendingBefore);

        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(submitted.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, approveEx.Code);
        AssertApplyUnchanged(db, submittedBefore);
    }

    [Fact]
    public async Task 定金申请单提交审核_恰好等于存储上限_放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-DEP-OK", "客户定金OK");
        var userId = SeedDepositOperator(db);
        var apply = SeedDepositApply(db, "DJ-OK", customer.Id, amount: MaxAmount);
        var controller = new FinanceDepositApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(apply.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(apply.Id));
        Assert.Equal(DocumentStatus.Approved,
            db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == apply.Id).Status);
    }

    // ==================== 5. 测试脚手架 ====================

    private sealed class CountingNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            return Task.FromResult($"{documentType}-{++_seq:D8}");
        }
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Pending)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 10, 1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "ACC-1",
            Remark = "ERP-508",
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static FinanceDepositApply SeedDepositApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount,
        DocumentStatus status = DocumentStatus.Pending, Currency currency = Currency.CNY)
    {
        var apply = new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 10, 1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            ExchangeRate = 1m,
            BankAccount = "ACC-1",
            Payee = "收款方",
            Reason = "ERP-508",
            Remark = "ERP-508",
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 2, 8, 0, 0)
        };
        db.FinanceDepositApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinanceReceipt ReceiptDraft(long customerId, decimal amount) => new()
    {
        ReceiptDate = new DateTime(2026, 10, 1),
        CustomerId = customerId,
        Amount = amount,
        Currency = Currency.USD,
        PaymentMethod = PaymentMethod.BankTransfer,
        BankAccount = "ACC-1",
        Remark = "ERP-508"
    };

    private static FinanceDepositApply DepositDraft(long customerId, decimal amount) => new()
    {
        ApplyDate = new DateTime(2026, 10, 1),
        CustomerId = customerId,
        Amount = amount,
        Currency = Currency.CNY,
        ExchangeRate = 1m,
        BankAccount = "ACC-1",
        Payee = "收款方",
        Reason = "ERP-508",
        Remark = "ERP-508"
    };

    private static long SeedReceiptOperator(ErpDbContext db)
        => SeedOperator(db, CustomerReceiptLifecycleRules.RequiredMenuCode,
            CustomerReceiptLifecycleRules.RequiredMenuText, "/finance/receipts");

    private static long SeedDepositOperator(ErpDbContext db)
        => SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText, "/finance/deposit-applies");

    /// <summary>播种一个具备指定既有菜单授权的启用账号（系统内置角色 → 可见全部客户）。</summary>
    private static long SeedOperator(ErpDbContext db, string menuCode, string menuText, string path)
    {
        var role = new SysRole
        {
            RoleName = "财务数值边界角色",
            RoleCode = $"FinanceAmount-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"finance-amount-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "财务数值边界操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = menuCode,
            MenuName = menuText,
            Path = path,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static void AssertReceiptUnchanged(ErpDbContext db, FinanceReceipt expected)
    {
        var stored = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.Currency, stored.Currency);
        Assert.Equal(expected.Amount, stored.Amount);
        Assert.Equal(expected.BankAccount, stored.BankAccount);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
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
}
