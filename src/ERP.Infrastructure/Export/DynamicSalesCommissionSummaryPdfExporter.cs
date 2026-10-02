using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态业务员提成证据报表（ERP-249）「全匹配」原币汇总中文 PDF 下载（窄、无状态、由控制器直接调用，无新 DI / 包 / 配置 / 字体资源写入）。
/// 复用 ERP-247 全匹配汇总 DTO / 规则（<see cref="DynamicSalesCommissionSummaryRules.BuildSummary"/>）与既有 PDFsharp + 共享中文字体解析器
/// <see cref="SimHeiPdfFontResolver"/>；仅渲染固定原币汇总行（与预览当前页 / 选定明细列无关），绝不渲染员工明细行、未选定明细列、
/// 跨币种合计金额或编造提成。
/// <para>固定有限原币汇总行渲染已知签名原币金额 / 计数与未知金额完整度 / 未知利润 / 未知提成 / 当前参考比例证据；
/// 上下文中始终标注规范化日期 / 应用筛选 / 全匹配覆盖 / 来源上限 / 全局去重业务员桶与已审核订单 / 来源 / 原币 / 当前参考比例 /
/// 未知历史成本（利润 / 利润率 / 提成额）依据；每页重复表头并按行页 / 列页拆分（每个列页恒保留原币身份列，避免重叠 / 裁切）；
/// 字体缺失 / 渲染失败显式失败（绝不空成功或缺失字形成功）。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicSalesCommissionSummaryPdfExporter
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

    /// <summary>报表标题</summary>
    private const string ReportTitle = "业务员提成全匹配原币汇总（中文 PDF）";

    /// <summary>每个列页恒保留的原币身份列（币种 + 标签），确保跨列页始终可识别该行币种</summary>
    private static readonly HashSet<string> IdentityColumnKeys = new(StringComparer.Ordinal)
    {
        "currency",
        "currencyLabel",
    };

    /// <summary>导出全匹配原币汇总为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicSalesCommissionSummaryDto summary, DynamicSalesCommissionReportPageDto page)
        => Export(summary, page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出全匹配原币汇总为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(
        DynamicSalesCommissionSummaryDto summary,
        DynamicSalesCommissionReportPageDto page,
        string? fontPath)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new BusinessException(
                "PDF 下载失败：未找到中文字体 SimHei（黑体）。请在 Windows 字体目录安装 simhei.ttf 后重试，避免生成乱码或缺字 PDF。",
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
            // 渲染失败时绝不返回损坏 / 半成品 PDF，仅给出清晰错误
            throw new BusinessException(
                "PDF 下载失败：渲染全匹配原币汇总时发生错误，未生成任何文件，请稍后重试。",
                ErrorCodes.InternalError);
        }
    }

    // ==================== 绘制 ====================

    private static void DrawReport(
        PdfDocument document,
        DynamicSalesCommissionSummaryDto summary,
        DynamicSalesCommissionReportPageDto page)
    {
        var columns = summary.CurrencyColumns ?? new List<DynamicSalesCommissionReportFieldDto>();
        var rows = summary.CurrencyRows ?? new List<DynamicSalesCommissionCurrencySummaryDto>();

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

        var noteLines = WrapNotes(BuildHeadNotes(summary, page), usableWidth);
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
                var y = DrawPageHead(gfx, titleFont, metaFont, summary, page, 0, columnPages.Count, 0, rowPageCount, noteLines);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, cols, widths, y);
                DrawEmptyNote(gfx, cellFont, summary, usableWidth, y);
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
                        var y = DrawPageHead(gfx, titleFont, metaFont, summary, page, cp, columnPages.Count, rp, rowPageCount, noteLines);
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

    /// <summary>上下文注释：只读声明 / 筛选 / 全匹配覆盖 / 全局去重计数 / 来源上限 / 原币 / 未知 / 未知利润 / 当前参考比例 / 未知历史依据 / 来源证据始终呈现。</summary>
    private static List<string> BuildHeadNotes(
        DynamicSalesCommissionSummaryDto summary,
        DynamicSalesCommissionReportPageDto page)
    {
        var notes = new List<string>
        {
            page.ReadOnlyText,
            string.IsNullOrWhiteSpace(page.FilterText)
                ? DynamicSalesCommissionReportRules.ContextNoFilterText
                : $"筛选条件：{page.FilterText}",
            summary.CoverageText,
            $"全局去重业务员桶 {summary.GlobalUniqueSalesmanBuckets} · 全局已审核订单 {summary.GlobalApprovedOrders}",
            page.SourceLimitText,
            page.CurrencyContextText,
            page.UnknownContextText,
            page.ProfitContextText,
            BuildRateContextText(summary),
            summary.ProfitBasisText,
            page.SourceContextText,
        };

        if (!string.IsNullOrWhiteSpace(summary.EmptyText))
            notes.Add(summary.EmptyText);

        return notes;
    }

    /// <summary>当前参考比例上下文：一致已知（含显式 0）显示数值，缺失 / 冲突 / 空证据显示「未知」并附显式原因（绝不回退为 0）。</summary>
    public static string BuildRateContextText(DynamicSalesCommissionSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var rateText = summary.CurrentReferenceRate.HasValue
            ? DynamicSalesCommissionPdfExporter.FormatCellValue(summary.CurrentReferenceRate.Value)
            : DynamicSalesCommissionReportRules.UnknownValueText;
        var reason = summary.CurrentReferenceRate.HasValue
            ? SalesCommissionEvidenceRules.CommissionRateEvidence
            : summary.CurrentReferenceRateReason;
        return $"当前参考比例：{rateText}（{reason}）";
    }

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
        DynamicSalesCommissionSummaryDto summary,
        DynamicSalesCommissionReportPageDto page,
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

        var meta = $"日期 {page.Start:yyyy-MM-dd} ~ {page.End:yyyy-MM-dd} · 全匹配原币汇总（与当前页/选定列无关）";
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
        IReadOnlyList<DynamicSalesCommissionReportFieldDto> columns,
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
        IReadOnlyList<DynamicSalesCommissionReportFieldDto> columns,
        double[] widths,
        DynamicSalesCommissionCurrencySummaryDto row,
        double y)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            var value = CellValue(col, row);
            var text = FormatSummaryCell(col, row);
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            // 已知订单数 / 原币金额等数值证据右对齐；未知金额 / 未知利润 / 未知提成显式「未知」左对齐，二者明显区分
            var isNumeric = col.DataType == "number" || col.DataType == "money";
            var format = isNumeric && value is not null
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format);
        }
    }

    private static void DrawEmptyNote(
        XGraphics gfx,
        XFont font,
        DynamicSalesCommissionSummaryDto summary,
        double usableWidth,
        double y)
    {
        var text = string.IsNullOrWhiteSpace(summary.EmptyText)
            ? DynamicSalesCommissionSummaryRules.EmptyText
            : summary.EmptyText;
        gfx.DrawString(text, font, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, Mm(DataRowHeightMm)), XStringFormats.TopLeft);
    }

    // ==================== 列页拆分与列宽 ====================

    /// <summary>按可用页宽贪心拆成多个「列页」（每个列页恒保留币种身份列），避免超宽列集被挤压 / 裁切。</summary>
    public static IReadOnlyList<IReadOnlyList<DynamicSalesCommissionReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicSalesCommissionReportFieldDto> columns,
        IReadOnlyList<DynamicSalesCommissionCurrencySummaryDto> rows)
        => SplitColumnPages(columns, rows, Mm(PageWidthMm - MarginLeftMm - MarginRightMm));

    private static List<List<DynamicSalesCommissionReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicSalesCommissionReportFieldDto> columns,
        IReadOnlyList<DynamicSalesCommissionCurrencySummaryDto> rows,
        double usableWidth)
    {
        var identity = columns.Where(c => IdentityColumnKeys.Contains(c.Key)).ToList();
        var data = columns.Where(c => !IdentityColumnKeys.Contains(c.Key)).ToList();

        var identityWidth = identity.Sum(c => ColumnWidthPoints(c, rows));
        var dataUsableWidth = Math.Max(0d, usableWidth - identityWidth);

        var pages = new List<List<DynamicSalesCommissionReportFieldDto>>();
        if (data.Count == 0)
        {
            pages.Add(new List<DynamicSalesCommissionReportFieldDto>(identity));
            return pages;
        }

        var current = new List<DynamicSalesCommissionReportFieldDto>();
        var used = 0d;
        foreach (var column in data)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && used + width > dataUsableWidth)
            {
                pages.Add(new List<DynamicSalesCommissionReportFieldDto>(identity).Concat(current).ToList());
                current = new List<DynamicSalesCommissionReportFieldDto>();
                used = 0;
            }

            current.Add(column);
            used += width;
        }

        if (current.Count > 0)
            pages.Add(new List<DynamicSalesCommissionReportFieldDto>(identity).Concat(current).ToList());

        return pages;
    }

    private static double[] ComputeColumnWidths(
        IReadOnlyList<DynamicSalesCommissionReportFieldDto> columns,
        IReadOnlyList<DynamicSalesCommissionCurrencySummaryDto> rows)
        => columns.Select(c => ColumnWidthPoints(c, rows)).ToArray();

    private static double ColumnWidthPoints(
        DynamicSalesCommissionReportFieldDto column,
        IReadOnlyList<DynamicSalesCommissionCurrencySummaryDto> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize) + 8;
        foreach (var row in rows)
        {
            var text = FormatSummaryCell(column, row);
            var width = EstimateWidthPoints(text, CellSize) + 8;
            if (width > max)
                max = width;
        }

        var mm = max / PointsPerMillimeter;
        return Mm(Math.Clamp(mm, MinColumnWidthMm, MaxColumnWidthMm));
    }

    // ==================== 单元格格式化 ====================

    /// <summary>按固定汇总列顺序把一条汇总行转成 PDF 单元格文本（供测试验证字段顺序与未知 / 签名 / 计数语义）。</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicSalesCommissionReportFieldDto> columns,
        DynamicSalesCommissionCurrencySummaryDto row)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(row);

        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatSummaryCell(col, row));
        return cells;
    }

    /// <summary>字段感知的汇总单元格文本：null 金额 / 利润 / 利润率 / 提成额显式渲染为「未知」（绝不写成 0）；已知计数 / 签名金额按数值呈现。</summary>
    public static string FormatSummaryCell(
        DynamicSalesCommissionReportFieldDto field,
        DynamicSalesCommissionCurrencySummaryDto row)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(row);

        var value = CellValue(field, row);
        if (value is null)
        {
            return field.DataType == "number" || field.DataType == "money"
                ? DynamicSalesCommissionReportRules.UnknownValueText
                : string.Empty;
        }

        return DynamicSalesCommissionPdfExporter.FormatCellValue(value);
    }

    private static object? CellValue(DynamicSalesCommissionReportFieldDto field, DynamicSalesCommissionCurrencySummaryDto row)
        => field.Key switch
        {
            "currency" => row.Currency,
            "currencyLabel" => row.CurrencyLabel,
            "approvedOrders" => row.ApprovedOrders,
            "uniqueSalesmanBuckets" => row.UniqueSalesmanBuckets,
            "salesAmount" => row.SalesAmount,
            "profit" => row.Profit,
            "profitRate" => row.ProfitRate,
            "commissionAmount" => row.CommissionAmount,
            "amountCompletenessText" => row.AmountCompletenessText,
            "profitReasonText" => row.ProfitReasonText,
            "profitRateReasonText" => row.ProfitRateReasonText,
            "commissionAmountReasonText" => row.CommissionAmountReasonText,
            _ => null,
        };

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
