using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 业务员提成表（ERP-243）前端源契约单元测试：
/// reports.js 目录列包含币种 / 金额 / 利润 / 利润率 / 提成比例 / 提成 / 来源依据与「当前参考」标签；
/// 表格渲染把 null 金额 / 利润 / 提成显式为「未知」，提成比例标注为「当前参考」；
/// KPI 只按原币分列、绝不跨币种合计金额，也绝不把当前参考比例标成已赚提成。
/// <para>全部为源文件契约断言，不启动浏览器、不连接 SQL Server。</para>
/// </summary>
public class SalesCommissionUiTests
{
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static string FunctionText(string source, string name, string nextMarker)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到 function {name}");
        var end = source.IndexOf(nextMarker, start + 1, StringComparison.Ordinal);
        if (end < 0) end = source.Length;
        return source.Substring(start, end - start);
    }

    [Fact]
    public void 目录列与当前参考标签契约()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "reports.js"));

        Assert.Contains("'sales-commission'", js);
        Assert.Contains("{ key: 'currencyLabel', label: '原币币种' }", js);
        Assert.Contains("{ key: 'currencyEvidence', label: '币种证据' }", js);
        Assert.Contains("{ key: 'amountLabel', label: '金额口径' }", js);
        Assert.Contains("{ key: 'profitEvidence', label: '利润依据' }", js);
        Assert.Contains("{ key: 'profitRateEvidence', label: '利润率依据' }", js);
        Assert.Contains("{ key: 'commissionRateEvidence', label: '提成比例依据' }", js);
        Assert.Contains("{ key: 'commissionEvidence', label: '提成依据' }", js);
        Assert.Contains("{ key: 'sourceLabel', label: '来源依据' }", js);
        Assert.Contains("提成比例%(当前参考)", js);
    }

    [Fact]
    public void 表格渲染_未知与当前参考标签()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "reports.js"));
        var render = FunctionText(js, "renderSalesCommissionData", "function emptyReportHtml");

        Assert.Contains("未知", render);
        Assert.Contains("当前参考", render);
        Assert.Contains("来源依据", render);
        Assert.Contains("非历史约定/实际提成", render);
        Assert.DoesNotContain("已赚", render);
        Assert.DoesNotContain("已计提", render);
    }

    [Fact]
    public void KPI_绝不跨币种合计_不标提成为已赚()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "reports.js"));
        var kpi = FunctionText(js, "fillSalesCommissionKpi", "function fillOrderProfitKpi");

        Assert.Contains("原币分列呈现，绝不跨币种合计金额", kpi);
        Assert.DoesNotContain("salesAmount", kpi);
        Assert.DoesNotContain("commissionAmount", kpi);
        Assert.DoesNotContain("profit", kpi);
        Assert.DoesNotContain("reduce", kpi);
        Assert.DoesNotContain("+=", kpi);
    }
}
