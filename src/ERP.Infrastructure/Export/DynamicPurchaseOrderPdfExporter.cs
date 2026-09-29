using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态采购订单报表（ERP-129）PDF 导出：复用 ERP-125 有界授权预览与 ERP-128 分组页面小计（币种分开），
/// 以 PDFsharp 6.2.4 分页渲染选定列；中文字体固定使用 Windows 黑体（SimHei），与销售订单 PDF（ERP-116）/
/// 应收账款 PDF（ERP-121）共用同一共享解析器（<see cref="SimHeiPdfFontResolver"/>），字体缺失时显式失败
/// （不产出乱码或缺字 PDF）。
/// <para>小计金额只对同币种求和，绝不跨币种换算或相加；金额按订单原币呈现，不产出结算 / 应付余额结论。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicPurchaseOrderPdfExporter
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

    private const double HeaderRowHeightMm = 7;
    private const double DataRowHeightMm = 6.5;

    private const double PointsPerMillimeter = 72.0 / 25.4;

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(
        DynamicPurchaseOrderReportPageDto page,
        IReadOnlyList<DynamicPurchaseOrderReportGroupDto>? groups,
        string groupBy)
        => Export(page, groups, groupBy, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(
        DynamicPurchaseOrderReportPageDto page,
        IReadOnlyList<DynamicPurchaseOrderReportGroupDto>? groups,
        string groupBy,
        string? fontPath)
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
        document.Info.Title = "采购订单报表";

        DrawReport(document, page, groups, groupBy);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawReport(
        PdfDocument document,
        DynamicPurchaseOrderReportPageDto page,
        IReadOnlyList<DynamicPurchaseOrderReportGroupDto>? groups,
        string groupBy)
    {
        var columns = page.Columns ?? new List<DynamicPurchaseOrderReportFieldDto>();
        var rows = page.Rows ?? new List<Dictionary<string, object?>>();
        var columnCount = Math.Max(1, columns.Count);

        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var tableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var colWidth = tableWidth / columnCount;
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);
        var dataRowHeight = Mm(DataRowHeightMm);

        var gfxList = new List<XGraphics>();
        try
        {
            XGraphics? gfx = null;
            double y = 0;
            var rowIndex = 0;

            while (rowIndex < rows.Count)
            {
                gfx = NewPage(document);
                gfxList.Add(gfx);
                y = DrawPageHead(gfx, titleFont, metaFont, page);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, columns, colWidth, y);

                while (rowIndex < rows.Count && y + dataRowHeight <= contentBottom)
                {
                    DrawDataRow(gfx, cellFont, borderPen, columns, rows[rowIndex], colWidth, y);
                    y += dataRowHeight;
                    rowIndex++;
                }
            }

            if (rows.Count == 0)
            {
                gfx = NewPage(document);
                gfxList.Add(gfx);
                y = DrawPageHead(gfx, titleFont, metaFont, page);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, columns, colWidth, y);
                gfx.DrawString("没有符合条件的采购订单", cellFont, XBrushes.Black,
                    new XRect(Mm(MarginLeftMm), y + Mm(2), tableWidth, dataRowHeight), XStringFormats.TopLeft);
            }

            if (!string.Equals(groupBy, DynamicPurchaseOrderReportRules.GroupNone, StringComparison.Ordinal)
                && groups is { Count: > 0 })
            {
                var subtotalHeight = Mm(8) + groups.Sum(g => g.Subtotals.Count) * Mm(6) + Mm(6);
                if (gfx is null || y + subtotalHeight > contentBottom)
                {
                    gfx = NewPage(document);
                    gfxList.Add(gfx);
                    y = DrawPageHead(gfx, titleFont, metaFont, page);
                }

                DrawSubtotals(gfx, headerFont, cellFont, borderPen, headerBrush, groups, y);
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

    private static double DrawPageHead(XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicPurchaseOrderReportPageDto page)
    {
        gfx.DrawString("采购订单报表", titleFont, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), Mm(MarginTopMm), Mm(PageWidthMm - MarginLeftMm - MarginRightMm), Mm(9)),
            XStringFormats.TopCenter);

        var meta = $"共 {page.Total} 条 · 预览第 {page.Page}/{page.TotalPages} 页 · 导出时间 {DateTime.Now:yyyy-MM-dd HH:mm}";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), Mm(MarginTopMm + 9), Mm(PageWidthMm - MarginLeftMm - MarginRightMm), Mm(6)),
            XStringFormats.TopCenter);

        return Mm(MarginTopMm + 18);
    }

    private static double DrawColumnHeaders(XGraphics gfx, XFont font, XBrush brush, XPen pen,
        IReadOnlyList<DynamicPurchaseOrderReportFieldDto> columns, double colWidth, double y)
    {
        for (var c = 0; c < columns.Count; c++)
        {
            var rect = new XRect(Mm(MarginLeftMm) + c * colWidth, y, colWidth, Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(columns[c].Label, font, XBrushes.Black, rect, XStringFormats.Center);
        }
        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawDataRow(XGraphics gfx, XFont font, XPen pen,
        IReadOnlyList<DynamicPurchaseOrderReportFieldDto> columns,
        Dictionary<string, object?> row, double colWidth, double y)
    {
        for (var c = 0; c < columns.Count; c++)
        {
            var rect = new XRect(Mm(MarginLeftMm) + c * colWidth, y, colWidth, Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            var value = row.TryGetValue(columns[c].Key, out var v) ? v : null;
            var text = FormatCellValue(value);
            var format = columns[c].DataType is "number" or "boolean"
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format);
        }
    }

    private static void DrawSubtotals(XGraphics gfx, XFont headerFont, XFont cellFont, XPen pen, XBrush brush,
        IReadOnlyList<DynamicPurchaseOrderReportGroupDto> groups, double y)
    {
        var left = Mm(MarginLeftMm);
        var widths = new[] { Mm(70), Mm(40), Mm(30), Mm(50) };
        var titles = new[] { "分组", "币种", "条数", "金额" };

        gfx.DrawString("本页小计（按币种分开，不跨币种合计）", headerFont, XBrushes.Black,
            new XRect(left, y, Mm(PageWidthMm - MarginLeftMm - MarginRightMm), Mm(7)), XStringFormats.TopLeft);
        y += Mm(8);

        for (var c = 0; c < titles.Length; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(6));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(titles[c], headerFont, XBrushes.Black, rect, XStringFormats.Center);
        }
        y += Mm(6);

        foreach (var group in groups)
        {
            foreach (var subtotal in group.Subtotals)
            {
                var cells = new[]
                {
                    group.Label,
                    subtotal.Currency,
                    subtotal.Count.ToString(CultureInfo.InvariantCulture),
                    subtotal.Amount.ToString("0.##", CultureInfo.InvariantCulture),
                };
                for (var c = 0; c < cells.Length; c++)
                {
                    var x = left + SumWidths(widths, c);
                    var rect = new XRect(x, y, widths[c], Mm(6));
                    gfx.DrawRectangle(pen, rect);
                    var format = c >= 2 ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
                    DrawCellText(gfx, cells[c], cellFont, rect, format);
                }
                y += Mm(6);
            }
        }
    }

    private static double SumWidths(double[] values, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++)
            total += values[i];
        return total;
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

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicPurchaseOrderReportFieldDto> columns,
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

    private static double Mm(double millimeters) => millimeters * PointsPerMillimeter;
}
