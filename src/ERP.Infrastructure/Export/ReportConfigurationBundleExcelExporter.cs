using ERP.Application.DTOs;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用报表配置捆绑（ERP-307 Stage 2）Excel 导出器：无状态、无数据集特化分派，只消费统一的
/// <see cref="ReportConfigurationBundleExportResultDto"/>，生成一个多工作表工作簿（每节一个数据工作表 +
/// 一个「报表口径」来源工作表）。全部节都已授权、已校验后才渲染；任何一节被拒绝 / 撤销即整体失败。
/// <para>口径与既有 <see cref="ReportConfigurationExcelExporter"/> 对齐：每节保留选定列顺序与类型化值
/// （数值保留符号、null 未知留空、日期 / 布尔按类型呈现、文本做公式注入转义）；每节标题稳定 / 安全、字段顺序
/// 稳定；「报表口径」始终包含节序号 / 标题 / 定义名称与版本 / 数据集 / 行粒度 / 规范化查询筛选与日期范围 /
/// 覆盖口径 / 币种单位语义 / 只读与边界 / 未知值说明，并保留各节独立币种 / 单位 / 粒度 / 证据边界，
/// 绝不跨节合并或换算。全程只读：仅生成 xlsx 字节流，不写库、不执行任意 SQL。</para>
/// </summary>
public sealed class ReportConfigurationBundleExcelExporter
{
    /// <summary>来源工作表名（复用既有导出器的口径工作表名）</summary>
    public const string ProvenanceSheetName = ReportConfigurationExcelExporter.ContextSheetName;

    /// <summary>未知值说明（复用既有导出器口径：null 在数据中留空，绝不写成 0）</summary>
    public const string UnknownValueText = ReportConfigurationExcelExporter.UnknownValueText;

    /// <summary>生成捆绑工作簿（只读）。</summary>
    public byte[] Build(ReportConfigurationBundleExportResultDto bundle)
        => Build(bundle, CancellationToken.None);

    /// <summary>生成捆绑工作簿（只读；可传播联动取消令牌到渲染循环）。</summary>
    public byte[] Build(ReportConfigurationBundleExportResultDto bundle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();

        using var workbook = new XSSFWorkbook();
        var styles = CreateStyles(workbook);

        foreach (var section in bundle.Sections ?? new List<ReportConfigurationBundleSectionExportDto>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildSectionSheet(workbook, section, styles);
        }

        BuildProvenanceSheet(workbook, bundle, styles);
        cancellationToken.ThrowIfCancellationRequested();

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    private static void BuildSectionSheet(
        XSSFWorkbook workbook,
        ReportConfigurationBundleSectionExportDto section,
        Styles styles)
    {
        var sheet = workbook.CreateSheet(SafeSectionSheetName(section.Ordinal, section.Title));
        var columns = section.Preview?.Columns ?? new List<ReportConfigurationColumnDto>();
        var rows = section.Facts is { Count: > 0 }
            ? section.Facts
            : (section.Preview?.Rows ?? new List<Dictionary<string, object?>>());

        // 表头行：真实中文标签 + 可选币种 / 单位语义（与既有导出器一致）
        var header = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = header.CreateCell(c);
            cell.SetCellValue(EscapeFormulaLeading(HeaderText(columns[c])));
            cell.CellStyle = styles.Header;
        }

        // 数据行：按选定列顺序、类型化写入；null 留空
        for (var r = 0; r < rows.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            for (var c = 0; c < columns.Count; c++)
            {
                var value = rows[r] is not null && rows[r].TryGetValue(columns[c].Key, out var v)
                    ? v
                    : null;
                WriteCell(row.CreateCell(c), value, styles);
            }
        }

        for (var c = 0; c < columns.Count; c++)
            sheet.SetColumnWidth(c, Math.Min(60, Math.Max(12, columns[c].Label.Length * 2 + 4)) * 256);
    }

    private static void BuildProvenanceSheet(
        XSSFWorkbook workbook,
        ReportConfigurationBundleExportResultDto bundle,
        Styles styles)
    {
        var sheet = workbook.CreateSheet(ProvenanceSheetName);
        var nextRow = 0;

        AddLabel(sheet, ref nextRow, "捆绑名称", bundle.Name ?? string.Empty, styles);
        AddLabel(sheet, ref nextRow, "节数", bundle.SectionCount.ToString(), styles);
        AddLabel(sheet, ref nextRow, "合计事实行数", bundle.TotalRowCount.ToString(), styles);
        if (!string.IsNullOrWhiteSpace(bundle.CorrelationId))
            AddLabel(sheet, ref nextRow, "关联 ID", bundle.CorrelationId, styles);

        foreach (var section in bundle.Sections ?? new List<ReportConfigurationBundleSectionExportDto>())
        {
            AddLabel(sheet, ref nextRow, string.Empty, string.Empty, styles);
            var evidence = section.Preview?.Evidence;
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节标题", section.Title ?? string.Empty, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节定义", section.Preview?.Name ?? string.Empty, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节版本", BuildVersionText(section), styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节数据集", section.Preview?.DatasetKey ?? string.Empty, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节行粒度", evidence?.Grain ?? string.Empty, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节筛选", section.Preview?.NormalizedFiltersText ?? string.Empty, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节日程", section.Preview?.DateRangeText ?? string.Empty, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节覆盖", BuildCoverageText(section), styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节币种/单位", evidence?.CurrencyUnitSemantics ?? string.Empty, styles);

            var readOnlyText = evidence?.ReadOnlyText ?? string.Empty;
            var boundaryText = evidence?.BoundaryText ?? string.Empty;
            var readOnlyBoundary = string.IsNullOrWhiteSpace(readOnlyText)
                ? boundaryText
                : (string.IsNullOrWhiteSpace(boundaryText) ? readOnlyText : $"{readOnlyText}；{boundaryText}");
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节只读与边界", readOnlyBoundary, styles);
            AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节未知值说明", UnknownValueText, styles);
            if (!string.IsNullOrWhiteSpace(evidence?.DisclaimerText))
                AddLabel(sheet, ref nextRow, $"第 {section.Ordinal} 节免责声明", evidence.DisclaimerText, styles);
        }

        sheet.SetColumnWidth(0, 18 * 256);
        sheet.SetColumnWidth(1, 90 * 256);
    }

    private static string BuildVersionText(ReportConfigurationBundleSectionExportDto section)
        => section.RevisionVersion is int revision && revision > 0
            ? $"固定修订 v{revision}"
            : $"草稿 v{section.Preview?.Version ?? 0}";

    private static string BuildCoverageText(ReportConfigurationBundleSectionExportDto section)
        => string.Equals(section.Coverage, ReportConfigurationConstants.CoverageMatchedSet, StringComparison.OrdinalIgnoreCase)
            ? $"完整匹配集 {section.MatchedCount} 条事实（有界一致快照）"
            : $"当前预览页 {section.Facts?.Count ?? 0} 行（非全量合计）";

    private static string HeaderText(ReportConfigurationColumnDto column)
        => string.IsNullOrWhiteSpace(column.CurrencyUnit)
            ? column.Label
            : $"{column.Label}（{column.CurrencyUnit}）";

    private static void AddLabel(ISheet sheet, ref int rowIndex, string label, string value, Styles styles)
    {
        var row = sheet.CreateRow(rowIndex++);
        var labelCell = row.CreateCell(0);
        labelCell.SetCellValue(EscapeFormulaLeading(label));
        labelCell.CellStyle = styles.Text;
        var valueCell = row.CreateCell(1);
        valueCell.SetCellValue(EscapeFormulaLeading(value));
        valueCell.CellStyle = styles.Text;
    }

    private static void WriteCell(ICell cell, object? value, Styles styles)
    {
        if (value is null || value is DBNull)
        {
            cell.SetCellValue(string.Empty);
            cell.CellStyle = styles.Text;
            return;
        }

        switch (value)
        {
            case bool b:
                cell.SetCellValue(b ? "是" : "否");
                cell.CellStyle = styles.Text;
                return;
            case DateTime dt:
                cell.SetCellValue(dt.ToString("yyyy-MM-dd"));
                cell.CellStyle = styles.Text;
                return;
            case DateTimeOffset dto:
                cell.SetCellValue(dto.DateTime.ToString("yyyy-MM-dd"));
                cell.CellStyle = styles.Text;
                return;
            case decimal m:
                cell.SetCellValue((double)m);
                cell.CellStyle = styles.Number;
                return;
            case double d:
                cell.SetCellValue(d);
                cell.CellStyle = styles.Number;
                return;
            case float f:
                cell.SetCellValue(f);
                cell.CellStyle = styles.Number;
                return;
            case int i:
                cell.SetCellValue(i);
                cell.CellStyle = styles.Integer;
                return;
            case long l:
                cell.SetCellValue(l);
                cell.CellStyle = styles.Integer;
                return;
            default:
                cell.SetCellValue(EscapeFormulaLeading(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
                cell.CellStyle = styles.Text;
                return;
        }
    }

    private static bool IsFormulaLeadingChar(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    private static string EscapeFormulaLeading(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return IsFormulaLeadingChar(value[0]) ? "'" + value : value;
    }

    private static string SafeSectionSheetName(int ordinal, string title)
    {
        var sanitized = new string((title ?? string.Empty)
            .Where(ch => !"[]:*?/\\".Contains(ch))
            .ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = $"报表配置 #{ordinal}";
        var prefix = ordinal.ToString("00");
        var maxTitleLength = 31 - prefix.Length - 1;
        if (sanitized.Length > maxTitleLength)
            sanitized = sanitized[..maxTitleLength];
        return $"{prefix}-{sanitized}";
    }

    private static Styles CreateStyles(XSSFWorkbook workbook)
    {
        var headerStyle = workbook.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerStyle.SetFont(headerFont);

        var textStyle = workbook.CreateCellStyle();
        textStyle.VerticalAlignment = VerticalAlignment.Center;

        var integerStyle = workbook.CreateCellStyle();
        integerStyle.Alignment = HorizontalAlignment.Right;
        integerStyle.DataFormat = workbook.CreateDataFormat().GetFormat("0");

        var numberStyle = workbook.CreateCellStyle();
        numberStyle.Alignment = HorizontalAlignment.Right;
        numberStyle.DataFormat = workbook.CreateDataFormat().GetFormat("0.00");

        return new Styles(headerStyle, textStyle, integerStyle, numberStyle);
    }

    private sealed class Styles
    {
        public Styles(ICellStyle header, ICellStyle text, ICellStyle integer, ICellStyle number)
        {
            Header = header;
            Text = text;
            Integer = integer;
            Number = number;
        }

        public ICellStyle Header { get; }
        public ICellStyle Text { get; }
        public ICellStyle Integer { get; }
        public ICellStyle Number { get; }
    }
}

