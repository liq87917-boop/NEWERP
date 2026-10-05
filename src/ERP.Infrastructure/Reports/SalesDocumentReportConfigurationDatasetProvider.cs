using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-318 Stage 2）的「销售单据打印快照」受控数据集适配器：把报价单 / 形式发票 PI
/// 两个既有 EF 主子表单据的持久打印快照（表头 + 有效明细）暴露为统一受控数据集。
/// <para>表头复用 <c>QuotationController.GetPrint</c> / <c>ProformaInvoiceController.GetPrint</c> 的持久快照语义
/// （主表字段一次呈现）；明细行复用 <c>Details.Where(!IsDeleted).OrderBy(SortNo).ThenBy(Id)</c> 的稳定行序，
/// 保留 null 快照与原币 / 基础单位，绝不回查档案、不重建缺失证据、不跨币种换算。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」销售单据菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）客户业务员数据范围（fail closed，先于任何源读取）。</para>
/// </summary>
public sealed class SalesDocumentReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;
    private readonly ReportSalesDocumentFamilyDefinition _family;

    public SalesDocumentReportConfigurationDatasetProvider(IErpDbContext db, string datasetKey)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _family = ReportConfigurationSalesDocumentCatalog.ResolveByDatasetKey(datasetKey);
    }

    /// <inheritdoc />
    public string DatasetKey => _family.DatasetKey;

    private string Label => _family.Title;
    private string Grain => $"{_family.Title}（表头粒度一行一张单据；明细行粒度一行一条行快照，二者通过字段区分，绝不混写）";
    private string CurrencyUnitSemantics => ReportConfigurationSalesDocumentCatalog.CurrencyUnitSemantics;
    private string RequiredMenuCode => _family.RequiredMenuCodes[0];
    private string RequiredMenuText => _family.RequiredMenuText;
    private string ReadOnlyText =>
        $"只读{_family.Title}打印数据集：仅读取既有{_family.Title}持久表头与有效明细快照，不新增 / 修改 / 删除任何记录，不重建缺失证据";
    private string BoundaryText =>
        $"口径：字段仅限{_family.Title}打印快照白名单；表头金额只在表头粒度出现，明细行只输出行金额（原币）与数量 / 单价；"
        + $"日期窗口有界（含首尾最多 {MaxDateRangeDays} 天）；受控单据选择上限 {MaxSourceDocuments} 张、明细行上限 {MaxLineRows} 行；"
        + "每次读取都重新校验菜单与客户业务员数据范围";
    private string DisclaimerText =>
        $"本预览为只读{_family.Title}打印证据：金额按原币、数量按基础单位呈现，不跨币种换算或合并；不构成报关、清关、退税或财务结论";

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxSourceDocuments = 5000;
    private const int MaxLineRows = 5000;
    private const int MaxDateRangeDays = 366;

    private static readonly IReadOnlyList<string> GroupingKeys = new[] { ReportConfigurationConstants.GroupNone };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityPaging,
        ReportConfigurationConstants.CapabilityDateRange,
    };

    private static readonly IReadOnlyList<string> UnsupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    private static readonly IReadOnlyList<string> EqOnlyOperators = new[] { ReportConfigurationConstants.OperatorEq };

    private static readonly IReadOnlyList<string> DateRangeOperators = new[]
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorGte,
        ReportConfigurationConstants.OperatorLte,
        ReportConfigurationConstants.OperatorGt,
        ReportConfigurationConstants.OperatorLt,
        ReportConfigurationConstants.OperatorBetween,
    };

    private static readonly HashSet<string> FilterableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "docNo", "docDate", "customerId", "status",
    };

    private static readonly HashSet<string> AggregatableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "exchangeRate", "totalAmount", "totalAmountCny", "depositRatio", "depositAmount",
        "quantity", "unitPrice", "amount",
    };

    private static readonly HashSet<string> AmountFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "totalAmount", "totalAmountCny", "depositAmount", "unitPrice", "amount",
    };

    private IReadOnlyList<ReportSalesDocumentColumn> DetailColumns
        => _family.Columns.Where(c => c.Grain == ReportSalesDocumentGrain.Detail).ToList();

    private static bool IsFilterable(ReportSalesDocumentColumn column)
        => FilterableFields.Contains(column.Key);

    private static bool IsAggregatable(ReportSalesDocumentColumn column)
        => AggregatableFields.Contains(column.Key);

    private static string? UnitFor(string key)
        => AmountFields.Contains(key) ? "原币金额"
            : string.Equals(key, "quantity", StringComparison.OrdinalIgnoreCase) ? "数量按基础单位"
            : null;

    private ReportConfigurationFieldDto Field(ReportSalesDocumentColumn column)
    {
        var filterable = IsFilterable(column);
        IReadOnlyList<string> operators = filterable
            && string.Equals(column.Type, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase)
                ? DateRangeOperators
                : filterable
                    ? EqOnlyOperators
                    : ReportConfigurationRules.GetOperatorsForType(column.Type);
        return new ReportConfigurationFieldDto(
            column.Key,
            column.Title,
            column.Type,
            UnitFor(column.Key),
            filterable,
            IsAggregatable(column),
            false,
            operators);
    }

    private ReportConfigurationDatasetDto BuildDataset()
    {
        var fields = _family.Columns.Select(Field).ToList();
        return new ReportConfigurationDatasetDto(
            DatasetKey,
            Label,
            Grain,
            CurrencyUnitSemantics,
            RequiredMenuCode,
            RequiredMenuText,
            fields,
            GroupingKeys,
            SupportedCapabilities,
            UnsupportedCapabilities,
            DefaultPageSize,
            MaxPageSize,
            ReadOnlyText,
            BoundaryText)
        {
            Metrics = ReportConfigurationMetricRules.BuildMetrics(fields, Grain),
            GroupingDimensions = new List<ReportConfigurationGroupingDimensionDto>(),
            Relations = new List<ReportConfigurationRelationDto>(),
            SortingExplanation = $"本数据集不支持任意排序：表头稳定按单据 Id 降序（与既有{_family.Title}列表一致），明细按 SortNo 升序再按明细 Id 升序",
        };
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthorizedAsync(userId, cancellationToken);
        }
        catch (BusinessException ex) when (ex.Code == ErrorCodes.Forbidden)
        {
            return null;
        }

        return BuildDataset();
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationPreviewDto> PreviewAsync(
        ReportConfigurationDefinition definition,
        ReportConfigurationPreviewParameters parameters,
        long? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(parameters);

        await EnsureAuthorizedAsync(userId, cancellationToken);
        ReportConfigurationRules.Validate(definition, BuildDataset());

        var (page, pageSize) = ValidatePageBounds(parameters);
        var fieldKeys = NormalizeFields(definition.Fields);
        var selected = new HashSet<string>(fieldKeys, StringComparer.OrdinalIgnoreCase);
        var isLineGrain = DetailColumns.Any(c => selected.Contains(c.Key));

        var range = ResolveDateRange(definition);
        var filters = new SalesDocumentFilters(
            ResolveLongEq(definition, "id"),
            ResolveTextEq(definition, "docNo"),
            ResolveLongEq(definition, "customerId"),
            ResolveStatus(ResolveTextEq(definition, "status")),
            range.Start,
            range.End);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        return _family.FamilyKey switch
        {
            "quotation" => await PreviewQuotationAsync(scope, filters, fieldKeys, isLineGrain, page, pageSize, columns, cancellationToken),
            "proforma-invoice" => await PreviewProformaInvoiceAsync(scope, filters, fieldKeys, isLineGrain, page, pageSize, columns, cancellationToken),
            _ => throw BusinessException.InvalidParameter($"未知的销售单据打印族：{_family.FamilyKey}"),
        };
    }

    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再预览{_family.Title}", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝预览{_family.Title}"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }

    private static (int Page, int PageSize) ValidatePageBounds(ReportConfigurationPreviewParameters parameters)
    {
        if (parameters.Page < 1)
            throw BusinessException.InvalidParameter($"页码必须从 1 开始（收到 {parameters.Page}）");
        if (parameters.PageSize < 1 || parameters.PageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {parameters.PageSize}）");
        return (parameters.Page, parameters.PageSize);
    }

    private IReadOnlyList<string> NormalizeFields(IReadOnlyList<string>? fields)
    {
        var selected = new List<string>();
        foreach (var raw in fields ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var column = _family.Columns.FirstOrDefault(c =>
                string.Equals(c.Key, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (column is null)
                continue; // 已由 ReportConfigurationRules.Validate 拒绝，此处仅做规范化

            if (!selected.Contains(column.Key, StringComparer.Ordinal))
                selected.Add(column.Key);
        }

        if (selected.Count == 0)
            selected.AddRange(_family.Columns.Select(c => c.Key));

        return selected;
    }

    private ReportConfigurationColumnDto BuildColumn(string key)
    {
        var column = _family.Columns.First(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(column.Key, column.Title, column.Type, UnitFor(column.Key));
    }

    private ReportConfigurationPreviewDto BuildPreview(
        IReadOnlyList<Dictionary<string, object?>> rows,
        int total,
        int page,
        int pageSize,
        IReadOnlyList<ReportConfigurationColumnDto> columns)
    {
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns.ToList(),
            Rows = rows.ToList(),
            Total = total,
            MatchedCount = total,
            SourceEvidenceCount = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groupings = new List<string>(),
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                ReadOnlyText, BoundaryText, DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private sealed record SalesDocumentFilters(
        long? Id,
        string? DocNo,
        long? CustomerId,
        DocumentStatus? Status,
        DateTime? StartDate,
        DateTime? EndDate);

    private static long? ResolveLongEq(ReportConfigurationDefinition definition, string fieldKey)
    {
        long? value = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), fieldKey, StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            value = ReportConfigurationDatasetTranslation.CoalesceLong(value, filter.Value, fieldKey);
        }

        return value;
    }

    private static string? ResolveTextEq(ReportConfigurationDefinition definition, string fieldKey)
    {
        string? value = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), fieldKey, StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            value = ReportConfigurationDatasetTranslation.CoalesceString(value, filter.Value, fieldKey);
        }

        return value;
    }

    private static DocumentStatus? ResolveStatus(string? value)
    {
        if (value is null)
            return null;
        if (!Enum.TryParse<DocumentStatus>(value, ignoreCase: true, out var parsed))
            throw BusinessException.InvalidParameter($"状态筛选值非法：{value}");
        return parsed;
    }

    private (DateTime? Start, DateTime? End) ResolveDateRange(ReportConfigurationDefinition definition)
    {
        DateTime? start = null;
        DateTime? end = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), "docDate", StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "docDate", ref start, ref end);
        }

        if (start is null && end is null)
            return (null, null);

        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;
        if (endDate < startDate)
            throw BusinessException.InvalidParameter($"{Label}的结束日期不能早于开始日期");
        if ((endDate - startDate).Days + 1 > MaxDateRangeDays)
            throw BusinessException.InvalidParameter($"{Label}的日期范围最大 {MaxDateRangeDays} 天（含首尾）");

        return (startDate, endDate);
    }

    private async Task<ReportConfigurationPreviewDto> PreviewQuotationAsync(
        SalespersonDataScope scope,
        SalesDocumentFilters filters,
        IReadOnlyList<string> fieldKeys,
        bool isLineGrain,
        int page,
        int pageSize,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        CancellationToken cancellationToken)
    {
        var source = SalespersonDataScopeService.FilterByCustomer(
            _db.Quotations.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);

        if (filters.Id is not null) source = source.Where(o => o.Id == filters.Id.Value);
        if (filters.DocNo is not null) source = source.Where(o => o.QuotationNo == filters.DocNo);
        if (filters.CustomerId is not null) source = source.Where(o => o.CustomerId == filters.CustomerId.Value);
        if (filters.Status is not null) source = source.Where(o => o.Status == filters.Status.Value);
        if (filters.StartDate is not null) source = source.Where(o => o.QuotationDate >= filters.StartDate);
        if (filters.EndDate is not null) source = source.Where(o => o.QuotationDate <= filters.EndDate);

        if (!isLineGrain)
        {
            var total = await source.CountAsync(cancellationToken);
            if (total > MaxSourceDocuments)
                throw new BusinessException(
                    $"{Label}的授权范围内选择超过 {MaxSourceDocuments} 张单据，请缩小筛选范围后重试",
                    ErrorCodes.RuleConflict);

            var headers = await source.OrderByDescending(o => o.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
            var rows = headers.Select(h => BuildHeaderRow(h, fieldKeys)).ToList();
            return BuildPreview(rows, total, page, pageSize, columns);
        }

        var allHeaders = await source.OrderByDescending(o => o.Id).ToListAsync(cancellationToken);
        if (allHeaders.Count > MaxSourceDocuments)
            throw new BusinessException(
                $"{Label}的授权范围内选择超过 {MaxSourceDocuments} 张单据，请缩小筛选范围后重试",
                ErrorCodes.RuleConflict);

        var headerIds = allHeaders.Select(h => h.Id).ToList();
        var items = await _db.QuotationDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && headerIds.Contains(d.QuotationId))
            .OrderBy(d => d.QuotationId).ThenBy(d => d.SortNo).ThenBy(d => d.Id)
            .Take(MaxLineRows + 1)
            .ToListAsync(cancellationToken);
        if (items.Count > MaxLineRows)
            throw new BusinessException(
                $"{Label}的明细行超过上限 {MaxLineRows} 行，请缩小单据选择范围后重试",
                ErrorCodes.RuleConflict);

        return BuildLinePreview(allHeaders, items, h => h.Id, d => d.QuotationId, fieldKeys, page, pageSize, columns);
    }

    private async Task<ReportConfigurationPreviewDto> PreviewProformaInvoiceAsync(
        SalespersonDataScope scope,
        SalesDocumentFilters filters,
        IReadOnlyList<string> fieldKeys,
        bool isLineGrain,
        int page,
        int pageSize,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        CancellationToken cancellationToken)
    {
        var source = SalespersonDataScopeService.FilterByCustomer(
            _db.ProformaInvoices.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);

        if (filters.Id is not null) source = source.Where(o => o.Id == filters.Id.Value);
        if (filters.DocNo is not null) source = source.Where(o => o.PiNo == filters.DocNo);
        if (filters.CustomerId is not null) source = source.Where(o => o.CustomerId == filters.CustomerId.Value);
        if (filters.Status is not null) source = source.Where(o => o.Status == filters.Status.Value);
        if (filters.StartDate is not null) source = source.Where(o => o.PiDate >= filters.StartDate);
        if (filters.EndDate is not null) source = source.Where(o => o.PiDate <= filters.EndDate);

        if (!isLineGrain)
        {
            var total = await source.CountAsync(cancellationToken);
            if (total > MaxSourceDocuments)
                throw new BusinessException(
                    $"{Label}的授权范围内选择超过 {MaxSourceDocuments} 张单据，请缩小筛选范围后重试",
                    ErrorCodes.RuleConflict);

            var headers = await source.OrderByDescending(o => o.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
            var rows = headers.Select(h => BuildHeaderRow(h, fieldKeys)).ToList();
            return BuildPreview(rows, total, page, pageSize, columns);
        }

        var allHeaders = await source.OrderByDescending(o => o.Id).ToListAsync(cancellationToken);
        if (allHeaders.Count > MaxSourceDocuments)
            throw new BusinessException(
                $"{Label}的授权范围内选择超过 {MaxSourceDocuments} 张单据，请缩小筛选范围后重试",
                ErrorCodes.RuleConflict);

        var headerIds = allHeaders.Select(h => h.Id).ToList();
        var items = await _db.ProformaInvoiceDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && headerIds.Contains(d.PiId))
            .OrderBy(d => d.PiId).ThenBy(d => d.SortNo).ThenBy(d => d.Id)
            .Take(MaxLineRows + 1)
            .ToListAsync(cancellationToken);
        if (items.Count > MaxLineRows)
            throw new BusinessException(
                $"{Label}的明细行超过上限 {MaxLineRows} 行，请缩小单据选择范围后重试",
                ErrorCodes.RuleConflict);

        return BuildLinePreview(allHeaders, items, h => h.Id, d => d.PiId, fieldKeys, page, pageSize, columns);
    }

    private ReportConfigurationPreviewDto BuildLinePreview<THeader, TLine>(
        IReadOnlyList<THeader> allHeaders,
        IReadOnlyList<TLine> items,
        Func<THeader, long> headerKey,
        Func<TLine, long> lineForeignKey,
        IReadOnlyList<string> fieldKeys,
        int page,
        int pageSize,
        IReadOnlyList<ReportConfigurationColumnDto> columns)
    {
        var byHeader = items.GroupBy(lineForeignKey).ToDictionary(g => g.Key, g => g.ToList());
        var lineRows = new List<Dictionary<string, object?>>();
        foreach (var header in allHeaders)
        {
            if (!byHeader.TryGetValue(headerKey(header), out var lines))
                continue;
            foreach (var line in lines)
                lineRows.Add(BuildLineRow(header!, line!, fieldKeys));
        }

        var totalLines = lineRows.Count;
        var paged = lineRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return BuildPreview(paged, totalLines, page, pageSize, columns);
    }

    private Dictionary<string, object?> BuildHeaderRow(object header, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in fieldKeys)
        {
            var column = FindColumn(key);
            if (column is null)
                continue;

            if (column.Grain == ReportSalesDocumentGrain.Detail)
            {
                row[column.Key] = null;
                continue;
            }

            row[column.Key] = NormalizeValue(header.GetType().GetProperty(column.Property)?.GetValue(header));
        }

        return row;
    }

    private Dictionary<string, object?> BuildLineRow(object header, object line, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in fieldKeys)
        {
            var column = FindColumn(key);
            if (column is null)
                continue;

            if (column.Grain == ReportSalesDocumentGrain.Header)
            {
                row[column.Key] = null;
                continue;
            }

            var source = column.Grain == ReportSalesDocumentGrain.Identity ? header : line;
            row[column.Key] = NormalizeValue(source.GetType().GetProperty(column.Property)?.GetValue(source));
        }

        return row;
    }

    private ReportSalesDocumentColumn? FindColumn(string key)
        => _family.Columns.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    private static object? NormalizeValue(object? value)
        => value is Enum e ? e.ToString() : value;
}





