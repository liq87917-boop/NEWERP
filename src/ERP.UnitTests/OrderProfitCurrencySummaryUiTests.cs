using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-224 动态订单利润暂估字段设计器「分币种汇总」前端 UI 契约测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言：汇总独立于当前页明细行 / 选定列渲染，
/// 仅渲染后端返回的 <c>view.summary</c> 列与指标并全部转义，空 / 无汇总不渲染，并在结果区插入汇总。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class OrderProfitCurrencySummaryUiTests
{
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static string Script =>
        File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "reports.js"));

    private static string Segment(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        return j < 0 ? source[i..] : source[i..j];
    }

    [Fact]
    public void 汇总函数_存在且只读后端summary列与指标()
    {
        var js = Script;

        Assert.Contains("function opdSummaryHtml(", js);
        var fn = Segment(js, "function opdSummaryHtml(", "function opdResultHtml(");
        Assert.Contains("view && view.summary", fn);
        Assert.Contains("summary && summary.columns", fn);
        Assert.Contains("summary && summary.rows", fn);
        Assert.Contains("summary.coverageText", fn);
        // 只渲染后端返回的 summary 列与指标，绝不读取当前页明细行或选定列
        Assert.DoesNotContain("view.rows", fn);
        Assert.DoesNotContain("view.columns", fn);
    }

    [Fact]
    public void 汇总渲染_全部转义_未知金额显示未知_不跨币种合计()
    {
        var js = Script;
        var fn = Segment(js, "function opdSummaryHtml(", "function opdResultHtml(");

        Assert.Contains("opdEsc(c.label || c.key)", fn);
        Assert.Contains("opdRenderCell(r[c.key], c)", fn);
        Assert.Contains("opdEsc(summary.coverageText)", fn);
        Assert.Contains("分币种汇总", fn);
        Assert.Contains("按订单原币合并全部匹配已审核订单", fn);
    }

    [Fact]
    public void 结果区_插入分币种汇总_独立于当前页明细与选定列()
    {
        var js = Script;
        var fn = Segment(js, "function opdResultHtml(", "function opdFieldChooserHtml(");

        Assert.Contains("const currencySummary = opdSummaryHtml(view);", fn);
        Assert.Contains("filterLine + ctx + currencySummary", fn);
        Assert.Contains("filterLine + ctx + currencySummary + opdEmptyHtml(view)", fn);
        Assert.Contains("filterLine + ctx + currencySummary + opdTableHtml(view) + opdPagingHtml(view)", fn);
    }
}
