using ERP.Application.Common;
using ERP.Application.DTOs;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态供应商采购敞口预览（ERP-148）PDF 导出（ERP-151）：复用 ERP-148 有界授权预览与选定列顺序，以 PDFsharp 6.2.4 分页渲染。
/// 选定字段、中文标签、原币（不同币种分别成行、绝不换算或合并）与链接不唯一 / 无引用的未知结算金额、未知收货数量
/// 显式保留（null →「未知」，绝不回落为 0）；宽列集按可用页宽拆成多个「列页」，行数超出时按「行页」拆分，避免列被裁切。
/// 中文字体固定使用 Windows 黑体（SimHei），与其它报表 PDF 共用共享解析器（<see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicSupplierExposurePdfExporter
{
    /// <summary>固定使用的中文字体族（Windows 黑体，共享解析器 SimHeiPdfFontResolver）</summary>
    private const string FontFamily = SimHeiPdfFontResolver.FontFamily;

    /// <summary>页面尺寸与边距（毫米）</summary>
    private const double PageWidthMm = 210;
    private const double PageHeightMm = 297;
    private const double MarginLeftMm = 10;
    private const double MarginRightMm = 10;
    private const double MarginTopMm = 12;
    private const double MarginBottomMm = 12;

    private const double TitleSize = 13;
    private const double MetaSize = 8;
    private const double HeaderSize = 8;
    private const double CellSize = 8;

    private const double TitleHeightMm = 9;
    private const double MetaHeightMm = 6;
    private const double NoteLineHeightMm = 4;
    private const double HeaderRowHeightMm = 7;
    private const double DataRowHeightMm = 6.5;

    /// <summary>列宽有界（毫米）：最小保证中文标签可读，最大避免单列独占整页，超宽列集靠「列页」拆分而非挤压</summary>
    private const double MinColumnWidthMm = 20;
    private const double MaxColumnWidthMm = 45;

    private const double PointsPerMillimeter = 72.0 / 25.4;

    /// <summary>未知证据（结算金额 / 收货数量为 null）的统一「未知」标记（绝不回落为 0）</summary>
    private const string UnknownText = "未知";

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicSupplierExposureReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicSupplierExposureReportPageDto page, string? fontPath)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new BusinessException(
                "PDF 导出失败：未找到中文字体 SimHei（黑体）。请在 Windows 字体目录安装 simhei.ttf 后重试，"
                + "避免生成乱码或缺字 PDF。",
                ErrorCodes.InternalError);
        }

        SimHeiPdfFontResolver.Ensure(fontPath);

        using var document = new PdfDocument();
        document.Info.Title = "供应商采购敞口报表";

        DrawReport(document, page);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicSupplierExposureReportPageDto page)
    {
        var columns = page.Columns ?? new List<DynamicSupplierExposureReportFieldDto>();
        var rows = page.Rows ?? new List<Dictionary<string, object?>>();

        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);
        var headerHeight = Mm(HeaderRowHeightMm);
        var dataRowHeight = Mm(DataRowHeightMm);

        // 页头口径（只读 / 边界 / 免责）对每个列页完全相同，故行页拆分在各列页保持一致
        var headNotes = BuildHeadNotes(page, metaFont, usableWidth);
        var headHeightMm = TitleHeightMm + MetaHeightMm + headNotes.Count * NoteLineHeightMm;
        var rowsPerPage = Math.Max(1, (int)Math.Floor(
            (contentBottom - Mm(MarginTopMm) - Mm(headHeightMm) - headerHeight) / dataRowHeight));

        var columnPages = SplitColumnPages(columns, rows, usableWidth);
        var rowPageCount = rows.Count == 0 ? 1 : (int)Math.Ceiling(rows.Count / (double)rowsPerPage);

        var gfxList = new List<XGraphics>();
        try
        {
            if (rows.Count == 0)
            {
                var gfx = NewPage(document);
                gfxList.Add(gfx);
                var cols = columnPages[0];
                var widths = ComputeColumnWidths(cols, rows);
                var y = DrawPageHead(gfx, titleFont, metaFont, page, 0, columnPages.Count, 0, 1, headNotes);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);
                gfx.DrawString("没有符合条件的供应商采购敞口证据", cellFont, XBrushes.Black,
                    new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, dataRowHeight), XStringFormats.TopLeft);
            }
            else
            {
                for (var cp = 0; cp < columnPages.Count; cp++)
                {
                    var cols = columnPages[cp];
                    var widths = ComputeColumnWidths(cols, rows);
                    for (var rp = 0; rp < rowPageCount; rp++)
                    {
                        var gfx = NewPage(document);
                        gfxList.Add(gfx);
                        var y = DrawPageHead(gfx, titleFont, metaFont, page, cp, columnPages.Count, rp, rowPageCount, headNotes);
                        y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);

                        var start = rp * rowsPerPage;
                        var count = Math.Min(rowsPerPage, rows.Count - start);
                        for (var i = 0; i < count; i++)
                        {
                            DrawDataRow(gfx, cellFont, borderPen, cols, widths, rows[start + i],
                                y + i * dataRowHeight);
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var g in gfxList)
                g.Dispose();
        }
    }

    private static XGraphics NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width = new XUnit(PageWidthMm, XGraphicsUnit.Millimeter);
        page.Height = new XUnit(PageHeightMm, XGraphicsUnit.Millimeter);
        return XGraphics.FromPdfPage(page);
    }

    /// <summary>页头口径：只读 / 边界 / 免责文案按可用宽度折行（供应商采购敞口口径，无跨币种合计）</summary>
    private static List<string> BuildHeadNotes(
        DynamicSupplierExposureReportPageDto page, XFont metaFont, double usableWidth)
    {
        var lines = new List<string>();
        AddWrapped(lines, page.ReadOnlyText, metaFont, usableWidth);
        AddWrapped(lines, page.BoundaryText, metaFont, usableWidth);
        AddWrapped(lines, page.DisclaimerText, metaFont, usableWidth);
        return lines;
    }

    private static void AddWrapped(List<string> lines, string? text, XFont font, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var current = string.Empty;
        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (current.Length == 0 || EstimateWidthPoints(candidate, font.Size) <= maxWidth)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current);
                current = ch.ToString();
            }
        }

        if (current.Length > 0)
            lines.Add(current);
    }

    /// <summary>按内容估算列宽（点），夹在最小 / 最大列宽之间，超宽列集由「列页」拆分而非挤压</summary>
    private static double ColumnWidthPoints(
        DynamicSupplierExposureReportFieldDto column, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize);
        foreach (var row in rows)
            max = Math.Max(max, EstimateWidthPoints(FormatFieldCell(column, row), CellSize));

        return Math.Clamp(max + 6, Mm(MinColumnWidthMm), Mm(MaxColumnWidthMm));
    }

    /// <summary>把选定列按可用页宽贪心拆分为多个「列页」，保证每个列页总宽不超页宽（列不裁切）</summary>
    private static List<List<DynamicSupplierExposureReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicSupplierExposureReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicSupplierExposureReportFieldDto>> { new() };

        var pages = new List<List<DynamicSupplierExposureReportFieldDto>>();
        var current = new List<DynamicSupplierExposureReportFieldDto>();
        var currentWidth = 0d;

        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && currentWidth + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicSupplierExposureReportFieldDto>();
                currentWidth = 0;
            }

            current.Add(column);
            currentWidth += width;
        }

        if (current.Count > 0)
            pages.Add(current);

        return pages;
    }

    private static double[] ComputeColumnWidths(
        IReadOnlyList<DynamicSupplierExposureReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = ColumnWidthPoints(columns[c], rows);
        return widths;
    }

    private static double DrawPageHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicSupplierExposureReportPageDto page,
        int columnPage, int columnPageCount, int rowPage, int rowPageCount,
        IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = "供应商采购敞口报表";
        if (columnPageCount > 1)
            title += $"（列 {columnPage + 1}/{columnPageCount}）";

        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"共 {page.Total.ToString(CultureInfo.InvariantCulture)} 条"
            + $" · 第 {page.Page.ToString(CultureInfo.InvariantCulture)}/{page.TotalPages.ToString(CultureInfo.InvariantCulture)} 页"
            + $" · 列页 {columnPage + 1}/{columnPageCount}"
            + $" · 行页 {rowPage + 1}/{rowPageCount}";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm + TitleHeightMm), width, Mm(MetaHeightMm)), XStringFormats.TopCenter);

        var y = Mm(MarginTopMm + TitleHeightMm + MetaHeightMm);
        foreach (var note in headNotes)
        {
            gfx.DrawString(note, metaFont, XBrushes.Black,
                new XRect(left, y, width, Mm(NoteLineHeightMm)), XStringFormats.TopLeft);
            y += Mm(NoteLineHeightMm);
        }

        return y;
    }

    private static double DrawColumnHeaders(
        XGraphics gfx, XFont font, XBrush brush, XPen pen,
        IReadOnlyList<DynamicSupplierExposureReportFieldDto> columns, double[] widths, double y)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(columns[c].Label, font, XBrushes.Black, rect, XStringFormats.Center);
        }

        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawDataRow(
        XGraphics gfx, XFont font, XPen pen,
        IReadOnlyList<DynamicSupplierExposureReportFieldDto> columns, double[] widths,
        Dictionary<string, object?> row, double y)
    {
        var left = Mm(MarginLeftMm);
        var cells = BuildRowCells(columns, row);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            var format = columns[c].DataType is "number" or "boolean"
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, cells[c], font, rect, format);
        }
    }

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序与未知证据）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicSupplierExposureReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatFieldCell(col, row));
        return cells;
    }

    /// <summary>未知证据字段键（null = 未知，绝不回落为 0）：链接不唯一 / 无引用的结算金额与收货数量</summary>
    public static bool IsUnknownEvidenceKey(string key)
        => key switch
        {
            "settledAmount" => true,
            "outstandingAmount" => true,
            "submittedAmount" => true,
            "orderedQuantity" => true,
            "receivedQuantity" => true,
            "outstandingQuantity" => true,
            "pendingQuantity" => true,
            _ => false,
        };

    /// <summary>
    /// 字段感知的单元格文本：未知证据字段（结算金额 / 收货数量）为 null 时显式显示「未知」（绝不回落为 0）；
    /// 链接状态 / 收货状态映射为中文标签（显式未知 / 不唯一证据）；其余按通用口径格式化。
    /// 金额 / 数量一律按原币、按行呈现，不做跨币种换算或汇总。
    /// </summary>
    public static string FormatFieldCell(DynamicSupplierExposureReportFieldDto field, Dictionary<string, object?> row)
    {
        var value = row.TryGetValue(field.Key, out var v) ? v : null;

        if (value is null or DBNull)
            return IsUnknownEvidenceKey(field.Key) ? UnknownText : string.Empty;

        return field.Key switch
        {
            "linkStatus" => FormatLinkStatus(value),
            "receiptStatus" => FormatReceiptStatus(value),
            _ => FormatCellValue(value),
        };
    }

    /// <summary>链接状态 → 中文标签（显式「未知 / 不唯一」证据，未知取值照实保留原文）</summary>
    public static string FormatLinkStatus(object value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture);
        return s switch
        {
            "linked" => "链接可用",
            "ambiguous" => "链接不唯一（金额未知）",
            "unavailable" => "无可用链接（金额未知）",
            _ => s ?? string.Empty,
        };
    }

    /// <summary>收货状态 → 中文标签（显式「未知（超出派生上限）」证据，未知取值照实保留原文）</summary>
    public static string FormatReceiptStatus(object value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture);
        return s switch
        {
            "none" => "未收货",
            "partial" => "部分收货",
            "complete" => "已收齐",
            "over_received" => "超收",
            "unknown" => "未知（超出派生上限）",
            _ => s ?? string.Empty,
        };
    }

    /// <summary>把预览行的单元格值转成 PDF 单元格文本（中文布尔 / 日期 / 数值口径与导出保持一致）</summary>
    public static string FormatCellValue(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return string.Empty;
            case bool b:
                return b ? "是" : "否";
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString("0.##", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("0.##", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("0.##", CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static void DrawCellText(XGraphics gfx, string text, XFont font, XRect rect, XStringFormat format)
    {
        if (string.IsNullOrEmpty(text))
            return;

        const double padding = 2;
        var maxWidth = rect.Width - padding * 2;
        if (gfx.MeasureString(text, font).Width <= maxWidth)
        {
            gfx.DrawString(text, font, XBrushes.Black, rect, format);
            return;
        }

        const string ellipsis = "…";
        var available = maxWidth - gfx.MeasureString(ellipsis, font).Width;
        var buffer = string.Empty;
        foreach (var ch in text)
        {
            var candidate = buffer + ch;
            if (gfx.MeasureString(candidate, font).Width > available)
                break;
            buffer = candidate;
        }

        gfx.DrawString(buffer + ellipsis, font, XBrushes.Black, rect, format);
    }

    /// <summary>纯文本宽度估算（点）：中文 / 全角按一个字号宽、半角按半个字号宽（不依赖已注册字体，供折行与列宽估算）</summary>
    private static double EstimateWidthPoints(string? text, double size)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var width = 0d;
        foreach (var ch in text)
            width += IsWideChar(ch) ? size : size * 0.5;
        return width;
    }

    private static bool IsWideChar(char c) => c > 0x2E80;

    private static double SumWidths(double[] values, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++)
            total += values[i];
        return total;
    }

    private static double Mm(double millimeters) => millimeters * PointsPerMillimeter;
}
