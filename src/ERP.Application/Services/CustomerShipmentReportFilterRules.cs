using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态客户出货量证据报表（ERP-231）可选应用筛选的纯规则：客户 Id（正整数）与原币币种（仅已知 <see cref="Currency"/> 枚举码）
/// 的校验 / 规范化 / 上下文文案。无数据库依赖，便于逐条单测。
/// <para>币种口径与既有 <see cref="CurrencyAmountRules"/> 共享同一套「已知币种码」（<see cref="Currency"/> 枚举 CNY / USD / EUR / HKD / GBP / JPY）：
/// 留空 = 不过滤；已知码去首尾空白并大写为枚举名；纯数字 / 未知取值直接拒绝，绝不回退为 CNY 或任何默认币种；
/// 不提供「未知币种」选择器（省略筛选时保留全部已知 / 未知币种证据）。</para>
/// </summary>
public static class CustomerShipmentReportFilterRules
{
    /// <summary>支持的原币币种码列表（与 <see cref="Currency"/> 枚举 / <see cref="CurrencyAmountRules"/> 已知码同源）</summary>
    public const string SupportedCurrencyText = "CNY / USD / EUR / HKD / GBP / JPY";

    /// <summary>目录口径：支持的筛选能力说明（仅能力说明，不含任何客户 / 订单 / 金额数据）</summary>
    public const string SupportedFilterText =
        "可选应用筛选：客户 Id（正整数）与原币币种（" + SupportedCurrencyText + "）；留空 = 不过滤（保留全部已审核销售订单证据）";

    /// <summary>
    /// 规范化可选应用筛选（fail closed）：客户 Id 必须为正整数、原币币种仅接受空白（全部）/ 已知 <see cref="Currency"/> 枚举码；
    /// 非法 / 数字 / 未知取值直接拒绝，绝不静默丢弃或回退币种。两项全部留空时返回 null（表示不过滤）。
    /// </summary>
    public static CustomerShipmentFilterDto? NormalizeFilter(CustomerShipmentFilterDto? filter)
    {
        if (filter is null)
            return null;

        var customerId = ValidateFilterCustomerId(filter.CustomerId);
        var currency = NormalizeCurrencyFilter(filter.Currency);

        if (customerId is null && currency is null)
            return null;

        return new CustomerShipmentFilterDto
        {
            CustomerId = customerId,
            Currency = currency,
        };
    }

    /// <summary>校验客户 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤。</summary>
    public static long? ValidateFilterCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 筛选必须是正整数（大于 0）");
        return customerId;
    }

    /// <summary>
    /// 规范化原币币种筛选（fail closed）：留空 = 全部；已知 <see cref="Currency"/> 枚举码（大小写不敏感）去首尾空白并
    /// 大写归一化为枚举名；纯数字与未知取值直接拒绝（绝不回退为 CNY 或任何默认币种）。不提供「未知币种」桶。
    /// </summary>
    public static string? NormalizeCurrencyFilter(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        var value = currency.Trim();
        if (value.All(char.IsDigit))
            throw BusinessException.InvalidParameter(
                $"无效的原币币种筛选: {currency}（可选：{SupportedCurrencyText}）");

        if (Enum.TryParse<Currency>(value, true, out var parsed) && Enum.IsDefined(parsed))
            return parsed.ToString();

        throw BusinessException.InvalidParameter(
            $"无效的原币币种筛选: {currency}（可选：{SupportedCurrencyText}）");
    }

    /// <summary>把已规范化的应用筛选渲染为上下文文案（客户 Id / 原币币种）；无筛选时返回空串。</summary>
    public static string BuildFilterContext(CustomerShipmentFilterDto? filter)
    {
        if (filter is null)
            return string.Empty;

        var parts = new List<string>();
        if (filter.CustomerId.HasValue)
            parts.Add($"客户 Id {filter.CustomerId.Value}");
        if (!string.IsNullOrEmpty(filter.Currency))
            parts.Add($"原币币种 {filter.Currency}");

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }
}
