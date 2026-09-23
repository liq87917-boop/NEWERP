using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 收货计划控制器
/// </summary>
[Route("api/container/receiving-plans")]
public class ContainerReceivingPlanController : DocumentControllerBase<ContainerReceivingPlan>
{
    private readonly IDocumentNumberService _noService;

    public ContainerReceivingPlanController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.PlanNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<ContainerReceivingPlan>>.Success(
            new PagedResult<ContainerReceivingPlan> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<ContainerReceivingPlan>.Success(await GetOrThrowAsync(id, "收货计划不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerReceivingPlan entity)
    {
        entity.Id = 0;
        entity.PlanNo = await _noService.GenerateAsync(DocumentType.ReceivingPlan);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.ContainerReceivingPlans.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PlanNo }, "收货计划创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerReceivingPlan entity)
    {
        var existing = await GetOrThrowAsync(id, "收货计划不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.PlanDate = entity.PlanDate;
        existing.SupplierId = entity.SupplierId;
        existing.BookingNo = entity.BookingNo;
        existing.ContainerType = entity.ContainerType;
        existing.ContainerNo = entity.ContainerNo;
        existing.ExpectedArrivalDate = entity.ExpectedArrivalDate;
        existing.PortId = entity.PortId;
        existing.Destination = entity.Destination;
        existing.TotalQuantity = entity.TotalQuantity;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "收货计划更新成功"));
    }
}

/// <summary>
/// 订柜信息控制器
/// </summary>
[Route("api/container/bookings")]
public class ContainerBookingController : DocumentControllerBase<ContainerBooking>
{
    private readonly IDocumentNumberService _noService;

    public ContainerBookingController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.BookingNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<ContainerBooking>>.Success(
            new PagedResult<ContainerBooking> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<ContainerBooking>.Success(await GetOrThrowAsync(id, "订柜信息不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerBooking entity)
    {
        entity.Id = 0;
        entity.BookingNo = await _noService.GenerateAsync(DocumentType.ContainerBooking);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.ContainerBookings.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.BookingNo }, "订柜信息创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerBooking entity)
    {
        var existing = await GetOrThrowAsync(id, "订柜信息不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
        existing.BookingDate = entity.BookingDate;
        existing.CustomerId = entity.CustomerId;
        existing.SupplierId = entity.SupplierId;
        existing.ContainerType = entity.ContainerType;
        existing.ShippingCompany = entity.ShippingCompany;
        existing.VoyageNo = entity.VoyageNo;
        existing.SailingDate = entity.SailingDate;
        existing.DeparturePort = entity.DeparturePort;
        existing.DestinationPort = entity.DestinationPort;
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "订柜信息更新成功"));
    }
}
