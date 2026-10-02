using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态订单利润暂估报表「分币种汇总」PDF 下载（ERP-226，只读）：复用 ERP-225 分币种汇总 Excel 的同一有界授权预览管线，
/// 把服务端在「全部匹配已审核销售订单」（ERP-223 筛选后、分页前）上派生的 ERP-224 分币种汇总渲染为中文 PDF。
/// 稳定币种行（原币币种 / 已审核订单数 / 销售额(原币)）照实呈现：已知币种计数与签名销售额为数值，未知币种金额显式「未知」
/// （绝不回落为 0）；绝不合并币种、绝不追加跨币种金额合计、绝不含成本 / 利润 / 当前价估算或换算、绝不声称已实现/已收收入。
/// 日期 / 筛选 / 全部匹配 / 来源上限 / 未知成本依据 / 覆盖范围 / 原币证据 / 只读声明上下文显式保留；
/// 行数超出按「行页」拆分、宽列集按可用页宽拆成多个「列页」，空汇总显式说明。
/// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器 <see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失或渲染失败时显式失败（不产出乱码 / 缺字 / 损坏 PDF）。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class OrderProfitCurrencySummaryPdfExporter
{
    /// <summary>固定使用的中文字体族（Windows 黑体，共享解析器 SimHeiPdfFontResolver）</summary>
    private const string FontFamily = SimHeiPdfFontResolver.FontFamily;

    /// <summary>汇总 PDF 标题（区别于当前页明细 PDF 的「订单利润暂估表」）</summary>
    private const string SummaryTitle = "订单利润暂估分币种汇总";

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

    /// <summary>空汇总显式说明（与订单利润暂估口径同源兜底）</summary>
    private const string EmptyFallbackText = "没有符合所选日期范围与数据范围的分币种汇总数据（按订单原币合并全部匹配已审核订单）";

    /// <summary>导出分币种汇总为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicOrderProfitEstimateSummaryDto summary, DynamicOrderProfitEstimateReportPageDto page)
        => Export(summary, page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出分币种汇总为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(
        DynamicOrderProfitEstimateSummaryDto summary,
        DynamicOrderProfitEstimateReportPageDto page,
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
            document.Info.Title = SummaryTitle;

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
            // 渲染失败时绝不返回损坏 / 半成品 PDF，仅给出清晰错误
            throw new BusinessException(
                "PDF 下载失败：渲染分币种汇总时发生错误，未生成任何文件，请稍后重试。",
                ErrorCodes.InternalError);
        }
    }

    // ==================== 绘制 ====================

    private static void DrawReport(
        PdfDocument document, DynamicOrderProfitEstimateSummaryDto summary, DynamicOrderProfitEstimateReportPageDto page)
    {
        var columns = summary.Columns ?? new List<DynamicOrderProfitEstimateReportFieldDto>();
        var rows = summary.Rows ?? new List<DynamicOrderProfitEstimateCurrencySummaryDto>();
        var rowDicts = rows.Select(DynamicOrderProfitEstimateReportRules.BuildSummaryExportRow).ToList();

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

        // 页头口径（日期 / 筛选 / 覆盖范围 / 原币 / 未知成本依据 / 原币证据 / 来源上限 / 只读）对每个列页完全相同，
        // 故行页拆分在各列页保持一致
        var headNotes = BuildHeadNotes(page, summary, metaFont, usableWidth);
        var headHeightMm = TitleHeightMm + MetaHeightMm + headNotes.Count * NoteLineHeightMm;
        var rowsPerPage = Math.Max(1, (int)Math.Floor(
            (contentBottom - Mm(MarginTopMm) - Mm(headHeightMm) - headerHeight) / dataRowHeight));

        var columnPages = SplitColumnPages(columns, rowDicts, usableWidth);
        var rowPageCount = rowDicts.Count == 0 ? 1 : (int)Math.Ceiling(rowDicts.Count / (double)rowsPerPage);

        var gfxList = new List<XGraphics>();
        try
        {
            if (rowDicts.Count == 0)
            {
                var gfx = NewPage(document);
                gfxList.Add(gfx);
                var cols = columnPages[0];
                var widths = ComputeColumnWidths(cols, rowDicts);
                var y = DrawPageHead(gfx, titleFont, metaFont, summary.CurrencyCount, 0, columnPages.Count, 0, 1, headNotes);
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
                    var widths = ComputeColumnWidths(cols, rowDicts);
                    for (var rp = 0; rp < rowPageCount; rp++)
                    {
                        var gfx = NewPage(document);
                        gfxList.Add(gfx);
                        var y = DrawPageHead(gfx, titleFont, metaFont, summary.CurrencyCount, cp, columnPages.Count, rp, rowPageCount, headNotes);
                        y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);

                        var start = rp * rowsPerPage;
                        var count = Math.Min(rowsPerPage, rowDicts.Count - start);
                        for (var i = 0; i < count; i++)
                        {
                            DrawDataRow(gfx, cellFont, borderPen, cols, widths, rowDicts[start + i],
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

    /// <summary>页头口径：日期窗口 + 应用筛选 + 覆盖范围 + 原币口径 + 未知成本依据 + 原币证据 + 来源上限 + 只读声明按可用宽度折行</summary>
    private static List<string> BuildHeadNotes(
        DynamicOrderProfitEstimateReportPageDto page,
        DynamicOrderProfitEstimateSummaryDto summary,
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
        AddWrapped(lines, DynamicOrderProfitEstimateReportRules.MissingCostBasisText, metaFont, usableWidth);
        AddWrapped(lines, DynamicOrderProfitEstimateReportRules.ContextOriginalCurrencyEvidenceText, metaFont, usableWidth);
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
        XGraphics gfx, XFont titleFont, XFont metaFont,
        int currencyCount,
        int columnPage, int columnPageCount, int rowPage, int rowPageCount,
        IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = SummaryTitle;
        if (columnPageCount > 1)
            title += $"（列 {columnPage + 1}/{columnPageCount}）";

        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"共 {currencyCount.ToString(CultureInfo.InvariantCulture)} 币种"
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
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns, double[] widths, double y)
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
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns, double[] widths,
        Dictionary<string, object?> row, double y)
    {
        var left = Mm(MarginLeftMm);
        var cells = BuildRowCells(columns, row);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            var format = columns[c].DataType is "number"
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, cells[c], font, rect, format);
        }
    }

    /// <summary>按选定汇总列顺序把一条分币种汇总行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序与金额格式化）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        DynamicOrderProfitEstimateCurrencySummaryDto row)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);

        var dict = DynamicOrderProfitEstimateReportRules.BuildSummaryExportRow(row);
        return BuildRowCells(columns, dict);
    }

    private static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
        {
            var value = row.TryGetValue(col.Key, out var v) ? v : null;
            cells.Add(FormatCellValue(value));
        }
        return cells;
    }

    /// <summary>把汇总单元格值转成 PDF 单元格文本（中文布尔 / 日期 / 数值口径与当前页明细 PDF 保持一致）</summary>
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

    /// <summary>按内容估算列宽（点），夹在最小 / 最大列宽之间，超宽列集由「列页」拆分而非挤压</summary>
    private static double ColumnWidthPoints(
        DynamicOrderProfitEstimateReportFieldDto column, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize);
        foreach (var row in rows)
        {
            var value = row.TryGetValue(column.Key, out var v) ? v : null;
            max = Math.Max(max, EstimateWidthPoints(FormatCellValue(value), CellSize));
        }

        return Math.Clamp(max + 6, Mm(MinColumnWidthMm), Mm(MaxColumnWidthMm));
    }

    /// <summary>把选定列按可用页宽贪心拆分为多个「列页」，保证每个列页总宽不超页宽（列不裁切）</summary>
    private static List<List<DynamicOrderProfitEstimateReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicOrderProfitEstimateReportFieldDto>> { new() };

        var pages = new List<List<DynamicOrderProfitEstimateReportFieldDto>>();
        var current = new List<DynamicOrderProfitEstimateReportFieldDto>();
        var currentWidth = 0d;

        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && currentWidth + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicOrderProfitEstimateReportFieldDto>();
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
        IReadOnlyList<DynamicOrderProfitEstimateReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = ColumnWidthPoints(columns[c], rows);
        return widths;
    }

    private static double SumWidths(double[] values, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++)
            total += values[i];
        return total;
    }

    private static double Mm(double millimeters) => millimeters * PointsPerMillimeter;
}

