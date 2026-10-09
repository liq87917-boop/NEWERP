using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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

    /// <summary>
    /// ERP-417 转换前置守卫：若该比价行已存在有效「已批准」决定，则当前持久化供应商必须与决定选中的供应商一致，
    /// 不一致即拒绝转采购订单——杜绝「批准一家供应商却采购另一家」。无决定 / 非批准决定时不在此判定，
    /// 交由既有 <c>PurchaseQuoteConversion.BuildDraftAsync</c> 守卫处理（保持既有错误口径）。
    /// </summary>
    public static async Task EnsureApprovedSupplierCoherentAsync(IErpDbContext db, PurchaseQuote quote,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(quote);

        var decision = await FindCurrentDecisionAsync(db, quote, ct);
        if (decision is null || !string.Equals(decision.Decision, Approved, StringComparison.Ordinal))
            return;

        if (decision.SelectedSupplierId != quote.SupplierId)
            throw BusinessException.RuleConflict(SupplierCoherenceText(quote, decision));
    }

    /// <summary>
    /// ERP-418 批次（及单行）转换前的「批准供应商一致性」批量复核：一次查询决定后逐行比对，
    /// 与 <see cref="EnsureApprovedSupplierCoherentAsync"/> 逐字同口径（无决定 / 非批准决定不在此判定），
    /// 任一行不一致即整批受控拒绝（绝不部分写入）。
    /// </summary>
    public static async Task EnsureApprovedSuppliersCoherentAsync(IErpDbContext db,
        IReadOnlyList<PurchaseQuote> quotes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(quotes);
        if (quotes.Count == 0) return;

        var ids = quotes.Select(q => q.Id).Distinct().ToList();
        var decisions = await db.PurchaseQuoteDecisions.AsNoTracking()
            .Where(d => ids.Contains(d.QuoteId) && !d.IsDeleted)
            .ToListAsync(ct);

        foreach (var quote in quotes)
        {
            var decision = decisions.FirstOrDefault(d => d.QuoteId == quote.Id);
            if (decision is null || !string.Equals(decision.Decision, Approved, StringComparison.Ordinal))
                continue;
            if (decision.SelectedSupplierId != quote.SupplierId)
                throw BusinessException.RuleConflict(SupplierCoherenceText(quote, decision));
        }
    }

    private static string SupplierCoherenceText(PurchaseQuote quote, PurchaseQuoteDecision decision)
        => $"比价行 #{quote.Id} 的当前供应商（{Text(quote.SupplierId)}）与已批准决定的选中供应商" +
           $"（{Text(decision.SelectedSupplierId)}）不一致：拒绝转采购订单（绝不采购批准之外的供应商）";

    private static string Text(long? supplierId)
        => supplierId is null or <= 0 ? "（未维护）" : supplierId.Value.ToString();

    /// <summary>
    /// 记录审批决定（批准 / 拒绝）。append-only：不修改、不删除既有决定。
    /// <para><b>ERP-417 原子口径</b>：先对比价行取排它行锁（与修改 / 删除 / 批量删除 / 转采购订单共用同一把锁），
    /// 锁内重新加载权威行并复核实时身份 / 既有菜单 / 权威客户范围、批次一致 / 陈旧 / 已转换 / 已放弃 / 重复决定，
    /// 之后才追加<b>恰好一条</b>决定并提交；任一步失败整体回滚（锁刷新与新增决定都不落库）。</para>
    /// <para><b>决定人来源</b>：实时认证请求下 <paramref name="actor"/> 非空，<c>DecidedBy</c> / <c>DecidedByName</c>
    /// 一律取自登录账号，忽略请求体中的伪造字段；进程内直接调用（<paramref name="actor"/> 为 <c>null</c>、
    /// 无 HTTP 管线）保留既有内部口径。</para>
    /// </summary>
    public static async Task<PurchaseQuoteDecision> DecideAsync(IErpDbContext db, PurchaseQuoteDecisionRequest request,
        PurchaseQuoteAuthorizationRules.LiveActor? actor = null, CancellationToken ct = default)
    {
        if (request is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        if (request.QuoteId <= 0) throw BusinessException.InvalidParameter("请提供比价行 Id");

        var decision = (request.Decision ?? string.Empty).Trim();
        if (decision != Approved && decision != Rejected)
            throw BusinessException.InvalidParameter("决定类型只能是 Approved（批准）或 Rejected（拒绝）");

        await using var transaction = await PurchaseQuoteMutationRules.BeginMutationTransactionAsync(db, ct);
        try
        {
            // ERP-417：确定性比价行锁（与修改 / 删除 / 批量删除 / 转单共用同一把锁）。
            if (!await PurchaseQuoteMutationRules.LockQuoteRowAsync(db, request.QuoteId, ct))
                throw BusinessException.NotFound("比价记录不存在");

            var quote = await db.PurchaseQuotes
                .FirstOrDefaultAsync(q => q.Id == request.QuoteId && !q.IsDeleted, ct)
                ?? throw BusinessException.NotFound("比价记录不存在");

            // ERP-417：锁内重新读取实时身份 / 既有菜单 / 权威客户范围，并取权威决定人。
            if (actor is not null)
            {
                var liveActor = await PurchaseQuoteAuthorizationRules.EnsureLiveActorAsync(db, actor.Value.UserId, ct);
                var scope = await PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(db, liveActor.UserId, ct);
                await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(db, scope, quote.Id, ct);
                actor = liveActor;
            }

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
                DecidedBy = actor?.UserId ?? request.DecidedBy,
                DecidedByName = Clamp(actor?.DisplayName ?? request.DecidedByName, 100),
                DecidedAt = DateTime.Now,
                DecisionRef = DecisionRef(quote),
                CreatedAt = DateTime.Now,
                // ERP-417 / ERP-419：操作人归属——实时认证请求取登录账号；无 HTTP 管线的进程内直调回退请求决定人。
                // 归属仅用于审计溯源；删除冻结判据是所在比价行存在任何有效审批决定
                // （PurchaseQuoteMutationRules.IsDecisionFreezingDeletion），与 CreatedBy 是否为空的「未知归属」无关。
                CreatedBy = actor?.UserId ?? request.DecidedBy
            };
            db.PurchaseQuoteDecisions.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 数据库层「同一比价行唯一有效决定」过滤唯一索引兜底：并发赢家已落库，本次为受控输家。
                throw BusinessException.RuleConflict($"比价行 #{request.QuoteId} 已存在审批决定，不能重复决定");
            }

            if (transaction is not null) await transaction.CommitAsync(ct);
            return row;
        }
        catch
        {
            await RollbackAsync(db, transaction);
            throw;
        }
    }

    /// <summary>失败 / 拒绝后整体回滚并丢弃半成品变更（绝不残留部分写入）。</summary>
    private static async Task RollbackAsync(IErpDbContext db, IDbContextTransaction? transaction)
    {
        if (transaction is not null) await transaction.RollbackAsync();
        PurchaseQuoteMutationRules.DiscardTrackedChanges(db);
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
