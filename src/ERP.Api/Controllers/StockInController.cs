using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购入库控制器（审核通过后增加库存）
/// </summary>
[Route("api/stock-ins")]
public class StockInController : DocumentControllerBase<StockIn>
{
    private readonly IDocumentNumberService _noService;

    public StockInController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
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

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockIn entity)
    {
        entity.Id = 0;
        entity.StockInNo = await _noService.GenerateAsync(DocumentType.StockIn);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
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
        Calculate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "入库单更新成功"));
    }

    /// <summary>审核（增加库存）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.StockIns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("入库单不存在");
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

        SetStatus(entity, DocumentStatus.Approved);
        await ApplyStockAsync(entity, isIn: true);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "审核通过，库存已更新"));
    }

    private static void Calculate(StockIn entity)
    {
        entity.TotalQuantity = entity.Details.Sum(d => d.Quantity);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }

    private async Task ApplyStockAsync(StockIn entity, bool isIn)
    {
        foreach (var d in entity.Details)
        {
            var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId && s.ProductId == d.ProductId);
            if (stock is null)
            {
                stock = new Stock
                {
                    WarehouseId = entity.WarehouseId, ProductId = d.ProductId,
                    Quantity = 0, AvailableQuantity = 0, LockedQuantity = 0, CreatedAt = DateTime.Now
                };
                Db.Stocks.Add(stock);
            }
            stock.Quantity += isIn ? d.Quantity : -d.Quantity;
            stock.AvailableQuantity += isIn ? d.Quantity : -d.Quantity;
            stock.UpdatedAt = DateTime.Now;
        }
    }
}
