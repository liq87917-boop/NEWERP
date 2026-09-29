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
/// 动态供应商采购敞口预览（ERP-148）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/supplier-purchase-exposure/report</b>：返回采购订单敞口证据字段白名单目录（需登录 + 采购订单菜单授权）；</item>
/// <item><b>POST /api/supplier-purchase-exposure/report</b>：按选定字段与有界筛选预览当前账号可见的采购订单敞口，稳定分页（单页上限 200）。</item>
/// <item><b>POST /api/supplier-purchase-exposure/report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/supplier-purchase-exposure/report/pdf</b>：导出当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分）。</item>
/// </list>
/// <para>复用 ERP-031 <see cref="SupplierPurchaseExposure.ForQueryAsync"/> 的权威派生：供应商 / 币种 / 订单日期 / 链接状态 /
/// 关键字筛选与稳定分页全部由既有只读方法完成，本控制器只做授权、字段校验与选定列投影，不做写入。</para>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/supplier-purchase-exposure/report")]
[Authorize]
public class DynamicSupplierExposureReportController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DynamicSupplierExposureReportController(IErpDbContext db)
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
        return Ok(ApiResponse<DynamicSupplierExposureReportCatalogDto>.Success(
            DynamicSupplierExposureReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界；单页上限 200）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicSupplierExposureReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicSupplierExposureReportPageDto>.Success(
            await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-150，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；金额保留原币、不做汇率换算或跨币种求和；未知结算金额与未知收货数量照实保留，
    /// 绝不推算或修复；文本单元格做公式注入转义。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicSupplierExposureReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await BuildPageAsync(request);

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SupplierPurchaseExposure_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为分页中文 PDF（ERP-151，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；选定字段、中文标签、原币（不同币种分别成行、绝不换算或合并）与链接不唯一 /
    /// 无引用的未知结算金额、未知收货数量显式保留，宽列集按可用页宽跨页拆分、行数超出按行页拆分避免裁切。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失时显式失败（不产出乱码或缺字 PDF）。</para>
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicSupplierExposureReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await BuildPageAsync(request);

        var bytes = DynamicSupplierExposurePdfExporter.Export(page);
        return File(bytes, "application/pdf",
            $"SupplierPurchaseExposure_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义）</summary>
    private static byte[] BuildWorkbook(DynamicSupplierExposureReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicSupplierExposureReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows("供应商采购敞口", rows, columns);
    }

    /// <summary>复用同一有界、已授权预览管线：授权 → 校验 → 只读查询 → 选定列投影</summary>
    private async Task<DynamicSupplierExposureReportPageDto> BuildPageAsync(DynamicSupplierExposureReportRequest request)
    {
        // 1) 身份 + 既有采购订单菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(CurrentUserId());

        // 2) 字段 / 供应商 / 币种 / 日期 / 链接状态 / 关键字 / 页大小校验（全部在源读取之前完成）
        var fieldKeys = DynamicSupplierExposureReportRules.NormalizeFields(request.Fields);
        var supplierId = DynamicSupplierExposureReportRules.NormalizeSupplierId(request.SupplierId);
        var currency = DynamicSupplierExposureReportRules.NormalizeCurrency(request.Currency);
        var linkStatus = DynamicSupplierExposureReportRules.NormalizeLinkStatus(request.LinkStatus);
        var keyword = DynamicSupplierExposureReportRules.NormalizeKeyword(request.Keyword);
        var groupBy = DynamicSupplierExposureReportRules.NormalizeGroupBy(request.GroupBy);
        DynamicSupplierExposureReportRules.ValidateDateRange(request.OrderDateFrom, request.OrderDateTo);
        DynamicSupplierExposureReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;

        // 3) 复用 ERP-031 权威派生（供应商 / 币种 / 订单日期 / 链接状态 / 关键字筛选 + 稳定分页），全程只读不写库
        var query = new SupplierPurchaseExposureQuery
        {
            SupplierId = supplierId,
            Currency = currency,
            OrderDateFrom = request.OrderDateFrom?.Date,
            OrderDateTo = request.OrderDateTo?.Date,
            LinkStatus = linkStatus,
            Keyword = keyword,
            Page = page,
            PageSize = request.PageSize,
        };
        var report = await SupplierPurchaseExposure.ForQueryAsync(_db, query);

        // 4) 投影选定列（保持请求顺序；仅返回当前页采购订单敞口证据行，未知金额 / 收货数量 / 币种照实保留）
        var columns = fieldKeys.Select(k => DynamicSupplierExposureReportRules.GetField(k)!).ToList();
        var orderRows = report.Groups.SelectMany(g => g.Orders).ToList();
        var sourceRows = orderRows.Select(BuildSourceRow).ToList();
        var rows = sourceRows
            .Select(s => DynamicSupplierExposureReportRules.BuildRow(s, fieldKeys))
            .ToList();

        // 5) 分组计数（ERP-152）：只统计当前授权预览页的采购订单张数，绝不求和任何金额或数量、绝不跨币种合并或换算
        var groups = DynamicSupplierExposureReportRules.BuildGroupCounts(sourceRows, groupBy);

        return new DynamicSupplierExposureReportPageDto(
            columns,
            rows,
            report.Total,
            report.Page,
            report.PageSize,
            report.TotalPages,
            DynamicSupplierExposureReportRules.ReadOnlyText,
            DynamicSupplierExposureReportRules.BoundaryText,
            DynamicSupplierExposureReportRules.DisclaimerText,
            groupBy,
            groups);
    }

    /// <summary>把单张 ERP-031 采购订单敞口证据行展开为整行「字段 → 值」字典（仅白名单字段，供 <see cref="DynamicSupplierExposureReportRules.BuildRow"/> 投影）</summary>
    private static Dictionary<string, object?> BuildSourceRow(SupplierPurchaseExposureOrder o)
        => new(StringComparer.Ordinal)
        {
            ["orderId"] = o.OrderId,
            ["orderNo"] = o.OrderNo,
            ["orderDate"] = o.OrderDate,
            ["status"] = o.Status,
            ["supplierId"] = o.SupplierId,
            ["supplierName"] = o.SupplierName,
            ["currency"] = o.Currency,
            ["owningSalesOrderNo"] = o.OwningSalesOrderNo,
            ["recordedSettlementProgress"] = o.RecordedSettlementProgress,
            ["orderedAmount"] = o.OrderedAmount,
            ["linkStatus"] = o.LinkStatus,
            ["linkReason"] = o.LinkReason,
            ["settledAmount"] = o.SettledAmount,
            ["outstandingAmount"] = o.OutstandingAmount,
            ["submittedAmount"] = o.SubmittedAmount,
            ["overSettled"] = o.OverSettled,
            ["receiptStatus"] = o.ReceiptStatus,
            ["orderedQuantity"] = o.OrderedQuantity,
            ["receivedQuantity"] = o.ReceivedQuantity,
            ["outstandingQuantity"] = o.OutstandingQuantity,
            ["pendingQuantity"] = o.PendingQuantity,
            ["note"] = o.Note,
        };

    /// <summary>身份 + 既有「角色 → 菜单」采购订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览供应商采购敞口报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSupplierExposureReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSupplierExposureReportRules.RequiredMenuText}」"
                + $"（{DynamicSupplierExposureReportRules.RequiredMenuCode}）模块授权：拒绝预览供应商采购敞口报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}
