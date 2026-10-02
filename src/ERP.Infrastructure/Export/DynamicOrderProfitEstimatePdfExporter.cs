using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态订单利润暂估报表（ERP-222）PDF 下载：复用 ERP-221 有界、已授权预览与选定列顺序，
/// 以 PDFsharp 6.2.4 分页渲染选定字段（订单利润暂估证据白名单）。
/// 选定字段与中文标签、原币口径 / 未知成本利润依据 / 页面覆盖 / 来源上限上下文显式保留（即使对应列被取消选择）；
/// 已知销售额按数值渲染、未知成本 / 利润 / 利润率显式渲染为「未知」（绝不回落为 0、绝不跨币种合计、绝不声称已实现利润）；
/// 宽列集按可用页宽贪心拆成多个「列页」，行数超出时按「行页」拆分，避免列 / 行被裁切；空证据显式说明。
/// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器 <see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失或渲染失败时显式失败（不产出乱码 / 缺字 / 损坏 PDF）。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicOrderProfitEstimatePdfExporter
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

    /// <summary>空页显式说明（与 DynamicOrderProfitEstimateReportRules.EmptyText 同源兜底）</summary>
    private const string EmptyFallbackText = "没有符合所选日期范围与数据范围的已审核销售订单（或记录已被软删除）";

    /// <summary>报表标题</summary>
    private const string ReportTitle = "订单利润暂估表（原币证据）";

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicOrderProfitEstimateReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicOrderProfitEstimateReportPageDto page, string? fontPath)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new BusinessException(
                "PDF 下载失败：未找到中文字体 SimHei（黑体）。请在 Windows 字体目录安装 simhei.ttf 后重试，"
                + "避免生成乱码或缺字 PDF。",
                ErrorCodes.InternalError);
        }

        SimHeiPdfFontResolver.Ensure(fontPath);

        try
        {
            using var document = new PdfDocument();
            document.Info.Title = ReportTitle;

            DrawReport(document, page);

            using var stream = new MemoryStream();
            document.Save(stream, false);
            return stream.ToArray();
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception)
        {
            // 渲染失败时绝不返回损坏 / 半成品 PDF，仅给出清晰错误
            throw new BusinessException(
                "PDF 下载失败：渲染报表时发生错误，未生成任何文件，请稍后重试。",
                ErrorCodes.InternalError);
        }
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicOrderProfitEstimateReportPageDto page)
    {
        var columns = page.Columns ?? new List<DynamicOrderProfitEstimateReportFieldDto>();
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

        // 头部（标题 + 元信息 + 上下文注释）总高决定每个「行页」能容纳多少行
        var noteLines = WrapNotes(BuildHeadNotes(page), usableWidth);
        var headHeightMm = TitleHeightMm + MetaHeightMm + noteLines.Count * NoteLineHeightMm;
        var rowsPerPage = Math.Max(1, (int)Math.Floor(
            (contentBottom - Mm(MarginTopMm) - Mm(headHeightMm) - headerHeight) / dataRowHeight));

        var columnPages = SplitColumnPages(columns, rows, usableWidth);
        var rowPageCount = rows.Count == 0 ? 1 : (int)Math.Ceiling(rows.Count / (double)rowsPerPage);

        var graphics = new List<XGraphics>();
        try
        {
            if (rows.Count == 0)
            {
                var gfx = NewPage(document);
                graphics.Add(gfx);
                var cols = columnPages[0];
                var widths = ComputeColumnWidths(cols, rows);
                var y = DrawPageHead(gfx, titleFont, metaFont, page, 0, columnPages.Count, 0, rowPageCount, noteLines);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);
                DrawEmptyNote(gfx, cellFont, page, usableWidth, y);
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
                        graphics.Add(gfx);
                        var y = DrawPageHead(gfx, titleFont, metaFont, page, cp, columnPages.Count, rp, rowPageCount, noteLines);
                        y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);

                        var start = rp * rowsPerPage;
                        var count = Math.Min(rowsPerPage, rows.Count - start);
                        for (var i = 0; i < count; i++)
                            DrawDataRow(gfx, cellFont, borderPen, cols, widths, rows[start + i], y + i * dataRowHeight);
                    }
                }
            }
        }
        finally
        {
            foreach (var g in graphics)
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

    /// <summary>上下文注释（与原币 / 未知成本利润 / 页面覆盖 / 来源上限一一对应，即使对应列被取消选择也始终呈现）</summary>
    private static List<string> BuildHeadNotes(DynamicOrderProfitEstimateReportPageDto page)
        => new()
        {
            page.ReadOnlyText,
            page.CurrencyContextText,
            page.UnknownBasisText,
            page.DisclaimerText,
            DynamicOrderProfitEstimateReportRules.MissingCostBasisText,
            page.PageOnlyText,
            page.SourceLimitText,
        };

    private static List<string> WrapNotes(IReadOnlyList<string> notes, double maxWidth)
    {
        var lines = new List<string>();
        foreach (var note in notes)
        {
            if (string.IsNullOrWhiteSpace(note))
                continue;

            foreach (var line in WrapText(note, MetaSize, maxWidth))
                lines.Add(line);
        }

        return lines;
    }

    private static List<string> WrapText(string text, double size, double maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
            return lines;

        var buffer = string.Empty;
        foreach (var ch in text)
        {
            var candidate = buffer + ch;
            if (EstimateWidthPoints(candidate, size) > maxWidth && buffer.Length > 0)
            {
                lines.Add(buffer);
                buffer = ch.ToString();
            }
            else
            {
                buffer = candidate;
            }
        }

        if (buffer.Length > 0)
            lines.Add(buffer);

        return lines;
    }

    private static double DrawPageHead(
        XGraphics gfx,
        XFont titleFont,
        XFont metaFont,
        DynamicOrderProfitEstimateReportPageDto page,
        int columnPageIndex,
        int columnPageCount,
        int rowPageIndex,
        int rowPageCount,
        IReadOnlyList<string> noteLines)
    {
        var left = Mm(MarginLeftMm);
        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var y = Mm(MarginTopMm);

        var title = ReportTitle;
        if (columnPageCount > 1 || rowPageCount > 1)
            title += $"（第 {rowPageIndex + 1}/{rowPageCount} 行页 · 第 {columnPageIndex + 1}/{columnPageCount} 列页）";
        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, y, usableWidth, Mm(TitleHeightMm)), XStringFormats.TopLeft);
        y += Mm(TitleHeightMm);

        var meta = $"日期 {page.Start:yyyy-MM-dd} ~ {page.End:yyyy-MM-dd} · "
            + DynamicOrderProfitEstimateReportRules.BuildPageContext(
                page.Page, page.PageSize, page.Total, page.TotalPages, page.Truncated);
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(left, y, usableWidth, Mm(MetaHeightMm)), XStringFormats.TopLeft);
        y += Mm(MetaHeightMm);

        foreach (var line in noteLines)
        {
            gfx.DrawString(line, metaFont, XBrushes.Black,
                new XRect(left, y, usableWidth, Mm(NoteLineHeightMm)), XStringFormats.TopLeft);
            y += Mm(NoteLineHeightMm);
        }

        return y;
    }

    private static double DrawColumnHeaders(
        XGraphics gfx,
        XFont headerFont,
        XSolidBrush brush,
        XPen pen,
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        double[] widths,
        double y)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(brush, rect);
            gfx.DrawRectangle(pen, rect);
            DrawCellText(gfx, columns[c].Label, headerFont, rect, XStringFormats.Center);
        }

        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawDataRow(
        XGraphics gfx,
        XFont font,
        XPen pen,
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        double[] widths,
        Dictionary<string, object?> row,
        double y)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            var value = row.TryGetValue(col.Key, out var v) ? v : null;
            var text = FormatFieldCell(col, row);
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            // 已知销售额等数值证据右对齐呈现；未知成本 / 利润 / 利润率显式「未知」左对齐呈现，二者明显区分
            var format = col.DataType == "number" && value is not null
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format);
        }
    }

    private static void DrawEmptyNote(
        XGraphics gfx,
        XFont font,
        DynamicOrderProfitEstimateReportPageDto page,
        double usableWidth,
        double y)
    {
        var text = string.IsNullOrWhiteSpace(page.EmptyText) ? EmptyFallbackText : page.EmptyText;
        gfx.DrawString(text, font, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, Mm(DataRowHeightMm)), XStringFormats.TopLeft);
    }

    // ==================== 列页拆分与列宽 ====================

    /// <summary>把选定列按可用页宽贪心拆成多个「列页」，避免超宽列集被挤压 / 裁切</summary>
    private static List<List<DynamicOrderProfitEstimateReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        var pages = new List<List<DynamicOrderProfitEstimateReportFieldDto>>();
        if (columns.Count == 0)
        {
            pages.Add(new List<DynamicOrderProfitEstimateReportFieldDto>());
            return pages;
        }

        var current = new List<DynamicOrderProfitEstimateReportFieldDto>();
        var used = 0d;
        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && used + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicOrderProfitEstimateReportFieldDto>();
                used = 0;
            }

            current.Add(column);
            used += width;
        }

        if (current.Count > 0)
            pages.Add(current);

        return pages;
    }

    private static double[] ComputeColumnWidths(
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
        => columns.Select(c => ColumnWidthPoints(c, rows)).ToArray();

    private static double ColumnWidthPoints(
        DynamicOrderProfitEstimateReportFieldDto column,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize) + 8;
        foreach (var row in rows)
        {
            var value = row.TryGetValue(column.Key, out var v) ? v : null;
            var text = value is null or DBNull
                ? DynamicOrderProfitEstimateReportRules.UnknownAmountText
                : FormatCellValue(value);
            var width = EstimateWidthPoints(text, CellSize) + 8;
            if (width > max)
                max = width;
        }

        var mm = max / PointsPerMillimeter;
        return Mm(Math.Clamp(mm, MinColumnWidthMm, MaxColumnWidthMm));
    }

    // ==================== 单元格格式化 ====================

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序与未知金额语义）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatFieldCell(col, row));
        return cells;
    }

    /// <summary>字段感知的单元格文本：null 金额显式渲染为「未知」（绝不写成 0），已知数值按数值格式呈现</summary>
    public static string FormatFieldCell(DynamicOrderProfitEstimateReportFieldDto field, Dictionary<string, object?> row)
    {
        var value = row.TryGetValue(field.Key, out var v) ? v : null;
        if (value is null or DBNull)
            return DynamicOrderProfitEstimateReportRules.UnknownAmountText;
        return FormatCellValue(value);
    }

    /// <summary>把单元格值转成 PDF 单元格文本（中文布尔 / 日期 / 数值口径与 Excel 导出保持一致）</summary>
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

    // ==================== 绘制工具 ====================

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
