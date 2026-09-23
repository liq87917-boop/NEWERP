using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ERP.Api.Services;

/// <summary>
/// 打印模板网格 ↔ Excel(.xlsx) 互转（基于项目已有的 NPOI）
/// <para>网格 JSON：{ font, cols:[px], rows:[px], cells:{"r_c":{v,bold,italic,align,fs,color,bg,bd}},</para>
/// <para>merges:[{r,c,rs,cs}], detailRow:{enabled,row}, watermark:{...} }</para>
/// </summary>
public static class PrintGridExcel
{
    /* ============ 网格 → Excel ============ */
    public static byte[] Build(string layoutJson, string title)
    {
        var grid = ParseGridJson(layoutJson);
        IWorkbook wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet(string.IsNullOrWhiteSpace(title) ? "打印模板" : Truncate(title, 28));

        // 列宽（px → Excel 字符宽：约 7px / 字符）
        for (int c = 0; c < grid.Cols.Count; c++)
            sheet.SetColumnWidth(c, Math.Clamp((int)Math.Round(grid.Cols[c] * 256.0 / 7.0), 256, 255 * 256));

        var covered = new HashSet<string>(grid.CoveredKeys);
        for (int r = 0; r < grid.Rows.Count; r++)
        {
            var row = sheet.CreateRow(r);
            row.HeightInPoints = (float)Math.Round(grid.Rows[r] * 0.75, 1);      // px → pt
            for (int c = 0; c < grid.Cols.Count; c++)
            {
                if (covered.Contains(r + "_" + c)) continue;                      // 合并覆盖区不写值
                var cell = row.CreateCell(c);
                var (value, style) = grid.GetCell(r, c);
                cell.SetCellValue(value ?? string.Empty);
                if (style != null) cell.CellStyle = BuildCellStyle(wb, style);
            }
        }

        // 合并单元格
        foreach (var m in grid.Merges)
        {
            try
            {
                sheet.AddMergedRegion(new CellRangeAddress(m.R, m.R + Math.Max(1, m.Rs) - 1,
                    m.C, m.C + Math.Max(1, m.Cs) - 1));
            }
            catch { /* 忽略非法区间 */ }
        }

        using var ms = new MemoryStream();
        wb.Write(ms, true);
        return ms.ToArray();
    }

    /* ============ 单元格样式：网格属性 → NPOI 样式 ============ */
    private static ICellStyle BuildCellStyle(IWorkbook wb, GridCellData cell)
    {
        var style = wb.CreateCellStyle();
        var font = wb.CreateFont();
        font.FontName = string.IsNullOrWhiteSpace(cell.Font) ? "微软雅黑" : cell.Font!;
        font.FontHeightInPoints = (short)Math.Clamp(cell.Fs <= 0 ? 12 : cell.Fs, 8, 40);
        font.IsBold = cell.Bold;
        font.IsItalic = cell.Italic;
        var fg = HexToColor(cell.Color);
        if (fg != null && font is XSSFFont xf) xf.SetColor(fg);
        style.SetFont(font);

        // 背景色
        var bg = HexToColor(cell.Bg);
        if (bg != null)
        {
            style.FillPattern = FillPattern.SolidForeground;
            if (style is XSSFCellStyle xcs) xcs.SetFillForegroundColor(bg);
            else style.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        }

        // 边框样式（颜色统一使用默认色）
        var bs = cell.Bd switch
        {
            0 => BorderStyle.None,
            2 => BorderStyle.Medium,
            _ => BorderStyle.Thin,
        };
        style.BorderTop = style.BorderBottom = style.BorderLeft = style.BorderRight = bs;
        // 边框颜色：不同 NPOI 版本 API 差异较大，导出时统一使用默认边框色（保证兼容）

        style.Alignment = cell.Align switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        style.VerticalAlignment = VerticalAlignment.Center;
        style.WrapText = false;
        return style;
    }

    private static XSSFColor? HexToColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(ch => "" + ch + ch));
        if (h.Length != 6) return null;
        try
        {
            var bytes = new byte[3];
            for (int i = 0; i < 3; i++) bytes[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return new XSSFColor(bytes);
        }
        catch { return null; }
    }

    private static string ColorToHex(IColor? color)
    {
        if (color is XSSFColor xc)
        {
            var rgb = xc.RGB;
            if (rgb != null && rgb.Length >= 3)
            {
                var offset = rgb.Length == 4 ? 1 : 0;                 // ARGB → 去掉 Alpha
                return "#" + BitConverter.ToString(rgb, offset, 3).Replace("-", "").ToLowerInvariant();
            }
        }
        return string.Empty;
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value.Substring(0, max));

    /* ============ 内部模型 ============ */
    private sealed class GridCellData
    {
        public string? V { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public string Align { get; set; } = "left";
        public int Fs { get; set; } = 12;
        public string? Color { get; set; }
        public string? Bg { get; set; }
        public int Bd { get; set; } = 1;
        public string? BorderColor { get; set; }
        public string? Font { get; set; }
    }

    private sealed class GridMergeData
    {
        public int R { get; set; }
        public int C { get; set; }
        public int Rs { get; set; } = 1;
        public int Cs { get; set; } = 1;
    }

    private sealed class GridModel
    {
        public string Font { get; set; } = "Microsoft YaHei";
        public List<double> Cols { get; set; } = new();
        public List<double> Rows { get; set; } = new();
        public Dictionary<string, GridCellData> Cells { get; set; } = new();
        public List<GridMergeData> Merges { get; set; } = new();
        public List<string> CoveredKeys { get; set; } = new();

        public (string? Value, GridCellData? Style) GetCell(int r, int c)
        {
            if (Cells.TryGetValue(r + "_" + c, out var cell))
                return (cell.V, cell);
            return (null, null);
        }
    }

    /// <summary>解析网格 JSON（前端 LayoutJson）</summary>
    private static GridModel ParseGridJson(string? layoutJson)
    {
        var model = new GridModel();
        if (string.IsNullOrWhiteSpace(layoutJson)) return model;
        try
        {
            var root = JsonNode.Parse(layoutJson) as JsonObject;
            if (root == null) return model;

            if (root["font"] != null) model.Font = root["font"]!.GetValue<string>();
            if (root["cols"] is JsonArray cols)
                foreach (var node in cols) model.Cols.Add(node?.GetValue<double>() ?? 110);
            if (root["rows"] is JsonArray rows)
                foreach (var node in rows) model.Rows.Add(node?.GetValue<double>() ?? 34);
            if (model.Cols.Count == 0) model.Cols.AddRange(Enumerable.Repeat(110.0, 8));
            if (model.Rows.Count == 0) model.Rows.AddRange(Enumerable.Repeat(34.0, 12));

            if (root["cells"] is JsonObject cells)
            {
                foreach (var kv in cells)
                {
                    var obj = kv.Value as JsonObject;
                    if (obj == null) continue;
                    model.Cells[kv.Key] = new GridCellData
                    {
                        V = obj["v"]?.ToString(),
                        Bold = obj["bold"]?.GetValue<bool>() ?? false,
                        Italic = obj["italic"]?.GetValue<bool>() ?? false,
                        Align = obj["align"]?.ToString() ?? "left",
                        Fs = (int)Math.Round(obj["fs"]?.GetValue<double>() ?? 12),
                        Color = obj["color"]?.ToString(),
                        Bg = obj["bg"]?.ToString(),
                        Bd = (int)Math.Round(obj["bd"]?.GetValue<double>() ?? 1),
                        BorderColor = obj["borderColor"]?.ToString(),
                        Font = obj["font"]?.ToString(),
                    };
                }
            }

            if (root["merges"] is JsonArray merges)
            {
                foreach (var node in merges)
                {
                    var obj = node as JsonObject;
                    if (obj == null) continue;
                    var m = new GridMergeData
                    {
                        R = (int)Math.Round(obj["r"]?.GetValue<double>() ?? 0),
                        C = (int)Math.Round(obj["c"]?.GetValue<double>() ?? 0),
                        Rs = (int)Math.Round(obj["rs"]?.GetValue<double>() ?? 1),
                        Cs = (int)Math.Round(obj["cs"]?.GetValue<double>() ?? 1),
                    };
                    model.Merges.Add(m);
                    for (int r = m.R; r < m.R + m.Rs; r++)
                        for (int c = m.C; c < m.C + m.Cs; c++)
                            if (r != m.R || c != m.C) model.CoveredKeys.Add(r + "_" + c);
                }
            }
        }
        catch { /* 解析失败时按空网格处理 */ }
        return model;
    }

    /* ============ Excel → 网格 JSON（供前端导入） ============ */
    public static string Parse(Stream stream)
    {
        IWorkbook wb;
        try { wb = new XSSFWorkbook(stream); }
        catch { stream.Position = 0; wb = WorkbookFactory.Create(stream); }

        var sheet = wb.GetSheetAt(0);
        if (sheet == null) return "{}";

        int colCount = 0;
        for (int r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row != null) colCount = Math.Max(colCount, row.LastCellNum);
        }
        colCount = Math.Clamp(colCount, 1, 40);
        int rowCount = Math.Clamp(sheet.LastRowNum + 1, 1, 200);

        // 列宽 / 行高（Excel 单位 → px）
        var cols = new List<int>();
        for (int c = 0; c < colCount; c++)
            cols.Add(Math.Clamp((int)Math.Round(sheet.GetColumnWidth(c) * 7.0 / 256.0), 40, 600));
        var rows = new List<int>();
        for (int r = 0; r < rowCount; r++)
        {
            var row = sheet.GetRow(r);
            var pt = row?.HeightInPoints ?? 0;
            rows.Add(Math.Clamp(pt > 0 ? (int)Math.Round(pt * 4.0 / 3.0) : 34, 18, 240));
        }

        var covered = new HashSet<string>();
        var merges = new List<object>();
        for (int i = 0; i < sheet.NumMergedRegions; i++)
        {
            var region = sheet.GetMergedRegion(i);
            merges.Add(new
            {
                r = region.FirstRow,
                c = region.FirstColumn,
                rs = region.LastRow - region.FirstRow + 1,
                cs = region.LastColumn - region.FirstColumn + 1,
            });
            for (int r = region.FirstRow; r <= region.LastRow; r++)
                for (int c = region.FirstColumn; c <= region.LastColumn; c++)
                    if (r != region.FirstRow || c != region.FirstColumn) covered.Add(r + "_" + c);
        }

        var formatter = new DataFormatter();
        var cells = new Dictionary<string, object>();
        for (int r = 0; r < rowCount; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            for (int c = 0; c < colCount; c++)
            {
                if (covered.Contains(r + "_" + c)) continue;
                var cell = row.GetCell(c);
                if (cell == null) continue;

                var text = formatter.FormatCellValue(cell) ?? string.Empty;
                var style = cell.CellStyle;
                var font = style?.GetFont(wb);

                bool noFill = style == null || style.FillPattern == FillPattern.NoFill;
                bool noBorder = style == null ||
                    (style.BorderTop == BorderStyle.None && style.BorderBottom == BorderStyle.None
                     && style.BorderLeft == BorderStyle.None && style.BorderRight == BorderStyle.None);
                bool styled = style != null && ((font?.IsBold ?? false) || (font?.IsItalic ?? false)
                    || !noFill || !noBorder || style.Alignment != HorizontalAlignment.General);
                if (text.Length == 0 && !styled) continue;

                int bd = noBorder ? 0 : (style!.BorderTop is BorderStyle.Medium or BorderStyle.Thick ? 2 : 1);
                var fgHex = ColorToHex(SafeXssfColor(font as XSSFFont));
                var bgHex = noFill ? string.Empty : ColorToHex(style?.FillForegroundColorColor);

                cells[r + "_" + c] = new
                {
                    v = text,
                    bold = font?.IsBold ?? false,
                    italic = font?.IsItalic ?? false,
                    align = style?.Alignment switch
                    {
                        HorizontalAlignment.Center => "center",
                        HorizontalAlignment.Right => "right",
                        _ => "left",
                    },
                    fs = (int)Math.Round(font?.FontHeightInPoints ?? 12),
                    color = string.IsNullOrEmpty(fgHex) ? "#000000" : fgHex,
                    bg = bgHex,
                    bd = bd,
                    font = font?.FontName ?? "Microsoft YaHei",
                };
            }
        }

        var layout = new
        {
            font = DetectFont(sheet, wb),
            cols,
            rows,
            cells,
            merges,
            detail = new
            {
                enabled = false,
                fields = new[] { "ProductName", "Spec", "Quantity", "Unit", "UnitPrice", "Amount" },
                fs = 12,
                headBg = "#f2f2f2",
                bd = 1,
            },
            detailRow = new { enabled = false, row = Math.Min(5, rowCount - 1) },
            watermark = new
            {
                enabled = false,
                text = "{CompanyName}",
                color = "#93c5fd",
                opacity = 0.18,
                size = 20,
                rotate = -30,
                repeat = true,
            },
        };
        return JsonSerializer.Serialize(layout);
    }

    /// <summary>安全获取 XSSF 字体颜色（不同 NPOI 版本 API 名称不同）</summary>
    private static XSSFColor? SafeXssfColor(XSSFFont? font)
    {
        if (font == null) return null;
        try { return font.GetXSSFColor(); } catch { return null; }
    }

    /// <summary>推断整表字体（取首个有字体的单元格）</summary>
    private static string DetectFont(ISheet sheet, IWorkbook wb)
    {
        for (int r = 0; r <= Math.Min(sheet.LastRowNum, 3); r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            for (int c = 0; c < Math.Min((int)row.LastCellNum, 6); c++)
            {
                var name = row.GetCell(c)?.CellStyle?.GetFont(wb)?.FontName;
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
        }
        return "Microsoft YaHei";
    }
}