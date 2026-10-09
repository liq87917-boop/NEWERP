using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ParameterController 单元测试：参数键唯一性、GetByKey、更新。
/// <para>ERP-446 起每条路由在读取 / 写入之前都要求实时启用身份与既有「系统参数」菜单，测试统一通过
/// <see cref="NewController"/> 注入一个已授予既有菜单的启用账号；既有响应 / 分页 / 错误契约保持不变。</para>
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
        var ctl = NewController(db);

        var result = await ctl.GetAll(new PageQuery { Page = 1, PageSize = 10, Keyword = "Amount" });

        var resp = Assert.IsType<ApiResponse<PagedResult<SysParameter>>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, resp.Data!.Total);
    }

    [Fact]
    public async Task GetByKey_存在_返回参数_()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "DefaultCurrency", "默认币种", "CNY");
        var ctl = NewController(db);

        var result = await ctl.GetByKey("DefaultCurrency");

        var resp = Assert.IsType<ApiResponse<SysParameter>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("CNY", resp.Data!.ParamValue);
    }

    [Fact]
    public async Task GetByKey_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetByKey("NotExistKey"));
    }

    [Fact]
    public async Task Create_参数键重复_抛Duplicate_且数据库未变()
    {
        using var db = TestDbFactory.Create();
        SeedParam(db, "Key1", "n1", "v1");
        var ctl = NewController(db);

        var newParam = new SysParameter { ParamKey = "Key1", ParamName = "n2", ParamValue = "v2", Description = "" };
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(newParam));
        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.SysParameters);
    }

    [Fact]
    public async Task Create_正常_数据库可查到_Id自增_ParamKeyParamValue正确()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

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
        var ctl = NewController(db);

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
        var ctl = NewController(db);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(999, new SysParameter { ParamKey = "k", ParamName = "n", ParamValue = "v" }));
    }

    /// <summary>构造已授权控制器：播种启用账号 + 既有「系统参数」菜单授权并注入登录身份。</summary>
    private static ParameterController NewController(ErpDbContext db)
    {
        var controller = new ParameterController(db);
        TestAuth.SetUser(controller, SeedAuthorizedUser(db));
        return controller;
    }

    /// <summary>播种一个启用账号并显式授予既有「系统参数」（<c>sys-parameter</c>）菜单（不新增菜单编码）。</summary>
    private static long SeedAuthorizedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"sys-param-{Guid.NewGuid():N}",
            DisplayName = "系统参数操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "系统参数操作员",
            RoleCode = $"SysParamRole-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = new SysMenu
        {
            MenuCode = SystemParameterAuthorizationRules.RequiredMenuCode,
            MenuName = SystemParameterAuthorizationRules.RequiredMenuText,
            MenuType = MenuType.Menu,
            Path = "/system/parameter"
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        return user.Id;
    }

    private static SysParameter SeedParam(ErpDbContext db, string key, string name, string value)
    {
        var p = new SysParameter { ParamKey = key, ParamName = name, ParamValue = value, Description = "" };
        db.SysParameters.Add(p);
        db.SaveChanges();
        return p;
    }
}