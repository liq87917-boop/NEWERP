using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-244 动态业务员提成证据字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言，覆盖设计器入口、字段选择（仅目录白名单、无自由字段名 / SQL）、
/// 字段顺序 / 去重、有界请求组装（字段 / 开始结束日期 / 客户 / 业务员 / 原币 / 分页）、
/// 字段与筛选变更重置页、失败清空旧数据、上下文始终显示与 HTML 转义。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicSalesCommissionReportUiTests
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
    public void 入口_业务员提成表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'sales-commission': { api: '/api/reports/sales-commission', title: '业务员提成表', designer: 'sales-commission',", js);
        Assert.Contains("onclick=\"openSalesCommissionDesigner()\"", js);
        Assert.Contains("function openSalesCommissionDesigner()", js);
        Assert.Contains("id=\"scd-designer\"", js);
        Assert.Contains("function loadSalesCommissionDesignerCatalog()", js);
        Assert.Contains("function scdPreview(", js);
    }

    // ==================== 2. 字段选择（仅目录白名单、无自由输入 / SQL） ====================

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function scdFieldChooserHtml(", js);
        Assert.Contains("name=\"scd-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function scdSelectFields(", js);
        Assert.Contains("valid.has(key)", js);
        Assert.Contains("seen.has(key)", js);
        Assert.DoesNotContain("scd-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;
        var fn = Segment(js, "function scdSelectFields(", "function scdDateError(");
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

        Assert.Contains("function scdBuildRequest(", js);
        Assert.Contains("const fields = scdSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("filter: scdBuildFilter(state),", js);
    }

    [Fact]
    public void 筛选_组装客户业务员币种_全部留空为null()
    {
        var js = Script;
        var fn = Segment(js, "function scdBuildFilter(", "function scdBuildRequest(");

        Assert.Contains("filter.customerId", fn);
        Assert.Contains("filter.salesmanId", fn);
        Assert.Contains("filter.currency = currency.toUpperCase()", fn);
        Assert.Contains("=== undefined && filter.currency === undefined) ? null : filter", fn);
    }

    [Fact]
    public void 筛选校验_正整数与受支持币种_非法可见()
    {
        var js = Script;
        var err = Segment(js, "function scdFilterError(", "function scdBuildFilter(");
        Assert.Contains("客户 Id 必须是正整数（大于 0）", err);
        Assert.Contains("业务员 Id 必须是正整数（大于 0）", err);
        Assert.Contains("原币币种仅支持 CNY / USD / EUR / HKD / GBP / JPY", err);
    }

    [Fact]
    public void 日期校验_最多366天()
    {
        var js = Script;
        var de = Segment(js, "function scdDateError(", "function scdFilterError(");
        Assert.Contains("日期范围最多 366 天（含首尾）", de);
    }

    // ==================== 4. 字段 / 筛选变更重置页、失败清空旧数据、转义与上下文 ====================

    [Fact]
    public void 变更重置页_字段与筛选均重置到第1页并清空旧结果()
    {
        var js = Script;

        Assert.Contains("onchange=\"scdResetPage(); scdSyncSelection()\"", js);
        Assert.Contains("onchange=\"scdResetPage()\"", js);

        var rp = Segment(js, "function scdResetPage(", "function scdBuildState(");
        Assert.Contains("SCD_DYN.page = 1", rp);
        Assert.Contains("SCD_DYN.view = null", rp);
    }

    [Fact]
    public void 失败清空旧数据_网络与未授权均清空视图()
    {
        var js = Script;
        var pv = Segment(js, "function scdPreview(", "function scdPage(");

        Assert.Contains("SCD_DYN.view = null", pv);
        Assert.Contains("scdErrorHtml('unauthorized'", pv);
        Assert.Contains("scdErrorHtml('network'", pv);
        Assert.Contains("scdKindOfCode(resp.code)", pv);
    }

    [Fact]
    public void 转义标签_结果与单元格均转义_不信任后端文本()
    {
        var js = Script;

        Assert.Contains("function scdEsc(", js);
        Assert.Contains("function scdRenderCell(", js);
        Assert.Contains("scdEsc(scdCellText(value, field))", js);
        Assert.Contains("scdEsc(c.label || c.key)", js);
    }

    [Fact]
    public void 上下文始终显示_来源上限来源依据币种与提成口径即使列隐藏()
    {
        var js = Script;
        var res = Segment(js, "function scdResultHtml(", "function scdFieldChooserHtml(");

        Assert.Contains("view.sourceLimitText", res);
        Assert.Contains("view.sourceContextText", res);
        Assert.Contains("view.currencyContextText", res);
        Assert.Contains("view.unknownContextText", res);
        Assert.Contains("view.profitContextText", res);
        Assert.Contains("view.commissionContextText", res);
        Assert.Contains("view.rateContextText", res);
    }

    // ==================== 5. Excel 导出（ERP-245，前端契约） ====================

    [Fact]
    public void 导出Excel_入口按钮与函数_复用预览请求体_绝不传客户端行或旧预览()
    {
        var js = Script;

        Assert.Contains("onclick=\"scdExportExcel()\"", js);
        Assert.Contains("function scdExportExcel(", js);
        Assert.Contains("/api/dynamic-sales-commission-report/export", js);

        var fn = Segment(js, "function scdExportExcel(", "function loadSalesCommissionDesignerCatalog(");
        Assert.Contains("scdBuildRequest(state)", fn);
        Assert.Contains("scdDateError(state)", fn);
        Assert.Contains("scdFilterError(state)", fn);
        Assert.Contains("spreadsheetml", fn);
        Assert.Contains("createObjectURL", fn);
        // 绝不把预览行 / 客户端行 / 金额作为请求体（后端按请求重建有界证据）
        Assert.DoesNotContain("rows:", fn);
        Assert.DoesNotContain("JSON.stringify(SCD_DYN.view)", fn);
    }
}
