using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-299 Stage 2）的库存预警表数据集适配器：暴露既有
/// <see cref="IReportService.GetStockAlertAsync"/> 固定报表的有限字段白名单
/// （商品 / 规格 / 单位 / 仓库 / 现存量 / 安全库存 / 库存上限 / 差额 / 预警级别）与只读、有界预览口径。
/// <para>预览委托既有固定报表服务语义（低于安全库存 / 高于上限），数量一律为基础单位，无金额 / 币种；
/// 不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」库存预警表（stock-alert）菜单授权（fail closed）。</para>
/// </summary>
public sealed class StockAlertReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public StockAlertReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetStockAlert;

    private const string Label = "库存预警表";
    private const string Grain = "库存预警（一行一条预警库存行：商品 + 仓库）";
    private const string CurrencyUnitSemantics = "数量按基础单位；无金额/币种";
    private const string RequiredMenuCode = "stock-alert";
    private const string RequiredMenuText = "库存预警表";
    private const string ReadOnlyText = "只读库存预警数据集：仅按既有固定报表服务读取库存行与商品阈值，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：现存量低于安全库存 → 低于安全库存；高于上限 → 超出库存上限；未设置阈值不预警；数量一律为基础单位，无金额 / 币种";
    private const string DisclaimerText = "本预览为只读库存预警证据，不构成补货 / 采购 / 处置决策结论";
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

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

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("productName", "商品名称", ReportConfigurationConstants.TypeText, null),
        Field("spec", "规格", ReportConfigurationConstants.TypeText, null),
        Field("unit", "基础单位", ReportConfigurationConstants.TypeText, null),
        Field("warehouseName", "仓库名称", ReportConfigurationConstants.TypeText, null),
        Field("quantity", "现存量", ReportConfigurationConstants.TypeNumber, "基础单位"),
        Field("minStock", "安全库存", ReportConfigurationConstants.TypeNumber, "基础单位"),
        Field("maxStock", "库存上限", ReportConfigurationConstants.TypeNumber, "基础单位"),
        Field("diff", "差额", ReportConfigurationConstants.TypeNumber, "基础单位"),
        Field("alertLevel", "预警级别", ReportConfigurationConstants.TypeEnum, null),
    };

    private static ReportConfigurationFieldDto Field(string key, string label, string type, string? unit)
        => new(key, label, type, unit,
            Filterable: false,
            Aggregatable: string.Equals(type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase),
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

        // 每次请求重新校验身份 / 库存预警表菜单授权（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 对「当前」目录重新校验有界定义：拒绝未知字段 / 未知筛选 / 不支持的排序 / 分组 / 聚合 / 能力。
        ReportConfigurationRules.Validate(definition, BuildDataset());

        var (page, pageSize) = ValidatePageBounds(parameters);

        var fieldKeys = NormalizeFields(definition.Fields);
        var items = await _reportService.GetStockAlertAsync();

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
            SortingExplanation = "本数据集不支持任意排序：稳定按预警级别 → 商品名称排序（与固定报表一致）",
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
        ReportDtos.StockAlertItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("productName", StringComparer.OrdinalIgnoreCase)) row["productName"] = item.ProductName;
        if (fieldKeys.Contains("spec", StringComparer.OrdinalIgnoreCase)) row["spec"] = item.Spec;
        if (fieldKeys.Contains("unit", StringComparer.OrdinalIgnoreCase)) row["unit"] = item.Unit;
        if (fieldKeys.Contains("warehouseName", StringComparer.OrdinalIgnoreCase)) row["warehouseName"] = item.WarehouseName;
        if (fieldKeys.Contains("quantity", StringComparer.OrdinalIgnoreCase)) row["quantity"] = item.Quantity;
        if (fieldKeys.Contains("minStock", StringComparer.OrdinalIgnoreCase)) row["minStock"] = item.MinStock;
        if (fieldKeys.Contains("maxStock", StringComparer.OrdinalIgnoreCase)) row["maxStock"] = item.MaxStock;
        if (fieldKeys.Contains("diff", StringComparer.OrdinalIgnoreCase)) row["diff"] = item.Diff;
        if (fieldKeys.Contains("alertLevel", StringComparer.OrdinalIgnoreCase)) row["alertLevel"] = item.AlertLevel;
        return row;
    }
}

