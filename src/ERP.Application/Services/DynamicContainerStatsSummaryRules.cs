using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 柜量与装柜利用率证据报表（ERP-255）「全匹配」汇总的纯规则：在分页与选定列投影之前，
/// 对同一份完整的有界、作用域化、已筛选 <see cref="ReportDtos.ContainerStatsItem"/> 证据桶列表做纯派生，
/// 按装柜日历日升序呈现证据桶数 / 已审核装柜清单数 / 缺柜号（空白柜号）清单数与签名持久化头箱数 / 毛重 / 体积证据的独立单位合计，
/// 并给出相同的期间合计。绝不合计桶级授权范围客户数、绝不推断全局去重客户数或实体柜容量、绝不读取第二来源或求和当前页行。
/// 无数据库依赖，便于逐条单测。
/// </summary>
public static class DynamicContainerStatsSummaryRules
{
    /// <summary>每日汇总列（固定，与明细页选定列无关）：装柜日历日 / 证据桶数 / 已审核装柜清单数 / 缺柜号清单数 / 箱数 / 毛重 / 体积</summary>
    public static readonly IReadOnlyList<DynamicContainerStatsReportFieldDto> DailyColumns =
        new List<DynamicContainerStatsReportFieldDto>
        {
            new("date", "装柜日历日", "date", false),
            new("bucketCount", "证据桶数", "number", false),
            new("approvedLists", "已审核装柜清单数", "number", false),
            new("missingContainerNoCount", "缺柜号清单数", "number", false),
            new("totalCartons", "箱数(cartons)", "number", false),
            new("totalWeight", "毛重(kg)", "number", false),
            new("totalVolume", "体积(m³)", "number", false),
        };

    /// <summary>
    /// 汇总覆盖口径：覆盖全部匹配的有界、作用域化、已筛选装柜清单头证据（分页与选定列投影之前服务端派生），
    /// 与当前页 / 选定列无关；箱数 / 毛重 / 体积为签名持久化头证据的独立单位合计，绝不跨单位合计、绝不合计授权范围客户数或推断实体柜容量；
    /// 证据依据为已审核、未删除装柜清单头，非实际发货 / 实体柜 / 收入 / 装载率 / 柜型权威。
    /// </summary>
    public const string CoverageText =
        "覆盖范围：全部匹配的有界、作用域化、已筛选装柜清单头证据（分页与选定列投影之前服务端派生），" +
        "与当前页 / 选定列无关；箱数 / 毛重 / 体积为签名持久化头证据的独立单位合计，绝不跨单位合计、绝不合计授权范围客户数或推断实体柜容量；" +
        "证据依据为已审核、未删除装柜清单头，非实际发货 / 实体柜 / 收入 / 装载率 / 柜型权威";

    /// <summary>无证据时的显式说明（区分「无证据」与「已记录零」）</summary>
    public const string NoEvidenceContext =
        "全匹配汇总：没有符合所选日期范围、数据范围与筛选的已审核装柜清单头证据（无每日行、无期间合计）";

    /// <summary>
    /// 在分页 / 选定列投影之前，基于全部匹配的有界、作用域化、已筛选证据桶纯派生「全匹配」每日与期间汇总。
    /// </summary>
    public static DynamicContainerStatsReportSummaryDto BuildSummary(
        IReadOnlyList<ReportDtos.ContainerStatsItem>? items)
    {
        var source = items ?? Array.Empty<ReportDtos.ContainerStatsItem>();

        var daily = source
            .GroupBy(x => x.LoadingDate.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DynamicContainerStatsReportDailySummaryDto(
                g.Key,
                g.Count(),
                g.Sum(x => x.LoadingListCount),
                g.Where(x => x.ContainerNoBlank).Sum(x => x.LoadingListCount),
                g.Sum(x => x.TotalCartons),
                g.Sum(x => x.TotalWeight),
                g.Sum(x => x.TotalVolume)))
            .ToList();

        var period = new DynamicContainerStatsReportPeriodSummaryDto(
            source.Count,
            source.Sum(x => x.LoadingListCount),
            source.Where(x => x.ContainerNoBlank).Sum(x => x.LoadingListCount),
            source.Sum(x => x.TotalCartons),
            source.Sum(x => x.TotalWeight),
            source.Sum(x => x.TotalVolume));

        return new DynamicContainerStatsReportSummaryDto(
            DailyColumns.ToList(),
            daily,
            period,
            CoverageText,
            daily.Count == 0 ? NoEvidenceContext : string.Empty);
    }

    // ==================== 汇总 Excel 导出 ====================

    /// <summary>全匹配汇总 Excel 每日汇总工作表名（与明细「柜量装柜证据」工作表刻意区分）</summary>
    public const string SummarySheetName = "每日证据汇总";

    /// <summary>全匹配汇总 Excel 期间合计与口径工作表名</summary>
    public const string PeriodContextSheetName = "期间合计与口径";

    /// <summary>全匹配汇总 Excel 上下文表「全匹配覆盖」行标签</summary>
    public const string ContextCoverageLabel = "全匹配覆盖";

    /// <summary>空证据工作簿的显式标注（每日汇总工作表无每日行时写入第一数据行）</summary>
    public const string EmptyEvidenceRowText =
        "没有符合所选日期范围、数据范围与筛选的已审核装柜清单头证据（空证据工作簿，无每日行、无期间合计）";

    /// <summary>期间合计「证据桶数」行标签</summary>
    public const string PeriodBucketLabel = "期间证据桶数";

    /// <summary>期间合计「已审核装柜清单数」行标签</summary>
    public const string PeriodApprovedListsLabel = "期间已审核装柜清单数";

    /// <summary>期间合计「缺柜号清单数」行标签</summary>
    public const string PeriodMissingLabel = "期间缺柜号清单数";

    /// <summary>期间合计「箱数」行标签</summary>
    public const string PeriodCartonsLabel = "期间箱数(cartons)";

    /// <summary>期间合计「毛重」行标签</summary>
    public const string PeriodWeightLabel = "期间毛重(kg)";

    /// <summary>期间合计「体积」行标签</summary>
    public const string PeriodVolumeLabel = "期间体积(m³)";
}
