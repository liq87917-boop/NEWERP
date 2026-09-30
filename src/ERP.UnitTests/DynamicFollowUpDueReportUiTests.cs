using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-194 动态跟进提醒字段设计器「前端 UI 契约」测试。
/// <para>只对前端脚本 <c>reports.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖设计器入口、字段选择（仅 ERP-193 目录白名单、无自由字段名 / SQL）、字段顺序 / 去重 / 丢弃未知键、
/// 有界请求组装（字段 / 到期状态 / as-of 日期 / 提前天数 / 分页）、翻页复用当前筛选、
/// 授权撤销（权限不足 / 未登录）、空状态、截断与分页、错误 / 加载可见与安全文本渲染。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicFollowUpDueReportUiTests
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
    public void 入口_跟进提醒报表_提供字段设计器入口_不新增菜单或脚本注册()
    {
        var js = Script;

        Assert.Contains("'follow-up-due': { api: '/api/reports/follow-up-due', title: '跟进提醒', asOf: true, designer: true,", js);
        Assert.Contains("onclick=\"openFollowUpDueDesigner()\"", js);
        Assert.Contains("function openFollowUpDueDesigner()", js);
        Assert.Contains("id=\"fud-designer\"", js);
        Assert.Contains("function loadFollowUpDueDesignerCatalog()", js);
        Assert.Contains("function fudDesPreview(", js);
    }

    // ==================== 2. 字段选择（仅目录白名单、无自由输入 / SQL） ====================

    [Fact]
    public void 字段选择_仅目录白名单复选框_无自由字段名与SQL()
    {
        var js = Script;

        Assert.Contains("function fudDesFieldChooserHtml(", js);
        Assert.Contains("name=\"fud-des-field\"", js);
        Assert.Contains("type=\"checkbox\"", js);
        Assert.Contains("function fudDesSelectFields(", js);
        Assert.Contains("!valid.has(key)", js);
        Assert.DoesNotContain("fud-des-field\" type=\"text\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 字段顺序_保留选定顺序去重_丢弃未知键()
    {
        var js = Script;

        var fn = Segment(js, "function fudDesSelectFields(", "function fudDesDueStatusKey(");
        Assert.Contains("valid.has(key)", fn);
        Assert.Contains("seen.has(key)", fn);
        Assert.Contains("seen.add(key)", fn);
        Assert.Contains("result.push(key)", fn);
    }

    // ==================== 3. 有界请求（字段 / 筛选 / 分页） ====================

    [Fact]
    public void 请求_组装有界字段筛选分页_无任意字段名()
    {
        var js = Script;

        Assert.Contains("function fudDesBuildRequest(", js);
        Assert.Contains("const fields = fudDesSelectFields(state.catalogFields, state.selectedKeys);", js);
        Assert.Contains("const page = Math.max(1, Math.floor(Number(state.page) || 1));", js);
        Assert.Contains("pageSize = Math.max(1, Math.min(maxPageSize, pageSize));", js);
        Assert.Contains("req.asOfDate = String(state.asOfDate).slice(0, 10);", js);
        Assert.Contains("req.aheadDays = aheadDays;", js);
        Assert.Contains("req.dueStatus = dueStatus;", js);
    }


    [Fact]
    public void 筛选_到期状态仅白名单_无自由输入()
    {
        var js = Script;

        Assert.Contains("function fudDesDueStatusKey(", js);
        Assert.Contains("s === 'overdue' || s === 'today' || s === 'upcoming'", js);
        Assert.Contains("id=\"fud-des-due-status\"", js);
        Assert.Contains("<option value=\"overdue\">已逾期</option>", js);
        Assert.Contains("<option value=\"today\">今日到期</option>", js);
        Assert.Contains("<option value=\"upcoming\">即将到期</option>", js);
        Assert.DoesNotContain("fud-des-due-status\" type=\"text\"", js);
    }

    [Fact]
    public void 预览_调用既有目录与预览接口()
    {
        var js = Script;

        Assert.Contains("await fudDesRequest('/api/dynamic-follow-up-due-report');", js);
        Assert.Contains("await fudDesRequest('/api/dynamic-follow-up-due-report', 'POST', req);", js);
        Assert.Contains("function fudDesBuildState(", js);
        Assert.Contains("fudDesBuildState(page)", js);
    }

    // ==================== 4. 翻页复用当前筛选 ====================

    [Fact]
    public void 翻页_保留当前字段与筛选()
    {
        var js = Script;

        Assert.Contains("function fudDesPage(delta)", js);
        Assert.Contains("fudDesPreview(page);", js);
        var preview = Segment(js, "function fudDesPreview(", "function fudDesPage(delta)");
        Assert.Contains("fudDesBuildState(page)", preview);
    }

    // ==================== 5. 授权撤销 / 错误 / 加载 ====================

    [Fact]
    public void 预览_授权撤销_权限不足与未登录可见_不渲染数据()
    {
        var js = Script;

        Assert.Contains("function fudDesKindOfCode(", js);
        Assert.Contains("if (code === 2002) return 'forbidden';", js);
        Assert.Contains("if (code === 2000 || code === 2003) return 'unauthorized';", js);
        Assert.Contains("forbidden: '权限不足'", js);
        Assert.Contains("unauthorized: '未登录 / 登录已过期'", js);
        Assert.Contains("if (typeof logout === 'function') logout();", js);
    }

    [Fact]
    public void 加载与网络失败_可见()
    {
        var js = Script;

        Assert.Contains("function fudDesLoadingHtml()", js);
        Assert.Contains("正在预览（只读查询）…", js);
        Assert.Contains("fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器')", js);
        Assert.Contains("network: '网络请求失败'", js);
        Assert.Contains("invalid: '请求无效'", js);
    }


    // ==================== 6. 空状态 / 截断分页 ====================

    [Fact]
    public void 空状态_显式空页提示_来自后端emptyText()
    {
        var js = Script;

        Assert.Contains("function fudDesEmptyHtml(", js);
        Assert.Contains("view.emptyText", js);
        Assert.Contains("没有符合筛选条件的跟进提醒证据", js);
    }

    [Fact]
    public void 分页_有界稳定_截断可见()
    {
        var js = Script;

        Assert.Contains("function fudDesPagingHtml(", js);
        Assert.Contains("view.truncated", js);
        Assert.Contains("（仅当前页，后续仍有记录）", js);
        Assert.Contains("function fudDesPage(", js);
        Assert.Contains("if (page < 1) return;", js);
    }

    // ==================== 7. 安全文本渲染 ====================

    [Fact]
    public void 安全渲染_列名与单元格全部HTML转义()
    {
        var js = Script;

        Assert.Contains("function fudDesEsc(", js);
        Assert.Contains("'&': '&amp;'", js);
        Assert.Contains("'<': '&lt;'", js);
        Assert.Contains("'>': '&gt;'", js);
        Assert.Contains("'&quot;'", js);
        Assert.Contains("'&#39;'", js);
        Assert.Contains("function fudDesRenderCell(", js);
        Assert.Contains("return fudDesEsc(fudDesCellText(value, field));", js);
        Assert.Contains("fudDesEsc(c.label || c.key)", js);
    }

    // ==================== 8. Excel 导出（ERP-195） ====================

    [Fact]
    public void 导出_入口与请求_复用预览请求体与导出端点()
    {
        var js = Script;

        Assert.Contains("onclick=\"fudDesExport()\"", js);
        Assert.Contains("function fudDesExport()", js);
        Assert.Contains("fetch('/api/dynamic-follow-up-due-report/export'", js);
        Assert.Contains("fudDesBuildState(FUD_DYN.view.page)", js);
        Assert.Contains("fudDesBuildRequest(state)", js);
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", js);
        Assert.Contains("URL.createObjectURL(blob)", js);
        Assert.Contains("a.download", js);
    }

    [Fact]
    public void 导出_空结果与错误可见_不下载内容()
    {
        var js = Script;

        Assert.Contains("fudDesErrorHtml('invalid', '请先预览后再导出 Excel')", js);
        Assert.Contains("fudDesErrorHtml('empty', '没有符合筛选条件的跟进提醒证据，无法导出（请先预览）')", js);
        Assert.Contains("fudDesErrorHtml('unauthorized', message)", js);
        Assert.Contains("fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器')", js);
    }

    // ==================== 9. PDF 导出（ERP-196） ====================

    [Fact]
    public void 导出PDF_入口与请求_复用预览请求体与PDF端点()
    {
        var js = Script;

        Assert.Contains("onclick=\"fudDesExportPdf()\"", js);
        Assert.Contains("function fudDesExportPdf()", js);
        Assert.Contains("fetch('/api/dynamic-follow-up-due-report/pdf'", js);
        Assert.Contains("fudDesBuildState(FUD_DYN.view.page)", js);
        Assert.Contains("fudDesBuildRequest(state)", js);
        Assert.Contains("contentType.indexOf('application/pdf') >= 0", js);
        Assert.Contains("URL.createObjectURL(blob)", js);
        Assert.Contains("a.download", js);
    }

    [Fact]
    public void 导出PDF_空结果与错误可见_不下载内容()
    {
        var js = Script;

        Assert.Contains("fudDesErrorHtml('invalid', '请先预览后再导出 PDF')", js);
        Assert.Contains("fudDesErrorHtml('empty', '没有符合筛选条件的跟进提醒证据，无法导出 PDF（请先预览）')", js);
        Assert.Contains("fudDesErrorHtml('unauthorized', message)", js);
        Assert.Contains("fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器')", js);
    }

    // ==================== 10. 页面分组计数（ERP-197） ====================

    [Fact]
    public void 分组_请求_仅白名单选择器_无自由输入()
    {
        var js = Script;

        Assert.Contains("function fudDesGroupKey(", js);
        Assert.Contains("req.groupBy = groupBy;", js);
        Assert.Contains("groupBy: val('fud-des-group')", js);
        Assert.Contains("id=\"fud-des-group\"", js);
        Assert.Contains("value=\"none\"", js);
        Assert.Contains("value=\"dueStatus\"", js);
        Assert.Contains("value=\"salesman\"", js);
        Assert.DoesNotContain("fud-des-group\" type=\"text\"", js);
    }

    [Fact]
    public void 分组_结果_本页计数且标签转义()
    {
        var js = Script;

        Assert.Contains("function fudDesGroupsHtml(", js);
        Assert.Contains("groupBy === 'none'", js);
        Assert.Contains("view.groups", js);
        Assert.Contains("仅统计本页", js);
        Assert.Contains("role=\"list\"", js);
        Assert.Contains("fudDesEsc(label)", js);
    }

    [Fact]
    public void 分组_翻页与筛选_保留分组选择()
    {
        var js = Script;

        Assert.Contains("function fudDesPage(", js);
        Assert.Contains("fudDesPreview(page)", js);
        Assert.Contains("function fudDesBuildState(", js);
        Assert.Contains("groupBy: val('fud-des-group')", js);
    }
}

