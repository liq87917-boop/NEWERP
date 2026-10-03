using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-262 通用报表配置工作台前端接线源契约测试：独立工作台路由 / 脚本加载、目录驱动字段 / 筛选 / 分组 /
/// 预览、保存 / 复制 / 重命名 / 删除 / 发布 / 恢复 / 修订、草稿与已发布区分、未保存 / 陈旧版本 / 权限 /
/// 环境阻断 / 转义 / 迟到响应丢弃，以及币种单位分离与当前预览页覆盖口径。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互、不连接数据库。</para>
/// </summary>
public class ReportConfigurationUiTests
{
    // ==================== 1. 独立工作台路由 / 脚本加载 ====================

    [Fact]
    public void 前端接线_独立工作台路由与脚本加载_且保留既有报表入口()
    {
        var app = File.ReadAllText(Path.Combine(JsDirectory(), "app.js"));
        var index = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("code === 'report-configuration'", app);
        Assert.Contains("renderReportConfigurationWorkspace()", app);
        Assert.Contains("/js/report-configuration.js", index);
        Assert.Contains("function renderReportConfigurationWorkspace()", js);

        // 旧报表菜单 / URL 分派保持不变：REPORTS 分派仍在，未破坏既有入口
        Assert.Contains("REPORTS[code]", app);
        Assert.Contains("renderReport(REPORTS[code], name)", app);
    }

    // ==================== 2. 目录驱动字段 / 筛选 / 分组，不暴露不支持控件 ====================

    [Fact]
    public void 目录驱动_字段白名单规范化与有界定义_不含任意SQL脚本联接()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccSelectFields(catalogFields, selectedKeys)", js);
        Assert.Contains("valid.has(key)", js);          // 未知 / 隐藏键丢弃，fail closed
        Assert.Contains("function rccBuildDefinition(state)", js);
        Assert.Contains("aggregates: rccBuildAggregates(state)", js);  // 指标只来自目录 metric 白名单，绝不自由聚合 / SQL
        Assert.Contains("capabilities: []", js);        // 不请求超出目录的能力
        Assert.Contains("function rccGroupingHtml(groupingDimensions, groupings)", js);
        Assert.Contains("function rccFilterRowHtml(fields, f, i)", js);
        Assert.DoesNotContain("SELECT ", js);
        Assert.DoesNotContain("localStorage.setItem('rcc", js);   // 绝不使用本地存储替代持久化定义
    }

    [Fact]
    public void 不支持能力_以为什么不支持说明呈现_而非装饰性控件()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccUnsupportedHtml(dataset)", js);
        Assert.Contains("为什么不支持", js);
        Assert.Contains("自定义公式：本阶段不支持", js);
        Assert.Contains("任意透视：本阶段不支持", js);
        Assert.Contains("跨数据集联接：本阶段不支持", js);
        Assert.DoesNotContain("共享：本阶段仅支持私有配置", js);   // 共享已是真实能力，不再列入不支持清单
        Assert.DoesNotContain("导出：本阶段不支持导出", js);   // 导出已成为真实能力，不再列入不支持清单
    }

    // ==================== 7. 通用导出按钮与失败保留未保存编辑 ====================

    [Fact]
    public void 导出_通用下载按钮与端点接线_不提交客户端行()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("onclick=\"rccExport()\"", js);
        Assert.Contains("async function rccExport()", js);
        Assert.Contains("RCC_API + '/export'", js);
        Assert.Contains("rccBuildPreviewRequest(RCC)", js);       // 复用预览请求体，绝不信任客户端行 / 缓存
        Assert.Contains("contentType.indexOf('spreadsheetml') >= 0", js);
        Assert.Contains("a.download = '报表配置_'", js);
        Assert.DoesNotContain("rows:", Segment(js, "async function rccExport()", "/* 工作台入口"));
    }

    [Fact]
    public void 导出_失败保留未保存编辑_绝不覆盖设计器状态()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));
        var fn = Segment(js, "async function rccExport()", "/* 工作台入口");

        Assert.Contains("存在未保存编辑，请先保存后再导出", fn);   // 未保存编辑显式阻断，绝不导出过期定义
        Assert.Contains("rccRenderResult(rccErrorHtml(rccKindOfCode(code), message))", fn);
        Assert.Contains("rccRenderResult(rccErrorHtml('network'", fn);
        Assert.DoesNotContain("RCC.dirty = false", fn);          // 失败 / 成功路径都不清除未保存标记
        Assert.DoesNotContain("rccTouch()", fn);                 // 失败不触发数据集 / 配置变化，保留设计器控件
    }

    // ==================== 3. 保存 / 复制 / 重命名 / 删除 / 发布 / 恢复 / 修订 ====================

    [Fact]
    public void 生命周期接口_保存复制重命名删除发布恢复修订预览接线完整()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("const RCC_API = '/api/report-configurations'", js);
        Assert.Contains("'/catalog'", js);
        Assert.Contains("'/preview'", js);
        Assert.Contains("'/copy'", js);
        Assert.Contains("/rename?version=", js);
        Assert.Contains("/publish?version=", js);
        Assert.Contains("/restore?version=", js);
        Assert.Contains("'/revisions'", js);
        Assert.Contains("function rccSave()", js);
        Assert.Contains("function rccCopy()", js);
        Assert.Contains("function rccRename()", js);
        Assert.Contains("function rccDelete()", js);
        Assert.Contains("function rccPublish()", js);
        Assert.Contains("function rccRestore(version)", js);
        Assert.Contains("function rccLoadRevisions(id)", js);
        Assert.Contains("function rccPreview()", js);
    }

    [Fact]
    public void 草稿已发布区分_且陈旧版本冲突不覆盖()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccStatusLabel(v)", js);
        Assert.Contains("草稿", js);
        Assert.Contains("已发布", js);
        Assert.Contains("function rccStatusBadge(v)", js);
        Assert.Contains("if (env.code === 1004)", js);   // 陈旧版本冲突分支
        Assert.Contains("版本冲突", js);
        Assert.Contains("未覆盖", js);                    // 冲突时不覆盖
    }

    // ==================== 4. 转义 / 迟到响应 / 失败清除 / 空错误环境态 ====================

    [Fact]
    public void 转义与迟到响应丢弃与失败清除过期行_空错误环境态可见()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccEsc(v)", js);       // 所有用户 / 目录字符串转义
        Assert.Contains("if (seq !== RCC.requestSeq) return;", js);   // 丢弃迟到预览响应
        Assert.Contains("RCC.requestSeq++", js);          // 数据集 / 配置变化令牌递增
        Assert.Contains("function rccErrorHtml(kind, message)", js);
        Assert.Contains("function rccEmptyHtml()", js);
        Assert.Contains("function rccEnvBlockedHtml(message)", js);
    }

    [Fact]
    public void 环境阻断_显式环境未就绪且不用本地存储替代持久化()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("环境未就绪（environment-blocked）", js);
        Assert.Contains("不使用浏览器本地存储替代持久化定义", js);
        Assert.Contains("function rccKindOfCode(code)", js);
        Assert.Contains("if (code === 5000) return 'environment';", js);
    }

    // ==================== 5. 币种单位分离与当前预览页覆盖口径 ====================

    [Fact]
    public void 币种单位分离_分组小计按币种分区_并明确当前预览页覆盖口径()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccGroupHtml(preview)", js);
        Assert.Contains("币种：", js);                    // 分组小计按币种分区
        Assert.Contains("绝不跨币种", js);
        Assert.Contains("function rccTableHtml(preview)", js);
        Assert.Contains("currencyUnit", js);              // 列头带币种单位语义
        Assert.Contains("当前预览页（非全量合计）", js);   // 与全量合计明确区分
    }

    // ==================== 6. 数据集 / 字段变化 ====================

    [Fact]
    public void 数据集与字段变化_重置筛选分组并按目录白名单规范化()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccSelectDataset(key, touch = true)", js);
        Assert.Contains("RCC.filters = []", js);           // 切换数据集重置筛选
        Assert.Contains("RCC.groupings = []", js);         // 切换数据集重置分组
        Assert.Contains("function rccApplyDefinition(def)", js);
        Assert.Contains("rccSelectFields(RCC.fields, (def && def.fields) || [])", js);  // 加载时丢弃未知字段
    }

    // ==================== 8. 中文 PDF 下载按钮与失败保留设计器状态 ====================

    [Fact]
    public void 导出PDF_按钮与端点接线_不提交客户端行()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("onclick=\"rccExportPdf()\"", js);
        Assert.Contains("async function rccExportPdf()", js);
        Assert.Contains("RCC_API + '/export/pdf'", js);
        Assert.Contains("rccBuildPreviewRequest(RCC)", js);       // 复用预览请求体，绝不信任客户端行 / 缓存
        Assert.Contains("contentType.indexOf('pdf') >= 0", js);
        Assert.Contains("a.download = '报表配置_'", js);
        Assert.DoesNotContain("rows:", Segment(js, "async function rccExportPdf()", "/* 工作台入口"));
    }

    [Fact]
    public void 导出PDF_失败保留未保存编辑_并丢弃迟到下载响应()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));
        var fn = Segment(js, "async function rccExportPdf()", "/* 工作台入口");

        Assert.Contains("存在未保存编辑，请先保存后再导出", fn);
        Assert.Contains("if (seq !== RCC.requestSeq) return;", fn);   // 丢弃迟到下载响应
        Assert.Contains("rccRenderResult(rccErrorHtml(rccKindOfCode(code), message))", fn);
        Assert.Contains("rccRenderResult(rccErrorHtml('network'", fn);
        Assert.DoesNotContain("RCC.dirty = false", fn);                // 失败 / 成功路径都不清除未保存标记
    }

    // ==================== 9. 只读共享：owned/shared 区分 + owner grant/revoke ====================

    [Fact]
    public void 共享_owned_shared_区分与共享列表接线()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccRenderShared()", js);
        Assert.Contains("共享给我的", js);
        Assert.Contains("class=\"status status-info\">共享</span>", js);   // shared 与 owned 区分
        Assert.Contains("async function rccLoadShared()", js);
        Assert.Contains("RCC_API + '/shared'", js);
        Assert.Contains("function rccCopyShared(id)", js);
        Assert.Contains("RCC_API + '/shared/' + id + '/copy'", js);
        Assert.Contains("sharedCurrent", js);
    }

    [Fact]
    public void 共享_owner_grant_revoke_控件与转义与冲突保留()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccRenderGrants()", js);
        Assert.Contains("共享授权（owner-only）", js);
        Assert.Contains("function rccGrant()", js);
        Assert.Contains("RCC_API + '/' + RCC.current.id + '/grants'", js);
        Assert.Contains("function rccRevoke(recipientUserId, version)", js);
        Assert.Contains("'/grants/' + recipientUserId + '?version=' + version", js);
        Assert.Contains("rccEsc(g.recipientDisplayName", js);   // 授权列表值转义，防注入
        Assert.Contains("env.code === 1004", js);               // 冲突码处理，保留未提交输入
        Assert.Contains("await rccLoadGrants()", js);           // 冲突后刷新授权列表
    }

    // ==================== 10. 受限计算列结构化编辑器 ====================

    [Fact]
    public void 计算列_结构化编辑器与证据接线_且保留任意公式不支持()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccSupportsComputedColumns()", js);
        Assert.Contains("computed-columns", js);
        Assert.Contains("function rccFormulaNodeHtml(node, colIndex, path)", js);
        Assert.Contains("function rccComputedColumnsHtml()", js);
        Assert.Contains("function rccAddComputedColumn()", js);
        Assert.Contains("function rccComputedEvidenceHtml(preview)", js);
        Assert.Contains("computedColumns: rccBuildComputedColumns(state)", js);
        Assert.Contains("RCC_FORMULA_NODE_KINDS", js);
        Assert.Contains("字段 / 数字 / + - × ÷", js);
        Assert.DoesNotContain("eval(", js);              // 计算列同样绝不执行脚本
        Assert.Contains("自定义公式：本阶段不支持", js);   // 任意公式仍显式不支持
    }

    // ==================== 11. 指标汇总（目录驱动、当前页、分组 + 币种分区） ====================

    [Fact]
    public void 指标汇总_目录驱动编辑器与结果渲染_绝不自由函数与跨币种合计()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccBuildAggregates(state)", js);
        Assert.Contains("aggregates: rccBuildAggregates(state)", js);
        Assert.Contains("function rccMetricEditorHtml()", js);
        Assert.Contains("allowedFunctions", js);
        Assert.Contains("function rccMetricsHtml(preview)", js);
        Assert.Contains("最多 4 个", js);
        Assert.Contains("非全量合计", js);
        Assert.Contains("金额按币种分区", js);
    }

    // ==================== 12. 复合分组（有序维度选择、构建与渲染接线） ====================

    [Fact]
    public void 复合分组_有序维度选择与构建接线_有界状态且不自由分组()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("groupings: []", js);
        Assert.Contains("function rccOnGrouping(index, value)", js);
        Assert.Contains("第一分组", js);
        Assert.Contains("第二分组", js);
        Assert.Contains("groupings: (state.groupings && state.groupings.length) ? state.groupings : ['none']", js);
        Assert.Contains("rccEffectiveGroupings(preview)", js);
        Assert.Contains("function rccEffectiveGroupings(preview)", js);
    }

    [Fact]
    public void 透视_有界选择器与结果渲染接线_不自由透视()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccBuildPivot(state)", js);
        Assert.Contains("pivot: rccBuildPivot(state)", js);
        Assert.Contains("function rccPivotHtml()", js);
        Assert.Contains("function rccOnPivotDimension(axis, value)", js);
        Assert.Contains("行维度", js);
        Assert.Contains("列维度", js);
        Assert.Contains("function rccPivotResultHtml(preview)", js);
        Assert.Contains("rccPivotResultHtml(preview)", js);
        Assert.Contains("function rccPivotCellText(parts)", js);
        Assert.Contains("透视（当前页 · 非全量合计）", js);
        Assert.Contains("已知", js);
    }

    /// <summary>截取源码中两个锚点之间的片段，便于对单个函数做「不含某内容」的契约断言。</summary>
    private static string Segment(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        return j < 0 ? source[i..] : source[i..j];
    }

    [Fact]
    public void 有界失败_错误分类与显式重试_且防重复点击()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("if (code === 1005) return 'busy'", js);
        Assert.Contains("if (code === 1006) return 'timeout'", js);
        Assert.Contains("if (code === 1007) return 'cancelled'", js);
        Assert.Contains("if (code === 1008) return 'too-large'", js);
        Assert.Contains("if (code === 5001) return 'rendering'", js);
        Assert.Contains("busy: '执行繁忙'", js);
        Assert.Contains("rendering: '文件生成失败'", js);
        Assert.Contains("function rccRetry()", js);
        Assert.Contains("onclick=\"rccRetry()\"", js);
        Assert.Contains("RCC.lastAction", js);
        Assert.Contains("RCC.busy = false;", js);   // 预览 / 导出结束都复位，防止重复点击
    }

    [Fact]
    public void 匹配集覆盖_设计器切换与结果标注_不含全量合计()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccCoverageHtml(ds)", js);
        Assert.Contains("function rccSetCoverage(v)", js);
        Assert.Contains("coverage: state.coverage || 'current-page'", js);
        Assert.Contains("有界匹配集", js);
        Assert.Contains("覆盖口径：", js);
        Assert.Contains("汇总覆盖全部匹配事实", js);
        Assert.Contains("if (code === 5002) return 'environment'", js);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 CustomerShipmentReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}

