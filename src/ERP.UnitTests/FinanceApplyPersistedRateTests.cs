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
/// ERP-517 定金申请单 / 货款申请单「汇率存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：<see cref="FinancePersistedAmountRules"/> 的汇率列 <c>DECIMAL(18,2)</c> 边界与实际 EF 汇率列精度同源；
/// 既有「6 位小数归一化」语义（<c>AwayFromZero</c>）对**可原样落库**的汇率原样保留；</item>
/// <item>极小正汇率（6 位小数归一化为 <c>0</c>）、真实列会静默收敛为 <c>0.00</c> / 改写小数位的正汇率、取整进位越界 /
/// <c>1e16</c> / 极大十进制一律受控拒绝（<c>InvalidParameter</c>）；</item>
/// <item>只读历史口径（<see cref="FinanceDepositApplyLifecycleRules.AuthoritativeExchangeRate"/> /
/// <see cref="FinancePaymentApplyLifecycleRules.AuthoritativeExchangeRate"/>）**不加**任何单列上限、绝不改写；</item>
/// <item>两个申请单控制器创建 / 修改：受权账号提交不可原样落库的汇率在**单号预约与任何写入之前**被受控拒绝
/// （零表头写入、零单号消耗），合法汇率（含精确上限）照常落库；</item>
/// <item>提交 / 审核在既有行锁内复核**已持久化**汇率（经两个申请单的 <c>EnsurePersistedApplyConsistentAsync</c>），
/// 非法历史快照被拒绝且状态 / 字段 / 审计不变、绝不静默修复；取消等撤销动作**不新增**草稿有效性闸门。</item>
/// </list>
/// 说明：除「实际 EF 汇率列精度」用例以 SQL Server 提供程序**仅构建模型**（不打开连接）外，全部使用内存数据库。
/// </summary>
public class FinanceApplyPersistedRateTests
{
    private const decimal MaxRate = FinancePersistedAmountRules.MaxDecimal18Scale2;   // 9_999_999_999_999_999.99
    private const decimal Tiny = 0.0000001m;          // 6 位小数取整后为 0
    private const decimal CoarsenZero = 0.001m;       // 真实列会静默收敛为 0.00
    private const decimal CoarsenChange = 1.234568m;  // 真实列会静默改写为 1.23
    private const decimal SixDecimalRate = 7.123456m; // 真实列会静默改写为 7.12
    private const decimal BoundaryCross = 9_999_999_999_999_999.995m; // 6 位小数归一化后越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;

    // ==================== 1. 纯规则：汇率边界、归一化与只读历史口径 ====================

    [Fact]
    public void 汇率可表示判定与归一化小数位同源()
    {
        Assert.Equal(6, FinanceDepositApplyLifecycleRules.ExchangeRateDecimals);
        Assert.Equal(6, FinancePaymentApplyLifecycleRules.ExchangeRateDecimals);

        Assert.True(FinancePersistedAmountRules.IsRateRepresentable(0.01m));
        Assert.True(FinancePersistedAmountRules.IsRateRepresentable(1m));
        Assert.True(FinancePersistedAmountRules.IsRateRepresentable(7.2m));
        Assert.True(FinancePersistedAmountRules.IsRateRepresentable(MaxRate));

        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(0m));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(-1.5m));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(Tiny));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(CoarsenZero));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(CoarsenChange));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(SixDecimalRate));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(MaxRate + 0.01m));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(OneE16));
        Assert.False(FinancePersistedAmountRules.IsRateRepresentable(decimal.MaxValue));

        Assert.Equal(7.2m, FinancePersistedAmountRules.EnsureRateRepresentable(7.2m, "汇率"));
        var ex = Assert.Throws<BusinessException>(
            () => FinancePersistedAmountRules.EnsureRateRepresentable(CoarsenChange, "汇率"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Contains(FinancePersistedAmountRules.ExchangeRateOverflowText, ex.Message);
    }

    [Fact]
    public void 可表示汇率_六位小数归一化语义原样保留()
    {
        // 既有「6 位小数归一化（AwayFromZero）」对可原样落库的汇率原样保留。
        Assert.Equal(7.2m, FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(7.20000004m));
        Assert.Equal(7.2m, FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(7.19999991m));
        Assert.Equal(1.25m, FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(1.25000004m));
        Assert.Equal(0.01m, FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(0.01000049m));

        Assert.Equal(7.2m, FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(7.20000004m));
        Assert.Equal(7.2m, FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(7.19999991m));
        Assert.Equal(1.25m, FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(1.25000004m));
        Assert.Equal(0.01m, FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(0.01000049m));

        // 精确上限（DECIMAL(18,2) 最大可表示值）照常放行。
        Assert.Equal(MaxRate, FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(MaxRate));
        Assert.Equal(MaxRate, FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(MaxRate));
    }
    [Theory]
    [InlineData("tiny")]
    [InlineData("coarsen-zero")]
    [InlineData("coarsen-change")]
    [InlineData("six-decimal")]
    [InlineData("boundary-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 汇率不可原样落库_受控拒绝(string scenario)
    {
        var deposit = Assert.Throws<BusinessException>(
            () => FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(Rate(scenario)));
        Assert.Equal(ErrorCodes.InvalidParameter, deposit.Code);

        var payment = Assert.Throws<BusinessException>(
            () => FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(Rate(scenario)));
        Assert.Equal(ErrorCodes.InvalidParameter, payment.Code);

        if (scenario == "tiny")
        {
            // 极小正汇率归一化为 0：按既有「必须大于 0」口径拒绝，且明确「绝不静默归零」。
            Assert.Contains("大于 0", deposit.Message);
            Assert.Contains("取整后为 0", deposit.Message);
            Assert.Contains("取整后为 0", payment.Message);
        }
        else
        {
            Assert.Contains("DECIMAL(18,2)", deposit.Message);
            Assert.Contains("DECIMAL(18,2)", payment.Message);
        }
    }

    [Fact]
    public void 非正汇率与极大归一化_受控错误且无未处理异常()
    {
        foreach (var value in new[] { 0m, -0.0000001m, -1m, decimal.MinValue })
        {
            Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
                () => FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(value)).Code);
            Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
                () => FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(value)).Code);
        }

        // CLR 刻度 / 范围：归一化本身不抛未处理异常（十进制尺度不变），边界判定随后受控拒绝。
        Assert.Equal(decimal.MaxValue,
            FinancePersistedAmountRules.SafeRoundRate(decimal.MaxValue, 6, "汇率"));
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(decimal.MaxValue)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(decimal.MaxValue)).Code);
    }

    [Fact]
    public void 只读权威汇率比对口径_不施加单列上限且不改写()
    {
        // 历史比对 / 只读口径保持无上限（单列边界只存在于写入守卫），绝不被 ERP-517 收窄或改写。
        Assert.Equal(1.234568m, FinanceDepositApplyLifecycleRules.AuthoritativeExchangeRate(1.23456789m));
        Assert.Equal(0m, FinanceDepositApplyLifecycleRules.AuthoritativeExchangeRate(Tiny));
        Assert.Equal(decimal.MaxValue,
            FinanceDepositApplyLifecycleRules.AuthoritativeExchangeRate(decimal.MaxValue));

        Assert.Equal(SixDecimalRate, FinancePaymentApplyLifecycleRules.AuthoritativeExchangeRate(SixDecimalRate));
        Assert.Equal(0m, FinancePaymentApplyLifecycleRules.AuthoritativeExchangeRate(0m));
        Assert.Equal(OneE16, FinancePaymentApplyLifecycleRules.AuthoritativeExchangeRate(OneE16));
    }

    // ==================== 2. 实际 EF 汇率列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF汇率列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP517_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        AssertScalarRateColumn(db.Model, typeof(FinanceDepositApply), nameof(FinanceDepositApply.ExchangeRate));
        AssertScalarRateColumn(db.Model, typeof(FinancePaymentApply), nameof(FinancePaymentApply.ExchangeRate));
    }

    private static void AssertScalarRateColumn(IModel model, Type entityType, string propertyName)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        // 汇率列**未**显式 HasPrecision：EF SQL Server 提供程序按默认精度给出 decimal(18,2)；
        // 规则常量必须与该实际列类型逐字同源（若将来显式声明精度，本断言会立即失败并要求同步规则常量）。
        Assert.Null(property.GetPrecision());
        Assert.Null(property.GetScale());
        Assert.Equal(
            $"decimal({FinancePersistedAmountRules.DecimalPrecision},{FinancePersistedAmountRules.DecimalScale})",
            property.GetColumnType());
        Assert.Equal("decimal(18,2)", property.GetColumnType());
    }

    // ==================== 3. 控制器：创建 / 修改拒绝（零写入 / 零占号）与合法汇率放行 ====================

    [Theory]
    [InlineData("coarsen-zero")]
    [InlineData("coarsen-change")]
    [InlineData("six-decimal")]
    [InlineData("boundary-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建定金申请单_汇率不可原样落库_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-DEP", "客户汇率定金");
        var userId = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var numbers = new CountingNumberService();
        var controller = new FinanceDepositApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(DepositDraft(customer.Id, rate: Rate(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.FinanceDepositApplies);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 创建定金申请单_极小正汇率归一化为0_拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-TINY", "客户汇率极小");
        var userId = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var numbers = new CountingNumberService();
        var controller = new FinanceDepositApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(DepositDraft(customer.Id, rate: Tiny)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("大于 0", ex.Message);
        Assert.Empty(db.FinanceDepositApplies);
        Assert.Equal(0, numbers.Calls);
    }

    [Theory]
    [InlineData("coarsen-zero")]
    [InlineData("coarsen-change")]
    [InlineData("six-decimal")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建货款申请单_汇率不可原样落库_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-PAY", "客户汇率货款");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText);
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(ApplyDraft(customer.Id, rate: Rate(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.FinancePaymentApplies);
        Assert.Equal(0, numbers.Calls);
    }

    [Fact]
    public async Task 创建定金申请单_合法汇率_原样落库含精确上限()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-OK", "客户汇率OK");
        var order = SeedSalesOrder(db, "SO-RATE", customer.Id);
        var userId = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var numbers = new CountingNumberService();
        var controller = new FinanceDepositApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(
            DepositDraft(customer.Id, rate: 7.20000004m, salesOrderId: order.Id)));
        var stored = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == id);
        Assert.Equal(7.2m, stored.ExchangeRate);
        Assert.Equal(1, numbers.Calls);

        var maxId = CreatedId(await controller.Create(DepositDraft(customer.Id, rate: MaxRate)));
        Assert.Equal(MaxRate, db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == maxId).ExchangeRate);
    }

    [Fact]
    public async Task 创建货款申请单_合法汇率_原样落库含精确上限()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-PAY-OK", "客户汇率货款OK");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText);
        var numbers = new CountingNumberService();
        var controller = new FinancePaymentApplyController(db, numbers);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(ApplyDraft(customer.Id, rate: 7.19999991m)));
        Assert.Equal(7.2m, db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == id).ExchangeRate);

        var maxId = CreatedId(await controller.Create(ApplyDraft(customer.Id, rate: MaxRate)));
        Assert.Equal(MaxRate, db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == maxId).ExchangeRate);
    }

    [Fact]
    public async Task 修改定金申请单_汇率不可原样落库_拒绝且原始字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-DEP-UPD", "客户汇率定金改");
        var userId = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var apply = SeedDepositApply(db, "DJ-RATE-UPD", customer.Id, rate: 1m);
        var before = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == apply.Id);
        var controller = new FinanceDepositApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var request = DepositDraft(customer.Id, rate: CoarsenChange);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(apply.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertDepositApplyUnchanged(db, before);

        // 合法汇率修改照常生效（既有字段 / 锁定 / 来源语义不变）。
        Assert.IsType<OkObjectResult>(await controller.Update(apply.Id, DepositDraft(customer.Id, rate: 2.5m)));
        Assert.Equal(2.5m, db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == apply.Id).ExchangeRate);
    }

    [Fact]
    public async Task 修改货款申请单_汇率不可原样落库_拒绝且原始字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-PAY-UPD", "客户汇率货款改");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText);
        var apply = SeedPaymentApply(db, "HK-RATE-UPD", customer.Id, rate: 1m);
        var before = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == apply.Id);
        var controller = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var request = ApplyDraft(customer.Id, rate: OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(apply.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertPaymentApplyUnchanged(db, before);

        Assert.IsType<OkObjectResult>(await controller.Update(apply.Id, ApplyDraft(customer.Id, rate: 7.2m)));
        Assert.Equal(7.2m, db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == apply.Id).ExchangeRate);
    }

    // ==================== 4. 提交 / 审核：锁内复核已持久化汇率（撤销语义不变） ====================

    [Fact]
    public async Task 定金申请单提交审核_已持久化非法汇率_拒绝且状态字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-DEP-FLOW", "客户汇率定金流转");
        var userId = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var coarsened = SeedDepositApply(db, "DJ-RATE-SUB", customer.Id, rate: CoarsenChange);
        var zero = SeedDepositApply(db, "DJ-RATE-APP", customer.Id,
            status: DocumentStatus.Submitted, rate: 0m);
        var coarsenedBefore = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == coarsened.Id);
        var zeroBefore = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == zero.Id);
        var controller = new FinanceDepositApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(coarsened.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, submitEx.Code);
        AssertDepositApplyUnchanged(db, coarsenedBefore);   // 非法历史快照原样保留、绝不静默修复

        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(zero.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, approveEx.Code);
        AssertDepositApplyUnchanged(db, zeroBefore);
    }

    [Fact]
    public async Task 货款申请单提交审核_已持久化非法汇率_拒绝且状态字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-PAY-FLOW", "客户汇率货款流转");
        var userId = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText);
        var coarsened = SeedPaymentApply(db, "HK-RATE-SUB", customer.Id, rate: SixDecimalRate);
        var zero = SeedPaymentApply(db, "HK-RATE-APP", customer.Id,
            status: DocumentStatus.Submitted, rate: 0m);
        var coarsenedBefore = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == coarsened.Id);
        var zeroBefore = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == zero.Id);
        var controller = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(coarsened.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, submitEx.Code);
        AssertPaymentApplyUnchanged(db, coarsenedBefore);

        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(zero.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, approveEx.Code);
        AssertPaymentApplyUnchanged(db, zeroBefore);
    }

    [Fact]
    public async Task 合法汇率_关联与未关联来源生命周期照常流转()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-LIFE", "客户汇率生命周期");
        var depositUser = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var paymentUser = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText);

        // 定金申请单：显式关联来源销售订单 + 合法汇率。
        var order = SeedSalesOrder(db, "SO-RATE-LIFE", customer.Id);
        var depositNumbers = new CountingNumberService();
        var deposit = new FinanceDepositApplyController(db, depositNumbers);
        TestAuth.SetUser(deposit, depositUser);
        var depositId = CreatedId(await deposit.Create(
            DepositDraft(customer.Id, rate: 7.2m, salesOrderId: order.Id)));
        Assert.IsType<OkObjectResult>(await deposit.Submit(depositId));
        Assert.IsType<OkObjectResult>(await deposit.Approve(depositId));
        var storedDeposit = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == depositId);
        Assert.Equal(DocumentStatus.Approved, storedDeposit.Status);
        Assert.Equal(7.2m, storedDeposit.ExchangeRate);
        Assert.Equal(order.Id, storedDeposit.SalesOrderId);
        Assert.Equal(1, depositNumbers.Calls);

        // 货款申请单：历史「未关联来源」场景 + 合法汇率。
        var paymentNumbers = new CountingNumberService();
        var payment = new FinancePaymentApplyController(db, paymentNumbers);
        TestAuth.SetUser(payment, paymentUser);
        var paymentId = CreatedId(await payment.Create(ApplyDraft(customer.Id, rate: 0.01m)));
        Assert.IsType<OkObjectResult>(await payment.Submit(paymentId));
        Assert.IsType<OkObjectResult>(await payment.Approve(paymentId));
        var storedPayment = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == paymentId);
        Assert.Equal(DocumentStatus.Approved, storedPayment.Status);
        Assert.Equal(0.01m, storedPayment.ExchangeRate);
        Assert.Null(storedPayment.SalesOrderId);
        Assert.Equal(1, paymentNumbers.Calls);
    }

    [Fact]
    public async Task 取消_已持久化非法汇率_仍按既有撤销语义执行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-RATE-CAN", "客户汇率取消");
        var depositUser = SeedOperator(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
            FinanceDepositApplyLifecycleRules.RequiredMenuText);
        var paymentUser = SeedOperator(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
            FinancePaymentApplyLifecycleRules.RequiredMenuText);
        var deposit = SeedDepositApply(db, "DJ-RATE-CAN", customer.Id, rate: CoarsenChange);
        var payment = SeedPaymentApply(db, "HK-RATE-CAN", customer.Id, rate: 0m);

        // 取消 / 删除等撤销路径不新增草稿有效性闸门：非法历史汇率不阻止既有撤销语义，也绝不改写快照。
        var depositController = new FinanceDepositApplyController(db, new CountingNumberService());
        TestAuth.SetUser(depositController, depositUser);
        Assert.IsType<OkObjectResult>(await depositController.Cancel(deposit.Id));
        var cancelledDeposit = db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == deposit.Id);
        Assert.Equal(DocumentStatus.Cancelled, cancelledDeposit.Status);
        Assert.Equal(CoarsenChange, cancelledDeposit.ExchangeRate);

        var paymentController = new FinancePaymentApplyController(db, new CountingNumberService());
        TestAuth.SetUser(paymentController, paymentUser);
        Assert.IsType<OkObjectResult>(await paymentController.Cancel(payment.Id));
        var cancelledPayment = db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == payment.Id);
        Assert.Equal(DocumentStatus.Cancelled, cancelledPayment.Status);
        Assert.Equal(0m, cancelledPayment.ExchangeRate);
    }

    // ==================== 5. 测试脚手架 ====================

    private static decimal Rate(string scenario) => scenario switch
    {
        "tiny" => Tiny,
        "coarsen-zero" => CoarsenZero,
        "coarsen-change" => CoarsenChange,
        "six-decimal" => SixDecimalRate,
        "boundary-cross" => BoundaryCross,
        "1e16" => OneE16,
        _ => decimal.MaxValue
    };

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

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = Currency.CNY,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceDepositApply SeedDepositApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m,
        DocumentStatus status = DocumentStatus.Pending, decimal rate = 1m)
    {
        var apply = new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 5),
            CustomerId = customerId,
            Amount = amount,
            Currency = Currency.CNY,
            ExchangeRate = rate,
            BankAccount = "ACC-517",
            Payee = "收款方",
            Reason = "ERP-517",
            Remark = "ERP-517",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 6, 8, 0, 0)
        };
        db.FinanceDepositApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePaymentApply SeedPaymentApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m,
        DocumentStatus status = DocumentStatus.Pending, decimal rate = 1m)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 5),
            CustomerId = customerId,
            Amount = amount,
            Currency = Currency.CNY,
            ExchangeRate = rate,
            BankAccount = "ACC-517",
            Payee = "义乌档口",
            Reason = "ERP-517",
            Remark = "ERP-517",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 6, 8, 0, 0)
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinanceDepositApply DepositDraft(
        long customerId, decimal amount = 1000m, decimal rate = 1m, long? salesOrderId = null) => new()
    {
        ApplyDate = new DateTime(2026, 9, 5),
        CustomerId = customerId,
        Amount = amount,
        Currency = Currency.CNY,
        SalesOrderId = salesOrderId,
        ExchangeRate = rate,
        BankAccount = "ACC-517",
        Payee = "收款方",
        Reason = "ERP-517",
        Remark = "ERP-517"
    };

    private static FinancePaymentApply ApplyDraft(
        long customerId, decimal amount = 1000m, decimal rate = 1m, long? salesOrderId = null) => new()
    {
        ApplyDate = new DateTime(2026, 9, 5),
        CustomerId = customerId,
        Amount = amount,
        Currency = Currency.CNY,
        SalesOrderId = salesOrderId,
        ExchangeRate = rate,
        BankAccount = "ACC-517",
        Payee = "义乌档口",
        Reason = "ERP-517",
        Remark = "ERP-517"
    };

    /// <summary>播种一个具备指定既有菜单授权的启用账号（系统内置角色 → 可见全部客户）。</summary>
    private static long SeedOperator(ErpDbContext db, string menuCode, string menuText)
    {
        var role = new SysRole
        {
            RoleName = "财务汇率边界角色",
            RoleCode = $"FinanceRate-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"finance-rate-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "财务汇率边界操作员",
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
            Path = "/finance/applies",
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

    private static void AssertDepositApplyUnchanged(ErpDbContext db, FinanceDepositApply expected)
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

    private static void AssertPaymentApplyUnchanged(ErpDbContext db, FinancePaymentApply expected)
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
