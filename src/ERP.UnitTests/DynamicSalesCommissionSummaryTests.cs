using ERP.Application.Interfaces;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态业务员提成证据报表（ERP-247）「全匹配」汇总聚焦单元测试：
/// 纯规则在分页 / 选定列投影之前对同一份完整有界作用域化列表派生原币汇总，
/// 已知币种签名合计、去重业务员桶数与不相交已审核订单数、未知 / 无效币种保留原始键且金额 null 仅计数证据、
/// 绝不跨币种合计 / 比较；利润 / 利润率 / 提成额恒为 null；跨币种去重业务员桶（未指定业务员桶单独计）；
/// 当前参考比例一致已知保留（含显式 0）、缺失 / 冲突为 null 并给原因；空来源与越界页 / 隐藏字段 / 单行页下汇总仍覆盖全部匹配行。
/// <para>全部为纯规则，不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicSalesCommissionSummaryTests
{
    private static ReportDtos.SalesCommissionItem Item(
        long? salesmanId, string currency, decimal? salesAmount, int orderCount,
        decimal? commissionRate = null, string? commissionRateEvidence = null)
        => new()
        {
            SalesmanId = salesmanId,
            SalesmanName = salesmanId.HasValue ? "业务员" + salesmanId : SalesCommissionEvidenceRules.UnassignedSalesmanName,
            Currency = currency,
            CurrencyLabel = SalesCommissionEvidenceRules.CurrencyGroupLabel(currency),
            OrderCount = orderCount,
            SalesAmount = salesAmount,
            Profit = null,
            ProfitRate = null,
            CommissionAmount = null,
            CommissionRate = commissionRate,
            CommissionRateEvidence = commissionRateEvidence
                ?? (commissionRate.HasValue
                    ? SalesCommissionEvidenceRules.CommissionRateEvidence
                    : SalesCommissionEvidenceRules.RateMissingReason),
        };

    [Fact]
    public void 原币汇总_已知币种签名合计_去重业务员桶与不相交订单数_无跨币种合计()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 1000m, 2),
            Item(2, "USD", -200m, 3),
            Item(1, "CNY", 500m, 1),
        });

        Assert.Equal(2, summary.CurrencyRows.Count);

        var usd = Assert.Single(summary.CurrencyRows, r => r.Currency == "USD");
        Assert.Equal(800m, usd.SalesAmount);
        Assert.Equal(2, usd.UniqueSalesmanBuckets);
        Assert.Equal(5, usd.ApprovedOrders);
        Assert.Equal(DynamicSalesCommissionSummaryRules.KnownAmountCompleteText, usd.AmountCompletenessText);
        Assert.Equal("USD 美元", usd.CurrencyLabel);

        var cny = Assert.Single(summary.CurrencyRows, r => r.Currency == "CNY");
        Assert.Equal(500m, cny.SalesAmount);
        Assert.Equal(1, cny.UniqueSalesmanBuckets);
        Assert.Equal(1, cny.ApprovedOrders);

        Assert.DoesNotContain(summary.CurrencyRows, r => r.SalesAmount == 1300m);
    }

    [Fact]
    public void 原币汇总_未知币种保留各自原始键_金额null_显式未知计数证据()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "999", null, 2),
            Item(2, "999", null, 1),
            Item(3, "1000", null, 1),
        });

        Assert.Equal(2, summary.CurrencyRows.Count);

        var row999 = Assert.Single(summary.CurrencyRows, r => r.Currency == "999");
        Assert.Equal("未知币种", row999.CurrencyLabel);
        Assert.Null(row999.SalesAmount);
        Assert.Equal(2, row999.UniqueSalesmanBuckets);
        Assert.Equal(3, row999.ApprovedOrders);
        Assert.Equal(DynamicSalesCommissionSummaryRules.UnknownAmountIncompleteText, row999.AmountCompletenessText);

        var row1000 = Assert.Single(summary.CurrencyRows, r => r.Currency == "1000");
        Assert.Null(row1000.SalesAmount);
        Assert.Equal(1, row1000.UniqueSalesmanBuckets);
        Assert.Equal(1, row1000.ApprovedOrders);
    }

    [Fact]
    public void 汇总_利润利润率提成额恒null_附历史依据说明_不做当前价推算()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 1),
        });

        var row = Assert.Single(summary.CurrencyRows);
        Assert.Null(row.Profit);
        Assert.Null(row.ProfitRate);
        Assert.Null(row.CommissionAmount);
        Assert.Equal(SalesCommissionEvidenceRules.ProfitEvidence, row.ProfitReasonText);
        Assert.Equal(SalesCommissionEvidenceRules.ProfitRateEvidence, row.ProfitRateReasonText);
        Assert.Equal(SalesCommissionEvidenceRules.CommissionEvidence, row.CommissionAmountReasonText);

        Assert.Contains("利润未知", summary.ProfitBasisText);
        Assert.Contains("利润率未知", summary.ProfitBasisText);
        Assert.Contains("提成额未知", summary.ProfitBasisText);
    }

    [Fact]
    public void 汇总_跨币种去重业务员桶_未指定业务员桶单独计_非员工人数()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(null, "USD", 100m, 1),
            Item(1, "USD", 100m, 1),
            Item(2, "USD", 100m, 1),
            Item(1, "CNY", 100m, 1),
        });

        Assert.Equal(3, summary.GlobalUniqueSalesmanBuckets);

        var usd = Assert.Single(summary.CurrencyRows, r => r.Currency == "USD");
        Assert.Equal(3, usd.UniqueSalesmanBuckets);

        var cny = Assert.Single(summary.CurrencyRows, r => r.Currency == "CNY");
        Assert.Equal(1, cny.UniqueSalesmanBuckets);
    }

    [Fact]
    public void 汇总_全局已审核订单数_不相交合计()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 2),
            Item(1, "CNY", 200m, 3),
        });

        Assert.Equal(5, summary.GlobalApprovedOrders);
        Assert.Equal(5, summary.CurrencyRows.Sum(r => r.ApprovedOrders));
    }

    [Fact]
    public void 汇总_当前参考比例_一致已知保留_含显式0()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 1, 12.5m),
            Item(2, "USD", 100m, 1, 12.5m),
        });

        Assert.Equal(12.5m, summary.CurrentReferenceRate);
        Assert.Equal(DynamicSalesCommissionSummaryRules.RateKnownReason, summary.CurrentReferenceRateReason);

        var zero = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 1, 0m),
        });

        Assert.Equal(0m, zero.CurrentReferenceRate);
        Assert.Equal(DynamicSalesCommissionSummaryRules.RateKnownReason, zero.CurrentReferenceRateReason);

        // 配置为 0 的当前参考比例不改变历史利润 / 利润率 / 提成额恒未知（绝不由当前比例派生）
        var zeroRow = Assert.Single(zero.CurrencyRows);
        Assert.Null(zeroRow.Profit);
        Assert.Null(zeroRow.ProfitRate);
        Assert.Null(zeroRow.CommissionAmount);
    }

    [Fact]
    public void 汇总_当前参考比例_缺失或冲突_为null并给原因_绝不加权平均或回退0()
    {
        var missing = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 1, null),
        });

        Assert.Null(missing.CurrentReferenceRate);
        Assert.Equal(DynamicSalesCommissionSummaryRules.RateMissingReason, missing.CurrentReferenceRateReason);

        var conflict = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 1, 10m),
            Item(2, "USD", 100m, 1, 20m),
        });

        Assert.Null(conflict.CurrentReferenceRate);
        Assert.Equal(DynamicSalesCommissionSummaryRules.RateConflictReason, conflict.CurrentReferenceRateReason);

        var partial = DynamicSalesCommissionSummaryRules.BuildSummary(new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 100m, 1, 10m),
            Item(2, "USD", 100m, 1, null),
        });

        Assert.Null(partial.CurrentReferenceRate);
        Assert.Equal(DynamicSalesCommissionSummaryRules.RateMissingReason, partial.CurrentReferenceRateReason);
    }

    [Fact]
    public void 空来源_汇总仍存在_空行且无证据说明非空()
    {
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(
            Array.Empty<ReportDtos.SalesCommissionItem>());

        Assert.NotNull(summary);
        Assert.Empty(summary.CurrencyRows);
        Assert.Equal(0, summary.GlobalUniqueSalesmanBuckets);
        Assert.Equal(0, summary.GlobalApprovedOrders);
        Assert.Null(summary.CurrentReferenceRate);
        Assert.Equal(DynamicSalesCommissionSummaryRules.RateEmptyReason, summary.CurrentReferenceRateReason);
        Assert.NotEmpty(summary.CoverageText);
        Assert.NotEmpty(summary.ProfitBasisText);
        Assert.NotEmpty(summary.EmptyText);
        Assert.Contains("无匹配证据", summary.EmptyText);
    }

    [Fact]
    public void 汇总列_固定且含关键列_与明细选定列无关()
    {
        var columns = DynamicSalesCommissionSummaryRules.CurrencySummaryColumns;

        Assert.Contains(columns, c => c.Key == "currency");
        Assert.Contains(columns, c => c.Key == "approvedOrders");
        Assert.Contains(columns, c => c.Key == "uniqueSalesmanBuckets");
        Assert.Contains(columns, c => c.Key == "salesAmount");
        Assert.Contains(columns, c => c.Key == "profit");
        Assert.Contains(columns, c => c.Key == "profitRate");
        Assert.Contains(columns, c => c.Key == "commissionAmount");
        Assert.Contains(columns, c => c.Key == "amountCompletenessText");
    }

    [Fact]
    public void BuildPage_越界页与隐藏字段_汇总仍覆盖全部匹配行()
    {
        var items = new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 1000m, 1),
            Item(2, "USD", 200m, 1),
            Item(3, "CNY", 500m, 1),
        };

        var page = DynamicSalesCommissionReportRules.BuildPage(
            items,
            new List<string>(),
            page: 99,
            pageSize: 20,
            start: new DateTime(2026, 9, 1),
            end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Empty(page.Columns);
        Assert.Empty(page.Rows);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.UniqueSalesmanBuckets));
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.ApprovedOrders));
    }

    [Fact]
    public void BuildPage_当前页仅一行_汇总仍覆盖全部匹配行()
    {
        var items = new List<ReportDtos.SalesCommissionItem>
        {
            Item(1, "USD", 1000m, 1),
            Item(2, "USD", 200m, 1),
            Item(3, "CNY", 500m, 1),
        };

        var page = DynamicSalesCommissionReportRules.BuildPage(
            items,
            new List<string> { "currency" },
            page: 1,
            pageSize: 1,
            start: new DateTime(2026, 9, 1),
            end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Single(page.Rows);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);
        Assert.Equal(3, page.Summary.GlobalApprovedOrders);
    }
}
