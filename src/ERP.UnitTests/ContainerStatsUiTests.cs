using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-251 柜量与装柜利用率统计前端接线源契约测试：报表入口与接口路径保持，
/// 摘要与列标签明确标识「装柜日历日 × 原始非空白柜号证据桶」「空白柜号按清单独立」
/// 与「签名头箱数 / 毛重 / 体积（非实体柜）」「装载率 / 柜型未知」，并移除误导性的
/// 40HQ 68m³ 装载率与按客户数推断整柜 / 拼柜文案。
/// <para>只做源码静态断言，不启动浏览器、不执行任何 UI 交互。</para>
/// </summary>
public class ContainerStatsUiTests
{
    [Fact]
    public void 报表定义_保留入口与接口路径()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("'container-stats': { api: '/api/reports/container-stats'", reports);
    }

    [Fact]
    public void 报表定义_标识证据桶_且移除40HQ68m3与整柜拼柜推断()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("装柜日历日×原始非空白柜号证据桶", reports);
        Assert.Contains("空白柜号按清单独立", reports);
        Assert.Contains("签名头箱数/毛重/体积(非实体柜)", reports);
        Assert.Contains("装载率/柜型未知", reports);
        Assert.DoesNotContain("40HQ 68m³ 基准", reports);
        Assert.DoesNotContain("装载率（40HQ", reports);
    }

    [Fact]
    public void 报表定义_列与口径_无金额格式化_不跨单位合计()
    {
        var reports = File.ReadAllText(Path.Combine(JsDirectory(), "reports.js"));

        Assert.Contains("function renderContainerStatsData(data)", reports);
        Assert.Contains("function fillContainerStatsKpi(data)", reports);
        Assert.Contains("key: 'loadingListCount', label: '装柜清单数'", reports);
        Assert.Contains("key: 'authorizedCustomerCount', label: '授权范围客户数'", reports);
        Assert.Contains("key: 'invalidCustomerCount', label: '客户身份无效数'", reports);
        Assert.Contains("key: 'totalCartons', label: '箱数(cartons)'", reports);
        Assert.Contains("key: 'totalWeight', label: '毛重(kg)'", reports);
        Assert.Contains("key: 'totalVolume', label: '体积(m³)'", reports);
        Assert.Contains("分组桶数", reports);
        Assert.Contains("装柜清单数", reports);
        Assert.Contains("绝不按金额格式化", reports);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 CustomerShipmentReportUiTests 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
