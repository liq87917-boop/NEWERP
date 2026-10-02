using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-272 有界透视纯规则单元测试：校验（schema 版本 / 授权不同轴 / 指标数量 / 分组互斥），
/// 以及当前预览页执行（多指标、空 / null / 负数、混合币种分区、显式未知桶、确定性轴序、超限前置拒绝）。
/// <para>纯内存、无数据库依赖，不连接 SQL Server、不执行 SQL。</para>
/// </summary>
public class ReportConfigurationPivotTests
{
    private static ReportConfigurationFieldDto Field(
        string key, string type, bool aggregatable = false, string? unit = null)
        => new(key, key, type, unit, Filterable: false, aggregatable, Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(type));

    private static ReportConfigurationDatasetDto BuildDataset()
    {
        var fields = new List<ReportConfigurationFieldDto>
        {
            Field("amount", ReportConfigurationConstants.TypeNumber, aggregatable: true, unit: "原币金额"),
            Field("qty", ReportConfigurationConstants.TypeNumber, aggregatable: true, unit: "%"),
            Field("customerId", ReportConfigurationConstants.TypeNumber),
            Field("orderDate", ReportConfigurationConstants.TypeDate),
        };

        return new ReportConfigurationDatasetDto(
            "test", "测试数据集", "测试行", "金额按原币呈现", "test-menu", "测试菜单", fields,
            new[] { "none", "customer", "month" },
            new[] { "preview", "grouping", "date-range", "paging" },
            new[] { "custom-formula", "cross-dataset-join", "pivot", "all-match-total" },
            20, 200, "只读", "边界")
        {
            GroupCustomerFieldKey = "customerId",
            GroupMonthFieldKey = "orderDate",
        };
    }

    private static ReportConfigurationPivotDefinition Pivot(string row, string column)
        => new() { SchemaVersion = 1, RowDimension = row, ColumnDimension = column };

    private static ReportConfigurationAggregate Aggregate(string function, string fieldKey)
        => new() { Function = function, FieldKey = fieldKey };

    private static ReportConfigurationDefinition Definition(
        string row, string column, params ReportConfigurationAggregate[] aggregates)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "customerId", "orderDate", "amount" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { "none" },
            Aggregates = aggregates.ToList(),
            Capabilities = new List<string>(),
            Pivot = Pivot(row, column),
        };

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            row[key] = value;
        return row;
    }

    [Fact]
    public void Validate_无透视定义_不校验直接返回()
    {
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        definition.Pivot = null;
        ReportConfigurationPivotRules.Validate(definition, BuildDataset());
    }

    [Fact]
    public void Validate_相同行列维度_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPivotRules.Validate(Definition("customer", "customer", Aggregate("sum", "amount")), BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_缺列维度_拒绝()
    {
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        definition.Pivot!.ColumnDimension = string.Empty;
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPivotRules.Validate(definition, BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_与分组共存_拒绝()
    {
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        definition.Grouping = new List<string> { "customer" };
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPivotRules.Validate(definition, BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_无指标_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPivotRules.Validate(Definition("customer", "month"), BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_超过四个指标_拒绝()
    {
        var aggregates = new[]
        {
            Aggregate("sum", "amount"),
            Aggregate("count", "amount"),
            Aggregate("avg", "amount"),
            Aggregate("min", "amount"),
            Aggregate("max", "amount"),
        };
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPivotRules.Validate(Definition("customer", "month", aggregates), BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_未知维度_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPivotRules.Validate(Definition("customer", "quarter", Aggregate("sum", "amount")), BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Build_确定性轴序与求和()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var rows = new[]
        {
            Row(("amount", 1m), ("currency", "USD"), ("customerId", 2L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 2m), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 4m), ("currency", "USD"), ("customerId", 2L), ("orderDate", new DateTime(2026, 10, 1))),
        };

        var result = ReportConfigurationPivotRules.Build(definition, dataset, rows);

        Assert.Equal("customer", result.RowDimension);
        Assert.Equal("month", result.ColumnDimension);
        Assert.Equal(3, result.SourceRowCount);
        Assert.Equal(ReportConfigurationConstants.CoverageCurrentPage, result.Coverage);

        Assert.Equal(2, result.RowAxis.Count);
        Assert.Equal("客户 #1", result.RowAxis[0].Label);
        Assert.Equal("客户 #2", result.RowAxis[1].Label);

        Assert.Equal(2, result.ColumnAxis.Count);
        Assert.Equal("2026年9月", result.ColumnAxis[0].Label);
        Assert.Equal("2026年10月", result.ColumnAxis[1].Label);

        var metric = Assert.Single(result.Metrics);
        Assert.Equal(3, metric.Cells.Count);
        var cell = Assert.Single(metric.Cells, c => c.RowIndex == 1 && c.ColumnIndex == 1);
        Assert.Equal(4m, cell.Value);
        Assert.Equal(1, cell.KnownCount);
    }

    [Fact]
    public void Build_多指标_稀疏空交叉点无单元格()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month",
            Aggregate("sum", "amount"), Aggregate("avg", "qty"));
        var rows = new[]
        {
            Row(("amount", 10m), ("qty", 2m), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
        };

        var result = ReportConfigurationPivotRules.Build(definition, dataset, rows);

        Assert.Equal(2, result.Metrics.Count);
        var avg = result.Metrics[1];
        Assert.Equal(2m, Assert.Single(avg.Cells).Value);
    }

    [Fact]
    public void Build_空页_轴为空且指标保留元数据()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var result = ReportConfigurationPivotRules.Build(definition, dataset, Array.Empty<Dictionary<string, object?>>());

        Assert.Empty(result.RowAxis);
        Assert.Empty(result.ColumnAxis);
        var metric = Assert.Single(result.Metrics);
        Assert.Empty(metric.Cells);
        Assert.Equal(0, metric.KnownCount);
    }

    [Fact]
    public void Build_空与null与负数_保留null语义且区分真实零()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var rows = new[]
        {
            Row(("amount", null), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", -5m), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 0m), ("currency", "USD"), ("customerId", 2L), ("orderDate", new DateTime(2026, 9, 1))),
        };

        var result = ReportConfigurationPivotRules.Build(definition, dataset, rows);
        var metric = Assert.Single(result.Metrics);

        var customer1 = Assert.Single(metric.Cells, c => c.RowIndex == 0);
        Assert.Equal(-5m, customer1.Value);
        Assert.Equal(1, customer1.KnownCount);
        Assert.Equal(1, customer1.MissingCount);

        var customer2 = Assert.Single(metric.Cells, c => c.RowIndex == 1);
        Assert.Equal(0m, customer2.Value);
        Assert.Equal(1, customer2.KnownCount);
    }

    [Fact]
    public void Build_混合币种_按币种分区且未知币种隔离()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var rows = new[]
        {
            Row(("amount", 1m), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 2m), ("currency", "CNY"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 3m), ("currency", ""), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
        };

        var result = ReportConfigurationPivotRules.Build(definition, dataset, rows);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal(3, metric.Cells.Count);

        Assert.Equal(1m, Assert.Single(metric.Cells, c => c.Currency == "USD").Value);
        Assert.Equal(2m, Assert.Single(metric.Cells, c => c.Currency == "CNY").Value);
        var unknown = Assert.Single(metric.Cells, c => c.Currency == ReportConfigurationMetricRules.UnknownCurrencyText);
        Assert.Equal(3m, unknown.Value);
        Assert.Equal(ReportConfigurationMetricRules.UnknownCurrencyReason, unknown.Reason);
    }

    [Fact]
    public void Build_缺失维度_显式未知桶且不并入已知桶()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var rows = new[] { Row(("amount", 5m), ("currency", "USD")) };

        var result = ReportConfigurationPivotRules.Build(definition, dataset, rows);
        Assert.Equal("未知客户", Assert.Single(result.RowAxis).Label);
        Assert.Equal("未知月份", Assert.Single(result.ColumnAxis).Label);
        Assert.True(result.RowAxis[0].IsUnknown);
        Assert.True(result.ColumnAxis[0].IsUnknown);
    }

    [Fact]
    public void Build_列数超限_渲染前拒绝()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var rows = Enumerable.Range(0, ReportConfigurationPivotRules.MaxPivotColumns + 1)
            .Select(i => Row(("amount", 1m), ("currency", "USD"), ("customerId", 1L),
                ("orderDate", new DateTime(2026, 1, 1).AddMonths(i))))
            .ToList();

        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationPivotRules.Build(definition, dataset, rows));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
        Assert.Contains("透视列数", ex.Message);
    }

    [Fact]
    public void Build_行数超限_渲染前拒绝()
    {
        var dataset = BuildDataset();
        var definition = Definition("customer", "month", Aggregate("sum", "amount"));
        var rows = Enumerable.Range(0, ReportConfigurationPivotRules.MaxPivotRows + 1)
            .Select(i => Row(("amount", 1m), ("currency", "USD"), ("customerId", (long)i),
                ("orderDate", new DateTime(2026, 9, 1))))
            .ToList();

        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationPivotRules.Build(definition, dataset, rows));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
        Assert.Contains("透视行数", ex.Message);
    }

    [Fact]
    public void FormatCellText_多币种分区_规范文本且不跨币种相加()
    {
        var partitions = new List<ReportConfigurationPivotCellDto>
        {
            new() { Currency = "USD", Value = 1.5m },
            new() { Currency = "CNY", Value = 2m },
        };

        Assert.Equal("CNY 2 / USD 1.5", ReportConfigurationPivotRules.FormatCellText(partitions));
        Assert.Equal(string.Empty, ReportConfigurationPivotRules.FormatCellText(null));
        Assert.Equal(string.Empty, ReportConfigurationPivotRules.FormatCellText(new List<ReportConfigurationPivotCellDto>()));
    }
}
