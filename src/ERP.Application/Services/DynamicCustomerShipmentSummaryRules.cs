using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态客户出货量证据报表（ERP-232）「全匹配」汇总的纯规则：在分页与选定列投影之前，
/// 对同一份完整的有界、作用域化、已筛选 <see cref="ReportDtos.CustomerShipmentItem"/> 列表做纯派生，
/// 独立呈现「原币金额汇总」与「精确单位数量汇总」，绝不跨币种 / 跨单位合计、绝不读取第二来源或求和当前页行。
/// 无数据库依赖，便于逐条单测。
/// <para>金额汇总仅在原币面板出现（已知币种签名合计、未知币种金额为 null 仅计数证据），绝不复制到任何单位行；
/// 单位汇总在每个原币桶内按明细原始单位身份（Ordinal、不归一化 / 换算）合并现有单位分组，
/// 未知单位数量为 null 仅明细计数；缺失 / 不完整来源显式保留逐客户 × 原币完整度原因与不完整桶数。</para>
/// </summary>
public static class DynamicCustomerShipmentSummaryRules
{
    /// <summary>原币金额汇总面板标题</summary>
    public const string CurrencySummaryTitle = "全匹配原币金额汇总（按原币合并全部匹配客户 × 原币证据行）";

    /// <summary>精确单位数量汇总面板标题</summary>
    public const string UnitSummaryTitle = "全匹配精确单位数量汇总（按原币内原始单位合并已审核订单明细数量）";

    /// <summary>
    /// 汇总覆盖口径：覆盖全部匹配的有界、作用域化、已筛选客户 × 原币证据行（分页与选定列投影之前服务端派生），
    /// 与当前页 / 选定列无关；金额仅按原币独立小计、数量按原币内原始单位独立合计，绝不跨币种 / 跨单位合计。
    /// </summary>
    public const string SummaryCoverageText =
        "覆盖范围：全部匹配的有界、作用域化、已筛选客户 × 原币证据行（分页与选定列投影之前服务端派生），" +
        "与当前页 / 选定列无关；金额仅按原币独立小计、数量按原币内原始单位独立合计，绝不跨币种 / 跨单位合计；" +
        "证据依据为已审核、未删除销售订单，非实际出库 / 装柜 / 收款";

    /// <summary>原币金额汇总列（固定，与明细页选定列无关）</summary>
    public static readonly IReadOnlyList<DynamicCustomerShipmentReportFieldDto> CurrencySummaryColumns =
        new List<DynamicCustomerShipmentReportFieldDto>
        {
            new("currency", "原币币种", "string", false),
            new("customerCount", "客户数", "number", false),
            new("orderCount", "已审核订单数", "number", false),
            new("totalAmount", "原币金额合计", "number", false),
            new("evidence", "金额证据", "string", false),
        };

    /// <summary>精确单位数量汇总列（固定，与明细页选定列无关；不含任何货币金额列）</summary>
    public static readonly IReadOnlyList<DynamicCustomerShipmentReportFieldDto> UnitSummaryColumns =
        new List<DynamicCustomerShipmentReportFieldDto>
        {
            new("currency", "原币币种", "string", false),
            new("unit", "精确单位", "string", false),
            new("quantity", "数量合计", "number", false),
            new("detailCount", "明细条数", "number", false),
            new("quantityLabel", "数量证据", "string", false),
        };

    /// <summary>
    /// 在分页 / 选定列投影之前，基于全部匹配的有界、作用域化、已筛选客户 × 原币证据行纯派生「全匹配」汇总。
    /// </summary>
    public static DynamicCustomerShipmentSummaryDto BuildSummary(
        IReadOnlyList<ReportDtos.CustomerShipmentItem>? items)
    {
        var source = items ?? Array.Empty<ReportDtos.CustomerShipmentItem>();

        var currencyRows = source
            .GroupBy(x => x.Currency ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                var known = rows[0].TotalAmount.HasValue;
                return new DynamicCustomerShipmentCurrencySummaryDto
                {
                    Currency = g.Key,
                    CurrencyLabel = CustomerShipmentEvidenceRules.CurrencyGroupLabel(g.Key),
                    TotalAmount = known ? (decimal?)rows.Sum(r => r.TotalAmount!.Value) : null,
                    CustomerCount = rows.Select(r => r.CustomerId).Distinct().Count(),
                    OrderCount = rows.Sum(r => r.OrderCount),
                    Evidence = known
                        ? CustomerShipmentEvidenceRules.KnownCurrencyEvidence
                        : CustomerShipmentEvidenceRules.UnknownCurrencyEvidence,
                };
            })
            .ToList();

        var unitAcc = new Dictionary<(string Currency, string Unit), UnitAcc>();
        foreach (var item in source)
        {
            foreach (var ug in item.UnitGroups)
            {
                (string Currency, string Unit) key = (item.Currency ?? string.Empty, ug.Unit ?? string.Empty);
                if (!unitAcc.TryGetValue(key, out var acc))
                {
                    acc = new UnitAcc
                    {
                        Currency = key.Currency,
                        Unit = key.Unit,
                        QuantityLabel = ug.QuantityLabel ?? string.Empty,
                    };
                    unitAcc[key] = acc;
                }

                if (ug.Quantity.HasValue)
                    acc.Quantity = (acc.Quantity ?? 0m) + ug.Quantity.Value;
                acc.DetailCount += ug.DetailCount;
            }
        }

        var unitRows = unitAcc
            .OrderBy(kv => kv.Key.Currency, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Unit, StringComparer.Ordinal)
            .Select(kv => new DynamicCustomerShipmentUnitSummaryDto
            {
                Currency = kv.Value.Currency,
                Unit = kv.Value.Unit,
                Quantity = kv.Value.Quantity,
                DetailCount = kv.Value.DetailCount,
                QuantityLabel = kv.Value.QuantityLabel,
            })
            .ToList();

        var incomplete = source
            .Where(r => !string.IsNullOrEmpty(r.QuantityCompletenessReason))
            .ToList();
        var completenessReasons = incomplete
            .Select(r => r.QuantityCompletenessReason)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new DynamicCustomerShipmentSummaryDto(
            CurrencySummaryColumns.ToList(),
            UnitSummaryColumns.ToList(),
            currencyRows,
            unitRows,
            incomplete.Count,
            completenessReasons,
            SummaryCoverageText);
    }

    private sealed class UnitAcc
    {
        public string Currency = string.Empty;
        public string Unit = string.Empty;
        public decimal? Quantity;
        public int DetailCount;
        public string QuantityLabel = string.Empty;
    }
}
