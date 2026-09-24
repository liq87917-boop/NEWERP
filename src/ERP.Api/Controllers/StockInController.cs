using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购入库控制器（审核通过后增加库存）
/// <para>ERP-025：审核 / 取消统一经 <see cref="IInventoryService"/> 记账，库存数量与库存流水
/// （<see cref="StockMovement"/>）同步落地，跨单据可追溯；本次改造前已审核的历史单据没有流水，
/// 取消时退化为按明细基础单位原路冲回，保证既有库存台账不被改写。</para>
/// </summary>
[Route("api/stock-ins")]
public class StockInController : DocumentControllerBase<StockIn>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockInController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.StockInNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockIn>>.Success(
            new PagedResult<StockIn> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("入库单不存在");
        return Ok(ApiResponse<StockIn>.Success(entity));
    }

    /// <summary>该单据的库存流水（ERP-025，含红字冲销流水，按发生顺序）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await GetOrThrowAsync(id, "入库单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockInType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockIn entity)
    {
        entity.Id = 0;
        entity.StockInNo = await _noService.GenerateAsync(DocumentType.StockIn);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);
        Db.StockIns.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.StockInNo }, "入库单创建成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockIn entity)
    {
        var existing = await Db.StockIns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("入库单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.StockInDate = entity.StockInDate;
        existing.PurchaseOrderId = entity.PurchaseOrderId;
        existing.SupplierId = entity.SupplierId;
        existing.WarehouseId = entity.WarehouseId;
        existing.Remark = entity.Remark;

        Db.StockInDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.StockInId = id;
            d.CreatedAt = DateTime.Now;
        }
        existing.Details = entity.Details;
        await StockUnitConversion.NormalizeAsync(Db, existing.Details);
        Calculate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "入库单更新成功"));
    }

    /// <summary>审核（按基础单位增加库存并写入库存流水，恰好一次）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.StockIns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("入库单不存在");
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);
        // 幂等护栏：已产生有效流水的单据不允许再次审核（状态被人工改回同样兜住，与 ERP-009 四类单据同一口径）
        if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockInType, entity.Id) > 0)
            throw BusinessException.RuleConflict("该入库单已产生库存流水，不能重复审核");

        foreach (var d in entity.Details.Where(d => !d.IsDeleted))
        {
            // 零数量明细（旧版页面允许留空行）不产生库存变动；负数属于数据错误，直接拒绝
            if (d.Quantity < 0)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的入库数量不能为负数");
            if (d.Quantity == 0) continue;

            var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
            var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                InventoryDocumentHelper.StockInType, entity.Id, entity.StockInNo,
                InventoryMovementType.PurchaseIn, entity.WarehouseId, d.ProductId, product.Code, product.Name,
                product.Spec, product.Unit, entity.StockInDate, MovementRemark(entity));

            // 成本基准：入库单明细没有成本列（采购价按包装单位计价，直接套用会算错基础单位成本），
            // 因此按 InventoryService 既有兜底口径计价（当前加权平均成本，首次入库为 0），不臆造成本单价。
            await _inventory.IncreaseAsync(context, d.Quantity, 0m);
        }

        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "审核通过，库存已更新并写入库存流水"));
    }

    /// <summary>取消：已审核单据按库存流水冲销（无流水的历史单据按基础单位原路冲回）。</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var entity = await Db.StockIns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("入库单不存在");
        var status = GetStatus(entity);
        if (status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("入库单已取消");
        if (status == DocumentStatus.Approved)
            await ReverseStockAsync(entity);
        else if (status is not (DocumentStatus.Pending or DocumentStatus.Submitted))
            throw BusinessException.RuleConflict("当前状态不允许取消");
        SetStatus(entity, DocumentStatus.Cancelled);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已取消，库存已按基础单位冲回"));
    }

    /// <summary>
    /// 冲销已审核入库：优先按库存流水生成红字流水（ERP-025，与 ERP-009 四类单据同一口径，
    /// 重复冲销由流水的 <c>IsReversed</c> 标记兜住）；本次改造前审核的历史单据没有流水，
    /// 退化为按明细基础单位原路冲回，避免库存台账漂移。
    /// </summary>
    private async Task ReverseStockAsync(StockIn entity)
    {
        var reversals = await _inventory.ReverseAsync(InventoryDocumentHelper.StockInType, entity.Id,
            "采购入库单取消冲销");
        if (reversals.Count == 0)
            await ReverseLegacyStockAsync(entity);
    }

    /// <summary>流水备注：带上来源采购订单 Id，便于按流水反查采购执行情况</summary>
    private static string MovementRemark(StockIn entity)
        => entity.PurchaseOrderId is > 0
            ? $"采购入库单审核入库（采购订单 Id {entity.PurchaseOrderId}）"
            : "采购入库单审核入库";

    private static void Calculate(StockIn entity)
    {
        entity.TotalQuantity = entity.Details.Sum(d => d.Quantity);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }

    /// <summary>
    /// 历史单据兜底（ERP-023 之前审核、没有库存流水的入库单）：按明细已持久化的基础单位数量原路冲回。
    /// 有流水的单据一律走 <see cref="ReverseStockAsync"/> 的红字冲销，本方法不会再改动库存金额。
    /// </summary>
    private async Task ReverseLegacyStockAsync(StockIn entity)
    {
        foreach (var d in entity.Details)
        {
            var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId && s.ProductId == d.ProductId)
                ?? throw BusinessException.RuleConflict($"商品 [{d.ProductName}] 库存记录不存在，不能冲销入库");
            if (stock.AvailableQuantity < d.Quantity)
                throw BusinessException.RuleConflict($"商品 [{d.ProductName}] 已有库存不足，不能冲销入库");
            stock.Quantity -= d.Quantity;
            stock.AvailableQuantity -= d.Quantity;
            stock.UpdatedAt = DateTime.Now;
        }
    }
}
