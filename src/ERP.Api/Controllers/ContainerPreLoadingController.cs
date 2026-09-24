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
/// 预装柜单控制器（ERP-040：提供按持久化订柜引用只读回显外贸与物流跟踪值）
/// </summary>
[Route("api/container/pre-loadings")]
public class ContainerPreLoadingController : DocumentControllerBase<ContainerPreLoading>
{
    private readonly IDocumentNumberService _noService;

    public ContainerPreLoadingController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.PreLoadingNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<ContainerPreLoading>>.Success(
            new PagedResult<ContainerPreLoading> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");
        return Ok(ApiResponse<ContainerPreLoading>.Success(entity));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerPreLoading entity)
    {
        entity.Id = 0;
        entity.PreLoadingNo = await _noService.GenerateAsync(DocumentType.PreLoading);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Calculate(entity);
        Db.ContainerPreLoadings.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PreLoadingNo }, "预装柜单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerPreLoading entity)
    {
        var existing = await Db.ContainerPreLoadings.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.LoadingDate = entity.LoadingDate;
        existing.BookingId = entity.BookingId;
        existing.ContainerNo = entity.ContainerNo;
        existing.SealNo = entity.SealNo;
        existing.Remark = entity.Remark;

        Db.ContainerPreLoadingDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.PreLoadingId = id;
            d.CreatedAt = DateTime.Now;
        }
        existing.Details = entity.Details;
        Calculate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "预装柜单更新成功"));
    }

    /// <summary>
    /// 权威外贸 / 物流跟踪值（ERP-040，**只读**）：按预装柜单持久化的订柜引用
    /// （<c>BookingId</c>）读取订柜信息并原样回显；没有引用（或引用指向的订柜记录已删除）时返回
    /// 「未关联」，所有跟踪字段为未知 —— 不按柜号等自由文本匹配，也不在本单上另存一份跟踪值。
    /// </summary>
    [HttpGet("{id:long}/shipment-tracking")]
    public async Task<IActionResult> GetShipmentTracking(long id)
    {
        var entity = await GetOrThrowAsync(id, "预装柜单不存在");
        var tracking = await ContainerShipmentTrackingService.ResolveForPreLoadingAsync(Db, entity);
        return Ok(ApiResponse<ContainerShipmentTrackingDto>.Success(tracking, "已按持久化订柜引用返回跟踪信息（只读）"));
    }

    private static void Calculate(ContainerPreLoading entity)
    {
        entity.TotalCartons = entity.Details.Sum(d => d.Cartons);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }
}
