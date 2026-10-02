using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-237 动态业务员产值证据字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言，覆盖设计器入口、字段选择（仅目录白名单、无自由字段名 / SQL）、
/// 字段顺序 / 去重、有界请求组装（字段 / 开始结束日期 / 客户 / 业务员 / 原币 / 分页）、
/// 原币 / 未知 / 未知利润 / 来源上下文始终显示、空状态 / 错误可见、Excel 导出下载与公式安全。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicSalesmanOutputReportUiTests
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
    public void 入口_业务员产值报表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'salesman-output': { api: '/api/reports/salesman-output', title: '业务员产值报表', designer: 'salesman-output',", js);
        Assert.Contains("onclick=\"openSalesmanOutputDesigner()\"", js);
        Assert.Contains("function openSalesmanOutputDesigner()", js);
        Assert.Contains("id=\"sod-designer\"", js);
        Assert.Contains("function loadSalesmanOutputDesignerCatalog()", js);
        Assert.Contains("function sodPreview(", js);
    }

    // ==================== 2. 字段选择（仅目录白名单、无自由输入 / SQL） ====================

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function sodFieldChooserHtml(", js);
        Assert.Contains("name=\"sod-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function sodSelectFields(", js);
        Assert.Contains("valid.has(key)", js);
        Assert.Contains("seen.has(key)", js);
        Assert.DoesNotContain("sod-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;
        var fn = Segment(js, "function sodSelectFields(", "function sodDateError(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
        Assert.Contains("continue;", fn);
    }

    // ==================== 3. 有界请求（字段 / 日期 / 客户 / 业务员 / 原币 / 分页） ====================

    [Fact]
    public void 请求_组装有界字段日期分页与三组筛选_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function sodBuildRequest(", js);
        Assert.Contains("const fields = sodSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("filter: sodBuildFilter(state),", js);
        Assert.Contains("function sodBuildFilter(", js);
        Assert.Contains("filter.customerId", js);
        Assert.Contains("filter.salesmanId", js);
        Assert.Contains("filter.currency", js);
    }

    [Fact]
    public void 筛选校验_客户业务员正整数_币种仅已知枚举()
    {
        var js = Script;
        var fn = Segment(js, "function sodFilterError(", "function sodBuildFilter(");
        Assert.Contains("客户 Id 必须是正整数", fn);
        Assert.Contains("业务员 Id 必须是正整数", fn);
        Assert.Contains("/^(CNY|USD|EUR|HKD|GBP|JPY)$/i", fn);
    }

    // ==================== 4. 上下文始终显示（独立于选定列） ====================

    [Fact]
    public void 结果区_原币未知利润来源与分配订单上下文始终显示()
    {
        var js = Script;

        Assert.Contains("function sodResultHtml(", js);
        Assert.Contains("function sodScopeLine(", js);
        Assert.Contains("view.currencyContextText", js);
        Assert.Contains("view.unknownContextText", js);
        Assert.Contains("view.profitContextText", js);
        Assert.Contains("view.sourceContextText", js);
        Assert.Contains("view.pageOnlyText", js);
        Assert.Contains("salesmanCurrencyRows", js);
        Assert.Contains("uniqueSalesmen", js);
        Assert.Contains("assignedApprovedOrders", js);
        Assert.Contains("evidenceBasis", js);
    }

    // ==================== 5. 错误状态与导出 ====================

    [Fact]
    public void 错误状态_未登录权限不足无效网络分别可见()
    {
        var js = Script;

        Assert.Contains("function sodErrorHtml(", js);
        Assert.Contains("function sodKindOfCode(", js);
        Assert.Contains("'forbidden'", js);
        Assert.Contains("'unauthorized'", js);
        Assert.Contains("'network'", js);
        Assert.Contains("'invalid'", js);
        Assert.Contains("无法连接到服务器", js);
    }

    [Fact]
    public void 导出_复用预览请求体_仅下载xlsx_先预览守卫()
    {
        var js = Script;

        Assert.Contains("function sodExport()", js);
        Assert.Contains("/api/dynamic-salesman-output-report/export", js);
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", js);
        Assert.Contains("请先预览后再导出 Excel", js);

        var fn = Segment(js, "function sodExport()", "function loadSalesmanOutputDesignerCatalog()");
        Assert.Contains("sodBuildRequest(state)", fn);
    }

    [Fact]
    public void 导出PDF_复用预览请求体_仅下载pdf_先预览守卫()
    {
        var js = Script;

        Assert.Contains("function sodExportPdf()", js);
        Assert.Contains("/api/dynamic-salesman-output-report/pdf", js);
        Assert.Contains("contentType.indexOf('application/pdf') >= 0", js);
        Assert.Contains("请先预览后再导出 PDF", js);

        var fn = Segment(js, "function sodExportPdf()", "function loadSalesmanOutputDesignerCatalog()");
        Assert.Contains("sodBuildRequest(state)", fn);
    }

    [Fact]
    public void 导出PDF按钮_设计器工具栏提供_并保留当前状态()
    {
        var js = Script;
        Assert.Contains("onclick=\"sodExportPdf()\"", js);
        Assert.Contains("📄 导出当前页 PDF", js);
    }

    // ==================== 6. ERP-239 全匹配汇总渲染 ====================

    [Fact]
    public void 汇总渲染_仅后端summary列与指标_不读取当前页明细行或选定列()
    {
        var js = Script;

        Assert.Contains("function sodSummaryPanelHtml(", js);
        Assert.Contains("function sodCurrencySummaryHtml(", js);

        var fn = Segment(js, "function sodSummaryPanelHtml(", "function sodResultHtml(");
        Assert.Contains("summary && summary.currencyColumns", js);
        Assert.Contains("summary && summary.currencyRows", js);
        Assert.Contains("summary.coverageText", fn);
        Assert.Contains("summary.profitBasisText", fn);
        // 只渲染后端返回的 summary 列与指标，绝不读取当前页明细行或选定列
        Assert.DoesNotContain("view.rows", fn);
        Assert.DoesNotContain("view.columns", fn);
    }

    [Fact]
    public void 汇总渲染_全部转义_金额利润利润率未知显式渲染()
    {
        var js = Script;
        var fn = Segment(js, "function sodSummaryPanelHtml(", "function sodCurrencySummaryHtml(");

        Assert.Contains("sodEsc(c.label || c.key)", fn);
        Assert.Contains("sodRenderCell(r[c.key], c)", fn);
        Assert.Contains("sodEsc(summary.coverageText)", fn);
        Assert.Contains("sodEsc(summary.profitBasisText)", fn);
        Assert.Contains("全匹配原币汇总", js);
    }

    [Fact]
    public void 结果区_插入全匹配汇总_独立于当前页明细与选定列()
    {
        var js = Script;
        var fn = Segment(js, "function sodResultHtml(", "function sodFieldChooserHtml(");

        Assert.Contains("const summaryHtml = sodCurrencySummaryHtml(view);", fn);
        Assert.Contains("${summaryHtml}${body}", fn);
    }

    [Fact]
    public void 汇总面板_始终显示日期筛选来源上限与覆盖范围_独立于选定列()
    {
        var js = Script;
        var fn = Segment(js, "function sodCurrencySummaryHtml(", "function sodResultHtml(");

        Assert.Contains("view.start", fn);
        Assert.Contains("view.end", fn);
        Assert.Contains("fmtDate(view.start)", fn);
        Assert.Contains("view.filterText", fn);
        Assert.Contains("view.sourceLimitText", fn);
        Assert.Contains("summary.coverageText", js);
    }

    // ==================== 7. ERP-240 全匹配汇总 Excel 导出 ====================

    [Fact]
    public void 导出汇总_按钮与函数存在_无需先预览_仅下载xlsx()
    {
        var js = Script;

        Assert.Contains("function sodExportSummary()", js);
        Assert.Contains("onclick=\"sodExportSummary()\"", js);
        Assert.Contains("下载全匹配汇总 Excel", js);
        Assert.Contains("/api/dynamic-salesman-output-report/export-summary", js);
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", js);
        Assert.Contains("业务员产值证据汇总_", js);

        // 汇总导出独立于详情页预览：绝无「请先预览」守卫，也不读取当前页明细行 / 选定列
        var fn = Segment(js, "function sodExportSummary()", "function loadSalesmanOutputDesignerCatalog()");
        Assert.DoesNotContain("请先预览后再导出", fn);
        Assert.DoesNotContain("view.rows", fn);
        Assert.DoesNotContain("view.columns", fn);
    }

    [Fact]
    public void 导出汇总_复用当前字段日期筛选分页请求体_保留输入_失败可见()
    {
        var js = Script;
        var fn = Segment(js, "function sodExportSummary()", "function loadSalesmanOutputDesignerCatalog()");

        Assert.Contains("sodBuildState(SOD_DYN.page || 1)", fn);
        Assert.Contains("sodDateError(state)", fn);
        Assert.Contains("sodFilterError(state)", fn);
        Assert.Contains("sodBuildRequest(state)", fn);
        Assert.Contains("sodErrorHtml(sodKindOfCode(code), message)", fn);
        Assert.Contains("sodErrorHtml('network'", fn);
        Assert.Contains("'invalid'", fn);
    }

    // ==================== 8. ERP-241 全匹配汇总 PDF 导出 ====================

    [Fact]
    public void 导出汇总PDF_按钮与函数存在_无需先预览_仅下载pdf()
    {
        var js = Script;

        Assert.Contains("function sodExportSummaryPdf()", js);
        Assert.Contains("onclick=\"sodExportSummaryPdf()\"", js);
        Assert.Contains("下载全匹配汇总 PDF", js);
        Assert.Contains("/api/dynamic-salesman-output-report/export-summary-pdf", js);

        // 汇总 PDF 导出独立于详情页预览：绝无「请先预览」守卫，也不读取当前页明细行 / 选定列
        var fn = Segment(js, "function sodExportSummaryPdf()", "function loadSalesmanOutputDesignerCatalog()");
        Assert.Contains("contentType.indexOf('application/pdf') >= 0", fn);
        Assert.Contains("业务员产值证据汇总_' + dateStr + '.pdf'", fn);
        Assert.DoesNotContain("请先预览后再导出", fn);
        Assert.DoesNotContain("view.rows", fn);
        Assert.DoesNotContain("view.columns", fn);
    }

    [Fact]
    public void 导出汇总PDF_复用当前字段日期筛选分页请求体_保留输入_失败可见()
    {
        var js = Script;
        var fn = Segment(js, "function sodExportSummaryPdf()", "function loadSalesmanOutputDesignerCatalog()");

        Assert.Contains("sodBuildState(SOD_DYN.page || 1)", fn);
        Assert.Contains("sodDateError(state)", fn);
        Assert.Contains("sodFilterError(state)", fn);
        Assert.Contains("sodBuildRequest(state)", fn);
        Assert.Contains("sodErrorHtml(sodKindOfCode(code), message)", fn);
        Assert.Contains("sodErrorHtml('network'", fn);
        Assert.Contains("'invalid'", fn);
    }


    // ==================== 9. ERP-242 业务员姓名关键字（UI 契约） ====================

    [Fact]
    public void 姓名关键字_输入校验与请求体_变更重置页_翻页下载复用()
    {
        var js = Script;

        // 设计器提供姓名关键字输入，变更重置到第 1 页
        Assert.Contains("id=\"sod-des-salesman-name\"", js);
        Assert.Contains("onchange=\"sodResetPage()\"", js);

        // 状态组装包含姓名关键字（翻页 / 导出 / 汇总导出均复用 sodBuildState）
        var state = Segment(js, "function sodBuildState(", "function sodPreview(");
        Assert.Contains("salesmanName: val('sod-des-salesman-name')", state);

        // 客户端校验：80 字符上限 + 控制字符拒绝
        var err = Segment(js, "function sodFilterError(", "function sodBuildFilter(");
        Assert.Contains("业务员姓名关键字最多 80 个字符", err);
        Assert.Contains("业务员姓名关键字不能包含控制字符", err);

        // 组装筛选：发送 salesmanName，且保留在请求体中
        var build = Segment(js, "function sodBuildFilter(", "function sodBuildRequest(");
        Assert.Contains("filter.salesmanName = salesmanName", build);
    }

}
