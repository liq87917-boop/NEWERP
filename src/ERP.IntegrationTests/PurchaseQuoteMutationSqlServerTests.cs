using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-417 供应商比价生命周期与审批决定的真实 SQL Server 并发 / 原子性集成测试。
/// <list type="number">
/// <item><b>真实既有授权 + 真实控制器</b>：复用 ERP-416 已播种的既有「供应商比价」<c>purchase-quote</c> /
/// 「采购订单」<c>purchase-order</c> 菜单授权与业务员客户数据范围，不新增 / 不修改任何权限模型，无匿名 / 管理员降级。</item>
/// <item><b>两个独立连接竞态</b>（真实 GUID 独占库、两条真实 <see cref="ErpDbContext"/> 连接）：
/// ① 审批 vs 拒绝 → 恰好一条决定、受控输家；② 审批 vs 商业编辑 → 一致结果（批准快照不变）或编辑先行条款。</item>
/// <item><b>原子性</b>：审批 vs 删除恰好一个赢家；混合批量删除全有或全无；伪造决定人被登录账号覆盖；
/// 强制保存失败整体回滚（锁刷新与新增决定都不落库）。</item>
/// <item><b>保留库存来源单据审计与原始失败日志</b>：只读取计数与权威行，不删除 / 不清理任何既有行。</item>
/// <item><b>专用目标护栏</b>：访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；错误实例 / 错误库名 / 非集成安全一律 fail closed。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才构成阶段验收证据。</para>
/// </summary>
public sealed class PurchaseQuoteMutationSqlServerTests
    : IClassFixture<PurchaseQuoteAuthorizationSqlServerFixture>
{
    private readonly PurchaseQuoteAuthorizationSqlServerFixture _fixture;

    public PurchaseQuoteMutationSqlServerTests(PurchaseQuoteAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard() => PurchaseQuoteAuthorizationSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    // ==================== 专用目标护栏（fail closed，访问数据库之前） ====================

    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void 专用目标护栏_错误目标在访问数据库之前拒绝(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseQuoteAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void 真实SQL上下文_判定为关系型_且比价行锁语句与既有协议同源()
    {
        Guard();
        using var db = _fixture.CreateDbContext();
        Assert.True(PurchaseQuoteMutationRules.IsRelationalProvider(db));
        Assert.Equal("SELECT Id FROM db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            PurchaseQuoteMutationRules.QuoteRowLockSql);
    }

    // ==================== 两个独立连接竞态：审批 vs 拒绝 ====================

    [Fact]
    public async Task 竞态_审批与拒绝_恰好一条决定_受控输家()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> ApproveAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => DecisionController(dbA, _fixture.OperatorUserId).Decide(
                new PurchaseQuoteDecisionRequest { QuoteId = quoteId, Decision = PurchaseQuoteApproval.Approved }));
        }

        async Task<Outcome> RejectAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => DecisionController(dbB, _fixture.OperatorUserId).Decide(
                new PurchaseQuoteDecisionRequest { QuoteId = quoteId, Decision = PurchaseQuoteApproval.Rejected }));
        }

        var approve = ApproveAsync();
        var reject = RejectAsync();
        gate.Release(2);
        var results = new[] { await approve, await reject };

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = Assert.Single(results.Where(r => !r.Success));
        Assert.Contains(ErrorCodes.RuleConflict.ToString(), loser.Error, StringComparison.Ordinal);

        await using var check = _fixture.CreateDbContext();
        var decisions = await check.PurchaseQuoteDecisions.AsNoTracking()
            .Where(d => d.QuoteId == quoteId && !d.IsDeleted).ToListAsync();
        Assert.Single(decisions); // append-only：赢家恰好落一条，输家受控拒绝
    }

    // ==================== 两个独立连接竞态：审批 vs 商业编辑 ====================

    [Fact]
    public async Task 竞态_审批与商业编辑_一致结果_批准快照不变或编辑先行条款()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> ApproveAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => DecisionController(dbA, _fixture.OperatorUserId).Decide(
                new PurchaseQuoteDecisionRequest { QuoteId = quoteId, Decision = PurchaseQuoteApproval.Approved }));
        }

        async Task<Outcome> EditAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(async () =>
            {
                var current = await dbB.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
                await QuoteController(dbB, _fixture.OperatorUserId).Update(quoteId, CloneWithQuantity(current, 999m));
            });
        }

        var approve = ApproveAsync();
        var edit = EditAsync();
        gate.Release(2);
        var approveResult = await approve;
        var editResult = await edit;

        await using var check = _fixture.CreateDbContext();
        var stored = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal(1, await check.PurchaseQuoteDecisions.AsNoTracking()
            .CountAsync(d => d.QuoteId == quoteId && !d.IsDeleted));
        Assert.True(approveResult.Success, approveResult.Error);

        if (editResult.Success)
        {
            // 编辑先赢：批准基于编辑后的条款（编辑先行条款）。
            Assert.Equal(999m, stored.Quantity);
        }
        else
        {
            // 批准先赢：商业编辑在锁内看到审批决定被原子拒绝，批准快照条款不变。
            Assert.Equal(100m, stored.Quantity);
            Assert.Contains(PurchaseQuoteMutationRules.DecidedImmutableText, editResult.Error, StringComparison.Ordinal);
        }
    }

    // ==================== 原子性：审批 vs 删除 / 混合批量删除 / 伪造决定人 ====================

    [Fact]
    public async Task 竞态_审批与删除_恰好一个赢家_绝不批准又被删()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> ApproveAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => DecisionController(dbA, _fixture.OperatorUserId).Decide(
                new PurchaseQuoteDecisionRequest { QuoteId = quoteId, Decision = PurchaseQuoteApproval.Approved }));
        }

        async Task<Outcome> DeleteAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => QuoteController(dbB, _fixture.OperatorUserId).Delete(quoteId));
        }

        var approve = ApproveAsync();
        var delete = DeleteAsync();
        gate.Release(2);
        var approveResult = await approve;
        var deleteResult = await delete;

        await using var check = _fixture.CreateDbContext();
        var row = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        var decisionCount = await check.PurchaseQuoteDecisions.AsNoTracking()
            .CountAsync(d => d.QuoteId == quoteId && !d.IsDeleted);

        Assert.True(approveResult.Success ^ deleteResult.Success, $"approve={approveResult.Error}; delete={deleteResult.Error}");
        if (approveResult.Success)
        {
            Assert.False(row.IsDeleted);
            Assert.Equal(1, decisionCount);
            Assert.Contains(PurchaseQuoteMutationRules.DecidedNoDeleteText, deleteResult.Error, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(row.IsDeleted);
            Assert.Equal(0, decisionCount);
        }
    }

    [Fact]
    public async Task 混合批量删除_全有或全无()
    {
        Guard();
        long aId, bId, decidedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            aId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;
            bId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;
            decidedId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;
            seed.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
            {
                QuoteId = decidedId, QuoteNo = $"PQ-417-{Guid.NewGuid():N}"[..20], Decision = PurchaseQuoteApproval.Approved,
                SelectedSupplierId = 88L, DecidedBy = _fixture.OperatorUserId, DecidedByName = "ERP417 隔离账号",
                DecidedAt = DateTime.Now, DecisionRef = $"APV-{decidedId}",
                // ERP-417：归属决定（实时审批生命周期追加）冻结软删除。
                CreatedAt = DateTime.Now, CreatedBy = _fixture.OperatorUserId
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _fixture.CreateDbContext();
        var ctl = QuoteController(db, _fixture.OperatorUserId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.BatchDelete(new List<long> { aId, bId, decidedId }));
        Assert.Equal(PurchaseQuoteMutationRules.DecidedNoDeleteText, ex.Message);

        await using (var check = _fixture.CreateDbContext())
        {
            var rows = await check.PurchaseQuotes.AsNoTracking()
                .Where(q => new[] { aId, bId, decidedId }.Contains(q.Id)).ToListAsync();
            Assert.All(rows, r => Assert.False(r.IsDeleted)); // 全有或全无：一条不合格即全部不删
        }

        await using (var db2 = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await QuoteController(db2, _fixture.OperatorUserId)
                .BatchDelete(new List<long> { aId, bId }));

        await using (var check = _fixture.CreateDbContext())
        {
            Assert.True(await check.PurchaseQuotes.AsNoTracking().Where(q => q.Id == aId).Select(q => q.IsDeleted).SingleAsync());
            Assert.True(await check.PurchaseQuotes.AsNoTracking().Where(q => q.Id == bId).Select(q => q.IsDeleted).SingleAsync());
        }
    }

    [Fact]
    public async Task 伪造决定人_被登录账号覆盖()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false)).Id;

        string liveName;
        await using var db = _fixture.CreateDbContext();
        liveName = await db.SysUsers.AsNoTracking().Where(u => u.Id == _fixture.OperatorUserId)
            .Select(u => u.DisplayName).SingleAsync();

        var ok = Assert.IsType<OkObjectResult>(await DecisionController(db, _fixture.OperatorUserId).Decide(
            new PurchaseQuoteDecisionRequest
            {
                QuoteId = quoteId, Decision = PurchaseQuoteApproval.Approved,
                DecidedBy = 9_000_000L, DecidedByName = "伪造决定人"
            }));
        var decision = Assert.IsType<ApiResponse<PurchaseQuoteDecision>>(ok.Value).Data!;

        Assert.Equal(_fixture.OperatorUserId, decision.DecidedBy);
        Assert.Equal(liveName, decision.DecidedByName);
        Assert.NotEqual("伪造决定人", decision.DecidedByName);
    }

    // ==================== 强制保存失败：整体回滚（锁刷新 + 新增决定都不落库） ====================

    [Fact]
    public async Task 强制保存失败_回滚比价与决定全部变更()
    {
        Guard();
        long quoteId;
        DateTime? beforeUpdatedAt;
        await using (var seed = _fixture.CreateDbContext())
        {
            var quote = await AddOwnQuoteAsync(seed, PurchaseQuoteMutationRules.PendingStatus, selected: false);
            quoteId = quote.Id;
            beforeUpdatedAt = quote.UpdatedAt;
        }

        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .AddInterceptors(new FailingDecisionSaveInterceptor())
            .Options;
        await using (var db = new ErpDbContext(options))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => PurchaseQuoteApproval.DecideAsync(db,
                new PurchaseQuoteDecisionRequest { QuoteId = quoteId, Decision = PurchaseQuoteApproval.Approved },
                new PurchaseQuoteAuthorizationRules.LiveActor(_fixture.OperatorUserId, "ERP417 强制失败")));
            Assert.Contains("ERP-417 forced", ex.Message, StringComparison.Ordinal);
        }

        await using var check = _fixture.CreateDbContext();
        Assert.Empty(await check.PurchaseQuoteDecisions.AsNoTracking()
            .Where(d => d.QuoteId == quoteId && !d.IsDeleted).ToListAsync());
        var stored = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal(beforeUpdatedAt, stored.UpdatedAt); // 行锁的审计时间戳刷新同样被整体回滚
    }

    // ==================== 工厂与夹具 ====================

    private static PurchaseQuoteController QuoteController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase/quotes";
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static PurchaseQuoteDecisionController DecisionController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase/quote-decisions";
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return new PurchaseQuoteDecisionController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private sealed record Outcome(bool Success, string Error);

    private static async Task<Outcome> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return new Outcome(true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return new Outcome(false, $"{ex.Code}:{ex.Message}");
        }
    }

    /// <summary>播种一条属于受限业务员客户数据范围的、未审批 / 未转换的比价行（全新行，不清理既有数据）。</summary>
    private async Task<PurchaseQuote> AddOwnQuoteAsync(ErpDbContext db, string status, bool selected)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = $"PQ-417-{Tag()}",
            QuoteDate = new DateTime(2026, 10, 1),
            ProductId = 310L,
            ProductName = "ERP417 SQL 商品",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 100m,
            SupplierId = 88L,
            SupplierName = "ERP417 SQL 档口",
            SupplierType = "档口",
            QuotePrice = 2m,
            TotalAmount = 200m,
            Currency = "USD",
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = selected,
            Status = status,
            CustomerId = _fixture.CustomerAId,
            CustomerName = "ERP416 可见客户",
            Remark = "ERP417_SQL_TEST"
        };
        db.PurchaseQuotes.Add(quote);
        await db.SaveChangesAsync();
        return quote;
    }

    private static PurchaseQuote CloneWithQuantity(PurchaseQuote quote, decimal quantity) => new()
    {
        Id = quote.Id,
        QuoteNo = quote.QuoteNo,
        QuoteDate = quote.QuoteDate,
        ProductId = quote.ProductId,
        ProductName = quote.ProductName,
        Spec = quote.Spec,
        Unit = quote.Unit,
        Quantity = quantity,
        SupplierId = quote.SupplierId,
        SupplierName = quote.SupplierName,
        SupplierType = quote.SupplierType,
        QuotePrice = quote.QuotePrice,
        TotalAmount = quote.TotalAmount,
        Currency = quote.Currency,
        TaxIncluded = quote.TaxIncluded,
        DeliveryDays = quote.DeliveryDays,
        MinOrderQty = quote.MinOrderQty,
        PaymentTerms = quote.PaymentTerms,
        IsSelected = quote.IsSelected,
        Status = quote.Status,
        CustomerId = quote.CustomerId,
        CustomerName = quote.CustomerName,
        RefOrderNo = quote.RefOrderNo,
        Remark = quote.Remark
    };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// ERP-417 强制保存失败注入：仅在**新增审批决定**时抛出，用于证明「行锁的审计时间戳刷新 + 新增决定」
/// 在同一事务内被整体回滚（失败侧零部分写入）。
/// </summary>
internal sealed class FailingDecisionSaveInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfDecisionAdded(eventData);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ThrowIfDecisionAdded(eventData);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ThrowIfDecisionAdded(DbContextEventData eventData)
    {
        if (eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<PurchaseQuoteDecision>().Any(e => e.State == EntityState.Added))
            throw new InvalidOperationException("ERP-417 forced save failure");
    }
}
