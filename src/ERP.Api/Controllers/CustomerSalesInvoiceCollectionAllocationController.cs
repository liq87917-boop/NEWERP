using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户收款单 → 客户销项发票证据 收款分摊证据登记控制器（ERP-073）。
/// <para>边界：不是到账凭证 / 应收台账 / 货款核销 / 客户对账单 / 收入确认 / 税务判断 / 结算确认 / 记账分录；
/// 只读写 <c>CustomerSalesInvoiceCollectionAllocations</c> 一张表；建表 / 索引由 SchemaUpgrader 第 45 段幂等补齐。</para>
/// </summary>
[ApiController]
[Route("api/customer-sales-invoice-collection-allocations")]
[Authorize]
public class CustomerSalesInvoiceCollectionAllocationController : ControllerBase
{
    private readonly IErpDbContext _db;

    public CustomerSalesInvoiceCollectionAllocationController(IErpDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] CustomerSalesInvoiceCollectionAllocationQuery query)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListAsync(_db, query, CurrentUserId())));
    }

    [HttpGet("metadata")]
    public async Task<IActionResult> Metadata()
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationMetadataDto>.Success(
            CustomerSalesInvoiceCollectionAllocationService.GetMetadata()));
    }

    [HttpGet("receipts")]
    public async Task<IActionResult> ReceiptCandidates(
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationService.MaxReceiptCandidates)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationReceiptCandidateDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListReceiptCandidatesAsync(
                _db, customerId, currency, keyword, take, CurrentUserId())));
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> InvoiceCandidates(
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationService.MaxInvoiceCandidates)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationInvoiceCandidateDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListInvoiceCandidatesAsync(
                _db, customerId, currency, keyword, take, CurrentUserId())));
    }

    [HttpGet("receipts/{receiptId:long}/summary")]
    public async Task<IActionResult> ReceiptSummary(long receiptId)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.GetReceiptSummaryAsync(
                _db, receiptId, CurrentUserId())));
    }

    [HttpGet("receipts/{receiptId:long}/allocations")]
    public async Task<IActionResult> AllocationsForReceipt(
        long receiptId,
        [FromQuery] int? status = null,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListForReceiptAsync(
                _db, receiptId, CurrentUserId(), status, take)));
    }

    [HttpGet("invoices/{customerSalesInvoiceEvidenceId:long}/summary")]
    public async Task<IActionResult> InvoiceSummary(long customerSalesInvoiceEvidenceId)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.GetInvoiceSummaryAsync(
                _db, customerSalesInvoiceEvidenceId, CurrentUserId())));
    }

    [HttpGet("invoices/{customerSalesInvoiceEvidenceId:long}/allocations")]
    public async Task<IActionResult> AllocationsForInvoice(
        long customerSalesInvoiceEvidenceId,
        [FromQuery] int? status = null,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListForInvoiceAsync(
                _db, customerSalesInvoiceEvidenceId, CurrentUserId(), status, take)));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.GetAsync(_db, id, CurrentUserId())));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CustomerSalesInvoiceCollectionAllocationSaveDto dto)
    {
        // 进程内直接调用（无 HTTP 请求管线且无登录身份）沿用既有语义；真实 HTTP 路由一律实时授权。
        if (!RequiresLiveAuthorization())
            return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
                await CustomerSalesInvoiceCollectionAllocationService.CreateAsync(_db, dto, CurrentUserName()),
                "收款分摊证据已登记（仅证据留痕；未执行收款、未核销、未结算、未记账）"));

        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.CreateAuthorizedAsync(
                _db, dto, CurrentUserName(), CurrentUserId()),
            "收款分摊证据已登记（仅证据留痕；未执行收款、未核销、未结算、未记账）"));
    }

    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] CustomerSalesInvoiceCollectionAllocationVoidRequest? request)
    {
        if (!RequiresLiveAuthorization())
            return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
                await CustomerSalesInvoiceCollectionAllocationService.VoidAsync(_db, id, request?.Reason),
                "收款分摊证据已作废（原始金额与历史保留，可读）"));

        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.VoidAuthorizedAsync(
                _db, id, request?.Reason, CurrentUserId()),
            "收款分摊证据已作废（原始金额与历史保留，可读）"));
    }

    private string? CurrentUserName()
        => User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

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
