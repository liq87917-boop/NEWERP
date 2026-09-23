using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 付款单控制器
/// </summary>
[Route("api/finance/payments")]
public class FinancePaymentController : DocumentControllerBase<FinancePayment>
{
    private readonly IDocumentNumberService _noService;

    public FinancePaymentController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.PaymentNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinancePayment>>.Success(
            new PagedResult<FinancePayment> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinancePayment>.Success(await GetOrThrowAsync(id, "付款单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinancePayment entity)
    {
        entity.Id = 0;
        entity.PaymentNo = await _noService.GenerateAsync(DocumentType.Payment);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinancePayments.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PaymentNo }, "付款单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinancePayment entity)
    {
        var existing = await GetOrThrowAsync(id, "付款单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.PaymentDate = entity.PaymentDate;
        existing.SupplierId = entity.SupplierId;
        existing.PaymentApplyId = entity.PaymentApplyId;
        existing.Amount = entity.Amount;
        existing.Currency = entity.Currency;
        existing.PaymentMethod = entity.PaymentMethod;
        existing.BankAccount = entity.BankAccount;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "付款单更新成功"));
    }
}
