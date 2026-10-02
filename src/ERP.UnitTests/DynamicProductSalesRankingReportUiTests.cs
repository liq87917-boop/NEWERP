using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-213 动态商品销量排名字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖设计器入口、字段选择（仅目录白名单、无自由字段名 / SQL）、字段顺序 / 去重 / 丢弃未知键、
/// 有界请求组装（字段 / 开始 / 结束日期 / Top）、日期与 Top 校验（结束不早于开始、最多 366 天、Top 1~200）、
/// 预览 / 导出端点、金额估算排除、单位不跨单位合计、授权撤销 / 空状态 / 错误 / 加载可见与安全文本渲染。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicProductSalesRankingReportUiTests
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
    public void 入口_商品销量排名报表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'product-sales-ranking': { api: '/api/reports/product-sales-ranking', title: '爆款 SKU 销售排行', designer: 'product-sales-ranking',", js);
        Assert.Contains("onclick=\"openProductSalesRankingDesigner()\"", js);
        Assert.Contains("function openProductSalesRankingDesigner()", js);
        Assert.Contains("id=\"psr-designer\"", js);
        Assert.Contains("function loadProductSalesRankingDesignerCatalog()", js);
        Assert.Contains("function psrPreview(", js);
    }

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function psrFieldChooserHtml(", js);
        Assert.Contains("name=\"psr-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function psrSelectFields(", js);
        Assert.Contains("!valid.has(key)", js);
        Assert.DoesNotContain("psr-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;

        var fn = Segment(js, "function psrSelectFields(", "function psrDateError(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
    }

    [Fact]
    public void 请求_组装有界字段日期Top_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function psrBuildRequest(", js);
        Assert.Contains("const fields = psrSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("start: String(state.start).slice(0, 10),", js);
        Assert.Contains("end: String(state.end).slice(0, 10),", js);
        Assert.Contains("top,", js);
        Assert.Contains("top = Math.max(1, Math.min(maxTop, top));", js);
    }

    [Fact]
    public void 日期与Top校验_结束不早于开始_最多366天_Top1到200()
    {
        var js = Script;

        var date = Segment(js, "function psrDateError(", "function psrTopError(");
        Assert.Contains("if (!start || !end) return '请填写开始与结束日期';", date);
        Assert.Contains("if (endMs < startMs) return '结束日期不能早于开始日期';", date);
        Assert.Contains("if (days > 366) return '日期范围最多 366 天（含首尾）';", date);

        var top = Segment(js, "function psrTopError(", "function psrFilterError(");
        Assert.Contains("if (n < 1 || n > maxTop) return 'Top 必须在 1 到 ' + maxTop + ' 之间';", top);
    }

    [Fact]
    public void 预览与导出_端点_金额估算排除_单位不跨单位合计_错误加载可见()
    {
        var js = Script;

        Assert.Contains("fetch('/api/dynamic-product-sales-ranking-report/export'", js);
        Assert.Contains("psrRequest('/api/dynamic-product-sales-ranking-report', 'POST', req)", js);
        Assert.Contains("a.download = '商品销量排名_'", js);
        Assert.Contains("psrErrorHtml('unauthorized'", js);
        Assert.Contains("psrErrorHtml('invalid'", js);
        Assert.Contains("psrErrorHtml('network'", js);
        Assert.Contains("psrLoadingHtml()", js);
        // 金额估算与跨单位合计明确排除
        Assert.Contains("金额估算已排除", js);
        Assert.Contains("单位不兼容不跨单位合计", js);
        Assert.Contains("绝不跨单位合计数量", js);
    }

    // ==================== PDF 下载（ERP-214） ====================

    [Fact]
    public void PDF下载_入口_设计器提供下载按钮_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("function psrExportPdf()", js);
        Assert.Contains("onclick=\"psrExportPdf()\"", js);
        Assert.Contains("/api/dynamic-product-sales-ranking-report/pdf", js);
    }

    [Fact]
    public void PDF下载_复用当前字段日期Top请求_无自由SQL_不要求先预览()
    {
        var js = Script;

        var fn = Segment(js, "async function psrExportPdf()", "/* 加载字段目录（需登录 + 商品销量排名榜菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */");
        Assert.Contains("psrBuildState()", fn);
        Assert.Contains("psrFilterError(state)", fn);
        Assert.Contains("psrBuildRequest(state)", fn);
        Assert.Contains("method: 'POST'", fn);
        Assert.DoesNotContain("PSR_DYN.view", fn);
        Assert.DoesNotContain("FromSql", fn);
        Assert.DoesNotContain("ExecuteSql", fn);
        Assert.DoesNotContain("SqlCommand", fn);
        Assert.DoesNotContain("localStorage.setItem", fn);
    }

    [Fact]
    public void PDF下载_成功下载pdf附件_授权无效网络失败可见()
    {
        var js = Script;

        var fn = Segment(js, "async function psrExportPdf()", "/* 加载字段目录（需登录 + 商品销量排名榜菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */");
        Assert.Contains("contentType.indexOf('application/pdf') >= 0", fn);
        Assert.Contains("URL.createObjectURL(blob)", fn);
        Assert.Contains("a.download = '商品销量排名_'", fn);
        Assert.Contains("psrErrorHtml('unauthorized', message)", fn);
        Assert.Contains("psrErrorHtml('invalid', filterError)", fn);
        Assert.Contains("psrErrorHtml(psrKindOfCode(code), message)", fn);
        Assert.Contains("psrErrorHtml('network', (err && err.message) || '无法连接到服务器')", fn);
    }
}