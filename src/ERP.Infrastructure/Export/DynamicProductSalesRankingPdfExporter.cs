using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态商品销量排名报表（ERP-214）PDF 导出：复用 ERP-213 有界、已授权预览与选定列顺序，以 PDFsharp 6.2.4 分页渲染。
/// 选定字段（发货数量证据白名单）、中文标签、日期窗口 / Top 限定 / 单位口径 / 已审核发货证据口径与空结果说明显式保留；
/// 宽列集按可用页宽贪心拆成多个「列页」，行数超出时按「行页」拆分，避免列 / 行被裁切。
/// <para>口径边界：只渲染请求选定、且来自服务端白名单的发货数量证据列（排名 / 商品Id / 编码 / 名称 / 规格 / 单位 / 发货数量），
/// 绝不包含当前价估算金额，绝不跨单位合计数量。</para>
/// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器 <see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失或渲染失败时显式失败（不产出乱码 / 缺字 / 损坏 PDF）。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicProductSalesRankingPdfExporter
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

    /// <summary>空页显式说明（与 DynamicProductSalesRankingReportRules.EmptyText 同源兜底）</summary>
    private const string EmptyFallbackText = "没有符合日期范围与数据范围的已审核发货证据（或记录已被软删除）";

    /// <summary>报表标题</summary>
    private const string ReportTitle = "商品销量排名报表（发货数量证据）";

    /// <summary>单位汇总分区标题（ERP-218：仅 unit 分组时渲染，绝不跨单位合计数量、绝不追加金额）</summary>
    private const string UnitSummaryTitle = "商品销量排名报表（单位汇总 · 发货数量证据）";

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicProductSalesRankingReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicProductSalesRankingReportPageDto page, string? fontPath)
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
            throw new BusinessException(
                "PDF 导出失败：渲染报表时发生错误，未生成任何文件，请稍后重试。",
                ErrorCodes.InternalError);
        }
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicProductSalesRankingReportPageDto page)
    {
        var columns = page.Columns ?? new List<DynamicProductSalesRankingReportFieldDto>();
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

        // 页头口径（只读 / 边界 / 免责 / 单位 / 发货证据）对每个列页完全相同，故行页拆分在各列页保持一致
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
            if (DynamicProductSalesRankingReportRules.IsUnitGrouping(page.GroupBy))
            {
                DrawUnitSummarySection(document, page, titleFont, metaFont, headerFont, cellFont,
                    borderPen, headerBrush, gfxList, usableWidth, contentBottom);
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

    /// <summary>页头口径：只读 / 边界 / 免责 / 单位 / 已审核发货证据文案按可用宽度折行（绝不追加金额合计）</summary>
    private static List<string> BuildHeadNotes(
        DynamicProductSalesRankingReportPageDto page, XFont metaFont, double usableWidth)
    {
        var lines = new List<string>();
        AddWrapped(lines, page.ReadOnlyText, metaFont, usableWidth);
        AddWrapped(lines, page.BoundaryText, metaFont, usableWidth);
        AddWrapped(lines, page.DisclaimerText, metaFont, usableWidth);
        if (!string.IsNullOrEmpty(page.FilterText))
            AddWrapped(lines, "筛选：" + page.FilterText, metaFont, usableWidth);
        AddWrapped(lines, page.UnitContextText, metaFont, usableWidth);
        AddWrapped(lines, page.ApprovedShipmentText, metaFont, usableWidth);
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
        DynamicProductSalesRankingReportFieldDto column, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize);
        foreach (var row in rows)
            max = Math.Max(max, EstimateWidthPoints(FormatFieldCell(column, row), CellSize));

        return Math.Clamp(max + 6, Mm(MinColumnWidthMm), Mm(MaxColumnWidthMm));
    }

    /// <summary>把选定列按可用页宽贪心拆分为多个「列页」，保证每个列页总宽不超页宽（列不裁切）</summary>
    private static List<List<DynamicProductSalesRankingReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicProductSalesRankingReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicProductSalesRankingReportFieldDto>> { new() };

        var pages = new List<List<DynamicProductSalesRankingReportFieldDto>>();
        var current = new List<DynamicProductSalesRankingReportFieldDto>();
        var currentWidth = 0d;

        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && currentWidth + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicProductSalesRankingReportFieldDto>();
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
        IReadOnlyList<DynamicProductSalesRankingReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = ColumnWidthPoints(columns[c], rows);
        return widths;
    }

    private static double DrawPageHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicProductSalesRankingReportPageDto page,
        int columnPage, int columnPageCount, int rowPage, int rowPageCount,
        IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = ReportTitle;
        if (columnPageCount > 1)
            title += $"（列 {columnPage + 1}/{columnPageCount}）";

        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"日期 {page.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $" ~ {page.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $" · Top {page.Top.ToString(CultureInfo.InvariantCulture)}"
            + (page.TopLimited ? "（已达 Top 上限）" : string.Empty)
            + $" · 共 {page.Total.ToString(CultureInfo.InvariantCulture)} 条"
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
        IReadOnlyList<DynamicProductSalesRankingReportFieldDto> columns, double[] widths, double y)
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
        IReadOnlyList<DynamicProductSalesRankingReportFieldDto> columns, double[] widths,
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


    // ==================== 单位汇总分区（ERP-218） ====================

    /// <summary>
    /// 渲染「单位汇总」分区（仅 unit 分组时）：按精确单位稳定呈现排名桶数与同单位签名数量小计，
    /// 未知单位数量显式渲染「未知」；行数超出按「行页」拆分、每个行页重复表头与标题；空分组渲染显式空提示；
    /// 绝不跨单位合计数量、绝不追加金额。
    /// </summary>
    private static void DrawUnitSummarySection(
        PdfDocument document,
        DynamicProductSalesRankingReportPageDto page,
        XFont titleFont, XFont metaFont, XFont headerFont, XFont cellFont,
        XPen borderPen, XBrush headerBrush,
        List<XGraphics> gfxList, double usableWidth, double contentBottom)
    {
        var groups = page.Groups ?? new List<DynamicProductSalesRankingReportGroupDto>();

        var notes = new List<string>();
        AddWrapped(notes, page.GroupContextText, metaFont, usableWidth);
        AddWrapped(notes, DynamicProductSalesRankingReportRules.BuildTopContext(page.Top, page.TopLimited), metaFont, usableWidth);
        if (!string.IsNullOrEmpty(page.FilterText))
            AddWrapped(notes, "筛选：" + page.FilterText, metaFont, usableWidth);
        AddWrapped(notes, page.UnitContextText, metaFont, usableWidth);

        var headHeightMm = TitleHeightMm + MetaHeightMm + notes.Count * NoteLineHeightMm;
        var headerHeight = Mm(HeaderRowHeightMm);
        var dataRowHeight = Mm(DataRowHeightMm);
        var rowsPerPage = Math.Max(1, (int)Math.Floor(
            (contentBottom - Mm(MarginTopMm) - Mm(headHeightMm) - headerHeight) / dataRowHeight));
        var rowPageCount = groups.Count == 0 ? 1 : (int)Math.Ceiling(groups.Count / (double)rowsPerPage);

        var widths = new[] { Mm(60), Mm(40), Mm(90) };
        var headers = new[]
        {
            DynamicProductSalesRankingReportRules.UnitSummaryUnitColumn,
            DynamicProductSalesRankingReportRules.UnitSummaryBucketCountColumn,
            DynamicProductSalesRankingReportRules.UnitSummaryQuantityColumn,
        };

        for (var rp = 0; rp < rowPageCount; rp++)
        {
            var gfx = NewPage(document);
            gfxList.Add(gfx);
            var y = DrawUnitSummaryHead(gfx, titleFont, metaFont, page, groups.Count, rp, rowPageCount, notes);
            y = DrawUnitSummaryHeaders(gfx, headerFont, headerBrush, borderPen, headers, widths, y);

            if (groups.Count == 0)
            {
                gfx.DrawString(DynamicProductSalesRankingReportRules.UnitSummaryEmptyNote,
                    cellFont, XBrushes.Black,
                    new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, dataRowHeight), XStringFormats.TopLeft);
                continue;
            }

            var start = rp * rowsPerPage;
            var count = Math.Min(rowsPerPage, groups.Count - start);
            for (var i = 0; i < count; i++)
                DrawGroupSummaryRow(gfx, cellFont, borderPen, widths, groups[start + i], y + i * dataRowHeight);
        }
    }

    /// <summary>单位汇总页头：标题（分页时带页码）+ 日期 / Top / 组数 + 分组 / Top / 筛选 / 单位口径上下文</summary>
    private static double DrawUnitSummaryHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicProductSalesRankingReportPageDto page, int groupCount,
        int rowPage, int rowPageCount, IReadOnlyList<string> notes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = UnitSummaryTitle;
        if (rowPageCount > 1)
            title += $"（页 {rowPage + 1}/{rowPageCount}）";

        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"日期 {page.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $" ~ {page.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $" · Top {page.Top.ToString(CultureInfo.InvariantCulture)}"
            + (page.TopLimited ? "（已达 Top 上限）" : string.Empty)
            + $" · 共 {groupCount.ToString(CultureInfo.InvariantCulture)} 组";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm + TitleHeightMm), width, Mm(MetaHeightMm)), XStringFormats.TopCenter);

        var y = Mm(MarginTopMm + TitleHeightMm + MetaHeightMm);
        foreach (var note in notes)
        {
            gfx.DrawString(note, metaFont, XBrushes.Black,
                new XRect(left, y, width, Mm(NoteLineHeightMm)), XStringFormats.TopLeft);
            y += Mm(NoteLineHeightMm);
        }

        return y;
    }

    /// <summary>单位汇总表头（3 列：单位 / 排名桶数 / 同单位数量小计）</summary>
    private static double DrawUnitSummaryHeaders(
        XGraphics gfx, XFont font, XBrush brush, XPen pen,
        IReadOnlyList<string> headers, double[] widths, double y)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < headers.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(headers[c], font, XBrushes.Black, rect, XStringFormats.Center);
        }

        return y + Mm(HeaderRowHeightMm);
    }

    /// <summary>单位汇总行：单位标签 / 排名桶数 / 同单位签名数量小计（未知单位数量显式「未知」，绝不回落 0）</summary>
    private static void DrawGroupSummaryRow(
        XGraphics gfx, XFont font, XPen pen,
        double[] widths, DynamicProductSalesRankingReportGroupDto group, double y)
    {
        var left = Mm(MarginLeftMm);
        var cells = new[]
        {
            group.Label,
            group.RankingBucketCount.ToString(CultureInfo.InvariantCulture),
            group.IsUnknown
                ? DynamicProductSalesRankingReportRules.UnknownQuantityText
                : FormatCellValue(group.TotalQuantity),
        };

        for (var c = 0; c < cells.Length; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);
            var format = c == 0 ? XStringFormats.CenterLeft : XStringFormats.CenterRight;
            DrawCellText(gfx, cells[c], font, rect, format);
        }
    }

    /// <summary>把一行按选定列顺序转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序与空值）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicProductSalesRankingReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatFieldCell(col, row));
        return cells;
    }

    /// <summary>把分组桶转成 PDF 单元格文本（与绘制共用同一口径，供测试验证精确单位 / 签名数量 / 未知单位证据）</summary>
    public static IReadOnlyList<IReadOnlyList<string>> BuildGroupSummaryRows(
        IReadOnlyList<DynamicProductSalesRankingReportGroupDto> groups)
    {
        var list = groups ?? Array.Empty<DynamicProductSalesRankingReportGroupDto>();
        var rows = new List<IReadOnlyList<string>>(list.Count);
        foreach (var group in list)
        {
            rows.Add(new[]
            {
                group.Label,
                group.RankingBucketCount.ToString(CultureInfo.InvariantCulture),
                group.IsUnknown
                    ? DynamicProductSalesRankingReportRules.UnknownQuantityText
                    : FormatCellValue(group.TotalQuantity),
            });
        }

        return rows;
    }

    /// <summary>字段感知的单元格文本：按选定列键照实取行值；null 显示空文本（与既有 Excel 导出口径一致，绝不推算 / 修复）</summary>
    public static string FormatFieldCell(DynamicProductSalesRankingReportFieldDto field, Dictionary<string, object?> row)
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

