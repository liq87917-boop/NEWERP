using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-298）的应收账款账龄分析数据集适配器：暴露既有 <c>ArAging</c> 固定报表的
/// 有限字段白名单（客户 / 订单 / 日期 / 币种 / 金额 / 账龄等）与只读、有界预览口径。
/// <para>预览委托既有 <see cref="IReportService.GetArAgingAsync"/> 语义，金额按订单原币呈现，账龄按自然日，
/// 不跨币种换算或合并；不新增控制器 / 设计器 / 导出器。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」应收账龄菜单授权（fail closed）。</para>
/// </summary>
public sealed class ArAgingReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IReportService _reportService;
    private readonly IErpDbContext _db;

    public ArAgingReportConfigurationDatasetProvider(IReportService reportService, IErpDbContext db)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetArAging;

    private const string Label = "应收账龄分析表";
    private const string Grain = "应收账款账龄分析（一行一条仍有未收余额的销售订单）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；账龄按自然日；不跨币种换算或合并";
    private const string RequiredMenuCode = "ar-aging";
    private const string RequiredMenuText = "应收账龄分析表";
    private const string ReadOnlyText = "只读应收账龄分析数据集：仅按既有报表服务语义读取销售订单与已审核收款证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：金额按订单原币呈现，账龄按自然日；仅列出仍有未收余额（>0.005）的订单；不跨币种换算或合并";
    private const string DisclaimerText = "本预览为只读应收账龄证据，不构成应收账款余额、收款 / 核销或法定对账结论";
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

    private static readonly Dictionary<string, string> CurrencyUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["currency"] = "币种代码",
        ["orderAmount"] = "原币金额",
        ["receivedAmount"] = "原币金额",
        ["balance"] = "原币金额",
        ["agingDays"] = "天",
        ["creditDays"] = "天",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new[]
    {
        Field("customerName", "客户名称", ReportConfigurationConstants.TypeText, null),
        Field("orderNo", "订单号", ReportConfigurationConstants.TypeText, null),
        Field("orderDate", "订单日期", ReportConfigurationConstants.TypeDate, null),
        Field("currency", "币种", ReportConfigurationConstants.TypeText, "币种代码"),
        Field("orderAmount", "订单金额", ReportConfigurationConstants.TypeNumber, "原币金额"),
        Field("receivedAmount", "已收金额", ReportConfigurationConstants.TypeNumber, "原币金额"),
        Field("balance", "未收余额", ReportConfigurationConstants.TypeNumber, "原币金额"),
        Field("agingDays", "账龄天数", ReportConfigurationConstants.TypeNumber, "天"),
        Field("creditDays", "账期天数", ReportConfigurationConstants.TypeNumber, "天"),
        Field("bucket", "账龄区间", ReportConfigurationConstants.TypeText, null),
        Field("status", "状态", ReportConfigurationConstants.TypeText, null),
    };

    private static ReportConfigurationFieldDto Field(string key, string label, string type, string? unit)
        => new(key, label, type, unit,
            Filterable: false, Aggregatable: false, Hidden: false,
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
            // 无应收账龄菜单授权 → 该数据集不暴露（fail closed）。
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

        // 每次请求重新校验身份 / 应收账龄菜单授权（fail closed，先于任何源读取）。
        await EnsureAuthorizedAsync(userId, cancellationToken);

        var fieldKeys = NormalizeFields(definition.Fields);
        var items = await _reportService.GetArAgingAsync(DateTime.Today);

        var page = parameters.Page < 1 ? 1 : parameters.Page;
        var pageSize = Math.Clamp(parameters.PageSize, 1, MaxPageSize);
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
            SortingExplanation = "本数据集不支持任意排序：稳定按账龄天数降序 → 客户名称升序",
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
        ReportDtos.ArAgingItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fieldKeys.Contains("customerName", StringComparer.OrdinalIgnoreCase)) row["customerName"] = item.CustomerName;
        if (fieldKeys.Contains("orderNo", StringComparer.OrdinalIgnoreCase)) row["orderNo"] = item.OrderNo;
        if (fieldKeys.Contains("orderDate", StringComparer.OrdinalIgnoreCase)) row["orderDate"] = item.OrderDate;
        if (fieldKeys.Contains("currency", StringComparer.OrdinalIgnoreCase)) row["currency"] = item.Currency;
        if (fieldKeys.Contains("orderAmount", StringComparer.OrdinalIgnoreCase)) row["orderAmount"] = item.OrderAmount;
        if (fieldKeys.Contains("receivedAmount", StringComparer.OrdinalIgnoreCase)) row["receivedAmount"] = item.ReceivedAmount;
        if (fieldKeys.Contains("balance", StringComparer.OrdinalIgnoreCase)) row["balance"] = item.Balance;
        if (fieldKeys.Contains("agingDays", StringComparer.OrdinalIgnoreCase)) row["agingDays"] = item.AgingDays;
        if (fieldKeys.Contains("creditDays", StringComparer.OrdinalIgnoreCase)) row["creditDays"] = item.CreditDays;
        if (fieldKeys.Contains("bucket", StringComparer.OrdinalIgnoreCase)) row["bucket"] = item.Bucket;
        if (fieldKeys.Contains("status", StringComparer.OrdinalIgnoreCase)) row["status"] = item.Status;
        return row;
    }

}
