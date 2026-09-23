using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 报价单控制器（主子表：一张报价单多行商品）
/// 业务链：询价单 Inquiry → **报价单 Quotation** → 形式发票 PI → 销售订单
/// 说明：本单据使用 EF 主子表实现（不走存储过程），不触碰现有单据的 SP
/// </summary>
[Route("api/sales/quotations")]
public class QuotationController : DocumentControllerBase<Quotation>
{
    private readonly IDocumentNumberService _noService;

    public QuotationController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页查询（keyword 匹配单号 / 客户名 / 来源询价单号 / 业务员）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.QuotationDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.QuotationDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.QuotationNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.InquiryNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<Quotation>>.Success(new PagedResult<Quotation>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        }));
    }

    /// <summary>查询详情（含明细）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<Quotation>.Success(entity));
    }

    /// <summary>按询价单带出客户与明细（新建报价单时用「带入询价明细」）</summary>
    [HttpGet("from-inquiry/{inquiryId:long}")]
    public async Task<IActionResult> FromInquiry(long inquiryId)
    {
        var inquiry = await Db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");

        BaseCustomer? customer = null;
        if (inquiry.CustomerId > 0)
            customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == inquiry.CustomerId && !c.IsDeleted);

        var result = new
        {
            inquiry.Id,
            inquiry.InquiryNo,
            inquiry.CustomerId,
            CustomerName = customer?.CustomerName ?? string.Empty,
            ContactPerson = string.IsNullOrWhiteSpace(inquiry.ContactPerson) ? customer?.ContactPerson : inquiry.ContactPerson,
            ContactPhone = string.IsNullOrWhiteSpace(inquiry.ContactPhone) ? customer?.Phone : inquiry.ContactPhone,
            ContactEmail = customer?.Email ?? string.Empty,
            TradeTerms = customer?.TradeTerms ?? string.Empty,
            PortOfDestination = customer?.DestinationPort ?? string.Empty,
            PaymentTerms = customer?.PaymentTerms ?? string.Empty,
            inquiry.Currency,
            inquiry.ExchangeRate,
            inquiry.ValidDays,
            Details = inquiry.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).Select((d, i) => new
            {
                SortNo = i + 1,
                d.ProductId,
                d.ProductName,
                d.Spec,
                d.Unit,
                d.Quantity,
                d.UnitPrice,
                d.Amount,
                d.Remark,
                Moq = string.Empty
            }).ToList()
        };
        return Ok(ApiResponse<object>.Success(result));
    }

    /// <summary>审核（草稿可直接审核，也支持提交后审核）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await GetOrThrowAsync(id, "报价单不存在");
        if (GetStatus(entity) == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("报价单已审核");
        SetStatus(entity, DocumentStatus.Approved);
        entity.Details.Clear();
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "报价单已审核"));
    }

    /// <summary>销审（退回草稿，可继续修改）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        var entity = await GetOrThrowAsync(id, "报价单不存在");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，可继续修改"));
    }

    /// <summary>创建（单号缺省由字轨生成；行号/金额/合计后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Quotation entity)
    {
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.QuotationNo))
            entity.QuotationNo = await _noService.GenerateAsync(DocumentType.Quotation);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Normalize(entity);
        Db.Quotations.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.QuotationNo }, "报价单创建成功"));
    }

    /// <summary>修改（已审核 / 已取消不可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] Quotation entity)
    {
        var existing = await Db.Quotations.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");
        if (GetStatus(existing) is DocumentStatus.Approved or DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已审核或已取消的报价单不可修改，请先销审");

        existing.QuotationDate = entity.QuotationDate;
        existing.ValidUntil = entity.ValidUntil;
        existing.CustomerId = entity.CustomerId;
        existing.CustomerName = entity.CustomerName;
        existing.ContactPerson = entity.ContactPerson;
        existing.ContactPhone = entity.ContactPhone;
        existing.ContactEmail = entity.ContactEmail;
        existing.InquiryId = entity.InquiryId;
        existing.InquiryNo = entity.InquiryNo;
        existing.TradeTerms = entity.TradeTerms;
        existing.PortOfLoading = entity.PortOfLoading;
        existing.PortOfDestination = entity.PortOfDestination;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.LeadTime = entity.LeadTime;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.SalesmanId = entity.SalesmanId;
        existing.SalesmanName = entity.SalesmanName;
        existing.Remark = entity.Remark;

        Db.QuotationDetails.RemoveRange(existing.Details);
        entity.QuotationNo = existing.QuotationNo;
        entity.Id = id;
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalAmount = entity.TotalAmount;
        existing.TotalAmountCny = entity.TotalAmountCny;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "报价单更新成功"));
    }

    /// <summary>行号 / 金额 / 合计 / 有效期统一整理（后端复核，防止前端篡改合计）</summary>
    private static void Normalize(Quotation e)
    {
        decimal total = 0;
        var line = 0;
        foreach (var d in e.Details)
        {
            d.Id = 0;
            d.QuotationId = e.Id;
            d.QuotationNo = e.QuotationNo;
            d.SortNo = ++line;
            d.Amount = Math.Round(d.Quantity * d.UnitPrice, 2);
            d.CreatedAt = DateTime.Now;
            total += d.Amount;
        }
        e.TotalAmount = Math.Round(total, 2);
        var rate = e.ExchangeRate == 0 ? 1 : e.ExchangeRate;
        e.TotalAmountCny = Math.Round(e.TotalAmount * rate, 2);
        if (e.QuotationDate == default) e.QuotationDate = DateTime.Today;
        e.ValidUntil ??= e.QuotationDate.Date.AddDays(30);
    }
}
