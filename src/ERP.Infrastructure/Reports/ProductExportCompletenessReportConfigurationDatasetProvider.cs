using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-306 Stage 2）的「出口字段完整度工作台」受控数据集适配器：把既有
/// 「出口字段完整度工作台」（export:product-export-field-completeness）的有限字段白名单
/// （商品身份 + 英文报关品名 / 装箱单位与每箱数量 / 外箱尺寸 / 毛重 / 出口退税率填写状态）与
/// 只读、有界预览口径暴露为统一受控数据集。
/// <para>预览复用 <see cref="ProductExportFieldCompletenessRules.BuildRow"/> 与
/// <see cref="ProductExportFieldCompletenessRules.BuildGroupPredicate"/><c>/</c>
/// <see cref="ProductExportFieldCompletenessRules.NormalizeGroup"/> 的既有判定口径；
/// 只读、无金额 / 币种，绝不生成报关合规、退税资格或税率结论。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」商品资料（product）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）特权数据范围（旧工作台为全局只读，
/// 仅特权账号可执行；受限制 / 撤销范围一律 fail closed，且先于任何源读取）。</para>
/// </summary>
public sealed class ProductExportCompletenessReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public ProductExportCompletenessReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetProductExportFieldCompleteness;

    private const string Label = "出口字段完整度工作台";
    private const string Grain = "出口字段完整度（一行一个启用商品；只读字段缺口，无金额 / 币种）";
    private const string CurrencyUnitSemantics = "只读字段完整度（无金额/币种）";
    private const string RequiredMenuCode = "product";
    private const string RequiredMenuText = "商品资料";
    private const string ReadOnlyText = "只读出口字段完整度数据集：仅读取启用商品资料的出口 / 装箱字段填写与缺口，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限英文报关品名、装箱单位与每箱数量、外箱尺寸 / 毛重、出口退税率；只报告填写 / 空白 / 为 0 / 无效值，不做报关合规、退税资格或税率结论；每次调用重新校验商品资料菜单与特权数据范围；分页有界（每页最多 200）；受控选择上限 5000 个商品";
    private const string DisclaimerText = ProductExportFieldCompletenessRules.DisclaimerText;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;
    private const int MaxSourceProducts = 5000;

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

    private static readonly IReadOnlyList<string> EqOnlyOperators = new[] { ReportConfigurationConstants.OperatorEq };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("productId", "商品Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("productCode", "商品编码", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("productName", "商品名称", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("spec", "规格型号", ReportConfigurationConstants.TypeText, null),
        Field("unit", "单位", ReportConfigurationConstants.TypeText, null),
        Field("completeness", "完整度", ReportConfigurationConstants.TypeEnum, null),
        Field("group", "完整度分组（筛选）", ReportConfigurationConstants.TypeEnum, null, filterable: true),
        Field("gapCount", "缺口数", ReportConfigurationConstants.TypeNumber, null, aggregatable: true),
        Field("fieldCount", "字段数", ReportConfigurationConstants.TypeNumber, null, aggregatable: true),
        Field("englishDeclareName", "英文报关品名", ReportConfigurationConstants.TypeEnum, null),
        Field("packageUnit", "装箱单位", ReportConfigurationConstants.TypeEnum, null),
        Field("unitsPerPackage", "每箱数量", ReportConfigurationConstants.TypeEnum, null),
        Field("outerLength", "外箱长(cm)", ReportConfigurationConstants.TypeEnum, null),
        Field("outerWidth", "外箱宽(cm)", ReportConfigurationConstants.TypeEnum, null),
        Field("outerHeight", "外箱高(cm)", ReportConfigurationConstants.TypeEnum, null),
        Field("outerWeight", "外箱毛重(kg)", ReportConfigurationConstants.TypeEnum, null),
        Field("refundRate", "出口退税率(%)", ReportConfigurationConstants.TypeEnum, null),
    };

    private static ReportConfigurationFieldDto Field(
        string key, string label, string type, string? unit, bool filterable = false, bool aggregatable = false)
        => new(key, label, type, unit,
            Filterable: filterable,
            Aggregatable: aggregatable,
            Hidden: false,
            filterable ? EqOnlyOperators : ReportConfigurationRules.GetOperatorsForType(type));

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
        var selected = new HashSet<string>(fieldKeys, StringComparer.OrdinalIgnoreCase);

        var group = ResolveGroup(definition);
        var productCode = ResolveTextEq(definition, "productCode");
        var productName = ResolveTextEq(definition, "productName");
        var productId = ResolveLongEq(definition, "productId");

        var source = _db.BaseProducts.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == ProductExportFieldCompletenessRules.ActiveStatus);

        var groupPredicate = ProductExportFieldCompletenessRules.BuildGroupPredicate(group);
        if (groupPredicate is not null)
            source = source.Where(groupPredicate);

        if (productCode is not null)
            source = source.Where(p => p.ProductCode == productCode);
        if (productName is not null)
            source = source.Where(p => p.ProductName == productName);
        if (productId is not null)
            source = source.Where(p => p.Id == productId.Value);

        var total = await source.CountAsync(cancellationToken);
        if (total > MaxSourceProducts)
        {
            throw new BusinessException(
                $"{Label}的授权范围内选择超过 {MaxSourceProducts} 个商品，请缩小筛选范围后重试",
                ErrorCodes.RuleConflict);
        }

        var products = await source
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = products.Select(p => BuildRow(p, selected)).ToList();
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);

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

        // 旧工作台为全局只读：仅特权账号可执行，受限制 / 未解析范围一律 fail closed（先于任何源读取）。
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId.Value);
        if (!scope.IsPrivileged)
        {
            throw new BusinessException(
                $"当前账号不是全量数据范围账号：拒绝预览{Label}"
                + "（fail closed，不执行全局只读）",
                ErrorCodes.Forbidden);
        }
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
            SortingExplanation = "本数据集不支持任意排序：稳定按商品 Id 升序（与既有完整度工作台一致）",
        };
    }

    private static (int Page, int PageSize) ValidatePageBounds(ReportConfigurationPreviewParameters parameters)
    {
        if (parameters.Page < 1)
            throw BusinessException.InvalidParameter($"页码必须从 1 开始（收到 {parameters.Page}）");
        if (parameters.PageSize < 1 || parameters.PageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {parameters.PageSize}）");
        return (parameters.Page, parameters.PageSize);
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

    private static Dictionary<string, object?> BuildRow(BaseProduct product, HashSet<string> selected)
    {
        var row = ProductExportFieldCompletenessRules.BuildRow(product);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (selected.Contains("productId")) result["productId"] = row.ProductId;
        if (selected.Contains("productCode")) result["productCode"] = row.ProductCode;
        if (selected.Contains("productName")) result["productName"] = row.ProductName;
        if (selected.Contains("spec")) result["spec"] = row.Spec;
        if (selected.Contains("unit")) result["unit"] = row.Unit;
        if (selected.Contains("completeness")) result["completeness"] = row.Completeness;
        if (selected.Contains("group")) result["group"] = null; // 仅筛选维度，不输出事实值
        if (selected.Contains("gapCount")) result["gapCount"] = row.GapCount;
        if (selected.Contains("fieldCount")) result["fieldCount"] = row.FieldCount;

        foreach (var field in row.Fields)
        {
            if (selected.Contains(field.Key))
                result[field.Key] = field.State;
        }

        return result;
    }

    private static string ResolveGroup(ReportConfigurationDefinition definition)
    {
        string? group = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            if (!string.Equals(filter.FieldKey.Trim(), "group", StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            var value = ReportConfigurationDatasetTranslation.RequireString(filter.Value, "group");
            var normalized = ProductExportFieldCompletenessRules.NormalizeGroup(value);
            if (group is not null && !string.Equals(group, normalized, StringComparison.Ordinal))
                throw BusinessException.InvalidParameter("完整度分组筛选冲突：只允许一个一致的 group 筛选");
            group = normalized;
        }

        return group ?? ProductExportFieldCompletenessRules.GroupAll;
    }

    private static string? ResolveTextEq(ReportConfigurationDefinition definition, string fieldKey)
    {
        string? value = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), fieldKey, StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            value = ReportConfigurationDatasetTranslation.CoalesceString(value, filter.Value, fieldKey);
        }

        return value;
    }

    private static long? ResolveLongEq(ReportConfigurationDefinition definition, string fieldKey)
    {
        long? value = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), fieldKey, StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            value = ReportConfigurationDatasetTranslation.CoalesceLong(value, filter.Value, fieldKey);
        }

        return value;
    }
}

