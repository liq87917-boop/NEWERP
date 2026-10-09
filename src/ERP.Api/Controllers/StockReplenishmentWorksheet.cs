using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 只读补货工作台控制器（ERP-106）：把既有库存 <c>Stocks</c>、商品最低 / 上限库存
/// （<c>BaseProduct.MinStock</c> / <c>BaseProduct.MaxStock</c>）与启用中的商品货源关系
/// （<c>BaseProductSuppliers</c>）按「仓库 + 商品」逐行只读呈现，并给出低于最低库存时的补货建议。
/// <para>授权（ERP-438）：在读取<b>任何</b>库存数量、可用数量、最低 / 上限库存或货源关系之前，
/// 复用 <see cref="StockQueryAuthorizationRules"/>（ERP-356）重新解析实时身份 → 账号状态 →
/// 既有「库存查询」（<c>stock-query</c>）菜单授权 → 权威数据范围，任一缺失即 fail closed；
/// 受限账号没有权威的仓库级数据范围时拒绝读取全局库存，绝不授予全局可见性。</para>
/// <para>审计口径：本控制器<strong>不新建任何表、不新增任何列、不执行任何写操作</strong>，
/// 只按显式字段读取（全库查询均为 <c>AsNoTracking</c>，无 Add / Update / Remove / SaveChanges），
/// 不改写库存 / 商品 / 供应商 / 货源关系，也不生成任何采购报价或采购订单。</para>
/// <para>边界：货源信息仅作参考（不自动选择供应商、不生成订单、不改写库存）；不跨仓汇总、不做单位换算；
/// 补货建议 = 库存上限 − 现有库存，仅在「低于最低库存且存在有效上限目标」时给出，其余情况显式标注不推断。</para>
/// </summary>
[ApiController]
[Route("api/stocks/replenishment-worksheet")]
[Authorize]
public class StockReplenishmentWorksheetController : ControllerBase
{
    private readonly IErpDbContext _db;

    public StockReplenishmentWorksheetController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 实时授权（fail closed）：校验身份 / 账号状态 / 既有 stock-query 菜单授权 / 权威数据范围。
    /// 精确复用 <see cref="StockQueryAuthorizationRules"/>，不新增用户授权，也不把空身份当作管理员。
    /// </summary>
    private Task<StockQueryAuthorizationRules.StockQueryScope> AuthorizeAsync(CancellationToken ct = default)
        => StockReplenishmentRules.EnsureAuthorizedAsync(_db, CurrentUserId(), ct);

    /// <summary>
    /// 只读补货工作台（分页）：按仓库（必填）/ 可选商品筛选，按稳定库存行 Id 分页；
    /// 单页内批量装载商品阈值、仓库名与启用中的货源关系（含供应商参考），不产生逐行数据库访问。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetWorksheet([FromQuery] StockReplenishmentWorksheetQuery query)
    {
        query.Normalize();
        // 授权 / 范围判定先于任何计数、分页与库存 / 阈值 / 货源字段读取（fail closed）：
        // 身份 / 账号状态 / 既有 stock-query 菜单 / 权威数据范围任一缺失即拒绝。
        var scope = await AuthorizeAsync();
        var warehouseId = StockReplenishmentRules.RequireWarehouse(query.WarehouseId);
        var productId = StockReplenishmentRules.NormalizeProductFilter(query.ProductId);

        var source = StockReplenishmentRules.ApplyScope(
            _db.Stocks.AsNoTracking().Where(s => !s.IsDeleted), scope);
        source = source.Where(s => s.WarehouseId == warehouseId);
        if (productId.HasValue)
            source = source.Where(s => s.ProductId == productId.Value);

        var total = await source.CountAsync();
        var pageStocks = await source
            .OrderBy(s => s.ProductId).ThenBy(s => s.Id)   // 稳定分页（仓库已固定，仅需商品 / Id 排序）
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(s => new { s.Id, s.WarehouseId, s.ProductId, s.Quantity, s.AvailableQuantity })
            .ToListAsync();

        // 仓库名：本工作台按仓库筛选，单页仅涉及一个仓库
        var warehouse = await _db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId);
        var warehouseName = string.IsNullOrWhiteSpace(warehouse?.WarehouseName)
            ? $"仓库#{warehouseId}"
            : warehouse!.WarehouseName;

        // 有界批量装载：本页商品阈值 / 编码 / 名称 / 规格 / 单位
        var productIds = pageStocks.Select(s => s.ProductId).Distinct().ToList();
        var products = productIds.Count == 0
            ? new List<BaseProduct>()
            : await _db.BaseProducts.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync();
        var productsById = products.ToDictionary(p => p.Id);

        // 有界批量装载：本页商品启用中的货源关系 + 关联供应商参考（一次批量查询，无逐行查库）
        var relations = productIds.Count == 0
            ? new List<BaseProductSupplier>()
            : await _db.BaseProductSuppliers.AsNoTracking()
                .Where(r => !r.IsDeleted && r.Status == StockReplenishmentRules.ActiveStatus
                            && productIds.Contains(r.ProductId))
                .OrderByDescending(r => r.IsPreferred).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
                .ToListAsync();

        var supplierIds = relations.Select(r => r.SupplierId).Distinct().ToList();
        var suppliers = supplierIds.Count == 0
            ? new List<BaseSupplier>()
            : await _db.BaseSuppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToListAsync();
        var suppliersById = suppliers.ToDictionary(s => s.Id);

        var relationsByProduct = relations.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // 组装（本页内存计算，不再查库）：建议状态与货源可用性全部由纯规则判定
        var rows = pageStocks.Select(s =>
        {
            productsById.TryGetValue(s.ProductId, out var product);
            var minStock = product?.MinStock ?? 0m;
            var maxStock = product?.MaxStock ?? 0m;

            var decision = StockReplenishmentRules.Evaluate(s.Quantity, minStock, maxStock);
            var references = BuildSourcing(relationsByProduct, s.ProductId, suppliersById);
            var availableCount = references.Count(r => r.SupplierAvailable);

            return new StockReplenishmentRowDto(
                s.Id,
                s.WarehouseId,
                warehouseName,
                s.ProductId,
                product?.ProductCode ?? string.Empty,
                string.IsNullOrWhiteSpace(product?.ProductName) ? $"商品#{s.ProductId}" : product!.ProductName,
                product?.Spec ?? string.Empty,
                product?.Unit ?? string.Empty,
                s.Quantity,
                s.AvailableQuantity,
                minStock,
                maxStock,
                StockReplenishmentRules.IsBelowMinimum(s.Quantity, minStock),
                decision.SuggestedTopUp,
                decision.State,
                StockReplenishmentRules.RecommendationText(decision, minStock, maxStock),
                availableCount > 0,
                StockReplenishmentRules.SourcingText(availableCount, references.Count),
                references);
        }).ToList();

        var worksheet = new StockReplenishmentWorksheetDto(
            rows,
            total,
            query.Page,
            query.PageSize,
            StockReplenishmentRules.ReadOnlyText,
            StockReplenishmentRules.BoundaryText,
            StockReplenishmentRules.DisclaimerText);

        return Ok(ApiResponse<StockReplenishmentWorksheetDto>.Success(worksheet));
    }

    /// <summary>把本商品启用中的货源关系映射为只读参考行（供应商缺失 / 停用 / 删除时显式标注，不静默消失）</summary>
    private static List<StockReplenishmentSourcingDto> BuildSourcing(
        Dictionary<long, List<BaseProductSupplier>> relationsByProduct,
        long productId,
        Dictionary<long, BaseSupplier> suppliersById)
    {
        if (!relationsByProduct.TryGetValue(productId, out var relations))
            return new List<StockReplenishmentSourcingDto>();

        var result = new List<StockReplenishmentSourcingDto>(relations.Count);
        foreach (var relation in relations)
        {
            suppliersById.TryGetValue(relation.SupplierId, out var supplier);
            var available = supplier is not null && !supplier.IsDeleted
                            && supplier.Status == StockReplenishmentRules.ActiveStatus;

            result.Add(new StockReplenishmentSourcingDto(
                relation.SupplierId,
                supplier?.SupplierCode ?? string.Empty,
                string.IsNullOrWhiteSpace(supplier?.SupplierName) ? $"供应商#{relation.SupplierId}" : supplier!.SupplierName,
                available,
                relation.SupplierItemCode,
                relation.PurchaseUnit,
                relation.MinOrderQty,
                relation.LeadTimeDays,
                relation.IsPreferred));
        }

        return result;
    }
}

