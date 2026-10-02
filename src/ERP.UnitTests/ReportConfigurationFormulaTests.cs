using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-266 受限计算列纯规则单元测试（无数据库依赖）：覆盖有界校验（数量 / 键冲突 / AST 结构 / 节点与深度 /
/// 字面量边界 / 未授权 / 隐藏 / 非数值输入 / 计算列间引用 / 单位币种兼容）与 checked 求值（缺失 / null 输入、
/// 除数为零、溢出 → null 并有界原因）、依赖收集、单位推导与证据口径。
/// </summary>
public class ReportConfigurationFormulaTests
{
    // ==================== 脚手架 ====================

    private static ReportConfigurationFieldDto Num(string key, string? unit = null, bool hidden = false)
        => new(key, key, ReportConfigurationConstants.TypeNumber, unit, false, true, hidden,
            ReportConfigurationRules.GetOperatorsForType(ReportConfigurationConstants.TypeNumber));

    private static ReportConfigurationFieldDto Text(string key)
        => new(key, key, ReportConfigurationConstants.TypeText, null, false, false, false,
            ReportConfigurationRules.GetOperatorsForType(ReportConfigurationConstants.TypeText));

    private static ReportConfigurationDatasetDto Dataset()
        => new("test", "测试数据集", "测试行", "原币",
            "test-menu", "测试菜单",
            new List<ReportConfigurationFieldDto>
            {
                Num("amount", "原币金额"),
                Num("depositAmount", "原币金额"),
                Num("depositRatio", "%"),
                Num("exchangeRate"),
                Num("hiddenAmount", "原币金额", hidden: true),
                Num("id"),
                Text("orderNo"),
            },
            new[] { ReportConfigurationConstants.GroupNone, ReportConfigurationConstants.GroupCustomer },
            new[]
            {
                ReportConfigurationConstants.CapabilityPreview,
                ReportConfigurationConstants.CapabilityGrouping,
                ReportConfigurationConstants.CapabilityDateRange,
                ReportConfigurationConstants.CapabilityPaging,
                ReportConfigurationConstants.CapabilityComputedColumns,
            },
            new[]
            {
                ReportConfigurationConstants.CapabilityCustomFormula,
                ReportConfigurationConstants.CapabilityCrossDatasetJoin,
                ReportConfigurationConstants.CapabilityPivot,
                ReportConfigurationConstants.CapabilityAllMatchTotal,
            },
            20, 200, "只读", "边界");

    private static ReportConfigurationDefinition Definition(params ReportConfigurationComputedColumn[] columns)
        => new()
        {
            SchemaVersion = 1,
            DatasetKey = "test",
            Fields = new List<string> { "amount" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            ComputedColumns = columns.ToList(),
        };

    private static ReportConfigurationFormulaNode Field(string key) => new() { Kind = "field", FieldKey = key };
    private static ReportConfigurationFormulaNode Lit(decimal value) => new() { Kind = "literal", Literal = value };
    private static ReportConfigurationFormulaNode Bin(string kind, ReportConfigurationFormulaNode left, ReportConfigurationFormulaNode right)
        => new() { Kind = kind, Left = left, Right = right };
    private static ReportConfigurationComputedColumn Col(string key, ReportConfigurationFormulaNode expression, string label = "")
        => new() { Key = key, Label = label, Expression = expression };

    private static ReportConfigurationFormulaNode LeftDeep(int count)
    {
        var node = Lit(1m);
        for (var i = 1; i < count; i++)
            node = Bin(ReportConfigurationFormulaRules.NodeAdd, node, Lit(1m));
        return node;
    }

    private static ReportConfigurationFormulaNode RightDeep(int count)
    {
        var node = Lit(1m);
        for (var i = 1; i < count; i++)
            node = Bin(ReportConfigurationFormulaRules.NodeAdd, Lit(1m), node);
        return node;
    }

    private static void AssertInvalid(ReportConfigurationDefinition definition)
        => Assert.Throws<BusinessException>(
            () => ReportConfigurationFormulaRules.ValidateComputedColumns(definition, Dataset()));

    private static void AssertValid(ReportConfigurationDefinition definition)
        => ReportConfigurationFormulaRules.ValidateComputedColumns(definition, Dataset());

    // ==================== 1. 向后兼容与合法有界表达式 ====================

    [Fact]
    public void Validate_无计算列_通过()
    {
        AssertValid(Definition());
    }

    [Fact]
    public void Validate_加减同单位_通过()
    {
        AssertValid(Definition(Col("sum", Bin(ReportConfigurationFormulaRules.NodeAdd, Field("amount"), Field("depositAmount")))));
    }

    [Fact]
    public void Validate_乘除无量纲常量与同单位比值_通过()
    {
        AssertValid(Definition(
            Col("c1", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("amount"), Lit(2m))),
            Col("c2", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("amount"), Field("depositRatio"))),
            Col("c3", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("depositAmount"), Field("amount"))),
            Col("c4", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("amount"), Field("exchangeRate")))));
    }

    // ==================== 2. 单位 / 币种兼容性 ====================

    [Fact]
    public void Validate_加减单位不兼容_拒绝()
    {
        AssertInvalid(Definition(Col("bad", Bin(ReportConfigurationFormulaRules.NodeAdd, Field("amount"), Field("depositRatio")))));
    }

    [Fact]
    public void Validate_货币乘货币_拒绝()
    {
        AssertInvalid(Definition(Col("bad", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("amount"), Field("depositAmount")))));
    }

    [Fact]
    public void Validate_无法证明的除组合_拒绝()
    {
        AssertInvalid(Definition(Col("bad", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("depositRatio"), Field("amount")))));
    }

    // ==================== 3. 未授权 / 隐藏 / 非数值输入 ====================

    [Fact]
    public void Validate_未知字段_拒绝()
    {
        AssertInvalid(Definition(Col("bad", Field("missing"))));
    }

    [Fact]
    public void Validate_隐藏字段_拒绝()
    {
        AssertInvalid(Definition(Col("bad", Field("hiddenAmount"))));
    }

    [Fact]
    public void Validate_非数值字段_拒绝()
    {
        AssertInvalid(Definition(Col("bad", Field("orderNo"))));
    }

    [Fact]
    public void Validate_计算列间引用_拒绝()
    {
        AssertInvalid(Definition(
            Col("c1", Field("amount")),
            Col("c2", Bin(ReportConfigurationFormulaRules.NodeAdd, Field("c1"), Lit(1m)))));
    }

    // ==================== 4. 键约束 ====================

    [Fact]
    public void Validate_键与基础字段冲突_拒绝()
    {
        AssertInvalid(Definition(Col("amount", Field("amount"))));
    }

    [Fact]
    public void Validate_键重复_拒绝()
    {
        AssertInvalid(Definition(Col("dup", Field("amount")), Col("dup", Field("depositAmount"))));
    }

    [Fact]
    public void Validate_超过8列_拒绝()
    {
        var columns = Enumerable.Range(1, 9)
            .Select(i => Col($"c{i}", Field("amount")))
            .ToArray();
        AssertInvalid(Definition(columns));
    }

    // ==================== 5. 节点 / 深度 / 字面量边界 ====================

    [Fact]
    public void Validate_节点数超限_拒绝()
    {
        AssertInvalid(Definition(Col("deep", LeftDeep(33))));
    }

    [Fact]
    public void Validate_深度超限_拒绝()
    {
        AssertInvalid(Definition(Col("deep", RightDeep(9))));
    }

    [Fact]
    public void Validate_字面量绝对值超限_拒绝()
    {
        AssertInvalid(Definition(Col("big", Lit(2_000_000_000_000m))));
    }

    [Fact]
    public void Validate_不支持节点种类_拒绝()
    {
        AssertInvalid(Definition(Col("bad", new ReportConfigurationFormulaNode { Kind = "power" })));
    }

    // ==================== 6. checked 求值 ====================

    private static Dictionary<string, object?> Row(object? amount = null, object? depositAmount = null, object? exchangeRate = null)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (amount is not null) row["amount"] = amount;
        if (depositAmount is not null) row["depositAmount"] = depositAmount;
        if (exchangeRate is not null) row["exchangeRate"] = exchangeRate;
        return row;
    }

    [Fact]
    public void Evaluate_加减乘除_返回正确值()
    {
        var columns = new[]
        {
            Col("sum", Bin(ReportConfigurationFormulaRules.NodeAdd, Field("amount"), Field("depositAmount"))),
            Col("ratio", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("depositAmount"), Field("amount"))),
            Col("scaled", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("amount"), Lit(2m))),
        };
        var rows = new List<Dictionary<string, object?>> { Row(amount: 100m, depositAmount: 30m) };

        var result = ReportConfigurationFormulaRules.Evaluate(columns, rows);
        var values = Assert.Single(result).Values;

        Assert.Equal(130m, values["sum"]);
        Assert.Equal(0.3m, values["ratio"]);
        Assert.Equal(200m, values["scaled"]);
    }

    [Fact]
    public void Evaluate_缺失输入_返回null与原因()
    {
        var columns = new[] { Col("sum", Bin(ReportConfigurationFormulaRules.NodeAdd, Field("amount"), Field("depositAmount"))) };
        var rows = new List<Dictionary<string, object?>> { Row(amount: 100m) };

        var result = ReportConfigurationFormulaRules.Evaluate(columns, rows);
        var row = Assert.Single(result);

        Assert.Null(row.Values["sum"]);
        Assert.Equal(ReportConfigurationFormulaRules.ReasonMissingInput, row.Reasons["sum"]);
    }

    [Fact]
    public void Evaluate_除数为零_返回null与原因()
    {
        var columns = new[] { Col("r", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("amount"), Lit(0m))) };
        var rows = new List<Dictionary<string, object?>> { Row(amount: 100m) };

        var result = ReportConfigurationFormulaRules.Evaluate(columns, rows);
        var row = Assert.Single(result);

        Assert.Null(row.Values["r"]);
        Assert.Equal(ReportConfigurationFormulaRules.ReasonZeroDivisor, row.Reasons["r"]);
    }

    [Fact]
    public void Evaluate_溢出_返回null与原因()
    {
        var columns = new[] { Col("big", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("amount"), Lit(2m))) };
        var rows = new List<Dictionary<string, object?>> { Row(amount: decimal.MaxValue) };

        var result = ReportConfigurationFormulaRules.Evaluate(columns, rows);
        var row = Assert.Single(result);

        Assert.Null(row.Values["big"]);
        Assert.Equal(ReportConfigurationFormulaRules.ReasonOverflow, row.Reasons["big"]);
    }

    // ==================== 7. 依赖收集 / 单位推导 / 证据 ====================

    [Fact]
    public void CollectDependencies_去重并保持出现顺序()
    {
        var column = Col("c", Bin(
            ReportConfigurationFormulaRules.NodeMultiply,
            Bin(ReportConfigurationFormulaRules.NodeAdd, Field("amount"), Field("depositAmount")),
            Field("exchangeRate")));

        var deps = ReportConfigurationFormulaRules.CollectDependencies(new[] { column });

        Assert.Equal(new[] { "amount", "depositAmount", "exchangeRate" }, deps);
    }

    [Fact]
    public void DeriveUnit_同单位比值无量纲()
    {
        var ratio = Col("ratio", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("depositAmount"), Field("amount")));
        var money = Col("money", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("amount"), Field("depositRatio")));
        var percent = Col("percent", Bin(ReportConfigurationFormulaRules.NodeMultiply, Field("depositRatio"), Lit(2m)));

        Assert.Null(ReportConfigurationFormulaRules.DeriveUnit(ratio, Lookup));
        Assert.Equal("原币金额", ReportConfigurationFormulaRules.DeriveUnit(money, Lookup));
        Assert.Equal("%", ReportConfigurationFormulaRules.DeriveUnit(percent, Lookup));
    }

    [Fact]
    public void BuildEvidence_包含单位未知值口径与依赖()
    {
        var column = Col("ratio", Bin(ReportConfigurationFormulaRules.NodeDivide, Field("depositAmount"), Field("amount")));

        var evidence = Assert.Single(ReportConfigurationFormulaRules.BuildEvidence(new[] { column }, Lookup));

        Assert.Equal("ratio", evidence.Key);
        Assert.Equal(string.Empty, evidence.Unit);
        Assert.Equal(ReportConfigurationFormulaRules.UnknownReasonText, evidence.UnknownReason);
        Assert.Equal(new[] { "depositAmount", "amount" }, evidence.Dependencies);
    }

    private static string? Lookup(string key) => key switch
    {
        "amount" => "原币金额",
        "depositAmount" => "原币金额",
        "depositRatio" => "%",
        _ => null,
    };
}



