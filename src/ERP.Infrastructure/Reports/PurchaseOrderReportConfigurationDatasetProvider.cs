using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的数据集适配器：把动态采购订单报表（ERP-125）的有限字段白名单
/// 与能力 / 粒度 / 币种口径暴露为统一目录，预览复用既有 <see cref="IDynamicPurchaseOrderReportQuery"/> 的
/// 只读有界查询（采购订单菜单授权 + 未删除可见性；金额按订单原币呈现，不跨币种换算或合并）。
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」采购订单模块授权（fail closed）；不新增控制器 /
/// 设计器 / 导出器，也不把静态目录扩大到数据库全量元数据发现。</para>
/// </summary>
public sealed class PurchaseOrderReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IDynamicPurchaseOrderReportQuery _query;

    public PurchaseOrderReportConfigurationDatasetProvider(IDynamicPurchaseOrderReportQuery query)
    {
        _query = query;
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetPurchaseOrder;

    private const string Label = "采购订单";
    private const string Grain = "采购订单（一行一条采购订单）";
    private const string CurrencyUnitSemantics = "金额按订单原币呈现，不跨币种换算或合并";
    private const string RequiredMenuCode = DynamicPurchaseOrderReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicPurchaseOrderReportRules.RequiredMenuText;
    private const int DefaultPageSize = DynamicPurchaseOrderReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicPurchaseOrderReportRules.MaxPageSize;
    private const string ReadOnlyText = DynamicPurchaseOrderReportRules.ReadOnlyText;
    private const string BoundaryText = DynamicPurchaseOrderReportRules.BoundaryText;

    private static readonly IReadOnlyList<string> GroupingKeys = new[]
    {
        ReportConfigurationConstants.GroupNone,
        ReportConfigurationConstants.GroupMonth,
    };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityGrouping,
        ReportConfigurationConstants.CapabilityDateRange,
        ReportConfigurationConstants.CapabilityPaging,
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
        ["taxRate"] = "%",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "supplierId", "buyerId", "portId", "owningCustomerId", "owningSalesOrderId",
    };

    private const string SortingExplanation =
        "本数据集不支持任意排序：稳定分页按订单 Id 升序；金额 / 税率 / 币种 / 派生字段均不可排序";

    private static (bool Sortable, string? Reason) SortabilityOf(string key)
        => (false, "本数据集不支持任意排序：稳定分页按订单 Id 升序");

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

    /// <inheritdoc />
    public async Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        DynamicPurchaseOrderReportCatalogDto catalog;
        try
        {
            catalog = await _query.GetCatalogAsync(userId, cancellationToken);
        }
        catch (BusinessException ex) when (ex.Code == ErrorCodes.Forbidden)
        {
            // 无采购订单菜单授权 → 该数据集不暴露（fail closed 由目录层收敛，不整体失败）
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

        // 分组键 fail closed：仅 none / month（与既有采购订单报表同口径；先于任何读取）。
        var groupBy = DynamicPurchaseOrderReportRules.NormalizeGroupBy(parameters.GroupBy);

        var fieldKeys = DynamicPurchaseOrderReportRules.NormalizeFields(definition.Fields);
        var fields = fieldKeys.ToList();
        if (groupBy != DynamicPurchaseOrderReportRules.GroupNone)
            fields = DynamicPurchaseOrderReportRules.EnsureGroupingFields(fields, groupBy) ?? fields;

        var request = new DynamicPurchaseOrderReportRequest
        {
            Fields = fields,
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            GroupBy = groupBy,
        };

        MapFilters(definition.Filters, request);
        DynamicPurchaseOrderReportRules.ValidateDateRange(request.StartDate, request.EndDate);

        // 复用既有采购订单只读有界查询：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小。
        var page = await _query.PreviewAsync(request, userId, cancellationToken);

        List<ReportConfigurationGroupSubtotalDto>? groups = null;
        if (groupBy != DynamicPurchaseOrderReportRules.GroupNone)
        {
            groups = DynamicPurchaseOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy)
                .Select(g => new ReportConfigurationGroupSubtotalDto(
                    g.Key,
                    g.Label,
                    g.Subtotals.Select(s => new ReportConfigurationCurrencyPartitionDto(
                        s.Currency, s.Count, s.Amount, null, null, null, string.Empty)).ToList()))
                .ToList();
        }

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = page.Columns
                .Select(c => new ReportConfigurationColumnDto(c.Key, c.Label, c.DataType, CurrencyUnitOf(c.Key)))
                .ToList(),
            Rows = page.Rows,
            Total = page.Total,
            MatchedCount = page.Total,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalPages = page.TotalPages,
            GroupBy = groupBy,
            Groups = groups,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                ReadOnlyText, BoundaryText, DynamicPurchaseOrderReportRules.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static void MapFilters(
        IReadOnlyList<ReportConfigurationFilter>? filters, DynamicPurchaseOrderReportRequest request)
    {
        DateTime? start = null;
        DateTime? end = null;
        long? supplierId = null;
        string? currency = null;
        string? status = null;

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
                        ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "orderDate", ref start, ref end);
                        break;
                    case "supplierid":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        supplierId = ReportConfigurationDatasetTranslation.CoalesceLong(supplierId, filter.Value, "supplierId");
                        break;
                    case "currency":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                        break;
                    case "status":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        status = ReportConfigurationDatasetTranslation.CoalesceString(status, filter.Value, "status");
                        break;
                    default:
                        throw BusinessException.InvalidParameter(
                            $"数据集 {ReportConfigurationConstants.DatasetPurchaseOrder} 不支持的筛选字段: {key}");
                }
            }
        }

        request.StartDate = start;
        request.EndDate = end;
        request.SupplierId = supplierId;
        request.Currency = currency;
        request.Status = status;
    }


    private static ReportConfigurationDatasetDto BuildDataset(DynamicPurchaseOrderReportCatalogDto catalog)
    {
        var fields = catalog.Fields
            .Select(f => BuildField(f.Key, f.Label, f.DataType, f.Filterable))
            .ToList();

        return new ReportConfigurationDatasetDto(
            ReportConfigurationConstants.DatasetPurchaseOrder,
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
            GroupMonthFieldKey = "orderDate",
            Relations = new List<ReportConfigurationRelationDto>(),
            SortingExplanation = SortingExplanation,
        };
    }

    private static ReportConfigurationFieldDto BuildField(
        string key, string label, string dataType, bool filterable)
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

