using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

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

