using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态客户出货量证据报表（ERP-234）「全匹配汇总」PDF 下载：复用 ERP-233 全匹配汇总的同一有界授权预览管线，
/// 把服务端在全部匹配客户 × 原币证据行（分页与选定列投影之前）上派生的 ERP-232 全匹配汇总渲染为中文 PDF。
/// 分「原币金额汇总」与「精确单位数量汇总」两块：原币汇总照实呈现签名金额 / 去重客户数 / 已审核订单数，
/// 精确单位汇总照实呈现签名数量 / 明细条数，绝不携带货币金额、绝不跨币种 / 跨单位合计、绝不重复金额到单位行；
/// 未知币种金额 / 未知单位数量显式「未知」（绝不回落为 0），缺失 / 不完整来源显式保留完整度原因与不完整桶数；
/// 日期 / 应用筛选 / 全部匹配覆盖 / 来源上限 / 原币 / 单位 / 未知 / 来源证据 / 只读声明上下文显式保留；绝不含客户明细行。
/// 行数超出按「行页」拆分、宽列集按可用页宽拆成多个「列页」，每页重复标题与表头；空匹配显式说明。
/// 中文字体固定使用 Windows 黑体（SimHei，共享解析器 <see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失或渲染失败时显式失败（不产出乱码 / 缺字 / 损坏 PDF）。
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicCustomerShipmentSummaryPdfExporter
{
    /// <summary>固定使用的中文字体族（Windows 黑体，共享解析器 SimHeiPdfFontResolver）</summary>
    private const string FontFamily = SimHeiPdfFontResolver.FontFamily;

    /// <summary>汇总 PDF 标题（区别于当前页明细 PDF 的「客户出货量证据报表（原币证据）」）</summary>
    private const string ReportTitle = "客户出货量全匹配汇总";

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

    /// <summary>原币金额汇总块空证据显式说明</summary>
    private const string CurrencyEmptyText =
        "没有符合所选日期范围与数据范围的原币金额汇总证据（已审核、未删除销售订单）";

    /// <summary>精确单位数量汇总块空证据显式说明</summary>
    private const string UnitEmptyText =
        "没有符合所选日期范围与数据范围的精确单位数量汇总证据（已审核、未删除销售订单明细）";

    /// <summary>两块均为空时的兜底说明</summary>
    private const string EmptyFallbackText = "没有符合所选日期范围与数据范围的全匹配汇总证据";

    /// <summary>导出全匹配汇总为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicCustomerShipmentSummaryDto summary, DynamicCustomerShipmentReportPageDto page)
        => Export(summary, page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出全匹配汇总为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(
        DynamicCustomerShipmentSummaryDto summary,
        DynamicCustomerShipmentReportPageDto page,
        string? fontPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
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

            DrawReport(document, summary, page);

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
            throw new BusinessException(
                "PDF 下载失败：渲染全匹配汇总时发生错误，未生成任何文件，请稍后重试。",
                ErrorCodes.InternalError);
        }
    }

    // ==================== 绘制 ====================

    private static void DrawReport(
        PdfDocument document,
        DynamicCustomerShipmentSummaryDto summary,
        DynamicCustomerShipmentReportPageDto page)
    {
        var currencyColumns = summary.CurrencyColumns ?? new List<DynamicCustomerShipmentReportFieldDto>();
        var unitColumns = summary.UnitColumns ?? new List<DynamicCustomerShipmentReportFieldDto>();
        var currencyRows = (summary.CurrencyRows ?? new List<DynamicCustomerShipmentCurrencySummaryDto>())
            .Select(DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow).ToList();
        var unitRows = (summary.UnitRows ?? new List<DynamicCustomerShipmentUnitSummaryDto>())
            .Select(DynamicCustomerShipmentReportRules.BuildUnitSummaryExportRow).ToList();

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

        // 页头口径（日期 / 筛选 / 覆盖范围 / 原币 / 单位 / 未知 / 完整度 / 原币证据 / 来源 / 来源上限 / 只读）
        // 对每个列页完全相同，故行页拆分在各列页保持一致
        var headNotes = BuildHeadNotes(page, summary, metaFont, usableWidth);
        var headHeightMm = TitleHeightMm + MetaHeightMm + headNotes.Count * NoteLineHeightMm;
        var rowsPerPage = Math.Max(1, (int)Math.Floor(
            (contentBottom - Mm(MarginTopMm) - Mm(headHeightMm) - headerHeight) / dataRowHeight));

        var graphics = new List<XGraphics>();
        try
        {
            if (currencyRows.Count == 0 && unitRows.Count == 0)
            {
                var gfx = NewPage(document);
                graphics.Add(gfx);
                var y = DrawPageHead(gfx, titleFont, metaFont, ReportTitle, null, 0, 0, 1, 0, 1, headNotes);
                var text = string.IsNullOrWhiteSpace(page.EmptyText) ? EmptyFallbackText : page.EmptyText;
                gfx.DrawString(text, cellFont, XBrushes.Black,
                    new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, dataRowHeight), XStringFormats.TopLeft);
            }
            else
            {
                RenderSection(document, graphics, currencyColumns, currencyRows,
                    DynamicCustomerShipmentSummaryRules.CurrencySummaryTitle,
                    summary.CurrencyRows?.Count ?? 0, "币种", CurrencyEmptyText,
                    page, headNotes, rowsPerPage, usableWidth, headerHeight, dataRowHeight,
                    titleFont, metaFont, headerFont, cellFont, headerBrush, borderPen);

                RenderSection(document, graphics, unitColumns, unitRows,
                    DynamicCustomerShipmentSummaryRules.UnitSummaryTitle,
                    summary.UnitRows?.Count ?? 0, "单位分组", UnitEmptyText,
                    page, headNotes, rowsPerPage, usableWidth, headerHeight, dataRowHeight,
                    titleFont, metaFont, headerFont, cellFont, headerBrush, borderPen);
            }
        }
        finally
        {
            foreach (var g in graphics)
                g.Dispose();
        }
    }

    /// <summary>渲染一个汇总块：行数超出按「行页」拆分、宽列集按「列页」拆分，每页重复标题与表头；空块显式说明</summary>
    private static void RenderSection(
        PdfDocument document,
        List<XGraphics> graphics,
        IReadOnlyList<DynamicCustomerShipmentReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        string title,
        int count,
        string countLabel,
        string emptyText,
        DynamicCustomerShipmentReportPageDto page,
        IReadOnlyList<string> headNotes,
        int rowsPerPage,
        double usableWidth,
        double headerHeight,
        double dataRowHeight,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont,
        XSolidBrush headerBrush,
        XPen borderPen)
    {
        var columnPages = SplitColumnPages(columns, rows, usableWidth);
        var rowPageCount = rows.Count == 0 ? 1 : (int)Math.Ceiling(rows.Count / (double)rowsPerPage);

        if (rows.Count == 0)
        {
            var gfx = NewPage(document);
            graphics.Add(gfx);
            var cols = columnPages[0];
            var widths = ComputeColumnWidths(cols, rows);
            var y = DrawPageHead(gfx, titleFont, metaFont, title, countLabel, count,
                0, columnPages.Count, 0, rowPageCount, headNotes);
            y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);
            var text = string.IsNullOrWhiteSpace(page.EmptyText) ? emptyText : page.EmptyText;
            gfx.DrawString(text, cellFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, dataRowHeight), XStringFormats.TopLeft);
            return;
        }

        for (var cp = 0; cp < columnPages.Count; cp++)
        {
            var cols = columnPages[cp];
            var widths = ComputeColumnWidths(cols, rows);
            for (var rp = 0; rp < rowPageCount; rp++)
            {
                var gfx = NewPage(document);
                graphics.Add(gfx);
                var y = DrawPageHead(gfx, titleFont, metaFont, title, countLabel, count,
                    cp, columnPages.Count, rp, rowPageCount, headNotes);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);

                var start = rp * rowsPerPage;
                var take = Math.Min(rowsPerPage, rows.Count - start);
                for (var i = 0; i < take; i++)
                    DrawDataRow(gfx, cellFont, borderPen, cols, widths, rows[start + i], y + i * dataRowHeight);
            }
        }
    }

    private static XGraphics NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width = new XUnit(PageWidthMm, XGraphicsUnit.Millimeter);
        page.Height = new XUnit(PageHeightMm, XGraphicsUnit.Millimeter);
        return XGraphics.FromPdfPage(page);
    }

    /// <summary>
    /// 页头口径：日期范围 / 应用筛选 / 覆盖范围 / 原币 / 单位 / 未知 / 完整度（含不完整桶数与原因）/
    /// 原币证据 / 来源证据 / 来源上限 / 只读声明；对每个列页完全相同，始终可见（即使对应列被取消选择）。
    /// </summary>
    private static List<string> BuildHeadNotes(
        DynamicCustomerShipmentReportPageDto page,
        DynamicCustomerShipmentSummaryDto summary,
        XFont metaFont,
        double usableWidth)
    {
        var lines = new List<string>();

        AddWrapped(lines,
            $"日期范围：{page.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 至 "
            + $"{page.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            metaFont, usableWidth);

        if (!string.IsNullOrWhiteSpace(page.FilterText))
            AddWrapped(lines, $"筛选条件：{page.FilterText}", metaFont, usableWidth);

        AddWrapped(lines, summary.CoverageText, metaFont, usableWidth);
        AddWrapped(lines, page.CurrencyContextText, metaFont, usableWidth);
        AddWrapped(lines, page.UnitContextText, metaFont, usableWidth);
        AddWrapped(lines, page.UnknownContextText, metaFont, usableWidth);

        var completenessText = summary.CompletenessReasons is { Count: > 0 }
            ? $"{summary.IncompleteBucketCount} 个不完整桶；{string.Join("；", summary.CompletenessReasons)}"
            : "数量证据完整";
        AddWrapped(lines, $"完整度：{completenessText}", metaFont, usableWidth);

        AddWrapped(lines, DynamicCustomerShipmentReportRules.ContextCurrencyEvidenceText, metaFont, usableWidth);
        AddWrapped(lines, page.SourceContextText, metaFont, usableWidth);
        AddWrapped(lines, page.SourceLimitText, metaFont, usableWidth);
        AddWrapped(lines, page.ReadOnlyText, metaFont, usableWidth);

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

    private static double DrawPageHead(
        XGraphics gfx,
        XFont titleFont,
        XFont metaFont,
        string title,
        string? countLabel,
        int count,
        int columnPageIndex,
        int columnPageCount,
        int rowPageIndex,
        int rowPageCount,
        IReadOnlyList<string> noteLines)
    {
        var left = Mm(MarginLeftMm);
        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var y = Mm(MarginTopMm);

        var displayTitle = title;
        if (columnPageCount > 1 || rowPageCount > 1)
            displayTitle += $"（第 {rowPageIndex + 1}/{rowPageCount} 行页 · 第 {columnPageIndex + 1}/{columnPageCount} 列页）";

        gfx.DrawString(displayTitle, titleFont, XBrushes.Black,
            new XRect(left, y, usableWidth, Mm(TitleHeightMm)), XStringFormats.TopLeft);
        y += Mm(TitleHeightMm);

        var meta = countLabel is null
            ? "全匹配汇总"
            : $"共 {count.ToString(CultureInfo.InvariantCulture)} {countLabel} · "
            + $"列页 {columnPageIndex + 1}/{columnPageCount} · 行页 {rowPageIndex + 1}/{rowPageCount}";
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
        IReadOnlyList<DynamicCustomerShipmentReportFieldDto> columns,
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
        IReadOnlyList<DynamicCustomerShipmentReportFieldDto> columns,
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

            // 已知金额 / 计数 / 数量右对齐；未知（「未知」文本）左对齐，二者明显区分
            var format = (col.DataType == "number" || col.DataType == "money") && value is not null
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format);
        }
    }

    // ==================== 列页拆分与列宽 ====================

    /// <summary>把汇总列按可用页宽贪心拆成多个「列页」，避免超宽列集被挤压 / 裁切</summary>
    private static List<List<DynamicCustomerShipmentReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicCustomerShipmentReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicCustomerShipmentReportFieldDto>> { new() };

        var pages = new List<List<DynamicCustomerShipmentReportFieldDto>>();
        var current = new List<DynamicCustomerShipmentReportFieldDto>();
        var used = 0d;
        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && used + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicCustomerShipmentReportFieldDto>();
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
        IReadOnlyList<DynamicCustomerShipmentReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
        => columns.Select(c => ColumnWidthPoints(c, rows)).ToArray();

    private static double ColumnWidthPoints(
        DynamicCustomerShipmentReportFieldDto column,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize) + 8;
        foreach (var row in rows)
        {
            var value = row.TryGetValue(column.Key, out var v) ? v : null;
            var text = value is null or DBNull
                ? DynamicCustomerShipmentReportRules.UnknownValueText
                : FormatCellValue(value);
            var width = EstimateWidthPoints(text, CellSize) + 8;
            if (width > max)
                max = width;
        }

        var mm = max / PointsPerMillimeter;
        return Mm(Math.Clamp(mm, MinColumnWidthMm, MaxColumnWidthMm));
    }

    // ==================== 单元格格式化 ====================

    /// <summary>字段感知的单元格文本：null 金额 / 数量显式渲染为「未知」（绝不写成 0），已知数值按数值格式呈现</summary>
    public static string FormatFieldCell(DynamicCustomerShipmentReportFieldDto field, Dictionary<string, object?> row)
    {
        var value = row.TryGetValue(field.Key, out var v) ? v : null;
        if (value is null or DBNull)
            return DynamicCustomerShipmentReportRules.UnknownValueText;
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

