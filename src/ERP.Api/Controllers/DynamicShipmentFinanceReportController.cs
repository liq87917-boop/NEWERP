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
/// 动态销售订单出货 / 财务进度报表（ERP-156）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/sales-orders/dynamic-shipment-finance-report</b>：返回 ERP-032 订单证据字段白名单目录（需登录 + 销售订单菜单授权）；</item>
/// <item><b>POST /api/sales-orders/dynamic-shipment-finance-report</b>：按选定字段与有界筛选预览当前账号数据范围内的订单，稳定分页。</item>
/// <item><b>POST /api/sales-orders/dynamic-shipment-finance-report/export</b>：把当前页选定列导出为 xlsx（只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/sales-orders/dynamic-shipment-finance-report/pdf</b>：把当前页选定列导出为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分）。</item>
/// </list>
/// <para>复用 ERP-032 <see cref="SalesOrderShipmentFinanceReport.ForQueryAsync"/> 的权威派生：客户 / 币种 / 订单日期 / 出货状态 /
/// 收款链接状态筛选与稳定分页全部由既有只读方法完成，本控制器只做授权、字段校验、数据范围过滤与选定列投影，不做写入。</para>
/// <para>每次请求重新校验身份 / 销售订单菜单授权 / 字段 / 筛选 / 页大小 / 业务员数据范围（fail closed）；全程只读：无 Add / Update / Remove / SaveChanges，
/// 不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/sales-orders/dynamic-shipment-finance-report")]
[Authorize]
public class DynamicShipmentFinanceReportController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DynamicShipmentFinanceReportController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>字段白名单目录（有限、只读）</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        await EnsureAuthorizedAsync(CurrentUserId());
        return Ok(ApiResponse<DynamicShipmentFinanceReportCatalogDto>.Success(
            DynamicShipmentFinanceReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界；单页上限 200）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicShipmentFinanceReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicShipmentFinanceReportPageDto>.Success(
            await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-158，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），仅导出当前页选定列；未知金额 / 未知数量保持 null（空单元格，绝不回落为 0），
    /// 金额按原币分别成行、绝不跨币种合并或换算，文本单元格做公式注入转义。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicShipmentFinanceReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 数据范围
        var page = await BuildPageAsync(request);

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"ShipmentFinanceReport_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为分页中文 PDF（ERP-159，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），仅导出当前页选定列；金额与数量按原币分别成行、绝不跨币种合并或换算，
    /// 未知金额 / 未知数量显式保留（null → 「未知」，绝不回落为 0），宽列集按可用页宽跨页拆分、行数超出按行页拆分，避免列被裁切。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失时显式失败（不产出乱码或缺字 PDF）；全程只读，
    /// 不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicShipmentFinanceReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 数据范围
        var page = await BuildPageAsync(request);

        var bytes = DynamicShipmentFinancePdfExporter.Export(page);
        return File(bytes, "application/pdf", $"ShipmentFinanceReport_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>
    /// 有界、已授权的预览构建：先校验（身份 / 菜单 / 字段 / 筛选 / 页大小），再解析业务员数据范围，
    /// 然后把数据范围过滤应用到 ERP-032 源查询内部（计数与分页之前），最后只投影当前页选定列。
    /// </summary>
    private async Task<DynamicShipmentFinanceReportPageDto> BuildPageAsync(
        DynamicShipmentFinanceReportRequest request)
    {
        // 1) 身份 + 既有销售订单菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        var userId = CurrentUserId();
        await EnsureAuthorizedAsync(userId);

        // 2) 字段 / 筛选 / 页大小校验（全部在读取任何源数据之前完成）
        var fieldKeys = DynamicShipmentFinanceReportRules.NormalizeFields(request.Fields);
        var customerId = DynamicShipmentFinanceReportRules.NormalizeCustomerId(request.CustomerId);
        var currency = DynamicShipmentFinanceReportRules.NormalizeCurrency(request.Currency);
        var shipmentStatus = DynamicShipmentFinanceReportRules.NormalizeShipmentStatus(request.ShipmentStatus);
        var financeLinkStatus = DynamicShipmentFinanceReportRules.NormalizeFinanceLinkStatus(request.FinanceLinkStatus);
        DynamicShipmentFinanceReportRules.ValidateDateRange(request.OrderDateFrom, request.OrderDateTo);
        DynamicShipmentFinanceReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;

        // 3) 解析当前账号业务员数据范围（特权账号不过滤；受限制业务员仅其被分配客户）
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        // 4) 复用 ERP-032 源查询：数据范围过滤在源查询内部完成（先于计数与分页）
        var query = new SalesOrderShipmentFinanceQuery
        {
            CustomerId = customerId,
            Currency = currency,
            OrderDateFrom = request.OrderDateFrom,
            OrderDateTo = request.OrderDateTo,
            ShipmentStatus = shipmentStatus,
            FinanceLinkStatus = financeLinkStatus,
            Page = page,
            PageSize = request.PageSize,
        };
        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(_db, query, scope);

        // 5) 仅投影选定列（保持请求顺序；未知金额 / 未知数量 / 原币照实保留）
        var columns = fieldKeys.Select(k => DynamicShipmentFinanceReportRules.GetField(k)!).ToList();
        var rows = report.Groups.SelectMany(g => g.Orders)
            .Select(BuildSourceRow)
            .Select(s => DynamicShipmentFinanceReportRules.BuildRow(s, fieldKeys))
            .ToList();

        return new DynamicShipmentFinanceReportPageDto(
            columns,
            rows,
            report.Total,
            report.Page,
            report.PageSize,
            report.TotalPages,
            DynamicShipmentFinanceReportRules.ReadOnlyText,
            DynamicShipmentFinanceReportRules.BoundaryText,
            DynamicShipmentFinanceReportRules.DisclaimerText);
    }

    /// <summary>把单张 ERP-032 订单证据行展开为整行「字段 → 值」字典（仅白名单字段，供 <see cref="DynamicShipmentFinanceReportRules.BuildRow"/> 投影）</summary>
    private static Dictionary<string, object?> BuildSourceRow(SalesOrderShipmentFinanceOrder o)
        => new(StringComparer.Ordinal)
        {
            ["orderId"] = o.OrderId,
            ["orderNo"] = o.OrderNo,
            ["orderDate"] = o.OrderDate,
            ["status"] = o.Status,
            ["customerId"] = o.CustomerId,
            ["customerName"] = o.CustomerName,
            ["currency"] = o.Currency,
            ["orderAmount"] = o.OrderAmount,
            ["recordedDepositAmount"] = o.RecordedDepositAmount,
            ["orderedQuantity"] = o.OrderedQuantity,
            ["shippedQuantity"] = o.ShippedQuantity,
            ["pendingShipmentQuantity"] = o.PendingShipmentQuantity,
            ["outstandingQuantity"] = o.OutstandingQuantity,
            ["shipmentStatus"] = o.ShipmentStatus,
            ["hasApprovedShipment"] = o.HasApprovedShipment,
            ["shipmentDocumentCount"] = o.ShipmentDocumentCount,
            ["approvedShipmentCount"] = o.ApprovedShipmentCount,
            ["financeLinkStatus"] = o.FinanceLinkStatus,
            ["financeLinkReason"] = o.FinanceLinkReason,
            ["linkedAmount"] = o.LinkedAmount,
            ["uncoveredAmount"] = o.UncoveredAmount,
            ["submittedAmount"] = o.SubmittedAmount,
            ["otherCurrencyRecordCount"] = o.OtherCurrencyRecordCount,
            ["unapprovedRecordCount"] = o.UnapprovedRecordCount,
            ["unattributedRecordCount"] = o.UnattributedRecordCount,
            ["overReceived"] = o.OverReceived,
            ["note"] = o.Note,
        };

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义；未知金额 / 数量保持 null → 空单元格）</summary>
    private static byte[] BuildWorkbook(DynamicShipmentFinanceReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicShipmentFinanceReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows("销售订单出货财务进度", rows, columns);
    }

    /// <summary>身份 + 既有「角色 → 菜单」销售订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览销售订单出货 / 财务进度报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicShipmentFinanceReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicShipmentFinanceReportRules.RequiredMenuText}」"
                + $"（{DynamicShipmentFinanceReportRules.RequiredMenuCode}）模块授权：拒绝预览销售订单出货 / 财务进度报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}

