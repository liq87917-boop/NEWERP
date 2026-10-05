using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-306 Stage 2）的「出口单证中心」受控数据集适配器：把既有
/// 「出口单证打印数据」（document:trade-document-print）与「出口单证台账导出」
/// （document:trade-document-export-excel）共用的持久表头 / 明细行快照，暴露为统一受控数据集。
/// <para>表头复用 <see cref="TradeDocumentPrintModel.From(TradeDocument)"/> 的持久快照语义；
/// 明细行复用 <see cref="TradeDocumentPrintLine.From(TradeDocumentItem, string?, string?)"/>
/// 的持久行快照语义，保留 null 快照与原币 / 基础单位，绝不回查档案、不重建缺失证据、不跨币种换算。
/// 表头金额只在表头粒度出现，绝不在明细行聚合中重复。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」单证中心（doc-center）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）特权数据范围（旧台账为全局只读，
/// 仅特权账号可执行；受限制 / 撤销范围一律 fail closed，且先于任何源读取）。</para>
/// </summary>
public sealed class TradeDocumentReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public TradeDocumentReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetTradeDocument;

    private const string Label = "出口单证中心";
    private const string Grain = "出口单证中心（表头粒度一行一张单证；明细行粒度一行一条行快照，二者通过字段区分，绝不混写）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；数量按基础单位；不跨币种换算或合并";
    private const string RequiredMenuCode = "doc-center";
    private const string RequiredMenuText = "单证中心";
    private const string ReadOnlyText = "只读出口单证中心数据集：仅读取单证台账持久表头与明细行快照，不新增 / 修改 / 删除任何记录，不重建缺失证据";
    private const string BoundaryText = "口径：字段仅限单证台账持久表头与明细行快照白名单；表头金额只在表头粒度出现，明细行只输出行金额（原币）与箱数 / 净重 / 毛重，绝不在明细聚合中重复表头金额；日期窗口有界（含首尾最多 366 天）；受控文档选择上限 5000 张、明细行上限 5000 行；模糊关键字与打印布局兼容尚未复现，属不支持 / 未验证范围";
    private const string DisclaimerText = "本预览为只读单证中心证据：金额按原币、数量按基础单位呈现，不跨币种换算或合并；表头与明细行分粒度输出，不把表头金额摊入明细；不构成报关、清关、退税或财务结论";
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxSourceDocuments = 5000;
    private const int MaxDateRangeDays = 366;
    private const int MaxLineRows = 5000;

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

    private static readonly HashSet<string> HeaderOnlyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "amount", "currency", "departurePort", "destinationPort", "issuedBy", "copies", "fileNote", "remark",
    };

    private static readonly HashSet<string> SharedIdentityFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "docNo", "docType", "status", "issueDate", "customerName", "salesOrderNo", "refNo", "declareNo",
    };

    private static readonly HashSet<string> LineOnlyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "lineNo", "productCode", "productNameCn", "productNameEn", "spec", "quantity", "unit",
        "unitPrice", "lineCurrency", "lineAmount", "packageCount", "netWeight", "grossWeight", "lineRemark",
    };

    private static ReportConfigurationFieldDto Field(
        string key, string label, string type, string? unit, bool filterable = false, bool aggregatable = false)
        => new(key, label, type, unit,
            Filterable: filterable,
            Aggregatable: aggregatable,
            Hidden: false,
            filterable && string.Equals(type, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase)
                ? DateRangeOperators
                : filterable
                    ? EqOnlyOperators
                    : ReportConfigurationRules.GetOperatorsForType(type));

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("id", "单证Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("docNo", "单证编号", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("docType", "单证类型", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("status", "状态", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("issueDate", "出具/签发日期", ReportConfigurationConstants.TypeDate, null, filterable: true),
        Field("customerName", "客户名称", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("salesOrderNo", "关联销售订单号", ReportConfigurationConstants.TypeText, null),
        Field("refNo", "关联柜号/订舱号", ReportConfigurationConstants.TypeText, null),
        Field("declareNo", "关联报关单号", ReportConfigurationConstants.TypeText, null),
        Field("amount", "单证金额", ReportConfigurationConstants.TypeNumber, "原币金额", aggregatable: true),
        Field("currency", "币种", ReportConfigurationConstants.TypeText, null),
        Field("departurePort", "起运港", ReportConfigurationConstants.TypeText, null),
        Field("destinationPort", "目的港", ReportConfigurationConstants.TypeText, null),
        Field("issuedBy", "制作人/出证机构", ReportConfigurationConstants.TypeText, null),
        Field("copies", "份数", ReportConfigurationConstants.TypeNumber, null, aggregatable: true),
        Field("fileNote", "附件说明", ReportConfigurationConstants.TypeText, null),
        Field("remark", "备注", ReportConfigurationConstants.TypeText, null),
        Field("lineNo", "行序", ReportConfigurationConstants.TypeNumber, null),
        Field("productCode", "商品编码", ReportConfigurationConstants.TypeText, null),
        Field("productNameCn", "商品中文名称", ReportConfigurationConstants.TypeText, null),
        Field("productNameEn", "商品英文名称", ReportConfigurationConstants.TypeText, null),
        Field("spec", "规格型号", ReportConfigurationConstants.TypeText, null),
        Field("quantity", "数量", ReportConfigurationConstants.TypeNumber, "数量按基础单位", aggregatable: true),
        Field("unit", "单位", ReportConfigurationConstants.TypeText, null),
        Field("unitPrice", "单价", ReportConfigurationConstants.TypeNumber, "原币", aggregatable: true),
        Field("lineCurrency", "行币种", ReportConfigurationConstants.TypeText, null),
        Field("lineAmount", "行金额", ReportConfigurationConstants.TypeNumber, "原币金额", aggregatable: true),
        Field("packageCount", "箱数", ReportConfigurationConstants.TypeNumber, null, aggregatable: true),
        Field("netWeight", "净重kg", ReportConfigurationConstants.TypeNumber, "kg", aggregatable: true),
        Field("grossWeight", "毛重kg", ReportConfigurationConstants.TypeNumber, "kg", aggregatable: true),
        Field("lineRemark", "行备注", ReportConfigurationConstants.TypeText, null),
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
        var selected = new HashSet<string>(fieldKeys, StringComparer.OrdinalIgnoreCase);
        var isLineGrain = LineOnlyFields.Any(selected.Contains);

        var id = ResolveLongEq(definition, "id");
        var docNo = ResolveTextEq(definition, "docNo");
        var docType = ResolveTextEq(definition, "docType");
        var status = ResolveTextEq(definition, "status");
        var customerName = ResolveTextEq(definition, "customerName");
        var (startDate, endDate) = ResolveDateRange(definition);

        var source = _db.TradeDocuments.AsNoTracking().Where(d => !d.IsDeleted);
        if (id is not null) source = source.Where(d => d.Id == id.Value);
        if (docNo is not null) source = source.Where(d => d.DocNo == docNo);
        if (docType is not null) source = source.Where(d => d.DocType == docType);
        if (status is not null) source = source.Where(d => d.Status == status);
        if (customerName is not null) source = source.Where(d => d.CustomerName == customerName);
        if (startDate is not null) source = source.Where(d => d.IssueDate >= startDate);
        if (endDate is not null) source = source.Where(d => d.IssueDate <= endDate);

        var columns = fieldKeys.Select(BuildColumn).ToList();

        if (!isLineGrain)
        {
            var total = await source.CountAsync(cancellationToken);
            if (total > MaxSourceDocuments)
            {
                throw new BusinessException(
                    $"{Label}的授权范围内选择超过 {MaxSourceDocuments} 张单证，请缩小筛选范围后重试",
                    ErrorCodes.RuleConflict);
            }

            var documents = await source
                .OrderByDescending(d => d.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            var rows = documents.Select(d => BuildHeaderRow(d, selected)).ToList();
            return BuildPreview(rows, total, page, pageSize, columns);
        }

        // 明细行粒度：一次装载有界文档集，再按文档 Id 集合批量装载明细行（避免逐单证 / 逐行查库）。
        var allDocuments = await source.OrderByDescending(d => d.Id).ToListAsync(cancellationToken);
        if (allDocuments.Count > MaxSourceDocuments)
        {
            throw new BusinessException(
                $"{Label}的授权范围内选择超过 {MaxSourceDocuments} 张单证，请缩小筛选范围后重试",
                ErrorCodes.RuleConflict);
        }

        var documentIds = allDocuments.Select(d => d.Id).ToList();
        var items = await _db.TradeDocumentItems.AsNoTracking()
            .Where(i => !i.IsDeleted && documentIds.Contains(i.TradeDocumentId))
            .OrderBy(i => i.TradeDocumentId).ThenBy(i => i.LineNo).ThenBy(i => i.Id)
            .Take(MaxLineRows + 1)
            .ToListAsync(cancellationToken);

        if (items.Count > MaxLineRows)
        {
            throw new BusinessException(
                $"{Label}的明细行超过上限 {MaxLineRows} 行，请缩小文档选择范围后重试",
                ErrorCodes.RuleConflict);
        }

        var byDocument = items.GroupBy(i => i.TradeDocumentId).ToDictionary(g => g.Key, g => g.ToList());
        var lineRows = new List<Dictionary<string, object?>>();
        foreach (var document in allDocuments)
        {
            if (!byDocument.TryGetValue(document.Id, out var lines))
                continue;

            foreach (var item in lines)
                lineRows.Add(BuildLineRow(document, item, selected));
        }

        var totalLines = lineRows.Count;
        var pagedLines = lineRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return BuildPreview(pagedLines, totalLines, page, pageSize, columns);
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

        // 旧单证中心台账为全局只读：仅特权账号可执行，受限制 / 未解析范围一律 fail closed（先于任何源读取）。
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId.Value);
        if (!scope.IsPrivileged)
        {
            throw new BusinessException(
                $"当前账号不是全量数据范围账号：拒绝预览{Label}"
                + "（fail closed，不执行全局只读）",
                ErrorCodes.Forbidden);
        }
    }

    private ReportConfigurationPreviewDto BuildPreview(
        IReadOnlyList<Dictionary<string, object?>> rows,
        int total,
        int page,
        int pageSize,
        IReadOnlyList<ReportConfigurationColumnDto> columns)
    {
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns.ToList(),
            Rows = rows.ToList(),
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
            SortingExplanation = "本数据集不支持任意排序：稳定按单证 Id 降序（与既有台账导出列表一致）",
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

    private static Dictionary<string, object?> BuildHeaderRow(TradeDocument document, HashSet<string> selected)
    {
        var model = TradeDocumentPrintModel.From(document);
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        void Set(string key, object? value) { if (selected.Contains(key)) row[key] = value; }

        Set("id", document.Id);
        Set("docNo", document.DocNo);
        Set("docType", document.DocType);
        Set("status", document.Status);
        Set("issueDate", IssueDateAvailable(model) ? document.IssueDate : null);
        Set("customerName", document.CustomerName);
        Set("salesOrderNo", document.SalesOrderNo);
        Set("refNo", document.RefNo);
        Set("declareNo", document.DeclareNo);
        Set("amount", document.Amount);
        Set("currency", document.Currency);
        Set("departurePort", document.DeparturePort);
        Set("destinationPort", document.DestinationPort);
        Set("issuedBy", document.IssuedBy);
        Set("copies", document.Copies);
        Set("fileNote", document.FileNote);
        Set("remark", document.Remark);
        foreach (var key in LineOnlyFields) Set(key, null);
        return row;
    }

    private static bool IssueDateAvailable(TradeDocumentPrintModel model)
        => model.Fields.FirstOrDefault(f =>
            string.Equals(f.Key, TradeDocumentPrintSemantics.IssueDate, StringComparison.OrdinalIgnoreCase))?.Available == true;

    private static Dictionary<string, object?> BuildLineRow(TradeDocument document, TradeDocumentItem item, HashSet<string> selected)
    {
        var line = TradeDocumentPrintLine.From(item, document.DocType, document.Currency);
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        void Set(string key, object? value) { if (selected.Contains(key)) row[key] = value; }

        Set("docNo", document.DocNo);
        Set("docType", document.DocType);
        Set("status", document.Status);
        Set("issueDate", document.IssueDate);
        Set("customerName", document.CustomerName);
        Set("salesOrderNo", document.SalesOrderNo);
        Set("refNo", document.RefNo);
        Set("declareNo", document.DeclareNo);
        foreach (var key in HeaderOnlyFields) Set(key, null);
        Set("id", document.Id);

        Set("lineNo", line.LineNo);
        Set("productCode", line.ProductCode);
        Set("productNameCn", line.ProductNameCn);
        Set("productNameEn", line.ProductNameEn);
        Set("spec", line.Spec);
        Set("quantity", line.Quantity);
        Set("unit", line.Unit);
        Set("unitPrice", line.HasPricing ? line.UnitPrice : null);
        Set("lineCurrency", line.HasPricing ? line.Currency : null);
        Set("lineAmount", line.HasPricing ? line.LineAmount : null);
        Set("packageCount", line.HasPackaging ? line.PackageCount : null);
        Set("netWeight", line.HasPackaging ? line.NetWeight : null);
        Set("grossWeight", line.HasPackaging ? line.GrossWeight : null);
        Set("lineRemark", line.Remark);
        return row;
    }

    private static (DateTime? Start, DateTime? End) ResolveDateRange(ReportConfigurationDefinition definition)
    {
        DateTime? start = null;
        DateTime? end = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;
            if (!string.Equals(filter.FieldKey.Trim(), "issueDate", StringComparison.OrdinalIgnoreCase))
                continue;

            ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "issueDate", ref start, ref end);
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

