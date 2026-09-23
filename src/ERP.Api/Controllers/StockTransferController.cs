using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 仓库调拨控制器（ERP-009，EF 主子表）
/// 业务规则：
/// 1) 调出仓与调入仓必须不同；仅已提交可审核，仅已审核可销审；
/// 2) 审核后调出仓减少、调入仓增加，两侧<b>使用同一成本单价</b>，因此数量与金额双向守恒；
/// 3) 调出仓库存不足直接拒绝（整单不落库，不出现半截流水）；
/// 4) 销审按流水冲销：调入仓退回、调出仓还原；调入仓货物已被消耗时拒绝销审。
/// </summary>
[Route("api/inventory/stock-transfers")]
public class StockTransferController : DocumentControllerBase<StockTransfer>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockTransferController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>分页查询（keyword 匹配单号 / 调出仓 / 调入仓 / 备注）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] long? fromWarehouseId, [FromQuery] long? toWarehouseId,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (fromWarehouseId.HasValue) source = source.Where(o => o.FromWarehouseId == fromWarehouseId.Value);
        if (toWarehouseId.HasValue) source = source.Where(o => o.ToWarehouseId == toWarehouseId.Value);
        if (start.HasValue) source = source.Where(o => o.TransferDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.TransferDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.TransferNo.Contains(kw) || o.FromWarehouseName.Contains(kw)
                                       || o.ToWarehouseName.Contains(kw) || o.Remark.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockTransfer>>.Success(new PagedResult<StockTransfer>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        }));
    }

    /// <summary>查询详情（含明细）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("调拨单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<StockTransfer>.Success(entity));
    }

    /// <summary>该单据的库存流水（含调出 / 调入与红字冲销流水）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await GetOrThrowAsync(id, "调拨单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockTransferType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建（单号由字轨生成；金额与合计由后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockTransfer entity)
    {
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.TransferNo))
            entity.TransferNo = await _noService.GenerateAsync(DocumentType.StockTransfer);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        await ResolveWarehouseNamesAsync(entity);
        ResetDetailIds(entity);
        Normalize(entity);
        Db.StockTransfers.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.TransferNo }, "调拨单创建成功"));
    }

    /// <summary>修改（仅待提交可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockTransfer entity)
    {
        var existing = await Db.StockTransfers.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("调拨单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.TransferDate = entity.TransferDate;
        existing.FromWarehouseId = entity.FromWarehouseId;
        existing.ToWarehouseId = entity.ToWarehouseId;
        await ResolveWarehouseNamesAsync(entity);
        existing.FromWarehouseName = entity.FromWarehouseName;
        existing.ToWarehouseName = entity.ToWarehouseName;
        existing.Remark = entity.Remark;

        Db.StockTransferDetails.RemoveRange(existing.Details);
        entity.TransferNo = existing.TransferNo;
        entity.Id = id;
        ResetDetailIds(entity);
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalQuantity = entity.TotalQuantity;
        existing.TotalAmount = entity.TotalAmount;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "调拨单更新成功"));
    }

    /// <summary>审核：调出仓扣减、调入仓增加（同一成本单价），总量守恒恰好一次</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.StockTransfers.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("调拨单不存在");
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("仅已提交的调拨单可审核");
        if (entity.FromWarehouseId <= 0 || entity.ToWarehouseId <= 0)
            throw BusinessException.InvalidParameter("调出仓与调入仓都必须填写");
        if (entity.FromWarehouseId == entity.ToWarehouseId)
            throw BusinessException.RuleConflict("调出仓与调入仓不能相同");

        var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        if (details.Count == 0)
            throw BusinessException.RuleConflict("调拨单无明细，不能审核");
        if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockTransferType, entity.Id) > 0)
            throw BusinessException.RuleConflict("该调拨单已产生库存流水，不能重复审核");

        Normalize(entity);
        decimal totalQuantity = 0, totalAmount = 0;
        foreach (var d in details)
        {
            if (d.Quantity <= 0)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的调拨数量必须大于 0");

            var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
            d.ProductCode = product.Code;
            d.ProductName = product.Name;
            d.Spec = product.Spec;
            d.Unit = product.Unit;

            // 调出：成本基准取调出仓当前加权平均成本（或明细指定成本）
            var outContext = await InventoryDocumentHelper.BuildContextAsync(Db,
                InventoryDocumentHelper.StockTransferType, entity.Id, entity.TransferNo,
                InventoryMovementType.TransferOut, entity.FromWarehouseId, d.ProductId, d.ProductCode,
                d.ProductName, d.Spec, d.Unit, entity.TransferDate,
                $"调拨出库 → {entity.ToWarehouseName}");
            var outMovement = await _inventory.DecreaseAsync(outContext, d.Quantity, d.UnitCost);

            // 调入：沿用同一成本单价，保证「调出金额 = 调入金额」，总量与总金额均守恒
            var inContext = await InventoryDocumentHelper.BuildContextAsync(Db,
                InventoryDocumentHelper.StockTransferType, entity.Id, entity.TransferNo,
                InventoryMovementType.TransferIn, entity.ToWarehouseId, d.ProductId, d.ProductCode,
                d.ProductName, d.Spec, d.Unit, entity.TransferDate,
                $"调拨入库 ← {entity.FromWarehouseName}");
            await _inventory.IncreaseAsync(inContext, d.Quantity, outMovement.UnitCost);

            d.UnitCost = outMovement.UnitCost;
            d.Amount = Math.Abs(outMovement.Amount);
            totalQuantity += d.Quantity;
            totalAmount += d.Amount;
        }

        entity.TotalQuantity = totalQuantity;
        entity.TotalAmount = InventoryService.RoundAmount(totalAmount);
        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "调拨单已审核，库存已在两仓之间移动"));
    }

    /// <summary>销审：按流水冲销（调入仓退回、调出仓还原），重复销审被拒绝</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        var entity = await GetOrThrowAsync(id, "调拨单不存在");
        if (GetStatus(entity) != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的调拨单可销审");

        await _inventory.ReverseAsync(InventoryDocumentHelper.StockTransferType, entity.Id, "调拨单销审冲销");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，两仓库存已冲销还原"));
    }

    /// <summary>取消（已审核需先销审）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var entity = await GetOrThrowAsync(id, "调拨单不存在");
        if (GetStatus(entity) == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("已审核的调拨单不能取消，请先销审");
        return await base.Cancel(id);
    }

    /// <summary>仓库名称：前端未带出时按 Id 解析（列表与流水始终可读）</summary>
    private async Task ResolveWarehouseNamesAsync(StockTransfer entity)
    {
        if (string.IsNullOrWhiteSpace(entity.FromWarehouseName))
            entity.FromWarehouseName = await InventoryDocumentHelper.WarehouseNameAsync(Db, entity.FromWarehouseId);
        if (string.IsNullOrWhiteSpace(entity.ToWarehouseName))
            entity.ToWarehouseName = await InventoryDocumentHelper.WarehouseNameAsync(Db, entity.ToWarehouseId);
    }

    /// <summary>行号 / 金额 / 合计统一整理（后端复核，防止前端篡改合计）</summary>
    /// <remarks>只做「计算」，不动明细主键：审核时明细是已跟踪的实体，重置主键会触发 EF 的 key 修改异常。</remarks>
    private static void Normalize(StockTransfer entity)
    {
        if (entity.TransferDate == default) entity.TransferDate = DateTime.Today;
        var line = 0;
        decimal totalQuantity = 0, totalAmount = 0;
        foreach (var d in entity.Details)
        {
            d.StockTransferId = entity.Id;
            d.TransferNo = entity.TransferNo;
            d.SortNo = ++line;
            d.Amount = InventoryService.RoundAmount(d.Quantity * d.UnitCost);
            totalQuantity += d.Quantity;
            totalAmount += d.Amount;
        }
        entity.TotalQuantity = totalQuantity;
        entity.TotalAmount = InventoryService.RoundAmount(totalAmount);
    }

    /// <summary>明细整体替换时强制作为新行插入（请求体可能带回历史明细 Id，直接保存会被 EF 当成已存在行）</summary>
    private static void ResetDetailIds(StockTransfer entity)
    {
        foreach (var d in entity.Details) d.Id = 0;
    }
}

