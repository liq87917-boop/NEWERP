using ERP.Application.DTOs;
using ERP.Infrastructure.Export;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-311 Stage 2 通用报表配置 PDF 共享列带布局单元测试（纯离线，不连接 SQL Server / 不启动 API / 不依赖字体）。
/// 覆盖：宽列集（27+ 列）拆分为多个逻辑列带且恒重复允许身份列、无身份列回退稳定行序号、窄页宽下每个列带
/// 宽度有界且总和不超过可用宽、字段顺序与列覆盖无重复无遗漏、长中文文本折行绝不丢字、单列单带、以及
/// null / 零值 / 数值 / 布尔 / 日期类型化语义。
/// </summary>
public class ReportConfigurationPdfColumnLayoutTests
{
    private static ReportConfigurationColumnDto Col(string key, string label, string type = ReportConfigurationConstants.TypeText)
        => new(key, label, type, null);

    private static double Mm(double mm) => mm * 72.0 / 25.4;

    [Fact]
    public void Layout_宽列集_拆成多个列带并重复身份列_字段无重复无遗漏()
    {
        var columns = new List<ReportConfigurationColumnDto> { Col("orderNo", "订单号") };
        for (var i = 1; i <= 27; i++)
            columns.Add(Col($"f{i}", $"字段{i}较长中文标签用于触发列带拆分"));

        var bands = ReportConfigurationPdfColumnLayout.Layout(
            columns, Array.Empty<Dictionary<string, object?>>(), Mm(210 - 10 - 10));

        Assert.True(bands.Count >= 2);

        var dataKeys = new List<string>();
        foreach (var band in bands)
        {
            Assert.Equal("orderNo", band.Columns[0].Key);
            Assert.Equal(1, band.IdentityColumnCount);
            for (var c = 1; c < band.Columns.Count; c++)
                dataKeys.Add(band.Columns[c].Key);
        }

        Assert.Equal(Enumerable.Range(1, 27).Select(i => $"f{i}"), dataKeys);
    }

    [Fact]
    public void Layout_无允许身份列_回退本页序号()
    {
        var columns = new List<ReportConfigurationColumnDto>
        {
            Col("customerName", "客户名称"),
            Col("totalAmount", "金额", ReportConfigurationConstants.TypeNumber),
        };

        var bands = ReportConfigurationPdfColumnLayout.Layout(
            columns, Array.Empty<Dictionary<string, object?>>(), Mm(190));

        var band = Assert.Single(bands);
        Assert.Equal(ReportConfigurationPdfColumnLayout.RowOrdinalColumnKey, band.Columns[0].Key);
        Assert.Equal(ReportConfigurationPdfColumnLayout.RowOrdinalLabel, band.Columns[0].Label);
        Assert.Equal(1, band.IdentityColumnCount);
    }

    [Fact]
    public void Layout_窄页宽_列带宽度有界且总和不超过可用宽()
    {
        var columns = new List<ReportConfigurationColumnDto> { Col("orderNo", "订单号") };
        for (var i = 1; i <= 27; i++)
            columns.Add(Col($"f{i}", $"字段{i}较长中文标签"));

        var usableWidth = Mm(120);
        var bands = ReportConfigurationPdfColumnLayout.Layout(columns, Array.Empty<Dictionary<string, object?>>(), usableWidth);

        Assert.True(bands.Count >= 2);

        var min = ReportConfigurationPdfColumnLayout.MinColumnWidthMm * ReportConfigurationPdfColumnLayout.PointsPerMillimeter;
        var max = ReportConfigurationPdfColumnLayout.MaxNumberColumnWidthMm * ReportConfigurationPdfColumnLayout.PointsPerMillimeter;

        foreach (var band in bands)
        {
            var sum = band.Widths.Sum();
            Assert.True(sum <= usableWidth + 0.5, $"列带总宽 {sum} 超过可用宽 {usableWidth}");
            foreach (var width in band.Widths)
            {
                Assert.True(width >= min - 0.001, $"列宽 {width} 小于下限 {min}");
                Assert.True(width <= max + 0.001, $"列宽 {width} 大于上限 {max}");
            }
        }
    }

    [Fact]
    public void Layout_单列_单带()
    {
        var columns = new List<ReportConfigurationColumnDto>
        {
            Col("orderNo", "订单号"),
            Col("amount", "金额", ReportConfigurationConstants.TypeNumber),
        };

        var bands = ReportConfigurationPdfColumnLayout.Layout(columns, Array.Empty<Dictionary<string, object?>>(), Mm(190));

        var band = Assert.Single(bands);
        Assert.Equal(2, band.Columns.Count);
        Assert.Equal("orderNo", band.Columns[0].Key);
        Assert.Equal("amount", band.Columns[1].Key);
    }

    [Fact]
    public void WrapText_长中文文本_绝不丢字()
    {
        const string text = "这是一个非常长的中文单元格文本用于验证折行不丢失任何字符同时保持行连续完整可读";

        var lines = ReportConfigurationPdfExporter.WrapText(text, 8, 48);

        Assert.True(lines.Count > 1);
        Assert.Equal(text, string.Concat(lines));
    }

    [Fact]
    public void FormatCellValue_数值与null语义()
    {
        Assert.Equal(string.Empty, ReportConfigurationPdfExporter.FormatCellValue(null));
        Assert.Equal("0", ReportConfigurationPdfExporter.FormatCellValue(0m));
        Assert.Equal("-5.5", ReportConfigurationPdfExporter.FormatCellValue(-5.5m));
        Assert.Equal("是", ReportConfigurationPdfExporter.FormatCellValue(true));
        Assert.Equal("2026-09-01", ReportConfigurationPdfExporter.FormatCellValue(new DateTime(2026, 9, 1)));
    }
}
