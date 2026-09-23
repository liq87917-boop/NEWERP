using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜结算单控制器
/// </summary>
[Route("api/finance/container-settlements")]
public class FinanceContainerSettlementController : DocumentControllerBase<FinanceContainerSettlement>
{
    private readonly IDocumentNumberService _noService;

    public FinanceContainerSettlementController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.SettlementNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceContainerSettlement>>.Success(
            new PagedResult<FinanceContainerSettlement> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinanceContainerSettlement>.Success(await GetOrThrowAsync(id, "装柜结算单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceContainerSettlement entity)
    {
        entity.Id = 0;
        entity.SettlementNo = await _noService.GenerateAsync(DocumentType.ContainerSettlement);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceContainerSettlements.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.SettlementNo }, "装柜结算单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceContainerSettlement entity)
    {
        var existing = await GetOrThrowAsync(id, "装柜结算单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.SettlementDate = entity.SettlementDate;
        existing.LoadingListId = entity.LoadingListId;
        existing.CustomerId = entity.CustomerId;
        existing.TotalAmount = entity.TotalAmount;
        existing.FreightCost = entity.FreightCost;
        existing.OtherCost = entity.OtherCost;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "装柜结算单更新成功"));
    }
}

/// <summary>
/// 散货结算单控制器
/// </summary>
[Route("api/finance/bulk-settlements")]
public class FinanceBulkSettlementController : DocumentControllerBase<FinanceBulkSettlement>
{
    private readonly IDocumentNumberService _noService;

    public FinanceBulkSettlementController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.SettlementNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceBulkSettlement>>.Success(
            new PagedResult<FinanceBulkSettlement> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinanceBulkSettlement>.Success(await GetOrThrowAsync(id, "散货结算单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceBulkSettlement entity)
    {
        entity.Id = 0;
        entity.SettlementNo = await _noService.GenerateAsync(DocumentType.BulkSettlement);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceBulkSettlements.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.SettlementNo }, "散货结算单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceBulkSettlement entity)
    {
        var existing = await GetOrThrowAsync(id, "散货结算单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.SettlementDate = entity.SettlementDate;
        existing.CustomerId = entity.CustomerId;
        existing.TotalAmount = entity.TotalAmount;
        existing.FreightCost = entity.FreightCost;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "散货结算单更新成功"));
    }
}
