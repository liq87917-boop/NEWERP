using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-329 迁移 parity 四维比对的单元测试：覆盖数据粒度（列键 / 类型 / 行键 / null 感知单元格值）、
/// 币种 / 单位分区（跨币种 / 单位合并拒绝）与权限（行归属 / 数据范围）维度，以及输出语义透传。
/// </summary>
public class ReportMigrationParityComparatorTests
{
    private const string Number = ReportConfigurationConstants.TypeNumber;
    private const string Text = ReportConfigurationConstants.TypeText;
    private const string Date = ReportConfigurationConstants.TypeDate;
    private const string Boolean = ReportConfigurationConstants.TypeBoolean;

    private readonly ReportMigrationParityComparator _comparator = new();

    // ==================== 脚手架 ====================

    private static ReportMigrationParityColumnDto Col(string key, string type, string? currency = null, string? unit = null)
        => new(key, type, currency, unit);

    private static ReportMigrationParityRowDto Row(string rowKey, object?[] cells, string? currency = null, string? unit = null)
        => new(new[] { rowKey }, currency, unit, cells);

    private static ReportMigrationParitySnapshotDto Snapshot(
        ReportMigrationParityColumnDto[] columns,
        ReportMigrationParityRowDto[] rows,
        long[]? ownedRowIds = null,
        string scope = "scope-a")
        => new(columns, rows, new ReportMigrationParityPermissionsDto(
            ownedRowIds ?? Array.Empty<long>(), scope));

    // ==================== 数据粒度：列 ====================

    [Fact]
    public void Compare_IdenticalSnapshots_CompleteEvidenceAndNoMismatches()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number), Col("name", Text), Col("amount", Number, "CNY", "件") },
            new[]
            {
                Row("1", new object?[] { 1L, "A", 10.5m }, "CNY", "件"),
                Row("2", new object?[] { 2L, "B", null }, "CNY", "件"),
            },
            new long[] { 1, 2 });

        var generic = Snapshot(
            new[] { Col("id", Number), Col("name", Text), Col("amount", Number, "CNY", "件") },
            new[]
            {
                Row("1", new object?[] { 1L, "A", 10.5m }, "CNY", "件"),
                Row("2", new object?[] { 2L, "B", null }, "CNY", "件"),
            },
            new long[] { 2, 1 });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.True(result.Evidence.Complete);
        Assert.True(result.Evidence.DataGrainMatched);
        Assert.True(result.Evidence.CurrencyUnitMatched);
        Assert.True(result.Evidence.PermissionsMatched);
        Assert.True(result.Evidence.OutputSemanticsMatched);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void Compare_ColumnCountDivergence_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) });

        var generic = Snapshot(
            new[] { Col("id", Number), Col("name", Text) },
            new[] { Row("1", new object?[] { 1L, "A" }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Column);
    }

    [Fact]
    public void Compare_ColumnKeyDivergence_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) });

        var generic = Snapshot(
            new[] { Col("orderId", Number) },
            new[] { Row("1", new object?[] { 1L }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m =>
            m.Kind == ReportMigrationParityMismatchKind.Column && m.ColumnKey == "id");
    }

    [Fact]
    public void Compare_ColumnTypeDivergence_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { 1m }) });

        var generic = Snapshot(
            new[] { Col("amount", Text) },
            new[] { Row("1", new object?[] { "1" }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m =>
            m.Kind == ReportMigrationParityMismatchKind.Column && m.ColumnType == Number);
    }

    [Fact]
    public void Compare_ColumnOrderDivergence_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number), Col("name", Text) },
            new[] { Row("1", new object?[] { 1L, "A" }) });

        var generic = Snapshot(
            new[] { Col("name", Text), Col("id", Number) },
            new[] { Row("1", new object?[] { "A", 1L }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Column);
    }

    // ==================== 数据粒度：行键 ====================

    [Fact]
    public void Compare_RowKeyMismatch_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) });

        var generic = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("2", new object?[] { 1L }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m =>
            m.Kind == ReportMigrationParityMismatchKind.Row && m.RowIndex == 0);
    }

    [Fact]
    public void Compare_CompositeRowKeyMismatch_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("orderId", Number), Col("lineNo", Number) },
            new[] { new ReportMigrationParityRowDto(new[] { "10", "1" }, null, null, new object?[] { 10L, 1L }) });

        var generic = Snapshot(
            new[] { Col("orderId", Number), Col("lineNo", Number) },
            new[] { new ReportMigrationParityRowDto(new[] { "10", "2" }, null, null, new object?[] { 10L, 1L }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Row);
    }

    // ==================== 数据粒度：null 感知单元格值 ====================

    [Fact]
    public void Compare_NullCellsOnBothSides_DataGrainMatches()
    {
        var legacy = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { null }) });

        var generic = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { null }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.True(result.Evidence.DataGrainMatched);
        Assert.DoesNotContain(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Cell);
    }

    [Fact]
    public void Compare_NullVsValueCell_DataGrainFails()
    {
        var legacy = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { null }) });

        var generic = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { 5m }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m =>
            m.Kind == ReportMigrationParityMismatchKind.Cell && m.ColumnKey == "amount");
    }

    [Fact]
    public void Compare_NumericCells_ComparedByDeclaredType_Equal()
    {
        var legacy = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { 1.10m }) });

        var generic = Snapshot(
            new[] { Col("amount", Number) },
            new[] { Row("1", new object?[] { 1.1m }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.True(result.Evidence.DataGrainMatched);
    }

    [Fact]
    public void Compare_TextCells_OrdinalComparison_MismatchOnCase()
    {
        var legacy = Snapshot(
            new[] { Col("name", Text) },
            new[] { Row("1", new object?[] { "Alpha" }) });

        var generic = Snapshot(
            new[] { Col("name", Text) },
            new[] { Row("1", new object?[] { "alpha" }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.DataGrainMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Cell);
    }

    [Fact]
    public void Compare_DateCells_ComparedByDeclaredType_Equal()
    {
        var value = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

        var legacy = Snapshot(
            new[] { Col("orderDate", Date) },
            new[] { Row("1", new object?[] { value }) });

        var generic = Snapshot(
            new[] { Col("orderDate", Date) },
            new[] { Row("1", new object?[] { new DateTimeOffset(value) }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.True(result.Evidence.DataGrainMatched);
    }

    [Fact]
    public void Compare_BooleanCells_ComparedByDeclaredType_Equal()
    {
        var legacy = Snapshot(
            new[] { Col("isActive", Boolean) },
            new[] { Row("1", new object?[] { true }) });

        var generic = Snapshot(
            new[] { Col("isActive", Boolean) },
            new[] { Row("1", new object?[] { "true" }) });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.True(result.Evidence.DataGrainMatched);
    }

    // ==================== 币种 / 单位分区（跨币种 / 单位合并拒绝） ====================

    [Fact]
    public void Compare_CurrencyPartitionDivergence_CurrencyUnitFails()
    {
        var legacy = Snapshot(
            new[] { Col("amount", Number, "CNY") },
            new[] { Row("1", new object?[] { 1m }, currency: null) });

        var generic = Snapshot(
            new[] { Col("amount", Number, "CNY") },
            new[] { Row("1", new object?[] { 1m }, currency: "USD") });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.CurrencyUnitMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Currency);
    }

    [Fact]
    public void Compare_UnitPartitionDivergence_CurrencyUnitFails()
    {
        var legacy = Snapshot(
            new[] { Col("qty", Number, unit: "件") },
            new[] { Row("1", new object?[] { 1m }, unit: null) });

        var generic = Snapshot(
            new[] { Col("qty", Number, unit: "件") },
            new[] { Row("1", new object?[] { 1m }, unit: "件") });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.CurrencyUnitMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Unit);
    }

    // ==================== 权限（行归属 / 数据范围） ====================

    [Fact]
    public void Compare_DataScopeFingerprintDivergence_PermissionsFail()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) },
            scope: "scope-a");

        var generic = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) },
            scope: "scope-b");

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.PermissionsMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Scope);
    }

    [Fact]
    public void Compare_OwnedRowIdsDivergence_PermissionsFail()
    {
        var legacy = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) },
            ownedRowIds: new long[] { 1 });

        var generic = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) },
            ownedRowIds: new long[] { 2 });

        var result = _comparator.Compare(legacy, generic, outputSemanticsMatched: true);

        Assert.False(result.Evidence.PermissionsMatched);
        Assert.Contains(result.Mismatches, m => m.Kind == ReportMigrationParityMismatchKind.Scope);
    }

    // ==================== 输出语义透传 ====================

    [Fact]
    public void Compare_OutputSemanticsNotMatched_EvidenceNotComplete()
    {
        var snapshot = Snapshot(
            new[] { Col("id", Number) },
            new[] { Row("1", new object?[] { 1L }) });

        var result = _comparator.Compare(snapshot, snapshot, outputSemanticsMatched: false);

        Assert.True(result.Evidence.DataGrainMatched);
        Assert.True(result.Evidence.CurrencyUnitMatched);
        Assert.True(result.Evidence.PermissionsMatched);
        Assert.False(result.Evidence.OutputSemanticsMatched);
        Assert.False(result.Evidence.Complete);
    }
}
