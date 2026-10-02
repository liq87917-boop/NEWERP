using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using System.Globalization;
using System.Text.Json;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的数据集适配器：把两个既有数据集（销售订单 ERP-112 /
/// 客户应收账款证据 ERP-117）的有限字段白名单与能力 / 粒度 / 币种口径暴露为统一目录。
/// <para>每次调用都复用既有 <c>GetCatalogAsync</c> 的菜单授权与数据范围校验（fail closed）；
/// 目录是有限静态元数据，绝不扩大到数据库全量元数据发现。新增数据集 = 新增一个适配器并注册，
/// 不改动目录聚合与控制器。</para>
/// </summary>
public sealed class SalesOrderReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IDynamicSalesOrderReportQuery _query;

    public SalesOrderReportConfigurationDatasetProvider(IDynamicSalesOrderReportQuery query)
    {
        _query = query;
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetSalesOrder;

    private const string Label = "销售订单";
    private const string Grain = "销售订单（一行一条销售订单）";
    private const string CurrencyUnitSemantics = "金额按订单原币呈现，不跨币种换算或合并";
    private const string RequiredMenuCode = DynamicSalesOrderReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicSalesOrderReportRules.RequiredMenuText;
    private const int DefaultPageSize = DynamicSalesOrderReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicSalesOrderReportRules.MaxPageSize;
    private const string ReadOnlyText = DynamicSalesOrderReportRules.ReadOnlyText;
    private const string BoundaryText = DynamicSalesOrderReportRules.BoundaryText;

    private static readonly IReadOnlyList<string> GroupingKeys = new[]
    {
        ReportConfigurationConstants.GroupNone,
        ReportConfigurationConstants.GroupCustomer,
        ReportConfigurationConstants.GroupMonth,
    };

    private static readonly IReadOnlyList<ReportConfigurationGroupingDimensionDto> GroupingDimensions = new[]
    {
        new ReportConfigurationGroupingDimensionDto(
            ReportConfigurationConstants.GroupCustomer,
            "按客户分组",
            "customerId",
            ReportConfigurationConstants.TypeNumber,
            ReportConfigurationConstants.GroupingSemanticsIdentity),
        new ReportConfigurationGroupingDimensionDto(
            ReportConfigurationConstants.GroupMonth,
            "按月份分组",
            "orderDate",
            ReportConfigurationConstants.TypeDate,
            ReportConfigurationConstants.GroupingSemanticsCalendarMonth),
    };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityGrouping,
        ReportConfigurationConstants.CapabilityDateRange,
        ReportConfigurationConstants.CapabilityPaging,
        ReportConfigurationConstants.CapabilityComputedColumns,
    };

    private static readonly IReadOnlyList<string> UnsupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    private static readonly Dictionary<string, string> CurrencyUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["currency"] = "币种代码",
        ["totalAmount"] = "原币金额",
        ["depositAmount"] = "原币金额",
        ["depositRatio"] = "%",
        ["commissionRatio"] = "%",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "customerId", "salesmanId", "portId",
    };

    private static readonly HashSet<string> SortableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "orderDate", "customerId",
    };

    private const string SortingExplanation =
        "仅订单 Id / 订单日期 / 客户 Id（原生持久化键）可排序；金额 / 比例 / 派生 / 关系字段不可排序；并列时按订单 Id 升序稳定分页";

    private static (bool Sortable, string? Reason) SortabilityOf(string key)
    {
        if (SortableFields.Contains(key))
            return (true, null);
        if (CurrencyUnits.ContainsKey(key))
            return (false, "金额 / 比例 / 币种字段不可排序（非原生可排序键）");
        return (false, "仅订单 Id / 订单日期 / 客户 Id 可排序");
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        DynamicSalesOrderReportCatalogDto catalog;
        try
        {
            catalog = await _query.GetCatalogAsync(userId, cancellationToken);
        }
        catch (BusinessException ex) when (ex.Code == ErrorCodes.Forbidden)
        {
            // 无销售订单菜单授权 → 该数据集不暴露（fail closed 由目录层收敛，不整体失败）
            return null;
        }

        return BuildDataset(catalog);
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

        var compositeGroupings = ReportConfigurationGroupingRules.NormalizeGroupingKeys(parameters.Groupings);
        var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(parameters.GroupBy);
        IReadOnlyList<string> pivotDimensionKeys = definition.Pivot is null
            ? Array.Empty<string>()
            : CompositeFieldKeys(new[] { definition.Pivot.RowDimension, definition.Pivot.ColumnDimension });
        List<string>? compositeFieldKeys = null;
        if (compositeGroupings.Count >= 2)
            compositeFieldKeys = CompositeFieldKeys(compositeGroupings).Concat(new[] { "currency", "totalAmount" }).ToList();
        else if (pivotDimensionKeys.Count > 0)
            compositeFieldKeys = pivotDimensionKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var request = new DynamicSalesOrderReportRequest
        {
            Fields = BuildFields(definition.Fields, definition.ComputedColumns, definition.Aggregates, groupBy, compositeFieldKeys, definition.Relations),
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            GroupBy = groupBy,
            SortFieldKey = parameters.SortFieldKey,
            SortDirection = parameters.SortDirection,
        };

        MapFilters(definition.Filters, request);
        DynamicSalesOrderReportRules.ValidateDateRange(request.StartDate, request.EndDate);

        var page = await _query.PreviewAsync(request, userId, cancellationToken);

        List<ReportConfigurationGroupSubtotalDto>? groups = null;
        if (compositeGroupings.Count >= 2)
        {
            groups = ReportConfigurationGroupingRules.BuildCompositeGroupSubtotals(
                page.Rows, compositeGroupings, GroupingDimensions,
                row => ReadCurrency(row), row => ReadDecimalValue(row, "totalAmount"));
        }
        else if (groupBy != DynamicSalesOrderReportRules.GroupNone)
        {
            groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy)
                .Select(g => new ReportConfigurationGroupSubtotalDto(
                    g.Key,
                    g.Label,
                    g.Subtotals.Select(s => new ReportConfigurationCurrencyPartitionDto(
                        s.Currency, s.Count, s.Amount, null, null, null, string.Empty)).ToList()))
                .ToList();
        }

        if (groupBy == DynamicSalesOrderReportRules.GroupNone && definition.ComputedColumns is { Count: > 0 })
        {
            var baseColumns = page.Columns
                .Select(c => (c.Key, c.Label, c.DataType))
                .ToList();
            var projection = ReportConfigurationComputedProjection.Apply(
                definition.Fields, baseColumns, page.Rows, definition.ComputedColumns, CurrencyUnitOf);

            return new ReportConfigurationPreviewDto
            {
                DatasetKey = DatasetKey,
                Columns = projection.Columns,
                Rows = projection.Rows,
                CellReasons = projection.CellReasons,
                ComputedColumns = projection.Evidence,
                Total = page.Total,
                Page = page.Page,
                PageSize = page.PageSize,
                TotalPages = page.TotalPages,
                GroupBy = groupBy,
                Groups = groups,
                Evidence = new ReportConfigurationEvidenceContextDto(
                    DatasetKey, Grain, CurrencyUnitSemantics,
                    page.ReadOnlyText, page.BoundaryText, page.DisclaimerText,
                    ReportConfigurationConstants.CoverageCurrentPage),
            };
        }

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = page.Columns.Select(c => new ReportConfigurationColumnDto(
                c.Key, c.Label, c.DataType, CurrencyUnitOf(c.Key))).ToList(),
            Rows = page.Rows,
            Total = page.Total,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalPages = page.TotalPages,
            GroupBy = groupBy,
            Groups = groups,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                page.ReadOnlyText, page.BoundaryText, page.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static List<string> BuildFields(
        IReadOnlyList<string> fields,
        IReadOnlyList<ReportConfigurationComputedColumn> computedColumns,
        IReadOnlyList<ReportConfigurationAggregate> aggregates,
        string groupBy,
        IReadOnlyList<string>? compositeFieldKeys,
        IReadOnlyList<ReportConfigurationRelationSelection> relations)
    {
        var selected = (fields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();

        if (groupBy != DynamicSalesOrderReportRules.GroupNone)
            selected = DynamicSalesOrderReportRules.EnsureGroupingFields(selected, groupBy) ?? selected;

        // 复合分组（ERP-271）：读取未分组源页时仍补齐维度底层授权字段，执行后由通用引擎侧剥离隐藏依赖。
        foreach (var dependency in compositeFieldKeys ?? Array.Empty<string>())
        {
            if (!selected.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                selected.Add(dependency);
        }

        if (computedColumns is { Count: > 0 })
        {
            foreach (var dependency in ReportConfigurationFormulaRules.CollectDependencies(computedColumns))
            {
                if (!selected.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                    selected.Add(dependency);
            }
        }

        // 指标依赖字段：即使用户未选择展示，也必须获取（执行后在引擎侧剥离，绝不返回隐藏原始依赖值）
        var needsCurrency = false;
        foreach (var aggregate in aggregates ?? new List<ReportConfigurationAggregate>())
        {
            if (aggregate is null || string.IsNullOrWhiteSpace(aggregate.FieldKey))
                continue;
            var key = aggregate.FieldKey.Trim();
            if (!selected.Contains(key, StringComparer.OrdinalIgnoreCase))
                selected.Add(key);

            var isCount = string.Equals(aggregate.Function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase);
            if (!isCount && ReportConfigurationMetricRules.IsMonetaryUnit(CurrencyUnitOf(key)))
                needsCurrency = true;
        }

        if (needsCurrency && !selected.Contains("currency", StringComparer.OrdinalIgnoreCase))
            selected.Add("currency");

        // 关系来源事实键：即使用户未选择展示，也必须获取（引擎补全后剥离，绝不返回隐藏依赖值）
        if (relations is { Count: > 0 } && !selected.Contains("customerId", StringComparer.OrdinalIgnoreCase))
            selected.Add("customerId");

        return selected;
    }

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

    private static IReadOnlyList<string> CompositeFieldKeys(IReadOnlyList<string> groupings)
    {
        var keys = new List<string>();
        foreach (var grouping in groupings ?? Array.Empty<string>())
        {
            if (string.Equals(grouping, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase))
                keys.Add("customerId");
            else if (string.Equals(grouping, ReportConfigurationConstants.GroupMonth, StringComparison.OrdinalIgnoreCase))
                keys.Add("orderDate");
        }
        return keys;
    }

    private static string ReadCurrency(Dictionary<string, object?> row)
    {
        var value = ReadValue(row, "currency");
        if (value is null or DBNull)
            return string.Empty;
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static decimal? ReadDecimalValue(Dictionary<string, object?> row, string key)
    {
        var value = ReadValue(row, key);
        if (value is null or DBNull)
            return null;
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? ReadValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static void MapFilters(IReadOnlyList<ReportConfigurationFilter>? filters, DynamicSalesOrderReportRequest request)
    {
        DateTime? start = null;
        DateTime? end = null;

        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (filter is null)
                    continue;

                var key = (filter.FieldKey ?? string.Empty).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "orderdate":
                        ReportConfigurationDatasetTranslation.ApplyDateFilter(
                            filter, "orderDate", ref start, ref end);
                        break;
                    case "customerid":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        request.CustomerId = ReportConfigurationDatasetTranslation.RequireLong(filter.Value, "customerId");
                        break;
                    case "currency":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        request.Currency = ReportConfigurationDatasetTranslation.RequireString(filter.Value, "currency");
                        break;
                    case "status":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        request.Status = ReportConfigurationDatasetTranslation.RequireString(filter.Value, "status");
                        break;
                    default:
                        throw BusinessException.InvalidParameter(
                            $"数据集 {ReportConfigurationConstants.DatasetSalesOrder} 不支持的筛选字段: {key}");
                }
            }
        }

        request.StartDate = start;
        request.EndDate = end;
    }

    private static ReportConfigurationDatasetDto BuildDataset(DynamicSalesOrderReportCatalogDto catalog)
    {
        var fields = catalog.Fields
            .Select(f => BuildField(f.Key, f.Label, f.DataType, f.Filterable))
            .ToList();

        return new ReportConfigurationDatasetDto(
            ReportConfigurationConstants.DatasetSalesOrder,
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
            GroupingDimensions = GroupingDimensions.ToList(),
            GroupCustomerFieldKey = "customerId",
            GroupMonthFieldKey = "orderDate",
            Relations = new List<ReportConfigurationRelationDto>
            {
                ReportConfigurationRelationRules.BuildCustomerRelation(Grain),
            },
            SortingExplanation = SortingExplanation,
        };
    }

    private static ReportConfigurationFieldDto BuildField(string key, string label, string dataType, bool filterable)
    {
        CurrencyUnits.TryGetValue(key, out var currencyUnit);
        var aggregatable = string.Equals(dataType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
            && !NonAggregatable.Contains(key);
        var sortability = SortabilityOf(key);

        return new ReportConfigurationFieldDto(
            key,
            label,
            dataType,
            currencyUnit,
            filterable,
            aggregatable,
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(dataType))
        {
            Sortable = sortability.Sortable,
            SortUnavailableReason = sortability.Reason,
        };
    }
}

/// <summary>
/// 客户应收账款证据数据集适配器（ERP-117）：复用既有「客户资料」菜单授权与 ERP-074 对账证据行白名单。
/// </summary>
public sealed class ReceivableReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IDynamicReceivableReportQuery _query;

    public ReceivableReportConfigurationDatasetProvider(IDynamicReceivableReportQuery query)
    {
        _query = query;
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetReceivable;

    private const string Label = "客户应收账款证据";
    private const string Grain = "客户销项发票证据（一行一条发票 / 分摊证据）";
    private const string CurrencyUnitSemantics = "金额按证据原币呈现，不跨币种换算或合并；算术剩余证据不主张权威余额";
    private const string RequiredMenuCode = DynamicReceivableReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicReceivableReportRules.RequiredMenuText;
    private const int DefaultPageSize = DynamicReceivableReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicReceivableReportRules.MaxPageSize;
    private const string ReadOnlyText = DynamicReceivableReportRules.ReadOnlyText;
    private const string BoundaryText = DynamicReceivableReportRules.BoundaryText;

    private static readonly IReadOnlyList<string> GroupingKeys = new[]
    {
        ReportConfigurationConstants.GroupNone,
        ReportConfigurationConstants.GroupCustomer,
        ReportConfigurationConstants.GroupMonth,
    };

    private static readonly IReadOnlyList<ReportConfigurationGroupingDimensionDto> GroupingDimensions = new[]
    {
        new ReportConfigurationGroupingDimensionDto(
            ReportConfigurationConstants.GroupCustomer,
            "按客户分组",
            "customerId",
            ReportConfigurationConstants.TypeNumber,
            ReportConfigurationConstants.GroupingSemanticsIdentity),
        new ReportConfigurationGroupingDimensionDto(
            ReportConfigurationConstants.GroupMonth,
            "按月份分组",
            "invoiceDate",
            ReportConfigurationConstants.TypeDate,
            ReportConfigurationConstants.GroupingSemanticsCalendarMonth),
    };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityGrouping,
        ReportConfigurationConstants.CapabilityDateRange,
        ReportConfigurationConstants.CapabilityPaging,
        ReportConfigurationConstants.CapabilityComputedColumns,
    };

    private static readonly IReadOnlyList<string> UnsupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    private static readonly Dictionary<string, string> CurrencyUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["currency"] = "币种代码",
        ["grossAmount"] = "原币金额",
        ["activeAmount"] = "原币金额",
        ["effectiveAmount"] = "原币金额",
        ["remainingAmount"] = "原币金额",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoiceId", "customerId", "amountDecimals", "status",
    };

    private static readonly HashSet<string> SortableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoiceId", "invoiceDate", "customerId",
    };

    private const string SortingExplanation =
        "仅发票 Id / 开票日期 / 客户 Id（原生持久化键）可排序；金额 / 精度 / 派生 / 关系字段不可排序；并列时按发票 Id 升序稳定分页";

    private static (bool Sortable, string? Reason) SortabilityOf(string key)
    {
        if (SortableFields.Contains(key))
            return (true, null);
        if (CurrencyUnits.ContainsKey(key))
            return (false, "金额 / 币种字段不可排序（非原生可排序键）");
        return (false, "仅发票 Id / 开票日期 / 客户 Id 可排序");
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        DynamicReceivableReportCatalogDto catalog;
        try
        {
            catalog = await _query.GetCatalogAsync(userId, cancellationToken);
        }
        catch (BusinessException ex) when (ex.Code == ErrorCodes.Forbidden)
        {
            // 无客户资料菜单授权 → 该数据集不暴露
            return null;
        }

        return BuildDataset(catalog);
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

        var compositeGroupings = ReportConfigurationGroupingRules.NormalizeGroupingKeys(parameters.Groupings);
        var groupBy = DynamicReceivableReportRules.NormalizeGroupBy(parameters.GroupBy);
        IReadOnlyList<string> pivotDimensionKeys = definition.Pivot is null
            ? Array.Empty<string>()
            : CompositeFieldKeys(new[] { definition.Pivot.RowDimension, definition.Pivot.ColumnDimension });
        List<string>? compositeFieldKeys = null;
        if (compositeGroupings.Count >= 2)
            compositeFieldKeys = CompositeFieldKeys(compositeGroupings).Concat(new[] { "currency", "grossAmount" }).ToList();
        else if (pivotDimensionKeys.Count > 0)
            compositeFieldKeys = pivotDimensionKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var request = new DynamicReceivableReportRequest
        {
            Fields = BuildFields(definition.Fields, definition.ComputedColumns, definition.Aggregates, groupBy, compositeFieldKeys, definition.Relations),
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            GroupBy = groupBy,
            SortFieldKey = parameters.SortFieldKey,
            SortDirection = parameters.SortDirection,
        };

        MapFilters(definition.Filters, request);
        DynamicReceivableReportRules.ValidateDateRange(request.StartDate, request.EndDate);

        var page = await _query.PreviewAsync(request, userId, cancellationToken);

        List<ReportConfigurationGroupSubtotalDto>? groups = null;
        if (compositeGroupings.Count >= 2)
        {
            groups = ReportConfigurationGroupingRules.BuildCompositeGroupSubtotals(
                page.Rows, compositeGroupings, GroupingDimensions,
                row => ReadCurrency(row), row => ReadDecimalValue(row, "grossAmount"));
        }
        else if (groupBy != DynamicReceivableReportRules.GroupNone)
        {
            groups = DynamicReceivableReportRules.BuildGroupSubtotals(page.Rows, groupBy)
                .Select(g => new ReportConfigurationGroupSubtotalDto(
                    g.Key,
                    g.Label,
                    g.Subtotals.Select(s => new ReportConfigurationCurrencyPartitionDto(
                        s.Currency, s.Count, null, s.GrossAmount, s.EffectiveAllocatedAmount,
                        s.RemainingAmount, s.RemainingState)).ToList()))
                .ToList();
        }

        if (groupBy == DynamicReceivableReportRules.GroupNone && definition.ComputedColumns is { Count: > 0 })
        {
            var baseColumns = page.Columns
                .Select(c => (c.Key, c.Label, c.DataType))
                .ToList();
            var projection = ReportConfigurationComputedProjection.Apply(
                definition.Fields, baseColumns, page.Rows, definition.ComputedColumns, CurrencyUnitOf);

            return new ReportConfigurationPreviewDto
            {
                DatasetKey = DatasetKey,
                Columns = projection.Columns,
                Rows = projection.Rows,
                CellReasons = projection.CellReasons,
                ComputedColumns = projection.Evidence,
                Total = page.Total,
                Page = page.Page,
                PageSize = page.PageSize,
                TotalPages = page.TotalPages,
                GroupBy = groupBy,
                Groups = groups,
                Evidence = new ReportConfigurationEvidenceContextDto(
                    DatasetKey, Grain, CurrencyUnitSemantics,
                    page.ReadOnlyText, page.BoundaryText, page.DisclaimerText,
                    ReportConfigurationConstants.CoverageCurrentPage),
            };
        }

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = page.Columns.Select(c => new ReportConfigurationColumnDto(
                c.Key, c.Label, c.DataType, CurrencyUnitOf(c.Key))).ToList(),
            Rows = page.Rows,
            Total = page.Total,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalPages = page.TotalPages,
            GroupBy = groupBy,
            Groups = groups,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                page.ReadOnlyText, page.BoundaryText, page.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static List<string> BuildFields(
        IReadOnlyList<string> fields,
        IReadOnlyList<ReportConfigurationComputedColumn> computedColumns,
        IReadOnlyList<ReportConfigurationAggregate> aggregates,
        string groupBy,
        IReadOnlyList<string>? compositeFieldKeys,
        IReadOnlyList<ReportConfigurationRelationSelection> relations)
    {
        var selected = (fields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();

        if (groupBy != DynamicReceivableReportRules.GroupNone)
            selected = DynamicReceivableReportRules.EnsureGroupingFields(selected, groupBy) ?? selected;

        // 复合分组（ERP-271）：读取未分组源页时仍补齐维度底层授权字段，执行后由通用引擎侧剥离隐藏依赖。
        foreach (var dependency in compositeFieldKeys ?? Array.Empty<string>())
        {
            if (!selected.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                selected.Add(dependency);
        }

        if (computedColumns is { Count: > 0 })
        {
            foreach (var dependency in ReportConfigurationFormulaRules.CollectDependencies(computedColumns))
            {
                if (!selected.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                    selected.Add(dependency);
            }
        }

        // 指标依赖字段：即使用户未选择展示，也必须获取（执行后在引擎侧剥离，绝不返回隐藏原始依赖值）
        var needsCurrency = false;
        foreach (var aggregate in aggregates ?? new List<ReportConfigurationAggregate>())
        {
            if (aggregate is null || string.IsNullOrWhiteSpace(aggregate.FieldKey))
                continue;
            var key = aggregate.FieldKey.Trim();
            if (!selected.Contains(key, StringComparer.OrdinalIgnoreCase))
                selected.Add(key);

            var isCount = string.Equals(aggregate.Function, ReportConfigurationConstants.AggregateCount, StringComparison.OrdinalIgnoreCase);
            if (!isCount && ReportConfigurationMetricRules.IsMonetaryUnit(CurrencyUnitOf(key)))
                needsCurrency = true;
        }

        if (needsCurrency && !selected.Contains("currency", StringComparer.OrdinalIgnoreCase))
            selected.Add("currency");

        // 关系来源事实键：即使用户未选择展示，也必须获取（引擎补全后剥离，绝不返回隐藏依赖值）
        if (relations is { Count: > 0 } && !selected.Contains("customerId", StringComparer.OrdinalIgnoreCase))
            selected.Add("customerId");

        return selected;
    }

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

    private static IReadOnlyList<string> CompositeFieldKeys(IReadOnlyList<string> groupings)
    {
        var keys = new List<string>();
        foreach (var grouping in groupings ?? Array.Empty<string>())
        {
            if (string.Equals(grouping, ReportConfigurationConstants.GroupCustomer, StringComparison.OrdinalIgnoreCase))
                keys.Add("customerId");
            else if (string.Equals(grouping, ReportConfigurationConstants.GroupMonth, StringComparison.OrdinalIgnoreCase))
                keys.Add("invoiceDate");
        }
        return keys;
    }

    private static string ReadCurrency(Dictionary<string, object?> row)
    {
        var value = ReadValue(row, "currency");
        if (value is null or DBNull)
            return string.Empty;
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static decimal? ReadDecimalValue(Dictionary<string, object?> row, string key)
    {
        var value = ReadValue(row, key);
        if (value is null or DBNull)
            return null;
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? ReadValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static void MapFilters(IReadOnlyList<ReportConfigurationFilter>? filters, DynamicReceivableReportRequest request)
    {
        DateTime? start = null;
        DateTime? end = null;

        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (filter is null)
                    continue;

                var key = (filter.FieldKey ?? string.Empty).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "invoicedate":
                        ReportConfigurationDatasetTranslation.ApplyDateFilter(
                            filter, "invoiceDate", ref start, ref end);
                        break;
                    case "customerid":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        request.CustomerId = ReportConfigurationDatasetTranslation.RequireLong(filter.Value, "customerId");
                        break;
                    case "currency":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        request.Currency = ReportConfigurationDatasetTranslation.RequireString(filter.Value, "currency");
                        break;
                    case "allocationstate":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        request.AllocationState = ReportConfigurationDatasetTranslation.RequireString(filter.Value, "allocationState");
                        break;
                    default:
                        throw BusinessException.InvalidParameter(
                            $"数据集 {ReportConfigurationConstants.DatasetReceivable} 不支持的筛选字段: {key}");
                }
            }
        }

        request.StartDate = start;
        request.EndDate = end;
    }

    private static ReportConfigurationDatasetDto BuildDataset(DynamicReceivableReportCatalogDto catalog)
    {
        var fields = catalog.Fields
            .Select(f => BuildField(f.Key, f.Label, f.DataType, f.Filterable))
            .ToList();

        return new ReportConfigurationDatasetDto(
            ReportConfigurationConstants.DatasetReceivable,
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
            GroupingDimensions = GroupingDimensions.ToList(),
            GroupCustomerFieldKey = "customerId",
            GroupMonthFieldKey = "invoiceDate",
            Relations = new List<ReportConfigurationRelationDto>
            {
                ReportConfigurationRelationRules.BuildCustomerRelation(Grain),
            },
            SortingExplanation = SortingExplanation,
        };
    }

    private static ReportConfigurationFieldDto BuildField(string key, string label, string dataType, bool filterable)
    {
        CurrencyUnits.TryGetValue(key, out var currencyUnit);
        var aggregatable = string.Equals(dataType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
            && !NonAggregatable.Contains(key);
        var sortability = SortabilityOf(key);

        return new ReportConfigurationFieldDto(
            key,
            label,
            dataType,
            currencyUnit,
            filterable,
            aggregatable,
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(dataType))
        {
            Sortable = sortability.Sortable,
            SortUnavailableReason = sortability.Reason,
        };
    }
}

/// <summary>
/// 通用报表配置数据集的「有界类型化值翻译」辅助：把反序列化定义中的有限筛选值安全还原为 CLR 值，
/// 只支持适配器显式声明的有限字段 / 操作符；任何不兼容操作显式拒绝（fail closed）。
/// </summary>
internal static class ReportConfigurationDatasetTranslation
{
    public static void EnsureOperator(ReportConfigurationFilter filter, string supportedOperator)
    {
        var op = (filter.Operator ?? string.Empty).Trim();
        if (!string.Equals(op, supportedOperator, StringComparison.OrdinalIgnoreCase))
        {
            throw BusinessException.InvalidParameter(
                $"字段 {filter.FieldKey} 在当前数据集适配中仅支持操作符 {supportedOperator}（收到 {op}）");
        }
    }

    public static string RequireString(object? value, string context)
    {
        var v = ToClr(value);
        if (v is string s)
            return s;
        throw BusinessException.InvalidParameter($"{context} 必须是字符串");
    }

    public static long RequireLong(object? value, string context)
    {
        var v = ToClr(value);
        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case short s: return s;
            case byte b: return b;
            case decimal m when m == decimal.Truncate(m): return (long)m;
            case double d when d == Math.Truncate(d): return (long)d;
            default:
                throw BusinessException.InvalidParameter($"{context} 必须是整数");
        }
    }

    public static DateTime RequireDate(object? value, string context)
    {
        var v = ToClr(value);
        switch (v)
        {
            case DateTime dt: return dt;
            case DateTimeOffset dto: return dto.DateTime;
            case string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed):
                return parsed;
            default:
                throw BusinessException.InvalidParameter($"{context} 必须是日期");
        }
    }

    public static void ApplyDateFilter(
        ReportConfigurationFilter filter, string fieldKey, ref DateTime? start, ref DateTime? end)
    {
        var op = (filter.Operator ?? string.Empty).Trim();
        switch (op)
        {
            case ReportConfigurationConstants.OperatorEq:
                var eq = RequireDate(filter.Value, fieldKey).Date;
                start = eq;
                end = eq;
                break;
            case ReportConfigurationConstants.OperatorGte:
                start = RequireDate(filter.Value, fieldKey).Date;
                break;
            case ReportConfigurationConstants.OperatorLte:
                end = RequireDate(filter.Value, fieldKey).Date;
                break;
            case ReportConfigurationConstants.OperatorGt:
                start = RequireDate(filter.Value, fieldKey).Date.AddDays(1);
                break;
            case ReportConfigurationConstants.OperatorLt:
                end = RequireDate(filter.Value, fieldKey).Date.AddDays(-1);
                break;
            case ReportConfigurationConstants.OperatorBetween:
                start = RequireDate(filter.Value, fieldKey).Date;
                end = RequireDate(filter.Value2, fieldKey).Date;
                break;
            default:
                throw BusinessException.InvalidParameter($"字段 {fieldKey} 不支持的日期筛选操作符: {op}");
        }
    }

    private static object? ToClr(object? value)
        => value is JsonElement element ? JsonElementToClr(element) : value;

    private static object? JsonElementToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDecimal();
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(JsonElementToClr).ToList();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.Object:
            default:
                return null;
        }
    }
}
internal static class ReportConfigurationComputedProjection
{
    /// <summary>
    /// 投影当前预览明细行：只返回「已选基础字段 + 计算列」，绝不返回隐藏依赖字段值。
    /// <para>求值由 <see cref="ReportConfigurationFormulaRules.Evaluate"/> 完成（checked 十进制，null 携带有界原因）。</para>
    /// </summary>
    public static (
        List<ReportConfigurationColumnDto> Columns,
        List<Dictionary<string, object?>> Rows,
        List<Dictionary<string, string?>> CellReasons,
        List<ReportConfigurationComputedColumnEvidenceDto> Evidence) Apply(
        IReadOnlyList<string> selectedFields,
        IReadOnlyList<(string Key, string Label, string Type)> baseColumns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<ReportConfigurationComputedColumn> computedColumns,
        Func<string, string?> unitLookup)
    {
        var selected = (selectedFields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();

        var evidence = ReportConfigurationFormulaRules.BuildEvidence(computedColumns, unitLookup).ToList();
        if (computedColumns is null || computedColumns.Count == 0)
        {
            return (BuildBaseColumns(selected, baseColumns, unitLookup),
                (rows ?? new List<Dictionary<string, object?>>()).ToList(),
                new List<Dictionary<string, string?>>(),
                evidence);
        }

        var evaluation = ReportConfigurationFormulaRules.Evaluate(computedColumns, rows);

        var projectedRows = new List<Dictionary<string, object?>>();
        var cellReasons = new List<Dictionary<string, string?>>();
        var sourceRows = rows ?? new List<Dictionary<string, object?>>();

        for (var i = 0; i < sourceRows.Count; i++)
        {
            var source = sourceRows[i];
            var projected = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in selected)
                projected[key] = ReadValue(source, key);

            if (i < evaluation.Count)
            {
                foreach (var column in computedColumns)
                {
                    if (column is null)
                        continue;
                    evaluation[i].Values.TryGetValue(column.Key, out var value);
                    projected[column.Key] = value;
                }

                var reasons = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var kv in evaluation[i].Reasons)
                    reasons[kv.Key] = kv.Value;
                cellReasons.Add(reasons);
            }
            else
            {
                cellReasons.Add(new Dictionary<string, string?>());
            }

            projectedRows.Add(projected);
        }

        var columns = BuildBaseColumns(selected, baseColumns, unitLookup);
        foreach (var column in computedColumns)
        {
            if (column is null)
                continue;
            var key = (column.Key ?? string.Empty).Trim();
            var label = string.IsNullOrWhiteSpace(column.Label) ? key : column.Label.Trim();
            columns.Add(new ReportConfigurationColumnDto(
                key,
                label,
                ReportConfigurationConstants.TypeNumber,
                ReportConfigurationFormulaRules.DeriveUnit(column, unitLookup),
                ReportConfigurationFormulaRules.UnknownReasonText,
                IsComputed: true));
        }

        return (columns, projectedRows, cellReasons, evidence);
    }

    private static List<ReportConfigurationColumnDto> BuildBaseColumns(
        IReadOnlyList<string> selected,
        IReadOnlyList<(string Key, string Label, string Type)> baseColumns,
        Func<string, string?> unitLookup)
    {
        var result = new List<ReportConfigurationColumnDto>();
        foreach (var key in selected)
        {
            var column = default((string Key, string Label, string Type));
            foreach (var candidate in baseColumns)
            {
                if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    column = candidate;
                    break;
                }
            }

            result.Add(new ReportConfigurationColumnDto(
                key,
                string.IsNullOrWhiteSpace(column.Label) ? key : column.Label,
                string.IsNullOrWhiteSpace(column.Type) ? ReportConfigurationConstants.TypeNumber : column.Type,
                unitLookup(key)));
        }

        return result;
    }

    private static object? ReadValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }
}



