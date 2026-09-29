using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态供应商采购敞口预览（ERP-148）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/supplier-purchase-exposure/report</b>：返回采购订单敞口证据字段白名单目录（需登录 + 采购订单菜单授权）；</item>
/// <item><b>POST /api/supplier-purchase-exposure/report</b>：按选定字段与有界筛选预览当前账号可见的采购订单敞口，稳定分页（单页上限 200）。</item>
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

        return new DynamicSupplierExposureReportPageDto(
            columns,
            rows,
            report.Total,
            report.Page,
            report.PageSize,
            report.TotalPages,
            DynamicSupplierExposureReportRules.ReadOnlyText,
            DynamicSupplierExposureReportRules.BoundaryText,
            DynamicSupplierExposureReportRules.DisclaimerText);
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
