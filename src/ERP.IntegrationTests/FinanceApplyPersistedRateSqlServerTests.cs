using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-517 定金申请单 / 货款申请单「汇率存储边界」的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际列精度：EF 模型未显式 <c>HasPrecision</c> → 真实 <c>INFORMATION_SCHEMA</c> 汇率列为
/// <c>decimal(18,2)</c>，与 <see cref="FinancePersistedAmountRules"/> 常量同源；可表示汇率（含精确上限）原样往返；</item>
/// <item>物理事实：真实列把正汇率 <c>0.001</c> / <c>1.234568</c> / <c>7.123456</c> 静默收敛为 <c>0.00</c> / <c>1.23</c> /
/// <c>7.12</c>，越界值触发 SQL Server 算术溢出（8115）—— 因此必须在写入前拒绝；</item>
/// <item>创建 / 修改：受权账号提交「归一化为 0 的极小正汇率 / 会被真实列收敛或改写小数位的正汇率 / 越界极大汇率」一律在
/// **单号预约与任何写入之前**被受控拒绝（受权同一控制器随后合法创建成功，证明拒绝原因是汇率而非授权，且零表头写入、
/// 零占号），合法汇率照常落库；</item>
/// <item>提交 / 审核在既有行锁内复核**已持久化**汇率：非法历史快照被拒绝且不改写；取消等撤销动作**不新增**闸门；</item>
/// <item>合法汇率（关联来源销售订单与历史未关联来源）照常「创建 → 提交 → 审核」。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class FinanceApplyPersistedRateSqlServerTests
    : IClassFixture<FinanceApplyPersistedRateSqlServerFixture>
{
    private const decimal MaxRate = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal Tiny = 0.0000001m;          // 6 位小数取整后为 0
    private const decimal CoarsenZero = 0.001m;       // 真实列收敛为 0.00
    private const decimal CoarsenChange = 1.234568m;  // 真实列改写为 1.23
    private const decimal SixDecimalRate = 7.123456m; // 真实列改写为 7.12
    private const decimal BoundaryCross = 9_999_999_999_999_999.995m;
    private const decimal OneE16 = 10_000_000_000_000_000m;

    private readonly FinanceApplyPersistedRateSqlServerFixture _fixture;

    public FinanceApplyPersistedRateSqlServerTests(FinanceApplyPersistedRateSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceApplyPersistedRateSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 实际汇率列精度 + 可表示汇率原样往返 ====================

    [Fact]
    public async Task Actual_rate_column_precision_matches_rules_and_accepted_rates_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal("decimal(18,2)", ColumnType(typeof(FinanceDepositApply), nameof(FinanceDepositApply.ExchangeRate)));
        Assert.Equal("decimal(18,2)", ColumnType(typeof(FinancePaymentApply), nameof(FinancePaymentApply.ExchangeRate)));

        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinanceDepositApplies", "ExchangeRate"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinancePaymentApplies", "ExchangeRate"));

        var customerId = await SeedCustomerAsync(db, "汇率精度边界客户");

        var depositMax = await SeedDepositApplyAsync(db, $"DJ-RATE-{Tag()}", customerId, MaxRate);
        var depositPlain = await SeedDepositApplyAsync(db, $"DJ-RATE-{Tag()}", customerId, 7.2m);
        var paymentMax = await SeedPaymentApplyAsync(db, $"HK-RATE-{Tag()}", customerId, MaxRate);
        var paymentPlain = await SeedPaymentApplyAsync(db, $"HK-RATE-{Tag()}", customerId, 0.01m);

        Assert.Equal(MaxRate, await RateAsync<FinanceDepositApply>(db, depositMax.Id));
        Assert.Equal(7.2m, await RateAsync<FinanceDepositApply>(db, depositPlain.Id));
        Assert.Equal(MaxRate, await RateAsync<FinancePaymentApply>(db, paymentMax.Id));
        Assert.Equal(0.01m, await RateAsync<FinancePaymentApply>(db, paymentPlain.Id));
    }

    [Fact]
    public async Task Sql_server_physically_coarsens_positive_rates_and_overflows_huge_rates()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        // 真实列精度 decimal(18,2)：正汇率被静默收敛为 0.00 / 改写小数位（这正是必须在写入前拒绝的物理事实）。
        Assert.Equal(0m, await DecimalScalarAsync(db, "SELECT CAST(0.001 AS decimal(18,2)) AS [Value]"));
        Assert.Equal(0m, await DecimalScalarAsync(db, "SELECT CAST(0.0000001 AS decimal(18,2)) AS [Value]"));
        Assert.Equal(1.23m, await DecimalScalarAsync(db, "SELECT CAST(1.234568 AS decimal(18,2)) AS [Value]"));
        Assert.Equal(7.12m, await DecimalScalarAsync(db, "SELECT CAST(7.123456 AS decimal(18,2)) AS [Value]"));

        // 越界值由 SQL Server 自身报「算术溢出」（错误号 8115，与语言无关）。
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            DecimalScalarAsync(db, "SELECT CAST(10000000000000000.00 AS decimal(18,2)) AS [Value]"));
        Assert.Equal(8115, ex.Number);
    }

    [Fact]
    public async Task Raw_ef_write_of_unpersistable_rate_shows_actual_column_coarsening()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "汇率物理收敛客户");

        var coarsened = await SeedDepositApplyAsync(db, $"DJ-COARSE-{Tag()}", customerId, CoarsenChange);
        var coarsenedPayment = await SeedPaymentApplyAsync(db, $"HK-COARSE-{Tag()}", customerId, CoarsenChange);
        var zeroed = await SeedDepositApplyAsync(db, $"DJ-ZERO-{Tag()}", customerId, Tiny);

        // 原始 EF 写入（绕过应用层守卫）：真实 decimal(18,2) 列把汇率静默收敛 / 归零 —— 应用层因此必须在写入前拒绝。
        Assert.Equal(1.23m, await RateAsync<FinanceDepositApply>(db, coarsened.Id));
        Assert.Equal(1.23m, await RateAsync<FinancePaymentApply>(db, coarsenedPayment.Id));
        Assert.Equal(0m, await RateAsync<FinanceDepositApply>(db, zeroed.Id));
    }

    // ==================== 2. 创建 / 修改：不可原样落库汇率拒绝且零写入 / 零占号 ====================

    [Theory]
    [InlineData("tiny")]
    [InlineData("coarsen-zero")]
    [InlineData("coarsen-change")]
    [InlineData("six-decimal")]
    [InlineData("boundary-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Create_deposit_apply_rejects_unpersistable_rate_without_numbering_or_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "定金汇率越界客户");
        var userId = await SeedUserAsync(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode, "定金汇率边界操作员");
        var controller = NewDepositController(db, userId);

        var before = await DepositSnapshotAsync(db, customerId);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(DepositDraft(customerId, Rate(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        await AssertDepositRejectedWithoutSideEffectsAsync(db, customerId, before);

        // 同一受权控制器随后合法创建成功：证明拒绝原因是汇率而非授权，单号只被合法请求消耗。
        var id = CreatedId(await controller.Create(DepositDraft(customerId, 7.2m)));
        var after = await DepositSnapshotAsync(db, customerId);
        Assert.True(id > 0);
        Assert.Equal(before.Documents + 1, after.Documents);
        Assert.Equal(before.Sequence + 1, after.Sequence);
        Assert.Equal(7.2m, await RateAsync<FinanceDepositApply>(db, id));
    }

    [Theory]
    [InlineData("tiny")]
    [InlineData("coarsen-zero")]
    [InlineData("coarsen-change")]
    [InlineData("six-decimal")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Create_payment_apply_rejects_unpersistable_rate_without_numbering_or_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "货款汇率越界客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "货款汇率边界操作员");
        var controller = NewPaymentApplyController(db, userId);

        var before = await PaymentApplySnapshotAsync(db, customerId);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(ApplyDraft(customerId, Rate(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        await AssertPaymentApplyRejectedWithoutSideEffectsAsync(db, customerId, before);

        var id = CreatedId(await controller.Create(ApplyDraft(customerId, 7.2m)));
        var after = await PaymentApplySnapshotAsync(db, customerId);
        Assert.True(id > 0);
        Assert.Equal(before.Documents + 1, after.Documents);
        Assert.Equal(before.Sequence + 1, after.Sequence);
        Assert.Equal(7.2m, await RateAsync<FinancePaymentApply>(db, id));
    }

    [Fact]
    public async Task Update_deposit_apply_rejects_unpersistable_rate_and_leaves_stored_apply_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "定金汇率修改客户");
        var userId = await SeedUserAsync(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode, "定金汇率修改操作员");
        var controller = NewDepositController(db, userId);
        var id = CreatedId(await controller.Create(DepositDraft(customerId, 1m)));
        var before = await db.FinanceDepositApplies.AsNoTracking().SingleAsync(a => a.Id == id);

        var request = DepositDraft(customerId, CoarsenChange);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.FinanceDepositApplies.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(before.ExchangeRate, after.ExchangeRate);
        Assert.Equal(before.ApplyNo, after.ApplyNo);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        // 合法汇率（精确上限）修改照常生效并原样落库；被拒的第一次尝试已整体回滚（含行锁时间戳），
        // 因此这里清空变更跟踪器，让同一测试上下文按数据库真实并发令牌重新加载（生产端每次请求各自持有上下文）。
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await controller.Update(id, DepositDraft(customerId, MaxRate)));
        Assert.Equal(MaxRate, await RateAsync<FinanceDepositApply>(db, id));
    }

    [Fact]
    public async Task Update_payment_apply_rejects_unpersistable_rate_and_leaves_stored_apply_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "货款汇率修改客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "货款汇率修改操作员");
        var controller = NewPaymentApplyController(db, userId);
        var id = CreatedId(await controller.Create(ApplyDraft(customerId, 1m)));
        var before = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == id);

        var request = ApplyDraft(customerId, Tiny);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(before.ExchangeRate, after.ExchangeRate);
        Assert.Equal(before.ApplyNo, after.ApplyNo);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        // 合法汇率修改照常生效并原样落库；被拒的第一次尝试已整体回滚（含行锁时间戳），
        // 因此清空变更跟踪器，让同一测试上下文按数据库真实并发令牌重新加载（生产端每次请求各自持有上下文）。
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await controller.Update(id, ApplyDraft(customerId, 7.2m)));
        Assert.Equal(7.2m, await RateAsync<FinancePaymentApply>(db, id));
    }

    // ==================== 3. 提交 / 审核：锁内复核已持久化汇率（撤销语义不变） ====================

    [Fact]
    public async Task Submit_and_approve_reject_malformed_legacy_rate_without_rewriting_snapshot()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "汇率历史快照客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "汇率历史快照操作员");
        var controller = NewPaymentApplyController(db, userId);

        // 真实列写入的「历史快照」：decimal(18,2) 已把 0.001 收敛为 0.00（非法持久化值）。
        var zeroed = await SeedPaymentApplyAsync(db, $"HK-LEGACY-{Tag()}", customerId, 0m);
        var submitted = await SeedPaymentApplyAsync(db, $"HK-LEGACY-{Tag()}", customerId, 0m,
            DocumentStatus.Submitted);
        var zeroedBefore = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == zeroed.Id);
        var submittedBefore = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == submitted.Id);

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(zeroed.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, submitEx.Code);
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(submitted.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, approveEx.Code);

        // 非法历史快照被拒绝且原样保留（状态 / 汇率 / 审计不变，绝不静默修复）。
        var zeroedAfter = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == zeroed.Id);
        var submittedAfter = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == submitted.Id);
        Assert.Equal(zeroedBefore.Status, zeroedAfter.Status);
        Assert.Equal(zeroedBefore.ExchangeRate, zeroedAfter.ExchangeRate);
        Assert.Equal(zeroedBefore.UpdatedAt, zeroedAfter.UpdatedAt);
        Assert.Equal(submittedBefore.Status, submittedAfter.Status);
        Assert.Equal(submittedBefore.ExchangeRate, submittedAfter.ExchangeRate);
        Assert.Equal(submittedBefore.UpdatedAt, submittedAfter.UpdatedAt);
    }

    // ==================== 4. 合法汇率生命周期与撤销语义（真实 SQL） ====================

    [Fact]
    public async Task Linked_and_manual_lifecycles_persist_exact_rate_and_cancel_keeps_undo_semantics()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "汇率生命周期客户");
        var depositUserId = await SeedUserAsync(
            db, FinanceDepositApplyLifecycleRules.RequiredMenuCode, "汇率生命周期定金操作员");
        var paymentUserId = await SeedUserAsync(
            db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "汇率生命周期货款操作员");
        var orderId = await SeedSalesOrderAsync(db, customerId);

        // 定金申请单：显式关联来源销售订单 + 合法汇率 → 创建 / 提交 / 审核并原样落库。
        var depositController = NewDepositController(db, depositUserId);
        var depositBefore = await DepositSnapshotAsync(db, customerId);
        var depositId = CreatedId(await depositController.Create(
            DepositDraft(customerId, 7.20000004m, orderId)));
        Assert.IsType<OkObjectResult>(await depositController.Submit(depositId));
        Assert.IsType<OkObjectResult>(await depositController.Approve(depositId));
        var storedDeposit = await db.FinanceDepositApplies.AsNoTracking().SingleAsync(a => a.Id == depositId);
        Assert.Equal(DocumentStatus.Approved, storedDeposit.Status);
        Assert.Equal(7.2m, storedDeposit.ExchangeRate);
        Assert.Equal(orderId, storedDeposit.SalesOrderId);
        Assert.Equal(depositBefore.Sequence + 1, (await DepositSnapshotAsync(db, customerId)).Sequence);

        // 货款申请单：历史「未关联来源」+ 合法汇率 → 创建 / 提交 / 审核并原样落库。
        var paymentController = NewPaymentApplyController(db, paymentUserId);
        var applyBefore = await PaymentApplySnapshotAsync(db, customerId);
        var paymentId = CreatedId(await paymentController.Create(ApplyDraft(customerId, 0.01m)));
        Assert.IsType<OkObjectResult>(await paymentController.Submit(paymentId));
        Assert.IsType<OkObjectResult>(await paymentController.Approve(paymentId));
        var storedPayment = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == paymentId);
        Assert.Equal(DocumentStatus.Approved, storedPayment.Status);
        Assert.Equal(0.01m, storedPayment.ExchangeRate);
        Assert.Null(storedPayment.SalesOrderId);
        Assert.Equal(applyBefore.Sequence + 1, (await PaymentApplySnapshotAsync(db, customerId)).Sequence);

        // 取消：非法历史快照（真实列已收敛为 0.00 的汇率）不阻止既有撤销语义，也绝不改写快照。
        var legacy = await SeedPaymentApplyAsync(db, $"HK-CANCEL-{Tag()}", customerId, 0m);
        Assert.IsType<OkObjectResult>(await paymentController.Cancel(legacy.Id));
        var cancelled = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == legacy.Id);
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(0m, cancelled.ExchangeRate);
    }

    // ==================== 5. 真实 SQL 夹具种子与脚手架 ====================

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

    private static FinanceDepositApplyController NewDepositController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceDepositApplyController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static FinancePaymentApplyController NewPaymentApplyController(ErpDbContext db, long? userId)
    {
        var controller = new FinancePaymentApplyController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static Task<decimal> RateAsync<TEntity>(ErpDbContext db, long id) where TEntity : class
        => typeof(TEntity) == typeof(FinanceDepositApply)
            ? db.FinanceDepositApplies.AsNoTracking().Where(a => a.Id == id).Select(a => a.ExchangeRate).SingleAsync()
            : db.FinancePaymentApplies.AsNoTracking().Where(a => a.Id == id).Select(a => a.ExchangeRate).SingleAsync();

    private static Task<decimal> DecimalScalarAsync(ErpDbContext db, string sql)
        => db.Database.SqlQueryRaw<decimal>(sql).SingleAsync();

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"ERC-{Tag()}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedSalesOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-RATE-{Tag()}",
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.CNY,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task<FinanceDepositApply> SeedDepositApplyAsync(
        ErpDbContext db, string applyNo, long customerId, decimal exchangeRate,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var apply = new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.CNY,
            ExchangeRate = exchangeRate,
            BankAccount = "ACC-517",
            Payee = "收款方",
            Reason = "ERP-517_SQL_TEST",
            Remark = "ERP-517_SQL_TEST",
            Status = status
        };
        db.FinanceDepositApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    private static async Task<FinancePaymentApply> SeedPaymentApplyAsync(
        ErpDbContext db, string applyNo, long customerId, decimal exchangeRate,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.CNY,
            ExchangeRate = exchangeRate,
            BankAccount = "ACC-517",
            Payee = "义乌档口",
            Reason = "ERP-517_SQL_TEST",
            Remark = "ERP-517_SQL_TEST",
            Status = status
        };
        db.FinancePaymentApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    private static FinanceDepositApply DepositDraft(long customerId, decimal exchangeRate, long? salesOrderId = null)
        => new()
        {
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.CNY,
            SalesOrderId = salesOrderId,
            ExchangeRate = exchangeRate,
            BankAccount = "ACC-517",
            Payee = "收款方",
            Reason = "ERP-517_SQL_TEST",
            Remark = "ERP-517_SQL_TEST"
        };

    private static FinancePaymentApply ApplyDraft(long customerId, decimal exchangeRate)
        => new()
        {
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.CNY,
            ExchangeRate = exchangeRate,
            BankAccount = "ACC-517",
            Payee = "义乌档口",
            Reason = "ERP-517_SQL_TEST",
            Remark = "ERP-517_SQL_TEST"
        };

    /// <summary>播种真实授权账号：既有菜单授权 + 启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string menuCode, string displayName)
    {
        var role = new SysRole { RoleCode = $"RATE-{Tag()}", RoleName = displayName, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"rate-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = displayName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>本用例名下单据数与既有单号序列基线（夹具库被多个用例复用，拒绝断言一律相对快照）。</summary>
    private sealed record AmountSnapshot(int Documents, long Sequence);

    private static Task<AmountSnapshot> DepositSnapshotAsync(ErpDbContext db, long customerId)
        => SnapshotAsync(db, DocumentType.DepositApply,
            db.FinanceDepositApplies.CountAsync(a => a.CustomerId == customerId));

    private static Task<AmountSnapshot> PaymentApplySnapshotAsync(ErpDbContext db, long customerId)
        => SnapshotAsync(db, DocumentType.PaymentApply,
            db.FinancePaymentApplies.CountAsync(a => a.CustomerId == customerId));

    private static async Task<AmountSnapshot> SnapshotAsync(ErpDbContext db, DocumentType type, Task<int> documents)
        => new(await documents, await SequenceAsync(db, type));

    private static Task<long> SequenceAsync(ErpDbContext db, DocumentType type)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == type && !r.IsDeleted)
            .Select(r => r.CurrentSequence).SingleAsync();

    private static async Task AssertDepositRejectedWithoutSideEffectsAsync(
        ErpDbContext db, long customerId, AmountSnapshot before)
    {
        Assert.Equal(before.Documents, await db.FinanceDepositApplies.CountAsync(a => a.CustomerId == customerId));
        Assert.Equal(before.Sequence, await SequenceAsync(db, DocumentType.DepositApply));
    }

    private static async Task AssertPaymentApplyRejectedWithoutSideEffectsAsync(
        ErpDbContext db, long customerId, AmountSnapshot before)
    {
        Assert.Equal(before.Documents, await db.FinancePaymentApplies.CountAsync(a => a.CustomerId == customerId));
        Assert.Equal(before.Sequence, await SequenceAsync(db, DocumentType.PaymentApply));
    }

    private static string ColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=(localdb)\\NEWERP_AutoAcceptance;" +
                          "Database=NEWERP_AUTOTEST_ERP517_MODEL_ONLY;Integrated Security=true;" +
                          "TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>），证明实际存储精度与规则常量同源。</summary>
    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-517）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class FinanceApplyPersistedRateSqlServerFixture : IAsyncLifetime
{
    internal const string InstanceMarker = "NEWERP_AutoAcceptance";
    internal const string DatabasePrefix = "NEWERP_AUTOTEST";
    private const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP517";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-517] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-517] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceApplyPersistedRateTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            FinanceApplyPersistedRateSqlServerFixture.AssertDedicatedTarget(connection));
}

