using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-512 / ERP-538 费用单 / 装柜费用分摊「标量声明列精度与派生金额存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：金额 / 折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额等**客户端可持久化标量**必须**逐位原样**
/// 落在声明列精度 <c>DECIMAL(18,4)</c> 内、汇率落在 <c>DECIMAL(18,6)</c> 内（<see cref="FinancePersistedAmountRules"/>
/// 覆盖 2 / 4 / 6 三种小数位），过精度 / 取整进位越界与极大十进制受控拒绝，含乘法溢出防护；</item>
/// <item>费用 CRUD：负数 / 过精度 / 越界标量在**任何已跟踪写入之前**被拒（零写入、零改写），
/// 恰好上限与**零值可选税 / 分摊字段**照常放行，可表示的 4 位小数金额与 6 位小数汇率原样往返；</item>
/// <item>传统分摊：分摊总额按声明列精度逐位原样判定，折人民币乘法（金额 × 汇率）越界或溢出时预览与生成共用边界
/// 一律受控拒绝（零写入、零单号消耗），合法请求的单号与折人民币口径保持既有语义；</item>
/// <item>ERP-042 批次：历史 / 被外部改写的越界（过精度 / 超出量级）来源费用与越界基准值在**单号预约与任何 Db.Add
/// 之前**被拒（零批次、零分摊行、零费用行），合法生成仍保持余差归属与行合计等于来源金额；</item>
/// <item>历史读取 / 作废**不新增**草稿有效性闸门：越界历史行照常可读，只读回显沿用纯计算口径。</item>
/// </list>
/// 说明：除「实际 EF 列精度」用例以 SQL Server 提供程序**仅构建模型**（不打开连接）外，全部使用内存数据库。
/// </summary>
public class ExpensePersistedNumericBoundsTests
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale4;  // 99_999_999_999_999.9999（费用单金额类声明列上限）
    private const decimal MaxRate = FinancePersistedAmountRules.MaxDecimal18Scale6;    // 999_999_999_999.999999（汇率声明列上限）
    private const decimal MaxBasis = FinancePersistedAmountRules.MaxDecimal18Scale4;   // 99_999_999_999_999.9999
    private const decimal MaxScale2 = FinancePersistedAmountRules.MaxDecimal18Scale2;  // 9_999_999_999_999_999.99（既有 2 位小数快照列）
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;                // 取整进位越过 DECIMAL(18,2) 上限
    private const decimal OneE16 = 10_000_000_000_000_000m;                            // 1e16（超出 DECIMAL(18,4) 上限）
    private const decimal OneE15 = 1_000_000_000_000_000m;                             // 1e15（超出 DECIMAL(18,4) 上限）
    private static readonly DateTime FixedDate = new(2026, 10, 14);

    // ==================== 1. 纯规则：实际列精度常量与边界判定 ====================

    [Fact]
    public void 标量边界常量与实际列精度_覆盖2_4_6三种小数位且取整进位越界受控拒绝()
    {
        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal(4, FinancePersistedAmountRules.DecimalScale4);
        Assert.Equal(6, FinancePersistedAmountRules.DecimalScale6);
        Assert.Equal(9_999_999_999_999_999.99m, FinancePersistedAmountRules.MaxDecimal18Scale2);
        Assert.Equal(99_999_999_999_999.9999m, FinancePersistedAmountRules.MaxDecimal18Scale4);
        Assert.Equal(999_999_999_999.999999m, FinancePersistedAmountRules.MaxDecimal18Scale6);

        // DECIMAL(18,2)（既有批次表头快照列）：恰好上限（含负向）可表示；取整进位越过上限 / 1e16 / 极大十进制不可表示。
        Assert.True(FinancePersistedAmountRules.IsWithinDecimal18(MaxScale2, 2));
        Assert.True(FinancePersistedAmountRules.IsWithinDecimal18(-MaxScale2, 2));
        Assert.False(FinancePersistedAmountRules.IsWithinDecimal18(RoundUpCrossing, 2));
        Assert.False(FinancePersistedAmountRules.IsWithinDecimal18(OneE16, 2));
        Assert.False(FinancePersistedAmountRules.IsWithinDecimal18(-OneE16, 2));
        Assert.False(FinancePersistedAmountRules.IsWithinDecimal18(decimal.MaxValue, 2));

        // DECIMAL(18,4)（分摊基数值 / 比例）与 DECIMAL(18,6)（批次汇率）：区间更窄，按各自小数位取整后判定。
        Assert.True(FinancePersistedAmountRules.IsWithinDecimal18(MaxBasis, 4));
        Assert.False(FinancePersistedAmountRules.IsWithinDecimal18(OneE15, 4));
        Assert.True(FinancePersistedAmountRules.IsWithinDecimal18(FinancePersistedAmountRules.MaxDecimal18Scale6, 6));
        Assert.False(FinancePersistedAmountRules.IsWithinDecimal18(1_000_000_000_000m, 6));

        var basisEx = Assert.Throws<BusinessException>(() => FinancePersistedAmountRules.EnsureWithinDecimal18(
            OneE15, FinancePersistedAmountRules.DecimalScale4, ContainerExpenseAllocationRules.BasisValueLabel));
        Assert.Equal(ErrorCodes.InvalidParameter, basisEx.Code);
        Assert.Contains("DECIMAL(18,4)", basisEx.Message, StringComparison.Ordinal);

        var rateEx = Assert.Throws<BusinessException>(() => FinancePersistedAmountRules.EnsureWithinDecimal18(
            1_000_000_000_000m, FinancePersistedAmountRules.DecimalScale6,
            ContainerExpenseAllocationRules.ExchangeRateLabel));
        Assert.Equal(ErrorCodes.InvalidParameter, rateEx.Code);
        Assert.Contains("DECIMAL(18,6)", rateEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 受控乘法_合法乘积照常_乘积越界与CLR溢出一律受控拒绝且全局取整不加单列上限()
    {
        Assert.Equal(720m, FinancePersistedAmountRules.MultiplyScalar(100m, 7.2m, 2, "折人民币金额"));
        Assert.Equal(30.02m, FinancePersistedAmountRules.MultiplyScalar(4.17m, 7.1999m, 2, "折人民币金额"));
        Assert.Equal(0.01m, FinancePersistedAmountRules.MultiplyScalar(0.005m, 2m, 2, "折人民币金额"));

        // 乘积本身超出 DECIMAL(18,2)：拒绝而不是静默截断 / 取整。
        var outOfRange = Assert.Throws<BusinessException>(() =>
            FinancePersistedAmountRules.MultiplyScalar(MaxScale2, 2m, 2, "折人民币金额"));
        Assert.Equal(ErrorCodes.InvalidParameter, outOfRange.Code);
        Assert.Contains("DECIMAL(18,2)", outOfRange.Message, StringComparison.Ordinal);

        // CLR 十进制乘法溢出：转换为受控业务错误，绝不以 OverflowException 崩溃。
        var overflow = Assert.Throws<BusinessException>(() =>
            FinancePersistedAmountRules.MultiplyScalar(decimal.MaxValue, 2m, 2, "折人民币金额"));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Contains("溢出", overflow.Message, StringComparison.Ordinal);

        // 全局币种取整与只读求和**没有**单列上限（只读聚合可合法地超过单个存储列）。
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(RoundUpCrossing, "USD"));
        Assert.Equal(19_999_999_999_999_999.98m, MaxScale2 + MaxScale2);
    }

    [Fact]
    public void 费用金额与汇率规则_按声明列精度逐位原样_过精度与越界受控拒绝_零值可选标量放行()
    {
        // ERP-538：不再按 2 位小数取整 —— 真实可表示的 3 / 4 位小数金额与 3~6 位小数汇率**逐位原样**放行。
        Assert.Equal(0.005m, ExpenseAuthorizationRules.NormalizeAmount(0.005m));
        Assert.Equal(1234.5678m, ExpenseAuthorizationRules.NormalizeAmount(1234.5678m));
        Assert.Equal(MaxAmount, ExpenseAuthorizationRules.NormalizeAmount(MaxAmount));
        Assert.Equal(1.005m, ExpenseAuthorizationRules.NormalizeRate(1.005m));
        Assert.Equal(7.123456m, ExpenseAuthorizationRules.NormalizeRate(7.123456m));
        Assert.Equal(MaxRate, ExpenseAuthorizationRules.NormalizeRate(MaxRate));

        // 过精度（金额第 5 位小数 / 汇率第 7 位小数）与超出量级：受控拒绝且绝不取整进位后放行。
        foreach (var badAmount in new[] { 1.00001m, RoundUpCrossing, OneE15, OneE16, decimal.MaxValue })
        {
            var amountEx = Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeAmount(badAmount));
            Assert.Equal(ErrorCodes.InvalidParameter, amountEx.Code);
            Assert.Contains("DECIMAL(18,4)", amountEx.Message, StringComparison.Ordinal);
        }

        foreach (var badRate in new[] { 1.0000005m, 1_000_000_000_000m, OneE15, decimal.MaxValue })
        {
            var rateEx = Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeRate(badRate));
            Assert.Equal(ErrorCodes.InvalidParameter, rateEx.Code);
            Assert.Contains("DECIMAL(18,6)", rateEx.Message, StringComparison.Ordinal);
        }

        // 既有「必须为正」语义不变（零与负数）。
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeAmount(0m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeAmount(-1m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeRate(0m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeRate(-7.2m));

        // 零值可选标量（未启用税 / 未分摊）与 4 位小数边界值照常放行；过精度 / 越界只读判定为 false。
        Assert.True(ExpenseAuthorizationRules.IsScalarRepresentable(0m));
        Assert.Equal(0m, ExpenseAuthorizationRules.EnsureScalarRepresentable(
            0m, ExpenseAuthorizationRules.TaxAmountOverflowLabel));
        Assert.Equal(MaxAmount, ExpenseAuthorizationRules.EnsureScalarRepresentable(
            MaxAmount, ExpenseAuthorizationRules.AllocatedAmountOverflowLabel));
        Assert.Equal(1.2345m, ExpenseAuthorizationRules.EnsureScalarRepresentable(
            1.2345m, ExpenseAuthorizationRules.AllocationRatioOverflowLabel));
        Assert.False(ExpenseAuthorizationRules.IsScalarRepresentable(1.23456m));
        Assert.False(ExpenseAuthorizationRules.IsScalarRepresentable(OneE15));

        // 声明列小数值常量与权威建表脚本 / EF 映射同源。
        Assert.Equal(4, ExpenseAuthorizationRules.PersistedAmountScale);
        Assert.Equal(6, ExpenseAuthorizationRules.PersistedExchangeRateScale);
    }

    // ==================== 2. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP512_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        // ERP-538：FinanceExpense 的金额类列显式声明 decimal(18,4)，汇率列显式声明 decimal(18,6) ——
        // 与权威建表脚本 db_owner.FinanceExpenses 的声明列类型逐字同源。
        foreach (var property in new[]
        {
            nameof(FinanceExpense.Amount),
            nameof(FinanceExpense.AmountCny),
            nameof(FinanceExpense.TaxRate),
            nameof(FinanceExpense.TaxAmount),
            nameof(FinanceExpense.AllocationRatio),
            nameof(FinanceExpense.AllocatedAmount)
        })
            AssertDecimalColumn(db.Model, typeof(FinanceExpense), property, 18, 4);
        AssertDecimalColumn(db.Model, typeof(FinanceExpense),
            nameof(FinanceExpense.ExchangeRate), 18, 6);

        // ERP-042 表头：来源金额 / 分摊合计 decimal(18,2)，汇率 decimal(18,6)。
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.SourceAmount), 18, 2);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.AllocatedTotal), 18, 2);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.ExchangeRate), 18, 6);

        // ERP-042 分摊行：基数值 / 比例 decimal(18,4)，分摊金额 / 折人民币金额 decimal(18,2)。
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.BasisValue), 18, 4);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.Ratio), 18, 4);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.AllocatedAmount), 18, 2);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.AllocatedAmountCny), 18, 2);
    }

    private static void AssertDecimalColumn(
        IModel model, Type entityType, string propertyName, int precision, int scale)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
        Assert.Equal($"decimal({precision},{scale})", property.GetColumnType());
    }

    // ==================== 3. 费用 CRUD：越界标量拒绝且零写入 / 零改写 ====================

    [Fact]
    public async Task 新增_客户端标量超出声明列精度_拒绝且零写入_零值可选字段与边界金额放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-512", "精度客户");
        var controller = BuildController(db);

        var overLimit = new (string Name, FinanceExpense Entity)[]
        {
            ("AmountCny", NewExpense(customer.Id, amountCny: OneE16)),
            ("TaxRate", NewExpense(customer.Id, taxRate: OneE16)),
            ("TaxAmount", NewExpense(customer.Id, taxAmount: OneE16)),
            ("AllocationRatio", NewExpense(customer.Id, allocationRatio: OneE16)),
            ("AllocatedAmount", NewExpense(customer.Id, allocatedAmount: OneE16)),
            ("AmountMagnitude", NewExpense(customer.Id, amount: OneE15)),
            ("AmountOverScale", NewExpense(customer.Id, amount: 1.00001m)),
            ("AllocatedAmountOverScale", NewExpense(customer.Id, allocatedAmount: 1.00001m)),
            ("ExchangeRateMagnitude", NewExpense(customer.Id, rate: OneE15)),
            ("ExchangeRateOverScale", NewExpense(customer.Id, rate: 1.0000005m))
        };

        foreach (var (name, entity) in overLimit)
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(entity));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Contains("DECIMAL(18,", ex.Message, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        // 负数：金额 / 汇率与可选标量一律拒绝（费用标量不做负数登记），且零写入。
        foreach (var negative in new[]
        {
            NewExpense(customer.Id, amount: -1m),
            NewExpense(customer.Id, rate: -7.2m),
            NewExpense(customer.Id, taxRate: -1m),
            NewExpense(customer.Id, allocatedAmount: -0.0001m)
        })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(negative));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        // 被拒请求零写入。
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        // 恰好声明列上限（金额类 18,4 / 汇率 18,6）+ 零值可选（税额 / 分摊金额 / 未传折人民币）照常落库，
        // 且 4 位小数金额 / 汇率与可选标量**逐位原样**往返。
        var created = AssertOk<FinanceExpense>(await controller.Create(NewExpense(
            customer.Id, amount: MaxAmount, amountCny: MaxAmount,
            rate: MaxRate, currency: "USD",
            taxRate: 1.2345m, taxAmount: 0m, allocationRatio: 33.3333m, allocatedAmount: 0m)));
        Assert.Equal(MaxAmount, created.Amount);
        Assert.Equal(MaxAmount, created.AmountCny);
        Assert.Equal(MaxRate, created.ExchangeRate);
        Assert.Equal(1.2345m, created.TaxRate);
        Assert.Equal(0m, created.TaxAmount);
        Assert.Equal(33.3333m, created.AllocationRatio);
        Assert.Equal(0m, created.AllocatedAmount);

        var stored = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(MaxAmount, stored.AmountCny);
        Assert.Equal(MaxRate, stored.ExchangeRate);
        Assert.Equal(1.2345m, stored.TaxRate);
        Assert.Equal(33.3333m, stored.AllocationRatio);
        Assert.Equal(1, await db.FinanceExpenses.CountAsync());
    }

    [Fact]
    public async Task 修改_客户端标量超出声明列精度_拒绝且已存储行逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-512-UPD", "修改精度客户");
        var controller = BuildController(db);

        var created = AssertOk<FinanceExpense>(await controller.Create(
            NewExpense(customer.Id, amount: 100.1234m, amountCny: 720m, rate: 7.123456m, currency: "USD")));
        var before = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(100.1234m, before.Amount);
        Assert.Equal(7.123456m, before.ExchangeRate);

        var update = NewExpense(customer.Id, amount: 100m, amountCny: OneE16);
        update.Id = created.Id;
        update.Remark = "SHOULD_NOT_BE_SAVED";

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(created.Id, update));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,4)", ex.Message, StringComparison.Ordinal);

        // 第 5 位小数金额（过精度）同样拒绝；被拒修改不改写任何字段与审计时间戳。
        var overScale = NewExpense(customer.Id, amount: 100.12345m, rate: 7.123456m, currency: "USD");
        overScale.Id = created.Id;
        overScale.Remark = "SHOULD_NOT_BE_SAVED";
        var scaleEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(created.Id, overScale));
        Assert.Equal(ErrorCodes.InvalidParameter, scaleEx.Code);
        Assert.Contains("DECIMAL(18,4)", scaleEx.Message, StringComparison.Ordinal);

        var after = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.AmountCny, after.AmountCny);
        Assert.Equal(before.ExchangeRate, after.ExchangeRate);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // ==================== 4. 传统分摊：派生金额 / 折人民币乘法边界（预览与生成共用） ====================

    [Fact]
    public async Task 传统分摊_折人民币乘法溢出与总额越界_预览与生成同边界且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-512-LEGACY", "传统分摊客户");
        var controller = BuildController(db);

        // 折人民币 = 原币金额 × 汇率：各自都在声明列内可存储的极大分摊金额 × 极大汇率仍使乘积超出
        // 折人民币声明列（预览与生成共用边界）。
        var overflowPreview = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocatePreview(
            AllocateRequest(customer.Id, total: 1_000_000_000_000m, currency: "USD", rate: MaxRate)));
        Assert.Equal(ErrorCodes.InvalidParameter, overflowPreview.Code);
        Assert.Contains("折人民币金额", overflowPreview.Message, StringComparison.Ordinal);

        var overflowApply = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocateApply(
            AllocateRequest(customer.Id, total: 1_000_000_000_000m, currency: "USD", rate: MaxRate)));
        Assert.Equal(ErrorCodes.InvalidParameter, overflowApply.Code);
        Assert.Contains("折人民币金额", overflowApply.Message, StringComparison.Ordinal);

        // 分摊总额自身超出声明列精度（1e15 / 第 5 位小数）：同样在单号生成之前拒绝（预览与生成一致）。
        foreach (var badTotal in new[] { OneE15, 1.00005m })
        {
            await Assert.ThrowsAsync<BusinessException>(() => controller.AllocatePreview(
                AllocateRequest(customer.Id, total: badTotal)));
            await Assert.ThrowsAsync<BusinessException>(() => controller.AllocateApply(
                AllocateRequest(customer.Id, total: badTotal)));
        }

        // 被拒请求零写入（也绝不消耗单号）。
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        // 合法请求照常生成：折人民币 = 分摊金额 × 汇率（既有口径），单号从 001 开始（拒绝未占号）。
        var apply = await controller.AllocateApply(
            AllocateRequest(customer.Id, total: 100m, currency: "USD", rate: 7.2m));
        Assert.IsType<OkObjectResult>(apply);
        var generated = await db.FinanceExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(100m, generated.Amount);
        Assert.Equal(100m, generated.AllocatedAmount);
        Assert.Equal(720m, generated.AmountCny);
        Assert.Equal("EXP-20261014-001", generated.ExpenseNo);
    }

    // ==================== 5. ERP-042 批次：越界来源 / 派生值拒绝且零批次零分摊行零费用行 ====================

    [Fact]
    public async Task 批次预览与生成_越界来源与派生值_拒绝且零批次零分摊行_合法生成保持余差与行合计()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-512-A", "柜客户A");
        var customerB = SeedCustomer(db, "C-512-B", "柜客户B");
        var list = SeedLoadingList(db, "ZG512001", "TCLU-512", customerA.Id);
        var participantA = SeedParticipant(db, list.Id, customerA, primary: true);
        var participantB = SeedParticipant(db, list.Id, customerB);
        var controller = BuildController(db);

        // ① 已存储来源金额越界（历史 / 被外部改写）：预览与生成都以规则冲突受控拒绝，且绝不改写历史行。
        var broken = SeedSourceExpense(db, "EXP-512-BROKEN", "TCLU-512", OneE16);
        var brokenPreview = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationPreview(
            BatchRequest(broken.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));
        Assert.Equal(ErrorCodes.RuleConflict, brokenPreview.Code);
        var brokenGenerate = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationGenerate(
            BatchRequest(broken.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));
        Assert.Equal(ErrorCodes.RuleConflict, brokenGenerate.Code);
        Assert.Equal(OneE16,
            (await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == broken.Id)).Amount);

        // ② 折人民币派生越界：各自都在声明列内可存储的极大汇率 × 分摊金额使折人民币乘积超出其声明列
        // （受控拒绝，预览与生成同边界）。
        var overflowCny = SeedSourceExpense(
            db, "EXP-512-CNY", "TCLU-512", 1_000_000_000_000m, currency: "USD", exchangeRate: MaxRate);
        var cnyEx = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationPreview(
            BatchRequest(overflowCny.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));
        Assert.Equal(ErrorCodes.InvalidParameter, cnyEx.Code);
        Assert.Contains("折人民币金额", cnyEx.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationGenerate(
            BatchRequest(overflowCny.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));

        // ③ 基准值超出 DECIMAL(18,4)：在任何 Db.Add 之前受控拒绝。
        var source = SeedSourceExpense(db, "EXP-512-OK", "TCLU-512", 1000m, currency: "USD", exchangeRate: 7.2m);
        var basisEx = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationGenerate(
            BatchRequest(source.Id, list.Id, (participantA.Id, OneE15), (participantB.Id, 1m))));
        Assert.Equal(ErrorCodes.InvalidParameter, basisEx.Code);
        Assert.Contains("DECIMAL(18,4)", basisEx.Message, StringComparison.Ordinal);

        // 被拒请求零批次 / 零分摊行 / 零生成费用行（此刻仅 3 条来源费用行）。
        Assert.Equal(0, await db.FinanceExpenseAllocationBatches.CountAsync());
        Assert.Equal(0, await db.FinanceExpenseAllocationLines.CountAsync());
        Assert.Equal(3, await db.FinanceExpenses.CountAsync());

        // ④ 合法生成：余差归基准值最大的参与方，行合计恒等于来源金额，折人民币按汇率换算（既有口径）。
        var result = AssertOk<ContainerExpenseAllocationGenerateResultDto>(await controller.AllocationGenerate(
            BatchRequest(source.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));
        Assert.Equal(2, result.LineCount);
        Assert.Equal(1000m, result.AllocatedTotal);
        Assert.Equal(1000m, result.Lines.Sum(l => l.AllocatedAmount));
        Assert.Equal(100m, result.Lines.Sum(l => l.Ratio));
        Assert.Equal(300m, Assert.Single(result.Lines, l => l.ParticipantId == participantA.Id).AllocatedAmount);
        Assert.Equal(700m, Assert.Single(result.Lines, l => l.ParticipantId == participantB.Id).AllocatedAmount);
        Assert.Equal(2160m, Assert.Single(result.Lines, l => l.ParticipantId == participantA.Id).AllocatedAmountCny);
        Assert.Equal(5040m, Assert.Single(result.Lines, l => l.ParticipantId == participantB.Id).AllocatedAmountCny);

        Assert.Equal(1, await db.FinanceExpenseAllocationBatches.CountAsync());
        Assert.Equal(2, await db.FinanceExpenseAllocationLines.CountAsync());
        Assert.Equal(5, await db.FinanceExpenses.CountAsync());
    }

    // ==================== 6. 历史读取：不新增草稿有效性闸门 ====================

    [Fact]
    public async Task 历史读取_越界历史行仍可读_读取路径不施加写入边界()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-512-HIST", "历史客户");
        SeedSourceExpense(db, "EXP-512-HIST", "TCLU-HIST", OneE16, customerId: customer.Id);

        // 列表 / 详情读取不调用写入侧标量守卫：历史越界行照常可读，绝不因新边界被拒、也不被自动更正。
        var page = AssertOk<PagedResult<FinanceExpense>>(await BuildController(db).GetPaged(new PageQuery()));
        var row = Assert.Single(page.Items);
        Assert.Equal("EXP-512-HIST", row.ExpenseNo);
        Assert.Equal(OneE16, row.Amount);
        Assert.Equal(customer.Id, row.CustomerId);

        // 只读纯计算口径同样不受写入边界影响（历史回显仍按既有取整口径）。
        Assert.Equal(OneE16, ContainerExpenseAllocationRules.ConvertToCny(OneE16, "CNY", 7.2m));
    }

    // ==================== 7. 纯规则：分摊基准 / 折人民币派生边界（不依赖数据库） ====================

    [Fact]
    public void 分摊规则_基准值与来源金额越界受控拒绝_乘积溢出时仍保持余差与行合计()
    {
        var basisEx = Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.Distribute(
            100m, "CNY", new List<ContainerExpenseAllocationRules.AllocationBasis> { new(1, OneE15) }));
        Assert.Equal(ErrorCodes.InvalidParameter, basisEx.Code);
        Assert.Contains("DECIMAL(18,4)", basisEx.Message, StringComparison.Ordinal);

        var totalEx = Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.Distribute(
            OneE16, "CNY", new List<ContainerExpenseAllocationRules.AllocationBasis> { new(1, 1m) }));
        Assert.Equal(ErrorCodes.InvalidParameter, totalEx.Code);
        Assert.Contains("DECIMAL(18,2)", totalEx.Message, StringComparison.Ordinal);

        // 两个各自合法的极大基准值：中间乘积会溢出 CLR，但绝不崩溃，且行合计恒等于来源金额、比例合计 100。
        var shares = ContainerExpenseAllocationRules.Distribute(
            MaxAmount, "CNY",
            new List<ContainerExpenseAllocationRules.AllocationBasis> { new(1, MaxBasis), new(2, MaxBasis) });
        Assert.Equal(MaxAmount, shares.Sum(s => s.AllocatedAmount));
        Assert.Equal(100m, shares.Sum(s => s.Ratio));

        // 折人民币派生：CNY 原样、其余币种按汇率换算 2 位小数（AwayFromZero）；越界 / 溢出受控拒绝。
        Assert.Equal(100m, ContainerExpenseAllocationRules.EnsureConvertedToCnyRepresentable(100m, "CNY", 7.2m));
        Assert.Equal(720m, ContainerExpenseAllocationRules.EnsureConvertedToCnyRepresentable(100m, "usd", 7.2m));
        var cnyEx = Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationRules.EnsureConvertedToCnyRepresentable(MaxAmount, "USD", MaxAmount));
        Assert.Equal(ErrorCodes.InvalidParameter, cnyEx.Code);
        Assert.Contains("折人民币金额", cnyEx.Message, StringComparison.Ordinal);

        // 只读纯计算口径（历史回显 / 分摊证据）继续沿用 ConvertToCny，不受写入边界影响：
        // 它仍按**币种精度**取整（CNY 2 位），因此 4 位小数金额在只读回显时同样收敛到 2 位。
        Assert.Equal(MaxScale2, ContainerExpenseAllocationRules.ConvertToCny(MaxScale2, "CNY", 1m));
        Assert.Equal(
            100_000_000_000_000.00m,
            ContainerExpenseAllocationRules.ConvertToCny(MaxAmount, "CNY", 1m));
    }

    // ==================== 8. 接线与文档契约 ====================

    [Fact]
    public void 接线与文档契约_写入边界入口与说明同源()
    {
        var controller = ReadRepoFile("src", "ERP.Api", "Controllers", "ExpenseBillController.cs");
        Assert.Contains("ExpenseAuthorizationRules.DeriveCnyAmount", controller, StringComparison.Ordinal);
        Assert.Contains("EnsureAllocateResultRepresentable", controller, StringComparison.Ordinal);
        Assert.Contains("EnsureProposedExpenseValidAsync", controller, StringComparison.Ordinal);

        var authorization = ReadRepoFile("src", "ERP.Application", "Services", "ExpenseAuthorizationRules.cs");
        Assert.Contains("EnsureStoredExpenseScalarsRepresentable", authorization, StringComparison.Ordinal);
        Assert.Contains("EnsureScalarRepresentable", authorization, StringComparison.Ordinal);
        // ERP-538：声明列精度判定与非负可选标量校验入口。
        Assert.Contains("PersistedAmountScale", authorization, StringComparison.Ordinal);
        Assert.Contains("PersistedExchangeRateScale", authorization, StringComparison.Ordinal);
        Assert.Contains("EnsureWithinDeclaredPrecision", authorization, StringComparison.Ordinal);
        Assert.Contains("EnsureNonNegativeScalar", authorization, StringComparison.Ordinal);

        // ERP-538：EF 映射为七个标量列显式声明精度（与权威建表脚本同源）。
        var mapping = ReadRepoFile("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");
        Assert.Contains("Property(x => x.Amount).HasPrecision(18, 4)", mapping, StringComparison.Ordinal);
        Assert.Contains("Property(x => x.AmountCny).HasPrecision(18, 4)", mapping, StringComparison.Ordinal);
        Assert.Contains("Property(x => x.TaxRate).HasPrecision(18, 4)", mapping, StringComparison.Ordinal);
        Assert.Contains("Property(x => x.TaxAmount).HasPrecision(18, 4)", mapping, StringComparison.Ordinal);
        Assert.Contains("Property(x => x.AllocationRatio).HasPrecision(18, 4)", mapping, StringComparison.Ordinal);
        Assert.Contains("Property(x => x.AllocatedAmount).HasPrecision(18, 4)", mapping, StringComparison.Ordinal);
        Assert.Contains("Property(x => x.ExchangeRate).HasPrecision(18, 6)", mapping, StringComparison.Ordinal);

        var service = ReadRepoFile("src", "ERP.Application", "Services", "ContainerExpenseAllocationService.cs");
        Assert.Contains("EnsureConvertedToCnyRepresentable", service, StringComparison.Ordinal);
        Assert.Contains("EnsureStoredExpenseScalarsRepresentable", service, StringComparison.Ordinal);

        var rules = ReadRepoFile("src", "ERP.Application", "Services", "ContainerExpenseAllocationRules.cs");
        Assert.Contains("EnsureConvertedToCnyRepresentable", rules, StringComparison.Ordinal);
        Assert.Contains("ExactShareAmount", rules, StringComparison.Ordinal);

        var doc = ReadRepoFile("docs", "expense-persisted-numeric-bounds.md");
        Assert.Contains("ERP-512", doc, StringComparison.Ordinal);
        Assert.Contains("ERP-538", doc, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,2)", doc, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,4)", doc, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,6)", doc, StringComparison.Ordinal);
        Assert.Contains("FinancePersistedAmountRules", doc, StringComparison.Ordinal);
    }

    // ==================== 9. 测试脚手架（隔离内存库 + 真实 HTTP 身份 + 既有主数据） ====================

    /// <summary>
    /// 真实费用单控制器：控制器动作**自行**实时解析身份 / 账号状态 / 既有「费用单」（<c>expense-bill</c>）菜单 /
    /// 权威客户范围，因此夹具绑定一个**真实启用**、持有既有菜单授权的特权账号（既有系统内置角色口径）。
    /// </summary>
    private static ExpenseBillController BuildController(ErpDbContext db)
    {
        var userId = SeedPrivilegedExpenseOperator(db);
        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
            },
            ActionDescriptor = new ControllerActionDescriptor()
        };
        return controller;
    }

    /// <summary>播种真实启用、持有既有「费用单」菜单的特权操作员（只在隔离测试数据内创建，不新增生产授权）。</summary>
    private static long SeedPrivilegedExpenseOperator(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = $"费用单精度夹具角色-{Guid.NewGuid():N}",
            RoleCode = $"Role-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"exp-bounds-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "费用单精度夹具账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = db.SysMenus
            .FirstOrDefault(m => m.MenuCode == ExpenseAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuCode = ExpenseAuthorizationRules.RequiredMenuCode,
                MenuName = ExpenseAuthorizationRules.RequiredMenuText,
                Path = "/finance/expense-bill",
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

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

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        decimal cartons = 10m, decimal weight = 500m, decimal volume = 3.5m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = FixedDate,
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingListParticipant SeedParticipant(
        ErpDbContext db, long loadingListId, BaseCustomer customer, bool primary = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = 1
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    /// <summary>
    /// 直接播种**已存储**来源费用（绕过写入守卫）：用于验证历史 / 被外部改写的越界值仍可读、
    /// 但拒绝新的分摊生成而不被静默改写。
    /// </summary>
    private static FinanceExpense SeedSourceExpense(
        ErpDbContext db, string expenseNo, string containerNo, decimal amount,
        string currency = "CNY", decimal exchangeRate = 1m, string refType = "拼柜",
        long? customerId = null)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = FixedDate,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            AmountCny = amount,
            RefType = refType,
            RefNo = containerNo,
            CustomerId = customerId,
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    private static FinanceExpense NewExpense(
        long? customerId, decimal amount = 100m, string currency = "CNY", decimal rate = 1m,
        decimal amountCny = 0m, decimal taxRate = 0m, decimal taxAmount = 0m,
        decimal allocationRatio = 0m, decimal allocatedAmount = 0m)
        => new()
        {
            ExpenseNo = $"EXP-512-{Guid.NewGuid():N}"[..14],
            ExpenseDate = FixedDate,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = rate,
            AmountCny = amountCny,
            TaxRate = taxRate,
            TaxAmount = taxAmount,
            AllocationRatio = allocationRatio,
            AllocatedAmount = allocatedAmount,
            CustomerId = customerId,
            RefType = "客户",
            RefNo = "TCLU-512",
            AllocationBase = "不分摊"
        };

    private static ExpenseAllocateRequest AllocateRequest(
        long? customerId, decimal total = 100m, string currency = "CNY", decimal rate = 1m)
        => new()
        {
            RefType = "拼柜",
            RefNo = "TCLU-512-LEGACY",
            ExpenseType = "报关费",
            TotalAmount = total,
            Currency = currency,
            ExchangeRate = rate,
            AllocationBase = "按体积",
            ExpenseDate = FixedDate,
            Details = new List<ExpenseAllocateDetail>
            {
                new() { CustomerId = customerId, CustomerName = "传统分摊客户", Volume = 1m }
            }
        };

    private static ContainerExpenseAllocationRequest BatchRequest(
        long sourceExpenseId, long loadingListId, params (long ParticipantId, decimal BasisValue)[] lines)
        => new()
        {
            SourceExpenseId = sourceExpenseId,
            LoadingListId = loadingListId,
            AllocationMethod = ContainerExpenseAllocationRules.MethodByVolume,
            Lines = lines.Select(l => new ContainerExpenseAllocationBasisDto
            {
                ParticipantId = l.ParticipantId,
                BasisValue = l.BasisValue
            }).ToList()
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static string ReadRepoFile(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));
}
