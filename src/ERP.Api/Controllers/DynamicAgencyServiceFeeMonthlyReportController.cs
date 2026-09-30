using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态代理服务费月度汇总报表（ERP-181）控制器：只读的字段目录与预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-agency-service-fee-monthly-report</b>：返回有限字段白名单目录（需登录 + 客户资料菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-agency-service-fee-monthly-report</b>：按选定字段与有界筛选预览 ERP-180 月度汇总，稳定分页、原币隔离、状态金额口径不变。</item>
/// </list>
/// <para>复用 ERP-180 的 <see cref="AgencyServiceFeeMonthlySummaryService.ForQueryAsync"/>：
/// 客户 / 币种 / 对账日期筛选、稳定分页与分组 / 金额口径全部由既有只读服务完成，本控制器只做授权与字段投影，不做写入。</para>
/// <para>每次目录 / 预览请求都重新校验身份、客户资料菜单授权与业务员数据范围（fail closed）；请求由既有
/// <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计（POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-agency-service-fee-monthly-report")]
[Authorize]
public class DynamicAgencyServiceFeeMonthlyReportController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DynamicAgencyServiceFeeMonthlyReportController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>字段白名单目录（有限、只读）</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        await EnsureAuthorizedAsync(CurrentUserId());
        return Ok(ApiResponse<DynamicAgencyServiceFeeMonthlyReportCatalogDto>.Success(
            DynamicAgencyServiceFeeMonthlyReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览代理服务费月度汇总（只读、分页有界；复用 ERP-180 只读服务）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicAgencyServiceFeeMonthlyReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 字段 / 分页 / 筛选全部在读取 ERP-180 数据源之前校验（未知 / 重复字段、越界分页、非法币种、日期倒置均 fail closed）
        var fieldKeys = DynamicAgencyServiceFeeMonthlyReportRules.NormalizeFields(request.Fields);
        DynamicAgencyServiceFeeMonthlyReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var query = DynamicAgencyServiceFeeMonthlyReportRules.BuildQuery(request);

        var view = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(_db, query, scope);

        var columns = fieldKeys
            .Select(k => DynamicAgencyServiceFeeMonthlyReportRules.GetField(k)!)
            .ToList();
        var rows = view.Rows
            .Select(r => DynamicAgencyServiceFeeMonthlyReportRules.BuildRow(r, fieldKeys))
            .ToList();

        return Ok(ApiResponse<DynamicAgencyServiceFeeMonthlyReportPageDto>.Success(
            new DynamicAgencyServiceFeeMonthlyReportPageDto(
                columns,
                rows,
                view.Total,
                view.Page,
                view.PageSize,
                view.TotalPages,
                view.Truncated,
                view.GroupCount,
                view.EmptyText,
                DynamicAgencyServiceFeeMonthlyReportRules.ReadOnlyText,
                DynamicAgencyServiceFeeMonthlyReportRules.BoundaryText,
                DynamicAgencyServiceFeeMonthlyReportRules.EvidenceOnlyText,
                DynamicAgencyServiceFeeMonthlyReportRules.CurrencyIsolationText,
                DynamicAgencyServiceFeeMonthlyReportRules.NoProrationText)));
    }

    /// <summary>身份 + 既有「角色 → 菜单」客户资料模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览代理服务费月度汇总报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuText}」"
                + $"（{DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode}）模块授权：拒绝预览代理服务费月度汇总报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
