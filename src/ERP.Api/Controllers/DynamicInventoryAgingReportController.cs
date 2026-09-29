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
/// 动态库存库龄与成本估值报表（ERP-135）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-inventory-aging-report</b>：返回库存库龄字段白名单目录（需登录 + 库存查询菜单授权）；</item>
/// <item><b>POST /api/dynamic-inventory-aging-report</b>：按选定字段与有界筛选预览库存库龄报表，稳定分页。</item>
/// <item><b>POST /api/dynamic-inventory-aging-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/dynamic-inventory-aging-report/pdf</b>：导出当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分）。</item>
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

    /// <summary>
    /// 导出当前页为 Excel（ERP-138，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；固定库龄分层、基础单位数量与 CNY 成本币种语义保持不变，
    /// 未知成本金额显式保留为未知（null → 空单元格，绝不回落为 0），文本单元格做公式注入转义。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicInventoryAgingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await BuildPageAsync(request);

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"InventoryAging_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为分页中文 PDF（ERP-139，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；固定 5 格库龄分层、基础单位数量与 CNY 成本币种语义保持不变，
    /// 未知库龄 / 未知成本证据显式保留（金额 null →「未知」，绝不回落为 0），宽列集按可用页宽跨页拆分避免裁切。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失时显式失败（不产出乱码或缺字 PDF）。
    /// 全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicInventoryAgingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await BuildPageAsync(request);

        var bytes = DynamicInventoryAgingPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"InventoryAging_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义）</summary>
    private static byte[] BuildWorkbook(DynamicInventoryAgingReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicInventoryAgingReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows("库存库龄", rows, columns);
    }

    /// <summary>复用同一有界、已授权预览管线：授权 → 校验 → 只读查询 → 选定列投影 → 分组行数分布</summary>
    private async Task<DynamicInventoryAgingReportPageDto> BuildPageAsync(DynamicInventoryAgingReportRequest request)
    {
        // 1) 身份 + 既有库存查询菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(CurrentUserId());

        // 2) 字段 / 日期 / 仓库 / 商品 / 页大小 / 分组键校验（全部在报告读取之前完成，失败即拒绝）
        var fieldKeys = DynamicInventoryAgingReportRules.NormalizeFields(request.Fields);
        DynamicInventoryAgingReportRules.ValidateAsOfDate(request.AsOfDate);
        DynamicInventoryAgingReportRules.ValidateWarehouseId(request.WarehouseId);
        DynamicInventoryAgingReportRules.ValidateProductId(request.ProductId);
        DynamicInventoryAgingReportRules.ValidatePageSize(request.PageSize);
        if (request.Page < 1) request.Page = 1;
        var groupBy = DynamicInventoryAgingReportRules.NormalizeGroupBy(request.GroupBy);

        // 3) 复用 ERP-034 报表服务（仓库 / 商品 / 日期 / 分页），全程只读不写库
        var query = DynamicInventoryAgingReportRules.BuildQuery(request);
        var report = await _reportService.GetInventoryAgingReportAsync(query);

        // 4) 投影选定列（未知库龄 / 未知成本 / CNY 币种语义保持不变）
        var columns = fieldKeys.Select(k => DynamicInventoryAgingReportRules.GetField(k)!).ToList();
        var rows = report.Items.Select(i => DynamicInventoryAgingReportRules.BuildRow(i, fieldKeys)).ToList();

        // 5) 分组行数分布（ERP-137）：只统计当前授权预览页的行数，绝不求和任何数量 / 金额
        var groups = DynamicInventoryAgingReportRules.BuildGroupCounts(report.Items, groupBy);

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
            DynamicInventoryAgingReportRules.DisclaimerText,
            groupBy,
            groups);
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
