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

    // ==================== 11. 客户 Id / 关键字筛选（ERP-200） ====================

    [Fact]
    public void 筛选_客户Id与关键字输入框_有界_无自由SQL()
    {
        var js = Script;

        Assert.Contains("id=\"fud-des-customer-id\"", js);
        Assert.Contains("min=\"1\"", js);
        Assert.Contains("id=\"fud-des-keyword\"", js);
        Assert.Contains("maxlength=\"80\"", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);
    }

    [Fact]
    public void 筛选_状态读取与请求组装_预览与导出复用()
    {
        var js = Script;

        Assert.Contains("function fudDesCustomerId(", js);
        Assert.Contains("function fudDesKeyword(", js);
        Assert.Contains("function fudDesFilterError(", js);
        Assert.Contains("customerId: val('fud-des-customer-id')", js);
        Assert.Contains("keyword: val('fud-des-keyword')", js);
        Assert.Contains("req.customerId = customerId;", js);
        Assert.Contains("req.keyword = keyword;", js);

        var export = Segment(js, "async function fudDesExport()", "async function fudDesExportPdf()");
        Assert.Contains("fudDesFilterError(state)", export);
        Assert.Contains("fudDesBuildRequest(state)", export);

        var pdf = Segment(js, "async function fudDesExportPdf()", "/* 翻页");
        Assert.Contains("fudDesFilterError(state)", pdf);
        Assert.Contains("fudDesBuildRequest(state)", pdf);
    }

    [Fact]
    public void 筛选_校验错误可见_不发送无效请求()
    {
        var js = Script;

        Assert.Contains("客户 Id 必须是正整数", js);
        Assert.Contains("关键字最多 80 个字符", js);
        Assert.Contains("fudDesErrorHtml('invalid', filterError)", js);
    }

    // ==================== 12. 筛选集到期状态合计（ERP-201） ====================

    [Fact]
    public void 合计_渲染筛选集全量到期状态_与分组区分()
    {
        var js = Script;

        Assert.Contains("function fudDesDueStatusTotalsHtml(", js);
        Assert.Contains("view.dueStatusTotals", js);
        Assert.Contains("totals.overdue", js);
        Assert.Contains("totals.today", js);
        Assert.Contains("totals.upcoming", js);
        Assert.Contains("筛选集到期状态合计", js);
        Assert.Contains("已逾期", js);
        Assert.Contains("今日到期", js);
        Assert.Contains("即将到期", js);
        Assert.Contains("合计", js);

        var result = Segment(js, "function fudDesResultHtml(", "function fudDesFieldChooserHtml(");
        Assert.Contains("const totals = fudDesDueStatusTotalsHtml(view, activeDueStatus);", result);
        Assert.Contains("${totals}", result);
        Assert.Contains("const groups = fudDesGroupsHtml(view);", result);
        Assert.Contains("${groups}", result);
    }

    [Fact]
    public void 合计_缺失或空集_安全渲染_不含分组计数()
    {
        var js = Script;

        var fn = Segment(js, "function fudDesDueStatusTotalsHtml(", "function fudDesErrorHtml(");
        Assert.Contains("if (!totals) return '';", fn);
        Assert.Contains("Number(totals.overdue) || 0", fn);
        Assert.Contains("Number(totals.today) || 0", fn);
        Assert.Contains("Number(totals.upcoming) || 0", fn);
        Assert.Contains("fudDesEsc(label)", fn);
        Assert.DoesNotContain("view.groups", fn);
    }

    // ==================== 13. 状态钻取（ERP-202） ====================

    [Fact]
    public void 钻取_入口_仅设置有限到期状态_回到第1页_复用只读预览()
    {
        var js = Script;

        Assert.Contains("function fudDesDrillDueStatus(", js);
        Assert.Contains("function fudDesResetDueStatus(", js);

        var drill = Segment(js, "function fudDesDrillDueStatus(", "function fudDesResetDueStatus(");
        Assert.Contains("const key = fudDesDueStatusKey(status);", drill);
        Assert.Contains("if (!key) return;", drill);
        Assert.Contains("getElementById('fud-des-due-status')", drill);
        Assert.Contains("el.value = key", drill);
        Assert.Contains("fudDesPreview(1);", drill);

        var reset = Segment(js, "function fudDesResetDueStatus(", "/* 错误提示");
        Assert.Contains("getElementById('fud-des-due-status')", reset);
        Assert.Contains("el.value = ''", reset);
        Assert.Contains("fudDesPreview(1);", reset);
    }

    [Fact]
    public void 钻取_合计入口可点击_活动状态与返回全部可见()
    {
        var js = Script;

        var fn = Segment(js, "function fudDesDueStatusTotalsHtml(", "function fudDesDrillDueStatus(");
        Assert.Contains("chip('overdue', '已逾期', overdue)", fn);
        Assert.Contains("chip('today', '今日到期', today)", fn);
        Assert.Contains("chip('upcoming', '即将到期', upcoming)", fn);
        Assert.Contains("onclick=\"fudDesDrillDueStatus('${key}')\"", fn);
        Assert.Contains("aria-current=\"true\"", fn);
        Assert.Contains("返回全部状态", fn);
        Assert.Contains("onclick=\"fudDesResetDueStatus()\"", fn);
        Assert.Contains("当前仅查看", fn);
    }

    [Fact]
    public void 钻取_结果与预览_传递活动状态_保留既有错误反馈_无自由SQL与原始数据缓存()
    {
        var js = Script;

        Assert.Contains("function fudDesResultHtml(view, activeDueStatus)", js);
        var result = Segment(js, "function fudDesResultHtml(", "function fudDesFieldChooserHtml(");
        Assert.Contains("const totals = fudDesDueStatusTotalsHtml(view, activeDueStatus);", result);

        var preview = Segment(js, "function fudDesPreview(", "function fudDesPage(delta)");
        Assert.Contains("fudDesResultHtml(resp.data, state.dueStatus)", preview);
        Assert.Contains("fudDesErrorHtml('unauthorized', resp.message)", preview);
        Assert.Contains("fudDesErrorHtml(fudDesKindOfCode(resp.code), resp.message)", preview);
        Assert.Contains("fudDesErrorHtml('invalid', filterError)", preview);
        Assert.Contains("fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器')", preview);

        var drill = Segment(js, "function fudDesDrillDueStatus(", "function fudDesResetDueStatus(");
        Assert.DoesNotContain("FromSql", drill);
        Assert.DoesNotContain("ExecuteSql", drill);
        Assert.DoesNotContain("SqlCommand", drill);
        Assert.DoesNotContain("localStorage.setItem", drill);
    }
}

