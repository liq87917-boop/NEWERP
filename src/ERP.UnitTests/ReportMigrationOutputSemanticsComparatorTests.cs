using ERP.Application.DTOs;
using ERP.Infrastructure.Export;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-331 输出语义比较器单元测试：覆盖列头不一致、单元格值不一致、币种 / 单位标签不一致、
/// 字体缺失 fail closed、旧导出不可用 fail closed、声明不兼容绝不强制比对，以及全量匹配（Excel / PDF）。
/// <para>全部为纯离线对象，不连接 SQL Server、不执行任何 SQL / seed；PDF 正向断言在无字体环境自动跳过。</para>
/// </summary>
public class ReportMigrationOutputSemanticsComparatorTests
{
    private const string Text = ReportConfigurationConstants.TypeText;

    private readonly ReportMigrationOutputSemanticsComparator _comparator = new();

    // ==================== 脚手架 ====================

    private static (string Key, string Label, string? CurrencyUnit) GCol(string key, string label, string? currencyUnit = null)
        => (key, label, currencyUnit);

    private static (string Key, string? Currency, string? Unit) LCol(string key, string? currency = null, string? unit = null)
        => (key, currency, unit);

    private static ReportConfigurationPreviewDto Generic(
        (string Key, string Label, string? CurrencyUnit)[] columns,
        object?[][] rows)
        => new()
        {
            Name = "通用预览",
            Columns = columns
                .Select(c => new ReportConfigurationColumnDto(c.Key, c.Label, Text, c.CurrencyUnit))
                .ToList(),
            Rows = rows.Select(r =>
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < columns.Length; i++)
                    dict[columns[i].Key] = r[i];
                return dict;
            }).ToList(),
        };

    private static ReportMigrationParitySnapshotDto Legacy(
        (string Key, string? Currency, string? Unit)[] columns,
        object?[][] rows)
        => new(
            columns
                .Select(c => new ReportMigrationParityColumnDto(c.Key, Text, c.Currency, c.Unit))
                .ToList(),
            rows.Select((r, i) => new ReportMigrationParityRowDto(
                new[] { $"row:{i}" }, null, null, r)).ToList(),
            new ReportMigrationParityPermissionsDto(Array.Empty<long>(), "global"));

    // ==================== 1. 全量匹配 ====================

    [Fact]
    public void Compare_列头单元格币种单位全部一致_Excel兼容_匹配()
    {
        var generic = Generic(
            new[] { GCol("orderNo", "订单号"), GCol("amount", "金额", "原币金额") },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var legacy = Legacy(
            new[] { LCol("orderNo"), LCol("amount", "原币", "金额") },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false);

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void Compare_字体可用_Pdf兼容_全部一致_匹配()
    {
        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is null)
            return; // 无字体环境：跳过正向 PDF 断言（fail closed 由字体缺失测试覆盖）

        var generic = Generic(
            new[] { GCol("orderNo", "订单号") },
            new object?[][] { new object?[] { "SO-1" } });

        var legacy = Legacy(
            new[] { LCol("orderNo") },
            new object?[][] { new object?[] { "SO-1" } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: false, pdfCompatible: true, fontPath);

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }

    // ==================== 2. 三个比对维度各自失败 ====================

    [Fact]
    public void Compare_列头不一致_不匹配()
    {
        var generic = Generic(
            new[] { GCol("orderNo", "订单号") },
            new object?[][] { new object?[] { "SO-1" } });

        var legacy = Legacy(
            new[] { LCol("invoiceNo") },
            new object?[][] { new object?[] { "SO-1" } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false);

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("列头"));
    }

    [Fact]
    public void Compare_单元格值不一致_不匹配()
    {
        var generic = Generic(
            new[] { GCol("amount", "金额") },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { LCol("amount") },
            new object?[][] { new object?[] { 20.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false);

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("单元格"));
    }

    [Fact]
    public void Compare_币种单位标签不一致_不匹配()
    {
        var generic = Generic(
            new[] { GCol("amount", "金额", "原币金额") },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { LCol("amount", "USD") },
            new object?[][] { new object?[] { 10.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false);

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("币种/单位"));
    }

    // ==================== 3. fail closed ====================

    [Fact]
    public void Compare_字体缺失_Pdf兼容_不匹配且带证据()
    {
        var generic = Generic(
            new[] { GCol("orderNo", "订单号") },
            new object?[][] { new object?[] { "SO-1" } });

        var legacy = Legacy(
            new[] { LCol("orderNo") },
            new object?[][] { new object?[] { "SO-1" } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: false, pdfCompatible: true,
            fontPath: @"C:\definitely-missing\simhei.ttf");

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("SimHei") || e.Contains("字体"));
    }

    [Fact]
    public void Compare_旧导出不可用_不匹配且带证据()
    {
        var generic = Generic(
            new[] { GCol("orderNo", "订单号") },
            Array.Empty<object?[]>());

        var result = _comparator.Compare(generic, legacySnapshot: null, excelCompatible: true, pdfCompatible: false);

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("旧导出"));
    }

    // ==================== 4. 声明兼容性口径 ====================

    [Fact]
    public void Compare_双方都不兼容_列头分歧也不强制失败()
    {
        var generic = Generic(
            new[] { GCol("a", "甲") },
            new object?[][] { new object?[] { "x" } });

        var legacy = Legacy(
            new[] { LCol("b") },
            new object?[][] { new object?[] { "x" } });

        // 两者均不兼容：旧路由本就不产出 Excel/PDF，输出语义为真空匹配，绝不强制比对列头。
        var result = _comparator.Compare(generic, legacy, excelCompatible: false, pdfCompatible: false);

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void Compare_仅Excel兼容_Pdf不兼容_只要求Excel渲染与语义一致()
    {
        var generic = Generic(
            new[] { GCol("orderNo", "订单号") },
            new object?[][] { new object?[] { "SO-1" } });

        var legacy = Legacy(
            new[] { LCol("orderNo") },
            new object?[][] { new object?[] { "SO-1" } });

        // PDF 声明不兼容：即便字体缺失也不应阻断（绝不强制比对 PDF）。
        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false, fontPath: null);

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }
}
