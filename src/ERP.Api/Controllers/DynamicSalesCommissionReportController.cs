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
/// 动态业务员提成证据报表（ERP-244）控制器：只读的字段目录与预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-sales-commission-report</b>：返回业务员提成证据字段白名单目录（需登录 + 业务员提成表菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-sales-commission-report</b>：按选定字段与有界日期窗口（start / end）及可选筛选（客户 / 业务员 / 原币）预览当前账号数据范围内的业务员桶 × 原币证据行，稳定分页。</item>
/// </list>
/// <para>复用既有「业务员提成表」（sales-commission）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService"/>（ERP-243 / ERP-244）只读完成，本控制器只做授权与字段投影，不做写入。
/// 请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计（POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-sales-commission-report")]
[Authorize]
public class DynamicSalesCommissionReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicSalesCommissionReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicSalesCommissionReportCatalogDto>.Success(
            DynamicSalesCommissionReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口及可选筛选预览业务员提成证据（只读、分页有界；复用 ERP-243 业务员提成口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicSalesCommissionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicSalesCommissionReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-245，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 已知签名原币金额 / 计数按类型写入数值单元格，null 金额 / 利润 / 利润率 / 提成比例 / 提成额显式「未知」（绝不写成 0），
    /// 配置为 0 的当前参考比例写入数值 0；并追加「报表口径」上下文工作表标注规范化日期 / 应用筛选 / 页面覆盖 / 来源计数与上限 /
    /// 当前用户受限已审核订单来源 / 当前参考比例 / 未知历史利润与提成口径（即使对应列被取消选择也始终包含）。
    /// 每次请求重新校验身份 / 业务员提成表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页（fail closed），
    /// 从不信任客户端行或预览缓存；绝不追加跨币种合计。授权撤销 / 无效输入 / 来源超限返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicSalesCommissionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = new DynamicSalesCommissionExcelExporter().Build(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalesCommissionEvidence_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 下载当前页为中文 PDF（ERP-246，只读）：复用同一有界、已授权预览与选定列顺序，重建当前页（绝不信任客户端行 / 预览缓存），
    /// 以 PDFsharp 分页渲染选定列：已知原币金额按签名数值呈现（绝不跨币种合计），null 金额 / 利润 / 利润率 / 提成比例 / 提成额显式「未知」，
    /// 配置为 0 的当前参考比例仍按数值 0 呈现；并始终标注规范化日期 / 应用筛选 / 页面覆盖 / 500 订单来源上限 / 来源与原始原币分组 /
    /// 当前参考比例 / 未知历史利润与提成口径（即使对应列被取消选择也始终包含）。宽列集按可用页宽拆成多个列页（携带身份 / 覆盖上下文），
    /// 行数超出按行页拆分、每页重复表头，绝不追加未选定列或跨币种合计。字体缺失 / 渲染失败显式拒绝下载，绝不空成功或缺失字形成功。
    /// 每次请求重新校验身份 / 业务员提成表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页 / 应用筛选（fail closed），
    /// 授权撤销 / 无效输入 / 来源超限返回错误、不返回任何文件。全程只读，请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出 PDF」）。
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicSalesCommissionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicSalesCommissionPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"SalesCommissionEvidence_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>每次重新校验身份 + 菜单授权 + 业务员数据范围，再交由服务层校验请求并只读查询当前页</summary>
    private async Task<DynamicSalesCommissionReportPageDto> BuildPageAsync(DynamicSalesCommissionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = await EnsureAuthorizedAsync(CurrentUserId());
        return await _reportService.GetDynamicSalesCommissionReportAsync(request, scope);
    }

    /// <summary>身份 + 既有「角色 → 菜单」业务员提成表模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态业务员提成证据报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSalesCommissionReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSalesCommissionReportRules.RequiredMenuText}」"
                + $"（{DynamicSalesCommissionReportRules.RequiredMenuCode}）模块授权：拒绝预览动态业务员提成证据报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
