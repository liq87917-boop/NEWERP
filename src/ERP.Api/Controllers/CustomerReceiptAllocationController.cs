using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户收款单 → 销售订单 收款引用（分摊）证据登记控制器（ERP-053）：登记「某张既有收款单把多少钱指向了
/// 哪几张既有销售订单」，让收款与销售订单的对应关系可追溯。
/// <para>为什么需要本模块（ERP-053 审计结论）：ERP-032 / ERP-046 的权威口径里收款单
/// （<c>FinanceReceipt</c>）<strong>只记录客户、没有订单级持久化引用</strong>，历史收款证据只能作为
/// 「未关联证据」列出；仓库中不存在可复用的收款单 → 订单权威关系，因此本模块提供**唯一**的收款引用
/// 登记模型（不在收款单 / 销售订单上加列，也不建第二套链接表）。</para>
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>银行入账 / 到账凭证、<strong>不是</strong>应收账款台账或余额、
/// <strong>不是</strong>货款核销、<strong>不是</strong>客户对账单、<strong>不是</strong>税务（销项）判断，
/// 也<strong>不构成</strong>债务清偿或客户信用结论；所有接口只读写 <c>CustomerReceiptAllocations</c> 一张表，
/// <strong>不</strong>改写收款单的审批 / 执行状态与任何字段、<strong>不</strong>改写销售订单状态 / 出货进度 /
/// 金额与明细 / 交期与合同字段、<strong>不</strong>改写客户信用状态、发票、库存与库存成本、装柜与单证、
/// 佣金 / 回佣、费用或退税记录，也不执行任何收款、记账、核销、结算或催收动作；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/customer-receipt-allocations")]
[Authorize]
public class CustomerReceiptAllocationController : ControllerBase
{
    private readonly IErpDbContext _db;

    public CustomerReceiptAllocationController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 收款引用行台账（分页，只读）：可按收款单 / 销售订单 / 客户 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读），收款单与销售订单可用性都是只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] CustomerReceiptAllocationQuery query)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<PagedResult<CustomerReceiptAllocationDto>>.Success(
            await CustomerReceiptAllocationService.ListAsync(_db, query, CurrentUserId())));
    }

    /// <summary>
    /// 可引用收款单候选（只读、有界）：只列出既有、未删除的客户收款单（可按客户筛选 / 收款单号检索），
    /// 并标注有效行已引用金额、未引用金额与资格文案（不是银行未到账金额、应收余额或客户欠款）。
    /// </summary>
    [HttpGet("receipts")]
    public async Task<IActionResult> ReceiptCandidates(
        [FromQuery] long? customerId,
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerReceiptAllocationRules.MaxReceiptCandidates)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerReceiptAllocationReceiptCandidateDto>>.Success(
            await CustomerReceiptAllocationService.ListReceiptCandidatesAsync(
                _db, customerId, keyword, CurrentUserId(), take)));
    }

    /// <summary>
    /// 收款单侧汇总（只读派生）：收款单快照 + 有效行已引用 / 未引用金额、行数与已作废行数 + 有界逐行明细；
    /// 已作废历史永不并入有效合计（只单独计数与列出），未引用金额绝不被猜测到任何订单，也不表示应收未收余额。
    /// </summary>
    [HttpGet("receipts/{receiptId:long}/summary")]
    public async Task<IActionResult> ReceiptSummary(long receiptId)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerReceiptAllocationReceiptSummaryDto>.Success(
            await CustomerReceiptAllocationService.GetReceiptSummaryAsync(_db, receiptId, CurrentUserId())));
    }

    /// <summary>
    /// 指定收款单的引用行清单（只读、有界；收款单详情工作流用）：默认返回全部状态（含已作废历史），
    /// status 传 1 只看有效 / 传 2 只看已作废。
    /// </summary>
    [HttpGet("receipts/{receiptId:long}/allocations")]
    public async Task<IActionResult> AllocationsForReceipt(
        long receiptId,
        [FromQuery] int? status = null,
        [FromQuery] int take = CustomerReceiptAllocationRules.MaxAllocationsPerReceipt)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerReceiptAllocationDto>>.Success(
            await CustomerReceiptAllocationService.ListForReceiptAsync(
                _db, receiptId, CurrentUserId(), status, take)));
    }

    /// <summary>
    /// 可引用销售订单候选（只读、有界）：只返回同客户 + 同币种的未删除订单（含已取消订单并标注不可引用），
    /// 附带订单总额、本收款单已引用、其他收款单已引用与剩余未被收款引用证据覆盖的金额（派生值，不是应收余额）。
    /// </summary>
    [HttpGet("receipts/{receiptId:long}/order-candidates")]
    public async Task<IActionResult> OrderCandidates(
        long receiptId,
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerReceiptAllocationRules.MaxOrderCandidates)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerReceiptAllocationOrderCandidateDto>>.Success(
            await CustomerReceiptAllocationService.ListOrderCandidatesAsync(
                _db, receiptId, keyword, CurrentUserId(), take)));
    }

    /// <summary>引用行详情（含收款单与销售订单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerReceiptAllocationDto>.Success(
            await CustomerReceiptAllocationService.GetAsync(_db, id, CurrentUserId())));
    }

    /// <summary>
    /// 登记一条收款引用行：校验收款单可用、币种口径、引用金额（精度 + 大于 0）、销售订单资格
    /// （存在 / 未删除 / 未取消 / 客户一致 / 币种一致）、重复有效行与收款金额上限，全部通过后才写入证据。
    /// <para>本接口<strong>不</strong>改写收款单与销售订单的任何字段，也<strong>不</strong>执行收款或核销。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CustomerReceiptAllocationSaveDto dto)
    {
        // 进程内直接调用（无 HTTP 请求管线且无登录身份）沿用既有语义；真实 HTTP 路由一律实时授权。
        if (!RequiresLiveAuthorization())
            return Ok(ApiResponse<CustomerReceiptAllocationDto>.Success(
                await CustomerReceiptAllocationService.CreateAsync(_db, dto),
                "收款引用已登记（仅证据留痕；未执行收款、未结算、未核销）"));

        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerReceiptAllocationDto>.Success(
            await CustomerReceiptAllocationService.CreateAuthorizedAsync(_db, dto, CurrentUserId()),
            "收款引用已登记（仅证据留痕；未执行收款、未结算、未核销）"));
    }

    /// <summary>
    /// 作废引用行（必须填写原因）：保留原始金额、收款单 / 客户 / 订单快照与审计历史，不物理删除、
    /// 不静默替换，也不改写收款单与销售订单。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] CustomerReceiptAllocationVoidRequest? request)
    {
        if (!RequiresLiveAuthorization())
            return Ok(ApiResponse<CustomerReceiptAllocationDto>.Success(
                await CustomerReceiptAllocationService.VoidAsync(_db, id, request?.Reason),
                "收款引用已作废（原始值与快照保留，可读）"));

        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerReceiptAllocationDto>.Success(
            await CustomerReceiptAllocationService.VoidAuthorizedAsync(_db, id, request?.Reason, CurrentUserId()),
            "收款引用已作废（原始值与快照保留，可读）"));
    }

    /// <summary>当前登录用户 Id（缺失或非数字返回 null，由授权规则 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 是否必须执行实时授权：真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
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
}
