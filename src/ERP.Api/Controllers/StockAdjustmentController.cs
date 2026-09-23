using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 库存盘点/调整控制器（ERP-009，EF 主子表）
/// 业务规则：
/// 1) 草稿 → 提交 → 审核；<b>仅已提交</b>可审核，<b>仅已审核</b>可销审，重复审核/销审一律拒绝；
/// 2) 审核按「实盘 - 账面」差异调整库存（盘盈增加、盘亏减少），每条差异写一笔可审计库存流水；
/// 3) 销审按流水逐笔生成红字流水并还原库存；差异入库已被后续业务占用时拒绝销审（不出现负库存）；
/// 4) 已审核单据不能取消、不能删除，必须先销审。
/// </summary>
[Route("api/inventory/stock-adjustments")]
public class StockAdjustmentController : DocumentControllerBase<StockAdjustment>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockAdjustmentController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>分页查询（keyword 匹配单号 / 仓库名 / 备注）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] long? warehouseId, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (warehouseId.HasValue) source = source.Where(o => o.WarehouseId == warehouseId.Value);
        if (start.HasValue) source = source.Where(o => o.AdjustmentDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.AdjustmentDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.AdjustmentNo.Contains(kw) || o.WarehouseName.Contains(kw)
                                       || o.Remark.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockAdjustment>>.Success(new PagedResult<StockAdjustment>
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
            ?? throw BusinessException.NotFound("盘点单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<StockAdjustment>.Success(entity));
    }

    /// <summary>该单据的库存流水（含红字冲销流水，按发生顺序）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await GetOrThrowAsync(id, "盘点单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockAdjustmentType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建（单号由字轨生成；差异数量与差异金额由后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockAdjustment entity)
    {
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.AdjustmentNo))
            entity.AdjustmentNo = await _noService.GenerateAsync(DocumentType.StockAdjustment);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        entity.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
        ResetDetailIds(entity);
        Normalize(entity);
        Db.StockAdjustments.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.AdjustmentNo }, "盘点单创建成功"));
    }

    /// <summary>修改（仅待提交可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockAdjustment entity)
    {
        var existing = await Db.StockAdjustments.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("盘点单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.AdjustmentDate = entity.AdjustmentDate;
        existing.WarehouseId = entity.WarehouseId;
        existing.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
        existing.AdjustType = entity.AdjustType;
        existing.Remark = entity.Remark;

        Db.StockAdjustmentDetails.RemoveRange(existing.Details);
        entity.AdjustmentNo = existing.AdjustmentNo;
        entity.Id = id;
        ResetDetailIds(entity);
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalDiffQuantity = entity.TotalDiffQuantity;
        existing.TotalDiffAmount = entity.TotalDiffAmount;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "盘点单更新成功"));
    }

    /// <summary>审核：按差异调整库存并写入流水（恰好一次）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.StockAdjustments.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("盘点单不存在");
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("仅已提交的盘点单可审核");

        var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        if (details.Count == 0)
            throw BusinessException.RuleConflict("盘点单无明细，不能审核");
        // 幂等护栏：已存在有效流水说明库存已调整过，禁止重复审核（防止重复改库存）
        if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockAdjustmentType, entity.Id) > 0)
            throw BusinessException.RuleConflict("该盘点单已产生库存流水，不能重复审核");

        Normalize(entity);
        foreach (var d in details.Where(d => d.DiffQuantity != 0))
        {
            var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
            d.ProductCode = product.Code;
            d.ProductName = product.Name;
            d.Spec = product.Spec;
            d.Unit = product.Unit;

            var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                InventoryDocumentHelper.StockAdjustmentType, entity.Id, entity.AdjustmentNo,
                InventoryMovementType.Adjustment, entity.WarehouseId, d.ProductId, d.ProductCode, d.ProductName,
                d.Spec, d.Unit, entity.AdjustmentDate,
                $"盘点差异：账面 {d.BookQuantity}，实盘 {d.ActualQuantity}");

            if (d.DiffQuantity > 0)
                await _inventory.IncreaseAsync(context, d.DiffQuantity, d.UnitCost);
            else
                await _inventory.DecreaseAsync(context, -d.DiffQuantity, d.UnitCost);
        }

        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "盘点单已审核，库存已按差异调整"));
    }

    /// <summary>销审：按流水冲销库存并退回待提交（重复销审被拒绝）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        var entity = await GetOrThrowAsync(id, "盘点单不存在");
        if (GetStatus(entity) != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的盘点单可销审");

        await _inventory.ReverseAsync(InventoryDocumentHelper.StockAdjustmentType, entity.Id, "盘点单销审冲销");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，库存已冲销还原"));
    }

    /// <summary>取消（已审核需先销审）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var entity = await GetOrThrowAsync(id, "盘点单不存在");
        if (GetStatus(entity) == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("已审核的盘点单不能取消，请先销审");
        return await base.Cancel(id);
    }

    private async Task<string> ResolveWarehouseNameAsync(long warehouseId, string fallback)
        => string.IsNullOrWhiteSpace(fallback)
            ? await InventoryDocumentHelper.WarehouseNameAsync(Db, warehouseId)
            : fallback;

    /// <summary>行号 / 差异数量 / 差异金额 / 合计统一整理（后端复核，防止前端篡改）</summary>
    /// <remarks>只做「计算」，不动明细主键：审核时明细是已跟踪的实体，重置主键会触发 EF 的 key 修改异常。</remarks>
    private static void Normalize(StockAdjustment entity)
    {
        if (entity.AdjustmentDate == default) entity.AdjustmentDate = DateTime.Today;
        var line = 0;
        decimal totalQuantity = 0, totalAmount = 0;
        foreach (var d in entity.Details)
        {
            d.StockAdjustmentId = entity.Id;
            d.AdjustmentNo = entity.AdjustmentNo;
            d.SortNo = ++line;
            d.DiffQuantity = d.ActualQuantity - d.BookQuantity;
            d.DiffAmount = InventoryService.RoundAmount(d.DiffQuantity * d.UnitCost);
            totalQuantity += d.DiffQuantity;
            totalAmount += d.DiffAmount;
        }
        entity.TotalDiffQuantity = totalQuantity;
        entity.TotalDiffAmount = InventoryService.RoundAmount(totalAmount);
    }

    /// <summary>明细整体替换时强制作为新行插入（请求体可能带回历史明细 Id，直接保存会被 EF 当成已存在行）</summary>
    private static void ResetDetailIds(StockAdjustment entity)
    {
        foreach (var d in entity.Details) d.Id = 0;
    }
}

