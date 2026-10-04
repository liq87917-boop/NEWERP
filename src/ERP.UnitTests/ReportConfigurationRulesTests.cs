using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-259 通用报表配置纯校验器单元测试：覆盖 schema 版本 / 数据集键、有序字段 / 重复与隐藏字段、
/// 类型化筛选与有限操作符 / 非法类型 / SQL 脚本载荷、分组 / 聚合 / 能力与展示边界、64KiB 序列化上限。
/// <para>纯内存、无数据库依赖，不连接 SQL Server、不执行 SQL。</para>
/// </summary>
public class ReportConfigurationRulesTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly IReadOnlyList<string> DefaultGrouping = new[] { "none", "customer", "month" };
    private static readonly IReadOnlyList<string> DefaultCapabilities = new[] { "preview", "grouping", "date-range", "paging" };
    private static readonly IReadOnlyList<string> DefaultUnsupported = new[]
        { "custom-formula", "cross-dataset-join", "pivot", "all-match-total" };

    private static ReportConfigurationFieldDto Field(
        string key, string type, bool filterable = false, bool aggregatable = false, bool hidden = false, bool sortable = false)
        => new(key, key, type, null, filterable, aggregatable, hidden,
            ReportConfigurationRules.GetOperatorsForType(type))
        {
            Sortable = sortable,
        };

    private static ReportConfigurationDatasetDto BuildDataset(
        IReadOnlyList<string>? groupingKeys = null,
        IReadOnlyList<string>? supportedCapabilities = null,
        List<ReportConfigurationFieldDto>? fields = null)
    {
        var f = fields ?? new List<ReportConfigurationFieldDto>
        {
            Field("customerName", ReportConfigurationConstants.TypeText, filterable: true),
            Field("amount", ReportConfigurationConstants.TypeNumber, filterable: true, aggregatable: true),
            Field("orderDate", ReportConfigurationConstants.TypeDate, filterable: true),
            Field("status", ReportConfigurationConstants.TypeEnum, filterable: true),
            Field("isActive", ReportConfigurationConstants.TypeBoolean, filterable: true),
            Field("aggregatableDate", ReportConfigurationConstants.TypeDate, aggregatable: true),
            Field("nonFilterable", ReportConfigurationConstants.TypeText, filterable: false),
            Field("nonAggregatableId", ReportConfigurationConstants.TypeNumber, filterable: false, aggregatable: false),
            Field("hiddenField", ReportConfigurationConstants.TypeText, hidden: true),
        };

        return new ReportConfigurationDatasetDto(
            "test", "测试数据集", "测试行", "原币",
            "test-menu", "测试菜单", f,
            groupingKeys ?? DefaultGrouping,
            supportedCapabilities ?? DefaultCapabilities,
            DefaultUnsupported,
            20, 200, "只读", "边界");
    }

    private static ReportConfigurationDefinition ValidDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new() { "customerName", "amount", "orderDate" },
            Filters = new()
            {
                new ReportConfigurationFilter
                {
                    FieldKey = "amount",
                    Operator = ReportConfigurationConstants.OperatorGte,
                    Value = 100m,
                },
            },
            Grouping = new() { "customer" },
            Aggregates = new()
            {
                new ReportConfigurationAggregate
                {
                    Function = ReportConfigurationConstants.AggregateSum,
                    FieldKey = "amount",
                },
            },
            Capabilities = new() { "preview", "grouping" },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
        };

    private static void AssertInvalid(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto? dataset = null)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationRules.Validate(definition, dataset ?? BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 1. 合法定义通过 ====================

    [Fact]
    public void Validate_合法定义_通过()
    {
        ReportConfigurationRules.Validate(ValidDefinition(), BuildDataset());
    }

    // ==================== 2. schema 与数据集键 ====================

    [Fact]
    public void Validate_不支持的Schema版本_拒绝()
    {
        var definition = ValidDefinition();
        definition.SchemaVersion = 999;
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_数据集键不匹配_拒绝()
    {
        var definition = ValidDefinition();
        definition.DatasetKey = "other";
        AssertInvalid(definition);
    }

    // ==================== 3. 字段边界 ====================

    [Fact]
    public void Validate_空字段_拒绝()
    {
        var definition = ValidDefinition();
        definition.Fields = new();
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_字段数量超限_拒绝()
    {
        var definition = ValidDefinition();
        definition.Fields = Enumerable.Range(0, ReportConfigurationRules.MaxFields + 1)
            .Select(_ => "customerName").ToList();
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_重复字段键_拒绝()
    {
        var definition = ValidDefinition();
        definition.Fields = new() { "amount", "amount" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_未知字段_拒绝()
    {
        var definition = ValidDefinition();
        definition.Fields = new() { "notExist" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_隐藏字段_拒绝()
    {
        var definition = ValidDefinition();
        definition.Fields = new() { "hiddenField" };
        AssertInvalid(definition);
    }

    // ==================== 4. 筛选边界 ====================

    [Fact]
    public void Validate_筛选数量超限_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = Enumerable.Range(0, ReportConfigurationRules.MaxFilters + 1)
            .Select(i => new ReportConfigurationFilter
            {
                FieldKey = "amount",
                Operator = ReportConfigurationConstants.OperatorGt,
                Value = i,
            }).ToList();
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_筛选不可筛选字段_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "nonFilterable", Operator = "eq", Value = "x" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_筛选操作符与类型不兼容_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "customerName", Operator = "gt", Value = "x" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_筛选值类型不匹配_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "amount", Operator = "eq", Value = "not-a-number" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_筛选值SQL注入_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "customerName", Operator = "eq", Value = "select * from users" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_筛选in数组_通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "status", Operator = "in", Value = new object[] { "Pending", "Submitted" } },
        };
        ReportConfigurationRules.Validate(definition, BuildDataset());
    }

    [Fact]
    public void Validate_筛选between日期顺序_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter
            {
                FieldKey = "orderDate",
                Operator = "between",
                Value = new DateTime(2026, 12, 31),
                Value2 = new DateTime(2026, 1, 1),
            },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_重复相等筛选_相同值_合并通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "status", Operator = "eq", Value = "Pending" },
            new ReportConfigurationFilter { FieldKey = "status", Operator = "eq", Value = "pending" },
        };
        ReportConfigurationRules.Validate(definition, BuildDataset());
    }

    [Fact]
    public void Validate_重复相等筛选_冲突值_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "status", Operator = "eq", Value = "Pending" },
            new ReportConfigurationFilter { FieldKey = "status", Operator = "eq", Value = "Submitted" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_日期筛选_重复下界交集_通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "orderDate", Operator = "gte", Value = new DateTime(2026, 2, 1) },
            new ReportConfigurationFilter { FieldKey = "orderDate", Operator = "gte", Value = new DateTime(2026, 1, 1) },
        };
        ReportConfigurationRules.Validate(definition, BuildDataset());
    }

    [Fact]
    public void Validate_日期筛选_重复上界交集_通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "orderDate", Operator = "lte", Value = new DateTime(2026, 1, 31) },
            new ReportConfigurationFilter { FieldKey = "orderDate", Operator = "lte", Value = new DateTime(2026, 2, 28) },
        };
        ReportConfigurationRules.Validate(definition, BuildDataset());
    }

    [Fact]
    public void Validate_日期筛选_交集为空_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            new ReportConfigurationFilter { FieldKey = "orderDate", Operator = "gte", Value = new DateTime(2026, 3, 1) },
            new ReportConfigurationFilter { FieldKey = "orderDate", Operator = "lte", Value = new DateTime(2026, 2, 1) },
        };
        AssertInvalid(definition);
    }

    // ==================== 5. 分组边界 ====================

    [Fact]
    public void Validate_分组数量超限_拒绝()
    {
        var definition = ValidDefinition();
        definition.Grouping = Enumerable.Range(0, ReportConfigurationRules.MaxGroupings + 1)
            .Select(_ => "customer").ToList();
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_重复分组键_拒绝()
    {
        var definition = ValidDefinition();
        definition.Grouping = new() { "customer", "customer" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_未知分组键_拒绝()
    {
        var definition = ValidDefinition();
        definition.Grouping = new() { "quarter" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_数据集不支持的分组键_拒绝()
    {
        var definition = ValidDefinition();
        definition.Grouping = new() { "month" };
        AssertInvalid(definition, BuildDataset(groupingKeys: new[] { "none", "customer" }));
    }

    // ==================== 6. 聚合边界 ====================

    [Fact]
    public void Validate_聚合数量超限_拒绝()
    {
        var definition = ValidDefinition();
        definition.Aggregates = Enumerable.Range(0, ReportConfigurationRules.MaxAggregates + 1)
            .Select(_ => new ReportConfigurationAggregate { Function = "count", FieldKey = "amount" })
            .ToList();
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_聚合未知函数_拒绝()
    {
        var definition = ValidDefinition();
        definition.Aggregates = new()
        {
            new ReportConfigurationAggregate { Function = "median", FieldKey = "amount" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_聚合不可聚合字段_拒绝()
    {
        var definition = ValidDefinition();
        definition.Aggregates = new()
        {
            new ReportConfigurationAggregate { Function = "sum", FieldKey = "nonAggregatableId" },
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_聚合函数与类型不兼容_拒绝()
    {
        var definition = ValidDefinition();
        definition.Aggregates = new()
        {
            new ReportConfigurationAggregate { Function = "sum", FieldKey = "aggregatableDate" },
        };
        AssertInvalid(definition);
    }


    // ==================== 7. 能力边界 ====================

    [Fact]
    public void Validate_不支持能力_拒绝()
    {
        var definition = ValidDefinition();
        definition.Capabilities = new() { "custom-formula" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_未知能力_拒绝()
    {
        var definition = ValidDefinition();
        definition.Capabilities = new() { "foo" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_数据集不支持的能力_拒绝()
    {
        var definition = ValidDefinition();
        definition.Capabilities = new() { "paging" };
        AssertInvalid(definition, BuildDataset(supportedCapabilities: new[] { "preview", "grouping", "date-range" }));
    }

    // ==================== 7.1 覆盖口径（ERP-274：仅快照能力数据集可全匹配集；旧定义保持当前页） ====================

    [Fact]
    public void Validate_匹配集覆盖_数据集支持快照_通过()
    {
        var definition = ValidDefinition();
        definition.Coverage = ReportConfigurationConstants.CoverageMatchedSet;

        var dataset = BuildDataset(supportedCapabilities: new[]
        {
            "preview", "grouping", "date-range", "paging", "matched-set",
        });

        ReportConfigurationRules.Validate(definition, dataset);
    }

    [Fact]
    public void Validate_匹配集覆盖_数据集不支持快照_拒绝()
    {
        var definition = ValidDefinition();
        definition.Coverage = ReportConfigurationConstants.CoverageMatchedSet;

        AssertInvalid(definition, BuildDataset());
    }

    [Fact]
    public void Validate_未知覆盖口径_拒绝()
    {
        var definition = ValidDefinition();
        definition.Coverage = "all";

        AssertInvalid(definition, BuildDataset(supportedCapabilities: new[]
        {
            "preview", "grouping", "date-range", "paging", "matched-set",
        }));
    }

    [Fact]
    public void Validate_旧定义缺省覆盖_默认当前页_通过()
    {
        var definition = ValidDefinition();
        definition.Coverage = string.Empty;

        ReportConfigurationRules.Validate(definition, BuildDataset());
    }

    // ==================== 8. 展示 / 分页边界 ====================

    [Fact]
    public void Validate_页码越界_拒绝()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation { Page = 0, PageSize = 20 };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_每页条数越界_拒绝()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 201 };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_排序字段不在已选字段_拒绝()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation { SortFieldKey = "status" };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_排序方向非法_拒绝()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation
        {
            SortFieldKey = "amount",
            SortDirection = "up",
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_排序字段不可排序_拒绝()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation
        {
            SortFieldKey = "amount",
            SortDirection = "asc",
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_排序字段可排序且方向合法_通过()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation
        {
            SortFieldKey = "orderDate",
            SortDirection = "desc",
        };

        var dataset = BuildDataset(fields: new List<ReportConfigurationFieldDto>
        {
            Field("customerName", ReportConfigurationConstants.TypeText, filterable: true),
            Field("amount", ReportConfigurationConstants.TypeNumber, filterable: true, aggregatable: true),
            Field("orderDate", ReportConfigurationConstants.TypeDate, filterable: true, sortable: true),
            Field("status", ReportConfigurationConstants.TypeEnum, filterable: true),
            Field("isActive", ReportConfigurationConstants.TypeBoolean, filterable: true),
            Field("aggregatableDate", ReportConfigurationConstants.TypeDate, aggregatable: true),
            Field("nonFilterable", ReportConfigurationConstants.TypeText, filterable: false),
            Field("nonAggregatableId", ReportConfigurationConstants.TypeNumber, filterable: false, aggregatable: false),
            Field("hiddenField", ReportConfigurationConstants.TypeText, hidden: true),
        });

        ReportConfigurationRules.Validate(definition, dataset);
    }

    // ==================== 9. 序列化大小上限（64KiB） ====================

    [Fact]
    public void Validate_序列化大小超限_拒绝()
    {
        var definition = ValidDefinition();
        definition.Presentation = new ReportConfigurationPresentation
        {
            Title = new string('x', ReportConfigurationRules.MaxSerializedBytes + 100),
        };
        AssertInvalid(definition);
    }
}

