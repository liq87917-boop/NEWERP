using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-205 报价成交率报表前端接线源契约测试：报表列包含「原币币种」与「(原币)」金额标签、
/// KPI 对报价成交率走币种安全分支（按原币分列，绝不跨币种合计），并保留既有入口与接口路径。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class QuotationConversionReportUiTests
{
    [Fact]
    public void 报表定义_包含币种列与原币金额标签()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("key: 'currency', label: '原币币种'", reports);
        Assert.Contains("有效报价金额(原币)", reports);
        Assert.Contains("已转出金额(原币)", reports);
        Assert.Contains("单笔成交均价(原币)", reports);
    }

    [Fact]
    public void 报价成交率KPI_按原币分列_绝不跨币种合计()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("function fillQuotationConversionKpi(", reports);
        Assert.Contains("if (code === 'quotation-conversion') { fillQuotationConversionKpi(data); return; }", reports);
        Assert.Contains("绝不跨币种", reports);
        Assert.Contains("byCurrency", reports);
    }

    [Fact]
    public void 报价成交率入口与接口路径保持()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("'quotation-conversion': { api: '/api/reports/quotation-conversion'", reports);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 QuotationGovernanceTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
