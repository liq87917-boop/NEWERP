using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 受控打印渲染中文 PDF 导出器（ERP-313 Stage 2）：无数据集特化分派，只消费 <see cref="ReportConfigurationPrintRenderDto"/>
/// （已通过受控绑定目录与既有执行服务重新校验的 standard 表格 / LayoutJson 网格渲染模型）。
/// <para>口径：金额按原币呈现、数量按基础单位，绝不跨币种 / 单位换算；null 未知一律留空（绝不写成 0）；
/// 中文字体固定使用 Windows 黑体（SimHei）；字体缺失或渲染失败显式失败；渲染循环传播取消；全程只读。</para>
/// </summary>
public static class ReportConfigurationPrintPdfExporter
{
    private const string FontFamily = SimHeiPdfFontResolver.FontFamily;
    private const double PointsPerMillimeter = ReportConfigurationPdfColumnLayout.PointsPerMillimeter;

    public static byte[] Export(ReportConfigurationPrintRenderDto render)
        => Export(render, SimHeiPdfFontResolver.FindFontPath(), CancellationToken.None);

    public static byte[] Export(ReportConfigurationPrintRenderDto render, CancellationToken cancellationToken)
        => Export(render, SimHeiPdfFontResolver.FindFontPath(), cancellationToken);

    public static byte[] Export(ReportConfigurationPrintRenderDto render, string? fontPath)
        => Export(render, fontPath, CancellationToken.None);

    public static byte[] Export(ReportConfigurationPrintRenderDto render, string? fontPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(render);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
            throw new BusinessException(
                "PDF 导出失败：未找到中文字体 SimHei（黑体）。请在 Windows 字体目录安装 simhei.ttf 后重试，避免生成乱码或缺字 PDF。",
                ErrorCodes.InternalError);

        SimHeiPdfFontResolver.Ensure(fontPath);

        try
        {
            using var document = new PdfDocument();
            document.Info.Title = string.IsNullOrWhiteSpace(render.Title) ? "打印" : render.Title;

            var size = PaperSizeOf(render.PaperSize);
            if (string.Equals(render.Layout, ReportPrintRenderLayoutText.Grid, StringComparison.OrdinalIgnoreCase))
                DrawGrid(document, render, size, cancellationToken);
            else
                DrawStandard(document, render, size, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new MemoryStream();
            document.Save(stream, false);
            return stream.ToArray();
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BusinessException(
                "打印 PDF 导出失败：渲染时发生错误，未生成任何文件，请稍后重试。",
                ReportConfigurationExecutionLimits.ErrorCodeRenderingFailed);
        }
    }

    private static (double Width, double Height) PaperSizeOf(string? paper)
        => (paper ?? "A4").Trim().ToUpperInvariant() switch
        {
            "A5" => (148, 210),
            "A4-L" => (297, 210),
            "80MM" => (80, 297),
            _ => (210, 297),
        };

    private static double MarginFor((double Width, double Height) size)
        => size.Width <= 100 ? 4 : 10;

    private static double Mm(double mm) => mm * PointsPerMillimeter;

    private static int Clamp(int value, int min, int max)
        => Math.Clamp(value, min, max);

    private static XGraphics NewPage(PdfDocument document, (double Width, double Height) size)
    {
        var page = document.AddPage();
        page.Width = new XUnit(size.Width, XGraphicsUnit.Millimeter);
        page.Height = new XUnit(size.Height, XGraphicsUnit.Millimeter);
        return XGraphics.FromPdfPage(page);
    }

    private static string CoverageText(ReportConfigurationPrintRenderDto render)
        => string.Equals(render.Coverage, ReportConfigurationConstants.CoverageMatchedSet, StringComparison.OrdinalIgnoreCase)
            ? "有界匹配集（≤1000 条一致快照）"
            : "当前预览页（非全量合计）";

    private static string JoinNonEmpty(string separator, params string?[] parts)
        => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static void DrawStandard(
        PdfDocument document,
        ReportConfigurationPrintRenderDto render,
        (double Width, double Height) size,
        CancellationToken cancellationToken)
    {
        var columns = render.Columns
            .Select(c => new ReportConfigurationColumnDto(c.ColumnKey, c.Title, c.Type, c.CurrencyUnit))
            .ToList();
        var rows = render.Rows ?? new List<Dictionary<string, object?>>();
        var margin = MarginFor(size);
        var usableWidth = Mm(size.Width - margin * 2);
        var contentBottom = Mm(size.Height - margin);

        const double cellSize = 8;
        const double headerSize = 8;

        var companyFont = new XFont(FontFamily, Clamp(render.CompanyFontSize, 10, 32), XFontStyleEx.Bold);
        var titleFont = new XFont(FontFamily, Clamp(render.TitleFontSize, 10, 32), XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, 7, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, headerSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, cellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0x99, 0x99, 0x99), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xF2, 0xF2, 0xF2));

        var lineHeight = Mm(6);
        var headerHeight = Mm(7);
        var headHeight = Mm(margin) + (render.ShowCompanyHeader ? Mm(16) : 0) + Mm(18);
        var contentHeight = Math.Max(lineHeight, contentBottom - headHeight - headerHeight);

        var bands = ReportConfigurationPdfColumnLayout.Layout(columns, rows, usableWidth);
        if (bands.Count == 0)
            bands = new List<ReportConfigurationPdfColumnBand>
            {
                new(columns, Enumerable.Repeat(Mm(20), Math.Max(1, columns.Count)).ToArray(), 1),
            };

        var pages = new List<(int Cp, int Rp, int Start, int Count)>();
        if (rows.Count == 0)
        {
            pages.Add((0, 0, 0, 0));
        }
        else
        {
            for (var cp = 0; cp < bands.Count; cp++)
            {
                var rowPages = PartitionRows(bands[cp].Columns, bands[cp].Widths, rows, contentHeight, lineHeight);
                for (var rp = 0; rp < rowPages.Count; rp++)
                    pages.Add((cp, rp, rowPages[rp].Start, rowPages[rp].Count));
            }
        }

        if (rows.Count == 0)
        {
            var band = bands[0];
            var gfx = NewPage(document, size);
            var y = DrawStandardHead(gfx, render, size, margin, companyFont, titleFont, metaFont, 0, bands.Count, 0, 1, usableWidth);
            y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, band.Columns, band.Widths, y, margin);
            gfx.DrawString("没有符合条件的数据", cellFont, XBrushes.Black,
                new XRect(Mm(margin), y + Mm(2), usableWidth, lineHeight), XStringFormats.TopLeft);
            DrawStandardFooter(gfx, render, size, margin, 1, 1, metaFont);
            return;
        }

        var pageIndex = 0;
        foreach (var p in pages)
        {
            pageIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            var band = bands[p.Cp];
            var gfx = NewPage(document, size);
            var y = DrawStandardHead(gfx, render, size, margin, companyFont, titleFont, metaFont, p.Cp, bands.Count, p.Rp, 1, usableWidth);
            y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, band.Columns, band.Widths, y, margin);
            var rowY = y;
            for (var i = 0; i < p.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowIndex = p.Start + i;
                var rowHeight = RowHeightPoints(band.Columns, band.Widths, rows[rowIndex], rowIndex + 1, lineHeight);
                DrawDataRow(gfx, cellFont, borderPen, band.Columns, band.Widths, rows[rowIndex], rowIndex + 1, rowY, rowHeight, lineHeight, margin);
                rowY += rowHeight;
            }
            DrawStandardFooter(gfx, render, size, margin, pageIndex, pages.Count, metaFont);
        }
    }

    private static double DrawStandardHead(
        XGraphics gfx,
        ReportConfigurationPrintRenderDto render,
        (double Width, double Height) size,
        double margin,
        XFont companyFont,
        XFont titleFont,
        XFont metaFont,
        int columnPage,
        int columnPageCount,
        int rowPage,
        int rowPageCount,
        double usableWidth)
    {
        var left = Mm(margin);
        var y = Mm(margin);

        if (render.ShowCompanyHeader)
        {
            if (!string.IsNullOrWhiteSpace(render.CompanyName))
                gfx.DrawString(render.CompanyName, companyFont, XBrushes.Black,
                    new XRect(left, y, usableWidth, Mm(10)), XStringFormats.TopCenter);
            y += Mm(10);

            var sub = JoinNonEmpty("　", render.CompanyAddress,
                string.IsNullOrWhiteSpace(render.CompanyPhone) ? null : "电话：" + render.CompanyPhone);
            if (!string.IsNullOrWhiteSpace(sub))
                gfx.DrawString(sub, metaFont, XBrushes.Black,
                    new XRect(left, y, usableWidth, Mm(6)), XStringFormats.TopCenter);
            y += Mm(6);
        }

        var title = render.Title ?? render.DefinitionName ?? "打印";
        if (columnPageCount > 1)
            title += $"（列 {columnPage + 1}/{columnPageCount}）";
        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, y, usableWidth, Mm(12)), XStringFormats.TopCenter);
        y += Mm(12);

        var meta = $"{render.DefinitionName} · v{render.DefinitionVersion} · 行 {rowPage + 1}/{rowPageCount} · {CoverageText(render)}";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(left, y, usableWidth, Mm(6)), XStringFormats.TopLeft);
        y += Mm(6);

        return y;
    }

    private static void DrawStandardFooter(
        XGraphics gfx,
        ReportConfigurationPrintRenderDto render,
        (double Width, double Height) size,
        double margin,
        int pageIndex,
        int pageCount,
        XFont metaFont)
    {
        var text = JoinNonEmpty("　", render.FooterText, $"第 {pageIndex}/{pageCount} 页");
        if (string.IsNullOrWhiteSpace(text))
            return;

        gfx.DrawString(text, metaFont, XBrushes.Black,
            new XRect(Mm(margin), Mm(size.Height - margin - 8), Mm(size.Width - margin * 2), Mm(8)),
            XStringFormats.TopCenter);
    }

    private static double DrawColumnHeaders(
        XGraphics gfx,
        XFont font,
        XBrush brush,
        XPen pen,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        double[] widths,
        double y,
        double margin)
    {
        var left = Mm(margin);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(7));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(ReportConfigurationPdfExporter.HeaderText(columns[c]), font, XBrushes.Black, rect, XStringFormats.Center);
        }
        return y + Mm(7);
    }

    private static void DrawDataRow(
        XGraphics gfx,
        XFont font,
        XPen pen,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        double[] widths,
        Dictionary<string, object?> row,
        int rowOrdinal,
        double y,
        double rowHeight,
        double lineHeight,
        double margin)
    {
        var left = Mm(margin);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], rowHeight);
            gfx.DrawRectangle(pen, rect);

            var text = string.Equals(columns[c].Key, ReportConfigurationPdfColumnLayout.RowOrdinalColumnKey, StringComparison.Ordinal)
                ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                : ReportConfigurationPdfExporter.FormatCellValue(columns[c], row);

            var isNumber = string.Equals(columns[c].Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
                || string.Equals(columns[c].Key, ReportConfigurationPdfColumnLayout.RowOrdinalColumnKey, StringComparison.Ordinal);
            var format = isNumber ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format, lineHeight);
        }
    }

    private static void DrawCellText(
        XGraphics gfx,
        string text,
        XFont font,
        XRect rect,
        XStringFormat format,
        double lineHeight)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var maxWidth = Math.Max(0, rect.Width - 4);
        var lines = WrapText(text, maxWidth);
        var verticalPadding = (rect.Height - lines.Count * lineHeight) / 2;
        var lineRect = new XRect(rect.X + 2, rect.Y + Math.Max(0, verticalPadding), rect.Width - 4, lineHeight);
        foreach (var line in lines)
        {
            gfx.DrawString(line, font, XBrushes.Black, lineRect, format);
            lineRect.Y += lineHeight;
        }
    }

    private static double RowHeightPoints(
        IReadOnlyList<ReportConfigurationColumnDto> band,
        double[] widths,
        Dictionary<string, object?> row,
        int rowOrdinal,
        double lineHeight)
    {
        var lines = 1;
        for (var c = 0; c < band.Count; c++)
        {
            var text = string.Equals(band[c].Key, ReportConfigurationPdfColumnLayout.RowOrdinalColumnKey, StringComparison.Ordinal)
                ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                : ReportConfigurationPdfExporter.FormatCellValue(band[c], row);
            var count = WrapText(text, Math.Max(0, widths[c] - 4)).Count;
            if (count > lines)
                lines = count;
        }

        return lines * lineHeight + Mm(1.2);
    }

    private static List<(int Start, int Count)> PartitionRows(
        IReadOnlyList<ReportConfigurationColumnDto> band,
        double[] widths,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double contentHeight,
        double lineHeight)
    {
        var pages = new List<(int Start, int Count)>();
        var start = 0;
        var used = 0d;
        for (var i = 0; i < rows.Count; i++)
        {
            var height = RowHeightPoints(band, widths, rows[i], i + 1, lineHeight);
            if (i > start && used + height > contentHeight)
            {
                pages.Add((start, i - start));
                start = i;
                used = 0d;
            }
            used += height;
        }

        if (start < rows.Count || pages.Count == 0)
            pages.Add((start, rows.Count - start));

        return pages;
    }

    private static IReadOnlyList<string> WrapText(string text, double maxWidthPoints)
    {
        if (string.IsNullOrEmpty(text))
            return new[] { string.Empty };

        if (ReportConfigurationPdfExporter.EstimateWidthPoints(text, 8) <= maxWidthPoints)
            return new[] { text };

        var lines = new List<string>();
        var current = string.Empty;
        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (current.Length > 0 && ReportConfigurationPdfExporter.EstimateWidthPoints(candidate, 8) > maxWidthPoints)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
            lines.Add(current);
        if (lines.Count == 0)
            lines.Add(text);

        return lines;
    }

    private static double SumWidths(double[] widths, int count)
    {
        var sum = 0d;
        for (var i = 0; i < count; i++)
            sum += widths[i];
        return sum;
    }

    private static void DrawGrid(
        PdfDocument document,
        ReportConfigurationPrintRenderDto render,
        (double Width, double Height) size,
        CancellationToken cancellationToken)
    {
        var margin = MarginFor(size);
        var usableWidth = Mm(size.Width - margin * 2);
        var usableHeight = Mm(size.Height - margin * 2);

        foreach (var block in render.GridBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawGridBlock(document, block, size, margin, usableWidth, usableHeight);
        }
    }

    private static void DrawGridBlock(
        PdfDocument document,
        ReportPrintGridBlockDto block,
        (double Width, double Height) size,
        double margin,
        double usableWidth,
        double usableHeight)
    {
        var gfx = NewPage(document, size);

        var colWidths = block.ColWidths.ToArray();
        var rowHeights = block.RowHeights.ToArray();
        if (colWidths.Length == 0 || rowHeights.Length == 0)
            return;

        var totalWidth = colWidths.Sum();
        var totalHeight = rowHeights.Sum();
        var scale = Math.Min(1.0, Math.Min(usableWidth / totalWidth, usableHeight / totalHeight));

        var colX = new double[colWidths.Length + 1];
        colX[0] = Mm(margin);
        for (var i = 0; i < colWidths.Length; i++)
            colX[i + 1] = colX[i] + colWidths[i] * scale;

        var rowY = new double[rowHeights.Length + 1];
        rowY[0] = Mm(margin);
        for (var i = 0; i < rowHeights.Length; i++)
            rowY[i + 1] = rowY[i] + rowHeights[i] * scale;

        var borderPen = new XPen(XColor.FromArgb(0x99, 0x99, 0x99), 0.4);

        foreach (var cell in block.Cells)
        {
            var x0 = colX[Math.Clamp(cell.Col, 0, colWidths.Length)];
            var x1 = colX[Math.Clamp(cell.Col + cell.ColSpan, 0, colWidths.Length)];
            var y0 = rowY[Math.Clamp(cell.Row, 0, rowHeights.Length)];
            var y1 = rowY[Math.Clamp(cell.Row + cell.RowSpan, 0, rowHeights.Length)];
            var rect = new XRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));

            var background = ParseXColor(cell.Background);
            if (background is not null)
                gfx.DrawRectangle(new XSolidBrush(background.Value), rect);
            if (cell.Border != 0)
                gfx.DrawRectangle(borderPen, rect);

            if (string.IsNullOrEmpty(cell.Text))
                continue;

            var style = cell.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular;
            var font = new XFont(FontFamily, Math.Clamp(cell.FontSize, 6, 48), style);
            var brush = XBrushes.Black;
            var color = ParseXColor(cell.Color);
            if (color is not null)
                brush = new XSolidBrush(color.Value);

            var format = cell.Align switch
            {
                "center" => XStringFormats.Center,
                "right" => XStringFormats.CenterRight,
                _ => XStringFormats.CenterLeft,
            };

            const double pad = 2;
            gfx.DrawString(cell.Text, font, brush,
                new XRect(rect.X + pad, rect.Y, Math.Max(0, rect.Width - pad * 2), rect.Height), format);
        }

        if (!string.IsNullOrWhiteSpace(block.FooterText))
        {
            var footerFont = new XFont(FontFamily, 8, XFontStyleEx.Regular);
            gfx.DrawString(block.FooterText, footerFont, XBrushes.Black,
                new XRect(Mm(margin), rowY[rowHeights.Length] + Mm(2), usableWidth, Mm(8)),
                XStringFormats.TopCenter);
        }
    }

    private static XColor? ParseXColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var value = hex.Trim().TrimStart('#');
        if (value.Length == 3)
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
        if (value.Length != 6)
            return null;

        try
        {
            return XColor.FromArgb(0xFF,
                byte.Parse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        catch (FormatException)
        {
            return null;
        }
    }





}
