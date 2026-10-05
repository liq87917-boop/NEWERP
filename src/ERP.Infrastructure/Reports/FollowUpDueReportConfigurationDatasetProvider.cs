using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-302 Stage 2）的跟进提醒数据集适配器：把既有跟进提醒报表
/// （<see cref="IReportService.GetFollowUpDueAsync"/> / <see cref="IReportService.GetDynamicFollowUpDueReportAsync"/>）
/// 的有限字段白名单（跟进记录持久化字段 + 由下次跟进日期 / as-of 日期派生的到期天数与到期状态）暴露为统一受控数据集。
/// <para>预览委托既有动态跟进提醒证据报表语义（按当前账号业务员数据范围过滤，已逾期 / 今日到期 / 即将到期），
/// 到期口径与既有 <see cref="ReportService.GetFollowUpDueAsync"/> 完全一致；本数据集无金额 / 币种，日期为自然日，
/// 到期天数与状态只由下次跟进日期与 as-of 日期派生，不构成催收 / 账龄 / 会计结论。不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」跟进提醒（follow-up-due）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class FollowUpDueReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public FollowUpDueReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetFollowUpDue;

    private const string Label = "跟进提醒";
    private const string Grain = "跟进提醒（一行一条当前账号数据范围内的客户跟进记录）";
    private const string CurrencyUnitSemantics = "无金额/币种；日期为自然日；到期天数与到期状态由下次跟进日期与 as-of 日期派生";
    private const string RequiredMenuCode = DynamicFollowUpDueReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicFollowUpDueReportRules.RequiredMenuText;
    private const string ReadOnlyText = "只读跟进提醒数据集：仅按既有报表服务读取当前账号数据范围内的客户跟进记录，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限跟进提醒字段白名单；筛选仅限到期状态（overdue / today / upcoming）；as-of 日期为当天、提前天数固定 7 天；分页页码 ≥ 1、每页 1~200；结果限定在当前账号业务员数据范围（特权账号不受限）；不执行任意 SQL、不做写入";
    private const string DisclaimerText = "本预览为只读跟进提醒证据：到期天数 / 到期状态仅由下次跟进日期与 as-of 日期派生（已逾期 / 今日到期 / 即将到期），不构成催收、账龄或任何会计 / 结算结论";
    private const int DefaultPageSize = DynamicFollowUpDueReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicFollowUpDueReportRules.MaxPageSize;
    private const int AheadDays = 7;

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
        "id", "customerId", "salesmanId", "dueDays",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("id", "跟进记录Id", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("followNo", "跟进编号", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("followDate", "跟进日期", ReportConfigurationConstants.TypeDate, null, filterable: false),
        Field("customerId", "客户Id", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("customerName", "客户名称", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("followType", "跟进方式", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("contactPerson", "对接人", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("salesmanId", "跟进人Id", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("salesmanName", "跟进人姓名", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("subject", "跟进主题", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("content", "跟进内容", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("result", "跟进结果", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("nextFollowDate", "下次跟进日期", ReportConfigurationConstants.TypeDate, null, filterable: false),
        Field("dueDays", "到期天数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("dueStatus", "到期状态", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("remark", "备注", ReportConfigurationConstants.TypeText, null, filterable: false),
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
        var dueStatus = BuildFilter(definition);
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var request = new DynamicFollowUpDueReportRequest
        {
            Fields = fieldKeys.ToList(),
            AsOfDate = DateTime.Today,
            AheadDays = AheadDays,
            DueStatus = dueStatus,
            Page = page,
            PageSize = pageSize,
        };

        var pageDto = await _reportService.GetDynamicFollowUpDueReportAsync(request, scope);

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

    private static string? BuildFilter(ReportConfigurationDefinition definition)
    {
        string? dueStatus = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            if (key.Equals("dueStatus", StringComparison.OrdinalIgnoreCase))
            {
                ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                dueStatus = ReportConfigurationDatasetTranslation.CoalesceString(dueStatus, filter.Value, "dueStatus");
            }
            else
            {
                throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetFollowUpDue} 不支持的筛选字段：{key}");
            }
        }

        return dueStatus;
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
            SortingExplanation = "本数据集不支持任意排序：稳定按下次跟进日期升序（逾期最久在前）→ 客户名称 → 跟进记录 Id（与既有跟进提醒一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}

