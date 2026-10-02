using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-252）控制器：只读的字段目录与预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-container-stats-report</b>：返回柜量与装柜利用率证据字段白名单目录（需登录 + 柜量与装柜利用率统计菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-container-stats-report</b>：按选定字段与有界日期窗口（start / end）及可选筛选（客户 / 柜号关键字）预览当前账号数据范围内的证据桶，稳定分页。</item>
/// </list>
/// <para>复用既有「柜量与装柜利用率统计」（container-stats）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService"/>（ERP-251 / ERP-252）只读完成，本控制器只做授权与字段投影，不做写入。
/// 请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计（POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-container-stats-report")]
[Authorize]
public class DynamicContainerStatsReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicContainerStatsReportController(IErpDbContext db, IReportService reportService)
    {
        _db = db;
        _reportService = reportService;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>字段白名单目录（有限、只读）</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        await EnsureAuthorizedAsync(CurrentUserId());
        return Ok(ApiResponse<DynamicContainerStatsReportCatalogDto>.Success(
            DynamicContainerStatsReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口及可选筛选预览柜量与装柜利用率证据（只读、分页有界；复用 ERP-251 柜量统计口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicContainerStatsReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicContainerStatsReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>每次重新校验身份 + 菜单授权 + 业务员数据范围，再交由服务层校验请求并只读查询当前页</summary>
    private async Task<DynamicContainerStatsReportPageDto> BuildPageAsync(DynamicContainerStatsReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = await EnsureAuthorizedAsync(CurrentUserId());
        return await _reportService.GetDynamicContainerStatsReportAsync(request, scope);
    }

    /// <summary>身份 + 既有「角色 → 菜单」柜量与装柜利用率统计模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态柜量与装柜利用率证据报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicContainerStatsReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicContainerStatsReportRules.RequiredMenuText}」"
                + $"（{DynamicContainerStatsReportRules.RequiredMenuCode}）模块授权：拒绝预览动态柜量与装柜利用率证据报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
