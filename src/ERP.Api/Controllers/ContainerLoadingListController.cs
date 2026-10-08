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
/// 装柜清单控制器（ERP-040：提供按持久化引用链只读回显外贸与物流跟踪值；
/// ERP-041：提供一柜多客户的参与方维护与有界只读视图）
/// </summary>
/// <remarks>
/// 参与方边界（ERP-041）：只读写装柜清单的参与方子表与兼容客户字段 ——
/// 不按数量 / 体积 / 金额分摊费用、不生成费用单 / 结算记录，不改写装柜明细、柜号、订柜跟踪值、
/// 单证、库存与客户主数据；生产库结构变更仍由 Human Gate 控制（建表 / 索引由 SchemaUpgrader 幂等补齐）。
/// </remarks>
[Route("api/container/loading-lists")]
public class ContainerLoadingListController : DocumentControllerBase<ContainerLoadingList>
{
    private readonly IDocumentNumberService _noService;

    public ContainerLoadingListController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        // ERP-364：列表计数 / 分页之前先实时授权，并把权威客户范围（含有效参与方与显式上游客户）下推到 SQL 侧。
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = LoadingListAuthorizationRules.ApplyScope(
            Set.AsNoTracking().Where(o => !o.IsDeleted), Db, scope);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.LoadingListNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        // ERP-041：为本页装柜清单补写参与方计数 / 主参与方 / 历史单客户视图标注（一次批量查询，无逐行查询）
        await ContainerLoadingParticipantService.AnnotateAsync(Db, items);
        return Ok(ApiResponse<PagedResult<ContainerLoadingList>>.Success(
            new PagedResult<ContainerLoadingList> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        // ERP-364：详情读取之前先实时授权并复核该单（含有效参与方与显式上游客户）的权威客户范围。
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details).Include(o => o.Participants)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

        // ERP-041：只读标注（不写库）—— 参与方计数 / 主参与方 / 历史单客户视图，以及每个参与方的客户可用性
        await ContainerLoadingParticipantService.AnnotateAsync(Db, new[] { entity });
        await ContainerLoadingParticipantService.AnnotateParticipantsAsync(Db, entity.Participants);
        return Ok(ApiResponse<ContainerLoadingList>.Success(entity));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerLoadingList entity)
    {
        // ERP-364：单据号生成之前先实时授权，并校验「拟议」权威客户范围（兼容客户字段 + 显式上游共享出运客户）。
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        entity.Id = 0;
        await LoadingListAuthorizationRules.EnsureProposedScopeAllowedAsync(
            Db, scope, entity, entity.CustomerId, entity.PreLoadingId);
        entity.LoadingListNo = await _noService.GenerateAsync(DocumentType.LoadingList);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        await ContainerLoadingFulfillmentRules.ValidateLinkAsync(Db, entity, CurrentUserId());
        // ERP-366：在写入任何明细之前校验显式出运证据链接（来源已审核且未删除、商品 / 基础单位一致、客户在范围内）。
        await LoadingStockOutLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());
        Calculate(entity);
        Db.ContainerLoadingLists.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.LoadingListNo }, "装柜清单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerLoadingList entity)
    {
        // ERP-364：修改之前先实时授权 + 复核「已存储」范围；字段 / 明细写入包在可串行化事务与行锁内。
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        var existing = await Db.ContainerLoadingLists.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, existing);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, existing.PreLoadingId);

            // 在任何字段 / 明细被改写之前，先校验「拟议」完整范围（有效参与方 + 拟议兼容客户 + 显式上游客户）。
            await LoadingListAuthorizationRules.EnsureProposedScopeAllowedAsync(
                Db, scope, existing, entity.CustomerId, entity.PreLoadingId);
            await ContainerLoadingFulfillmentRules.ValidateLinkAsync(Db, entity, CurrentUserId());
            // ERP-366：拟议明细的显式出运证据链接必须整体有效，否则在替换任何明细之前 fail closed。
            entity.Id = id;
            await LoadingStockOutLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());

            existing.PreLoadingId = entity.PreLoadingId;
            existing.LoadingDate = entity.LoadingDate;
            existing.ContainerNo = entity.ContainerNo;
            existing.CustomerId = entity.CustomerId;
            existing.ShippingMark = entity.ShippingMark;
            existing.Remark = entity.Remark;

            // ERP-373：未携带明细 = 仅更新主表 —— 保留既有明细与其显式出运证据链接
            // （SourceStockOutDetailId），绝不因主表编辑而静默清空出运证据。
            // 携带明细 = 既有「整体替换」语义（替换前已由 ValidateLinksAsync 统一校验）。
            if (entity.Details is { Count: > 0 })
            {
                Db.ContainerLoadingDetails.RemoveRange(existing.Details);
                foreach (var d in entity.Details)
                {
                    d.Id = 0;
                    d.LoadingListId = id;
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

        return Ok(ApiResponse<object>.Success(null, "装柜清单更新成功"));
    }

    /// <summary>
    /// 带入单证预填（ERP-019；ERP-052 增加来源明细行快照预览）：按装柜清单返回**未落库**的单证草稿
    /// （装箱单 / 提单 / 报关单 / 订舱确认），柜号写入「关联柜号 / 订舱号」，港口按
    /// 「预装柜单 → 订柜信息 → 客户档案」回退，并回传该柜 / 该清单已生成过的单证类型（前端置灰）。
    /// 不写库、不占用单证编号流水。
    /// <para>明细行只取清单明细确有证据的值（商品 / 数量 / 箱数 / 毛重）：净重来源没有该列一律留空，
    /// 清单以 0 表示未登记 <strong>不</strong>写成 0；清单明细超过有界行数时明确拒绝。</para>
    /// </summary>
    [HttpGet("{id:long}/trade-documents/prefill")]
    public async Task<IActionResult> TradeDocumentPrefill(long id)
    {
        var list = await GetOrThrowAsync(id, "装柜清单不存在");
        var customer = await TradeDocumentGeneration.LoadCustomerAsync(Db, list.CustomerId);
        var booking = await TradeDocumentGeneration.LoadBookingAsync(Db, list);
        var drafts = TradeDocumentGeneration.LoadingListDocTypes
            .Select(docType => TradeDocumentGeneration.BuildFromLoadingList(list, customer, booking, docType))
            .ToList();

        // 来源明细与商品资料各**一次**有界查询（不逐行查库），供明细行快照预览使用
        var details = await TradeDocumentLineSnapshotRules.LoadLoadingListDetailsAsync(Db, list.Id);
        var products = await TradeDocumentLineSnapshotRules.LoadProductsAsync(
            Db, TradeDocumentLineSnapshotRules.ProductIdsOf(details));

        var result = await TradeDocumentGeneration.PrefillAsync(Db,
            TradeDocumentGeneration.LoadingListSourceType, list.Id, list.LoadingListNo,
            containerNo: list.ContainerNo, salesOrderNo: null, loadingListNo: list.LoadingListNo, drafts: drafts,
            buildLines: docType => TradeDocumentLineSnapshotRules.BuildFromLoadingList(
                list, details, products, docType, DraftCurrencyOf(drafts, docType)));

        return Ok(ApiResponse<TradeDocPrefillResult>.Success(result, "已按装柜清单带入单证草稿与明细行快照预览"));
    }

    /// <summary>
    /// 生成单证（ERP-019；ERP-052 增加明细行快照）：按装柜清单生成单证中心台账记录（默认装箱单），
    /// 并在**同一事务**内写入由清单明细构造的装箱单行快照（数量 / 箱数 / 毛重；净重与价格口径不含）。
    /// 守卫：已作废清单拒绝；来源明细非法 / 超限拒绝；同一柜号（或同一装柜清单）+ 同一单证类型只允许一张，
    /// 重复点击不会产生重复单证；单证落库状态统一为「待制作」，生成后仍可人工修改。
    /// </summary>
    [HttpPost("{id:long}/trade-documents")]
    public async Task<IActionResult> GenerateTradeDocuments(long id, [FromBody] TradeDocGenerateRequest? request)
    {
        var list = await GetOrThrowAsync(id, "装柜清单不存在");
        if (list.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的装柜清单不能生成单证");

        var customer = await TradeDocumentGeneration.LoadCustomerAsync(Db, list.CustomerId);
        var booking = await TradeDocumentGeneration.LoadBookingAsync(Db, list);

        // 单证草稿与明细行快照共用同一份映射：币种取自草稿，保证行快照与单证台账币种一致
        var drafts = TradeDocumentGeneration.LoadingListDocTypes
            .Select(docType => TradeDocumentGeneration.BuildFromLoadingList(list, customer, booking, docType))
            .ToList();
        var details = await TradeDocumentLineSnapshotRules.LoadLoadingListDetailsAsync(Db, list.Id);
        var products = await TradeDocumentLineSnapshotRules.LoadProductsAsync(
            Db, TradeDocumentLineSnapshotRules.ProductIdsOf(details));

        var result = await TradeDocumentGeneration.GenerateAsync(Db,
            TradeDocumentGeneration.LoadingListSourceType, list.Id, list.LoadingListNo,
            containerNo: list.ContainerNo, salesOrderNo: null, loadingListNo: list.LoadingListNo,
            requestedDocTypes: request?.DocTypes,
            buildDraft: docType => TradeDocumentGeneration.BuildFromLoadingList(list, customer, booking, docType),
            buildLines: docType => TradeDocumentLineSnapshotRules.BuildFromLoadingList(
                list, details, products, docType, DraftCurrencyOf(drafts, docType)));

        var numbers = string.Join("、", result.Documents.Select(d => d.DocNo));
        return Ok(ApiResponse<TradeDocGenerateResult>.Success(result,
            $"已生成单证：{numbers}（明细行快照 {result.TotalLineCount} 行）"));
    }

    /// <summary>取某单证类型的草稿币种（明细行快照与单证台账币种保持同一口径；缺失时回退空值由规则层规范化）</summary>
    private static string? DraftCurrencyOf(IEnumerable<TradeDocument> drafts, string docType)
        => drafts.FirstOrDefault(d => string.Equals(d.DocType, docType, StringComparison.Ordinal))?.Currency;

    /// <summary>
    /// 权威外贸 / 物流跟踪值（ERP-040，**只读**）：按装柜清单 → 预装柜单 → 订柜信息的
    /// 持久化引用链读取订柜记录并原样回显；链上任一环缺失即返回「未关联」（跟踪字段未知），
    /// 不按柜号等自由文本兜底匹配，也不在本单上另存一份跟踪值。
    /// </summary>
    [HttpGet("{id:long}/shipment-tracking")]
    public async Task<IActionResult> GetShipmentTracking(long id)
    {
        var entity = await GetOrThrowAsync(id, "装柜清单不存在");
        var tracking = await ContainerShipmentTrackingService.ResolveForLoadingListAsync(Db, entity);
        return Ok(ApiResponse<ContainerShipmentTrackingDto>.Success(tracking, "已按持久化引用链返回跟踪信息（只读）"));
    }

    /// <summary>
    /// 出运证据时间线（ERP-059，**只读**）：按本装柜清单的显式源记录 Id 取当前有效出运引用（ERP-057），
    /// 与 ERP-058 里程碑证据合成时间线 —— 计划时间（ETD / ETA）与实际事件分开标注，缺失事件显示「无 / 未知」，
    /// 已作废证据只出现在历史视图；不写任何表、不改写本单与出运引用，也不推断任何业务状态。
    /// </summary>
    [HttpGet("{id:long}/shipment-timeline")]
    public async Task<IActionResult> GetShipmentTimeline(
        long id,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await GetOrThrowAsync(id, "装柜清单不存在");
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
        var detail = await ContainerShipmentTimelineService.GetForSourceAsync(
            Db, ContainerShipmentReferenceRules.SourceTypeLoadingList, entity.Id, includeHistory, historyTake);
        return Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            detail, "已按显式源记录返回出运证据时间线（只读：计划与实际分开标注，缺失事件显示「无 / 未知」）"));
    }

    /// <summary>
    /// 费用分摊证据（ERP-060，**只读**）：按本装柜清单的显式 Id 读取 ERP-042 已持久化的分摊批次与分摊行 ——
    /// 有效批次条数、按「币种 → 客户」分组的有效分摊金额与分摊基数 / 方法、未分摊参考，
    /// 以及已作废 / 历史异常批次（历史视图）与失效链接标注。不同币种不合并、不换算；缺失证据显示
    /// 「无（未登记任何有效分摊批次）」或「未知」，绝不解释为零费用、已结算、应收、应付或客户对账单。
    /// 本接口不写任何表，也不改写本单、分摊批次 / 分摊行、费用单与结算记录。
    /// </summary>
    [HttpGet("{id:long}/expense-allocation-evidence")]
    public async Task<IActionResult> GetExpenseAllocationEvidence(
        long id,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await GetOrThrowAsync(id, "装柜清单不存在");
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
        var evidence = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(
            Db, entity.Id, includeHistory, historyTake);
        return Ok(ApiResponse<ContainerExpenseAllocationEvidenceDto>.Success(
            evidence, "已按显式装柜清单返回分摊证据（只读：缺失显示「无 / 未知」，不改写任何单据）"));
    }

    // ==================== ERP-041：一柜多客户参与方（客户归属清单） ====================

    /// <summary>
    /// 参与方列表（<b>只读</b>，有界）：默认含停用参与方（历史可读），<paramref name="activeOnly"/> 为真时
    /// 只返回启用中的参与方；每条带客户编码 / 名称快照与客户可用性标注，客户停用 / 删除后仍可读。
    /// </summary>
    [HttpGet("{id:long}/participants")]
    public async Task<IActionResult> GetParticipants(long id, [FromQuery] bool activeOnly = false)
    {
        // ERP-364：参与方读取之前先实时授权并复核权威客户范围（有效参与方 + 显式上游客户）。
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        await EnsureStoredLoadingListScopeAsync(id, scope);
        var rows = await ContainerLoadingParticipantService.ListAsync(Db, id, activeOnly);
        return Ok(ApiResponse<List<ContainerLoadingParticipantDto>>.Success(rows));
    }

    /// <summary>新增参与方（已作废的清单除外；客户必须启用；同一清单同一客户不重复）</summary>
    [HttpPost("{id:long}/participants")]
    public async Task<IActionResult> CreateParticipant(long id, [FromBody] ContainerLoadingParticipantSaveDto dto)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);
            await EnsureStoredLoadingListScopeAsync(id, scope);
            var created = await ContainerLoadingParticipantService.CreateAsync(Db, id, dto, scope);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(created, "参与方新增成功"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>修改参与方（更换客户时重新校验；未变更的客户保留历史编码 / 名称快照）</summary>
    [HttpPut("{id:long}/participants/{participantId:long}")]
    public async Task<IActionResult> UpdateParticipant(
        long id, long participantId, [FromBody] ContainerLoadingParticipantSaveDto dto)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);
            await EnsureStoredLoadingListScopeAsync(id, scope);
            var updated = await ContainerLoadingParticipantService.UpdateAsync(Db, id, participantId, dto, scope);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(updated, "参与方更新成功"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 显式设为主参与方：先释放同清单内旧的启用主参与方，再把清单兼容客户字段同步为该客户
    /// —— 结果与列表顺序无关，任何时刻清单内最多一条启用主参与方。
    /// </summary>
    [HttpPost("{id:long}/participants/{participantId:long}/primary")]
    public async Task<IActionResult> SetPrimaryParticipant(long id, long participantId)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);
            await EnsureStoredLoadingListScopeAsync(id, scope);
            var result = await ContainerLoadingParticipantService.SetPrimaryAsync(Db, id, participantId, scope);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(
                result, "已设为主参与方，并已同步装柜清单的兼容客户字段"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>停用参与方（历史仍可读；停用当前主参与方要求清单不再有其他启用参与方）</summary>
    [HttpPost("{id:long}/participants/{participantId:long}/disable")]
    public async Task<IActionResult> DisableParticipant(long id, long participantId)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);
            await EnsureStoredLoadingListScopeAsync(id, scope);
            var result = await ContainerLoadingParticipantService.SetStatusAsync(
                Db, id, participantId, ContainerLoadingParticipantRules.DisabledStatus, scope);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(result, "参与方已停用"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>重新启用参与方（客户必须仍可用；不会自动恢复主参与方标记）</summary>
    [HttpPost("{id:long}/participants/{participantId:long}/enable")]
    public async Task<IActionResult> EnableParticipant(long id, long participantId)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);
            await EnsureStoredLoadingListScopeAsync(id, scope);
            var result = await ContainerLoadingParticipantService.SetStatusAsync(
                Db, id, participantId, ContainerLoadingParticipantRules.ActiveStatus, scope);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(result, "参与方已启用"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>删除参与方（仅本表软删除，保留历史可读；不改写装柜明细 / 跟踪值 / 单证 / 费用 / 库存）</summary>
    [HttpDelete("{id:long}/participants/{participantId:long}")]
    public async Task<IActionResult> DeleteParticipant(long id, long participantId)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);
            await EnsureStoredLoadingListScopeAsync(id, scope);
            await ContainerLoadingParticipantService.DeleteAsync(Db, id, participantId, scope);
            await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "参与方已删除（历史记录保留）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }


    /// <summary>提交：提交前在同一可串行化事务与行锁内重新解析已审核权威来源并校验当前账号身份 / 菜单 / 权威客户范围。</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);

            var entity = await Db.ContainerLoadingLists.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜清单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
            await ContainerLoadingFulfillmentRules.ValidateLinkAsync(Db, entity, CurrentUserId());
            // ERP-366：提交前复核显式出运证据链接（来源是否仍为已审核且未删除、商品 / 客户是否仍匹配）。
            await LoadingStockOutLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());

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
    /// 审核：把「累计已审核装柜数量 ≤ 预装柜单授权数量」的判定放进同一个可串行化事务，
    /// 并对预装柜单行 + 本装柜清单行加 UPDLOCK/HOLDLOCK 串行化同源并发审核、来源取消与参与方维护；任一步失败整体回滚，状态不变。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var header = await Db.ContainerLoadingLists.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");

        // ERP-366：确定性锁序 —— 上游销售出库行（按 StockOutId 升序）→ 预装柜单行 → 装柜清单行。
        var linkedStockOutIds = await LoadingStockOutLinkRules.LoadLinkedStockOutIdsAsync(Db, header);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireStockOutLinkRowLocksAsync(linkedStockOutIds);
            await AcquirePreLoadingApprovalLockAsync(header.PreLoadingId);
            await AcquireLoadingListRowLockAsync(id);

            // 锁内重新加载本单：同源并发审核 / 来源取消 / 参与方维护串行化后，后到者能看到先到者已提交的状态与数量。
            var entity = await Db.ContainerLoadingLists.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜清单不存在");

            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
            // ERP-348 预装柜单来源 / 累计超装护栏（只判定，不过账库存 / 财务）。
            await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(Db, entity, CurrentUserId());
            // ERP-366 显式出运证据链接与累计容量护栏（只判定，不过账库存 / 财务）。
            await LoadingStockOutLinkRules.ValidateApprovalAsync(Db, entity, CurrentUserId());

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
    /// 取消：在同一可串行化事务与行锁内校验当前账号身份 / 菜单 / 权威客户范围；
    /// 已审核数量由审核累计查询按状态自动释放，不写库存 / 财务。
    /// <para>ERP-367：取消只把状态置为「已取消」—— 装柜明细的显式出运证据链接
    /// （<see cref="ContainerLoadingDetail.SourceStockOutDetailId"/>）与历史数量<b>原样保留</b>
    /// （绝不回填 / 重写 / 删除），来源出库单取消护栏据此在状态不再是「已审核」后即时释放；
    /// 释放后来源出库单仍按既有流程恢复库存并写红字流水。</para>
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);

            var entity = await Db.ContainerLoadingLists.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜清单不存在");

            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
            // ERP-384：存在未删除且未取消的装柜结算单引用本清单时，拒绝取消装柜清单（先取消结算单释放）。
            await FinanceContainerSettlementLifecycleRules.EnsureNoActiveSettlementForLoadingListAsync(Db, id);

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
    /// 删除（软删除，仅待提交状态可删）：在同一可串行化事务与行锁内校验当前账号身份 / 菜单 / 权威客户范围，
    /// 失败回滚且不改写原单字段与审计时间戳。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var preLoadingId = await LoadPreLoadingIdOrThrowAsync(id);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireLoadingListWriteLocksAsync(id, preLoadingId);

            var entity = await Db.ContainerLoadingLists
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜清单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");

            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

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

    // ==================== ERP-366：装柜明细 → 销售出库明细 的显式出运证据链接 ====================

    /// <summary>
    /// 可链接出运证据候选（<b>只读、有界</b>）：返回属于本装柜清单权威客户范围、「已审核、未删除」的
    /// 销售出库明细，并显式回传父出库单 / 销售订单 / 商品 / 客户与剩余可链接基础单位数量（作为出运证据）。
    /// 复用既有「装柜清单」与「销售出库」菜单授权与实时客户数据范围；不写任何表。
    /// </summary>
    [HttpGet("{id:long}/stock-out-candidates")]
    public async Task<IActionResult> GetStockOutCandidates(
        long id, [FromQuery] string? keyword, [FromQuery] int take = 0)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await GetOrThrowAsync(id, "装柜清单不存在");
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
        // 候选直接暴露销售出库证据：非特权账号必须有既有「销售出库」菜单授权。
        await LoadingStockOutLinkRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentUserId());
        var candidates = await LoadingStockOutLinkRules.QueryCandidatesAsync(Db, entity, scope, keyword, take);
        return Ok(ApiResponse<List<LoadingStockOutCandidateDto>>.Success(
            candidates, "已返回可链接的已审核销售出库证据（只读：缺失即无可用容量，绝不猜测来源）"));
    }

    /// <summary>
    /// ERP-373：回显本装柜清单**全部明细行**的显式出运证据链接状态（只读、有界），供业务界面打开 /
    /// 重新加载出运证据工作台时读取**服务端持久化结果**：未链接（<c>null</c>）是显式历史事实，来源已不可用
    /// （明细删除 / 出库单取消 / 撤销审核）时原链接**原样保留**并显式标注 —— 绝不清除、绝不猜测来源。
    /// 复用既有「装柜清单」菜单授权 + 实时客户数据范围；存在显式链接时额外要求既有「销售出库」菜单授权
    /// （不新增任何用户授权，也不提供匿名 / 管理员降级）。
    /// </summary>
    [HttpGet("{id:long}/stock-out-links")]
    public async Task<IActionResult> GetStockOutLinks(long id)
    {
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

        if (entity.Details.Any(d => !d.IsDeleted && d.SourceStockOutDetailId is > 0))
            await LoadingStockOutLinkRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentUserId());

        var lines = await LoadingStockOutLinkRules.DescribeLinksAsync(Db, entity);
        return Ok(ApiResponse<List<LoadingStockOutLinkLineDto>>.Success(
            lines, "已返回全部装柜明细的显式出运证据链接状态（未链接 = 显式事实，绝不回填猜测）"));
    }

    /// <summary>
    /// 指派 / 清除装柜明细的显式出运证据链接：在改写任何一行之前先统一校验全部拟议链接
    /// （来源已审核且未删除、商品 / 基础单位一致、客户属于权威客户范围、新链接数量为正），
    /// 并在同一可串行化事务内按「上游销售出库行（升序）→ 装柜清单行」确定性锁序提交；任一步失败整体回滚，
    /// 明细 / 状态 / 历史保持不变。
    /// </summary>
    [HttpPost("{id:long}/stock-out-links")]
    public async Task<IActionResult> AssignStockOutLinks(
        long id, [FromBody] LoadingStockOutLinkAssignRequest request)
    {
        request ??= new LoadingStockOutLinkAssignRequest();
        var links = request.Links ?? new List<LoadingStockOutLinkAssignmentDto>();
        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());

        // 确定性锁序：先解析本次拟议链接涉及的上游出库单，再按 Id 升序加锁。
        var proposedSourceDetailIds = links
            .Where(l => l.SourceStockOutDetailId is > 0)
            .Select(l => l.SourceStockOutDetailId!.Value).ToList();
        var lockStockOutIds = await LoadingStockOutLinkRules
            .LoadStockOutIdsBySourceDetailIdsAsync(Db, proposedSourceDetailIds);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireStockOutLinkRowLocksAsync(lockStockOutIds);
            await AcquireLoadingListRowLockAsync(id);

            var entity = await Db.ContainerLoadingLists.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜清单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的装柜清单可维护出运证据链接");

            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);

            var assignments = links
                .GroupBy(l => l.LoadingDetailId)
                .Select(g => g.Last())
                .ToList();
            if (assignments.Count != links.Count)
                throw BusinessException.InvalidParameter("链接指派存在重复的装柜明细行");

            // 先在副本上校验全部拟议链接：被拒绝时不改动库中任何一行。
            var proposed = new ContainerLoadingList
            {
                Id = entity.Id,
                CustomerId = entity.CustomerId,
                PreLoadingId = entity.PreLoadingId,
                Details = entity.Details.Where(d => !d.IsDeleted).Select(d => new ContainerLoadingDetail
                {
                    Id = d.Id,
                    LoadingListId = d.LoadingListId,
                    ProductId = d.ProductId,
                    ProductName = d.ProductName,
                    Quantity = d.Quantity,
                    Cartons = d.Cartons,
                    Weight = d.Weight,
                    Volume = d.Volume,
                    Remark = d.Remark,
                    SourceStockOutDetailId = d.SourceStockOutDetailId
                }).ToList()
            };

            foreach (var assignment in assignments)
            {
                var target = proposed.Details.FirstOrDefault(d => d.Id == assignment.LoadingDetailId)
                    ?? throw BusinessException.NotFound(
                        $"装柜明细 {assignment.LoadingDetailId} 不存在或不属于本装柜清单");
                target.SourceStockOutDetailId = assignment.SourceStockOutDetailId;
            }

            await LoadingStockOutLinkRules.ValidateLinksAsync(Db, proposed, CurrentUserId());

            var linkedCount = 0;
            var clearedCount = 0;
            foreach (var assignment in assignments)
            {
                var target = entity.Details.First(d => d.Id == assignment.LoadingDetailId);
                if (assignment.SourceStockOutDetailId is > 0)
                {
                    if (target.SourceStockOutDetailId != assignment.SourceStockOutDetailId) linkedCount++;
                    target.SourceStockOutDetailId = assignment.SourceStockOutDetailId;
                }
                else
                {
                    if (target.SourceStockOutDetailId is not null) clearedCount++;
                    target.SourceStockOutDetailId = null;
                }
            }

            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();

            var lines = await LoadingStockOutLinkRules.DescribeLinksAsync(Db, entity);
            return Ok(ApiResponse<LoadingStockOutLinkAssignResultDto>.Success(
                new LoadingStockOutLinkAssignResultDto
                {
                    LinkedCount = linkedCount,
                    ClearedCount = clearedCount,
                    Items = lines
                },
                "出运证据链接已更新（历史未链接明细保持显式未链接）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// ERP-366：按 <c>StockOutId</c> 升序对上游销售出库单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>，与销售退货审核 /
    /// 销审 / 来源取消共用同一把来源行锁），把并发「装柜审核 / 链接」与「来源取消 / 退货」串行化在同一事务内。
    /// 确定性锁序固定为「上游销售出库行 → 预装柜单行 → 装柜清单行」，避免锁环；非关系型提供程序跳过。
    /// </summary>
    private async Task AcquireStockOutLinkRowLocksAsync(IEnumerable<long> stockOutIds)
    {
        if (!LoadingStockOutLinkRules.IsRelationalProvider(Db)) return;

        foreach (var stockOutId in stockOutIds.Where(id => id > 0).Distinct().OrderBy(id => id))
        {
            await Db.Database
                .SqlQueryRaw<long>(LoadingStockOutLinkRules.LockStockOutRowSql, stockOutId)
                .ToListAsync();
        }
    }

    /// <summary>
    /// 对来源预装柜单行加更新锁（UPDLOCK, HOLDLOCK），把同源并发「装柜审核 / 来源取消」串行化在同一事务内。
    /// 未链接时无需加锁；非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquirePreLoadingApprovalLockAsync(long? preLoadingId)
    {
        if (preLoadingId is not > 0) return;
        if (!Db.Database.IsRelational()) return;

        // 来源行不存在时无需加锁：ValidateApprovalAsync 会对显式链接 fail closed 抛异常，在取得任何写入前拒绝履约。
        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                preLoadingId.Value)
            .ToListAsync();
    }

    /// <summary>
    /// 对装柜清单行加更新锁（UPDLOCK, HOLDLOCK），把同单并发「修改 / 提交 / 审核 / 取消 / 删除 / 参与方维护」
    /// 串行化在同一事务内；非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquireLoadingListRowLockAsync(long loadingListId)
    {
        if (!Db.Database.IsRelational()) return;

        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                loadingListId)
            .ToListAsync();
    }

    /// <summary>
    /// 按固定锁序「预装柜单行 → 装柜清单行」取得写路由的更新锁：与既有审核（<see cref="AcquirePreLoadingApprovalLockAsync"/>）
    /// 共用同一把预装柜单行锁，并与 ERP-363 上游预装柜写入保持同一锁序，避免锁环。
    /// </summary>
    private async Task AcquireLoadingListWriteLocksAsync(long loadingListId, long? preLoadingId)
    {
        await AcquirePreLoadingApprovalLockAsync(preLoadingId);
        await AcquireLoadingListRowLockAsync(loadingListId);
    }

    /// <summary>读取本单头（用于取 <c>PreLoadingId</c> 以加锁）；单据不存在或已删除时按不存在拒绝。</summary>
    private async Task<long?> LoadPreLoadingIdOrThrowAsync(long id)
    {
        var header = await Db.ContainerLoadingLists.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.PreLoadingId })
            .FirstOrDefaultAsync()
            ?? throw BusinessException.NotFound("装柜清单不存在");
        return header.PreLoadingId;
    }

    /// <summary>锁内重新加载本单并复核当前账号对「已存储」单据的权威客户范围（含有效参与方与显式上游客户）。</summary>
    private async Task EnsureStoredLoadingListScopeAsync(long id, SalespersonDataScope scope)
    {
        var entity = await Db.ContainerLoadingLists
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
    }

    private static void Calculate(ContainerLoadingList entity)
    {
        entity.TotalCartons = entity.Details.Sum(d => d.Cartons);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }
}
