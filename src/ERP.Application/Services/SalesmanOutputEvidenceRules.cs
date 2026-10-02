using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 业务员产值报表（ERP-236）纯分组规则：把「业务员级不安全跨币种金额 / 利润」替换为
/// 「业务员 Id × 原始原币」分桶。已知枚举币种按签名原币金额小计；未知 / 无效币种保留各自原始键
/// （如 <c>"999"</c>），金额为 null 仅保留订单头计数，绝不回落为 0、绝不换算汇率、绝不跨币种合计。
/// 利润恒为未知（null），因为当前商品售价 / 成本价不能证明历史可比较成本 / 利润。
/// 全部为纯函数（无数据库、无 IO），便于逐条单测。
/// </summary>
public static class SalesmanOutputEvidenceRules
{
    /// <summary>未知 / 无效币种的展示名（与 <see cref="ReportService.UnknownCurrencyGroup"/> 同源，绝不默认币种或推断汇率）</summary>
    public const string UnknownCurrencyGroup = ReportService.UnknownCurrencyGroup;

    /// <summary>员工姓名快照缺失 / 已删除时的显式未知身份</summary>
    public const string UnknownSalesmanName = "未知业务员";

    /// <summary>业务员身份证据：姓名快照缺失 / 已删除，身份未知（仅订单属性，非权限边界）</summary>
    public const string UnknownSalesmanIdentityEvidence = "业务员姓名快照缺失或已删除，身份未知（仅订单属性，非权限边界）";

    /// <summary>已知币种金额证据标签：已审核订单原币金额，非实际收款金额</summary>
    public const string KnownCurrencyEvidence = "原币金额（已审核订单，非实际收款金额）";

    /// <summary>未知币种证据标签：不推断币种，金额未知（仅计数证据）</summary>
    public const string UnknownCurrencyEvidence = "未知/无效币种：不推断币种，金额未知（仅计数证据）";

    /// <summary>金额口径证据标签：已审核订单金额（原币），非实际收款金额</summary>
    public const string AmountLabel = "已审核订单金额合计（原币，非实际收款金额）";

    /// <summary>利润证据标签（未知原因）</summary>
    public const string ProfitEvidence = "利润未知：当前商品售价/成本价不能证明历史可比较成本/利润，不回落为0";

    /// <summary>利润率证据标签（未知原因：与利润同源，当前商品售价/成本价不能证明历史可比较成本/利润）</summary>
    public const string ProfitRateEvidence = "利润率未知：与利润同源，当前商品售价/成本价不能证明历史可比较成本/利润，不回落为0";

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
}
