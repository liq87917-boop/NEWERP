using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态客户订单与收款核对报表（ERP-169 / ERP-175 / ERP-177）PDF 导出：复用 ERP-164 有界授权预览与选定列顺序（订单证据与未关联收款证据两个独立分区），
/// 以 PDFsharp 6.2.4 分页渲染当前页；宽列集按可用页宽拆成多个「列页」，避免列被裁切；中文字体固定使用 Windows 黑体（SimHei），
/// 与其它报表 PDF 共用共享解析器（<see cref="SimHeiPdfFontResolver"/>），字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// <para>金额保留原币、未知金额 / 未知数量显式保留（证据行 null → 空文本；金额汇总行 null → 「未知」，绝不回落 0）、状态与未关联收款截断警告显式保留；
/// ERP-175 非 none 分组模式追加两个计数分组分区、ERP-177 customerCurrency 金额汇总模式追加两个金额汇总分区（只汇总当前页、绝不跨币种合并 / 换算、绝不推断收款分配 / 应收余额）；
/// 全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicReceiptReconciliationPdfExporter
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

    /// <summary>列宽有界（毫米）：最小保证中文标签可读，最大避免单列独占整页，超宽列集靠「列页」拆分而非挤压</summary>
    private const double MinColumnWidthMm = 20;
    private const double MaxColumnWidthMm = 45;

    /// <summary>订单计数分组 PDF 分区标题（ERP-175，明确「当前页」，绝不暗示全量合计）</summary>
    private const string OrderGroupSectionTitle = "三、订单计数分组（当前页）";

    /// <summary>未关联收款计数分组 PDF 分区标题（ERP-175，明确「当前页」）</summary>
    private const string ReceiptGroupSectionTitle = "四、未关联收款计数分组（当前页）";

    /// <summary>订单计数分组不适用 / 当前页为空时的可见提示（绝不静默留白）</summary>
    private const string OrderGroupEmptyNote = "无订单计数分组（不适用或当前页为空）";

    /// <summary>未关联收款计数分组不适用 / 当前页为空时的可见提示（绝不静默留白）</summary>
    private const string ReceiptGroupEmptyNote = "无未关联收款计数分组（不适用或当前页为空）";

    private const string GroupLabelKey = "label";
    private const string OrderCountKey = "orderCount";
    private const string ReceiptCountKey = "receiptCount";
    private const string TruncatedKey = "truncated";
    private const string GroupLabelColumn = "分组标签";
    private const string OrderCountColumn = "订单张数";
    private const string ReceiptCountColumn = "收款张数";
    private const string TruncatedColumn = "截断";

    /// <summary>订单金额汇总 PDF 分区标题（ERP-177，明确「当前页」，绝不暗示全量合计）</summary>
    private const string OrderSummarySectionTitle = "五、订单金额汇总（当前页）";

    /// <summary>未关联收款金额汇总 PDF 分区标题（ERP-177，明确「当前页」）</summary>
    private const string ReceiptSummarySectionTitle = "六、未关联收款金额汇总（当前页）";

    /// <summary>订单金额汇总不适用 / 当前页为空时的可见提示（绝不静默留白）</summary>
    private const string OrderSummaryEmptyNote = "本页没有可汇总金额的订单证据（空页）";

    /// <summary>未关联收款金额汇总不适用 / 当前页为空时的可见提示（绝不静默留白）</summary>
    private const string ReceiptSummaryEmptyNote = "本页没有可汇总金额的未关联收款证据（空页）";

    /// <summary>可未知金额的统一「未知」标记（绝不回落为 0 或臆造金额）</summary>
    public const string UnknownText = "未知";

    private const string CustomerKey = "customerName";
    private const string CurrencyKey = "currency";
    private const string EvidenceStatusKey = "evidenceStatus";
    private const string OrderAmountKey = "orderAmount";
    private const string KnownLinkedKey = "knownLinkedReceiptAmountRows";
    private const string UnknownLinkedKey = "unknownLinkedReceiptAmountRows";
    private const string LinkedAmountKey = "linkedReceiptAmount";
    private const string KnownUncoveredKey = "knownUncoveredAmountRows";
    private const string UnknownUncoveredKey = "unknownUncoveredAmountRows";
    private const string UncoveredAmountKey = "uncoveredAmount";
    private const string AmountKey = "amount";

    private const string CustomerColumn = "客户";
    private const string CurrencyColumn = "币种";
    private const string EvidenceStatusColumn = "收款证据状态";
    private const string OrderAmountColumn = "订单金额";
    private const string KnownLinkedReceiptAmountRowsColumn = "已关联收款金额已知行数";
    private const string UnknownLinkedReceiptAmountRowsColumn = "已关联收款金额未知行数";
    private const string LinkedReceiptAmountColumn = "已关联收款金额";
    private const string KnownUncoveredAmountRowsColumn = "未覆盖金额已知行数";
    private const string UnknownUncoveredAmountRowsColumn = "未覆盖金额未知行数";
    private const string UncoveredAmountColumn = "未覆盖金额";
    private const string ReceiptAmountColumn = "金额";

    private const double PointsPerMillimeter = 72.0 / 25.4;

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicReceiptReconciliationReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicReceiptReconciliationReportPageDto page, string? fontPath)
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
        document.Info.Title = "客户订单与收款核对报表";

        DrawReport(document, page);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicReceiptReconciliationReportPageDto page)
    {
        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        // 两类证据各自独立分区，绝不合并：订单证据（选定订单列）与未关联收款证据（选定收款列）。
        DrawSection(
            document,
            "一、订单证据",
            page.Columns,
            page.Rows,
            null,
            page,
            titleFont,
            metaFont,
            headerFont,
            cellFont);

        DrawSection(
            document,
            "二、未关联收款证据",
            page.ReceiptColumns,
            page.ReceiptRows,
            page.UnlinkedReceiptTruncated ? DynamicReceiptReconciliationReportRules.ReceiptTruncationNote : null,
            page,
            titleFont,
            metaFont,
            headerFont,
            cellFont);

        // ERP-175：仅非 none 分组模式追加两个独立计数分区（复用 ERP-170 同一批有界、已授权分组数据，只计数、不含金额、绝不推断匹配）。
        if (!string.Equals(page.GroupBy, DynamicReceiptReconciliationReportRules.GroupNone, StringComparison.Ordinal))
        {
            DrawOrderGroupSection(document, page, titleFont, metaFont, headerFont, cellFont);
            DrawReceiptGroupSection(document, page, titleFont, metaFont, headerFont, cellFont);
        }

        // ERP-177：仅 customerCurrency 金额汇总模式追加两个独立金额汇总分区（复用 ERP-172 同一批有界、已授权当前页汇总数据，
        // 只汇总当前页、绝不跨币种合并 / 换算、绝不推断收款分配 / 应收余额；未知 null 显式「未知」、截断 / 空页显式保留）。
        if (string.Equals(page.SummaryMode, DynamicReceiptReconciliationReportRules.SummaryCustomerCurrency, StringComparison.Ordinal))
        {
            DrawOrderSummarySection(document, page, titleFont, metaFont, headerFont, cellFont);
            DrawReceiptSummarySection(document, page, titleFont, metaFont, headerFont, cellFont);
        }
    }

    /// <summary>渲染「订单计数分组」独立分区：分组标签 + 订单张数（数值），只计数、不含金额；不适用 / 空页显式提示。</summary>
    private static void DrawOrderGroupSection(
        PdfDocument document,
        DynamicReceiptReconciliationReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont)
    {
        var columns = new List<DynamicReceiptReconciliationReportFieldDto>
        {
            new(GroupLabelKey, GroupLabelColumn, "text", false),
            new(OrderCountKey, OrderCountColumn, "number", false),
        };

        var rows = page.OrderGroups?.Select(g => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [GroupLabelKey] = g.Label,
            [OrderCountKey] = g.OrderCount,
        }).ToList() ?? new List<Dictionary<string, object?>>();

        DrawSection(document, OrderGroupSectionTitle, columns, rows, null, page,
            titleFont, metaFont, headerFont, cellFont, OrderGroupEmptyNote);
    }

    /// <summary>渲染「未关联收款计数分组」独立分区：分组标签 + 收款张数（数值）+ 截断（是 / 否，显式保留），只计数、不含金额；不适用 / 空页显式提示。</summary>
    private static void DrawReceiptGroupSection(
        PdfDocument document,
        DynamicReceiptReconciliationReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont)
    {
        var columns = new List<DynamicReceiptReconciliationReportFieldDto>
        {
            new(GroupLabelKey, GroupLabelColumn, "text", false),
            new(ReceiptCountKey, ReceiptCountColumn, "number", false),
            new(TruncatedKey, TruncatedColumn, "text", false),
        };

        var rows = page.ReceiptGroups?.Select(g => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [GroupLabelKey] = g.Label,
            [ReceiptCountKey] = g.ReceiptCount,
            [TruncatedKey] = g.Truncated,
        }).ToList() ?? new List<Dictionary<string, object?>>();

        DrawSection(document, ReceiptGroupSectionTitle, columns, rows, null, page,
            titleFont, metaFont, headerFont, cellFont, ReceiptGroupEmptyNote);
    }

    /// <summary>渲染「订单金额汇总」独立分区：客户 + 币种 + 订单张数 + 订单金额 + 已关联收款金额（已知 / 未知行数 + 未知整列显式）+ 未覆盖金额（同口径）。</summary>
    private static void DrawOrderSummarySection(
        PdfDocument document,
        DynamicReceiptReconciliationReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont)
    {
        var columns = new List<DynamicReceiptReconciliationReportFieldDto>
        {
            new(CustomerKey, CustomerColumn, "text", false),
            new(CurrencyKey, CurrencyColumn, "text", false),
            new(OrderCountKey, OrderCountColumn, "number", false),
            new(OrderAmountKey, OrderAmountColumn, "number", false),
            new(KnownLinkedKey, KnownLinkedReceiptAmountRowsColumn, "number", false),
            new(UnknownLinkedKey, UnknownLinkedReceiptAmountRowsColumn, "number", false),
            new(LinkedAmountKey, LinkedReceiptAmountColumn, "number", false),
            new(KnownUncoveredKey, KnownUncoveredAmountRowsColumn, "number", false),
            new(UnknownUncoveredKey, UnknownUncoveredAmountRowsColumn, "number", false),
            new(UncoveredAmountKey, UncoveredAmountColumn, "number", false),
        };

        DrawSection(document, OrderSummarySectionTitle, columns,
            BuildOrderSummaryRows(page.OrderSummaries), null, page,
            titleFont, metaFont, headerFont, cellFont, OrderSummaryEmptyNote);
    }

    /// <summary>渲染「未关联收款金额汇总」独立分区：客户 + 币种 + 收款证据状态 + 收款张数 + 金额 + 截断（是 / 否，显式保留）；页级截断警告显式标注。</summary>
    private static void DrawReceiptSummarySection(
        PdfDocument document,
        DynamicReceiptReconciliationReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont)
    {
        var columns = new List<DynamicReceiptReconciliationReportFieldDto>
        {
            new(CustomerKey, CustomerColumn, "text", false),
            new(CurrencyKey, CurrencyColumn, "text", false),
            new(EvidenceStatusKey, EvidenceStatusColumn, "text", false),
            new(ReceiptCountKey, ReceiptCountColumn, "number", false),
            new(AmountKey, ReceiptAmountColumn, "number", false),
            new(TruncatedKey, TruncatedColumn, "text", false),
        };

        DrawSection(document, ReceiptSummarySectionTitle, columns,
            BuildReceiptSummaryRows(page.ReceiptSummaries),
            page.UnlinkedReceiptTruncated ? DynamicReceiptReconciliationReportRules.ReceiptTruncationNote : null,
            page, titleFont, metaFont, headerFont, cellFont, ReceiptSummaryEmptyNote);
    }

    private static List<Dictionary<string, object?>> BuildOrderSummaryRows(
        List<DynamicReceiptReconciliationReportOrderSummaryDto>? summaries)
    {
        if (summaries is null)
            return new List<Dictionary<string, object?>>();

        var keys = new[]
        {
            CustomerKey, CurrencyKey, OrderCountKey, OrderAmountKey,
            KnownLinkedKey, UnknownLinkedKey, LinkedAmountKey,
            KnownUncoveredKey, UnknownUncoveredKey, UncoveredAmountKey,
        };

        return summaries.Select(s =>
        {
            var cells = BuildOrderSummaryCells(s);
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < keys.Length; i++)
                row[keys[i]] = cells[i];
            return row;
        }).ToList();
    }

    private static List<Dictionary<string, object?>> BuildReceiptSummaryRows(
        List<DynamicReceiptReconciliationReportReceiptSummaryDto>? summaries)
    {
        if (summaries is null)
            return new List<Dictionary<string, object?>>();

        var keys = new[]
        {
            CustomerKey, CurrencyKey, EvidenceStatusKey, ReceiptCountKey, AmountKey, TruncatedKey,
        };

        return summaries.Select(s =>
        {
            var cells = BuildReceiptSummaryCells(s);
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < keys.Length; i++)
                row[keys[i]] = cells[i];
            return row;
        }).ToList();
    }

    /// <summary>订单金额汇总行按列序渲染为 PDF 单元格文本（可未知金额 null → 「未知」，绝不回落为 0；供测试验证语义）</summary>
    public static IReadOnlyList<string> BuildOrderSummaryCells(DynamicReceiptReconciliationReportOrderSummaryDto s) => new[]
    {
        s.CustomerName,
        s.Currency,
        FormatCellValue(s.OrderCount),
        FormatCellValue(s.OrderAmount),
        FormatCellValue(s.KnownLinkedReceiptAmountRows),
        FormatCellValue(s.UnknownLinkedReceiptAmountRows),
        FormatSummaryAmount(s.LinkedReceiptAmount),
        FormatCellValue(s.KnownUncoveredAmountRows),
        FormatCellValue(s.UnknownUncoveredAmountRows),
        FormatSummaryAmount(s.UncoveredAmount),
    };

    /// <summary>未关联收款金额汇总行按列序渲染为 PDF 单元格文本（active / pending / historical 显式保留，绝不回落、绝不并入有效合计）</summary>
    public static IReadOnlyList<string> BuildReceiptSummaryCells(DynamicReceiptReconciliationReportReceiptSummaryDto s) => new[]
    {
        s.CustomerName,
        s.Currency,
        s.EvidenceStatus,
        FormatCellValue(s.ReceiptCount),
        FormatCellValue(s.Amount),
        FormatCellValue(s.Truncated),
    };

    /// <summary>把可未知金额渲染为 PDF 单元格文本：null → 「未知」，否则按数值口径格式化（绝不回落为 0）</summary>
    public static string FormatSummaryAmount(decimal? amount)
        => amount is null ? UnknownText : FormatCellValue(amount);

    /// <summary>渲染一个独立分区：宽列集按列页拆分（每个列页重复表头），行按纵向分页；空结果仅渲染分区头与空提示（可自定义空提示文案）。</summary>
    private static void DrawSection(
        PdfDocument document,
        string sectionTitle,
        List<DynamicReceiptReconciliationReportFieldDto>? columns,
        List<Dictionary<string, object?>>? rows,
        string? note,
        DynamicReceiptReconciliationReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont,
        string emptyText = "没有符合筛选条件的证据")
    {
        var cols = columns ?? new List<DynamicReceiptReconciliationReportFieldDto>();
        var dataRows = rows ?? new List<Dictionary<string, object?>>();

        var columnPages = SplitColumnPages(cols);

        var gfxList = new List<XGraphics>();
        try
        {
            foreach (var columnPage in columnPages)
            {
                var widths = ComputeColumnWidths(columnPage.Count);

                if (dataRows.Count == 0)
                {
                    var gfx = NewPage(document);
                    gfxList.Add(gfx);
                    var y = DrawSectionHead(gfx, sectionTitle, note, page, titleFont, metaFont);
                    y = DrawColumnHeaders(gfx, headerFont, columnPage, widths, y);
                    gfx.DrawString(emptyText, cellFont, XBrushes.Black,
                        new XRect(Mm(MarginLeftMm), y + Mm(2), TableWidthMm(), DataRowHeightMmPoints),
                        XStringFormats.TopLeft);
                    continue;
                }

                var rowIndex = 0;
                while (rowIndex < dataRows.Count)
                {
                    var gfx = NewPage(document);
                    gfxList.Add(gfx);
                    var y = DrawSectionHead(gfx, sectionTitle, note, page, titleFont, metaFont);
                    y = DrawColumnHeaders(gfx, headerFont, columnPage, widths, y);

                    while (rowIndex < dataRows.Count && y + DataRowHeightMmPoints <= ContentBottomPoints)
                    {
                        DrawDataRow(gfx, cellFont, columnPage, dataRows[rowIndex], widths, y);
                        y += DataRowHeightMmPoints;
                        rowIndex++;
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

    /// <summary>绘制分区标题 + 预览元信息 +（收款分区）截断警告；返回内容起始 y 坐标（点）。</summary>
    private static double DrawSectionHead(
        XGraphics gfx,
        string sectionTitle,
        string? note,
        DynamicReceiptReconciliationReportPageDto page,
        XFont titleFont,
        XFont metaFont)
    {
        gfx.DrawString(sectionTitle, titleFont, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), Mm(MarginTopMm), TableWidthMm(), Mm(9)), XStringFormats.TopLeft);

        var meta = $"共 {page.Total} 条 · 预览第 {page.Page}/{page.TotalPages} 页 · 导出时间 {DateTime.Now:yyyy-MM-dd HH:mm}";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), Mm(MarginTopMm + 9), TableWidthMm(), Mm(6)), XStringFormats.TopLeft);

        var y = Mm(MarginTopMm + 18);

        if (!string.IsNullOrWhiteSpace(note))
        {
            gfx.DrawString(note, metaFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y, TableWidthMm(), Mm(6)), XStringFormats.TopLeft);
            y += Mm(7);
        }

        return y;
    }

    private static double DrawColumnHeaders(
        XGraphics gfx,
        XFont font,
        IReadOnlyList<DynamicReceiptReconciliationReportFieldDto> columns,
        double[] widths,
        double y)
    {
        var pen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var brush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        for (var c = 0; c < columns.Count; c++)
        {
            var rect = new XRect(Mm(MarginLeftMm) + SumWidths(widths, c), y, widths[c], Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(columns[c].Label, font, XBrushes.Black, rect, XStringFormats.Center);
        }

        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawDataRow(
        XGraphics gfx,
        XFont font,
        IReadOnlyList<DynamicReceiptReconciliationReportFieldDto> columns,
        IReadOnlyDictionary<string, object?> row,
        double[] widths,
        double y)
    {
        var pen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);

        for (var c = 0; c < columns.Count; c++)
        {
            var rect = new XRect(Mm(MarginLeftMm) + SumWidths(widths, c), y, widths[c], Mm(DataRowHeightMm));
            gfx.DrawRectangle(pen, rect);

            var value = row.TryGetValue(columns[c].Key, out var v) ? v : null;
            var format = string.Equals(columns[c].DataType, "number", StringComparison.Ordinal)
                ? XStringFormats.CenterRight
                : XStringFormats.CenterLeft;
            DrawCellText(gfx, FormatCellValue(value), font, rect, format);
        }
    }



    /// <summary>把整列集合按有界列宽拆成多个「列页」（每个列页不超过可用页宽能容纳的列数，避免挤压 / 裁切）</summary>
    private static List<List<T>> SplitColumnPages<T>(IReadOnlyList<T> columns)
    {
        var result = new List<List<T>>();
        if (columns.Count == 0)
        {
            result.Add(new List<T>());
            return result;
        }

        var maxPerPage = Math.Max(1, (int)Math.Floor(TableWidthMm() / MinColumnWidthMm));
        for (var i = 0; i < columns.Count; i += maxPerPage)
            result.Add(columns.Skip(i).Take(maxPerPage).ToList());

        return result;
    }

    private static double[] ComputeColumnWidths(int count)
    {
        var widths = new double[count];
        var width = Math.Min(MaxColumnWidthMm, Math.Max(MinColumnWidthMm, TableWidthMm() / count));
        for (var i = 0; i < count; i++)
            widths[i] = width;
        return widths;
    }

    /// <summary>把预览行的单元格值转成 PDF 单元格文本（中文布尔 / 日期 / 数值口径与 Excel 导出保持一致；null 未知 → 空文本，绝不回落 0）</summary>
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

    private static double SumWidths(double[] values, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++)
            total += values[i];
        return total;
    }

    private static double TableWidthMm() => PageWidthMm - MarginLeftMm - MarginRightMm;

    private static double DataRowHeightMmPoints => Mm(DataRowHeightMm);

    private static double ContentBottomPoints => Mm(PageHeightMm - MarginBottomMm);

    private static double Mm(double millimeters) => millimeters * PointsPerMillimeter;
}
