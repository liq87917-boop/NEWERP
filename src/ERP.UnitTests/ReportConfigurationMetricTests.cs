using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-267 通用报表配置指标纯规则单元测试：指标描述符派生（粒度 / 单位 / 币种行为 / 允许函数 / 当前页覆盖）、
/// 已持久化选中聚合校验（先于源读取 fail closed），以及当前预览页通用执行（sum / count / avg / min / max 的
/// 已知 / 缺失 / 来源条数、币种分区、未知币种隔离、空 / 全 null、checked 溢出、稳定组序）。
/// <para>纯内存、无数据库依赖，不连接 SQL Server、不执行 SQL。</para>
/// </summary>
public class ReportConfigurationMetricTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ReportConfigurationFieldDto Field(
        string key, string type, bool aggregatable = false, bool hidden = false, string? unit = null)
        => new(key, key, type, unit, Filterable: false, aggregatable, hidden,
            ReportConfigurationRules.GetOperatorsForType(type));

    private static ReportConfigurationDatasetDto BuildDataset(List<ReportConfigurationFieldDto>? fields = null)
    {
        var f = fields ?? new List<ReportConfigurationFieldDto>
        {
            Field("amount", ReportConfigurationConstants.TypeNumber, aggregatable: true, unit: "原币金额"),
            Field("ratio", ReportConfigurationConstants.TypeNumber, aggregatable: true, unit: "%"),
            Field("status", ReportConfigurationConstants.TypeEnum),
            Field("customerId", ReportConfigurationConstants.TypeNumber),
            Field("orderDate", ReportConfigurationConstants.TypeDate),
        };

        return new ReportConfigurationDatasetDto(
            "test", "测试数据集", "测试行", "金额按原币呈现", "test-menu", "测试菜单", f,
            new[] { "none", "customer", "month" },
            new[] { "preview", "grouping", "date-range", "paging" },
            new[] { "custom-formula", "cross-dataset-join", "pivot", "all-match-total" },
            20, 200, "只读", "边界")
        {
            GroupCustomerFieldKey = "customerId",
            GroupMonthFieldKey = "orderDate",
        };
    }

    private static ReportConfigurationDefinition Definition(params ReportConfigurationAggregate[] aggregates)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "amount", "status" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { "none" },
            Aggregates = aggregates.ToList(),
            Capabilities = new List<string>(),
        };

    private static ReportConfigurationAggregate Aggregate(string function, string fieldKey)
        => new() { Function = function, FieldKey = fieldKey };

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            row[key] = value;
        return row;
    }

    // ==================== 1. 指标描述符派生 ====================

    [Fact]
    public void BuildMetrics_数值可聚合字段_允许全部函数与货币分区()
    {
        var metrics = ReportConfigurationMetricRules.BuildMetrics(
            new[] { Field("amount", ReportConfigurationConstants.TypeNumber, aggregatable: true, unit: "原币金额") },
            "测试行");

        var metric = Assert.Single(metrics);
        Assert.Equal("amount", metric.Key);
        Assert.Equal(ReportConfigurationConstants.TypeNumber, metric.Type);
        Assert.Equal("测试行", metric.Grain);
        Assert.Equal("原币金额", metric.Unit);
        Assert.Equal(ReportConfigurationMetricRules.CurrencyBehaviorPartition, metric.CurrencyBehavior);
        Assert.Equal(ReportConfigurationConstants.CoverageCurrentPage, metric.Coverage);
        Assert.Equal(new[]
        {
            ReportConfigurationConstants.AggregateCount,
            ReportConfigurationConstants.AggregateSum,
            ReportConfigurationConstants.AggregateAverage,
            ReportConfigurationConstants.AggregateMin,
            ReportConfigurationConstants.AggregateMax,
        }, metric.AllowedFunctions);
    }

    [Fact]
    public void BuildMetrics_非聚合字段仅允许count_隐藏字段不暴露()
    {
        var metrics = ReportConfigurationMetricRules.BuildMetrics(new[]
        {
            Field("status", ReportConfigurationConstants.TypeEnum),
            Field("customerId", ReportConfigurationConstants.TypeNumber),
            Field("hiddenField", ReportConfigurationConstants.TypeNumber, aggregatable: true, hidden: true),
        }, "测试行");

        Assert.Equal(2, metrics.Count);
        Assert.All(metrics, m => Assert.Equal(new[] { ReportConfigurationConstants.AggregateCount }, m.AllowedFunctions));
        Assert.DoesNotContain(metrics, m => m.Key == "hiddenField");
    }

    // ==================== 2. 校验（先于源读取） ====================

    [Fact]
    public void ValidateAggregates_count允许非聚合字段_拒绝ID求和()
    {
        var dataset = BuildDataset();

        ReportConfigurationMetricRules.ValidateAggregates(
            Definition(Aggregate(ReportConfigurationConstants.AggregateCount, "status")), dataset);

        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationMetricRules.ValidateAggregates(
            Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "customerId")), dataset));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateAggregates_超过四个_拒绝()
    {
        var aggregates = new[]
        {
            Aggregate(ReportConfigurationConstants.AggregateCount, "amount"),
            Aggregate(ReportConfigurationConstants.AggregateSum, "amount"),
            Aggregate(ReportConfigurationConstants.AggregateAverage, "amount"),
            Aggregate(ReportConfigurationConstants.AggregateMin, "amount"),
            Aggregate(ReportConfigurationConstants.AggregateMax, "amount"),
        };

        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationMetricRules.ValidateAggregates(
            Definition(aggregates), BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateAggregates_未知函数_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationMetricRules.ValidateAggregates(
            Definition(Aggregate("median", "amount")), BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    // ==================== 3. 执行（当前页） ====================

    [Fact]
    public void Compute_sum_按币种分区_未知币种隔离并附原因()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));
        var rows = new[]
        {
            Row(("amount", 100m), ("currency", "USD")),
            Row(("amount", 50m), ("currency", "USD")),
            Row(("amount", 200m), ("currency", "CNY")),
            Row(("amount", 300m), ("currency", "")),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset, rows, "none"));

        var usd = Assert.Single(result.Cells, c => c.Currency == "USD");
        Assert.Equal(150m, usd.Value);
        Assert.Equal(2, usd.KnownCount);
        Assert.Equal(0, usd.MissingCount);
        Assert.Null(usd.Reason);

        var cny = Assert.Single(result.Cells, c => c.Currency == "CNY");
        Assert.Equal(200m, cny.Value);

        var unknown = Assert.Single(result.Cells, c => c.Currency == ReportConfigurationMetricRules.UnknownCurrencyText);
        Assert.Equal(300m, unknown.Value);
        Assert.Equal(ReportConfigurationMetricRules.UnknownCurrencyReason, unknown.Reason);
    }

    [Fact]
    public void Compute_count_已知缺失来源条数_且不按币种分区()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateCount, "amount"));
        var rows = new[]
        {
            Row(("amount", 10m)),
            Row(("amount", null)),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset, rows, "none"));
        var cell = Assert.Single(result.Cells);

        Assert.Null(cell.Currency);
        Assert.Equal(1m, cell.Value);
        Assert.Equal(1, cell.KnownCount);
        Assert.Equal(1, cell.MissingCount);
        Assert.Equal(2, cell.SourceCount);
    }

    [Fact]
    public void Compute_avg_已知值分母_全null返回null()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateAverage, "amount"));

        var known = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset,
            new[] { Row(("amount", 10m)), Row(("amount", 20m)), Row(("amount", null)) }, "none"));
        var cell = Assert.Single(known.Cells);
        Assert.Equal(15m, cell.Value);
        Assert.Equal(2, cell.KnownCount);
        Assert.Equal(1, cell.MissingCount);

        var allNull = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset,
            new[] { Row(("amount", null)) }, "none"));
        Assert.Null(Assert.Single(allNull.Cells).Value);
    }

    [Fact]
    public void Compute_sum_溢出返回null与显式原因()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));
        var rows = new[]
        {
            Row(("amount", decimal.MaxValue), ("currency", "USD")),
            Row(("amount", decimal.MaxValue), ("currency", "USD")),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset, rows, "none"));
        var cell = Assert.Single(result.Cells);
        Assert.Null(cell.Value);
        Assert.Equal(ReportConfigurationMetricRules.OverflowReason, cell.Reason);
    }

    [Fact]
    public void Compute_空页_保留指标元数据且无单元格()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset,
            Array.Empty<Dictionary<string, object?>>(), "none"));
        Assert.Equal("amount", result.Key);
        Assert.Equal(ReportConfigurationConstants.AggregateSum, result.Function);
        Assert.Empty(result.Cells);
    }



    [Fact]
    public void Compute_按客户分组_组序稳定()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));
        var rows = new[]
        {
            Row(("amount", 1m), ("currency", "USD"), ("customerId", 2L)),
            Row(("amount", 2m), ("currency", "USD"), ("customerId", 1L)),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset, rows, "customer"));
        Assert.Equal(2, result.Cells.Count);
        Assert.Equal("客户 #1", result.Cells[0].GroupLabel);
        Assert.Equal("客户 #2", result.Cells[1].GroupLabel);
    }

    [Fact]
    public void Compute_按月份分组_组序稳定()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));
        var rows = new[]
        {
            Row(("amount", 1m), ("currency", "USD"), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 2m), ("currency", "USD"), ("orderDate", new DateTime(2026, 8, 1))),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(definition, dataset, rows, "month"));
        Assert.Equal("2026年8月", result.Cells[0].GroupLabel);
        Assert.Equal("2026年9月", result.Cells[1].GroupLabel);
    }

    [Fact]
    public void Compute_min_max_只统计已知值()
    {
        var dataset = BuildDataset();
        var definition = Definition(
            Aggregate(ReportConfigurationConstants.AggregateMin, "amount"),
            Aggregate(ReportConfigurationConstants.AggregateMax, "amount"));
        var rows = new[]
        {
            Row(("amount", 5m), ("currency", "USD")),
            Row(("amount", 30m), ("currency", "USD")),
            Row(("amount", null), ("currency", "USD")),
        };

        var results = ReportConfigurationMetricRules.Compute(definition, dataset, rows, "none");
        var min = Assert.Single(results, r => r.Function == ReportConfigurationConstants.AggregateMin);
        var max = Assert.Single(results, r => r.Function == ReportConfigurationConstants.AggregateMax);
        Assert.Equal(5m, Assert.Single(min.Cells).Value);
        Assert.Equal(30m, Assert.Single(max.Cells).Value);
    }

    [Fact]
    public void Compute_复合分组_按元组与币种分区()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));
        var rows = new[]
        {
            Row(("amount", 1m), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))),
            Row(("amount", 2m), ("currency", "USD"), ("customerId", 1L), ("orderDate", new DateTime(2026, 10, 1))),
            Row(("amount", 3m), ("currency", "USD"), ("customerId", 2L), ("orderDate", new DateTime(2026, 9, 1))),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(
            definition, dataset, rows, new[] { "customer", "month" }));
        Assert.Equal(3, result.Cells.Count);
        Assert.All(result.Cells, c => Assert.Equal(2, c.Dimensions.Count));
        Assert.Equal("customer", result.Cells[0].Dimensions[0].Key);
        Assert.Equal("month", result.Cells[0].Dimensions[1].Key);
        Assert.Equal("客户 #1 · 2026年9月", result.Cells[0].GroupLabel);
    }

    [Fact]
    public void Compute_复合分组_缺失维度形成显式未知桶()
    {
        var dataset = BuildDataset();
        var definition = Definition(Aggregate(ReportConfigurationConstants.AggregateSum, "amount"));
        var rows = new[]
        {
            Row(("amount", 5m), ("currency", "USD")),
        };

        var result = Assert.Single(ReportConfigurationMetricRules.Compute(
            definition, dataset, rows, new[] { "customer", "month" }));
        var cell = Assert.Single(result.Cells);
        Assert.All(cell.Dimensions, d => Assert.True(d.IsUnknown));
        Assert.Contains("未知客户", cell.GroupLabel);
        Assert.Contains("未知月份", cell.GroupLabel);
    }
}
