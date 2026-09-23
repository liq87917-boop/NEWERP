using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 定金申请单控制器
/// </summary>
[Route("api/finance/deposit-applies")]
public class FinanceDepositApplyController : DocumentControllerBase<FinanceDepositApply>
{
    private readonly IDocumentNumberService _noService;

    public FinanceDepositApplyController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ApplyNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceDepositApply>>.Success(
            new PagedResult<FinanceDepositApply> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinanceDepositApply>.Success(await GetOrThrowAsync(id, "定金申请单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceDepositApply entity)
    {
        entity.Id = 0;
        entity.ApplyNo = await _noService.GenerateAsync(DocumentType.DepositApply);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceDepositApplies.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ApplyNo }, "定金申请单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceDepositApply entity)
    {
        var existing = await GetOrThrowAsync(id, "定金申请单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.ApplyDate = entity.ApplyDate;
        existing.SalesOrderId = entity.SalesOrderId;
        existing.CustomerId = entity.CustomerId;
        existing.Amount = entity.Amount;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.BankAccount = entity.BankAccount;
        existing.Payee = entity.Payee;
        existing.Reason = entity.Reason;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "定金申请单更新成功"));
    }
}

/// <summary>
/// 货款申请单控制器
/// </summary>
[Route("api/finance/payment-applies")]
public class FinancePaymentApplyController : DocumentControllerBase<FinancePaymentApply>
{
    private readonly IDocumentNumberService _noService;

    public FinancePaymentApplyController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ApplyNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinancePaymentApply>>.Success(
            new PagedResult<FinancePaymentApply> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinancePaymentApply>.Success(await GetOrThrowAsync(id, "货款申请单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinancePaymentApply entity)
    {
        entity.Id = 0;
        entity.ApplyNo = await _noService.GenerateAsync(DocumentType.PaymentApply);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinancePaymentApplies.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ApplyNo }, "货款申请单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinancePaymentApply entity)
    {
        var existing = await GetOrThrowAsync(id, "货款申请单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.ApplyDate = entity.ApplyDate;
        existing.SalesOrderId = entity.SalesOrderId;
        existing.CustomerId = entity.CustomerId;
        existing.Amount = entity.Amount;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.BankAccount = entity.BankAccount;
        existing.Payee = entity.Payee;
        existing.Reason = entity.Reason;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "货款申请单更新成功"));
    }
}
