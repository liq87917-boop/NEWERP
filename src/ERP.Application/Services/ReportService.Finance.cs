using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 财务报表实现：资产负债表、利润表、现金流量表（基于单据金额汇总的简化口径）
/// </summary>
public partial class ReportService
{
    /// <summary>资产负债表：资产 = 库存价值 + 应收；负债 = 应付；权益 = 资产 - 负债</summary>
    public async Task<ReportDtos.FinancialStatement> GetBalanceSheetAsync(DateTime asOfDate)
    {
        // 库存价值 = Σ(库存数量 × 成本价)
        var stocks = await _db.Stocks.Where(s => !s.IsDeleted).ToListAsync();
        var productIds = stocks.Select(s => s.ProductId).Distinct().ToList();
        var products = await _db.BaseProducts.Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);
        var inventoryValue = stocks.Sum(s =>
            s.Quantity * (products.TryGetValue(s.ProductId, out var p) ? p.CostPrice : 0));

        // 应收 = 已审核销售订单总额
        var receivable = await _db.SalesOrders
            .Where(o => !o.IsDeleted && o.OrderDate <= asOfDate
                        && (o.Status == DocumentStatus.Submitted || o.Status == DocumentStatus.Approved))
            .SumAsync(o => o.TotalAmount);

        // 应付 = 已审核采购订单总额
        var payable = await _db.PurchaseOrders
            .Where(o => !o.IsDeleted && o.OrderDate <= asOfDate
                        && (o.Status == DocumentStatus.Submitted || o.Status == DocumentStatus.Approved))
            .SumAsync(o => o.TotalAmount);

        var totalAssets = inventoryValue + receivable;
        var totalLiabilities = payable;
        var equity = totalAssets - totalLiabilities;

        return new ReportDtos.FinancialStatement
        {
            Title = "资产负债表",
            PeriodStart = DateTime.MinValue,
            PeriodEnd = asOfDate,
            Lines = new List<ReportDtos.StatementLine>
            {
                new() { Name = "库存价值", Amount = inventoryValue },
                new() { Name = "应收账款", Amount = receivable },
                new() { Name = "资产合计", Amount = totalAssets },
                new() { Name = "应付账款", Amount = payable },
                new() { Name = "负债合计", Amount = totalLiabilities },
                new() { Name = "所有者权益", Amount = equity }
            },
            Total = totalAssets
        };
    }

    /// <summary>利润表：收入 - 成本 - 费用</summary>
    public async Task<ReportDtos.FinancialStatement> GetIncomeStatementAsync(DateTime start, DateTime end)
    {
        var revenue = await _db.FinanceReceipts
            .Where(r => !r.IsDeleted && r.ReceiptDate >= start && r.ReceiptDate <= end
                        && r.Status == DocumentStatus.Approved)
            .SumAsync(r => r.Amount);

        var cost = await _db.FinancePayments
            .Where(p => !p.IsDeleted && p.PaymentDate >= start && p.PaymentDate <= end
                        && p.Status == DocumentStatus.Approved)
            .SumAsync(p => p.Amount);

        var profit = revenue - cost;

        return new ReportDtos.FinancialStatement
        {
            Title = "利润表",
            PeriodStart = start,
            PeriodEnd = end,
            Lines = new List<ReportDtos.StatementLine>
            {
                new() { Name = "营业收入", Amount = revenue },
                new() { Name = "营业支出", Amount = cost },
                new() { Name = "净利润", Amount = profit }
            },
            Total = profit
        };
    }

    /// <summary>现金流量表：经营流入 - 经营流出</summary>
    public async Task<ReportDtos.FinancialStatement> GetCashFlowStatementAsync(DateTime start, DateTime end)
    {
        var inflow = await _db.FinanceReceipts
            .Where(r => !r.IsDeleted && r.ReceiptDate >= start && r.ReceiptDate <= end
                        && r.Status == DocumentStatus.Approved)
            .SumAsync(r => r.Amount);

        var outflow = await _db.FinancePayments
            .Where(p => !p.IsDeleted && p.PaymentDate >= start && p.PaymentDate <= end
                        && p.Status == DocumentStatus.Approved)
            .SumAsync(p => p.Amount);

        var netFlow = inflow - outflow;

        return new ReportDtos.FinancialStatement
        {
            Title = "现金流量表",
            PeriodStart = start,
            PeriodEnd = end,
            Lines = new List<ReportDtos.StatementLine>
            {
                new() { Name = "经营活动现金流入", Amount = inflow },
                new() { Name = "经营活动现金流出", Amount = outflow },
                new() { Name = "现金净流量", Amount = netFlow }
            },
            Total = netFlow
        };
    }
}
