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
/// 订柜信息控制器（ERP-040：承载外贸与物流跟踪字段，是本套跟踪值的**权威记录**）
/// </summary>
/// <remarks>
/// 跟踪字段只写订柜信息自身：不写费用 / 单证 / 库存，也不调用船公司、海关、货代等外部跟踪系统。
/// 报关行引用复用「其他资料」数据字典（<c>InfoType = CustomsBroker</c>），名称快照由服务端权威写入；
/// 文本与日期一律「未填写 = 未知」，不由任何自由文本推断。
/// </remarks>
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
        // ERP-040：列表补充报关行引用可用性标注（只读，一次查询解析全部引用，不写库）
        await ContainerShipmentTrackingService.AnnotateAsync(Db, items);
        return Ok(ApiResponse<PagedResult<ContainerBooking>>.Success(
            new PagedResult<ContainerBooking> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>订柜信息详情（补充报关行引用可用性标注，不写库）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await GetOrThrowAsync(id, "订柜信息不存在");
        await ContainerShipmentTrackingService.AnnotateAsync(Db, new[] { entity });
        return Ok(ApiResponse<ContainerBooking>.Success(entity));
    }

    /// <summary>报关行下拉选项（只返回未删除、已启用、类型为 CustomsBroker 的字典项；只读不写库）</summary>
    [HttpGet("customs-broker-options")]
    public async Task<IActionResult> GetCustomsBrokerOptions()
    {
        var options = await ContainerShipmentTrackingService.LoadCustomsBrokerOptionsAsync(Db);
        return Ok(ApiResponse<List<OtherInfoOptionDto>>.Success(options));
    }

    /// <summary>
    /// 新增订柜信息（ERP-040：跟踪字段按统一口径校验后落库 —— 出运方式只接受 LCL / FCL / 未指定，
    /// 报关行必须是启用未删除的 CustomsBroker 字典项，查验要求保持三态）
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerBooking entity)
    {
        entity.Id = 0;
        // 先校验：不合格直接拒绝，不占用单据号流水
        await ContainerShipmentTrackingService.ApplyAsync(Db, entity, stored: null);
        entity.BookingNo = await _noService.GenerateAsync(DocumentType.ContainerBooking);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.ContainerBookings.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.BookingNo }, "订柜信息创建成功"));
    }

    /// <summary>
    /// 更新订柜信息（ERP-040：跟踪字段先校验 / 规范化再写入；报关行引用未变更时保留历史引用，
    /// 字典项后来停用 / 删除也不清空、不改写已有名称快照）
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerBooking entity)
    {
        var existing = await GetOrThrowAsync(id, "订柜信息不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        // 先校验：不合格直接拒绝（库中记录保持原样，不产生半更新）
        await ContainerShipmentTrackingService.ApplyAsync(Db, entity, existing);

        existing.BookingDate = entity.BookingDate;
        existing.CustomerId = entity.CustomerId;
        existing.SupplierId = entity.SupplierId;
        existing.ContainerType = entity.ContainerType;
        existing.ShippingCompany = entity.ShippingCompany;
        existing.VoyageNo = entity.VoyageNo;
        existing.SailingDate = entity.SailingDate;
        existing.DeparturePort = entity.DeparturePort;
        existing.DestinationPort = entity.DestinationPort;
        CopyTrackingFields(entity, existing);
        existing.Remark = entity.Remark;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "订柜信息更新成功"));
    }

    /// <summary>
    /// 把（已校验 / 已规范化的）ERP-040 跟踪字段复制到库中实体。
    /// 只覆盖这些列：不影响订柜信息的其他字段，也不触达费用 / 单证 / 库存等其他单据。
    /// </summary>
    private static void CopyTrackingFields(ContainerBooking source, ContainerBooking target)
    {
        target.ShipmentMode = source.ShipmentMode;
        target.BillOfLadingNo = source.BillOfLadingNo;
        target.ShippingOrderNo = source.ShippingOrderNo;
        target.TransitPort = source.TransitPort;
        target.Etd = source.Etd;
        target.Eta = source.Eta;
        target.Atd = source.Atd;
        target.Ata = source.Ata;
        target.TruckerName = source.TruckerName;
        target.CustomsBrokerId = source.CustomsBrokerId;
        target.CustomsBrokerName = source.CustomsBrokerName;
        target.CustomsBrokerAvailable = source.CustomsBrokerAvailable;
        target.InspectionRequired = source.InspectionRequired;
        target.InspectionDate = source.InspectionDate;
        target.CustomsReleaseDate = source.CustomsReleaseDate;
    }
}
