using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 迁移 parity 输出语义比较器（ERP-331）：渲染通用平台 Excel/PDF 导出，并与旧来源接缝归一化的旧导出语义快照
/// 做确定性只读比对，得出 <see cref="ReportMigrationOutputComparisonResultDto.OutputSemanticsMatched"/>。
/// <list type="bullet">
/// <item><b>渲染</b>：声明兼容 Excel 时经 <see cref="ReportConfigurationExcelExporter"/> 渲染；声明兼容 PDF 时经
/// <see cref="ReportConfigurationPdfExporter"/> + 共享 <see cref="SimHeiPdfFontResolver"/> 渲染（字体缺失显式失败）。</item>
/// <item><b>比对</b>：列头（有序、按位置的稳定列键）、有序行单元格值（复用导出器共用的类型化文本口径）与
/// 币种 / 单位标签（通用列 <c>CurrencyUnit</c> vs 旧导出列 <c>Currency</c> + <c>Unit</c>），任一分歧即该维度失败。</item>
/// <item><b>fail closed</b>：字体缺失 / 旧导出不可用（<paramref name="legacySnapshot"/> 为 null）/ 渲染失败一律不匹配，
/// 并携带显式证据，绝不猜测。</item>
/// </list>
/// <para>声明为不兼容的格式绝不强制比对；两者均不兼容（旧路由本就不产出 Excel/PDF）为真空匹配，与登记册的
/// <c>CompatibilityDeclared</c> 口径一致。纯只读：仅渲染字节流与比较内存语义，不写库、不执行任意 SQL、不扩权。</para>
/// </summary>
public sealed class ReportMigrationOutputSemanticsComparator : IReportMigrationOutputComparator
{
    /// <inheritdoc />
    public ReportMigrationOutputComparisonResultDto Compare(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto? legacySnapshot,
        bool excelCompatible,
        bool pdfCompatible,
        string? fontPath = null)
    {
        ArgumentNullException.ThrowIfNull(genericPreview);

        var evidence = new List<string>();

        // 旧导出不可用（旧来源接缝无快照）：fail closed，绝不猜测。
        if (legacySnapshot is null)
        {
            evidence.Add("旧导出不可用（legacy exporter unavailable）：无法取得旧导出语义快照，输出语义不匹配");
            return new ReportMigrationOutputComparisonResultDto(false, evidence);
        }

        // 仅渲染声明为兼容的格式；声明不兼容的格式绝不强制比对。
        var excelMatched = !excelCompatible || RenderExcel(genericPreview, evidence);
        var pdfMatched = !pdfCompatible || RenderPdf(genericPreview, fontPath, evidence);

        // 两者均不兼容 → 旧路由本就不产出 Excel/PDF，输出语义为真空匹配；否则必须逐列 / 逐行一致。
        var requiresComparison = excelCompatible || pdfCompatible;
        var semanticsMatched = !requiresComparison || CompareSemantics(genericPreview, legacySnapshot, evidence);

        return new ReportMigrationOutputComparisonResultDto(
            excelMatched && pdfMatched && semanticsMatched,
            evidence);
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
