using System.Globalization;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 迁移 parity 四维比对的通用实现（ERP-329）：纯只读、确定性比较两个有界结果快照。
/// <list type="bullet">
/// <item><b>数据粒度（精确）</b>：列键与类型必须对齐，行数与稳定行键必须一致，单元格值按声明类型 null 感知比较。</item>
/// <item><b>币种 / 单位（fail closed）</b>：列声明的币种 / 单位口径与每一行的币种 / 单位分区必须一致；一侧分区、另一侧
/// 不分区（即一侧会跨币种 / 单位合并而另一侧不会）一律视为分歧。</item>
/// <item><b>权限（fail closed）</b>：当前账号行归属集合与数据范围指纹必须等价，任何分歧一律拒绝。</item>
/// </list>
/// <para>输出语义维度由独立导出比对接缝提供，本实现仅透传合成完整四维证据；绝不触碰数据库、绝不扩权。</para>
/// </summary>
public sealed class ReportMigrationParityComparator : IReportMigrationParityComparator
{
    /// <inheritdoc />
    public ReportMigrationParityComparisonResultDto Compare(
        ReportMigrationParitySnapshotDto legacy,
        ReportMigrationParitySnapshotDto generic,
        bool outputSemanticsMatched)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(generic);

        var mismatches = new List<ReportMigrationParityMismatchDto>();

        var dataGrainMatched = CompareDataGrain(legacy, generic, mismatches);
        var currencyUnitMatched = CompareCurrencyUnit(legacy, generic, mismatches);
        var permissionsMatched = ComparePermissions(legacy, generic, mismatches);

        var evidence = new ReportMigrationParityEvidenceDto(
            dataGrainMatched,
            currencyUnitMatched,
            permissionsMatched,
            outputSemanticsMatched);

        return new ReportMigrationParityComparisonResultDto(evidence, mismatches);
    }

    /// <summary>数据粒度：列键 / 类型 + 行数与稳定行键 + null 感知单元格值。</summary>
    private static bool CompareDataGrain(
        ReportMigrationParitySnapshotDto legacy,
        ReportMigrationParitySnapshotDto generic,
        List<ReportMigrationParityMismatchDto> mismatches)
    {
        var matched = true;

        var columnCount = legacy.Columns.Count;
        if (columnCount != generic.Columns.Count)
        {
            matched = false;
            mismatches.Add(Mismatch(
                ReportMigrationParityMismatchKind.Column,
                detail: $"列数不一致：旧路由 {legacy.Columns.Count} 列 vs 通用平台 {generic.Columns.Count} 列"));
        }

        var alignedColumnCount = Math.Min(legacy.Columns.Count, generic.Columns.Count);
        var columnsAligned = new bool[alignedColumnCount];
        for (var i = 0; i < alignedColumnCount; i++)
        {
            var left = legacy.Columns[i];
            var right = generic.Columns[i];
            var keyMatch = string.Equals(left.Key, right.Key, StringComparison.Ordinal);
            var typeMatch = string.Equals(left.Type, right.Type, StringComparison.Ordinal);
            columnsAligned[i] = keyMatch && typeMatch;

            if (!keyMatch)
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Column,
                    columnKey: left.Key,
                    columnType: left.Type,
                    detail: $"第 {i} 列键不一致：旧路由 '{left.Key}' vs 通用平台 '{right.Key}'"));
            }

            if (!typeMatch)
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Column,
                    columnKey: left.Key,
                    columnType: left.Type,
                    detail: $"第 {i} 列类型不一致：旧路由 '{left.Type}' vs 通用平台 '{right.Type}'"));
            }
        }

        var rowCount = legacy.Rows.Count;
        if (rowCount != generic.Rows.Count)
        {
            matched = false;
            mismatches.Add(Mismatch(
                ReportMigrationParityMismatchKind.Row,
                detail: $"行数不一致：旧路由 {legacy.Rows.Count} 行 vs 通用平台 {generic.Rows.Count} 行"));
        }

        var alignedRowCount = Math.Min(legacy.Rows.Count, generic.Rows.Count);
        var rowsAligned = new bool[alignedRowCount];
        for (var i = 0; i < alignedRowCount; i++)
        {
            rowsAligned[i] = RowKeysEqual(legacy.Rows[i].RowKeys, generic.Rows[i].RowKeys);
            if (!rowsAligned[i])
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Row,
                    rowIndex: i,
                    rowKey: RowKeyText(legacy.Rows[i].RowKeys),
                    detail: $"第 {i} 行行键不一致：旧路由 '{RowKeyText(legacy.Rows[i].RowKeys)}' vs 通用平台 '{RowKeyText(generic.Rows[i].RowKeys)}'"));
            }
        }

        for (var i = 0; i < alignedRowCount; i++)
        {
            if (!rowsAligned[i])
                continue;

            for (var j = 0; j < alignedColumnCount; j++)
            {
                if (!columnsAligned[j])
                    continue;

                var column = legacy.Columns[j];
                var leftCell = ElementAtOrDefault(legacy.Rows[i].Cells, j);
                var rightCell = ElementAtOrDefault(generic.Rows[i].Cells, j);
                if (CellValuesEqual(leftCell, rightCell, column.Type))
                    continue;

                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Cell,
                    columnKey: column.Key,
                    columnType: column.Type,
                    rowIndex: i,
                    rowKey: RowKeyText(legacy.Rows[i].RowKeys),
                    detail: $"第 {i} 行列 '{column.Key}' 单元格值不一致：旧路由 {FormatValue(leftCell)} vs 通用平台 {FormatValue(rightCell)}"));
            }
        }

        return matched;
    }

    /// <summary>币种 / 单位：列声明口径 + 行分区，任何合并口径分歧（一侧分区另一侧不分区）fail closed。</summary>
    private static bool CompareCurrencyUnit(
        ReportMigrationParitySnapshotDto legacy,
        ReportMigrationParitySnapshotDto generic,
        List<ReportMigrationParityMismatchDto> mismatches)
    {
        var matched = true;

        var alignedColumnCount = Math.Min(legacy.Columns.Count, generic.Columns.Count);
        for (var i = 0; i < alignedColumnCount; i++)
        {
            var left = legacy.Columns[i];
            var right = generic.Columns[i];

            if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Currency,
                    columnKey: left.Key,
                    columnType: left.Type,
                    currency: left.Currency,
                    detail: $"第 {i} 列 '{left.Key}' 币种口径不一致：旧路由 '{left.Currency ?? "<null>"}' vs 通用平台 '{right.Currency ?? "<null>"}'"));
            }

            if (!string.Equals(left.Unit, right.Unit, StringComparison.Ordinal))
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Unit,
                    columnKey: left.Key,
                    columnType: left.Type,
                    unit: left.Unit,
                    detail: $"第 {i} 列 '{left.Key}' 单位口径不一致：旧路由 '{left.Unit ?? "<null>"}' vs 通用平台 '{right.Unit ?? "<null>"}'"));
            }
        }

        var alignedRowCount = Math.Min(legacy.Rows.Count, generic.Rows.Count);
        for (var i = 0; i < alignedRowCount; i++)
        {
            var left = legacy.Rows[i];
            var right = generic.Rows[i];

            if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Currency,
                    rowIndex: i,
                    rowKey: RowKeyText(left.RowKeys),
                    currency: left.Currency,
                    detail: $"第 {i} 行币种分区不一致（会跨币种合并）：旧路由 '{left.Currency ?? "<null>"}' vs 通用平台 '{right.Currency ?? "<null>"}'"));
            }

            if (!string.Equals(left.Unit, right.Unit, StringComparison.Ordinal))
            {
                matched = false;
                mismatches.Add(Mismatch(
                    ReportMigrationParityMismatchKind.Unit,
                    rowIndex: i,
                    rowKey: RowKeyText(left.RowKeys),
                    unit: left.Unit,
                    detail: $"第 {i} 行单位分区不一致（会跨单位合并）：旧路由 '{left.Unit ?? "<null>"}' vs 通用平台 '{right.Unit ?? "<null>"}'"));
            }
        }

        return matched;
    }

    /// <summary>权限：当前账号行归属集合 + 数据范围指纹，任何分歧 fail closed。</summary>
    private static bool ComparePermissions(
        ReportMigrationParitySnapshotDto legacy,
        ReportMigrationParitySnapshotDto generic,
        List<ReportMigrationParityMismatchDto> mismatches)
    {
        var matched = true;

        var ownedMatch = OwnedRowIdsEqual(
            legacy.Permissions.OwnedRowIds,
            generic.Permissions.OwnedRowIds);
        if (!ownedMatch)
        {
            matched = false;
            mismatches.Add(Mismatch(
                ReportMigrationParityMismatchKind.Scope,
                scope: legacy.Permissions.DataScopeFingerprint,
                detail: $"当前账号行归属不一致：旧路由 {OwnedRowIdsText(legacy.Permissions.OwnedRowIds)} vs 通用平台 {OwnedRowIdsText(generic.Permissions.OwnedRowIds)}"));
        }

        var scopeMatch = string.Equals(
            legacy.Permissions.DataScopeFingerprint,
            generic.Permissions.DataScopeFingerprint,
            StringComparison.Ordinal);
        if (!scopeMatch)
        {
            matched = false;
            mismatches.Add(Mismatch(
                ReportMigrationParityMismatchKind.Scope,
                scope: legacy.Permissions.DataScopeFingerprint,
                detail: $"数据范围指纹不一致：旧路由 '{legacy.Permissions.DataScopeFingerprint}' vs 通用平台 '{generic.Permissions.DataScopeFingerprint}'"));
        }

        return matched;
    }

    private static ReportMigrationParityMismatchDto Mismatch(
        ReportMigrationParityMismatchKind kind,
        string? columnKey = null,
        string? columnType = null,
        int? rowIndex = null,
        string? rowKey = null,
        string? currency = null,
        string? unit = null,
        string? scope = null,
        string? detail = null)
        => new(kind, columnKey, columnType, rowIndex, rowKey, currency, unit, scope, detail ?? string.Empty);

    private static bool RowKeysEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool OwnedRowIdsEqual(IReadOnlyList<long> left, IReadOnlyList<long> right)
        => left.Count == right.Count && new HashSet<long>(left).SetEquals(right);

    private static object? ElementAtOrDefault(IReadOnlyList<object?> cells, int index)
        => index < cells.Count ? cells[index] : null;

    private static string RowKeyText(IReadOnlyList<string> keys)
        => string.Join("|", keys.Select(k => k ?? "<null>"));

    private static string OwnedRowIdsText(IReadOnlyList<long> ids)
        => "[" + string.Join(",", ids.OrderBy(id => id)) + "]";

    private static string FormatValue(object? value)
        => value is null ? "<null>" : InvariantText(value);

    /// <summary>null 感知 + 按声明类型的单元格值比较（null == null；非 null 按声明类型归一化比较）。</summary>
    private static bool CellValuesEqual(object? left, object? right, string columnType)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null)
            return false;

        return columnType switch
        {
            ReportConfigurationConstants.TypeNumber => DecimalOrStringEqual(left, right),
            ReportConfigurationConstants.TypeBoolean => BooleanOrStringEqual(left, right),
            ReportConfigurationConstants.TypeDate => DateTimeOrStringEqual(left, right),
            _ => string.Equals(InvariantText(left), InvariantText(right), StringComparison.Ordinal),
        };
    }

    private static bool DecimalOrStringEqual(object left, object right)
        => TryToDecimal(left, out var l) && TryToDecimal(right, out var r)
            ? l == r
            : string.Equals(InvariantText(left), InvariantText(right), StringComparison.Ordinal);

    private static bool BooleanOrStringEqual(object left, object right)
        => TryToBoolean(left, out var l) && TryToBoolean(right, out var r)
            ? l == r
            : string.Equals(InvariantText(left), InvariantText(right), StringComparison.Ordinal);

    private static bool DateTimeOrStringEqual(object left, object right)
        => TryToDateTime(left, out var l) && TryToDateTime(right, out var r)
            ? l == r
            : string.Equals(InvariantText(left), InvariantText(right), StringComparison.Ordinal);

    private static string InvariantText(object value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal d: result = d; return true;
            case double db: result = (decimal)db; return true;
            case float f: result = (decimal)f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case string str when decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0m;
                return false;
        }
    }

    private static bool TryToBoolean(object value, out bool result)
    {
        switch (value)
        {
            case bool b: result = b; return true;
            case string str when bool.TryParse(str, out var parsed): result = parsed; return true;
            default: result = false; return false;
        }
    }

    private static bool TryToDateTime(object value, out DateTime result)
    {
        switch (value)
        {
            case DateTime dt: result = dt; return true;
            case DateTimeOffset dto: result = dto.UtcDateTime; return true;
            case string str when DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed):
                result = parsed;
                return true;
            default: result = default; return false;
        }
    }

}
