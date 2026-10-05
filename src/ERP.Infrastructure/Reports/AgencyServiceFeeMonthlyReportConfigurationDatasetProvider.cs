using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-303 Stage 2）的代理服务费月度汇总数据集适配器：把既有
/// 「动态代理服务费月度汇总报表」（dynamic:agency-service-fee-monthly）的有限字段白名单（18 个字段）
/// 暴露为统一受控数据集。预览<strong>直接复用</strong>
/// <see cref="AgencyServiceFeeMonthlySummaryService.ForQueryAsync"/>（ERP-110 权威派生），不再复制任何分组 / 聚合算法。
/// <para>行粒度为「一条「对账日期所属年月 + 客户 + 原币」分组」，金额按原币呈现、绝不跨币种换算或合并，
/// 仅未删除且已登记的对账单计入原币合计，草稿与已作废单独计数。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」客户资料（customer）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class AgencyServiceFeeMonthlyReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public AgencyServiceFeeMonthlyReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly;

    private const string Label = "代理服务费月度汇总";
    private const string Grain = "代理服务费对账单月度汇总证据（一行一条「对账日期所属年月 + 客户 + 原币」分组）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；按对账月份/客户分组；不跨币种换算或合并";
    private const string RequiredMenuCode = AgencyServiceFeeReconciliationRules.RequiredMenuCode;
    private const string RequiredMenuText = AgencyServiceFeeReconciliationRules.RequiredMenuText;
    private const string ReadOnlyText = "只读代理服务费月度汇总数据集：仅按既有 ERP-110 口径读取未删除对账单证据并按「年月 + 客户 + 原币」汇总，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限代理服务费月度汇总字段白名单（年月 / 客户 / 原币 / 已登记 / 草稿 / 已作废张数与金额）；客户 / 币种有界筛选；分页页码 ≥ 1、每页 1~200；金额按原币、绝不跨币种换算或合并；不执行任意 SQL、不做写入";
    private const string DisclaimerText = "本预览为只读代理服务费月度汇总证据：金额与计数只是证据数字，不代表收入 / 应收 / 已收款；服务期间跨月不按期间分摊";
    private const int DefaultPageSize = AgencyServiceFeeMonthlySummaryRules.DefaultPageSize;
    private const int MaxPageSize = AgencyServiceFeeMonthlySummaryRules.MaxPageSize;

    private static readonly IReadOnlyList<string> GroupingKeys = new[] { ReportConfigurationConstants.GroupNone };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityPaging,
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
        "statementYear", "statementMonth", "customerId",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("statementYear", "年份", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("statementMonth", "月份", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("statementMonthText", "对账日期所属月份", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("customerId", "客户Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("customerCode", "客户编码", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("customerName", "客户名称", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currency", "币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("amountDecimals", "币种小数位", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("registeredCount", "已登记张数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("registeredTotalAmount", "已登记原币合计", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("registeredTotalAmountText", "已登记原币合计文案", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("draftCount", "草稿张数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("draftTotalAmount", "草稿金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("draftTotalAmountText", "草稿金额文案", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("voidedCount", "已作废张数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("voidedTotalAmount", "已作废金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("voidedTotalAmountText", "已作废金额文案", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("statementCount", "对账单总张数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
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

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);
        var query = BuildQuery(definition, page, pageSize);

        var view = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(_db, query, scope);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = view.Rows.Select(r => BuildRow(r, fieldKeys)).ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = view.Total,
            MatchedCount = view.Total,
            Page = view.Page,
            PageSize = view.PageSize,
            TotalPages = view.TotalPages,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics, ReadOnlyText, BoundaryText, DisclaimerText,
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

    private static AgencyServiceFeeMonthlySummaryQuery BuildQuery(
        ReportConfigurationDefinition definition, int page, int pageSize)
    {
        long? customerId = null;
        string? currency = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "customerid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    customerId = ReportConfigurationDatasetTranslation.CoalesceLong(customerId, filter.Value, "customerId");
                    break;
                case "currency":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                default:
                    throw BusinessException.InvalidParameter(
                        $"数据集 {ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly} 不支持的筛选字段：{key}");
            }
        }

        return new AgencyServiceFeeMonthlySummaryQuery
        {
            CustomerId = customerId,
            Currency = currency,
            Page = page,
            PageSize = pageSize,
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

    private static Dictionary<string, object?> BuildRow(AgencyServiceFeeMonthlySummaryRow row, IReadOnlyList<string> fieldKeys)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["statementYear"] = row.StatementYear,
            ["statementMonth"] = row.StatementMonth,
            ["statementMonthText"] = row.StatementMonthText,
            ["customerId"] = row.CustomerId,
            ["customerCode"] = row.CustomerCode,
            ["customerName"] = row.CustomerName,
            ["currency"] = row.Currency,
            ["amountDecimals"] = row.AmountDecimals,
            ["registeredCount"] = row.RegisteredCount,
            ["registeredTotalAmount"] = row.RegisteredTotalAmount,
            ["registeredTotalAmountText"] = row.RegisteredTotalAmountText,
            ["draftCount"] = row.DraftCount,
            ["draftTotalAmount"] = row.DraftTotalAmount,
            ["draftTotalAmountText"] = row.DraftTotalAmountText,
            ["voidedCount"] = row.VoidedCount,
            ["voidedTotalAmount"] = row.VoidedTotalAmount,
            ["voidedTotalAmountText"] = row.VoidedTotalAmountText,
            ["statementCount"] = row.StatementCount,
        };

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            result[key] = source.TryGetValue(key, out var value) ? value : null;

        return result;
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
            SortingExplanation = "本数据集不支持任意排序：稳定按年份 → 月份 → 客户 Id → 币种排序（与既有代理服务费月度汇总一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}

