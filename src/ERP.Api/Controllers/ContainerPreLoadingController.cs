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
/// 预装柜单控制器（ERP-040：按持久化订柜引用只读回显外贸与物流跟踪值；
/// ERP-363：列表 / 详情 / 出运跟踪 / 出运时间线 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除每一个路由
/// 都先解析实时身份、账号状态、既有「预装柜单」菜单授权与权威客户数据范围，且严格先于任何计数、
/// 单据号生成与写入）。
/// </summary>
[Route("api/container/pre-loadings")]
public class ContainerPreLoadingController : DocumentControllerBase<ContainerPreLoading>
{
    private readonly IDocumentNumberService _noService;

    public ContainerPreLoadingController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>列表（ERP-363：授权 / 范围严格先于计数与分页，受限账号范围下推到 SQL）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        // 认证 / 账号状态 / 菜单 / 数据范围先于任何计数与分页（数据库侧范围下推）
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        source = PreLoadingAuthorizationRules.ApplyScope(source, Db, scope);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.PreLoadingNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<ContainerPreLoading>>.Success(
            new PagedResult<ContainerPreLoading> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情（ERP-363：先授权，再按权威来源订柜归属客户复核范围）。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");
        await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
        return Ok(ApiResponse<ContainerPreLoading>.Success(entity));
    }

    /// <summary>
    /// 新增（ERP-363）：授权先于一切；再校验「本次拟议来源范围」（受限账号不得新建无权威归属单据）
    /// 与「订柜信息」上游链接（存在 / 未删除 / 已审核 + 菜单授权 + 客户数据范围 + 柜号链接不冲突）；
    /// 全部通过后才占用单据号流水并落库。任何拒绝都不占流水、不落任何数据。
    /// 特权账号对未填写 <c>BookingId</c> 的历史式单据保持既有行为。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerPreLoading entity)
    {
        entity.Id = 0;
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        await PreLoadingAuthorizationRules.EnsureProposedScopeAllowedAsync(Db, scope, entity);
        await PreLoadingBookingLinkRules.ValidateLinkAsync(Db, entity, CurrentUserId());
        // ERP-368：显式需求计划证据链接在占用流水 / 落库之前统一校验（未链接的历史单据行为不变）。
        await PreLoadingSalesOrderLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());

        entity.PreLoadingNo = await _noService.GenerateAsync(DocumentType.PreLoading);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Calculate(entity);
        Db.ContainerPreLoadings.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PreLoadingNo }, "预装柜单创建成功"));
    }

    /// <summary>
    /// 修改（ERP-363）：同一可串行化事务内先按既有锁序（来源订柜行 → 本单行）加更新锁，再在锁内重新加载本单；
    /// 先校验「库中已存储单据」与「本次提交的拟议单据」的权威客户范围，再校验 ERP-353 上游链接（含改派到别的
    /// 订柜信息）。任一不合格都在替换字段 / 明细之前拒绝，库中单据 / 柜号 / 封条号 / 状态 / 原始明细数量与审计时间戳
    /// 一律保持不变；全部通过后才写入。
    /// <para>ERP-372：请求**未携带明细**（<c>null</c> / 空集合）时视为「仅更新主表」，既有明细与其显式需求计划
    /// 证据链接（<c>SourceSalesOrderDetailId</c>）原样保留 —— 主表编辑绝不静默清空需求来源证据；请求**携带明细**
    /// 时保持既有「整体替换」语义（替换前仍由 ERP-368 统一校验全部拟议链接）。</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerPreLoading entity)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var header = await Db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            // 兼容既有锁序：先来源订柜行（未链接跳过）再本单行，把同源并发修改 / 提交 / 审核 / 取消串行化。
            await AcquireBookingLinkLockAsync(header.BookingId);
            await AcquirePreLoadingRowLockAsync(id);

            var existing = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, existing);
            await PreLoadingAuthorizationRules.EnsureProposedScopeAllowedAsync(Db, scope, entity);
            await PreLoadingBookingLinkRules.ValidateLinkAsync(Db, entity, CurrentUserId(), id);
            // ERP-368：在替换任何字段 / 明细之前统一校验显式需求计划证据链接（失败时库中单据原样保留）。
            await PreLoadingSalesOrderLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());

            existing.LoadingDate = entity.LoadingDate;
            existing.BookingId = entity.BookingId;
            existing.ContainerNo = entity.ContainerNo;
            existing.SealNo = entity.SealNo;
            existing.Remark = entity.Remark;

            // ERP-372：未携带明细 = 仅更新主表 —— 保留既有明细与其显式需求计划证据链接
            // （SourceSalesOrderDetailId），绝不因主表编辑而静默清空需求来源证据。
            // 携带明细 = 既有「整体替换」语义（替换前已由 ValidateLinksAsync 统一校验）。
            if (entity.Details is { Count: > 0 })
            {
                Db.ContainerPreLoadingDetails.RemoveRange(existing.Details);
                foreach (var d in entity.Details)
                {
                    d.Id = 0;
                    d.PreLoadingId = id;
                    d.CreatedAt = DateTime.Now;
                }
                existing.Details = entity.Details;
            }
            Calculate(existing);
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "预装柜单更新成功"));
    }

    /// <summary>
    /// 权威外贸 / 物流跟踪值（ERP-040，**只读**）：先实时授权（ERP-363：实时身份 / 账号状态 / 既有「预装柜单」
    /// 菜单授权 / 权威客户数据范围，与下方出运时间线同口径），再按预装柜单持久化的订柜引用
    /// （<c>BookingId</c>）读取订柜信息并原样回显；没有引用（或引用指向的订柜记录已删除）时返回
    /// 「未关联」，所有跟踪字段为未知 —— 不按柜号等自由文本匹配，也不在本单上另存一份跟踪值。
    /// 范围外 / 已删除 / 不存在单据在授权后返回与出运时间线相同的受控错误，不泄露柜号 / 订柜号 / ETD / ETA / 报关行值。
    /// </summary>
    [HttpGet("{id:long}/shipment-tracking")]
    public async Task<IActionResult> GetShipmentTracking(long id)
    {
        // ERP-363：实时授权与权威客户范围严格先于单据解析与跟踪读取（与出运时间线同一口径，无匿名 / 管理员回退）。
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await GetOrThrowAsync(id, "预装柜单不存在");
        await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
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
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await GetOrThrowAsync(id, "预装柜单不存在");
        await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
        var detail = await ContainerShipmentTimelineService.GetForSourceAsync(
            Db, ContainerShipmentReferenceRules.SourceTypePreLoading, entity.Id, includeHistory, historyTake);
        return Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            detail, "已按显式源记录返回出运证据时间线（只读：计划与实际分开标注，缺失事件显示「无 / 未知」）"));
    }

    /// <summary>
    /// 提交（ERP-363）：同一可串行化事务内先按既有锁序（来源订柜行 → 本单行）加更新锁，再在锁内重新加载本单；
    /// 校验状态、权威客户范围与上游订柜链接（订柜信息必须仍然存在、未删除、已审核，且在范围内）后置为已提交；
    /// 失败直接拒绝并回滚，状态与明细保持不变。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var header = await Db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireBookingLinkLockAsync(header.BookingId);
            await AcquirePreLoadingRowLockAsync(id);

            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
            await PreLoadingBookingLinkRules.ValidateLinkAsync(Db, entity, CurrentUserId(), entity.Id);
            // ERP-368：提交前复核显式需求计划证据链接（失败直接拒绝并回滚，状态与明细保持不变）。
            await PreLoadingSalesOrderLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());

            SetStatus(entity, DocumentStatus.Submitted);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

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
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var header = await Db.ContainerPreLoadings.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        // ERP-368：确定性锁序 —— 上游销售订单行（按 SalesOrderId 升序）→ 订柜信息行 → 预装柜单行。
        var linkedSalesOrderIds = await PreLoadingSalesOrderLinkRules.LoadLinkedSalesOrderIdsAsync(Db, header);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            // 兼容既有锁序：先上游销售订单行，再来源订柜行，最后本单行。
            await AcquireSalesOrderLinkRowLocksAsync(linkedSalesOrderIds);
            await AcquireBookingLinkLockAsync(header.BookingId);
            await AcquirePreLoadingRowLockAsync(id);

            // 锁内重新加载本单：订柜取消 / 同订柜并发审核 / 并发修改串行化后，后到者能看到先到者已提交的状态。
            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");

            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
            await PreLoadingBookingLinkRules.ValidateApprovalAsync(Db, entity, CurrentUserId());
            // ERP-368 显式需求计划证据链接与累计容量护栏（按来源销售订单明细逐条；只判定，不锁库 / 不过账）。
            await PreLoadingSalesOrderLinkRules.ValidateApprovalAsync(Db, entity, CurrentUserId());

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
    /// 取消（ERP-363 / ERP-369）：先实时授权；在同一可串行化事务内按确定性锁序
    /// （上游销售订单行（ERP-368 显式来源链接，SalesOrderId 升序）→ 来源订柜行 → 本单行）加 UPDLOCK/HOLDLOCK，
    /// 再复核权威客户范围并校验是否存在「以本单为来源、未删除、已审核」的装柜清单（ERP-348）；存在则拒绝，
    /// 与同源装柜清单审核串行化。上游订单行锁与 ERP-347 / ERP-369 销售订单取消**共用同一把锁**：
    /// 先提交者决定结果，二者不可能同时成功（取消后来源取消看到有效需求证据即拒绝）。
    /// 本方法只改状态为已取消，不写库存 / 财务、不改写装柜清单与明细，也**不**清除需求计划证据链接
    /// （链接与数量作为历史证据原样保留），解除关系只走既有显式工作流。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var header = await Db.ContainerPreLoadings.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        // ERP-369：取消前解析上游销售订单，按同一确定性锁序先锁来源订单行（与销售订单取消串行化）。
        var linkedSalesOrderIds = await PreLoadingSalesOrderLinkRules.LoadLinkedSalesOrderIdsAsync(Db, header);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireSalesOrderLinkRowLocksAsync(linkedSalesOrderIds);
            await AcquireBookingLinkLockAsync(header.BookingId);
            await AcquirePreLoadingRowLockAsync(id);

            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");

            await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
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
    /// 删除（ERP-363）：软删除仅待提交单据。先实时授权；同一可串行化事务内按既有锁序（来源订柜行 → 本单行）
    /// 加更新锁，锁内复核权威客户范围后再置软删除；失败回滚，单据 / 状态 / 明细与审计保持原样。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var header = await Db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireBookingLinkLockAsync(header.BookingId);
            await AcquirePreLoadingRowLockAsync(id);

            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");

            await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    // ==================== ERP-368：预装柜明细 → 销售订单明细 的显式需求计划证据链接 ====================

    /// <summary>
    /// 可链接需求计划证据候选（<b>只读、有界</b>）：返回本预装柜单权威订柜客户下「已审核、未删除」的
    /// 销售订单明细，并显式回传父销售订单 / 商品 / 客户与剩余可链接基础单位数量。
    /// 复用既有「预装柜单」与「销售订单」菜单授权与实时客户数据范围；不写任何表。
    /// </summary>
    [HttpGet("{id:long}/sales-order-candidates")]
    public async Task<IActionResult> GetSalesOrderCandidates(
        long id, [FromQuery] string? keyword, [FromQuery] int take = 0)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await GetOrThrowAsync(id, "预装柜单不存在");
        await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
        // 候选直接暴露销售订单需求证据：非特权账号必须有既有「销售订单」菜单授权。
        await PreLoadingSalesOrderLinkRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentUserId());
        var candidates = await PreLoadingSalesOrderLinkRules.QueryCandidatesAsync(Db, entity, scope, keyword, take);
        return Ok(ApiResponse<List<PreLoadingSalesOrderCandidateDto>>.Success(
            candidates, "已返回可链接的已审核销售订单需求证据（只读：缺失即无可用容量，绝不猜测来源）"));
    }

    /// <summary>
    /// ERP-372：回显本预装柜单**全部明细行**的显式需求计划证据链接状态（只读、有界），供业务界面打开 /
    /// 重新加载需求来源工作台时读取**服务端持久化结果**：未链接（<c>null</c>）是显式历史事实，来源已不可用
    /// （明细删除 / 订单取消 / 撤销审核）时原链接**原样保留**并显式标注 —— 绝不清除、绝不猜测来源。
    /// 复用既有「预装柜单」菜单授权 + 实时客户数据范围；存在显式链接时额外要求既有「销售订单」菜单授权
    /// （不新增任何用户授权，也不提供匿名 / 管理员降级）。
    /// </summary>
    [HttpGet("{id:long}/sales-order-links")]
    public async Task<IActionResult> GetSalesOrderLinks(long id)
    {
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");
        await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

        if (entity.Details.Any(d => !d.IsDeleted && d.SourceSalesOrderDetailId is > 0))
            await PreLoadingSalesOrderLinkRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentUserId());

        var lines = await PreLoadingSalesOrderLinkRules.DescribeLinksAsync(Db, entity);
        return Ok(ApiResponse<List<PreLoadingSalesOrderLinkLineDto>>.Success(
            lines, "已返回全部预装柜明细的显式需求计划证据链接状态（未链接 = 显式事实，绝不回填猜测）"));
    }

    /// <summary>
    /// 指派 / 清除预装柜明细的显式需求计划证据链接：在改写任何一行之前先统一校验全部拟议链接
    /// （来源订单已审核且未删除、商品 / 基础单位一致、客户等于权威订柜客户、新链接数量为正），
    /// 并在同一可串行化事务内按「上游销售订单行（升序）→ 订柜信息行 → 预装柜单行」确定性锁序提交；
    /// 任一步失败整体回滚，明细 / 状态 / 历史保持不变。
    /// </summary>
    [HttpPost("{id:long}/sales-order-links")]
    public async Task<IActionResult> AssignSalesOrderLinks(
        long id, [FromBody] PreLoadingSalesOrderLinkAssignRequest request)
    {
        request ??= new PreLoadingSalesOrderLinkAssignRequest();
        var links = request.Links ?? new List<PreLoadingSalesOrderLinkAssignmentDto>();
        var scope = await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var header = await Db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("预装柜单不存在");

        // 确定性锁序：先解析本次拟议链接涉及的上游订单，再按 Id 升序加锁。
        var proposedSourceDetailIds = links
            .Where(l => l.SourceSalesOrderDetailId is > 0)
            .Select(l => l.SourceSalesOrderDetailId!.Value).ToList();
        var lockSalesOrderIds = await PreLoadingSalesOrderLinkRules
            .LoadSalesOrderIdsBySourceDetailIdsAsync(Db, proposedSourceDetailIds);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireSalesOrderLinkRowLocksAsync(lockSalesOrderIds);
            await AcquireBookingLinkLockAsync(header.BookingId);
            await AcquirePreLoadingRowLockAsync(id);

            var entity = await Db.ContainerPreLoadings.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("预装柜单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的预装柜单可维护需求计划证据链接");

            await PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

            var assignments = links
                .GroupBy(l => l.PreLoadingDetailId)
                .Select(g => g.Last())
                .ToList();
            if (assignments.Count != links.Count)
                throw BusinessException.InvalidParameter("链接指派存在重复的预装柜明细行");

            // 先在副本上校验全部拟议链接：被拒绝时不改动库中任何一行。
            var proposed = new ContainerPreLoading
            {
                Id = entity.Id,
                BookingId = entity.BookingId,
                Details = entity.Details.Where(d => !d.IsDeleted).Select(d => new ContainerPreLoadingDetail
                {
                    Id = d.Id,
                    PreLoadingId = d.PreLoadingId,
                    ProductId = d.ProductId,
                    ProductName = d.ProductName,
                    Quantity = d.Quantity,
                    Cartons = d.Cartons,
                    Weight = d.Weight,
                    Volume = d.Volume,
                    Remark = d.Remark,
                    SourceSalesOrderDetailId = d.SourceSalesOrderDetailId
                }).ToList()
            };

            foreach (var assignment in assignments)
            {
                var target = proposed.Details.FirstOrDefault(d => d.Id == assignment.PreLoadingDetailId)
                    ?? throw BusinessException.NotFound(
                        $"预装柜明细 {assignment.PreLoadingDetailId} 不存在或不属于本预装柜单");
                target.SourceSalesOrderDetailId = assignment.SourceSalesOrderDetailId;
            }

            await PreLoadingSalesOrderLinkRules.ValidateLinksAsync(Db, proposed, CurrentUserId());

            var linkedCount = 0;
            var clearedCount = 0;
            foreach (var assignment in assignments)
            {
                var target = entity.Details.First(d => d.Id == assignment.PreLoadingDetailId);
                if (assignment.SourceSalesOrderDetailId is > 0)
                {
                    if (target.SourceSalesOrderDetailId != assignment.SourceSalesOrderDetailId) linkedCount++;
                    target.SourceSalesOrderDetailId = assignment.SourceSalesOrderDetailId;
                }
                else
                {
                    if (target.SourceSalesOrderDetailId is not null) clearedCount++;
                    target.SourceSalesOrderDetailId = null;
                }
            }

            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();

            var lines = await PreLoadingSalesOrderLinkRules.DescribeLinksAsync(Db, entity);
            return Ok(ApiResponse<PreLoadingSalesOrderLinkAssignResultDto>.Success(
                new PreLoadingSalesOrderLinkAssignResultDto
                {
                    LinkedCount = linkedCount,
                    ClearedCount = clearedCount,
                    Items = lines
                },
                "需求计划证据链接已更新（历史未链接明细保持显式未链接）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// ERP-368：按 <c>SalesOrderId</c> 升序对上游销售订单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>），
    /// 把并发「预装柜审核 / 链接」与「销售订单取消 / 出库审核」串行化在同一事务内。
    /// 确定性锁序固定为「上游销售订单行 → 订柜信息行 → 预装柜单行」，避免锁环；非关系型提供程序跳过。
    /// </summary>
    private async Task AcquireSalesOrderLinkRowLocksAsync(IEnumerable<long> salesOrderIds)
    {
        if (!PreLoadingSalesOrderLinkRules.IsRelationalProvider(Db)) return;

        foreach (var salesOrderId in salesOrderIds.Where(id => id > 0).Distinct().OrderBy(id => id))
        {
            await Db.Database
                .SqlQueryRaw<long>(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, salesOrderId)
                .ToListAsync();
        }
    }

    /// <summary>
    /// 对预装柜单行加更新锁（UPDLOCK, HOLDLOCK），把同源并发「修改 / 删除 / 提交 / 审核 / 取消」与既有
    /// 「装柜清单审核 / 来源取消」串行化在同一事务内；非关系型提供程序（内存库）无法执行表提示，跳过即可
    /// （事务本身等价无事务）。
    /// </summary>
    private async Task AcquirePreLoadingRowLockAsync(long preLoadingId)
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
