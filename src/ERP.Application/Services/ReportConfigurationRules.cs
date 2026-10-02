using ERP.Application.Common;
using ERP.Application.DTOs;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的纯校验器：在保存 / 执行前，把报表定义与已授权数据集目录
/// 逐项比对并强制有界约束（schema 版本、64KiB 序列化上限、字段 / 筛选 / 分组 / 聚合数量上限、
/// 类型化筛选与有限操作符、能力标志与分页边界），拒绝未知 / 重复 / 隐藏 / 未授权字段、不兼容操作、
/// 非法类型与 SQL / 脚本载荷。
/// <para>无数据库依赖，便于逐条单测；自定义公式、跨数据集联接、透视与全匹配合计在 Stage 1 显式不支持。</para>
/// </summary>
public static class ReportConfigurationRules
{
    // ==================== 0. 有界约束常量 ====================

    /// <summary>当前受支持的 schema 版本</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>序列化定义上限（64KiB）</summary>
    public const int MaxSerializedBytes = 64 * 1024;

    /// <summary>字段数量上限</summary>
    public const int MaxFields = 32;

    /// <summary>筛选数量上限</summary>
    public const int MaxFilters = 32;

    /// <summary>分组数量上限</summary>
    public const int MaxGroupings = 8;

    /// <summary>聚合定义数量上限</summary>
    public const int MaxAggregates = 4;

    // ==================== 0.1 有限枚举集合 ====================

    private static readonly string[] TextOperators =
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorNe,
        ReportConfigurationConstants.OperatorIn,
    };

    private static readonly string[] NumberOperators =
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorNe,
        ReportConfigurationConstants.OperatorIn,
        ReportConfigurationConstants.OperatorGt,
        ReportConfigurationConstants.OperatorGte,
        ReportConfigurationConstants.OperatorLt,
        ReportConfigurationConstants.OperatorLte,
        ReportConfigurationConstants.OperatorBetween,
    };

    private static readonly string[] DateOperators =
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorNe,
        ReportConfigurationConstants.OperatorIn,
        ReportConfigurationConstants.OperatorGt,
        ReportConfigurationConstants.OperatorGte,
        ReportConfigurationConstants.OperatorLt,
        ReportConfigurationConstants.OperatorLte,
        ReportConfigurationConstants.OperatorBetween,
    };

    private static readonly string[] BooleanOperators =
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorNe,
        ReportConfigurationConstants.OperatorIn,
    };

    private static readonly string[] EnumOperators =
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorNe,
        ReportConfigurationConstants.OperatorIn,
    };

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportConfigurationConstants.TypeText,
        ReportConfigurationConstants.TypeNumber,
        ReportConfigurationConstants.TypeDate,
        ReportConfigurationConstants.TypeBoolean,
        ReportConfigurationConstants.TypeEnum,
    };

    private static readonly HashSet<string> KnownOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorNe,
        ReportConfigurationConstants.OperatorIn,
        ReportConfigurationConstants.OperatorGt,
        ReportConfigurationConstants.OperatorGte,
        ReportConfigurationConstants.OperatorLt,
        ReportConfigurationConstants.OperatorLte,
        ReportConfigurationConstants.OperatorBetween,
    };

    private static readonly HashSet<string> KnownGroupingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportConfigurationConstants.GroupNone,
        ReportConfigurationConstants.GroupCustomer,
        ReportConfigurationConstants.GroupMonth,
    };

    private static readonly HashSet<string> KnownAggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportConfigurationConstants.AggregateSum,
        ReportConfigurationConstants.AggregateCount,
        ReportConfigurationConstants.AggregateAverage,
        ReportConfigurationConstants.AggregateMin,
        ReportConfigurationConstants.AggregateMax,
    };

    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityGrouping,
        ReportConfigurationConstants.CapabilityDateRange,
        ReportConfigurationConstants.CapabilityPaging,
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    private static readonly HashSet<string> UnsupportedCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    // ==================== 1. 公共只读助手 ====================

    /// <summary>字段类型是否为已知有限取值</summary>
    public static bool IsKnownFieldType(string? type)
        => !string.IsNullOrWhiteSpace(type) && KnownTypes.Contains(type.Trim());

    /// <summary>筛选操作符是否为已知有限取值</summary>
    public static bool IsKnownOperator(string? op)
        => !string.IsNullOrWhiteSpace(op) && KnownOperators.Contains(op.Trim());

    /// <summary>分组键是否为已知有限取值</summary>
    public static bool IsKnownGroupingKey(string? group)
        => !string.IsNullOrWhiteSpace(group) && KnownGroupingKeys.Contains(group.Trim());

    /// <summary>聚合函数是否为已知有限取值</summary>
    public static bool IsKnownAggregateFunction(string? function)
        => !string.IsNullOrWhiteSpace(function) && KnownAggregateFunctions.Contains(function.Trim());

    /// <summary>能力标志是否为已知有限取值</summary>
    public static bool IsKnownCapability(string? capability)
        => !string.IsNullOrWhiteSpace(capability) && KnownCapabilities.Contains(capability.Trim());

    /// <summary>返回某字段类型允许的有限筛选操作符（与校验器同源）</summary>
    public static IReadOnlyList<string> GetOperatorsForType(string type)
    {
        return type switch
        {
            ReportConfigurationConstants.TypeText => TextOperators,
            ReportConfigurationConstants.TypeNumber => NumberOperators,
            ReportConfigurationConstants.TypeDate => DateOperators,
            ReportConfigurationConstants.TypeBoolean => BooleanOperators,
            ReportConfigurationConstants.TypeEnum => EnumOperators,
            _ => Array.Empty<string>(),
        };
    }


    // ==================== 2. 校验入口 ====================

    /// <summary>
    /// 校验报表定义（fail closed）：任何违反有界约束 / 未知或隐藏字段 / 不兼容操作 / 非法类型 /
    /// SQL 脚本载荷 / 不支持能力的定义都会被拒绝，绝不返回「部分有效」。
    /// </summary>
    public static void Validate(ReportConfigurationDefinition? definition, ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dataset);

        ValidateSerializedSize(definition);

        if (definition.SchemaVersion != CurrentSchemaVersion)
            throw BusinessException.InvalidParameter($"不支持的 schema 版本: {definition.SchemaVersion}（当前仅支持 {CurrentSchemaVersion}）");

        if (string.IsNullOrWhiteSpace(definition.DatasetKey))
            throw BusinessException.InvalidParameter("数据集键不能为空");
        if (!string.Equals(definition.DatasetKey.Trim(), dataset.DatasetKey, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter($"数据集键不匹配: {definition.DatasetKey}（预期 {dataset.DatasetKey}）");

        ValidateFields(definition, dataset);
        ValidateFilters(definition, dataset);
        ValidateGrouping(definition, dataset);
        ValidateAggregates(definition, dataset);
        ValidateCapabilities(definition, dataset);
        ValidatePresentation(definition, dataset);
    }

    // ==================== 3. 有界尺寸 / 字段 / 筛选 ====================

    private static void ValidateSerializedSize(ReportConfigurationDefinition definition)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(definition);
        if (bytes.Length > MaxSerializedBytes)
            throw BusinessException.InvalidParameter($"报表定义序列化大小 {bytes.Length} 字节，超过上限 {MaxSerializedBytes} 字节（64KiB）");
    }

    private static void ValidateFields(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var fields = definition.Fields ?? new List<string>();
        if (fields.Count == 0)
            throw BusinessException.InvalidParameter("至少选择一个字段");
        if (fields.Count > MaxFields)
            throw BusinessException.InvalidParameter($"字段数量不能超过 {MaxFields} 个");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in fields)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw BusinessException.InvalidParameter("字段键不能为空");
            var key = raw.Trim();
            if (!seen.Add(key))
                throw BusinessException.InvalidParameter($"重复字段键: {key}");
            if (!TryGetField(dataset, key, out var field))
                throw BusinessException.InvalidParameter($"未知字段: {key}（不在数据集白名单内）");
            if (field.Hidden)
                throw BusinessException.InvalidParameter($"隐藏字段不允许选择: {key}");
        }
    }

    private static void ValidateFilters(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var filters = definition.Filters ?? new List<ReportConfigurationFilter>();
        if (filters.Count > MaxFilters)
            throw BusinessException.InvalidParameter($"筛选数量不能超过 {MaxFilters} 个");

        foreach (var filter in filters)
            ValidateFilter(filter, dataset);
    }

    private static void ValidateFilter(ReportConfigurationFilter filter, ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (string.IsNullOrWhiteSpace(filter.FieldKey))
            throw BusinessException.InvalidParameter("筛选字段键不能为空");
        var key = filter.FieldKey.Trim();
        if (!TryGetField(dataset, key, out var field))
            throw BusinessException.InvalidParameter($"未知筛选字段: {key}");
        if (field.Hidden)
            throw BusinessException.InvalidParameter($"隐藏字段不允许用于筛选: {key}");
        if (!field.Filterable)
            throw BusinessException.InvalidParameter($"字段不可筛选: {key}");

        var op = (filter.Operator ?? string.Empty).Trim();
        if (!IsKnownOperator(op))
            throw BusinessException.InvalidParameter($"未知筛选操作符: {op}");
        if (!field.FilterOperators.Contains(op, StringComparer.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter($"操作符 {op} 与字段类型 {field.Type} 不兼容（{key}）");

        if (string.Equals(op, ReportConfigurationConstants.OperatorIn, StringComparison.OrdinalIgnoreCase))
        {
            ValidateInValues(field, filter.Value);
        }
        else if (string.Equals(op, ReportConfigurationConstants.OperatorBetween, StringComparison.OrdinalIgnoreCase))
        {
            var lower = NormalizeScalar(field, filter.Value, "between 下界");
            var upper = NormalizeScalar(field, filter.Value2, "between 上界");
            ValidateBetweenOrdering(field, lower, upper);
        }
        else
        {
            NormalizeScalar(field, filter.Value, "筛选值");
        }
    }


    // ==================== 4. 分组 / 聚合 / 能力 / 展示 ====================

    private static void ValidateGrouping(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var grouping = definition.Grouping ?? new List<string>();
        if (grouping.Count > MaxGroupings)
            throw BusinessException.InvalidParameter($"分组数量不能超过 {MaxGroupings} 个");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in grouping)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw BusinessException.InvalidParameter("分组键不能为空");
            var key = raw.Trim();
            if (!seen.Add(key))
                throw BusinessException.InvalidParameter($"重复分组键: {key}");
            if (!IsKnownGroupingKey(key))
                throw BusinessException.InvalidParameter($"未知分组键: {key}");
            if (!dataset.GroupingKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"分组键 {key} 不是数据集支持的分组（{dataset.DatasetKey}）");
        }
    }

    private static void ValidateAggregates(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var aggregates = definition.Aggregates ?? new List<ReportConfigurationAggregate>();
        if (aggregates.Count > MaxAggregates)
            throw BusinessException.InvalidParameter($"聚合定义数量不能超过 {MaxAggregates} 个");

        foreach (var aggregate in aggregates)
            ValidateAggregate(aggregate, dataset);
    }

    private static void ValidateAggregate(ReportConfigurationAggregate aggregate, ReportConfigurationDatasetDto dataset)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var function = (aggregate.Function ?? string.Empty).Trim();
        if (!IsKnownAggregateFunction(function))
            throw BusinessException.InvalidParameter($"未知聚合函数: {function}");

        if (string.IsNullOrWhiteSpace(aggregate.FieldKey))
            throw BusinessException.InvalidParameter("聚合字段键不能为空");
        var key = aggregate.FieldKey.Trim();
        if (!TryGetField(dataset, key, out var field))
            throw BusinessException.InvalidParameter($"未知聚合字段: {key}");
        if (field.Hidden)
            throw BusinessException.InvalidParameter($"隐藏字段不允许用于聚合: {key}");

        var isCount = string.Equals(function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase);
        if (!isCount)
        {
            if (!field.Aggregatable)
                throw BusinessException.InvalidParameter($"字段不可聚合: {key}");

            var isNumeric = string.Equals(field.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase);
            var isSumOrAvg = string.Equals(function, ReportConfigurationConstants.AggregateSum, StringComparison.OrdinalIgnoreCase)
                || string.Equals(function, ReportConfigurationConstants.AggregateAverage, StringComparison.OrdinalIgnoreCase);
            if (isSumOrAvg && !isNumeric)
                throw BusinessException.InvalidParameter($"聚合函数 {function} 只适用于数值字段（{key} 类型为 {field.Type}）");
        }
    }

    private static void ValidateCapabilities(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var capabilities = definition.Capabilities ?? new List<string>();
        foreach (var raw in capabilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw BusinessException.InvalidParameter("能力标志不能为空");
            var capability = raw.Trim();
            if (!IsKnownCapability(capability))
                throw BusinessException.InvalidParameter($"未知能力标志: {capability}");
            if (UnsupportedCapabilities.Contains(capability))
                throw BusinessException.InvalidParameter($"能力 {capability} 在当前阶段明确不支持（不提供惰性成功）");
            if (!dataset.SupportedCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"数据集 {dataset.DatasetKey} 不支持能力 {capability}");
        }
    }

    private static void ValidatePresentation(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var presentation = definition.Presentation;
        if (presentation is null)
            return;

        if (presentation.Page is < 1)
            throw BusinessException.InvalidParameter("页码必须从 1 开始");
        if (presentation.PageSize is < 1 || presentation.PageSize > dataset.MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {dataset.MaxPageSize} 之间");

        if (!string.IsNullOrWhiteSpace(presentation.SortFieldKey))
        {
            var selected = (definition.Fields ?? new List<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim());
            if (!selected.Contains(presentation.SortFieldKey.Trim(), StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"排序字段 {presentation.SortFieldKey} 必须属于已选字段");
        }

        if (!string.IsNullOrWhiteSpace(presentation.SortDirection))
        {
            var direction = presentation.SortDirection.Trim();
            if (!string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"排序方向仅支持 asc / desc：{presentation.SortDirection}");
        }
    }


    // ==================== 5. 类型化值 / SQL 脚本载荷 ====================

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

    private static object? NormalizeScalar(ReportConfigurationFieldDto field, object? raw, string context)
    {
        var value = ToClrValue(raw);
        switch (field.Type)
        {
            case ReportConfigurationConstants.TypeText:
            case ReportConfigurationConstants.TypeEnum:
                if (value is string s)
                {
                    EnsureNoSqlOrScript(s, context);
                    return s;
                }
                throw BusinessException.InvalidParameter($"{context} 必须是字符串（{field.Key} 类型为 {field.Type}）");

            case ReportConfigurationConstants.TypeNumber:
                if (IsNumeric(value))
                    return value;
                throw BusinessException.InvalidParameter($"{context} 必须是数字（{field.Key} 类型为 {field.Type}）");

            case ReportConfigurationConstants.TypeDate:
                if (value is DateTime dt)
                    return dt;
                if (value is DateTimeOffset dto)
                    return dto.DateTime;
                if (value is string ds && TryParseDate(ds, out var parsed))
                    return parsed;
                throw BusinessException.InvalidParameter($"{context} 必须是日期（{field.Key} 类型为 {field.Type}）");

            case ReportConfigurationConstants.TypeBoolean:
                if (value is bool b)
                    return b;
                throw BusinessException.InvalidParameter($"{context} 必须是布尔值（{field.Key} 类型为 {field.Type}）");

            default:
                throw BusinessException.InvalidParameter($"未知字段类型: {field.Type}");
        }
    }

    private static void ValidateInValues(ReportConfigurationFieldDto field, object? raw)
    {
        var value = ToClrValue(raw);
        if (value is string || value is not IEnumerable enumerable)
            throw BusinessException.InvalidParameter($"in 操作符需要数组值（{field.Key}）");

        var items = new List<object?>();
        foreach (var item in enumerable)
            items.Add(item);

        if (items.Count == 0)
            throw BusinessException.InvalidParameter($"in 数组不能为空（{field.Key}）");

        foreach (var item in items)
            NormalizeScalar(field, item, "in 数组元素");
    }

    private static void ValidateBetweenOrdering(ReportConfigurationFieldDto field, object? lower, object? upper)
    {
        if (string.Equals(field.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase))
        {
            if (Convert.ToDecimal(lower, CultureInfo.InvariantCulture)
                > Convert.ToDecimal(upper, CultureInfo.InvariantCulture))
                throw BusinessException.InvalidParameter($"between 下界不能大于上界（{field.Key}）");
        }
        else if (string.Equals(field.Type, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase))
        {
            if (Convert.ToDateTime(lower, CultureInfo.InvariantCulture)
                > Convert.ToDateTime(upper, CultureInfo.InvariantCulture))
                throw BusinessException.InvalidParameter($"between 开始日期不能晚于结束日期（{field.Key}）");
        }
    }


    private static bool IsNumeric(object? value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
    }

    private static bool TryParseDate(string text, out DateTime date)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out date);

    private static object? ToClrValue(object? value)
    {
        if (value is JsonElement element)
            return JsonElementToClr(element);
        return value;
    }

    private static object? JsonElementToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDecimal();
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(JsonElementToClr).ToList();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.Object:
            default:
                return null;
        }
    }

    private static void EnsureNoSqlOrScript(string? value, string context)
    {
        if (string.IsNullOrEmpty(value))
            return;

        var lower = value.ToLowerInvariant();
        var markers = new[] { "--", "/*", "*/", ";", "<script", "</script", "eval(", "exec(", "execute(" };
        if (markers.Any(m => lower.Contains(m)))
            throw BusinessException.InvalidParameter($"{context} 含疑似 SQL / 脚本载荷，已拒绝");

        var keywords = new[] { "select", "insert", "update", "delete", "drop", "truncate",
            "alter", "create", "exec", "execute", "union", "declare" };
        foreach (var keyword in keywords)
        {
            if (ContainsWholeWord(lower, keyword))
                throw BusinessException.InvalidParameter($"{context} 含疑似 SQL / 脚本关键词，已拒绝");
        }
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + word.Length >= text.Length || !char.IsLetterOrDigit(text[index + word.Length]);
            if (before && after)
                return true;
            index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal);
        }
        return false;
    }
}

