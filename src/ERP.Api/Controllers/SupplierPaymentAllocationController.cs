using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商付款单 → 采购订单 付款引用（分摊）证据登记控制器（ERP-049）：登记「某张既有付款单把多少钱指向了
/// 哪几张既有采购订单」，让付款与采购订单的对应关系可追溯。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>银行付款凭证、<strong>不是</strong>应付账款核销、
/// <strong>不是</strong>发票核销、<strong>不是</strong>税务（进项）抵扣判断，也<strong>不是</strong>供应商余额；
/// 所有接口只读写 <c>SupplierPaymentAllocations</c> 一张表，<strong>不</strong>改写付款单的审批 / 执行状态与任何字段、
/// <strong>不</strong>改写采购订单状态 / 到货进度 / 金额与明细 / 结算进度、<strong>不</strong>改写发票与发票关联、
/// 库存与库存成本、退税记录、费用与供应商余额，也不执行任何付款、记账、核销或结算动作；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/supplier-payment-allocations")]
[Authorize]
public class SupplierPaymentAllocationController : ControllerBase
{
    private readonly IErpDbContext _db;

    public SupplierPaymentAllocationController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 付款引用行台账（分页，只读）：可按付款单 / 采购订单 / 供应商 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读），付款单与采购订单可用性都是只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] SupplierPaymentAllocationQuery query)
        => Ok(ApiResponse<PagedResult<SupplierPaymentAllocationDto>>.Success(
            await SupplierPaymentAllocationService.ListAsync(_db, query)));

    /// <summary>
    /// 可引用付款单候选（只读、有界）：只列出既有、未删除的付款单（可按供应商筛选 / 付款单号检索），
    /// 并标注有效行已引用金额、未引用金额与资格文案（不是银行未付金额、应付余额或发票余额）。
    /// </summary>
    [HttpGet("payments")]
    public async Task<IActionResult> PaymentCandidates(
        [FromQuery] long? supplierId,
        [FromQuery] string? keyword,
        [FromQuery] int take = SupplierPaymentAllocationRules.MaxPaymentCandidates)
        => Ok(ApiResponse<List<SupplierPaymentAllocationPaymentCandidateDto>>.Success(
            await SupplierPaymentAllocationService.ListPaymentCandidatesAsync(_db, supplierId, keyword, take)));

    /// <summary>
    /// 付款单侧汇总（只读派生）：付款单快照 + 有效行已引用 / 未引用金额、行数与已作废行数 + 有界逐行明细；
    /// 已作废历史永不并入有效合计（只单独计数与列出），未引用金额绝不被猜测到任何订单。
    /// </summary>
    [HttpGet("payments/{paymentId:long}/summary")]
    public async Task<IActionResult> PaymentSummary(long paymentId)
        => Ok(ApiResponse<SupplierPaymentAllocationPaymentSummaryDto>.Success(
            await SupplierPaymentAllocationService.GetPaymentSummaryAsync(_db, paymentId)));

    /// <summary>
    /// 指定付款单的引用行清单（只读、有界；付款单详情工作流用）：默认返回全部状态（含已作废历史），
    /// status 传 1 只看有效 / 传 2 只看已作废。
    /// </summary>
    [HttpGet("payments/{paymentId:long}/allocations")]
    public async Task<IActionResult> AllocationsForPayment(
        long paymentId,
        [FromQuery] int? status = null,
        [FromQuery] int take = SupplierPaymentAllocationRules.MaxAllocationsPerPayment)
        => Ok(ApiResponse<List<SupplierPaymentAllocationDto>>.Success(
            await SupplierPaymentAllocationService.ListForPaymentAsync(_db, paymentId, status, take)));

    /// <summary>
    /// 可引用采购订单候选（只读、有界）：只返回同供应商 + 同币种的未删除订单（含已取消订单并标注不可引用），
    /// 附带订单总额、本付款单已引用、其他付款单已引用与剩余未被付款引用证据覆盖的金额（派生值，不是应付余额）。
    /// </summary>
    [HttpGet("payments/{paymentId:long}/order-candidates")]
    public async Task<IActionResult> OrderCandidates(
        long paymentId,
        [FromQuery] string? keyword,
        [FromQuery] int take = SupplierPaymentAllocationRules.MaxOrderCandidates)
        => Ok(ApiResponse<List<SupplierPaymentAllocationOrderCandidateDto>>.Success(
            await SupplierPaymentAllocationService.ListOrderCandidatesAsync(_db, paymentId, keyword, take)));

    /// <summary>引用行详情（含付款单与采购订单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<SupplierPaymentAllocationDto>.Success(
            await SupplierPaymentAllocationService.GetAsync(_db, id)));

    /// <summary>
    /// 登记一条付款引用行：校验付款单可用、币种口径、引用金额（精度 + 大于 0）、采购订单资格
    /// （存在 / 未删除 / 未取消 / 供应商一致 / 币种一致）、重复有效行与付款金额上限，全部通过后才写入证据。
    /// <para>本接口<strong>不</strong>改写付款单与采购订单的任何字段，也<strong>不</strong>执行付款或核销。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SupplierPaymentAllocationSaveDto dto)
        => Ok(ApiResponse<SupplierPaymentAllocationDto>.Success(
            await SupplierPaymentAllocationService.CreateAsync(_db, dto),
            "付款引用已登记（仅证据留痕；未执行付款、未结算、未核销）"));

    /// <summary>
    /// 作废引用行（必须填写原因）：保留原始金额、付款单 / 供应商 / 订单快照与审计历史，不物理删除、
    /// 不静默替换，也不改写付款单与采购订单。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] SupplierPaymentAllocationVoidRequest? request)
        => Ok(ApiResponse<SupplierPaymentAllocationDto>.Success(
            await SupplierPaymentAllocationService.VoidAsync(_db, id, request?.Reason),
            "付款引用已作废（原始值与快照保留，可读）"));
}
