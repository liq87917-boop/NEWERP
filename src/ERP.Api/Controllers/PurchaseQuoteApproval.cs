using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商比价价格审批与供应商选择历史（ERP-095）共用逻辑。
/// 业务链：采购需求 → 供应商比价（同一 QuoteNo 多家报价）→ 审批选中供应商 → 采购订单。
/// 口径：append-only（只追加、不修改、不删除，一比价行至多一条有效决定）；
/// 重复 / 陈旧 / 跨批次决定一律拒绝；订单转换只接受「已批准」比价行并保留审批参考号。
/// </summary>
public static class PurchaseQuoteApproval
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Pending = "Pending";

    /// <summary>审批参考号（一比价行一条决定，故稳定唯一）</summary>
    public static string DecisionRef(PurchaseQuote quote) => $"APV-{quote.QuoteNo}-#{quote.Id}";

    /// <summary>比价行当前有效审批决定（无决定时为 null）</summary>
    public static async Task<PurchaseQuoteDecision?> FindCurrentDecisionAsync(IErpDbContext db, PurchaseQuote quote,
        CancellationToken ct = default)
        => await db.PurchaseQuoteDecisions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.QuoteId == quote.Id && !d.IsDeleted, ct);

    /// <summary>记录审批决定（批准 / 拒绝）。append-only：不修改、不删除既有决定。</summary>
    public static async Task<PurchaseQuoteDecision> DecideAsync(IErpDbContext db, PurchaseQuoteDecisionRequest request,
        CancellationToken ct = default)
    {
        if (request is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        if (request.QuoteId <= 0) throw BusinessException.InvalidParameter("请提供比价行 Id");

        var decision = (request.Decision ?? string.Empty).Trim();
        if (decision != Approved && decision != Rejected)
            throw BusinessException.InvalidParameter("决定类型只能是 Approved（批准）或 Rejected（拒绝）");

        var quote = await db.PurchaseQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == request.QuoteId && !q.IsDeleted, ct)
            ?? throw BusinessException.NotFound("比价记录不存在");

        // 跨批次：请求批次号与比价行所属批次不一致
        var batchNo = (request.QuoteNo ?? string.Empty).Trim();
        if (batchNo.Length > 0 && !string.Equals(batchNo, quote.QuoteNo, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.RuleConflict($"比价行 #{quote.Id} 属于批次 {quote.QuoteNo}，不能在批次 {batchNo} 下审批");

        // 陈旧：已转采购订单 / 已放弃
        if (quote.Status == PurchaseQuoteConversion.ConvertedStatus)
            throw BusinessException.RuleConflict("该比价行已转采购订单，不能审批");
        if (quote.Status == PurchaseQuoteConversion.DiscardedStatus)
            throw BusinessException.RuleConflict("已放弃的比价行不能审批");

        // 重复：该比价行已有有效决定（append-only，不覆盖）
        if (await db.PurchaseQuoteDecisions.AsNoTracking().AnyAsync(d => d.QuoteId == quote.Id && !d.IsDeleted, ct))
            throw BusinessException.RuleConflict($"比价行 #{quote.Id} 已存在审批决定，不能重复决定");

        var row = new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = decision,
            SelectedSupplierId = decision == Approved ? quote.SupplierId : null,
            SelectedSupplierName = decision == Approved ? Clamp(quote.SupplierName, 200) : string.Empty,
            DecisionBasis = Clamp(request.DecisionBasis, 500),
            DecidedBy = request.DecidedBy,
            DecidedByName = Clamp(request.DecidedByName, 100),
            DecidedAt = DateTime.Now,
            DecisionRef = DecisionRef(quote),
            CreatedAt = DateTime.Now
        };
        db.PurchaseQuoteDecisions.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>批次审批状态（只读）：逐行 pending / approved / rejected + 全量决定历史。</summary>
    public static async Task<PurchaseQuoteDecisionBatch> GetBatchStatusAsync(IErpDbContext db, string? quoteNo,
        CancellationToken ct = default)
    {
        var batchNo = (quoteNo ?? string.Empty).Trim();
        if (batchNo.Length == 0) throw BusinessException.InvalidParameter("请提供比价批次号");

        var lines = await db.PurchaseQuotes.AsNoTracking()
            .Where(q => q.QuoteNo == batchNo && !q.IsDeleted)
            .OrderBy(q => q.Id)
            .ToListAsync(ct);
        if (lines.Count == 0) throw BusinessException.NotFound($"比价批次 {batchNo} 不存在或已删除");

        var decisions = await db.PurchaseQuoteDecisions.AsNoTracking()
            .Where(d => d.QuoteNo == batchNo && !d.IsDeleted)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);

        var byLine = new Dictionary<long, PurchaseQuoteDecision>();
        foreach (var d in decisions) byLine[d.QuoteId] = d;

        var result = new PurchaseQuoteDecisionBatch
        {
            QuoteNo = batchNo,
            LineCount = lines.Count,
            History = decisions
        };

        foreach (var line in lines)
        {
            byLine.TryGetValue(line.Id, out var decision);
            var state = decision is null ? Pending : decision.Decision;
            if (state == Approved) result.ApprovedCount++;
            else if (state == Rejected) result.RejectedCount++;
            else result.PendingCount++;

            result.Lines.Add(new PurchaseQuoteDecisionLine
            {
                QuoteId = line.Id,
                QuoteNo = line.QuoteNo,
                ProductName = line.ProductName,
                SupplierId = line.SupplierId,
                SupplierName = line.SupplierName,
                QuotePrice = line.QuotePrice,
                State = state,
                Decision = decision
            });
        }
        return result;
    }

    private static string Clamp(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
