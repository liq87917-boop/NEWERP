using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 客户出货量统计表（ERP-228）纯分组规则：把「客户级不安全金额 / 数量合计」替换为
/// 「客户 × 原币」金额分组与「精确单位」数量分组，绝不跨币种合计金额、绝不归一化 / 换算 / 合并不兼容单位。
/// 全部为纯函数（无数据库、无 IO），便于逐条单测；口径与 <see cref="ReportService.GetCustomerShipmentStatsAsync"/> 一致。
/// </summary>
public static class CustomerShipmentEvidenceRules
{
    /// <summary>空白 / 未知单位时的分组名称（单位是自由文本，空白即视为未知；绝不把未知数量当成 0）</summary>
    public const string UnknownUnitGroup = "未知单位";

    /// <summary>未知币种时的分组名称（与 <see cref="ReportService.UnknownCurrencyGroup"/> 同源，绝不默认币种或推断汇率）</summary>
    public const string UnknownCurrencyGroup = ReportService.UnknownCurrencyGroup;

    /// <summary>已知币种金额证据标签：已审核订单原币金额，非实际收款金额</summary>
    public const string KnownCurrencyEvidence = "原币金额（已审核订单，非实际收款金额）";

    /// <summary>未知币种证据标签：不推断币种、金额未知，仅保留订单头计数证据</summary>
    public const string UnknownCurrencyEvidence = "未知币种：不推断币种，金额未知（仅计数证据）";

    /// <summary>已知单位数量证据标签：该精确单位下的已审核订单明细数量，非实际出库 / 装柜数量</summary>
    public const string KnownUnitQuantityLabel = "已审核订单明细数量（该单位，非实际出库/装柜数量）";

    /// <summary>未知单位数量证据标签：不推断单位、数量未知，仅保留明细计数证据</summary>
    public const string UnknownUnitQuantityLabel = "未知单位：不推断单位，数量未知（仅明细计数证据）";

    /// <summary>数量完整度：缺明细（该客户/币种下无任何未删除明细）</summary>
    public const string MissingDetailReason = "该客户/币种下无未删除明细，数量未知";

    /// <summary>数量完整度：存在无明细的已审核订单，明细证据不完整</summary>
    public const string IncompleteDetailReason = "存在无明细的已审核订单，明细证据不完整";

    /// <summary>数量完整度：存在空白 / 未知单位</summary>
    public const string UnknownUnitReason = "存在空白/未知单位，数量未知";

    /// <summary>数量完整度：存在多种不同单位（不归一化、不换算、不合并）</summary>
    public const string MixedUnitReason = "存在多种不同单位（不归一化/不换算/不合并），数量未知";

    /// <summary>原币分组键：已知枚举码返回其名称（如 USD），未定义枚举值归入「未知币种」；绝不默认币种或推断汇率</summary>
    public static string CurrencyGroupKey(Currency currency)
        => Enum.IsDefined(typeof(Currency), currency) ? currency.ToString() : UnknownCurrencyGroup;

    /// <summary>原币分组标签（如「USD 美元」；未知为「未知币种」）</summary>
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
    /// 精确单位分组：非删除明细按原始单位原样分组（仅空白视为未知；不 trim、不改大小写、不归一化、不换算、不合并不兼容单位）。
    /// 已知单位给签名数量合计；空白 / 未知单位数量为 null，仅保留明细计数证据。
    /// </summary>
    public static IReadOnlyList<ReportDtos.CustomerShipmentUnitGroup> BuildUnitGroups(
        IEnumerable<SalesOrderDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return details
            .GroupBy(d => string.IsNullOrWhiteSpace(d.Unit) ? UnknownUnitGroup : d.Unit, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ReportDtos.CustomerShipmentUnitGroup
            {
                Unit = g.Key,
                Quantity = g.Key == UnknownUnitGroup ? null : g.Sum(d => d.Quantity),
                DetailCount = g.Count(),
                QuantityLabel = g.Key == UnknownUnitGroup ? UnknownUnitQuantityLabel : KnownUnitQuantityLabel
            })
            .ToList();
    }

    /// <summary>
    /// 旧口径数量合计（可空）：仅当该客户/币种下明细证据完整（每张已审核订单都有未删除明细）、
    /// 且全部明细只有一种受支持的非空精确单位时可知；否则为 null（未知），绝不回落为 0。
    /// </summary>
    public static (decimal? Quantity, string Reason) BuildLegacyTotalQuantity(
        IReadOnlyList<SalesOrder> orders, IReadOnlyDictionary<long, List<SalesOrderDetail>> detailsByOrder)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(detailsByOrder);

        var allDetails = new List<SalesOrderDetail>();
        foreach (var order in orders)
        {
            if (!detailsByOrder.TryGetValue(order.Id, out var orderDetails) || orderDetails.Count == 0)
                return (null, IncompleteDetailReason);
            allDetails.AddRange(orderDetails);
        }

        if (allDetails.Count == 0)
            return (null, MissingDetailReason);

        var exactUnits = allDetails
            .Where(d => !string.IsNullOrWhiteSpace(d.Unit))
            .Select(d => d.Unit)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (exactUnits.Count == 0)
            return (null, UnknownUnitReason);

        if (allDetails.Any(d => string.IsNullOrWhiteSpace(d.Unit)))
            return (null, UnknownUnitReason);

        if (exactUnits.Count > 1)
            return (null, MixedUnitReason);

        return (allDetails.Sum(d => d.Quantity), string.Empty);
    }
}
