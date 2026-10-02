using System.Globalization;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 业务员提成表（ERP-243）纯分组与参数解析规则：把「业务员级不安全跨币种金额 / 利润 / 提成」替换为
/// 「持久化业务员桶 × 原始原币」分桶。已知枚举币种按签名订单头金额小计；未知 / 无效币种保留原始键
/// （如 <c>"999"</c>），金额为 null 仅保留订单头计数，绝不回落为 0、绝不换算汇率、绝不跨币种合计。
/// 利润 / 利润率 / 提成额恒为未知（null），因为当前商品售价 / 成本价不能证明历史可比较成本 / 利润 / 提成；
/// 系统参数 <c>SalesCommissionRate</c> 仅作为「当前参考比例」（可空），缺失 / 重复 / 非法 / 负数 / 超范围一律未知，绝不回退为 0。
/// 全部为纯函数（无数据库、无 IO），便于逐条单测。
/// </summary>
public static class SalesCommissionEvidenceRules
{
    /// <summary>业务员提成比例系统参数键（与 SeedData / SchemaUpgrader 同源）</summary>
    public const string SalesCommissionRateKey = "SalesCommissionRate";

    /// <summary>未知 / 无效币种的展示名（与 <see cref="ReportService.UnknownCurrencyGroup"/> 同源，绝不默认币种或推断汇率）</summary>
    public const string UnknownCurrencyGroup = ReportService.UnknownCurrencyGroup;

    /// <summary>未指定业务员桶的展示名（与「缺失 / 已删除员工」桶刻意区分）</summary>
    public const string UnassignedSalesmanName = "(未指定业务员)";

    /// <summary>员工姓名快照缺失 / 已删除时的显式未知身份</summary>
    public const string UnknownSalesmanName = "未知业务员";

    /// <summary>未指定业务员桶的身份依据：订单未分配业务员（非缺失 / 已删除员工身份）</summary>
    public const string UnassignedSalesmanIdentityEvidence = "订单未分配业务员（未指定业务员桶，非缺失/已删除员工身份）";

    /// <summary>业务员身份依据：姓名快照缺失 / 已删除，身份未知（仅订单属性，非权限边界）</summary>
    public const string UnknownSalesmanIdentityEvidence = "业务员姓名快照缺失或已删除，身份未知（仅订单属性，非权限边界）";

    /// <summary>来源依据标签：已审核、未删除、授权客户销售订单证据，非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款</summary>
    public const string SourceLabel = "已审核·未删除·授权客户销售订单证据（非总ERP订单/产值/实际收入/出货/收款）";

    /// <summary>已知币种金额证据标签：已审核订单原币金额，非实际收款金额</summary>
    public const string KnownCurrencyEvidence = "原币金额（已审核订单，非实际收款金额）";

    /// <summary>未知币种证据标签：不推断币种，金额未知（仅计数证据）</summary>
    public const string UnknownCurrencyEvidence = "未知/无效币种：不推断币种，金额未知（仅计数证据）";

    /// <summary>金额口径证据标签：已审核订单金额（原币），非实际收款金额</summary>
    public const string AmountLabel = "已审核订单金额合计（原币，非实际收款金额）";

    /// <summary>利润证据标签（未知原因）</summary>
    public const string ProfitEvidence = "利润未知：当前商品售价/成本价不能证明历史可比较成本/利润，不回落为0";

    /// <summary>利润率证据标签（未知原因：与利润同源）</summary>
    public const string ProfitRateEvidence = "利润率未知：与利润同源，当前商品售价/成本价不能证明历史可比较成本/利润，不回落为0";

    /// <summary>提成额证据标签（未知原因）</summary>
    public const string CommissionEvidence = "提成额未知：历史可比较成本/利润缺失，绝不从当前成本派生或推断为0";

    /// <summary>提成比例依据标签（当前参考，非历史约定比例、非实际提成 / 客户代理费 / 台账分录）</summary>
    public const string CommissionRateEvidence = "当前参考比例（非历史约定比例、非实际提成/客户代理费/台账分录）";

    /// <summary>参数缺失时的未知原因（绝不回退为 0）</summary>
    public const string RateMissingReason = "缺少 SalesCommissionRate 系统参数，当前参考比例未知（不回退为0）";

    /// <summary>参数重复时的未知原因（绝不回退为 0）</summary>
    public const string RateDuplicateReason = "SalesCommissionRate 系统参数存在多条，当前参考比例未知（不回退为0）";

    /// <summary>参数非法时的未知原因（绝不回退为 0）</summary>
    public const string RateMalformedReason = "SalesCommissionRate 系统参数不是不变量的普通十进制小数，当前参考比例未知（不回退为0）";

    /// <summary>参数为负数时的未知原因（绝不回退为 0）</summary>
    public const string RateNegativeReason = "SalesCommissionRate 系统参数为负数，当前参考比例未知（不回退为0）";

    /// <summary>参数超范围时的未知原因（绝不回退为 0）</summary>
    public const string RateExcessiveReason = "SalesCommissionRate 系统参数超过 100，当前参考比例未知（不回退为0）";

    /// <summary>原币分组键：保留原始枚举键（已知为名称如 USD；未知 / 无效为原始数值如 999），绝不折叠为默认币种或推断汇率</summary>
    public static string CurrencyGroupKey(Currency currency) => currency.ToString();

    /// <summary>原币分组标签（如「USD 美元」；未知 / 无效为「未知币种」）</summary>
    public static string CurrencyGroupLabel(string currencyKey) => currencyKey switch
    {
        nameof(Currency.CNY) => "CNY 人民币",
        nameof(Currency.USD) => "USD 美元",
        nameof(Currency.EUR) => "EUR 欧元",
        nameof(Currency.HKD) => "HKD 港币",
        nameof(Currency.GBP) => "GBP 英镑",
        nameof(Currency.JPY) => "JPY 日元",
        _ => UnknownCurrencyGroup
    };

    /// <summary>币种是否为已知枚举值（否则为未知 / 无效币种）</summary>
    public static bool IsKnownCurrency(Currency currency) => Enum.IsDefined(typeof(Currency), currency);

    /// <summary>
    /// 解析 <c>SalesCommissionRate</c> 系统参数的当前参考比例（可空）。规则：
    /// 恰好一条「不变量普通十进制小数 0..100」（含显式 0）返回其值并给出空原因；
    /// 缺失 / 重复（&gt;1 条）/ 非法 / 负数 / 超范围一律返回 null 并给出显式原因，绝不回退为 0、绝不改写参数。
    /// <para>「不变量普通十进制」：仅允许可带前导符号与小数点的普通十进制（拒绝千分位、指数、货币符号、前后除空格外字符）。</para>
    /// </summary>
    public static (decimal? Rate, string Reason) ParseRate(IReadOnlyList<string> rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);

        if (rawValues.Count == 0)
            return (null, RateMissingReason);

        if (rawValues.Count > 1)
            return (null, RateDuplicateReason);

        var text = (rawValues[0] ?? string.Empty).Trim();
        var styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out var value))
            return (null, RateMalformedReason);

        if (value < 0m)
            return (null, RateNegativeReason);

        if (value > 100m)
            return (null, RateExcessiveReason);

        return (value, string.Empty);
    }
}
