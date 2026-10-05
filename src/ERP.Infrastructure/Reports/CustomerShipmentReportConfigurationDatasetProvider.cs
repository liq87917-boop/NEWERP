using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-301 Stage 2）的客户出货量统计数据集适配器：把既有
/// 「客户出货量统计表」（customer-shipment）固定报表与动态证据报表的有限字段白名单
/// （客户 / 原币 / 已审核订单数 / 原币金额小计 / 已知单一单位数量 / 显式数量与来源证据）
/// 暴露为统一受控数据集，预览复用 <see cref="IReportService.GetCustomerShipmentStatsAsync"/> 的只读有界语义。
/// <para>行口径与既有 ERP-227 / ERP-228 完全一致：客户 × 原币证据行、金额仅在已知币种下签名小计、未知币种金额为 null、
/// 精确单位分组独立呈现、绝不跨币种 / 跨单位合计。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」客户出货量统计表（customer-shipment）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class CustomerShipmentReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public CustomerShipmentReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetCustomerShipment;

    private const string Label = "客户出货量统计表";
    private const string Grain = "客户出货量统计（一行一个客户 × 原币证据行）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；数量按基础单位；不跨币种换算或合并";
    private const string RequiredMenuCode = "customer-shipment";
    private const string RequiredMenuText = "客户出货量统计表";
    private const string ReadOnlyText = "只读客户出货量证据数据集：仅按既有报表服务读取已审核、未删除、授权范围销售订单证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限客户出货量证据字段白名单（客户 / 原币 / 已审核订单数 / 原币金额小计 / 已知单一单位数量 / 显式数量与来源证据）；日期窗口有界（含首尾最多 366 天）；订单头探测上限 500 张、明细探测上限 10000 条；金额按客户 × 原币独立小计、数量按原始精确单位分组，绝不跨币种 / 跨单位合计";
    private const string DisclaimerText = "本预览为只读客户出货量证据：金额与数量均取自已审核、未删除销售订单头 / 明细，非实际出库 / 装柜数量，非实际收款金额";
    private const string UnknownValueText = "未知";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;

    private static readonly IReadOnlyList<string> GroupingKeys = new[] { ReportConfigurationConstants.GroupNone };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
    };

    private static readonly IReadOnlyList<string> UnsupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "customerId", "orderCount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("customerId", "客户Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("customerName", "客户", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currency", "原币币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("currencyLabel", "原币币种标签", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("orderCount", "已审核订单数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("totalAmount", "原币金额小计", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("currencyEvidence", "币种证据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("amountLabel", "金额口径", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("unitGroups", "单位分组", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("totalQuantity", "已知单一单位数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
        Field("quantityCompletenessReason", "数量完整度", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("quantityLabel", "数量口径", ReportConfigurationConstants.TypeText, null, filterable: false),
    };

    private static ReportConfigurationFieldDto Field(string key, string label, string type, string? unit, bool filterable)
        => new(key, label, type, unit,
            Filterable: filterable,
            Aggregatable: string.Equals(type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
                && !NonAggregatable.Contains(key),
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(type));

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
        var (start, end, filter) = BuildQuery(definition);

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var items = await _reportService.GetCustomerShipmentStatsAsync(start, end, scope, filter);

        var total = items.Count;
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
        var skip = (page - 1) * pageSize;
        var pageItems = items.Skip(skip).Take(pageSize).ToList();

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = pageItems.Select(item => BuildRow(item, fieldKeys)).ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = total,
            MatchedCount = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                ReadOnlyText, BoundaryText, DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再预览{Label}", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝预览{Label}"
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

    private static (DateTime Start, DateTime End, CustomerShipmentFilterDto? Filter) BuildQuery(
        ReportConfigurationDefinition definition)
    {
        DateTime? start = null;
        DateTime? end = null;
        long? customerId = null;
        string? currency = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "orderdate":
                    ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "orderDate", ref start, ref end);
                    break;
                case "customerid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    customerId = ReportConfigurationDatasetTranslation.CoalesceLong(customerId, filter.Value, "customerId");
                    break;
                case "currency":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetCustomerShipment} 不支持的筛选字段：{key}");
            }
        }

        var filterDto = customerId is null && currency is null
            ? null
            : new CustomerShipmentFilterDto
            {
                CustomerId = customerId,
                Currency = currency,
            };

        return ((start ?? DateTime.Today).Date, (end ?? DateTime.Today).Date, filterDto);
    }

    private static IReadOnlyList<string> NormalizeFields(IReadOnlyList<string>? fields)
    {
        var selected = (fields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            selected.AddRange(Fields.Select(f => f.Key));

        return selected;
    }

    private static Dictionary<string, object?> BuildRow(
        ReportDtos.CustomerShipmentItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("customerId", StringComparer.OrdinalIgnoreCase)) row["customerId"] = item.CustomerId;
        if (fieldKeys.Contains("customerName", StringComparer.OrdinalIgnoreCase)) row["customerName"] = item.CustomerName;
        if (fieldKeys.Contains("currency", StringComparer.OrdinalIgnoreCase)) row["currency"] = item.Currency;
        if (fieldKeys.Contains("currencyLabel", StringComparer.OrdinalIgnoreCase)) row["currencyLabel"] = item.CurrencyLabel;
        if (fieldKeys.Contains("orderCount", StringComparer.OrdinalIgnoreCase)) row["orderCount"] = item.OrderCount;
        if (fieldKeys.Contains("totalAmount", StringComparer.OrdinalIgnoreCase)) row["totalAmount"] = item.TotalAmount;
        if (fieldKeys.Contains("currencyEvidence", StringComparer.OrdinalIgnoreCase)) row["currencyEvidence"] = item.CurrencyEvidence;
        if (fieldKeys.Contains("amountLabel", StringComparer.OrdinalIgnoreCase)) row["amountLabel"] = item.AmountLabel;
        if (fieldKeys.Contains("unitGroups", StringComparer.OrdinalIgnoreCase)) row["unitGroups"] = FormatUnitGroups(item.UnitGroups);
        if (fieldKeys.Contains("totalQuantity", StringComparer.OrdinalIgnoreCase)) row["totalQuantity"] = item.TotalQuantity;
        if (fieldKeys.Contains("quantityCompletenessReason", StringComparer.OrdinalIgnoreCase)) row["quantityCompletenessReason"] = item.QuantityCompletenessReason;
        if (fieldKeys.Contains("quantityLabel", StringComparer.OrdinalIgnoreCase)) row["quantityLabel"] = item.QuantityLabel;
        return row;
    }

    private static string FormatUnitGroups(IReadOnlyList<ReportDtos.CustomerShipmentUnitGroup>? groups)
    {
        if (groups is null || groups.Count == 0)
            return "无单位分组证据";

        return string.Join("; ", groups.Select(g =>
            $"{g.Unit}={g.Quantity?.ToString() ?? UnknownValueText}({g.DetailCount}条)"));
    }

    private ReportConfigurationDatasetDto BuildDataset()
    {
        var fields = Fields.ToList();
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
            SortingExplanation = "本数据集不支持任意排序：稳定按客户 Id → 原币分组排序（与既有客户出货量统计一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}
