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

}
