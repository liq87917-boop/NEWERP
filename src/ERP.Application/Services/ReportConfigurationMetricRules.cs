using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-267）的指标纯规则：从已授权字段派生指标描述符，按「粒度 / 单位 / 币种行为 /
/// 允许函数」契约校验已持久化的选中聚合（先于任何源读取），并对当前预览页执行通用指标汇总。
/// <para>口径：只计算用户已选中的聚合（sum / count / avg / min / max）；count 统计非 null 有效值并披露
/// 来源 / 缺失条数；avg 使用已知值条数作分母；空 / 全 null 的 sum / min / max / avg 保持 null；checked 十进制
/// 溢出返回显式未知原因（绝不静默置零）；货币指标按币种分区、未知币种单独隔离并附原因，绝不跨币种 / 单位合并；
/// 分组仅支持 customer / month（单一分组），组序与币种序确定性；覆盖口径显式为「当前预览页」而非全量合计。</para>
/// <para>无数据库依赖，便于逐条单测；不新增任何会计算法，仅复用已授权字段的既有值（含应收剩余 / 分摊的未知态）。</para>
/// </summary>
public static class ReportConfigurationMetricRules
{
    // ==================== 币种行为（有限） ====================

    /// <summary>货币指标：按币种分区，绝不跨币种合并。</summary>
    public const string CurrencyBehaviorPartition = "currency-partition";

    /// <summary>非货币指标：不按币种分区。</summary>
    public const string CurrencyBehaviorNone = "none";

    // ==================== 聚合函数中文标签（UI / Excel / PDF 共用） ====================

    public const string FunctionLabelSum = "合计";
    public const string FunctionLabelCount = "计数";
    public const string FunctionLabelAverage = "平均";
    public const string FunctionLabelMin = "最小";
    public const string FunctionLabelMax = "最大";

    // ==================== 显式原因（有界） ====================

    /// <summary>未知币种（单独隔离，绝不并入其它币种）。</summary>
    public const string UnknownCurrencyText = "未知币种";

    /// <summary>未知币种隔离原因说明。</summary>
    public const string UnknownCurrencyReason = "未知币种：单独隔离，不并入任何币种";

    /// <summary>数值溢出（checked 十进制，绝不静默置零）。</summary>
    public const string OverflowReason = "数值溢出";

    // ==================== 私有常量 ====================

    private const string MonetaryUnitText = "原币金额";
    private const string CurrencyFieldKey = "currency";

    // ==================== 指标描述符派生 ====================

    /// <summary>
    /// 从已授权字段白名单派生指标描述符（有限、只读）：稳定键 / 中文标签 / 类型 / 行粒度 / 单位 /
    /// 币种行为 / 允许函数与当前页覆盖口径。隐藏字段不暴露。
    /// <para>允许函数：count 对所有非隐藏字段开放（统计非 null 值）；sum / avg / min / max 仅对
    /// 已授权可聚合的数值字段开放（ID / 状态等一律不可作为数值求和）。</para>
    /// </summary>
    public static List<ReportConfigurationMetricDto> BuildMetrics(
        IEnumerable<ReportConfigurationFieldDto> fields, string grain)
    {
        var result = new List<ReportConfigurationMetricDto>();
        foreach (var field in fields)
        {
            if (field is null || field.Hidden)
                continue;

            var currencyBehavior = IsMonetaryUnit(field.CurrencyUnit)
                ? CurrencyBehaviorPartition
                : CurrencyBehaviorNone;

            result.Add(new ReportConfigurationMetricDto(
                field.Key,
                field.Label,
                field.Type,
                grain ?? string.Empty,
                field.CurrencyUnit,
                currencyBehavior,
                AllowedFunctions(field),
                ReportConfigurationConstants.CoverageCurrentPage));
        }

        return result;
    }

    /// <summary>字段允许的聚合函数（count 始终允许；sum / avg / min / max 仅限可聚合数值字段）。</summary>
    public static IReadOnlyList<string> AllowedFunctions(ReportConfigurationFieldDto field)
    {
        var functions = new List<string> { ReportConfigurationConstants.AggregateCount };
        if (field.Aggregatable
            && string.Equals(field.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase))
        {
            functions.Add(ReportConfigurationConstants.AggregateSum);
            functions.Add(ReportConfigurationConstants.AggregateAverage);
            functions.Add(ReportConfigurationConstants.AggregateMin);
            functions.Add(ReportConfigurationConstants.AggregateMax);
        }

        return functions;
    }

    /// <summary>是否为货币字段（需要按币种分区）。</summary>
    public static bool IsMonetaryUnit(string? unit)
        => string.Equals(unit, MonetaryUnitText, StringComparison.OrdinalIgnoreCase);

    /// <summary>聚合函数中文标签（未知函数原样返回）。</summary>
    public static string FunctionLabel(string function)
    {
        return (function ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            ReportConfigurationConstants.AggregateSum => FunctionLabelSum,
            ReportConfigurationConstants.AggregateCount => FunctionLabelCount,
            ReportConfigurationConstants.AggregateAverage => FunctionLabelAverage,
            ReportConfigurationConstants.AggregateMin => FunctionLabelMin,
            ReportConfigurationConstants.AggregateMax => FunctionLabelMax,
            _ => function ?? string.Empty,
        };
    }

    // ==================== 校验（先于任何源读取） ====================

    /// <summary>
    /// 校验已持久化的选中聚合（fail closed）：数量上限、函数有限、字段已授权且未隐藏、函数与字段的
    /// 粒度 / 单位 / 币种 / 允许函数契约兼容。任何不合规都在源读取前拒绝，绝不返回「部分有效」。
    /// </summary>
    public static void ValidateAggregates(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dataset);

        var aggregates = definition.Aggregates ?? new List<ReportConfigurationAggregate>();
        if (aggregates.Count > ReportConfigurationRules.MaxAggregates)
            throw BusinessException.InvalidParameter($"聚合定义数量不能超过 {ReportConfigurationRules.MaxAggregates} 个");

        var metrics = BuildMetrics(dataset.Fields, dataset.Grain)
            .ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var aggregate in aggregates)
            ValidateAggregate(aggregate, dataset, metrics);
    }

    private static void ValidateAggregate(
        ReportConfigurationAggregate aggregate,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyDictionary<string, ReportConfigurationMetricDto> metrics)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var function = (aggregate.Function ?? string.Empty).Trim();
        if (!ReportConfigurationRules.IsKnownAggregateFunction(function))
            throw BusinessException.InvalidParameter($"未知聚合函数: {function}");

        if (string.IsNullOrWhiteSpace(aggregate.FieldKey))
            throw BusinessException.InvalidParameter("聚合字段键不能为空");

        var key = aggregate.FieldKey.Trim();
        if (!TryGetField(dataset, key, out var field))
            throw BusinessException.InvalidParameter($"未知聚合字段: {key}");
        if (field.Hidden)
            throw BusinessException.InvalidParameter($"隐藏字段不允许用于聚合: {key}");

        if (!metrics.TryGetValue(key, out var metric))
            throw BusinessException.InvalidParameter($"字段不可用于聚合: {key}");

        if (!metric.AllowedFunctions.Contains(function, StringComparer.OrdinalIgnoreCase))
        {
            throw BusinessException.InvalidParameter(
                $"字段 {key} 不支持聚合函数 {function}（允许：{string.Join(" / ", metric.AllowedFunctions)}）");
        }
    }


    // ==================== 执行（当前预览页、分组 + 币种分区） ====================

    /// <summary>
    /// 对当前预览页行执行用户已选中的聚合，返回仅选中指标的汇总结果（分组 + 币种分区）。空页返回
    /// 空单元格列表（每个指标仍保留元数据，但绝不造假 0 / 全量合计）。</summary>
    public static List<ReportConfigurationMetricResultDto> Compute(
        ReportConfigurationDefinition definition,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyList<Dictionary<string, object?>> rows,
        string groupBy)
        => Compute(definition, dataset, rows, new[] { groupBy ?? ReportConfigurationConstants.GroupNone });

    /// <summary>
    /// 对当前预览页行执行用户已选中的聚合，返回仅选中指标的汇总结果（分组 + 币种分区）。
    /// <para>ERP-271：分组支持 0 / 1 / 2 个有序基础维度；复合分组使用类型化复合键与显式未知桶，绝不使用字符串拼接。</para>
    /// </summary>
    public static List<ReportConfigurationMetricResultDto> Compute(
        ReportConfigurationDefinition definition,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string>? groupings)
    {
        var results = new List<ReportConfigurationMetricResultDto>();
        var aggregates = definition?.Aggregates ?? new List<ReportConfigurationAggregate>();
        if (aggregates.Count == 0)
            return results;

        var effective = ReportConfigurationGroupingRules.NormalizeGroupingKeys(groupings);
        var metrics = BuildMetrics(dataset.Fields, dataset.Grain)
            .ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var aggregate in aggregates)
        {
            if (aggregate is null)
                continue;
            var result = ComputeAggregate(aggregate, dataset, metrics, rows, effective);
            if (result is not null)
                results.Add(result);
        }

        return results;
    }

    private static ReportConfigurationMetricResultDto ComputeAggregate(
        ReportConfigurationAggregate aggregate,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyDictionary<string, ReportConfigurationMetricDto> metrics,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> groupings)
    {
        var function = (aggregate.Function ?? string.Empty).Trim();
        var fieldKey = (aggregate.FieldKey ?? string.Empty).Trim();
        if (fieldKey.Length == 0 || !TryGetField(dataset, fieldKey, out var field))
            return null!;
        if (!metrics.TryGetValue(fieldKey, out var metric))
            return null!;

        var isCount = string.Equals(function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase);
        var partition = !isCount && IsMonetaryUnit(field.CurrencyUnit);

        var buckets = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        var source = rows ?? Array.Empty<Dictionary<string, object?>>();

        foreach (var row in source)
        {
            if (row is null)
                continue;

            var (groupKey, groupLabel, sortKey, dimensions) = GroupBucketOfComposite(row, groupings, dataset);
            if (!buckets.TryGetValue(groupKey, out var group))
            {
                group = new GroupAccumulator { Key = groupKey, Label = groupLabel, SortKey = sortKey, Dimensions = dimensions };
                buckets[groupKey] = group;
            }

            var cell = group.GetOrCreateCell(partition ? ReadCurrency(row) : null, partition);
            cell.SourceCount++;

            var raw = ReadValue(row, fieldKey);
            if (isCount)
            {
                if (raw is not null and not DBNull)
                    cell.KnownCount++;
                else
                    cell.MissingCount++;
                continue;
            }

            if (TryReadDecimal(raw, out var value, out var overflow))
            {
                cell.KnownCount++;
                Accumulate(cell, value, function);
            }
            else
            {
                cell.MissingCount++;
                if (overflow)
                    cell.Overflow = true;
            }
        }

        var result = new ReportConfigurationMetricResultDto
        {
            Key = fieldKey,
            Function = function,
            Label = field.Label,
            Unit = field.CurrencyUnit,
            CurrencyBehavior = metric.CurrencyBehavior,
        };

        foreach (var group in buckets.Values.OrderBy(b => b.SortKey, StringComparer.Ordinal))
        {
            var cells = group.Cells.Values
                .OrderBy(c => CurrencyOrder(c.Currency))
                .ThenBy(c => c.Currency ?? string.Empty, StringComparer.Ordinal);

            foreach (var cell in cells)
                result.Cells.Add(cell.ToDto(group, function));
        }

        return result;
    }


    // ==================== 分组 / 读取辅助 ====================

    private static (string Key, string Label, string SortKey) GroupBucketOf(
        Dictionary<string, object?> row, string groupBy, ReportConfigurationDatasetDto dataset)
    {
        if (string.Equals(groupBy, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase))
        {
            var customerId = ReadLong(row, dataset.GroupCustomerFieldKey ?? "customerId");
            return ($"customer:{customerId}", $"客户 #{customerId}",
                customerId.ToString("D19", CultureInfo.InvariantCulture));
        }

        if (string.Equals(groupBy, ReportConfigurationConstants.GroupMonth, StringComparison.OrdinalIgnoreCase))
        {
            var date = ReadDate(row, dataset.GroupMonthFieldKey ?? "orderDate");
            var yyyyMM = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return ($"month:{yyyyMM}", $"{date.Year}年{date.Month}月",
                date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        }

        return ("all", "全部", "0");
    }

    /// <summary>
    /// 计算单个事实行的分组桶：0 / 1 个维度沿用旧单分组契约（键 / 标签 / 排序不变），
    /// 2 个维度走 <see cref="ReportConfigurationGroupingRules.BuildGroupBucket"/> 的类型化复合键。
    /// </summary>
    private static (string Key, string Label, string SortKey, IReadOnlyList<ReportConfigurationGroupDimensionValueDto> Dimensions) GroupBucketOfComposite(
        Dictionary<string, object?> row, IReadOnlyList<string> groupings, ReportConfigurationDatasetDto dataset)
    {
        var effective = ReportConfigurationGroupingRules.NormalizeGroupingKeys(groupings);
        if (effective.Count == 0)
            return ("all", "全部", "0", Array.Empty<ReportConfigurationGroupDimensionValueDto>());

        if (effective.Count == 1)
        {
            var single = GroupBucketOf(row, effective[0], dataset);
            return (single.Key, single.Label, single.SortKey, Array.Empty<ReportConfigurationGroupDimensionValueDto>());
        }

        var bucket = ReportConfigurationGroupingRules.BuildGroupBucket(
            row, effective, ReportConfigurationGroupingRules.ResolveDimensions(dataset));
        return (bucket.Key, bucket.Label, bucket.SortKey, bucket.Dimensions);
    }

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

    private static string ReadCurrency(Dictionary<string, object?> row)
    {
        var value = ReadValue(row, CurrencyFieldKey);
        if (value is null or DBNull)
            return string.Empty;
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
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

    private static int CurrencyOrder(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || string.Equals(currency, UnknownCurrencyText, StringComparison.Ordinal))
            return int.MaxValue;
        return Enum.TryParse<Currency>(currency, true, out var c) ? (int)c : int.MaxValue;
    }

    private static bool TryReadDecimal(object? value, out decimal result, out bool overflow)
    {
        result = 0m;
        overflow = false;

        if (value is null or DBNull)
            return false;
        if (value is string s && s.Trim().Length == 0)
            return false;

        try
        {
            result = value switch
            {
                decimal m => m,
                int i => i,
                long l => l,
                short sh => sh,
                byte b => b,
                sbyte sb => sb,
                ushort us => us,
                uint ui => ui,
                ulong ul => checked((decimal)ul),
                float f => checked((decimal)f),
                double d => checked((decimal)d),
                _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            };
            return true;
        }
        catch (OverflowException)
        {
            overflow = true;
            return false;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            return false;
        }
    }

    private static void Accumulate(CellAccumulator cell, decimal value, string function)
    {
        if (cell.Overflow)
            return;

        switch (function)
        {
            case ReportConfigurationConstants.AggregateSum:
            case ReportConfigurationConstants.AggregateAverage:
                try
                {
                    cell.Sum = checked((cell.Sum ?? 0m) + value);
                }
                catch (OverflowException)
                {
                    cell.Overflow = true;
                    cell.Sum = null;
                }
                break;

            case ReportConfigurationConstants.AggregateMin:
                cell.Min = cell.Min.HasValue ? Math.Min(cell.Min.Value, value) : value;
                break;

            case ReportConfigurationConstants.AggregateMax:
                cell.Max = cell.Max.HasValue ? Math.Max(cell.Max.Value, value) : value;
                break;
        }
    }

    private static bool TryGetField(
        ReportConfigurationDatasetDto dataset, string key, out ReportConfigurationFieldDto field)
    {
        foreach (var candidate in dataset.Fields)
        {

            if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

        field = null!;
        return false;
    }

    // ==================== 聚合累加器（内部） ====================

    private sealed class GroupAccumulator
    {
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string SortKey = string.Empty;
        public IReadOnlyList<ReportConfigurationGroupDimensionValueDto> Dimensions = Array.Empty<ReportConfigurationGroupDimensionValueDto>();
        public Dictionary<string, CellAccumulator> Cells = new(StringComparer.Ordinal);

        public CellAccumulator GetOrCreateCell(string? currency, bool partition)
        {
            var key = currency ?? string.Empty;
            if (!Cells.TryGetValue(key, out var cell))
            {
                var isUnknownCurrency = partition && key.Length == 0;
                cell = new CellAccumulator
                {
                    Currency = partition ? (isUnknownCurrency ? UnknownCurrencyText : key) : string.Empty,
                    Reason = isUnknownCurrency ? UnknownCurrencyReason : null,
                };
                Cells[key] = cell;
            }

            return cell;
        }
    }

    private sealed class CellAccumulator
    {
        public string Currency = string.Empty;
        public string? Reason;
        public int SourceCount;
        public int KnownCount;
        public int MissingCount;
        public bool Overflow;
        public decimal? Sum;
        public decimal? Min;
        public decimal? Max;

        public ReportConfigurationMetricCellDto ToDto(GroupAccumulator group, string function)
        {
            return new ReportConfigurationMetricCellDto
            {
                GroupKey = group.Key,
                GroupLabel = group.Label,
                Dimensions = group.Dimensions.ToList(),
                Currency = string.IsNullOrEmpty(Currency) ? null : Currency,
                Value = Finalize(function),
                KnownCount = KnownCount,
                MissingCount = MissingCount,
                SourceCount = SourceCount,
                Reason = Overflow ? OverflowReason : Reason,
            };
        }

        private decimal? Finalize(string function)
        {
            if (string.Equals(function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase))
                return KnownCount;

            if (Overflow)
                return null;
            if (KnownCount == 0)
                return null;

            return function switch
            {
                ReportConfigurationConstants.AggregateSum => Sum,
                ReportConfigurationConstants.AggregateAverage => Sum / KnownCount,
                ReportConfigurationConstants.AggregateMin => Min,
                ReportConfigurationConstants.AggregateMax => Max,
                _ => null,
            };
        }
    }
}

