using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-300 Stage 2）的订单利润暂估数据集适配器：暴露既有
/// <see cref="IReportService.GetOrderProfitEstimateAsync"/> 固定报表的有限字段白名单
/// （订单 / 客户 / 日期 / 原币 / 销售额 / 显式未知成本利润证据 / 独立当前价估算）与只读、有界预览口径。
/// <para>预览委托既有固定报表服务语义：销售额保留订单原币 <c>TotalAmount</c>，成本 / 利润 / 利润率为未知（null，绝不回落为 0），
/// 「当前价估算」仅作独立口径（币种未知，仅估算）；不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」订单利润暂估表（order-profit）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class OrderProfitEstimateReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public OrderProfitEstimateReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetOrderProfit;

    private const string Label = "订单利润暂估表";
    private const string Grain = "订单利润暂估表（一行一条已审核、未删除、授权范围内的销售订单）";
    private const string CurrencyUnitSemantics = "销售额为订单原币金额、绝不跨币种合计；成本 / 利润 / 利润率为未知（null，绝不回落为 0）；当前价估算为独立口径（币种未知，仅估算）";
    private const string RequiredMenuCode = "order-profit";
    private const string RequiredMenuText = "订单利润暂估表";
    private const string ReadOnlyText = "只读订单利润暂估数据集：仅按既有固定报表服务读取已审核销售订单证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限订单利润暂估字段白名单；筛选仅限订单日期（含首尾最多 366 天）、客户 Id（正整数）与原币币种（CNY / USD / EUR / HKD / GBP / JPY）；金额均为订单原币、绝不跨币种合计；成本 / 利润 / 利润率为未知（无历史成本依据），当前价估算为独立口径（币种未知，仅估算）";
    private const string DisclaimerText = "本预览为只读订单利润暂估证据：销售额保留订单原币；成本 / 利润 / 利润率为未知（无历史成本依据）；当前价估算仅作独立口径（币种未知，仅估算），绝不推断实际利润或利润率";
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
        "orderId", "customerId",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("orderId", "订单Id", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("customerId", "客户Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("orderNo", "订单号", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("orderDate", "订单日期", ReportConfigurationConstants.TypeDate, null, filterable: true),
        Field("customerName", "客户名称", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currency", "原币币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("currencyLabel", "原币币种标签", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("salesAmount", "销售额(原币)", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("salesAmountLabel", "销售额口径", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("costAmount", "成本金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("profit", "利润", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("profitRate", "利润率%", ReportConfigurationConstants.TypeNumber, "%", filterable: false),
        Field("costEvidence", "成本证据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("profitEvidence", "利润证据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currentPriceEstimate", "当前价估算(币种未知)", ReportConfigurationConstants.TypeNumber, "币种未知，仅估算", filterable: false),
        Field("currentPriceEstimateLabel", "当前价估算口径", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currentPriceEstimateReason", "估算说明", ReportConfigurationConstants.TypeText, null, filterable: false),
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

        var items = await _reportService.GetOrderProfitEstimateAsync(start, end, scope, filter);

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

    private static (DateTime Start, DateTime End, OrderProfitEstimateFilterDto? Filter) BuildQuery(
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
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetOrderProfit} 不支持的筛选字段：{key}");
            }
        }

        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;

        var filterDto = customerId is null && currency is null
            ? null
            : new OrderProfitEstimateFilterDto
            {
                CustomerId = customerId,
                Currency = currency,
            };

        return (startDate, endDate, filterDto);
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
            SortingExplanation = "本数据集不支持任意排序：稳定按订单日期降序 → 订单Id 降序排序（与固定报表一致）",
        };
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

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }

    private static Dictionary<string, object?> BuildRow(
        ReportDtos.OrderProfitItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("orderId", StringComparer.OrdinalIgnoreCase)) row["orderId"] = item.OrderId;
        if (fieldKeys.Contains("customerId", StringComparer.OrdinalIgnoreCase)) row["customerId"] = item.CustomerId;
        if (fieldKeys.Contains("orderNo", StringComparer.OrdinalIgnoreCase)) row["orderNo"] = item.OrderNo;
        if (fieldKeys.Contains("orderDate", StringComparer.OrdinalIgnoreCase)) row["orderDate"] = item.OrderDate;
        if (fieldKeys.Contains("customerName", StringComparer.OrdinalIgnoreCase)) row["customerName"] = item.CustomerName;
        if (fieldKeys.Contains("currency", StringComparer.OrdinalIgnoreCase)) row["currency"] = item.Currency;
        if (fieldKeys.Contains("currencyLabel", StringComparer.OrdinalIgnoreCase)) row["currencyLabel"] = item.CurrencyLabel;
        if (fieldKeys.Contains("salesAmount", StringComparer.OrdinalIgnoreCase)) row["salesAmount"] = item.SalesAmount;
        if (fieldKeys.Contains("salesAmountLabel", StringComparer.OrdinalIgnoreCase)) row["salesAmountLabel"] = item.SalesAmountLabel;
        if (fieldKeys.Contains("costAmount", StringComparer.OrdinalIgnoreCase)) row["costAmount"] = item.CostAmount;
        if (fieldKeys.Contains("profit", StringComparer.OrdinalIgnoreCase)) row["profit"] = item.Profit;
        if (fieldKeys.Contains("profitRate", StringComparer.OrdinalIgnoreCase)) row["profitRate"] = item.ProfitRate;
        if (fieldKeys.Contains("costEvidence", StringComparer.OrdinalIgnoreCase)) row["costEvidence"] = item.CostEvidence;
        if (fieldKeys.Contains("profitEvidence", StringComparer.OrdinalIgnoreCase)) row["profitEvidence"] = item.ProfitEvidence;
        if (fieldKeys.Contains("currentPriceEstimate", StringComparer.OrdinalIgnoreCase)) row["currentPriceEstimate"] = item.CurrentPriceEstimate;
        if (fieldKeys.Contains("currentPriceEstimateLabel", StringComparer.OrdinalIgnoreCase)) row["currentPriceEstimateLabel"] = item.CurrentPriceEstimateLabel;
        if (fieldKeys.Contains("currentPriceEstimateReason", StringComparer.OrdinalIgnoreCase)) row["currentPriceEstimateReason"] = item.CurrentPriceEstimateReason;
        return row;
    }
}
