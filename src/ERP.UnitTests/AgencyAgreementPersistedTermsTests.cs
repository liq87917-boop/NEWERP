using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-516 代理服务费协议「显式固定金额存储边界 + 登记前已持久化费用条款复核」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：固定金额在**币种精度取整之后**必须可在实际金额列 <c>DECIMAL(18,2)</c> 内原样存储（复用既有
/// <see cref="FinancePersistedAmountRules.EnsureRepresentable"/>），取整进位越界 / <c>1e16</c> / <c>decimal.MaxValue</c> /
/// 零小数币种进位越界一律受控拒绝；精确上限、半进位恰好进位到上限、<c>JPY</c> 整数上限照常放行；费率 (0,100] 且 4 位小数、
/// 两种口径互斥、币种显式且不推断等既有口径**原样保留**；</item>
/// <item>全局 <see cref="CurrencyAmountRules.RoundAmount"/> 与只读权威金额 / 聚合求和口径**不加**单列上限；</item>
/// <item><see cref="AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent"/>：合法存量条款放行，
/// 存量金额越界 / 精度不符 / 费率越界或精度不符 / 口径互斥 / 计费方式非法一律按权威口径受控拒绝；</item>
/// <item>控制器创建 / 修改：受权账号提交超出单列精度的固定金额在**任何写入之前**被受控拒绝（零协议写入、
/// 零身份消耗），恰好等于上限的固定金额照常落库；</item>
/// <item>登记：状态 / 登记人 / 时间戳冻结**之前**复核已持久化条款，非法存量行被拒绝且协议与审计逐字段不变；
/// 合法行照常登记；历史读取与显式作废**不**新增闸门（非法存量行仍可按既有规则作废）。</item>
/// </list>
/// 说明：除「实际 EF 列精度」用例以 SQL Server 提供程序**仅构建模型**（不打开连接）外，全部使用内存数据库；
/// 不连接 SQL Server、不执行 DDL / 部署脚本、不开票 / 不记账 / 不授权付款，也不做浏览器 / UI 验收。
/// </summary>
public class AgencyAgreementPersistedTermsTests
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;   // 9_999_999_999_999_999.99
    private const decimal HalfUpToMax = 9_999_999_999_999_999.985m;                     // USD 半进位后恰好等于上限
    private const decimal JustOver = 9_999_999_999_999_999.999m;                        // USD 取整进位后越过上限
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;                 // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;                              // 1e16（超出上限）
    private const decimal JpyRoundUpCrossing = 9_999_999_999_999_999.6m;                 // JPY 取整进位越过上限

    private const string FeeMethodRate = AgencyServiceFeeAgreementRules.FeeMethodRate;
    private const string FeeMethodFixed = AgencyServiceFeeAgreementRules.FeeMethodFixed;

    // ==================== 1. 纯规则：固定金额存储边界与全局取整口径 ====================

    [Fact]
    public void 固定金额存储常量与实际列精度同源_且不扩散到全局取整()
    {
        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal(9_999_999_999_999_999.99m, FinancePersistedAmountRules.MaxDecimal18Scale2);
        Assert.Equal(-9_999_999_999_999_999.99m, FinancePersistedAmountRules.MinDecimal18Scale2);

        Assert.True(FinancePersistedAmountRules.IsRepresentable(MaxAmount));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(OneE16));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(1.234m));

        // 全局币种取整 / 只读口径保持无上限：单列边界只存在于显式固定金额守卫。
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(RoundUpCrossing, "USD"));
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(JustOver, "USD"));
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(JpyRoundUpCrossing, "JPY"));
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(OneE16, "USD"));

        // 精确上限放行：守卫只在越界时给出受控错误。
        Assert.Equal(MaxAmount,
            FinancePersistedAmountRules.EnsureRepresentable(MaxAmount, AgencyServiceFeeAgreementRules.FixedAmountLabel));
    }

    [Fact]
    public void 固定金额_币种取整后精确上限与半进位放行()
    {
        Assert.Equal(MaxAmount, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, MaxAmount, "USD").FixedAmount);
        Assert.Equal(MaxAmount, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, HalfUpToMax, "USD").FixedAmount);
        Assert.Equal(MaxAmount, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, MaxAmount, "CNY").FixedAmount);

        // 零小数币种：16 位整数上限（JPY 取整到整数）照常放行。
        Assert.Equal(9_999_999_999_999_999m, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, 9_999_999_999_999_999m, "JPY").FixedAmount);

        // 费率口径不变：100% 放行。
        Assert.Equal(100m, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodRate, 100m, null, "USD").RatePercent);
    }

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("just-over")]
    [InlineData("1e16")]
    [InlineData("max-decimal")]
    [InlineData("jpy-round-up-cross")]
    public void 固定金额_超出实际存储精度_取整之后受控拒绝(string scenario)
    {
        var (amount, currency) = OverLimit(scenario);

        var ex = Assert.Throws<BusinessException>(() => AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, amount, currency));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Fact]
    public void 固定金额_既有正数与互斥口径保持不变()
    {
        // 取整后为 0 / 负数仍按既有「必须大于 0」拒绝（与存储边界无关）。
        Assert.Throws<BusinessException>(() => AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, 0.4m, "JPY"));
        Assert.Throws<BusinessException>(() => AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, -1m, "USD"));

        // 两种口径互斥 / 未知计费方式仍拒绝，不做静默归一化。
        Assert.Throws<BusinessException>(() => AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, 1.5m, 100m, "USD"));
        Assert.Throws<BusinessException>(() => AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodRate, 1.5m, 100m, "USD"));
        Assert.Throws<BusinessException>(() => AgencyServiceFeeAgreementRules
            .ValidateFeeTerms("阶梯费率", 1.5m, null, "USD"));

        // 费率仍保留 4 位小数（0.5 进位），固定金额仍按币种精度取整。
        Assert.Equal(1.2346m, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodRate, 1.23456m, null, "USD").RatePercent);
        Assert.Equal(12.35m, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, 12.345m, "USD").FixedAmount);
        Assert.Equal(1000m, AgencyServiceFeeAgreementRules
            .ValidateFeeTerms(FeeMethodFixed, null, 1000.4m, "JPY").FixedAmount);
    }

    // ==================== 2. 纯规则：登记前已持久化费用条款复核 ====================

    [Fact]
    public void 已持久化条款复核_合法存量行放行()
    {
        AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(Stored(FeeMethodRate, 1.5m, 0m, "usd"));
        AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(Stored(FeeMethodFixed, 0m, 100.01m, "USD"));
        AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(
            Stored(FeeMethodFixed, 0m, 9_999_999_999_999_999m, "JPY"));
        AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(Stored(FeeMethodFixed, 0m, MaxAmount, "CNY"));
        AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(Stored(FeeMethodRate, 100m, 0m, "USD"));
    }

    [Fact]
    public void 已持久化条款复核_非法或精度不符的存量行被受控拒绝()
    {
        // 计费方式非法 / 缺失
        AssertThrowsStored(Stored(string.Empty, 1.5m, 0m));
        AssertThrowsStored(Stored("阶梯费率", 1.5m, 0m));

        // 费率越界 / 精度不符（不足 4 位小数无法原样复现）
        AssertThrowsStored(Stored(FeeMethodRate, 0m, 0m));
        AssertThrowsStored(Stored(FeeMethodRate, -1m, 0m));
        AssertThrowsStored(Stored(FeeMethodRate, 100.0001m, 0m));
        AssertThrowsStored(Stored(FeeMethodRate, 1.23456m, 0m));
        AssertThrowsStored(Stored(FeeMethodRate, 0.00001m, 0m));

        // 固定金额越界 / 精度不符 / 正数口径
        AssertThrowsStored(Stored(FeeMethodFixed, 0m, 0m));
        AssertThrowsStored(Stored(FeeMethodFixed, 0m, -1m));
        AssertThrowsStored(Stored(FeeMethodFixed, 0m, OneE16, "USD"));
        AssertThrowsStored(Stored(FeeMethodFixed, 0m, RoundUpCrossing, "USD"));
        AssertThrowsStored(Stored(FeeMethodFixed, 0m, 1.005m, "USD"));
        AssertThrowsStored(Stored(FeeMethodFixed, 0m, 1000.5m, "JPY"));

        // 口径互斥
        AssertThrowsStored(Stored(FeeMethodRate, 1.5m, 10m));
        AssertThrowsStored(Stored(FeeMethodFixed, 1.5m, 100m));
    }

    [Fact]
    public void 已持久化条款复核_不取整不截断也不改写入参()
    {
        var stored = Stored(FeeMethodFixed, 0m, RoundUpCrossing, "USD");

        Assert.Throws<BusinessException>(() =>
            AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(stored));

        // 复核是纯只读：协议条款与状态逐字段不变（绝不静默取整 / 回填）。
        Assert.Equal(RoundUpCrossing, stored.FixedAmount);
        Assert.Equal(0m, stored.RatePercent);
        Assert.Equal(FeeMethodFixed, stored.FeeMethod);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, stored.Status);
        Assert.Null(stored.RecordedAt);
        Assert.Equal(string.Empty, stored.RecordedBy);
    }

    // ==================== 3. 实际 EF 列精度（仅构建模型） ====================

    [Fact]
    public void 实际EF列精度_固定金额decimal18_2_费率decimal9_4()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP516_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        var entity = db.Model.FindEntityType(typeof(AgencyServiceFeeAgreement));
        Assert.NotNull(entity);

        var fixedAmount = entity!.FindProperty(nameof(AgencyServiceFeeAgreement.FixedAmount))!;
        Assert.Equal(FinancePersistedAmountRules.DecimalPrecision, fixedAmount.GetPrecision());
        Assert.Equal(FinancePersistedAmountRules.DecimalScale, fixedAmount.GetScale());
        Assert.Equal("decimal(18,2)", fixedAmount.GetColumnType());

        var rate = entity.FindProperty(nameof(AgencyServiceFeeAgreement.RatePercent))!;
        Assert.Equal(AgencyServiceFeeAgreementRules.RateDecimals, rate.GetScale());
        Assert.Equal("decimal(9,4)", rate.GetColumnType());

        // 规则上限与实际列可表示上限同源。
        Assert.True(FinancePersistedAmountRules.IsRepresentable(MaxAmount));
        Assert.False(FinancePersistedAmountRules.IsRepresentable(MaxAmount + 0.01m));
    }

    // ==================== 4. 控制器：创建 / 修改拒绝（零写入 / 零身份消耗）与上限放行 ====================

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("just-over")]
    [InlineData("1e16")]
    [InlineData("max-decimal")]
    [InlineData("jpy-round-up-cross")]
    public async Task 创建固定金额协议_超出存储精度_拒绝且零写入零身份消耗(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-CREATE", "客户516");
        var controller = BuildController(db);
        var (amount, currency) = OverLimit(scenario);

        var before = db.AgencyServiceFeeAgreements.AsNoTracking().Count();
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(FixedDto(customer.Id, amount, currency)));

        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Equal(before, db.AgencyServiceFeeAgreements.AsNoTracking().Count());

        // 同一受权控制器随后用同一客户 + 合法金额创建成功：证明拒绝原因是金额而非授权 / 身份。
        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(FixedDto(customer.Id, 100m, currency)));
        Assert.Equal(created.Id, db.AgencyServiceFeeAgreements.AsNoTracking().Single().Id);
    }

    [Fact]
    public async Task 创建固定金额协议_精确上限放行并原样落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-MAX", "客户上限");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(FixedDto(customer.Id, MaxAmount, "USD")));

        Assert.Equal(FeeMethodFixed, created.FeeMethod);
        Assert.Equal(MaxAmount, created.FixedAmount);
        Assert.Equal(0m, created.RatePercent);
        Assert.Equal("USD", created.Currency);
        Assert.Equal(MaxAmount, db.AgencyServiceFeeAgreements.AsNoTracking().Single().FixedAmount);
    }

    [Fact]
    public async Task 修改固定金额协议_超出存储精度_拒绝且既有协议逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-UPD", "客户修改");
        var controller = BuildController(db);
        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(FixedDto(customer.Id, 800m, "USD", "ASF-516-UPD")));
        var before = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == created.Id);

        var request = FixedDto(customer.Id, OneE16, "USD", "ASF-516-UPD");
        request.Remark = "SHOULD_NOT_BE_SAVED";
        request.FeeBasis = "SHOULD_NOT_BE_SAVED";
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(created.Id, request));

        Assert.Contains("DECIMAL(18,2)", ex.Message);
        var after = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == created.Id);
        Assert.Equal(before.FixedAmount, after.FixedAmount);
        Assert.Equal(before.RatePercent, after.RatePercent);
        Assert.Equal(before.AgreementNo, after.AgreementNo);
        Assert.Equal(before.FeeBasis, after.FeeBasis);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Currency, after.Currency);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task 创建固定金额协议_币种显式且不推断()
    {
        using var db = TestDbFactory.Create();
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = "SalesCommissionRate", ParamValue = "7", ParamName = "业务员提成比例", IsSystem = true
        });
        var customer = SeedCustomer(db, "C-516-CUR", "客户币种", commissionRatio: 3.5m);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var usd = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(FixedDto(customer.Id, 12.345m, "usd", "ASF-516-USD")));
        Assert.Equal(12.35m, usd.FixedAmount);      // 显式 USD 精度取整
        Assert.Equal("USD", usd.Currency);

        var jpy = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(FixedDto(customer.Id, 1000.4m, "JPY", "ASF-516-JPY")));
        Assert.Equal(1000m, jpy.FixedAmount);       // 显式 JPY 零小数币种取整
        Assert.Equal("JPY", jpy.Currency);

        // 显式固定金额绝不被提成参数 / 客户比例覆盖或补齐。
        Assert.DoesNotContain("7", usd.FeeTermsText);
        Assert.DoesNotContain("3.5", usd.FeeTermsText);

        // 计费方式为固定金额但金额缺失：不从任何口径推断补齐，仍拒绝。
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(FixedDto(customer.Id, 0m, "USD", "ASF-516-MISSING")));
    }

    // ==================== 5. 控制器：登记前复核已持久化条款（零变更 / 历史语义不变） ====================

    [Fact]
    public async Task 登记_存量固定金额超出存储精度_拒绝且状态与审计逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-REC-OVER", "客户登记越界");
        var controller = BuildController(db);
        var draft = SeedDraft(db, customer.Id, "ASF-516-REC-OVER", FeeMethodFixed, 0m, OneE16, "USD");
        var before = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == draft.Id);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Record(draft.Id));

        Assert.Contains("DECIMAL(18,2)", ex.Message);
        AssertAgreementUnchanged(db, before);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft,
            db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == draft.Id).Status);
    }

    [Theory]
    [InlineData("fixed-precision")]
    [InlineData("rate-precision")]
    [InlineData("rate-over-limit")]
    [InlineData("rate-zero")]
    [InlineData("unknown-method")]
    [InlineData("exclusive-fixed")]
    [InlineData("exclusive-rate")]
    public async Task 登记_存量条款不可原样复现_拒绝且零变更(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-REC-BAD", "客户登记非法");
        var controller = BuildController(db);
        var (method, rate, fixedAmount, currency) = MalformedStored(scenario);
        var draft = SeedDraft(db, customer.Id, $"ASF-516-BAD-{scenario}", method, rate, fixedAmount, currency);
        var before = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == draft.Id);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Record(draft.Id));

        AssertAgreementUnchanged(db, before);
    }

    [Fact]
    public async Task 登记_合法存量条款_正常冻结并保留历史读取与作废()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-REC-OK", "客户登记合法");
        var controller = BuildController(db);
        var draft = SeedDraft(db, customer.Id, "ASF-516-REC-OK", FeeMethodFixed, 0m, MaxAmount, "USD");

        var recorded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(draft.Id));
        Assert.True(recorded.IsRecorded);
        Assert.Equal(MaxAmount, recorded.FixedAmount);
        Assert.NotNull(recorded.RecordedAt);

        // 历史读取照常可用（已登记冻结但可读）。
        var reloaded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(draft.Id));
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, reloaded.Status);

        // 显式作废仍按既有规则执行（撤销路径不调用登记前复核）。
        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(
            draft.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "协议终止" }));
        Assert.True(voided.IsVoided);
        Assert.Equal(MaxAmount, voided.FixedAmount);
    }

    [Fact]
    public async Task 登记_存量非法条款仍可按既有规则作废_历史撤销语义不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-REC-VOID", "客户存量作废");
        var controller = BuildController(db);
        var draft = SeedDraft(db, customer.Id, "ASF-516-REC-VOID", FeeMethodFixed, 0m, OneE16, "USD");
        var before = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == draft.Id);

        // 登记被拒（非法存量条款），但历史行原样保留。
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Record(draft.Id));
        AssertAgreementUnchanged(db, before);

        // 既有显式作废语义不受影响：非法存量金额不阻止作废，也不被静默修复。
        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(
            draft.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "存量条款更正" }));
        Assert.True(voided.IsVoided);
        Assert.Equal(OneE16, voided.FixedAmount);
        Assert.Equal(OneE16,
            db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == draft.Id).FixedAmount);
    }

    [Fact]
    public async Task 登记_未认证身份先于条款复核_fail_closed且零变更()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-516-ANON", "客户匿名");
        var draft = SeedDraft(db, customer.Id, "ASF-516-ANON", FeeMethodFixed, 0m, MaxAmount, "USD");
        var before = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == draft.Id);

        var anonymous = BuildControllerWithUser(db, userId: null);
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => anonymous.Record(draft.Id));

        AssertAgreementUnchanged(db, before);
    }

    // ==================== 6. 测试脚手架 ====================

    /// <summary>播种既有「客户资料」（customer）功能菜单（复用既有菜单口径，不新增菜单 / 权限）</summary>
    private static SysMenu EnsureCustomerMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted
            && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = AgencyServiceFeeReconciliationRules.RequiredMenuCode,
            MenuName = AgencyServiceFeeReconciliationRules.RequiredMenuText,
            Path = "/master/customer",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种具备既有「客户资料」菜单的启用账号（系统内置角色 → 数据范围不受限）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "代理服务费协议固定金额角色",
            RoleCode = $"AsfAgrTerms-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"asf-agr-terms-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "代理服务费协议固定金额操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsureCustomerMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static AgencyServiceFeeAgreementController BuildController(ErpDbContext db)
        => BuildControllerWithUser(db, SeedPrivilegedUser(db));

    private static AgencyServiceFeeAgreementController BuildControllerWithUser(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new AgencyServiceFeeAgreementController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, decimal commissionRatio = 0m)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            CommissionRatio = commissionRatio
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static AgencyServiceFeeAgreementSaveDto FixedDto(
        long customerId, decimal amount, string currency = "USD", string agreementNo = "ASF-516-001")
        => new()
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = FeeMethodFixed,
            FixedAmount = amount,
            FeeBasis = "按月固定",
            Remark = "ERP-516"
        };

    private static AgencyServiceFeeAgreement SeedDraft(
        ErpDbContext db, long customerId, string agreementNo,
        string feeMethod, decimal ratePercent, decimal fixedAmount, string currency)
    {
        var agreement = new AgencyServiceFeeAgreement
        {
            AgreementNo = agreementNo,
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo),
            CustomerId = customerId,
            CustomerCode = "C-516-SEED",
            CustomerName = "存量客户",
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = "按出口发票金额",
            Status = AgencyServiceFeeAgreementRules.StatusDraft
        };
        db.AgencyServiceFeeAgreements.Add(agreement);
        db.SaveChanges();
        return agreement;
    }

    /// <summary>超界场景 → (提交金额, 币种)，覆盖 USD 取整进位 / 1e16 / 极大十进制 / JPY 零小数进位。</summary>
    private static (decimal Amount, string Currency) OverLimit(string scenario) => scenario switch
    {
        "round-up-cross" => (RoundUpCrossing, "USD"),
        "just-over" => (JustOver, "USD"),
        "1e16" => (OneE16, "USD"),
        "max-decimal" => (decimal.MaxValue, "USD"),
        "jpy-round-up-cross" => (JpyRoundUpCrossing, "JPY"),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知场景")
    };

    /// <summary>非法存量行场景 → (计费方式, 费率, 固定金额, 币种)</summary>
    private static (string Method, decimal Rate, decimal FixedAmount, string Currency) MalformedStored(
        string scenario) => scenario switch
    {
        "fixed-precision" => (FeeMethodFixed, 0m, 1.005m, "USD"),
        "rate-precision" => (FeeMethodRate, 1.23456m, 0m, "USD"),
        "rate-over-limit" => (FeeMethodRate, 100.0001m, 0m, "USD"),
        "rate-zero" => (FeeMethodRate, 0m, 0m, "USD"),
        "unknown-method" => ("阶梯费率", 1.5m, 0m, "USD"),
        "exclusive-fixed" => (FeeMethodRate, 1.5m, 10m, "USD"),
        "exclusive-rate" => (FeeMethodFixed, 1.5m, 100m, "USD"),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知场景")
    };

    private static AgencyServiceFeeAgreement Stored(
        string feeMethod, decimal ratePercent, decimal fixedAmount, string currency = "USD")
        => new()
        {
            AgreementNo = "ASF-516-STORED",
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeIdentityPart("ASF-516-STORED"),
            CustomerId = 1,
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = "按出口发票金额",
            Status = AgencyServiceFeeAgreementRules.StatusDraft
        };

    private static void AssertThrowsStored(AgencyServiceFeeAgreement stored)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(stored));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>被拒请求零变更：协议逐字段（含状态 / 登记 / 作废 / 审计）不变。</summary>
    private static void AssertAgreementUnchanged(ErpDbContext db, AgencyServiceFeeAgreement expected)
    {
        var stored = db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == expected.Id);
        Assert.Equal(expected.AgreementNo, stored.AgreementNo);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.Currency, stored.Currency);
        Assert.Equal(expected.FeeMethod, stored.FeeMethod);
        Assert.Equal(expected.RatePercent, stored.RatePercent);
        Assert.Equal(expected.FixedAmount, stored.FixedAmount);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.RecordedAt, stored.RecordedAt);
        Assert.Equal(expected.RecordedBy, stored.RecordedBy);
        Assert.Equal(expected.VoidedAt, stored.VoidedAt);
        Assert.Equal(expected.VoidReason, stored.VoidReason);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }
}
