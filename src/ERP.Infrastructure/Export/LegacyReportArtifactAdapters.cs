using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 旧报表实际产物适配器的有界能力状态（有限、稳定）。
/// </summary>
public enum LegacyReportArtifactCapabilityStatus
{
    /// <summary>已绑定既有规范导出器（可产出真实旧 Excel / PDF 字节）。</summary>
    Supported = 0,

    /// <summary>明确阻塞：无规范旧导出器 / 仅为浏览器打印来源（fail closed，绝不回退为通用导出器）。</summary>
    Blocked = 1,
}

/// <summary>
/// 单个旧报表键的有界产物能力（Excel / PDF 支持标记 + 阻塞原因）。
/// </summary>
public sealed record LegacyReportArtifactCapability(
    string LegacyKey,
    bool ExcelSupported,
    bool PdfSupported,
    LegacyReportArtifactCapabilityStatus Status,
    string? BlockReason);

/// <summary>
/// 旧报表实际产物适配器的有限可执行目录（ERP-337 Stage 2）：把共享迁移目录内声明 Excel/PDF 兼容的旧报表键，
/// 精确映射为「已绑定规范导出器（supported）」或「打印 / 浏览器来源（blocked）」，绝不猜测、绝不回退为通用导出器。
/// <para>全部为服务端编译期常量，绝不来自客户端；未知键在打开任何查询之前拒绝。</para>
/// </summary>
public static class LegacyReportArtifactCatalog
{
    private const string DynamicBlockReason = "无共享查询接缝/规范旧导出器（不在有限迁移引擎内）";
    private const string PrintBlockReason = "浏览器打印来源（无规范 PDF 导出器）";

    private static readonly string[] BlockedDynamicKeys =
    {
        "dynamic:agency-service-fee-monthly",
        "dynamic:container-stats",
        "dynamic:customer-shipment",
        "dynamic:follow-up-due",
        "dynamic:inventory-aging",
        "dynamic:inventory-movement",
        "dynamic:order-profit",
        "dynamic:product-sales-ranking",
        "dynamic:quotation-conversion",
        "dynamic:receipt-reconciliation",
        "dynamic:sales-commission",
        "dynamic:salesman-output",
        "dynamic:shipment-finance",
        "dynamic:supplier-aging",
        "dynamic:supplier-exposure",
    };

    /// <summary>有限能力矩阵（顺序稳定：动态查询 / 导出族 → 旧单据导出 → 报告包 → 单证 → 打印模板族）。</summary>
    public static readonly IReadOnlyList<LegacyReportArtifactCapability> Capabilities = BuildCapabilities();

    private static readonly Dictionary<string, LegacyReportArtifactCapability> ByKey =
        new(StringComparer.OrdinalIgnoreCase);

    static LegacyReportArtifactCatalog()
    {
        foreach (var capability in Capabilities)
            ByKey[capability.LegacyKey] = capability;
    }

    /// <summary>按旧报表键解析能力（未知 / 空白 / 畸形返回 false，不抛异常；在打开任何查询之前调用）。</summary>
    public static bool TryResolve(string? legacyKey, out LegacyReportArtifactCapability capability)
    {
        capability = null!;
        if (string.IsNullOrWhiteSpace(legacyKey))
            return false;

        var key = legacyKey.Trim();
        if (key.Length > 128 || ContainsUnsafeKey(key))
            return false;

        return ByKey.TryGetValue(key, out capability!);
    }

    private static List<LegacyReportArtifactCapability> BuildCapabilities()
    {
        var list = new List<LegacyReportArtifactCapability>();

        // 动态查询 / 导出族（既有共享查询接缝 + Excel/PDF 规范导出器）。
        list.Add(Supported("dynamic:sales-order", excel: true, pdf: true));
        list.Add(Supported("dynamic:receivable", excel: true, pdf: true));
        list.Add(Supported("dynamic:purchase-order", excel: true, pdf: true));

        // 其余动态报表：无共享查询接缝（不在有限迁移引擎内），精确阻塞。
        foreach (var key in BlockedDynamicKeys)
            list.Add(Blocked(key, excel: true, pdf: true, DynamicBlockReason));

        // 16 个旧单据导出族：Excel 规范导出（BillProcController.Export）。
        foreach (var family in LegacyBillExportCatalog.Families)
            list.Add(Supported("export:bill-proc:" + family.FamilyKey, excel: true, pdf: false));

        // 客户报告包：Excel + PDF。
        list.Add(Supported("packet:customer-report-packet", excel: true, pdf: true));

        // 单证：台账导出（Excel）与打印数据（浏览器打印 → 阻塞）。
        list.Add(Supported("document:trade-document-export-excel", excel: true, pdf: false));
        list.Add(Blocked("document:trade-document-print", excel: false, pdf: true, PrintBlockReason));

        // 打印模板族（16 旧单据 + 7 基础资料 + 2 销售单据 + 单证中心）：浏览器打印 → 阻塞。
        foreach (var family in ReportPrintTemplateFamilies.Families)
            list.Add(Blocked("print-template:" + family.FamilyKey, excel: false, pdf: true, PrintBlockReason));

        return list;
    }

    private static LegacyReportArtifactCapability Supported(string key, bool excel, bool pdf)
        => new(key, excel, pdf, LegacyReportArtifactCapabilityStatus.Supported, null);

    private static LegacyReportArtifactCapability Blocked(string key, bool excel, bool pdf, string reason)
        => new(key, excel, pdf, LegacyReportArtifactCapabilityStatus.Blocked, reason);

    private static bool ContainsUnsafeKey(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':'));
}

/// <summary>
/// 单个旧报表键的有界产物适配器（只读、fail closed）：读取既有受控数据并调用既有规范导出器产出真实旧字节。
/// </summary>
internal interface ILegacyReportArtifactAdapter
{
    Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken);
}

/// <summary>适配器共享的有界辅助。</summary>
internal static class LegacyReportArtifactAdapterHelpers
{
    internal const int MaxPageSize = 200;
    internal const int MaxArtifactBytes = 32 * 1024 * 1024;

    internal static int PageOf(LegacyReportSourceRequest request)
        => request.Page < 1 ? 1 : request.Page;

    internal static int ClampPageSize(int pageSize, int max)
        => pageSize < 1 ? max : Math.Min(pageSize, max);

    internal static LegacyReportArtifactBytesDto? Validate(byte[]? excelBytes, byte[]? pdfBytes)
    {
        if (excelBytes is { Length: 0 } || pdfBytes is { Length: 0 })
            return null;
        if (excelBytes is { Length: > MaxArtifactBytes } || pdfBytes is { Length: > MaxArtifactBytes })
            return null;
        return new LegacyReportArtifactBytesDto(excelBytes, pdfBytes);
    }

    internal static bool IsMenuAuthorized(IReadOnlyList<string> required, HashSet<string> authorized)
    {
        if (required.Count == 0)
            return false;

        foreach (var code in required)
        {
            if (!authorized.Contains(code))
                return false;
        }

        return true;
    }
}

/// <summary>动态销售订单报表产物适配器：复用既有查询 + ExcelExporter + DynamicSalesOrderPdfExporter。</summary>
internal sealed class DynamicSalesOrderArtifactAdapter : ILegacyReportArtifactAdapter
{
    private readonly IDynamicSalesOrderReportQuery _query;

    public DynamicSalesOrderArtifactAdapter(IDynamicSalesOrderReportQuery query)
        => _query = query ?? throw new ArgumentNullException(nameof(query));

    public async Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = await _query.PreviewAsync(new DynamicSalesOrderReportRequest
        {
            StartDate = request.Start,
            EndDate = request.End,
            Page = LegacyReportArtifactAdapterHelpers.PageOf(request),
            PageSize = LegacyReportArtifactAdapterHelpers.ClampPageSize(
                request.PageSize, LegacyReportArtifactAdapterHelpers.MaxPageSize),
        }, request.UserId, cancellationToken);

        var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(null);
        var groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicSalesOrderReportRules.BuildExportRow).ToList();

        return LegacyReportArtifactAdapterHelpers.Validate(
            ExcelExporter.ExportRows("销售订单", rows, columns),
            DynamicSalesOrderPdfExporter.Export(page, groups, groupBy));
    }
}

/// <summary>动态客户应收账款证据报表产物适配器：复用既有查询 + ExcelExporter + DynamicReceivablePdfExporter。</summary>
internal sealed class DynamicReceivableArtifactAdapter : ILegacyReportArtifactAdapter
{
    private readonly IDynamicReceivableReportQuery _query;

    public DynamicReceivableArtifactAdapter(IDynamicReceivableReportQuery query)
        => _query = query ?? throw new ArgumentNullException(nameof(query));

    public async Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = await _query.PreviewAsync(new DynamicReceivableReportRequest
        {
            StartDate = request.Start,
            EndDate = request.End,
            Page = LegacyReportArtifactAdapterHelpers.PageOf(request),
            PageSize = LegacyReportArtifactAdapterHelpers.ClampPageSize(request.PageSize, 100),
        }, request.UserId, cancellationToken);

        var groupBy = DynamicReceivableReportRules.NormalizeGroupBy(null);
        var groups = DynamicReceivableReportRules.BuildGroupSubtotals(page.Rows, groupBy);
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicReceivableReportRules.BuildExportRow).ToList();

        return LegacyReportArtifactAdapterHelpers.Validate(
            ExcelExporter.ExportRows("应收证据", rows, columns),
            DynamicReceivablePdfExporter.Export(page, groups, groupBy));
    }
}

/// <summary>动态采购订单报表产物适配器：复用既有查询 + ExcelExporter + DynamicPurchaseOrderPdfExporter。</summary>
internal sealed class DynamicPurchaseOrderArtifactAdapter : ILegacyReportArtifactAdapter
{
    private readonly IDynamicPurchaseOrderReportQuery _query;

    public DynamicPurchaseOrderArtifactAdapter(IDynamicPurchaseOrderReportQuery query)
        => _query = query ?? throw new ArgumentNullException(nameof(query));

    public async Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = await _query.PreviewAsync(new DynamicPurchaseOrderReportRequest
        {
            StartDate = request.Start,
            EndDate = request.End,
            Page = LegacyReportArtifactAdapterHelpers.PageOf(request),
            PageSize = LegacyReportArtifactAdapterHelpers.ClampPageSize(request.PageSize, 100),
        }, request.UserId, cancellationToken);

        var groupBy = DynamicPurchaseOrderReportRules.NormalizeGroupBy(null);
        var groups = DynamicPurchaseOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicPurchaseOrderReportRules.BuildExportRow).ToList();

        return LegacyReportArtifactAdapterHelpers.Validate(
            ExcelExporter.ExportRows("采购订单", rows, columns),
            DynamicPurchaseOrderPdfExporter.Export(page, groups, groupBy));
    }
}

/// <summary>
/// 旧单据导出族产物适配器（16 族）：复用既有受控读取 + ExcelExporter 产出真实旧 Excel 字节；菜单授权每次重检。
/// </summary>
internal sealed class BillExportArtifactAdapter : ILegacyReportArtifactAdapter
{
    private readonly LegacyBillExportFamilyDefinition _family;
    private readonly ILegacyBillExportReadService _reader;
    private readonly IErpDbContext _db;

    public BillExportArtifactAdapter(
        LegacyBillExportFamilyDefinition family,
        ILegacyBillExportReadService reader,
        IErpDbContext db)
    {
        _family = family ?? throw new ArgumentNullException(nameof(family));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.UserId is null or <= 0)
            return null;

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, request.UserId.Value);
        if (!LegacyReportArtifactAdapterHelpers.IsMenuAuthorized(_family.RequiredMenuCodes, menuCodes))
            return null;

        var page = await _reader.ReadPageAsync(new LegacyBillExportQuery
        {
            FamilyKey = _family.FamilyKey,
            Page = LegacyReportArtifactAdapterHelpers.PageOf(request),
            PageSize = LegacyReportArtifactAdapterHelpers.ClampPageSize(
                request.PageSize, LegacyReportArtifactAdapterHelpers.MaxPageSize),
            StartDate = request.Start,
            EndDate = request.End,
            Keyword = request.Keyword,
        }, cancellationToken);

        var columns = page.Columns.Select(c => (c.Key, c.Title)).ToList();
        return LegacyReportArtifactAdapterHelpers.Validate(
            ExcelExporter.ExportRows(_family.TableName, page.Rows, columns), null);
    }
}

/// <summary>客户报告包产物适配器：复用既有双查询 + 双工作表 Excel + CustomerReportPacketPdfExporter。</summary>
internal sealed class CustomerReportPacketArtifactAdapter : ILegacyReportArtifactAdapter
{
    private readonly IDynamicSalesOrderReportQuery _orderQuery;
    private readonly IDynamicReceivableReportQuery _receivableQuery;

    public CustomerReportPacketArtifactAdapter(
        IDynamicSalesOrderReportQuery orderQuery,
        IDynamicReceivableReportQuery receivableQuery)
    {
        _orderQuery = orderQuery ?? throw new ArgumentNullException(nameof(orderQuery));
        _receivableQuery = receivableQuery ?? throw new ArgumentNullException(nameof(receivableQuery));
    }

    public async Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var customerId = request.CustomerId ?? 0;
        CustomerReportPacketRules.ValidateCustomerId(customerId);
        CustomerReportPacketRules.ValidateDateRange(request.Start, request.End);

        var page = LegacyReportArtifactAdapterHelpers.PageOf(request);
        var pageSize = LegacyReportArtifactAdapterHelpers.ClampPageSize(
            request.PageSize, CustomerReportPacketRules.MaxPageSize);

        var salesOrders = await _orderQuery.PreviewAsync(new DynamicSalesOrderReportRequest
        {
            CustomerId = customerId,
            StartDate = request.Start,
            EndDate = request.End,
            Page = page,
            PageSize = pageSize,
        }, request.UserId, cancellationToken);

        var receivable = await _receivableQuery.PreviewAsync(new DynamicReceivableReportRequest
        {
            CustomerId = customerId,
            StartDate = request.Start,
            EndDate = request.End,
            Page = page,
            PageSize = pageSize,
        }, request.UserId, cancellationToken);

        var packet = CustomerReportPacketRules.BuildPacket(customerId, salesOrders, receivable);

        return LegacyReportArtifactAdapterHelpers.Validate(
            BuildWorkbook(packet),
            CustomerReportPacketPdfExporter.Export(packet));
    }

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

/// <summary>出口单证台账导出产物适配器：复用既有单证读取 + ExcelExporter（默认表头导出）。</summary>
internal sealed class TradeDocumentExportArtifactAdapter : ILegacyReportArtifactAdapter
{
    private const string RequiredMenuCode = "doc-center";

    private static readonly List<(string Key, string Title)> ExportColumns = new()
    {
        ("DocNo", "单证编号"), ("DocType", "单证类型"), ("IssueDate", "出具/签发日期"),
        ("SalesOrderNo", "关联销售订单号"), ("RefNo", "关联柜号/订舱号"), ("DeclareNo", "关联报关单号"),
        ("CustomerName", "客户名称"), ("Amount", "单证金额"), ("Currency", "币种"),
        ("DeparturePort", "起运港"), ("DestinationPort", "目的港"),
        ("IssuedBy", "制作人/出证机构"), ("Copies", "份数"), ("Status", "状态"),
        ("FileNote", "附件说明"), ("Remark", "备注"),
    };

    private readonly IErpDbContext _db;

    public TradeDocumentExportArtifactAdapter(IErpDbContext db)
        => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<LegacyReportArtifactBytesDto?> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.UserId is null or <= 0)
            return null;

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, request.UserId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            return null;

        var documentId = request.DocumentId ?? 0;
        if (documentId <= 0)
            return null;

        var document = await _db.TradeDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && !d.IsDeleted, cancellationToken);
        if (document is null)
            return null;

        var rows = new List<Dictionary<string, object?>> { HeaderRow(document) };
        return LegacyReportArtifactAdapterHelpers.Validate(
            ExcelExporter.ExportRows("TradeDocuments", rows, ExportColumns), null);
    }

    private static Dictionary<string, object?> HeaderRow(TradeDocument document)
        => new(StringComparer.Ordinal)
        {
            ["DocNo"] = document.DocNo,
            ["DocType"] = document.DocType,
            ["IssueDate"] = document.IssueDate,
            ["SalesOrderNo"] = document.SalesOrderNo,
            ["RefNo"] = document.RefNo,
            ["DeclareNo"] = document.DeclareNo,
            ["CustomerName"] = document.CustomerName,
            ["Amount"] = document.Amount,
            ["Currency"] = document.Currency,
            ["DeparturePort"] = document.DeparturePort,
            ["DestinationPort"] = document.DestinationPort,
            ["IssuedBy"] = document.IssuedBy,
            ["Copies"] = document.Copies,
            ["Status"] = document.Status,
            ["FileNote"] = document.FileNote,
            ["Remark"] = document.Remark,
        };
}

/// <summary>有限共享适配器登记册：把受支持旧报表键映射到既有规范导出器适配器（只读）。</summary>
internal sealed class LegacyReportArtifactAdapterRegistry
{
    private readonly Dictionary<string, ILegacyReportArtifactAdapter> _adapters;

    private LegacyReportArtifactAdapterRegistry(Dictionary<string, ILegacyReportArtifactAdapter> adapters)
        => _adapters = adapters;

    public bool TryGetAdapter(string legacyKey, out ILegacyReportArtifactAdapter adapter)
        => _adapters.TryGetValue(legacyKey, out adapter!);

    internal static LegacyReportArtifactAdapterRegistry Create(
        IDynamicSalesOrderReportQuery salesOrderQuery,
        IDynamicReceivableReportQuery? receivableQuery,
        IDynamicPurchaseOrderReportQuery? purchaseOrderQuery,
        ILegacyBillExportReadService? billExportReader,
        IErpDbContext? db)
    {
        var adapters = new Dictionary<string, ILegacyReportArtifactAdapter>(StringComparer.OrdinalIgnoreCase);

        if (salesOrderQuery is not null)
            adapters["dynamic:sales-order"] = new DynamicSalesOrderArtifactAdapter(salesOrderQuery);

        if (receivableQuery is not null)
        {
            adapters["dynamic:receivable"] = new DynamicReceivableArtifactAdapter(receivableQuery);
            if (salesOrderQuery is not null)
                adapters["packet:customer-report-packet"] =
                    new CustomerReportPacketArtifactAdapter(salesOrderQuery, receivableQuery);
        }

        if (purchaseOrderQuery is not null)
            adapters["dynamic:purchase-order"] = new DynamicPurchaseOrderArtifactAdapter(purchaseOrderQuery);

        if (billExportReader is not null && db is not null)
        {
            foreach (var family in LegacyBillExportCatalog.Families)
            {
                adapters["export:bill-proc:" + family.FamilyKey] =
                    new BillExportArtifactAdapter(family, billExportReader, db);
            }
        }

        if (db is not null)
            adapters["document:trade-document-export-excel"] = new TradeDocumentExportArtifactAdapter(db);

        return new LegacyReportArtifactAdapterRegistry(adapters);
    }
}
