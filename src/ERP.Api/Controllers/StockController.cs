using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 库存查询控制器
/// </summary>
[ApiController]
[Route("api/stocks")]
[Authorize]
public class StockController : ControllerBase
{
    private readonly IErpDbContext _db;

    public StockController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>分页查询库存（关联商品与仓库名称）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] long? warehouseId)
    {
        query.Normalize();
        var source = _db.Stocks.AsNoTracking().Where(s => !s.IsDeleted);
        if (warehouseId.HasValue) source = source.Where(s => s.WarehouseId == warehouseId.Value);

        var total = await source.CountAsync();
        var stocks = await source.OrderByDescending(s => s.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        var productIds = stocks.Select(s => s.ProductId).Distinct().ToList();
        var warehouseIds = stocks.Select(s => s.WarehouseId).Distinct().ToList();
        var products = await _db.BaseProducts.Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.ProductCode, p.ProductName, p.Spec, p.Unit });
        var warehouses = await _db.BaseWarehouses.Where(w => warehouseIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.WarehouseName);

        var items = stocks.Select(s => new StockView
        {
            Id = s.Id,
            WarehouseId = s.WarehouseId,
            WarehouseName = warehouses.TryGetValue(s.WarehouseId, out var wn) ? wn : "",
            ProductId = s.ProductId,
            ProductCode = products.TryGetValue(s.ProductId, out var p) ? p.ProductCode : "",
            ProductName = p?.ProductName ?? "",
            Spec = p?.Spec ?? "",
            Unit = p?.Unit ?? "",
            Quantity = s.Quantity,
            AvailableQuantity = s.AvailableQuantity,
            LockedQuantity = s.LockedQuantity,
            UpdatedAt = s.UpdatedAt
        }).ToList();

        return Ok(ApiResponse<PagedResult<StockView>>.Success(
            new PagedResult<StockView> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>库存汇总（用于库存预警等）</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var totalQuantity = await _db.Stocks.Where(s => !s.IsDeleted).SumAsync(s => s.Quantity);
        var totalAvailable = await _db.Stocks.Where(s => !s.IsDeleted).SumAsync(s => s.AvailableQuantity);
        var warehouseCount = await _db.Stocks.Where(s => !s.IsDeleted).Select(s => s.WarehouseId).Distinct().CountAsync();
        return Ok(ApiResponse<object>.Success(new { totalQuantity, totalAvailable, warehouseCount }));
    }
}

/// <summary>库存视图</summary>
public class StockView
{
    public long Id { get; set; }
    public long WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public long ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Spec { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal LockedQuantity { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
