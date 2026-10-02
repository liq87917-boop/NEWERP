using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-223 动态订单利润暂估报表「客户 Id / 原币币种」筛选的纯规则单元测试：
/// 筛选规范化（fail closed：客户 Id 正整数、币种仅已知枚举码、数字 / 未知取值拒绝）、
/// 规范化筛选上下文文案，以及空筛选兼容（全部留空 = null = 不过滤）。
/// <para>纯规则测试无数据库依赖，不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicOrderProfitEstimateFilterTests
{
    [Fact]
    public void NormalizeFilter_留空或null_返回null表示不过滤()
    {
        Assert.Null(DynamicOrderProfitEstimateReportRules.NormalizeFilter(null));
        Assert.Null(DynamicOrderProfitEstimateReportRules.NormalizeFilter(new OrderProfitEstimateFilterDto()));
        Assert.Null(DynamicOrderProfitEstimateReportRules.NormalizeFilter(
            new OrderProfitEstimateFilterDto { CustomerId = null, Currency = "  " }));
    }

    [Fact]
    public void NormalizeFilter_客户Id保留_币种归一化为枚举名()
    {
        var filter = DynamicOrderProfitEstimateReportRules.NormalizeFilter(
            new OrderProfitEstimateFilterDto { CustomerId = 3L, Currency = " usd " });

        Assert.Equal(3L, filter!.CustomerId);
        Assert.Equal("USD", filter.Currency);
    }

    [Fact]
    public void NormalizeFilter_客户Id非正整数_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            DynamicOrderProfitEstimateReportRules.NormalizeFilter(new OrderProfitEstimateFilterDto { CustomerId = 0 }));
        Assert.Throws<BusinessException>(() =>
            DynamicOrderProfitEstimateReportRules.NormalizeFilter(new OrderProfitEstimateFilterDto { CustomerId = -1 }));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("BTC")]
    [InlineData("人民币")]
    public void NormalizeFilter_数字或未知币种_拒绝(string currency)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicOrderProfitEstimateReportRules.NormalizeFilter(new OrderProfitEstimateFilterDto { Currency = currency }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(" cny ", "CNY")]
    [InlineData("usd", "USD")]
    [InlineData("EUR", "EUR")]
    [InlineData("HKD", "HKD")]
    [InlineData("gbp", "GBP")]
    [InlineData("jpy", "JPY")]
    public void NormalizeCurrencyFilter_已知枚举码_去空白大写归一化(string raw, string expected)
    {
        Assert.Equal(expected, DynamicOrderProfitEstimateReportRules.NormalizeCurrencyFilter(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeCurrencyFilter_留空_返回null(string? raw)
    {
        Assert.Null(DynamicOrderProfitEstimateReportRules.NormalizeCurrencyFilter(raw));
    }

    [Fact]
    public void BuildFilterContext_无筛选_返回空串()
    {
        Assert.Equal(string.Empty, DynamicOrderProfitEstimateReportRules.BuildFilterContext(null));
        Assert.Equal(string.Empty, DynamicOrderProfitEstimateReportRules.BuildFilterContext(new OrderProfitEstimateFilterDto()));
    }

    [Fact]
    public void BuildFilterContext_客户与币种_准确标注规范化筛选()
    {
        var filter = DynamicOrderProfitEstimateReportRules.NormalizeFilter(
            new OrderProfitEstimateFilterDto { CustomerId = 7L, Currency = "eur" });

        var text = DynamicOrderProfitEstimateReportRules.BuildFilterContext(filter);
        Assert.Contains("客户 Id 7", text);
        Assert.Contains("原币币种 EUR", text);
    }
}
