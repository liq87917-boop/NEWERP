using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商报价 → 采购订单转化漏斗（ERP-103，只读派生）。
/// 用途：把已持久化的采购报价（<see cref="PurchaseQuote"/>）按比价批次号（<see cref="PurchaseQuote.QuoteNo"/>）分组，
/// 逐批次展示「报价 → 选中 → 批准 → 转采购订单」转化漏斗，并区分 已拒绝 / 未解决 两类侧枝证据，
/// 帮助采购人员看清哪些批次卡在哪一步，但**不转单、不创建订单、不改报价 / 审批决定**。
/// 边界（重要）：本工作台只读、不写库——不修改报价 / 采购订单 / 价格 / 审批；转采购订单只以
/// 「状态 = 已转采购订单 + 采购订单备注来源标记（<see cref="PurchaseQuoteConversion.SourceMarker"/>）+ 订单链接」三重证据认定，
/// 绝不凭 <see cref="PurchaseQuote.RefOrderNo"/> 文本单独推断转换（该列在转换前可表示关联销售订单号）。
/// </summary>
public static class PurchaseQuoteConversionFunnel
{
    /// <summary>默认每页条数（批次）</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多批次）</summary>
    public const int MaxPageSize = 200;

    /// <summary>漏斗阶段：已报价（进入漏斗的最低阶段）</summary>
    public const string StageQuoted = "quoted";

    /// <summary>漏斗阶段：已选中（IsSelected）</summary>
    public const string StageSelected = "selected";

    /// <summary>漏斗阶段：已批准（存在 Approved 审批决定）</summary>
    public const string StageApproved = "approved";

    /// <summary>漏斗阶段：已转采购订单（状态 + 来源标记 + 订单链接三重证据均成立）</summary>
    public const string StageConverted = "converted";

    /// <summary>漏斗侧枝：已拒绝（存在 Rejected 审批决定）</summary>
    public const string StageRejected = "rejected";

    /// <summary>漏斗侧枝：未解决（状态已转采购订单，但链接缺失 / 订单删除 / 单号歧义 / 来源标记缺失）</summary>
    public const string StageUnresolved = "unresolved";

    /// <summary>口径与边界说明（界面与文档同源）</summary>
    public const string RuleText =
        "本视图是供应商报价「转采购订单」转化漏斗的只读证据（不写库、不转单、不创建订单、不改报价 / 审批）：" +
        "按比价批次号分组，仅按报价日期 + 可选供应商筛选未删除报价行，稳定按批次最早报价日期 + 批次号排序并分页；" +
        "各批次区分 已报价 / 已选中 / 已批准 / 已转采购订单 / 已拒绝 / 未解决 六类证据（漏斗口径，逐级为上一级的子集，已拒绝 / 未解决为侧枝）：" +
        "已选中按 IsSelected、已批准 / 已拒绝按 append-only 审批决定；已转采购订单只以「状态 = 已转采购订单 + 采购订单备注来源标记 + 订单链接」三重证据认定，" +
        "绝不凭 RefOrderNo 文本单独推断（该列在转换前可表示关联销售订单号），链接缺失 / 订单删除 / 单号歧义 / 来源标记缺失一律显式标注为未解决；" +
        "分页有界（默认 50 个批次、上限 200），计数只统计本次返回页的批次，命中截断时显式标注。";

    /// <summary>
    /// 通用转化漏斗查询（只读）：按报价日期 + 可选供应商筛选未删除报价行，按比价批次号分组，
    /// 稳定按批次最早报价日期 + 批次号排序并分页，逐批次分类六类证据与行明细。
    /// </summary>
    public static async Task<PurchaseQuoteConversionFunnelView> QueryAsync(IErpDbContext db,
        PurchaseQuoteConversionFunnelQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = NormalizePaging(query);
        var (dateFrom, dateTo) = NormalizeDateRange(query);

        var baseQuery = db.PurchaseQuotes.AsNoTracking()
            .Where(q => !q.IsDeleted);

        if (query.SupplierId is > 0)
            baseQuery = baseQuery.Where(q => q.SupplierId == query.SupplierId.Value);
        if (dateFrom is not null)
            baseQuery = baseQuery.Where(q => q.QuoteDate >= dateFrom.Value);
        if (dateTo is not null)
            baseQuery = baseQuery.Where(q => q.QuoteDate < dateTo.Value);

        // 先按「报价日期 + 行 Id」稳定排序取回全部命中行，再在内存里按批次号分组（与既有只读工作台一致）。
        var allLines = await baseQuery
            .OrderBy(q => q.QuoteDate).ThenBy(q => q.Id)
            .ToListAsync(ct);

        var totalLineCount = allLines.Count;

        var groups = new Dictionary<string, List<PurchaseQuote>>(StringComparer.Ordinal);
        foreach (var line in allLines)
        {
            if (!groups.TryGetValue(line.QuoteNo, out var list))
            {
                list = new List<PurchaseQuote>();
                groups[line.QuoteNo] = list;
            }
            list.Add(line);
        }

        var totalBatchCount = groups.Count;
        var pageBatchNos = groups
            .Select(kv => (No: kv.Key, Date: kv.Value.Select(l => l.QuoteDate).Min()))
            .OrderBy(x => x.Date).ThenBy(x => x.No, StringComparer.Ordinal)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.No)
            .ToList();

        var paged = pageBatchNos.Select(no => (QuoteNo: no, Lines: groups[no])).ToList();
        var batches = await BuildBatchesAsync(db, paged, ct);

        var truncated = (page - 1) * pageSize + pageBatchNos.Count < totalBatchCount;

        return new PurchaseQuoteConversionFunnelView
        {
            TotalBatchCount = totalBatchCount,
            TotalLineCount = totalLineCount,
            Page = page,
            PageSize = pageSize,
            Truncated = truncated,
            Batches = batches,
            EmptyText = totalBatchCount == 0 ? "没有符合筛选条件的供应商比价批次（或报价行已软删除）" : string.Empty,
            RuleText = RuleText
        };
    }

    // ==================== 派生逻辑 ====================

    private static async Task<List<PurchaseQuoteConversionFunnelBatch>> BuildBatchesAsync(IErpDbContext db,
        IReadOnlyList<(string QuoteNo, List<PurchaseQuote> Lines)> paged, CancellationToken ct)
    {
        if (paged.Count == 0) return new List<PurchaseQuoteConversionFunnelBatch>();

        var allLines = paged.SelectMany(p => p.Lines).ToList();
        var quoteIds = allLines.Select(l => l.Id).ToList();

        // 审批决定（append-only：一比价行至多一条有效决定）
        var decisions = await db.PurchaseQuoteDecisions.AsNoTracking()
            .Where(d => !d.IsDeleted && quoteIds.Contains(d.QuoteId))
            .ToListAsync(ct);
        var decisionByQuote = decisions
            .GroupBy(d => d.QuoteId)
            .ToDictionary(g => g.Key, g => g.First());

        // 仅「已转采购订单」状态的行才解析订单链接（绝不凭 RefOrderNo 文本推断转换）
        var convertedLines = allLines.Where(l => l.Status == PurchaseQuoteConversion.ConvertedStatus).ToList();
        var refNos = convertedLines
            .Select(l => (l.RefOrderNo ?? string.Empty).Trim())
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
        var ordersByNo = orders
            .GroupBy(o => o.OrderNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var batches = new List<PurchaseQuoteConversionFunnelBatch>();
        foreach (var (quoteNo, lines) in paged)
        {
            var batch = new PurchaseQuoteConversionFunnelBatch
            {
                QuoteNo = quoteNo,
                QuoteDate = lines.Select(l => l.QuoteDate).Min()
            };

            foreach (var line in lines)
            {
                decisionByQuote.TryGetValue(line.Id, out var decision);
                var (stage, reason, orderNo, orderId) = ClassifyLine(line, decision, ordersByNo);
                batch.Lines.Add(BuildLine(line, decision, stage, reason, orderNo, orderId));

                // 漏斗口径（累计）：每一级是上一级的子集，已拒绝 / 未解决为侧枝
                batch.QuotedCount++;
                if (line.IsSelected) batch.SelectedCount++;
                if (decision is not null && decision.Decision == PurchaseQuoteApproval.Approved) batch.ApprovedCount++;
                else if (decision is not null && decision.Decision == PurchaseQuoteApproval.Rejected) batch.RejectedCount++;
                if (stage == StageConverted) batch.ConvertedCount++;
                else if (stage == StageUnresolved) batch.UnresolvedCount++;
            }

            batch.LineCount = batch.QuotedCount;
            batches.Add(batch);
        }
        return batches;
    }

    private static (string Stage, string Reason, string OrderNo, long? OrderId) ClassifyLine(PurchaseQuote line,
        PurchaseQuoteDecision? decision, IReadOnlyDictionary<string, List<PurchaseOrder>> ordersByNo)
    {
        // 转采购订单证据：仅当状态已是「已转采购订单」时才解析链接（转换前 RefOrderNo 可能是销售订单号）
        if (line.Status == PurchaseQuoteConversion.ConvertedStatus)
        {
            var refNo = (line.RefOrderNo ?? string.Empty).Trim();
            if (refNo.Length == 0)
                return (StageUnresolved, "未记录采购单号（未链接）", string.Empty, null);

            if (!ordersByNo.TryGetValue(refNo, out var candidates) || candidates.Count == 0)
                return (StageUnresolved, $"采购订单 {refNo} 不存在或已删除（陈旧链接）", string.Empty, null);

            if (candidates.Count > 1)
                return (StageUnresolved, $"采购单号 {refNo} 对应多张采购订单（链接歧义）", string.Empty, null);

            var order = candidates[0];
            var marker = PurchaseQuoteConversion.SourceMarker(line);
            if (!order.Remark.Contains(marker, StringComparison.Ordinal))
                return (StageUnresolved, "陈旧链接：采购订单备注未包含来源比价标记", string.Empty, null);

            return (StageConverted, string.Empty, order.OrderNo, order.Id);
        }

        if (decision is not null && decision.Decision == PurchaseQuoteApproval.Rejected)
            return (StageRejected, string.Empty, string.Empty, null);
        if (decision is not null && decision.Decision == PurchaseQuoteApproval.Approved)
            return (StageApproved, string.Empty, string.Empty, null);
        if (line.IsSelected)
            return (StageSelected, string.Empty, string.Empty, null);
        return (StageQuoted, string.Empty, string.Empty, null);
    }

    private static PurchaseQuoteConversionFunnelLine BuildLine(PurchaseQuote line,
        PurchaseQuoteDecision? decision, string stage, string reason, string orderNo, long? orderId)
        => new()
        {
            QuoteId = line.Id,
            QuoteDate = line.QuoteDate,
            SupplierId = line.SupplierId,
            SupplierName = line.SupplierName,
            ProductName = line.ProductName,
            Spec = line.Spec,
            Unit = line.Unit,
            QuotePrice = line.QuotePrice,
            IsSelected = line.IsSelected,
            Status = line.Status,
            RefOrderNo = line.RefOrderNo,
            ApprovalState = decision is null ? PurchaseQuoteApproval.Pending : decision.Decision,
            DecisionRef = decision?.DecisionRef ?? string.Empty,
            Stage = stage,
            StageReason = reason,
            OrderNo = orderNo,
            OrderId = orderId
        };

    // ==================== 归一化 / 校验 ====================

    private static (int page, int pageSize) NormalizePaging(PurchaseQuoteConversionFunnelQuery query)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);
        return (page, pageSize);
    }

    private static (DateTime? from, DateTime? to) NormalizeDateRange(PurchaseQuoteConversionFunnelQuery query)
    {
        var from = query.DateFrom?.Date;
        var to = query.DateTo?.Date.AddDays(1); // 含当天：结束日期当日整天都计入（上界开区间）
        if (from is not null && to is not null && from >= to)
            throw BusinessException.InvalidParameter("报价日期区间不合法（开始日期不能晚于结束日期）");
        return (from, to);
    }
}

/// <summary>供应商报价转采购订单转化漏斗查询条件（全部为只读筛选参数）</summary>
public sealed class PurchaseQuoteConversionFunnelQuery
{
    /// <summary>报价日期开始（含当天；留空 = 不限）</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>报价日期结束（含当天；留空 = 不限）</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = PurchaseQuoteConversionFunnel.DefaultPageSize;
}

/// <summary>供应商报价转采购订单转化漏斗视图（只读派生）</summary>
public sealed class PurchaseQuoteConversionFunnelView
{
    /// <summary>符合筛选条件的比价批次总数（分页前）</summary>
    public int TotalBatchCount { get; init; }

    /// <summary>符合筛选条件的报价行总数（分页前）</summary>
    public int TotalLineCount { get; init; }

    public int Page { get; init; }
    public int PageSize { get; init; }

    /// <summary>是否命中分页截断（true = 本页之外仍有更多批次）</summary>
    public bool Truncated { get; init; }

    public List<PurchaseQuoteConversionFunnelBatch> Batches { get; init; } = new();

    /// <summary>无匹配时显式提示</summary>
    public string EmptyText { get; init; } = string.Empty;

    /// <summary>口径与边界说明</summary>
    public string RuleText { get; init; } = string.Empty;
}

/// <summary>一个比价批次的转化漏斗：六类证据计数 + 行明细（供批次钻取）</summary>
public sealed class PurchaseQuoteConversionFunnelBatch
{
    /// <summary>比价批次号</summary>
    public string QuoteNo { get; init; } = string.Empty;

    /// <summary>批次最早报价日期（作为稳定排序键）</summary>
    public DateTime QuoteDate { get; init; }

    /// <summary>批次内报价行数（= QuotedCount）</summary>
    public int LineCount { get; set; }

    /// <summary>已报价：批次内全部未删除报价行（漏斗顶部）</summary>
    public int QuotedCount { get; set; }

    /// <summary>已选中：IsSelected 的报价行</summary>
    public int SelectedCount { get; set; }

    /// <summary>已批准：存在 Approved 审批决定的报价行</summary>
    public int ApprovedCount { get; set; }

    /// <summary>已转采购订单：状态 + 来源标记 + 订单链接三重证据均成立的报价行</summary>
    public int ConvertedCount { get; set; }

    /// <summary>已拒绝：存在 Rejected 审批决定的报价行</summary>
    public int RejectedCount { get; set; }

    /// <summary>未解决：状态已转采购订单，但链接缺失 / 订单删除 / 单号歧义 / 来源标记缺失</summary>
    public int UnresolvedCount { get; set; }

    public List<PurchaseQuoteConversionFunnelLine> Lines { get; init; } = new();
}

/// <summary>批次内一条报价行及其漏斗阶段证据</summary>
public sealed class PurchaseQuoteConversionFunnelLine
{
    public long QuoteId { get; init; }
    public DateTime QuoteDate { get; init; }
    public long? SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public decimal QuotePrice { get; init; }
    public bool IsSelected { get; init; }
    public string Status { get; init; } = string.Empty;
    public string RefOrderNo { get; init; } = string.Empty;

    /// <summary>审批状态：Approved / Rejected / Pending（无决定记录）</summary>
    public string ApprovalState { get; init; } = PurchaseQuoteApproval.Pending;
    public string DecisionRef { get; init; } = string.Empty;

    /// <summary>该行到达的最深漏斗阶段：quoted / selected / approved / converted / rejected / unresolved</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>未解决原因（仅 stage = unresolved 时非空）</summary>
    public string StageReason { get; init; } = string.Empty;

    /// <summary>已转采购订单时对应的采购单号（未解决 / 未转换时为空）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>已转采购订单时对应的采购订单 Id（未解决 / 未转换时为空）</summary>
    public long? OrderId { get; init; }
}
