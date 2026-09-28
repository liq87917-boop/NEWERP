using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商报价价格历史（ERP-098，只读派生）。
/// 用途：为已持久化的采购报价（<see cref="PurchaseQuote"/>）提供一个只读的比价历史工作台：
/// 按商品展示历史报价，仅在相同比价口径（规格 + 单位 + 币种 + 是否含税）内计算价格差异，
/// 口径不一致的行按口径分组后明确单列，绝不跨口径合并或换算价格。
/// 边界（重要）：本工作台只读、不写库——不修改报价 / 采购订单 / 价格，不自动选择供应商；
/// 审批 / 选中信息（<see cref="PurchaseQuoteDecision"/>）仅作证据回显，不据此推进任何业务状态。
/// </summary>
public static class PurchaseQuotePriceHistory
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多报价行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>口径说明（界面与文档同源）</summary>
    public const string RuleText =
        "本视图是供应商报价的只读历史（不写库、不改价格、不自动选供应商）：按商品聚合所有未删除的报价行，" +
        "仅当「规格 + 单位 + 币种 + 是否含税」完全一致时视为同一比价口径，价格最低 / 最高 / 最新 / 价差只在该口径内部计算；" +
        "口径不一致的报价行按口径分组成行单列，绝不跨口径比较、绝不合并不同币种金额、绝不做汇率换算；" +
        "审批 / 选中状态只作证据回显（来自 append-only 的比价审批决定），不代表本视图自动选中供应商或改写任何单据；" +
        "分页有界，合计与计数只统计本次返回页的报价行，命中截断时显式标注。";

    /// <summary>
    /// 通用比价历史查询（只读）：按商品 + 可选供应商 / 报价日期区间筛选未删除报价行，
    /// 稳定按报价日期 + 行 Id 排序并分页，再按比价口径分组计算口径内价格差异。
    /// </summary>
    public static async Task<PurchaseQuotePriceHistoryView> QueryAsync(IErpDbContext db,
        PurchaseQuotePriceHistoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var productId = NormalizeProductId(query);
        var (page, pageSize) = NormalizePaging(query);
        var (dateFrom, dateTo) = NormalizeDateRange(query);

        var reference = await LoadReferenceAsync(db, query.ReferenceQuoteId, ct);

        var baseQuery = db.PurchaseQuotes.AsNoTracking()
            .Where(q => !q.IsDeleted && q.ProductId == productId);

        if (query.SupplierId is > 0)
            baseQuery = baseQuery.Where(q => q.SupplierId == query.SupplierId.Value);
        if (dateFrom is not null)
            baseQuery = baseQuery.Where(q => q.QuoteDate >= dateFrom.Value);
        if (dateTo is not null)
            baseQuery = baseQuery.Where(q => q.QuoteDate < dateTo.Value);

        var totalCount = await baseQuery.CountAsync(ct);

        var rows = await baseQuery
            .OrderBy(q => q.QuoteDate).ThenBy(q => q.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var decisions = await LoadDecisionsAsync(db, rows, ct);
        var groups = BuildGroups(rows, decisions, reference);

        var truncated = (page - 1) * pageSize + rows.Count < totalCount;
        var productName = rows.Select(r => r.ProductName).FirstOrDefault(n => n.Length > 0)
            ?? reference?.ProductName ?? string.Empty;

        return new PurchaseQuotePriceHistoryView
        {
            ProductId = productId,
            ProductName = productName,
            ReferenceQuoteId = reference?.Id,
            ReferenceBasisText = reference is null
                ? null
                : BasisText(reference.Spec, reference.Unit, reference.Currency, reference.TaxIncluded),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Truncated = truncated,
            GroupCount = groups.Count,
            Groups = groups,
            EmptyText = totalCount == 0 ? "没有符合筛选条件的报价历史（或报价已被软删除）" : string.Empty,
        };
    }

    /// <summary>
    /// 从某个比价行打开比价历史（只读）：以该行商品作为筛选商品、以该行口径作为参照口径，
    /// 与参照口径一致的报价行为「可同比价」组，口径不一致的报价行明确分单列。
    /// </summary>
    public static async Task<PurchaseQuotePriceHistoryView> ForQuoteAsync(IErpDbContext db, long quoteId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var quote = await db.PurchaseQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == quoteId && !q.IsDeleted, ct)
            ?? throw BusinessException.NotFound("比价记录不存在");

        return await QueryAsync(db, new PurchaseQuotePriceHistoryQuery
        {
            ProductId = quote.ProductId,
            ReferenceQuoteId = quote.Id,
            Page = 1,
            PageSize = MaxPageSize,
        }, ct);
    }
    // ==================== 派生逻辑 ====================

    private static async Task<PurchaseQuote?> LoadReferenceAsync(IErpDbContext db, long? referenceQuoteId,
        CancellationToken ct)
    {
        if (referenceQuoteId is not > 0) return null;
        return await db.PurchaseQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == referenceQuoteId.Value && !q.IsDeleted, ct);
    }

    private static async Task<Dictionary<long, PurchaseQuoteDecision>> LoadDecisionsAsync(IErpDbContext db,
        List<PurchaseQuote> rows, CancellationToken ct)
    {
        var quoteIds = rows.Select(r => r.Id).ToList();
        if (quoteIds.Count == 0) return new Dictionary<long, PurchaseQuoteDecision>();

        var decisions = await db.PurchaseQuoteDecisions.AsNoTracking()
            .Where(d => quoteIds.Contains(d.QuoteId) && !d.IsDeleted)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);

        var map = new Dictionary<long, PurchaseQuoteDecision>();
        foreach (var d in decisions) map[d.QuoteId] = d;
        return map;
    }

    private static List<PurchaseQuotePriceHistoryGroup> BuildGroups(List<PurchaseQuote> rows,
        Dictionary<long, PurchaseQuoteDecision> decisions, PurchaseQuote? reference)
    {
        var referenceKey = reference is null
            ? null
            : ComparisonKey(reference.Spec, reference.Unit, reference.Currency, reference.TaxIncluded);

        var ordered = new List<PurchaseQuotePriceHistoryGroup>();
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var q in rows)
        {
            var key = ComparisonKey(q.Spec, q.Unit, q.Currency, q.TaxIncluded);
            if (!indexByKey.TryGetValue(key, out var idx))
            {
                idx = ordered.Count;
                indexByKey[key] = idx;
                ordered.Add(new PurchaseQuotePriceHistoryGroup
                {
                    Spec = q.Spec,
                    Unit = q.Unit,
                    Currency = q.Currency,
                    TaxIncluded = q.TaxIncluded,
                    GroupKey = key,
                    BasisText = BasisText(q.Spec, q.Unit, q.Currency, q.TaxIncluded),
                    Comparable = reference is null || referenceKey == key,
                    Rows = new List<PurchaseQuotePriceHistoryRow>(),
                });
            }

            decisions.TryGetValue(q.Id, out var decision);
            ordered[idx].Rows.Add(ToRow(q, decision));
        }

        foreach (var group in ordered)
        {
            group.RowCount = group.Rows.Count;
            var prices = group.Rows.Select(r => r.QuotePrice).ToList();
            group.MinPrice = prices.Min();
            group.MaxPrice = prices.Max();
            group.LatestPrice = group.Rows[^1].QuotePrice; // 行已按报价日期 + Id 稳定排序，末行为最新报价
            group.PriceSpread = group.MaxPrice - group.MinPrice;
        }

        return ordered;
    }

    private static PurchaseQuotePriceHistoryRow ToRow(PurchaseQuote q, PurchaseQuoteDecision? decision)
        => new()
        {
            QuoteId = q.Id,
            QuoteNo = q.QuoteNo,
            QuoteDate = q.QuoteDate,
            SupplierId = q.SupplierId,
            SupplierName = q.SupplierName,
            SupplierType = q.SupplierType,
            Spec = q.Spec,
            Unit = q.Unit,
            Currency = q.Currency,
            TaxIncluded = q.TaxIncluded,
            Quantity = q.Quantity,
            QuotePrice = q.QuotePrice,
            TotalAmount = q.TotalAmount,
            DeliveryDays = q.DeliveryDays,
            MinOrderQty = q.MinOrderQty,
            PaymentTerms = q.PaymentTerms,
            IsSelected = q.IsSelected,
            Status = q.Status,
            RefOrderNo = q.RefOrderNo,
            Remark = q.Remark,
            ApprovalState = decision is null ? PurchaseQuoteApproval.Pending : decision.Decision,
            DecisionBasis = decision?.DecisionBasis ?? string.Empty,
            DecidedByName = decision?.DecidedByName ?? string.Empty,
            DecidedAt = decision?.DecidedAt,
            DecisionRef = decision?.DecisionRef ?? string.Empty,
        };

    // ==================== 归一化 / 校验 ====================

    private static long NormalizeProductId(PurchaseQuotePriceHistoryQuery query)
    {
        if (query.ProductId is null or <= 0)
            throw BusinessException.InvalidParameter("请提供商品 Id");
        return query.ProductId.Value;
    }

    private static (int page, int pageSize) NormalizePaging(PurchaseQuotePriceHistoryQuery query)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);
        return (page, pageSize);
    }

    private static (DateTime? from, DateTime? to) NormalizeDateRange(PurchaseQuotePriceHistoryQuery query)
    {
        var from = query.DateFrom?.Date;
        var to = query.DateTo?.Date.AddDays(1); // 含当天：结束日期当日整天都计入（上界开区间）
        if (from is not null && to is not null && from >= to)
            throw BusinessException.InvalidParameter("报价日期区间不合法（开始日期不能晚于结束日期）");
        return (from, to);
    }

    /// <summary>比价口径键：规格 + 单位 + 币种 + 是否含税（币种不区分大小写，未填写按空串归组）</summary>
    private static string ComparisonKey(string? spec, string? unit, string? currency, bool taxIncluded)
        => $"{Normalize(spec)}\u0001{Normalize(unit)}\u0001{Normalize(currency).ToUpperInvariant()}\u0001{(taxIncluded ? "1" : "0")}";

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static string BasisText(string spec, string unit, string currency, bool taxIncluded)
        => $"规格：{Display(spec)} · 单位：{Display(unit)} · 币种：{Display(currency)} · {(taxIncluded ? "含税" : "不含税")}";

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "（未填）" : value!.Trim();
}

/// <summary>供应商报价价格历史查询条件（全部为只读筛选参数）</summary>
public sealed class PurchaseQuotePriceHistoryQuery
{
    /// <summary>商品 Id（必填）</summary>
    public long? ProductId { get; set; }

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>报价日期开始（含当天；留空 = 不限）</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>报价日期结束（含当天；留空 = 不限）</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>参照比价行 Id（可选；给定时以其口径标记「可同比价」组）</summary>
    public long? ReferenceQuoteId { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = PurchaseQuotePriceHistory.DefaultPageSize;
}

/// <summary>供应商报价价格历史视图（只读派生）</summary>
public sealed class PurchaseQuotePriceHistoryView
{
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public long? ReferenceQuoteId { get; init; }
    public string? ReferenceBasisText { get; init; }

    /// <summary>符合筛选条件的未删除报价行总数（分页前）</summary>
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }

    /// <summary>是否命中分页截断（true = 本页之外仍有更多报价行）</summary>
    public bool Truncated { get; init; }
    public int GroupCount { get; init; }
    public List<PurchaseQuotePriceHistoryGroup> Groups { get; init; } = new();
    public string EmptyText { get; init; } = string.Empty;
}

/// <summary>一个比价口径组：组内规格 + 单位 + 币种 + 含税一致，价格差异只在该组内计算</summary>
public sealed class PurchaseQuotePriceHistoryGroup
{
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public bool TaxIncluded { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public string BasisText { get; init; } = string.Empty;

    /// <summary>是否与参照比价行口径一致（无参照时全部为 true，各组各自构成独立比价口径）</summary>
    public bool Comparable { get; init; }

    public int RowCount { get; set; }
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public decimal LatestPrice { get; set; }
    public decimal PriceSpread { get; set; }
    public List<PurchaseQuotePriceHistoryRow> Rows { get; init; } = new();
}

/// <summary>一条报价历史行：保留供应商 / 币种 / 单位 / 规格 / 含税 / 审批选中上下文</summary>
public sealed class PurchaseQuotePriceHistoryRow
{
    public long QuoteId { get; init; }
    public string QuoteNo { get; init; } = string.Empty;
    public DateTime QuoteDate { get; init; }
    public long? SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string SupplierType { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public bool TaxIncluded { get; init; }
    public decimal Quantity { get; init; }
    public decimal QuotePrice { get; init; }
    public decimal TotalAmount { get; init; }
    public int DeliveryDays { get; init; }
    public int MinOrderQty { get; init; }
    public string PaymentTerms { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public string Status { get; init; } = string.Empty;
    public string RefOrderNo { get; init; } = string.Empty;
    public string Remark { get; init; } = string.Empty;

    /// <summary>审批状态：Approved / Rejected / Pending（无决定记录）</summary>
    public string ApprovalState { get; init; } = PurchaseQuoteApproval.Pending;
    public string DecisionBasis { get; init; } = string.Empty;
    public string DecidedByName { get; init; } = string.Empty;
    public DateTime? DecidedAt { get; init; }
    public string DecisionRef { get; init; } = string.Empty;
}
