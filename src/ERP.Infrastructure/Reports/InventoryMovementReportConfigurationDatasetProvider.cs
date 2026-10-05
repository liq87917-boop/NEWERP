using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-299 Stage 2）的库存移动与呆滞报表数据集适配器：暴露既有
/// <see cref="IReportService.GetInventoryMovementReportAsync"/> 固定报表的有限字段白名单
/// （仓库 / 商品 / 日期 / 数量 / 呆滞分类等）与只读、有界预览口径。
/// <para>预览委托既有固定报表服务语义（库存行权威现存量 + 库存流水台账派生出入库 / 最后移动日期 / 停滞天数），
/// 数量一律为基础单位，不跨币种合并，也不估算成本 / 金额；不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」库存查询（stock-query）菜单授权（fail closed）。</para>
/// </summary>
public sealed class InventoryMovementReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public InventoryMovementReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetInventoryMovement;

    private const string Label = "库存移动与呆滞报表";
    private const string Grain = "库存移动与呆滞报表（一行一条库存行：仓库 + 商品）";
    private const string CurrencyUnitSemantics = "数量按基础单位；成本按移动加权平均；不跨币种合并";
    private const string RequiredMenuCode = "stock-query";
    private const string RequiredMenuText = "库存查询";
    private const string ReadOnlyText = "只读库存移动与呆滞报表数据集：仅按既有固定报表服务读取库存行与库存流水台账，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：主表为库存行（Stocks），出入库 / 最后移动日期 / 停滞天数取自库存流水台账；数量一律为基础单位；无台账的行以「未知」呈现；不估算成本 / 金额；本数据集预览以当天为截止日、窗口默认前 90 天、呆滞阈值默认 90 天";
    private const string DisclaimerText = "本预览为只读库存移动与呆滞证据，不构成库存占用 / 呆滞处置 / 成本估值结论";
    private const int DefaultPageSize = 50;
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
        "warehouseId", "productId", "movementCount", "reversalCount", "inactivityDays",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = BuildFields();

    private static IReadOnlyList<ReportConfigurationFieldDto> BuildFields()
    {
        var fields = new List<ReportConfigurationFieldDto>
        {
            Field("warehouseId", "仓库Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
            Field("warehouseName", "仓库名称", ReportConfigurationConstants.TypeText, null, filterable: false),
            Field("productId", "商品Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
            Field("productCode", "商品编码", ReportConfigurationConstants.TypeText, null, filterable: false),
            Field("productName", "商品名称", ReportConfigurationConstants.TypeText, null, filterable: false),
            Field("spec", "规格", ReportConfigurationConstants.TypeText, null, filterable: false),
            Field("unit", "基础单位", ReportConfigurationConstants.TypeText, null, filterable: false),
            Field("currentQuantity", "当前现存量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("lastMovementDate", "最后移动日期", ReportConfigurationConstants.TypeDate, null, filterable: false),
            Field("inboundQuantity", "窗口入库数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("outboundQuantity", "窗口出库数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("netQuantity", "窗口净变动", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("movementCount", "窗口台账行数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
            Field("reversalCount", "窗口红字冲销行数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
            Field("inactivityDays", "停滞天数", ReportConfigurationConstants.TypeNumber, "天", filterable: false),
            Field("historyStatus", "台账状态", ReportConfigurationConstants.TypeEnum, null, filterable: false),
            Field("classification", "呆滞分类", ReportConfigurationConstants.TypeEnum, null, filterable: false),
            Field("note", "口径说明", ReportConfigurationConstants.TypeText, null, filterable: false),
        };
        return fields;
    }

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

        // 每次请求重新校验身份 / 库存查询菜单授权（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 对「当前」目录重新校验有界定义：拒绝未知字段 / 未知筛选 / 不支持的排序 / 分组 / 聚合 / 能力。
        ReportConfigurationRules.Validate(definition, BuildDataset());

        // 分页边界显式拒绝（绝不静默钳制）：页码 >= 1，每页条数 1 ~ MaxPageSize。
        var (page, pageSize) = ValidatePageBounds(parameters);

        var fieldKeys = NormalizeFields(definition.Fields);
        var query = BuildQuery(definition, page, pageSize);

        var report = await _reportService.GetInventoryMovementReportAsync(query, cancellationToken);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = report.Items.Select(item => BuildRow(item, fieldKeys)).ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = report.Total,
            MatchedCount = report.Total,
            Page = page,
            PageSize = pageSize,
            TotalPages = report.TotalPages,
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

    private static ReportDtos.InventoryMovementReportQuery BuildQuery(
        ReportConfigurationDefinition definition, int page, int pageSize)
    {
        long? warehouseId = null;
        long? productId = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "warehouseid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    warehouseId = ReportConfigurationDatasetTranslation.CoalesceLong(warehouseId, filter.Value, "warehouseId");
                    break;
                case "productid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    productId = ReportConfigurationDatasetTranslation.CoalesceLong(productId, filter.Value, "productId");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetInventoryMovement} 不支持的筛选字段: {key}");
            }
        }

        return new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = DateTime.Today,
            WarehouseId = warehouseId,
            ProductId = productId,
            InactiveDays = ReportDtos.InventoryMovementReportQuery.DefaultInactiveDays,
            OnlyPositiveQuantity = true,
            Page = page,
            PageSize = pageSize,
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
            SortingExplanation = "本数据集不支持任意排序：稳定按仓库 + 商品排序（与固定报表分页一致）",
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
        ReportDtos.InventoryMovementItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("warehouseId", StringComparer.OrdinalIgnoreCase)) row["warehouseId"] = item.WarehouseId;
        if (fieldKeys.Contains("warehouseName", StringComparer.OrdinalIgnoreCase)) row["warehouseName"] = item.WarehouseName;
        if (fieldKeys.Contains("productId", StringComparer.OrdinalIgnoreCase)) row["productId"] = item.ProductId;
        if (fieldKeys.Contains("productCode", StringComparer.OrdinalIgnoreCase)) row["productCode"] = item.ProductCode;
        if (fieldKeys.Contains("productName", StringComparer.OrdinalIgnoreCase)) row["productName"] = item.ProductName;
        if (fieldKeys.Contains("spec", StringComparer.OrdinalIgnoreCase)) row["spec"] = item.Spec;
        if (fieldKeys.Contains("unit", StringComparer.OrdinalIgnoreCase)) row["unit"] = item.Unit;
        if (fieldKeys.Contains("currentQuantity", StringComparer.OrdinalIgnoreCase)) row["currentQuantity"] = item.CurrentQuantity;
        if (fieldKeys.Contains("lastMovementDate", StringComparer.OrdinalIgnoreCase)) row["lastMovementDate"] = item.LastMovementDate;
        if (fieldKeys.Contains("inboundQuantity", StringComparer.OrdinalIgnoreCase)) row["inboundQuantity"] = item.InboundQuantity;
        if (fieldKeys.Contains("outboundQuantity", StringComparer.OrdinalIgnoreCase)) row["outboundQuantity"] = item.OutboundQuantity;
        if (fieldKeys.Contains("netQuantity", StringComparer.OrdinalIgnoreCase)) row["netQuantity"] = item.NetQuantity;
        if (fieldKeys.Contains("movementCount", StringComparer.OrdinalIgnoreCase)) row["movementCount"] = item.MovementCount;
        if (fieldKeys.Contains("reversalCount", StringComparer.OrdinalIgnoreCase)) row["reversalCount"] = item.ReversalCount;
        if (fieldKeys.Contains("inactivityDays", StringComparer.OrdinalIgnoreCase)) row["inactivityDays"] = item.InactivityDays;
        if (fieldKeys.Contains("historyStatus", StringComparer.OrdinalIgnoreCase)) row["historyStatus"] = item.HistoryStatus;
        if (fieldKeys.Contains("classification", StringComparer.OrdinalIgnoreCase)) row["classification"] = item.Classification;
        if (fieldKeys.Contains("note", StringComparer.OrdinalIgnoreCase)) row["note"] = item.Note;
        return row;
    }
}



