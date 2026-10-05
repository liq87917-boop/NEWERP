using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-299 Stage 2）的库存库龄与成本估值报表数据集适配器：暴露既有
/// <see cref="IReportService.GetInventoryAgingReportAsync"/> 固定报表的有限字段白名单
/// （仓库 / 商品 / 数量 / 库龄分层 / 成本金额等）与只读、有界预览口径。
/// <para>预览委托既有固定报表服务语义（FIFO 库龄分层 + 库存行持久化移动加权平均成本），
/// 数量一律为基础单位，金额一律为库存成本币种（CNY），不跨币种合并；库龄分层以 5 个固定格 + 库龄未知
/// 展平为一对多字段（每格数量 / 金额，金额成本未知时为 null）；不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」库存查询（stock-query）菜单授权（fail closed）。</para>
/// </summary>
public sealed class InventoryAgingReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public InventoryAgingReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetInventoryAging;

    private const string Label = "库存库龄与成本估值报表";
    private const string Grain = "库存库龄与成本估值报表（一行一条库存行：仓库 + 商品）";
    private const string CurrencyUnitSemantics = "数量按基础单位；成本/金额按移动加权平均；不跨币种合并";
    private const string RequiredMenuCode = "stock-query";
    private const string RequiredMenuText = "库存查询";
    private const string ReadOnlyText = "只读库存库龄与成本估值报表数据集：仅按既有固定报表服务读取库存行与库存流水台账，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：主表为库存行（Stocks），库龄分层由库存流水（StockMovements）按 FIFO 派生；数量一律为基础单位；金额一律为库存成本币种（CNY）；成本依据缺失时金额记为未知（null）；本数据集预览以当天为截止日";
    private const string DisclaimerText = "本预览为只读库存库龄与成本估值证据，不重算、不重建历史成本、不计提跌价准备";
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
        "warehouseId", "productId", "reversalCount",
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
            Field("knownAgedQuantity", "有台账分层依据数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("unknownAgeQuantity", "库龄未知数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("evidenceStatus", "库龄依据", ReportConfigurationConstants.TypeEnum, null, filterable: false),
            Field("averageCost", "移动加权平均成本", ReportConfigurationConstants.TypeNumber, "成本单价（CNY）", filterable: false),
            Field("costStatus", "成本状态", ReportConfigurationConstants.TypeEnum, null, filterable: false),
            Field("costCurrency", "成本币种", ReportConfigurationConstants.TypeText, null, filterable: false),
            Field("authoritativeAmount", "权威库存金额", ReportConfigurationConstants.TypeNumber, "金额（CNY）", filterable: false),
            Field("agedAmount", "分层金额合计", ReportConfigurationConstants.TypeNumber, "金额（CNY）", filterable: false),
            Field("unknownAgeAmount", "库龄未知金额", ReportConfigurationConstants.TypeNumber, "金额（CNY）", filterable: false),
            Field("unknownCostQuantity", "成本未知数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("ledgerDeficitQuantity", "台账分层超额扣减差额", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("unpairedQuantity", "无可扣减分层数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
            Field("reversalCount", "红字冲销流水条数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
            Field("note", "口径说明", ReportConfigurationConstants.TypeText, null, filterable: false),
        };

        foreach (var key in InventoryAgingSemantics.BucketKeys)
        {
            var prefix = BucketPrefix(key);
            var label = InventoryAgingSemantics.BucketLabel(key);
            fields.Add(Field($"bucket{prefix}Quantity", $"{label}数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false));
        }
        foreach (var key in InventoryAgingSemantics.BucketKeys)
        {
            var prefix = BucketPrefix(key);
            var label = InventoryAgingSemantics.BucketLabel(key);
            fields.Add(Field($"bucket{prefix}Amount", $"{label}金额", ReportConfigurationConstants.TypeNumber, "金额（CNY）", filterable: false));
        }

        return fields;
    }

    private static string BucketPrefix(string key) => key switch
    {
        InventoryAgingSemantics.Bucket0To30 => "0To30",
        InventoryAgingSemantics.Bucket31To60 => "31To60",
        InventoryAgingSemantics.Bucket61To90 => "61To90",
        InventoryAgingSemantics.Bucket91To180 => "91To180",
        InventoryAgingSemantics.BucketOver180 => "Over180",
        _ => throw BusinessException.InvalidParameter($"未知库龄分层键: {key}"),
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

        // 每次请求重新校验身份 / 库存查询菜单授权（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 对「当前」目录重新校验有界定义：拒绝未知字段 / 未知筛选 / 不支持的排序 / 分组 / 聚合 / 能力。
        ReportConfigurationRules.Validate(definition, BuildDataset());

        var (page, pageSize) = ValidatePageBounds(parameters);

        var fieldKeys = NormalizeFields(definition.Fields);
        var query = BuildQuery(definition, page, pageSize);

        var report = await _reportService.GetInventoryAgingReportAsync(query, cancellationToken);

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

    private static ReportDtos.InventoryAgingReportQuery BuildQuery(
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
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetInventoryAging} 不支持的筛选字段: {key}");
            }
        }

        return new ReportDtos.InventoryAgingReportQuery
        {
            AsOfDate = DateTime.Today,
            WarehouseId = warehouseId,
            ProductId = productId,
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
        ReportDtos.InventoryAgingItem item, IReadOnlyList<string> fieldKeys)
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
        if (fieldKeys.Contains("knownAgedQuantity", StringComparer.OrdinalIgnoreCase)) row["knownAgedQuantity"] = item.KnownAgedQuantity;
        if (fieldKeys.Contains("unknownAgeQuantity", StringComparer.OrdinalIgnoreCase)) row["unknownAgeQuantity"] = item.UnknownAgeQuantity;
        if (fieldKeys.Contains("evidenceStatus", StringComparer.OrdinalIgnoreCase)) row["evidenceStatus"] = item.EvidenceStatus;
        if (fieldKeys.Contains("averageCost", StringComparer.OrdinalIgnoreCase)) row["averageCost"] = item.AverageCost;
        if (fieldKeys.Contains("costStatus", StringComparer.OrdinalIgnoreCase)) row["costStatus"] = item.CostStatus;
        if (fieldKeys.Contains("costCurrency", StringComparer.OrdinalIgnoreCase)) row["costCurrency"] = InventoryAgingSemantics.CostCurrency;
        if (fieldKeys.Contains("authoritativeAmount", StringComparer.OrdinalIgnoreCase)) row["authoritativeAmount"] = item.AuthoritativeAmount;
        if (fieldKeys.Contains("agedAmount", StringComparer.OrdinalIgnoreCase)) row["agedAmount"] = item.AgedAmount;
        if (fieldKeys.Contains("unknownAgeAmount", StringComparer.OrdinalIgnoreCase)) row["unknownAgeAmount"] = item.UnknownAgeAmount;
        if (fieldKeys.Contains("unknownCostQuantity", StringComparer.OrdinalIgnoreCase)) row["unknownCostQuantity"] = item.UnknownCostQuantity;
        if (fieldKeys.Contains("ledgerDeficitQuantity", StringComparer.OrdinalIgnoreCase)) row["ledgerDeficitQuantity"] = item.LedgerDeficitQuantity;
        if (fieldKeys.Contains("unpairedQuantity", StringComparer.OrdinalIgnoreCase)) row["unpairedQuantity"] = item.UnpairedQuantity;
        if (fieldKeys.Contains("reversalCount", StringComparer.OrdinalIgnoreCase)) row["reversalCount"] = item.ReversalCount;
        if (fieldKeys.Contains("note", StringComparer.OrdinalIgnoreCase)) row["note"] = item.Note;

        foreach (var key in InventoryAgingSemantics.BucketKeys)
        {
            var prefix = BucketPrefix(key);
            var bucket = item.Buckets.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
            var quantityKey = $"bucket{prefix}Quantity";
            var amountKey = $"bucket{prefix}Amount";
            if (fieldKeys.Contains(quantityKey, StringComparer.OrdinalIgnoreCase)) row[quantityKey] = bucket?.Quantity;
            if (fieldKeys.Contains(amountKey, StringComparer.OrdinalIgnoreCase)) row[amountKey] = bucket?.Amount;
        }

        return row;
    }
}




