using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-301 Stage 2）的柜量与装柜利用率证据数据集适配器：把既有
/// 「柜量与装柜利用率统计」（container-stats）固定报表与动态证据报表的有限字段白名单
/// （装柜日历日 / 原始柜号 / 装柜清单数 / 授权范围客户数 / 箱数 / 毛重 / 体积 / 未知装载率柜型 / 未知原因）
/// 暴露为统一受控数据集，预览复用 <see cref="IReportService.GetDynamicContainerStatsReportAsync"/> 的只读有界语义。
/// <para>行口径与既有 <see cref="IReportService.GetContainerStatsAsync"/>（ERP-251）完全一致：装柜日历日 × 精确原始非空白柜号
/// 证据桶，签名持久化头箱数 / 毛重 / 体积证据，装载率与柜型恒为未知（绝不按 68m³ 估算）。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」柜量与装柜利用率统计（container-stats）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class ContainerStatsReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public ContainerStatsReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetContainerStats;

    private const string Label = "柜量与装柜利用率统计";
    private const string Grain = "柜量与装柜利用率证据桶（一行一个装柜日历日 × 精确原始非空白柜号；空白柜号按装柜清单 Id 独立）";
    private const string CurrencyUnitSemantics = "箱数/毛重/体积按原始单位；装载率与柜型未知；不跨币种换算";
    private const string RequiredMenuCode = "container-stats";
    private const string RequiredMenuText = "柜量与装柜利用率统计";
    private const string ReadOnlyText = "只读柜量与装柜利用率证据数据集：仅按既有报表服务读取已审核、未删除、授权范围装柜清单头证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限柜量与装柜利用率证据字段白名单（装柜日历日 / 原始柜号 / 装柜清单数 / 授权范围客户数 / 箱数 / 毛重 / 体积 / 未知装载率柜型 / 未知原因）；日期窗口有界（含首尾最多 366 天）；来源探测上限 500 张装柜清单；装载率与柜型恒为未知，绝不按 68m³ 估算";
    private const string DisclaimerText = "本预览为只读柜量与装柜利用率证据：箱数 / 毛重 / 体积取自签名持久化装柜清单头，非实际发货 / 实体柜 / 收入 / 收款；装载率 / 柜型未知";
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
        "loadingListCount", "authorizedCustomerCount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("loadingDate", "装柜日历日", ReportConfigurationConstants.TypeDate, null, filterable: true),
        Field("containerNo", "原始柜号", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("loadingListCount", "装柜清单数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("authorizedCustomerCount", "授权范围客户数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("totalCartons", "箱数(cartons)", ReportConfigurationConstants.TypeNumber, "箱数(cartons)", filterable: false),
        Field("totalWeight", "毛重(kg)", ReportConfigurationConstants.TypeNumber, "毛重(kg)", filterable: false),
        Field("totalVolume", "体积(m³)", ReportConfigurationConstants.TypeNumber, "体积(m³)", filterable: false),
        Field("utilizationType", "装载率/柜型(未知)", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("reasons", "未知原因", ReportConfigurationConstants.TypeText, null, filterable: false),
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
        var request = BuildRequest(definition, fieldKeys, page, pageSize);

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var pageDto = await _reportService.GetDynamicContainerStatsReportAsync(request, scope);

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

    private static DynamicContainerStatsReportRequest BuildRequest(
        ReportConfigurationDefinition definition, IReadOnlyList<string> fieldKeys, int page, int pageSize)
    {
        DateTime? start = null;
        DateTime? end = null;
        long? customerId = null;
        string? containerNo = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "loadingdate":
                    ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "loadingDate", ref start, ref end);
                    break;
                case "customerid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    customerId = ReportConfigurationDatasetTranslation.CoalesceLong(customerId, filter.Value, "customerId");
                    break;
                case "containerno":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    containerNo = ReportConfigurationDatasetTranslation.CoalesceString(containerNo, filter.Value, "containerNo");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetContainerStats} 不支持的筛选字段：{key}");
            }
        }

        var request = new DynamicContainerStatsReportRequest
        {
            Fields = fieldKeys.ToList(),
            Start = start ?? DateTime.Today,
            End = end ?? DateTime.Today,
            Page = page,
            PageSize = pageSize,
        };

        if (customerId is not null || !string.IsNullOrEmpty(containerNo))
        {
            request.Filter = new DynamicContainerStatsReportFilterDto
            {
                CustomerId = customerId,
                ContainerNo = containerNo,
            };
        }

        return request;
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
            SortingExplanation = "本数据集不支持任意排序：稳定按装柜日历日升序 → 分组身份键升序（与既有柜量统计一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}
