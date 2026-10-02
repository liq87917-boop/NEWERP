using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-206 动态报价成交率字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖设计器入口、字段选择（仅目录白名单、无自由字段名 / SQL）、字段顺序 / 去重 / 丢弃未知键、
/// 有界请求组装（字段 / 开始 / 结束日期 / 分页）、日期校验（结束不早于开始、最多 366 天）、
/// 预览 / 导出端点、授权撤销 / 空状态 / 错误 / 加载可见与安全文本渲染。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicQuotationConversionReportUiTests
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
    public void 入口_报价成交率报表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'quotation-conversion': { api: '/api/reports/quotation-conversion', title: '报价成交率分析', designer: 'quotation',", js);
        Assert.Contains("onclick=\"openQuotationConversionDesigner()\"", js);
        Assert.Contains("function openQuotationConversionDesigner()", js);
        Assert.Contains("id=\"qcd-designer\"", js);
        Assert.Contains("function loadQuotationConversionDesignerCatalog()", js);
        Assert.Contains("function qcdPreview(", js);
    }

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function qcdFieldChooserHtml(", js);
        Assert.Contains("name=\"qcd-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function qcdSelectFields(", js);
        Assert.Contains("!valid.has(key)", js);
        Assert.DoesNotContain("qcd-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;

        var fn = Segment(js, "function qcdSelectFields(", "function qcdDateError(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
    }

    [Fact]
    public void 请求_组装有界字段日期分页_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function qcdBuildRequest(", js);
        Assert.Contains("const fields = qcdSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("start: String(state.start).slice(0, 10),", js);
        Assert.Contains("end: String(state.end).slice(0, 10),", js);
    }

    [Fact]
    public void 日期校验_结束不早于开始_最多366天()
    {
        var js = Script;

        var fn = Segment(js, "function qcdDateError(", "function qcdBuildRequest(");
        Assert.Contains("if (!start || !end) return '请填写开始与结束日期';", fn);
        Assert.Contains("if (end < start) return '结束日期不能早于开始日期';", fn);
        Assert.Contains("> 366) return '日期范围最多 366 天（含首尾）';", fn);
    }

    [Fact]
    public void 预览与导出_端点与安全状态可见()
    {
        var js = Script;

        Assert.Contains("/api/dynamic-quotation-conversion-report", js);
        Assert.Contains("/api/dynamic-quotation-conversion-report/export", js);
        Assert.Contains("function qcdPreview(", js);
        Assert.Contains("function qcdExport()", js);

        var preview = Segment(js, "function qcdPreview(", "function qcdExport()");
        Assert.Contains("qcdErrorHtml('invalid', dateError)", preview);
        Assert.Contains("qcdErrorHtml('unauthorized', resp.message)", preview);
        Assert.Contains("qcdErrorHtml(qcdKindOfCode(resp.code), resp.message)", preview);
        Assert.Contains("qcdErrorHtml('network', (err && err.message) || '无法连接到服务器')", preview);
        Assert.Contains("qcdResultHtml(resp.data)", preview);
    }

    [Fact]
    public void 导出_下载xlsx附件_授权无效空结果网络失败可见()
    {
        var js = Script;

        var fn = Segment(js, "function qcdExport()", "function qcdPage(");
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", fn);
        Assert.Contains("URL.createObjectURL(blob)", fn);
        Assert.Contains("a.download = '报价成交率_'", fn);
        Assert.Contains("qcdErrorHtml('unauthorized', message)", fn);
        Assert.Contains("qcdErrorHtml('invalid', '请先预览后再导出 Excel')", fn);
        Assert.Contains("qcdErrorHtml('empty', '没有符合所选日期范围的报价成交率数据，无法导出（请先预览）')", fn);
        Assert.Contains("qcdErrorHtml(qcdKindOfCode(code), message)", fn);
        Assert.Contains("qcdErrorHtml('network', (err && err.message) || '无法连接到服务器')", fn);
    }

    [Fact]
    public void 安全渲染_全部转义_不缓存原始数据_无自由SQL()
    {
        var js = Script;

        Assert.Contains("function qcdEsc(", js);
        Assert.Contains("qcdRenderCell(r[c.key], c)", js);
        Assert.Contains("qcdEsc(c.label || c.key)", js);

        var chooser = Segment(js, "function qcdFieldChooserHtml(", "function qcdLoadingHtml(");
        Assert.DoesNotContain("FromSql", chooser);
        Assert.DoesNotContain("ExecuteSql", chooser);
        Assert.DoesNotContain("SqlCommand", chooser);
        Assert.DoesNotContain("localStorage.setItem", chooser);
    }
}
