using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态业务员产值证据报表（ERP-239）「全匹配」汇总的纯规则：在分页与选定列投影之前，
/// 对同一份完整的有界、作用域化、已筛选 <see cref="ReportDtos.SalesmanOutputItem"/> 列表做纯派生，
/// 独立呈现「按原始原币合并全部匹配业务员 × 原币证据行」的汇总面板，绝不跨币种合计 / 比较、
/// 绝不读取第二来源或求和当前页行。无数据库依赖，便于逐条单测。
/// <para>每个桶对应一个持久化原始币种键；已知币种求和签名原币金额（不先对每个业务员四舍五入）、
/// 已分配已审核订单数求和不相交 OrderCount、业务员数去重 SalesmanId；未知 / 无效币种保留各自原始键、
/// 金额为 null 且显式未知订单计数；利润与利润率恒为 null 并给出历史成本依据说明，绝不做当前价成本 / 利润推算。</para>
/// </summary>
public static class DynamicSalesmanOutputSummaryRules
{
    /// <summary>全匹配原币汇总面板标题</summary>
    public const string CurrencySummaryTitle = "全匹配原币汇总（按原始原币合并全部匹配业务员 × 原币证据行）";

    /// <summary>
    /// 汇总覆盖口径：覆盖全部匹配的有界、作用域化、已筛选业务员 × 原币证据行（分页与选定列投影之前服务端派生），
    /// 与当前页 / 选定列无关；金额仅按原始原币独立小计、绝不跨币种合计 / 比较，绝不读取第二来源或求和当前页行。
    /// </summary>
    public const string SummaryCoverageText =
        "覆盖范围：全部匹配的有界、作用域化、已筛选业务员 × 原币证据行（分页与选定列投影之前服务端派生），" +
        "与当前页 / 选定列无关；金额仅按原始原币独立小计，绝不跨币种合计 / 比较，绝不读取第二来源或求和当前页行；" +
        "证据依据为已分配业务员、已审核、未删除、授权客户销售订单，非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款";

    /// <summary>利润 / 利润率历史成本依据：所有桶恒未知（null），不做当前价成本 / 利润推算。</summary>
    public const string ProfitBasisText =
        SalesmanOutputEvidenceRules.ProfitEvidence + "；" + SalesmanOutputEvidenceRules.ProfitRateEvidence;

    /// <summary>全匹配原币汇总列（固定，与明细页选定列 / 当前页无关）</summary>
    public static readonly IReadOnlyList<DynamicSalesmanOutputReportFieldDto> CurrencySummaryColumns =
        new List<DynamicSalesmanOutputReportFieldDto>
        {
            new("currency", "原币币种", "string", false),
            new("currencyLabel", "原币币种标签", "string", false),
            new("salesmanCount", "业务员数", "number", false),
            new("orderCount", "已分配已审核订单数", "number", false),
            new("totalAmount", "原币金额合计", "number", false),
            new("totalProfit", "利润(未知)", "number", false),
            new("profitRate", "利润率(未知)", "number", false),
            new("evidence", "金额证据", "string", false),
            new("profitBasis", "利润依据", "string", false),
        };

    /// <summary>
    /// 在分页 / 选定列投影之前，基于全部匹配的有界、作用域化、已筛选业务员 × 原币证据行纯派生「全匹配」汇总。
    /// 稳定原始币种键排序；未知 / 无效币种保留各自原始键、金额 null 仅计数证据；绝不跨币种合计 / 比较。
    /// </summary>
    public static DynamicSalesmanOutputSummaryDto BuildSummary(
        IReadOnlyList<ReportDtos.SalesmanOutputItem>? items)
    {
        var source = items ?? Array.Empty<ReportDtos.SalesmanOutputItem>();

        var currencyRows = source
            .GroupBy(x => x.Currency ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                var known = rows[0].TotalAmount.HasValue;
                return new DynamicSalesmanOutputCurrencySummaryDto
                {
                    Currency = g.Key,
                    CurrencyLabel = SalesmanOutputEvidenceRules.CurrencyGroupLabel(g.Key),
                    TotalAmount = known ? (decimal?)rows.Sum(r => r.TotalAmount!.Value) : null,
                    SalesmanCount = rows.Select(r => r.SalesmanId).Distinct().Count(),
                    OrderCount = rows.Sum(r => r.OrderCount),
                    TotalProfit = null,
                    ProfitRate = null,
                    Evidence = known
                        ? SalesmanOutputEvidenceRules.KnownCurrencyEvidence
                        : SalesmanOutputEvidenceRules.UnknownCurrencyEvidence,
                    ProfitBasis = ProfitBasisText,
                };
            })
            .ToList();

        return new DynamicSalesmanOutputSummaryDto(
            CurrencySummaryColumns.ToList(),
            currencyRows,
            SummaryCoverageText,
            ProfitBasisText);
    }
}
