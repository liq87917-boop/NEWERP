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
/// <para>ERP-487：<strong>每一条入口（台账 / 元数据 / 收款单候选 / 发票候选 / 收款单侧汇总 / 收款单侧清单 /
/// 发票侧清单 / 详情 / 登记 / 作废）都无条件执行</strong>实时身份（缺失 / 为零 / 未知 / 已删除一律未认证）、
/// 既有「收款单」（<c>receipt</c>）菜单授权（禁用 / 缺少或被撤销一律权限不足）与 ERP-097 权威客户数据范围；
/// 登记 / 作废不再存在「无 HTTP 请求管线且无身份」的服务直调旁路，被拒调用零证据行 / 收款单 / 发票变更。
/// 契约与边界见 <see cref="ERP.Application.Services.CustomerReceiptLifecycleRules.PathIndependenceText"/>
/// 与 <see cref="ERP.Application.Services.CustomerReceiptLifecycleRules.CollectionAllocationPathIndependenceText"/>。</para>
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

    /// <summary>
    /// 登记一条收款分摊证据：校验收款单可用、币种口径、分摊金额（精度 + 大于 0）、发票证据资格
    /// （存在 / 未删除 / 已登记 / 客户一致 / 币种一致）、重复有效行与收款单同币种共享额度（ERP-350）。
    /// <para>ERP-487：与调用形状完全无关 —— 登记入口<strong>无条件</strong>先解析实时启用身份、既有「收款单」（receipt）
    /// 菜单授权与 ERP-097 权威客户数据范围（含收款单与发票证据两侧归属客户），再执行授权服务重载；
    /// 缺失 / 为零 / 未知 / 已删除身份一律未认证（2000），禁用 / 被撤销菜单一律权限不足（2002），越界客户 / 来源
    /// 一律受控非披露拒绝，且全部先于任何金额、快照、候选读取与写入。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CustomerSalesInvoiceCollectionAllocationSaveDto dto)
    {
        // ERP-487：绑定到请求管线（空路径或已赋值路径）与纯进程内直调都一律实时授权；
        // 缺失身份 fail closed，绝不退回无授权的服务直调（不再有请求形状 / 空上下文旁路）。
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<CustomerSalesInvoiceCollectionAllocationDto>.Success(
            await CustomerSalesInvoiceCollectionAllocationService.CreateAuthorizedAsync(
                _db, dto, CurrentUserName(), CurrentUserId()),
            "收款分摊证据已登记（仅证据留痕；未执行收款、未核销、未结算、未记账）"));
    }

    /// <summary>
    /// 作废证据行（必须填写原因）：保留原始金额、收款单 / 客户 / 发票快照与审计历史，不物理删除、不静默替换。
    /// <para>ERP-487：与登记同一口径 —— 作废入口无条件实时授权（身份 + 既有「收款单」菜单 + 权威客户范围），
    /// 绝不因请求形状或缺少身份而放行，被拒调用零行变更。</para>
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] CustomerSalesInvoiceCollectionAllocationVoidRequest? request)
    {
        // ERP-487：作废同样无条件实时授权（空路径 / 缺失身份同样 fail closed）。
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
}
