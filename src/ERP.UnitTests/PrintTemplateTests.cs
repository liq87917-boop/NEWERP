using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 打印模板（打印设计）测试：默认模板、保存、设为默认、软删除。
/// <para>ERP-493：授权在每一个入口<b>无条件</b>执行（与 <c>Request.Path</c> 是否赋值、请求形状、
/// 是否绑定 <c>HttpContext</c> 完全无关），因此进程内夹具一律绑定隔离内存测试库中的<b>真实启用身份</b>，
/// 且该身份持有既有「样式设计」（<c>print-design</c>）菜单授权（授予口径与生产端「角色 → 菜单」完全同源）。
/// 菜单 / 角色 / 账号只播种在隔离测试数据里，绝不新增或修改任何生产授权，也不使用任何测试专用开关。</para>
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
        var controller = Controller(db);

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
        var controller = Controller(db);

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
        var controller = Controller(db);
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
        var controller = Controller(db);
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
        var controller = Controller(db);
        var saved = GetData<SysPrintTemplate>(await controller.Save(new SysPrintTemplate
        {
            BillType = "complaint", TemplateName = "客诉模板"
        }));

        await controller.Delete(saved.Id);

        var template = GetData<SysPrintTemplate>(await controller.GetDefault("complaint"));
        Assert.Equal("默认模板", template.TemplateName); // 回退到内置默认模板
    }

    // ==================== ERP-493 授权夹具（隔离测试数据：真实启用身份 + 既有 print-design 菜单） ====================

    /// <summary>
    /// ERP-493：为进程内直调夹具绑定一个真实启用身份 —— 该身份持有隔离内存测试库中的既有「样式设计」
    /// （<c>print-design</c>）菜单授权。夹具<b>不设置 <c>Request.Path</c></b>（默认空路径），
    /// 以证明授权与请求路径 / 请求形状 / 是否绑定请求上下文完全无关。
    /// 菜单 / 角色 / 账号只播种在隔离测试数据里，绝不新增或修改任何生产授权。
    /// </summary>
    private static PrintTemplateController Controller(ErpDbContext db)
    {
        var ctl = new PrintTemplateController(db);
        TestAuth.SetUser(ctl, SeedPrintDesignOperator(db));
        return ctl;
    }

    /// <summary>播种启用账号 + 既有「样式设计」菜单授权的角色（与生产端「角色 → 菜单」授予口径同源）。</summary>
    private static long SeedPrintDesignOperator(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == PrintTemplateAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = PrintTemplateAuthorizationRules.RequiredMenuCode,
                MenuName = PrintTemplateAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu,
                Path = "/print-design"
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        var role = new SysRole
        {
            RoleName = "打印设计单元测试角色",
            RoleCode = $"PrintTemplate-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"print-template-{Guid.NewGuid():N}",
            DisplayName = "打印设计单元测试账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }
}
