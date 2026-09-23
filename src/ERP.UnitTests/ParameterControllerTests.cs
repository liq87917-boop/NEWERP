using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ParameterController 单元测试：参数键唯一性、GetByKey、更新。
/// </summary>
public class ParameterControllerTests
{
    [Fact]
    public async Task GetAll_关键字匹配ParamKey_ParamValue_分页正确()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "MaxOrderAmount", "最大订单金额", "100000");
        SeedParam(db, "MinOrderAmount", "最小订单金额", "100");
        SeedParam(db, "DefaultCurrency", "默认币种", "CNY");
        var ctl = new ParameterController(db);

        var result = await ctl.GetAll(new PageQuery { Page = 1, PageSize = 10, Keyword = "Amount" });

        var resp = Assert.IsType<ApiResponse<PagedResult<SysParameter>>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, resp.Data!.Total);
    }

    [Fact]
    public async Task GetByKey_存在_返回参数_()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "DefaultCurrency", "默认币种", "CNY");
        var ctl = new ParameterController(db);

        var result = await ctl.GetByKey("DefaultCurrency");

        var resp = Assert.IsType<ApiResponse<SysParameter>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("CNY", resp.Data!.ParamValue);
    }

    [Fact]
    public async Task GetByKey_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new ParameterController(db);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetByKey("NotExistKey"));
    }

    [Fact]
    public async Task Create_参数键重复_抛Duplicate_且数据库未变()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "Key1", "n1", "v1");
        var ctl = new ParameterController(db);

        var newParam = new SysParameter { ParamKey = "Key1", ParamName = "n2", ParamValue = "v2", Description = "" };
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(newParam));
        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.SysParameters);
    }

    [Fact]
    public async Task Create_正常_数据库可查到_Id自增_ParamKeyParamValue正确()
    {
        using var db = TestDbFactory.Create();
        var ctl = new ParameterController(db);

        var result = await ctl.Create(new SysParameter { ParamKey = "NewKey", ParamName = "新参数", ParamValue = "1", Description = "desc" });
        Assert.IsType<OkObjectResult>(result);

        var p = db.SysParameters.Single();
        Assert.True(p.Id > 0);
        Assert.Equal("NewKey", p.ParamKey);
        Assert.Equal("1", p.ParamValue);
        Assert.Equal("新参数", p.ParamName);
    }

    [Fact]
    public async Task Update_正常_ParamValue刷新_UpdatedAt被设置()
    {
        using var db = TestDbFactory.Create();
        var p = SeedParam(db, "Key1", "n1", "old");
        var ctl = new ParameterController(db);

        var update = new SysParameter { ParamKey = "Key1", ParamName = "n1-new", ParamValue = "new", Description = "d-new" };
        await ctl.Update(p.Id, update);

        var dbP = db.SysParameters.Single();
        Assert.Equal("new", dbP.ParamValue);
        Assert.Equal("n1-new", dbP.ParamName);
        Assert.NotNull(dbP.UpdatedAt);
    }

    [Fact]
    public async Task Update_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new ParameterController(db);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(999, new SysParameter { ParamKey = "k", ParamName = "n", ParamValue = "v" }));
    }

    private static SysParameter SeedParam(ErpDbContext db, string key, string name, string value)
    {
        var p = new SysParameter { ParamKey = key, ParamName = name, ParamValue = value, Description = "" };
        db.SysParameters.Add(p);
        db.SaveChanges();
        return p;
    }
}