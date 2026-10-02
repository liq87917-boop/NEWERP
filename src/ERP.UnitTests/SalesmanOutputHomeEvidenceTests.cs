using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-236 业务员产值前端消费者源契约测试：登录页 ticker「今日订单 / 本月销售额」与首页「本月销售 KPI」
/// 都必须正确解包 ApiResponse.data、使用已分配已审核订单数（orderCount 汇总）而不是行数、按原币展示金额
/// 或显式多币种 / 未知、绝不硬编码 USD 或伪造 0 / 利润；授权 / 网络失败清空为显式「--」。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class SalesmanOutputHomeEvidenceTests
{
    [Fact]
    public void appjs_三个消费者_解包ApiResponse数据并使用orderCount与币种感知展示()
    {
        var app = File.ReadAllText(Path.Combine(JsDirectory(), "app.js"));

        Assert.Contains("function salesmanOutputApprovedOrderCount(rows)", app);
        Assert.Contains("function salesmanOutputCurrencyAmountText(rows)", app);

        // 登录页今日订单：解包 data.data，使用 orderCount 汇总（不是行数）
        Assert.Contains("const rows = Array.isArray(data.data) ? data.data : [];", app);
        Assert.Contains("setText('tk-orders', fmt(salesmanOutputApprovedOrderCount(rows)))", app);

        // 登录页本月销售额 ticker：原币分别呈现
        Assert.Contains("setText('tk-sales', salesmanOutputCurrencyAmountText(rows))", app);

        // 首页本月销售 KPI：原币分别呈现
        Assert.Contains("salesmanOutputCurrencyAmountText(rows)", app);
        Assert.Contains("[data-ykpi=\"sales-month\"]", app);
    }

    [Fact]
    public void appjs_index_无硬编码USD且失败清空为不可用()
    {
        var app = File.ReadAllText(Path.Combine(JsDirectory(), "app.js"));
        var index = File.ReadAllText(Path.Combine(WwwrootDirectory(), "index.html"));

        Assert.DoesNotContain("USD", app);      // 首页 KPI / ticker 不再硬编码美元
        Assert.DoesNotContain("USD", index);    // 登录页 ticker 单位不再硬编码美元

        Assert.Contains("setText('tk-orders', '--')", app);
        Assert.Contains("setText('tk-sales', '--')", app);
        Assert.Contains("firstChild.nodeValue = '--'", app);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 SalesmanOutputReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>前端 wwwroot 目录（沿测试程序集输出目录上溯到仓库根）</summary>
    private static string WwwrootDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot"));
}
