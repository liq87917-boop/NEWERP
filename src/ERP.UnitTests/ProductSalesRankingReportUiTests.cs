using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-212 商品销量排名前端接线源契约测试：报表列包含单位独立与「当前价估算金额(币种未知)」标签，
/// KPI 对商品销量排名走单位安全分支（单位保持独立，绝不跨单位合计数量，也不展示销售货款口径），
/// 并保留既有入口与接口路径。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class ProductSalesRankingReportUiTests
{
    [Fact]
    public void 报表定义_包含单位独立与当前价估算标签()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("key: 'unit', label: '单位'", reports);
        Assert.Contains("key: 'totalQuantity', label: '销售件数(签名)'", reports);
        Assert.Contains("key: 'totalAmount', label: '当前价估算金额(币种未知)'", reports);
        Assert.Contains("单位独立不合并", reports);
        Assert.Contains("非实际发货收入", reports);
    }

    [Fact]
    public void 商品销量排名KPI_单位独立不跨单位合计且无销售货款口径()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("function fillProductSalesRankingKpi(", reports);
        Assert.Contains("if (code === 'product-sales-ranking') { fillProductSalesRankingKpi(data); return; }", reports);
        Assert.Contains("禁止跨单位合计数量", reports);
        Assert.Contains("单位保持独立", reports);
    }

    [Fact]
    public void 商品销量排名入口与接口路径保持()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("'product-sales-ranking': { api: '/api/reports/product-sales-ranking'", reports);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 QuotationConversionReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
