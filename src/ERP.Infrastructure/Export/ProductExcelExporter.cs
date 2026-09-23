using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using QRCoder;
using System.Drawing;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 商品资料 Excel 导出服务：支持文本/数值/货币、图片按单元格缩放居中、二维码、行下求和。
/// 后续可改为读取客户提供的 Excel 模板在指定单元格填充。
/// </summary>
public class ProductExcelExporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const double EMU_PER_PX = 9525;
    private readonly IErpDbContext _db;

    public ProductExcelExporter(IErpDbContext db) => _db = db;

    public async Task<byte[]> ExportAsync(string templatePath)
    {
        var products = await _db.BaseProducts.AsNoTracking()
            .Where(p => !p.IsDeleted).OrderBy(p => p.Id).ToListAsync();

        using var fs = File.OpenRead(templatePath);
        var workbook = new XSSFWorkbook(fs);
        var sheet = (XSSFSheet)workbook.GetSheetAt(0);

        const int startRow = 7; // 数据模板行（第 8 行，0-based）
        var templateRow = sheet.GetRow(startRow);

        for (var i = 0; i < products.Count; i++)
        {
            var p = products[i];
            var rowIndex = startRow + i;
            var row = sheet.GetRow(rowIndex) ?? sheet.CreateRow(rowIndex);
            if (row != templateRow)
                row.HeightInPoints = templateRow?.HeightInPoints ?? 200;

            // 图片列 A/B/C（FOTO 1/2/3）：先清空占位符文本，再插入图片
            CopyAndFill(row, templateRow, 0, string.Empty);
            CopyAndFill(row, templateRow, 1, string.Empty);
            CopyAndFill(row, templateRow, 2, string.Empty);
            await FillImageAsync(workbook, sheet, p.Image1, 0, rowIndex);
            await FillImageAsync(workbook, sheet, p.Image2, 1, rowIndex);
            await FillImageAsync(workbook, sheet, p.Image3, 2, rowIndex);

            // 文本/数值（沿用模板行样式）
            CopyAndFill(row, templateRow, 3, p.ProductCode);       // D 货号
            CopyAndFill(row, templateRow, 4, p.ProductName);       // E 商品名称/型号
            CopyAndFill(row, templateRow, 5, p.EnglishName);       // F 西语品名（英文名替代）
            CopyAndFill(row, templateRow, 6, p.Spec);              // G 中文细节
            CopyAndFill(row, templateRow, 8, (double)p.SalePrice); // I 单价
            CopyAndFill(row, templateRow, 10, p.Unit);             // K 单位
            CopyAndFill(row, templateRow, 12, 1.0);                // M 件数（默认 1）
            CopyAndFill(row, templateRow, 18, (double)p.Weight);   // S 毛重

            // 行内公式列（行号 1-based）
            var r = rowIndex + 1;
            CopyFormula(row, templateRow, 13, $"M{r}*L{r}"); // N 总数量
            CopyFormula(row, templateRow, 14, $"N{r}*I{r}"); // O 总金额
            CopyFormula(row, templateRow, 15, $"N{r}*J{r}"); // P 总美金
            CopyFormula(row, templateRow, 17, $"Q{r}*M{r}"); // R 总体积
            CopyFormula(row, templateRow, 19, $"M{r}*S{r}"); // T 总重量
        }

        // 行下求和
        AddTotalRow(sheet, templateRow, startRow + products.Count, products.Count);

        using var ms = new MemoryStream();
        workbook.Write(ms);
        return ms.ToArray();
    }

    private async Task FillImageAsync(XSSFWorkbook workbook, XSSFSheet sheet, string? url, int col, int rowIndex)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var imgBytes = await DownloadImageAsync(url);
        if (imgBytes is { Length: > 0 })
            AddImageScaled(workbook, sheet, imgBytes, col, rowIndex);
    }

    private static void CopyAndFill(IRow row, IRow? templateRow, int col, object value)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        var templateCell = templateRow?.GetCell(col);
        if (templateCell != null) cell.CellStyle = templateCell.CellStyle;
        switch (value)
        {
            case string s: cell.SetCellValue(s); break;
            case double d: cell.SetCellValue(d); break;
            case int n: cell.SetCellValue(n); break;
            default: cell.SetCellValue(value?.ToString() ?? string.Empty); break;
        }
    }

    private static void CopyFormula(IRow row, IRow? templateRow, int col, string formula)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        var templateCell = templateRow?.GetCell(col);
        if (templateCell != null) cell.CellStyle = templateCell.CellStyle;
        cell.SetCellFormula(formula);
    }

    private static void AddTotalRow(XSSFSheet sheet, IRow? templateRow, int rowIndex, int dataCount)
    {
        var row = sheet.CreateRow(rowIndex);
        var labelCell = row.CreateCell(4);
        labelCell.SetCellValue("TOTAL");
        var templateLabelCell = templateRow?.GetCell(4);
        if (templateLabelCell != null) labelCell.CellStyle = templateLabelCell.CellStyle;

        var first = 8; // 第一条数据在第 8 行
        var last = 8 + dataCount - 1;
        SetSumFormula(row, 14, $"SUM(O{first}:O{last})"); // 总金额
        SetSumFormula(row, 15, $"SUM(P{first}:P{last})"); // 总美金
        SetSumFormula(row, 19, $"SUM(T{first}:T{last})"); // 总重量
    }

    private static void SetSumFormula(IRow row, int col, string formula)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        cell.SetCellFormula(formula);
    }

    private static void AddImageScaled(XSSFWorkbook workbook, XSSFSheet sheet, byte[] imageBytes, int col, int row)
    {
        try
        {
            var pictureType = GetPictureType(imageBytes);
            if (pictureType == PictureType.None) return;

            var pictureIdx = workbook.AddPicture(imageBytes, pictureType);
            var drawing = sheet.CreateDrawingPatriarch();

            using var imgMs = new MemoryStream(imageBytes);
            using var img = Image.FromStream(imgMs);
            var imgW = (double)img.Width;
            var imgH = (double)img.Height;

            var cellW = sheet.GetColumnWidth(col) / 256.0 * 7.0;
            var cellH = (sheet.GetRow(row)?.HeightInPoints ?? 60) * 96.0 / 72.0;

            const double margin = 6;
            var availW = cellW - margin * 2;
            var availH = cellH - margin * 2;
            var scale = Math.Min(availW / imgW, availH / imgH);
            var w = imgW * scale;
            var h = imgH * scale;

            var offX = (cellW - w) / 2;
            var offY = (cellH - h) / 2;

            var anchor = new XSSFClientAnchor(
                (int)(offX * EMU_PER_PX), (int)(offY * EMU_PER_PX),
                (int)((offX + w) * EMU_PER_PX), (int)((offY + h) * EMU_PER_PX),
                col, row, col, row);

            drawing.CreatePicture(anchor, pictureIdx);
        }
        catch { /* 图片插入失败时忽略 */ }
    }

    private static async Task<byte[]?> DownloadImageAsync(string url)
    {
        try
        {
            var resp = await Http.GetAsync(url);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync() : null;
        }
        catch { return null; }
    }

    private static byte[]? GenerateQrCode(string content)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            return qr.GetGraphic(8);
        }
        catch { return null; }
    }

    private static PictureType GetPictureType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return PictureType.JPEG;
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return PictureType.PNG;
        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return PictureType.GIF;
        return PictureType.None;
    }

    private static void SetText(ICell cell, string value, ICellStyle style)
    {
        cell.SetCellValue(value ?? string.Empty);
        cell.CellStyle = style;
    }

    private static void SetNumeric(ICell cell, decimal value, ICellStyle style)
    {
        cell.SetCellValue((double)value);
        cell.CellStyle = style;
    }

    private static string GetColLetter(int col)
    {
        var n = col + 1;
        var s = string.Empty;
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            s = (char)('A' + rem) + s;
            n = (n - 1) / 26;
        }
        return s;
    }

    private static ICellStyle CreateHeaderStyle(XSSFWorkbook wb)
    {
        var style = wb.CreateCellStyle();
        style.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        style.FillPattern = FillPattern.SolidForeground;
        style.Alignment = HorizontalAlignment.Center;
        style.VerticalAlignment = VerticalAlignment.Center;
        var font = wb.CreateFont();
        font.IsBold = true;
        style.SetFont(font);
        style.BorderBottom = BorderStyle.Thin;
        style.BorderTop = BorderStyle.Thin;
        style.BorderLeft = BorderStyle.Thin;
        style.BorderRight = BorderStyle.Thin;
        return style;
    }

    private static ICellStyle CreateTextStyle(XSSFWorkbook wb)
    {
        var style = wb.CreateCellStyle();
        style.VerticalAlignment = VerticalAlignment.Center;
        return style;
    }

    private static ICellStyle CreateMoneyStyle(XSSFWorkbook wb)
    {
        var style = wb.CreateCellStyle();
        style.DataFormat = wb.CreateDataFormat().GetFormat("¥#,##0.00");
        style.Alignment = HorizontalAlignment.Right;
        style.VerticalAlignment = VerticalAlignment.Center;
        return style;
    }

    private static ICellStyle CreateNumberStyle(XSSFWorkbook wb)
    {
        var style = wb.CreateCellStyle();
        style.DataFormat = wb.CreateDataFormat().GetFormat("0.00");
        style.Alignment = HorizontalAlignment.Right;
        style.VerticalAlignment = VerticalAlignment.Center;
        return style;
    }
}
