using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-271 Stage 1）的复合分组纯规则：把「最多两个有区别的基础分组维度」校验为有序维度，
/// 并用「类型化复合键」对当前预览页事实行分组。
/// <para>类型化复合键：每个维度先规范为稳定串（客户 = 主键十进制字符串；月份 = yyyy-MM），再用「长度前缀」拼接，
/// 绝不使用无长度信息的字符串拼接（避免客户 / 月份取值碰撞）；维度顺序即用户保存顺序，组序确定性。</para>
/// <para>缺失 / 非法维度值显式形成「未知」桶（未知客户 / 未知月份），绝不并入其它维度；金额 / 币种分区由调用方按
/// 数据集口径提供读取函数，本规则只负责「分组维度 + 当前页覆盖」，绝不跨币种 / 单位合并。</para>
/// <para>无数据库依赖，便于逐条单测；Stage 1 仅支持 customer / month 两个基础维度，不支持相关 / 计算 / 未授权字段分组。</para>
/// </summary>
public static class ReportConfigurationGroupingRules
{
    /// <summary>复合分组最多支持的基础维度数。</summary>
    public const int MaxCompositeGroupings = 2;

    // ==================== 维度目录解析 ====================

    /// <summary>
    /// 解析数据集的可用分组维度目录（有序）。优先使用目录暴露的 <see cref="ReportConfigurationDatasetDto.GroupingDimensions"/>；
    /// 为空时按旧契约（GroupingKeys + GroupCustomerFieldKey / GroupMonthFieldKey）派生，保证既有目录 / 测试兼容。
    /// </summary>
    public static IReadOnlyList<ReportConfigurationGroupingDimensionDto> ResolveDimensions(
        ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.GroupingDimensions is { Count: > 0 })
            return dataset.GroupingDimensions;

        var result = new List<ReportConfigurationGroupingDimensionDto>();
        foreach (var key in dataset.GroupingKeys ?? Array.Empty<string>())
        {
            if (string.Equals(key, ReportConfigurationConstants.GroupNone, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(key, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ReportConfigurationGroupingDimensionDto(
                    ReportConfigurationConstants.GroupCustomer,
                    "按客户分组",
                    dataset.GroupCustomerFieldKey ?? "customerId",
                    ReportConfigurationConstants.TypeNumber,
                    ReportConfigurationConstants.GroupingSemanticsIdentity));
            }
            else if (string.Equals(key, ReportConfigurationConstants.GroupMonth, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ReportConfigurationGroupingDimensionDto(
                    ReportConfigurationConstants.GroupMonth,
                    "按月份分组",
                    dataset.GroupMonthFieldKey ?? "orderDate",
                    ReportConfigurationConstants.TypeDate,
                    ReportConfigurationConstants.GroupingSemanticsCalendarMonth));
            }
        }

        return result;
    }

    // ==================== 有序维度规范化 / 校验 ====================

    /// <summary>
    /// 把已通过校验的分组键规范化为有序维度列表：去除空白与 none、去重（保持首次出现顺序）。
    /// </summary>
    public static IReadOnlyList<string> NormalizeGroupingKeys(IReadOnlyList<string>? grouping)
    {
        var result = new List<string>();
        foreach (var raw in grouping ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var key = raw.Trim();
            if (string.Equals(key, ReportConfigurationConstants.GroupNone, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!result.Contains(key, StringComparer.OrdinalIgnoreCase))
                result.Add(key);
        }

        return result;
    }

    /// <summary>
    /// 校验并返回有序有效分组维度（fail closed）：拒绝空键、重复、未知、none 与其它维度并存、超过两个维度、
    /// 以及数据集未授权 / 隐藏底层字段的维度。
    /// </summary>
    public static IReadOnlyList<string> ValidateGrouping(
        IReadOnlyList<string>? grouping,
        ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var raw = grouping ?? Array.Empty<string>();
        var keys = new List<string>();
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item))
                throw BusinessException.InvalidParameter("分组维度不能为空");
            keys.Add(item.Trim());
        }

        var hasNone = keys.Any(k => string.Equals(k, ReportConfigurationConstants.GroupNone, StringComparison.OrdinalIgnoreCase));
        if (hasNone)
        {
            if (keys.Count > 1)
                throw BusinessException.InvalidParameter("none 不能与其它分组维度并存，请只选择不分组或具体维度");
            return Array.Empty<string>();
        }

        if (keys.Count > MaxCompositeGroupings)
            throw BusinessException.InvalidParameter($"分组维度不能超过 {MaxCompositeGroupings} 个（当前收到 {keys.Count} 个）");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dimensionByKey = ResolveDimensions(dataset).ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var hasExplicitDimensions = dataset.GroupingDimensions is { Count: > 0 };

        foreach (var key in keys)
        {
            if (!seen.Add(key))
                throw BusinessException.InvalidParameter($"重复分组维度: {key}");

            if (!IsKnownGroupingKey(key))
                throw BusinessException.InvalidParameter($"未知分组维度: {key}");

            if (!dataset.GroupingKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"分组维度 {key} 不是数据集 {dataset.DatasetKey} 支持的分组");

            // 目录提供维度元数据时，强制校验底层字段已授权且未隐藏；旧契约回退（无维度元数据）沿用 GroupingKeys 白名单校验。
            if (hasExplicitDimensions)
            {
                if (!dimensionByKey.TryGetValue(key, out var dimension) || string.IsNullOrWhiteSpace(dimension.FieldKey))
                    throw BusinessException.InvalidParameter($"分组维度 {key} 缺少授权必需字段");

                if (!HasAuthorizedField(dataset, dimension.FieldKey))
                    throw BusinessException.InvalidParameter($"分组维度 {key} 的底层字段 {dimension.FieldKey} 未授权或已隐藏");
            }
        }

        return keys;
    }

    /// <summary>分组键是否为已知有限取值（customer / month；none 由调用方单独处理）。</summary>
    public static bool IsKnownGroupingKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        return string.Equals(key, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, ReportConfigurationConstants.GroupMonth, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAuthorizedField(ReportConfigurationDatasetDto dataset, string fieldKey)
    {
        return dataset.Fields?.Any(f =>
            !f.Hidden && string.Equals(f.Key, fieldKey, StringComparison.OrdinalIgnoreCase)) == true;
    }

    // ==================== 类型化复合键 / 桶 ====================

    /// <summary>
    /// 为单个事实行构建分组桶：按保存顺序计算每个维度的稳定类型化值 / 标签 / 未知态，并生成
    /// 长度前缀的类型化复合键、中文复合标签与确定性排序键。
    /// </summary>
    public static ReportConfigurationGroupBucket BuildGroupBucket(
        Dictionary<string, object?> row,
        IReadOnlyList<string> groupings,
        IReadOnlyList<ReportConfigurationGroupingDimensionDto> dimensions)
    {
        if (row is null)
            throw new ArgumentNullException(nameof(row));

        var effective = NormalizeGroupingKeys(groupings);
        if (effective.Count == 0)
            return new ReportConfigurationGroupBucket("all", "全部", "0", Array.Empty<ReportConfigurationGroupDimensionValueDto>());

        var values = new List<ReportConfigurationGroupDimensionValueDto>(effective.Count);
        foreach (var key in effective)
        {
            var dimension = dimensions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw BusinessException.InvalidParameter($"未知分组维度: {key}");
            values.Add(BuildDimensionValue(dimension, row));
        }

        var compositeKey = BuildCompositeKey(values.Select(v => v.Value).ToList());
        var compositeLabel = string.Join(" · ", values.Select(v => v.Label));
        var sortKey = BuildCompositeSortKey(values);
        return new ReportConfigurationGroupBucket(compositeKey, compositeLabel, sortKey, values);
    }

    /// <summary>构建单个维度的稳定类型化值 / 标签 / 未知态。</summary>
    public static ReportConfigurationGroupDimensionValueDto BuildDimensionValue(
        ReportConfigurationGroupingDimensionDto dimension,
        Dictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(dimension);

        if (string.Equals(dimension.Semantics, ReportConfigurationConstants.GroupingSemanticsCalendarMonth, StringComparison.OrdinalIgnoreCase))
        {
            var date = ReadDate(row, dimension.FieldKey);
            if (date == DateTime.MinValue)
                return new ReportConfigurationGroupDimensionValueDto(dimension.Key, UnknownLabelFor(dimension.Key), null, true);

            var yyyyMM = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return new ReportConfigurationGroupDimensionValueDto(
                dimension.Key, $"{date.Year}年{date.Month}月", yyyyMM, false);
        }

        // identity（初始客户）：按稳定主键原样分组，非法 / 非正主键为显式未知。
        var id = ReadLong(row, dimension.FieldKey);
        if (id <= 0)
            return new ReportConfigurationGroupDimensionValueDto(dimension.Key, UnknownLabelFor(dimension.Key), null, true);

        var value = id.ToString(CultureInfo.InvariantCulture);
        var label = string.Equals(dimension.Key, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase)
            ? $"客户 #{id}"
            : $"{dimension.Label} #{id}";
        return new ReportConfigurationGroupDimensionValueDto(dimension.Key, label, value, false);
    }

    private static string UnknownLabelFor(string dimensionKey)
    {
        return dimensionKey switch
        {
            ReportConfigurationConstants.GroupCustomer => "未知客户",
            ReportConfigurationConstants.GroupMonth => "未知月份",
            _ => "未知",
        };
    }

    /// <summary>分组维度列头短标签（供工作台 / Excel / PDF 共用，按保存顺序渲染复合维度）。</summary>
    public static string DimensionHeaderLabel(string? dimensionKey)
    {
        return dimensionKey switch
        {
            ReportConfigurationConstants.GroupCustomer => "客户",
            ReportConfigurationConstants.GroupMonth => "月份",
            _ => dimensionKey ?? string.Empty,
        };
    }

    /// <summary>长度前缀拼接：绝不使用无长度信息的字符串拼接，避免「客户 1 + 月份 2026-01」与「客户 1 2026 + 月份 01」碰撞。</summary>
    public static string BuildCompositeKey(IReadOnlyList<string?> values)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var value in values)
        {
            if (value is null)
            {
                builder.Append("-1:");
                continue;
            }
            builder.Append(value.Length).Append(':').Append(value);
        }

        return builder.ToString();
    }

    /// <summary>确定性排序键：每个维度用固定宽度的规范串（客户 D19 / 月份 yyyyMM），未知桶置后。</summary>
    public static string BuildCompositeSortKey(IReadOnlyList<ReportConfigurationGroupDimensionValueDto> values)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var value in values)
        {
            if (value.IsUnknown)
            {
                builder.Append('~'); // ASCII 126，排在所有数字之后
                continue;
            }

            if (string.Equals(value.Key, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                builder.Append(id.ToString("D19", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(value.Value ?? string.Empty);
            }
        }

        return builder.ToString();
    }

    // ==================== 复合分组小计（当前预览页、按币种分区） ====================

    /// <summary>
    /// 从当前预览页事实行构建复合分组小计：组内按币种分区统计条数与金额（金额只对同币种求和），
    /// 绝不跨币种相加、绝不追加全匹配合计。金额 / 币种读取函数由数据集适配器提供。
    /// </summary>
    public static List<ReportConfigurationGroupSubtotalDto> BuildCompositeGroupSubtotals(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> groupings,
        IReadOnlyList<ReportConfigurationGroupingDimensionDto> dimensions,
        Func<Dictionary<string, object?>, string> readCurrency,
        Func<Dictionary<string, object?>, decimal?> readAmount)
    {
        ArgumentNullException.ThrowIfNull(readCurrency);
        ArgumentNullException.ThrowIfNull(readAmount);

        var buckets = new Dictionary<string, CompositeSubtotalAccumulator>(StringComparer.Ordinal);
        foreach (var row in rows ?? Array.Empty<Dictionary<string, object?>>())
        {
            if (row is null)
                continue;

            var bucket = BuildGroupBucket(row, groupings, dimensions);
            if (!buckets.TryGetValue(bucket.Key, out var accumulator))
            {
                accumulator = new CompositeSubtotalAccumulator(bucket);
                buckets[bucket.Key] = accumulator;
            }

            var currency = readCurrency(row) ?? string.Empty;
            var amount = readAmount(row) ?? 0m;
            if (!accumulator.Currencies.TryGetValue(currency, out var partition))
                partition = (0, 0m);
            accumulator.Currencies[currency] = (partition.Count + 1, partition.Amount + amount);
        }

        return buckets.Values
            .OrderBy(a => a.Bucket.SortKey, StringComparer.Ordinal)
            .Select(a => new ReportConfigurationGroupSubtotalDto(
                a.Bucket.Key,
                a.Bucket.Label,
                a.Currencies
                    .OrderBy(kv => CurrencyOrder(kv.Key))
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new ReportConfigurationCurrencyPartitionDto(
                        kv.Key, kv.Value.Count, kv.Value.Amount, null, null, null, string.Empty))
                    .ToList())
            {
                Dimensions = a.Bucket.Dimensions,
            })
            .ToList();
    }

    private static int CurrencyOrder(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return int.MaxValue;
        return Enum.TryParse<Currency>(currency, true, out var c) ? (int)c : int.MaxValue;
    }

    private sealed class CompositeSubtotalAccumulator
    {
        public CompositeSubtotalAccumulator(ReportConfigurationGroupBucket bucket)
        {
            Bucket = bucket;
        }

        public ReportConfigurationGroupBucket Bucket { get; }
        public Dictionary<string, (int Count, decimal Amount)> Currencies { get; } = new(StringComparer.Ordinal);
    }

    // ==================== 读取辅助（与 MetricRules 同源） ====================

    private static object? ReadValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static long ReadLong(Dictionary<string, object?> row, string key)
    {
        var value = ReadValue(row, key);
        if (value is null or DBNull)
            return 0L;
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0L;
        }
    }

    private static DateTime ReadDate(Dictionary<string, object?> row, string key)
    {
        var value = ReadValue(row, key);
        if (value is DateTime dt)
            return dt;
        if (value is DateTimeOffset dto)
            return dto.DateTime;
        if (value is null or DBNull)
            return DateTime.MinValue;
        if (DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        return DateTime.MinValue;
    }
}

/// <summary>类型化分组桶：复合键 / 中文标签 / 确定性排序键 + 有序维度值。</summary>
public sealed record ReportConfigurationGroupBucket(
    string Key,
    string Label,
    string SortKey,
    IReadOnlyList<ReportConfigurationGroupDimensionValueDto> Dimensions);



