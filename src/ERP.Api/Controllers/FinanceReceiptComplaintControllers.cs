using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 收款单控制器
/// </summary>
[Route("api/finance/receipts")]
public class FinanceReceiptController : DocumentControllerBase<FinanceReceipt>
{
    private readonly IDocumentNumberService _noService;

    public FinanceReceiptController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ReceiptNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceReceipt>>.Success(
            new PagedResult<FinanceReceipt> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinanceReceipt>.Success(await GetOrThrowAsync(id, "收款单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceReceipt entity)
    {
        entity.Id = 0;
        entity.ReceiptNo = await _noService.GenerateAsync(DocumentType.Receipt);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceReceipts.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ReceiptNo }, "收款单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceReceipt entity)
    {
        var existing = await GetOrThrowAsync(id, "收款单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.ReceiptDate = entity.ReceiptDate;
        existing.CustomerId = entity.CustomerId;
        existing.Amount = entity.Amount;
        existing.Currency = entity.Currency;
        existing.PaymentMethod = entity.PaymentMethod;
        existing.BankAccount = entity.BankAccount;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "收款单更新成功"));
    }
}

/// <summary>
/// 客诉单控制器
/// </summary>
[Route("api/finance/complaints")]
public class FinanceComplaintController : DocumentControllerBase<FinanceComplaint>
{
    private readonly IDocumentNumberService _noService;

    public FinanceComplaintController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ComplaintNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceComplaint>>.Success(
            new PagedResult<FinanceComplaint> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinanceComplaint>.Success(await GetOrThrowAsync(id, "客诉单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceComplaint entity)
    {
        entity.Id = 0;
        entity.ComplaintNo = await _noService.GenerateAsync(DocumentType.Complaint);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceComplaints.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ComplaintNo }, "客诉单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceComplaint entity)
    {
        var existing = await GetOrThrowAsync(id, "客诉单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.ComplaintDate = entity.ComplaintDate;
        existing.CustomerId = entity.CustomerId;
        existing.SalesOrderId = entity.SalesOrderId;
        existing.ComplaintType = entity.ComplaintType;
        existing.Description = entity.Description;
        existing.ResponsibleDept = entity.ResponsibleDept;
        existing.HandleResult = entity.HandleResult;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "客诉单更新成功"));
    }
}
