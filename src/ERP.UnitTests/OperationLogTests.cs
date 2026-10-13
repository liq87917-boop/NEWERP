using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 操作日志查询测试（模块 / 单据号 / 关键字 / 时间范围筛选）。
/// <para>ERP-499：入口新增实时身份 + 既有「系统日志」（<c>sys-log</c>）菜单授权后，本文件的历史筛选契约
/// 由**真实启用身份且持有既有 sys-log 菜单授权**的进程内夹具驱动（仅隔离测试数据，不新增任何授权）；
/// 分页筛选与 <c>ApiResponse&lt;PagedResult&lt;SysOperationLog&gt;&gt;</c> 响应契约保持不变。</para>
/// </summary>
public class OperationLogTests
{
    /// <summary>构造日志控制器（注入持有既有 sys-log 授权的真实启用身份）并写入测试日志</summary>
    private static async Task<(OperationLogController Controller, List<SysOperationLog> Logs)> BuildAsync()
    {
        var db = TestDbFactory.Create();
        var userId = SeedLogOperator(db);
        var logs = new List<SysOperationLog>
        {
            new() { UserName = "admin", Module = "销售订单", Action = "新增", Path = "/api/v2/bills/sales-order/1", BillNo = "SO202609120001", CreatedAt = DateTime.Now.AddHours(-2) },
            new() { UserName = "admin", Module = "销售订单", Action = "审核", Path = "/api/v2/bills/sales-order/1", BillNo = "SO202609120001", CreatedAt = DateTime.Now.AddHours(-1) },
            new() { UserName = "tom", Module = "基础资料", Action = "新增", Path = "/api/base/customers", BillNo = string.Empty, CreatedAt = DateTime.Now },
            new() { UserName = "tom", Module = "基础资料", Action = "删除", Path = "/api/base/customers/9", BillNo = string.Empty, CreatedAt = DateTime.Now, IsDeleted = true },
        };
        db.SysOperationLogs.AddRange(logs);
        await db.SaveChangesAsync();

        var controller = new OperationLogController(db);
        TestAuth.SetUser(controller, userId);
        return (controller, logs);
    }

    /// <summary>播种真实启用身份 + 既有「系统日志」（sys-log）功能菜单授权（仅隔离测试数据，不新增授权）。</summary>
    private static long SeedLogOperator(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"sys-log-{Guid.NewGuid():N}",
            DisplayName = "系统日志授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);

        var role = new SysRole
        {
            RoleName = "系统日志操作员",
            RoleCode = $"SysLogRole-{Guid.NewGuid():N}"
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        var menu = new SysMenu
        {
            MenuCode = OperationLogAuthorizationRules.RequiredMenuCode,
            MenuName = OperationLogAuthorizationRules.RequiredMenuText,
            MenuType = MenuType.Menu,
            Path = "/system/log"
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>读取分页响应</summary>
    private static PagedResult<SysOperationLog> GetPaged(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<PagedResult<SysOperationLog>>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    [Fact]
    public async Task GetPaged_默认排除软删除日志()
    {
        var (controller, _) = await BuildAsync();

        var data = GetPaged(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null, null, null));

        Assert.Equal(3, data.Total);
    }

    [Fact]
    public async Task GetPaged_按单据号筛选_返回该单据日志()
    {
        var (controller, _) = await BuildAsync();

        var data = GetPaged(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, "SO202609120001", null, null));

        Assert.Equal(2, data.Total);
        Assert.All(data.Items, l => Assert.Equal("SO202609120001", l.BillNo));
    }

    [Fact]
    public async Task GetPaged_按模块与用户筛选()
    {
        var (controller, _) = await BuildAsync();

        var byModule = GetPaged(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, "基础资料", null, null, null, null));
        var byUser = GetPaged(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, "tom", null, null, null));

        Assert.Equal(1, byModule.Total);
        Assert.Equal(1, byUser.Total);
    }

    [Fact]
    public async Task GetPaged_按关键字与时间范围筛选()
    {
        var (controller, _) = await BuildAsync();

        var byKeyword = GetPaged(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50, Keyword = "审核" }, null, null, null, null, null));
        var byRange = GetPaged(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null, null, null,
            DateTime.Today.AddHours(-3), DateTime.Today));

        Assert.Equal(1, byKeyword.Total);
        Assert.Equal(3, byRange.Total);
    }
}
