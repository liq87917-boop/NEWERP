using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-529 出口退税台账（<c>api/base/tax-refunds</c>）金额 / 退税率 / 汇率的**实际存储精度**边界单元测试
/// （内存库 + 真实控制器 + 真实已认证身份）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：<c>ExportAmount</c> / <c>RefundRate</c> / <c>RefundableAmount</c> /
/// <c>RefundedAmount</c> = <c>decimal(18,4)</c>、<c>ExchangeRate</c> = <c>decimal(18,6)</c>
/// （仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>纯规则：四个小数位金额 / 退税率与六个小数位汇率的逐位原样可表示边界（恰好上限放行、第 5 / 第 7 位小数与
/// 量级溢出拒绝、零 / 负汇率拒绝、「半进位取整后恰好落到上限」的过小数位值仍拒绝）；</item>
/// <item><see cref="TaxRefundLedgerRules.Validate"/>：与既有非负 / 0..100 / 已退不超过可退 / 币种 / 期间 /
/// 日期规则叠加，拒绝时逐字段不变（绝不取整、截断或回填）；</item>
/// <item>真实控制器新增 / 修改：不可表示的值零台账行、零改写，合法 4 位小数金额与 6 位小数汇率原样落库；</item>
/// <item>源码契约：EF 映射与权威列声明逐字同源、护栏先于任何写入且规则层不取整。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返 / 越界被真实列拒绝」由
/// <c>ERP.IntegrationTests/TaxRefundLedgerPersistedNumericBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class TaxRefundLedgerPersistedNumericBoundsTests
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = TaxRefundLedgerRules.MaxAmount;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max6 = TaxRefundLedgerRules.MaxExchangeRate;

    // ==================== 0. 测试脚手架（真实已认证身份 + 真实控制器） ====================

    /// <summary>绑定已认证身份的出口退税台账控制器（<paramref name="userId"/> 为 null 表示未认证请求主体）。</summary>
    private static TaxRefundController Controller(ErpDbContext db, long? userId)
    {
        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new TaxRefundController(new GenericService<BaseTaxRefund>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>播种特权账号（系统内置角色，ERP-097 特权口径），仅需实时身份校验（与 ERP-442 同口径）。</summary>
    private static async Task<SysUser> SeedPrivilegedUserAsync(ErpDbContext db)
    {
        var code = $"TAX529-ADM-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"退税台账系统角色-{code}", RoleCode = $"TAX529-SYS-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user;
    }

    private static BaseTaxRefund Draft(long? customerId, decimal exportAmount = 1000m, decimal exchangeRate = 1m,
        decimal refundRate = 13m, decimal refundableAmount = 130m, decimal refundedAmount = 0m,
        string currency = "USD", string period = "2026-08")
        => new()
        {
            RefundNo = $"TR529-{Guid.NewGuid():N}",
            RefundPeriod = period,
            DeclareDate = new DateTime(2026, 8, 5),
            CustomerId = customerId,
            ExportAmount = exportAmount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            RefundRate = refundRate,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            Status = "待申报"
        };

    private static BaseTaxRefund SeedLedger(ErpDbContext db, string refundNo, long? customerId,
        decimal exportAmount = 1000m, decimal exchangeRate = 1m, decimal refundRate = 13m,
        decimal refundableAmount = 130m, decimal refundedAmount = 0m)
    {
        var entity = new BaseTaxRefund
        {
            RefundNo = refundNo,
            RefundPeriod = "2026-08",
            DeclareDate = new DateTime(2026, 8, 5),
            CustomerId = customerId,
            ExportAmount = exportAmount,
            Currency = "USD",
            ExchangeRate = exchangeRate,
            RefundRate = refundRate,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            Status = "待申报",
            UpdatedAt = new DateTime(2026, 8, 6, 9, 30, 0)
        };
        db.BaseTaxRefunds.Add(entity);
        db.SaveChanges();
        return entity;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP529_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(18, TaxRefundLedgerRules.DecimalPrecision);
        Assert.Equal(4, TaxRefundLedgerRules.AmountScale);
        Assert.Equal(6, TaxRefundLedgerRules.ExchangeRateScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);
        Assert.Equal(-99_999_999_999_999.9999m, TaxRefundLedgerRules.MinAmount);
        Assert.Equal(999_999_999_999.999999m, Max6);

        // 精度常量复用系统唯一标量边界口径（ERP-508），不新增第二套常量。
        Assert.Equal(FinancePersistedAmountRules.DecimalPrecision, TaxRefundLedgerRules.DecimalPrecision);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale4, TaxRefundLedgerRules.AmountScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale6, TaxRefundLedgerRules.ExchangeRateScale);
        Assert.Equal(FinancePersistedAmountRules.MaxDecimal18Scale4, Max4);
        Assert.Equal(FinancePersistedAmountRules.MaxDecimal18Scale6, Max6);

        // 四个小数位金额 / 退税率列：模型必须显式声明 (18,4)，否则按 EF 默认 decimal(18,2) 静默取整。
        AssertLedgerColumn(db.Model, nameof(BaseTaxRefund.ExportAmount), 18, 4);
        AssertLedgerColumn(db.Model, nameof(BaseTaxRefund.RefundRate), 18, 4);
        AssertLedgerColumn(db.Model, nameof(BaseTaxRefund.RefundableAmount), 18, 4);
        AssertLedgerColumn(db.Model, nameof(BaseTaxRefund.RefundedAmount), 18, 4);

        // 汇率列：(18,6)，与 SchemaUpgrader 第 12.4 段建表脚本逐字同源。
        AssertLedgerColumn(db.Model, nameof(BaseTaxRefund.ExchangeRate), 18, 6);
    }

    /// <summary>
    /// 模型映射必须与 SchemaUpgrader 第 12.4 段的权威建表脚本（<c>DECIMAL(18,4)</c> / <c>DECIMAL(18,6)</c>）逐字一致；
    /// 若退回 EF 默认 <c>decimal(18,2)</c>，合法的 3 / 4 位小数金额与 3..6 位小数汇率会在写入前被静默取整。
    /// </summary>
    private static void AssertLedgerColumn(IModel model, string propertyName, int precision, int scale)
    {
        var property = model.FindEntityType(typeof(BaseTaxRefund))!.FindProperty(propertyName)!;
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
        Assert.Equal($"decimal({precision},{scale})", property.GetColumnType());
    }

    // ==================== 2. 纯规则：逐位原样可表示边界（不取整、不截断） ====================

    [Theory]
    [InlineData("0")]
    [InlineData("0.0001")]
    [InlineData("1")]
    [InlineData("1.2345")]
    [InlineData("-1.2345")]
    [InlineData("4.0000")]
    [InlineData("13")]
    [InlineData("100")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 可表示_四位小数金额与退税率边界放行(string raw)
        => Assert.True(TaxRefundLedgerRules.IsAmountRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1.00005")]                  // 第 5 位小数
    [InlineData("0.00005")]
    [InlineData("13.00001")]
    [InlineData("100000000000000")]          // 1e14 量级越界
    [InlineData("100000000000000.0000")]
    [InlineData("-100000000000000")]
    [InlineData("99999999999999.99985")]     // 半进位取整后恰好落到上限，但本身不可逐位原样存储
    [InlineData("99999999999999.99995")]     // 半进位取整后越过上限
    public void 不可表示_金额过小数位或量级溢出(string raw)
        => Assert.False(TaxRefundLedgerRules.IsAmountRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void 不可表示_金额极大十进制()
    {
        Assert.False(TaxRefundLedgerRules.IsAmountRepresentable(decimal.MaxValue));
        Assert.False(TaxRefundLedgerRules.IsAmountRepresentable(decimal.MinValue));
    }

    [Theory]
    [InlineData("0.000001")]
    [InlineData("1")]
    [InlineData("1.234567")]
    [InlineData("7.1234")]
    [InlineData("999999999999.999999")]
    public void 可表示_六位小数汇率边界放行(string raw)
        => Assert.True(TaxRefundLedgerRules.IsExchangeRateRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]                        // 零汇率
    [InlineData("-0.000001")]                // 负汇率
    [InlineData("-1")]
    [InlineData("1.0000005")]                // 第 7 位小数
    [InlineData("0.0000005")]
    [InlineData("1000000000000")]            // 1e12 量级越界
    [InlineData("999999999999.9999995")]     // 半进位取整后恰好落到上限，但本身不可逐位原样存储
    public void 不可表示_汇率零负过小数位或量级溢出(string raw)
        => Assert.False(TaxRefundLedgerRules.IsExchangeRateRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void 不可表示_汇率极大十进制()
    {
        Assert.False(TaxRefundLedgerRules.IsExchangeRateRepresentable(decimal.MaxValue));
        Assert.False(TaxRefundLedgerRules.IsExchangeRateRepresentable(decimal.MinValue));
    }

    [Fact]
    public void Ensure金额不可表示_抛既有受控错误且不改写入参()
    {
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.EnsureAmountRepresentable(
            1.00005m, TaxRefundLedgerRules.ExportAmountFieldText));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExportAmount", ex.Message);
        Assert.Contains("DECIMAL(18,4)", ex.Message);

        // 恰好上限与合法四位小数按原值返回（绝不取整 / 回填）。
        Assert.Equal(Max4, TaxRefundLedgerRules.EnsureAmountRepresentable(
            Max4, TaxRefundLedgerRules.ExportAmountFieldText));
        Assert.Equal(13.0001m, TaxRefundLedgerRules.EnsureAmountRepresentable(
            13.0001m, TaxRefundLedgerRules.RefundRateFieldText));
    }

    [Fact]
    public void Ensure汇率不可表示_抛既有受控错误且不改写入参()
    {
        var ex = Assert.Throws<BusinessException>(
            () => TaxRefundLedgerRules.EnsureExchangeRateRepresentable(0m));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExchangeRate", ex.Message);
        Assert.Contains("DECIMAL(18,6)", ex.Message);

        Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.EnsureExchangeRateRepresentable(-1.5m));
        Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.EnsureExchangeRateRepresentable(1.0000005m));

        Assert.Equal(Max6, TaxRefundLedgerRules.EnsureExchangeRateRepresentable(Max6));
        Assert.Equal(7.123456m, TaxRefundLedgerRules.EnsureExchangeRateRepresentable(7.123456m));
    }

    // ==================== 3. Validate：与既有规则叠加，拒绝时逐字段不变 ====================

    [Fact]
    public void 校验_四位小数金额与六位小数汇率边界_通过且不改写字段()
    {
        var entity = Draft(customerId: null, exportAmount: Max4, exchangeRate: Max6, refundRate: 100m,
            refundableAmount: Max4, refundedAmount: Max4);

        TaxRefundLedgerRules.Validate(entity);

        Assert.Equal(Max4, entity.ExportAmount);
        Assert.Equal(Max6, entity.ExchangeRate);
        Assert.Equal(100m, entity.RefundRate);
        Assert.Equal(Max4, entity.RefundableAmount);
        Assert.Equal(Max4, entity.RefundedAmount);
    }

    [Fact]
    public void 校验_出口金额第五位小数_拒绝且逐字段不变()
    {
        var entity = Draft(customerId: null, exportAmount: 1000.00005m, exchangeRate: 7.123456m);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExportAmount", ex.Message);
        Assert.Contains("DECIMAL(18,4)", ex.Message);
        Assert.Equal(1000.00005m, entity.ExportAmount);
        Assert.Equal(7.123456m, entity.ExchangeRate);
        Assert.Equal(13m, entity.RefundRate);
    }

    [Fact]
    public void 校验_可退税与已退税第五位小数_拒绝()
    {
        var refundable = Draft(customerId: null, refundableAmount: 130.00005m, refundedAmount: 0m);
        var ex1 = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(refundable));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);
        Assert.Contains("RefundableAmount", ex1.Message);
        Assert.Equal(130.00005m, refundable.RefundableAmount);

        var refunded = Draft(customerId: null, refundableAmount: 200m, refundedAmount: 130.00001m);
        var ex2 = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(refunded));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
        Assert.Contains("RefundedAmount", ex2.Message);
        Assert.Equal(130.00001m, refunded.RefundedAmount);
    }

    [Fact]
    public void 校验_退税率第五位小数_拒绝()
    {
        var entity = Draft(customerId: null, refundRate: 13.00005m);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundRate", ex.Message);
        Assert.Equal(13.00005m, entity.RefundRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.000001)]
    [InlineData(-1)]
    public void 校验_汇率零或负_拒绝(decimal rate)
    {
        var entity = Draft(customerId: null, exchangeRate: rate);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExchangeRate", ex.Message);
        Assert.Equal(rate, entity.ExchangeRate);   // 绝不归零 / 回填默认值
    }

    [Theory]
    [InlineData("1.0000005")]                    // 第 7 位小数
    [InlineData("0.1234565")]
    [InlineData("1000000000000")]                // 1e12 量级溢出
    [InlineData("999999999999.9999995")]         // 半进位取整后恰好落到上限，但本身不可逐位原样存储
    public void 校验_汇率第七位小数或量级溢出_拒绝(string raw)
    {
        var rate = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var entity = Draft(customerId: null, exchangeRate: rate);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExchangeRate", ex.Message);
        Assert.Contains("DECIMAL(18,6)", ex.Message);
        Assert.Equal(rate, entity.ExchangeRate);
    }

    [Theory]
    [InlineData("100000000000000")]              // 1e14 量级溢出
    [InlineData("99999999999999.99995")]         // 半进位取整后越过上限
    [InlineData("99999999999999.99985")]         // 半进位取整后恰好落到上限（仍拒绝：绝不采信取整结果）
    public void 校验_金额量级溢出或半进位取整后才到上限_拒绝(string raw)
    {
        var amount = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var entity = Draft(customerId: null, exportAmount: amount, refundableAmount: amount, refundedAmount: 0m);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExportAmount", ex.Message);
        Assert.Equal(amount, entity.ExportAmount);
    }

    [Fact]
    public void 校验_极大十进制金额_拒绝()
    {
        var entity = Draft(customerId: null, exportAmount: decimal.MaxValue, refundableAmount: decimal.MaxValue);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExportAmount", ex.Message);
        Assert.Equal(decimal.MaxValue, entity.ExportAmount);
    }

    [Fact]
    public void 校验_既有非负与区间文案优先_顺序不变()
    {
        var negative = Draft(customerId: null, exportAmount: -1m);
        var ex1 = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(negative));
        Assert.Contains(TaxRefundLedgerRules.NegativeExportAmountText, ex1.Message);

        var badRate = Draft(customerId: null, refundRate: 101m);
        var ex2 = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(badRate));
        Assert.Contains(TaxRefundLedgerRules.InvalidRefundRateText, ex2.Message);

        var exceeds = Draft(customerId: null, refundableAmount: 100m, refundedAmount: 100.0001m);
        var ex3 = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(exceeds));
        Assert.Contains(TaxRefundLedgerRules.RefundedExceedsRefundableText, ex3.Message);
    }

    // ==================== 4. 真实控制器：新增 / 修改（拒绝零写入、零改写） ====================

    [Fact]
    public async Task 控制器_特权_新增四位小数金额与六位小数汇率_原样落库()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);

        var draft = Draft(null, exportAmount: 1234.5678m, exchangeRate: 7.123456m, refundRate: 13.1234m,
            refundableAmount: 160.4321m, refundedAmount: 0.0001m);
        var saved = AssertOk<BaseTaxRefund>(await Controller(db, admin.Id).Create(draft));

        var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == saved.Id);
        Assert.Equal(1234.5678m, stored.ExportAmount);
        Assert.Equal(7.123456m, stored.ExchangeRate);
        Assert.Equal(13.1234m, stored.RefundRate);
        Assert.Equal(160.4321m, stored.RefundableAmount);
        Assert.Equal(0.0001m, stored.RefundedAmount);
    }

    [Fact]
    public async Task 控制器_特权_新增不可表示金额_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Create(Draft(null, exportAmount: 1000.00005m)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExportAmount", ex.Message);
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());

        // 量级溢出（1e14）同样零写入。
        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Create(Draft(null, exportAmount: 100000000000000m)));
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());

        // 同一受权控制器随后合法创建成功（证明拒绝来自写入护栏而不是授权）。
        var ok = AssertOk<BaseTaxRefund>(await Controller(db, admin.Id).Create(Draft(null)));
        Assert.True(ok.Id > 0);
        Assert.Equal(1, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 控制器_特权_新增零或负汇率_拒绝且零写入(decimal rate)
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Create(Draft(null, exchangeRate: rate)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExchangeRate", ex.Message);
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_特权_修改不可表示退税率或汇率_拒绝且原行不变()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);
        var row = SeedLedger(db, "TR529-UPD", null, exportAmount: 700.1234m, exchangeRate: 6.54321m, refundRate: 9m);
        var updatedAt = row.UpdatedAt;

        var badRate = Draft(null, exportAmount: 800m, exchangeRate: 6.54321m, refundRate: 13.00005m);
        badRate.Id = row.Id;
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Update(row.Id, badRate));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundRate", ex.Message);

        var badRate2 = Draft(null, exchangeRate: 1.0000005m);
        badRate2.Id = row.Id;
        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Update(row.Id, badRate2));

        var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(700.1234m, stored.ExportAmount);
        Assert.Equal(6.54321m, stored.ExchangeRate);
        Assert.Equal(9m, stored.RefundRate);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 控制器_特权_修改为四位小数边界值_落库()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);
        var row = SeedLedger(db, "TR529-UPD2", null, exportAmount: 700m);

        var edit = Draft(null, exportAmount: Max4, exchangeRate: Max6, refundRate: 100m,
            refundableAmount: Max4, refundedAmount: Max4);
        edit.Id = row.Id;
        AssertOk<BaseTaxRefund>(await Controller(db, admin.Id).Update(row.Id, edit));

        var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(Max4, stored.ExportAmount);
        Assert.Equal(Max6, stored.ExchangeRate);
        Assert.Equal(100m, stored.RefundRate);
    }

    // ==================== 5. 源码契约（EF 映射同源 + 护栏先于写入 + 规则层不取整） ====================

    [Fact]
    public void 源码契约_EF映射与权威列声明同源()
    {
        var source = ReadSource("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");

        Assert.Contains(
            "modelBuilder.Entity<BaseTaxRefund>().Property(x => x.ExportAmount).HasPrecision(18, 4);", source);
        Assert.Contains(
            "modelBuilder.Entity<BaseTaxRefund>().Property(x => x.ExchangeRate).HasPrecision(18, 6);", source);
        Assert.Contains(
            "modelBuilder.Entity<BaseTaxRefund>().Property(x => x.RefundRate).HasPrecision(18, 4);", source);
        Assert.Contains(
            "modelBuilder.Entity<BaseTaxRefund>().Property(x => x.RefundableAmount).HasPrecision(18, 4);", source);
        Assert.Contains(
            "modelBuilder.Entity<BaseTaxRefund>().Property(x => x.RefundedAmount).HasPrecision(18, 4);", source);
    }

    [Fact]
    public void 源码契约_控制器在写入之前校验且规则层不取整()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "TaxRefundController.cs");
        var createStart = controller.IndexOf(
            "public override async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf(
            "public override async Task<IActionResult> Update", StringComparison.Ordinal);
        var deleteStart = controller.IndexOf(
            "public override async Task<IActionResult> Delete", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart && deleteStart > updateStart);

        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("TaxRefundLedgerRules.Validate", StringComparison.Ordinal)
                    < create.IndexOf("Service.CreateAsync", StringComparison.Ordinal));

        var update = controller[updateStart..deleteStart];
        Assert.True(update.IndexOf("TaxRefundLedgerRules.Validate", StringComparison.Ordinal)
                    < update.IndexOf("Service.UpdateAsync", StringComparison.Ordinal));

        var rules = ReadSource("src", "ERP.Application", "Services", "TaxRefundLedgerRules.cs");
        Assert.Contains("public static bool IsAmountRepresentable(decimal value)", rules);
        Assert.Contains("public static bool IsExchangeRateRepresentable(decimal value)", rules);
        Assert.DoesNotContain("entity.ExportAmount =", rules);   // 校验绝不回填 / 改写任何字段
        Assert.DoesNotContain("entity.ExchangeRate =", rules);
        Assert.DoesNotContain("entity.RefundRate =", rules);
        Assert.DoesNotContain("SysRoleMenus.Add", rules);        // 不新增任何授权
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));
}





