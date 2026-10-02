using ERP.Application.DTOs;
using ERP.Application.Services;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用报表配置平台（ERP-263 Stage 1）Excel 导出器：无状态、无数据集特化分派，只消费统一的
/// <see cref="ReportConfigurationPreviewDto"/>（列 / 行 / 分组页面小计 / 证据上下文 + 定义名称 / 版本 +
/// 规范化筛选 / 日期范围），生成当前预览页工作簿。
/// <para>口径：仅导出当前预览页选定列（保留选择顺序）；金额 / 计数等数值保留符号并按类型写入数值单元格；
/// null 未知一律留空（绝不写成 0），并在「报表口径」工作表说明未知值口径；日期 / 布尔按类型正确呈现；
/// 文本单元格做公式注入转义（= / + / - / @ / 制表符 / 回车 / 换行前缀单引号），保持字面文本、不被当作公式执行。</para>
/// <para>分组小计仅覆盖当前预览页且按币种分区（绝不跨币种 / 单位相加），绝不追加全匹配合计；「报表口径」工作表始终包含
/// 定义名称 / 版本、数据集 / 行粒度、规范化查询筛选与日期范围、当前页覆盖口径、币种 / 单位语义、只读与边界、
/// 未知值说明（即使相关展示列被取消选择也始终包含）。全程只读：仅生成 xlsx 字节流，不写库、不执行任意 SQL。</para>
/// </summary>
public sealed class ReportConfigurationExcelExporter
{
    /// <summary>数据工作表名（当前预览页选定字段证据）</summary>
    public const string DataSheetName = "报表数据";

    /// <summary>分组页面小计工作表名（仅分组时追加；当前预览页、按币种分区）</summary>
    public const string GroupsSheetName = "分组小计（当前页）";

    /// <summary>指标汇总工作表名（仅选中指标时追加；当前预览页、分组 + 币种分区）</summary>
    public const string MetricsSheetName = "指标汇总（当前页）";

    /// <summary>报表口径上下文工作表名</summary>
    public const string ContextSheetName = "报表口径";

    /// <summary>未知值说明（null 在数据 / 小计中留空，绝不写成 0）</summary>
    public const string UnknownValueText = "空单元格表示未知（null），绝不写成 0 或推算值";

    /// <summary>生成当前预览页工作簿（只读）。</summary>
    public byte[] Build(ReportConfigurationPreviewDto preview)
        => Build(preview, CancellationToken.None);

    /// <summary>生成当前预览页工作簿（只读；可传播联动取消令牌到渲染循环，超时 / 断连时提前停止）。</summary>
    public byte[] Build(ReportConfigurationPreviewDto preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();

        using var workbook = new XSSFWorkbook();
        var styles = CreateStyles(workbook);

        BuildDataSheet(workbook, preview, styles, cancellationToken);

        if (preview.Groups is { Count: > 0 })
            BuildGroupsSheet(workbook, preview.Groups, styles, cancellationToken);

        if (preview.Metrics is { Count: > 0 })
            BuildMetricsSheet(workbook, preview.Metrics, styles, cancellationToken);

        BuildContextSheet(workbook, preview, styles, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    // ==================== 样式 ====================

    private static Styles CreateStyles(XSSFWorkbook workbook)
    {
        var headerStyle = workbook.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;
        headerStyle.BorderBottom = BorderStyle.Thin;
        headerStyle.BorderTop = BorderStyle.Thin;
        headerStyle.BorderLeft = BorderStyle.Thin;
        headerStyle.BorderRight = BorderStyle.Thin;
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerStyle.SetFont(headerFont);

        var textStyle = workbook.CreateCellStyle();
        textStyle.VerticalAlignment = VerticalAlignment.Center;
        textStyle.BorderBottom = BorderStyle.Thin;
        textStyle.BorderLeft = BorderStyle.Thin;
        textStyle.BorderRight = BorderStyle.Thin;

        var integerStyle = workbook.CreateCellStyle();
        integerStyle.Alignment = HorizontalAlignment.Right;
        integerStyle.VerticalAlignment = VerticalAlignment.Center;
        integerStyle.BorderBottom = BorderStyle.Thin;
        integerStyle.BorderLeft = BorderStyle.Thin;
        integerStyle.BorderRight = BorderStyle.Thin;

        var numberStyle = workbook.CreateCellStyle();
        numberStyle.DataFormat = workbook.CreateDataFormat().GetFormat("0.00");
        numberStyle.Alignment = HorizontalAlignment.Right;
        numberStyle.VerticalAlignment = VerticalAlignment.Center;
        numberStyle.BorderBottom = BorderStyle.Thin;
        numberStyle.BorderLeft = BorderStyle.Thin;
        numberStyle.BorderRight = BorderStyle.Thin;

        return new Styles(headerStyle, textStyle, integerStyle, numberStyle);
    }

    // ==================== 数据工作表 ====================

    private static void BuildDataSheet(XSSFWorkbook workbook, ReportConfigurationPreviewDto preview, Styles styles, CancellationToken cancellationToken)
    {
        var sheet = workbook.CreateSheet(DataSheetName);
        var columns = preview.Columns ?? new List<ReportConfigurationColumnDto>();

        var headerRow = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = headerRow.CreateCell(c);
            cell.SetCellValue(HeaderText(columns[c]));
            cell.CellStyle = styles.Header;
        }

        for (var r = 0; r < preview.Rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = sheet.CreateRow(r + 1);
            var source = preview.Rows[r];
            for (var c = 0; c < columns.Count; c++)
            {
                var value = source is not null && source.TryGetValue(columns[c].Key, out var v) ? v : null;
                WriteCell(row.CreateCell(c), value, columns[c], styles);
            }
        }

        for (var c = 0; c < columns.Count; c++)
        {
            var width = Math.Max(HeaderText(columns[c]).Length * 2 + 4, 8);
            var sample = sheet.GetRow(1)?.GetCell(c);
            if (sample is not null && sample.CellType == CellType.String && sample.StringCellValue.Length > 0)
                width = Math.Max(width, sample.StringCellValue.Length * 2 + 4);
            sheet.SetColumnWidth(c, Math.Min(width, 60) * 256);
        }
    }

    private static string HeaderText(ReportConfigurationColumnDto column)
        => string.IsNullOrWhiteSpace(column.CurrencyUnit)
            ? column.Label
            : $"{column.Label}（{column.CurrencyUnit}）";

    private static void WriteCell(ICell cell, object? value, ReportConfigurationColumnDto column, Styles styles)
    {
        if (value is null || value is DBNull)
        {
            cell.SetCellValue(string.Empty);
            cell.CellStyle = styles.Text;
            return;
        }

        if (string.Equals(column.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase))
        {
            if (TryWriteNumber(cell, value, styles))
                return;
        }

        if (string.Equals(column.Type, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase))
        {
            if (value is DateTime dt)
            {
                cell.SetCellValue(FormatDate(dt));
                cell.CellStyle = styles.Text;
                return;
            }
            if (value is DateTimeOffset dto)
            {
                cell.SetCellValue(FormatDate(dto.DateTime));
                cell.CellStyle = styles.Text;
                return;
            }
        }

        if (string.Equals(column.Type, ReportConfigurationConstants.TypeBoolean, StringComparison.OrdinalIgnoreCase)
            && value is bool boolean)
        {
            cell.SetCellValue(boolean ? "是" : "否");
            cell.CellStyle = styles.Text;
            return;
        }

        cell.SetCellValue(EscapeFormulaLeading(Convert.ToString(value, CultureInfo.InvariantCulture)));
        cell.CellStyle = styles.Text;
    }

    private static bool TryWriteNumber(ICell cell, object? value, Styles styles)
    {
        switch (value)
        {
            case int i:
                cell.SetCellValue(i);
                cell.CellStyle = styles.Integer;
                return true;
            case long l:
                cell.SetCellValue(l);
                cell.CellStyle = styles.Integer;
                return true;
            case decimal m:
                cell.SetCellValue((double)m);
                cell.CellStyle = styles.Number;
                return true;
            case double d:
                cell.SetCellValue(d);
                cell.CellStyle = styles.Number;
                return true;
            case float f:
                cell.SetCellValue(f);
                cell.CellStyle = styles.Number;
                return true;
            case short s:
                cell.SetCellValue((double)s);
                cell.CellStyle = styles.Integer;
                return true;
            case byte b2:
                cell.SetCellValue((double)b2);
                cell.CellStyle = styles.Integer;
                return true;
            case sbyte sb:
                cell.SetCellValue((double)sb);
                cell.CellStyle = styles.Integer;
                return true;
            case ushort us:
                cell.SetCellValue((double)us);
                cell.CellStyle = styles.Integer;
                return true;
            case uint ui:
                cell.SetCellValue((double)ui);
                cell.CellStyle = styles.Integer;
                return true;
            case ulong ul:
                cell.SetCellValue((double)ul);
                cell.CellStyle = styles.Integer;
                return true;
            default:
                return false;
        }
    }

    private static string FormatDate(DateTime value)
        => value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd")
            : value.ToString("yyyy-MM-dd HH:mm:ss");

    // ==================== 分组页面小计工作表（当前预览页、按币种分区） ====================

    private static void BuildGroupsSheet(
        XSSFWorkbook workbook, IReadOnlyList<ReportConfigurationGroupSubtotalDto> groups, Styles styles, CancellationToken cancellationToken)
    {
        var sheet = workbook.CreateSheet(GroupsSheetName);
        var headerRow = sheet.CreateRow(0);
        var headers = new[] { "分组", "币种", "条数", "金额", "含税金额", "已分摊金额", "剩余金额", "剩余状态" };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = headerRow.CreateCell(c);
            cell.SetCellValue(headers[c]);
            cell.CellStyle = styles.Header;
        }

        var rowIndex = 1;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var partition in group.Partitions)
            {
                var row = sheet.CreateRow(rowIndex++);
                WriteText(row.CreateCell(0), group.Label, styles.Text);
                WriteText(row.CreateCell(1), partition.Currency, styles.Text);
                row.CreateCell(2).SetCellValue(partition.Count);
                row.GetCell(2).CellStyle = styles.Integer;
                WriteOptionalAmount(row.CreateCell(3), partition.Amount, styles.Number);
                WriteOptionalAmount(row.CreateCell(4), partition.GrossAmount, styles.Number);
                WriteOptionalAmount(row.CreateCell(5), partition.EffectiveAllocatedAmount, styles.Number);
                WriteOptionalAmount(row.CreateCell(6), partition.RemainingAmount, styles.Number);
                WriteText(row.CreateCell(7), partition.RemainingState, styles.Text);
            }
        }

        for (var c = 0; c < headers.Length; c++)
            sheet.SetColumnWidth(c, Math.Min(headers[c].Length * 2 + 4, 40) * 256);
    }

    // ==================== 指标汇总工作表（当前预览页、分组 + 币种分区） ====================

    private static void BuildMetricsSheet(
        XSSFWorkbook workbook, IReadOnlyList<ReportConfigurationMetricResultDto> metrics, Styles styles, CancellationToken cancellationToken)
    {
        var sheet = workbook.CreateSheet(MetricsSheetName);
        var headerRow = sheet.CreateRow(0);
        var headers = new[] { "指标", "分组", "币种", "数值", "已知值条数", "缺失条数", "来源条数", "原因" };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = headerRow.CreateCell(c);
            cell.SetCellValue(headers[c]);
            cell.CellStyle = styles.Header;
        }

        var rowIndex = 1;
        foreach (var metric in metrics)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var label = MetricTitle(metric);
            foreach (var cell in metric.Cells)
            {
                var row = sheet.CreateRow(rowIndex++);
                WriteText(row.CreateCell(0), label, styles.Text);
                WriteText(row.CreateCell(1), cell.GroupLabel, styles.Text);
                WriteText(row.CreateCell(2), cell.Currency, styles.Text);
                WriteOptionalAmount(row.CreateCell(3), cell.Value, styles.Number);
                row.CreateCell(4).SetCellValue(cell.KnownCount);
                row.GetCell(4).CellStyle = styles.Integer;
                row.CreateCell(5).SetCellValue(cell.MissingCount);
                row.GetCell(5).CellStyle = styles.Integer;
                row.CreateCell(6).SetCellValue(cell.SourceCount);
                row.GetCell(6).CellStyle = styles.Integer;
                WriteText(row.CreateCell(7), cell.Reason, styles.Text);
            }
        }

        for (var c = 0; c < headers.Length; c++)
            sheet.SetColumnWidth(c, Math.Min(headers[c].Length * 2 + 4, 40) * 256);
    }

    private static string MetricTitle(ReportConfigurationMetricResultDto metric)
    {
        var unit = string.IsNullOrWhiteSpace(metric.Unit) ? string.Empty : $"（{metric.Unit}）";
        return $"{metric.Label}（{ReportConfigurationMetricRules.FunctionLabel(metric.Function)}）{unit}";
    }

    private static void WriteOptionalAmount(ICell cell, decimal? value, ICellStyle style)
    {
        if (value.HasValue)
        {
            cell.SetCellValue((double)value.Value);
            cell.CellStyle = style;
        }
        else
        {
            cell.SetCellValue(string.Empty);
        }
    }

    private static void WriteText(ICell cell, string? value, ICellStyle style)
    {
        cell.SetCellValue(EscapeFormulaLeading(value));
        cell.CellStyle = style;
    }

    // ==================== 报表口径上下文工作表 ====================

    private static void BuildContextSheet(XSSFWorkbook workbook, ReportConfigurationPreviewDto preview, Styles styles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sheet = workbook.CreateSheet(ContextSheetName);
        var evidence = preview.Evidence;

        var nextRow = 0;
        AddLabel(sheet, ref nextRow, "报表名称", preview.Name, styles);
        AddLabel(sheet, ref nextRow, "定义版本",
            preview.PinnedRevisionVersion.HasValue
                ? $"发布修订 {preview.PinnedRevisionVersion.Value}"
                : $"草稿（版本令牌 {preview.Version}）", styles);
        AddLabel(sheet, ref nextRow, "数据集", preview.DatasetKey, styles);
        AddLabel(sheet, ref nextRow, "行粒度", evidence?.Grain ?? string.Empty, styles);
        AddLabel(sheet, ref nextRow, "查询筛选",
            string.IsNullOrWhiteSpace(preview.NormalizedFiltersText) ? "无筛选" : preview.NormalizedFiltersText, styles);
        AddLabel(sheet, ref nextRow, "日期范围",
            string.IsNullOrWhiteSpace(preview.DateRangeText) ? "无日期筛选" : preview.DateRangeText, styles);
        AddLabel(sheet, ref nextRow, "排序",
            string.IsNullOrWhiteSpace(preview.SortEvidence) ? "默认排序（稳定分页）" : preview.SortEvidence, styles);
        AddLabel(sheet, ref nextRow, "页面覆盖",
            $"第 {preview.Page} 页 · 每页 {preview.PageSize} 条 · 命中 {preview.Total} 条 · 共 {preview.TotalPages} 页 · 当前预览页（非全量合计）",
            styles);
        AddLabel(sheet, ref nextRow, "币种/单位口径", evidence?.CurrencyUnitSemantics ?? string.Empty, styles);

        var computedText = BuildComputedEvidenceText(preview.ComputedColumns);
        if (!string.IsNullOrWhiteSpace(computedText))
            AddLabel(sheet, ref nextRow, "计算列口径", computedText, styles);

        var readOnlyText = evidence?.ReadOnlyText ?? string.Empty;
        var boundaryText = evidence?.BoundaryText ?? string.Empty;
        var readOnlyBoundary = string.IsNullOrWhiteSpace(readOnlyText)
            ? boundaryText
            : (string.IsNullOrWhiteSpace(boundaryText) ? readOnlyText : $"{readOnlyText}；{boundaryText}");
        AddLabel(sheet, ref nextRow, "只读与边界", readOnlyBoundary, styles);

        AddLabel(sheet, ref nextRow, "未知值说明", UnknownValueText, styles);

        if (!string.IsNullOrWhiteSpace(evidence?.DisclaimerText))
            AddLabel(sheet, ref nextRow, "免责声明", evidence.DisclaimerText, styles);

        sheet.SetColumnWidth(0, 18 * 256);
        sheet.SetColumnWidth(1, 90 * 256);
    }

    private static string BuildComputedEvidenceText(
        IReadOnlyList<ReportConfigurationComputedColumnEvidenceDto>? computedColumns)
    {
        if (computedColumns is null || computedColumns.Count == 0)
            return string.Empty;

        return string.Join("；", computedColumns.Select(c =>
        {
            var unit = string.IsNullOrWhiteSpace(c.Unit) ? string.Empty : $"（{c.Unit}）";
            var deps = c.Dependencies is { Count: > 0 } ? $"；依赖：{string.Join(", ", c.Dependencies)}" : string.Empty;
            return $"{c.Label}{unit}：{c.UnknownReason}{deps}";
        }));
    }

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

    // ==================== 公式注入防护（OWASP） ====================

    private static bool IsFormulaLeadingChar(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    private static string EscapeFormulaLeading(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return IsFormulaLeadingChar(value[0]) ? "'" + value : value;
    }

    /// <summary>工作簿单元格样式（无状态、由 <see cref="Build"/> 创建一次并在各工作表复用）。</summary>
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
