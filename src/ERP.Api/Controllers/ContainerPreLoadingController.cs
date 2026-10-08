using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;

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

    /// <summary>
    /// 新增：写入前先按当前账号重新校验「订柜信息」上游链接（
    /// 存在 / 未删除 / 已审核 + 菜单授权 + 客户数据范围 + 柜号链接不冲突）；
    /// 未填写 <c>BookingId</c> 的历史式单据保持既有行为。失败直接拒绝，不占用单据号流水、不落任何数据。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerPreLoading entity)
    {
        entity.Id = 0;
        await PreLoadingBookingLinkRules.ValidateLinkAsync(Db, entity, CurrentUserId());
        entity.PreLoadingNo = await _noService.GenerateAsync(DocumentType.PreLoading);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Calculate(entity);
        Db.ContainerPreLoadings.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PreLoadingNo }, "预装柜单创建成功"));
    }

    /// <summary>
    /// 修改：先校验「本次提交的」上游链接（含改派到别的订柜信息），不合格直接拒绝且库中
    /// 单据 / 柜号 / 封条号 / 原始明细数量一律保持不变；通过后才写入。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerPreLoading entity)
    {
        var existing = await Db.ContainerPreLoadings.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        await PreLoadingBookingLinkRules.ValidateLinkAsync(Db, entity, CurrentUserId(), id);

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

    /// <summary>
    /// 出运证据时间线（ERP-059，**只读**）：按本预装柜单的显式源记录 Id 取当前有效出运引用（ERP-057），
    /// 与 ERP-058 里程碑证据合成时间线 —— 计划时间（ETD / ETA）与实际事件分开标注，缺失事件显示「无 / 未知」，
    /// 已作废证据只出现在历史视图；不写任何表、不改写本单与出运引用，也不推断任何业务状态。
    /// </summary>
    [HttpGet("{id:long}/shipment-timeline")]
    public async Task<IActionResult> GetShipmentTimeline(
        long id,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
    {
        var entity = await GetOrThrowAsync(id, "预装柜单不存在");
        var detail = await ContainerShipmentTimelineService.GetForSourceAsync(
            Db, ContainerShipmentReferenceRules.SourceTypePreLoading, entity.Id, includeHistory, historyTake);
        return Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            detail, "已按显式源记录返回出运证据时间线（只读：计划与实际分开标注，缺失事件显示「无 / 未知」）"));
    }

    /// <summary>
    /// 提交：置为已提交之前，先按当前账号重新校验上游订柜链接（订柜信息必须仍然存在、未删除、已审核，
    /// 且在当前账号菜单授权与客户数据范围之内）；失败直接拒绝，状态保持不变。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");
        if (GetStatus(entity) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

        await PreLoadingBookingLinkRules.ValidateLinkAsync(Db, entity, CurrentUserId(), entity.Id);

        SetStatus(entity, DocumentStatus.Submitted);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "提交成功"));
    }

    /// <summary>
    /// 审核：同一可串行化事务内先对来源订柜信息行加 UPDLOCK/HOLDLOCK（未链接不加锁），
    /// 再在锁内重新加载本单并校验当前状态与上游链接权威性（来源仍有效 + 范围 + 柜号链接不冲突），
    /// 最后才置为已审核。这样「订柜信息取消」与「本单审核」共用同一把订柜行锁，只能成功其一；
    /// 任一步失败整体回滚，来源 / 本单状态与明细都保持不变（不写库存 / 财务）。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var header = await Db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireBookingLinkLockAsync(header.BookingId);

            // 锁内重新加载本单：订柜取消 / 同订柜并发审核串行化后，后到者能看到先到者已提交的状态。
            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");

            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await PreLoadingBookingLinkRules.ValidateApprovalAsync(Db, entity, CurrentUserId());

            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "审核通过"));
    }


    /// <summary>
    /// 取消：在同一可串行化事务内先对预装柜单行加 UPDLOCK/HOLDLOCK，再校验是否存在
    /// 「以本单为来源、未删除、已审核」的装柜清单；存在则拒绝，与同源装柜清单审核串行化。
    /// 本方法只改状态为已取消，不写库存 / 财务、不改写装柜清单与明细。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquirePreLoadingCancellationLockAsync(id);

            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");

            await ContainerLoadingFulfillmentRules.ValidateSourceCancellationAsync(Db, entity, CurrentUserId());

            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "已取消"));
    }

    /// <summary>
    /// 对预装柜单行加更新锁（UPDLOCK, HOLDLOCK），把同源并发「装柜审核 / 来源取消」串行化在同一事务内；
    /// 非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquirePreLoadingCancellationLockAsync(long preLoadingId)
    {
        if (!Db.Database.IsRelational()) return;
        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                preLoadingId)
            .ToListAsync();
    }

    /// <summary>
    /// 对来源订柜信息行加更新锁（UPDLOCK, HOLDLOCK），把同一订柜下的并发「预装柜审核 / 订柜取消」串行化在
    /// 同一事务内（ERP-353）。未链接（null）或非关系型提供程序（内存库）跳过即可 —— 事务等价无事务，
    /// 判定语义不受影响；来源行不存在时也无需加锁，权威判定会对显式链接 fail closed 拒绝。
    /// </summary>
    private async Task AcquireBookingLinkLockAsync(long? bookingId)
    {
        if (bookingId is not > 0) return;
        if (!Db.Database.IsRelational()) return;

        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                bookingId.Value)
            .ToListAsync();
    }

    private static void Calculate(ContainerPreLoading entity)
    {
        entity.TotalCartons = entity.Details.Sum(d => d.Cartons);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }
}
