using System.Globalization;
using System.Text;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 迁移 parity 输出语义比较器（ERP-331 / ERP-333）：渲染通用平台 Excel/PDF 导出，并与旧来源接缝归一化的旧导出语义快照
/// 及真实旧产物字节做确定性只读比对，得出 <see cref="ReportMigrationOutputComparisonResultDto.OutputSemanticsMatched"/>。
/// <list type="bullet">
/// <item><b>渲染</b>：声明兼容 Excel 时经 <see cref="ReportConfigurationExcelExporter"/> 渲染；声明兼容 PDF 时经
/// <see cref="ReportConfigurationPdfExporter"/> + 共享 <see cref="SimHeiPdfFontResolver"/> 渲染（字体缺失显式失败）。</item>
/// <item><b>真实产物比对（ERP-333）</b>：解码实际旧 Excel/PDF 字节与通用 Excel/PDF 字节，比较有序列头、类型化 / null 单元格、
/// 公式安全字面标签、原始币种 / 单位标签与支持的布局语义；缺失旧字节 / 旧导出 / 字体、畸形或超大输出、不可提取格式一律 fail closed。</item>
/// <item><b>旧语义回退（ERP-331）</b>：未接线旧产物来源时保留旧语义快照比对，绝不使用通用导出器充当旧导出器、绝不接受调用方布尔凭据。</item>
/// </list>
/// <para>声明为不兼容的格式绝不强制比对；两者均不兼容（旧路由本就不产出 Excel/PDF）为真空匹配。纯只读：仅渲染 / 解码字节流，
/// 不写库、不执行任意 SQL、不扩权。</para>
/// </summary>
public sealed class ReportMigrationOutputSemanticsComparator : IReportMigrationOutputComparator
{
    private const int DefaultMaxArtifactBytes = 8 * 1024 * 1024;

    private static readonly object NullCellSentinel = new();

    private readonly int _maxArtifactBytes;

    public ReportMigrationOutputSemanticsComparator(int maxArtifactBytes = DefaultMaxArtifactBytes)
    {
        _maxArtifactBytes = maxArtifactBytes > 0 ? maxArtifactBytes : DefaultMaxArtifactBytes;
    }

    /// <inheritdoc />
    public ReportMigrationOutputComparisonResultDto Compare(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto? legacySnapshot,
        bool excelCompatible,
        bool pdfCompatible,
        string? fontPath = null,
        LegacyReportArtifactBytesDto? legacyArtifacts = null)
    {
        ArgumentNullException.ThrowIfNull(genericPreview);

        var evidence = new List<string>();

        // 旧导出不可用（旧来源接缝无快照）：fail closed，绝不猜测。
        if (legacySnapshot is null)
        {
            evidence.Add("旧导出不可用（legacy exporter unavailable）：无法取得旧导出语义快照，输出语义不匹配");
            return new ReportMigrationOutputComparisonResultDto(false, evidence);
        }

        var requiresComparison = excelCompatible || pdfCompatible;
        if (!requiresComparison)
        {
            // 两者均不兼容 → 旧路由本就不产出 Excel/PDF，输出语义为真空匹配。
            return new ReportMigrationOutputComparisonResultDto(true, evidence);
        }

        // 未接线旧产物来源 → 保留 ERP-331 旧语义快照比对（不破坏未迁移旧部署）。
        if (legacyArtifacts is null)
            return CompareSnapshotSemantics(genericPreview, legacySnapshot, excelCompatible, pdfCompatible, fontPath, evidence);

        var excelMatched = !excelCompatible || CompareExcelArtifact(genericPreview, legacySnapshot, legacyArtifacts.ExcelBytes, evidence);
        var pdfMatched = !pdfCompatible || ComparePdfArtifact(genericPreview, legacySnapshot, legacyArtifacts.PdfBytes, fontPath, evidence);

        return new ReportMigrationOutputComparisonResultDto(excelMatched && pdfMatched, evidence);
    }

    /// <summary>ERP-331 旧语义回退：仅渲染通用字节 + 旧语义快照比对（未接线旧产物来源时使用）。</summary>
    private static ReportMigrationOutputComparisonResultDto CompareSnapshotSemantics(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto legacySnapshot,
        bool excelCompatible,
        bool pdfCompatible,
        string? fontPath,
        List<string> evidence)
    {
        var excelMatched = !excelCompatible || RenderExcel(genericPreview, evidence);
        var pdfMatched = !pdfCompatible || RenderPdf(genericPreview, fontPath, evidence);
        var semanticsMatched = CompareSemantics(genericPreview, legacySnapshot, evidence);

        return new ReportMigrationOutputComparisonResultDto(excelMatched && pdfMatched && semanticsMatched, evidence);
    }

    /// <summary>渲染通用 Excel 导出（只读；任一异常即 fail closed）。</summary>
    private static bool RenderExcel(ReportConfigurationPreviewDto preview, List<string> evidence)
    {
        try
        {
            var bytes = new ReportConfigurationExcelExporter().Build(preview);
            if (bytes is null || bytes.Length == 0)
            {
                evidence.Add("通用 Excel 导出渲染结果为空");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            evidence.Add($"通用 Excel 导出渲染失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>渲染通用 PDF 导出（只读；字体缺失 / 渲染失败即 fail closed）。</summary>
    private static bool RenderPdf(ReportConfigurationPreviewDto preview, string? fontPath, List<string> evidence)
    {
        var resolved = string.IsNullOrWhiteSpace(fontPath)
            ? SimHeiPdfFontResolver.FindFontPath()
            : fontPath;

        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
        {
            evidence.Add("通用 PDF 导出失败：未找到中文字体 SimHei（黑体），输出语义不匹配（fail closed）");
            return false;
        }

        try
        {
            var bytes = ReportConfigurationPdfExporter.Export(preview, resolved);
            if (bytes is null || bytes.Length == 0)
            {
                evidence.Add("通用 PDF 导出渲染结果为空");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            evidence.Add($"通用 PDF 导出渲染失败：{ex.Message}");
            return false;
        }
    }

    private bool CompareExcelArtifact(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto legacySnapshot,
        byte[]? legacyBytes,
        List<string> evidence)
    {
        byte[] genericBytes;
        try
        {
            genericBytes = new ReportConfigurationExcelExporter().Build(genericPreview);
        }
        catch (Exception ex)
        {
            evidence.Add($"通用 Excel 导出渲染失败：{ex.Message}");
            return false;
        }

        if (genericBytes is null || genericBytes.Length == 0)
        {
            evidence.Add("通用 Excel 导出渲染结果为空");
            return false;
        }

        var genericArtifact = DecodeExcel(genericBytes, "通用 Excel", evidence);
        if (genericArtifact is null)
            return false;

        if (legacyBytes is null || legacyBytes.Length == 0)
        {
            evidence.Add("旧导出不可用：声明兼容 Excel 但未取得旧 Excel 产物字节（fail closed）");
            return false;
        }

        var legacyArtifact = DecodeExcel(legacyBytes, "旧 Excel", evidence);
        if (legacyArtifact is null)
            return false;

        return CompareExcelArtifacts(genericArtifact, legacyArtifact, genericPreview, legacySnapshot, evidence);
    }

    private bool ComparePdfArtifact(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto legacySnapshot,
        byte[]? legacyBytes,
        string? fontPath,
        List<string> evidence)
    {
        var resolved = ResolveFont(fontPath, evidence);
        if (resolved is null)
            return false;

        byte[] genericBytes;
        try
        {
            genericBytes = ReportConfigurationPdfExporter.Export(genericPreview, resolved);
        }
        catch (Exception ex)
        {
            evidence.Add($"通用 PDF 导出渲染失败：{ex.Message}");
            return false;
        }

        if (genericBytes is null || genericBytes.Length == 0)
        {
            evidence.Add("通用 PDF 导出渲染结果为空");
            return false;
        }

        var genericArtifact = DecodePdf(genericBytes, "通用 PDF", evidence);
        if (genericArtifact is null)
            return false;

        if (legacyBytes is null || legacyBytes.Length == 0)
        {
            evidence.Add("旧导出不可用：声明兼容 PDF 但未取得旧 PDF 产物字节（fail closed）");
            return false;
        }

        var legacyArtifact = DecodePdf(legacyBytes, "旧 PDF", evidence);
        if (legacyArtifact is null)
            return false;

        return ComparePdfArtifacts(genericArtifact, legacyArtifact, genericPreview, legacySnapshot, evidence);
    }

    private static string? ResolveFont(string? fontPath, List<string> evidence)
    {
        var resolved = string.IsNullOrWhiteSpace(fontPath)
            ? SimHeiPdfFontResolver.FindFontPath()
            : fontPath;

        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
        {
            evidence.Add("通用 PDF 导出失败：未找到中文字体 SimHei（黑体），输出语义不匹配（fail closed）");
            return null;
        }

        return resolved;
    }

    private static bool CompareExcelArtifacts(
        ReportMigrationArtifactDto generic,
        ReportMigrationArtifactDto legacy,
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto legacySnapshot,
        List<string> evidence)
    {
        var matched = true;
        var genericColumns = genericPreview.Columns ?? new List<ReportConfigurationColumnDto>();
        var legacyColumns = legacySnapshot.Columns ?? new List<ReportMigrationParityColumnDto>();

        if (generic.Headers.Count != legacy.Headers.Count)
        {
            matched = false;
            evidence.Add($"列数不一致：通用 {generic.Headers.Count} 列 vs 旧导出 {legacy.Headers.Count} 列");
        }

        var alignedColumnCount = Math.Min(generic.Headers.Count, legacy.Headers.Count);
        for (var c = 0; c < alignedColumnCount; c++)
        {
            if (!string.Equals(generic.Headers[c], legacy.Headers[c], StringComparison.Ordinal))
            {
                matched = false;
                evidence.Add($"第 {c} 列头不一致：通用 '{generic.Headers[c]}' vs 旧导出 '{legacy.Headers[c]}'");
            }
        }

        if (generic.Rows.Count != legacy.Rows.Count)
        {
            matched = false;
            evidence.Add($"行数不一致：通用 {generic.Rows.Count} 行 vs 旧导出 {legacy.Rows.Count} 行");
        }
        else
        {
            for (var r = 0; r < generic.Rows.Count; r++)
            {
                for (var c = 0; c < alignedColumnCount; c++)
                {
                    var declaredType = c < genericColumns.Count ? genericColumns[c].Type : ReportConfigurationConstants.TypeText;
                    var left = NormalizeCell(generic.Rows[r][c], declaredType);
                    var right = NormalizeCell(legacy.Rows[r][c], declaredType);

                    if (!Equals(left, right))
                    {
                        matched = false;
                        evidence.Add($"第 {r} 行第 {c} 列单元格不一致：通用 '{CellText(generic.Rows[r][c])}' vs 旧导出 '{CellText(legacy.Rows[r][c])}'");
                    }
                }
            }
        }

        for (var c = 0; c < alignedColumnCount; c++)
        {
            var genericCurrencyUnit = c < genericColumns.Count ? genericColumns[c].CurrencyUnit ?? string.Empty : string.Empty;
            var legacyCurrencyUnit = c < legacyColumns.Count ? (legacyColumns[c].Currency ?? string.Empty) + (legacyColumns[c].Unit ?? string.Empty) : string.Empty;

            if (!string.Equals(genericCurrencyUnit, legacyCurrencyUnit, StringComparison.Ordinal))
            {
                matched = false;
                evidence.Add($"第 {c} 列币种/单位标签不一致：通用 '{genericCurrencyUnit}' vs 旧导出 '{legacyCurrencyUnit}'");
            }
        }

        return matched;
    }

    private static bool ComparePdfArtifacts(
        ReportMigrationArtifactDto generic,
        ReportMigrationArtifactDto legacy,
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto legacySnapshot,
        List<string> evidence)
    {
        var matched = true;

        if (!generic.HasContent || !legacy.HasContent)
        {
            matched = false;
            evidence.Add("PDF 内容为空或不可提取（unextractable）");
        }

        if (!generic.EmbeddedFont || !legacy.EmbeddedFont)
        {
            matched = false;
            evidence.Add("PDF 未内嵌中文字体 SimHei（缺字 / 乱码风险，fail closed）");
        }

        if (!CompareSemantics(genericPreview, legacySnapshot, evidence))
            matched = false;

        if (!matched)
            return false;

        evidence.Add("Actual PDF cell/layout extraction is unavailable; input snapshots and embedded-font markers cannot prove output parity.");
        return false;
    }

    private ReportMigrationArtifactDto? DecodeExcel(byte[] bytes, string side, List<string> evidence)
    {
        if (bytes is null || bytes.Length == 0)
        {
            evidence.Add($"{side} 为空");
            return null;
        }

        if (bytes.Length > _maxArtifactBytes)
        {
            evidence.Add($"{side} 超限（oversized）：{bytes.Length} 字节，拒绝比对（fail closed）");
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var workbook = new XSSFWorkbook(stream);
            if (workbook.NumberOfSheets == 0)
            {
                evidence.Add($"{side} 无工作表（unextractable）");
                return null;
            }

            var sheet = workbook.GetSheetAt(0);
            if (sheet.LastRowNum > 200)
            {
                evidence.Add($"{side} exceeds the 200-row comparison boundary; narrow the query without truncating proof.");
                return null;
            }
            var headers = new List<string>();
            var columnCount = 0;
            var headerRow = sheet.GetRow(0);
            if (headerRow is not null)
            {
                columnCount = headerRow.LastCellNum;
                for (var c = 0; c < columnCount; c++)
                    headers.Add(ReadHeaderText(headerRow.GetCell(c)));
            }

            var rows = new List<IReadOnlyList<ReportMigrationArtifactCellDto>>();
            for (var r = 1; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r);
                if (row is null)
                    continue;

                var cells = new List<ReportMigrationArtifactCellDto>(columnCount);
                for (var c = 0; c < columnCount; c++)
                    cells.Add(DecodeCell(row.GetCell(c)));
                rows.Add(cells);
            }

            return new ReportMigrationArtifactDto(
                ReportMigrationArtifactFormat.Excel,
                headers,
                rows,
                PageCount: 0,
                EmbeddedFont: false,
                HasContent: sheet.LastRowNum >= 1);
        }
        catch (Exception ex)
        {
            evidence.Add($"{side} 解码失败（malformed / unextractable）：{ex.Message}");
            return null;
        }
    }

    private ReportMigrationArtifactDto? DecodePdf(byte[] bytes, string side, List<string> evidence)
    {
        if (bytes is null || bytes.Length == 0)
        {
            evidence.Add($"{side} 为空");
            return null;
        }

        if (bytes.Length > _maxArtifactBytes)
        {
            evidence.Add($"{side} 超限（oversized）：{bytes.Length} 字节，拒绝比对（fail closed）");
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

            var pageCount = document.PageCount;
            var ascii = Encoding.ASCII.GetString(bytes);
            var embeddedFont = ascii.Contains("SimHei", StringComparison.Ordinal)
                && ascii.Contains("FontFile2", StringComparison.Ordinal);

            var hasContent = false;
            for (var i = 0; i < pageCount; i++)
            {
                if (document.Pages[i].Contents.Elements.Count > 0)
                {
                    hasContent = true;
                    break;
                }
            }

            return new ReportMigrationArtifactDto(
                ReportMigrationArtifactFormat.Pdf,
                Array.Empty<string>(),
                Array.Empty<IReadOnlyList<ReportMigrationArtifactCellDto>>(),
                pageCount,
                embeddedFont,
                hasContent);
        }
        catch (Exception ex)
        {
            evidence.Add($"{side} 解码失败（malformed / unextractable）：{ex.Message}");
            return null;
        }
    }

    private static string ReadHeaderText(ICell? cell)
    {
        if (cell is null)
            return string.Empty;

        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => cell.NumericCellValue.ToString("0.###############", CultureInfo.InvariantCulture),
            CellType.Boolean => cell.BooleanCellValue ? "是" : "否",
            CellType.Formula => cell.CellFormula ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static ReportMigrationArtifactCellDto DecodeCell(ICell? cell)
    {
        if (cell is null || cell.CellType == CellType.Blank || cell.CellType == CellType.Unknown)
            return new ReportMigrationArtifactCellDto(null, true, "blank");

        switch (cell.CellType)
        {
            case CellType.Numeric:
                return new ReportMigrationArtifactCellDto((decimal)cell.NumericCellValue, false, "number");

            case CellType.Boolean:
                return new ReportMigrationArtifactCellDto(cell.BooleanCellValue, false, "boolean");

            case CellType.String:
            {
                var text = cell.StringCellValue;
                if (string.IsNullOrEmpty(text))
                    return new ReportMigrationArtifactCellDto(null, true, "text");
                return new ReportMigrationArtifactCellDto(text, false, "text");
            }

            case CellType.Formula:
                return new ReportMigrationArtifactCellDto(cell.CellFormula ?? string.Empty, false, "formula");

            default:
                return new ReportMigrationArtifactCellDto(null, true, cell.CellType.ToString());
        }
    }

    private static object? NormalizeCell(ReportMigrationArtifactCellDto cell, string declaredType)
    {
        if (cell is null || cell.IsNull)
            return NullCellSentinel;

        var value = cell.Value;
        return declaredType switch
        {
            ReportConfigurationConstants.TypeNumber => TryDecimal(value) is { } number ? number : InvariantText(value),
            ReportConfigurationConstants.TypeDate => TryDateTime(value) is { } date ? date : InvariantText(value),
            ReportConfigurationConstants.TypeBoolean => TryBoolean(value) is { } boolean ? boolean : InvariantText(value),
            _ => InvariantText(value),
        };
    }

    private static string CellText(ReportMigrationArtifactCellDto cell)
        => cell is null || cell.IsNull ? "<null>" : InvariantText(cell.Value);

    private static string InvariantText(object? value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal? TryDecimal(object? value) => value switch
    {
        decimal d => d,
        double db => (decimal)db,
        float f => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        string str when decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static bool? TryBoolean(object? value) => value switch
    {
        bool b => b,
        string str when bool.TryParse(str, out var parsed) => parsed,
        _ => null,
    };

    private static DateTime? TryDateTime(object? value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.DateTime,
        string str when DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>列头 / 有序行单元格值 / 币种单位标签的确定性比对（任一即整体失败）。</summary>
    private static bool CompareSemantics(
        ReportConfigurationPreviewDto generic,
        ReportMigrationParitySnapshotDto legacy,
        List<string> evidence)
    {
        var genericColumns = generic.Columns ?? new List<ReportConfigurationColumnDto>();
        var legacyColumns = legacy.Columns ?? new List<ReportMigrationParityColumnDto>();
        var genericRows = generic.Rows ?? new List<Dictionary<string, object?>>();
        var legacyRows = legacy.Rows ?? new List<ReportMigrationParityRowDto>();

        var matched = true;

        // 1) 列头（有序、按位置的稳定列键）。
        if (genericColumns.Count != legacyColumns.Count)
        {
            matched = false;
            evidence.Add($"列数不一致：通用 {genericColumns.Count} 列 vs 旧导出 {legacyColumns.Count} 列");
        }

        var alignedColumnCount = Math.Min(genericColumns.Count, legacyColumns.Count);
        for (var c = 0; c < alignedColumnCount; c++)
        {
            if (!string.Equals(genericColumns[c].Key, legacyColumns[c].Key, StringComparison.Ordinal))
            {
                matched = false;
                evidence.Add($"第 {c} 列头不一致：通用 '{genericColumns[c].Key}' vs 旧导出 '{legacyColumns[c].Key}'");
            }
        }

        // 2) 有序行单元格值（复用导出器共用的类型化文本口径：null 留空、布尔 是/否、日期 yyyy-MM-dd、数值 0.##）。
        if (genericRows.Count != legacyRows.Count)
        {
            matched = false;
            evidence.Add($"行数不一致：通用 {genericRows.Count} 行 vs 旧导出 {legacyRows.Count} 行");
        }
        else
        {
            for (var r = 0; r < genericRows.Count; r++)
            {
                for (var c = 0; c < alignedColumnCount; c++)
                {
                    var genericCell = ReportConfigurationPdfExporter.FormatCellValue(genericColumns[c], genericRows[r]);
                    var legacyCell = c < legacyRows[r].Cells.Count
                        ? ReportConfigurationPdfExporter.FormatCellValue(legacyRows[r].Cells[c])
                        : string.Empty;

                    if (!string.Equals(genericCell, legacyCell, StringComparison.Ordinal))
                    {
                        matched = false;
                        evidence.Add($"第 {r} 行第 {c} 列单元格值不一致：通用 '{genericCell}' vs 旧导出 '{legacyCell}'");
                    }
                }
            }
        }

        // 3) 币种 / 单位标签（按列、有序；通用为组合标签 CurrencyUnit，旧导出为 Currency + Unit）。
        for (var c = 0; c < alignedColumnCount; c++)
        {
            var genericCurrencyUnit = genericColumns[c].CurrencyUnit ?? string.Empty;
            var legacyCurrencyUnit = (legacyColumns[c].Currency ?? string.Empty) + (legacyColumns[c].Unit ?? string.Empty);

            if (!string.Equals(genericCurrencyUnit, legacyCurrencyUnit, StringComparison.Ordinal))
            {
                matched = false;
                evidence.Add($"第 {c} 列币种/单位标签不一致：通用 '{genericCurrencyUnit}' vs 旧导出 '{legacyCurrencyUnit}'");
            }
        }

        return matched;
    }
}
