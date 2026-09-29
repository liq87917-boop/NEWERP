using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态客户应收账款证据报表（ERP-117）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-receivable-report</b>：返回应收账款证据字段白名单目录（需登录 + 客户资料菜单授权）；</item>
/// <item><b>POST /api/dynamic-receivable-report</b>：按选定字段与有界筛选预览当前账号数据范围内的发票证据，稳定分页。</item>
/// <item><b>POST /api/dynamic-receivable-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览）。</item>
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

    /// <summary>按选定字段与有界筛选预览（只读、分页有界；单页上限 100）；可选按客户 / 月份分组的页面小计（ERP-120，币种分开）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicReceivableReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed：仅 none / customer / month；无效取值在此直接拒绝（先于任何读取）。
        var groupBy = DynamicReceivableReportRules.NormalizeGroupBy(request.GroupBy);
        if (groupBy != DynamicReceivableReportRules.GroupNone)
            request.Fields = DynamicReceivableReportRules.EnsureGroupingFields(request.Fields, groupBy);

        var page = await _query.PreviewAsync(request, CurrentUserId());

        // 页面小计：从「同一批有界、已授权预览行」计算，组内按币种分开、绝不跨币种相加。
        var groups = DynamicReceivableReportRules.BuildGroupSubtotals(page.Rows, groupBy);
        return Ok(ApiResponse<DynamicReceivableReportPageDto>.Success(
            page with { GroupBy = groupBy, Groups = groups }));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-119，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），仅导出当前页选定列；金额保留原币、不做汇率换算或跨币种求和。
    /// <para>文本单元格做公式注入转义；全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicReceivableReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 数据范围
        var page = await _query.PreviewAsync(request, CurrentUserId());

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"CustomerReceivableEvidence_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义）</summary>
    private static byte[] BuildWorkbook(DynamicReceivableReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicReceivableReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows("应收证据", rows, columns);
    }
}
