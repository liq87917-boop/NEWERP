using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 入出库统一以商品基础单位保存和记账。明细单位等于商品 PackageUnit 时，
/// Quantity × UnitsPerPackage 后改写为基础 Unit；其他/历史单位保持原数量。
/// </summary>
public static class StockUnitConversion
{
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<StockInDetail> details)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)));

    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<StockOutDetail> details)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)));

    private static async Task NormalizeAsync(IErpDbContext db, IEnumerable<MutableLine> source)
    {
        var lines = source.ToList();
        var ids = lines.Select(l => l.ProductId).Where(id => id > 0).Distinct().ToList();
        var products = await db.BaseProducts.AsNoTracking().Where(p => ids.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id);
        foreach (var line in lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product)) continue;
            var lineUnit = line.GetUnit().Trim();
            var packageUnit = (product.PackageUnit ?? string.Empty).Trim();
            var baseUnit = (product.Unit ?? string.Empty).Trim();
            if (packageUnit.Length == 0 || !lineUnit.Equals(packageUnit, StringComparison.OrdinalIgnoreCase)
                || lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase)) continue;
            if (product.UnitsPerPackage <= 0)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 的每包装数量必须大于 0");
            if (baseUnit.Length == 0)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 未设置基础单位，不能执行包装换算");
            line.SetQuantity(Math.Round(line.GetQuantity() * product.UnitsPerPackage, 4));
            line.SetUnit(baseUnit);
        }
    }

    private sealed record MutableLine(long ProductId, Func<string> GetUnit, Action<string> SetUnit,
        Func<decimal> GetQuantity, Action<decimal> SetQuantity);
}
