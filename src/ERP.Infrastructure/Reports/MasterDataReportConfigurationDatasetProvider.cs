using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-317 Stage 2）的基础资料打印族受控数据集适配器：把某一个基础资料打印族
/// （客户 / 供应商 / 员工 / 费用科目 / 仓库 / 商品 / 其他资料，共 7 族）的有限字段白名单暴露为统一受控数据集。
/// <para>预览直接读取对应 Base* 实体的未删除记录，按稳定主键 Id 倒序分页（与既有基础资料列表一致），
/// null 原样保留；客户族复用 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// <para>每次目录 / 预览调用都重新校验身份与既有基础资料菜单授权（fail closed）；金额 / 单位字段原样分区呈现，
/// 绝不跨币种 / 单位换算或合并；不新增控制器 / 设计器 / 导出器。</para>
/// </summary>
public sealed class MasterDataReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;
    private readonly ReportMasterDataFamilyDefinition _family;

    public MasterDataReportConfigurationDatasetProvider(IErpDbContext db, string datasetKey)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _family = ReportConfigurationMasterDataCatalog.ResolveByDatasetKey(datasetKey);
    }

    /// <inheritdoc />
    public string DatasetKey => _family.DatasetKey;

    private const string Grain = "基础资料（一行一条记录；按受控字段白名单顺序；null 原样保留）";
    private const string CurrencyUnitSemantics = ReportConfigurationMasterDataCatalog.CurrencyUnitSemantics;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 200;

    private string RequiredMenuCode =>
        _family.RequiredMenuCodes.Count == 1 ? _family.RequiredMenuCodes[0] : string.Join("+", _family.RequiredMenuCodes);

    private string RequiredMenuText => _family.RequiredMenuText;

    private string ReadOnlyText =>
        $"只读基础资料打印数据集（{_family.Title}）：仅读取既有基础资料表受控字段白名单，不新增 / 修改 / 删除任何记录";

    private string BoundaryText =>
        $"口径：字段仅限 {_family.Title} 打印字段白名单（与既有基础资料打印列一致）；分页有界（每页最多 {MaxPageSize}）；"
        + "软删除记录不返回；null 原样保留；客户族复用业务员数据范围（fail closed）";

    private string DisclaimerText =>
        $"本预览为只读基础资料打印证据（{_family.Title}）：纯资料字段，无金额 / 币种 / 单位换算；"
        + "不构成报关、清关、退税或财务结论";

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

        var source = BuildSource(scope);
        var (total, items) = await PageAsync(source, page, pageSize, cancellationToken);

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = ProjectRows(items, fieldKeys);
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);

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

    private ReportConfigurationDatasetDto BuildDataset()
    {
        var fields = _family.Columns.Select(Field).ToList();
        return new ReportConfigurationDatasetDto(
            DatasetKey,
            _family.Title,
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
            Metrics = new List<ReportConfigurationMetricDto>(),
            GroupingDimensions = new List<ReportConfigurationGroupingDimensionDto>(),
            Relations = new List<ReportConfigurationRelationDto>(),
            SortingExplanation = "本数据集不支持任意排序：稳定按主键 Id 倒序（与既有基础资料列表一致）",
        };
    }

    private static ReportConfigurationFieldDto Field(ReportMasterDataColumn column)
        => new(
            column.Key,
            column.Title,
            column.Type,
            null,
            Filterable: false,
            Aggregatable: false,
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(column.Type));

    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再预览{_family.Title}", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝预览{_family.Title}"
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

    private IReadOnlyList<string> NormalizeFields(IReadOnlyList<string>? fields)
    {
        var selected = new List<string>();
        foreach (var raw in fields ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var column = _family.Columns.FirstOrDefault(c =>
                string.Equals(c.Key, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (column is null)
                continue; // 已由 ReportConfigurationRules.Validate 拒绝，此处仅做规范化

            if (!selected.Contains(column.Key, StringComparer.Ordinal))
                selected.Add(column.Key);
        }

        if (selected.Count == 0)
            selected.AddRange(_family.Columns.Select(c => c.Key));

        return selected;
    }

    private ReportConfigurationColumnDto BuildColumn(string key)
    {
        var column = _family.Columns.First(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(column.Key, column.Title, column.Type, null);
    }

    private IQueryable<BaseEntity> BuildSource(SalespersonDataScope scope)
    {
        switch (_family.FamilyKey)
        {
            case "customer":
            {
                var customers = _db.BaseCustomers.AsNoTracking().Where(c => !c.IsDeleted);
                if (scope.AllowedCustomerIds is not null)
                {
                    var allowed = scope.AllowedCustomerIds.ToList();
                    customers = customers.Where(c => allowed.Contains(c.Id));
                }
                return customers;
            }
            case "supplier":
                return _db.BaseSuppliers.AsNoTracking().Where(s => !s.IsDeleted);
            case "employee":
                return _db.BaseEmployees.AsNoTracking().Where(s => !s.IsDeleted);
            case "expense-account":
                return _db.BaseExpenseAccounts.AsNoTracking().Where(s => !s.IsDeleted);
            case "warehouse":
                return _db.BaseWarehouses.AsNoTracking().Where(s => !s.IsDeleted);
            case "product":
                return _db.BaseProducts.AsNoTracking().Where(s => !s.IsDeleted);
            case "other-info":
                return _db.BaseOtherInfos.AsNoTracking().Where(s => !s.IsDeleted);
            default:
                throw BusinessException.InvalidParameter($"未知的基础资料打印族：{_family.FamilyKey}");
        }
    }

    private static async Task<(int Total, List<BaseEntity> Items)> PageAsync(
        IQueryable<BaseEntity> source, int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await source.CountAsync(cancellationToken);
        var items = await source
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (total, items);
    }

    private List<Dictionary<string, object?>> ProjectRows(
        IReadOnlyList<BaseEntity> items, IReadOnlyList<string> keys)
    {
        var rows = new List<Dictionary<string, object?>>(items.Count);
        foreach (var item in items)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var column = _family.Columns.First(c =>
                    string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
                row[column.Key] = item.GetType().GetProperty(column.Property)?.GetValue(item);
            }
            rows.Add(row);
        }

        return rows;
    }
}


