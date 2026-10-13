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
/// <para>ERP-486：实时授权与调用形状完全无关 —— 不读取 <c>Request.Path</c>、不判断是否存在 <c>HttpContext</c>、
/// 不依据身份是否可解析，两条路由（批次审批状态与记录决定）都<b>无条件</b>调用实时身份 / 既有菜单 / 权威范围判定，
/// 缺失 / 零 / 未知 / 已删除身份一律受控非披露拒绝，且拒绝先于任何证据读取、决定追加与单号 / 状态变更。</para>
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
    /// ERP-486：比价数据入口授权（实时身份 + 既有「供应商比价」菜单 + ERP-097 客户数据范围）。
    /// <b>与调用形状完全无关</b>：不读取 <c>Request.Path</c>、不判断是否存在 <c>HttpContext</c>、不依据身份是否可解析，
    /// 两条路由都无条件调用；缺失 / 零 / 未知 / 已删除身份一律未认证（fail closed），授权先于任何读取与追加。
    /// </summary>
    private Task<SalespersonDataScope> EnsureAuthorizedAsync()
        => PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId());

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

    /// <summary>
    /// 记录审批决定（批准 / 拒绝）：append-only，拒绝重复 / 陈旧 / 跨批次决定。
    /// ERP-417：实时认证请求下先取实时身份 / 既有菜单授权与可信操作人，随后 <see cref="PurchaseQuoteApproval.DecideAsync"/>
    /// 在比价行锁内再次复核实时身份 / 菜单 / 客户范围并追加**恰好一条**决定；决定人一律取自登录账号，
    /// 请求体中的 <c>DecidedBy</c> / <c>DecidedByName</c> 在实时请求下被忽略（伪造无效）。
    /// </summary>
    [HttpPost("decide")]
    public async Task<IActionResult> Decide([FromBody] PurchaseQuoteDecisionRequest request)
    {
        // ERP-486：无条件实时身份 + 既有菜单 + 权威客户范围 + 实时可信操作人（授权先于任何读取与追加，
        // 缺失 / 零 / 未知身份一律受控非披露拒绝）。判定人一律取自登录账号，请求体中的
        // DecidedBy / DecidedByName 被忽略（伪造无效）。
        var scope = await EnsureAuthorizedAsync();
        var actor = await PurchaseQuoteAuthorizationRules.EnsureLiveActorAsync(_db, CurrentUserId());
        if (request is { QuoteId: > 0 })
            await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, scope, request.QuoteId);

        var decision = await PurchaseQuoteApproval.DecideAsync(_db, request, actor);
        var verb = decision.Decision == PurchaseQuoteApproval.Approved ? "批准选中" : "拒绝";
        return Ok(ApiResponse<PurchaseQuoteDecision>.Success(decision,
            $"已{verb}比价行 #{decision.QuoteId}（审批参考 {decision.DecisionRef}）"));
    }
}

