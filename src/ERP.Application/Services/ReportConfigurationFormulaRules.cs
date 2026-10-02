using ERP.Application.Common;
using ERP.Application.DTOs;
using System.Globalization;
using System.Text.Json;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-266）受限计算列纯规则：校验与求值结构化算术 AST。
/// <para>仅允许已授权数值字段引用、数值字面量与 + - * /；无任意公式 / eval / 脚本 / SQL。</para>
/// <para>有界：每定义最多 8 列、每列 64 节点、深度 8、字面量绝对值 ≤ 1e12、整定义 ≤ 64KiB（由
/// <see cref="ReportConfigurationRules"/> 统一执行）；计算列键不得与基础字段冲突、不得引用其它计算列。</para>
/// <para>单位 / 币种语义：加减要求兼容单位；乘除允许无量纲常量；同单位比值无量纲；无法证明兼容即拒绝。</para>
/// <para>checked 十进制：缺失 / null 输入、除数为零、溢出返回 null 并携带显式有界原因，绝不静默置零。
/// 求值只作用于当前预览明细行（分组视图不计算行级计算列），不做计算列筛选 / 分组 / 聚合 / 全匹配合计。</para>
/// <para>无数据库依赖，便于逐条单测。</para>
/// </summary>
public static class ReportConfigurationFormulaRules
{
    // ==================== 有界约束 ====================

    /// <summary>每定义最多计算列数</summary>
    public const int MaxComputedColumns = 8;

    /// <summary>每列最多 AST 节点数</summary>
    public const int MaxNodes = 64;

    /// <summary>每列最大嵌套深度</summary>
    public const int MaxDepth = 8;

    /// <summary>字面量绝对值上限</summary>
    public const decimal MaxLiteralAbs = 1_000_000_000_000m;

    // ==================== 节点种类（有限） ====================

    public const string NodeField = "field";
    public const string NodeLiteral = "literal";
    public const string NodeAdd = "add";
    public const string NodeSubtract = "subtract";
    public const string NodeMultiply = "multiply";
    public const string NodeDivide = "divide";

    // ==================== 显式有界未知原因 ====================

    /// <summary>缺失输入（字段键不存在或值为 null）</summary>
    public const string ReasonMissingInput = "缺失输入";

    /// <summary>除数为零</summary>
    public const string ReasonZeroDivisor = "除数为零";

    /// <summary>数值溢出</summary>
    public const string ReasonOverflow = "数值溢出";

    /// <summary>未知（无法证明 / 非数值输入，fail closed）</summary>
    public const string ReasonUnknown = "未知";

    /// <summary>计算列 null 的列级口径说明（UI / Excel / PDF 共用）</summary>
    public const string UnknownReasonText = "计算列 null 表示：缺失输入 / 除数为零 / 数值溢出 / 未知";

    // ==================== 单位 / 币种语义 ====================

    private const string CurrencyUnitText = "原币金额";
    private const string RatioUnitText = "%";

    /// <summary>计算列推导出的单位种类（有界、用于兼容性证明）</summary>
    public enum ReportConfigurationFormulaUnitKind
    {
        Dimensionless = 0,
        Currency = 1,
        Ratio = 2,
        Unknown = 3,
    }

    // ==================== 校验 ====================

    /// <summary>
    /// 校验定义中的计算列（纯、无副作用）：数量 / 键唯一性与冲突 / AST 结构 / 字面量边界 /
    /// 节点与深度 / 已授权数值字段引用 / 计算列间引用拒绝 / 单位币种兼容性。
    /// <para>定义无计算列时立即返回（向后兼容旧定义）。</para>
    /// </summary>
    public static void ValidateComputedColumns(
        ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        if (definition?.ComputedColumns is null || definition.ComputedColumns.Count == 0)
            return;

        if (definition.ComputedColumns.Count > MaxComputedColumns)
            throw BusinessException.InvalidParameter($"计算列最多 {MaxComputedColumns} 个");

        var baseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in dataset.Fields)
            baseKeys.Add(field.Key);

        var computedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in definition.ComputedColumns)
        {
            if (column is null)
                throw BusinessException.InvalidParameter("计算列不能为空");
            var key = (column.Key ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(key))
                throw BusinessException.InvalidParameter("计算列键不能为空");
            if (!computedKeys.Add(key))
                throw BusinessException.InvalidParameter($"计算列键重复：{key}");
            if (baseKeys.Contains(key))
                throw BusinessException.InvalidParameter($"计算列键与基础字段冲突：{key}");
        }

        foreach (var column in definition.ComputedColumns)
        {
            var key = (column.Key ?? string.Empty).Trim();
            if (column.Expression is null)
                throw BusinessException.InvalidParameter($"计算列 {key} 缺少表达式");

            var nodeCount = 0;
            ValidateNode(column.Expression, dataset, computedKeys, ref nodeCount, depth: 1, key);
        }
    }

    private static ReportConfigurationFormulaUnitKind ValidateNode(
        ReportConfigurationFormulaNode? node,
        ReportConfigurationDatasetDto dataset,
        HashSet<string> computedKeys,
        ref int nodeCount,
        int depth,
        string columnKey)
    {
        if (node is null)
            throw BusinessException.InvalidParameter($"计算列 {columnKey} 表达式节点不能为空");

        nodeCount++;
        if (nodeCount > MaxNodes)
            throw BusinessException.InvalidParameter($"计算列 {columnKey} 表达式节点数超过 {MaxNodes}");
        if (depth > MaxDepth)
            throw BusinessException.InvalidParameter($"计算列 {columnKey} 表达式嵌套深度超过 {MaxDepth}");

        var kind = (node.Kind ?? string.Empty).Trim().ToLowerInvariant();
        switch (kind)
        {
            case NodeLiteral:
                if (node.Literal is null)
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 字面量节点缺少数值");
                if (node.Left is not null || node.Right is not null || !string.IsNullOrWhiteSpace(node.FieldKey))
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 字面量节点含非法子节点 / 字段引用");
                if (node.Literal.Value < -MaxLiteralAbs || node.Literal.Value > MaxLiteralAbs)
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 字面量绝对值超过 {MaxLiteralAbs}");
                return ReportConfigurationFormulaUnitKind.Dimensionless;

            case NodeField:
            {
                if (node.Literal is not null || node.Left is not null || node.Right is not null)
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 字段节点含非法子节点 / 字面量");
                var key = (node.FieldKey ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(key))
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 字段节点缺少字段键");
                if (computedKeys.Contains(key))
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 引用了其它计算列 {key}（不支持计算列间引用）");
                if (!TryGetField(dataset, key, out var field))
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 引用了未知字段 {key}");
                if (field.Hidden)
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 引用了隐藏字段 {key}");
                if (!string.Equals(field.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase))
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 引用了非数值字段 {key}");

                var unit = ClassifyUnit(field.CurrencyUnit);
                if (unit == ReportConfigurationFormulaUnitKind.Unknown)
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 引用的字段 {key} 单位 / 币种语义无法证明");
                return unit;
            }

            case NodeAdd:
            case NodeSubtract:
            case NodeMultiply:
            case NodeDivide:
            {
                if (node.Literal is not null || !string.IsNullOrWhiteSpace(node.FieldKey))
                    throw BusinessException.InvalidParameter($"计算列 {columnKey} 二元节点含非法字面量 / 字段引用");
                var left = ValidateNode(node.Left, dataset, computedKeys, ref nodeCount, depth + 1, columnKey);
                var right = ValidateNode(node.Right, dataset, computedKeys, ref nodeCount, depth + 1, columnKey);
                return CombineUnit(kind, left, right, columnKey);
            }

            default:
                throw BusinessException.InvalidParameter($"计算列 {columnKey} 含不支持的表达式节点种类 {node.Kind}");
        }
    }

    private static ReportConfigurationFormulaUnitKind CombineUnit(
        string kind,
        ReportConfigurationFormulaUnitKind left,
        ReportConfigurationFormulaUnitKind right,
        string columnKey)
    {
        var result = CombineUnitKind(kind, left, right);
        if (result == ReportConfigurationFormulaUnitKind.Unknown)
            throw BusinessException.InvalidParameter($"计算列 {columnKey} 的单位 / 币种语义无法证明兼容");
        return result;
    }

    private static ReportConfigurationFormulaUnitKind CombineUnitKind(
        string kind,
        ReportConfigurationFormulaUnitKind left,
        ReportConfigurationFormulaUnitKind right)
    {
        switch (kind)
        {
            case NodeAdd:
            case NodeSubtract:
                return left != ReportConfigurationFormulaUnitKind.Unknown && left == right
                    ? left
                    : ReportConfigurationFormulaUnitKind.Unknown;

            case NodeMultiply:
                if (left == ReportConfigurationFormulaUnitKind.Unknown || right == ReportConfigurationFormulaUnitKind.Unknown)
                    return ReportConfigurationFormulaUnitKind.Unknown;
                if (left == ReportConfigurationFormulaUnitKind.Currency && right == ReportConfigurationFormulaUnitKind.Currency)
                    return ReportConfigurationFormulaUnitKind.Unknown;
                if (left == ReportConfigurationFormulaUnitKind.Currency || right == ReportConfigurationFormulaUnitKind.Currency)
                    return ReportConfigurationFormulaUnitKind.Currency;
                if (left == ReportConfigurationFormulaUnitKind.Ratio || right == ReportConfigurationFormulaUnitKind.Ratio)
                    return ReportConfigurationFormulaUnitKind.Ratio;
                return ReportConfigurationFormulaUnitKind.Dimensionless;

            case NodeDivide:
                if (left == ReportConfigurationFormulaUnitKind.Unknown || right == ReportConfigurationFormulaUnitKind.Unknown)
                    return ReportConfigurationFormulaUnitKind.Unknown;
                return right switch
                {
                    ReportConfigurationFormulaUnitKind.Currency =>
                        left == ReportConfigurationFormulaUnitKind.Currency
                            ? ReportConfigurationFormulaUnitKind.Dimensionless
                            : ReportConfigurationFormulaUnitKind.Unknown,
                    ReportConfigurationFormulaUnitKind.Ratio => left switch
                    {
                        ReportConfigurationFormulaUnitKind.Currency => ReportConfigurationFormulaUnitKind.Currency,
                        ReportConfigurationFormulaUnitKind.Ratio => ReportConfigurationFormulaUnitKind.Dimensionless,
                        _ => ReportConfigurationFormulaUnitKind.Unknown,
                    },
                    _ => left switch
                    {
                        ReportConfigurationFormulaUnitKind.Currency => ReportConfigurationFormulaUnitKind.Currency,
                        ReportConfigurationFormulaUnitKind.Ratio => ReportConfigurationFormulaUnitKind.Ratio,
                        _ => ReportConfigurationFormulaUnitKind.Dimensionless,
                    },
                };

            default:
                return ReportConfigurationFormulaUnitKind.Unknown;
        }
    }

    // ==================== 单位分类 ====================

    /// <summary>把字段的币种 / 单位文案归类为有界单位种类（无法证明归类即 Unknown，fail closed）。</summary>
    public static ReportConfigurationFormulaUnitKind ClassifyUnit(string? currencyUnit)
    {
        var unit = currencyUnit?.Trim();
        if (string.IsNullOrEmpty(unit))
            return ReportConfigurationFormulaUnitKind.Dimensionless;
        if (unit == RatioUnitText)
            return ReportConfigurationFormulaUnitKind.Ratio;
        if (unit.Contains("金额", StringComparison.Ordinal))
            return ReportConfigurationFormulaUnitKind.Currency;
        return ReportConfigurationFormulaUnitKind.Unknown;
    }

    // ==================== 依赖收集 ====================

    /// <summary>收集全部计算列依赖的基础字段键（去重、保持出现顺序）。</summary>
    public static IReadOnlyList<string> CollectDependencies(IReadOnlyList<ReportConfigurationComputedColumn> columns)
    {
        var dependencies = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (columns is null)
            return dependencies;

        foreach (var column in columns)
            CollectNodeDependencies(column?.Expression, dependencies, seen);
        return dependencies;
    }

    private static void CollectNodeDependencies(
        ReportConfigurationFormulaNode? node,
        List<string> dependencies,
        HashSet<string> seen)
    {
        if (node is null)
            return;

        var kind = (node.Kind ?? string.Empty).Trim().ToLowerInvariant();
        switch (kind)
        {
            case NodeField:
                var key = (node.FieldKey ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(key) && seen.Add(key))
                    dependencies.Add(key);
                return;
            case NodeAdd:
            case NodeSubtract:
            case NodeMultiply:
            case NodeDivide:
                CollectNodeDependencies(node.Left, dependencies, seen);
                CollectNodeDependencies(node.Right, dependencies, seen);
                return;
        }
    }

    // ==================== 求值 ====================

    /// <summary>
    /// 对当前预览明细行求值全部计算列（纯）：每行返回各计算列的 decimal? 值与 null 的有界原因。
    /// <para>缺失 / null 输入、除数为零、溢出均返回 null（绝不静默置零）。</para>
    /// </summary>
    public static IReadOnlyList<ReportConfigurationFormulaRow> Evaluate(
        IReadOnlyList<ReportConfigurationComputedColumn> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var result = new List<ReportConfigurationFormulaRow>(rows?.Count ?? 0);
        if (rows is null)
            return result;

        var effectiveColumns = columns ?? new List<ReportConfigurationComputedColumn>();
        foreach (var row in rows)
        {
            var values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
            var reasons = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in effectiveColumns)
            {
                if (column is null)
                    continue;
                var (value, reason) = EvaluateNode(column.Expression, row);
                values[column.Key] = value;
                if (reason is not null)
                    reasons[column.Key] = reason;
            }

            result.Add(new ReportConfigurationFormulaRow(values, reasons));
        }

        return result;
    }

    private static (decimal? Value, string? Reason) EvaluateNode(
        ReportConfigurationFormulaNode? node,
        Dictionary<string, object?> row)
    {
        if (node is null)
            return (null, ReasonUnknown);

        var kind = (node.Kind ?? string.Empty).Trim().ToLowerInvariant();
        switch (kind)
        {
            case NodeLiteral:
                return node.Literal.HasValue ? (node.Literal.Value, null) : (null, ReasonUnknown);

            case NodeField:
            {
                var key = (node.FieldKey ?? string.Empty).Trim();
                var raw = ReadValue(row, key);
                if (raw is null)
                    return (null, ReasonMissingInput);
                return TryReadDecimal(raw, out var value) ? (value, null) : (null, ReasonUnknown);
            }

            case NodeAdd:
            case NodeSubtract:
            case NodeMultiply:
            case NodeDivide:
            {
                var (left, leftReason) = EvaluateNode(node.Left, row);
                if (leftReason is not null)
                    return (null, leftReason);
                var (right, rightReason) = EvaluateNode(node.Right, row);
                if (rightReason is not null)
                    return (null, rightReason);
                if (!left.HasValue || !right.HasValue)
                    return (null, ReasonMissingInput);

                try
                {
                    decimal computed = kind switch
                    {
                        NodeAdd => checked(left.Value + right.Value),
                        NodeSubtract => checked(left.Value - right.Value),
                        NodeMultiply => checked(left.Value * right.Value),
                        _ => right.Value == 0m
                            ? throw new DivideByZeroException()
                            : checked(left.Value / right.Value),
                    };
                    return (computed, null);
                }
                catch (DivideByZeroException)
                {
                    return (null, ReasonZeroDivisor);
                }
                catch (OverflowException)
                {
                    return (null, ReasonOverflow);
                }
            }

            default:
                return (null, ReasonUnknown);
        }
    }

    private static object? ReadValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;

        // 大小写不敏感回退：字段键以目录为准，仍做防御性查找
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    private static bool TryReadDecimal(object? raw, out decimal value)
    {
        switch (raw)
        {
            case decimal m: value = m; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short s: value = s; return true;
            case byte b: value = b; return true;
            case uint ui: value = ui; return true;
            case ulong ul: value = ul; return true;
            case ushort us: value = us; return true;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) { value = 0m; return false; }
                try { value = Convert.ToDecimal(d); return true; }
                catch (OverflowException) { value = 0m; return false; }
            case float f:
                if (float.IsNaN(f) || float.IsInfinity(f)) { value = 0m; return false; }
                try { value = Convert.ToDecimal(f); return true; }
                catch (OverflowException) { value = 0m; return false; }
            case string s:
                return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Number)
                    return element.TryGetDecimal(out value);
                if (element.ValueKind == JsonValueKind.String)
                    return decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
                value = 0m;
                return false;
            default:
                value = 0m;
                return false;
        }
    }

    // ==================== 单位推导 / 证据 ====================

    /// <summary>推导计算列展示单位（原币金额 / % / 无量纲空）。仅对已校验列使用。</summary>
    public static string? DeriveUnit(ReportConfigurationComputedColumn column, Func<string, string?> unitLookup)
    {
        if (column?.Expression is null)
            return null;

        return DeriveUnitKind(column.Expression, unitLookup) switch
        {
            ReportConfigurationFormulaUnitKind.Currency => CurrencyUnitText,
            ReportConfigurationFormulaUnitKind.Ratio => RatioUnitText,
            _ => null,
        };
    }

    private static ReportConfigurationFormulaUnitKind DeriveUnitKind(
        ReportConfigurationFormulaNode? node, Func<string, string?> unitLookup)
    {
        if (node is null)
            return ReportConfigurationFormulaUnitKind.Unknown;

        var kind = (node.Kind ?? string.Empty).Trim().ToLowerInvariant();
        switch (kind)
        {
            case NodeLiteral:
                return ReportConfigurationFormulaUnitKind.Dimensionless;
            case NodeField:
                return ClassifyUnit(unitLookup((node.FieldKey ?? string.Empty).Trim()));
            case NodeAdd:
            case NodeSubtract:
            case NodeMultiply:
            case NodeDivide:
                var left = DeriveUnitKind(node.Left, unitLookup);
                var right = DeriveUnitKind(node.Right, unitLookup);
                return CombineUnitKind(kind, left, right);
            default:
                return ReportConfigurationFormulaUnitKind.Unknown;
        }
    }

    /// <summary>构建计算列证据（键 / 标签 / 单位 / 未知值口径 / 依赖），供 UI / Excel / PDF 共用。</summary>
    public static IReadOnlyList<ReportConfigurationComputedColumnEvidenceDto> BuildEvidence(
        IReadOnlyList<ReportConfigurationComputedColumn> columns,
        Func<string, string?> unitLookup)
    {
        var result = new List<ReportConfigurationComputedColumnEvidenceDto>();
        if (columns is null)
            return result;

        foreach (var column in columns)
        {
            if (column is null)
                continue;
            var key = (column.Key ?? string.Empty).Trim();
            var label = string.IsNullOrWhiteSpace(column.Label) ? key : column.Label.Trim();
            result.Add(new ReportConfigurationComputedColumnEvidenceDto
            {
                Key = key,
                Label = label,
                Unit = DeriveUnit(column, unitLookup) ?? string.Empty,
                UnknownReason = UnknownReasonText,
                Dependencies = CollectDependencies(new[] { column }),
            });
        }

        return result;
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
}

/// <summary>单行计算列求值结果：每列 decimal? 值 + null 单元格的有界原因（非 null 单元格无原因）。</summary>
public sealed record ReportConfigurationFormulaRow(
    IReadOnlyDictionary<string, decimal?> Values,
    IReadOnlyDictionary<string, string?> Reasons);

