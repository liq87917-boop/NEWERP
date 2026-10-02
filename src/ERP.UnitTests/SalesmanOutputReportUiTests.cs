using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-235 业务员产值报表前端接线源契约测试：报表入口与接口路径保持，
/// 摘要与列标签明确标识「已分配业务员 · 已审核 · 未删除 · 授权客户销售订单证据」
/// （非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款），并说明未分配业务员的订单不参与。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class SalesmanOutputReportUiTests
{
    [Fact]
    public void 报表定义_标识已分配业务员已审核未删除授权客户销售订单证据()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("'salesman-output': { api: '/api/reports/salesman-output'", reports);
        Assert.Contains("已分配业务员", reports);
        Assert.Contains("已审核", reports);
        Assert.Contains("未删除", reports);
        Assert.Contains("授权客户销售订单证据", reports);
        Assert.Contains("非总ERP订单", reports);
        Assert.Contains("非产值", reports);
        Assert.Contains("非实际收入", reports);
        Assert.Contains("非出货", reports);
        Assert.Contains("非收款", reports);
    }

    [Fact]
    public void 报表定义_列标签为证据口径且含当前价估算说明()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("key: 'salesmanName', label: '业务员'", reports);
        Assert.Contains("key: 'orderCount', label: '已审核订单数'", reports);
        Assert.Contains("key: 'totalAmount', label: '订单金额合计(原币)'", reports);
        Assert.Contains("key: 'totalProfit', label: '当前价估算利润(币种未知)'", reports);
    }

    [Fact]
    public void 报表渲染_保留未分配业务员排除口径且不显示产值收入出货收款()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("function renderSalesmanOutputData(data)", reports);
        Assert.Contains("if (code === 'salesman-output') { renderSalesmanOutputData(data); return; }", reports);
        Assert.Contains("未分配业务员的订单不参与", reports);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 ProductSalesRankingReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
