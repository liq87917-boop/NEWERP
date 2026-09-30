using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态代理服务费月度汇总报表（ERP-183）PDF 导出：复用 ERP-181 有界授权预览与选定列顺序，以 PDFsharp 6.2.4 分页渲染。
/// 选定字段、中文标签、原币（不同币种分别成行、绝不换算或合并）与状态口径（已登记 / 草稿 / 已作废分开列示）显式保留；
/// 宽列集按可用页宽拆成多个「列页」，行数超出时按「行页」拆分，避免列被裁切；空页显式说明。
/// month / customer 分组时追加「分组计数（当前页）」与「分组金额（当前页）」两个独立分区（ERP-189 / ERP-191），
/// 分组金额按原币 + 币种精度分别列示已登记 / 草稿 / 已作废金额，绝不跨币种 / 跨页合计；none 保持既有证据 PDF 不变。
/// 中文字体固定使用 Windows 黑体（SimHei），与其它报表 PDF 共用共享解析器（<see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class DynamicAgencyServiceFeeMonthlyPdfExporter
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

    /// <summary>空页显式说明（证据数字，不代表收入或应收）</summary>
    private const string EmptyPageNote = "没有符合筛选条件的代理服务费对账单证据（或已被软删除；证据数字不代表收入或应收）";

    /// <summary>分组计数 PDF 分区标题（ERP-189，明确「当前页」，绝不暗示全量合计）</summary>
    private const string GroupCountSectionTitle = "分组计数（当前页）";

    /// <summary>分组分区总标题（ERP-189 计数 + ERP-191 金额合并为一个分组页序列）</summary>
    private const string GroupSectionTitle = "分组汇总（当前页）";

    /// <summary>分组计数分区列标题（计数为数值；标签 / 原币为文本；不含金额列）</summary>
    private const string GroupLabelColumn = "分组标签";
    private const string CurrencyColumn = "原币";
    private const string RowCountColumn = "月度行数";
    private const string RegisteredCountColumn = "已登记张数";
    private const string DraftCountColumn = "草稿张数";
    private const string VoidedCountColumn = "已作废张数";
    private const string StatementCountColumn = "对账单总张数";

    private const string GroupLabelKey = "groupLabel";
    private const string CurrencyKey = "currency";
    private const string RowCountKey = "rowCount";
    private const string RegisteredCountKey = "registeredCount";
    private const string DraftCountKey = "draftCount";
    private const string VoidedCountKey = "voidedCount";
    private const string StatementCountKey = "statementCount";

    /// <summary>分组计数当前页为空时的可见提示（绝不静默留白）</summary>
    private const string GroupCountEmptyNote = "当前页没有符合分组条件的对账单证据（空页）";

    /// <summary>分组计数截断说明（后续分页未计入本分区）</summary>
    private const string GroupCountTruncatedNote = "当前页已被截断，后续分页未计入本分组计数（仅本页）";

    /// <summary>分组分区仅本页说明（兜底；优先使用页面自带的分组范围文案）</summary>
    private const string GroupCountPageOnlyNote =
        "本分区只统计当前授权预览页的月度汇总行，不覆盖整份报表，也绝不跨币种、跨页合计；"
        + "计数与原币金额只是证据数字，不代表收入 / 应收 / 已收款等会计结论。";

    /// <summary>分组金额 PDF 分区标题（ERP-191，明确「当前页」，绝不暗示全量合计 / 收入 / 应收）</summary>
    private const string GroupAmountSectionTitle = "分组金额（当前页）";

    /// <summary>分组金额分区列标题（金额为原币 + 币种精度文案；标签 / 原币为文本）</summary>
    private const string RegisteredAmountColumn = "已登记原币金额";
    private const string DraftAmountColumn = "草稿原币金额";
    private const string VoidedAmountColumn = "已作废原币金额";

    private const string RegisteredAmountKey = "registeredAmountText";
    private const string DraftAmountKey = "draftAmountText";
    private const string VoidedAmountKey = "voidedAmountText";

    /// <summary>分组金额当前页为空时的可见提示（绝不静默留白）</summary>
    private const string GroupAmountEmptyNote = "当前页没有符合分组条件的原币金额证据（空页）";

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(DynamicAgencyServiceFeeMonthlyReportPageDto page)
        => Export(page, SimHeiPdfFontResolver.FindFontPath());

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(DynamicAgencyServiceFeeMonthlyReportPageDto page, string? fontPath)
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
        document.Info.Title = "代理服务费月度汇总报表";

        DrawReport(document, page);

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, DynamicAgencyServiceFeeMonthlyReportPageDto page)
    {
        var columns = page.Columns ?? new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>();
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

        // 页头口径（只读 / 边界 / 证据 / 币种隔离 / 不分摊）对每个列页完全相同，故行页拆分在各列页保持一致
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

                var note = string.IsNullOrWhiteSpace(page.EmptyText) ? EmptyPageNote : page.EmptyText;
                gfx.DrawString(note, cellFont, XBrushes.Black,
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

        // ERP-189 / ERP-191：month / customer 分组时，在选定列证据页之后追加「分组计数」与「分组金额」两个子表（同组页连续分页）；none 保持既有文档不变
        if (IsCountGrouped(page.GroupBy))
            DrawGroupSection(document, page, titleFont, metaFont, headerFont, cellFont, borderPen, headerBrush);
    }

    // ==================== 分组分区（ERP-189 计数 + ERP-191 金额） ====================

    /// <summary>是否需要追加分组计数分区（仅 month / customer；none 保持既有文档不变）</summary>
    private static bool IsCountGrouped(string? groupBy)
        => string.Equals(groupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, StringComparison.OrdinalIgnoreCase)
           || string.Equals(groupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer, StringComparison.OrdinalIgnoreCase);

    /// <summary>分组计数列定义（与 Excel 分组工作表同一套标签；仅计数、不含金额列）</summary>
    private static List<DynamicAgencyServiceFeeMonthlyReportFieldDto> BuildGroupColumns()
        => new()
        {
            new(GroupLabelKey, GroupLabelColumn, "text", false),
            new(CurrencyKey, CurrencyColumn, "text", false),
            new(RowCountKey, RowCountColumn, "number", false),
            new(RegisteredCountKey, RegisteredCountColumn, "number", false),
            new(DraftCountKey, DraftCountColumn, "number", false),
            new(VoidedCountKey, VoidedCountColumn, "number", false),
            new(StatementCountKey, StatementCountColumn, "number", false),
        };

    /// <summary>分组计数列宽（点）：分组标签列更宽以容纳较长客户名，其余固定；总宽不超过可用页宽，避免列被裁切</summary>
    private static double[] GroupColumnWidths()
        => new[]
        {
            Mm(64), // 分组标签
            Mm(20), // 原币
            Mm(21), // 月度行数
            Mm(21), // 已登记张数
            Mm(21), // 草稿张数
            Mm(21), // 已作废张数
            Mm(21), // 对账单总张数
        };

    /// <summary>把 ERP-184 分组计数转成 PDF 行（仅分组标签 + 原币 + 各状态张数，绝不进入金额列）</summary>
    private static List<Dictionary<string, object?>> BuildGroupRows(
        List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto> groups)
        => groups.Select(g => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [GroupLabelKey] = GroupLabel(g),
            [CurrencyKey] = g.Currency,
            [RowCountKey] = g.RowCount,
            [RegisteredCountKey] = g.RegisteredCount,
            [DraftCountKey] = g.DraftCount,
            [VoidedCountKey] = g.VoidedCount,
            [StatementCountKey] = g.StatementCount,
        }).ToList();

    /// <summary>分组标签：month → 年月文案（yyyy-MM）；customer → 客户名称（回退客户编码）</summary>
    private static string GroupLabel(DynamicAgencyServiceFeeMonthlyReportGroupCountDto group)
    {
        if (string.Equals(group.GroupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(group.StatementMonthText)
                ? (group.StatementYear.HasValue && group.StatementMonth.HasValue
                    ? $"{group.StatementYear.Value:0000}-{group.StatementMonth.Value:00}"
                    : string.Empty)
                : group.StatementMonthText;
        }

        return string.IsNullOrWhiteSpace(group.CustomerName) ? group.CustomerCode : group.CustomerName;
    }

    /// <summary>
    /// 渲染分组分区（ERP-189 计数 + ERP-191 金额）：先「分组计数」子表，再「分组金额」子表，
    /// 两个子表在同一组页序列上纵向连续分页（空间不足时续新页并重复分组页头）；只计数、只列原币金额（币种精度文案），
    /// 绝不跨币种 / 跨页合计；空分组与截断页显式说明，仅本页口径显式标注。
    /// </summary>
    private static void DrawGroupSection(
        PdfDocument document,
        DynamicAgencyServiceFeeMonthlyReportPageDto page,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont,
        XPen borderPen,
        XBrush headerBrush)
    {
        var groups = page.GroupCounts ?? new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>();
        var countColumns = BuildGroupColumns();
        var countRows = BuildGroupRows(groups);
        var countWidths = GroupColumnWidths();
        var amountColumns = BuildAmountColumns();
        var amountRows = BuildAmountGroupRows(groups);
        var amountWidths = GroupAmountColumnWidths();

        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);

        var scopeNote = string.IsNullOrWhiteSpace(page.GroupCountScopeText)
            ? GroupCountPageOnlyNote
            : page.GroupCountScopeText;
        var notes = new List<string>();
        AddWrapped(notes, scopeNote, metaFont, usableWidth);
        if (page.Truncated)
            AddWrapped(notes, GroupCountTruncatedNote, metaFont, usableWidth);

        var headHeight = Mm(TitleHeightMm + MetaHeightMm + notes.Count * NoteLineHeightMm);
        var availablePerPage = contentBottom - Mm(MarginTopMm) - headHeight;
        var totalPages = ComputeGroupSectionPages(availablePerPage, countRows.Count, amountRows.Count);

        var gfxList = new List<XGraphics>();
        var pageIndex = 0;
        try
        {
            var gfx = NewPage(document);
            gfxList.Add(gfx);
            var y = DrawGroupSectionHead(gfx, titleFont, metaFont, page, notes, pageIndex, totalPages);

            (gfx, y, pageIndex) = DrawGroupSubTable(document, gfxList, page, notes,
                titleFont, metaFont, headerFont, cellFont, borderPen, headerBrush,
                GroupCountSectionTitle, countColumns, countWidths, countRows, GroupCountEmptyNote,
                gfx, y, pageIndex, totalPages);
            (gfx, y, pageIndex) = DrawGroupSubTable(document, gfxList, page, notes,
                titleFont, metaFont, headerFont, cellFont, borderPen, headerBrush,
                GroupAmountSectionTitle, amountColumns, amountWidths, amountRows, GroupAmountEmptyNote,
                gfx, y, pageIndex, totalPages);
        }
        finally
        {
            foreach (var g in gfxList)
                g.Dispose();
        }
    }

    /// <summary>计算分组分区（计数子表 + 金额子表）所需页数：按子标题 / 表头 / 数据行块高度贪心分页，与渲染口径一致</summary>
    private static int ComputeGroupSectionPages(double availablePerPage, int countRows, int amountRows)
    {
        var subTitleHeight = Mm(TitleHeightMm);
        var headerHeight = Mm(HeaderRowHeightMm);
        var dataRowHeight = Mm(DataRowHeightMm);

        var heights = new List<double> { subTitleHeight, headerHeight };
        heights.AddRange(Enumerable.Repeat(dataRowHeight, Math.Max(1, countRows)));
        heights.AddRange(new[] { subTitleHeight, headerHeight });
        heights.AddRange(Enumerable.Repeat(dataRowHeight, Math.Max(1, amountRows)));

        var pages = 1;
        var used = 0d;
        foreach (var h in heights)
        {
            if (used + h > availablePerPage + 1e-6)
            {
                pages++;
                used = 0;
            }
            used += h;
        }
        return pages;
    }

    /// <summary>在给定页面上续绘一个分组子表（子标题 + 表头 + 数据行；空间不足时续新页并重复分组页头），空行显式提示；返回续绘后的游标</summary>
    private static (XGraphics Gfx, double Y, int PageIndex) DrawGroupSubTable(
        PdfDocument document,
        List<XGraphics> gfxList,
        DynamicAgencyServiceFeeMonthlyReportPageDto page,
        IReadOnlyList<string> notes,
        XFont titleFont,
        XFont metaFont,
        XFont headerFont,
        XFont cellFont,
        XPen borderPen,
        XBrush headerBrush,
        string subTitle,
        IReadOnlyList<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns,
        double[] widths,
        IReadOnlyList<Dictionary<string, object?>> rows,
        string emptyNote,
        XGraphics gfx,
        double y,
        int pageIndex,
        int totalPages)
    {
        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);
        var subTitleHeight = Mm(TitleHeightMm);
        var headerHeight = Mm(HeaderRowHeightMm);
        var dataRowHeight = Mm(DataRowHeightMm);

        (XGraphics Gfx, double Y, int PageIndex) NextPage(int currentIndex)
        {
            var next = NewPage(document);
            gfxList.Add(next);
            var index = currentIndex + 1;
            var nextY = DrawGroupSectionHead(next, titleFont, metaFont, page, notes, index, totalPages);
            return (next, nextY, index);
        }

        if (y + subTitleHeight > contentBottom + 1e-6)
            (gfx, y, pageIndex) = NextPage(pageIndex);
        gfx.DrawString(subTitle, titleFont, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), y, usableWidth, subTitleHeight), XStringFormats.TopCenter);
        y += subTitleHeight;

        if (y + headerHeight > contentBottom + 1e-6)
            (gfx, y, pageIndex) = NextPage(pageIndex);
        y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, columns, widths, y);

        if (rows.Count == 0)
        {
            if (y + dataRowHeight > contentBottom + 1e-6)
                (gfx, y, pageIndex) = NextPage(pageIndex);
            gfx.DrawString(emptyNote, cellFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, dataRowHeight), XStringFormats.TopLeft);
            y += dataRowHeight;
            return (gfx, y, pageIndex);
        }

        foreach (var row in rows)
        {
            if (y + dataRowHeight > contentBottom + 1e-6)
                (gfx, y, pageIndex) = NextPage(pageIndex);
            DrawDataRow(gfx, cellFont, borderPen, columns, widths, row, y);
            y += dataRowHeight;
        }

        return (gfx, y, pageIndex);
    }

    // ==================== 分组金额分区（ERP-191） ====================

    /// <summary>分组金额列定义（复用分组标签 / 原币，追加已登记 / 草稿 / 已作废原币金额；金额为币种精度文案）</summary>
    private static List<DynamicAgencyServiceFeeMonthlyReportFieldDto> BuildAmountColumns()
        => new()
        {
            new(GroupLabelKey, GroupLabelColumn, "text", false),
            new(CurrencyKey, CurrencyColumn, "text", false),
            new(RegisteredAmountKey, RegisteredAmountColumn, "text", false),
            new(DraftAmountKey, DraftAmountColumn, "text", false),
            new(VoidedAmountKey, VoidedAmountColumn, "text", false),
        };

    /// <summary>分组金额列宽（点）：分组标签列更宽以容纳较长客户名，金额列容纳「数值 + 币种」；总宽不超过可用页宽</summary>
    private static double[] GroupAmountColumnWidths()
        => new[]
        {
            Mm(64), // 分组标签
            Mm(20), // 原币
            Mm(35), // 已登记原币金额
            Mm(35), // 草稿原币金额
            Mm(35), // 已作废原币金额
        };

    /// <summary>把 ERP-186 分组金额转成 PDF 行（分组标签 + 原币 + 已登记 / 草稿 / 已作废原币金额，金额为币种精度文案，绝不跨币种合计）</summary>
    public static IReadOnlyList<Dictionary<string, object?>> BuildAmountGroupRows(
        List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto> groups)
        => groups.Select(g => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [GroupLabelKey] = GroupLabel(g),
            [CurrencyKey] = g.Currency,
            [RegisteredAmountKey] = g.RegisteredTotalAmountText,
            [DraftAmountKey] = g.DraftTotalAmountText,
            [VoidedAmountKey] = g.VoidedTotalAmountText,
        }).ToList();


    private static double DrawGroupSectionHead(
        XGraphics gfx,
        XFont titleFont,
        XFont metaFont,
        DynamicAgencyServiceFeeMonthlyReportPageDto page,
        IReadOnlyList<string> notes,
        int rowPage,
        int rowPageCount)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        gfx.DrawString(GroupSectionTitle, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var groupCount = page.GroupCounts?.Count ?? 0;
        var meta = $"分组键：{GroupByLabel(page.GroupBy)}"
            + $" · 共 {groupCount.ToString(CultureInfo.InvariantCulture)} 组"
            + $" · 行页 {rowPage + 1}/{rowPageCount}";
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

    /// <summary>分组键中文文案（仅 month / customer 会进入本分区）</summary>
    private static string GroupByLabel(string? groupBy)
        => string.Equals(groupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, StringComparison.OrdinalIgnoreCase)
            ? "对账月份"
            : "客户";

    private static XGraphics NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width = new XUnit(PageWidthMm, XGraphicsUnit.Millimeter);
        page.Height = new XUnit(PageHeightMm, XGraphicsUnit.Millimeter);
        return XGraphics.FromPdfPage(page);
    }

    /// <summary>页头口径：只读 / 边界 / 证据 / 币种隔离 / 不分摊文案按可用宽度折行（无跨币种合计）</summary>
    private static List<string> BuildHeadNotes(
        DynamicAgencyServiceFeeMonthlyReportPageDto page, XFont metaFont, double usableWidth)
    {
        var lines = new List<string>();
        AddWrapped(lines, page.ReadOnlyText, metaFont, usableWidth);
        AddWrapped(lines, page.BoundaryText, metaFont, usableWidth);
        AddWrapped(lines, page.EvidenceOnlyText, metaFont, usableWidth);
        AddWrapped(lines, page.CurrencyIsolationText, metaFont, usableWidth);
        AddWrapped(lines, page.NoProrationText, metaFont, usableWidth);
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
        DynamicAgencyServiceFeeMonthlyReportFieldDto column, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var max = EstimateWidthPoints(column.Label, HeaderSize);
        foreach (var row in rows)
            max = Math.Max(max, EstimateWidthPoints(FormatFieldCell(column, row), CellSize));

        return Math.Clamp(max + 6, Mm(MinColumnWidthMm), Mm(MaxColumnWidthMm));
    }

    /// <summary>把选定列按可用页宽贪心拆分为多个「列页」，保证每个列页总宽不超页宽（列不裁切）</summary>
    private static List<List<DynamicAgencyServiceFeeMonthlyReportFieldDto>> SplitColumnPages(
        IReadOnlyList<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        if (columns.Count == 0)
            return new List<List<DynamicAgencyServiceFeeMonthlyReportFieldDto>> { new() };

        var pages = new List<List<DynamicAgencyServiceFeeMonthlyReportFieldDto>>();
        var current = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>();
        var currentWidth = 0d;

        foreach (var column in columns)
        {
            var width = ColumnWidthPoints(column, rows);
            if (current.Count > 0 && currentWidth + width > usableWidth)
            {
                pages.Add(current);
                current = new List<DynamicAgencyServiceFeeMonthlyReportFieldDto>();
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
        IReadOnlyList<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var widths = new double[columns.Count];
        for (var c = 0; c < columns.Count; c++)
            widths[c] = ColumnWidthPoints(columns[c], rows);
        return widths;
    }

    private static double DrawPageHead(
        XGraphics gfx, XFont titleFont, XFont metaFont,
        DynamicAgencyServiceFeeMonthlyReportPageDto page,
        int columnPage, int columnPageCount, int rowPage, int rowPageCount,
        IReadOnlyList<string> headNotes)
    {
        var left = Mm(MarginLeftMm);
        var width = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);

        var title = "代理服务费月度汇总报表";
        if (columnPageCount > 1)
            title += $"（列 {columnPage + 1}/{columnPageCount}）";

        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), width, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"共 {page.Total.ToString(CultureInfo.InvariantCulture)} 个月份分组"
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
        IReadOnlyList<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns, double[] widths, double y)
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
        IReadOnlyList<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns, double[] widths,
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

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（与绘制共用同一口径，供测试验证字段顺序）</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<DynamicAgencyServiceFeeMonthlyReportFieldDto> columns,
        Dictionary<string, object?> row)
    {
        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
            cells.Add(FormatFieldCell(col, row));
        return cells;
    }

    /// <summary>字段感知的单元格文本：本报表字段均为聚合证据（非 null），统一按通用口径格式化；null 兜底为空文本（绝不回落为 0）</summary>
    public static string FormatFieldCell(
        DynamicAgencyServiceFeeMonthlyReportFieldDto field, Dictionary<string, object?> row)
    {
        var value = row.TryGetValue(field.Key, out var v) ? v : null;
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
