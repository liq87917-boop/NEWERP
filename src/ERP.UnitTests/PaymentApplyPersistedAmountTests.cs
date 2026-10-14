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
/// ERP-509 供应商付款单 / 货款申请单「标量金额存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：<see cref="SupplierPaymentLifecycleRules.NormalizePaymentAmount"/> 与
/// <see cref="FinancePaymentApplyLifecycleRules.NormalizeApplyAmount"/> 在既有「按币种精度取整 + 大于 0」之后，
/// 复用 <see cref="FinancePersistedAmountRules"/> 追加 <c>DECIMAL(18,2)</c> 存储边界判定；
/// 取整**进位后越过单列上限**、超过上限的极大十进制、最小正数、小数位 / 零小数币种（<c>JPY</c>）取整口径
/// 全部保持既有语义（<c>AwayFromZero</c>）；</item>
/// <item>全局 <see cref="CurrencyAmountRules.RoundAmount"/> 与只读权威金额 / 聚合求和口径**不加**单列上限；</item>
/// <item>控制器创建 / 修改：受权账号提交超出单列精度的正金额在**单号预约与任何写入之前**被受控拒绝
/// （零表头写入、零单号消耗），恰好等于上限的金额照常落库；</item>
/// <item>提交 / 审核在既有行锁内复核**已持久化**金额（付款单经
/// <see cref="SupplierPaymentLifecycleRules.EnsurePersistedPaymentConsistentAsync"/>，货款申请单经
/// <see cref="FinancePaymentApplyLifecycleRules.EnsurePersistedApplyConsistentAsync"/>），非法历史值被拒绝且
/// 状态 / 字段 / 审计不变；取消等撤销动作**不新增**草稿有效性闸门。</item>
/// </list>
/// 说明：除「实际 EF 列精度」用例以 SQL Server 提供程序**仅构建模型**（不打开连接）外，全部使用内存数据库。
/// </summary>
public class PaymentApplyPersistedAmountTests
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal HalfUpToMax = 9_999_999_999_999_999.985m;      // USD 半进位后恰好等于上限
    private const decimal JustOver = 9_999_999_999_999_999.999m;         // 取整进位后越过上限
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）

    // ==================== 1. 纯规则：标量边界与全局取整口径 ====================

    [Fact]
    public void 付款与申请金额规则_精确上限与半进位放行_零小数币种同源()
    {
        // 付款单：恰好等于上限 / 半进位进位到上限 / 小数位 / 零小数币种全部保持既有语义。
        Assert.Equal(MaxAmount, SupplierPaymentLifecycleRules.NormalizePaymentAmount(MaxAmount, "CNY"));
        Assert.Equal(MaxAmount, SupplierPaymentLifecycleRules.NormalizePaymentAmount(HalfUpToMax, "USD"));
        Assert.Equal(100.01m, SupplierPaymentLifecycleRules.NormalizePaymentAmount(100.005m, "USD"));
        Assert.Equal(0.01m, SupplierPaymentLifecycleRules.NormalizePaymentAmount(0.005m, "USD"));
        Assert.Equal(100m, SupplierPaymentLifecycleRules.NormalizePaymentAmount(100.4m, "JPY"));
        Assert.Equal(101m, SupplierPaymentLifecycleRules.NormalizePaymentAmount(100.5m, "JPY"));
        Assert.Equal(9_999_999_999_999_999m,
            SupplierPaymentLifecycleRules.NormalizePaymentAmount(9_999_999_999_999_999.4m, "JPY"));

        // 货款申请单：与付款单同一权威口径。
        Assert.Equal(MaxAmount, FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(MaxAmount, "CNY"));
        Assert.Equal(MaxAmount, FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(HalfUpToMax, "USD"));
        Assert.Equal(0.01m, FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(0.005m, "USD"));
        Assert.Equal(100m, FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(100.4m, "JPY"));
        Assert.Equal(101m, FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(100.5m, "JPY"));
    }

    [Fact]
    public void 付款与申请金额规则_取整后仍非正_按既有口径拒绝()
    {
        var payment = Assert.Throws<BusinessException>(
            () => SupplierPaymentLifecycleRules.NormalizePaymentAmount(0.004m, "USD"));
        Assert.Equal(ErrorCodes.InvalidParameter, payment.Code);
        Assert.DoesNotContain("DECIMAL(18,2)", payment.Message);

        var apply = Assert.Throws<BusinessException>(
            () => FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(0.4m, "JPY"));
        Assert.Equal(ErrorCodes.InvalidParameter, apply.Code);
        Assert.DoesNotContain("DECIMAL(18,2)", apply.Message);
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 付款金额_超出单列上限_受控拒绝(string scenario)
    {
        var ex = Assert.Throws<BusinessException>(
            () => SupplierPaymentLifecycleRules.NormalizePaymentAmount(OverLimit(scenario), "USD"));
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
            () => FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(OverLimit(scenario), "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Fact]
    public void 零小数币种取整进位越过上限_同样受控拒绝()
    {
        var payment = Assert.Throws<BusinessException>(
            () => SupplierPaymentLifecycleRules.NormalizePaymentAmount(9_999_999_999_999_999.6m, "JPY"));
        Assert.Equal(ErrorCodes.InvalidParameter, payment.Code);
        Assert.Contains("DECIMAL(18,2)", payment.Message);

        var apply = Assert.Throws<BusinessException>(
            () => FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(9_999_999_999_999_999.6m, "JPY"));
        Assert.Equal(ErrorCodes.InvalidParameter, apply.Code);
        Assert.Contains("DECIMAL(18,2)", apply.Message);
    }

    [Fact]
    public void 全局取整与只读权威金额口径_不施加单列上限()
    {
        // 全局币种取整与只读权威金额（用于历史比对 / 聚合）保持无上限：单列边界只存在于标量守卫。
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(RoundUpCrossing, "USD"));
        Assert.Equal(OneE16, SupplierPaymentLifecycleRules.AuthoritativePaymentAmount(RoundUpCrossing, "USD"));
        Assert.Equal(OneE16, FinancePaymentApplyLifecycleRules.AuthoritativeApplyAmount(RoundUpCrossing, "USD"));
        Assert.Equal(decimal.MaxValue, CurrencyAmountRules.RoundAmount(decimal.MaxValue, "USD"));

        // 两个合法上限金额的只读求和（聚合报告口径）合法地超过单个存储列：不得被单列边界限制。
        Assert.Equal(19_999_999_999_999_999.98m, MaxAmount + MaxAmount);
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
                + "Database=NEWERP_AUTOTEST_ERP509_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        AssertScalarAmountColumn(db.Model, typeof(FinancePayment), nameof(FinancePayment.Amount));
        AssertScalarAmountColumn(db.Model, typeof(FinancePaymentApply), nameof(FinancePaymentApply.Amount));
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

    // ==================== 3. 控制器：付款单创建 / 修改拒绝（零写入 / 零占号）与精确上限放行 ====================

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建付款单_金额超出存储精度_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-509", "供应商509");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(PaymentDraft(supplier.Id, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Empty(db.FinancePayments);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 创建付款单_恰好等于存储上限_照常落库()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-MAX", "供应商MAX");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(PaymentDraft(supplier.Id, MaxAmount)));

        Assert.Equal(1, numbers.Calls);
        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == id);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.PaymentApplyId);
    }

    [Fact]
    public async Task 创建关联来源的付款单_边界金额不越来源容量_放行且保留来源链接()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-LINK", "客户LINK");
        var supplier = SeedSupplier(db, "S-LINK", "供应商LINK");
        var apply = SeedApply(db, "HK-LINK", customer.Id, amount: MaxAmount, status: DocumentStatus.Approved);
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var draft = PaymentDraft(supplier.Id, MaxAmount);
        draft.PaymentApplyId = apply.Id;
        var id = CreatedId(await controller.Create(draft));

        // 存储边界与来源容量（付款金额不得超过申请金额、币种一致）原样保留：恰好等于上限且等于申请金额照常放行。
        Assert.Equal(1, numbers.Calls);
        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == id);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(apply.Id, stored.PaymentApplyId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 创建关联来源的付款单_金额超出存储精度_在来源校验前拒绝且零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-LINK-OVF", "客户LINKOVF");
        var supplier = SeedSupplier(db, "S-LINK-OVF", "供应商LINKOVF");
        var apply = SeedApply(db, "HK-LINK-OVF", customer.Id, amount: MaxAmount, status: DocumentStatus.Approved);
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var draft = PaymentDraft(supplier.Id, RoundUpCrossing);
        draft.PaymentApplyId = apply.Id;
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(draft));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Empty(db.FinancePayments);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 修改付款单_金额超出存储精度_拒绝且原字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-UPD", "供应商UPD");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var payment = SeedPayment(db, "FK-UPD", supplier.Id, amount: 1000m);
        var before = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        var controller = new FinancePaymentController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var request = PaymentDraft(supplier.Id, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(payment.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertPaymentUnchanged(db, before);
    }

    [Fact]
    public async Task 修改付款单_恰好等于存储上限_放行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-UPD-MAX", "供应商UPDMAX");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var payment = SeedPayment(db, "FK-UPD-MAX", supplier.Id, amount: 1000m);
        var controller = new FinancePaymentController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(
            await controller.Update(payment.Id, PaymentDraft(supplier.Id, MaxAmount)));
        Assert.Equal(MaxAmount, db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id).Amount);
    }

    // ==================== 4. 付款单提交 / 审核在既有行锁内复核已持久化金额（撤销语义不变） ====================

    [Fact]
    public async Task 提交付款单_已持久化金额超出存储精度_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-SUB", "供应商SUB");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var payment = SeedPayment(db, "FK-SUB", supplier.Id, amount: OneE16);
        var before = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        var controller = new FinancePaymentController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(payment.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertPaymentUnchanged(db, before);
        Assert.Equal(DocumentStatus.Pending, before.Status);
    }

    [Fact]
    public async Task 审核付款单_已持久化金额超出存储精度_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-APP", "供应商APP");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var payment = SeedPayment(db, "FK-APP", supplier.Id, amount: RoundUpCrossing,
            status: DocumentStatus.Submitted);
        var before = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        var controller = new FinancePaymentController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(payment.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertPaymentUnchanged(db, before);
        Assert.Equal(DocumentStatus.Submitted, before.Status);
    }

    [Fact]
    public async Task 付款单提交审核_恰好等于存储上限_放行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-FLOW", "供应商FLOW");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var payment = SeedPayment(db, "FK-FLOW", supplier.Id, amount: MaxAmount);
        var controller = new FinancePaymentController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(payment.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(payment.Id));
        Assert.Equal(DocumentStatus.Approved,
            db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id).Status);
    }

    [Fact]
    public async Task 取消付款单_已持久化金额超出存储精度_仍按既有撤销语义执行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-CAN", "供应商CAN");
        var userId = SeedOperator(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");
        var payment = SeedPayment(db, "FK-CAN", supplier.Id, amount: OneE16);
        var controller = new FinancePaymentController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        // 取消 / 删除等撤销路径不新增草稿有效性闸门：历史（或异常）金额不阻止既有撤销语义。
        Assert.IsType<OkObjectResult>(await controller.Cancel(payment.Id));
        Assert.Equal(DocumentStatus.Cancelled,
            db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id).Status);
    }

    // ==================== 5. 控制器：货款申请单创建 / 修改拒绝（零写入 / 零占号）与精确上限放行 ====================

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建货款申请单_金额超出存储精度_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-509", "客户509");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(ApplyDraft(customer.Id, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Empty(db.FinancePaymentApplies);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 创建货款申请单_恰好等于存储上限_照常落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-MAX", "客户MAX");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(ApplyDraft(customer.Id, MaxAmount)));

        Assert.Equal(1, numbers.Calls);
        var stored = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == id);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Null(stored.SalesOrderId);
    }

    [Fact]
    public async Task 修改货款申请单_金额超出存储精度_拒绝且原字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-UPD", "客户UPD");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        var apply = SeedApply(db, "HK-UPD", customer.Id, amount: 1000m);
        var before = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == apply.Id);
        var controller = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var request = ApplyDraft(customer.Id, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(apply.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertApplyUnchanged(db, before);
    }

    // ==================== 6. 货款申请单提交 / 审核在既有行锁内复核已持久化金额（撤销语义不变） ====================

    [Fact]
    public async Task 货款申请单提交审核_已持久化金额超出存储精度_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-FLOW", "客户FLOW");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        var pending = SeedApply(db, "HK-SUB", customer.Id, amount: OneE16);
        var submitted = SeedApply(db, "HK-APP", customer.Id, amount: RoundUpCrossing,
            status: DocumentStatus.Submitted);
        var pendingBefore = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == pending.Id);
        var submittedBefore = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == submitted.Id);
        var controller = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(pending.Id));
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(submitted.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, submitEx.Code);
        Assert.Equal(ErrorCodes.InvalidParameter, approveEx.Code);
        AssertApplyUnchanged(db, pendingBefore);
        AssertApplyUnchanged(db, submittedBefore);
        Assert.Equal(DocumentStatus.Pending, pendingBefore.Status);
        Assert.Equal(DocumentStatus.Submitted, submittedBefore.Status);
    }

    [Fact]
    public async Task 货款申请单提交审核_恰好等于存储上限_放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-FLOW-MAX", "客户FLOWMAX");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        var apply = SeedApply(db, "HK-FLOW", customer.Id, amount: MaxAmount);
        var controller = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(apply.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(apply.Id));
        Assert.Equal(DocumentStatus.Approved,
            db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == apply.Id).Status);
    }

    [Fact]
    public async Task 取消货款申请单_已持久化金额超出存储精度_仍按既有撤销语义执行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-CAN", "客户CAN");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        var apply = SeedApply(db, "HK-CAN", customer.Id, amount: OneE16);
        var controller = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Cancel(apply.Id));
        Assert.Equal(DocumentStatus.Cancelled,
            db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == apply.Id).Status);
    }

    // ==================== 7. 测试脚手架 ====================

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

    private static long SeedOperator(ErpDbContext db, string menuCode, string menuText, string path)
    {
        var role = new SysRole
        {
            RoleName = "付款数值边界角色",
            RoleCode = $"PaymentAmount-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"payment-amount-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "付款数值边界操作员",
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

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Pending)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 10, 1),
            SupplierId = supplierId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "ACC-1",
            Remark = "ERP-509",
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 2, 8, 0, 0)
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static FinancePaymentApply SeedApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Pending)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 10, 1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            ExchangeRate = 1m,
            BankAccount = "ACC-1",
            Payee = "义乌档口",
            Reason = "ERP-509",
            Remark = "ERP-509",
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 2, 8, 0, 0)
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment PaymentDraft(long supplierId, decimal amount) => new()
    {
        PaymentDate = new DateTime(2026, 10, 1),
        SupplierId = supplierId,
        Amount = amount,
        Currency = Currency.CNY,
        PaymentMethod = PaymentMethod.BankTransfer,
        BankAccount = "ACC-1",
        Remark = "ERP-509"
    };

    private static FinancePaymentApply ApplyDraft(long customerId, decimal amount) => new()
    {
        ApplyDate = new DateTime(2026, 10, 1),
        CustomerId = customerId,
        Amount = amount,
        Currency = Currency.CNY,
        ExchangeRate = 1m,
        BankAccount = "ACC-1",
        Payee = "义乌档口",
        Reason = "ERP-509",
        Remark = "ERP-509"
    };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static void AssertPaymentUnchanged(ErpDbContext db, FinancePayment expected)
    {
        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.SupplierId, stored.SupplierId);
        Assert.Equal(expected.PaymentApplyId, stored.PaymentApplyId);
        Assert.Equal(expected.Currency, stored.Currency);
        Assert.Equal(expected.Amount, stored.Amount);
        Assert.Equal(expected.PaymentMethod, stored.PaymentMethod);
        Assert.Equal(expected.BankAccount, stored.BankAccount);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
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
}
