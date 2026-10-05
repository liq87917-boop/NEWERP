using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-333 实际产物 parity 单元测试：覆盖有界旧产物来源 fail closed（未知 / 空白键、无身份）、
/// 实际 Excel 产物解码比对（匹配 / 分歧 / 原始 null 与零区分 / 公式安全标签 / 缺失旧字节 / 畸形 / 超大）、
/// 以及原始币种 / 单位标签的隔离比对。全部为纯离线对象，不连接 SQL Server、不执行任何 SQL / seed。
/// </summary>
public class ReportMigrationArtifactParityTests
{
    private const string Text = ReportConfigurationConstants.TypeText;
    private const string Number = ReportConfigurationConstants.TypeNumber;

    private readonly ReportMigrationOutputSemanticsComparator _comparator = new();

    [Fact]
    public void Pdf_with_different_actual_cells_cannot_pass_from_matching_input_snapshots()
    {
        var generic = Generic(new[] { ("orderNo", "Order", Text, (string?)null) }, new[] { new object?[] { "EXPECTED" } });
        var wrong = Generic(new[] { ("orderNo", "Order", Text, (string?)null) }, new[] { new object?[] { "DIFFERENT" } });
        var snapshot = Legacy(new[] { ("orderNo", (string?)null, (string?)null) }, new[] { new object?[] { "EXPECTED" } });
        var bytes = ReportConfigurationPdfExporter.Export(wrong, @"C:\Windows\Fonts\simhei.ttf");
        var result = _comparator.Compare(generic, snapshot, false, true,
            fontPath: @"C:\Windows\Fonts\simhei.ttf", legacyArtifacts: new(null, bytes));
        Assert.False(result.OutputSemanticsMatched);
    }

    [Fact]
    public void Actual_excel_over_200_rows_cannot_pass_the_bounded_comparison()
    {
        var rows = Enumerable.Range(1, 201).Select(i => new object?[] { "ROW-" + i }).ToArray();
        var generic = Generic(new[] { ("orderNo", "Order", Text, (string?)null) }, rows);
        var snapshot = Legacy(new[] { ("orderNo", (string?)null, (string?)null) }, rows);
        var bytes = LegacyExcel(new[] { ("orderNo", "Order") }, rows);
        var result = _comparator.Compare(generic, snapshot, true, false,
            legacyArtifacts: new(bytes, null));
        Assert.False(result.OutputSemanticsMatched);
    }

    [Fact]
    public void Highly_compressed_excel_xml_is_rejected_before_workbook_materialization()
    {
        var rows = new[] { new object?[] { "SAFE" } };
        var generic = Generic(new[] { ("orderNo", "Order", Text, (string?)null) }, rows);
        var snapshot = Legacy(new[] { ("orderNo", (string?)null, (string?)null) }, rows);
        using var buffer = new MemoryStream();
        var original = LegacyExcel(new[] { ("orderNo", "Order") }, rows);
        buffer.Write(original);
        using (var archive = new System.IO.Compression.ZipArchive(buffer,
            System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!;
            string xml;
            using (var reader = new StreamReader(sheet.Open())) xml = reader.ReadToEnd();
            sheet.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open());
            var declarationEnd = xml.IndexOf("?>", StringComparison.Ordinal) + 2;
            writer.Write(xml[..declarationEnd]);
            var spaces = new string(' ', 1024 * 1024);
            for (var i = 0; i < 65; i++) writer.Write(spaces);
            writer.Write(xml[declarationEnd..]);
        }
        var result = _comparator.Compare(generic, snapshot, true, false,
            legacyArtifacts: new(buffer.ToArray(), null));
        Assert.False(result.OutputSemanticsMatched);
    }

    // ==================== 脚手架 ====================

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

    private static byte[] LegacyExcel((string Key, string Title)[] columns, object?[][] rows)
    {
        var exportRows = rows.Select(r =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Length; i++)
                dict[columns[i].Key] = r[i];
            return DynamicSalesOrderReportRules.BuildExportRow(dict);
        }).ToList();

        return ExcelExporter.ExportRows("销售订单", exportRows, columns.ToList());
    }

    // ==================== 1. 有界旧产物来源 fail closed ====================

    [Fact]
    public async Task LegacyReportArtifactSource_空白未知键无身份_返回null()
    {
        var source = new LegacyReportArtifactSource(new ThrowingSalesOrderQuery());

        Assert.Null(await source.ReadArtifactsAsync(new LegacyReportSourceRequest { LegacyKey = null }));
        Assert.Null(await source.ReadArtifactsAsync(new LegacyReportSourceRequest { LegacyKey = "   " }));
        Assert.Null(await source.ReadArtifactsAsync(new LegacyReportSourceRequest { LegacyKey = "unknown:legacy-key", UserId = 1 }));
        Assert.Null(await source.ReadArtifactsAsync(new LegacyReportSourceRequest { LegacyKey = "dynamic:sales-order", UserId = null }));
        Assert.Null(await source.ReadArtifactsAsync(new LegacyReportSourceRequest { LegacyKey = "dynamic:sales-order", UserId = 0 }));
    }

    // ==================== 2. 实际 Excel 产物匹配 ====================

    [Fact]
    public void Compare_实际Excel产物_列头单元格全部一致_匹配()
    {
        var generic = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null), ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var legacy = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null), ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var legacyBytes = LegacyExcel(
            new[] { ("orderNo", "订单号"), ("amount", "金额") },
            new object?[][] { new object?[] { "SO-1", 10.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(legacyBytes, null));

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }

    // ==================== 3. 分歧 ====================

    [Fact]
    public void Compare_实际Excel产物_单元格值分歧_不匹配()
    {
        var generic = Generic(
            new[] { ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { 20.5m } });

        var legacyBytes = LegacyExcel(
            new[] { ("amount", "金额") },
            new object?[][] { new object?[] { 20.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(legacyBytes, null));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("单元格"));
    }

    [Fact]
    public void Compare_实际Excel产物_原始null与零严格区分_不匹配()
    {
        var generic = Generic(
            new[] { ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { null } });

        var legacy = Legacy(
            new[] { ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { 0m } });

        var legacyBytes = LegacyExcel(
            new[] { ("amount", "金额") },
            new object?[][] { new object?[] { 0m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(legacyBytes, null));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("单元格"));
    }

    // ==================== 4. 公式安全标签 ====================

    [Fact]
    public void Compare_实际Excel产物_公式安全标签一致_匹配()
    {
        var generic = Generic(
            new[] { ("orderNo", "订单号", Text, (string?)null) },
            new object?[][] { new object?[] { "=1+1" } });

        var legacy = Legacy(
            new[] { ("orderNo", (string?)null, (string?)null) },
            new object?[][] { new object?[] { "=1+1" } });

        var legacyBytes = LegacyExcel(
            new[] { ("orderNo", "订单号") },
            new object?[][] { new object?[] { "=1+1" } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(legacyBytes, null));

        Assert.True(result.OutputSemanticsMatched);
        Assert.Empty(result.Evidence);
    }


    // ==================== 5. 币种 / 单位标签隔离 ====================

    [Fact]
    public void Compare_实际Excel产物_币种单位标签不一致_不匹配()
    {
        var generic = Generic(
            new[] { ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { ("amount", (string?)"USD", (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var legacyBytes = LegacyExcel(
            new[] { ("amount", "金额") },
            new object?[][] { new object?[] { 10.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(legacyBytes, null));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("币种/单位"));
    }

    // ==================== 6. fail closed ====================

    [Fact]
    public void Compare_声明兼容Excel但缺失旧字节_不匹配()
    {
        var generic = Generic(
            new[] { ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(null, null));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("旧 Excel"));
    }

    [Fact]
    public void Compare_旧Excel畸形_不匹配()
    {
        var generic = Generic(
            new[] { ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var result = _comparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(new byte[] { 1, 2, 3, 4 }, null));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("malformed") || e.Contains("解码失败"));
    }

    [Fact]
    public void Compare_旧Excel超大_不匹配()
    {
        var generic = Generic(
            new[] { ("amount", "金额", Number, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var legacy = Legacy(
            new[] { ("amount", (string?)null, (string?)null) },
            new object?[][] { new object?[] { 10.5m } });

        var smallComparator = new ReportMigrationOutputSemanticsComparator(maxArtifactBytes: 16);
        var result = smallComparator.Compare(generic, legacy, excelCompatible: true, pdfCompatible: false,
            legacyArtifacts: new LegacyReportArtifactBytesDto(new byte[32], null));

        Assert.False(result.OutputSemanticsMatched);
        Assert.Contains(result.Evidence, e => e.Contains("超限") || e.Contains("oversized"));
    }

    private sealed class ThrowingSalesOrderQuery : IDynamicSalesOrderReportQuery
    {
        public Task<DynamicSalesOrderReportCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DynamicSalesOrderReportPageDto> PreviewAsync(DynamicSalesOrderReportRequest request, long? userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

