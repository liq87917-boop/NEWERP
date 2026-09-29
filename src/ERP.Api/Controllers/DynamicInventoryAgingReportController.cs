using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态库存库龄与成本估值报表（ERP-135）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-inventory-aging-report</b>：返回库存库龄字段白名单目录（需登录 + 库存查询菜单授权）；</item>
/// <item><b>POST /api/dynamic-inventory-aging-report</b>：按选定字段与有界筛选预览库存库龄报表，稳定分页。</item>
/// </list>
/// <para>复用 ERP-034 报表服务（<see cref="IReportService.GetInventoryAgingReportAsync"/>）：
/// 仓库 / 商品 / 截止日期筛选与稳定分页全部由既有只读服务完成，本控制器只做授权与字段投影，不做写入。</para>
/// <para>请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计（POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-inventory-aging-report")]
[Authorize]
public class DynamicInventoryAgingReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicInventoryAgingReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicInventoryAgingReportCatalogDto>.Success(
            DynamicInventoryAgingReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览库存库龄报表（只读、分页有界；复用 ERP-034 只读服务）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicInventoryAgingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicInventoryAgingReportPageDto>.Success(
            await BuildPageAsync(request)));
    }

    /// <summary>复用同一有界、已授权预览管线：授权 → 校验 → 只读查询 → 选定列投影</summary>
    private async Task<DynamicInventoryAgingReportPageDto> BuildPageAsync(DynamicInventoryAgingReportRequest request)
    {
        // 1) 身份 + 既有库存查询菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(CurrentUserId());

        // 2) 字段 / 日期 / 仓库 / 商品 / 页大小校验（全部在报告读取之前完成，失败即拒绝）
        var fieldKeys = DynamicInventoryAgingReportRules.NormalizeFields(request.Fields);
        DynamicInventoryAgingReportRules.ValidateAsOfDate(request.AsOfDate);
        DynamicInventoryAgingReportRules.ValidateWarehouseId(request.WarehouseId);
        DynamicInventoryAgingReportRules.ValidateProductId(request.ProductId);
        DynamicInventoryAgingReportRules.ValidatePageSize(request.PageSize);
        if (request.Page < 1) request.Page = 1;

        // 3) 复用 ERP-034 报表服务（仓库 / 商品 / 日期 / 分页），全程只读不写库
        var query = DynamicInventoryAgingReportRules.BuildQuery(request);
        var report = await _reportService.GetInventoryAgingReportAsync(query);

        // 4) 投影选定列（未知库龄 / 未知成本 / CNY 币种语义保持不变）
        var columns = fieldKeys.Select(k => DynamicInventoryAgingReportRules.GetField(k)!).ToList();
        var rows = report.Items.Select(i => DynamicInventoryAgingReportRules.BuildRow(i, fieldKeys)).ToList();

        return new DynamicInventoryAgingReportPageDto(
            columns,
            rows,
            report.Total,
            report.Page,
            report.PageSize,
            report.TotalPages,
            report.AsOfDate,
            report.CostCurrency,
            DynamicInventoryAgingReportRules.ReadOnlyText,
            DynamicInventoryAgingReportRules.BoundaryText,
            DynamicInventoryAgingReportRules.DisclaimerText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」库存查询模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览库存库龄报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicInventoryAgingReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicInventoryAgingReportRules.RequiredMenuText}」"
                + $"（{DynamicInventoryAgingReportRules.RequiredMenuCode}）模块授权：拒绝预览库存库龄报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}
