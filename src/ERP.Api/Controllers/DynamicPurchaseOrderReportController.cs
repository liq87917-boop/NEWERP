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
/// 动态采购订单报表（ERP-125）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/purchase-orders/report</b>：返回采购订单字段白名单目录（需登录 + 采购订单菜单授权）；</item>
/// <item><b>POST /api/purchase-orders/report</b>：按选定字段与有界筛选预览当前账号可见（未删除）的采购订单，稳定分页。</item>
/// <item><b>POST /api/purchase-orders/report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用 ERP-125 有界授权预览与选定列顺序）。</item>
/// </list>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL；请求由既有
/// <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/purchase-orders/report")]
[Authorize]
public class DynamicPurchaseOrderReportController : ControllerBase
{
    private readonly IDynamicPurchaseOrderReportQuery _query;

    public DynamicPurchaseOrderReportController(IDynamicPurchaseOrderReportQuery query)
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
        return Ok(ApiResponse<DynamicPurchaseOrderReportCatalogDto>.Success(catalog));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicPurchaseOrderReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await _query.PreviewAsync(request, CurrentUserId());
        return Ok(ApiResponse<DynamicPurchaseOrderReportPageDto>.Success(page));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-127，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；供应商 / 订单编号等标识与金额原币原样保留，文本单元格做公式注入转义。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicPurchaseOrderReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await _query.PreviewAsync(request, CurrentUserId());

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"PurchaseOrderReport_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义）</summary>
    private static byte[] BuildWorkbook(DynamicPurchaseOrderReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicPurchaseOrderReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows("采购订单", rows, columns);
    }
}
