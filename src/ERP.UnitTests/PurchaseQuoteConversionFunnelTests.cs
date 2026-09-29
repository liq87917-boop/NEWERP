using System.Reflection;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商报价 → 采购订单转化漏斗（ERP-103）单元测试：
/// 多供应商批次六类证据计数、审批变化、批次转换、陈旧 / 删除 / 未链接、RefOrderNo 不单独推断转换、
/// 分页有界与截断、供应商 / 日期筛选、无写入语义、接口路由与前端接线契约。
/// 说明：全部使用内存数据库，不连接 SQL Server、不启动 API（browser_deferred）。
/// </summary>
public class PurchaseQuoteConversionFunnelTests
{
    // ==================== 多供应商批次 ====================

    [Fact]
    public async Task 多供应商批次_六类证据分别计数()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830001L;
        var quoted = SeedQuote(db, "PQ-103-A", productId, 91L, new DateTime(2026, 9, 1), isSelected: false);
        var selected = SeedQuote(db, "PQ-103-A", productId, 92L, new DateTime(2026, 9, 1), isSelected: true);
        var approved = SeedQuote(db, "PQ-103-A", productId, 93L, new DateTime(2026, 9, 1), isSelected: true);
        SeedDecision(db, approved, PurchaseQuoteApproval.Approved);

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(3, batch.LineCount);
        Assert.Equal(3, batch.QuotedCount);
        Assert.Equal(2, batch.SelectedCount);
        Assert.Equal(1, batch.ApprovedCount);
        Assert.Equal(0, batch.ConvertedCount);
        Assert.Equal(0, batch.RejectedCount);
        Assert.Equal(0, batch.UnresolvedCount);

        Assert.Equal(PurchaseQuoteConversionFunnel.StageQuoted, batch.Lines.Single(l => l.QuoteId == quoted.Id).Stage);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageSelected, batch.Lines.Single(l => l.QuoteId == selected.Id).Stage);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageApproved, batch.Lines.Single(l => l.QuoteId == approved.Id).Stage);
    }

    // ==================== 审批变化 ====================

    [Fact]
    public async Task 审批变化_已批准已拒绝待审批_分别归类()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830002L;
        var approved = SeedQuote(db, "PQ-103-B", productId, 91L, new DateTime(2026, 9, 1), isSelected: true);
        SeedDecision(db, approved, PurchaseQuoteApproval.Approved);
        var rejected = SeedQuote(db, "PQ-103-B", productId, 92L, new DateTime(2026, 9, 1), isSelected: false);
        SeedDecision(db, rejected, PurchaseQuoteApproval.Rejected);
        var pending = SeedQuote(db, "PQ-103-B", productId, 93L, new DateTime(2026, 9, 1), isSelected: true);

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(1, batch.ApprovedCount);
        Assert.Equal(1, batch.RejectedCount);
        Assert.Equal(2, batch.SelectedCount);

        Assert.Equal(PurchaseQuoteConversionFunnel.StageApproved, batch.Lines.Single(l => l.QuoteId == approved.Id).Stage);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageRejected, batch.Lines.Single(l => l.QuoteId == rejected.Id).Stage);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageSelected, batch.Lines.Single(l => l.QuoteId == pending.Id).Stage);

        Assert.Equal(PurchaseQuoteApproval.Approved, batch.Lines.Single(l => l.QuoteId == approved.Id).ApprovalState);
        Assert.Equal(PurchaseQuoteApproval.Rejected, batch.Lines.Single(l => l.QuoteId == rejected.Id).ApprovalState);
        Assert.Equal(PurchaseQuoteApproval.Pending, batch.Lines.Single(l => l.QuoteId == pending.Id).ApprovalState);
    }

    // ==================== 批次转换 ====================

    [Fact]
    public async Task 批次转换_状态与来源标记及订单链接_已转采购订单()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830003L;
        var q = SeedQuote(db, "PQ-103-C", productId, 91L, new DateTime(2026, 9, 1), isSelected: true,
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-103-C");
        SeedDecision(db, q, PurchaseQuoteApproval.Approved);
        var order = SeedOrder(db, q, "PO-103-C");

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(1, batch.ConvertedCount);
        Assert.Equal(0, batch.UnresolvedCount);
        var line = Assert.Single(batch.Lines);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageConverted, line.Stage);
        Assert.Equal("PO-103-C", line.OrderNo);
        Assert.Equal(order.Id, line.OrderId);
        Assert.Equal(PurchaseQuoteApproval.Approved, line.ApprovalState);
    }

    // ==================== 陈旧 / 删除 / 未链接 ====================

    [Fact]
    public async Task 陈旧链接_订单备注缺失来源标记_未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830004L;
        var q = SeedQuote(db, "PQ-103-S", productId, 91L, new DateTime(2026, 9, 1), isSelected: true,
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-103-S");
        SeedOrder(db, q, "PO-103-S", remark: "人工创建的采购订单（无来源标记）");

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(0, batch.ConvertedCount);
        Assert.Equal(1, batch.UnresolvedCount);
        var line = Assert.Single(batch.Lines);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageUnresolved, line.Stage);
        Assert.Contains("陈旧链接", line.StageReason);
        Assert.Equal(string.Empty, line.OrderNo);
    }

    [Fact]
    public async Task 订单已删除_未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830005L;
        var q = SeedQuote(db, "PQ-103-D", productId, 91L, new DateTime(2026, 9, 1), isSelected: true,
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-103-D");
        var order = SeedOrder(db, q, "PO-103-D");
        order.IsDeleted = true;
        db.SaveChanges();

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(0, batch.ConvertedCount);
        Assert.Equal(1, batch.UnresolvedCount);
        Assert.Contains("不存在或已删除", batch.Lines.Single().StageReason);
    }

    [Fact]
    public async Task 未链接_RefOrderNo为空_未解决()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830006L;
        SeedQuote(db, "PQ-103-NL", productId, 91L, new DateTime(2026, 9, 1), isSelected: true,
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: string.Empty);

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(1, batch.UnresolvedCount);
        Assert.Contains("未链接", batch.Lines.Single().StageReason);
    }

    // ==================== RefOrderNo 不单独推断转换 ====================

    [Fact]
    public async Task 未转换状态_RefOrderNo为销售订单号_不推断转换()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830007L;
        SeedQuote(db, "PQ-103-X", productId, 91L, new DateTime(2026, 9, 1), isSelected: true,
            status: "已选中", refOrderNo: "SO-2026-001");

        var view = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        var batch = Assert.Single(view.Batches);
        Assert.Equal(0, batch.ConvertedCount);
        Assert.Equal(0, batch.UnresolvedCount);
        Assert.Equal(PurchaseQuoteConversionFunnel.StageSelected, batch.Lines.Single().Stage);
    }

    // ==================== 分页 / 筛选 / 排序 ====================

    [Fact]
    public async Task 分页有界_供应商与日期筛选_稳定排序_并显式截断()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830008L;
        SeedQuote(db, "PQ-103-P1", productId, 91L, new DateTime(2026, 9, 1));
        SeedQuote(db, "PQ-103-P2", productId, 92L, new DateTime(2026, 9, 2));
        SeedQuote(db, "PQ-103-P3", productId, 91L, new DateTime(2026, 9, 3));

        var page1 = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.TotalBatchCount);
        Assert.Equal(3, page1.TotalLineCount);
        Assert.True(page1.Truncated);
        Assert.Equal(2, page1.Batches.Count);
        Assert.Equal(new[] { "PQ-103-P1", "PQ-103-P2" }, page1.Batches.Select(b => b.QuoteNo).ToArray());

        var bySupplier = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { SupplierId = 91L, PageSize = 100 });
        Assert.Equal(2, bySupplier.TotalBatchCount);
        Assert.Equal(2, bySupplier.TotalLineCount);
        Assert.All(bySupplier.Batches.SelectMany(b => b.Lines), l => Assert.Equal(91L, l.SupplierId));

        var byDate = await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery
        { DateFrom = new DateTime(2026, 9, 2), DateTo = new DateTime(2026, 9, 2), PageSize = 100 });
        Assert.Equal(1, byDate.TotalBatchCount);
        Assert.Equal("PQ-103-P2", byDate.Batches.Single().QuoteNo);
    }

    // ==================== 无写入语义 ====================

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        const long productId = 830009L;
        var q = SeedQuote(db, "PQ-103-R", productId, 91L, new DateTime(2026, 9, 1), isSelected: true,
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-103-R");
        SeedDecision(db, q, PurchaseQuoteApproval.Approved);
        SeedOrder(db, q, "PO-103-R");

        var beforeQuotes = db.PurchaseQuotes.Count();
        var beforeOrders = db.PurchaseOrders.Count();
        var beforeDecisions = db.PurchaseQuoteDecisions.Count();
        await QueryAsync(db, new PurchaseQuoteConversionFunnelQuery { PageSize = 100 });

        Assert.Equal(beforeQuotes, db.PurchaseQuotes.Count());
        Assert.Equal(beforeOrders, db.PurchaseOrders.Count());
        Assert.Equal(beforeDecisions, db.PurchaseQuoteDecisions.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 参数校验 ====================

    [Fact]
    public async Task 日期区间倒置_抛参数错误()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new PurchaseQuoteConversionFunnelQuery
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
        AssertRoute(nameof(PurchaseQuoteController.ConversionFunnel), "conversion-funnel");
    }

    [Fact]
    public void 前端接线_入口函数_接口路径与脚本加载_及规则文档()
    {
        var modules = File.ReadAllText(Path.Combine(JsDirectory(), "modules.js"));
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "purchase-quote-conversion-funnel.js"));
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        Assert.Contains("showPurchaseQuoteConversionFunnel", modules);
        Assert.Contains("function showPurchaseQuoteConversionFunnel(", js);
        Assert.Contains("/api/purchase/quotes/conversion-funnel", js);
        Assert.Contains("/js/purchase-quote-conversion-funnel.js", indexHtml);

        Assert.Contains("不写库", PurchaseQuoteConversionFunnel.RuleText);
        var doc = File.ReadAllText(DocPath());
        Assert.Contains("ERP-103", doc);
        Assert.Contains("转化漏斗", doc);
    }

    // ==================== 助手 ====================

    private static async Task<PurchaseQuoteConversionFunnelView> QueryAsync(ErpDbContext db,
        PurchaseQuoteConversionFunnelQuery query)
        => await PurchaseQuoteConversionFunnel.QueryAsync(db, query);

    private static PurchaseQuote SeedQuote(ErpDbContext db, string quoteNo, long productId,
        long? supplierId, DateTime quoteDate, bool isSelected = false,
        string status = "待比较", string refOrderNo = "", decimal price = 10m)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = quoteDate,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 100m,
            SupplierId = supplierId,
            SupplierName = $"供应商{supplierId}",
            SupplierType = "档口",
            QuotePrice = price,
            TotalAmount = price * 100m,
            Currency = "CNY",
            TaxIncluded = false,
            IsSelected = isSelected,
            Status = status,
            RefOrderNo = refOrderNo,
            Remark = "FUNNEL_TEST"
        };
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }

    private static void SeedDecision(ErpDbContext db, PurchaseQuote quote, string decision)
    {
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = decision,
            SelectedSupplierId = decision == PurchaseQuoteApproval.Approved ? quote.SupplierId : null,
            SelectedSupplierName = decision == PurchaseQuoteApproval.Approved ? quote.SupplierName : string.Empty,
            DecisionBasis = "测试决定",
            DecidedByName = "测试员",
            DecidedAt = DateTime.Now,
            DecisionRef = $"APV-{quote.QuoteNo}-#{quote.Id}",
        });
        db.SaveChanges();
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, PurchaseQuote quote, string orderNo,
        string? remark = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = quote.SupplierId ?? 0,
            Currency = PurchaseQuoteConversion.ParseCurrency(quote.Currency),
            ExchangeRate = 1m,
            TaxIncluded = quote.TaxIncluded,
            TotalAmount = quote.TotalAmount,
            Status = DocumentStatus.Approved,
            Remark = remark ?? PurchaseQuoteConversion.SourceMarker(quote)
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
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
        "..", "..", "..", "..", "..", "docs", "供应商报价转单漏斗说明.md"));
}
