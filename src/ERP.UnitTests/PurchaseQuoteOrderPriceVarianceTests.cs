using System.Reflection;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商比价 → 采购订单价格差异（ERP-105）单元测试：
/// 单行 / 批次转换、重复商品歧义、单位 / 币种 / 含税口径不一致、陈旧链接、未链接、
/// 分页有界与截断、无写入语义、从比价行打开、参数校验与接口 / 前端接线契约。
/// 说明：全部使用内存数据库，不连接 SQL Server、不启动 API（browser_deferred）。
/// </summary>
public class PurchaseQuoteOrderPriceVarianceTests
{
    // ==================== 单行转换 ====================

    [Fact]
    public async Task 单行转换_口径一致_计算单价差与金额差()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820001L;
        var quote = SeedConvertedQuote(db, "PQ-105-A1", productId, 91L, new DateTime(2026, 9, 1), "PO-105-A1");
        var order = SeedOrderHeader(db, quote, "PO-105-A1");
        var detail = SeedDetail(db, order.Id, quote, unitPrice: 12m, amount: 1200m);

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        Assert.Equal(1, view.TotalCount);
        Assert.Equal(1, view.ResolvedCount);
        Assert.Equal(0, view.UnresolvedCount);
        var row = Assert.Single(view.Rows);
        Assert.True(row.Resolved);
        Assert.Equal(2m, row.UnitPriceDelta);       // 12 - 10
        Assert.Equal(200m, row.AmountDelta);        // 1200 - 1000
        Assert.Equal("PO-105-A1", row.OrderNo);
        Assert.Equal(detail.Id, row.OrderDetailId);
        Assert.Equal(12m, row.OrderUnitPrice);
        Assert.Equal(1200m, row.OrderAmount);
        Assert.Equal(string.Empty, row.Reason);
    }

    // ==================== 批次转换 / 重复商品 ====================

    [Fact]
    public async Task 批次转换_重复商品_来源标记唯一定位()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820002L;
        var q1 = SeedConvertedQuote(db, "PQ-105-B", productId, 91L, new DateTime(2026, 9, 1), "PO-105-B", price: 10m);
        var q2 = SeedConvertedQuote(db, "PQ-105-B", productId, 91L, new DateTime(2026, 9, 1), "PO-105-B", price: 11m);

        var orderRemark = PurchaseQuoteConversion.SourceMarker(q1) + " ｜ " + PurchaseQuoteConversion.SourceMarker(q2);
        var order = SeedOrderHeader(db, q1, "PO-105-B", orderRemark);
        var d1 = SeedDetail(db, order.Id, q1, unitPrice: 12m, amount: 1200m, detailRemark: PurchaseQuoteConversion.SourceMarker(q1));
        var d2 = SeedDetail(db, order.Id, q2, unitPrice: 10m, amount: 1000m, detailRemark: PurchaseQuoteConversion.SourceMarker(q2));

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        Assert.Equal(2, view.ResolvedCount);
        Assert.Equal(0, view.UnresolvedCount);
        Assert.All(view.Rows, r => Assert.True(r.Resolved));
        var row1 = view.Rows.Single(r => r.QuoteId == q1.Id);
        Assert.Equal(d1.Id, row1.OrderDetailId);
        Assert.Equal(2m, row1.UnitPriceDelta);
        var row2 = view.Rows.Single(r => r.QuoteId == q2.Id);
        Assert.Equal(d2.Id, row2.OrderDetailId);
        Assert.Equal(-1m, row2.UnitPriceDelta);     // 10 - 11
    }

    [Fact]
    public async Task 重复商品_无来源标记_明细歧义未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820003L;
        var quote = SeedConvertedQuote(db, "PQ-105-C", productId, 91L, new DateTime(2026, 9, 1), "PO-105-C");
        var order = SeedOrderHeader(db, quote, "PO-105-C");
        SeedDetail(db, order.Id, quote, unitPrice: 12m, amount: 1200m);
        SeedDetail(db, order.Id, quote, unitPrice: 13m, amount: 1300m);

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.False(row.Resolved);
        Assert.Null(row.UnitPriceDelta);
        Assert.Null(row.AmountDelta);
        Assert.Contains("歧义", row.Reason);
    }

    // ==================== 口径不一致 ====================

    [Fact]
    public async Task 单位不一致_未解决且无价差()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820004L;
        var quote = SeedConvertedQuote(db, "PQ-105-U", productId, 91L, new DateTime(2026, 9, 1), "PO-105-U", unit: "PCS");
        var order = SeedOrderHeader(db, quote, "PO-105-U");
        SeedDetail(db, order.Id, quote, unitPrice: 12m, amount: 1200m, detailUnit: "BOX");

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.False(row.Resolved);
        Assert.Null(row.UnitPriceDelta);
        Assert.Null(row.AmountDelta);
        Assert.Contains("单位", row.Reason);
    }

    [Fact]
    public async Task 币种不一致_未解决且无价差()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820005L;
        var quote = SeedConvertedQuote(db, "PQ-105-CY", productId, 91L, new DateTime(2026, 9, 1), "PO-105-CY", currency: "USD");
        var order = SeedOrderHeader(db, quote, "PO-105-CY", currency: Currency.CNY);

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.False(row.Resolved);
        Assert.Null(row.UnitPriceDelta);
        Assert.Null(row.AmountDelta);
        Assert.Contains("币种", row.Reason);
        Assert.Equal("CNY", row.OrderCurrency);
    }

    [Fact]
    public async Task 含税口径不一致_未解决且无价差()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820006L;
        var quote = SeedConvertedQuote(db, "PQ-105-TX", productId, 91L, new DateTime(2026, 9, 1), "PO-105-TX", taxIncluded: true);
        var order = SeedOrderHeader(db, quote, "PO-105-TX", taxIncluded: false);

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.False(row.Resolved);
        Assert.Null(row.UnitPriceDelta);
        Assert.Null(row.AmountDelta);
        Assert.Contains("含税", row.Reason);
    }

    // ==================== 陈旧链接 / 删除 / 未链接 ====================

    [Fact]
    public async Task 陈旧链接_订单备注缺失来源标记_未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820007L;
        var quote = SeedConvertedQuote(db, "PQ-105-S", productId, 91L, new DateTime(2026, 9, 1), "PO-105-S");
        var order = SeedOrderHeader(db, quote, "PO-105-S", orderRemark: "人工创建的采购订单（无来源标记）");
        SeedDetail(db, order.Id, quote, unitPrice: 12m, amount: 1200m);

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.False(row.Resolved);
        Assert.Null(row.UnitPriceDelta);
        Assert.Null(row.AmountDelta);
        Assert.Contains("陈旧链接", row.Reason);
    }

    [Fact]
    public async Task 订单已删除或不存在_未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820008L;
        SeedConvertedQuote(db, "PQ-105-D1", productId, 91L, new DateTime(2026, 9, 1), "PO-105-D1");
        var deleted = SeedConvertedQuote(db, "PQ-105-D2", productId, 91L, new DateTime(2026, 9, 2), "PO-105-D2");
        var order = SeedOrderHeader(db, deleted, "PO-105-D2");
        order.IsDeleted = true;
        db.SaveChanges();

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        Assert.Equal(2, view.UnresolvedCount);
        Assert.All(view.Rows, r =>
        {
            Assert.False(r.Resolved);
            Assert.Null(r.UnitPriceDelta);
            Assert.Contains("不存在或已删除", r.Reason);
        });
    }

    [Fact]
    public async Task 未链接_RefOrderNo为空_未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820009L;
        SeedConvertedQuote(db, "PQ-105-NL", productId, 91L, new DateTime(2026, 9, 1), string.Empty);

        var view = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.False(row.Resolved);
        Assert.Null(row.UnitPriceDelta);
        Assert.Contains("未链接", row.Reason);
    }

    // ==================== 分页 / 筛选 / 排序 ====================

    [Fact]
    public async Task 分页有界_供应商与日期筛选_稳定排序_并显式截断()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820010L;
        var a = SeedConvertedQuote(db, "PQ-105-P1", productId, 91L, new DateTime(2026, 9, 1), "PO-105-P1");
        var b = SeedConvertedQuote(db, "PQ-105-P2", productId, 92L, new DateTime(2026, 9, 2), "PO-105-P2");
        var c = SeedConvertedQuote(db, "PQ-105-P3", productId, 91L, new DateTime(2026, 9, 3), "PO-105-P3");
        foreach (var q in new[] { a, b, c })
        {
            var order = SeedOrderHeader(db, q, q.RefOrderNo);
            SeedDetail(db, order.Id, q, unitPrice: 12m, amount: 1200m);
        }

        var page1 = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.TotalCount);
        Assert.True(page1.Truncated);
        Assert.Equal(2, page1.Rows.Count);
        Assert.Equal(new[] { a.Id, b.Id }, page1.Rows.Select(r => r.QuoteId).ToArray());

        var bySupplier = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { SupplierId = 91L, PageSize = 100 });
        Assert.Equal(2, bySupplier.TotalCount);
        Assert.All(bySupplier.Rows, r => Assert.Equal(91L, r.SupplierId));

        var byDate = await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery
        { DateFrom = new DateTime(2026, 9, 2), DateTo = new DateTime(2026, 9, 2), PageSize = 100 });
        Assert.Equal(1, byDate.TotalCount);
        Assert.Equal(b.Id, byDate.Rows.Single().QuoteId);
    }

    // ==================== 无写入语义 ====================

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820011L;
        var quote = SeedConvertedQuote(db, "PQ-105-R", productId, 91L, new DateTime(2026, 9, 1), "PO-105-R");
        var order = SeedOrderHeader(db, quote, "PO-105-R");
        SeedDetail(db, order.Id, quote, unitPrice: 12m, amount: 1200m);

        var beforeQuotes = db.PurchaseQuotes.Count();
        var beforeOrders = db.PurchaseOrders.Count();
        var beforeDetails = db.PurchaseOrderDetails.Count();
        await QueryAsync(db, new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 100 });

        Assert.Equal(beforeQuotes, db.PurchaseQuotes.Count());
        Assert.Equal(beforeOrders, db.PurchaseOrders.Count());
        Assert.Equal(beforeDetails, db.PurchaseOrderDetails.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 从比价行打开 ====================

    [Fact]
    public async Task 从比价行打开_返回该行对照_未转订单显式标注()
    {
        using var db = TestDbFactory.Create();
        const long productId = 820012L;
        var converted = SeedConvertedQuote(db, "PQ-105-F1", productId, 91L, new DateTime(2026, 9, 1), "PO-105-F1");
        var order = SeedOrderHeader(db, converted, "PO-105-F1");
        SeedDetail(db, order.Id, converted, unitPrice: 12m, amount: 1200m);
        var pending = SeedConvertedQuote(db, "PQ-105-F2", productId, 92L, new DateTime(2026, 9, 2), string.Empty, status: "已选中");

        var view = await PurchaseQuoteOrderPriceVariance.ForQuoteAsync(db, converted.Id);
        var row = Assert.Single(view.Rows);
        Assert.True(row.Resolved);
        Assert.Equal(converted.Id, row.QuoteId);

        var pendingView = await PurchaseQuoteOrderPriceVariance.ForQuoteAsync(db, pending.Id);
        var pendingRow = Assert.Single(pendingView.Rows);
        Assert.False(pendingRow.Resolved);
        Assert.Contains("尚未转采购订单", pendingRow.Reason);
    }

    // ==================== 参数校验 ====================

    [Fact]
    public async Task 日期区间倒置_抛参数错误()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new PurchaseQuoteOrderPriceVarianceQuery
            {
                DateFrom = new DateTime(2026, 9, 10),
                DateTo = new DateTime(2026, 9, 1),
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 接口路由 / 前端接线 / 文档 ====================

    [Fact]
    public void 接口路由_与前端调用路径一致()
    {
        var route = typeof(PurchaseQuoteController).GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/purchase/quotes", route);
        AssertRoute(nameof(PurchaseQuoteController.OrderPriceVariance), "order-price-variance");
        AssertRoute(nameof(PurchaseQuoteController.QuoteOrderPriceVariance), "{id:long}/order-price-variance");
    }

    [Fact]
    public void 前端接线_行操作函数_接口路径与脚本加载_及规则文档()
    {
        var modules = File.ReadAllText(Path.Combine(JsDirectory(), "modules.js"));
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "purchase-quote-order-price-variance.js"));
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        Assert.Contains("onclick: 'showPurchaseQuoteOrderPriceVariance'", modules);
        Assert.Contains("function showPurchaseQuoteOrderPriceVariance(", js);
        Assert.Contains("`/api/purchase/quotes/${id}/order-price-variance`", js);
        Assert.Contains("/js/purchase-quote-order-price-variance.js", indexHtml);

        Assert.Contains("不重定价", PurchaseQuoteOrderPriceVarianceRules.RuleText);
        var doc = File.ReadAllText(DocPath());
        Assert.Contains("ERP-105", doc);
        Assert.Contains("价格差异", doc);
    }

    // ==================== 助手 ====================

    private static async Task<PurchaseQuoteOrderPriceVarianceView> QueryAsync(ErpDbContext db,
        PurchaseQuoteOrderPriceVarianceQuery query)
        => await PurchaseQuoteOrderPriceVariance.QueryAsync(db, query);

    private static PurchaseQuote SeedConvertedQuote(ErpDbContext db, string quoteNo, long productId,
        long? supplierId, DateTime quoteDate, string orderNo, string spec = "大号", string unit = "PCS",
        string currency = "CNY", bool taxIncluded = false, decimal price = 10m, decimal quantity = 100m,
        string status = PurchaseQuoteConversion.ConvertedStatus)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = quoteDate,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = spec,
            Unit = unit,
            Quantity = quantity,
            SupplierId = supplierId,
            SupplierName = $"供应商{supplierId}",
            SupplierType = "档口",
            QuotePrice = price,
            TotalAmount = price * quantity,
            Currency = currency,
            TaxIncluded = taxIncluded,
            IsSelected = true,
            Status = status,
            RefOrderNo = orderNo,
            Remark = "VARIANCE_TEST"
        };
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }

    private static PurchaseOrder SeedOrderHeader(ErpDbContext db, PurchaseQuote quote, string orderNo,
        string? orderRemark = null, Currency? currency = null, bool? taxIncluded = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = quote.SupplierId ?? 0,
            Currency = currency ?? PurchaseQuoteConversion.ParseCurrency(quote.Currency),
            ExchangeRate = 1m,
            TaxIncluded = taxIncluded ?? quote.TaxIncluded,
            TotalAmount = quote.TotalAmount,
            Status = DocumentStatus.Approved,
            Remark = orderRemark ?? PurchaseQuoteConversion.SourceMarker(quote)
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrderDetail SeedDetail(ErpDbContext db, long orderId, PurchaseQuote quote,
        decimal unitPrice, decimal amount, string? detailRemark = null, string? detailUnit = null, string? detailSpec = null)
    {
        var detail = new PurchaseOrderDetail
        {
            PurchaseOrderId = orderId,
            ProductId = quote.ProductId ?? 0,
            ProductName = quote.ProductName,
            Spec = detailSpec ?? quote.Spec,
            Unit = detailUnit ?? quote.Unit,
            Quantity = quote.Quantity,
            UnitPrice = unitPrice,
            Amount = amount,
            Remark = detailRemark ?? string.Empty
        };
        db.PurchaseOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
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

    private static string DocPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "docs", "purchase-quote-order-price-variance.md"));
}


