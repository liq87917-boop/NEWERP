using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-220 订单利润暂估表前端接线源契约测试：报表列包含订单 / 客户身份与原币币种、
/// 「销售额(原币)」与「当前价估算(币种未知)」标签；KPI 对订单利润暂估走币种安全分支
/// （原币分别成行，绝不跨币种合计、绝不展示实际利润 / 利润率口径）；表格对 null 显式显示「未知」、
/// 文本安全转义，并移除误导性的物流利润文案。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class OrderProfitEstimateReportUiTests
{
    [Fact]
    public void 报表定义_包含身份原币与未知证据标签_且移除物流利润文案()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("key: 'customerName', label: '客户'", reports);
        Assert.Contains("key: 'currencyLabel', label: '原币币种'", reports);
        Assert.Contains("key: 'salesAmount', label: '销售额(原币)'", reports);
        Assert.Contains("key: 'costAmount', label: '成本金额'", reports);
        Assert.Contains("key: 'currentPriceEstimate', label: '当前价估算(币种未知)'", reports);
        Assert.Contains("key: 'costEvidence', label: '成本证据'", reports);
        Assert.Contains("key: 'currentPriceEstimateReason', label: '估算说明'", reports);
        Assert.Contains("原币销售额", reports);
        Assert.Contains("成本/利润未知", reports);
        Assert.Contains("当前价估算(币种未知，仅估算)", reports);
        Assert.DoesNotContain("FOB/CIF/DDP 利润核算", reports);
    }

    [Fact]
    public void 订单利润暂估KPI_原币分别成行_绝不跨币种合计_且无实际利润口径()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("function fillOrderProfitKpi(", reports);
        Assert.Contains("if (code === 'order-profit') { fillOrderProfitKpi(data); return; }", reports);
        Assert.Contains("绝不跨币种合计", reports);
        Assert.Contains("不提供跨币种总额 / 利润率", reports);
        Assert.Contains("原币分别成行", reports);
    }

    [Fact]
    public void 订单利润暂估表格_未知显式展示_文本安全转义_按行币种渲染()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("function renderOrderProfitData(", reports);
        Assert.Contains("if (code === 'order-profit') { renderOrderProfitData(data); return; }", reports);
        Assert.Contains("未知", reports);
        Assert.Contains("fudDesEsc", reports);
        Assert.Contains("r.currencyLabel || r.currency || '未知币种'", reports);
    }

    [Fact]
    public void 订单利润暂估入口与接口路径保持()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("'order-profit': { api: '/api/reports/order-profit'", reports);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 ProductSalesRankingReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
