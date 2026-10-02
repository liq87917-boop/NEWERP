using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态业务员产值证据报表（ERP-239）「全匹配」汇总聚焦单元测试：
/// 纯规则在分页 / 选定列投影之前对同一份完整有界作用域化列表派生原币汇总，
/// 已知币种签名合计（不先对每个业务员四舍五入）、去重业务员数与不相交已分配已审核订单数、
/// 未知 / 无效币种保留各自原始键且金额 null 仅显式计数证据、绝不跨币种合计 / 比较；
/// 利润与利润率恒为 null 并附历史成本依据说明；空来源与越界页 / 隐藏字段 / 单行页下汇总仍覆盖全部匹配行。
/// <para>全部为纯规则，不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicSalesmanOutputSummaryTests
{
    private static ReportDtos.SalesmanOutputItem Item(
        long salesmanId, string currency, decimal? totalAmount, int orderCount)
        => new()
        {
            SalesmanId = salesmanId,
            SalesmanName = "业务员" + salesmanId,
            Currency = currency,
            CurrencyLabel = SalesmanOutputEvidenceRules.CurrencyGroupLabel(currency),
            OrderCount = orderCount,
            TotalAmount = totalAmount,
            TotalProfit = null,
            AmountLabel = SalesmanOutputEvidenceRules.AmountLabel,
            CurrencyEvidence = totalAmount.HasValue
                ? SalesmanOutputEvidenceRules.KnownCurrencyEvidence
                : SalesmanOutputEvidenceRules.UnknownCurrencyEvidence,
            ProfitEvidence = SalesmanOutputEvidenceRules.ProfitEvidence,
        };

    // ==================== 1. 原币金额汇总 ====================

    [Fact]
    public void 原币汇总_已知币种签名合计_去重业务员数与不相交订单数_无跨币种合计()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(new List<ReportDtos.SalesmanOutputItem>
        {
            Item(1, "USD", 1000m, 2),
            Item(2, "USD", -200m, 3),
            Item(1, "CNY", 500m, 1),
        });

        Assert.Equal(2, summary.CurrencyRows.Count);

        var usd = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "USD"));
        Assert.Equal(800m, usd.TotalAmount);       // 1000 + (-200)，签名合计、不逐业务员四舍五入
        Assert.Equal(2, usd.SalesmanCount);         // 去重业务员数
        Assert.Equal(5, usd.OrderCount);            // 不相交已分配已审核订单数合计
        Assert.Equal(SalesmanOutputEvidenceRules.KnownCurrencyEvidence, usd.Evidence);
        Assert.Equal("USD 美元", usd.CurrencyLabel);

        var cny = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "CNY"));
        Assert.Equal(500m, cny.TotalAmount);
        Assert.Equal(1, cny.SalesmanCount);
        Assert.Equal(1, cny.OrderCount);

        // 绝不出现跨币种合计
        Assert.DoesNotContain(summary.CurrencyRows, r => r.TotalAmount == 1300m);
    }

    [Fact]
    public void 原币汇总_签名小数金额_不先对每个业务员四舍五入()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(new List<ReportDtos.SalesmanOutputItem>
        {
            Item(1, "USD", 1.005m, 1),
            Item(2, "USD", 1.004m, 1),
        });

        var usd = Assert.Single(summary.CurrencyRows);
        Assert.Equal(2.009m, usd.TotalAmount);      // 原始 decimal 直接求和，不逐业务员取整
    }

    [Fact]
    public void 原币汇总_未知币种保留各自原始键_金额null_显式未知计数证据()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(new List<ReportDtos.SalesmanOutputItem>
        {
            Item(1, "999", null, 2),
            Item(2, "999", null, 1),
            Item(3, "1000", null, 1),
        });

        Assert.Equal(2, summary.CurrencyRows.Count);

        var row999 = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "999"));
        Assert.Equal("未知币种", row999.CurrencyLabel);
        Assert.Null(row999.TotalAmount);            // 未知币种金额未知，绝不回落为 0
        Assert.Equal(2, row999.SalesmanCount);
        Assert.Equal(3, row999.OrderCount);         // 显式未知订单计数
        Assert.Equal(SalesmanOutputEvidenceRules.UnknownCurrencyEvidence, row999.Evidence);

        var row1000 = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "1000"));
        Assert.Null(row1000.TotalAmount);
        Assert.Equal(1, row1000.SalesmanCount);
        Assert.Equal(1, row1000.OrderCount);
    }

    // ==================== 2. 利润 / 利润率（恒 null + 历史成本依据） ====================

    [Fact]
    public void 汇总_利润与利润率恒null_附历史成本依据说明_不做当前价推算()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(new List<ReportDtos.SalesmanOutputItem>
        {
            Item(1, "USD", 100m, 1),
        });

        var row = Assert.Single(summary.CurrencyRows);
        Assert.Null(row.TotalProfit);
        Assert.Null(row.ProfitRate);
        Assert.Equal(DynamicSalesmanOutputSummaryRules.ProfitBasisText, row.ProfitBasis);

        Assert.Contains("利润未知", summary.ProfitBasisText);
        Assert.Contains("利润率未知", summary.ProfitBasisText);
    }

    [Fact]
    public void 汇总列_固定且含业务员数订单数金额利润利润率_与明细选定列无关()
    {
        var columns = DynamicSalesmanOutputSummaryRules.CurrencySummaryColumns;

        Assert.Contains(columns, c => c.Key == "currency");
        Assert.Contains(columns, c => c.Key == "salesmanCount");
        Assert.Contains(columns, c => c.Key == "orderCount");
        Assert.Contains(columns, c => c.Key == "totalAmount");
        Assert.Contains(columns, c => c.Key == "totalProfit");
        Assert.Contains(columns, c => c.Key == "profitRate");
        Assert.Contains(columns, c => c.Key == "evidence");
        Assert.Contains(columns, c => c.Key == "profitBasis");
    }

    // ==================== 3. 空来源 / 越界页 / 隐藏字段 / 单行页 ====================

    [Fact]
    public void 空来源_汇总仍存在_覆盖说明与利润依据非空()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(
            Array.Empty<ReportDtos.SalesmanOutputItem>());

        Assert.NotNull(summary);
        Assert.Empty(summary.CurrencyRows);
        Assert.NotEmpty(summary.CoverageText);
        Assert.Contains("已分配业务员", summary.CoverageText);
        Assert.Contains("实际收入", summary.CoverageText);
        Assert.NotEmpty(summary.ProfitBasisText);
    }

    [Fact]
    public void BuildPage_越界页与隐藏字段_汇总仍覆盖全部匹配行()
    {
        var items = new List<ReportDtos.SalesmanOutputItem>
        {
            Item(1, "USD", 1000m, 1),
            Item(2, "USD", 200m, 1),
            Item(3, "CNY", 500m, 1),
        };

        var page = DynamicSalesmanOutputReportRules.BuildPage(
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
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.SalesmanCount));
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.OrderCount));
    }

    [Fact]
    public void BuildPage_当前页仅一行_汇总仍覆盖全部匹配行()
    {
        var items = new List<ReportDtos.SalesmanOutputItem>
        {
            Item(1, "USD", 1000m, 1),
            Item(2, "USD", 200m, 1),
            Item(3, "CNY", 500m, 1),
        };

        var page = DynamicSalesmanOutputReportRules.BuildPage(
            items,
            new List<string> { "currency" },
            page: 1,
            pageSize: 1,
            start: new DateTime(2026, 9, 1),
            end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Single(page.Rows);                  // 当前页仅 1 行
        Assert.Equal(2, page.Summary.CurrencyRows.Count);   // 汇总仍覆盖全部匹配行
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.SalesmanCount));
    }

    [Fact]
    public void 汇总_稳定原始键排序()
    {
        var summary = DynamicSalesmanOutputSummaryRules.BuildSummary(new List<ReportDtos.SalesmanOutputItem>
        {
            Item(3, "USD", 100m, 1),
            Item(4, "CNY", 100m, 1),
            Item(2, "999", null, 1),
            Item(1, "1000", null, 1),
        });

        Assert.Equal(new[] { "1000", "999", "CNY", "USD" },
            summary.CurrencyRows.Select(r => r.Currency).ToArray());
    }

    // ==================== 4. ERP-240 汇总导出行 ====================

    [Fact]
    public void 汇总导出行_已知金额签名数值_计数为整数_未知金额利润利润率显式文本()
    {
        var export = DynamicSalesmanOutputSummaryRules.BuildCurrencySummaryExportRow(
            new DynamicSalesmanOutputCurrencySummaryDto
            {
                Currency = "USD",
                CurrencyLabel = "USD 美元",
                SalesmanCount = 2,
                OrderCount = 3,
                TotalAmount = 800m,
                TotalProfit = null,
                ProfitRate = null,
                Evidence = SalesmanOutputEvidenceRules.KnownCurrencyEvidence,
                ProfitBasis = DynamicSalesmanOutputSummaryRules.ProfitBasisText,
            });

        Assert.Equal("USD", export["currency"]);
        Assert.Equal("USD 美元", export["currencyLabel"]);
        Assert.Equal(2, export["salesmanCount"]);
        Assert.Equal(3, export["orderCount"]);
        Assert.Equal(800m, export["totalAmount"]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, export["totalProfit"]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, export["profitRate"]);
        Assert.Equal(SalesmanOutputEvidenceRules.KnownCurrencyEvidence, export["evidence"]);
        Assert.Contains("利润未知", (string)export["profitBasis"]!);
    }

    [Fact]
    public void 汇总导出行_公式前导文本转义_绝不写入数值零()
    {
        var export = DynamicSalesmanOutputSummaryRules.BuildCurrencySummaryExportRow(
            new DynamicSalesmanOutputCurrencySummaryDto
            {
                Currency = "=1+1",
                CurrencyLabel = "+USD",
                SalesmanCount = 1,
                OrderCount = 1,
                TotalAmount = null,
                TotalProfit = null,
                ProfitRate = null,
                Evidence = "@已知原币",
                ProfitBasis = "-利润未知",
            });

        Assert.Equal("'=1+1", export["currency"]);
        Assert.Equal("'+USD", export["currencyLabel"]);
        Assert.Equal("'@已知原币", export["evidence"]);
        Assert.Equal("'-利润未知", export["profitBasis"]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, export["totalAmount"]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, export["totalProfit"]);
        Assert.Equal(DynamicSalesmanOutputReportRules.UnknownValueText, export["profitRate"]);
    }
}
