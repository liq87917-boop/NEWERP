using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 打印模板（打印设计）测试：默认模板、保存、设为默认、软删除
/// </summary>
public class PrintTemplateTests
{
    /// <summary>读取控制器返回的统一响应数据</summary>
    private static T GetData<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    [Fact]
    public async Task GetDefault_无模板时_返回内置默认配置()
    {
        using var db = TestDbFactory.Create();
        db.SysParameters.Add(new SysParameter { ParamKey = "CompanyName", ParamValue = "某某外贸有限公司", ParamName = "公司名称" });
        await db.SaveChangesAsync();
        var controller = new PrintTemplateController(db);

        var template = GetData<SysPrintTemplate>(await controller.GetDefault("sales-order"));

        Assert.Equal("销售订单", template.Title);
        Assert.Equal("某某外贸有限公司", template.CompanyName);
        Assert.Equal("A4", template.PaperSize);
        Assert.True(template.ShowDetailTable);
        Assert.True(template.IsDefault);
    }

    [Fact]
    public async Task Save_新增模板_可查询并作为默认模板()
    {
        using var db = TestDbFactory.Create();
        var controller = new PrintTemplateController(db);

        var saved = GetData<SysPrintTemplate>(await controller.Save(new SysPrintTemplate
        {
            BillType = "sales-order",
            TemplateName = "外贸标准模板",
            Title = "销售订单（出口）",
            CompanyName = "测试公司",
            PaperSize = "A5",
            FontSize = 10,
            FieldKeys = "[\"BillNo\",\"OrderDate\"]",
            IsDefault = true,
        }));

        Assert.True(saved.Id > 0);
        var template = GetData<SysPrintTemplate>(await controller.GetDefault("sales-order"));
        Assert.Equal("销售订单（出口）", template.Title);
        Assert.Equal("A5", template.PaperSize);
        Assert.Equal("[\"BillNo\",\"OrderDate\"]", template.FieldKeys);
    }

    [Fact]
    public async Task Save_同名模板重复新增_返回重复错误码()
    {
        using var db = TestDbFactory.Create();
        var controller = new PrintTemplateController(db);
        await controller.Save(new SysPrintTemplate { BillType = "receipt", TemplateName = "模板A" });

        var result = await controller.Save(new SysPrintTemplate { BillType = "receipt", TemplateName = "模板A" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Duplicate, response.Code);
    }

    [Fact]
    public async Task Save_设置新默认模板_原默认模板被取消()
    {
        using var db = TestDbFactory.Create();
        var controller = new PrintTemplateController(db);
        var first = GetData<SysPrintTemplate>(await controller.Save(new SysPrintTemplate
        {
            BillType = "payment", TemplateName = "模板1", IsDefault = true
        }));
        var second = GetData<SysPrintTemplate>(await controller.Save(new SysPrintTemplate
        {
            BillType = "payment", TemplateName = "模板2", IsDefault = true
        }));

        var list = await db.SysPrintTemplates.AsNoTracking().ToListAsync();
        Assert.False(list.First(t => t.Id == first.Id).IsDefault);
        Assert.True(list.First(t => t.Id == second.Id).IsDefault);
    }

    [Fact]
    public async Task Delete_软删除后_不再返回该模板()
    {
        using var db = TestDbFactory.Create();
        var controller = new PrintTemplateController(db);
        var saved = GetData<SysPrintTemplate>(await controller.Save(new SysPrintTemplate
        {
            BillType = "complaint", TemplateName = "客诉模板"
        }));

        await controller.Delete(saved.Id);

        var template = GetData<SysPrintTemplate>(await controller.GetDefault("complaint"));
        Assert.Equal("默认模板", template.TemplateName); // 回退到内置默认模板
    }
}
