using ERP.Domain.Enums;
using ERP.Infrastructure.Export;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// Excel 导入值规范化测试（系统字段忽略、币种、日期、数值、是否）
/// </summary>
public class ValueNormalizerTests
{
    [Theory]
    [InlineData("BillNo")]
    [InlineData("Status")]
    [InlineData("Oid")]
    [InlineData("IsDeleted")]
    public void ToParameter_系统字段_返回空表示忽略(string key)
    {
        Assert.True(ValueNormalizer.IsSystemField(key));
        Assert.Null(ValueNormalizer.ToParameter(key, "任意值"));
    }

    [Theory]
    [InlineData("USD", (int)Currency.USD)]
    [InlineData("美元", (int)Currency.USD)]
    [InlineData("CNY", (int)Currency.CNY)]
    [InlineData("人民币", (int)Currency.CNY)]
    [InlineData("EUR", (int)Currency.EUR)]
    [InlineData("3", (int)Currency.EUR)]
    public void ToParameter_币种文本_转换为枚举值(string raw, int expected)
    {
        Assert.Equal(expected, ValueNormalizer.ToParameter("Currency", raw));
    }

    [Fact]
    public void ToParameter_日期文本_转换为DateTime()
    {
        var value = ValueNormalizer.ToParameter("OrderDate", "2026-09-12");
        Assert.IsType<DateTime>(value);
        Assert.Equal(new DateTime(2026, 9, 12), (DateTime)value!);
    }

    [Fact]
    public void ToParameter_含千分位金额_去除分隔符后转换()
    {
        Assert.Equal(12345.67m, ValueNormalizer.ToParameter("TotalAmount", "12,345.67"));
    }

    [Fact]
    public void ToParameter_数量字段_转换为数值()
    {
        Assert.Equal(100m, ValueNormalizer.ToParameter("Quantity", "100")!);
    }

    [Fact]
    public void ToParameter_是否字段_转换为1或0()
    {
        Assert.Equal(1, ValueNormalizer.ToParameter("IsSalesman", "是"));
        Assert.Equal(0, ValueNormalizer.ToParameter("IsDefault", "否"));
    }

    [Fact]
    public void ToParameter_普通文本_原样返回()
    {
        Assert.Equal("C001", ValueNormalizer.ToParameter("CustomerCode", "C001"));
    }

    [Fact]
    public void ToParameter_空值_返回空表示忽略()
    {
        Assert.Null(ValueNormalizer.ToParameter("CustomerName", "   "));
        Assert.Null(ValueNormalizer.ToParameter("CustomerName", null));
    }
}
