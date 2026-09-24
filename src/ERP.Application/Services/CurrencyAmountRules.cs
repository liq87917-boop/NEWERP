namespace ERP.Application.Services;

/// <summary>
/// 币种与金额精度的**唯一权威口径**（ERP-043 抽出，供装柜费用分摊（ERP-042）与供应商采购发票登记（ERP-043）共用，
/// 目的：同一系统内不允许出现第二套取整 / 币种规范化规则）：
/// <list type="number">
/// <item>币种规范化：去首尾空白并大写；空值按 <see cref="DefaultCurrency"/>（CNY）处理；</item>
/// <item>币种小数位：JPY / KRW / VND / IDR 等无小数币种为 <b>0 位</b>，其余（含未知币种）为 <b>2 位</b>；</item>
/// <item>金额取整：按币种小数位四舍五入，固定 <b>0.5 进位</b>（<see cref="MidpointRounding.AwayFromZero"/>），
/// 不使用银行家舍入 —— 结果确定、可复现，与前端展示一致。</item>
/// </list>
/// <para>边界：本类只做**纯计算**，不访问数据库、不写库、不涉及任何单据状态或金额口径的派生规则。</para>
/// </summary>
public static class CurrencyAmountRules
{
    /// <summary>默认币种（空值按它处理）</summary>
    public const string DefaultCurrency = "CNY";

    /// <summary>默认小数位（无小数币种之外的所有币种）</summary>
    public const int DefaultDecimals = 2;

    /// <summary>无小数币种（金额取整到整数）</summary>
    public static readonly string[] ZeroDecimalCurrencies = { "JPY", "KRW", "VND", "IDR" };

    /// <summary>币种规范化（去空白并大写；空值按 CNY 处理）</summary>
    public static string NormalizeCurrency(string? currency)
    {
        var value = (currency ?? string.Empty).Trim().ToUpperInvariant();
        return value.Length == 0 ? DefaultCurrency : value;
    }

    /// <summary>币种金额小数位：无小数币种为 0 位，其余（含未知币种）按 2 位处理</summary>
    public static int PrecisionOf(string? currency)
        => ZeroDecimalCurrencies.Contains(NormalizeCurrency(currency), StringComparer.Ordinal)
            ? 0
            : DefaultDecimals;

    /// <summary>按币种精度四舍五入（0.5 进位，确定性；不使用银行家舍入）</summary>
    public static decimal RoundAmount(decimal amount, string? currency)
        => Math.Round(amount, PrecisionOf(currency), MidpointRounding.AwayFromZero);
}
