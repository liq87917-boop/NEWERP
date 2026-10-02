using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-221 动态订单利润暂估字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖设计器入口、字段选择（仅目录白名单、无自由字段名 / SQL）、字段顺序 / 去重 / 丢弃未知键、
/// 有界请求组装（字段 / 开始 / 结束日期 / 分页）、日期校验（结束不早于开始、最多 366 天）、
/// 预览 / 导出端点、页面覆盖 / 原币 / 未知依据上下文、null 金额显示「未知」、授权撤销 / 空状态 / 错误 / 网络可见。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicOrderProfitEstimateReportUiTests
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
    public void 入口_订单利润暂估报表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'order-profit': { api: '/api/reports/order-profit', title: '订单利润暂估表', designer: 'order-profit',", js);
        Assert.Contains("onclick=\"openOrderProfitEstimateDesigner()\"", js);
        Assert.Contains("function openOrderProfitEstimateDesigner()", js);
        Assert.Contains("id=\"opd-designer\"", js);
        Assert.Contains("function loadOrderProfitEstimateDesignerCatalog()", js);
        Assert.Contains("function opdPreview(", js);
    }

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function opdFieldChooserHtml(", js);
        Assert.Contains("name=\"opd-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function opdSelectFields(", js);
        Assert.Contains("!valid.has(key)", js);
        Assert.DoesNotContain("opd-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;

        var fn = Segment(js, "function opdSelectFields(", "function opdDateError(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
    }

    [Fact]
    public void 请求_组装有界字段日期分页_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function opdBuildRequest(", js);
        Assert.Contains("const fields = opdSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("start: String(state.start).slice(0, 10),", js);
        Assert.Contains("end: String(state.end).slice(0, 10),", js);
    }

    [Fact]
    public void 日期校验_结束不早于开始_最多366天()
    {
        var js = Script;

        var fn = Segment(js, "function opdDateError(", "function opdBuildRequest(");
        Assert.Contains("if (!start || !end) return '请填写开始与结束日期';", fn);
        Assert.Contains("if (end < start) return '结束日期不能早于开始日期';", fn);
        Assert.Contains("+ 1 > 366) return '日期范围最多 366 天（含首尾）';", fn);
    }

    [Fact]
    public void 预览_组装有界请求_安全渲染与上下文_且null金额显示未知()
    {
        var js = Script;

        var preview = Segment(js, "async function opdPreview(", "async function opdExport(");
        Assert.Contains("opdRequest('/api/dynamic-order-profit-estimate-report', 'POST', req)", preview);
        Assert.Contains("opdErrorHtml('unauthorized', resp.message)", preview);
        Assert.Contains("opdErrorHtml(opdKindOfCode(resp.code), resp.message)", preview);
        Assert.Contains("opdErrorHtml('network', (err && err.message) || '无法连接到服务器')", preview);

        var result = Segment(js, "function opdResultHtml(", "function opdFieldChooserHtml(");
        Assert.Contains("view.pageOnlyText", result);
        Assert.Contains("view.currencyContextText", result);
        Assert.Contains("view.unknownBasisText", result);

        var cell = Segment(js, "function opdRenderCell(", "function opdTableHtml(");
        Assert.Contains("return '<span class=\"text-muted\">未知</span>'", cell);
    }

    [Fact]
    public void 导出_入口按钮与函数存在_复用有界请求体()
    {
        var js = Script;

        Assert.Contains("onclick=\"opdExport()\"", js);
        Assert.Contains("function opdExport()", js);
        Assert.Contains("/api/dynamic-order-profit-estimate-report/export", js);
        Assert.Contains("const state = opdBuildState(OPD_DYN.view.page);", js);
        Assert.Contains("const req = opdBuildRequest(state);", js);
        Assert.Contains("body: JSON.stringify(req)", js);
        Assert.Contains("导出 Excel（当前页）", js);
    }

    [Fact]
    public void 导出_成功xlsx下载_授权无效网络失败可见_要求先预览()
    {
        var js = Script;

        var fn = Segment(js, "async function opdExport()", "function opdPage(");
        Assert.Contains("请先预览后再导出 Excel", fn);
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", fn);
        Assert.Contains("URL.createObjectURL(blob)", fn);
        Assert.Contains("a.download = '订单利润暂估_'", fn);
        Assert.Contains("opdErrorHtml('unauthorized', message)", fn);
        Assert.Contains("opdErrorHtml(opdKindOfCode(code), message)", fn);
        Assert.Contains("opdErrorHtml('network', (err && err.message) || '无法连接到服务器')", fn);
    }

    [Fact]
    public void 导出PDF_入口按钮与函数存在_复用有界请求体()
    {
        var js = Script;

        Assert.Contains("onclick=\"opdExportPdf()\"", js);
        Assert.Contains("function opdExportPdf()", js);
        Assert.Contains("/api/dynamic-order-profit-estimate-report/pdf", js);
        Assert.Contains("下载 PDF（当前页）", js);

        var fn = Segment(js, "async function opdExportPdf()", "function opdPage(");
        Assert.Contains("const state = opdBuildState(OPD_DYN.view.page);", fn);
        Assert.Contains("const req = opdBuildRequest(state);", fn);
        Assert.Contains("body: JSON.stringify(req)", fn);
    }

    [Fact]
    public void 导出PDF_成功pdf下载_授权无效网络失败可见_要求先预览并保留状态()
    {
        var js = Script;

        var fn = Segment(js, "async function opdExportPdf()", "function opdPage(");
        Assert.Contains("请先预览后再下载 PDF", fn);
        Assert.Contains("contentType.indexOf('application/pdf') >= 0", fn);
        Assert.Contains("URL.createObjectURL(blob)", fn);
        Assert.Contains("a.download = '订单利润暂估_'", fn);
        Assert.Contains("opdErrorHtml('unauthorized', message)", fn);
        Assert.Contains("opdErrorHtml(opdKindOfCode(code), message)", fn);
        Assert.Contains("opdErrorHtml('network', (err && err.message) || '无法连接到服务器')", fn);
        Assert.DoesNotContain("OPD_DYN.view = null", fn);
        Assert.DoesNotContain("OPD_DYN.view = {", fn);
    }
}
