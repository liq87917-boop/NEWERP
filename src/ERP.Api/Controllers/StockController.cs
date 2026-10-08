using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 库存查询控制器（ERP-356 实时授权与数据范围护栏）。
/// <para>三个只读端点（分页列表 / 库存流水证据 / 汇总）在读取任何数量、成本或来源单据字段之前，
/// 都先复用 <see cref="StockQueryAuthorizationRules"/> 重新解析：实时身份 → 账号状态 → 既有「库存查询」
/// （stock-query）菜单授权 → 权威数据范围。任一缺失即 fail closed，绝不返回任何数量 / 成本 / 来源单据字段。</para>
/// <para>授权通过后沿用既有查询与响应契约：数量、加权平均成本与库存金额语义不变；请求筛选（仓库 / 单据号 /
/// 移动类型）只能收窄，绝不放大已授权范围。全程只读，不写库、不改单据 / 库存 / 流水。</para>
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

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 实时授权（fail closed）：校验身份 / 账号状态 / 既有 stock-query 菜单授权 / 权威数据范围。
    /// 复用既有权限模型，不新增用户授权，也不把空身份当作管理员。
    /// </summary>
    private Task<StockQueryAuthorizationRules.StockQueryScope> AuthorizeAsync(CancellationToken ct = default)
        => StockQueryAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId(), ct);

    /// <summary>分页查询库存（关联商品与仓库名称）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] long? warehouseId)
    {
        query.Normalize();
        // 授权 / 范围判定先于任何计数、分页与成本字段投影。
        var scope = await AuthorizeAsync();
        var source = StockQueryAuthorizationRules.ApplyScope(
            _db.Stocks.AsNoTracking().Where(s => !s.IsDeleted), scope);
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
            AverageCost = s.AverageCost,
            TotalCost = s.TotalCost,
            UpdatedAt = s.UpdatedAt
        }).ToList();

        return Ok(ApiResponse<PagedResult<StockView>>.Success(
            new PagedResult<StockView> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>
    /// 库存流水查询（ERP-009）：按单据 / 仓库 / 商品 / 移动类型追溯每一次库存变动。
    /// keyword 匹配来源单据号 / 商品编码 / 商品名称 / 仓库名称。
    /// </summary>
    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements([FromQuery] PageQuery query, [FromQuery] long? warehouseId,
        [FromQuery] long? productId, [FromQuery] string? sourceDocNo,
        [FromQuery] InventoryMovementType? movementType)
    {
        query.Normalize();
        // 与列表 / 汇总使用同一套授权 / 范围策略，且先于任何计数、分页与来源单据证据投影。
        var scope = await AuthorizeAsync();
        var source = StockQueryAuthorizationRules.ApplyScope(
            _db.StockMovements.AsNoTracking().Where(m => !m.IsDeleted), scope);
        if (warehouseId.HasValue) source = source.Where(m => m.WarehouseId == warehouseId.Value);
        if (productId.HasValue) source = source.Where(m => m.ProductId == productId.Value);
        if (movementType.HasValue) source = source.Where(m => m.MovementType == movementType.Value);
        if (!string.IsNullOrWhiteSpace(sourceDocNo)) source = source.Where(m => m.SourceDocNo == sourceDocNo);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(m => m.SourceDocNo.Contains(kw) || m.ProductCode.Contains(kw)
                                       || m.ProductName.Contains(kw) || m.WarehouseName.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(m => m.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockMovement>>.Success(new PagedResult<StockMovement>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        }));
    }

    /// <summary>库存汇总（用于库存预警等）</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        // 汇总（数量 / 可用数量 / 仓库数）在同一套授权 / 范围策略之后聚合，未授权时不泄露任何计数。
        var scope = await AuthorizeAsync();
        var stocks = StockQueryAuthorizationRules.ApplyScope(
            _db.Stocks.Where(s => !s.IsDeleted), scope);
        var totalQuantity = await stocks.SumAsync(s => s.Quantity);
        var totalAvailable = await stocks.SumAsync(s => s.AvailableQuantity);
        var warehouseCount = await stocks.Select(s => s.WarehouseId).Distinct().CountAsync();
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
    /// <summary>加权平均成本单价（ERP-009 新增）</summary>
    public decimal AverageCost { get; set; }
    /// <summary>库存金额（数量 × 加权平均成本）</summary>
    public decimal TotalCost { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
