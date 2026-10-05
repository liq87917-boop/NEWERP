using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-300 Stage 2）的商品销量排名数据集适配器：暴露既有
/// <see cref="IReportService.GetProductSalesRankingAsync"/> 固定报表的有限字段白名单
/// （排名 / 商品 / 规格 / 单位 / 发货数量 / 当前价估算金额）与只读、有界预览口径。
/// <para>预览委托既有固定报表服务语义（已审核销售出库发货数量，按商品 / 规格 / 单位分桶），
/// 发货数量按基础单位独立、绝不跨单位合计；金额为数量 × 商品当前售价的估算（币种未知，仅估算，非实际发货收入）；
/// 不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」商品销量排名榜（product-sales-ranking）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class ProductSalesRankingReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public ProductSalesRankingReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetProductSalesRanking;

    private const string Label = "商品销量排名榜";
    private const string Grain = "商品销量排名榜（一行一条商品 × 规格 × 单位发货数量排名桶）";
    private const string CurrencyUnitSemantics = "发货数量按基础单位独立（绝不跨单位合计）；金额为数量 × 商品当前售价的估算（币种未知，仅估算）";
    private const string RequiredMenuCode = "product-sales-ranking";
    private const string RequiredMenuText = "商品销量排名榜";
    private const string ReadOnlyText = "只读商品销量排名数据集：仅按既有固定报表服务读取已审核销售出库发货证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：仅统计已审核、未删除、当前账号数据范围内的销售出库明细，按商品 / 规格 / 单位独立分桶；发货数量为基础单位、绝不跨单位合计；金额为数量 × 商品当前售价的估算（币种未知，仅估算，非实际发货收入）；本数据集预览以当天为日期窗口、Top 固定 200 条";
    private const string DisclaimerText = "本预览为只读发货数量证据：仅展示 Top 限定数量的排名行，绝不声称 Top 之外完整，也绝不跨单位合计数量；金额为当前价估算（币种未知，非实际发货收入）";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;
    private const int MaxTop = 200;

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
        "rank", "productId",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("rank", "排名", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("productId", "商品Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("productCode", "商品编码", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("productName", "商品名称", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("spec", "规格", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("unit", "单位", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("totalQuantity", "发货数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
        Field("totalAmount", "当前价估算金额", ReportConfigurationConstants.TypeNumber, "当前价估算(币种未知)", filterable: false),
        Field("amountLabel", "金额口径", ReportConfigurationConstants.TypeText, null, filterable: false),
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

        // 每次请求重新校验身份 / 菜单授权（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 对「当前」目录重新校验有界定义：拒绝未知字段 / 未知筛选 / 不支持的能力（先于源读取）。
        ReportConfigurationRules.Validate(definition, BuildDataset());

        var (page, pageSize) = ValidatePageBounds(parameters);
        var fieldKeys = NormalizeFields(definition.Fields);
        var filter = BuildFilter(definition);
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var items = await _reportService.GetProductSalesRankingAsync(DateTime.Today, DateTime.Today, MaxTop, scope, filter);

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

    private static ProductSalesRankingFilterDto? BuildFilter(ReportConfigurationDefinition definition)
    {
        long? productId = null;
        string? unit = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "productid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    productId = ReportConfigurationDatasetTranslation.CoalesceLong(productId, filter.Value, "productId");
                    break;
                case "unit":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    unit = ReportConfigurationDatasetTranslation.CoalesceString(unit, filter.Value, "unit");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetProductSalesRanking} 不支持的筛选字段：{key}");
            }
        }

        if (productId is null && unit is null)
            return null;

        return new ProductSalesRankingFilterDto
        {
            ProductId = productId,
            Unit = unit,
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
            SortingExplanation = "本数据集不支持任意排序：稳定按发货数量降序 → 商品Id → 商品名称 → 规格 → 单位排序（与固定报表一致）",
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
        ReportDtos.ProductSalesRankItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("rank", StringComparer.OrdinalIgnoreCase)) row["rank"] = item.Rank;
        if (fieldKeys.Contains("productId", StringComparer.OrdinalIgnoreCase)) row["productId"] = item.ProductId;
        if (fieldKeys.Contains("productCode", StringComparer.OrdinalIgnoreCase)) row["productCode"] = item.ProductCode;
        if (fieldKeys.Contains("productName", StringComparer.OrdinalIgnoreCase)) row["productName"] = item.ProductName;
        if (fieldKeys.Contains("spec", StringComparer.OrdinalIgnoreCase)) row["spec"] = item.Spec;
        if (fieldKeys.Contains("unit", StringComparer.OrdinalIgnoreCase)) row["unit"] = item.Unit;
        if (fieldKeys.Contains("totalQuantity", StringComparer.OrdinalIgnoreCase)) row["totalQuantity"] = item.TotalQuantity;
        if (fieldKeys.Contains("totalAmount", StringComparer.OrdinalIgnoreCase)) row["totalAmount"] = item.TotalAmount;
        if (fieldKeys.Contains("amountLabel", StringComparer.OrdinalIgnoreCase)) row["amountLabel"] = item.AmountLabel;
        return row;
    }
}
