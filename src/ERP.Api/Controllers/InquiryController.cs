using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 询价单控制器
/// </summary>
[Route("api/inquiries")]
public class InquiryController : DocumentControllerBase<Inquiry>
{
    private readonly IDocumentNumberService _noService;

    public InquiryController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.InquiryNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<Inquiry>>.Success(
            new PagedResult<Inquiry> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>查询详情（含明细）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");
        return Ok(ApiResponse<Inquiry>.Success(entity));
    }

    /// <summary>把已审核询价单带入报价单新增表单，不落库。</summary>
    [HttpGet("{id:long}/quotation-prefill")]
    public async Task<IActionResult> QuotationPrefill(long id)
    {
        var quotation = await InquiryQuotationConversion.BuildDraftAsync(Db, id);
        return Ok(ApiResponse<object>.Success(new { SourceId = id, quotation }));
    }

    /// <summary>把已审核询价单直接生成一张报价单；同一询价单只允许一张。</summary>
    [HttpPost("{id:long}/to-quotation")]
    public async Task<IActionResult> ToQuotation(long id)
    {
        var quotation = await InquiryQuotationConversion.BuildDraftAsync(Db, id);
        quotation.QuotationNo = await _noService.GenerateAsync(DocumentType.Quotation);
        quotation.CreatedAt = DateTime.Now;
        foreach (var detail in quotation.Details)
        {
            detail.QuotationNo = quotation.QuotationNo;
            detail.CreatedAt = DateTime.Now;
        }

        var inquiry = await Db.Inquiries.FirstAsync(o => o.Id == id && !o.IsDeleted);
        Db.Quotations.Add(quotation);
        inquiry.Status = DocumentStatus.Completed;
        inquiry.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new
        {
            quotation.Id,
            quotation.QuotationNo,
            SourceId = id,
            SourceNo = inquiry.InquiryNo
        }, "报价单生成成功"));
    }

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Inquiry entity)
    {
        entity.Id = 0;
        entity.InquiryNo = await _noService.GenerateAsync(DocumentType.Inquiry);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        foreach (var d in entity.Details) d.Amount = d.Quantity * d.UnitPrice;
        Db.Inquiries.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.InquiryNo }, "询价单创建成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] Inquiry entity)
    {
        var existing = await Db.Inquiries.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.InquiryDate = entity.InquiryDate;
        existing.CustomerId = entity.CustomerId;
        existing.ContactPerson = entity.ContactPerson;
        existing.ContactPhone = entity.ContactPhone;
        existing.SalesmanId = entity.SalesmanId;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.ValidDays = entity.ValidDays;
        existing.Remark = entity.Remark;

        Db.InquiryDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.InquiryId = id;
            d.CreatedAt = DateTime.Now;
            d.Amount = d.Quantity * d.UnitPrice;
        }
        existing.Details = entity.Details;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "询价单更新成功"));
    }

    /// <summary>导出</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var source = Db.Inquiries.AsNoTracking().Include(o => o.Details).Where(o => !o.IsDeleted);
        if (start.HasValue) source = source.Where(o => o.InquiryDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.InquiryDate <= end.Value);
        var items = await source.OrderByDescending(o => o.Id).ToListAsync();
        return Ok(ApiResponse<List<Inquiry>>.Success(items));
    }
}
