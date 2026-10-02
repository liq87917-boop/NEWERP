using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-227 客户出货量统计表前端接线源契约测试：报表入口与接口路径保持，
/// 摘要与列标签明确标识「已审核销售订单证据」（非实际出库 / 装柜 / 收款），
/// 并移除误导性的「按目的港口 / 区域」文案；数量与金额证据标签列齐全。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class CustomerShipmentReportUiTests
{
    [Fact]
    public void 报表定义_标识已审核订单证据_且不含实际出货收款文案()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("'customer-shipment': { api: '/api/reports/customer-shipment'", reports);
        Assert.Contains("已审核销售订单证据", reports);
        Assert.Contains("非实际出库", reports);
        Assert.Contains("非实际收款", reports);
        Assert.Contains("key: 'totalQuantity', label: '已审核订单数量(非实际出库)'", reports);
        Assert.Contains("key: 'totalAmount', label: '已审核订单金额(原币，非实际收款)'", reports);
        Assert.DoesNotContain("按目的港口 / 区域", reports);
    }

    [Fact]
    public void 报表定义_包含数量与金额证据标签列()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("key: 'customerName', label: '客户'", reports);
        Assert.Contains("key: 'orderCount', label: '订单数'", reports);
        Assert.Contains("key: 'quantityLabel', label: '数量口径'", reports);
        Assert.Contains("key: 'amountLabel', label: '金额口径'", reports);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 ProductSalesRankingReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
