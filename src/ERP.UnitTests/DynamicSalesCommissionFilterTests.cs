using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态业务员提成证据报表（ERP-244）纯规则筛选校验单元测试：
/// 客户 Id / 业务员 Id / 原币币种可选筛选规范化（blank = 缺席）、非法取值先于任何源读取 fail closed，
/// 以及筛选上下文文案渲染。无数据库依赖。
/// </summary>
public class DynamicSalesCommissionFilterTests
{
    [Fact]
    public void NormalizeFilter_null返回null()
    {
        Assert.Null(DynamicSalesCommissionReportRules.NormalizeFilter(null));
    }

    [Fact]
    public void NormalizeFilter_三项全空返回null()
    {
        var result = DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto());
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeFilter_客户Id非正整数拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto { CustomerId = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_业务员Id非正整数拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto { SalesmanId = -1 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_币种纯数字拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto { Currency = "999" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_币种未知码拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto { Currency = "XYZ" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_币种小写归一化为大写()
    {
        var result = DynamicSalesCommissionReportRules.NormalizeFilter(
            new SalesCommissionFilterDto { Currency = "usd" });
        Assert.NotNull(result);
        Assert.Equal("USD", result!.Currency);
    }

    [Fact]
    public void NormalizeFilter_组合筛选规范化()
    {
        var result = DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto
        {
            CustomerId = 11,
            SalesmanId = 22,
            Currency = "cny",
        });

        Assert.NotNull(result);
        Assert.Equal(11, result!.CustomerId);
        Assert.Equal(22, result.SalesmanId);
        Assert.Equal("CNY", result.Currency);
    }

    [Fact]
    public void BuildFilterContext_空返回空串()
    {
        Assert.Equal(string.Empty, DynamicSalesCommissionReportRules.BuildFilterContext(null));
        Assert.Equal(string.Empty, DynamicSalesCommissionReportRules.BuildFilterContext(new SalesCommissionFilterDto()));
    }

    [Fact]
    public void BuildFilterContext_组合渲染()
    {
        var text = DynamicSalesCommissionReportRules.BuildFilterContext(new SalesCommissionFilterDto
        {
            CustomerId = 11,
            SalesmanId = 22,
            Currency = "USD",
        });

        Assert.Equal("客户 Id 11；业务员 Id 22；原币币种 USD", text);
    }

    [Fact]
    public void NormalizeSalesmanNameKeyword_留空去空白超长控制字符()
    {
        Assert.Null(DynamicSalesCommissionReportRules.NormalizeSalesmanNameKeyword(null));
        Assert.Null(DynamicSalesCommissionReportRules.NormalizeSalesmanNameKeyword(""));
        Assert.Null(DynamicSalesCommissionReportRules.NormalizeSalesmanNameKeyword("   "));
        Assert.Equal("张三", DynamicSalesCommissionReportRules.NormalizeSalesmanNameKeyword("  张三  "));

        var over = new string('张', DynamicSalesCommissionReportRules.MaxFilterKeywordLength + 1);
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeSalesmanNameKeyword(over));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        Assert.Throws<BusinessException>(() =>
            DynamicSalesCommissionReportRules.NormalizeSalesmanNameKeyword("张\u0001三"));
    }

    [Fact]
    public void NormalizeFilter_姓名关键字_去空白并保留字面百分号下划线()
    {
        var result = DynamicSalesCommissionReportRules.NormalizeFilter(new SalesCommissionFilterDto
        {
            SalesmanName = "  100%_  ",
        });
        Assert.NotNull(result);
        Assert.Equal("100%_", result!.SalesmanName);
    }

    [Fact]
    public void BuildFilterContext_含姓名关键字()
    {
        var text = DynamicSalesCommissionReportRules.BuildFilterContext(new SalesCommissionFilterDto
        {
            CustomerId = 11,
            SalesmanId = 22,
            SalesmanName = "张三",
            Currency = "USD",
        });
        Assert.Equal("客户 Id 11；业务员 Id 22；业务员姓名关键字 张三；原币币种 USD", text);
    }
}
