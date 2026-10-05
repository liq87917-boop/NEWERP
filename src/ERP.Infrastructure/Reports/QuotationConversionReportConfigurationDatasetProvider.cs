using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-302 Stage 2）的报价成交率数据集适配器：把既有报价成交率分析报表
/// （<see cref="IReportService.GetQuotationConversionAsync"/>）的有限字段白名单（业务员 × 原币分桶的
/// 有效报价数 / 已转出数 / 成交率 / 金额等）暴露为统一受控数据集。
/// <para>预览委托既有报价成交率语义（业务员 × 原币聚合，分子 = 已转 PI / 已转销售订单 / 状态已完成），
/// 金额均为报价单原币、绝不跨币种换算或合并；本数据集预览以当天为日期窗口。不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」报价单（quotation）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class QuotationConversionReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public QuotationConversionReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetQuotationConversion;

    private const string Label = "报价成交率";
    private const string Grain = "报价成交率（一行一个业务员 × 原币分桶）";
    private const string CurrencyUnitSemantics = "金额均为报价单原币；按业务员 × 原币分列；绝不跨币种换算或合并";
    private const string RequiredMenuCode = DynamicQuotationConversionReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicQuotationConversionReportRules.RequiredMenuText;
    private const string ReadOnlyText = "只读报价成交率数据集：仅按既有报表服务读取当前账号数据范围内的报价单证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限报价成交率分桶字段；筛选仅限业务员姓名关键字与原币币种（CNY / USD / EUR / HKD / GBP / JPY 或未知币种）；本数据集预览以当天为日期窗口；分页页码 ≥ 1、每页 1~200；金额均为报价单原币、绝不跨币种合计；不执行任意 SQL、不做写入";
    private const string DisclaimerText = "本预览为只读报价成交率证据：金额均为报价单原币，不折算、不默认币种、不推断汇率；本表不构成收入确认、应收或任何会计 / 结算结论";
    private const int DefaultPageSize = DynamicQuotationConversionReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicQuotationConversionReportRules.MaxPageSize;

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
        "conversionRate", "avgConvertedAmount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("salesmanName", "业务员", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("currency", "原币币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("quotationCount", "有效报价数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("convertedCount", "已转出数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("conversionRate", "成交率%", ReportConfigurationConstants.TypeNumber, "%", filterable: false),
        Field("expiredCount", "已过期未成交", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("cancelledCount", "已作废", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("totalAmount", "有效报价金额(原币)", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("convertedAmount", "已转出金额(原币)", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("avgConvertedAmount", "单笔成交均价(原币)", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
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
        var filter = BuildFilter(definition);
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var start = DateTime.Today;
        var end = DateTime.Today;
        var filterText = DynamicQuotationConversionReportRules.BuildFilterContext(filter);
        var items = await _reportService.GetQuotationConversionAsync(start, end, scope, filter);
        var pageDto = DynamicQuotationConversionReportRules.BuildPage(items, fieldKeys, page, pageSize, start, end, filterText);

        var columns = fieldKeys.Select(BuildColumn).ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = pageDto.Rows,
            Total = pageDto.Total,
            MatchedCount = pageDto.Total,
            Page = pageDto.Page,
            PageSize = pageDto.PageSize,
            TotalPages = pageDto.TotalPages,
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

    private static QuotationConversionFilterDto? BuildFilter(ReportConfigurationDefinition definition)
    {
        string? salesmanName = null;
        string? currency = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "salesmanname":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    salesmanName = ReportConfigurationDatasetTranslation.CoalesceString(salesmanName, filter.Value, "salesmanName");
                    break;
                case "currency":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetQuotationConversion} 不支持的筛选字段：{key}");
            }
        }

        if (salesmanName is null && currency is null)
            return null;

        return DynamicQuotationConversionReportRules.NormalizeFilter(new QuotationConversionFilterDto
        {
            SalespersonName = salesmanName,
            Currency = currency,
        });
    }

    private static IReadOnlyList<string> NormalizeFields(IReadOnlyList<string>? fields)
    {
        var selected = new List<string>();
        foreach (var raw in fields ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var field = Fields.First(f => string.Equals(f.Key, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!selected.Contains(field.Key, StringComparer.Ordinal))
                selected.Add(field.Key);
        }

        if (selected.Count == 0)
            selected.AddRange(Fields.Select(f => f.Key));

        return selected;
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
            SortingExplanation = "本数据集不支持任意排序：稳定按成交率降序 → 报价数降序 → 业务员 → 原币币种（与既有报价成交率一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}
