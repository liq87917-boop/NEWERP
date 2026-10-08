using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-417 供应商比价生命周期与审批决定并发护栏单元测试（全部使用内存库，不连接 SQL Server）：
/// 行锁 / 原子事务契约（与既有父单证锁同源、Id 升序、非关系型等价无操作）；待比较草稿校验
/// （正数量 / 非负价格 / EF 精度 / 受支持币种 / 选中与状态一致 / 服务端重算总额）；
/// 伪造「已转采购订单」状态、采购单号式 RefOrderNo 与审计字段一律拒绝 / 忽略；
/// 已存在审批决定后商业条款 / 改派 / 选中 / 删除一律拒绝，仅允许安全非商业备注修改；
/// 已转换行冻结；批量删除全有或全无；决定人取自可信操作人（伪造输入被忽略）。
/// 说明：真实 SQL Server 并发竞态在 <c>PurchaseQuoteMutationSqlServerTests</c>（受控 localdb）覆盖。
/// </summary>
public class PurchaseQuoteMutationTests
{
    // ==================== 1. 锁 / 事务契约 ====================

    [Fact]
    public async Task 非关系型_锁定与事务等价无操作()
    {
        using var db = TestDbFactory.Create();
        Assert.False(PurchaseQuoteMutationRules.IsRelationalProvider(db));
        Assert.Null(await PurchaseQuoteMutationRules.BeginMutationTransactionAsync(db));
    }

    [Fact]
    public async Task 行锁_非关系型直接放行_且非法Id为假()
    {
        using var db = TestDbFactory.Create();
        Assert.True(await PurchaseQuoteMutationRules.LockQuoteRowAsync(db, 1));
        Assert.False(await PurchaseQuoteMutationRules.LockQuoteRowAsync(db, 0));
    }

    [Fact]
    public void MergeLockIds_去重_仅正整数_Id升序()
    {
        Assert.Equal(new long[] { 2, 5, 9 },
            PurchaseQuoteMutationRules.MergeLockIds(new long[] { 5, 0, -1, 2, 5, 9 }).ToArray());
        Assert.Empty(PurchaseQuoteMutationRules.MergeLockIds(null));
    }

    [Fact]
    public void 锁语句与状态常量_与既有转换口径逐字一致()
    {
        Assert.Equal("SELECT Id FROM db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            PurchaseQuoteMutationRules.QuoteRowLockSql);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, PurchaseQuoteMutationRules.SelectedStatus);
        Assert.Equal(PurchaseQuoteConversion.DiscardedStatus, PurchaseQuoteMutationRules.DiscardedStatus);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, PurchaseQuoteMutationRules.ConvertedStatus);
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, new PurchaseQuote().Status);
        Assert.Contains("Id 升序", PurchaseQuoteMutationRules.LockOrderText);
        Assert.Equal(PurchaseQuoteMutationRules.DecimalScale, 4);
    }

    // ==================== 2. 待比较草稿校验 ====================

    [Fact]
    public void 草稿校验_正数量与非负价格_服务端重算总额()
    {
        var quote = NewDraft("PQ-VALID");
        quote.Quantity = 10m;
        quote.QuotePrice = 3m;
        quote.TotalAmount = 1m; // 客户端伪造总额，服务端必须按单价 × 数量重算
        quote.Currency = "usd";

        PurchaseQuoteMutationRules.ValidateDraft(quote);

        Assert.Equal(30m, quote.TotalAmount);
        Assert.Equal("USD", quote.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 草稿校验_数量非正_拒绝(decimal quantity)
    {
        var quote = NewDraft("PQ-QTY");
        quote.Quantity = quantity;
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, ex.Message);
    }

    [Fact]
    public void 草稿校验_数量超出EF精度_拒绝()
    {
        var quote = NewDraft("PQ-QTY-PREC");
        quote.Quantity = 1.00001m; // 超过 4 位小数
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, ex.Message);

        quote.Quantity = 100_000_000_000_000m; // 超过 DECIMAL(18,4)
        Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
    }

    [Fact]
    public void 草稿校验_单价为负或超精度_拒绝()
    {
        var quote = NewDraft("PQ-PRICE");
        quote.QuotePrice = -0.0001m;
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(PurchaseQuoteMutationRules.PriceText, ex.Message);

        quote.QuotePrice = 1.23456m;
        Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
    }

    [Fact]
    public void 草稿校验_币种不支持_拒绝()
    {
        var quote = NewDraft("PQ-CUR");
        quote.Currency = "EUR";
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(PurchaseQuoteMutationRules.CurrencyText, ex.Message);
    }

    [Fact]
    public void 草稿校验_选中与状态不一致_拒绝()
    {
        var quote = NewDraft("PQ-SEL");
        quote.Status = PurchaseQuoteMutationRules.PendingStatus;
        quote.IsSelected = true;
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(PurchaseQuoteMutationRules.SelectionText, ex.Message);
    }

    [Fact]
    public void 草稿校验_伪造已转采购订单状态_拒绝()
    {
        var quote = NewDraft("PQ-CVT");
        quote.Status = PurchaseQuoteMutationRules.ConvertedStatus;
        quote.IsSelected = false;
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(PurchaseQuoteMutationRules.StatusText, ex.Message);
    }

    // ==================== 3. 客户端伪造转换证据 ====================

    [Fact]
    public async Task 新增_RefOrderNo指向既有采购单号_拒绝伪造转换证据()
    {
        using var db = TestDbFactory.Create();
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = "PO-FAKE-1", OrderDate = DateTime.Today, SupplierId = 88L,
            Currency = ERP.Domain.Enums.Currency.USD, TotalAmount = 1m
        });
        db.SaveChanges();

        var draft = NewDraft("PQ-FAKE");
        draft.RefOrderNo = "PO-FAKE-1";

        var ex = await Assert.ThrowsAsync<BusinessException>(() => QuoteController(db).Create(draft));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(0, db.PurchaseQuotes.Count());
    }

    // ==================== 4. 控制器 CRUD（进程内口径，权威字段一律服务端为准） ====================

    [Fact]
    public async Task 新增_规范化审计字段与服务端总额_忽略客户端伪造审计字段()
    {
        using var db = TestDbFactory.Create();
        var draft = NewDraft("PQ-CREATE");
        draft.TotalAmount = 999m;                    // 伪造总额
        draft.CreatedAt = new DateTime(2000, 1, 1);  // 伪造审计
        draft.CreatedBy = 4242;
        draft.UpdatedBy = 4242;
        draft.IsDeleted = true;

        var created = AssertOk<PurchaseQuote>(await QuoteController(db).Create(draft));

        Assert.Equal(30m, created.TotalAmount);      // 服务端按 10 × 3 重算
        Assert.False(created.IsDeleted);
        Assert.True(created.CreatedAt > new DateTime(2020, 1, 1));
        Assert.Null(created.CreatedBy);
        Assert.Single(db.PurchaseQuotes);
    }

    [Fact]
    public async Task 修改_待比较草稿_按服务端重算总额并保存()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-UPD-PENDING", PurchaseQuoteMutationRules.PendingStatus, selected: false);

        var proposed = CloneQuote(quote);
        proposed.Quantity = 7m;
        proposed.QuotePrice = 2.5m;
        proposed.TotalAmount = 1m;

        var updated = AssertOk<PurchaseQuote>(await QuoteController(db).Update(quote.Id, proposed));

        Assert.Equal(17.5m, updated.TotalAmount);
        Assert.Equal(7m, db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).Quantity);
    }

    [Fact]
    public async Task 修改_已决定行_商业条款与改派拒绝_仅备注允许()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-UPD-DECIDED", withDecision: true);
        var ctl = QuoteController(db);

        var reassign = CloneQuote(quote);
        reassign.SupplierId = 99L;
        reassign.SupplierName = "别家供应商";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(quote.Id, reassign));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(PurchaseQuoteMutationRules.DecidedImmutableText, ex.Message);
        Assert.Equal(88L, db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).SupplierId);

        var priceChange = CloneQuote(quote);
        priceChange.QuotePrice = 1m;
        priceChange.TotalAmount = 100m;
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(quote.Id, priceChange));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(quote.Id, CloneQuote(quote, isSelected: false)));

        // 安全非商业备注修改：显式白名单字段，且不覆盖其它血缘字段。
        var remarkOnly = CloneQuote(quote);
        remarkOnly.Remark = "ERP-417 备注补充";
        var updated = AssertOk<PurchaseQuote>(await ctl.Update(quote.Id, remarkOnly));
        Assert.Equal("ERP-417 备注补充", updated.Remark);
        var stored = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id);
        Assert.Equal(88L, stored.SupplierId);
        Assert.Equal(quote.QuotePrice, stored.QuotePrice);
        Assert.True(stored.IsSelected);
    }

    [Fact]
    public async Task 修改_已转换行_拒绝()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-UPD-CVT", PurchaseQuoteMutationRules.ConvertedStatus,
            selected: true, withDecision: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuoteController(db).Update(quote.Id, CloneQuote(quote)));
        Assert.Equal(PurchaseQuoteMutationRules.ConvertedImmutableText, ex.Message);
    }

    [Fact]
    public async Task 删除_已决定行拒绝_未决定行放行()
    {
        using var db = TestDbFactory.Create();
        var ctl = QuoteController(db);
        var decided = SeedQuote(db, "PQ-DEL-DECIDED", withDecision: true);
        var pending = SeedQuote(db, "PQ-DEL-PENDING", PurchaseQuoteMutationRules.PendingStatus, selected: false);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(decided.Id));
        Assert.Equal(PurchaseQuoteMutationRules.DecidedNoDeleteText, ex.Message);
        Assert.False(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == decided.Id).IsDeleted);

        Assert.IsType<OkObjectResult>(await ctl.Delete(pending.Id));
        Assert.True(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == pending.Id).IsDeleted);
    }

    [Fact]
    public async Task 删除_未归属历史决定_保留既有ERP416软删除契约_决定证据不被删除()
    {
        using var db = TestDbFactory.Create();
        var ctl = QuoteController(db);
        var quote = SeedQuote(db, "PQ-DEL-LEGACY");

        // 历史 / 种子 / 导入数据：决定没有操作人归属（CreatedBy 为空），非实时审批生命周期追加。
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id, QuoteNo = quote.QuoteNo, Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId, SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "历史导入", DecidedBy = 1L, DecidedByName = "历史审批人",
            DecidedAt = DateTime.Now, DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
        });
        db.SaveChanges();

        Assert.False(PurchaseQuoteMutationRules.IsAttributedDecision(
            db.PurchaseQuoteDecisions.AsNoTracking().Single()));

        // 未归属决定不冻结软删除（ERP-416 既有契约）：只隐藏比价行，决策证据行绝不被删除、血缘完整。
        Assert.IsType<OkObjectResult>(await ctl.Delete(quote.Id));
        Assert.True(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).IsDeleted);
        var evidence = db.PurchaseQuoteDecisions.AsNoTracking().Single();
        Assert.False(evidence.IsDeleted);
        Assert.Equal(quote.Id, evidence.QuoteId);
    }

    [Fact]
    public async Task 删除_归属决定_冻结软删除()
    {
        using var db = TestDbFactory.Create();
        var ctl = QuoteController(db);
        var quote = SeedQuote(db, "PQ-DEL-ATTRIBUTED", withDecision: true);

        Assert.True(PurchaseQuoteMutationRules.IsAttributedDecision(
            db.PurchaseQuoteDecisions.AsNoTracking().Single()));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(quote.Id));
        Assert.Equal(PurchaseQuoteMutationRules.DecidedNoDeleteText, ex.Message);
        Assert.False(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).IsDeleted);
    }

    [Fact]
    public async Task 批量删除_混入已决定行_整批拒绝零删除_全部未决定则全删除()
    {
        using var db = TestDbFactory.Create();
        var ctl = QuoteController(db);
        var a = SeedQuote(db, "PQ-BD-A", PurchaseQuoteMutationRules.PendingStatus, selected: false);
        var b = SeedQuote(db, "PQ-BD-B", withDecision: true);
        var c = SeedQuote(db, "PQ-BD-C", PurchaseQuoteMutationRules.PendingStatus, selected: false);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchDelete(new List<long> { a.Id, b.Id, c.Id }));
        Assert.Equal(PurchaseQuoteMutationRules.DecidedNoDeleteText, ex.Message);
        Assert.All(new[] { a.Id, b.Id, c.Id }, id =>
            Assert.False(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == id).IsDeleted));

        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { a.Id, c.Id, a.Id }));
        Assert.True(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == a.Id).IsDeleted);
        Assert.True(db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == c.Id).IsDeleted);
    }

    // ==================== 5. 审批决定：append-only + 可信决定人 ====================

    [Fact]
    public async Task 重复决定_抛RuleConflict_仅一条决定()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-DUP", PurchaseQuoteMutationRules.PendingStatus, selected: false);
        var ctl = new PurchaseQuoteDecisionController(db);

        AssertOk<PurchaseQuoteDecision>(await ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved
        }));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Rejected
        }));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Single(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 决定人_取自可信操作人_伪造输入被忽略()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-ACTOR", PurchaseQuoteMutationRules.PendingStatus, selected: false);
        var userId = SeedPrivilegedUser(db, "可信审批人");

        var decision = await PurchaseQuoteApproval.DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved,
            DecidedBy = 9_999_999L, DecidedByName = "伪造决定人"
        }, new PurchaseQuoteAuthorizationRules.LiveActor(userId, "伪造传入名"));

        Assert.Equal(userId, decision.DecidedBy);
        Assert.Equal("可信审批人", decision.DecidedByName);
    }

    [Fact]
    public async Task 审批通过后_转采购订单_保留审批参考()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-APV-CONV");
        var ctl = new PurchaseQuoteDecisionController(db);
        await ctl.Decide(new PurchaseQuoteDecisionRequest { QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved });

        var converted = AssertOk<PurchaseOrderConversionResult>(await QuoteController(db).ToPurchaseOrder(quote.Id));
        Assert.StartsWith("PO", converted.OrderNo);
        Assert.Equal(PurchaseQuoteMutationRules.ConvertedStatus,
            db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).Status);
        Assert.Contains("审批参考", db.PurchaseOrders.Single().Remark);
    }

    // ==================== 6. 接线契约（源码断言） ====================

    [Fact]
    public void 契约_生命周期与审批决定接入比价行锁_事务_草稿校验与实时决定人()
    {
        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseQuoteMutationRules.cs");
        Assert.Contains("QuoteRowLockSql", rules);
        Assert.Contains("UPDLOCK, HOLDLOCK", rules);
        Assert.Contains("LockQuoteRowAsync", rules);
        Assert.Contains("LockQuoteRowsAsync", rules);
        Assert.Contains("BeginMutationTransactionAsync", rules);

        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteController.cs");
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteMutationRules.LockQuoteRowAsync", controller);
        Assert.Contains("PurchaseQuoteMutationRules.LockQuoteRowsAsync", controller);
        Assert.Contains("PurchaseQuoteMutationRules.ValidateDraft", controller);
        Assert.Contains("PurchaseQuoteMutationRules.IsRemarkOnlyChange", controller);
        Assert.Contains("PurchaseQuoteMutationRules.EnsureNoForgedConversionEvidenceAsync", controller);
        Assert.Contains("PurchaseQuoteApproval.EnsureApprovedSupplierCoherentAsync", controller);
        Assert.Contains("ReauthorizeAsync", controller);

        var approval = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteApproval.cs");
        Assert.Contains("PurchaseQuoteMutationRules.LockQuoteRowAsync", approval);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureLiveActorAsync", approval);
        Assert.Contains("EnsureApprovedSupplierCoherentAsync", approval);

        var decision = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteDecisionController.cs");
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureLiveActorAsync", decision);
        Assert.Contains("PurchaseQuoteApproval.DecideAsync(_db, request, actor)", decision);
    }

    // ==================== 工厂与种子数据 ====================

    private static PurchaseQuoteController QuoteController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static PurchaseQuote NewDraft(string quoteNo) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 10, 1),
        ProductId = 310L,
        ProductName = "ERP417 商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 10m,
        SupplierId = 88L,
        SupplierName = "ERP417 档口",
        SupplierType = "档口",
        QuotePrice = 3m,
        TotalAmount = 30m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = PurchaseQuoteMutationRules.PendingStatus,
        Remark = "ERP417_TEST"
    };

    private static PurchaseQuote SeedQuote(ErpDbContext db, string quoteNo,
        string status = PurchaseQuoteMutationRules.SelectedStatus, bool selected = true, bool withDecision = false,
        long supplierId = 88L)
    {
        var quote = NewDraft(quoteNo);
        quote.Status = status;
        quote.IsSelected = selected || status == PurchaseQuoteMutationRules.SelectedStatus;
        quote.SupplierId = supplierId;
        quote.SupplierName = $"供应商{supplierId}";
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();

        if (withDecision)
        {
            db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
            {
                QuoteId = quote.Id, QuoteNo = quote.QuoteNo, Decision = PurchaseQuoteApproval.Approved,
                SelectedSupplierId = quote.SupplierId, SelectedSupplierName = quote.SupplierName,
                DecisionBasis = "单价最优", DecidedBy = 1L, DecidedByName = "审批人",
                DecidedAt = DateTime.Now, DecisionRef = PurchaseQuoteApproval.DecisionRef(quote),
                // ERP-417：模拟由实时审批生命周期追加的**归属决定**（带操作人归属，冻结软删除）。
                CreatedAt = DateTime.Now, CreatedBy = 1L
            });
            db.SaveChanges();
        }

        return quote;
    }

    private static PurchaseQuote CloneQuote(PurchaseQuote quote, bool? isSelected = null) => new()
    {
        Id = quote.Id,
        QuoteNo = quote.QuoteNo,
        QuoteDate = quote.QuoteDate,
        ProductId = quote.ProductId,
        ProductName = quote.ProductName,
        Spec = quote.Spec,
        Unit = quote.Unit,
        Quantity = quote.Quantity,
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
        IsSelected = isSelected ?? quote.IsSelected,
        Status = quote.Status,
        CustomerId = quote.CustomerId,
        CustomerName = quote.CustomerName,
        RefOrderNo = quote.RefOrderNo,
        Remark = quote.Remark
    };

    private static long SeedPrivilegedUser(ErpDbContext db, string displayName)
    {
        var code = $"erp417-p-{Guid.NewGuid():N}";
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = displayName, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));
}

