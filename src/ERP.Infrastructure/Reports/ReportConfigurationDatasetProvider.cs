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
        ["depositAmount"] = "原币金额",
        ["depositRatio"] = "%",
        ["commissionRatio"] = "%",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "customerId", "salesmanId", "portId",
    };

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

        var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(parameters.GroupBy);
        var request = new DynamicSalesOrderReportRequest
        {
            Fields = BuildFields(definition.Fields, groupBy),
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            GroupBy = groupBy,
        };

        MapFilters(definition.Filters, request);
        DynamicSalesOrderReportRules.ValidateDateRange(request.StartDate, request.EndDate);

        var page = await _query.PreviewAsync(request, userId, cancellationToken);

        var groups = groupBy == DynamicSalesOrderReportRules.GroupNone
            ? null
            : DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);

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
            Groups = groups?.Select(g => new ReportConfigurationGroupSubtotalDto(
                g.Key,
                g.Label,
                g.Subtotals.Select(s => new ReportConfigurationCurrencyPartitionDto(
                    s.Currency, s.Count, s.Amount, null, null, null, string.Empty)).ToList())).ToList(),
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                page.ReadOnlyText, page.BoundaryText, page.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static List<string> BuildFields(IReadOnlyList<string> fields, string groupBy)
    {
        var selected = (fields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();

        if (groupBy != DynamicSalesOrderReportRules.GroupNone)
            return DynamicSalesOrderReportRules.EnsureGroupingFields(selected, groupBy) ?? selected;

        return selected;
    }

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

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
            BoundaryText);
    }

    private static ReportConfigurationFieldDto BuildField(string key, string label, string dataType, bool filterable)
    {
        CurrencyUnits.TryGetValue(key, out var currencyUnit);
        var aggregatable = string.Equals(dataType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
            && !NonAggregatable.Contains(key);

        return new ReportConfigurationFieldDto(
            key,
            label,
            dataType,
            currencyUnit,
            filterable,
            aggregatable,
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(dataType));
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
        ["grossAmount"] = "原币金额",
        ["activeAmount"] = "原币金额",
        ["effectiveAmount"] = "原币金额",
        ["remainingAmount"] = "原币金额",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoiceId", "customerId", "amountDecimals", "status",
    };

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

        var groupBy = DynamicReceivableReportRules.NormalizeGroupBy(parameters.GroupBy);
        var request = new DynamicReceivableReportRequest
        {
            Fields = BuildFields(definition.Fields, groupBy),
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            GroupBy = groupBy,
        };

        MapFilters(definition.Filters, request);
        DynamicReceivableReportRules.ValidateDateRange(request.StartDate, request.EndDate);

        var page = await _query.PreviewAsync(request, userId, cancellationToken);

        var groups = groupBy == DynamicReceivableReportRules.GroupNone
            ? null
            : DynamicReceivableReportRules.BuildGroupSubtotals(page.Rows, groupBy);

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
            Groups = groups?.Select(g => new ReportConfigurationGroupSubtotalDto(
                g.Key,
                g.Label,
                g.Subtotals.Select(s => new ReportConfigurationCurrencyPartitionDto(
                    s.Currency, s.Count, null, s.GrossAmount, s.EffectiveAllocatedAmount,
                    s.RemainingAmount, s.RemainingState)).ToList())).ToList(),
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                page.ReadOnlyText, page.BoundaryText, page.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static List<string> BuildFields(IReadOnlyList<string> fields, string groupBy)
    {
        var selected = (fields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();

        if (groupBy != DynamicReceivableReportRules.GroupNone)
            return DynamicReceivableReportRules.EnsureGroupingFields(selected, groupBy) ?? selected;

        return selected;
    }

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

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
            BoundaryText);
    }

    private static ReportConfigurationFieldDto BuildField(string key, string label, string dataType, bool filterable)
    {
        CurrencyUnits.TryGetValue(key, out var currencyUnit);
        var aggregatable = string.Equals(dataType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
            && !NonAggregatable.Contains(key);

        return new ReportConfigurationFieldDto(
            key,
            label,
            dataType,
            currencyUnit,
            filterable,
            aggregatable,
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(dataType));
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


