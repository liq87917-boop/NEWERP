using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-305 Stage 2）的采购成本分析数据集适配器：把既有
/// 「采购成本分析」（report:purchase-cost）固定报表的有限字段白名单（供应商名称 / 类型 / 订单数 /
/// 采购金额 / 平均单笔 / 最近下单）与只读、有界预览口径暴露为统一受控数据集。
/// <para>预览复用 <see cref="IReportService.GetPurchaseCostAsync"/> 的既有聚合语义；因旧算法只按供应商分组、
/// 不携带币种，而 <see cref="PurchaseOrder.Currency"/> 已存在，适配器在调用旧合计<strong>之前</strong>按供应商
/// 探测原币币种：任一供应商出现多个原币币种即拒绝预览（fail closed，绝不静默跨币种合计），
/// 单一币种安全分区才委托旧服务。日期窗口有界（含首尾最多 366 天）。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」采购成本分析（purchase-cost）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）特权数据范围（全局无范围旧计算仅特权账号可执行，
/// 受限制 / 撤销范围一律 fail closed）。</para>
/// </summary>
public sealed class PurchaseCostReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public PurchaseCostReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetPurchaseCost;

    private const string Label = "采购成本分析";
    private const string Grain = "采购成本分析（一行一个供应商分区；仅单一原币币种安全分区才合计）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；按供应商聚合；不跨币种换算或合并";
    private const string RequiredMenuCode = "purchase-cost";
    private const string RequiredMenuText = "采购成本分析表";
    private const string ReadOnlyText = "只读采购成本分析数据集：仅按既有报表服务读取采购订单并按供应商聚合，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限采购成本字段白名单；日期窗口有界（含首尾最多 366 天）；状态仅排除已取消 / 已驳回（与既有报表一致）；金额按供应商单一原币币种分区合计，任一供应商混合币种即拒绝预览（绝不静默跨币种合计）；来源分区上限 2000 个";
    private const string DisclaimerText = "本预览为只读采购成本证据：金额取自已审核口径采购订单（排除已取消 / 已驳回），按供应商单一币种分区合计；混合币种分区拒绝预览；不构成实际采购成本 / 付款 / 结算结论";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxDateRangeDays = 366;
    private const int MaxSourcePartitions = 2000;
    private const int MaxMixedEvidenceItems = 10;

    private static readonly IReadOnlyList<string> GroupingKeys = new[] { ReportConfigurationConstants.GroupNone };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
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

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "orderCount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("supplierName", "供应商名称", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("supplierType", "供应商类型", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("orderCount", "订单数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("totalAmount", "采购金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("avgAmount", "平均单笔", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("lastOrderDate", "最近下单", ReportConfigurationConstants.TypeDate, null, filterable: false),
        Field("orderDate", "订单日期（筛选）", ReportConfigurationConstants.TypeDate, null, filterable: true),
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
            // 无菜单授权或非特权数据范围 → 该数据集不暴露
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

        // 每次请求重新校验身份 / 菜单授权 / 特权数据范围（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 对「当前」目录重新校验有界定义：拒绝未知字段 / 未知筛选 / 不支持的能力（先于源读取）。
        ReportConfigurationRules.Validate(definition, BuildDataset());

        var (page, pageSize) = ValidatePageBounds(parameters);
        var fieldKeys = NormalizeFields(definition.Fields);
        var (startDate, endDate) = ResolveDateRange(definition);

        // 1) 聚合前按供应商探测原币币种：任一供应商混合币种即拒绝（fail closed，绝不委托旧合计）。
        var mixed = await DetectMixedCurrencyAsync(startDate, endDate, cancellationToken);
        if (mixed.Count > 0)
            throw new BusinessException(BuildMixedCurrencyError(mixed), ErrorCodes.RuleConflict);

        // 2) 单一币种安全分区才委托既有旧报表服务（复用既有聚合 / 排序，不复制任何求和 / 分组算法）。
        var items = await _reportService.GetPurchaseCostAsync(startDate, endDate);

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

        // 旧采购成本为全局无范围计算：仅特权账号可执行，受限制 / 未解析范围一律 fail closed。
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

    private static (DateTime Start, DateTime End) ResolveDateRange(ReportConfigurationDefinition definition)
    {
        DateTime? start = null;
        DateTime? end = null;

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
                default:
                    throw BusinessException.InvalidParameter(
                        $"数据集 {ReportConfigurationConstants.DatasetPurchaseCost} 不支持的筛选字段：{key}");
            }
        }

        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;
        if (endDate < startDate)
            throw BusinessException.InvalidParameter($"{Label}的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw new BusinessException($"{Label}的日期范围最多 {MaxDateRangeDays} 天（含首尾）", ErrorCodes.InvalidParameter);

        return (startDate, endDate);
    }

    private async Task<List<SupplierCurrencyMixture>> DetectMixedCurrencyAsync(
        DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        // 与旧服务同口径的源过滤：未删除、日期窗口、排除已取消 / 已驳回。
        // 仅投影 (供应商 Id, 币种) 去重对用于聚合前安全探测（绝不复制旧分组 / 求和算法）。
        var pairs = await _db.PurchaseOrders
            .Where(o => !o.IsDeleted
                        && o.OrderDate >= start
                        && o.OrderDate <= end
                        && o.Status != DocumentStatus.Cancelled
                        && o.Status != DocumentStatus.Rejected)
            .Select(o => new { o.SupplierId, o.Currency })
            .Distinct()
            .Take(MaxSourcePartitions + 1)
            .ToListAsync(cancellationToken);

        if (pairs.Count > MaxSourcePartitions)
        {
            throw new BusinessException(
                $"{Label}的授权范围内来源分区超过 {MaxSourcePartitions} 个，请缩小日期范围后重试",
                ErrorCodes.RuleConflict);
        }

        return pairs
            .GroupBy(p => p.SupplierId)
            .Select(g => new SupplierCurrencyMixture(
                g.Key,
                g.Select(p => CurrencyKey(p.Currency))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList()))
            .Where(x => x.Currencies.Count > 1)
            .OrderBy(x => x.SupplierId)
            .ToList();
    }

    private static string CurrencyKey(Currency currency)
        => currency.ToString().ToUpperInvariant();

    private static string BuildMixedCurrencyError(IReadOnlyList<SupplierCurrencyMixture> mixed)
    {
        var shown = mixed.Take(MaxMixedEvidenceItems).Select(x =>
            $"供应商#{x.SupplierId}（{string.Join("/", x.Currencies)}）");
        var suffix = mixed.Count > MaxMixedEvidenceItems
            ? $"；另有 {mixed.Count - MaxMixedEvidenceItems} 个供应商未列出"
            : string.Empty;
        return $"{Label}拒绝预览：检测到 {mixed.Count} 个供应商在原币币种上混合（旧算法只按供应商合计会静默跨币种），"
               + $"请先按单一币种拆分采购订单后重试。证据：{string.Join("、", shown)}{suffix}";
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
            SortingExplanation = "本数据集不支持任意排序：稳定按采购金额降序（与既有采购成本分析一致）",
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

    private static Dictionary<string, object?> BuildRow(ReportDtos.PurchaseCostItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("supplierName", StringComparer.OrdinalIgnoreCase)) row["supplierName"] = item.SupplierName;
        if (fieldKeys.Contains("supplierType", StringComparer.OrdinalIgnoreCase)) row["supplierType"] = item.SupplierType;
        if (fieldKeys.Contains("orderCount", StringComparer.OrdinalIgnoreCase)) row["orderCount"] = item.OrderCount;
        if (fieldKeys.Contains("totalAmount", StringComparer.OrdinalIgnoreCase)) row["totalAmount"] = item.TotalAmount;
        if (fieldKeys.Contains("avgAmount", StringComparer.OrdinalIgnoreCase)) row["avgAmount"] = item.AvgAmount;
        if (fieldKeys.Contains("lastOrderDate", StringComparer.OrdinalIgnoreCase)) row["lastOrderDate"] = item.LastOrderDate;
        if (fieldKeys.Contains("orderDate", StringComparer.OrdinalIgnoreCase)) row["orderDate"] = null;
        return row;
    }

    private sealed record SupplierCurrencyMixture(long SupplierId, List<string> Currencies);
}
