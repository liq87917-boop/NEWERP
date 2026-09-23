using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用 Excel 导出服务：将数据行集合（Dictionary 列名-&gt;值）导出为 xlsx 字节流
/// 支持指定导出列（key:中文标题），未指定时导出全部字段（字段名作列头）
/// </summary>
public static class ExcelExporter
{
    /// <summary>
    /// 将数据行导出为 xlsx 字节数组
    /// </summary>
    /// <param name="sheetName">工作表名称</param>
    /// <param name="rows">数据行集合</param>
    /// <param name="columns">导出列定义（Key:列名, Title:中文标题），为空时取第一行全部键</param>
    public static byte[] ExportRows(string sheetName, List<Dictionary<string, object?>> rows,
        List<(string Key, string Title)>? columns = null)
    {
        columns ??= rows.FirstOrDefault()?.Keys.Select(k => (k, k)).ToList() ?? new List<(string, string)>();

        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet(SafeSheetName(sheetName));

        // ============ 表头样式 ============
        var headerStyle = wb.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;
        headerStyle.BorderBottom = BorderStyle.Thin;
        headerStyle.BorderTop = BorderStyle.Thin;
        headerStyle.BorderLeft = BorderStyle.Thin;
        headerStyle.BorderRight = BorderStyle.Thin;
        var headerFont = wb.CreateFont();
        headerFont.IsBold = true;
        headerStyle.SetFont(headerFont);

        // ============ 内容样式 ============
        var textStyle = wb.CreateCellStyle();
        textStyle.VerticalAlignment = VerticalAlignment.Center;
        textStyle.BorderBottom = BorderStyle.Thin;
        textStyle.BorderLeft = BorderStyle.Thin;
        textStyle.BorderRight = BorderStyle.Thin;

        var numberStyle = wb.CreateCellStyle();
        numberStyle.DataFormat = wb.CreateDataFormat().GetFormat("0.00");
        numberStyle.Alignment = HorizontalAlignment.Right;
        numberStyle.VerticalAlignment = VerticalAlignment.Center;
        numberStyle.BorderBottom = BorderStyle.Thin;
        numberStyle.BorderLeft = BorderStyle.Thin;
        numberStyle.BorderRight = BorderStyle.Thin;

        // ============ 表头行 ============
        var headerRow = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = headerRow.CreateCell(c);
            cell.SetCellValue(columns[c].Title);
            cell.CellStyle = headerStyle;
        }

        // ============ 数据行 ============
        for (var r = 0; r < rows.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            for (var c = 0; c < columns.Count; c++)
            {
                var value = rows[r].TryGetValue(columns[c].Key, out var v) ? v : null;
                var cell = row.CreateCell(c);
                if (value is null || value is DBNull)
                {
                    cell.SetCellValue(string.Empty);
                    cell.CellStyle = textStyle;
                }
                else if (value is int i)
                {
                    cell.SetCellValue(i);
                    cell.CellStyle = numberStyle;
                }
                else if (value is long l)
                {
                    cell.SetCellValue(l);
                    cell.CellStyle = numberStyle;
                }
                else if (value is decimal m)
                {
                    cell.SetCellValue((double)m);
                    cell.CellStyle = numberStyle;
                }
                else if (value is double d)
                {
                    cell.SetCellValue(d);
                    cell.CellStyle = numberStyle;
                }
                else if (value is float f)
                {
                    cell.SetCellValue(f);
                    cell.CellStyle = numberStyle;
                }
                else if (value is bool b)
                {
                    cell.SetCellValue(b ? "是" : "否");
                    cell.CellStyle = textStyle;
                }
                else if (value is DateTime dt)
                {
                    cell.SetCellValue(dt.ToString("yyyy-MM-dd HH:mm"));
                    cell.CellStyle = textStyle;
                }
                else
                {
                    cell.SetCellValue(value.ToString() ?? string.Empty);
                    cell.CellStyle = textStyle;
                }
            }
        }

        // ============ 自动列宽（中文表头放宽） ============
        for (var c = 0; c < columns.Count; c++)
        {
            var width = columns[c].Title.Length * 2 + 4;
            var sample = sheet.GetRow(1)?.GetCell(c);
            if (sample is not null && sample.CellType == CellType.String && sample.StringCellValue.Length > 0)
                width = Math.Max(width, sample.StringCellValue.Length * 2 + 4);
            sheet.SetColumnWidth(c, Math.Min(width, 60) * 256);
        }

        using var ms = new MemoryStream();
        wb.Write(ms);
        return ms.ToArray();
    }

    /// <summary>处理非法工作表名称（去除 / \ : * ? 等字符，截断至 31 字符）</summary>
    private static string SafeSheetName(string name)
    {
        var sanitized = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Sheet1" : sanitized[..Math.Min(sanitized.Length, 31)];
    }
}
