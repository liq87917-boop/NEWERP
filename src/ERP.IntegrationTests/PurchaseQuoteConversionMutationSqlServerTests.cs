using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-418 供应商比价 → 采购订单（单行 / 批次）转换的真实 SQL Server 并发 / 原子性集成测试。
/// <list type="number">
/// <item><b>真实既有授权 + 真实控制器</b>：复用 ERP-416 已播种的既有「供应商比价」<c>purchase-quote</c> /
/// 「采购订单」<c>purchase-order</c> 菜单授权与业务员客户数据范围，不新增 / 不修改任何权限模型，
/// 无匿名 / 管理员降级。</item>
/// <item><b>两个独立连接竞态</b>（真实 GUID 独占库、两条真实 <see cref="ErpDbContext"/> 连接）：
/// ① 单行 vs 单行；② 单行 vs 重叠批次；③ 转换 vs 历史删除尝试（ERP-419：已批准来源的有效决定
/// 不论创建人归属一律冻结删除 → 转换放行、删除受控拒绝、来源与决定都不消失）；④ 转换 vs 归属销售订单取消
/// （与真实取消路由共用同一把销售订单行锁）。每例都断言「同一来源行恰好一条已提交订单血缘」。</item>
/// <item><b>受控拒绝</b>：币种无法识别（绝不回退 CNY）、供应商 / 商品档案停用、单位缺失、
/// 归属销售订单删除 / 取消 / 未审核。</item>
/// <item><b>原子性与精确写入面</b>：强制中途失败整体回滚（订单 / 明细 / 来源标记都不落库）；
/// 成功批次精确校验生成的表头 / 明细 / 来源链接与服务端重算合计，且<b>零库存 / 财务写入</b>。</item>
/// <item><b>保留库存来源单据审计与原始失败日志</b>：只读取计数与权威行，不删除 / 不清理任何既有行。</item>
/// <item><b>专用目标护栏</b>：访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；错误实例 / 错误库名 / 非集成安全一律 fail closed。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才构成阶段验收证据。</para>
/// </summary>
public sealed class PurchaseQuoteConversionMutationSqlServerTests
    : IClassFixture<PurchaseQuoteAuthorizationSqlServerFixture>
{
    private const string BatchPrefix = "PQ-418-BATCH-";

    private readonly PurchaseQuoteAuthorizationSqlServerFixture _fixture;

    public PurchaseQuoteConversionMutationSqlServerTests(PurchaseQuoteAuthorizationSqlServerFixture fixture)
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
    public void 真实SQL上下文_判定为关系型_且转换锁语句与既有协议同源()
    {
        Guard();
        using var db = _fixture.CreateDbContext();
        Assert.True(PurchaseQuoteConversionMutationRules.IsRelationalProvider(db));
        Assert.Equal("SELECT Id FROM db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            PurchaseQuoteConversionMutationRules.QuoteRowLockSql);
        Assert.Equal("SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            PurchaseQuoteConversionMutationRules.OwningSalesOrderRowLockSql);
    }

    // ==================== 两个独立连接竞态：单行 vs 单行 ====================

    [Fact]
    public async Task 竞态_两个独立连接的单行转换_同一来源行仅一条已提交订单血缘()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddQuoteAsync(seed, BatchPrefix + Tag(), selected: true)).Id;

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> ConvertAsync(ErpDbContext db)
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(quoteId));
        }

        var first = ConvertAsync(dbA);
        var second = ConvertAsync(dbB);
        gate.Release(2);
        var results = new[] { await first, await second };

        // 恰好一个赢家；输家得到受控的重复 / 陈旧业务拒绝（绝不产生第二张订单）。
        Assert.Equal(1, results.Count(r => r.Success));
        var loser = Assert.Single(results.Where(r => !r.Success));
        Assert.Contains(ErrorCodes.RuleConflict.ToString(), loser.Error, StringComparison.Ordinal);

        await using var check = _fixture.CreateDbContext();
        var orders = await check.PurchaseOrders.AsNoTracking()
            .Where(o => o.Remark.Contains($"比价行 #{quoteId}）"))
            .ToListAsync();
        var order = Assert.Single(orders);                                  // 一条已提交订单血缘
        var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
        Assert.Equal(order.OrderNo, source.RefOrderNo);
        Assert.Equal(1, await check.PurchaseOrderDetails.AsNoTracking()
            .CountAsync(d => d.PurchaseOrderId == order.Id));                // 无孤儿 / 重复明细
    }

    // ==================== 两个独立连接竞态：单行 vs 重叠批次 ====================

    [Fact]
    public async Task 竞态_单行与重叠批次_每条来源行恰好一条订单血缘且无部分写入()
    {
        Guard();
        var batchNo = BatchPrefix + Tag();
        long singleId, otherId;
        await using (var seed = _fixture.CreateDbContext())
        {
            singleId = (await AddQuoteAsync(seed, batchNo, selected: true)).Id;
            otherId = (await AddQuoteAsync(seed, batchNo, selected: true)).Id;
        }

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> SingleAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => Controller(dbA, _fixture.OperatorUserId).ToPurchaseOrder(singleId));
        }

        async Task<Outcome> BatchAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => Controller(dbB, _fixture.OperatorUserId).BatchToOrder(
                new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));
        }

        var single = SingleAsync();
        var batch = BatchAsync();
        gate.Release(2);
        var singleResult = await single;
        var batchResult = await batch;

        // 批次恒成功（重叠成员被并发单行抢先时，该行按既有语义显式跳过；其余行照常生成）；
        // 单行只有在抢先拿到来源行锁时才成功，否则得到受控的重复拒绝。
        Assert.True(batchResult.Success, batchResult.Error);
        if (!singleResult.Success)
            Assert.Contains(ErrorCodes.RuleConflict.ToString(), singleResult.Error, StringComparison.Ordinal);

        await using var check = _fixture.CreateDbContext();
        foreach (var id in new[] { singleId, otherId })
        {
            // 每条来源行恰好一条已提交订单血缘（单行赢 → 两张单；批次赢 → 一张单含两行明细）。
            var order = await check.PurchaseOrders.AsNoTracking()
                .SingleAsync(o => o.Remark.Contains($"比价行 #{id}）"));
            Assert.Equal(DocumentStatus.Pending, order.Status);

            var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == id);
            Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
            Assert.Equal(order.OrderNo, source.RefOrderNo);

            // 表头 ↔ 明细 ↔ 来源链接严格 1:1：明细行数 == 指向该单号的来源行数（无孤儿 / 重复明细）。
            var detailCount = await check.PurchaseOrderDetails.AsNoTracking()
                .CountAsync(d => d.PurchaseOrderId == order.Id);
            var linkedSources = await check.PurchaseQuotes.AsNoTracking()
                .CountAsync(q => q.RefOrderNo == order.OrderNo && !q.IsDeleted);
            Assert.Equal(linkedSources, detailCount);
        }
    }

    // ==================== 两个独立连接竞态：转换 vs 历史删除尝试 ====================

    /// <summary>
    /// ERP-419：已批准来源上存在的有效决定（历史 / 种子数据，缺失创建人归属 = 未知归属）冻结软删除，
    /// 因此并发的「转换 vs 删除」不再可能出现「删除赢家」：转换一律放行并原子落订单血缘，删除一律受控拒绝；
    /// 来源、审批决定与订单都不会消失，也绝不生成无来源订单。
    /// </summary>
    [Fact]
    public async Task 竞态_转换与历史删除尝试_转换放行且删除被拒_来源与决定绝不消失()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddQuoteAsync(seed, BatchPrefix + Tag(), selected: true)).Id;

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> ConvertAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => Controller(dbA, _fixture.OperatorUserId).ToPurchaseOrder(quoteId));
        }

        async Task<Outcome> DeleteAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => Controller(dbB, _fixture.OperatorUserId).Delete(quoteId));
        }

        var convertTask = ConvertAsync();
        var deleteTask = DeleteAsync();
        gate.Release(2);
        var convert = await convertTask;
        var delete = await deleteTask;

        Assert.True(convert.Success, convert.Error);
        Assert.False(delete.Success, delete.Error);
        Assert.True(
            delete.Error.Contains(PurchaseQuoteMutationRules.DecidedNoDeleteText, StringComparison.Ordinal)
            || delete.Error.Contains(PurchaseQuoteMutationRules.ConvertedImmutableText, StringComparison.Ordinal)
            || delete.Error.Contains(PurchaseQuoteMutationRules.ConcurrentMutationText, StringComparison.Ordinal),
            delete.Error);

        await using var check = _fixture.CreateDbContext();
        var row = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.False(row.IsDeleted);                                 // 来源保留：订单 + 来源留痕
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, row.Status);
        var orders = await check.PurchaseOrders.AsNoTracking()
            .Where(o => o.Remark.Contains($"比价行 #{quoteId}）")).ToListAsync();
        Assert.Single(orders);
        Assert.Equal(orders[0].OrderNo, row.RefOrderNo);
        Assert.True(await check.PurchaseQuoteDecisions.AsNoTracking()
            .AnyAsync(d => d.QuoteId == quoteId && !d.IsDeleted));    // 审批决定同样保留
    }

    // ==================== 两个独立连接竞态：转换 vs 归属销售订单取消 ====================

    [Fact]
    public async Task 竞态_转换与归属销售订单取消_恰好一个赢家_共用同一把销售订单行锁()
    {
        Guard();
        long salesOrderId, quoteId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var salesOrder = await AddSalesOrderAsync(seed, _fixture.CustomerAId);
            salesOrderId = salesOrder.Id;
            quoteId = (await AddQuoteAsync(seed, BatchPrefix + Tag(), selected: true,
                refOrderNo: salesOrder.OrderNo, customerId: _fixture.CustomerAId)).Id;
        }

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<Outcome> ConvertAsync()
        {
            await gate.WaitAsync();
            return await CaptureAsync(() => Controller(dbA, _fixture.OperatorUserId).ToPurchaseOrder(quoteId));
        }

        // 与真实「销售订单取消」同一把来源行锁（SalesOrderSourceLineageRules.LockSalesOrderRowAsync）：
        // 锁内重读「生效中的归属采购订单」证据，有证据即受控拒绝（不静默取消）。
        async Task<Outcome> CancelAsync()
        {
            await gate.WaitAsync();
            try
            {
                await using var transaction = await dbB.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable);
                if (!await SalesOrderSourceLineageRules.LockSalesOrderRowAsync(dbB, salesOrderId))
                    throw BusinessException.NotFound("销售订单不存在");
                var linked = await dbB.PurchaseOrders.AsNoTracking().AnyAsync(o =>
                    o.OwningSalesOrderId == salesOrderId && !o.IsDeleted && o.Status != DocumentStatus.Cancelled);
                if (linked)
                    throw BusinessException.RuleConflict("已存在生效的归属采购订单：拒绝取消来源销售订单");

                var order = await dbB.SalesOrders.FirstAsync(o => o.Id == salesOrderId);
                order.Status = DocumentStatus.Cancelled;
                order.UpdatedAt = DateTime.Now;
                await dbB.SaveChangesAsync();
                await transaction.CommitAsync();
                return new Outcome(true, string.Empty);
            }
            catch (BusinessException ex)
            {
                return new Outcome(false, $"{ex.Code}:{ex.Message}");
            }
        }

        var convertTask = ConvertAsync();
        var cancelTask = CancelAsync();
        gate.Release(2);
        var convert = await convertTask;
        var cancel = await cancelTask;

        Assert.True(convert.Success ^ cancel.Success, $"convert={convert.Error}; cancel={cancel.Error}");

        await using var check = _fixture.CreateDbContext();
        var storedOrder = await check.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == salesOrderId);
        var row = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        var orders = await check.PurchaseOrders.AsNoTracking()
            .Where(o => o.Remark.Contains($"比价行 #{quoteId}）")).ToListAsync();

        if (convert.Success)
        {
            Assert.Equal(DocumentStatus.Approved, storedOrder.Status);      // 取消被同一把行锁挡住
            Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, row.Status);
            Assert.Single(orders);
        }
        else
        {
            Assert.Equal(DocumentStatus.Cancelled, storedOrder.Status);     // 取消先行：转换受控拒绝
            Assert.Empty(orders);
            Assert.Equal(PurchaseQuoteConversion.SelectedStatus, row.Status);
            Assert.Contains(PurchaseQuoteConversionMutationRules.OwnershipCancelledText, convert.Error,
                StringComparison.Ordinal);
        }
    }

    // ==================== 受控拒绝：币种 / 主数据 / 归属来源 ====================

    [Fact]
    public async Task 币种无法识别_受控拒绝_绝不回退人民币也不落库()
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
            quoteId = (await AddQuoteAsync(seed, BatchPrefix + Tag(), selected: true, currency: "RUB")).Id;

        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(quoteId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("币种无法识别", ex.Message);

        await using var check = _fixture.CreateDbContext();
        var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal("RUB", source.Currency);                                  // 绝不回退 CNY
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.False(await check.PurchaseOrders.AsNoTracking().AnyAsync(o => o.Remark.Contains($"比价行 #{quoteId}）")));
    }

    [Fact]
    public async Task 非法主数据_供应商商品停用与单位缺失_受控拒绝且零写入()
    {
        Guard();
        long disabledSupplierId, disabledProductId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = new BaseSupplier
            {
                SupplierCode = $"S-418-{Tag()}", SupplierName = "ERP418 停用供应商", Status = 0
            };
            seed.BaseSuppliers.Add(supplier);
            var product = new BaseProduct
            {
                ProductCode = $"P-418-{Tag()}", ProductName = "ERP418 停用商品", Status = 0
            };
            seed.BaseProducts.Add(product);
            await seed.SaveChangesAsync();
            disabledSupplierId = supplier.Id;
            disabledProductId = product.Id;
        }

        await using var db = _fixture.CreateDbContext();
        long supplierQuoteId, productQuoteId, unitQuoteId;
        var tag = Tag();
        supplierQuoteId = (await AddQuoteAsync(db, $"{BatchPrefix}{tag}-S", selected: true,
            supplierId: disabledSupplierId)).Id;
        productQuoteId = (await AddQuoteAsync(db, $"{BatchPrefix}{tag}-P", selected: true,
            productId: disabledProductId)).Id;
        unitQuoteId = (await AddQuoteAsync(db, $"{BatchPrefix}{tag}-U", selected: true, unit: "")).Id;

        var supplierEx = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(supplierQuoteId));
        Assert.Contains(PurchaseQuoteConversionMutationRules.SupplierMasterText, supplierEx.Message);

        var productEx = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(productQuoteId));
        Assert.Contains(PurchaseQuoteConversionMutationRules.ProductMasterText, productEx.Message);

        var unitEx = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(unitQuoteId));
        Assert.Contains(PurchaseQuoteConversionMutationRules.UnitText, unitEx.Message);

        await using var check = _fixture.CreateDbContext();
        foreach (var id in new[] { supplierQuoteId, productQuoteId, unitQuoteId })
        {
            var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == id);
            Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
            Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
            Assert.False(await check.PurchaseOrders.AsNoTracking().AnyAsync(o => o.Remark.Contains($"比价行 #{id}）")));
        }
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("cancelled")]
    [InlineData("pending")]
    public async Task 归属销售订单删除取消未审核_受控拒绝且不生成无归属订单(string state)
    {
        Guard();
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var salesOrder = await AddSalesOrderAsync(seed, _fixture.CustomerAId,
                state == "cancelled" ? DocumentStatus.Cancelled
                : state == "pending" ? DocumentStatus.Pending : DocumentStatus.Approved,
                deleted: state == "deleted");
            quoteId = (await AddQuoteAsync(seed, BatchPrefix + Tag(), selected: true,
                refOrderNo: salesOrder.OrderNo, customerId: _fixture.CustomerAId)).Id;
        }

        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(quoteId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        var expected = state switch
        {
            "deleted" => PurchaseQuoteConversionMutationRules.OwnershipUnavailableText,
            "cancelled" => PurchaseQuoteConversionMutationRules.OwnershipCancelledText,
            _ => PurchaseQuoteConversionMutationRules.OwnershipNotApprovedText
        };
        Assert.Contains(expected, ex.Message);

        await using var check = _fixture.CreateDbContext();
        var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.False(await check.PurchaseOrders.AsNoTracking().AnyAsync(o => o.Remark.Contains($"比价行 #{quoteId}）")));
    }

    // ==================== 精确写入面：成功批次 + 零库存 / 财务写入 ====================

    [Fact]
    public async Task 成功批次_表头明细来源链接精确_服务端合计按币种_且零库存财务写入()
    {
        Guard();
        var batchNo = BatchPrefix + Tag();
        long lineAId, lineBId, skippedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            lineAId = (await AddQuoteAsync(seed, batchNo, selected: true, quantity: 100m, price: 2m)).Id;
            lineBId = (await AddQuoteAsync(seed, batchNo, selected: true, quantity: 300m, price: 3m)).Id;
            skippedId = (await AddQuoteAsync(seed, batchNo, selected: false, status: "待比较")).Id;
        }

        int stockBefore, movementsBefore, invoicesBefore, expensesBefore;
        await using (var before = _fixture.CreateDbContext())
        {
            stockBefore = await before.StockIns.AsNoTracking().CountAsync();
            movementsBefore = await before.StockMovements.AsNoTracking().CountAsync();
            invoicesBefore = await before.PurchaseInvoices.AsNoTracking().CountAsync();
            expensesBefore = await before.FinanceExpenses.AsNoTracking().CountAsync();
        }

        await using var db = _fixture.CreateDbContext();
        var ok = Assert.IsType<OkObjectResult>(await Controller(db, _fixture.OperatorUserId)
            .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));
        var result = Assert.IsType<ApiResponse<PurchaseQuoteBatchConversionResult>>(ok.Value).Data!;

        // 服务端合计按币种分列（本批次单币种 USD，故同时给出标量合计）。
        Assert.Equal(1, result.OrderCount);
        Assert.Equal(2, result.ConvertedLineCount);
        Assert.False(result.MixedCurrency);
        Assert.Equal(1100m, result.TotalAmount);
        var currencyTotal = Assert.Single(result.TotalAmountByCurrency);
        Assert.Equal("USD", currencyTotal.Currency);
        Assert.Equal(1100m, currencyTotal.TotalAmount);
        Assert.Equal(2, currencyTotal.LineCount);
        Assert.Equal(skippedId, Assert.Single(result.Skipped).LineId);

        await using var check = _fixture.CreateDbContext();
        var order = await check.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Remark.Contains($"比价行 #{lineAId}）"));

        // 表头：单币种、归属客户、服务端重算合计。
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(Currency.USD, order.Currency);
        Assert.Equal(1100m, order.TotalAmount);
        Assert.Equal(_fixture.CustomerAId, order.OwningCustomerId);
        Assert.StartsWith("PO", order.OrderNo);

        // 明细：逐行数量 / 单价 / 金额与单位，金额由服务端重算（来源报价总额不可信）。
        var details = await check.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == order.Id).OrderBy(d => d.Id).ToListAsync();
        Assert.Equal(2, details.Count);
        Assert.Equal(new[] { 100m, 300m }, details.Select(d => d.Quantity).ToArray());
        Assert.Equal(new[] { 2m, 3m }, details.Select(d => d.UnitPrice).ToArray());
        Assert.Equal(new[] { 200m, 900m }, details.Select(d => d.Amount).ToArray());
        Assert.All(details, d => Assert.Equal("PCS", d.Unit));
        Assert.Equal(1100m, details.Sum(d => d.Amount));

        // 来源链接：合格行逐行留痕且指向同一张采购订单；不合格行零改动。
        foreach (var id in new[] { lineAId, lineBId })
        {
            var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == id);
            Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
            Assert.Equal(order.OrderNo, source.RefOrderNo);
        }
        var skipped = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == skippedId);
        Assert.Equal("待比较", skipped.Status);
        Assert.Equal(string.Empty, skipped.RefOrderNo ?? string.Empty);

        // 库存 / 财务零写入（本路径绝不触发库存或财务过账）。
        Assert.Equal(stockBefore, await check.StockIns.AsNoTracking().CountAsync());
        Assert.Equal(movementsBefore, await check.StockMovements.AsNoTracking().CountAsync());
        Assert.Equal(invoicesBefore, await check.PurchaseInvoices.AsNoTracking().CountAsync());
        Assert.Equal(expensesBefore, await check.FinanceExpenses.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 跨币种批次_计划与结果合计显式未知_按币种分列()
    {
        Guard();
        var batchNo = BatchPrefix + Tag();
        await using (var seed = _fixture.CreateDbContext())
        {
            await AddQuoteAsync(seed, batchNo, selected: true, quantity: 100m, price: 2m, currency: "CNY");
            await AddQuoteAsync(seed, batchNo, selected: true, quantity: 100m, price: 3m, currency: "USD");
        }

        await using var db = _fixture.CreateDbContext();
        var plan = Assert.IsType<ApiResponse<PurchaseQuoteBatchPlan>>(
            Assert.IsType<OkObjectResult>(await Controller(db, _fixture.OperatorUserId).BatchOrderPlan(batchNo, null))
                .Value).Data!;
        Assert.True(plan.MixedCurrency);
        Assert.Null(plan.TotalAmount);                                    // 绝不把混合币种金额当钱
        Assert.Equal(200m, plan.TotalAmountByCurrency.Single(t => t.Currency == "CNY").TotalAmount);
        Assert.Equal(300m, plan.TotalAmountByCurrency.Single(t => t.Currency == "USD").TotalAmount);

        var result = Assert.IsType<ApiResponse<PurchaseQuoteBatchConversionResult>>(
            Assert.IsType<OkObjectResult>(await Controller(db, _fixture.OperatorUserId).BatchToOrder(
                new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo })).Value).Data!;
        Assert.True(result.MixedCurrency);
        Assert.Null(result.TotalAmount);
        Assert.Equal(2, result.OrderCount);
        Assert.Equal(2, result.TotalAmountByCurrency.Count);
    }

    // ==================== 目的地授权：越界归属即拒绝且不发号不写入 ====================

    [Fact]
    public async Task 目的地授权_归属销售订单客户越界_拒绝且不发号不写入()
    {
        Guard();
        long quoteId;
        string owningNo;
        await using (var seed = _fixture.CreateDbContext())
        {
            // 归属销售订单属于受限业务员数据范围之外的客户（CustomerB）：比价行本身在范围内，目的地权威归属越界。
            var foreignOrder = await AddSalesOrderAsync(seed, _fixture.CustomerBId);
            owningNo = foreignOrder.OrderNo;
            quoteId = (await AddQuoteAsync(seed, BatchPrefix + Tag(), selected: true,
                refOrderNo: foreignOrder.OrderNo, customerId: _fixture.CustomerAId)).Id;
        }

        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db, _fixture.OperatorUserId).ToPurchaseOrder(quoteId));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("客户数据范围", ex.Message);

        await using var check = _fixture.CreateDbContext();
        var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(owningNo, source.RefOrderNo);                    // 来源证据零改动（仍是原关联销售订单号）
        Assert.False(await check.PurchaseOrders.AsNoTracking().AnyAsync(o => o.Remark.Contains($"比价行 #{quoteId}）")));
    }

    // ==================== 强制中途失败：整体回滚（订单 / 明细 / 来源标记） ====================

    [Fact]
    public async Task 强制中途失败_批次整体回滚_无孤儿订单与来源标记()
    {
        Guard();
        var batchNo = BatchPrefix + Tag();
        long lineAId, lineBId;
        DateTime? beforeUpdatedAt;
        await using (var seed = _fixture.CreateDbContext())
        {
            var lineA = await AddQuoteAsync(seed, batchNo, selected: true);
            lineAId = lineA.Id;
            lineBId = (await AddQuoteAsync(seed, batchNo, selected: true)).Id;
            beforeUpdatedAt = await seed.PurchaseQuotes.AsNoTracking()
                .Where(q => q.Id == lineAId).Select(q => q.UpdatedAt).SingleAsync();
        }

        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .AddInterceptors(new FailingConversionSaveInterceptor())
            .Options;
        await using (var db = new ErpDbContext(options))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Controller(db, _fixture.OperatorUserId)
                .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));
            Assert.Contains("ERP-418 forced", ex.Message);
        }

        await using var check = _fixture.CreateDbContext();
        foreach (var id in new[] { lineAId, lineBId })
        {
            Assert.False(await check.PurchaseOrders.AsNoTracking()
                .AnyAsync(o => o.Remark.Contains($"比价行 #{id}）")));
            var source = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == id);
            Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
            Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
        }
        Assert.Empty(await check.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.Remark.Contains($"比价行 #{lineAId}）")).ToListAsync());
        // 事务内的行锁审计时间戳刷新同样被整体回滚。
        Assert.Equal(beforeUpdatedAt, await check.PurchaseQuotes.AsNoTracking()
            .Where(q => q.Id == lineAId).Select(q => q.UpdatedAt).SingleAsync());

        // 失败后重试（不再注入失败）可正常转换，证明失败未污染任何来源状态。
        await using var retry = _fixture.CreateDbContext();
        Assert.IsType<OkObjectResult>(await Controller(retry, _fixture.OperatorUserId)
            .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));
    }

    // ==================== 工厂 / 夹具 / 种子数据 ====================

    /// <summary>真实控制器 + 真实既有授权：HTTP 管线内身份为受控隔离账号（沿用既有菜单与客户数据范围）。</summary>
    private static PurchaseQuoteController Controller(ErpDbContext db, long? userId)
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

    /// <summary>
    /// 播种一条属于受限业务员客户数据范围、已选中且已批准的比价行（全新行，不清理既有数据）；
    /// 报价总额故意写成 1，且不引用任何主数据档案（默认供应商 / 商品主数据为弱引用，既有口径可转换）。
    /// </summary>
    private async Task<PurchaseQuote> AddQuoteAsync(ErpDbContext db, string quoteNo, bool selected,
        string status = PurchaseQuoteConversion.SelectedStatus, decimal quantity = 100m, decimal price = 2m,
        string currency = "USD", string unit = "PCS", long supplierId = 88L, long? productId = 310L,
        string refOrderNo = "", long? customerId = null)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = new DateTime(2026, 10, 1),
            ProductId = productId,
            ProductName = "ERP418 SQL 商品",
            Spec = "大号",
            Unit = unit,
            Quantity = quantity,
            SupplierId = supplierId,
            SupplierName = $"ERP418 SQL 档口 {supplierId}",
            SupplierType = "档口",
            QuotePrice = price,
            TotalAmount = 1m,
            Currency = currency,
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = selected,
            Status = status,
            CustomerId = customerId ?? _fixture.CustomerAId,
            CustomerName = "ERP416 可见客户",
            RefOrderNo = refOrderNo,
            Remark = "ERP418_SQL_TEST"
        };
        db.PurchaseQuotes.Add(quote);
        await db.SaveChangesAsync();

        // ERP-095：合格行需先「批准选中」（决定人为既有隔离账号，保持既有审批生命周期口径）。
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId,
            SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = _fixture.OperatorUserId,
            DecidedByName = "ERP418 隔离账号",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
        });
        await db.SaveChangesAsync();
        return quote;
    }

    /// <summary>播种一张归属指定客户的销售订单（默认已审核，不引用任何销售明细以保证判定可预期）。</summary>
    private static async Task<SalesOrder> AddSalesOrderAsync(ErpDbContext db, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-418-{Tag()}",
            OrderDate = new DateTime(2026, 10, 1),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            TotalAmount = 100m,
            Status = status,
            IsDeleted = deleted,
            Remark = "ERP418_SQL_TEST"
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// ERP-418 强制中途失败注入：仅在<b>新增采购订单</b>时抛出，用于证明「取号 + 订单 + 明细 + 来源留痕 +
/// 行锁审计时间戳刷新」在同一真实事务内被整体回滚（失败侧零部分写入）。
/// </summary>
internal sealed class FailingConversionSaveInterceptor : SaveChangesInterceptor
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
