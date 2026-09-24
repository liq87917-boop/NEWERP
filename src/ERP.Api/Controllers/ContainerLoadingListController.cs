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
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
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
        var entity = await Set.AsNoTracking().Include(o => o.Details).Include(o => o.Participants)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");

        // ERP-041：只读标注（不写库）—— 参与方计数 / 主参与方 / 历史单客户视图，以及每个参与方的客户可用性
        await ContainerLoadingParticipantService.AnnotateAsync(Db, new[] { entity });
        await ContainerLoadingParticipantService.AnnotateParticipantsAsync(Db, entity.Participants);
        return Ok(ApiResponse<ContainerLoadingList>.Success(entity));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerLoadingList entity)
    {
        entity.Id = 0;
        entity.LoadingListNo = await _noService.GenerateAsync(DocumentType.LoadingList);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Calculate(entity);
        Db.ContainerLoadingLists.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.LoadingListNo }, "装柜清单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerLoadingList entity)
    {
        var existing = await Db.ContainerLoadingLists.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

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

    // ==================== ERP-041：一柜多客户参与方（客户归属清单） ====================

    /// <summary>
    /// 参与方列表（<b>只读</b>，有界）：默认含停用参与方（历史可读），<paramref name="activeOnly"/> 为真时
    /// 只返回启用中的参与方；每条带客户编码 / 名称快照与客户可用性标注，客户停用 / 删除后仍可读。
    /// </summary>
    [HttpGet("{id:long}/participants")]
    public async Task<IActionResult> GetParticipants(long id, [FromQuery] bool activeOnly = false)
    {
        var rows = await ContainerLoadingParticipantService.ListAsync(Db, id, activeOnly);
        return Ok(ApiResponse<List<ContainerLoadingParticipantDto>>.Success(rows));
    }

    /// <summary>新增参与方（已作废的清单除外；客户必须启用；同一清单同一客户不重复）</summary>
    [HttpPost("{id:long}/participants")]
    public async Task<IActionResult> CreateParticipant(long id, [FromBody] ContainerLoadingParticipantSaveDto dto)
    {
        var created = await ContainerLoadingParticipantService.CreateAsync(Db, id, dto);
        return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(created, "参与方新增成功"));
    }

    /// <summary>修改参与方（更换客户时重新校验；未变更的客户保留历史编码 / 名称快照）</summary>
    [HttpPut("{id:long}/participants/{participantId:long}")]
    public async Task<IActionResult> UpdateParticipant(
        long id, long participantId, [FromBody] ContainerLoadingParticipantSaveDto dto)
    {
        var updated = await ContainerLoadingParticipantService.UpdateAsync(Db, id, participantId, dto);
        return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(updated, "参与方更新成功"));
    }

    /// <summary>
    /// 显式设为主参与方：先释放同清单内旧的启用主参与方，再把清单兼容客户字段同步为该客户
    /// —— 结果与列表顺序无关，任何时刻清单内最多一条启用主参与方。
    /// </summary>
    [HttpPost("{id:long}/participants/{participantId:long}/primary")]
    public async Task<IActionResult> SetPrimaryParticipant(long id, long participantId)
    {
        var result = await ContainerLoadingParticipantService.SetPrimaryAsync(Db, id, participantId);
        return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(
            result, "已设为主参与方，并已同步装柜清单的兼容客户字段"));
    }

    /// <summary>停用参与方（历史仍可读；停用当前主参与方要求清单不再有其他启用参与方）</summary>
    [HttpPost("{id:long}/participants/{participantId:long}/disable")]
    public async Task<IActionResult> DisableParticipant(long id, long participantId)
    {
        var result = await ContainerLoadingParticipantService.SetStatusAsync(
            Db, id, participantId, ContainerLoadingParticipantRules.DisabledStatus);
        return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(result, "参与方已停用"));
    }

    /// <summary>重新启用参与方（客户必须仍可用；不会自动恢复主参与方标记）</summary>
    [HttpPost("{id:long}/participants/{participantId:long}/enable")]
    public async Task<IActionResult> EnableParticipant(long id, long participantId)
    {
        var result = await ContainerLoadingParticipantService.SetStatusAsync(
            Db, id, participantId, ContainerLoadingParticipantRules.ActiveStatus);
        return Ok(ApiResponse<ContainerLoadingParticipantDto>.Success(result, "参与方已启用"));
    }

    /// <summary>删除参与方（仅本表软删除，保留历史可读；不改写装柜明细 / 跟踪值 / 单证 / 费用 / 库存）</summary>
    [HttpDelete("{id:long}/participants/{participantId:long}")]
    public async Task<IActionResult> DeleteParticipant(long id, long participantId)
    {
        await ContainerLoadingParticipantService.DeleteAsync(Db, id, participantId);
        return Ok(ApiResponse<object>.Success(null, "参与方已删除（历史记录保留）"));
    }


    private static void Calculate(ContainerLoadingList entity)
    {
        entity.TotalCartons = entity.Details.Sum(d => d.Cartons);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }
}
