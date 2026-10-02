using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-229 动态客户出货量证据字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖设计器入口、字段选择（仅 ERP-229 目录白名单、无自由字段名 / SQL）、字段顺序 / 去重 / 丢弃未知键、
/// 有界请求组装（字段 / 开始结束日期 / 分页）、授权撤销（权限不足 / 未登录）、空状态、截断与分页、
/// 原币 / 单位 / 未知 / 来源上下文始终显示、错误 / 加载可见与安全文本渲染、Excel 导出下载与公式安全。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicCustomerShipmentReportUiTests
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
    public void 入口_客户出货量报表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'customer-shipment': { api: '/api/reports/customer-shipment', title: '客户出货量统计表', designer: 'customer-shipment',", js);
        Assert.Contains("onclick=\"openCustomerShipmentDesigner()\"", js);
        Assert.Contains("function openCustomerShipmentDesigner()", js);
        Assert.Contains("id=\"csd-designer\"", js);
        Assert.Contains("function loadCustomerShipmentDesignerCatalog()", js);
        Assert.Contains("function csdPreview(", js);
    }

    // ==================== 2. 字段选择（仅目录白名单、无自由输入 / SQL） ====================

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function csdFieldChooserHtml(", js);
        Assert.Contains("name=\"csd-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function csdSelectFields(", js);
        Assert.Contains("valid.has(key)", js);
        Assert.Contains("seen.has(key)", js);
        Assert.DoesNotContain("csd-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;

        var fn = Segment(js, "function csdSelectFields(", "function csdDateError(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
        Assert.Contains("continue;", fn);
    }

    // ==================== 3. 有界请求（字段 / 日期 / 分页） ====================

    [Fact]
    public void 请求_组装有界字段日期分页_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function csdBuildRequest(", js);
        Assert.Contains("const fields = csdSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("start: String(state.start).slice(0, 10)", js);
        Assert.Contains("end: String(state.end).slice(0, 10)", js);
    }

    [Fact]
    public void 日期校验_客户端与后端一致_最多366天_结束不早于开始()
    {
        var js = Script;

        var fn = Segment(js, "function csdDateError(", "function csdBuildRequest(");
        Assert.Contains("结束日期不能早于开始日期", fn);
        Assert.Contains("日期范围最多 366 天（含首尾）", fn);
    }

    // ==================== 4. 上下文始终显示（即使证据列被隐藏） ====================

    [Fact]
    public void 上下文_原币单位未知来源与页面覆盖始终显示_含去重客户订单()
    {
        var js = Script;

        Assert.Contains("function csdResultHtml(", js);
        var fn = Segment(js, "function csdResultHtml(", "function csdFieldChooserHtml(");
        Assert.Contains("view.pageOnlyText", fn);
        Assert.Contains("view.currencyContextText", fn);
        Assert.Contains("view.unitContextText", fn);
        Assert.Contains("view.unknownContextText", fn);
        Assert.Contains("view.sourceContextText", fn);
        Assert.Contains("csdScopeLine(view)", fn);

        Assert.Contains("function csdScopeLine(", js);
        Assert.Contains("c.customerCurrencyRows", js);
        Assert.Contains("c.uniqueCustomers", js);
        Assert.Contains("c.uniqueOrders", js);
        Assert.Contains("c.evidenceBasis", js);
    }

    // ==================== 5. 授权 / 空 / 错误 / 网络状态可见 ====================

    [Fact]
    public void 状态_授权撤销无效空网络失败可见_无自由SQL与原始数据缓存()
    {
        var js = Script;

        var preview = Segment(js, "function csdPreview(", "function csdPage(");
        Assert.Contains("csdErrorHtml('unauthorized', resp.message)", preview);
        Assert.Contains("csdErrorHtml(csdKindOfCode(resp.code), resp.message)", preview);
        Assert.Contains("csdErrorHtml('invalid', dateError)", preview);
        Assert.Contains("csdErrorHtml('network', (err && err.message) || '无法连接到服务器')", preview);
        Assert.DoesNotContain("FromSql", preview);
        Assert.DoesNotContain("ExecuteSql", preview);
        Assert.DoesNotContain("SqlCommand", preview);
        Assert.DoesNotContain("localStorage.setItem", preview);
    }

    [Fact]
    public void 空状态_后端空页说明显式显示_截断与分页可见()
    {
        var js = Script;

        Assert.Contains("function csdEmptyHtml(", js);
        Assert.Contains("view.emptyText", js);
        Assert.Contains("function csdPagingHtml(", js);
        Assert.Contains("view.truncated", js);
        Assert.Contains("csdPage(-1)", js);
        Assert.Contains("csdPage(1)", js);
    }

    // ==================== 6. Excel 导出（复用预览请求体，公式安全） ====================

    [Fact]
    public void 导出_复用当前页请求_成功下载xlsx附件_无自由SQL()
    {
        var js = Script;

        Assert.Contains("function csdExport()", js);
        Assert.Contains("/api/dynamic-customer-shipment-report/export", js);
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", js);
        Assert.Contains("URL.createObjectURL(blob)", js);
        Assert.Contains("a.download = '客户出货量证据_'", js);

        var fn = Segment(js, "function csdExport()", "function loadCustomerShipmentDesignerCatalog(");
        Assert.Contains("csdBuildRequest(state)", fn);
        Assert.Contains("method: 'POST'", fn);
        Assert.DoesNotContain("FromSql", fn);
        Assert.DoesNotContain("ExecuteSql", fn);
        Assert.DoesNotContain("SqlCommand", fn);
        Assert.DoesNotContain("localStorage.setItem", fn);
    }

    [Fact]
    public void 导出_授权无效网络失败可见_不下载任何内容()
    {
        var js = Script;

        var fn = Segment(js, "function csdExport()", "function loadCustomerShipmentDesignerCatalog(");
        Assert.Contains("csdErrorHtml('invalid', '请先预览后再导出 Excel')", fn);
        Assert.Contains("csdErrorHtml('empty', '没有符合所选日期范围与数据范围的已审核销售订单，无法导出（请先预览）')", fn);
        Assert.Contains("csdErrorHtml('network', (err && err.message) || '无法连接到服务器')", fn);
    }

    // ==================== 7. PDF 下载（无需先预览，复用当前字段 / 日期 / 分页） ====================

    [Fact]
    public void 导出PDF_无需先预览_按当前字段日期分页组装请求_成功下载pdf附件_无自由SQL()
    {
        var js = Script;

        Assert.Contains("function csdExportPdf()", js);
        Assert.Contains("/api/dynamic-customer-shipment-report/pdf", js);
        Assert.Contains("contentType.indexOf('application/pdf') >= 0", js);
        Assert.Contains("URL.createObjectURL(blob)", js);
        Assert.Contains("a.download = '客户出货量证据_'", js);

        var fn = Segment(js, "function csdExportPdf()", "function loadCustomerShipmentDesignerCatalog(");
        Assert.Contains("csdBuildState(CSD_DYN.page || 1)", fn);
        Assert.Contains("csdBuildRequest(state)", fn);
        Assert.Contains("method: 'POST'", fn);
        Assert.DoesNotContain("请先预览后再导出", fn);
        Assert.DoesNotContain("FromSql", fn);
        Assert.DoesNotContain("ExecuteSql", fn);
        Assert.DoesNotContain("SqlCommand", fn);
        Assert.DoesNotContain("localStorage.setItem", fn);
    }

    [Fact]
    public void 导出PDF_授权无效网络失败可见_不下载任何内容()
    {
        var js = Script;

        var fn = Segment(js, "function csdExportPdf()", "function loadCustomerShipmentDesignerCatalog(");
        Assert.Contains("csdErrorHtml('invalid', dateError)", fn);
        Assert.Contains("csdErrorHtml(csdKindOfCode(code), message)", fn);
        Assert.Contains("csdErrorHtml('network', (err && err.message) || '无法连接到服务器')", fn);
        Assert.Contains("'PDF 下载失败'", fn);
    }

    // ==================== 8. ERP-231 可选应用筛选（客户 Id / 原币币种） ====================

    [Fact]
    public void 筛选_设计器提供客户Id与原币选择_筛选变化重置页码_保留其余输入()
    {
        var js = Script;

        Assert.Contains("id=\"csd-des-customer-id\"", js);
        Assert.Contains("id=\"csd-des-currency\"", js);
        Assert.Contains("<option value=\"CNY\">", js);
        Assert.Contains("<option value=\"USD\">", js);
        Assert.Contains("<option value=\"JPY\">", js);

        var designer = Segment(js, "function openCustomerShipmentDesigner()", "loadCustomerShipmentDesignerCatalog();");
        Assert.DoesNotContain("未知币种", designer);

        var reset = Segment(js, "function csdResetPage()", "function csdBuildState(");
        Assert.Contains("CSD_DYN.page = 1;", reset);
        Assert.Contains("CSD_DYN.view = null;", reset);
    }

    [Fact]
    public void 筛选_组装规范化筛选_校验非法取值_无自由SQL()
    {
        var js = Script;

        Assert.Contains("function csdBuildFilter(", js);
        Assert.Contains("function csdFilterError(", js);
        Assert.Contains("filter.customerId = Number(customerId)", js);
        Assert.Contains("filter.currency = currency.toUpperCase()", js);
        Assert.Contains("filter: csdBuildFilter(state)", js);
        Assert.Contains("客户 Id 必须是正整数（大于 0）", js);
        Assert.Contains("原币币种仅支持 CNY / USD / EUR / HKD / GBP / JPY", js);
        Assert.Contains("view.filterText", js);

        var build = Segment(js, "function csdBuildRequest(", "function csdCellText(");
        Assert.DoesNotContain("FromSql", build);
        Assert.DoesNotContain("ExecuteSql", build);
        Assert.DoesNotContain("SqlCommand", build);
    }

}
