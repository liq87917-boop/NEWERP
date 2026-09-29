using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态客户应收账款证据报表（ERP-117）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-receivable-report</b>：返回应收账款证据字段白名单目录（需登录 + 客户资料菜单授权）；</item>
/// <item><b>POST /api/dynamic-receivable-report</b>：按选定字段与有界筛选预览当前账号数据范围内的发票证据，稳定分页。</item>
/// </list>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-receivable-report")]
[Authorize]
public class DynamicReceivableReportController : ControllerBase
{
    private readonly IDynamicReceivableReportQuery _query;

    public DynamicReceivableReportController(IDynamicReceivableReportQuery query)
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
        return Ok(ApiResponse<DynamicReceivableReportCatalogDto>.Success(catalog));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界；单页上限 100）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicReceivableReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = await _query.PreviewAsync(request, CurrentUserId());
        return Ok(ApiResponse<DynamicReceivableReportPageDto>.Success(page));
    }
}
