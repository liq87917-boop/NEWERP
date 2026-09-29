using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 客户报告包（ERP-124）PDF 导出：复用 ERP-122 有界、双授权、作用域化的「销售订单 + 发票 / 收款分摊证据」预览，
/// 以 PDFsharp 6.2.4 把两个分区渲染为独立分页的两个章节（各自独立分页、续页重复列标题），中文字体固定使用
/// Windows 黑体（SimHei），与销售订单 PDF（ERP-116）/ 应收账款 PDF（ERP-121）共用同一共享解析器
/// （<see cref="SimHeiPdfFontResolver"/>），字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// <para>金额按原币呈现、剩余证据显式保留 known / unknown / over_allocated 标签；绝不推断发票到订单的链接、
/// 不结算、不计算账户余额或催收状态、不生成任何合计 / 余额 / 催收结论行。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class CustomerReportPacketPdfExporter
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
    private const double SectionSize = 11;
    private const double MetaSize = 8;
    private const double HeaderSize = 8;
    private const double CellSize = 8;

    private const double HeaderRowHeightMm = 7;
    private const double DataRowHeightMm = 6.5;

    private const double PointsPerMillimeter = 72.0 / 25.4;

    /// <summary>导出客户报告包为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(CustomerReportPacketDto packet)
        => Export(packet, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出客户报告包为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(CustomerReportPacketDto packet, string? fontPath)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new BusinessException(
                "PDF 导出失败：未找到中文字体 SimHei（黑体）。请在 Windows 字体目录安装 simhei.ttf 后重试，"
                + "避免生成乱码或缺字 PDF。",
                ErrorCodes.InternalError);
        }

        SimHeiPdfFontResolver.Ensure(fontPath);

        using var document = new PdfDocument();
        document.Info.Title = "客户报告包";

        DrawPacket(document, packet);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawPacket(PdfDocument document, CustomerReportPacketDto packet)
    {
        var orderColumns = ToColumns(packet.SalesOrders?.Columns);
        var orderRows = packet.SalesOrders?.Rows ?? new List<Dictionary<string, object?>>();
        var receivableColumns = ToColumns(packet.ReceivableEvidence?.Columns);
        var receivableRows = packet.ReceivableEvidence?.Rows ?? new List<Dictionary<string, object?>>();

        // 两个分区各自独立分页：销售订单分区自第 1 页起，应收证据分区另起一页。
        DrawSection(document, packet, "一、销售订单", orderColumns, orderRows, isReceivable: false, firstSection: true);
        DrawSection(document, packet, "二、发票 / 收款分摊证据", receivableColumns, receivableRows, isReceivable: true, firstSection: false);
    }

    /// <summary>把一个分区渲染为独立分页章节：首章节绘制标题块，续页重复章节标题与列标题</summary>
    private static void DrawSection(
        PdfDocument document,
        CustomerReportPacketDto packet,
        string sectionTitle,
        IReadOnlyList<(string Key, string Label, string DataType)> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        bool isReceivable,
        bool firstSection)
    {
        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var sectionFont = new XFont(FontFamily, SectionSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var tableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var left = Mm(MarginLeftMm);
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);
        var columnCount = Math.Max(1, columns.Count);
        var colWidth = tableWidth / columnCount;
        var dataRowHeight = Mm(DataRowHeightMm);

        var gfxList = new List<XGraphics>();
        try
        {
            XGraphics? gfx = null;
            double y = 0;
            var rowIndex = 0;
            var continuation = false;

            while (true)
            {
                gfx = NewPage(document);
                gfxList.Add(gfx);
                y = Mm(MarginTopMm);

                if (firstSection && !continuation)
                    y = DrawTitleBlock(gfx, titleFont, metaFont, packet, left, y, tableWidth);

                gfx.DrawString(continuation ? sectionTitle + "（续）" : sectionTitle, sectionFont, XBrushes.Black,
                    new XRect(left, y, tableWidth, Mm(8)), XStringFormats.TopLeft);
                y += Mm(9);

                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, columns, left, colWidth, y);
                continuation = true;

                while (rowIndex < rows.Count && y + dataRowHeight <= contentBottom)
                {
                    DrawDataRow(gfx, cellFont, borderPen, columns, rows[rowIndex], isReceivable, left, colWidth, y);
                    y += dataRowHeight;
                    rowIndex++;
                }

                if (rowIndex >= rows.Count)
                    break;
            }

            if (rows.Count == 0 && gfx is not null)
            {
                gfx.DrawString(isReceivable ? "没有符合条件的发票 / 收款分摊证据" : "没有符合条件的销售订单",
                    cellFont, XBrushes.Black,
                    new XRect(left, y + Mm(2), tableWidth, dataRowHeight), XStringFormats.TopLeft);
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

    private static double DrawTitleBlock(
        XGraphics gfx,
        XFont titleFont,
        XFont metaFont,
        CustomerReportPacketDto packet,
        double left,
        double y,
        double tableWidth)
    {
        gfx.DrawString("客户报告包", titleFont, XBrushes.Black,
            new XRect(left, y, tableWidth, Mm(9)), XStringFormats.TopCenter);
        y += Mm(9);

        var meta = $"客户 Id：{packet.CustomerId} · 导出时间 {DateTime.Now:yyyy-MM-dd HH:mm} · 只读快照";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(left, y, tableWidth, Mm(6)), XStringFormats.TopCenter);
        y += Mm(7);

        DrawTruncatedLine(gfx, packet.ReadOnlyText, metaFont, left, y, tableWidth);
        y += Mm(5);
        DrawTruncatedLine(gfx, packet.BoundaryText, metaFont, left, y, tableWidth);
        y += Mm(5);
        DrawTruncatedLine(gfx, packet.DisclaimerText, metaFont, left, y, tableWidth);
        y += Mm(7);
        return y;
    }

    private static double DrawColumnHeaders(
        XGraphics gfx,
        XFont font,
        XBrush brush,
        XPen pen,
        IReadOnlyList<(string Key, string Label, string DataType)> columns,
        double left,
        double colWidth,
        double y)
    {
        for (var c = 0; c < columns.Count; c++)
        {
            var rect = new XRect(left + c * colWidth, y, colWidth, Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(columns[c].Label, font, XBrushes.Black, rect, XStringFormats.Center);
        }
        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawDataRow(
        XGraphics gfx,
        XFont font,
        XPen pen,
        IReadOnlyList<(string Key, string Label, string DataType)> columns,
        Dictionary<string, object?> row,
        bool isReceivable,
        double left,
        double colWidth,
        double y)
    {
        for (var c = 0; c < columns.Count; c++)
        {
            var rect = new XRect(left + c * colWidth, y, colWidth, Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            var value = row.TryGetValue(columns[c].Key, out var v) ? v : null;
            var text = isReceivable && columns[c].Key == "remainingState"
                ? FormatRemainingState(value)
                : FormatCellValue(value);
            var format = columns[c].DataType is "number" or "boolean"
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format);
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

    private static void DrawTruncatedLine(XGraphics gfx, string? text, XFont font, double x, double y, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var width = gfx.MeasureString(text, font).Width;
        if (width <= maxWidth)
        {
            gfx.DrawString(text, font, XBrushes.Black, x, y);
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

        gfx.DrawString(buffer + ellipsis, font, XBrushes.Black, x, y);
    }

    // ==================== 列映射 / 单元格格式化 ====================

    private static List<(string Key, string Label, string DataType)> ToColumns(
        IReadOnlyList<DynamicSalesOrderReportFieldDto>? columns)
        => (columns ?? new List<DynamicSalesOrderReportFieldDto>())
            .Select(c => (c.Key, c.Label, c.DataType)).ToList();

    private static List<(string Key, string Label, string DataType)> ToColumns(
        IReadOnlyList<DynamicReceivableReportFieldDto>? columns)
        => (columns ?? new List<DynamicReceivableReportFieldDto>())
            .Select(c => (c.Key, c.Label, c.DataType)).ToList();

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

    /// <summary>
    /// 剩余证据状态 → 显式中文标签（known / unknown / over_allocated）；未知取值原样返回，绝不猜测或轧为金额。
    /// </summary>
    public static string FormatRemainingState(object? value)
    {
        var state = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

        if (string.Equals(state, CustomerReceivableReconciliationRules.RemainingKnown, StringComparison.Ordinal))
            return "剩余可确认";
        if (string.Equals(state, CustomerReceivableReconciliationRules.RemainingOverAllocated, StringComparison.Ordinal))
            return "剩余超额分摊（无效）";
        if (string.Equals(state, CustomerReceivableReconciliationRules.RemainingUnknown, StringComparison.Ordinal))
            return "剩余未知";

        return state;
    }

    private static double Mm(double millimeters) => millimeters * PointsPerMillimeter;
}



