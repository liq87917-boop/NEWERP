using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售出库控制器（审核通过后扣减库存）
/// </summary>
[Route("api/stock-outs")]
public class StockOutController : DocumentControllerBase<StockOut>
{
    private readonly IDocumentNumberService _noService;

    public StockOutController(IErpDbContext db, IDocumentNumberService noService) : base(db)
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

    /// <summary>审核（校验库存并扣减）</summary>
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
        // 校验库存充足
        foreach (var d in entity.Details)
        {
            var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId && s.ProductId == d.ProductId);
            if (stock is null || stock.AvailableQuantity < d.Quantity)
                throw BusinessException.RuleConflict($"商品 [{d.ProductName}] 库存不足");
            stock.AvailableQuantity -= d.Quantity;
            stock.Quantity -= d.Quantity;
            stock.UpdatedAt = DateTime.Now;
        }

        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "审核通过，库存已扣减"));
    }

    /// <summary>取消：已审核单据按持久化的基础单位数量原路恢复库存。</summary>
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
        {
            foreach (var detail in entity.Details)
            {
                var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId
                    && s.ProductId == detail.ProductId);
                if (stock is null)
                {
                    stock = new Stock { WarehouseId = entity.WarehouseId, ProductId = detail.ProductId,
                        CreatedAt = DateTime.Now };
                    Db.Stocks.Add(stock);
                }
                stock.Quantity += detail.Quantity;
                stock.AvailableQuantity += detail.Quantity;
                stock.UpdatedAt = DateTime.Now;
            }
        }
        else if (status is not (DocumentStatus.Pending or DocumentStatus.Submitted))
            throw BusinessException.RuleConflict("当前状态不允许取消");
        SetStatus(entity, DocumentStatus.Cancelled);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已取消，库存已按基础单位恢复"));
    }

    private static void Calculate(StockOut entity)
    {
        entity.TotalQuantity = entity.Details.Sum(d => d.Quantity);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }
}
