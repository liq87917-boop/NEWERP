using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商比价价格审批与供应商选择历史（ERP-095）。
/// append-only：仅提供「查询批次审批状态」与「记录决定」，不提供修改 / 删除接口。
/// </summary>
[ApiController]
[Route("api/purchase/quote-decisions")]
[Authorize]
public class PurchaseQuoteDecisionController : ControllerBase
{
    private readonly IErpDbContext _db;

    public PurchaseQuoteDecisionController(IErpDbContext db) => _db = db;

    /// <summary>批次审批状态（只读）：逐行 pending / approved / rejected + 全量决定历史</summary>
    [HttpGet("batch")]
    public async Task<IActionResult> BatchStatus([FromQuery] string quoteNo)
    {
        var batch = await PurchaseQuoteApproval.GetBatchStatusAsync(_db, quoteNo);
        return Ok(ApiResponse<PurchaseQuoteDecisionBatch>.Success(batch,
            $"比价批次 {batch.QuoteNo}：待审批 {batch.PendingCount}、已批准 {batch.ApprovedCount}、已拒绝 {batch.RejectedCount}"));
    }

    /// <summary>记录审批决定（批准 / 拒绝）：append-only，拒绝重复 / 陈旧 / 跨批次决定</summary>
    [HttpPost("decide")]
    public async Task<IActionResult> Decide([FromBody] PurchaseQuoteDecisionRequest request)
    {
        var decision = await PurchaseQuoteApproval.DecideAsync(_db, request);
        var verb = decision.Decision == PurchaseQuoteApproval.Approved ? "批准选中" : "拒绝";
        return Ok(ApiResponse<PurchaseQuoteDecision>.Success(decision,
            $"已{verb}比价行 #{decision.QuoteId}（审批参考 {decision.DecisionRef}）"));
    }
}
