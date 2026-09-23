using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 应收账款与账龄分析实现
/// 口径说明：
///   1. 应收来源 = 销售订单（排除已取消 / 已驳回），金额取订单总额；
///   2. 收款来源 = 已审核收款单，按客户汇总后**按订单日期先入先出冲抵**；
///   3. 账龄 = 截止日 − 订单日期；是否逾期按客户资料的「账期天数」判断（0 / 空视为现结，超过即逾期）；
///   4. 只列出仍有未收余额（> 0.005）的订单，便于直接催收。
/// </summary>
public partial class ReportService
{
    /// <summary>应收账款账龄分析</summary>
    public async Task<List<ReportDtos.ArAgingItem>> GetArAgingAsync(DateTime asOfDate)
    {
        var orders = await _db.SalesOrders
            .Where(o => !o.IsDeleted && o.OrderDate <= asOfDate
                        && o.Status != DocumentStatus.Cancelled && o.Status != DocumentStatus.Rejected)
            .OrderBy(o => o.OrderDate).ThenBy(o => o.Id)
            .ToListAsync();

        if (orders.Count == 0) return new List<ReportDtos.ArAgingItem>();

        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await _db.BaseCustomers
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c);

        var receipts = await _db.FinanceReceipts
            .Where(r => !r.IsDeleted && r.ReceiptDate <= asOfDate && r.Status == DocumentStatus.Approved)
            .Select(r => new { r.CustomerId, r.Amount })
            .ToListAsync();
        var poolByCustomer = receipts
            .GroupBy(r => r.CustomerId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var result = new List<ReportDtos.ArAgingItem>();

        foreach (var group in orders.GroupBy(o => o.CustomerId))
        {
            var pool = poolByCustomer.TryGetValue(group.Key, out var p) ? p : 0m;
            customers.TryGetValue(group.Key, out var cust);
            var creditDays = cust?.CreditDays ?? 0;

            foreach (var o in group)
            {
                var applied = Math.Min(pool, o.TotalAmount);   // 先入先出冲抵
                pool -= applied;
                var balance = o.TotalAmount - applied;
                if (balance <= 0.005m) continue;               // 已收完的订单不列出

                var agingDays = Math.Max(0, (asOfDate.Date - o.OrderDate.Date).Days);
                var overdue = agingDays > creditDays;

                result.Add(new ReportDtos.ArAgingItem
                {
                    CustomerName = cust?.CustomerName ?? ("客户#" + o.CustomerId),
                    OrderNo = o.OrderNo,
                    OrderDate = o.OrderDate,
                    Currency = o.Currency.ToString(),
                    OrderAmount = o.TotalAmount,
                    ReceivedAmount = applied,
                    Balance = balance,
                    AgingDays = agingDays,
                    CreditDays = creditDays,
                    Bucket = overdue ? BucketOf(agingDays) : "未到期",
                    Status = overdue ? "逾期" : "未逾期"
                });
            }
        }

        return result
            .OrderByDescending(r => r.AgingDays)
            .ThenBy(r => r.CustomerName)
            .ToList();
    }

    /// <summary>账龄区间</summary>
    private static string BucketOf(int days) =>
        days <= 30 ? "1-30 天"
        : days <= 60 ? "31-60 天"
        : days <= 90 ? "61-90 天"
        : days <= 180 ? "91-180 天"
        : "180 天以上";
}
