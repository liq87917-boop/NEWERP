using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用 Excel 导入服务：将上传的 xlsx 文件解析为「列标题 -&gt; 值」行集合，
/// 并可按列定义生成导入模板（首行中文表头）。
/// </summary>
public static class ExcelImporter
{
    /// <summary>
    /// 读取 Excel 首个工作表（首行为表头）
    /// </summary>
    /// <param name="stream">xlsx 文件流</param>
    /// <param name="headerMap">表头映射（中文标题 -&gt; 字段键）；为空时直接使用原始表头作为键</param>
    /// <returns>行集合（键为表头或映射后的字段键，值为去除首尾空白的字符串）</returns>
    public static List<Dictionary<string, string>> ReadRows(Stream stream,
        IReadOnlyDictionary<string, string>? headerMap = null)
    {
        var result = new List<Dictionary<string, string>>();
        IWorkbook workbook = new XSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);
        if (sheet is null || sheet.LastRowNum < 1) return result;

        var headerRow = sheet.GetRow(sheet.GetRow(0)?.FirstCellNum ?? 0) ?? sheet.GetRow(0);
        if (headerRow is null) return result;

        // 建立「列索引 -> 字段键」映射
        var columns = new Dictionary<int, string>();
        for (var c = headerRow.FirstCellNum; c < headerRow.LastCellNum; c++)
        {
            var title = ReadCellText(headerRow.GetCell(c));
            if (string.IsNullOrWhiteSpace(title)) continue;

            var key = title.Trim();
            if (headerMap is not null)
            {
                if (!headerMap.TryGetValue(key, out var mapped)) continue; // 未配置的列直接忽略
                key = mapped;
            }
            columns[c] = key;
        }

        for (var r = 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row is null) continue;

            var item = new Dictionary<string, string>();
            foreach (var kv in columns)
            {
                var text = ReadCellText(row.GetCell(kv.Key));
                if (!string.IsNullOrWhiteSpace(text))
                    item[kv.Value] = text.Trim();
            }
            // 整行为空时跳过（Excel 末尾常见空行）
            if (item.Count > 0) result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// 生成导入模板（首行中文表头，可选一行示例数据）
    /// </summary>
    /// <param name="sheetName">工作表名称</param>
    /// <param name="columns">列定义（Key: 字段键，Title: 中文标题）</param>
    /// <param name="sampleRow">示例行（键为字段键）</param>
    public static byte[] BuildTemplate(string sheetName, IReadOnlyList<(string Key, string Title)> columns,
        IReadOnlyDictionary<string, string>? sampleRow = null)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet(SafeSheetName(sheetName));

        var headerStyle = wb.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;
        var headerFont = wb.CreateFont();
        headerFont.IsBold = true;
        headerStyle.SetFont(headerFont);

        var headerRow = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = headerRow.CreateCell(c);
            cell.SetCellValue(columns[c].Title);
            cell.CellStyle = headerStyle;
            sheet.SetColumnWidth(c, Math.Min(columns[c].Title.Length * 2 + 8, 40) * 256);
        }

        if (sampleRow is not null)
        {
            var row = sheet.CreateRow(1);
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = row.CreateCell(c);
                cell.SetCellValue(sampleRow.TryGetValue(columns[c].Key, out var v) ? v ?? string.Empty : string.Empty);
            }
        }

        using var ms = new MemoryStream();
        wb.Write(ms);
        return ms.ToArray();
    }

    /// <summary>读取单元格文本（兼容字符串、数字、日期、布尔、公式）</summary>
    private static string ReadCellText(ICell? cell)
    {
        if (cell is null) return string.Empty;
        switch (cell.CellType)
        {
            case CellType.String:
                return cell.StringCellValue ?? string.Empty;
            case CellType.Numeric:
                if (DateUtil.IsCellDateFormatted(cell))
                    return cell.DateCellValue?.ToString("yyyy-MM-dd") ?? string.Empty;
                // 避免 1.0 之类的整数显示为 1
                var number = cell.NumericCellValue;
                return number == Math.Floor(number) ? ((long)number).ToString() : number.ToString("0.####");
            case CellType.Boolean:
                return cell.BooleanCellValue ? "是" : "否";
            case CellType.Formula:
                try { return cell.StringCellValue ?? string.Empty; }
                catch (Exception) { return cell.NumericCellValue.ToString("0.####"); }
            default:
                return string.Empty;
        }
    }

    /// <summary>处理非法工作表名称（去除 / \ : * ? 等字符，截断至 31 字符）</summary>
    private static string SafeSheetName(string name)
    {
        var sanitized = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Sheet1" : sanitized[..Math.Min(sanitized.Length, 31)];
    }
}
