using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-252 动态柜量与装柜利用率证据字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言，覆盖设计器入口、字段选择（仅目录白名单、无自由字段名 / SQL）、
/// 字段顺序 / 去重、有界请求组装（字段 / 开始结束日期 / 客户 / 柜号关键字 / 分页）、
/// 字段与筛选变更重置页、失败清空旧数据、上下文始终显示与 HTML 转义。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicContainerStatsReportUiTests
{
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static string Script =>
        File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "reports.js"));

    /// <summary>截取源码中两个锚点之间的片段，便于对单个函数做「不含某内容」的契约断言。</summary>
    private static string Segment(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        return j < 0 ? source[i..] : source[i..j];
    }

    // ==================== 1. 设计器入口 ====================

    [Fact]
    public void 入口_柜量统计_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'container-stats': { api: '/api/reports/container-stats', title: '柜量与装柜利用率统计', designer: 'container-stats',", js);
        Assert.Contains("onclick=\"openContainerStatsDesigner()\"", js);
        Assert.Contains("function openContainerStatsDesigner()", js);
        Assert.Contains("id=\"cst-designer\"", js);
        Assert.Contains("function loadContainerStatsDesignerCatalog()", js);
        Assert.Contains("function cstPreview(", js);
    }

    // ==================== 2. 字段选择（仅目录白名单、无自由输入 / SQL） ====================

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function cstFieldChooserHtml(", js);
        Assert.Contains("name=\"cst-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function cstSelectFields(", js);
        Assert.Contains("valid.has(key)", js);
        Assert.Contains("seen.has(key)", js);
        Assert.DoesNotContain("cst-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;
        var fn = Segment(js, "function cstSelectFields(", "function cstDateError(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
        Assert.Contains("continue;", fn);
    }

    // ==================== 3. 有界请求（字段 / 日期 / 客户 / 柜号关键字 / 分页） ====================

    [Fact]
    public void 请求_组装有界字段日期分页与筛选_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function cstBuildRequest(", js);
        Assert.Contains("const fields = cstSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("filter: cstBuildFilter(state),", js);
    }

    // ==================== 4. 筛选（客户 Id / 柜号关键字，字面、重置页） ====================

    [Fact]
    public void 筛选_客户Id与柜号关键字输入框存在_变更重置页_字面转义()
    {
        var js = Script;

        Assert.Contains("id=\"cst-des-customer-id\"", js);
        Assert.Contains("min=\"1\"", js);
        Assert.Contains("id=\"cst-des-container-no\"", js);
        Assert.Contains("maxlength=\"80\"", js);
        Assert.Contains("onchange=\"cstResetPage()\"", js);

        var bf = Segment(js, "function cstBuildFilter(", "function cstResetPage(");
        Assert.Contains("filter.customerId = Number(customerId)", bf);
        Assert.Contains("filter.containerNo = containerNo", bf);
        Assert.DoesNotContain("FromSql", bf);
        Assert.DoesNotContain("SqlCommand", bf);
    }

    // ==================== 5. 状态维护 / 失败清空 / 转义 / 上下文始终显示 ====================

    [Fact]
    public void 状态_字段筛选变更重置页_失败清空旧数据_上下文始终显示()
    {
        var js = Script;

        var rp = Segment(js, "function cstResetPage(", "function cstCellText(");
        Assert.Contains("CST_DYN.page = 1", rp);
        Assert.Contains("CST_DYN.view = null", rp);

        var result = Segment(js, "function cstResultHtml(", "function cstFieldChooserHtml(");
        Assert.Contains("view.unitContextText", result);
        Assert.Contains("view.unknownCapacityContextText", result);
        Assert.Contains("view.typeContextText", result);
        Assert.Contains("view.shippingContextText", result);
        Assert.Contains("view.sourceContextText", result);
        Assert.Contains("view.sourceLimitText", result);
        Assert.Contains("view.filterText", result);
    }

    [Fact]
    public void 渲染_表头与单元格全部转义_分页覆盖总页数与截断()
    {
        var js = Script;

        Assert.Contains("function cstEsc(", js);
        Assert.Contains(".replace(/[&<>\"']/g", js);

        var table = Segment(js, "function cstTableHtml(", "function cstPagingHtml(");
        Assert.Contains("cstEsc(c.label || c.key)", table);
        Assert.Contains("cstRenderCell(r[c.key], c)", table);

        var paging = Segment(js, "function cstPagingHtml(", "function cstEmptyHtml(");
        Assert.Contains("view.totalPages", paging);
        Assert.Contains("view.truncated", paging);
    }

    // ==================== 6. 导出 Excel（ERP-253） ====================

    [Fact]
    public void 导出_入口按钮与函数_复用当前字段日期筛选分页状态_失败不下载旧行()
    {
        var js = Script;

        Assert.Contains("onclick=\"cstExportExcel()\"", js);
        Assert.Contains("function cstExportExcel()", js);

        var fn = Segment(js, "function cstExportExcel()", "function cstPage(");
        Assert.Contains("'/api/dynamic-container-stats-report/export'", fn);
        Assert.Contains("method: 'POST'", fn);
        Assert.Contains("const state = cstBuildState(CST_DYN.view.page);", fn);
        Assert.Contains("const req = cstBuildRequest(state);", fn);
        Assert.Contains("body: JSON.stringify(req)", fn);

        // 成功路径：识别 xlsx 附件并触发下载，绝不使用旧预览行
        Assert.Contains("spreadsheetml", fn);
        Assert.Contains("resp.blob()", fn);
        Assert.Contains("URL.createObjectURL(blob)", fn);
        Assert.Contains("a.download", fn);

        // 失败路径：授权 / 无效 / 来源超限 / 网络失败可见，不下载任何内容
        Assert.Contains("cstErrorHtml(cstKindOfCode(code), message)", fn);
        Assert.Contains("cstErrorHtml('network'", fn);
        Assert.Contains("请先预览后再导出 Excel", fn);

        Assert.DoesNotContain("FromSql", fn);
        Assert.DoesNotContain("SqlCommand", fn);
        Assert.DoesNotContain("ExecuteSql", fn);
    }

    // ==================== 7. 下载 PDF（ERP-254） ====================

    [Fact]
    public void 下载PDF_入口按钮与函数_复用当前字段日期筛选分页状态_失败不下载旧行()
    {
        var js = Script;

        Assert.Contains("onclick=\"cstExportPdf()\"", js);
        Assert.Contains("function cstExportPdf()", js);

        var fn = Segment(js, "function cstExportPdf()", "function cstPage(");
        Assert.Contains("'/api/dynamic-container-stats-report/pdf'", fn);
        Assert.Contains("method: 'POST'", fn);
        Assert.Contains("const state = cstBuildState(CST_DYN.view.page);", fn);
        Assert.Contains("const req = cstBuildRequest(state);", fn);
        Assert.Contains("body: JSON.stringify(req)", fn);

        // 成功路径：识别 pdf 附件并触发下载，绝不使用旧预览行
        Assert.Contains("application/pdf", fn);
        Assert.Contains("resp.blob()", fn);
        Assert.Contains("URL.createObjectURL(blob)", fn);
        Assert.Contains("a.download", fn);

        // 失败路径：授权 / 无效 / 来源超限 / 字体缺失 / 渲染失败 / 网络失败可见，不下载任何内容
        Assert.Contains("cstErrorHtml(cstKindOfCode(code), message)", fn);
        Assert.Contains("cstErrorHtml('network'", fn);
        Assert.Contains("请先预览后再下载 PDF", fn);

        Assert.DoesNotContain("FromSql", fn);
        Assert.DoesNotContain("SqlCommand", fn);
        Assert.DoesNotContain("ExecuteSql", fn);
    }

    // ==================== 8. 全匹配汇总（ERP-255） ====================

    [Fact]
    public void 汇总_全匹配汇总_独立于当前页明细_转义()
    {
        var js = Script;

        Assert.Contains("function cstSummaryHtml(", js);
        var fn = Segment(js, "function cstSummaryHtml(", "function cstFieldChooserHtml(");
        Assert.Contains("view.summary", fn);
        Assert.Contains("summary.dailyRows", fn);
        Assert.Contains("summary.period", fn);
        Assert.Contains("summary.noEvidenceContext", fn);
        Assert.Contains("cstEsc(", fn);

        var result = Segment(js, "function cstResultHtml(", "function cstFieldChooserHtml(");
        Assert.Contains("cstSummaryHtml(view)", result);
    }

    [Fact]
    public void 汇总_失败清空_错误态不残留旧汇总()
    {
        var js = Script;

        var err = Segment(js, "function cstErrorHtml(", "function cstKindOfCode(");
        Assert.DoesNotContain("summary", err);
        Assert.DoesNotContain("cstSummaryHtml", err);

        var rp = Segment(js, "function cstResetPage(", "function cstCellText(");
        Assert.Contains("CST_DYN.view = null", rp);
    }
}
