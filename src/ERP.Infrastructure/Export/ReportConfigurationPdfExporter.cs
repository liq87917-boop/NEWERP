using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用报表配置平台（ERP-264 Stage 1）中文 PDF 导出器：无状态、无数据集特化分派，只消费统一的
/// <see cref="ReportConfigurationPreviewDto"/>（列 / 行 / 分组页面小计 / 证据上下文 + 定义名称 / 版本 +
/// 规范化筛选 / 日期范围），与 Excel 导出复用同一份有界、已授权预览。
/// <para>口径：仅导出当前预览页选定列（保留选择顺序）；宽列集按可用页宽拆成多个「列页」，每个列页恒重复
/// 允许的「行标识」列（订单号 / 发票号等；若未选择任何允许标识列则回退为当前页行序号「本页序号」），
/// 绝不泄露未授权隐藏字段；每页重复表头，文本折行、数值右对齐并保留符号，绝不裁切负数或上下文。
/// null 未知一律留空（绝不写成 0），并在头部元数据说明未知值口径。分组小计仅覆盖当前预览页且按币种分区
/// （绝不跨币种 / 单位相加、绝不追加全匹配合计）；元数据始终包含定义名称 / 版本、数据集 / 行粒度、
/// 规范化查询筛选与日期范围、当前页覆盖口径、币种 / 单位语义、只读与边界、未知值说明。</para>
/// <para>中文字体固定使用 Windows 黑体（SimHei），与其它报表 PDF 共用共享解析器（<see cref="SimHeiPdfFontResolver"/>），
/// 字体缺失或渲染失败时显式失败（绝不产出乱码 / 缺字 / 损坏 PDF）。全程只读：仅生成 PDF 字节流，不写库、
/// 不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public static class ReportConfigurationPdfExporter
{
    /// <summary>无允许标识列时回退的「当前页行序号」列键（合成列，不映射任何数据集字段）</summary>
    public const string RowOrdinalColumnKey = "__rowOrdinal__";

    /// <summary>无允许标识列时回退的「当前页行序号」列标签</summary>
    public const string RowOrdinalLabel = "本页序号";

    /// <summary>未知值说明（null 在数据 / 小计中留空，绝不写成 0 或推算值）</summary>
    public const string UnknownValueText = "空单元格表示未知（null），绝不写成 0 或推算值";

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
    private const double DataLineHeightMm = 6;
    private const double DataRowVerticalPaddingMm = 1.2;

    /// <summary>列宽有界（毫米）：最小保证中文标签可读；文本最大避免单列独占整页，超宽靠「列页」拆分而非挤压；
    /// 数值列允许更宽，避免裁切符号 / 数值。</summary>
    private const double MinColumnWidthMm = 20;
    private const double MaxColumnWidthMm = 45;
    private const double MaxNumberColumnWidthMm = 70;

    private const double CellPaddingPoints = 3;

    private const double PointsPerMillimeter = 72.0 / 25.4;

    private static readonly HashSet<string> IdentityColumnKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "orderNo", "orderNumber", "invoiceId", "invoiceNumber", "invoiceCode",
        "identityText", "receiptNo", "statementNo",
    };

    /// <summary>字段键是否为允许的「行标识」列（用于跨列页重复，仅限已授权选定列）。</summary>
    public static bool IsIdentityColumn(string? key)
        => !string.IsNullOrWhiteSpace(key) && IdentityColumnKeys.Contains(key);

    /// <summary>合成「当前页行序号」列（无允许标识列时回退使用）。</summary>
    public static ReportConfigurationColumnDto RowOrdinalColumn()
        => new(RowOrdinalColumnKey, RowOrdinalLabel, ReportConfigurationConstants.TypeNumber, null);

    /// <summary>导出当前预览页为 PDF 字节流（只读；字体缺失显式失败）</summary>
    public static byte[] Export(ReportConfigurationPreviewDto preview)
        => Export(preview, SimHeiPdfFontResolver.FindFontPath(), CancellationToken.None);

    /// <summary>导出当前预览页为 PDF 字节流（可传播联动取消令牌到渲染循环）。</summary>
    public static byte[] Export(ReportConfigurationPreviewDto preview, CancellationToken cancellationToken)
        => Export(preview, SimHeiPdfFontResolver.FindFontPath(), cancellationToken);

    /// <summary>
    /// 导出当前预览页为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。
    /// <para>公开该重载以便单元测试注入「字体缺失」路径，以及显式控制字体文件位置。</para>
    /// </summary>
    public static byte[] Export(ReportConfigurationPreviewDto preview, string? fontPath)
        => Export(preview, fontPath, CancellationToken.None);

    /// <summary>导出当前预览页为 PDF 字节流（字体缺失显式失败；渲染循环可传播取消）。</summary>
    public static byte[] Export(ReportConfigurationPreviewDto preview, string? fontPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            throw new BusinessException(
                "PDF 导出失败：未找到中文字体 SimHei（黑体）。请在 Windows 字体目录安装 simhei.ttf 后重试，避免生成乱码或缺字 PDF。",
                ErrorCodes.InternalError);
        }

        SimHeiPdfFontResolver.Ensure(fontPath);

        try
        {
            using var document = new PdfDocument();
            document.Info.Title = string.IsNullOrWhiteSpace(preview.Name) ? "报表配置" : preview.Name;

            DrawReport(document, preview, cancellationToken);
            DrawSubtotalSection(document, preview, cancellationToken);
            DrawMetricsSection(document, preview, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new MemoryStream();
            document.Save(stream, false);
            return stream.ToArray();
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (OperationCanceledException)
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

    // ==================== 单元格 / 列头格式化 ====================

    /// <summary>列头文本：中文标签 + 可选币种 / 单位语义（与 Excel 一致）</summary>
    public static string HeaderText(ReportConfigurationColumnDto column)
        => string.IsNullOrWhiteSpace(column.CurrencyUnit)
            ? column.Label
            : $"{column.Label}（{column.CurrencyUnit}）";

    /// <summary>把预览行单元格值转成 PDF 单元格文本（中文布尔 / 日期 / 数值口径与 Excel 保持一致）</summary>
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
            case DateTimeOffset dto:
                return dto.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    /// <summary>按选定列顺序把一行转成 PDF 单元格文本（缺字段 / null 一律留空，绝不写成 0）</summary>
    public static string FormatCellValue(ReportConfigurationColumnDto column, Dictionary<string, object?> row)
        => row is not null && row.TryGetValue(column.Key, out var value)
            ? FormatCellValue(value)
            : string.Empty;

    /// <summary>按列顺序把一行转成 PDF 单元格文本（合成「本页序号」列返回 <paramref name="rowOrdinal"/>）。</summary>
    public static IReadOnlyList<string> BuildRowCells(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        Dictionary<string, object?> row,
        int rowOrdinal)
    {
        columns ??= Array.Empty<ReportConfigurationColumnDto>();
        var cells = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            cells.Add(string.Equals(column.Key, RowOrdinalColumnKey, StringComparison.Ordinal)
                ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                : FormatCellValue(column, row));
        }
        return cells;
    }

    // ==================== 列页拆分与列宽 ====================

    /// <summary>按可用页宽把选定列拆成多个「列页」，每个列页恒重复允许的标识列（无标识列则回退本页序号）。</summary>
    public static IReadOnlyList<IReadOnlyList<ReportConfigurationColumnDto>> SplitColumnPages(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        columns ??= Array.Empty<ReportConfigurationColumnDto>();
        rows ??= Array.Empty<Dictionary<string, object?>>();

        var identity = ResolveIdentity(columns);
        return SplitBands(columns, identity, rows, Mm(PageWidthMm - MarginLeftMm - MarginRightMm));
    }

    private static List<ReportConfigurationColumnDto> ResolveIdentity(IReadOnlyList<ReportConfigurationColumnDto> columns)
    {
        var identity = columns.Where(c => IsIdentityColumn(c.Key)).ToList();
        if (identity.Count == 0)
            identity.Add(RowOrdinalColumn());
        return identity;
    }

    private static bool IsIdentity(string key)
        => IsIdentityColumn(key) || string.Equals(key, RowOrdinalColumnKey, StringComparison.Ordinal);

    private static List<List<ReportConfigurationColumnDto>> SplitBands(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        IReadOnlyList<ReportConfigurationColumnDto> identity,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        var data = columns.Where(c => !IsIdentityColumn(c.Key)).ToList();

        var identityWidth = identity.Sum(c => ColumnWidthPoints(c, rows, usableWidth));
        var dataUsable = Math.Max(MinColumnWidthPoints, usableWidth - identityWidth);

        var pages = new List<List<ReportConfigurationColumnDto>>();
        var current = new List<ReportConfigurationColumnDto>();
        var used = 0d;

        foreach (var column in data)
        {
            var width = ColumnWidthPoints(column, rows, dataUsable);
            if (current.Count > 0 && used + width > dataUsable)
            {
                pages.Add(new List<ReportConfigurationColumnDto>(identity).Concat(current).ToList());
                current = new List<ReportConfigurationColumnDto>();
                used = 0d;
            }

            current.Add(column);
            used += width;
        }

        if (current.Count > 0 || pages.Count == 0)
            pages.Add(new List<ReportConfigurationColumnDto>(identity).Concat(current).ToList());

        return pages;
    }

    private static double[] ComputeBandWidths(
        IReadOnlyList<ReportConfigurationColumnDto> band,
        IReadOnlyList<ReportConfigurationColumnDto> identity,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        var identityWidth = identity.Sum(c => ColumnWidthPoints(c, rows, usableWidth));
        var dataUsable = Math.Max(MinColumnWidthPoints, usableWidth - identityWidth);

        return band.Select(c =>
            IsIdentity(c.Key)
                ? ColumnWidthPoints(c, rows, usableWidth)
                : ColumnWidthPoints(c, rows, dataUsable)).ToArray();
    }

    private static double ColumnWidthPoints(
        ReportConfigurationColumnDto column,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double maxWidthPoints)
    {
        var max = EstimateWidthPoints(HeaderText(column), HeaderSize) + CellPaddingPoints * 2;
        foreach (var row in rows)
        {
            var width = EstimateWidthPoints(FormatCellValue(column, row), CellSize) + CellPaddingPoints * 2;
            if (width > max)
                max = width;
        }

        var isNumber = string.Equals(column.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase);
        var maxMm = isNumber ? MaxNumberColumnWidthMm : MaxColumnWidthMm;
        var capMm = Math.Min(maxMm, maxWidthPoints / PointsPerMillimeter);
        var mm = Math.Clamp(max / PointsPerMillimeter, MinColumnWidthMm, Math.Max(MinColumnWidthMm, capMm));
        return Mm(mm);
    }

    // ==================== 折行与宽度估算 ====================

    private static double MinColumnWidthPoints => Mm(MinColumnWidthMm);

    /// <summary>按纯文本宽度估算折行（逐字符、绝不丢字；负号始终保留在首行，绝不裁切）。</summary>
    public static IReadOnlyList<string> WrapText(string? text, double size, double maxWidthPoints)
    {
        if (string.IsNullOrEmpty(text))
            return new[] { string.Empty };

        if (maxWidthPoints <= 0 || EstimateWidthPoints(text, size) <= maxWidthPoints)
            return new[] { text };

        var lines = new List<string>();
        var current = string.Empty;
        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (current.Length > 0 && EstimateWidthPoints(candidate, size) > maxWidthPoints)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
            lines.Add(current);

        if (lines.Count == 0)
            lines.Add(text);

        return lines;
    }

    private static string BuildComputedEvidenceText(
        IReadOnlyList<ReportConfigurationComputedColumnEvidenceDto>? computedColumns)
    {
        if (computedColumns is null || computedColumns.Count == 0)
            return string.Empty;

        return string.Join("；", computedColumns.Select(c =>
        {
            var unit = string.IsNullOrWhiteSpace(c.Unit) ? string.Empty : $"（{c.Unit}）";
            var deps = c.Dependencies is { Count: > 0 } ? $"；依赖：{string.Join(", ", c.Dependencies)}" : string.Empty;
            return $"{c.Label}{unit}：{c.UnknownReason}{deps}";
        }));
    }

    /// <summary>纯文本宽度估算（点）：中文 / 全角按一个字号宽、半角按半个字号宽（不依赖已注册字体）。</summary>
    public static double EstimateWidthPoints(string? text, double size)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var width = 0d;
        foreach (var ch in text)
            width += IsWideChar(ch) ? size : size * 0.5;
        return width;
    }

    private static bool IsWideChar(char c) => c > 0x2E80;

    // ==================== 头部元数据 ====================

    private static List<string> BuildHeadNotes(ReportConfigurationPreviewDto preview)
    {
        var evidence = preview.Evidence;
        var notes = new List<string>();

        var grain = evidence?.Grain;
        notes.Add(string.IsNullOrWhiteSpace(grain)
            ? $"数据集：{preview.DatasetKey}"
            : $"数据集：{preview.DatasetKey} · 行粒度：{grain}");

        if (!string.IsNullOrWhiteSpace(evidence?.CurrencyUnitSemantics))
            notes.Add($"币种/单位口径：{evidence.CurrencyUnitSemantics}");

        var computedText = BuildComputedEvidenceText(preview.ComputedColumns);
        if (!string.IsNullOrWhiteSpace(computedText))
            notes.Add($"计算列口径：{computedText}");

        notes.Add($"查询筛选：{(string.IsNullOrWhiteSpace(preview.NormalizedFiltersText) ? "无筛选" : preview.NormalizedFiltersText)}");
        notes.Add($"日期范围：{(string.IsNullOrWhiteSpace(preview.DateRangeText) ? "无日期筛选" : preview.DateRangeText)}");
        notes.Add($"排序：{(string.IsNullOrWhiteSpace(preview.SortEvidence) ? "默认排序（稳定分页）" : preview.SortEvidence)}");
        notes.Add($"页面覆盖：第 {preview.Page} 页 · 每页 {preview.PageSize} 条 · 命中 {preview.Total} 条 · 共 {preview.TotalPages} 页 · 当前预览页（非全量合计）");

        var readOnly = evidence?.ReadOnlyText ?? string.Empty;
        var boundary = evidence?.BoundaryText ?? string.Empty;
        var readOnlyBoundary = string.IsNullOrWhiteSpace(readOnly)
            ? boundary
            : (string.IsNullOrWhiteSpace(boundary) ? readOnly : $"{readOnly}；{boundary}");
        if (!string.IsNullOrWhiteSpace(readOnlyBoundary))
            notes.Add($"只读与边界：{readOnlyBoundary}");

        notes.Add($"未知值说明：{UnknownValueText}");

        if (!string.IsNullOrWhiteSpace(evidence?.DisclaimerText))
            notes.Add($"免责声明：{evidence.DisclaimerText}");

        return notes;
    }

    private static List<string> WrapHeadNotes(IReadOnlyList<string> notes, double usableWidth)
    {
        var lines = new List<string>();
        var maxWidth = Math.Max(0, usableWidth - CellPaddingPoints * 2);
        foreach (var note in notes)
            lines.AddRange(WrapText(note, MetaSize, maxWidth));
        return lines;
    }

    private static string VersionLabel(ReportConfigurationPreviewDto preview)
        => preview.PinnedRevisionVersion.HasValue
            ? $"发布修订 {preview.PinnedRevisionVersion.Value}"
            : $"草稿（版本令牌 {preview.Version}）";

    // ==================== 分组小计（当前页、按币种分区） ====================

    /// <summary>分组小计表列（与 Excel「分组小计（当前页）」同口径）</summary>
    public static IReadOnlyList<ReportConfigurationColumnDto> SubtotalColumns { get; } = new[]
    {
        new ReportConfigurationColumnDto("group", "分组", ReportConfigurationConstants.TypeText, null),
        new ReportConfigurationColumnDto("currency", "币种", ReportConfigurationConstants.TypeText, null),
        new ReportConfigurationColumnDto("count", "条数", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("amount", "金额", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("grossAmount", "含税金额", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("effectiveAllocatedAmount", "已分摊金额", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("remainingAmount", "剩余金额", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("remainingState", "剩余状态", ReportConfigurationConstants.TypeText, null),
    };

    /// <summary>把分组页面小计拍平为导出行（每个分组 × 币种一行；空 / 无分组返回空列表，绝不追加全匹配合计）。</summary>
    public static IReadOnlyList<Dictionary<string, object?>> BuildSubtotalRows(
        IReadOnlyList<ReportConfigurationGroupSubtotalDto>? groups)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (groups is null)
            return rows;

        foreach (var group in groups)
        {
            foreach (var partition in group.Partitions)
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["group"] = group.Label,
                    ["currency"] = partition.Currency,
                    ["count"] = partition.Count,
                    ["amount"] = partition.Amount,
                    ["grossAmount"] = partition.GrossAmount,
                    ["effectiveAllocatedAmount"] = partition.EffectiveAllocatedAmount,
                    ["remainingAmount"] = partition.RemainingAmount,
                    ["remainingState"] = partition.RemainingState,
                });
            }
        }

        return rows;
    }

    // ==================== 指标汇总（当前预览页、分组 + 币种分区） ====================

    /// <summary>指标汇总表列（与 Excel「指标汇总（当前页）」同口径）</summary>
    public static IReadOnlyList<ReportConfigurationColumnDto> MetricsColumns { get; } = new[]
    {
        new ReportConfigurationColumnDto("metric", "指标", ReportConfigurationConstants.TypeText, null),
        new ReportConfigurationColumnDto("group", "分组", ReportConfigurationConstants.TypeText, null),
        new ReportConfigurationColumnDto("currency", "币种", ReportConfigurationConstants.TypeText, null),
        new ReportConfigurationColumnDto("value", "数值", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("known", "已知值条数", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("missing", "缺失条数", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("source", "来源条数", ReportConfigurationConstants.TypeNumber, null),
        new ReportConfigurationColumnDto("reason", "原因", ReportConfigurationConstants.TypeText, null),
    };

    /// <summary>把选中指标拍平为导出行（每个指标 × 分组 × 币种一行；无指标返回空列表，绝不追加全匹配合计）。</summary>
    public static IReadOnlyList<Dictionary<string, object?>> BuildMetricRows(
        IReadOnlyList<ReportConfigurationMetricResultDto>? metrics)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (metrics is null)
            return rows;

        foreach (var metric in metrics)
        {
            var title = MetricTitle(metric);
            foreach (var cell in metric.Cells)
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["metric"] = title,
                    ["group"] = cell.GroupLabel,
                    ["currency"] = cell.Currency,
                    ["value"] = cell.Value,
                    ["known"] = cell.KnownCount,
                    ["missing"] = cell.MissingCount,
                    ["source"] = cell.SourceCount,
                    ["reason"] = cell.Reason,
                });
            }
        }

        return rows;
    }

    private static string MetricTitle(ReportConfigurationMetricResultDto metric)
    {
        var unit = string.IsNullOrWhiteSpace(metric.Unit) ? string.Empty : $"（{metric.Unit}）";
        return $"{metric.Label}（{ReportConfigurationMetricRules.FunctionLabel(metric.Function)}）{unit}";
    }

    private static void DrawMetricsSection(PdfDocument document, ReportConfigurationPreviewDto preview, CancellationToken cancellationToken)
    {
        var metrics = preview.Metrics;
        if (metrics is null || metrics.Count == 0)
            return;

        var rows = BuildMetricRows(metrics);
        if (rows.Count == 0)
            return;

        var columns = MetricsColumns;
        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var lineHeight = Mm(DataLineHeightMm);

        var gfx = NewPage(document);
        try
        {
            var widths = ComputeSubtotalWidths(columns, rows, usableWidth);

            var y = Mm(MarginTopMm);
            gfx.DrawString("指标汇总（当前页）", titleFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y, usableWidth, Mm(TitleHeightMm)), XStringFormats.TopCenter);
            y += Mm(TitleHeightMm);

            gfx.DrawString("仅当前预览页；金额按币种分区，绝不跨币种 / 单位相加，绝不等于全匹配合计。",
                metaFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y, usableWidth, Mm(MetaHeightMm)), XStringFormats.TopLeft);
            y += Mm(MetaHeightMm + 2);

            y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, columns, widths, y);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var height = RowHeightPoints(columns, widths, row, 0, lineHeight);
                DrawDataRow(gfx, cellFont, borderPen, columns, widths, row, 0, y, height, lineHeight);
                y += height;
            }
        }
        finally
        {
            gfx.Dispose();
        }
    }

    // ==================== 绘制 ====================

    private static void DrawReport(PdfDocument document, ReportConfigurationPreviewDto preview, CancellationToken cancellationToken)
    {
        var columns = preview.Columns ?? new List<ReportConfigurationColumnDto>();
        var rows = preview.Rows ?? new List<Dictionary<string, object?>>();

        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var contentBottom = Mm(PageHeightMm - MarginBottomMm);
        var headerHeight = Mm(HeaderRowHeightMm);
        var lineHeight = Mm(DataLineHeightMm);

        var noteLines = WrapHeadNotes(BuildHeadNotes(preview), usableWidth);
        var headHeight = Mm(TitleHeightMm + MetaHeightMm) + noteLines.Count * Mm(NoteLineHeightMm);
        var contentHeight = Math.Max(lineHeight, contentBottom - Mm(MarginTopMm) - headHeight - headerHeight);

        var identity = ResolveIdentity(columns);
        var bands = SplitBands(columns, identity, rows, usableWidth);

        var graphics = new List<XGraphics>();
        try
        {
            if (rows.Count == 0)
            {
                var band = bands[0];
                var widths = ComputeBandWidths(band, identity, rows, usableWidth);
                var gfx = NewPage(document);
                graphics.Add(gfx);
                var y = DrawPageHead(gfx, titleFont, metaFont, preview, 0, bands.Count, 0, 1, noteLines, usableWidth);
                y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, band, widths, y);
                DrawEmptyNote(gfx, cellFont, usableWidth, y);
            }
            else
            {
                for (var cp = 0; cp < bands.Count; cp++)
                {
                    var band = bands[cp];
                    var widths = ComputeBandWidths(band, identity, rows, usableWidth);
                    var rowPages = PartitionRows(band, widths, rows, contentHeight, lineHeight);

                    for (var rp = 0; rp < rowPages.Count; rp++)
                    {
                        var gfx = NewPage(document);
                        graphics.Add(gfx);
                        var y = DrawPageHead(gfx, titleFont, metaFont, preview, cp, bands.Count, rp, rowPages.Count, noteLines, usableWidth);
                        y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, band, widths, y);

                        var (start, count) = rowPages[rp];
                        var rowY = y;
                        for (var i = 0; i < count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var rowIndex = start + i;
                            var rowHeight = RowHeightPoints(band, widths, rows[rowIndex], rowIndex + 1, lineHeight);
                            DrawDataRow(gfx, cellFont, borderPen, band, widths, rows[rowIndex], rowIndex + 1, rowY, rowHeight, lineHeight);
                            rowY += rowHeight;
                        }
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

    private static List<(int Start, int Count)> PartitionRows(
        IReadOnlyList<ReportConfigurationColumnDto> band,
        double[] widths,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double contentHeight,
        double lineHeight)
    {
        var pages = new List<(int, int)>();
        var start = 0;
        var used = 0d;

        for (var i = 0; i < rows.Count; i++)
        {
            var height = RowHeightPoints(band, widths, rows[i], i + 1, lineHeight);
            if (i > start && used + height > contentHeight)
            {
                pages.Add((start, i - start));
                start = i;
                used = 0d;
            }
            used += height;
        }

        if (start < rows.Count || pages.Count == 0)
            pages.Add((start, rows.Count - start));

        return pages;
    }

    private static double RowHeightPoints(
        IReadOnlyList<ReportConfigurationColumnDto> band,
        double[] widths,
        Dictionary<string, object?> row,
        int rowOrdinal,
        double lineHeight)
    {
        var lines = 1;
        for (var c = 0; c < band.Count; c++)
        {
            var text = string.Equals(band[c].Key, RowOrdinalColumnKey, StringComparison.Ordinal)
                ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                : FormatCellValue(band[c], row);
            var count = WrapText(text, CellSize, Math.Max(0, widths[c] - CellPaddingPoints * 2)).Count;
            if (count > lines)
                lines = count;
        }

        return lines * lineHeight + Mm(DataRowVerticalPaddingMm);
    }

    private static XGraphics NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width = new XUnit(PageWidthMm, XGraphicsUnit.Millimeter);
        page.Height = new XUnit(PageHeightMm, XGraphicsUnit.Millimeter);
        return XGraphics.FromPdfPage(page);
    }

    private static double DrawPageHead(
        XGraphics gfx,
        XFont titleFont,
        XFont metaFont,
        ReportConfigurationPreviewDto preview,
        int columnPage,
        int columnPageCount,
        int rowPage,
        int rowPageCount,
        IReadOnlyList<string> noteLines,
        double usableWidth)
    {
        var left = Mm(MarginLeftMm);

        var title = string.IsNullOrWhiteSpace(preview.Name) ? "报表配置" : preview.Name;
        if (columnPageCount > 1)
            title += $"（列 {columnPage + 1}/{columnPageCount}）";

        gfx.DrawString(title, titleFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm), usableWidth, Mm(TitleHeightMm)), XStringFormats.TopCenter);

        var meta = $"{VersionLabel(preview)} · 行 {rowPage + 1}/{rowPageCount}";
        gfx.DrawString(meta, metaFont, XBrushes.Black,
            new XRect(left, Mm(MarginTopMm + TitleHeightMm), usableWidth, Mm(MetaHeightMm)), XStringFormats.TopCenter);

        var y = Mm(MarginTopMm + TitleHeightMm + MetaHeightMm);
        foreach (var note in noteLines)
        {
            gfx.DrawString(note, metaFont, XBrushes.Black,
                new XRect(left, y, usableWidth, Mm(NoteLineHeightMm)), XStringFormats.TopLeft);
            y += Mm(NoteLineHeightMm);
        }

        return y;
    }

    private static double DrawColumnHeaders(
        XGraphics gfx,
        XFont font,
        XBrush brush,
        XPen pen,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        double[] widths,
        double y)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], Mm(HeaderRowHeightMm));
            gfx.DrawRectangle(pen, brush, rect);
            gfx.DrawString(HeaderText(columns[c]), font, XBrushes.Black, rect, XStringFormats.Center);
        }

        return y + Mm(HeaderRowHeightMm);
    }

    private static void DrawDataRow(
        XGraphics gfx,
        XFont font,
        XPen pen,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        double[] widths,
        Dictionary<string, object?> row,
        int rowOrdinal,
        double y,
        double rowHeight,
        double lineHeight)
    {
        var left = Mm(MarginLeftMm);
        for (var c = 0; c < columns.Count; c++)
        {
            var x = left + SumWidths(widths, c);
            var rect = new XRect(x, y, widths[c], rowHeight);
            gfx.DrawRectangle(pen, rect);

            var text = string.Equals(columns[c].Key, RowOrdinalColumnKey, StringComparison.Ordinal)
                ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                : FormatCellValue(columns[c], row);

            var isNumber = string.Equals(columns[c].Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
                || string.Equals(columns[c].Key, RowOrdinalColumnKey, StringComparison.Ordinal);
            var format = isNumber ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
            DrawCellText(gfx, text, font, rect, format, lineHeight);
        }
    }

    private static void DrawCellText(
        XGraphics gfx,
        string text,
        XFont font,
        XRect rect,
        XStringFormat format,
        double lineHeight)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var maxWidth = Math.Max(0, rect.Width - CellPaddingPoints * 2);
        var lines = WrapText(text, CellSize, maxWidth);

        var verticalPadding = (rect.Height - lines.Count * lineHeight) / 2;
        var lineRect = new XRect(
            rect.X + CellPaddingPoints,
            rect.Y + Math.Max(0, verticalPadding),
            rect.Width - CellPaddingPoints * 2,
            lineHeight);

        foreach (var line in lines)
        {
            gfx.DrawString(line, font, XBrushes.Black, lineRect, format);
            lineRect.Y += lineHeight;
        }
    }

    private static void DrawEmptyNote(XGraphics gfx, XFont font, double usableWidth, double y)
    {
        gfx.DrawString("没有符合条件的数据", font, XBrushes.Black,
            new XRect(Mm(MarginLeftMm), y + Mm(2), usableWidth, Mm(DataLineHeightMm)), XStringFormats.TopLeft);
    }

    // ==================== 分组小计分区（当前页、按币种分区） ====================

    private static void DrawSubtotalSection(PdfDocument document, ReportConfigurationPreviewDto preview, CancellationToken cancellationToken)
    {
        var groups = preview.Groups;
        if (groups is null || groups.Count == 0)
            return;

        var rows = BuildSubtotalRows(groups);
        if (rows.Count == 0)
            return;

        var columns = SubtotalColumns;
        var titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
        var metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
        var headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
        var cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);

        var borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
        var headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));

        var usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
        var lineHeight = Mm(DataLineHeightMm);

        var gfx = NewPage(document);
        try
        {
            var widths = ComputeSubtotalWidths(columns, rows, usableWidth);

            var y = Mm(MarginTopMm);
            gfx.DrawString("分组小计（当前页 · 按币种分区）", titleFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y, usableWidth, Mm(TitleHeightMm)), XStringFormats.TopCenter);
            y += Mm(TitleHeightMm);

            gfx.DrawString("仅当前预览页；金额按币种分区，绝不跨币种 / 单位相加，绝不等于全匹配合计。",
                metaFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), y, usableWidth, Mm(MetaHeightMm)), XStringFormats.TopLeft);
            y += Mm(MetaHeightMm + 2);

            y = DrawColumnHeaders(gfx, headerFont, headerBrush, borderPen, columns, widths, y);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var height = RowHeightPoints(columns, widths, row, 0, lineHeight);
                DrawDataRow(gfx, cellFont, borderPen, columns, widths, row, 0, y, height, lineHeight);
                y += height;
            }
        }
        finally
        {
            gfx.Dispose();
        }
    }

    private static double[] ComputeSubtotalWidths(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        var raw = columns.Select(c =>
        {
            var max = EstimateWidthPoints(HeaderText(c), HeaderSize) + CellPaddingPoints * 2;
            foreach (var row in rows)
            {
                var width = EstimateWidthPoints(FormatCellValue(c, row), CellSize) + CellPaddingPoints * 2;
                if (width > max)
                    max = width;
            }

            var isNumber = string.Equals(c.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase);
            var maxMm = isNumber ? MaxNumberColumnWidthMm : MaxColumnWidthMm;
            var mm = Math.Clamp(max / PointsPerMillimeter, MinColumnWidthMm, maxMm);
            return Mm(mm);
        }).ToArray();

        var sum = raw.Sum();
        if (sum <= usableWidth)
            return raw;

        var scale = usableWidth / sum;
        for (var i = 0; i < raw.Length; i++)
            raw[i] *= scale;

        return raw;
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
