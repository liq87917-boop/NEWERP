using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-216 动态商品销量排名「按单位分组」前端 UI 契约测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖分组键白名单（none / unit，无自由文本与 SQL）、分组键进入有界请求体（非法分组钳制为 none）、
/// 按单位独立呈现排名桶数与签名数量小计（未知单位数量为 null / 显示为空）、空分组不渲染、无跨单位总计与比较图。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicProductSalesRankingGroupUiTests
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
    public void 分组选择器_仅none_unit_无自由文本与SQL()
    {
        var js = Script;

        Assert.Contains("function psrGroupKey(", js);
        var fn = Segment(js, "function psrGroupKey(", "/* 读取当前字段");
        Assert.Contains("'none'", fn);
        Assert.Contains("'unit'", fn);

        Assert.Contains("id=\"psr-des-group\"", js);
        Assert.Contains("<option value=\"none\">不分组</option>", js);
        Assert.Contains("<option value=\"unit\">按单位</option>", js);
        Assert.DoesNotContain("psr-des-group\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", fn);
        Assert.DoesNotContain("ExecuteSql", fn);
        Assert.DoesNotContain("SqlCommand", fn);
    }

    [Fact]
    public void 请求_组装_分组键进入请求体_非法分组钳制为none()
    {
        var js = Script;

        Assert.Contains("groupBy: val('psr-des-group'),", js);

        var fn = Segment(js, "function psrBuildRequest(", "/* 单元格纯文本");
        Assert.Contains("const groupBy = psrGroupKey(state.groupBy);", fn);
        Assert.Contains("if (groupBy && groupBy !== 'none') req.groupBy = groupBy;", fn);
        Assert.Contains("return req;", fn);
    }

    [Fact]
    public void 分组渲染_按单位独立呈现桶数与签名数量_未知单位null数量_无跨单位总计与比较图()
    {
        var js = Script;

        var fn = Segment(js, "function psrGroupsHtml(", "/* 错误提示");
        Assert.Contains("if (!groupBy || groupBy === 'none' || !Array.isArray(groups) || groups.length === 0) return '';", fn);
        Assert.Contains("groupContextText", fn);
        Assert.Contains("rankingBucketCount", fn);
        Assert.Contains("totalQuantity !== null", fn);
        Assert.Contains("—", fn);
        Assert.Contains("<table>", fn);
        Assert.Contains("排名桶数", fn);
        Assert.Contains("同单位数量小计", fn);
        // 无跨单位总计、无比较图（条形图缩放 / 列表）
        Assert.DoesNotContain("合计", fn);
        Assert.DoesNotContain("总计", fn);
        Assert.DoesNotContain("<ul", fn);
        Assert.DoesNotContain("Math.max", fn);
    }

    [Fact]
    public void 结果渲染_接入分组_空分组不渲染()
    {
        var js = Script;

        var fn = Segment(js, "function psrResultHtml(", "/* 同步勾选状态");
        Assert.Contains("const groups = psrGroupsHtml(view);", fn);
        Assert.Contains("${groups}", fn);
    }
}
