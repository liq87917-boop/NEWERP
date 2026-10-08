using ERP.Api.Controllers;
using ERP.Domain.Enums;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商比价批次转采购订单（ERP-418）<b>前端接线与嵌套 DTO 契约</b>的静态断言测试：
/// 确认 <c>purchase-order-conversion.js</c> 的批次确认文案按币种分列展示服务端重算合计、
/// <b>绝不</b>把跨币种混合金额当成一个金额展示，且嵌套 DTO（<see cref="PurchaseQuoteBatchPlan"/> /
/// <see cref="PurchaseQuoteBatchConversionResult"/> / <see cref="PurchaseQuoteCurrencyTotal"/>）
/// 提供「显式未知 + 按币种小计」所需的字段。
/// <para>说明：本文件不启动浏览器、不连接数据库（纯静态 / 反射断言），浏览器验收在本阶段记为 deferred。</para>
/// </summary>
public class PurchaseQuoteConversionUiTests
{
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static string ReadJs(string fileName) => File.ReadAllText(Path.Combine(JsDirectory(), fileName));

    private static string ReadDoc(string fileName) => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", fileName)));

    // ==================== 1. 前端合计文案：按币种分列，绝不混合合计 ====================

    [Fact]
    public void 批次确认文案_按币种分列展示合计_且不把混合币种金额当钱()
    {
        var js = ReadJs("purchase-order-conversion.js");

        // 确认弹窗仍是「先看只读计划再确认」的既有链路（不改变交互入口）。
        Assert.Contains("confirm(batchPlanText(plan, groups))", js);
        Assert.Contains("function batchPlanText(plan, groups)", js);

        // 合计文案由 batchTotalText 统一产出，且以服务端返回的按币种小计为准。
        Assert.Contains("function batchTotalText(plan)", js);
        Assert.Contains("plan.totalAmountByCurrency", js);
        Assert.Contains("plan.mixedCurrency", js);
        Assert.Contains("不做跨币种合计", js);

        // 关键回归护栏：前端任何位置都不得再把 plan.totalAmount 直接当作金额格式化展示。
        Assert.DoesNotContain("fmtMoney(plan.totalAmount)", js);
        Assert.DoesNotContain("fmtMoney(plan?.totalAmount", js);
    }

    [Fact]
    public void 批次脚本_仍由页面加载_且保留只读计划与批次转单接口路径()
    {
        var js = ReadJs("purchase-order-conversion.js");
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        Assert.Contains("/js/purchase-order-conversion.js", indexHtml);
        Assert.Contains("/batch-order-plan?", js);
        Assert.Contains("/batch-to-order`", js);
        Assert.Contains("async function purchaseQuoteBatchToOrder(id)", js);
    }

    // ==================== 2. 嵌套 DTO 契约：显式未知 + 按币种小计 ====================

    [Fact]
    public void 批次计划DTO_提供显式未知合计与按币种小计字段()
    {
        var plan = typeof(PurchaseQuoteBatchPlan);

        var total = plan.GetProperty("TotalAmount");
        Assert.NotNull(total);
        Assert.Equal(typeof(decimal?), total!.PropertyType);          // 跨币种时为 null（显式未知）

        Assert.Equal(typeof(bool), plan.GetProperty("MixedCurrency")!.PropertyType);
        var byCurrency = plan.GetProperty("TotalAmountByCurrency");
        Assert.NotNull(byCurrency);
        Assert.Equal(typeof(List<PurchaseQuoteCurrencyTotal>), byCurrency!.PropertyType);

        // 组内草稿合计仍是「单币种」标量（一张订单只有一种币种），不存在跨币种混合。
        Assert.Equal(typeof(decimal), typeof(PurchaseQuoteBatchGroupPlan).GetProperty("TotalAmount")!.PropertyType);
        Assert.Equal(typeof(decimal), typeof(PurchaseQuoteBatchOrderResult).GetProperty("TotalAmount")!.PropertyType);
    }

    [Fact]
    public void 批次转换结果DTO_提供显式未知合计与按币种小计字段()
    {
        var result = typeof(PurchaseQuoteBatchConversionResult);

        var total = result.GetProperty("TotalAmount");
        Assert.NotNull(total);
        Assert.Equal(typeof(decimal?), total!.PropertyType);

        Assert.Equal(typeof(bool), result.GetProperty("MixedCurrency")!.PropertyType);
        Assert.Equal(typeof(List<PurchaseQuoteCurrencyTotal>), result.GetProperty("TotalAmountByCurrency")!.PropertyType);

        // 每张订单结果仍自带币种，保证「按单核算」在前端可追溯。
        Assert.Equal(typeof(string), typeof(PurchaseQuoteBatchOrderResult).GetProperty("Currency")!.PropertyType);
    }

    [Fact]
    public void 转换路径_货币解析不再存在人民币回退分支()
    {
        var parse = typeof(PurchaseQuoteConversion).GetMethod("ParseCurrency", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(parse);
        Assert.Equal(typeof(Currency), parse!.ReturnType);

        var tryParse = typeof(PurchaseQuoteConversion).GetMethod("TryParseCurrency", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(tryParse);
        Assert.True(tryParse!.GetParameters().Length == 2);

        // 严格解析：无法识别抛 InvalidParameter（绝不回退 CNY）。
        var ex = Assert.Throws<ERP.Application.Common.BusinessException>(() =>
            PurchaseQuoteConversion.ParseCurrency("RUB"));
        Assert.Equal(ERP.Application.Common.ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 3. 文档证据：锁序 / 币种 / 边界同源 ====================

    [Fact]
    public void 原子性文档_记录锁序币种口径与边界()
    {
        var doc = ReadDoc("purchase-quote-conversion-atomicity.md");

        Assert.Contains("UPDLOCK, HOLDLOCK", doc);
        Assert.Contains("归属销售订单", doc);
        Assert.Contains("比价行", doc);
        Assert.Contains("绝不回退", doc);
        Assert.Contains("跨币种", doc);
        Assert.Contains("两个独立连接", doc);
    }
}
