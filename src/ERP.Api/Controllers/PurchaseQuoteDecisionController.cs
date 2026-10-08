using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商比价价格审批与供应商选择历史（ERP-095）。
/// append-only：仅提供「查询批次审批状态」与「记录决定」，不提供修改 / 删除接口。
/// <para>ERP-416：两条路由都先经 <see cref="PurchaseQuoteAuthorizationRules"/> 复核实时身份 + 既有「供应商比价」
/// （<c>purchase-quote</c>）菜单 + ERP-097 客户数据范围：批次审批状态要求该批次**全部未删除行**都在范围内
/// （混入任何范围外 / 空归属行即整批非披露拒绝，绝不返回隐藏行计数 / 决定历史），
/// 记录决定先复核被审批比价行的**持久化归属**（不采信请求中的 <c>DecidedBy</c> / <c>DecidedByName</c> 快照），
/// 范围外 / 已删除 / 不存在返回同一非披露错误；授权先于任何读取 / 追加。</para>
/// </summary>
[ApiController]
[Route("api/purchase/quote-decisions")]
[Authorize]
public class PurchaseQuoteDecisionController : ControllerBase
{
    private readonly IErpDbContext _db;

    public PurchaseQuoteDecisionController(IErpDbContext db) => _db = db;

    /// <summary>当前登录用户 Id（缺失 / 非数字 / 无 HTTP 管线时返回 null，由实时授权 fail closed 拒绝，绝不猜测身份）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// ERP-416：是否必须执行实时授权。真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
    /// 进程内直接调用（历史单元测试 / 内部派生读取，无 HTTP 请求管线）仅在携带当前登录身份时执行。
    /// 只对「既无任何登录身份、又不在 HTTP 请求管线内」的调用免授权：这类调用不可能由外部请求到达，
    /// 也绝不把缺失身份当作管理员（真实匿名请求因处于请求管线内一律 fail closed）。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>比价数据入口授权（实时身份 + 既有「供应商比价」菜单 + ERP-097 客户数据范围）。</summary>
    private async Task<SalespersonDataScope?> EnsureAuthorizedAsync()
        => RequiresLiveAuthorization()
            ? await PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId())
            : null;

    /// <summary>批次审批状态（只读）：逐行 pending / approved / rejected + 全量决定历史</summary>
    [HttpGet("batch")]
    public async Task<IActionResult> BatchStatus([FromQuery] string quoteNo)
    {
        // ERP-416：先授权并按整批持久化归属复核（批次混入范围外 / 空归属行即整批非披露拒绝）。
        var scope = await EnsureAuthorizedAsync();
        await PurchaseQuoteAuthorizationRules.EnsureBatchAllowedAsync(_db, scope, quoteNo, null);
        var batch = await PurchaseQuoteApproval.GetBatchStatusAsync(_db, quoteNo);
        return Ok(ApiResponse<PurchaseQuoteDecisionBatch>.Success(batch,
            $"比价批次 {batch.QuoteNo}：待审批 {batch.PendingCount}、已批准 {batch.ApprovedCount}、已拒绝 {batch.RejectedCount}"));
    }

    /// <summary>记录审批决定（批准 / 拒绝）：append-only，拒绝重复 / 陈旧 / 跨批次决定</summary>
    [HttpPost("decide")]
    public async Task<IActionResult> Decide([FromBody] PurchaseQuoteDecisionRequest request)
    {
        // ERP-416：先授权并复核被审批比价行的持久化归属（决定人字段不是授权依据），授权先于任何追加。
        var scope = await EnsureAuthorizedAsync();
        if (RequiresLiveAuthorization() && request is { QuoteId: > 0 })
            await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, scope, request.QuoteId);

        var decision = await PurchaseQuoteApproval.DecideAsync(_db, request);
        var verb = decision.Decision == PurchaseQuoteApproval.Approved ? "批准选中" : "拒绝";
        return Ok(ApiResponse<PurchaseQuoteDecision>.Success(decision,
            $"已{verb}比价行 #{decision.QuoteId}（审批参考 {decision.DecisionRef}）"));
    }
}

