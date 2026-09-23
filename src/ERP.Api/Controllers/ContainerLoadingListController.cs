using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜清单控制器
/// </summary>
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
        return Ok(ApiResponse<PagedResult<ContainerLoadingList>>.Success(
            new PagedResult<ContainerLoadingList> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜清单不存在");
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
    /// 带入单证预填（ERP-019）：按装柜清单返回**未落库**的单证草稿（装箱单 / 提单 / 报关单 / 订舱确认），
    /// 柜号写入「关联柜号 / 订舱号」，港口按「预装柜单 → 订柜信息 → 客户档案」回退，
    /// 并回传该柜 / 该清单已生成过的单证类型（前端置灰，避免重复生成）。不写库、不占用单证编号流水。
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

        var result = await TradeDocumentGeneration.PrefillAsync(Db,
            TradeDocumentGeneration.LoadingListSourceType, list.Id, list.LoadingListNo,
            containerNo: list.ContainerNo, salesOrderNo: null, loadingListNo: list.LoadingListNo, drafts: drafts);

        return Ok(ApiResponse<TradeDocPrefillResult>.Success(result, "已按装柜清单带入单证草稿"));
    }

    /// <summary>
    /// 生成单证（ERP-019）：按装柜清单生成单证中心台账记录（默认装箱单）。
    /// 守卫：已作废清单拒绝；同一柜号（或同一装柜清单）+ 同一单证类型只允许一张，
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
        var result = await TradeDocumentGeneration.GenerateAsync(Db,
            TradeDocumentGeneration.LoadingListSourceType, list.Id, list.LoadingListNo,
            containerNo: list.ContainerNo, salesOrderNo: null, loadingListNo: list.LoadingListNo,
            requestedDocTypes: request?.DocTypes,
            buildDraft: docType => TradeDocumentGeneration.BuildFromLoadingList(list, customer, booking, docType));

        var numbers = string.Join("、", result.Documents.Select(d => d.DocNo));
        return Ok(ApiResponse<TradeDocGenerateResult>.Success(result, $"已生成单证：{numbers}"));
    }

    private static void Calculate(ContainerLoadingList entity)
    {
        entity.TotalCartons = entity.Details.Sum(d => d.Cartons);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }
}
