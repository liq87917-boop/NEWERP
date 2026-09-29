using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户报告包预览控制器（ERP-122，只读、有界）：从客户销项发票登记册打开，把同一客户的
/// <b>销售订单</b>与<b>发票 + 显式收款分摊证据</b>作为两个独立有界分区返回。
/// <list type="number">
/// <item><b>POST /api/customer-report-packet</b>：按正整数客户 Id + 有界日期 / 分页筛选预览两个分区。</item>
/// <item><b>POST /api/customer-report-packet/export</b>：把同一客户的两个分区导出为含两个独立工作表的 Excel（xlsx，只读，复用有界双授权预览，仅导出当前页）。</item>
/// </list>
/// <para>授权：必须同时具备「销售订单」与「客户资料」两个既有菜单授权；两个分区复用
/// <see cref="IDynamicSalesOrderReportQuery"/>（ERP-112）与 <see cref="IDynamicReceivableReportQuery"/>（ERP-117），
/// 各自独立重检本分区菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，任一失败即拒绝整个响应（fail closed）。</para>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不推断发票到订单的链接、不结算、不计算账户余额或催收状态；
/// 请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/customer-report-packet")]
[Authorize]
public class CustomerReportPacketController : ControllerBase
{
    private readonly IDynamicSalesOrderReportQuery _orderQuery;
    private readonly IDynamicReceivableReportQuery _receivableQuery;
    private readonly IErpDbContext _db;

    public CustomerReportPacketController(
        IDynamicSalesOrderReportQuery orderQuery,
        IDynamicReceivableReportQuery receivableQuery,
        IErpDbContext db)
    {
        _orderQuery = orderQuery;
        _receivableQuery = receivableQuery;
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由规则层 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>按有界筛选预览两个独立分区（只读；任一权限 / 校验失败均拒绝整个响应，绝不返回部分结果）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] CustomerReportPacketRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packet = await LoadPacketAsync(request);
        return Ok(ApiResponse<CustomerReportPacketDto>.Success(packet));
    }

    /// <summary>
    /// 导出同一客户的两个分区为 Excel（ERP-123，只读）：复用「有界、双授权、作用域化」的同一预览（每次请求重新校验身份 /
    /// 双菜单授权 / 客户 / 日期 / 分页 / 业务员数据范围），生成两个独立工作表（销售订单 + 应收证据），仅导出当前页，
    /// 金额按原币呈现、剩余证据状态显式保留（known / unknown / over_allocated）、不做跨单拼接或混合币种合计。
    /// <para>文本单元格做公式注入转义；全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] CustomerReportPacketRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packet = await LoadPacketAsync(request);
        var bytes = BuildWorkbook(packet);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"CustomerReportPacket_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>复用预览的全部有界校验与双授权 / 作用域化查询（预览与导出共用同一口径，任一失败均拒绝整个响应）</summary>
    private async Task<CustomerReportPacketDto> LoadPacketAsync(CustomerReportPacketRequest request)
    {
        // 1) 字段 / 日期 / 分页有界校验（先于任何读取）
        CustomerReportPacketRules.ValidateCustomerId(request.CustomerId);
        CustomerReportPacketRules.ValidateDateRange(request.StartDate, request.EndDate);
        CustomerReportPacketRules.ValidatePageSize(request.PageSize);
        var page = CustomerReportPacketRules.NormalizePage(request.Page);

        var userId = CurrentUserId();
        CustomerReportPacketRules.EnsureAuthenticated(userId);

        // 2) 端点级双菜单授权：销售订单 + 客户资料都必须具备（任一缺失即 fail closed）
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(_db, userId!.Value);
        CustomerReportPacketRules.EnsureBothMenuPermissions(menuCodes);

        // 3) 两个作用域化报表查询接口：各自独立重检本分区菜单授权 + 业务员数据范围
        var salesOrders = await _orderQuery.PreviewAsync(new DynamicSalesOrderReportRequest
        {
            CustomerId = request.CustomerId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Page = page,
            PageSize = request.PageSize,
        }, userId);

        var receivableEvidence = await _receivableQuery.PreviewAsync(new DynamicReceivableReportRequest
        {
            CustomerId = request.CustomerId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Page = page,
            PageSize = request.PageSize,
        }, userId);

        return CustomerReportPacketRules.BuildPacket(request.CustomerId, salesOrders, receivableEvidence);
    }

    /// <summary>生成含「销售订单」与「应收证据」两个独立工作表的只读工作簿（原币 / 标识 / 显式剩余状态，公式注入转义，绝不拼合计）</summary>
    private static byte[] BuildWorkbook(CustomerReportPacketDto packet)
    {
        using var workbook = new XSSFWorkbook();

        AppendSheet(
            workbook,
            CustomerReportPacketRules.SalesOrderSheetName,
            packet.SalesOrders.Columns.Select(c => (c.Key, c.Label)).ToList(),
            packet.SalesOrders.Rows.Select(CustomerReportPacketRules.BuildExportRow).ToList());

        AppendSheet(
            workbook,
            CustomerReportPacketRules.ReceivableEvidenceSheetName,
            packet.ReceivableEvidence.Columns.Select(c => (c.Key, c.Label)).ToList(),
            packet.ReceivableEvidence.Rows.Select(CustomerReportPacketRules.BuildExportRow).ToList());

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>把一个分区写为独立工作表：首行为列标题，后续行为该分区当前页的只读行（列顺序与预览一致）</summary>
    private static void AppendSheet(
        XSSFWorkbook workbook,
        string sheetName,
        List<(string Key, string Label)> columns,
        List<Dictionary<string, object?>> rows)
    {
        var sheet = workbook.CreateSheet(sheetName);

        var header = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
            header.CreateCell(c).SetCellValue(columns[c].Label);

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

    /// <summary>按类型写入单元格（与既有 ExcelExporter 同口径的文本 / 数值语义；未知剩余证据金额 null → 空文本，绝不回落 0）</summary>
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
