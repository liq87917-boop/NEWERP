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

            existing.PreLoadingId = entity.PreLoadingId;
            existing.LoadingDate = entity.LoadingDate;
            existing.ContainerNo = entity.ContainerNo;
            existing.CustomerId = entity.CustomerId;
            existing.ShippingMark = entity.ShippingMark;
            existing.Remark = entity.Remark;

            Db.ContainerLoadingDetails.RemoveRange(existing.Details);
            foreach (var d in entity.Details)
            {
                d.Id = 0;
                d.LoadingListId = id;
                d.CreatedAt = DateTime.Now;
            }
            existing.Details = entity.Details;
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
        var header = await Db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquirePreLoadingApprovalLockAsync(header.PreLoadingId);
            await AcquireLoadingListRowLockAsync(id);

            // 锁内重新加载本单：同源并发审核 / 来源取消 / 参与方维护串行化后，后到者能看到先到者已提交的状态与数量。
            var entity = await Db.ContainerLoadingLists.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜清单不存在");

            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(Db, scope, entity);
            await ContainerLoadingFulfillmentRules.ValidateApprovalAsync(Db, entity, CurrentUserId());

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
