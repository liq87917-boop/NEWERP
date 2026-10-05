using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-305 Stage 2）的退税汇总数据集适配器：把既有
/// 「退税汇总」（report:tax-refund-summary）固定报表的有限字段白名单（退税期间 / 记录数 / 已申报数 / 已退税数 /
/// 出口额 / 可退 / 已退 / 未退税额）与只读、有界预览口径暴露为统一受控数据集。
/// <para>预览复用 <see cref="IReportService.GetTaxRefundSummaryAsync"/> 的既有聚合语义；因旧算法只按退税期间分组、
/// 不携带原币币种，而 <see cref="BaseTaxRefund.Currency"/> 已存在，适配器在调用旧合计<strong>之前</strong>按期间
/// 探测出口额原币币种：任一期间出现多个原币币种即拒绝预览（fail closed，绝不静默跨币种合计），
/// 单一币种安全分区才委托旧服务。出口额为原币、可退 / 已退 / 未退为人民币（与既有报表口径一致）。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」退税汇总（tax-refund-summary）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）特权数据范围（全局无范围旧计算仅特权账号可执行，
/// 受限制 / 撤销范围一律 fail closed）。</para>
/// </summary>
public sealed class TaxRefundSummaryReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public TaxRefundSummaryReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetTaxRefundSummary;

    private const string Label = "退税汇总";
    private const string Grain = "退税汇总（一行一个退税期间分区；仅单一原币币种安全分区才合计）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；按退税期间聚合；不跨币种换算或合并";
    private const string RequiredMenuCode = "tax-refund-summary";
    private const string RequiredMenuText = "退税汇总表";
    private const string ReadOnlyText = "只读退税汇总数据集：仅按既有报表服务读取退税台账并按期间聚合，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限退税汇总字段白名单；出口额按原币、可退 / 已退 / 未退按人民币（与既有报表一致）；同一期间原币币种一致才合计出口额，任一期间混合币种即拒绝预览（绝不静默跨币种合计）；来源分区上限 2000 个";
    private const string DisclaimerText = "本预览为只读退税汇总证据：出口额为原币、可退 / 已退 / 未退为人民币（与既有报表一致）；混合原币期间拒绝预览；不构成税务申报 / 退税到账结论";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxSourcePartitions = 2000;
    private const int MaxMixedEvidenceItems = 10;

    private const string UnknownCurrency = "未知币种";
    private const string BlankPeriod = "(未填期间)";

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
        "recordCount", "declaredCount", "refundedCount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("refundPeriod", "退税期间", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("recordCount", "记录数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("declaredCount", "已申报数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("refundedCount", "已退税数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("exportAmount", "出口额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("refundableAmount", "可退税额", ReportConfigurationConstants.TypeNumber, "人民币", filterable: false),
        Field("refundedAmount", "已退税额", ReportConfigurationConstants.TypeNumber, "人民币", filterable: false),
        Field("unrefundedAmount", "未退税额", ReportConfigurationConstants.TypeNumber, "人民币", filterable: false),
    };

    private static ReportConfigurationFieldDto Field(string key, string label, string type, string? unit, bool filterable = false)
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

        // 1) 聚合前按期间探测出口额原币币种：任一期间混合币种即拒绝（fail closed，绝不委托旧合计）。
        var mixed = await DetectMixedCurrencyAsync(cancellationToken);
        if (mixed.Count > 0)
            throw new BusinessException(BuildMixedCurrencyError(mixed), ErrorCodes.RuleConflict);

        // 2) 单一币种安全分区才委托既有旧报表服务（复用既有聚合 / 排序，不复制任何求和 / 分组算法）。
        var items = await _reportService.GetTaxRefundSummaryAsync();

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
            SourceEvidenceCount = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groupings = new List<string>(),
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

        // 旧退税汇总为全局无范围计算：仅特权账号可执行，受限制 / 未解析范围一律 fail closed。
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId.Value);
        if (!scope.IsPrivileged)
        {
            throw new BusinessException(
                $"当前账号不是全量数据范围账号：拒绝预览{Label}"
                + "（fail closed，不执行全局汇总查询）",
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

    private async Task<List<PeriodCurrencyMixture>> DetectMixedCurrencyAsync(CancellationToken cancellationToken)
    {
        // 与旧服务同口径的源过滤：仅未删除。只投影 (期间, 币种) 去重对用于聚合前安全探测。
        var pairs = await _db.BaseTaxRefunds
            .Where(x => !x.IsDeleted)
            .Select(x => new { Period = x.RefundPeriod, Currency = x.Currency })
            .Distinct()
            .Take(MaxSourcePartitions + 1)
            .ToListAsync(cancellationToken);

        if (pairs.Count > MaxSourcePartitions)
        {
            throw new BusinessException(
                $"{Label}的授权范围内来源分区超过 {MaxSourcePartitions} 个，请缩小范围后重试",
                ErrorCodes.RuleConflict);
        }

        return pairs
            .GroupBy(p => PeriodKey(p.Period))
            .Select(g => new PeriodCurrencyMixture(
                g.Key,
                g.Select(p => CurrencyKey(p.Currency))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList()))
            .Where(x => x.Currencies.Count > 1)
            .OrderByDescending(x => x.Period, StringComparer.Ordinal)
            .ToList();
    }

    private static string PeriodKey(string? period)
        => string.IsNullOrWhiteSpace(period) ? BlankPeriod : period;

    private static string CurrencyKey(string? currency)
    {
        var value = (currency ?? string.Empty).Trim().ToUpperInvariant();
        return value.Length == 0 ? UnknownCurrency : value;
    }

    private static string BuildMixedCurrencyError(IReadOnlyList<PeriodCurrencyMixture> mixed)
    {
        var shown = mixed.Take(MaxMixedEvidenceItems).Select(x =>
            $"期间「{x.Period}」（{string.Join("/", x.Currencies)}）");
        var suffix = mixed.Count > MaxMixedEvidenceItems
            ? $"；另有 {mixed.Count - MaxMixedEvidenceItems} 个期间未列出"
            : string.Empty;
        return $"{Label}拒绝预览：检测到 {mixed.Count} 个退税期间在原币币种上混合（旧算法只按期间合计会静默跨币种），"
               + $"请先按单一币种拆分退税台账后重试。证据：{string.Join("、", shown)}{suffix}";
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
            SortingExplanation = "本数据集不支持任意排序：稳定按退税期间降序（与既有退税汇总一致）",
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

    private static Dictionary<string, object?> BuildRow(ReportDtos.TaxRefundSummaryItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("refundPeriod", StringComparer.OrdinalIgnoreCase)) row["refundPeriod"] = item.RefundPeriod;
        if (fieldKeys.Contains("recordCount", StringComparer.OrdinalIgnoreCase)) row["recordCount"] = item.RecordCount;
        if (fieldKeys.Contains("declaredCount", StringComparer.OrdinalIgnoreCase)) row["declaredCount"] = item.DeclaredCount;
        if (fieldKeys.Contains("refundedCount", StringComparer.OrdinalIgnoreCase)) row["refundedCount"] = item.RefundedCount;
        if (fieldKeys.Contains("exportAmount", StringComparer.OrdinalIgnoreCase)) row["exportAmount"] = item.ExportAmount;
        if (fieldKeys.Contains("refundableAmount", StringComparer.OrdinalIgnoreCase)) row["refundableAmount"] = item.RefundableAmount;
        if (fieldKeys.Contains("refundedAmount", StringComparer.OrdinalIgnoreCase)) row["refundedAmount"] = item.RefundedAmount;
        if (fieldKeys.Contains("unrefundedAmount", StringComparer.OrdinalIgnoreCase)) row["unrefundedAmount"] = item.UnrefundedAmount;
        return row;
    }

    private sealed record PeriodCurrencyMixture(string Period, List<string> Currencies);
}
