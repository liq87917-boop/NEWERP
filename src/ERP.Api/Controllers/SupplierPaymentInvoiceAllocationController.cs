using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商付款单 → 供应商采购发票 付款引用（分摊）证据登记控制器（ERP-066）：登记「某张既有付款单把多少钱指向了
/// 哪几张既有已登记采购发票」，让付款与采购发票的对应关系可追溯。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>银行付款凭证、<strong>不是</strong>应付账款核销、
/// <strong>不是</strong>发票认证 / 抵扣、<strong>不是</strong>税务申报，也<strong>不是</strong>供应商余额；
/// 所有接口只读写 <c>SupplierPaymentInvoiceAllocations</c> 一张表，<strong>不</strong>改写付款单的审批 / 执行状态与任何字段、
/// <strong>不</strong>改写供应商采购发票的类型 / 代码 / 号码 / 日期 / 到期日 / 付款条件 / 金额 / 状态 / 关联行、
/// <strong>不</strong>改写采购订单与到货进度、ERP-049 的采购订单引用行、库存与库存成本、退税记录、费用与供应商余额，
/// 也不执行任何付款、记账、核销或结算动作；本控制器<strong>刻意不提供</strong> <c>PUT</c> / <c>PATCH</c> /
/// <c>DELETE</c>（更正只走显式作废，保留原始值与历史）；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/supplier-payment-invoice-allocations")]
[Authorize]
public class SupplierPaymentInvoiceAllocationController : ControllerBase
{
    private readonly IErpDbContext _db;

    public SupplierPaymentInvoiceAllocationController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 付款发票引用行台账（分页，只读）：可按付款单 / 发票 / 供应商 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读），付款单与发票可用性都是只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] SupplierPaymentInvoiceAllocationQuery query)
        => Ok(ApiResponse<PagedResult<SupplierPaymentInvoiceAllocationDto>>.Success(
            await SupplierPaymentInvoiceAllocationService.ListAsync(_db, query)));

    /// <summary>
    /// 可引用付款单候选（只读、有界）：只列出既有、未删除的付款单（可按供应商筛选 / 付款单号检索），
    /// 并标注**发票引用**有效行已引用金额、未引用金额与资格文案（不是银行未付金额、应付余额或发票余额，
    /// 也不与 ERP-049 的采购订单引用金额相加）。
    /// </summary>
    [HttpGet("payments")]
    public async Task<IActionResult> PaymentCandidates(
        [FromQuery] long? supplierId,
        [FromQuery] string? keyword,
        [FromQuery] int take = SupplierPaymentInvoiceAllocationRules.MaxPaymentCandidates)
        => Ok(ApiResponse<List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>>.Success(
            await SupplierPaymentInvoiceAllocationService.ListPaymentCandidatesAsync(_db, supplierId, keyword, take)));

    /// <summary>
    /// 付款单侧汇总（只读派生）：付款单快照 + 有效发票引用行已引用 / 未引用金额、行数与已作废行数
    /// + 有界逐行明细 + **独立标注**的 ERP-049 采购订单引用维度（两个维度绝不相加）；
    /// 已作废历史永不并入有效合计（只单独计数与列出），未引用金额绝不被猜测到任何发票。
    /// </summary>
    [HttpGet("payments/{paymentId:long}/summary")]
    public async Task<IActionResult> PaymentSummary(long paymentId)
        => Ok(ApiResponse<SupplierPaymentInvoiceAllocationPaymentSummaryDto>.Success(
            await SupplierPaymentInvoiceAllocationService.GetPaymentSummaryAsync(_db, paymentId)));

    /// <summary>
    /// 指定付款单的引用行清单（只读、有界；付款单详情工作流用）：默认返回全部状态（含已作废历史），
    /// status 传 1 只看有效 / 传 2 只看已作废。
    /// </summary>
    [HttpGet("payments/{paymentId:long}/allocations")]
    public async Task<IActionResult> AllocationsForPayment(
        long paymentId,
        [FromQuery] int? status = null,
        [FromQuery] int take = SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerPayment)
        => Ok(ApiResponse<List<SupplierPaymentInvoiceAllocationDto>>.Success(
            await SupplierPaymentInvoiceAllocationService.ListForPaymentAsync(_db, paymentId, status, take)));

    /// <summary>
    /// 可引用供应商采购发票候选（只读、有界）：只返回同供应商 + 同币种的未删除发票（含草稿 / 已作废发票并标注不可引用），
    /// 附带含税总额、本付款单已引用、其他付款单已引用与剩余未被付款引用证据覆盖的金额（派生值，不是应付余额）。
    /// </summary>
    [HttpGet("payments/{paymentId:long}/invoice-candidates")]
    public async Task<IActionResult> InvoiceCandidates(
        long paymentId,
        [FromQuery] string? keyword,
        [FromQuery] int take = SupplierPaymentInvoiceAllocationRules.MaxInvoiceCandidates)
        => Ok(ApiResponse<List<SupplierPaymentInvoiceAllocationInvoiceCandidateDto>>.Success(
            await SupplierPaymentInvoiceAllocationService.ListInvoiceCandidatesAsync(_db, paymentId, keyword, take)));

    /// <summary>
    /// 发票侧汇总（只读派生）：发票快照 + 全部有效引用行已引用金额 / 未引用含税总额 / 行数 / 已作废行数
    /// + 有界逐行明细；未引用含税总额不是应付余额、账龄或付款依据。
    /// </summary>
    [HttpGet("invoices/{purchaseInvoiceId:long}/summary")]
    public async Task<IActionResult> InvoiceSummary(long purchaseInvoiceId)
        => Ok(ApiResponse<SupplierPaymentInvoiceAllocationInvoiceSummaryDto>.Success(
            await SupplierPaymentInvoiceAllocationService.GetInvoiceSummaryAsync(_db, purchaseInvoiceId)));

    /// <summary>指定供应商采购发票的引用行清单（只读、有界；status 传 1 只看有效 / 传 2 只看已作废）</summary>
    [HttpGet("invoices/{purchaseInvoiceId:long}/allocations")]
    public async Task<IActionResult> AllocationsForInvoice(
        long purchaseInvoiceId,
        [FromQuery] int? status = null,
        [FromQuery] int take = SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerInvoice)
        => Ok(ApiResponse<List<SupplierPaymentInvoiceAllocationDto>>.Success(
            await SupplierPaymentInvoiceAllocationService.ListForInvoiceAsync(
                _db, purchaseInvoiceId, status, take)));

    /// <summary>引用行详情（含付款单与发票可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<SupplierPaymentInvoiceAllocationDto>.Success(
            await SupplierPaymentInvoiceAllocationService.GetAsync(_db, id)));

    /// <summary>
    /// 登记一条付款发票引用行：校验付款单可用、币种口径、引用金额（精度 + 大于 0）、发票资格
    /// （存在 / 未删除 / 已登记未作废 / 供应商一致 / 币种一致）、重复有效行、付款单金额上限与发票未引用含税总额上限，
    /// 全部通过后才写入证据；登记人取当前登录账号（客户端不可提交）。
    /// <para>本接口<strong>不</strong>改写付款单与发票的任何字段，也<strong>不</strong>执行付款、认证或核销。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SupplierPaymentInvoiceAllocationSaveDto dto)
        => Ok(ApiResponse<SupplierPaymentInvoiceAllocationDto>.Success(
            await SupplierPaymentInvoiceAllocationService.CreateAsync(_db, dto, CurrentUserName()),
            "付款发票引用已登记（仅证据留痕；未执行付款、未核销、未认证）"));

    /// <summary>
    /// 作废引用行（必须填写原因）：保留原始金额、付款单 / 供应商 / 发票快照、登记人与审计历史，
    /// 不物理删除、不改派、不静默替换金额，也不改写付款单与发票。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] SupplierPaymentInvoiceAllocationVoidRequest? request)
        => Ok(ApiResponse<SupplierPaymentInvoiceAllocationDto>.Success(
            await SupplierPaymentInvoiceAllocationService.VoidAsync(_db, id, request?.Reason),
            "付款发票引用已作废（原始值、快照与登记人保留，可读）"));

    /// <summary>当前登录账号名（登记人快照只取服务端身份，绝不接受客户端提交的登记人字段）</summary>
    private string? CurrentUserName()
        => User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
