using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-271 复合分组纯规则单元测试：两个有序基础维度校验（拒绝重复 / 未知 / none 混用 / 超限 / 未授权）、
/// 类型化复合键无碰撞、缺失维度显式未知桶、稳定客户 / 月份顺序、目录维度元数据解析与旧单分组兼容。
/// <para>纯内存、无数据库依赖，不连接 SQL Server、不执行 SQL。</para>
/// </summary>
public class ReportConfigurationGroupingTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ReportConfigurationFieldDto Field(string key, string type, bool hidden = false)
        => new(key, key, type, null, Filterable: false, Aggregatable: false, hidden,
            ReportConfigurationRules.GetOperatorsForType(type));

    private static ReportConfigurationDatasetDto BuildDataset(bool explicitDimensions = true)
    {
        var fields = new List<ReportConfigurationFieldDto>
        {
            Field("orderNo", ReportConfigurationConstants.TypeText),
            Field("customerId", ReportConfigurationConstants.TypeNumber),
            Field("orderDate", ReportConfigurationConstants.TypeDate),
            Field("totalAmount", ReportConfigurationConstants.TypeNumber),
        };

        var dataset = new ReportConfigurationDatasetDto(
            "test", "测试数据集", "测试行", "原币", "test-menu", "测试菜单", fields,
            new[] { "none", "customer", "month" },
            new[] { "preview", "grouping", "date-range", "paging" },
            new[] { "custom-formula", "cross-dataset-join", "pivot", "all-match-total" },
            20, 200, "只读", "边界")
        {
            GroupCustomerFieldKey = "customerId",
            GroupMonthFieldKey = "orderDate",
            GroupingDimensions = explicitDimensions
                ? new List<ReportConfigurationGroupingDimensionDto>
                {
                    new(ReportConfigurationConstants.GroupCustomer, "按客户分组", "customerId",
                        ReportConfigurationConstants.TypeNumber, ReportConfigurationConstants.GroupingSemanticsIdentity),
                    new(ReportConfigurationConstants.GroupMonth, "按月份分组", "orderDate",
                        ReportConfigurationConstants.TypeDate, ReportConfigurationConstants.GroupingSemanticsCalendarMonth),
                }
                : new List<ReportConfigurationGroupingDimensionDto>(),
        };

        return dataset;
    }

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            row[key] = value;
        return row;
    }

    // ==================== 1. 维度校验 ====================

    [Fact]
    public void ValidateGrouping_两个有序基础维度_通过()
    {
        var keys = ReportConfigurationGroupingRules.ValidateGrouping(
            new[] { "customer", "month" }, BuildDataset());
        Assert.Equal(new[] { "customer", "month" }, keys);
    }

    [Fact]
    public void ValidateGrouping_反向顺序_保持提交顺序()
    {
        var keys = ReportConfigurationGroupingRules.ValidateGrouping(
            new[] { "month", "customer" }, BuildDataset());
        Assert.Equal(new[] { "month", "customer" }, keys);
    }

    [Fact]
    public void ValidateGrouping_重复维度_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "customer", "customer" }, BuildDataset()));
    }

    [Fact]
    public void ValidateGrouping_未知维度_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "quarter" }, BuildDataset()));
    }

    [Fact]
    public void ValidateGrouping_none与维度并存_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "none", "customer" }, BuildDataset()));
    }

    [Fact]
    public void ValidateGrouping_超过两个维度_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "customer", "month", "customer" }, BuildDataset()));
    }

    [Fact]
    public void ValidateGrouping_数据集未授权维度_拒绝()
    {
        var dataset = BuildDataset() with { GroupingKeys = new[] { "none", "customer" } };
        Assert.Throws<BusinessException>(() =>
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "month" }, dataset));
    }

    [Fact]
    public void ValidateGrouping_底层字段隐藏_拒绝()
    {
        var baseDataset = BuildDataset(explicitDimensions: false);
        var fields = baseDataset.Fields.ToList();
        fields.RemoveAll(f => f.Key == "customerId");
        fields.Add(Field("customerId", ReportConfigurationConstants.TypeNumber, hidden: true));
        var dataset = baseDataset with
        {
            Fields = fields,
            GroupingDimensions = new List<ReportConfigurationGroupingDimensionDto>
            {
                new(ReportConfigurationConstants.GroupCustomer, "按客户分组", "customerId",
                    ReportConfigurationConstants.TypeNumber, ReportConfigurationConstants.GroupingSemanticsIdentity),
            },
        };

        Assert.Throws<BusinessException>(() =>
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "customer" }, dataset));
    }

    [Fact]
    public void ValidateGrouping_旧单分组与默认不分组_兼容()
    {
        Assert.Equal(new[] { "customer" },
            ReportConfigurationGroupingRules.ValidateGrouping(new[] { "customer" }, BuildDataset()));
        Assert.Empty(ReportConfigurationGroupingRules.ValidateGrouping(new[] { "none" }, BuildDataset()));
        Assert.Empty(ReportConfigurationGroupingRules.ValidateGrouping(Array.Empty<string>(), BuildDataset()));
    }

    // ==================== 2. 类型化复合键 / 桶 ====================

    [Fact]
    public void BuildCompositeKey_长度前缀_避免字符串拼接碰撞()
    {
        Assert.NotEqual(
            ReportConfigurationGroupingRules.BuildCompositeKey(new string?[] { "a", "bc" }),
            ReportConfigurationGroupingRules.BuildCompositeKey(new string?[] { "ab", "c" }));
    }

    [Fact]
    public void BuildGroupBucket_缺失维度_显式未知桶()
    {
        var dataset = BuildDataset();
        var dimensions = ReportConfigurationGroupingRules.ResolveDimensions(dataset);
        var bucket = ReportConfigurationGroupingRules.BuildGroupBucket(
            Row(("orderNo", "SO-1")), new[] { "customer", "month" }, dimensions);

        Assert.Equal(2, bucket.Dimensions.Count);
        Assert.All(bucket.Dimensions, d => Assert.True(d.IsUnknown));
        Assert.Equal("未知客户", bucket.Dimensions[0].Label);
        Assert.Equal("未知月份", bucket.Dimensions[1].Label);
    }

    [Fact]
    public void BuildGroupBucket_稳定客户月份顺序()
    {
        var dataset = BuildDataset();
        var dimensions = ReportConfigurationGroupingRules.ResolveDimensions(dataset);

        var customerMonth = ReportConfigurationGroupingRules.BuildGroupBucket(
            Row(("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))), new[] { "customer", "month" }, dimensions);
        var monthCustomer = ReportConfigurationGroupingRules.BuildGroupBucket(
            Row(("customerId", 1L), ("orderDate", new DateTime(2026, 9, 1))), new[] { "month", "customer" }, dimensions);

        Assert.Equal("客户 #1 · 2026年9月", customerMonth.Label);
        Assert.Equal("2026年9月 · 客户 #1", monthCustomer.Label);
        Assert.NotEqual(customerMonth.Key, monthCustomer.Key);
    }

    [Fact]
    public void BuildGroupBucket_组序确定性_客户后月份升序()
    {
        var dataset = BuildDataset();
        var dimensions = ReportConfigurationGroupingRules.ResolveDimensions(dataset);

        var late = ReportConfigurationGroupingRules.BuildGroupBucket(
            Row(("customerId", 2L), ("orderDate", new DateTime(2026, 9, 1))), new[] { "customer", "month" }, dimensions);
        var early = ReportConfigurationGroupingRules.BuildGroupBucket(
            Row(("customerId", 1L), ("orderDate", new DateTime(2026, 10, 1))), new[] { "customer", "month" }, dimensions);

        Assert.True(string.Compare(early.SortKey, late.SortKey, StringComparison.Ordinal) < 0);
    }
}

