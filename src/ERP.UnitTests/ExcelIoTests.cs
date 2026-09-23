using ERP.Infrastructure.Export;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// Excel 导入导出（模板生成 / 解析 / 值规范化）测试
/// </summary>
public class ExcelIoTests
{
    private static readonly List<(string Key, string Title)> Columns = new()
    {
        ("BillNo", "单据号"),
        ("OrderDate", "订单日期"),
        ("CustId", "客户Id"),
        ("TotalAmount", "总金额"),
        ("Status", "状态"),
    };

    /// <summary>构造一个模拟用户填写后的工作簿（含文本、数字、日期单元格）</summary>
    private static byte[] BuildWorkbook()
    {
        var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("Sheet1");
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("订单日期");
        header.CreateCell(1).SetCellValue("客户Id");
        header.CreateCell(2).SetCellValue("总金额");
        header.CreateCell(3).SetCellValue("未知列");

        var row1 = sheet.CreateRow(1);
        row1.CreateCell(0).SetCellValue("2026-09-12");
        row1.CreateCell(1).SetCellValue(1001);
        row1.CreateCell(2).SetCellValue(1234.5);
        row1.CreateCell(3).SetCellValue("忽略");

        var row2 = sheet.CreateRow(2);
        row2.CreateCell(0).SetCellValue("2026/09/13");
        row2.CreateCell(1).SetCellValue(1002);
        row2.CreateCell(2).SetCellValue("2,000.00");

        // 空行应被跳过
        sheet.CreateRow(3);

        var ms = new MemoryStream();
        wb.Write(ms);
        // NPOI 在 Write 后会释放内部包（同时关闭流），因此以字节数组形式返回
        return ms.ToArray();
    }

    [Fact]
    public void ReadRows_按中文表头映射字段_忽略未配置列与空行()
    {
        var headerMap = Columns.ToDictionary(c => c.Title, c => c.Key);

        using var stream = new MemoryStream(BuildWorkbook());
        var rows = ExcelImporter.ReadRows(stream, headerMap);

        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-09-12", rows[0]["OrderDate"]);
        Assert.Equal("1001", rows[0]["CustId"]);
        Assert.Equal("1234.5", rows[0]["TotalAmount"]);
        Assert.False(rows[0].ContainsKey("未知列"));
        Assert.Equal("2,000.00", rows[1]["TotalAmount"]);
    }

    [Fact]
    public void BuildTemplate_生成首行中文表头文件()
    {
        var bytes = ExcelImporter.BuildTemplate("SalesOrder", Columns);

        Assert.NotEmpty(bytes);
        using var ms = new MemoryStream(bytes);
        var wb = new XSSFWorkbook(ms);
        var header = wb.GetSheetAt(0).GetRow(0);
        Assert.Equal("单据号", header.GetCell(0).StringCellValue);
        Assert.Equal("状态", header.GetCell(4).StringCellValue);
    }

    [Fact]
    public void BuildTemplate_生成的模板可被自身解析()
    {
        var bytes = ExcelImporter.BuildTemplate("SalesOrder", Columns);
        var headerMap = Columns.ToDictionary(c => c.Title, c => c.Key);

        using var ms = new MemoryStream(bytes);
        var rows = ExcelImporter.ReadRows(ms, headerMap);

        Assert.Empty(rows); // 模板只有表头，无数据行
    }
}
