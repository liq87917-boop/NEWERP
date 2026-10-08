using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用单据控制器：查询与翻页
/// </summary>
public partial class BillProcController
{
    /// <summary>
    /// 分页查询（BillNo 搜索 + 状态筛选）。ERP-405：先做读侧门禁（实时身份 + 该族既有功能菜单 + 客户数据范围），
    /// 再由受控只读服务在范围约束下完成计数与分页；被拒绝的行 / 越权结果绝不出现，响应只含有限授权列。
    /// </summary>
    [HttpGet("{billType}")]
    public async Task<IActionResult> GetPaged(string billType, [FromQuery] PageQuery query, [FromQuery] int? status)
    {
        if (!Bills.TryGetValue(billType, out _))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        try
        {
            var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(_db, CurrentUserId(), billType);
            query.Normalize();

            var page = await _legacyReads.ReadPageAsync(billType, new LegacyBillReadQuery
            {
                Keyword = query.Keyword,
                Status = status,
                Page = query.Page,
                PageSize = query.PageSize,
            }, scope, RequestCancellation());

            return Ok(ApiResponse<object>.Success(new
            {
                items = page.Items,
                total = page.Total,
                page = page.Page,
                pageSize = page.PageSize,
            }));
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("查询", billType, ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }

    /// <summary>
    /// 翻页导航（first/last/prev/next）。ERP-405：范围先于读取；prev/next 会在同一范围内核验锚点，
    /// 不可访问（越权 / 不存在）锚点与「没有更多」返回同一结果，绝不泄露锚点存在性。
    /// </summary>
    [HttpGet("{billType}/navigate")]
    public async Task<IActionResult> Navigate(string billType, [FromQuery] long oid, [FromQuery] string direction)
    {
        if (!Bills.TryGetValue(billType, out _))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        try
        {
            var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(_db, CurrentUserId(), billType);

            var navigation = await _legacyReads.NavigateAsync(billType, oid, direction, scope, RequestCancellation());
            if (!navigation.Found || navigation.Row is null)
                return Ok(ApiResponse<object>.Fail("没有更多单据", ErrorCodes.NotFound));

            return Ok(ApiResponse<object>.Success(navigation.Row));
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("翻页导航", billType, ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }

    /// <summary>
    /// 详情（主表 + 副表明细）。ERP-405：表头先受范围约束；越权 / 不存在的 Oid 返回「单据不存在」，
    /// 且绝不读取任何副表；响应只含有限授权列（无 <c>SELECT *</c> 敏感列泄露）。
    /// </summary>
    [HttpGet("{billType}/{oid:long}")]
    public async Task<IActionResult> GetDetail(string billType, long oid)
    {
        if (!Bills.TryGetValue(billType, out _))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        try
        {
            var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(_db, CurrentUserId(), billType);

            var detail = await _legacyReads.ReadDetailAsync(billType, oid, scope, RequestCancellation());
            if (detail is null)
                return Ok(ApiResponse<object>.Fail("单据不存在", ErrorCodes.NotFound));

            return Ok(ApiResponse<object>.Success(new { main = detail.Main, details = detail.Details }));
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("详情", billType, ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }

    /// <summary>请求取消令牌（直接实例化控制器的单元测试无 HttpContext 时安全退化为 <see cref="CancellationToken.None"/>）。</summary>
    private CancellationToken RequestCancellation()
        => HttpContext?.RequestAborted ?? CancellationToken.None;

    /// <summary>读侧门禁拒绝的结构化告警（只记录受控取值，不写库、不影响主流程）。</summary>
    private static void LogLegacyReadDenied(string operation, string billType, BusinessException ex)
        => Serilog.Log.Warning(
            "旧单据读侧门禁拒绝：{BillType} / {Operation}（错误码 {Code}）", billType, operation, ex.Code);
}
