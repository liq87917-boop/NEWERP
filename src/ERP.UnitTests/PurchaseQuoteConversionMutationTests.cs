using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商比价 → 采购订单（ERP-418）<b>共享事务协议 / 锁序 / 受控拒绝 / 跨币种合计口径</b>的单元测试：
/// 单行与批次共用同一协议（归属销售订单先于来源比价行的确定性锁序、锁内权威重读与成员一致复核）、
/// 币种无法识别一律拒绝（绝不回退人民币）、归属销售订单删除 / 取消 / 未审核时受控拒绝且不生成无归属订单、
/// 供应商 / 商品主数据停用或删除时 fail closed、跨币种不做合计（DTO 显式未知 + 按币种分列）、
/// 预填 / 计划保持只读（不占号 / 不改状态 / 不动审计时间戳）、强制保存失败整体回滚（无孤儿订单 / 明细）。
/// <para>说明：全部使用内存数据库（无行锁语义，锁定 / 事务等价无操作）；真实 SQL 行锁竞态由
/// <c>PurchaseQuoteConversionMutationSqlServerTests</c> 在两个独立连接上执行。</para>
/// </summary>
public class PurchaseQuoteConversionMutationTests
{
    private const string BatchNo = "PQ-418-BATCH";

    // ==================== 1. 共享协议：锁语句与锁序同源 ====================

    [Fact]
    public void 锁协议_与既有比价行锁及销售订单行锁同源_且锁序恒定先销售订单后比价行()
    {
        // 与 ERP-417 比价生命周期 / 审批决定共用同一把比价行锁。
        Assert.Equal(PurchaseQuoteMutationRules.QuoteRowLockSql,
            PurchaseQuoteConversionMutationRules.QuoteRowLockSql);
        // 与销售订单取消 / 预装柜 / 普通采购归属链接共用同一把销售订单行锁。
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            PurchaseQuoteConversionMutationRules.OwningSalesOrderRowLockSql);
        Assert.Contains("SalesOrders", PurchaseQuoteConversionMutationRules.OwningSalesOrderRowLockSql);
        Assert.Contains("PurchaseQuotes", PurchaseQuoteConversionMutationRules.QuoteRowLockSql);

        // 锁序文案必须写明「归属销售订单 → 来源比价行」，且两侧都按 Id 升序确定性获取。
        var order = PurchaseQuoteConversionMutationRules.LockOrderText;
        Assert.Contains("归属销售订单行锁", order);
        Assert.Contains("来源比价行锁", order);
        Assert.True(order.IndexOf("归属销售订单行锁", StringComparison.Ordinal)
            < order.IndexOf("来源比价行锁", StringComparison.Ordinal));
        Assert.Contains("Id 升序", order);
    }

    // ==================== 2. 币种：无法识别一律拒绝（绝不回退人民币） ====================

    [Fact]
    public async Task 批次内任一行币种无法识别_整批受控拒绝_不回退人民币也不静默跳过()
    {
        using var db = TestDbFactory.Create();
        var usd = AddLine(db, BatchNo, 88L, 100m, 2m, currency: "USD");
        var bogus = AddLine(db, BatchNo, 88L, 100m, 2m, currency: "RUB");
        var seedUpdatedAt = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == bogus.Id).UpdatedAt;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db)
            .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = BatchNo }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("币种无法识别", ex.Message);
        Assert.Contains($"#{bogus.Id}", ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);

        // 来源证据零改动：状态 / 采购单链接 / 审计时间戳都不变（也不存在被回退的人民币改写）。
        var stored = db.PurchaseQuotes.AsNoTracking().OrderBy(q => q.Id).ToList();
        Assert.All(stored, q => Assert.Equal(PurchaseQuoteConversion.SelectedStatus, q.Status));
        Assert.All(stored, q => Assert.Equal(string.Empty, q.RefOrderNo));
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, stored.Single(q => q.Id == usd.Id).Status);
        Assert.Equal(seedUpdatedAt, stored.Single(q => q.Id == bogus.Id).UpdatedAt);
    }

    [Fact]
    public void 币种唯一权威解析_仅接受受支持枚举名_拒绝数字与未知文本()
    {
        Assert.True(PurchaseQuoteConversionMutationRules.TryParseCurrency(" usd ", out var usd));
        Assert.Equal(Currency.USD, usd);
        Assert.Equal(Currency.CNY, PurchaseQuoteConversionMutationRules.RequireCurrency("CNY"));

        foreach (var text in new[] { "RUB", "EUR", "", null, "0", "1", "CNY,USD" })
        {
            Assert.False(PurchaseQuoteConversionMutationRules.TryParseCurrency(text, out _));
            var ex = Assert.Throws<BusinessException>(() =>
                PurchaseQuoteConversionMutationRules.RequireCurrency(text));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Contains("绝不回退", ex.Message);
        }
    }

    // ==================== 3. 归属销售订单：受控（删除 / 取消 / 未审核） ====================

    [Theory]
    [InlineData("deleted", PurchaseQuoteConversionMutationRules.OwnershipUnavailableText)]
    [InlineData("cancelled", PurchaseQuoteConversionMutationRules.OwnershipCancelledText)]
    [InlineData("pending", PurchaseQuoteConversionMutationRules.OwnershipNotApprovedText)]
    public async Task 归属销售订单已删除取消或未审核_单行批次预填计划都受控拒绝且零写入(string state, string expected)
    {
        using var db = TestDbFactory.Create();
        var owning = new SalesOrder
        {
            OrderNo = $"SO-418-{state}",
            OrderDate = DateTime.Today,
            CustomerId = 7L,
            Currency = Currency.USD,
            Status = state == "cancelled" ? DocumentStatus.Cancelled : DocumentStatus.Pending,
            IsDeleted = state == "deleted"
        };
        db.SalesOrders.Add(owning);
        db.SaveChanges();
        var quote = AddLine(db, BatchNo, 88L, 100m, 2m, refOrderNo: owning.OrderNo);

        foreach (var action in new Func<Task>[]
                 {
                     () => NewController(db).ToPurchaseOrder(quote.Id),
                     () => NewController(db).BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = BatchNo }),
                     () => NewController(db).OrderPrefill(quote.Id)
                 })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(action);
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains(expected, ex.Message);
        }

        // 只读批次计划同样不静默生成：该行显式跳过（0 组、0 合格行）且不落任何单据。
        var plan = Assert.IsType<ApiResponse<PurchaseQuoteBatchPlan>>(
            Assert.IsType<OkObjectResult>(await NewController(db).BatchOrderPlan(BatchNo, null)).Value).Data!;
        Assert.Equal(0, plan.GroupCount);
        Assert.Equal(0, plan.EligibleLineCount);
        Assert.Contains(expected, Assert.Single(plan.Skipped).Reason);

        // 绝不静默生成无归属 / 变更归属的订单，来源行商业证据保持不变。
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        var stored = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, stored.Status);
        Assert.Equal(owning.OrderNo, stored.RefOrderNo);
    }

    [Fact]
    public async Task 归属销售订单号匹配不到_保持既有口径_归属留空仍可转换()
    {
        using var db = TestDbFactory.Create();
        var quote = AddLine(db, BatchNo, 88L, 100m, 2m, refOrderNo: "SO-NOT-EXIST-418");

        await NewController(db).ToPurchaseOrder(quote.Id);

        var converted = db.PurchaseOrders.Single();
        Assert.Null(converted.OwningSalesOrderId);
        Assert.Equal(string.Empty, converted.OwningSalesOrderNo);
    }

    [Fact]
    public async Task 归属销售订单已审核_解析为权威归属_归属客户不一致时受控拒绝()
    {
        using var db = TestDbFactory.Create();
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-418-OK", OrderDate = DateTime.Today, CustomerId = 7L,
            Currency = Currency.USD, Status = DocumentStatus.Approved
        });
        db.SaveChanges();
        var salesOrderId = db.SalesOrders.Single().Id;
        var quote = AddLine(db, BatchNo, 88L, 100m, 2m, refOrderNo: "SO-418-OK", customerId: 7L,
            customerName: "客户A");

        await NewController(db).ToPurchaseOrder(quote.Id);
        Assert.Equal(salesOrderId, db.PurchaseOrders.Single().OwningSalesOrderId);

        // 归属客户与来源销售订单客户不一致：受控拒绝（绝不静默生成「另一客户」的采购订单）。
        using var db2 = TestDbFactory.Create();
        db2.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-418-OTHER", OrderDate = DateTime.Today, CustomerId = 99L,
            Currency = Currency.USD, Status = DocumentStatus.Approved
        });
        db2.SaveChanges();
        var foreign = AddLine(db2, BatchNo, 88L, 100m, 2m, refOrderNo: "SO-418-OTHER", customerId: 7L,
            customerName: "客户A");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db2).ToPurchaseOrder(foreign.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("归属客户与归属销售订单客户不一致", ex.Message);
        Assert.Empty(db2.PurchaseOrders);
    }

    // ==================== 4. 主数据引用：停用 / 删除 fail closed ====================

    [Fact]
    public async Task 供应商或商品档案已停用删除_受控拒绝且不落库()
    {
        using var supplierDb = TestDbFactory.Create();
        supplierDb.BaseSuppliers.Add(new BaseSupplier
        {
            SupplierCode = "S-418", SupplierName = "停用供应商", Status = 0
        });
        supplierDb.SaveChanges();
        var supplierId = supplierDb.BaseSuppliers.Single().Id;
        var supplierQuote = AddLine(supplierDb, BatchNo, supplierId, 100m, 2m);

        var supplierEx = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(supplierDb).ToPurchaseOrder(supplierQuote.Id));
        Assert.Equal(ErrorCodes.RuleConflict, supplierEx.Code);
        Assert.Contains("供应商档案已停用或已删除", supplierEx.Message);
        Assert.Empty(supplierDb.PurchaseOrders);

        using var productDb = TestDbFactory.Create();
        productDb.BaseProducts.Add(new BaseProduct
        {
            ProductCode = "P-418", ProductName = "停用商品", Status = 0
        });
        productDb.SaveChanges();
        var productId = productDb.BaseProducts.Single().Id;
        var productQuote = AddLine(productDb, BatchNo, 88L, 100m, 2m, productId: productId);

        var productEx = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(productDb).ToPurchaseOrder(productQuote.Id));
        Assert.Equal(ErrorCodes.RuleConflict, productEx.Code);
        Assert.Contains("商品档案已停用或已删除", productEx.Message);
        Assert.Empty(productDb.PurchaseOrders);
    }

    [Fact]
    public async Task 单位为空_受控拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var quote = AddLine(db, BatchNo, 88L, 100m, 2m, unit: string.Empty);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("单位不能为空", ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus,
            db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).Status);
    }

    // ==================== 5. 跨币种合计：显式未知 + 按币种分列 ====================

    [Fact]
    public async Task 跨币种批次_计划与结果合计显式未知_按币种分列且明细各自保留币种与单位()
    {
        using var db = TestDbFactory.Create();
        var cny = AddLine(db, BatchNo, 88L, 100m, 2m, currency: "CNY");
        var usd = AddLine(db, BatchNo, 88L, 100m, 3m, currency: "USD");

        var plan = Assert.IsType<ApiResponse<PurchaseQuoteBatchPlan>>(
            Assert.IsType<OkObjectResult>(await NewController(db).BatchOrderPlan(BatchNo, null)).Value).Data!;
        Assert.True(plan.MixedCurrency);
        Assert.Null(plan.TotalAmount);                                  // 绝不把混合币种金额当钱
        Assert.Equal(2, plan.TotalAmountByCurrency.Count);
        Assert.Equal(200m, plan.TotalAmountByCurrency.Single(t => t.Currency == "CNY").TotalAmount);
        Assert.Equal(300m, plan.TotalAmountByCurrency.Single(t => t.Currency == "USD").TotalAmount);

        var result = Assert.IsType<ApiResponse<PurchaseQuoteBatchConversionResult>>(
            Assert.IsType<OkObjectResult>(await NewController(db).BatchToOrder(
                new PurchaseQuoteBatchConversionRequest { QuoteNo = BatchNo })).Value).Data!;
        Assert.True(result.MixedCurrency);
        Assert.Null(result.TotalAmount);
        Assert.Equal(2, result.OrderCount);
        Assert.Equal(2, result.TotalAmountByCurrency.Count);

        // 每张订单各自保留币种与单位，服务端合计只在该订单内部累加。
        var orders = db.PurchaseOrders.AsNoTracking().OrderBy(o => o.Id).ToList();
        Assert.Equal(new[] { Currency.CNY, Currency.USD }, orders.Select(o => o.Currency).ToArray());
        Assert.Equal(new[] { 200m, 300m }, orders.Select(o => o.TotalAmount).ToArray());
        Assert.All(db.PurchaseOrderDetails.AsNoTracking().ToList(), d => Assert.Equal("PCS", d.Unit));

        // 来源行逐行留痕，商业条款（数量 / 单价 / 币种）保持不变。
        Assert.Equal(100m, db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == usd.Id).Quantity);
        Assert.Equal(3m, db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == usd.Id).QuotePrice);
        Assert.Equal("USD", db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == usd.Id).Currency);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus,
            db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == cny.Id).Status);
    }

    // ==================== 6. 只读路径：预填 / 计划不占号、不改状态、不改审计时间戳 ====================

    [Fact]
    public async Task 预填与批次计划_保持只读_不占号不改状态不改审计时间戳()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m);
        var l2 = AddLine(db, BatchNo, 88L, 300m, 2m);
        var before = db.PurchaseQuotes.AsNoTracking().ToList()
            .ToDictionary(q => q.Id, q => (q.Status, q.RefOrderNo, q.UpdatedAt));

        await NewController(db).OrderPrefill(l1.Id);
        await NewController(db).BatchOrderPlan(BatchNo, null);

        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        foreach (var quote in db.PurchaseQuotes.AsNoTracking().ToList())
        {
            var seed = before[quote.Id];
            Assert.Equal(seed.Status, quote.Status);
            Assert.Equal(seed.RefOrderNo, quote.RefOrderNo);
            Assert.Equal(seed.UpdatedAt, quote.UpdatedAt);
        }

        // 只读计划重复执行不会为同一批次预留任何采购单号（不新增订单即为不占号），也不留待保存变更。
        for (var i = 0; i < 3; i++) await NewController(db).BatchOrderPlan(BatchNo, null);
        Assert.Empty(db.PurchaseOrders);
        Assert.DoesNotContain(db.ChangeTracker.Entries<PurchaseOrder>(), e => e.State != EntityState.Unchanged);
        Assert.Contains(db.PurchaseQuotes, q => q.Id == l2.Id);
    }

    // ==================== 7. 强制保存失败：整体回滚（无孤儿订单 / 明细 / 来源标记） ====================

    [Fact]
    public async Task 强制保存失败_整体回滚_不残留孤儿订单与来源标记()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new FailingPurchaseOrderSaveInterceptor())
            .Options;
        await using var db = new ErpDbContext(options);
        var quote = AddLine(db, BatchNo, 88L, 100m, 2m);
        var seedUpdatedAt = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).UpdatedAt;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Contains("ERP-418 forced", ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);

        // 来源行留痕（状态 / RefOrderNo / 审计时间戳）同样被丢弃：绝不出现「有标记无订单」的半成品。
        var stored = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, stored.Status);
        Assert.Equal(string.Empty, stored.RefOrderNo);
        Assert.Equal(seedUpdatedAt, stored.UpdatedAt);

        // 失败后重试（不再注入失败）可正常转换，证明失败没有污染来源状态。
        var retryOptions = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var retryDb = new ErpDbContext(retryOptions);
        await NewController(retryDb).ToPurchaseOrder(quote.Id);
        Assert.Single(retryDb.PurchaseOrders);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus,
            retryDb.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).Status);
    }

    // ==================== 8. 嵌套 DTO / 报文契约：跨币种绝不出现混合金额 ====================

    [Fact]
    public void 批次计划与结果报文契约_跨币种时totalAmount为null并给出按币种小计()
    {
        var plan = new PurchaseQuoteBatchPlan
        {
            SourceType = PurchaseQuoteConversion.PurchaseQuoteSourceType,
            SourceNo = BatchNo,
            BatchLineCount = 2,
            EligibleLineCount = 2,
            GroupCount = 2,
            MixedCurrency = true,
            TotalAmount = null,
            TotalAmountByCurrency = new List<PurchaseQuoteCurrencyTotal>
            {
                new() { Currency = "CNY", TotalAmount = 200m, LineCount = 1 },
                new() { Currency = "USD", TotalAmount = 300m, LineCount = 1 }
            }
        };

        var json = JsonSerializer.Serialize(plan,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("totalAmount").ValueKind);
        Assert.True(root.GetProperty("mixedCurrency").GetBoolean());
        var byCurrency = root.GetProperty("totalAmountByCurrency");
        Assert.Equal(2, byCurrency.GetArrayLength());
        Assert.Equal("CNY", byCurrency[0].GetProperty("currency").GetString());
        Assert.Equal(200m, byCurrency[0].GetProperty("totalAmount").GetDecimal());
        Assert.Equal(300m, byCurrency[1].GetProperty("totalAmount").GetDecimal());

        // 单一币种时才给出合计金额（且与按币种小计一致）。
        var single = PurchaseQuoteConversion.SummarizeByCurrency(new[]
        {
            ("USD", 200m, 1), ("USD", 300m, 1)
        });
        Assert.False(single.Mixed);
        Assert.Equal(500m, single.Total);
        var only = Assert.Single(single.ByCurrency);
        Assert.Equal("USD", only.Currency);
        Assert.Equal(500m, only.TotalAmount);
        Assert.Equal(2, only.LineCount);
    }

    // ==================== 工厂 / 种子数据 ====================

    private static PurchaseQuoteController NewController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

    /// <summary>
    /// 已选中且已批准的比价行（报价总额故意写成 1，验证服务端按数量 × 单价重算）：
    /// 默认 USD / PCS / 10 天交期 / 无归属客户，可按需指定币种、单位、商品与关联销售订单号。
    /// </summary>
    private static PurchaseQuote AddLine(ErpDbContext db, string quoteNo, long supplierId, decimal quantity,
        decimal price, string currency = "USD", string unit = "PCS", long? productId = 310L,
        string refOrderNo = "", long? customerId = null, string customerName = "")
    {
        var line = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = DateTime.Today,
            ProductId = productId,
            ProductName = "ERP418 商品",
            Spec = "大号",
            Unit = unit,
            Quantity = quantity,
            SupplierId = supplierId > 0 ? supplierId : null,
            SupplierName = supplierId > 0 ? $"供应商{supplierId}" : string.Empty,
            SupplierType = "档口",
            QuotePrice = price,
            TotalAmount = 1m,
            Currency = currency,
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "T/T 30%",
            IsSelected = true,
            Status = PurchaseQuoteConversion.SelectedStatus,
            CustomerId = customerId,
            CustomerName = customerName,
            RefOrderNo = refOrderNo,
            Remark = "ERP418_TEST"
        };
        db.PurchaseQuotes.Add(line);
        db.SaveChanges();

        // ERP-095：合格行需先「批准选中」才能转采购订单。
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = line.Id,
            QuoteNo = line.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = line.SupplierId,
            SelectedSupplierName = line.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = 1L,
            DecidedByName = "审批人",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(line)
        });
        db.SaveChanges();
        return line;
    }
}

/// <summary>
/// ERP-418 强制保存失败注入：仅在<b>新增采购订单</b>时抛出，用于证明「取号 + 订单 + 明细 + 来源留痕」
/// 在同一次 SaveChanges 内整体失败，绝不残留孤儿订单 / 明细或有标记无订单的半成品。
/// </summary>
internal sealed class FailingPurchaseOrderSaveInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfOrderAdded(eventData);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ThrowIfOrderAdded(eventData);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ThrowIfOrderAdded(DbContextEventData eventData)
    {
        if (eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<PurchaseOrder>().Any(e => e.State == EntityState.Added))
            throw new InvalidOperationException("ERP-418 forced save failure");
    }
}
