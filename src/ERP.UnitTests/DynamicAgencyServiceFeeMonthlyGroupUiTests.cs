using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-185 代理服务费月度汇总设计器「当前页分组计数」前端契约测试。
/// <para>只对前端脚本 <c>agency-service-fee-monthly-summary.js</c> 做源码契约断言（与既有「前端接线契约」口径一致），
/// 覆盖分组键选择（仅 ERP-184 目录白名单、无自由输入）、请求透传分组键、切换分组保留分页、
/// 隐藏维度列标签、按币种分行与各状态张数分离、空数据、截断 / 页级范围与错误 / 加载可见。</para>
/// <para>不连接 SQL Server、不启动 API、不运行浏览器验收、不执行任何 SQL。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyGroupUiTests
{
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static string Script =>
        File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "agency-service-fee-monthly-summary.js"));

    /// <summary>截取源码中两个锚点之间的片段，便于对单个函数做「不含某内容」的契约断言。</summary>
    private static string Segment(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        return j < 0 ? source[i..] : source[i..j];
    }

    // ==================== 1. 分组键选择（仅目录白名单、无自由输入） ====================

    [Fact]
    public void 分组选择_仅目录分组键_且无自由输入()
    {
        var js = Script;

        Assert.Contains("function asfmsGroupByOptions()", js);
        Assert.Contains("catalog.groupBys", js);
        Assert.Contains("function asfmsGroupKey(", js);
        Assert.Contains("asfmsGroupKey((document.getElementById('asfms-f-groupby')", js);
        Assert.Contains("<select id=\"asfms-f-groupby\"", js);
        Assert.Contains("onchange=\"asfmsGroupChange()\"", js);
        // 目录未加载时 fail closed：不渲染任何分组选择（绝不臆造分组键）
        Assert.Contains("if (!opts.length) return '';", js);
        // 分组键选择器是 select，绝不提供自由文本输入
        Assert.DoesNotContain("asfms-f-groupby\" type=\"text\"", js);
    }

    // ==================== 2. 请求透传分组键 + 切换分组保留分页 ====================

    [Fact]
    public void 分组请求_透传所选分组键_且切换分组保留分页()
    {
        var js = Script;

        Assert.Contains("req.groupBy = groupBy", js);
        Assert.Contains("if (groupBy && groupBy !== 'none')", js);
        Assert.Contains("function asfmsGroupChange()", js);
        Assert.Contains("ASFMS.view && ASFMS.view.page > 0", js);
        Assert.Contains("asfmsPreview(page > 0 ? page : 1)", js);
        // 分组变化只更新 groupBy，不重置其它筛选（不调用重置分页到 1 的 asfmsSearch）
        Assert.DoesNotContain("function asfmsGroupChange()\n  {\n    asfmsSearch();", js);
    }

    // ==================== 3. 分组面板：按币种分行、各状态张数分离 ====================

    [Fact]
    public void 分组面板_按币种分行_各状态张数分离()
    {
        var js = Script;

        Assert.Contains("function asfmsGroupPanelHtml(", js);
        Assert.Contains("Array.isArray(view.groupCounts)", js);
        Assert.Contains("g && g.currency ? String(g.currency)", js);
        Assert.Contains("asfmsCountText(g && g.registeredCount)", js);
        Assert.Contains("asfmsCountText(g && g.draftCount)", js);
        Assert.Contains("asfmsCountText(g && g.voidedCount)", js);
        Assert.Contains("asfmsCountText(g && g.statementCount)", js);
        Assert.Contains("asfmsCountText(g && g.rowCount)", js);
        Assert.Contains("已登记", js);
        Assert.Contains("草稿", js);
        Assert.Contains("已作废", js);
        // 面板已接入结果区渲染
        Assert.Contains("asfmsGroupPanelHtml(v)", js);
    }

    // ==================== 4. 隐藏维度列：标签由分组维度而非选定列生成 ====================

    [Fact]
    public void 分组面板_隐藏维度列_由分组维度而非选定列生成标签()
    {
        var js = Script;

        Assert.Contains("function asfmsGroupCountLabel(", js);
        Assert.Contains("g.statementMonthText", js);
        Assert.Contains("g.customerCode", js);
        Assert.Contains("g.customerName", js);

        var panel = Segment(js, "function asfmsGroupPanelHtml(", "function asfmsResultHtml()");
        // 分组面板绝不从选定列（view.columns / view.rows）推导标签或合计
        Assert.DoesNotContain("view.columns", panel);
        Assert.DoesNotContain("view.rows", panel);
    }

    // ==================== 5. 混合币种：按原币分行、绝不合并 ====================

    [Fact]
    public void 混合币种_分组计数按原币分行_绝不合并()
    {
        var js = Script;

        Assert.Contains("g && g.currency ? String(g.currency)", js);
        Assert.Contains("仅当前预览页，非全量合计", js);
        // 面板不存在任何跨币种合并文案
        Assert.DoesNotContain("USD,JPY", js);
        Assert.DoesNotContain("USD, JPY", js);
    }

    // ==================== 6. 空数据 ====================

    [Fact]
    public void 空数据_分组面板显示空页提示()
    {
        var js = Script;

        Assert.Contains("counts.length === 0", js);
        Assert.Contains("本页没有可分组计数的月度汇总（空页）", js);
    }

    // ==================== 7. 分页 / 截断范围 ====================

    [Fact]
    public void 分页_分组仅当前页且截断标注清晰()
    {
        var js = Script;

        Assert.Contains("view && view.truncated", js);
        Assert.Contains("分组计数仅当前页，不含后续分页", js);
        Assert.Contains("view.groupCountScopeText", js);
    }

    // ==================== 8. 错误 / 加载可见 ====================

    [Fact]
    public void 错误与加载_授权无效网络失败均可见()
    {
        var js = Script;

        Assert.Contains("function asfmsErrorHtml(", js);
        Assert.Contains("forbidden: '权限不足'", js);
        Assert.Contains("unauthorized: '未登录 / 登录已过期'", js);
        Assert.Contains("invalid: '请求无效'", js);
        Assert.Contains("network: '网络请求失败'", js);
        Assert.Contains("function asfmsLoadingHtml()", js);
        Assert.Contains("if (ASFMS.error)", js);
        Assert.Contains("if (ASFMS.loading)", js);
    }
}
