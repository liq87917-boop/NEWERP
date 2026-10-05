using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-298）的简化财务报表数据集适配器公共基类：资产负债表 / 利润表 / 现金流量表
/// 共用同一有限字段白名单（报表行名称 + 金额）与只读、有界预览口径。
/// <para>预览委托既有 <see cref="IReportService"/> 的 GetBalanceSheetAsync / GetIncomeStatementAsync /
/// GetCashFlowStatementAsync 简化汇总语义，不新增控制器 / 设计器 / 导出器；金额按单据金额直接汇总，
/// 不按币种分区、也不跨币种换算（简化口径），该限制在币种 / 单位口径中显式声明。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」报表菜单授权（fail closed）。</para>
/// </summary>
public abstract class FinancialStatementReportConfigurationDatasetProviderBase : IReportConfigurationDatasetProvider
{
    protected readonly IReportService ReportService;
    private readonly IErpDbContext _db;

    protected FinancialStatementReportConfigurationDatasetProviderBase(IReportService reportService, IErpDbContext db)
    {
        ReportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public abstract string DatasetKey { get; }

    protected abstract string Label { get; }
    protected abstract string CurrencyUnitSemantics { get; }
    protected abstract string RequiredMenuCode { get; }
    protected abstract string RequiredMenuText { get; }

    private const string Grain = "财务报表（简化口径，一行一条报表行）";
    private const string ReadOnlyText = "只读简化财务报表数据集：仅按既有报表服务语义汇总单据金额，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "简化口径：金额按单据金额直接汇总，不按币种分区，也不跨币种换算；本数据集不是正式法定财务报表";
    private const string DisclaimerText = "本预览为只读简化财务报表证据，不构成法定 / 审计口径的财务报表结论";
    private const string AmountUnitText = "金额（简化汇总，不分币种）";
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

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new[]
    {
        new ReportConfigurationFieldDto(
            "lineName", "报表行名称", ReportConfigurationConstants.TypeText, null,
            Filterable: false, Aggregatable: false, Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(ReportConfigurationConstants.TypeText)),
        new ReportConfigurationFieldDto(
            "amount", "金额", ReportConfigurationConstants.TypeNumber, AmountUnitText,
            Filterable: false, Aggregatable: false, Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(ReportConfigurationConstants.TypeNumber)),
    };

    /// <summary>读取本数据集对应的既有简化财务报表。</summary>
    protected abstract Task<ReportDtos.FinancialStatement> LoadStatementAsync();

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
            // 无对应报表菜单授权 → 该数据集不暴露（fail closed）。
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

        // 每次请求重新校验身份 / 报表菜单授权（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        var fieldKeys = NormalizeFields(definition.Fields);
        var statement = await LoadStatementAsync();

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = statement.Lines
            .Select(line => BuildRow(line, fieldKeys))
            .ToList();

        var page = parameters.Page < 1 ? 1 : parameters.Page;
        var pageSize = Math.Clamp(parameters.PageSize, 1, MaxPageSize);

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = rows.Count,
            MatchedCount = rows.Count,
            Page = page,
            PageSize = Math.Max(pageSize, 1),
            TotalPages = 1,
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

        // ERP-304：财务报表为全局汇总口径（无按客户 / 业务员范围化的既有查询）。
        // 解析当前账号业务员数据范围；仅特权（全量）账号可执行全局汇总查询，
        // 受限制 / 未解析范围一律 fail closed，绝不事后过滤全局汇总结果。
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId.Value);
        if (!scope.IsPrivileged)
        {
            throw new BusinessException(
                $"当前账号不是全量数据范围账号：拒绝预览{Label}"
                + "（fail closed，不执行全局汇总查询）",
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
            SortingExplanation = "本数据集不支持任意排序：固定按报表行顺序呈现",
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
        ReportDtos.StatementLine line, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("lineName", StringComparer.OrdinalIgnoreCase))
            row["lineName"] = line.Name;
        if (fieldKeys.Contains("amount", StringComparer.OrdinalIgnoreCase))
            row["amount"] = line.Amount;
        return row;
    }

}

/// <summary>资产负债表数据集适配器（ERP-298）：预览委托 <see cref="IReportService.GetBalanceSheetAsync"/>。</summary>
public sealed class BalanceSheetReportConfigurationDatasetProvider : FinancialStatementReportConfigurationDatasetProviderBase
{
    public BalanceSheetReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
        : base(reportService, db)
    {
    }

    /// <inheritdoc />
    public override string DatasetKey => ReportConfigurationConstants.DatasetBalanceSheet;

    protected override string Label => "资产负债表";
    protected override string CurrencyUnitSemantics => "金额按单据金额直接汇总（资产/负债/权益）；不跨币种换算";
    protected override string RequiredMenuCode => "balance-sheet";
    protected override string RequiredMenuText => "资产负债表";

    /// <summary>资产负债表为截止日期快照：预览以当天为截止日（与旧路由默认「当前」口径一致）。</summary>
    protected override Task<ReportDtos.FinancialStatement> LoadStatementAsync()
        => ReportService.GetBalanceSheetAsync(DateTime.Today);
}

/// <summary>利润表数据集适配器（ERP-298）：预览委托 <see cref="IReportService.GetIncomeStatementAsync"/>。</summary>
public sealed class IncomeStatementReportConfigurationDatasetProvider : FinancialStatementReportConfigurationDatasetProviderBase
{
    public IncomeStatementReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
        : base(reportService, db)
    {
    }

    /// <inheritdoc />
    public override string DatasetKey => ReportConfigurationConstants.DatasetIncomeStatement;

    protected override string Label => "利润表";
    protected override string CurrencyUnitSemantics => "金额按单据金额直接汇总（收入-成本-费用）；不跨币种换算";
    protected override string RequiredMenuCode => "income-statement";
    protected override string RequiredMenuText => "利润表";

    /// <summary>利润表为期间汇总：数据集不暴露日期参数，预览以「全部历史至当天」为期间。</summary>
    protected override Task<ReportDtos.FinancialStatement> LoadStatementAsync()
        => ReportService.GetIncomeStatementAsync(DateTime.MinValue, DateTime.Today);
}

/// <summary>现金流量表数据集适配器（ERP-298）：预览委托 <see cref="IReportService.GetCashFlowStatementAsync"/>。</summary>
public sealed class CashFlowReportConfigurationDatasetProvider : FinancialStatementReportConfigurationDatasetProviderBase
{
    public CashFlowReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
        : base(reportService, db)
    {
    }

    /// <inheritdoc />
    public override string DatasetKey => ReportConfigurationConstants.DatasetCashFlow;

    protected override string Label => "现金流量表";
    protected override string CurrencyUnitSemantics => "金额按单据金额直接汇总（流入-流出）；不跨币种换算";
    protected override string RequiredMenuCode => "cash-flow";
    protected override string RequiredMenuText => "现金流量表";

    /// <summary>现金流量表为期间汇总：数据集不暴露日期参数，预览以「全部历史至当天」为期间。</summary>
    protected override Task<ReportDtos.FinancialStatement> LoadStatementAsync()
        => ReportService.GetCashFlowStatementAsync(DateTime.MinValue, DateTime.Today);
}

