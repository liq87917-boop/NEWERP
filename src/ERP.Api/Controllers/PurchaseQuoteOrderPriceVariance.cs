using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商比价 → 采购订单价格差异（ERP-105，只读派生）。
/// 用途：为「已转采购订单」的比价行提供一个只读的价格差异核对工作台：通过比价行持久化链接
/// （<see cref="PurchaseQuote.RefOrderNo"/>）+ 采购订单备注来源标记（<see cref="PurchaseQuoteConversion.SourceMarker"/>）
/// 定位生成的采购订单，仅在「商品 + 规格 + 单位 + 币种 + 是否含税」完全一致时计算单价差与金额差；
/// 歧义 / 口径不一致 / 陈旧链接 / 未链接一律显式标注为未解决，绝不重定价、不改审批。
/// 边界（重要）：本工作台只读、不写库——不修改报价 / 采购订单 / 价格 / 审批，也不做汇率换算或合并不同币种金额。
/// </summary>
public static class PurchaseQuoteOrderPriceVariance
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多比价行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// 通用价格差异查询（只读）：按报价日期 + 可选供应商筛选「已转采购订单」的未删除比价行，
    /// 稳定按报价日期 + 行 Id 排序并分页，逐行解析采购订单链接并计算口径内价格差异。
    /// </summary>
    public static async Task<PurchaseQuoteOrderPriceVarianceView> QueryAsync(IErpDbContext db,
        PurchaseQuoteOrderPriceVarianceQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = NormalizePaging(query);
        var (dateFrom, dateTo) = NormalizeDateRange(query);

        var baseQuery = db.PurchaseQuotes.AsNoTracking()
            .Where(q => !q.IsDeleted && q.Status == PurchaseQuoteConversion.ConvertedStatus);

        if (query.SupplierId is > 0)
            baseQuery = baseQuery.Where(q => q.SupplierId == query.SupplierId.Value);
        if (dateFrom is not null)
            baseQuery = baseQuery.Where(q => q.QuoteDate >= dateFrom.Value);
        if (dateTo is not null)
            baseQuery = baseQuery.Where(q => q.QuoteDate < dateTo.Value);

        var totalCount = await baseQuery.CountAsync(ct);

        var quotes = await baseQuery
            .OrderBy(q => q.QuoteDate).ThenBy(q => q.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var rows = await BuildRowsAsync(db, quotes, ct);

        return new PurchaseQuoteOrderPriceVarianceView
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Truncated = (page - 1) * pageSize + quotes.Count < totalCount,
            ResolvedCount = rows.Count(r => r.Resolved),
            UnresolvedCount = rows.Count(r => !r.Resolved),
            RuleText = PurchaseQuoteOrderPriceVarianceRules.RuleText,
            EmptyText = totalCount == 0 ? "没有符合筛选条件的已转采购订单比价行（或比价行已软删除）" : string.Empty,
            Rows = rows
        };
    }

    /// <summary>
    /// 从某个比价行打开价格差异（只读）：只返回该行（已转采购订单）与其采购订单的对照；未转采购订单时显式标注未解决。
    /// </summary>
    public static async Task<PurchaseQuoteOrderPriceVarianceView> ForQuoteAsync(IErpDbContext db, long quoteId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var quote = await db.PurchaseQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == quoteId && !q.IsDeleted, ct)
            ?? throw BusinessException.NotFound("比价记录不存在");

        List<PurchaseQuoteOrderPriceVarianceRow> rows;
        if (quote.Status == PurchaseQuoteConversion.ConvertedStatus)
        {
            rows = await BuildRowsAsync(db, new List<PurchaseQuote> { quote }, ct);
        }
        else
        {
            rows = new List<PurchaseQuoteOrderPriceVarianceRow>
            {
                SourceRow(quote, "该比价行尚未转采购订单，无采购订单可比对")
            };
        }

        return new PurchaseQuoteOrderPriceVarianceView
        {
            TotalCount = 1,
            Page = 1,
            PageSize = 1,
            Truncated = false,
            ResolvedCount = rows.Count(r => r.Resolved),
            UnresolvedCount = rows.Count(r => !r.Resolved),
            RuleText = PurchaseQuoteOrderPriceVarianceRules.RuleText,
            EmptyText = string.Empty,
            Rows = rows
        };
    }

    // ==================== 派生逻辑 ====================

    private static async Task<List<PurchaseQuoteOrderPriceVarianceRow>> BuildRowsAsync(IErpDbContext db,
        List<PurchaseQuote> quotes, CancellationToken ct)
    {
        if (quotes.Count == 0) return new List<PurchaseQuoteOrderPriceVarianceRow>();

        // 一次性预取：① 比价行 RefOrderNo 指向的未删除采购订单；② 这些订单的未删除明细。
        var refNos = quotes.Select(q => (q.RefOrderNo ?? string.Empty).Trim())
            .Where(no => no.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orders = new List<PurchaseOrder>();
        if (refNos.Count > 0)
        {
            orders = await db.PurchaseOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && refNos.Contains(o.OrderNo))
                .ToListAsync(ct);
        }

        var detailsByOrder = new Dictionary<long, List<PurchaseOrderDetail>>();
        var orderIds = orders.Select(o => o.Id).ToList();
        if (orderIds.Count > 0)
        {
            var details = await db.PurchaseOrderDetails.AsNoTracking()
                .Where(d => orderIds.Contains(d.PurchaseOrderId) && !d.IsDeleted)
                .ToListAsync(ct);
            foreach (var group in details.GroupBy(d => d.PurchaseOrderId))
                detailsByOrder[group.Key] = group.ToList();
        }

        var ordersByNo = orders
            .GroupBy(o => o.OrderNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return quotes.Select(q => BuildRow(q, ordersByNo, detailsByOrder)).ToList();
    }

    private static PurchaseQuoteOrderPriceVarianceRow BuildRow(PurchaseQuote quote,
        IReadOnlyDictionary<string, List<PurchaseOrder>> ordersByNo,
        IReadOnlyDictionary<long, List<PurchaseOrderDetail>> detailsByOrder)
    {
        var refNo = (quote.RefOrderNo ?? string.Empty).Trim();
        if (refNo.Length == 0)
            return SourceRow(quote, "未记录采购单号（未链接）");

        if (!ordersByNo.TryGetValue(refNo, out var candidates) || candidates.Count == 0)
            return SourceRow(quote, $"采购订单 {refNo} 不存在或已删除（陈旧链接）");

        if (candidates.Count > 1)
            return SourceRow(quote, $"采购单号 {refNo} 对应多张采购订单（链接歧义）");

        var order = candidates[0];
        detailsByOrder.TryGetValue(order.Id, out var orderDetails);
        var marker = PurchaseQuoteConversion.SourceMarker(quote);
        var resolution = PurchaseQuoteOrderPriceVarianceRules.Resolve(quote, order,
            orderDetails ?? new List<PurchaseOrderDetail>(), marker);

        var row = SourceRow(quote, resolution.Reason);
        row.Resolved = resolution.Resolved;
        row.UnitPriceDelta = resolution.UnitPriceDelta;
        row.AmountDelta = resolution.AmountDelta;

        row.OrderNo = order.OrderNo;
        row.OrderDate = order.OrderDate;
        row.OrderCurrency = order.Currency.ToString();
        row.OrderTaxIncluded = order.TaxIncluded;
        if (resolution.Detail is not null)
        {
            row.OrderDetailId = resolution.Detail.Id;
            row.OrderSpec = resolution.Detail.Spec;
            row.OrderUnit = resolution.Detail.Unit;
            row.OrderUnitPrice = resolution.Detail.UnitPrice;
            row.OrderAmount = resolution.Detail.Amount;
        }

        return row;
    }

    /// <summary>来源（比价行）证据行；未解决时订单证据留空、数值差异为 null</summary>
    private static PurchaseQuoteOrderPriceVarianceRow SourceRow(PurchaseQuote quote, string reason)
        => new()
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            QuoteDate = quote.QuoteDate,
            SupplierId = quote.SupplierId,
            SupplierName = quote.SupplierName,
            ProductName = quote.ProductName,
            Spec = quote.Spec,
            Unit = quote.Unit,
            Currency = quote.Currency,
            TaxIncluded = quote.TaxIncluded,
            Quantity = quote.Quantity,
            QuotePrice = quote.QuotePrice,
            QuoteAmount = quote.TotalAmount,
            RefOrderNo = quote.RefOrderNo,
            QuoteStatus = quote.Status,
            Resolved = false,
            Reason = reason
        };

    // ==================== 归一化 / 校验 ====================

    private static (int page, int pageSize) NormalizePaging(PurchaseQuoteOrderPriceVarianceQuery query)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);
        return (page, pageSize);
    }

    private static (DateTime? from, DateTime? to) NormalizeDateRange(PurchaseQuoteOrderPriceVarianceQuery query)
    {
        var from = query.DateFrom?.Date;
        var to = query.DateTo?.Date.AddDays(1); // 含当天：结束日期当日整天都计入（上界开区间）
        if (from is not null && to is not null && from >= to)
            throw BusinessException.InvalidParameter("报价日期区间不合法（开始日期不能晚于结束日期）");
        return (from, to);
    }
}
