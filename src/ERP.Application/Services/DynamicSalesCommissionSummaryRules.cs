using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态业务员提成证据报表（ERP-247）「全匹配」汇总的纯规则：在分页与选定列投影之前，
/// 对同一份完整的有界、作用域化、已筛选 <see cref="ReportDtos.SalesCommissionItem"/> 列表做纯派生，
/// 独立呈现「按原始原币合并全部匹配业务员桶 × 原币证据行」的汇总面板，绝不跨币种合计金额、绝不累加百分比、
/// 绝不读取第二来源或求和当前页行。无数据库依赖，便于逐条单测。
/// <para>每个桶对应一个持久化原始币种键；已知币种求和签名原币金额（仅在全部金额已知时）、
/// 不相交已审核订单数求和 OrderCount、去重业务员桶数去重 SalesmanId（未指定业务员桶单独计一个，非员工人数）；
/// 未知 / 无效币种保留各自原始键、金额 null 且显式未知订单计数；利润 / 利润率 / 提成额恒为 null 并给出历史依据说明；
/// 跨币种去重业务员桶与不相交已审核订单数为全局派生；当前参考比例仅在全部匹配证据行一致携带同一已知比例与依据时保留，
/// 缺失 / 冲突保持 null 并给原因，绝不加权平均 / 求和 / 回退为 0。</para>
/// </summary>
public static class DynamicSalesCommissionSummaryRules
{
    // ==================== 文案 ====================

    /// <summary>全匹配原币汇总面板标题</summary>
    public const string CurrencySummaryTitle = "全匹配原币汇总（按原始原币合并全部匹配业务员桶 × 原币证据行）";

    /// <summary>金额完整（全部已知）说明</summary>
    public const string KnownAmountCompleteText =
        "金额完整：全部为已知币种，签名原币金额合计（绝不跨币种合计、不回退为 0）";

    /// <summary>金额未知 / 不完整说明</summary>
    public const string UnknownAmountIncompleteText =
        "金额未知：存在未知/无效币种，仅订单头计数证据（不回退为 0、不推断币种）";

    /// <summary>无匹配证据时的显式说明</summary>
    public const string EmptyText =
        "无匹配证据：没有符合所选日期范围与数据范围的已审核销售订单";

    /// <summary>当前参考比例一致已知时的空原因（非 null 且无原因）</summary>
    public const string RateKnownReason = "";

    /// <summary>无匹配证据时的当前参考比例原因</summary>
    public const string RateEmptyReason =
        "无匹配证据，当前参考比例无来源（不回退为 0）";

    /// <summary>匹配证据行未一致携带已知比例时的原因</summary>
    public const string RateMissingReason =
        "匹配证据行未一致携带当前参考比例（缺失/重复/非法/负数/超范围），当前参考比例未知（不回退为 0）";

    /// <summary>匹配证据行比例相互冲突时的原因</summary>
    public const string RateConflictReason =
        "匹配证据行的当前参考比例相互冲突，当前参考比例未知（不回退为 0、不做加权平均/求和）";

    /// <summary>
    /// 汇总覆盖口径：覆盖全部匹配的有界、作用域化、已筛选业务员桶 × 原币证据行（分页与选定列投影之前服务端派生），
    /// 与当前页 / 选定列无关；金额仅按原始原币独立小计、绝不跨币种合计 / 比较，绝不读取第二来源或求和当前页行。
    /// </summary>
    public const string SummaryCoverageText =
        "覆盖范围：全部匹配的有界、作用域化、已筛选业务员桶 × 原币证据行（分页与选定列投影之前服务端派生），" +
        "与当前页 / 选定列无关；金额仅按原始原币独立小计，绝不跨币种合计 / 比较，绝不读取第二来源或求和当前页行；" +
        "证据依据为已审核、未删除、授权客户销售订单证据，非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款";

    /// <summary>利润 / 利润率 / 提成额历史依据：所有桶恒未知（null），不做当前价成本 / 利润 / 提成推算。</summary>
    public const string ProfitBasisText =
        SalesCommissionEvidenceRules.ProfitEvidence + "；" +
        SalesCommissionEvidenceRules.ProfitRateEvidence + "；" +
        SalesCommissionEvidenceRules.CommissionEvidence;

    // ==================== 汇总列 ====================

    /// <summary>全匹配原币汇总列（固定，与明细页选定列 / 当前页无关）</summary>
    public static readonly IReadOnlyList<DynamicSalesCommissionReportFieldDto> CurrencySummaryColumns =
        new List<DynamicSalesCommissionReportFieldDto>
        {
            new("currency", "原币币种", "string", false),
            new("currencyLabel", "原币币种标签", "string", false),
            new("approvedOrders", "已审核订单数", "number", false),
            new("uniqueSalesmanBuckets", "去重业务员桶", "number", false),
            new("salesAmount", "已知原币金额合计", "number", false),
            new("profit", "利润(未知)", "number", false),
            new("profitRate", "利润率(未知)", "number", false),
            new("commissionAmount", "提成额(未知)", "number", false),
            new("amountCompletenessText", "金额完整度", "string", false),
            new("profitReasonText", "利润依据", "string", false),
            new("profitRateReasonText", "利润率依据", "string", false),
            new("commissionAmountReasonText", "提成额依据", "string", false),
        };

    // ==================== 纯派生 ====================

    /// <summary>
    /// 在分页 / 选定列投影之前，基于全部匹配的有界、作用域化、已筛选业务员桶 × 原币证据行纯派生「全匹配」汇总。
    /// 稳定原始币种键排序；未知 / 无效币种保留各自原始键、金额 null 仅计数证据；绝不跨币种合计 / 比较。
    /// </summary>
    public static DynamicSalesCommissionSummaryDto BuildSummary(
        IReadOnlyList<ReportDtos.SalesCommissionItem>? items)
    {
        var source = items ?? Array.Empty<ReportDtos.SalesCommissionItem>();

        var currencyRows = source
            .GroupBy(x => x.Currency ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(BuildCurrencyRow)
            .ToList();

        var (rate, rateReason) = ResolveReferenceRate(source);

        return new DynamicSalesCommissionSummaryDto
        {
            CurrencyColumns = CurrencySummaryColumns.ToList(),
            CurrencyRows = currencyRows,
            GlobalUniqueSalesmanBuckets = source.Select(x => x.SalesmanId).Distinct().Count(),
            GlobalApprovedOrders = source.Sum(x => x.OrderCount),
            CurrentReferenceRate = rate,
            CurrentReferenceRateReason = rateReason,
            CoverageText = SummaryCoverageText,
            ProfitBasisText = ProfitBasisText,
            EmptyText = EmptyText,
        };
    }

    private static DynamicSalesCommissionCurrencySummaryDto BuildCurrencyRow(
        IGrouping<string, ReportDtos.SalesCommissionItem> group)
    {
        var rows = group.ToList();
        var everyKnown = rows.All(r => r.SalesAmount.HasValue);

        return new DynamicSalesCommissionCurrencySummaryDto
        {
            Currency = group.Key,
            CurrencyLabel = SalesCommissionEvidenceRules.CurrencyGroupLabel(group.Key),
            ApprovedOrders = rows.Sum(r => r.OrderCount),
            UniqueSalesmanBuckets = rows.Select(r => r.SalesmanId).Distinct().Count(),
            SalesAmount = everyKnown ? (decimal?)rows.Sum(r => r.SalesAmount!.Value) : null,
            AmountCompletenessText = everyKnown ? KnownAmountCompleteText : UnknownAmountIncompleteText,
            Profit = null,
            ProfitRate = null,
            CommissionAmount = null,
            ProfitReasonText = SalesCommissionEvidenceRules.ProfitEvidence,
            ProfitRateReasonText = SalesCommissionEvidenceRules.ProfitRateEvidence,
            CommissionAmountReasonText = SalesCommissionEvidenceRules.CommissionEvidence,
        };
    }

    /// <summary>
    /// 解析当前参考比例：仅在非空匹配证据行全部一致携带同一已知比例与依据时保留（含显式 0，视为已知）；
    /// 空来源 / 部分缺失或未知 / 相互冲突一律返回 null 并给显式原因，绝不加权平均 / 求和 / 回退为 0。
    /// </summary>
    private static (decimal? Rate, string Reason) ResolveReferenceRate(
        IReadOnlyList<ReportDtos.SalesCommissionItem> rows)
    {
        if (rows.Count == 0)
            return (null, RateEmptyReason);

        var knownRates = rows
            .Where(r => r.CommissionRate.HasValue
                        && string.Equals(r.CommissionRateEvidence,
                            SalesCommissionEvidenceRules.CommissionRateEvidence, StringComparison.Ordinal))
            .Select(r => r.CommissionRate!.Value)
            .ToList();

        if (knownRates.Count == 0)
            return (null, RateMissingReason);

        var distinctKnown = knownRates.Distinct().ToList();
        if (distinctKnown.Count > 1)
            return (null, RateConflictReason);

        var allConsistent = rows.All(r => r.CommissionRate.HasValue
                                          && string.Equals(r.CommissionRateEvidence,
                                              SalesCommissionEvidenceRules.CommissionRateEvidence, StringComparison.Ordinal)
                                          && r.CommissionRate.Value == distinctKnown[0]);

        return allConsistent ? (distinctKnown[0], RateKnownReason) : (null, RateMissingReason);
    }
}
