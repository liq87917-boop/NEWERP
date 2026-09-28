using System.Reflection;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商报价价格历史（ERP-098）单元测试：
/// 相同比价口径（规格 + 单位 + 币种 + 含税）分组、组内价格差异、筛选与软删除、
/// 分页有界与截断、审批选中上下文回显、无写入语义、参数校验与接口路由契约。
/// 说明：全部使用内存数据库，不连接 SQL Server、不启动 API（browser_deferred）。
/// </summary>
public class PurchaseQuotePriceHistoryTests
{
    // ==================== 分组 ====================

    [Fact]
    public async Task 相同商品不同口径_按规格单位币种含税分组()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810001L;
        SeedQuote(db, "PQ-098-A1", productId, 91L, new DateTime(2026, 9, 1), "大号", "PCS", "CNY", taxIncluded: false);
        SeedQuote(db, "PQ-098-A2", productId, 92L, new DateTime(2026, 9, 2), "大号", "PCS", "CNY", taxIncluded: false);
        SeedQuote(db, "PQ-098-B1", productId, 93L, new DateTime(2026, 9, 1), "大号", "PCS", "USD", taxIncluded: false);
        SeedQuote(db, "PQ-098-C1", productId, 94L, new DateTime(2026, 9, 1), "大号", "PCS", "CNY", taxIncluded: true);
        SeedQuote(db, "PQ-098-D1", productId, 95L, new DateTime(2026, 9, 1), "小号", "PCS", "CNY", taxIncluded: false);

        var view = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, PageSize = 100 });

        Assert.Equal(4, view.GroupCount);
        var cnyNoTax = view.Groups.Single(g => g.Currency == "CNY" && !g.TaxIncluded && g.Spec == "大号");
        Assert.Equal(2, cnyNoTax.RowCount);
        Assert.Equal(2, cnyNoTax.Rows.Count);
        Assert.All(cnyNoTax.Rows, r => Assert.Equal("PCS", r.Unit));
    }

    [Fact]
    public async Task 组内价格差异_最低最高最新价差只在该口径内计算()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810002L;
        SeedQuote(db, "PQ-098-P1", productId, 91L, new DateTime(2026, 9, 1), price: 2m);
        SeedQuote(db, "PQ-098-P2", productId, 92L, new DateTime(2026, 9, 2), price: 3m);
        // 口径不同（币种 USD）：不得并入上面 CNY 组的价格差异
        SeedQuote(db, "PQ-098-P3", productId, 93L, new DateTime(2026, 9, 1), currency: "USD", price: 100m);

        var view = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, PageSize = 100 });

        Assert.Equal(2, view.GroupCount);
        var cny = view.Groups.Single(g => g.Currency == "CNY");
        Assert.Equal(2m, cny.MinPrice);
        Assert.Equal(3m, cny.MaxPrice);
        Assert.Equal(3m, cny.LatestPrice); // 最新报价 = 9/2 那行
        Assert.Equal(1m, cny.PriceSpread);

        var usd = view.Groups.Single(g => g.Currency == "USD");
        Assert.Equal(100m, usd.MinPrice);
        Assert.Equal(100m, usd.MaxPrice);
        Assert.Equal(0m, usd.PriceSpread);
    }

    // ==================== 筛选与软删除 ====================

    [Fact]
    public async Task 供应商与日期区间筛选_并排除软删除行()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810003L;
        var q1 = SeedQuote(db, "PQ-098-S1", productId, 91L, new DateTime(2026, 9, 1));
        SeedQuote(db, "PQ-098-S2", productId, 92L, new DateTime(2026, 9, 5));
        var q3 = SeedQuote(db, "PQ-098-S3", productId, 91L, new DateTime(2026, 9, 10));
        q3.IsDeleted = true;
        db.SaveChanges();

        var bySupplier = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, SupplierId = 91L, PageSize = 100 });
        Assert.Equal(1, bySupplier.TotalCount);
        Assert.Equal(q1.Id, bySupplier.Groups.Single().Rows.Single().QuoteId);

        var byDate = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, DateFrom = new DateTime(2026, 9, 2), DateTo = new DateTime(2026, 9, 5), PageSize = 100 });
        Assert.Equal(1, byDate.TotalCount);
        Assert.Equal("PQ-098-S2", byDate.Groups.Single().Rows.Single().QuoteNo);
    }

    [Fact]
    public async Task 稳定按报价日期与Id排序()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810004L;
        SeedQuote(db, "PQ-098-O2", productId, 91L, new DateTime(2026, 9, 2));
        SeedQuote(db, "PQ-098-O1", productId, 91L, new DateTime(2026, 9, 1));
        SeedQuote(db, "PQ-098-O1B", productId, 92L, new DateTime(2026, 9, 1));

        var view = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, PageSize = 100 });
        var rows = view.Groups.Single().Rows;
        Assert.Equal(new[] { "PQ-098-O1", "PQ-098-O1B", "PQ-098-O2" }, rows.Select(r => r.QuoteNo).ToArray());
    }
    // ==================== 分页有界与截断 ====================

    [Fact]
    public async Task 分页有界_并显式标记截断()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810005L;
        for (var i = 0; i < 3; i++)
            SeedQuote(db, $"PQ-098-T{i}", productId, 91L, new DateTime(2026, 9, 1 + i));

        var view = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, Page = 1, PageSize = 2 });

        Assert.Equal(3, view.TotalCount);
        Assert.True(view.Truncated);
        Assert.Equal(2, view.Groups.Single().RowCount);

        var lastPage = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        { ProductId = productId, Page = 2, PageSize = 2 });
        Assert.False(lastPage.Truncated);
        Assert.Equal(1, lastPage.Groups.Single().RowCount);
    }

    // ==================== 无写入语义 ====================

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810006L;
        SeedQuote(db, "PQ-098-R1", productId, 91L, new DateTime(2026, 9, 1));
        SeedQuote(db, "PQ-098-R2", productId, 92L, new DateTime(2026, 9, 2));

        var beforeQuotes = db.PurchaseQuotes.Count();
        var beforeDecisions = db.PurchaseQuoteDecisions.Count();
        await QueryAsync(db, new PurchaseQuotePriceHistoryQuery { ProductId = productId, PageSize = 100 });

        Assert.Equal(beforeQuotes, db.PurchaseQuotes.Count());
        Assert.Equal(beforeDecisions, db.PurchaseQuoteDecisions.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 审批选中上下文 ====================

    [Fact]
    public async Task 审批与选中上下文_作为证据回显()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810007L;
        var approved = SeedQuote(db, "PQ-098-AV1", productId, 91L, new DateTime(2026, 9, 1),
            isSelected: true, status: "已选中");
        SeedDecision(db, approved, "Approved", "单价最低", "采购主管");
        SeedQuote(db, "PQ-098-AV2", productId, 92L, new DateTime(2026, 9, 2));

        var view = await QueryAsync(db, new PurchaseQuotePriceHistoryQuery { ProductId = productId, PageSize = 100 });
        var rows = view.Groups.Single().Rows;

        var approvedRow = rows.Single(r => r.QuoteId == approved.Id);
        Assert.Equal("Approved", approvedRow.ApprovalState);
        Assert.Equal("单价最低", approvedRow.DecisionBasis);
        Assert.Equal("采购主管", approvedRow.DecidedByName);
        Assert.True(approvedRow.IsSelected);

        var pendingRow = rows.Single(r => r.QuoteNo == "PQ-098-AV2");
        Assert.Equal("Pending", pendingRow.ApprovalState);
        Assert.Equal(string.Empty, pendingRow.DecisionBasis);
    }

    // ==================== 从比价行打开 ====================

    [Fact]
    public async Task 从比价行打开_标记可同比价组()
    {
        using var db = TestDbFactory.Create();
        const long productId = 810008L;
        var reference = SeedQuote(db, "PQ-098-F1", productId, 91L, new DateTime(2026, 9, 1),
            currency: "CNY", taxIncluded: false);
        SeedQuote(db, "PQ-098-F2", productId, 92L, new DateTime(2026, 9, 2),
            currency: "CNY", taxIncluded: false);
        SeedQuote(db, "PQ-098-F3", productId, 93L, new DateTime(2026, 9, 1),
            currency: "USD", taxIncluded: false);

        var view = await PurchaseQuotePriceHistory.ForQuoteAsync(db, reference.Id);

        Assert.Equal(reference.Id, view.ReferenceQuoteId);
        Assert.NotNull(view.ReferenceBasisText);
        var cny = view.Groups.Single(g => g.Currency == "CNY");
        Assert.True(cny.Comparable);
        Assert.Contains(reference.Id, cny.Rows.Select(r => r.QuoteId));
        var usd = view.Groups.Single(g => g.Currency == "USD");
        Assert.False(usd.Comparable);
    }

    // ==================== 参数校验 ====================

    [Fact]
    public async Task 缺商品Id_或日期区间倒置_抛参数错误()
    {
        using var db = TestDbFactory.Create();
        var noProduct = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new PurchaseQuotePriceHistoryQuery { ProductId = null }));
        Assert.Equal(ErrorCodes.InvalidParameter, noProduct.Code);

        var badDate = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new PurchaseQuotePriceHistoryQuery
            {
                ProductId = 1L,
                DateFrom = new DateTime(2026, 9, 10),
                DateTo = new DateTime(2026, 9, 1),
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, badDate.Code);
    }

    // ==================== 接口路由契约 ====================

    [Fact]
    public void 接口路由_与前端调用路径一致()
    {
        var route = typeof(PurchaseQuoteController).GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/purchase/quotes", route);
        AssertRoute(nameof(PurchaseQuoteController.PriceHistory), "price-history");
        AssertRoute(nameof(PurchaseQuoteController.QuotePriceHistory), "{id:long}/price-history");
    }

    [Fact]
    public void 前端接线_行操作函数_接口路径与脚本加载()
    {
        var modules = File.ReadAllText(Path.Combine(JsDirectory(), "modules.js"));
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "purchase-quote-price-history.js"));
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        Assert.Contains("onclick: 'showPurchaseQuotePriceHistory'", modules);
        Assert.Contains("function showPurchaseQuotePriceHistory(", js);
        Assert.Contains("`/api/purchase/quotes/${id}/price-history`", js);
        Assert.Contains("/js/purchase-quote-price-history.js", indexHtml);
    }
    // ==================== 助手 ====================

    private static async Task<PurchaseQuotePriceHistoryView> QueryAsync(ErpDbContext db,
        PurchaseQuotePriceHistoryQuery query)
        => await PurchaseQuotePriceHistory.QueryAsync(db, query);

    private static PurchaseQuote SeedQuote(ErpDbContext db, string quoteNo, long productId,
        long? supplierId, DateTime quoteDate, string spec = "大号", string unit = "PCS",
        string currency = "CNY", bool taxIncluded = false, decimal price = 2m,
        bool isSelected = false, string status = "待比较")
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = quoteDate,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = spec,
            Unit = unit,
            Quantity = 100m,
            SupplierId = supplierId,
            SupplierName = $"供应商{supplierId}",
            SupplierType = "档口",
            QuotePrice = price,
            TotalAmount = price * 100m,
            Currency = currency,
            TaxIncluded = taxIncluded,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = isSelected,
            Status = status,
            Remark = "HISTORY_TEST",
        };
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }

    private static void SeedDecision(ErpDbContext db, PurchaseQuote quote, string decision,
        string basis, string decidedByName)
    {
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = decision,
            SelectedSupplierId = decision == PurchaseQuoteApproval.Approved ? quote.SupplierId : null,
            SelectedSupplierName = decision == PurchaseQuoteApproval.Approved ? quote.SupplierName : string.Empty,
            DecisionBasis = basis,
            DecidedByName = decidedByName,
            DecidedAt = DateTime.Now,
            DecisionRef = $"APV-{quote.QuoteNo}-#{quote.Id}",
        });
        db.SaveChanges();
    }

    private static void AssertRoute(string methodName, string template)
    {
        var method = typeof(PurchaseQuoteController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(template, templates);
    }

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
