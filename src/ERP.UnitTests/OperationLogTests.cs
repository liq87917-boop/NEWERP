using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 操作日志查询测试（模块 / 单据号 / 关键字 / 时间范围筛选）
/// </summary>
public class OperationLogTests
{
    /// <summary>构造日志控制器并写入测试日志</summary>
    private static async Task<(OperationLogController Controller, List<SysOperationLog> Logs)> BuildAsync()
    {
        var db = TestDbFactory.Create();
        var logs = new List<SysOperationLog>
        {
            new() { UserName = "admin", Module = "销售订单", Action = "新增", Path = "/api/v2/bills/sales-order/1", BillNo = "SO202609120001", CreatedAt = DateTime.Now.AddHours(-2) },
            new() { UserName = "admin", Module = "销售订单", Action = "审核", Path = "/api/v2/bills/sales-order/1", BillNo = "SO202609120001", CreatedAt = DateTime.Now.AddHours(-1) },
            new() { UserName = "tom", Module = "基础资料", Action = "新增", Path = "/api/base/customers", BillNo = string.Empty, CreatedAt = DateTime.Now },
            new() { UserName = "tom", Module = "基础资料", Action = "删除", Path = "/api/base/customers/9", BillNo = string.Empty, CreatedAt = DateTime.Now, IsDeleted = true },
        };
        db.SysOperationLogs.AddRange(logs);
        await db.SaveChangesAsync();
        return (new OperationLogController(db), logs);
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
