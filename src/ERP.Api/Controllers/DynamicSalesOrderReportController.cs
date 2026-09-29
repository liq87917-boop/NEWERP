using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态销售订单报表（ERP-112）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/sales-orders/report</b>：返回销售订单字段白名单目录（需登录 + 销售订单菜单授权）；</item>
/// <item><b>POST /api/sales-orders/report</b>：按选定字段与有界筛选预览当前账号数据范围内的订单，稳定分页。</item>
/// </list>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL；请求由既有
/// <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/sales-orders/report")]
[Authorize]
public class DynamicSalesOrderReportController : ControllerBase
{
    private readonly IDynamicSalesOrderReportQuery _query;

    public DynamicSalesOrderReportController(IDynamicSalesOrderReportQuery query)
    {
        _query = query;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由查询层 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>字段白名单目录（有限、只读）</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        var catalog = await _query.GetCatalogAsync(CurrentUserId());
        return Ok(ApiResponse<DynamicSalesOrderReportCatalogDto>.Success(catalog));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界）；可选按客户 / 月份分组的页面小计（ERP-114，币种分开）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicSalesOrderReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed：仅 none / customer / month；无效取值在此直接拒绝（先于任何读取）。
        var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(request.GroupBy);
        if (groupBy != DynamicSalesOrderReportRules.GroupNone)
            request.Fields = DynamicSalesOrderReportRules.EnsureGroupingFields(request.Fields, groupBy);

        var page = await _query.PreviewAsync(request, CurrentUserId());

        // 页面小计：从「同一批有界、已授权预览行」计算，组内按币种分开、绝不跨币种相加。
        var groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);
        return Ok(ApiResponse<DynamicSalesOrderReportPageDto>.Success(
            page with { GroupBy = groupBy, Groups = groups }));
    }
}
