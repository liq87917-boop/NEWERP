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
        => Ok(ApiResponse<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListAsync(_db, query)));

    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationMetadataDto>.Success(
            CustomerSalesInvoiceCollectionAllocationService.GetMetadata()));

    [HttpGet("receipts")]
    public async Task<IActionResult> ReceiptCandidates(
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationService.MaxReceiptCandidates)
        => Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationReceiptCandidateDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListReceiptCandidatesAsync(
                _db, customerId, currency, keyword, take)));

    [HttpGet("invoices")]
    public async Task<IActionResult> InvoiceCandidates(
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationService.MaxInvoiceCandidates)
        => Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationInvoiceCandidateDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListInvoiceCandidatesAsync(
                _db, customerId, currency, keyword, take)));

    [HttpGet("receipts/{receiptId:long}/summary")]
    public async Task<IActionResult> ReceiptSummary(long receiptId)
        => Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.GetReceiptSummaryAsync(_db, receiptId)));

    [HttpGet("receipts/{receiptId:long}/allocations")]
    public async Task<IActionResult> AllocationsForReceipt(
        long receiptId,
        [FromQuery] int? status = null,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt)
        => Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListForReceiptAsync(_db, receiptId, status, take)));

    [HttpGet("invoices/{customerSalesInvoiceEvidenceId:long}/summary")]
    public async Task<IActionResult> InvoiceSummary(long customerSalesInvoiceEvidenceId)
        => Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.GetInvoiceSummaryAsync(
                _db, customerSalesInvoiceEvidenceId)));

    [HttpGet("invoices/{customerSalesInvoiceEvidenceId:long}/allocations")]
    public async Task<IActionResult> AllocationsForInvoice(
        long customerSalesInvoiceEvidenceId,
        [FromQuery] int? status = null,
        [FromQuery] int take = CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice)
        => Ok(ApiResponse<List<CustomerSalesInvoiceCollectionAllocationDto>>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.ListForInvoiceAsync(
                _db, customerSalesInvoiceEvidenceId, status, take)));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.GetAsync(_db, id)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CustomerSalesInvoiceCollectionAllocationSaveDto dto)
        => Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.CreateAsync(_db, dto, CurrentUserName()),
            "收款分摊证据已登记（仅证据留痕；未执行收款、未核销、未结算、未记账）"));

    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] CustomerSalesInvoiceCollectionAllocationVoidRequest? request)
        => Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.VoidAsync(_db, id, request?.Reason),
            "收款分摊证据已作废（原始金额与历史保留，可读）"));

    private string? CurrentUserName()
        => User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
