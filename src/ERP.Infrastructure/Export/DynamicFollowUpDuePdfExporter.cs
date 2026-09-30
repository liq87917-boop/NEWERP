using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态跟进提醒报表（ERP-196）PDF 导出：复用 ERP-193 有界授权预览与选定列顺序，以 PDFsharp 6.2.4 分页渲染。
/// 选定字段、中文标签、到期证据（下次跟进日期 / 到期天数 / 到期状态）与只读 / 边界 / 免责文案、空页说明显式保留；
/// 宽列集按可用页宽贪心拆成多个「列页」，行数超出时按「行页」拆分，避免列被裁切。
/// 中文字体固定使用 Windows 黑体（SimHei），与其它报表 PDF 共用共享解析器（<see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicFollowUpDuePdfExporter
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

    /// <summary>空页显式说明（与 <see cref="DynamicFollowUpDueReportRules.EmptyText"/> 同源）</summary>
    private const string EmptyFallbackText = "没有符合筛选条件的跟进提醒证据";

    /// <summary>分组计数区块：标签 / 数量两列（仅统计当前页，不重算；none 不渲染）</summary>
    private const double GroupLabelWidthMm = 120;
    private const double GroupCountWidthMm = 60;
    private const double GroupTitleHeightMm = 7;

    /// <summary>分组计数区块标题（与 Excel 导出「分组计数」同口径）</summary>
    private const string GroupSectionTitle = "本页分组计数（仅统计当前页，不重算）";

    /// <summary>分组计数空页显式说明（与 Excel 导出空页说明同源）</summary>
    private const string GroupEmptyNoteText = "当前页没有符合分组条件的跟进提醒证据（空页）";

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicFollowUpDueReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicFollowUpDueReportPageDto page, string? fontPath)
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
        document.Info.Title = "跟进提醒报表";

        DrawReport(document, page);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicFollowUpDueReportPageDto page)
    {
        var columns = page.Columns ?? new List<DynamicFollowUpDueReportFieldDto>();
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
                gfx.DrawString(
                    string.IsNullOrWhiteSpace(page.EmptyText) ? EmptyFallbackText : page.EmptyText,
                    cellFont, XBrushes.Black,
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

            DrawGroupSection(document, gfxList, titleFont, metaFont, headerFont, cellFont,
                borderPen, headerBrush, page, headNotes, contentBottom);
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

    /// <summary>页头口径：只读 / 边界 / 免责文案按可用宽度折行（跟进提醒口径，仅按白名单字段派生到期证据）</summary>
    private static List<string> BuildHeadNotes(
        DynamicFollowUpDueReportPageDto page, XFont metaFont, double usableWidth)
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
        DynamicFollowUpDueReportFieldDto column, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize);
        foreach (var row in rows)
            max = Math.Max(max, EstimateWidthPoints(FormatFieldCell(column, row), CellSize));

        return Math.Clamp(max + 6, Mm(MinColumnWidthMm), Mm(MaxColumnWidthMm));
    }

    /// <summary>把选定列按可用页宽贪心拆分为多个「列页」，保证每个列页总宽不超页宽（列不裁切）</summary>
    private static List<List<DynamicFollowUpDueReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicFollowUpDueReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicFollowUpDueReportFieldDto>> { new() };

        var pages = new List<List<DynamicFollowUpDueReportFieldDto>>();
        var current = new List<DynamicFollowUpDueReportFieldDto>();
        var currentWidth = 0d;

        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && currentWidth + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicFollowUpDueReportFieldDto>();
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
        IReadOnlyList<DynamicFollowUpDueReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = ColumnWidthPoints(columns[c], rows);
        return widths;
    }

    private static double DrawPageHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicFollowUpDueReportPageDto page,
        int columnPage, int columnPageCount, int rowPage, int rowPageCount,
        IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = "跟进提醒报表";
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
        IReadOnlyList<DynamicFollowUpDueReportFieldDto> columns, double[] widths, double y)
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
        IReadOnlyList<DynamicFollowUpDueReportFieldDto> columns, double[] widths,
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

    // ==================== 分组计数（ERP-199） ====================

    /// <summary>是否需要渲染「本页分组计数」区块：仅 dueStatus / salesman 分组；none 保持仅明细布局</summary>
    private static bool HasGroupSection(DynamicFollowUpDueReportPageDto page)
        => !string.Equals(page.GroupBy, DynamicFollowUpDueReportRules.GroupNone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 明细之后追加「本页分组计数」区块（状态 / 业务员标签与数量，仅统计当前授权预览页，绝不重算 / 外推）；
    /// 始终以新页开始（明细与分组计数之间以分页符隔开），分组行超出页高时按分页继续、每页重复表头，保证标签 / 数量可读。
    /// </summary>
    private static void DrawGroupSection(
        PdfDocument document,
        List<XGraphics> gfxList,
        XFont titleFont, XFont metaFont, XFont headerFont, XFont cellFont,
        XPen borderPen, XBrush headerBrush,
        DynamicFollowUpDueReportPageDto page,
        IReadOnlyList<string> headNotes,
        double contentBottom)
    {
        if (!HasGroupSection(page))
            return;

        var emptyPage = page.Rows is null || page.Rows.Count == 0;
        var groupRows = BuildGroupCountRows(page.Groups);

        var labelWidth = Mm(GroupLabelWidthMm);
        var countWidth = Mm(GroupCountWidthMm);
        var titleHeight = Mm(GroupTitleHeightMm);
        var headerHeight = Mm(HeaderRowHeightMm);
        var rowHeight = Mm(DataRowHeightMm);
        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var rowIndex = 0;
        while (true)
        {
            var gfx = NewPage(document);
            gfxList.Add(gfx);

            var y = DrawGroupPageHead(gfx, titleFont, metaFont, page, headNotes);
            gfx.DrawString(GroupSectionTitle, headerFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y, usableWidth, titleHeight), XStringFormats.TopLeft);
            y += titleHeight;

            y = DrawGroupHeaderRow(gfx, headerFont, headerBrush, borderPen, labelWidth, countWidth, y);

            while (rowIndex < groupRows.Count && y + rowHeight <= contentBottom)
            {
                DrawGroupRow(gfx, cellFont, borderPen, labelWidth, countWidth, groupRows[rowIndex], y);
                y += rowHeight;
                rowIndex++;
            }

            if (rowIndex < groupRows.Count)
                continue;

            if (emptyPage)
            {
                if (y + rowHeight > contentBottom)
                {
                    gfx = NewPage(document);
                    gfxList.Add(gfx);
                    y = DrawGroupPageHead(gfx, titleFont, metaFont, page, headNotes);
                    gfx.DrawString(GroupSectionTitle, headerFont, XBrushes.Black,
                        new XRect(Mm(MarginLeftMm), y, usableWidth, titleHeight), XStringFormats.TopLeft);
                    y += titleHeight;
                    y = DrawGroupHeaderRow(gfx, headerFont, headerBrush, borderPen, labelWidth, countWidth, y);
                }

                gfx.DrawString(GroupEmptyNoteText, cellFont, XBrushes.Black,
                    new XRect(Mm(MarginLeftMm), y, usableWidth, rowHeight), XStringFormats.TopLeft);
            }

            break;
        }
    }

    /// <summary>分组计数页头：标题 + 分页元信息 + 只读 / 边界 / 免责文案（与明细页同口径，保证可读与免责声明齐全）</summary>
    private static double DrawGroupPageHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicFollowUpDueReportPageDto page, IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        gfx.DrawString("跟进提醒报表（本页分组计数）", titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"共 {page.Total.ToString(CultureInfo.InvariantCulture)} 条"
            + $" · 第 {page.Page.ToString(CultureInfo.InvariantCulture)}/{page.TotalPages.ToString(CultureInfo.InvariantCulture)} 页"
            + " · 分组计数（仅当前页）";
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

    private static double DrawGroupHeaderRow(
        XGraphics gfx, XFont font, XBrush brush, XPen pen,
        double labelWidth, double countWidth, double y)
    {
        var left = Mm(MarginLeftMm);
        var labelRect = new XRect(left, y, labelWidth, Mm(HeaderRowHeightMm));
        var countRect = new XRect(left + labelWidth, y, countWidth, Mm(HeaderRowHeightMm));

        gfx.DrawRectangle(pen, brush, labelRect);
        gfx.DrawString("分组", font, XBrushes.Black, labelRect, XStringFormats.Center);
        gfx.DrawRectangle(pen, brush, countRect);
        gfx.DrawString("数量", font, XBrushes.Black, countRect, XStringFormats.Center);

        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawGroupRow(
        XGraphics gfx, XFont font, XPen pen,
        double labelWidth, double countWidth,
        (string Label, string Count) groupRow, double y)
    {
        var left = Mm(MarginLeftMm);
        var labelRect = new XRect(left, y, labelWidth, Mm(DataRowHeightMm));
        var countRect = new XRect(left + labelWidth, y, countWidth, Mm(DataRowHeightMm));

        gfx.DrawRectangle(pen, labelRect);
        DrawCellText(gfx, groupRow.Label, font, labelRect, XStringFormats.CenterLeft);
        gfx.DrawRectangle(pen, countRect);
        DrawCellText(gfx, groupRow.Count, font, countRect, XStringFormats.CenterRight);
    }

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序与到期证据）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicFollowUpDueReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatFieldCell(col, row));
        return cells;
    }

    /// <summary>
    /// 把「当前授权预览页」的分组计数映射为 PDF 分组行（标签 + 数量文本）；只照实呈现 <paramref name="groups"/>，
    /// 绝不重算 / 外推为整表总数（与绘制共用同一口径，供测试验证标签与数量）。
    /// </summary>
    public static IReadOnlyList<(string Label, string Count)> BuildGroupCountRows(
        IReadOnlyList<DynamicFollowUpDueReportGroupDto>? groups)
    {
        var source = groups ?? new List<DynamicFollowUpDueReportGroupDto>();
        var rows = new List<(string Label, string Count)>(source.Count);
        foreach (var g in source)
            rows.Add((g.Label ?? string.Empty, g.Count.ToString(CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>
    /// 字段感知的单元格文本：到期证据（下次跟进日期 / 到期天数 / 到期状态）已由预览派生并随行返回，照实格式化；
    /// 其余字段按通用口径格式化。为 null 时显示空文本（与既有 Excel 导出口径一致，绝不推算 / 修复）。
    /// </summary>
    public static string FormatFieldCell(DynamicFollowUpDueReportFieldDto field, Dictionary<string, object?> row)
    {
        var value = row.TryGetValue(field.Key, out var v) ? v : null;

        if (value is null or DBNull)
            return string.Empty;

        return FormatCellValue(value);
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

