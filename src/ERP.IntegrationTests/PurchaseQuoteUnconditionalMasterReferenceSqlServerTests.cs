using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-491：供应商比价「实时主数据引用」复核**与调用形状完全无关**的**真实 SQL Server** 集成测试
/// （复用 <see cref="PurchaseQuoteMasterReferenceSqlServerFixture"/> 的 GUID 独占 <c>NEWERP_AUTOTEST</c> 目标：
/// <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>，
/// 每次运行全新 GUID 库、发现同名库立即拒绝、绝不 drop / reset / 复用）。
/// <list type="number">
/// <item><b>调用形状无关</b>：同一载荷 + 同一真实启用身份下，「空 <c>Request.Path</c>」与「已赋值
/// <c>Request.Path</c>」在真实控制器上给出**完全相同**的受控结论，且被拒绝的请求零写入 / 零占号；</item>
/// <item><b>允许的草稿</b>：合法实时客户 / 供应商 / 商品引用在两种形状下照常落库；</item>
/// <item><b>承诺前复核</b>：带入预填 / 单行转采购订单在来源行锁内复核来源实时引用，显式商品引用缺失
/// （<c>ProductId = 0</c> 绝不静默占位）与归属客户被删除（撤销）一律原子拒绝，零订单 / 零发号 / 来源状态不变；</item>
/// <item><b>批次整批拒绝</b>：任一组来源客户失效即整批拒绝，绝不生成部分采购订单 / 部分批次组；</item>
/// <item><b>合法来源承诺</b>：实时主数据齐备时照常生成采购订单并回写来源留痕（单号恰好 +1）。</item>
/// </list>
/// <para>只读取计数与只读快照，不删除 / 不清理任何既有行，不新增菜单 / 角色 / 用户授权；构建完成不等于阶段验收，
/// 只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseQuoteUnconditionalMasterReferenceSqlServerTests
    : IClassFixture<PurchaseQuoteMasterReferenceSqlServerFixture>
{
    private const string QuotePath = "/api/purchase/quotes";

    private readonly PurchaseQuoteMasterReferenceSqlServerFixture _fixture;

    public PurchaseQuoteUnconditionalMasterReferenceSqlServerTests(
        PurchaseQuoteMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => PurchaseQuoteMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    /// <summary>
    /// 真实控制器：<paramref name="path"/> 为 <c>null</c> 时身份存在但 <c>Request.Path</c> 无值（空请求路径形状）。
    /// ERP-491 后两种形状必须给出完全相同的实时主数据引用结论。
    /// </summary>
    private static PurchaseQuoteController NewController(ErpDbContext db, long userId, string? path)
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static PurchaseQuote NewDraftBody(string quoteNo, long? customerId, long? supplierId, long? productId)
        => new()
        {
            QuoteNo = quoteNo,
            QuoteDate = DateTime.Today,
            ProductId = productId,
            ProductName = "ERP491 集成商品",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 100m,
            SupplierId = supplierId,
            SupplierName = supplierId is > 0 ? $"ERP491 集成供应商 {supplierId}" : string.Empty,
            SupplierType = "档口",
            QuotePrice = 2m,
            TotalAmount = 200m,
            Currency = "USD",
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = false,
            Status = PurchaseQuoteMutationRules.PendingStatus,
            CustomerId = customerId,
            CustomerName = "ERP491 客户名快照",
            Remark = "ERP491_SQL_TEST"
        };

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PurchaseQuotes"] = await db.PurchaseQuotes.CountAsync(),
            ["PurchaseOrders"] = await db.PurchaseOrders.CountAsync(),
            ["PurchaseOrderDetails"] = await db.PurchaseOrderDetails.CountAsync(),
            ["PurchaseQuoteDecisions"] = await db.PurchaseQuoteDecisions.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(Dictionary<string, int> before,
        Dictionary<string, int> after)
    {
        foreach (var (table, count) in before)
            Assert.Equal(count, after[table]);
    }

    private async Task<PurchaseQuote> ReadQuoteAsync(long quoteId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
    }

    // ==================== 1. 草稿写入：调用形状无关 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 新增草稿_合法实时主数据_空路径与真实路径一致落库(string? path)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var draft = NewDraftBody($"PQ-491-SQL-{Tag()}", _fixture.CustomerId, _fixture.SupplierId,
            _fixture.ProductId);

        var ok = Assert.IsType<OkObjectResult>(await NewController(db, _fixture.PrivilegedUserId, path)
            .Create(draft));

        var created = Assert.IsType<ApiResponse<PurchaseQuote>>(ok.Value).Data!;
        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == created.Id);
        Assert.Equal(_fixture.SupplierId, stored.SupplierId);
        Assert.Equal(_fixture.ProductId, stored.ProductId);
        Assert.Equal(_fixture.CustomerId, stored.CustomerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 新增草稿_悬空归属客户_空路径与真实路径同样拒绝且零写入(string? path)
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var draft = NewDraftBody($"PQ-491-SQL-{Tag()}", _fixture.MissingCustomerId, _fixture.SupplierId,
            _fixture.ProductId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db,
            _fixture.PrivilegedUserId, path).Create(draft));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 2. 采购承诺：显式引用缺失 / 主数据撤销 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 带入预填与单行转单_商品引用缺失_空路径与真实路径同样拒绝零发号(string? path)
    {
        Guard();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-491-SQL-{Tag()}",
            _fixture.SupplierId, productId: null, customerId: _fixture.CustomerId);
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId, path);

        var prefill = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(quote.Id));
        Assert.Equal(ErrorCodes.RuleConflict, prefill.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, prefill.Message);

        var toOrder = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));
        Assert.Equal(ErrorCodes.RuleConflict, toOrder.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, toOrder.Message);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.PurchaseOrderSequenceAsync());
        var source = await ReadQuoteAsync(quote.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 单行转采购订单_承诺前归属客户被删除_空路径与真实路径同样零写入零发号(string? path)
    {
        Guard();
        var supplierId = await _fixture.SeedTransientSupplierAsync();
        var customerId = await _fixture.SeedTransientCustomerAsync();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-491-SQL-{Tag()}",
            supplierId, _fixture.ProductId, customerId);
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        await using var db = _fixture.CreateDbContext();

        await _fixture.SoftDeleteCustomerAsync(customerId);   // 选中 / 批准后归属客户被撤回

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId, path).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.PurchaseOrderSequenceAsync());
        var source = await ReadQuoteAsync(quote.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
    }


    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 批次转采购订单_任一组来源客户被删除_空路径与真实路径同样整批拒绝(string? path)
    {
        Guard();
        var supplierA = await _fixture.SeedTransientSupplierAsync();
        var supplierB = await _fixture.SeedTransientSupplierAsync();
        var customerA = await _fixture.SeedTransientCustomerAsync();
        var customerB = await _fixture.SeedTransientCustomerAsync();
        var batchNo = $"PQ-491-SQL-{Tag()}";

        // 同一批次两个不同的兼容组（供应商不同 → 两张订单）。
        await _fixture.SeedSelectedApprovedQuoteAsync(batchNo, supplierA, _fixture.ProductId, customerA);
        await _fixture.SeedSelectedApprovedQuoteAsync(batchNo, supplierB, _fixture.ProductId, customerB);
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        await using var db = _fixture.CreateDbContext();

        await _fixture.SoftDeleteCustomerAsync(customerB);   // 仅一组失效

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db,
            _fixture.PrivilegedUserId, path).BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.PurchaseOrderSequenceAsync());
    }

    // ==================== 3. 合法来源承诺：照常生成并留痕 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 单行转采购订单_合法实时主数据_空路径与真实路径一致正向落库并留痕(string? path)
    {
        Guard();
        var supplierId = await _fixture.SeedTransientSupplierAsync();
        var customerId = await _fixture.SeedTransientCustomerAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-491-SQL-{Tag()}",
            supplierId, _fixture.ProductId, customerId);
        await using var db = _fixture.CreateDbContext();

        var ok = Assert.IsType<OkObjectResult>(await NewController(db, _fixture.PrivilegedUserId, path)
            .ToPurchaseOrder(quote.Id));

        var data = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Data!;
        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.Include(o => o.Details).AsNoTracking()
            .SingleAsync(o => o.Id == data.Id);
        Assert.Equal(supplierId, order.SupplierId);
        Assert.Equal(customerId, order.OwningCustomerId);
        Assert.Equal(_fixture.ProductId, order.Details.Single().ProductId);
        Assert.Equal(sequenceBefore + 1, await _fixture.PurchaseOrderSequenceAsync());
        var source = await ReadQuoteAsync(quote.Id);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
        Assert.Equal(order.OrderNo, source.RefOrderNo);
    }
}

