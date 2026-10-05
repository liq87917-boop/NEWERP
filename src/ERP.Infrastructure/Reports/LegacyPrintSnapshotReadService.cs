using System.Globalization;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 旧打印快照统一读取实现（ERP-334 Stage 2）：把注册的基础资料打印族（7 族）与销售单据打印族
/// （报价单 / 形式发票 PI）归一化为同一个有界旧打印快照。
/// <para>基础资料直接读取对应 <c>Base*</c> 实体的未删除记录（复用 <c>BaseDataControllers</c> /
/// <c>BaseDataIoController</c> 的列白名单与 <c>OrderByDescending(Id)</c> 行序，客户族复用业务员数据范围）；</para>
/// <para>销售单据直接读取 <c>Quotation</c> / <c>ProformaInvoice</c> 主表 + 有效明细（复用
/// <c>GetPrint</c> 的 <c>Details.Where(!IsDeleted).OrderBy(SortNo).ThenBy(Id)</c> 稳定行序），
/// 表头与明细身份、null、原币 / 基础单位、原始金额与空单据均原样保留；绝不调用通用数据集适配器充当旧侧。</para>
/// <para>每次读取都按当前账号重新校验既有「角色 → 菜单」授权与客户业务员数据范围（fail closed），
/// 并遵守有界分页 / 单据与明细上限与调用方取消令牌；未知 / 删除 / 越界来源 fail closed。</para>
/// </summary>
public sealed class LegacyPrintSnapshotReadService : ILegacyPrintSnapshotReadService
{
    private readonly IErpDbContext _db;

    private const int MaxPageSize = 200;
    private const int MaxSalesDocuments = 5000;
    private const int MaxSalesLines = 5000;
    private const string SourceKeyPrefix = "print-template:";

    public LegacyPrintSnapshotReadService(IErpDbContext db)
        => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task<LegacyPrintSnapshotResult> ReadAsync(
        LegacyPrintSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.UserId is null or <= 0)
            throw new BusinessException("请先登录后再读取旧打印快照", ErrorCodes.Unauthorized);

        var familyKey = ResolveFamilyKey(request.SourceKey);

        if (ReportConfigurationMasterDataCatalog.TryResolve(familyKey, out var master))
            return await ReadMasterAsync(master, request, cancellationToken);

        if (ReportConfigurationSalesDocumentCatalog.TryResolve(familyKey, out var sales))
            return await ReadSalesAsync(sales, request, cancellationToken);

        return Result(
            request.SourceKey,
            ReportMigrationRegistryCategories.PrintTemplate,
            LegacyPrintSnapshotStatus.EnvironmentBlocked,
            ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported,
            $"旧打印快照来源 {request.SourceKey} 尚未绑定受控读取实现（environment-blocked，绝不回退为全表读取）");
    }

    private IQueryable<BaseEntity> BuildMasterSource(
        ReportMasterDataFamilyDefinition family, SalespersonDataScope scope)
    {
        switch (family.FamilyKey)
        {
            case "customer":
            {
                var customers = _db.BaseCustomers.AsNoTracking().Where(c => !c.IsDeleted);
                if (scope.AllowedCustomerIds is not null)
                {
                    var allowed = scope.AllowedCustomerIds.ToList();
                    customers = customers.Where(c => allowed.Contains(c.Id));
                }
                return customers;
            }
            case "supplier":
                return _db.BaseSuppliers.AsNoTracking().Where(s => !s.IsDeleted);
            case "employee":
                return _db.BaseEmployees.AsNoTracking().Where(s => !s.IsDeleted);
            case "expense-account":
                return _db.BaseExpenseAccounts.AsNoTracking().Where(s => !s.IsDeleted);
            case "warehouse":
                return _db.BaseWarehouses.AsNoTracking().Where(s => !s.IsDeleted);
            case "product":
                return _db.BaseProducts.AsNoTracking().Where(s => !s.IsDeleted);
            case "other-info":
                return _db.BaseOtherInfos.AsNoTracking().Where(s => !s.IsDeleted);
            default:
                throw BusinessException.InvalidParameter($"未知的基础资料打印族：{family.FamilyKey}");
        }
    }

    private static LegacyPrintSnapshotRow BuildMasterRow(
        ReportMasterDataFamilyDefinition family, BaseEntity item)
    {
        var cells = new object?[family.Columns.Count];
        for (var i = 0; i < family.Columns.Count; i++)
        {
            var column = family.Columns[i];
            cells[i] = item.GetType().GetProperty(column.Property)?.GetValue(item);
        }

        return new LegacyPrintSnapshotRow(
            new[] { item.Id.ToString(CultureInfo.InvariantCulture) },
            null,
            null,
            cells);
    }

    // ==================== 基础资料打印族 ====================

    private async Task<LegacyPrintSnapshotResult> ReadMasterAsync(
        ReportMasterDataFamilyDefinition family,
        LegacyPrintSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, request.UserId!.Value);
        if (!IsMenuAuthorized(family.RequiredMenuCodes, menuCodes))
            return Forbidden(request.SourceKey, family.Title);

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, request.UserId);
        var (page, pageSize) = NormalizePage(request);

        var source = BuildMasterSource(family, scope);
        var items = await source
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var columns = family.Columns
            .Select(c => new LegacyPrintSnapshotColumn(c.Key, c.Type, null, null))
            .ToList();

        var rows = items.Select(item => BuildMasterRow(family, item)).ToList();

        return Result(
            request.SourceKey,
            ReportMigrationRegistryCategories.PrintTemplate,
            LegacyPrintSnapshotStatus.Success,
            null,
            null,
            new LegacyPrintSnapshot(columns, rows, PermissionsOfMaster(items, scope)));
    }

    // ==================== 销售单据打印族 ====================

    private async Task<LegacyPrintSnapshotResult> ReadSalesAsync(
        ReportSalesDocumentFamilyDefinition family,
        LegacyPrintSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, request.UserId!.Value);
        if (!IsMenuAuthorized(family.RequiredMenuCodes, menuCodes))
            return Forbidden(request.SourceKey, family.Title);

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, request.UserId);
        var (page, pageSize) = NormalizePage(request);

        return family.FamilyKey switch
        {
            "quotation" => await ReadQuotationAsync(family, request, scope, page, pageSize, cancellationToken),
            "proforma-invoice" => await ReadProformaInvoiceAsync(family, request, scope, page, pageSize, cancellationToken),
            _ => Result(
                request.SourceKey,
                ReportMigrationRegistryCategories.PrintTemplate,
                LegacyPrintSnapshotStatus.EnvironmentBlocked,
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported,
                $"未知的销售单据打印族：{family.FamilyKey}"),
        };
    }


    private async Task<LegacyPrintSnapshotResult> ReadQuotationAsync(
        ReportSalesDocumentFamilyDefinition family,
        LegacyPrintSnapshotRequest request,
        SalespersonDataScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var source = SalespersonDataScopeService.FilterByCustomer(
            _db.Quotations.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        source = ApplyQuotationFilter(source, request);

        var total = await source.CountAsync(cancellationToken);
        if (total > MaxSalesDocuments)
            throw new BusinessException(
                $"{family.Title}的授权范围内选择超过 {MaxSalesDocuments} 张单据，请缩小筛选范围后重试",
                ErrorCodes.RuleConflict);

        var headers = await source
            .OrderByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var headerIds = headers.Select(h => h.Id).ToList();
        var details = await _db.QuotationDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && headerIds.Contains(d.QuotationId))
            .OrderBy(d => d.QuotationId).ThenBy(d => d.SortNo).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);
        if (details.Count > MaxSalesLines)
            throw new BusinessException(
                $"{family.Title}的明细行超过上限 {MaxSalesLines} 行，请缩小单据选择范围后重试",
                ErrorCodes.RuleConflict);

        return Result(
            request.SourceKey,
            ReportMigrationRegistryCategories.PrintTemplate,
            LegacyPrintSnapshotStatus.Success,
            null,
            null,
            BuildSalesSnapshot(family, headers, details, h => h.Id, d => d.QuotationId, scope));
    }

    private async Task<LegacyPrintSnapshotResult> ReadProformaInvoiceAsync(
        ReportSalesDocumentFamilyDefinition family,
        LegacyPrintSnapshotRequest request,
        SalespersonDataScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var source = SalespersonDataScopeService.FilterByCustomer(
            _db.ProformaInvoices.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        source = ApplyProformaInvoiceFilter(source, request);

        var total = await source.CountAsync(cancellationToken);
        if (total > MaxSalesDocuments)
            throw new BusinessException(
                $"{family.Title}的授权范围内选择超过 {MaxSalesDocuments} 张单据，请缩小筛选范围后重试",
                ErrorCodes.RuleConflict);

        var headers = await source
            .OrderByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var headerIds = headers.Select(h => h.Id).ToList();
        var details = await _db.ProformaInvoiceDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && headerIds.Contains(d.PiId))
            .OrderBy(d => d.PiId).ThenBy(d => d.SortNo).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);
        if (details.Count > MaxSalesLines)
            throw new BusinessException(
                $"{family.Title}的明细行超过上限 {MaxSalesLines} 行，请缩小单据选择范围后重试",
                ErrorCodes.RuleConflict);

        return Result(
            request.SourceKey,
            ReportMigrationRegistryCategories.PrintTemplate,
            LegacyPrintSnapshotStatus.Success,
            null,
            null,
            BuildSalesSnapshot(family, headers, details, h => h.Id, d => d.PiId, scope));
    }


    private static IQueryable<Quotation> ApplyQuotationFilter(
        IQueryable<Quotation> source, LegacyPrintSnapshotRequest request)
    {
        if (request.DocumentId is not null)
            source = source.Where(o => o.Id == request.DocumentId.Value);
        if (request.Start is not null)
            source = source.Where(o => o.QuotationDate >= request.Start.Value);
        if (request.End is not null)
            source = source.Where(o => o.QuotationDate <= request.End.Value);
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var kw = request.Keyword.Trim();
            source = source.Where(o => o.QuotationNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.InquiryNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }
        return source;
    }

    private static IQueryable<ProformaInvoice> ApplyProformaInvoiceFilter(
        IQueryable<ProformaInvoice> source, LegacyPrintSnapshotRequest request)
    {
        if (request.DocumentId is not null)
            source = source.Where(o => o.Id == request.DocumentId.Value);
        if (request.Start is not null)
            source = source.Where(o => o.PiDate >= request.Start.Value);
        if (request.End is not null)
            source = source.Where(o => o.PiDate <= request.End.Value);
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var kw = request.Keyword.Trim();
            source = source.Where(o => o.PiNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.QuotationNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }
        return source;
    }

    private static LegacyPrintSnapshot BuildSalesSnapshot<THeader, TLine>(
        ReportSalesDocumentFamilyDefinition family,
        IReadOnlyList<THeader> headers,
        IReadOnlyList<TLine> details,
        Func<THeader, long> headerKey,
        Func<TLine, long> lineForeignKey,
        SalespersonDataScope scope)
    {
        var columns = family.Columns
            .Select(c => new LegacyPrintSnapshotColumn(c.Key, c.Type, null, null))
            .ToList();

        var byHeader = details
            .GroupBy(lineForeignKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<LegacyPrintSnapshotRow>();
        foreach (var header in headers)
        {
            rows.Add(BuildHeaderRow(family, header!));

            if (byHeader.TryGetValue(headerKey(header!), out var lines))
            {
                foreach (var line in lines)
                    rows.Add(BuildLineRow(family, header!, line!));
            }
        }

        return new LegacyPrintSnapshot(columns, rows, PermissionsOfSales(headers, scope));
    }

    private static LegacyPrintSnapshotRow BuildHeaderRow(
        ReportSalesDocumentFamilyDefinition family, object header)
    {
        var cells = new object?[family.Columns.Count];
        for (var i = 0; i < family.Columns.Count; i++)
        {
            var column = family.Columns[i];
            cells[i] = column.Grain == ReportSalesDocumentGrain.Detail
                ? null
                : NormalizeValue(header.GetType().GetProperty(column.Property)?.GetValue(header));
        }

        return new LegacyPrintSnapshotRow(
            new[] { IdOf(header).ToString(CultureInfo.InvariantCulture) },
            CurrencyOf(header),
            null,
            cells);
    }

    private static LegacyPrintSnapshotRow BuildLineRow(
        ReportSalesDocumentFamilyDefinition family, object header, object line)
    {
        var cells = new object?[family.Columns.Count];
        for (var i = 0; i < family.Columns.Count; i++)
        {
            var column = family.Columns[i];
            if (column.Grain == ReportSalesDocumentGrain.Header)
            {
                cells[i] = null;
                continue;
            }

            var source = column.Grain == ReportSalesDocumentGrain.Identity ? header : line;
            cells[i] = NormalizeValue(source.GetType().GetProperty(column.Property)?.GetValue(source));
        }

        return new LegacyPrintSnapshotRow(
            new[]
            {
                IdOf(header).ToString(CultureInfo.InvariantCulture),
                IdOf(line).ToString(CultureInfo.InvariantCulture),
            },
            CurrencyOf(header),
            UnitOf(line),
            cells);
    }

    private static long IdOf(object item)
        => (long)(item.GetType().GetProperty("Id")?.GetValue(item) ?? 0L);

    private static string? CurrencyOf(object header)
    {
        var value = header.GetType().GetProperty("Currency")?.GetValue(header);
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? UnitOf(object line)
    {
        var value = line.GetType().GetProperty("Unit")?.GetValue(line);
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static object? NormalizeValue(object? value)
        => value is Enum e ? e.ToString() : value;


    // ==================== 权限 / 有界 / 结果辅助 ====================

    private static ReportMigrationParityPermissionsDto PermissionsOfMaster(
        IReadOnlyList<BaseEntity> items, SalespersonDataScope scope)
        => new(
            items.Select(e => e.Id).Distinct().OrderBy(x => x).ToList(),
            ScopeFingerprint(scope));

    private static ReportMigrationParityPermissionsDto PermissionsOfSales<T>(
        IReadOnlyList<T> headers, SalespersonDataScope scope)
        => new(
            headers.Select(h => IdOf(h!)).Distinct().OrderBy(x => x).ToList(),
            ScopeFingerprint(scope));

    private static string ScopeFingerprint(SalespersonDataScope scope)
        => scope.IsPrivileged
            ? "privileged"
            : scope.AllowedCustomerIds is null
                ? "customers:"
                : "customers:" + string.Join(",", scope.AllowedCustomerIds.OrderBy(x => x));

    private static bool IsMenuAuthorized(
        IReadOnlyList<string> requiredMenuCodes, HashSet<string> authorizedMenuCodes)
    {
        if (requiredMenuCodes.Count == 0)
            return false;

        foreach (var code in requiredMenuCodes)
        {
            if (!authorizedMenuCodes.Contains(code))
                return false;
        }

        return true;
    }

    private static string ResolveFamilyKey(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
            throw BusinessException.InvalidParameter("旧打印快照来源键不能为空");

        var key = sourceKey.Trim();
        if (key.StartsWith(SourceKeyPrefix, StringComparison.OrdinalIgnoreCase))
            key = key[SourceKeyPrefix.Length..];

        if (key.Length == 0 || key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"旧打印快照来源键非法：{sourceKey}");

        return key;
    }

    private static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':'));

    private static (int Page, int PageSize) NormalizePage(LegacyPrintSnapshotRequest request)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? MaxPageSize : Math.Min(request.PageSize, MaxPageSize);
        return (page, pageSize);
    }

    private static LegacyPrintSnapshotResult Result(
        string? sourceKey,
        string category,
        LegacyPrintSnapshotStatus status,
        int? errorCode,
        string? errorMessage,
        LegacyPrintSnapshot? snapshot = null)
        => new(sourceKey?.Trim(), category, status, errorCode, errorMessage, snapshot);

    private static LegacyPrintSnapshotResult Forbidden(string? sourceKey, string title)
        => Result(
            sourceKey,
            ReportMigrationRegistryCategories.PrintTemplate,
            LegacyPrintSnapshotStatus.Forbidden,
            ErrorCodes.Forbidden,
            $"当前账号没有「{title}」模块授权：拒绝读取旧打印快照（fail closed，不返回任何数据）");
}

