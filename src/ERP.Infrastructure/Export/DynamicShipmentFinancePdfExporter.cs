using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态销售订单出货 / 财务进度报表（ERP-159）PDF 导出：复用 ERP-156 有界授权预览与选定列顺序，以 PDFsharp 6.2.4 分页渲染。
/// 宽列集按可用页宽贪心拆成多个「列页」、行数超出按「行页」拆分，避免列被裁切；中文字体固定使用 Windows 黑体（SimHei），
/// 与其它报表 PDF 共用共享解析器（<see cref="SimHeiPdfFontResolver"/>），字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// <para>金额与数量一律按原币分别成行：<c>currency</c> 为原币，不同币种绝不合并、不做汇率换算；未知金额
/// （<c>linkedAmount</c> / <c>uncoveredAmount</c> / <c>submittedAmount</c> 为 null）与未知数量
/// （<c>orderedQuantity</c> / <c>shippedQuantity</c> / <c>pendingShipmentQuantity</c> / <c>outstandingQuantity</c> 为 null）
/// 照实渲染为「未知」，绝不回落为 0、不推算、不修复。全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；
/// 请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicShipmentFinancePdfExporter
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

    /// <summary>无权威引用 / 命中派生上限的统一「未知」标记（绝不回落为 0 或臆造金额 / 数量）</summary>
    private const string UnknownText = "未知";

    /// <summary>金额汇总 PDF 分区标题（ERP-179，明确「当前页」，绝不暗示全量合计 / 应收余额）</summary>
    private const string AmountSummarySectionTitle = "金额汇总（当前页）";

    /// <summary>金额汇总不适用 / 当前页为空时的可见提示（绝不静默留白）</summary>
    private const string AmountSummaryEmptyNote = "本页没有可汇总金额的订单出货 / 财务证据（空页）";

    private const string SummaryCustomerKey = "customerName";
    private const string SummaryCurrencyKey = "currency";
    private const string SummaryShipmentStatusKey = "shipmentStatus";
    private const string SummaryFinanceLinkStatusKey = "financeLinkStatus";
    private const string SummaryOrderCountKey = "orderCount";
    private const string SummaryOrderAmountKey = "orderAmount";
    private const string SummaryKnownLinkedKey = "knownLinkedAmountRows";
    private const string SummaryUnknownLinkedKey = "unknownLinkedAmountRows";
    private const string SummaryLinkedAmountKey = "linkedAmount";
    private const string SummaryKnownUncoveredKey = "knownUncoveredAmountRows";
    private const string SummaryUnknownUncoveredKey = "unknownUncoveredAmountRows";
    private const string SummaryUncoveredAmountKey = "uncoveredAmount";
    private const string SummaryKnownSubmittedKey = "knownSubmittedAmountRows";
    private const string SummaryUnknownSubmittedKey = "unknownSubmittedAmountRows";
    private const string SummarySubmittedAmountKey = "submittedAmount";

    private const string SummaryCustomerColumn = "客户";
    private const string SummaryCurrencyColumn = "币种";
    private const string SummaryShipmentStatusColumn = "出货状态";
    private const string SummaryFinanceLinkStatusColumn = "收款链接状态";
    private const string SummaryOrderCountColumn = "订单张数";
    private const string SummaryOrderAmountColumn = "订单金额";
    private const string SummaryKnownLinkedColumn = "已关联金额已知行数";
    private const string SummaryUnknownLinkedColumn = "已关联金额未知行数";
    private const string SummaryLinkedAmountColumn = "已关联金额";
    private const string SummaryKnownUncoveredColumn = "未覆盖金额已知行数";
    private const string SummaryUnknownUncoveredColumn = "未覆盖金额未知行数";
    private const string SummaryUncoveredAmountColumn = "未覆盖金额";
    private const string SummaryKnownSubmittedColumn = "已提交金额已知行数";
    private const string SummaryUnknownSubmittedColumn = "已提交金额未知行数";
    private const string SummarySubmittedAmountColumn = "已提交金额";

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicShipmentFinanceReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicShipmentFinanceReportPageDto page, string? fontPath)
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
        document.Info.Title = "销售订单出货财务进度报表";

        DrawReport(document, page);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicShipmentFinanceReportPageDto page)
    {
        var columns = page.Columns ?? new List<DynamicShipmentFinanceReportFieldDto>();
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

        // 页头口径（只读 / 边界 / 免责文案）对每个列页完全相同，故行页拆分在各列页保持一致
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
                gfx.DrawString("没有符合条件的销售订单", cellFont, XBrushes.Black,
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

        // ERP-179：仅非 none 金额汇总模式追加独立「金额汇总（当前页）」分区（复用 ERP-162 同一批有界、已授权当前页汇总数据，
        // 只汇总当前页、绝不跨币种合并 / 换算、绝不推断应收余额 / 收款授权；未知 null 显式「未知」、空页显式保留）。
        if (!string.Equals(page.SummaryMode, DynamicShipmentFinanceReportRules.SummaryNone, StringComparison.Ordinal))
        {
            DrawAmountSummarySection(document, page, titleFont, metaFont, headerFont, cellFont);
        }
    }

    private static XGraphics NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width = new XUnit(PageWidthMm, XGraphicsUnit.Millimeter);
        page.Height = new XUnit(PageHeightMm, XGraphicsUnit.Millimeter);
        return XGraphics.FromPdfPage(page);
    }

    /// <summary>页头口径：只读 / 边界 / 免责文案按可用宽度折行（原币、未知证据与只读边界口径与预览同源）</summary>
    private static List<string> BuildHeadNotes(
        DynamicShipmentFinanceReportPageDto page, XFont metaFont, double usableWidth)
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
        DynamicShipmentFinanceReportFieldDto column, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize);
        foreach (var row in rows)
            max = Math.Max(max, EstimateWidthPoints(FormatFieldCell(column, row), CellSize));

        return Math.Clamp(max + 6, Mm(MinColumnWidthMm), Mm(MaxColumnWidthMm));
    }

    /// <summary>把选定列按可用页宽贪心拆分为多个「列页」，保证每个列页总宽不超页宽（列不裁切）</summary>
    private static List<List<DynamicShipmentFinanceReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicShipmentFinanceReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicShipmentFinanceReportFieldDto>> { new() };

        var pages = new List<List<DynamicShipmentFinanceReportFieldDto>>();
        var current = new List<DynamicShipmentFinanceReportFieldDto>();
        var currentWidth = 0d;

        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && currentWidth + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicShipmentFinanceReportFieldDto>();
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
        IReadOnlyList<DynamicShipmentFinanceReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = ColumnWidthPoints(columns[c], rows);
        return widths;
    }

    private static double DrawPageHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicShipmentFinanceReportPageDto page,
        int columnPage, int columnPageCount, int rowPage, int rowPageCount,
        IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = "销售订单出货财务进度报表";
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
        IReadOnlyList<DynamicShipmentFinanceReportFieldDto> columns, double[] widths, double y)
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
        IReadOnlyList<DynamicShipmentFinanceReportFieldDto> columns, double[] widths,
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

    // ==================== 金额汇总分区（ERP-179） ====================

    /// <summary>
    /// 渲染「金额汇总（当前页）」独立分区：客户 + 原币（可选出货状态 / 收款链接状态）+ 订单张数 + 订单金额
    /// + 已关联 / 未覆盖 / 已提交金额的已知 / 未知行数与金额（未知 null 显式「未知」，绝不回落为 0）；
    /// 金额按原币分别成行、绝不跨币种合并 / 换算、绝无应收余额 / 合计；宽列集按列页拆分、行数超出按行页拆分，空页显式提示。
    /// </summary>
    private static void DrawAmountSummarySection(
        PdfDocument document,
        DynamicShipmentFinanceReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont)
    {
        var columns = BuildSummaryColumns(page.SummaryMode);
        var rows = BuildSummaryRows(page.Summaries);

        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);
        var headerHeight = Mm(HeaderRowHeightMm);
        var dataRowHeight = Mm(DataRowHeightMm);
        var headHeightMm = TitleHeightMm + MetaHeightMm;

        var rowsPerPage = Math.Max(1, (int)Math.Floor(
            (contentBottom - Mm(MarginTopMm) - Mm(headHeightMm) - headerHeight) / dataRowHeight));
        var columnPages = SplitColumnPages(columns, rows, usableWidth);
        var rowPageCount = rows.Count == 0 ? 1 : (int)Math.Ceiling(rows.Count / (double)rowsPerPage);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var gfxList = new List<XGraphics>();
        try
        {
            if (rows.Count == 0)
            {
                var gfx = NewPage(document);
                gfxList.Add(gfx);
                var cols = columnPages[0];
                var widths = ComputeColumnWidths(cols, rows);
                var y = DrawSummaryHead(gfx, titleFont, metaFont, page, 0, columnPages.Count, 0, 1);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);
                gfx.DrawString(AmountSummaryEmptyNote, cellFont, XBrushes.Black,
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
                        var y = DrawSummaryHead(gfx, titleFont, metaFont, page, cp, columnPages.Count, rp, rowPageCount);
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

    /// <summary>金额汇总分区页头：分区标题（含列页标记）+ 当前页 / 行页 / 列页口径（明确「当前页」，绝不暗示全量合计）</summary>
    private static double DrawSummaryHead(
        XGraphics gfx,
        XFont titleFont,
        XFont metaFont,
        DynamicShipmentFinanceReportPageDto page,
        int columnPage,
        int columnPageCount,
        int rowPage,
        int rowPageCount)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = AmountSummarySectionTitle;
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

        return Mm(MarginTopMm + TitleHeightMm + MetaHeightMm);
    }

    /// <summary>金额汇总分区列：客户 + 原币（可选出货状态 / 收款链接状态）+ 订单张数 / 订单金额 + 三组金额证据（已知 / 未知行数 + 金额）</summary>
    private static List<DynamicShipmentFinanceReportFieldDto> BuildSummaryColumns(string summaryMode)
    {
        var columns = new List<DynamicShipmentFinanceReportFieldDto>
        {
            new(SummaryCustomerKey, SummaryCustomerColumn, "text", false),
            new(SummaryCurrencyKey, SummaryCurrencyColumn, "text", false),
        };

        if (string.Equals(summaryMode, DynamicShipmentFinanceReportRules.SummaryCustomerCurrencyShipment, StringComparison.Ordinal))
            columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryShipmentStatusKey, SummaryShipmentStatusColumn, "text", false));
        if (string.Equals(summaryMode, DynamicShipmentFinanceReportRules.SummaryCustomerCurrencyFinance, StringComparison.Ordinal))
            columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryFinanceLinkStatusKey, SummaryFinanceLinkStatusColumn, "text", false));

        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryOrderCountKey, SummaryOrderCountColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryOrderAmountKey, SummaryOrderAmountColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryKnownLinkedKey, SummaryKnownLinkedColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryUnknownLinkedKey, SummaryUnknownLinkedColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryLinkedAmountKey, SummaryLinkedAmountColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryKnownUncoveredKey, SummaryKnownUncoveredColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryUnknownUncoveredKey, SummaryUnknownUncoveredColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryUncoveredAmountKey, SummaryUncoveredAmountColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryKnownSubmittedKey, SummaryKnownSubmittedColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummaryUnknownSubmittedKey, SummaryUnknownSubmittedColumn, "number", false));
        columns.Add(new DynamicShipmentFinanceReportFieldDto(SummarySubmittedAmountKey, SummarySubmittedAmountColumn, "number", false));
        return columns;
    }

    /// <summary>金额汇总分区行：null 金额保持 null（由 <see cref="FormatFieldCell"/> 显式渲染为「未知」，绝不回落为 0）</summary>
    private static List<Dictionary<string, object?>> BuildSummaryRows(
        List<DynamicShipmentFinanceReportSummaryDto>? summaries)
    {
        if (summaries is null)
            return new List<Dictionary<string, object?>>();

        return summaries.Select(s => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SummaryCustomerKey] = s.CustomerName,
            [SummaryCurrencyKey] = s.Currency,
            [SummaryShipmentStatusKey] = s.ShipmentStatus,
            [SummaryFinanceLinkStatusKey] = s.FinanceLinkStatus,
            [SummaryOrderCountKey] = s.OrderCount,
            [SummaryOrderAmountKey] = s.OrderAmount,
            [SummaryKnownLinkedKey] = s.KnownLinkedAmountRows,
            [SummaryUnknownLinkedKey] = s.UnknownLinkedAmountRows,
            [SummaryLinkedAmountKey] = s.LinkedAmount,
            [SummaryKnownUncoveredKey] = s.KnownUncoveredAmountRows,
            [SummaryUnknownUncoveredKey] = s.UnknownUncoveredAmountRows,
            [SummaryUncoveredAmountKey] = s.UncoveredAmount,
            [SummaryKnownSubmittedKey] = s.KnownSubmittedAmountRows,
            [SummaryUnknownSubmittedKey] = s.UnknownSubmittedAmountRows,
            [SummarySubmittedAmountKey] = s.SubmittedAmount,
        }).ToList();
    }

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序与未知证据）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicShipmentFinanceReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatFieldCell(col, row));
        return cells;
    }

    /// <summary>
    /// 字段感知的单元格文本：未知金额 / 未知数量（null）显式显示「未知」（绝不回落为 0）；
    /// 出货状态 / 收款链接状态 / 单据状态映射中文文案；币种为原币（原样保留，绝不换算或合并）；其余按通用口径格式化。
    /// </summary>
    public static string FormatFieldCell(DynamicShipmentFinanceReportFieldDto field, Dictionary<string, object?> row)
    {
        var value = row.TryGetValue(field.Key, out var v) ? v : null;

        if ((value is null or DBNull) && IsUnknownEvidence(field.Key))
            return UnknownText;

        switch (field.Key)
        {
            case "shipmentStatus":
                return ShipmentStatusLabel(value);
            case "financeLinkStatus":
                return FinanceLinkStatusLabel(value);
            case "status":
                return OrderStatusLabel(value);
            default:
                return FormatCellValue(value);
        }
    }

    /// <summary>未知金额 / 未知数量字段：null = 未知（命中派生上限或无权威引用），绝不回落为 0</summary>
    private static bool IsUnknownEvidence(string key) =>
        key is "orderedQuantity" or "shippedQuantity" or "pendingShipmentQuantity" or "outstandingQuantity"
            or "linkedAmount" or "uncoveredAmount" or "submittedAmount";

    private static string ShipmentStatusLabel(object? value) => value switch
    {
        "none" => "未出货",
        "partial" => "部分出货",
        "complete" => "已出齐",
        "over_shipped" => "超发",
        "unknown" => "未知（超出派生上限）",
        _ => FormatCellValue(value),
    };

    private static string FinanceLinkStatusLabel(object? value) => value switch
    {
        "linked" => "收款引用完整",
        "partial" => "部分可归属（其余未知）",
        "unlinked" => "未链接（金额未知）",
        "unknown" => "未知（超出派生上限）",
        _ => FormatCellValue(value),
    };

    private static string OrderStatusLabel(object? value) => value switch
    {
        "Pending" => "待提交",
        "Submitted" => "已提交",
        "Approved" => "已审核",
        "Rejected" => "已驳回",
        "Completed" => "已完成",
        "Cancelled" => "已取消",
        _ => FormatCellValue(value),
    };

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

