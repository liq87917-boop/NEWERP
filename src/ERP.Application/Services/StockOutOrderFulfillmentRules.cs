using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售出库衔接来源订单并防止累计超发（ERP-343）。
/// <para>只复用既有 <see cref="StockOut.SalesOrderId"/> 显式链接：未链接（null）的历史单据不做任何校验，行为保持不变。</para>
/// <para>审核时，仅当链接构成「权威来源」（订单存在、未删除、已审核、客户一致、每商品有且仅有一行可折算的订单明细）
/// 才按商品、按基础单位逐行校验「累计已审核出库数量（含本单）≤ 订单授权数量」；链接不构成权威来源时
/// 不做累计校验——绝不猜测不明确的重复订单行，也绝不臆造授权数量、不阻断既有兜底出库。</para>
/// <para>已取消 / 已驳回 / 待提交 / 已提交的出库单不计入已审核数量，取消后自动释放剩余额度；跨单位、跨币种不合计。</para>
/// <para>本类只做纯内存判定与有界查询，不落库、不改单据、不开启事务；同单并发审核的串行化由调用方
/// （<c>StockOutController.Approve</c>）在同一关系型事务内对来源订单行加更新锁完成。</para>
/// </summary>
public static class StockOutOrderFulfillmentRules
{
    /// <summary>数量比较容差（与出库基础单位数量保留 4 位小数同口径，吸收装箱换算的舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>发货口径说明（界面 / 文档同源）</summary>
    public const string RuleText =
        "销售出库单关联销售订单后，仅当链接权威（订单已审核、客户一致、每商品唯一兼容明细行）时，审核时累计" +
        "「以该订单为来源、未删除、已审核」的出库数量（出库明细已折算基础单位）加上本单数量不得超过订单授权数量（按商品逐行比对）；" +
        "未关联或链接不权威的出库单不做累计校验，绝不猜测重复订单行、绝不臆造授权数量。";

    /// <summary>
    /// 校验审核时的来源订单链接与累计数量上限。调用方必须已把本单明细折算为基础单位（<c>StockUnitConversion.NormalizeAsync</c>）。
    /// <para>未链接或链接不构成权威来源时直接返回（不校验、不阻断）；链接权威时按基础单位逐商品累计校验，
    /// 超限抛业务异常且不落库、不改库存 / 流水 / 单据状态。</para>
    /// </summary>
    public static async Task ValidateApprovalAsync(IErpDbContext db, StockOut entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.SalesOrderId is not > 0) return;

        var link = await TryResolveAuthoritativeLinkAsync(db, entity, ct);
        if (link is null) return;

        await EnforceCumulativeAsync(db, entity, link, ct);
    }

    // ==================== 私有助手 ====================

    private static async Task<LinkContext?> TryResolveAuthoritativeLinkAsync(IErpDbContext db, StockOut entity,
        CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.SalesOrderId!.Value && !o.IsDeleted, ct);
        if (order is null || order.Status != DocumentStatus.Approved)
            return null;
        if (entity.CustomerId != order.CustomerId)
            return null;

        var productIds = entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0)
            .Select(d => d.ProductId).Distinct().ToList();
        var products = productIds.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        var lines = order.Details.Where(l => !l.IsDeleted).ToList();

        foreach (var detail in entity.Details.Where(d => !d.IsDeleted))
        {
            if (detail.ProductId <= 0)
                return null;
            if (!products.TryGetValue(detail.ProductId, out var product))
                return null;
            var matches = lines.Where(l => l.ProductId == detail.ProductId).ToList();
            if (matches.Count != 1)
                return null;
            if (!TryBaseUnitQuantity(product, matches[0], out _))
                return null;
            if (!IsStockOutUnitCompatible(product, detail))
                return null;
        }

        return new LinkContext(order, lines, products);
    }

    private static async Task EnforceCumulativeAsync(IErpDbContext db, StockOut entity, LinkContext link,
        CancellationToken ct)
    {
        var orderId = link.Order.Id;

        // 已审核出库数量：以本单为来源、未删除、已审核、且非本单的出库明细数量按商品汇总。
        // 本单由调用方在拿到订单锁后进入本方法，因此并发同单审核会被串行化，后到者能看到先到者已提交的数量。
        var approvedRows = await (
                from s in db.StockOuts
                join d in db.StockOutDetails on s.Id equals d.StockOutId
                where s.SalesOrderId == orderId
                      && !s.IsDeleted
                      && s.Status == DocumentStatus.Approved
                      && s.Id != entity.Id
                      && !d.IsDeleted
                      && d.ProductId > 0
                select new { d.ProductId, d.Quantity })
            .ToListAsync(ct);

        var already = approvedRows.GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

        foreach (var group in entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0 && d.Quantity > 0)
                     .GroupBy(d => d.ProductId))
        {
            var productId = group.Key;
            var product = link.Products[productId];
            var line = link.Lines.Single(l => l.ProductId == productId);
            TryBaseUnitQuantity(product, line, out var authorized);

            var shipped = already.TryGetValue(productId, out var value) ? value : 0m;
            var thisQuantity = group.Sum(d => d.Quantity);
            var total = shipped + thisQuantity;

            if (total > authorized + QuantityTolerance)
            {
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 累计出库数量 {total} 超过来源销售订单授权数量 {authorized}" +
                    $"（已审核 {shipped} + 本次 {thisQuantity}）");
            }
        }
    }


    /// <summary>
    /// 订单行数量 → 基础单位数量：订单行单位等于商品基础单位直接使用；等于装箱单位时按 UnitsPerPackage 折算；
    /// 其他 / 空单位或无效装箱数返回 false（由调用方 fail closed）。
    /// </summary>
    private static bool TryBaseUnitQuantity(BaseProduct product, SalesOrderDetail line, out decimal baseQuantity)
    {
        baseQuantity = 0m;

        var lineUnit = (line.Unit ?? string.Empty).Trim();
        var baseUnit = (product.Unit ?? string.Empty).Trim();
        var packageUnit = (product.PackageUnit ?? string.Empty).Trim();

        if (baseUnit.Length > 0 && lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase))
        {
            baseQuantity = line.Quantity;
            return true;
        }

        if (packageUnit.Length == 0 || !lineUnit.Equals(packageUnit, StringComparison.OrdinalIgnoreCase))
            return false;
        if (product.UnitsPerPackage <= 0)
            return false;

        baseQuantity = Math.Round(line.Quantity * product.UnitsPerPackage, 4);
        return true;
    }

    /// <summary>
    /// 出库明细单位是否与商品基础单位兼容：空单位按基础单位处理（出库流水读取时回退商品基础单位）；
    /// 装箱单位已由 <c>StockUnitConversion.NormalizeAsync</c> 折算为基础单位，因此此处只认基础单位。
    /// </summary>
    private static bool IsStockOutUnitCompatible(BaseProduct product, StockOutDetail detail)
    {
        var unit = (detail.Unit ?? string.Empty).Trim();
        if (unit.Length == 0) return true;

        var baseUnit = (product.Unit ?? string.Empty).Trim();
        return baseUnit.Length > 0 && unit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LinkContext
    {
        public LinkContext(SalesOrder order, IReadOnlyList<SalesOrderDetail> lines,
            IReadOnlyDictionary<long, BaseProduct> products)
        {
            Order = order;
            Lines = lines;
            Products = products;
        }

        public SalesOrder Order { get; }
        public IReadOnlyList<SalesOrderDetail> Lines { get; }
        public IReadOnlyDictionary<long, BaseProduct> Products { get; }
    }
}

