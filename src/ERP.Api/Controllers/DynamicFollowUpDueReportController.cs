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
/// 动态跟进提醒报表（ERP-193）控制器：只读的字段目录与预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-follow-up-due-report</b>：返回跟进提醒证据字段白名单目录（需登录 + 跟进提醒菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-follow-up-due-report</b>：按选定字段与有界筛选（as-of 日期 / 提前天数 / 可选到期状态）预览当前账号数据范围内的跟进证据，稳定分页。</item>
/// <item><b>POST /api/dynamic-follow-up-due-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/dynamic-follow-up-due-report/pdf</b>：导出当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分）。</item>
/// </list>
/// <para>复用 ERP-192 的「跟进提醒」菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览请求都重新校验身份、菜单授权与业务员数据范围（fail closed），查询由 <see cref="ReportService"/> 只读完成，
/// 本控制器只做授权与字段投影，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-follow-up-due-report")]
[Authorize]
public class DynamicFollowUpDueReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicFollowUpDueReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicFollowUpDueReportCatalogDto>.Success(
            DynamicFollowUpDueReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览跟进提醒证据（只读、分页有界；复用 ERP-192 跟进提醒口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicFollowUpDueReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-195，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 文本单元格做公式注入转义，数值 / 日期按类型写入。每次请求重新校验身份 / 跟进提醒菜单授权 /
    /// 业务员数据范围 / 字段 / 筛选 / 分页（fail closed）。授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"FollowUpDue_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>复用同一有界、已授权预览管线：每次重新校验身份 / 菜单授权 / 数据范围，再只读查询当前页</summary>
    private async Task<DynamicFollowUpDueReportPageDto> BuildPageAsync(DynamicFollowUpDueReportRequest request)
    {
        var scope = await EnsureAuthorizedAsync(CurrentUserId());
        return await _reportService.GetDynamicFollowUpDueReportAsync(request, scope);
    }

    /// <summary>
    /// 导出当前页为分页中文 PDF（ERP-196，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 /
    /// 跟进提醒菜单授权 / 业务员数据范围 / 字段 / 筛选 / 分页，fail closed），仅导出当前页选定列；选定字段、中文标签、
    /// 到期证据（下次跟进日期 / 到期天数 / 到期状态）与只读 / 边界 / 免责文案、空页说明显式保留，宽列集按可用页宽
    /// 跨页拆分、行数超出按行页拆分避免裁切。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失时显式失败（不产出乱码或缺字 PDF）。</para>
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicFollowUpDuePdfExporter.Export(page);
        return File(bytes, "application/pdf", $"FollowUpDue_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义）</summary>
    private static byte[] BuildWorkbook(DynamicFollowUpDueReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicFollowUpDueReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows(DynamicFollowUpDueReportRules.RequiredMenuText, rows, columns);
    }

    /// <summary>身份 + 既有「角色 → 菜单」跟进提醒模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态跟进提醒报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicFollowUpDueReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicFollowUpDueReportRules.RequiredMenuText}」"
                + $"（{DynamicFollowUpDueReportRules.RequiredMenuCode}）模块授权：拒绝预览动态跟进提醒报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
