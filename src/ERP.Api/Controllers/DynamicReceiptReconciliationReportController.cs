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
/// 动态客户订单与收款核对报表（ERP-164）控制器：只读预览接口。
/// <list type="number">
/// <item><b>GET /api/sales-orders/dynamic-receipt-reconciliation-report</b>：返回 ERP-046 订单证据字段白名单目录（需登录 + 销售订单菜单授权）；</item>
/// <item><b>POST /api/sales-orders/dynamic-receipt-reconciliation-report</b>：按选定字段与有界筛选预览当前账号数据范围内的订单与收款证据，稳定分页。</item>
/// </list>
/// <para>复用 ERP-046 <see cref="SalesOrderReceiptReconciliation.ForQueryAsync"/> 的权威派生：客户 / 币种 / 订单日期 / 出货状态 /
/// 收款链接状态 / 收款证据状态 / 订单状态 / 关键字筛选与稳定分页全部由既有只读方法完成，本控制器只做授权、字段校验、
/// 数据范围过滤与选定列投影，不做写入。未关联收款证据只由本页范围内客户派生，绝不匹配到任何订单。</para>
/// <para>每次请求重新校验身份 / 销售订单菜单授权 / 字段 / 筛选 / 页大小 / 业务员数据范围（fail closed）；全程只读：无 Add / Update / Remove / SaveChanges，
/// 不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（预览与下载均计入）。</para>
/// </summary>
[ApiController]
[Route("api/sales-orders/dynamic-receipt-reconciliation-report")]
[Authorize]
public class DynamicReceiptReconciliationReportController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DynamicReceiptReconciliationReportController(IErpDbContext db)
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
        return Ok(ApiResponse<DynamicReceiptReconciliationReportCatalogDto>.Success(
            DynamicReceiptReconciliationReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览（只读、分页有界；单页上限 200）；可选按有限分组键返回当前页订单与未关联收款计数分组（ERP-170）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicReceiptReconciliationReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed：仅 none / customer / currency / receiptCoverageStatus / receiptEvidenceStatus；
        // 金额汇总模式 fail closed：仅 none / customerCurrency；
        // 无效取值在此直接拒绝（先于任何源读取），业务员数据范围仍先于分页在 ERP-046 源查询内生效。
        var groupBy = DynamicReceiptReconciliationReportRules.NormalizeGroupBy(request.GroupBy);
        var summaryMode = DynamicReceiptReconciliationReportRules.NormalizeSummaryMode(request.SummaryMode);

        return Ok(ApiResponse<DynamicReceiptReconciliationReportPageDto>.Success(
            await BuildPageAsync(request, groupBy, summaryMode)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-167 / ERP-174，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 销售订单菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），把当前页选定订单列与未关联收款列分别写入两个独立工作表（仅导出当前页）。
    /// <para>金额保留原币、未知金额 null 保留为空文本（绝不回落 0）、收款证据状态与截断警告显式保留；文本单元格做公式注入转义；
    /// 全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// <para>ERP-174：分组键在读取源数据之前按有限白名单校验（fail closed）；非 none 分组模式在同一工作簿追加
    /// 「订单计数分组」与「未关联收款计数分组」两个独立工作表（复用 ERP-170 同一批有界、已授权分组数据，只计数、不含金额、绝不推断匹配）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicReceiptReconciliationReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed：先于任何源读取校验（与预览同口径）；none 保留原有两表默认结构。
        var groupBy = DynamicReceiptReconciliationReportRules.NormalizeGroupBy(request.GroupBy);

        var page = await BuildPageAsync(request, groupBy);
        var bytes = BuildWorkbook(page, groupBy);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"ReceiptReconciliation_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为 PDF（ERP-169 / ERP-175，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 销售订单菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），把当前页选定订单列与未关联收款列分别渲染为两个独立 PDF 分区（仅导出当前页）。
    /// <para>金额保留原币、未知金额 null 保留为空文本（绝不回落 0）、状态与未关联收款截断警告显式保留；宽列集自动分页；
    /// 中文字体（SimHei）缺失时显式失败；全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// <para>ERP-175：分组键在读取源数据之前按有限白名单校验（fail closed，与预览 / Excel 导出同口径）；非 none 分组模式在同一 PDF 追加
    /// 「订单计数分组」与「未关联收款计数分组」两个独立分区（复用 ERP-170 同一批有界、已授权分组数据，只计数、不含金额、绝不推断匹配），
    /// none 模式保留原有两分区默认结构（不追加分组分区）。</para>
    /// </summary>
    [HttpPost("export-pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicReceiptReconciliationReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 分组键 fail closed：先于任何源读取校验（与预览 / Excel 导出同口径）；none 保留原有两分区默认结构。
        var groupBy = DynamicReceiptReconciliationReportRules.NormalizeGroupBy(request.GroupBy);

        var page = await BuildPageAsync(request, groupBy);
        var bytes = DynamicReceiptReconciliationPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"ReceiptReconciliation_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>有界、已授权的订单与收款证据预览（每次请求重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 数据范围）；可选返回当前页计数分组（ERP-170）</summary>
    private async Task<DynamicReceiptReconciliationReportPageDto> BuildPageAsync(
        DynamicReceiptReconciliationReportRequest request,
        string groupBy = DynamicReceiptReconciliationReportRules.GroupNone,
        string summaryMode = DynamicReceiptReconciliationReportRules.SummaryNone)
    {
        var userId = CurrentUserId();
        await EnsureAuthorizedAsync(userId);

        // 1) 字段 / 筛选 / 页大小校验：全部在读取 ERP-046 源数据之前完成（fail closed）
        var fieldKeys = DynamicReceiptReconciliationReportRules.NormalizeFields(request.Fields);
        var receiptFieldKeys = DynamicReceiptReconciliationReportRules.NormalizeReceiptFields(request.ReceiptFields);
        var customerId = DynamicReceiptReconciliationReportRules.NormalizeCustomerId(request.CustomerId);
        var currency = DynamicReceiptReconciliationReportRules.NormalizeCurrency(request.Currency);
        DynamicReceiptReconciliationReportRules.ValidateDateRange(request.OrderDateFrom, request.OrderDateTo);
        DynamicReceiptReconciliationReportRules.NormalizeShipmentStatus(request.ShipmentStatus);
        DynamicReceiptReconciliationReportRules.NormalizeReceiptLinkStatus(request.ReceiptLinkStatus);
        DynamicReceiptReconciliationReportRules.NormalizeReceiptStatus(request.ReceiptStatus);
        DynamicReceiptReconciliationReportRules.NormalizeOrderStatus(request.OrderStatus);
        var keyword = DynamicReceiptReconciliationReportRules.NormalizeKeyword(request.Keyword);
        DynamicReceiptReconciliationReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;

        // 2) 解析当前账号业务员数据范围（特权账号不过滤）
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        // 3) 复用 ERP-046 源查询：数据范围过滤在源查询内部完成（先于计数与分页）
        var query = new SalesOrderReceiptReconciliationQuery
        {
            CustomerId = customerId,
            Currency = currency,
            OrderDateFrom = request.OrderDateFrom,
            OrderDateTo = request.OrderDateTo,
            ShipmentStatus = request.ShipmentStatus,
            ReceiptLinkStatus = request.ReceiptLinkStatus,
            ReceiptStatus = request.ReceiptStatus,
            OrderStatus = request.OrderStatus,
            Keyword = keyword,
            Page = page,
            PageSize = request.PageSize,
        };

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(_db, query, scope);

        // 4) 选定列投影（保持请求顺序），订单证据行来自本页（已按客户 + 币种 + 订单日期 + Id 稳定排序）
        var columns = fieldKeys.Select(k => DynamicReceiptReconciliationReportRules.GetField(k)!).ToList();
        var orderRows = report.Groups.SelectMany(g => g.Orders).ToList();
        var rows = orderRows
            .Select(o => DynamicReceiptReconciliationReportRules.BuildRow(BuildSourceRow(o), fieldKeys))
            .ToList();
        var receipts = report.UnlinkedReceipts.Select(MapReceipt).ToList();
        var receiptColumns = receiptFieldKeys.Select(k => DynamicReceiptReconciliationReportRules.GetReceiptField(k)!).ToList();
        var receiptRows = report.UnlinkedReceipts
            .Select(r => DynamicReceiptReconciliationReportRules.BuildRow(BuildReceiptSourceRow(r), receiptFieldKeys))
            .ToList();

        // 5) 当前页计数分组（ERP-170）：从同一批有界、已授权的完整源行计算，订单证据与未关联收款证据各自独立，
        //    只计数、不含金额、绝不跨币种合并；unknown / pending / historical / 截断语义由规则层显式保留。
        var orderSourceRows = orderRows.Select(BuildSourceRow).ToList();
        var receiptSourceRows = report.UnlinkedReceipts.Select(BuildReceiptSourceRow).ToList();

        var orderGroups = DynamicReceiptReconciliationReportRules.BuildOrderGroups(orderSourceRows, groupBy);
        var receiptGroups = DynamicReceiptReconciliationReportRules.BuildReceiptGroups(
            receiptSourceRows, groupBy, report.PageUnlinkedReceiptTruncated);

        // 5.1) 当前页金额汇总（ERP-172，只读）：只汇总当前页（非全量）、绝不跨币种合并或换算、绝不推断收款分配 / 应收余额；
        //      订单证据与未关联收款证据各自独立，未知 null 与截断语义由规则层显式保留。
        var orderSummaries = DynamicReceiptReconciliationReportRules.BuildOrderSummaries(orderSourceRows, summaryMode);
        var receiptSummaries = DynamicReceiptReconciliationReportRules.BuildReceiptSummaries(
            receiptSourceRows, summaryMode, report.PageUnlinkedReceiptTruncated);

        return new DynamicReceiptReconciliationReportPageDto(
            columns,
            rows,
            receipts,
            receiptColumns,
            receiptRows,
            report.PageUnlinkedReceiptTruncated,
            report.Total,
            report.Page,
            report.PageSize,
            report.TotalPages,
            DynamicReceiptReconciliationReportRules.ReadOnlyText,
            DynamicReceiptReconciliationReportRules.BoundaryText,
            DynamicReceiptReconciliationReportRules.DisclaimerText,
            groupBy,
            orderGroups,
            receiptGroups,
            summaryMode,
            orderSummaries,
            receiptSummaries);
    }


    /// <summary>把单张 ERP-046 订单证据行展开为整行「字段 → 值」字典（仅白名单字段，供 <see cref="DynamicReceiptReconciliationReportRules.BuildRow"/> 投影）</summary>
    private static Dictionary<string, object?> BuildSourceRow(SalesOrderReceiptReconciliationOrderRow o)
        => new(StringComparer.Ordinal)
        {
            ["orderId"] = o.OrderId,
            ["orderNo"] = o.OrderNo,
            ["orderDate"] = o.OrderDate,
            ["status"] = o.Status,
            ["customerId"] = o.CustomerId,
            ["customerName"] = o.CustomerName,
            ["currency"] = o.Currency,
            ["amountDecimals"] = o.AmountDecimals,
            ["orderAmount"] = o.OrderAmount,
            ["recordedDepositAmount"] = o.RecordedDepositAmount,
            ["orderedQuantity"] = o.OrderedQuantity,
            ["shippedQuantity"] = o.ShippedQuantity,
            ["pendingShipmentQuantity"] = o.PendingShipmentQuantity,
            ["outstandingQuantity"] = o.OutstandingQuantity,
            ["overShippedQuantity"] = o.OverShippedQuantity,
            ["shipmentStatus"] = o.ShipmentStatus,
            ["hasApprovedShipment"] = o.HasApprovedShipment,
            ["shipmentDocumentCount"] = o.ShipmentDocumentCount,
            ["approvedShipmentCount"] = o.ApprovedShipmentCount,
            ["receiptCoverageStatus"] = o.ReceiptCoverageStatus,
            ["receiptCoverageText"] = o.ReceiptCoverageText,
            ["receiptCoverageKnown"] = o.ReceiptCoverageKnown,
            ["linkedReceiptAmount"] = o.LinkedReceiptAmount,
            ["pendingReceiptAmount"] = o.PendingReceiptAmount,
            ["uncoveredAmount"] = o.UncoveredAmount,
            ["otherCurrencyReceiptCount"] = o.OtherCurrencyReceiptCount,
            ["unapprovedReceiptCount"] = o.UnapprovedReceiptCount,
            ["unattributedReceiptCount"] = o.UnattributedReceiptCount,
            ["unattributedReceiptsTruncated"] = o.UnattributedReceiptsTruncated,
            ["overReceived"] = o.OverReceived,
            ["receiptAllocationStatus"] = o.ReceiptAllocationStatus,
            ["receiptAllocationEvidenceLabel"] = o.ReceiptAllocationEvidenceLabel,
            ["receiptAllocationCount"] = o.ReceiptAllocationCount,
            ["recordedReceiptAllocationAmount"] = o.RecordedReceiptAllocationAmount,
            ["recordedReceiptCount"] = o.RecordedReceiptCount,
            ["voidedReceiptAllocationCount"] = o.VoidedReceiptAllocationCount,
            ["invalidReceiptAllocationCount"] = o.InvalidReceiptAllocationCount,
            ["unavailableReceiptAllocationCount"] = o.UnavailableReceiptAllocationCount,
            ["receiptAllocationTruncated"] = o.ReceiptAllocationTruncated,
            ["unreferencedOrderAmount"] = o.UnreferencedOrderAmount,
            ["receiptAllocationNote"] = o.ReceiptAllocationNote,
            ["invoiceEvidenceStatus"] = o.InvoiceEvidenceStatus,
            ["invoiceEvidenceLabel"] = o.InvoiceEvidenceLabel,
            ["invoiceAllocationCount"] = o.InvoiceAllocationCount,
            ["recordedInvoicedAmount"] = o.RecordedInvoicedAmount,
            ["recordedInvoiceCount"] = o.RecordedInvoiceCount,
            ["recordedInvoiceGrossAmount"] = o.RecordedInvoiceGrossAmount,
            ["unreferencedInvoiceAmount"] = o.UnreferencedInvoiceAmount,
            ["invoiceUnreferencedOrderAmount"] = o.InvoiceUnreferencedOrderAmount,
            ["draftInvoiceAllocationCount"] = o.DraftInvoiceAllocationCount,
            ["voidedInvoiceAllocationCount"] = o.VoidedInvoiceAllocationCount,
            ["invalidInvoiceAllocationCount"] = o.InvalidInvoiceAllocationCount,
            ["unavailableInvoiceAllocationCount"] = o.UnavailableInvoiceAllocationCount,
            ["invoiceEvidenceTruncated"] = o.InvoiceEvidenceTruncated,
            ["invoiceEvidenceNote"] = o.InvoiceEvidenceNote,
            ["note"] = o.Note,
        };


    /// <summary>把单张 ERP-046 未关联收款证据行展开为整行「字段 → 值」字典（仅收款证据白名单字段，供 <see cref="DynamicReceiptReconciliationReportRules.BuildRow"/> 投影）</summary>
    private static Dictionary<string, object?> BuildReceiptSourceRow(SalesOrderReceiptReconciliationReceipt r)
        => new(StringComparer.Ordinal)
        {
            ["receiptId"] = r.ReceiptId,
            ["receiptNo"] = r.ReceiptNo,
            ["receiptDate"] = r.ReceiptDate,
            ["customerId"] = r.CustomerId,
            ["customerName"] = r.CustomerName,
            ["currency"] = r.Currency,
            ["amount"] = r.Amount,
            ["paymentMethod"] = r.PaymentMethod,
            ["status"] = r.Status,
            ["evidenceStatus"] = r.EvidenceStatus,
            ["evidenceText"] = r.EvidenceText,
            ["receiptLinkageStatus"] = r.ReceiptLinkageStatus,
            ["receiptLinkageText"] = r.ReceiptLinkageText,
            ["referenceField"] = r.ReferenceField,
            ["note"] = r.Note,
        };

    /// <summary>把 ERP-046 未关联收款证据行映射为只读预览 DTO（链接状态恒为 unlinked，原币金额原样保留）</summary>
    private static DynamicReceiptReconciliationReportReceiptDto MapReceipt(
        SalesOrderReceiptReconciliationReceipt r)
        => new(
            r.ReceiptId,
            r.ReceiptNo,
            r.ReceiptDate,
            r.CustomerId,
            r.CustomerName,
            r.Currency,
            r.Amount,
            r.PaymentMethod,
            r.Status,
            r.EvidenceStatus,
            r.EvidenceText,
            r.ReceiptLinkageStatus,
            r.ReceiptLinkageText,
            r.ReferenceField,
            r.Note);

    /// <summary>身份 + 既有「角色 → 菜单」销售订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览客户订单与收款核对报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicReceiptReconciliationReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicReceiptReconciliationReportRules.RequiredMenuText}」"
                + $"（{DynamicReceiptReconciliationReportRules.RequiredMenuCode}）模块授权：拒绝预览客户订单与收款核对报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>生成只读工作簿：默认含「订单证据」与「未关联收款证据」两个独立工作表（原币 / 状态 / 截断警告显式保留，公式注入转义，绝不合并）；
    /// 非 none 分组模式追加「订单计数分组」与「未关联收款计数分组」两个独立工作表（ERP-174，只计数、不含金额、绝不推断匹配）。</summary>
    private static byte[] BuildWorkbook(DynamicReceiptReconciliationReportPageDto page, string groupBy)
    {
        using var workbook = new XSSFWorkbook();

        AppendSheet(
            workbook,
            DynamicReceiptReconciliationReportRules.OrderSheetName,
            page.Columns.Select(c => (c.Key, c.Label)).ToList(),
            page.Rows.Select(DynamicReceiptReconciliationReportRules.BuildExportRow).ToList(),
            null);

        AppendSheet(
            workbook,
            DynamicReceiptReconciliationReportRules.ReceiptSheetName,
            page.ReceiptColumns.Select(c => (c.Key, c.Label)).ToList(),
            page.ReceiptRows.Select(DynamicReceiptReconciliationReportRules.BuildExportRow).ToList(),
            page.UnlinkedReceiptTruncated ? DynamicReceiptReconciliationReportRules.ReceiptTruncationNote : null);

        // ERP-174：仅非 none 分组模式追加两个独立计数分组工作表（复用 ERP-170 同一批有界、已授权分组数据，
        // 只计数、不含金额、绝不推断收款单与订单的匹配；不适用 / 空 / 截断状态显式保留）。
        if (groupBy != DynamicReceiptReconciliationReportRules.GroupNone)
        {
            AppendOrderGroupSheet(workbook, page.OrderGroups);
            AppendReceiptGroupSheet(workbook, page.ReceiptGroups);
        }

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    // ==================== Excel 计数分组工作表（ERP-174，只读） ====================

    /// <summary>订单计数分组工作表名称（仅非 none 分组模式追加，与订单证据工作表互不混淆）</summary>
    private const string OrderGroupSheetName = "订单计数分组";

    /// <summary>未关联收款计数分组工作表名称（仅非 none 分组模式追加，与未关联收款证据工作表互不混淆）</summary>
    private const string ReceiptGroupSheetName = "未关联收款计数分组";

    /// <summary>分组标签列标题</summary>
    private const string GroupLabelColumn = "分组标签";

    /// <summary>订单张数列标题（数值，只计数不含金额）</summary>
    private const string OrderCountColumn = "订单张数";

    /// <summary>收款张数列标题（数值，只计数不含金额）</summary>
    private const string ReceiptCountColumn = "收款张数";

    /// <summary>截断状态列标题（显式保留，绝不静默截断）</summary>
    private const string TruncatedColumn = "截断";

    /// <summary>订单侧不适用 / 当前页为空时的显式提示（绝不静默留白）</summary>
    private const string OrderGroupEmptyNote = "无订单计数分组（不适用或当前页为空）";

    /// <summary>未关联收款侧不适用 / 当前页为空时的显式提示（绝不静默留白）</summary>
    private const string ReceiptGroupEmptyNote = "无未关联收款计数分组（不适用或当前页为空）";

    /// <summary>追加「订单计数分组」工作表：每行 = 分组标签 + 订单张数（数值，只计数不含金额）；标签做公式注入转义；无数据时显式提示</summary>
    private static void AppendOrderGroupSheet(
        XSSFWorkbook workbook,
        List<DynamicReceiptReconciliationReportOrderGroupDto>? groups)
    {
        var sheet = workbook.CreateSheet(OrderGroupSheetName);
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue(GroupLabelColumn);
        header.CreateCell(1).SetCellValue(OrderCountColumn);

        if (groups is null || groups.Count == 0)
        {
            sheet.CreateRow(1).CreateCell(0).SetCellValue(OrderGroupEmptyNote);
            return;
        }

        for (var r = 0; r < groups.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            row.CreateCell(0).SetCellValue(SafeLabel(groups[r].Label));
            row.CreateCell(1).SetCellValue(groups[r].OrderCount);
        }
    }

    /// <summary>追加「未关联收款计数分组」工作表：每行 = 分组标签 + 收款张数（数值）+ 截断（是 / 否，显式保留）；标签做公式注入转义；无数据时显式提示</summary>
    private static void AppendReceiptGroupSheet(
        XSSFWorkbook workbook,
        List<DynamicReceiptReconciliationReportReceiptGroupDto>? groups)
    {
        var sheet = workbook.CreateSheet(ReceiptGroupSheetName);
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue(GroupLabelColumn);
        header.CreateCell(1).SetCellValue(ReceiptCountColumn);
        header.CreateCell(2).SetCellValue(TruncatedColumn);

        if (groups is null || groups.Count == 0)
        {
            sheet.CreateRow(1).CreateCell(0).SetCellValue(ReceiptGroupEmptyNote);
            return;
        }

        for (var r = 0; r < groups.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            row.CreateCell(0).SetCellValue(SafeLabel(groups[r].Label));
            row.CreateCell(1).SetCellValue(groups[r].ReceiptCount);
            row.CreateCell(2).SetCellValue(groups[r].Truncated ? "是" : "否");
        }
    }

    /// <summary>分组标签统一做公式注入转义（与数据单元格同口径，保持字面文本）</summary>
    private static string SafeLabel(string? label)
        => (string?)DynamicReceiptReconciliationReportRules.EscapeFormulaLeading(label) ?? string.Empty;

    /// <summary>把一个分区写为独立工作表：首行为列标题，后续行为该分区当前页的只读行（列顺序与预览一致）；截断警告作为尾行显式标注</summary>
    private static void AppendSheet(
        XSSFWorkbook workbook,
        string sheetName,
        List<(string Key, string Label)> columns,
        List<Dictionary<string, object?>> rows,
        string? note)
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

        if (!string.IsNullOrEmpty(note))
        {
            var noteRow = sheet.CreateRow(rows.Count + 1);
            var safe = (string?)DynamicReceiptReconciliationReportRules.EscapeFormulaLeading(note) ?? string.Empty;
            noteRow.CreateCell(0).SetCellValue(safe);
        }
    }

    /// <summary>按类型写入单元格（与既有 ExcelExporter 同口径的文本 / 数值语义；未知金额 null → 空文本，绝不回落 0）</summary>
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

