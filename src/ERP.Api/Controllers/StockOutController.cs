using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售出库控制器（审核通过后扣减库存）
/// <para>ERP-025：审核 / 取消统一经 <see cref="IInventoryService"/> 记账，库存数量、库存金额与库存流水
/// （<see cref="StockMovement"/>）同步落地，跨单据可追溯；本次改造前已审核的历史单据没有流水，
/// 取消时退化为按明细基础单位原路恢复，保证既有库存台账不被改写。</para>
/// </summary>
[Route("api/stock-outs")]
public class StockOutController : DocumentControllerBase<StockOut>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockOutController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
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
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.StockOutNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockOut>>.Success(
            new PagedResult<StockOut> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        return Ok(ApiResponse<StockOut>.Success(entity));
    }

    /// <summary>该单据的库存流水（ERP-025，含红字冲销流水，按发生顺序）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await GetOrThrowAsync(id, "出库单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockOutType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockOut entity)
    {
        entity.Id = 0;
        entity.StockOutNo = await _noService.GenerateAsync(DocumentType.StockOut);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);
        Db.StockOuts.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.StockOutNo }, "出库单创建成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockOut entity)
    {
        var existing = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.StockOutDate = entity.StockOutDate;
        existing.SalesOrderId = entity.SalesOrderId;
        existing.CustomerId = entity.CustomerId;
        existing.WarehouseId = entity.WarehouseId;
        existing.Remark = entity.Remark;

        Db.StockOutDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.StockOutId = id;
            d.CreatedAt = DateTime.Now;
        }
        existing.Details = entity.Details;
        await StockUnitConversion.NormalizeAsync(Db, existing.Details);
        Calculate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "出库单更新成功"));
    }

    /// <summary>审核（按基础单位校验库存、扣减并写入库存流水，恰好一次）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);
        // 幂等护栏：已产生有效流水的单据不允许再次审核（状态被人工改回同样兜住，与 ERP-009 四类单据同一口径）
        if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockOutType, entity.Id) > 0)
            throw BusinessException.RuleConflict("该出库单已产生库存流水，不能重复审核");

        foreach (var d in entity.Details.Where(d => !d.IsDeleted))
        {
            // 零数量明细（旧版页面允许留空行）不产生库存变动；负数属于数据错误，直接拒绝
            if (d.Quantity < 0)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的出库数量不能为负数");
            if (d.Quantity == 0) continue;

            var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
            var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                InventoryDocumentHelper.StockOutType, entity.Id, entity.StockOutNo,
                InventoryMovementType.SalesOut, entity.WarehouseId, d.ProductId, product.Code, product.Name,
                product.Spec, product.Unit, entity.StockOutDate, MovementRemark(entity));

            // 出库成本基准：出库单明细没有成本列 → 由 InventoryService 按当前加权平均成本核减
            // （ERP-009 移动加权平均口径）；库存不足时服务抛业务异常，整单不落半截数据。
            await _inventory.DecreaseAsync(context, d.Quantity, 0m);
        }

        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "审核通过，库存已扣减并写入库存流水"));
    }

    /// <summary>取消：已审核单据按库存流水冲销（无流水的历史单据按基础单位原路恢复）。</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var entity = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        var status = GetStatus(entity);
        if (status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("出库单已取消");
        if (status == DocumentStatus.Approved)
            await ReverseStockAsync(entity);
        else if (status is not (DocumentStatus.Pending or DocumentStatus.Submitted))
            throw BusinessException.RuleConflict("当前状态不允许取消");
        SetStatus(entity, DocumentStatus.Cancelled);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已取消，库存已按基础单位恢复"));
    }

    /// <summary>
    /// 冲销已审核出库：优先按库存流水生成红字流水（ERP-025，与 ERP-009 四类单据同一口径，
    /// 重复冲销由流水的 <c>IsReversed</c> 标记兜住）；本次改造前审核的历史单据没有流水，
    /// 退化为按明细基础单位原路恢复，避免库存台账漂移。
    /// </summary>
    private async Task ReverseStockAsync(StockOut entity)
    {
        var reversals = await _inventory.ReverseAsync(InventoryDocumentHelper.StockOutType, entity.Id,
            "销售出库单取消冲销");
        if (reversals.Count == 0)
            await RestoreLegacyStockAsync(entity);
    }

    /// <summary>流水备注：带上来源销售订单 Id，便于按流水反查订单执行情况</summary>
    private static string MovementRemark(StockOut entity)
        => entity.SalesOrderId is > 0
            ? $"销售出库单审核出库（销售订单 Id {entity.SalesOrderId}）"
            : "销售出库单审核出库";

    private static void Calculate(StockOut entity)
    {
        entity.TotalQuantity = entity.Details.Sum(d => d.Quantity);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }

    /// <summary>
    /// 历史单据兜底（ERP-023 之前审核、没有库存流水的出库单）：按明细已持久化的基础单位数量原路恢复。
    /// 有流水的单据一律走 <see cref="ReverseStockAsync"/> 的红字冲销，库存金额由冲销流水精确还原。
    /// </summary>
    private async Task RestoreLegacyStockAsync(StockOut entity)
    {
        foreach (var detail in entity.Details)
        {
            var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId
                && s.ProductId == detail.ProductId);
            if (stock is null)
            {
                stock = new Stock
                {
                    WarehouseId = entity.WarehouseId, ProductId = detail.ProductId,
                    CreatedAt = DateTime.Now
                };
                Db.Stocks.Add(stock);
            }
            stock.Quantity += detail.Quantity;
            stock.AvailableQuantity += detail.Quantity;
            stock.UpdatedAt = DateTime.Now;
        }
    }
}
