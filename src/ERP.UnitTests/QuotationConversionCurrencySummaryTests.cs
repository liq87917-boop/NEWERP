using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-209 动态报价成交率报表「分币种汇总」聚焦单元测试（纯规则，无数据库依赖）。
/// 覆盖：按规范化原币键合并（仅相等币种键、绝不跨币种合计）、未知币种显式保留、
/// 字段选择（仅返回选定指标、省略未选金额 / 计数）、成交率与单笔成交均价按合计值计算（绝不按业务员平均）、
/// 零分母、空结果，以及汇总与页码 / 页大小无关。
/// <para>不连接 SQL Server、不启动 API、不执行任何 SQL / seed；来源超限时的「无任何汇总 / 部分结果」由
/// <see cref="DynamicQuotationConversionReportTests"/> 中既有来源超限用例覆盖（服务先抛错，绝不构建分页或汇总）。</para>
/// </summary>
public class QuotationConversionCurrencySummaryTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static ReportDtos.QuotationConversionItem Item(
        string currency,
        string salesmanName = "业务员",
        int quotationCount = 0,
        int convertedCount = 0,
        decimal conversionRate = 0,
        int expiredCount = 0,
        int cancelledCount = 0,
        decimal totalAmount = 0,
        decimal convertedAmount = 0,
        decimal avgConvertedAmount = 0)
        => new()
        {
            SalesmanName = salesmanName,
            Currency = currency,
            QuotationCount = quotationCount,
            ConvertedCount = convertedCount,
            ConversionRate = conversionRate,
            ExpiredCount = expiredCount,
            CancelledCount = cancelledCount,
            TotalAmount = totalAmount,
            ConvertedAmount = convertedAmount,
            AvgConvertedAmount = avgConvertedAmount,
        };

    private static DynamicQuotationConversionSummaryDto Build(
        IEnumerable<ReportDtos.QuotationConversionItem> items,
        IEnumerable<string>? fieldKeys = null)
        => DynamicQuotationConversionReportRules.BuildSummary(
            items.ToList(),
            fieldKeys?.ToList() ?? DynamicQuotationConversionReportRules.AllFieldKeys);

    // ==================== 按币种合并 / 不跨币种 ====================

    [Fact]
    public void 汇总_按规范化币种合并_仅相等币种键_不跨币种合计()
    {
        var summary = Build(new[]
        {
            Item("usd", quotationCount: 2, convertedCount: 1, totalAmount: 100m, convertedAmount: 50m),
            Item("USD", quotationCount: 3, convertedCount: 1, totalAmount: 200m, convertedAmount: 100m),
            Item("EUR", quotationCount: 5, convertedCount: 4, totalAmount: 500m, convertedAmount: 400m),
        });

        Assert.Equal(2, summary.CurrencyCount);
        Assert.Equal(new[] { "EUR", "USD" }, summary.Rows.Select(r => r.Currency).ToArray());

        var usd = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(5, usd.QuotationCount);
        Assert.Equal(2, usd.ConvertedCount);
        Assert.Equal(300m, usd.TotalAmount);
        Assert.Equal(150m, usd.ConvertedAmount);

        var eur = summary.Rows.Single(r => r.Currency == "EUR");
        Assert.Equal(5, eur.QuotationCount);
        Assert.Equal(400m, eur.ConvertedAmount);

        // 绝不出现混合币种合计行
        Assert.DoesNotContain(summary.Rows, r => string.IsNullOrWhiteSpace(r.Currency));
    }

    [Fact]
    public void 汇总_未知币种_显式保留为独立桶()
    {
        var summary = Build(new[]
        {
            Item("", quotationCount: 1, totalAmount: 10m),          // 空值 → 未知币种
            Item("未知币种", quotationCount: 2, totalAmount: 20m),   // 显式未知桶 → 未知币种
            Item("USD", quotationCount: 3, totalAmount: 30m),
        });

        Assert.Equal(2, summary.CurrencyCount);
        var unknown = summary.Rows.Single(r => r.Currency == "未知币种");
        Assert.Equal(3, unknown.QuotationCount);
        Assert.Equal(30m, unknown.TotalAmount);
        Assert.Contains(summary.Rows, r => r.Currency == "USD");
    }

    // ==================== 字段选择 / 省略未选指标 ====================

    [Fact]
    public void 汇总_字段选择_仅返回选定指标_省略未选金额()
    {
        var summary = Build(
            new[] { Item("USD", quotationCount: 2, convertedCount: 1, totalAmount: 100m, convertedAmount: 50m) },
            new[] { "currency", "quotationCount" });

        var row = Assert.Single(summary.Rows);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(2, row.QuotationCount);

        // 未选定的金额 / 计数指标省略为 null
        Assert.Null(row.TotalAmount);
        Assert.Null(row.ConvertedAmount);
        Assert.Null(row.AvgConvertedAmount);
        Assert.Null(row.ConvertedCount);
        Assert.Null(row.ConversionRate);
        Assert.Null(row.ExpiredCount);
        Assert.Null(row.CancelledCount);

        Assert.Equal(new[] { "currency", "quotationCount" },
            summary.Columns.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 汇总_列_仅币种加选定指标_保持请求顺序()
    {
        var summary = Build(
            new[] { Item("USD", quotationCount: 1) },
            new[] { "convertedAmount", "quotationCount", "salesmanName", "currency" });

        Assert.Equal(new[] { "currency", "convertedAmount", "quotationCount" },
            summary.Columns.Select(c => c.Key).ToArray());
    }

    // ==================== 派生比率按合计值计算 ====================

    [Fact]
    public void 汇总_成交率_按已转出总数除有效总数_绝不按业务员平均()
    {
        // 业务员 A：有效 2 / 已转出 2（100%）；业务员 B：有效 8 / 已转出 0（0%）。
        // 合计：有效 10 / 已转出 2 → 20%；若错误地按业务员成交率平均会得到 50%。
        var summary = Build(new[]
        {
            Item("USD", "A", quotationCount: 2, convertedCount: 2, totalAmount: 200m, convertedAmount: 200m),
            Item("USD", "B", quotationCount: 8, convertedCount: 0, totalAmount: 800m, convertedAmount: 0m),
        });

        Assert.Equal(20m, summary.Rows.Single(r => r.Currency == "USD").ConversionRate);
    }

    [Fact]
    public void 汇总_单笔成交均价_按金额总数除已转出总数_绝不按业务员平均()
    {
        // 业务员 A：1 笔 / 100 元（均价 100）；业务员 B：3 笔 / 900 元（均价 300）。
        // 合计：4 笔 / 1000 元 → 250；若错误地按业务员均价平均会得到 200。
        var summary = Build(new[]
        {
            Item("USD", "A", quotationCount: 4, convertedCount: 1, totalAmount: 1000m, convertedAmount: 100m),
            Item("USD", "B", quotationCount: 4, convertedCount: 3, totalAmount: 1000m, convertedAmount: 900m),
        });

        Assert.Equal(250m, summary.Rows.Single(r => r.Currency == "USD").AvgConvertedAmount);
    }

    [Fact]
    public void 汇总_零分母_成交率与均价为0()
    {
        var summary = Build(new[]
        {
            Item("USD", quotationCount: 0, convertedCount: 0, totalAmount: 0m, convertedAmount: 0m),
        });

        var row = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(0m, row.ConversionRate);
        Assert.Equal(0m, row.AvgConvertedAmount);
    }

    // ==================== 空结果 / 分页无关 ====================

    [Fact]
    public void 汇总_空结果_返回空行且覆盖文案仍存在()
    {
        var summary = Build(Array.Empty<ReportDtos.QuotationConversionItem>());

        Assert.Equal(0, summary.CurrencyCount);
        Assert.Empty(summary.Rows);
        Assert.Equal(DynamicQuotationConversionReportRules.CurrencySummaryCoverageText, summary.CoverageText);
        // 空结果仍保留「币种 + 全部选定指标」列头（与字段选择一致），但无任何汇总行
        Assert.Equal(
            new[] { "currency", "quotationCount", "convertedCount", "conversionRate", "expiredCount", "cancelledCount", "totalAmount", "convertedAmount", "avgConvertedAmount" },
            summary.Columns.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 汇总_与页码页大小无关()
    {
        var items = new[]
        {
            Item("USD", "A", quotationCount: 2, convertedCount: 1, totalAmount: 100m, convertedAmount: 50m),
            Item("USD", "B", quotationCount: 3, convertedCount: 1, totalAmount: 200m, convertedAmount: 100m),
            Item("EUR", "C", quotationCount: 5, convertedCount: 4, totalAmount: 500m, convertedAmount: 400m),
        };
        var keys = new[] { "currency", "quotationCount", "convertedCount", "totalAmount", "convertedAmount" };

        var page1 = DynamicQuotationConversionReportRules.BuildPage(items, keys, 1, 1, Start, End);
        var page2 = DynamicQuotationConversionReportRules.BuildPage(items, keys, 2, 1, Start, End);

        Assert.NotNull(page1.Summary);
        Assert.NotNull(page2.Summary);
        Assert.Equal(page1.Summary!.CurrencyCount, page2.Summary!.CurrencyCount);

        foreach (var expected in page1.Summary!.Rows)
        {
            var actual = page2.Summary!.Rows.Single(r => r.Currency == expected.Currency);
            Assert.Equal(expected.QuotationCount, actual.QuotationCount);
            Assert.Equal(expected.ConvertedCount, actual.ConvertedCount);
            Assert.Equal(expected.TotalAmount, actual.TotalAmount);
            Assert.Equal(expected.ConvertedAmount, actual.ConvertedAmount);
        }
    }
}
