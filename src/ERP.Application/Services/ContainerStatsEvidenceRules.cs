using System.Globalization;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 柜量与装柜利用率统计（ERP-251）纯分组规则：把「按柜号全局合并」替换为
/// 「装柜日历日 × 精确原始非空白柜号」证据桶，空白 / 纯空白柜号按装柜清单 Id 独立成桶，
/// 绝不并入一个伪造的共享柜；装载率与柜型恒为未知（无权威容积 / 整柜 / 满载证据）。
/// 全部为纯函数（无数据库、无 IO），便于逐条单测；口径与 <see cref="ReportService.GetContainerStatsAsync"/> 一致。
/// </summary>
public static class ContainerStatsEvidenceRules
{
    /// <summary>空白 / 纯空白柜号时的标签前缀（按装柜清单 Id 独立，绝不合并）</summary>
    public const string BlankContainerPrefix = "未填柜号";

    /// <summary>未知柜型（不再按范围客户数推断整柜 / 拼柜）</summary>
    public const string UnknownType = "未知";

    /// <summary>柜型未知原因：范围客户数不是整柜 / 拼柜的权威依据</summary>
    public const string TypeUnknownReason = "范围客户数不是整柜/拼柜的权威依据，柜型未知";

    /// <summary>装载率未知原因：缺少权威容积 / 整柜 / 满柜证据，绝不按 68m³ 估算</summary>
    public const string UtilizationUnknownReason = "缺少权威容积/整柜/满柜证据，装载率未知（不按 68m³ 估算）";

    /// <summary>客户身份无效 / 缺失原因：装柜清单头 CustomerId &lt;= 0，无有效客户身份</summary>
    public const string InvalidCustomerReason = "客户身份缺失或无效（CustomerId<=0），不计入授权范围客户数";

    /// <summary>客户数口径标签：授权范围客户数（不同正数 CustomerId）</summary>
    public const string CustomerCountLabel = "授权范围客户数（不同正数 CustomerId）";

    /// <summary>来源依据标签：仅已审核、未删除、授权范围的装柜清单头证据</summary>
    public const string SourceLabel = "仅已审核、未删除、授权范围装柜清单头证据";

    /// <summary>覆盖依据标签：日期窗口有界（<=366 天，含首尾，结束日排他上界），来源探测 501 行上限 500 张</summary>
    public const string CoverageLabel = "日期窗口有界(<=366天含首尾，结束日排他上界)；来源探测 Take(501)，>500 张即拒绝";

    /// <summary>分组依据标签：按装柜日历日 × 精确原始非空白柜号分组；空白柜号按装柜清单 Id 独立，绝不合并</summary>
    public const string GroupingLabel = "按装柜日历日×精确原始非空白柜号分组；空白柜号按装柜清单Id独立，绝不合并";

    /// <summary>容积依据标签：无权威容积 / 整柜 / 满柜证据，装载率未知</summary>
    public const string CapacityLabel = "无权威容积/整柜/满柜证据，装载率未知";

    /// <summary>柜型依据标签：不推断整柜 / 拼柜 / 满载状态</summary>
    public const string TypeLabel = "不推断整柜/拼柜/满载状态，柜型未知";

    /// <summary>数量依据标签：签名持久化头箱数/毛重/体积证据，非实际发货/实体柜数量</summary>
    public const string QuantityLabel = "签名持久化头箱数/毛重/体积证据，非实际发货/实体柜数量";

    /// <summary>柜号是否为空白 / 纯空白（空白柜号按装柜清单 Id 独立，绝不合并）</summary>
    public static bool IsContainerNoBlank(string? containerNo) => string.IsNullOrWhiteSpace(containerNo);

    /// <summary>
    /// 展示柜号：精确原始非空白柜号原样返回（不 trim、不改大小写）；空白 / 纯空白柜号使用
    /// 「未填柜号（装柜清单 #Id）」独立标签，绝不并入一个伪造的共享柜。
    /// </summary>
    public static string ContainerNoLabel(string? containerNo, long loadingListId)
        => IsContainerNoBlank(containerNo) ? $"{BlankContainerPrefix}（装柜清单 #{loadingListId}）" : containerNo!;

    /// <summary>证据桶分组身份的种类：原始柜号桶 / 空白（缺柜号）柜号桶。</summary>
    public enum ContainerStatsBucketKind
    {
        /// <summary>原始非空白柜号桶（按精确原始柜号合并）</summary>
        Raw,

        /// <summary>空白 / 纯空白柜号桶（按装柜清单 Id 独立）</summary>
        Blank,
    }

    /// <summary>
    /// 证据桶分组身份（类型化 / 消歧）：明确区分「原始柜号」与「空白 / 纯空白柜号（按装柜清单 Id 独立）」两种桶身份，
    /// 避免原始柜号字面文本（如「#1」）与空白柜号装柜清单 Id（1）产生字符串键碰撞；Raw 桶按原始柜号合并、Blank 桶按装柜清单 Id 独立。
    /// </summary>
    public readonly record struct ContainerStatsBucketId
    {
        public ContainerStatsBucketId(DateTime loadingDate, ContainerStatsBucketKind kind, string key)
        {
            LoadingDate = loadingDate;
            Kind = kind;
            Key = key;
        }

        /// <summary>装柜日历日（仅日期部分）</summary>
        public DateTime LoadingDate { get; }

        /// <summary>桶身份种类（原始柜号 / 空白柜号）</summary>
        public ContainerStatsBucketKind Kind { get; }

        /// <summary>身份键：Raw 为精确原始非空白柜号；Blank 为装柜清单 Id 文本</summary>
        public string Key { get; }

        public static ContainerStatsBucketId From(DateTime loadingDate, string? containerNo, long loadingListId)
            => IsContainerNoBlank(containerNo)
                ? new ContainerStatsBucketId(loadingDate.Date, ContainerStatsBucketKind.Blank, loadingListId.ToString(CultureInfo.InvariantCulture))
                : new ContainerStatsBucketId(loadingDate.Date, ContainerStatsBucketKind.Raw, containerNo!);
    }

    /// <summary>
    /// 稳定分组键（消歧后字符串）：装柜日历日 + 类型标记 + 精确原始非空白柜号 / 空白装柜清单 Id。
    /// 用于稳定排序（Ordinal），保证同一来源在不同请求下的桶顺序一致，且原始柜号「#1」与空白清单 Id 1 绝不碰撞。
    /// </summary>
    public static string BucketKey(DateTime loadingDate, string? containerNo, long loadingListId)
    {
        var id = ContainerStatsBucketId.From(loadingDate, containerNo, loadingListId);
        return id.Kind == ContainerStatsBucketKind.Blank
            ? $"{id.LoadingDate:yyyy-MM-dd}|B|{id.Key}"
            : $"{id.LoadingDate:yyyy-MM-dd}|R|{id.Key}";
    }

    /// <summary>按分组键在内存中构建证据桶（每组为一个桶；只读，不落库、不执行 SQL）</summary>
    public static IReadOnlyList<ReportDtos.ContainerStatsItem> BuildBuckets(
        IEnumerable<ContainerLoadingList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        return lists
            .GroupBy(x => ContainerStatsBucketId.From(x.LoadingDate, x.ContainerNo, x.Id))
            .Select(g =>
            {
                var first = g.First();
                var blank = g.Key.Kind == ContainerStatsBucketKind.Blank;
                var invalid = g.Count(x => x.CustomerId <= 0);
                return new ReportDtos.ContainerStatsItem
                {
                    BucketKey = BucketKey(first.LoadingDate, first.ContainerNo, first.Id),
                    LoadingDate = first.LoadingDate.Date,
                    ContainerNo = ContainerNoLabel(first.ContainerNo, first.Id),
                    ContainerNoBlank = blank,
                    LoadingListCount = g.Count(),
                    AuthorizedCustomerCount = g.Where(x => x.CustomerId > 0).Select(x => x.CustomerId).Distinct().Count(),
                    InvalidCustomerCount = invalid,
                    InvalidCustomerReason = invalid > 0 ? InvalidCustomerReason : string.Empty,
                    TotalCartons = g.Sum(x => x.TotalCartons),
                    TotalWeight = g.Sum(x => x.TotalWeight),
                    TotalVolume = g.Sum(x => x.TotalVolume),
                    Utilization = null,
                    UtilizationReason = UtilizationUnknownReason,
                    TypeText = UnknownType,
                    TypeReason = TypeUnknownReason,
                    CustomerCountLabel = CustomerCountLabel,
                    SourceLabel = SourceLabel,
                    CoverageLabel = CoverageLabel,
                    GroupingLabel = GroupingLabel,
                    CapacityLabel = CapacityLabel,
                    TypeLabel = TypeLabel,
                    QuantityLabel = QuantityLabel
                };
            })
            .OrderBy(x => x.LoadingDate)
            .ThenBy(x => x.BucketKey, StringComparer.Ordinal)
            .ToList();
    }
}
