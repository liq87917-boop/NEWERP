using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-308 Stage 2）的旧单据导出族受控数据集适配器：把某一个 BillProc 导出族
/// （共 16 族）的有限导出列 / 日期 / 状态 / 关键字口径暴露为统一受控数据集。
/// <para>预览复用 <see cref="ILegacyBillExportReadService"/> 的受控只读读取，保留旧导出字段顺序、
/// 原币 / 原单位 / null 原样与一行一条表头粒度；绝不调用写存储过程。</para>
/// <para>每次目录 / 预览调用都重新校验：身份 + 启用账号 + 全部必需菜单授权 + 特权全量数据范围（fail closed）。</para>
/// </summary>
public sealed class LegacyBillExportReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;
    private readonly ILegacyBillExportReadService _reader;
    private readonly LegacyBillExportFamilyDefinition _family;

    public LegacyBillExportReportConfigurationDatasetProvider(
        IErpDbContext db, ILegacyBillExportReadService reader, string datasetKey)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _family = LegacyBillExportCatalog.ResolveByDatasetKey(datasetKey);
    }

    /// <inheritdoc />
    public string DatasetKey => _family.DatasetKey;

    private const string Grain = "一行一条单据表头（按旧单据导出字段顺序；原币 / 原单位 / null 原样保留）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；数量按基础单位；不跨币种换算或合并";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;
    private const int MaxDateRangeDays = 366;

    private string Label => _family.Title + "（单据导出）";
    private string RequiredMenuCode =>
        _family.RequiredMenuCodes.Count == 1 ? _family.RequiredMenuCodes[0] : string.Join("+", _family.RequiredMenuCodes);
    private string RequiredMenuText => _family.RequiredMenuText;
    private string ReadOnlyText => $"只读旧单据导出数据集（{_family.Title}）：仅读取旧单据表白名单列，不新增 / 修改 / 删除任何记录，不调用写存储过程";
    private string BoundaryText =>
        $"口径：字段仅限 {_family.Title} 导出白名单（与旧导出一致）；日期窗口有界（含首尾最多 {MaxDateRangeDays} 天）；"
        + $"分页有界（每页最多 {MaxPageSize}）；关键字仅 BillNo LIKE、状态精确匹配、日期按 {_family.DateField} 过滤；"
        + "旧库 Oid-vs-Id / 缺表缺列显式 environment-blocked，绝不猜测或降级";
    private string DisclaimerText =>
        $"本预览为只读旧单据导出证据（{_family.Title}）：金额按原币、数量按基础单位呈现，不跨币种换算或合并；"
        + "不构成报关、清关、退税或财务结论";

    private static readonly IReadOnlyList<string> GroupingKeys = new[] { ReportConfigurationConstants.GroupNone };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityPaging,
        ReportConfigurationConstants.CapabilityDateRange,
    };

    private static readonly IReadOnlyList<string> UnsupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

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

        var keyword = ResolveKeyword(definition);
        var status = ResolveStatus(definition);
        var (start, end) = ResolveDateRange(definition);

        var query = new LegacyBillExportQuery
        {
            FamilyKey = _family.FamilyKey,
            Fields = fieldKeys,
            Keyword = keyword,
            Status = status,
            StartDate = start,
            EndDate = end,
            Page = page,
            PageSize = pageSize,
        };

        var result = await _reader.ReadPageAsync(query, cancellationToken);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var totalPages = result.Total == 0 ? 1 : (int)Math.Ceiling(result.Total / (double)pageSize);

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = _family.DatasetKey,
            Columns = columns,
            Rows = result.Rows,
            Total = result.Total,
            MatchedCount = result.Total,
            SourceEvidenceCount = result.Total,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groupings = new List<string>(),
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                _family.DatasetKey, Grain, CurrencyUnitSemantics,
                ReadOnlyText, BoundaryText, DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再预览{Label}", ErrorCodes.Unauthorized);

        var accountEnabled = await _db.SysUsers.AsNoTracking()
            .AnyAsync(u => u.Id == userId.Value && !u.IsDeleted && u.Status == UserStatus.Enabled, cancellationToken);
        if (!accountEnabled)
            throw new BusinessException($"当前账号不可用或已禁用：拒绝预览{Label}", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        foreach (var code in _family.RequiredMenuCodes)
        {
            if (!menuCodes.Contains(code))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」模块授权：拒绝预览{Label}（fail closed，不返回任何数据）",
                    ErrorCodes.Forbidden);
            }
        }

        // 旧导出为全局只读：仅特权全量数据范围账号可执行；受限制 / 未解析范围一律 fail closed（先于任何源读取）。
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId.Value);
        if (!scope.IsPrivileged)
            throw new BusinessException($"当前账号不是全量数据范围账号：拒绝预览{Label}（fail closed，不执行全局只读）", ErrorCodes.Forbidden);
    }

    private ReportConfigurationDatasetDto BuildDataset()
    {
        var fields = _family.Columns.Select(BuildField).ToList();
        return new ReportConfigurationDatasetDto(
            _family.DatasetKey,
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
            SortingExplanation = "本数据集不支持任意排序：稳定按单据 Oid 降序（与旧单据导出一致）",
        };
    }

    private ReportConfigurationFieldDto BuildField(LegacyBillExportColumn column)
    {
        var isBillNo = string.Equals(column.Key, "BillNo", StringComparison.OrdinalIgnoreCase);
        var isStatus = string.Equals(column.Key, "Status", StringComparison.OrdinalIgnoreCase);
        var isDate = string.Equals(column.Key, _family.DateField, StringComparison.OrdinalIgnoreCase);

        var filterable = isBillNo || isStatus || isDate;
        IReadOnlyList<string> operators = isDate ? DateRangeOperators
            : filterable ? EqOnlyOperators
            : ReportConfigurationRules.GetOperatorsForType(column.Type);

        return new ReportConfigurationFieldDto(
            column.Key,
            column.Title,
            column.Type,
            CurrencyUnitOf(column.Key),
            filterable,
            Aggregatable: false,
            Hidden: false,
            operators);
    }

    private static string? CurrencyUnitOf(string key)
        => key.ToLowerInvariant() switch
        {
            "currency" => "币种代码",
            "totalamount" or "amount" or "depositamount" or "freightcost" or "othercost" => "原币金额",
            "depositratio" => "%",
            _ => null,
        };

    private static readonly IReadOnlyList<string> EqOnlyOperators = new[] { ReportConfigurationConstants.OperatorEq };

    private static readonly IReadOnlyList<string> DateRangeOperators = new[]
    {
        ReportConfigurationConstants.OperatorEq,
        ReportConfigurationConstants.OperatorGte,
        ReportConfigurationConstants.OperatorLte,
        ReportConfigurationConstants.OperatorGt,
        ReportConfigurationConstants.OperatorLt,
        ReportConfigurationConstants.OperatorBetween,
    };

    private static (int Page, int PageSize) ValidatePageBounds(ReportConfigurationPreviewParameters parameters)
    {
        if (parameters.Page < 1)
            throw BusinessException.InvalidParameter($"页码必须从 1 开始（收到 {parameters.Page}）");
        if (parameters.PageSize < 1 || parameters.PageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {parameters.PageSize}）");
        return (parameters.Page, parameters.PageSize);
    }

    private IReadOnlyList<string> NormalizeFields(IReadOnlyList<string>? fields)
    {
        var selected = (fields ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            selected.AddRange(_family.Columns.Select(c => c.Key));

        return selected;
    }

    private ReportConfigurationColumnDto BuildColumn(string key)
    {
        var column = _family.Columns.First(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(column.Key, column.Title, column.Type, CurrencyUnitOf(column.Key));
    }

    private string? ResolveKeyword(ReportConfigurationDefinition definition)
    {
        string? value = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), "BillNo", StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            value = ReportConfigurationDatasetTranslation.CoalesceString(value, filter.Value, "BillNo");
        }

        return value;
    }

    private int? ResolveStatus(ReportConfigurationDefinition definition)
    {
        long? value = null;
        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), "Status", StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
            value = ReportConfigurationDatasetTranslation.CoalesceLong(value, filter.Value, "Status");
        }

        return value.HasValue ? (int)value.Value : null;
    }

    private (DateTime? Start, DateTime? End) ResolveDateRange(ReportConfigurationDefinition definition)
    {
        DateTime? start = null;
        DateTime? end = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), _family.DateField, StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, _family.DateField, ref start, ref end);
        }

        if (start is null && end is null)
            return (null, null);

        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;
        if (endDate < startDate)
            throw BusinessException.InvalidParameter($"{Label}的结束日期不能早于开始日期");
        if ((endDate - startDate).Days + 1 > MaxDateRangeDays)
            throw BusinessException.InvalidParameter($"{Label}的日期范围最大 {MaxDateRangeDays} 天（含首尾）");

        return (startDate, endDate);
    }
}
