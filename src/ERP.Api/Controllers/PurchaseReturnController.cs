using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购退货控制器（ERP-009，EF 主子表）
/// 业务规则：
/// 1) 可关联来源采购入库单（SourceStockInId / No）做追溯，也允许无来源直接退货；
/// 2) 审核后货物退出仓库：库存减少，出库成本优先取明细成本 → 来源入库流水成本 → 当前加权平均成本，
///    实际采用的成本会回填到明细并持久化到库存流水（估价可追溯）；
/// 3) 库存不足直接拒绝并整单回滚；仅已提交可审核、仅已审核可销审，重复动作一律拒绝。
/// </summary>
[Route("api/inventory/purchase-returns")]
public class PurchaseReturnController : DocumentControllerBase<PurchaseReturn>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public PurchaseReturnController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>分页查询（keyword 匹配单号 / 供应商 / 来源入库单号 / 退货原因）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] long? warehouseId, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (warehouseId.HasValue) source = source.Where(o => o.WarehouseId == warehouseId.Value);
        if (start.HasValue) source = source.Where(o => o.ReturnDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.ReturnDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.ReturnNo.Contains(kw) || o.SupplierName.Contains(kw)
                                       || o.SourceStockInNo.Contains(kw) || o.ReturnReason.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<PurchaseReturn>>.Success(new PagedResult<PurchaseReturn>
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
            ?? throw BusinessException.NotFound("采购退货单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<PurchaseReturn>.Success(entity));
    }

    /// <summary>该单据的库存流水（含红字冲销流水）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await GetOrThrowAsync(id, "采购退货单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.PurchaseReturnType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建（单号由字轨生成；金额与合计由后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PurchaseReturn entity)
    {
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.ReturnNo))
            entity.ReturnNo = await _noService.GenerateAsync(DocumentType.PurchaseReturn);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        entity.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
        entity.SourceStockInNo = await ResolveSourceStockInNoAsync(entity.SourceStockInId, entity.SourceStockInNo);
        ResetDetailIds(entity);
        Normalize(entity);
        Db.PurchaseReturns.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ReturnNo }, "采购退货单创建成功"));
    }

    /// <summary>修改（仅待提交可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] PurchaseReturn entity)
    {
        var existing = await Db.PurchaseReturns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购退货单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.ReturnDate = entity.ReturnDate;
        existing.SupplierId = entity.SupplierId;
        existing.SupplierName = entity.SupplierName;
        existing.WarehouseId = entity.WarehouseId;
        existing.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
        existing.SourceStockInId = entity.SourceStockInId;
        existing.SourceStockInNo = await ResolveSourceStockInNoAsync(entity.SourceStockInId, entity.SourceStockInNo);
        existing.ReturnReason = entity.ReturnReason;
        existing.Remark = entity.Remark;

        Db.PurchaseReturnDetails.RemoveRange(existing.Details);
        entity.ReturnNo = existing.ReturnNo;
        entity.Id = id;
        ResetDetailIds(entity);
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalQuantity = entity.TotalQuantity;
        existing.TotalAmount = entity.TotalAmount;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "采购退货单更新成功"));
    }

    /// <summary>审核：退货出库（库存减少）并写入流水，恰好一次</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.PurchaseReturns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购退货单不存在");
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("仅已提交的采购退货单可审核");
        if (entity.WarehouseId <= 0)
            throw BusinessException.InvalidParameter("退货出库仓库必须填写");

        var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        if (details.Count == 0)
            throw BusinessException.RuleConflict("采购退货单无明细，不能审核");
        if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.PurchaseReturnType, entity.Id) > 0)
            throw BusinessException.RuleConflict("该采购退货单已产生库存流水，不能重复审核");

        entity.SourceStockInNo = await ResolveSourceStockInNoAsync(entity.SourceStockInId, entity.SourceStockInNo);
        Normalize(entity);
        foreach (var d in details)
        {
            if (d.Quantity <= 0)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的退货数量必须大于 0");

            var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
            d.ProductCode = product.Code;
            d.ProductName = product.Name;
            d.Spec = product.Spec;
            d.Unit = product.Unit;

            // 成本口径：明细指定成本 → 来源采购入库流水成本 → 当前加权平均成本（由服务兜底）
            var sourceCost = d.UnitCost > 0
                ? 0m
                : await _inventory.ResolveSourceCostAsync(InventoryDocumentHelper.StockInType,
                    entity.SourceStockInId ?? 0, d.ProductId);
            var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                InventoryDocumentHelper.PurchaseReturnType, entity.Id, entity.ReturnNo,
                InventoryMovementType.PurchaseReturn, entity.WarehouseId, d.ProductId, d.ProductCode, d.ProductName,
                d.Spec, d.Unit, entity.ReturnDate,
                string.IsNullOrWhiteSpace(entity.SourceStockInNo)
                    ? "采购退货出库"
                    : $"采购退货出库（来源入库单 {entity.SourceStockInNo}）");

            var movement = await _inventory.DecreaseAsync(context, d.Quantity,
                d.UnitCost > 0 ? d.UnitCost : sourceCost);
            d.UnitCost = movement.UnitCost;      // 回填实际出库成本，便于对账与复核
        }

        entity.TotalQuantity = details.Sum(d => d.Quantity);
        entity.TotalAmount = InventoryService.RoundAmount(details.Sum(d => d.Amount));
        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "采购退货单已审核，退货已出库"));
    }

    /// <summary>销审：按流水冲销退货出库（重复销审被拒绝）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        var entity = await GetOrThrowAsync(id, "采购退货单不存在");
        if (GetStatus(entity) != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的采购退货单可销审");

        await _inventory.ReverseAsync(InventoryDocumentHelper.PurchaseReturnType, entity.Id, "采购退货单销审冲销");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，退货出库已冲销"));
    }

    /// <summary>取消（已审核需先销审）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var entity = await GetOrThrowAsync(id, "采购退货单不存在");
        if (GetStatus(entity) == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("已审核的采购退货单不能取消，请先销审");
        return await base.Cancel(id);
    }

    private async Task<string> ResolveWarehouseNameAsync(long warehouseId, string fallback)
        => string.IsNullOrWhiteSpace(fallback)
            ? await InventoryDocumentHelper.WarehouseNameAsync(Db, warehouseId)
            : fallback;

    /// <summary>来源采购入库单号：前端只传 Id 时按库存表回填（保证溯源字段可用）</summary>
    private async Task<string> ResolveSourceStockInNoAsync(long? sourceStockInId, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(fallback) || sourceStockInId is null or <= 0) return fallback;
        return await Db.StockIns.AsNoTracking()
            .Where(o => o.Id == sourceStockInId.Value)
            .Select(o => o.StockInNo)
            .FirstOrDefaultAsync() ?? string.Empty;
    }

    /// <summary>行号 / 金额 / 合计统一整理（后端复核，防止前端篡改合计）</summary>
    /// <remarks>只做「计算」，不动明细主键：审核时明细是已跟踪的实体，重置主键会触发 EF 的 key 修改异常。</remarks>
    private static void Normalize(PurchaseReturn entity)
    {
        if (entity.ReturnDate == default) entity.ReturnDate = DateTime.Today;
        var line = 0;
        decimal totalQuantity = 0, totalAmount = 0;
        foreach (var d in entity.Details)
        {
            d.PurchaseReturnId = entity.Id;
            d.ReturnNo = entity.ReturnNo;
            d.SortNo = ++line;
            d.Amount = InventoryService.RoundAmount(d.Quantity * d.UnitPrice);
            totalQuantity += d.Quantity;
            totalAmount += d.Amount;
        }
        entity.TotalQuantity = totalQuantity;
        entity.TotalAmount = InventoryService.RoundAmount(totalAmount);
    }

    /// <summary>明细整体替换时强制作为新行插入（请求体可能带回历史明细 Id，直接保存会被 EF 当成已存在行）</summary>
    private static void ResetDetailIds(PurchaseReturn entity)
    {
        foreach (var d in entity.Details) d.Id = 0;
    }
}

