using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
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

    /// <summary>
    /// 导出当前页为 Excel（ERP-115，只读）：复用「有界、已授权预览」与选定列顺序（重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），可选按分组追加「本页小计」工作表（币种分开）。
    /// <para>文本单元格做公式注入转义；全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicSalesOrderReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed + 补齐小计所需字段，与预览同口径
        var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(request.GroupBy);
        if (groupBy != DynamicSalesOrderReportRules.GroupNone)
            request.Fields = DynamicSalesOrderReportRules.EnsureGroupingFields(request.Fields, groupBy);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 数据范围
        var page = await _query.PreviewAsync(request, CurrentUserId());
        var groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);

        var bytes = BuildWorkbook(page, groups, groupBy);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalesOrderReport_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为 PDF（ERP-116，只读）：复用「有界、已授权预览」与选定列顺序（重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），以 PDFsharp 6.2.4 分页渲染选定列（中文黑体 SimHei），
    /// 可选按分组追加「本页小计」（币种分开）。缺失黑体字体时显式失败（不产出乱码 / 缺字 PDF）。
    /// </summary>
    [HttpPost("export/pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicSalesOrderReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed + 补齐小计所需字段，与预览同口径
        var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(request.GroupBy);
        if (groupBy != DynamicSalesOrderReportRules.GroupNone)
            request.Fields = DynamicSalesOrderReportRules.EnsureGroupingFields(request.Fields, groupBy);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 数据范围
        var page = await _query.PreviewAsync(request, CurrentUserId());
        var groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);

        var bytes = DynamicSalesOrderPdfExporter.Export(page, groups, groupBy);
        return File(bytes, "application/pdf", $"SalesOrderReport_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>用 ExcelExporter 生成数据工作表；分组时在同一工作簿追加「本页小计」工作表（币种分开）</summary>
    private static byte[] BuildWorkbook(
        DynamicSalesOrderReportPageDto page,
        IReadOnlyList<DynamicSalesOrderReportGroupDto> groups,
        string groupBy)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicSalesOrderReportRules.BuildExportRow).ToList();
        var bytes = ExcelExporter.ExportRows("销售订单", rows, columns);

        if (groupBy == DynamicSalesOrderReportRules.GroupNone)
            return bytes;

        using var input = new MemoryStream(bytes);
        var workbook = new XSSFWorkbook(input);
        AppendSubtotalSheet(workbook, groups);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「本页小计」工作表：每行 = 分组 × 币种（条数 / 金额），金额只对同币种求和</summary>
    private static void AppendSubtotalSheet(XSSFWorkbook workbook, IReadOnlyList<DynamicSalesOrderReportGroupDto> groups)
    {
        var sheet = workbook.CreateSheet("本页小计");
        var columns = DynamicSalesOrderReportRules.SubtotalColumns;

        var header = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
            header.CreateCell(c).SetCellValue(columns[c].Title);

        var rows = DynamicSalesOrderReportRules.BuildSubtotalRows(groups);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            for (var c = 0; c < columns.Count; c++)
            {
                var value = rows[r].TryGetValue(columns[c].Key, out var v) ? v : null;
                WriteCell(row.CreateCell(c), value);
            }
        }
    }

    /// <summary>按类型写入小计单元格（与 ExcelExporter 同口径的文本 / 数值语义）</summary>
    private static void WriteCell(ICell cell, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                cell.SetCellValue(string.Empty);
                break;
            case int i:
                cell.SetCellValue(i);
                break;
            case long l:
                cell.SetCellValue(l);
                break;
            case decimal m:
                cell.SetCellValue((double)m);
                break;
            case double d:
                cell.SetCellValue(d);
                break;
            case float f:
                cell.SetCellValue(f);
                break;
            case bool b:
                cell.SetCellValue(b ? "是" : "否");
                break;
            case DateTime dt:
                cell.SetCellValue(dt.ToString("yyyy-MM-dd HH:mm"));
                break;
            default:
                cell.SetCellValue(value.ToString() ?? string.Empty);
                break;
        }
    }
}
