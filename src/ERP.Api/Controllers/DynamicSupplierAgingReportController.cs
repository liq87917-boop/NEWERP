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
/// 动态供应商对账与账龄报表（ERP-140）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/supplier-reconciliation-aging/report</b>：返回发票证据字段白名单目录（需登录 + 采购订单菜单授权）；</item>
/// <item><b>POST /api/supplier-reconciliation-aging/report</b>：按选定字段与有界筛选预览当前账号可见的供应商发票证据，稳定分页（单页上限 200）。</item>
/// <item><b>POST /api/supplier-reconciliation-aging/report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/supplier-reconciliation-aging/report/pdf</b>：导出当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分）。</item>
/// </list>
/// <para>复用 ERP-068 <see cref="SupplierReconciliationAging.ForQueryAsync"/> 的权威派生：供应商 / 币种 / 发票状态 /
/// 分配状态 / 开票日期 / 到期日 / as-of 筛选与稳定分页全部由既有只读方法完成，本控制器只做授权、字段校验与选定列投影，不做写入。</para>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/supplier-reconciliation-aging/report")]
[Authorize]
public class DynamicSupplierAgingReportController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DynamicSupplierAgingReportController(IErpDbContext db)
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
        return Ok(ApiResponse<DynamicSupplierAgingReportCatalogDto>.Success(
            DynamicSupplierAgingReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界；单页上限 200）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicSupplierAgingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicSupplierAgingReportPageDto>.Success(
            await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-142，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；金额保留原币、不做汇率换算或跨币种求和；未知到期日与未知 / 无效 /
    /// 无法确认分配证据照实保留，绝不推算或修复；文本单元格做公式注入转义。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicSupplierAgingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await BuildPageAsync(request);

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SupplierAging_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为分页中文 PDF（ERP-143，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小），仅导出当前页选定列；选定字段、中文标签、原币（不同币种分别成行、绝不换算或合并）与未知到期日 /
    /// 未知剩余 / 未知 / 无效 / 无法确认分配证据显式保留，宽列集按可用页宽跨页拆分、行数超出按行页拆分避免裁切。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失时显式失败（不产出乱码或缺字 PDF）。</para>
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicSupplierAgingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小
        var page = await BuildPageAsync(request);

        var bytes = DynamicSupplierAgingPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"SupplierAging_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>用 ExcelExporter 生成当前页数据工作表（选定列顺序 + 公式注入转义）</summary>
    private static byte[] BuildWorkbook(DynamicSupplierAgingReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicSupplierAgingReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows("供应商对账与账龄", rows, columns);
    }

    /// <summary>复用同一有界、已授权预览管线：授权 → 校验 → 只读查询 → 选定列投影</summary>
    private async Task<DynamicSupplierAgingReportPageDto> BuildPageAsync(DynamicSupplierAgingReportRequest request)
    {
        // 1) 身份 + 既有采购订单菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(CurrentUserId());

        // 2) 字段 / 供应商 / 币种 / 发票状态 / 分配状态 / 日期区间 / as-of / 关键字 / 页大小校验（全部在源读取之前完成）
        var fieldKeys = DynamicSupplierAgingReportRules.NormalizeFields(request.Fields);
        var supplierId = DynamicSupplierAgingReportRules.NormalizeSupplierId(request.SupplierId);
        var currency = DynamicSupplierAgingReportRules.NormalizeCurrency(request.Currency);
        var invoiceStatus = DynamicSupplierAgingReportRules.NormalizeInvoiceStatus(request.InvoiceStatus);
        var allocationState = DynamicSupplierAgingReportRules.NormalizeAllocationState(request.AllocationState);
        var keyword = DynamicSupplierAgingReportRules.NormalizeKeyword(request.Keyword);
        DynamicSupplierAgingReportRules.ValidateDateRange(request.InvoiceDateFrom, request.InvoiceDateTo, "开票日期");
        DynamicSupplierAgingReportRules.ValidateDateRange(request.DueDateFrom, request.DueDateTo, "到期日");
        DynamicSupplierAgingReportRules.ValidateAsOfDate(request.AsOfDate);
        DynamicSupplierAgingReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;

        // 3) 复用 ERP-068 权威派生（供应商 / 币种 / 状态 / 日期 / as-of 筛选 + 稳定分页），全程只读不写库
        var query = new SupplierReconciliationAgingQuery
        {
            SupplierId = supplierId,
            Currency = currency,
            InvoiceStatus = invoiceStatus,
            AllocationState = allocationState,
            Keyword = keyword,
            InvoiceDateFrom = request.InvoiceDateFrom?.Date,
            InvoiceDateTo = request.InvoiceDateTo?.Date,
            DueDateFrom = request.DueDateFrom?.Date,
            DueDateTo = request.DueDateTo?.Date,
            AsOfDate = request.AsOfDate?.Date,
            Page = page,
            PageSize = request.PageSize,
        };
        var report = await SupplierReconciliationAging.ForQueryAsync(_db, query);

        // 4) 投影选定列（保持请求顺序；仅返回当前页发票证据行，未知到期日 / 币种 / 无效证据照实保留）
        var columns = fieldKeys.Select(k => DynamicSupplierAgingReportRules.GetField(k)!).ToList();
        var invoiceRows = report.Groups.SelectMany(g => g.Invoices).ToList();
        var rows = invoiceRows
            .Select(i => DynamicSupplierAgingReportRules.BuildRow(BuildSourceRow(i), fieldKeys))
            .ToList();

        return new DynamicSupplierAgingReportPageDto(
            columns,
            rows,
            report.Total,
            report.Page,
            report.PageSize,
            report.TotalPages,
            DynamicSupplierAgingReportRules.ReadOnlyText,
            DynamicSupplierAgingReportRules.BoundaryText,
            DynamicSupplierAgingReportRules.DisclaimerText);
    }

    /// <summary>把单张 ERP-068 发票证据行展开为整行「字段 → 值」字典（仅白名单字段，供 <see cref="DynamicSupplierAgingReportRules.BuildRow"/> 投影）</summary>
    private static Dictionary<string, object?> BuildSourceRow(SupplierReconciliationAgingInvoice i)
        => new(StringComparer.Ordinal)
        {
            ["invoiceId"] = i.InvoiceId,
            ["invoiceType"] = i.InvoiceType,
            ["invoiceTypeText"] = i.InvoiceTypeText,
            ["invoiceCode"] = i.InvoiceCode,
            ["invoiceNumber"] = i.InvoiceNumber,
            ["invoiceIdentityText"] = i.InvoiceIdentityText,
            ["invoiceDate"] = i.InvoiceDate,
            ["supplierId"] = i.SupplierId,
            ["supplierCode"] = i.SupplierCode,
            ["supplierName"] = i.SupplierName,
            ["supplierAvailable"] = i.SupplierAvailable,
            ["supplierAvailabilityText"] = i.SupplierAvailabilityText,
            ["currency"] = i.Currency,
            ["amountDecimals"] = i.AmountDecimals,
            ["netAmount"] = i.NetAmount,
            ["taxAmount"] = i.TaxAmount,
            ["grossAmount"] = i.GrossAmount,
            ["invoiceStatus"] = i.InvoiceStatus,
            ["invoiceStatusText"] = i.InvoiceStatusText,
            ["isActiveEvidence"] = i.IsActiveEvidence,
            ["isDraft"] = i.IsDraft,
            ["isVoided"] = i.IsVoided,
            ["dueDate"] = i.DueDate,
            ["dueDateKnown"] = i.DueDateKnown,
            ["dueDateText"] = i.DueDateText,
            ["paymentTerms"] = i.PaymentTerms,
            ["paymentTermsText"] = i.PaymentTermsText,
            ["agingBucket"] = i.AgingBucket,
            ["agingBucketText"] = i.AgingBucketText,
            ["overdueDays"] = i.OverdueDays,
            ["agingText"] = i.AgingText,
            ["allocationState"] = i.AllocationState,
            ["allocationStateText"] = i.AllocationStateText,
            ["activeAllocatedAmount"] = i.ActiveAllocatedAmount,
            ["activeAllocationCount"] = i.ActiveAllocationCount,
            ["activePaymentCount"] = i.ActivePaymentCount,
            ["remainingAmount"] = i.RemainingAmount,
            ["remainingState"] = i.RemainingState,
            ["remainingStateText"] = i.RemainingStateText,
            ["voidedAllocationCount"] = i.VoidedAllocationCount,
            ["voidedAllocationAmount"] = i.VoidedAllocationAmount,
            ["invoiceInactiveAllocationCount"] = i.InvoiceInactiveAllocationCount,
            ["invoiceInactiveAllocationAmount"] = i.InvoiceInactiveAllocationAmount,
            ["invalidAllocationCount"] = i.InvalidAllocationCount,
            ["invalidAllocationAmount"] = i.InvalidAllocationAmount,
            ["unavailableAllocationCount"] = i.UnavailableAllocationCount,
            ["unavailableAllocationAmount"] = i.UnavailableAllocationAmount,
            ["hasAllocationHistory"] = i.HasAllocationHistory,
            ["hasInvalidOrUnavailableEvidence"] = i.HasInvalidOrUnavailableEvidence,
            ["historicalEvidenceText"] = i.HistoricalEvidenceText,
            ["note"] = i.Note,
        };

    /// <summary>身份 + 既有「角色 → 菜单」采购订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览供应商对账与账龄报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSupplierAgingReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSupplierAgingReportRules.RequiredMenuText}」"
                + $"（{DynamicSupplierAgingReportRules.RequiredMenuCode}）模块授权：拒绝预览供应商对账与账龄报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}

