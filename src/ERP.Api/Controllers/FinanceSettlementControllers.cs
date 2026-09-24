using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
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

    /// <summary>
    /// 费用分摊证据（ERP-060，**只读**）：本单持久化字段（结算总金额 / 海运费 / 其他费用 / 客户）的只读回显
    /// + 本单显式关联装柜清单上 ERP-042 分摊批次 / 分摊行的证据（按「币种 → 客户」分组、含未分摊参考与
    /// 已作废历史）。分摊证据**不参与**结算金额计算，也**不会**被写入本单任何字段；金额对照只作算术证据，
    /// 不是结算差异、应收应付或对账结论。未关联装柜清单时证据显示「未知」，不按柜号或客户推断。
    /// </summary>
    [HttpGet("{id:long}/expense-allocation-evidence")]
    public async Task<IActionResult> GetExpenseAllocationEvidence(
        long id,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
    {
        var entity = await GetOrThrowAsync(id, "装柜结算单不存在");
        var evidence = await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(
            Db, entity.Id, includeHistory, historyTake);
        return Ok(ApiResponse<ContainerSettlementAllocationEvidenceDto>.Success(
            evidence, "已按显式装柜结算单返回分摊证据（只读：结算金额字段为原值回显，分摊证据不参与结算计算）"));
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
