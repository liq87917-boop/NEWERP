using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（三）：业务员提成表
/// 提成规则：**统一比例**，比例取自系统参数 `SalesCommissionRate`（%，可在「系统设置 → 系统参数」维护）；
///           默认为 0 时仅输出业绩数据（销售额 / 毛利 / 毛利率），提成额为 0，便于先看数据再定比例。
///           （阶梯提成、按柜提成等规则留待后续扩展，避免现在把规则写死。）
/// </summary>
public partial class ReportService
{
    /// <summary>业务员提成表（按毛利降序）</summary>
    public async Task<List<ReportDtos.SalesCommissionItem>> GetSalesCommissionAsync(DateTime start, DateTime end)
    {
        // 提成比例（系统参数，默认 0）
        var rateText = await _db.SysParameters
            .Where(p => !p.IsDeleted && p.ParamKey == "SalesCommissionRate")
            .Select(p => p.ParamValue)
            .FirstOrDefaultAsync();
        var rate = decimal.TryParse(rateText, out var parsed) && parsed > 0 ? parsed : 0m;

        var orders = await _db.SalesOrders
            .Where(o => !o.IsDeleted && o.OrderDate >= start && o.OrderDate <= end
                        && o.Status != DocumentStatus.Cancelled && o.Status != DocumentStatus.Rejected)
            .ToListAsync();
        if (orders.Count == 0) return new List<ReportDtos.SalesCommissionItem>();

        var orderIds = orders.Select(o => o.Id).ToList();
        var details = await _db.SalesOrderDetails
            .Where(x => orderIds.Contains(x.SalesOrderId) && !x.IsDeleted)
            .ToListAsync();

        var productIds = details.Select(x => x.ProductId).Distinct().ToList();
        var products = await _db.BaseProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var salesmanIds = orders.Where(o => o.SalesmanId.HasValue && o.SalesmanId > 0)
            .Select(o => o.SalesmanId!.Value).Distinct().ToList();
        var employees = await _db.BaseEmployees
            .Where(e => salesmanIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e);

        decimal ProfitOf(long orderId) => details
            .Where(x => x.SalesOrderId == orderId)
            .Sum(x => x.Quantity * (x.UnitPrice - (products.TryGetValue(x.ProductId, out var p) ? p.CostPrice : 0)));

        return orders
            .GroupBy(o => o.SalesmanId ?? 0)
            .Select(g =>
            {
                employees.TryGetValue(g.Key, out var emp);
                var sales = g.Sum(x => x.TotalAmount);
                var profit = g.Sum(x => ProfitOf(x.Id));
                return new ReportDtos.SalesCommissionItem
                {
                    SalesmanName = emp?.EmployeeName ?? (g.Key > 0 ? "业务员#" + g.Key : "(未指定业务员)"),
                    OrderCount = g.Count(),
                    SalesAmount = sales,
                    Profit = profit,
                    ProfitRate = sales > 0 ? Math.Round(profit / sales * 100, 2) : 0,
                    CommissionRate = rate,
                    CommissionAmount = profit > 0 ? Math.Round(profit * rate / 100m, 2) : 0
                };
            })
            .OrderByDescending(x => x.Profit)
            .ToList();
    }
}
