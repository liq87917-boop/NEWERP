using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using PdfSharp.Pdf;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-336 PDF 实际产物解码器单元测试：用真实生成的 PDF 字节证明
/// 有序列头 / 有序类型化 / null 单元格 / 币种单位标签 / 页·列带·行延续语义能被解码，
/// 且加密 / 畸形 / 超大 / 不可提取一律 fail closed。
/// </summary>
public class ReportMigrationPdfArtifactDecoderTests
{
    private const string Text = ReportConfigurationConstants.TypeText;
    private const string Number = ReportConfigurationConstants.TypeNumber;
    private const string Font = @"C:\Windows\Fonts\simhei.ttf";

    private readonly ReportMigrationPdfArtifactDecoder _decoder = new();
    private readonly ReportMigrationOutputSemanticsComparator _comparator = new();

    [Fact]
    public void Decode_真实PDF_提取有序列头与null单元格()
    {
        var preview = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null), ("amount", "金额", Number, "USD") },
            new object?[][] { new object?[] { "SO-1", 10.5m }, new object?[] { "SO-2", null } });

        var bytes = ReportConfigurationPdfExporter.Export(preview, Font);

        var evidence = new List<string>();
        var artifact = _decoder.Decode(bytes, "PDF", evidence);

        Assert.NotNull(artifact);
        Assert.Empty(evidence);
        Assert.Equal(new[] { "订单号", "金额（USD）" }, artifact!.Headers);
        Assert.Equal(2, artifact.Rows.Count);

        Assert.Equal("SO-1", artifact.Rows[0][0].Text);
        Assert.False(artifact.Rows[0][0].IsNull);
        Assert.Equal("10.5", artifact.Rows[0][1].Text);
        Assert.False(artifact.Rows[0][1].IsNull);

        Assert.Equal("SO-2", artifact.Rows[1][0].Text);
        Assert.True(artifact.Rows[1][1].IsNull); // 原始 null 绝不回落为 0
    }

    [Fact]
    public void Compare_相同PDF_输出语义匹配()
    {
        var preview = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null), ("amount", "金额", Number, "USD") },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var snapshot = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null), ("amount", "USD", (string?)null) },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var bytes = ReportConfigurationPdfExporter.Export(preview, Font);

        var result = _comparator.Compare(preview, snapshot, excelCompatible: false, pdfCompatible: true,
            fontPath: Font, legacyArtifacts: new LegacyReportArtifactBytesDto(null, bytes));

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void Compare_实际单元格值分歧_不匹配()
    {
        var generic = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            new object?[][] { new object?[] { "EXPECTED" } });

        var wrong = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            new object?[][] { new object?[] { "DIFFERENT" } });

        var snapshot = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null) },
            new object?[][] { new object?[] { "EXPECTED" } });

        var bytes = ReportConfigurationPdfExporter.Export(wrong, Font);

        var result = _comparator.Compare(generic, snapshot, excelCompatible: false, pdfCompatible: true,
            fontPath: Font, legacyArtifacts: new LegacyReportArtifactBytesDto(null, bytes));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("单元格"));
    }

    [Fact]
    public void Compare_缺失列头_不匹配()
    {
        var generic = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            new object?[][] { new object?[] { "SO-1" } });

        var missingHeader = Generic(
            new[] { ("orderNo", "订单编号", Text, (string?)null) },
            new object?[][] { new object?[] { "SO-1" } });

        var snapshot = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null) },
            new object?[][] { new object?[] { "SO-1" } });

        var bytes = ReportConfigurationPdfExporter.Export(missingHeader, Font);

        var result = _comparator.Compare(generic, snapshot, excelCompatible: false, pdfCompatible: true,
            fontPath: Font, legacyArtifacts: new LegacyReportArtifactBytesDto(null, bytes));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("列头"));
    }

    [Fact]
    public void Compare_缺失整列_不匹配()
    {
        var generic = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null), ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var missingColumn = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            new object?[][] { new object?[] { "SO-1" } });

        var snapshot = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null), ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var bytes = ReportConfigurationPdfExporter.Export(missingColumn, Font);

        var result = _comparator.Compare(generic, snapshot, excelCompatible: false, pdfCompatible: true,
            fontPath: Font, legacyArtifacts: new LegacyReportArtifactBytesDto(null, bytes));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("列数") || e.Contains("列头"));
    }


    [Fact]
    public void Compare_跨页行截断_不匹配()
    {
        var manyRows = Enumerable.Range(1, 80).Select(i => new object?[] { "SO-" + i }).ToArray();
        var fewerRows = Enumerable.Range(1, 40).Select(i => new object?[] { "SO-" + i }).ToArray();

        var generic = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            manyRows);

        var truncated = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            fewerRows);

        var snapshot = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null) },
            manyRows);

        var bytes = ReportConfigurationPdfExporter.Export(truncated, Font);

        var result = _comparator.Compare(generic, snapshot, excelCompatible: false, pdfCompatible: true,
            fontPath: Font, legacyArtifacts: new LegacyReportArtifactBytesDto(null, bytes));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("行数"));
    }

    [Fact]
    public void Decode_畸形字节_返回null并记录malformed()
    {
        var evidence = new List<string>();
        var artifact = _decoder.Decode(new byte[] { 1, 2, 3, 4 }, "PDF", evidence);

        Assert.Null(artifact);
        Assert.Contains(evidence, e => e.Contains("malformed") || e.Contains("解码失败"));
    }

    [Fact]
    public void Decode_超大字节_返回null并记录oversized()
    {
        var decoder = new ReportMigrationPdfArtifactDecoder(maxArtifactBytes: 16);
        var evidence = new List<string>();
        var artifact = decoder.Decode(new byte[32], "PDF", evidence);

        Assert.Null(artifact);
        Assert.Contains(evidence, e => e.Contains("超限") || e.Contains("oversized"));
    }

    [Fact]
    public void Decode_加密PDF_拒绝提取()
    {
        var encrypted = BuildEncryptedPdf();
        var evidence = new List<string>();
        var artifact = _decoder.Decode(encrypted, "PDF", evidence);

        Assert.Null(artifact);
        Assert.NotEmpty(evidence);
    }

    private static byte[] BuildEncryptedPdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        document.SecuritySettings.UserPassword = "user";
        document.SecuritySettings.OwnerPassword = "owner";
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static ReportConfigurationPreviewDto Generic(
        (string Key, string Label, string Type, string? CurrencyUnit)[] columns,
        object?[][] rows)
        => new()
        {
            Name = "通用预览",
            Columns = columns
                .Select(c => new ReportConfigurationColumnDto(c.Key, c.Label, c.Type, c.CurrencyUnit))
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
}

