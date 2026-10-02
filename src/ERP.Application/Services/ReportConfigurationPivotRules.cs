using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-272 Stage 1）的有界透视纯规则：校验并构建「一个行维度 × 一个不同列维度 ×
/// 选中基础指标」的当前预览页透视矩阵。
/// <para>维度仅限数据集授权分组维度（初始 customer / month），复用 <see cref="ReportConfigurationGroupingRules"/> 的
/// 类型化维度值 / 长度前缀复合键 / 确定性排序键与显式未知桶；指标复用 <see cref="ReportConfigurationMetricRules"/>
/// 的粒度 / 单位 / 币种行为 / 允许函数契约，且按币种分区，绝不跨币种 / 单位合并。</para>
/// <para>口径：仅当前预览页事实行，绝不声称全匹配合计 / 全局报表合计；空交叉点不产生单元格（无事实 =
/// null 聚合值 + 已知计数 0），并显式区分「全部未知来源值」与「真实数值 0」；单元格汇总从源累加器直接计算，
/// 绝不求平均的平均。</para>
/// <para>有界：最多 2 个轴、4 个选中基础指标、32 个可见列（列轴桶数）与 200 个渲染行（行轴桶数），
/// 超限在渲染前显式拒绝（不静默截断）。无数据库依赖，便于逐条单测。</para>
/// </summary>
public static class ReportConfigurationPivotRules
{
    /// <summary>透视允许的轴数（行 + 列）</summary>
    public const int MaxPivotAxes = 2;

    /// <summary>透视允许的最大选中基础指标数（与聚合定义上限同源）</summary>
    public const int MaxPivotMetrics = ReportConfigurationRules.MaxAggregates;

    /// <summary>透视允许的最大可见列数（列轴桶数）</summary>
    public const int MaxPivotColumns = ReportConfigurationExecutionLimits.MaxPreviewColumns;

    /// <summary>透视允许的最大渲染行数（行轴桶数）</summary>
    public const int MaxPivotRows = ReportConfigurationExecutionLimits.MaxPreviewRows;

    private const string CurrencyFieldKey = "currency";

    // ==================== 校验（先于任何源读取） ====================

    /// <summary>
    /// 校验已持久化的有界透视定义（fail closed）：schema 版本、授权且不同的行 / 列维度、不与分组共存、
    /// 以及选中基础指标契约（数量 1 ~ 4）。无透视时直接返回（非透视行为不变）。
    /// </summary>
    public static void Validate(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dataset);

        var pivot = definition.Pivot;
        if (pivot is null)
            return;

        if (pivot.SchemaVersion != ReportConfigurationRules.CurrentSchemaVersion)
            throw BusinessException.InvalidParameter(
                $"不支持的透视 schema 版本: {pivot.SchemaVersion}（当前仅支持 {ReportConfigurationRules.CurrentSchemaVersion}）");

        var row = (pivot.RowDimension ?? string.Empty).Trim();
        var column = (pivot.ColumnDimension ?? string.Empty).Trim();
        if (row.Length == 0 || column.Length == 0)
            throw BusinessException.InvalidParameter("透视必须同时选择行维度与列维度");

        if (string.Equals(row, ReportConfigurationConstants.GroupNone, StringComparison.OrdinalIgnoreCase)
            || string.Equals(column, ReportConfigurationConstants.GroupNone, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter("透视维度不能选择「不分组」");

        if (string.Equals(row, column, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter("透视行维度与列维度必须不同");

        // 授权且不同的两个基础分组维度（复用复合分组校验，拒绝未知 / 未授权 / 重复 / 超限）
        var axes = ReportConfigurationGroupingRules.ValidateGrouping(new[] { row, column }, dataset);
        if (axes.Count != MaxPivotAxes)
            throw BusinessException.InvalidParameter("透视必须恰好选择一个行维度与一个不同的列维度");

        // 透视与普通分组互斥（透视轴本身即分组，避免歧义）
        var groupings = ReportConfigurationGroupingRules.NormalizeGroupingKeys(definition.Grouping);
        if (groupings.Count > 0)
            throw BusinessException.InvalidParameter("透视与分组不能同时选择，请清空分组或取消透视");

        var aggregates = definition.Aggregates ?? new List<ReportConfigurationAggregate>();
        if (aggregates.Count == 0)
            throw BusinessException.InvalidParameter("透视至少选择 1 个指标");
        if (aggregates.Count > MaxPivotMetrics)
            throw BusinessException.InvalidParameter($"透视指标不能超过 {MaxPivotMetrics} 个（当前 {aggregates.Count} 个），请减少所选指标");
    }

    // ==================== 执行（当前预览页、类型化轴 + 币种分区） ====================

    /// <summary>
    /// 从当前预览页事实行构建有界透视结果：先构建确定性行 / 列轴并做上限预检（超限在渲染前显式拒绝），
    /// 再对每个选中指标做稀疏矩阵累加（行 × 列 × 币种分区）。空交叉点无单元格。
    /// </summary>
    public static ReportConfigurationPivotResultDto Build(
        ReportConfigurationDefinition definition,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dataset);
        var pivot = definition.Pivot ?? throw BusinessException.InvalidParameter("缺少透视定义");

        var rowDimension = (pivot.RowDimension ?? string.Empty).Trim();
        var columnDimension = (pivot.ColumnDimension ?? string.Empty).Trim();
        var dimensions = ReportConfigurationGroupingRules.ResolveDimensions(dataset);
        var rowMeta = dimensions.FirstOrDefault(d => string.Equals(d.Key, rowDimension, StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.InvalidParameter($"未知行维度: {rowDimension}");
        var columnMeta = dimensions.FirstOrDefault(d => string.Equals(d.Key, columnDimension, StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.InvalidParameter($"未知列维度: {columnDimension}");

        var source = rows ?? Array.Empty<Dictionary<string, object?>>();

        // 1) 确定性轴（按排序键升序；未知桶置后）
        var rowAxis = BuildAxis(source, rowMeta, cancellationToken);
        var columnAxis = BuildAxis(source, columnMeta, cancellationToken);

        // 2) 有界预检：超限在渲染前显式拒绝，绝不静默截断
        if (columnAxis.Count > MaxPivotColumns)
            throw new BusinessException(
                $"透视列数 {columnAxis.Count} 超出上限 {MaxPivotColumns}，请缩小筛选范围或减少列维度取值（当前预览页）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);
        if (rowAxis.Count > MaxPivotRows)
            throw new BusinessException(
                $"透视行数 {rowAxis.Count} 超出上限 {MaxPivotRows}，请缩小筛选范围或减少行维度取值（当前预览页）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        var rowIndexByKey = IndexByKey(rowAxis);
        var columnIndexByKey = IndexByKey(columnAxis);

        var aggregates = definition.Aggregates ?? new List<ReportConfigurationAggregate>();
        if (aggregates.Count > MaxPivotMetrics)
            throw new BusinessException(
                $"透视指标 {aggregates.Count} 超出上限 {MaxPivotMetrics}，请减少所选指标",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        var metrics = new List<ReportConfigurationPivotMetricDto>(aggregates.Count);
        foreach (var aggregate in aggregates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (aggregate is null || string.IsNullOrWhiteSpace(aggregate.FieldKey))
                continue;

            metrics.Add(BuildMetric(aggregate, dataset, source, rowMeta, columnMeta, rowIndexByKey, columnIndexByKey));
        }

        return new ReportConfigurationPivotResultDto
        {
            RowDimension = rowDimension,
            ColumnDimension = columnDimension,
            RowAxis = rowAxis,
            ColumnAxis = columnAxis,
            Metrics = metrics,
            SourceRowCount = source.Count,
            Coverage = ReportConfigurationConstants.CoverageCurrentPage,
        };
    }

    private static List<ReportConfigurationPivotAxisDto> BuildAxis(
        IReadOnlyList<Dictionary<string, object?>> rows,
        ReportConfigurationGroupingDimensionDto meta,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<string, ReportConfigurationPivotAxisDto>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row is null)
                continue;

            var value = ReportConfigurationGroupingRules.BuildDimensionValue(meta, row);
            var key = ReportConfigurationGroupingRules.BuildCompositeKey(new[] { value.Value });
            if (buckets.ContainsKey(key))
                continue;

            buckets[key] = new ReportConfigurationPivotAxisDto
            {
                Key = key,
                Label = value.Label,
                SortKey = ReportConfigurationGroupingRules.BuildCompositeSortKey(new[] { value }),
                Dimensions = new List<ReportConfigurationGroupDimensionValueDto> { value },
                IsUnknown = value.IsUnknown,
            };
        }

        return buckets.Values
            .OrderBy(a => a.SortKey, StringComparer.Ordinal)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, int> IndexByKey(IReadOnlyList<ReportConfigurationPivotAxisDto> axis)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < axis.Count; i++)
            map[axis[i].Key] = i;
        return map;
    }

    private static ReportConfigurationPivotMetricDto BuildMetric(
        ReportConfigurationAggregate aggregate,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyList<Dictionary<string, object?>> rows,
        ReportConfigurationGroupingDimensionDto rowMeta,
        ReportConfigurationGroupingDimensionDto columnMeta,
        IReadOnlyDictionary<string, int> rowIndexByKey,
        IReadOnlyDictionary<string, int> columnIndexByKey)
    {
        var function = (aggregate.Function ?? string.Empty).Trim();
        var fieldKey = (aggregate.FieldKey ?? string.Empty).Trim();
        var field = FindField(dataset, fieldKey);

        var isCount = string.Equals(function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase);
        var partition = !isCount && ReportConfigurationMetricRules.IsMonetaryUnit(field?.CurrencyUnit);

        var metric = new ReportConfigurationPivotMetricDto
        {
            Key = fieldKey,
            Function = function,
            Label = field?.Label ?? fieldKey,
            Unit = field?.CurrencyUnit,
            CurrencyBehavior = partition
                ? ReportConfigurationMetricRules.CurrencyBehaviorPartition
                : ReportConfigurationMetricRules.CurrencyBehaviorNone,
        };

        var buckets = new Dictionary<(int Row, int Column, string Currency), CellAccumulator>();
        foreach (var row in rows)
        {
            if (row is null)
                continue;

            var rowValue = ReportConfigurationGroupingRules.BuildDimensionValue(rowMeta, row);
            var rowKey = ReportConfigurationGroupingRules.BuildCompositeKey(new[] { rowValue.Value });
            if (!rowIndexByKey.TryGetValue(rowKey, out var rowIndex))
                continue;

            var columnValue = ReportConfigurationGroupingRules.BuildDimensionValue(columnMeta, row);
            var columnKey = ReportConfigurationGroupingRules.BuildCompositeKey(new[] { columnValue.Value });
            if (!columnIndexByKey.TryGetValue(columnKey, out var columnIndex))
                continue;

            var currencyKey = partition ? ReadCurrency(row) : string.Empty;
            var key = (rowIndex, columnIndex, currencyKey);
            if (!buckets.TryGetValue(key, out var cell))
            {
                var isUnknownCurrency = partition && currencyKey.Length == 0;
                cell = new CellAccumulator
                {
                    RowIndex = rowIndex,
                    ColumnIndex = columnIndex,
                    Currency = partition
                        ? (isUnknownCurrency ? ReportConfigurationMetricRules.UnknownCurrencyText : currencyKey)
                        : string.Empty,
                    Reason = isUnknownCurrency ? ReportConfigurationMetricRules.UnknownCurrencyReason : null,
                };
                buckets[key] = cell;
            }

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

        foreach (var cell in buckets.Values)
        {
            metric.Cells.Add(new ReportConfigurationPivotCellDto
            {
                RowIndex = cell.RowIndex,
                ColumnIndex = cell.ColumnIndex,
                Currency = string.IsNullOrEmpty(cell.Currency) ? null : cell.Currency,
                Value = Finalize(cell, function),
                KnownCount = cell.KnownCount,
                MissingCount = cell.MissingCount,
                SourceCount = cell.SourceCount,
                Reason = cell.Overflow ? ReportConfigurationMetricRules.OverflowReason : cell.Reason,
            });
            metric.KnownCount += cell.KnownCount;
            metric.MissingCount += cell.MissingCount;
            metric.SourceCount += cell.SourceCount;
        }

        metric.Cells.Sort((a, b) =>
        {
            var rowCompare = a.RowIndex.CompareTo(b.RowIndex);
            if (rowCompare != 0)
                return rowCompare;
            var columnCompare = a.ColumnIndex.CompareTo(b.ColumnIndex);
            if (columnCompare != 0)
                return columnCompare;
            return StringComparer.Ordinal.Compare(a.Currency ?? string.Empty, b.Currency ?? string.Empty);
        });

        return metric;
    }

    private static decimal? Finalize(CellAccumulator cell, string function)
    {
        if (string.Equals(function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase))
            return cell.KnownCount;

        if (cell.Overflow)
            return null;
        if (cell.KnownCount == 0)
            return null;

        return function switch
        {
            ReportConfigurationConstants.AggregateSum => cell.Sum,
            ReportConfigurationConstants.AggregateAverage => cell.Sum!.Value / cell.KnownCount,
            ReportConfigurationConstants.AggregateMin => cell.Min,
            ReportConfigurationConstants.AggregateMax => cell.Max,
            _ => null,
        };
    }

    private static ReportConfigurationFieldDto? FindField(ReportConfigurationDatasetDto dataset, string key)
    {
        foreach (var field in dataset.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
                return field;
        }
        return null;
    }

    // ==================== 渲染辅助（UI / Excel / PDF 共用同一份口径） ====================

    /// <summary>透视指标标题（指标标签 + 函数 + 单位；与指标汇总同口径）。</summary>
    public static string MetricTitle(ReportConfigurationPivotMetricDto metric)
    {
        var unit = string.IsNullOrWhiteSpace(metric.Unit) ? string.Empty : $"（{metric.Unit}）";
        return $"{metric.Label}（{ReportConfigurationMetricRules.FunctionLabel(metric.Function)}）{unit}";
    }

    /// <summary>
    /// 把某交叉点的一组币种分区格式化为规范单元格文本（货币指标「币种 数值」，非货币直接数值；
    /// 多个币种用「 / 」分隔，绝不跨币种相加；空交叉点返回空字符串）。
    /// </summary>
    public static string FormatCellText(IReadOnlyList<ReportConfigurationPivotCellDto>? partitions)
    {
        if (partitions is null || partitions.Count == 0)
            return string.Empty;

        var ordered = partitions
            .OrderBy(p => CurrencyOrder(p.Currency))
            .ThenBy(p => p.Currency ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var parts = new List<string>(ordered.Count);
        foreach (var partition in ordered)
        {
            var valueText = partition.Value is null
                ? string.Empty
                : partition.Value.Value.ToString("0.##", CultureInfo.InvariantCulture);
            parts.Add(string.IsNullOrEmpty(partition.Currency)
                ? valueText
                : (valueText.Length == 0 ? partition.Currency : $"{partition.Currency} {valueText}"));
        }

        return string.Join(" / ", parts);
    }

    private static int CurrencyOrder(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || string.Equals(currency, ReportConfigurationMetricRules.UnknownCurrencyText, StringComparison.Ordinal))
            return int.MaxValue;
        return Enum.TryParse<Currency>(currency, true, out var c) ? (int)c : int.MaxValue;
    }

    // ==================== 读取 / 累加辅助（与 MetricRules 同源） ====================

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

    private sealed class CellAccumulator
    {
        public string Currency = string.Empty;
        public string? Reason;
        public int RowIndex;
        public int ColumnIndex;
        public int SourceCount;
        public int KnownCount;
        public int MissingCount;
        public bool Overflow;
        public decimal? Sum;
        public decimal? Min;
        public decimal? Max;
    }
}
