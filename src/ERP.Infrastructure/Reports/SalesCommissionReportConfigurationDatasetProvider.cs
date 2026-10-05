using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-300 Stage 2）的业务员提成数据集适配器：暴露既有业务员提成表
/// （<see cref="IReportService.GetSalesCommissionAsync"/> / <see cref="IReportService.GetDynamicSalesCommissionReportAsync"/>）
/// 的有限字段白名单（业务员桶 / 原币 / 订单数 / 已知原币金额小计 / 未知利润 / 未知利润率 / 当前参考提成比例 /
/// 未知提成额 / 显式来源依据）与只读、有界预览口径。
/// <para>预览委托既有动态业务员提成证据报表语义（持久化业务员桶 × 原始原币证据行），金额仅已知币种签名小计、
/// 未知 / 无效币种金额为 null、利润 / 利润率 / 提成额恒为未知（null）、提成比例为可空的「当前参考比例」、
/// 绝不跨币种合计；不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」业务员提成表（sales-commission）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class SalesCommissionReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public SalesCommissionReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetSalesCommission;

    private const string Label = "业务员提成表";
    private const string Grain = "业务员提成表（一行一条持久化业务员桶 × 原始原币证据行）";
    private const string CurrencyUnitSemantics = "金额按业务员桶 × 原币独立小计，绝不跨币种合计；未知 / 无效币种金额与未知利润 / 利润率 / 提成额为未知（null，绝不回落为 0）";
    private const string RequiredMenuCode = "sales-commission";
    private const string RequiredMenuText = "业务员提成表";
    private const string ReadOnlyText = "只读业务员提成数据集：仅按既有报表服务读取已审核销售订单证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限业务员提成证据字段白名单；筛选仅限业务员 Id（正整数）、业务员姓名关键字（去首尾空白最多 80 字符、字面文本）与原币币种（CNY / USD / EUR / HKD / GBP / JPY）；金额按业务员桶 × 原币独立小计、利润 / 利润率 / 提成额恒为未知，绝不跨币种合计；本数据集预览以当天为日期窗口";
    private const string DisclaimerText = "本预览为只读业务员提成证据：金额取自已审核、未删除、授权客户的销售订单原币金额，非实际收款金额；利润 / 利润率 / 提成额未知，不回落为 0；提成比例为当前参考（非历史约定比例 / 实际提成 / 客户代理费 / 台账分录）";
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
        "salesmanId", "orderCount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("salesmanId", "业务员Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("salesmanName", "业务员", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("salesmanIdentityEvidence", "业务员身份依据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currency", "原币币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("currencyLabel", "原币币种标签", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("orderCount", "已审核订单数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("salesAmount", "已知原币金额小计", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("amountLabel", "金额口径", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currencyEvidence", "币种证据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("profit", "利润(未知)", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("profitEvidence", "利润依据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("profitRate", "利润率%(未知)", ReportConfigurationConstants.TypeNumber, "%", filterable: false),
        Field("profitRateEvidence", "利润率依据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("commissionRate", "提成比例%(当前参考)", ReportConfigurationConstants.TypeNumber, "%", filterable: false),
        Field("commissionRateEvidence", "提成比例依据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("commissionAmount", "提成额(未知)", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("commissionEvidence", "提成依据", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("sourceLabel", "来源依据", ReportConfigurationConstants.TypeText, null, filterable: false),
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

        var request = new DynamicSalesCommissionReportRequest
        {
            Fields = fieldKeys.ToList(),
            Start = DateTime.Today,
            End = DateTime.Today,
            Filter = filter,
            Page = page,
            PageSize = pageSize,
        };

        var pageDto = await _reportService.GetDynamicSalesCommissionReportAsync(request, scope);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = pageDto.Rows;

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
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

    private static SalesCommissionFilterDto? BuildFilter(ReportConfigurationDefinition definition)
    {
        long? salesmanId = null;
        string? salesmanName = null;
        string? currency = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "salesmanid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    salesmanId = ReportConfigurationDatasetTranslation.CoalesceLong(salesmanId, filter.Value, "salesmanId");
                    break;
                case "salesmanname":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    salesmanName = ReportConfigurationDatasetTranslation.CoalesceString(salesmanName, filter.Value, "salesmanName");
                    break;
                case "currency":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetSalesCommission} 不支持的筛选字段：{key}");
            }
        }

        if (salesmanId is null && salesmanName is null && currency is null)
            return null;

        return new SalesCommissionFilterDto
        {
            SalesmanId = salesmanId,
            SalesmanName = salesmanName,
            Currency = currency,
        };
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
            SortingExplanation = "本数据集不支持任意排序：稳定按业务员Id → 原币币种排序（与既有业务员提成表一致）",
        };
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

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}
