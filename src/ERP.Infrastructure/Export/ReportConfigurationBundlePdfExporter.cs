using ERP.Application.Common;
using ERP.Application.DTOs;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用报表配置捆绑（ERP-307 Stage 2）中文 PDF 导出器：无状态、无数据集特化分派，只消费统一的
/// <see cref="ReportConfigurationBundleExportResultDto"/>，生成一个多节 PDF（每节一个命名节标题 + 可读表头 /
/// 分页边界的证据表，节间显式分页）。全部节都已授权、已校验后才渲染；任何一节被拒绝 / 撤销即整体失败。
/// <para>口径与既有 <see cref="ReportConfigurationPdfExporter"/> 对齐：复用其公开格式化助手（列头 / 单元格 /
/// 折行），保留每节选定列顺序、类型化值（负数保留符号、null 未知留空、日期 / 布尔正确呈现）、每节独立
/// 币种 / 单位 / 粒度 / 证据与既有查询 / 审计边界，绝不跨节合并或换算；中文字体固定使用 Windows 黑体
/// （SimHei，共享解析器 <see cref="SimHeiPdfFontResolver"/>），字体缺失显式失败（绝不产出乱码 / 缺字 / 损坏 PDF）。</para>
/// <para>全程只读：仅生成 PDF 字节流，不写库、不执行任意 SQL。</para>
/// </summary>
public static class ReportConfigurationBundlePdfExporter
{
    private const string FontFamily = SimHeiPdfFontResolver.FontFamily;

    private const double PageWidthMm = 210;
    private const double PageHeightMm = 297;
    private const double MarginLeftMm = 10;
    private const double MarginRightMm = 10;
    private const double MarginTopMm = 12;
    private const double MarginBottomMm = 12;

    private const double TitleSize = 12;
    private const double MetaSize = 8;
    private const double HeaderSize = 8;
    private const double CellSize = 8;

    private const double TitleHeightMm = 8;
    private const double MetaHeightMm = 5;
    private const double HeaderRowHeightMm = 7;
    private const double DataLineHeightMm = 5;
    private const double CellPaddingPoints = 3;

    private static double Mm(double mm) => mm * 72.0 / 25.4;

    /// <summary>导出捆绑为 PDF 字节流（只读；字体缺失显式失败）。</summary>
    public static byte[] Export(ReportConfigurationBundleExportResultDto bundle)
        => Export(bundle, SimHeiPdfFontResolver.FindFontPath(), CancellationToken.None);

    /// <summary>导出捆绑为 PDF 字节流（可传播联动取消令牌到渲染循环）。</summary>
    public static byte[] Export(ReportConfigurationBundleExportResultDto bundle, CancellationToken cancellationToken)
        => Export(bundle, SimHeiPdfFontResolver.FindFontPath(), cancellationToken);

    /// <summary>导出捆绑为 PDF 字节流；<paramref name="fontPath"/> 为空或文件不存在时显式失败。</summary>
    public static byte[] Export(ReportConfigurationBundleExportResultDto bundle, string? fontPath)
        => Export(bundle, fontPath, CancellationToken.None);

    /// <summary>导出捆绑为 PDF 字节流（字体缺失显式失败；渲染循环可传播取消）。</summary>
    public static byte[] Export(ReportConfigurationBundleExportResultDto bundle, string? fontPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
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
            document.Info.Title = string.IsNullOrWhiteSpace(bundle.Name) ? "报表配置捆绑" : bundle.Name;

            var renderer = new Renderer(document);
            var sections = bundle.Sections ?? new List<ReportConfigurationBundleSectionExportDto>();
            foreach (var section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                renderer.DrawSection(section, cancellationToken);
                if (!ReferenceEquals(section, sections[^1]))
                    renderer.NewPage();
            }

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
                "PDF 导出失败：渲染捆绑报表时发生错误，未生成任何文件，请稍后重试。",
                ErrorCodes.InternalError);
        }
    }

    private sealed class Renderer
    {
        private readonly PdfDocument _document;
        private readonly XFont _titleFont;
        private readonly XFont _metaFont;
        private readonly XFont _headerFont;
        private readonly XFont _cellFont;
        private readonly XPen _borderPen;
        private readonly XSolidBrush _headerBrush;
        private readonly double _usableWidth;
        private readonly double _contentBottom;
        private readonly double _lineHeight;
        private XGraphics _gfx = null!;
        private double _y;

        public Renderer(PdfDocument document)
        {
            _document = document;
            _titleFont = new XFont(FontFamily, TitleSize, XFontStyleEx.Bold);
            _metaFont = new XFont(FontFamily, MetaSize, XFontStyleEx.Regular);
            _headerFont = new XFont(FontFamily, HeaderSize, XFontStyleEx.Bold);
            _cellFont = new XFont(FontFamily, CellSize, XFontStyleEx.Regular);
            _borderPen = new XPen(XColor.FromArgb(0xC4, 0xC4, 0xC4), 0.4);
            _headerBrush = new XSolidBrush(XColor.FromArgb(0xED, 0xED, 0xED));
            _usableWidth = Mm(PageWidthMm - MarginLeftMm - MarginRightMm);
            _contentBottom = Mm(PageHeightMm - MarginBottomMm);
            _lineHeight = Mm(DataLineHeightMm);
            NewPage();
        }

        public void NewPage()
        {
            var page = _document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            _gfx = XGraphics.FromPdfPage(page);
            _y = Mm(MarginTopMm);
        }

        private void EnsureSpace(double height)
        {
            if (_y + height > _contentBottom)
                NewPage();
        }

        public void DrawSection(ReportConfigurationBundleSectionExportDto section, CancellationToken cancellationToken)
        {
            var columns = section.Preview?.Columns ?? new List<ReportConfigurationColumnDto>();
            var rows = section.Facts is { Count: > 0 }
                ? section.Facts
                : (section.Preview?.Rows ?? new List<Dictionary<string, object?>>());

            var widths = ColumnWidths(columns.Count);
            var metaText = BuildMetaText(section);

            EnsureSpace(Mm(TitleHeightMm + MetaHeightMm + HeaderRowHeightMm) + _lineHeight);
            DrawTitle(section.Ordinal, section.Title);
            DrawMeta(metaText);
            DrawHeaderRow(columns, widths);

            if (columns.Count == 0)
            {
                DrawNote("（空节：无选定列）");
                return;
            }

            if (rows.Count == 0)
            {
                DrawNote("没有符合条件的数据");
                return;
            }

            for (var r = 0; r < rows.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowHeight = RowHeight(columns, widths, rows[r], r + 1);
                EnsureSpace(rowHeight);
                DrawDataRow(columns, widths, rows[r], r + 1, rowHeight);
            }

            _y += Mm(2);
        }

        private void DrawTitle(int ordinal, string title)
        {
            _gfx.DrawString($"{ordinal}. {title}", _titleFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), _y, _usableWidth, Mm(TitleHeightMm)), XStringFormats.TopLeft);
            _y += Mm(TitleHeightMm);
        }

        private void DrawMeta(string metaText)
        {
            _gfx.DrawString(metaText, _metaFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), _y, _usableWidth, Mm(MetaHeightMm)), XStringFormats.TopLeft);
            _y += Mm(MetaHeightMm);
        }

        private void DrawHeaderRow(IReadOnlyList<ReportConfigurationColumnDto> columns, double[] widths)
        {
            var left = Mm(MarginLeftMm);
            for (var c = 0; c < columns.Count; c++)
            {
                var rect = new XRect(left + SumWidths(widths, c), _y, widths[c], Mm(HeaderRowHeightMm));
                _gfx.DrawRectangle(_borderPen, _headerBrush, rect);
                _gfx.DrawString(ReportConfigurationPdfExporter.HeaderText(columns[c]), _headerFont,
                    XBrushes.Black, rect, XStringFormats.Center);
            }

            _y += Mm(HeaderRowHeightMm);
        }

        private void DrawDataRow(
            IReadOnlyList<ReportConfigurationColumnDto> columns,
            double[] widths,
            Dictionary<string, object?> row,
            int rowOrdinal,
            double rowHeight)
        {
            var left = Mm(MarginLeftMm);
            for (var c = 0; c < columns.Count; c++)
            {
                var rect = new XRect(left + SumWidths(widths, c), _y, widths[c], rowHeight);
                _gfx.DrawRectangle(_borderPen, rect);

                var text = string.Equals(columns[c].Key, ReportConfigurationPdfExporter.RowOrdinalColumnKey, StringComparison.Ordinal)
                    ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                    : ReportConfigurationPdfExporter.FormatCellValue(columns[c], row);

                var isNumber = string.Equals(columns[c].Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(columns[c].Key, ReportConfigurationPdfExporter.RowOrdinalColumnKey, StringComparison.Ordinal);
                var format = isNumber ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
                DrawCellText(text, rect, format);
            }

            _y += rowHeight;
        }

        private void DrawNote(string note)
        {
            _gfx.DrawString(note, _cellFont, XBrushes.Black,
                new XRect(Mm(MarginLeftMm), _y + Mm(2), _usableWidth, _lineHeight), XStringFormats.TopLeft);
            _y += _lineHeight + Mm(4);
        }

        private void DrawCellText(string text, XRect rect, XStringFormat format)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var maxWidth = Math.Max(0, rect.Width - CellPaddingPoints * 2);
            var lines = ReportConfigurationPdfExporter.WrapText(text, CellSize, maxWidth);

            var verticalPadding = Math.Max(0, (rect.Height - lines.Count * _lineHeight) / 2);
            var lineRect = new XRect(
                rect.X + CellPaddingPoints,
                rect.Y + verticalPadding,
                Math.Max(0, rect.Width - CellPaddingPoints * 2),
                _lineHeight);

            foreach (var line in lines)
            {
                _gfx.DrawString(line, _cellFont, XBrushes.Black, lineRect, format);
                lineRect.Y += _lineHeight;
            }
        }

        private double RowHeight(
            IReadOnlyList<ReportConfigurationColumnDto> columns,
            double[] widths,
            Dictionary<string, object?> row,
            int rowOrdinal)
        {
            var maxLines = 1;
            for (var c = 0; c < columns.Count; c++)
            {
                var text = string.Equals(columns[c].Key, ReportConfigurationPdfExporter.RowOrdinalColumnKey, StringComparison.Ordinal)
                    ? rowOrdinal.ToString(CultureInfo.InvariantCulture)
                    : ReportConfigurationPdfExporter.FormatCellValue(columns[c], row);
                if (string.IsNullOrEmpty(text))
                    continue;
                var lines = ReportConfigurationPdfExporter.WrapText(text, CellSize, Math.Max(0, widths[c] - CellPaddingPoints * 2)).Count;
                if (lines > maxLines)
                    maxLines = lines;
            }

            return Math.Max(_lineHeight, maxLines * _lineHeight + Mm(1.2));
        }

        private double[] ColumnWidths(int count)
        {
            if (count <= 0)
                return Array.Empty<double>();

            var width = Math.Min(Mm(80), _usableWidth / count);
            var widths = new double[count];
            for (var i = 0; i < count; i++)
                widths[i] = width;
            return widths;
        }

        private static double SumWidths(double[] widths, int end)
        {
            var sum = 0d;
            for (var i = 0; i < end; i++)
                sum += widths[i];
            return sum;
        }

        private static string BuildMetaText(ReportConfigurationBundleSectionExportDto section)
        {
            var version = section.RevisionVersion is int revision && revision > 0
                ? $"固定修订 v{revision}"
                : $"草稿 v{section.Preview?.Version ?? 0}";
            var coverage = string.Equals(section.Coverage, ReportConfigurationConstants.CoverageMatchedSet, StringComparison.OrdinalIgnoreCase)
                ? $"匹配集 {section.MatchedCount} 条"
                : $"当前页 {section.Facts?.Count ?? 0} 行";

            var parts = new List<string>
            {
                $"定义：{section.Preview?.Name ?? string.Empty}（{version}）",
                $"数据集：{section.Preview?.DatasetKey ?? string.Empty}",
                $"覆盖：{coverage}",
            };

            var semantics = section.Preview?.Evidence?.CurrencyUnitSemantics;
            if (!string.IsNullOrWhiteSpace(semantics))
                parts.Add($"币种/单位：{semantics}");

            return string.Join(" · ", parts);
        }
    }
}


