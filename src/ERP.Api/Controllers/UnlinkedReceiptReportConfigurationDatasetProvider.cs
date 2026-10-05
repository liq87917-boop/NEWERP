using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用报表配置平台（ERP-309 Stage 2）的客户级未关联收款证据数据集适配器：把既有
/// 「动态客户订单与收款核对报表」（dynamic:receipt-reconciliation）中独立承载的未关联收款证据
/// （legacy <c>ReceiptRows</c>）暴露为统一受控数据集。
/// <para>预览<strong>直接复用</strong> <see cref="SalesOrderReceiptReconciliation.ForQueryAsync"/> 的
/// <see cref="SalesOrderReceiptReconciliationReport.UnlinkedReceipts"/> 结果（ERP-046 权威派生），
/// 不再复制任何收款匹配 / 归属算法；行粒度为「一行一条收款单」，收款单只有客户级引用
/// （<c>FinanceReceipt.CustomerId</c>），链接状态恒为 unlinked，金额按收款单自身币种原样列出，
/// 绝不并入订单合计、绝不按客户名 / 单号文本 / 日期 / 金额相似度猜测订单。</para>
/// <para>本数据集与「客户订单与收款核对」订单侧数据集相互独立：订单侧只回显订单证据，本数据集只回显
/// 未关联收款证据；同一受限订单页的客户 / 筛选集合有界派生本数据集（无隐藏联接、无跨数据集合计）。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」销售订单（sales-order）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class UnlinkedReceiptReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public UnlinkedReceiptReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetUnlinkedReceipt;

    private const string Label = "客户级未关联收款证据";
    private const string Grain = "未关联收款证据（一行一条收款单：只记录客户、无订单级引用，链接状态恒为 unlinked）";
    private const string CurrencyUnitSemantics = "金额按收款单自身币种原样列出；不跨币种换算或合并";
    private const string RequiredMenuCode = "sales-order";
    private const string RequiredMenuText = "销售订单";
    private const string ReadOnlyText = "只读客户级未关联收款证据数据集：仅按既有 ERP-046 口径读取本页订单客户名下的收款单，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限未关联收款证据白名单（收款单身份 / 客户 / 原币 / 收款金额 / 付款方式 / 状态 / 证据分档 / 链接状态与引用）；客户 / 币种 / 收款证据状态有界筛选；收款单只记录客户、绝不匹配或并入订单；金额按收款单原币、未知为 null；不执行任意 SQL、不做写入";
    private const string DisclaimerText = "本预览为只读客户级未关联收款证据：收款单只记录客户（FinanceReceipt.CustomerId），没有订单级引用，链接状态恒为 unlinked；金额绝不并入订单合计、绝不按客户名 / 单号 / 日期 / 金额相似度猜测匹配；命中读取上限时显式标注截断";
    private const string TruncationDisclaimer = DisclaimerText + "；本页收款证据命中读取上限，被截断（不完整，请缩小筛选范围后重试）";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

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

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "receiptId", "customerId",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("receiptId", "收款单Id", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("receiptNo", "收款单号", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("receiptDate", "收款日期", ReportConfigurationConstants.TypeDate, null, filterable: false),
        Field("customerId", "客户Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("customerName", "客户名", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currency", "币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("amount", "收款金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("paymentMethod", "付款方式", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("status", "单据状态", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("evidenceStatus", "收款证据状态", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("evidenceText", "收款证据文案", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("receiptLinkageStatus", "收款链接状态", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("receiptLinkageText", "收款链接文案", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("referenceField", "建立引用字段", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("note", "说明", ReportConfigurationConstants.TypeText, null, filterable: false),
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

        await EnsureAuthorizedAsync(userId, cancellationToken);
        ReportConfigurationRules.Validate(definition, BuildDataset());

        var (page, pageSize) = ValidatePageBounds(parameters);
        var fieldKeys = NormalizeFields(definition.Fields);

        // 固定第 1 页、最多 200 条订单派生「本页客户」集合（与 legacy 未关联收款证据同口径：按本页客户有界读取），
        // 未关联收款证据本身是有限单页（≤ UnlinkedReceiptLimit），在此之上按通用分页在内存中有界切片。
        var query = BuildQuery(definition, page: 1, pageSize: 200);
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(_db, query, scope);
        var receipts = report.UnlinkedReceipts;

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = receipts
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => BuildRow(r, fieldKeys))
            .ToList();

        var total = receipts.Count;
        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = total,
            MatchedCount = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize),
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics, ReadOnlyText, BoundaryText,
                report.PageUnlinkedReceiptTruncated ? TruncationDisclaimer : DisclaimerText,
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

    private static SalesOrderReceiptReconciliationQuery BuildQuery(
        ReportConfigurationDefinition definition, int page, int pageSize)
    {
        long? customerId = null;
        string? currency = null;
        string? receiptStatus = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            var key = (filter.FieldKey ?? string.Empty).Trim().ToLowerInvariant();
            switch (key)
            {
                case "customerid":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    customerId = ApiDatasetFilterTranslation.CoalesceLong(customerId, filter.Value, "customerId");
                    break;
                case "currency":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ApiDatasetFilterTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                case "evidencestatus":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    receiptStatus = ApiDatasetFilterTranslation.CoalesceString(receiptStatus, filter.Value, "evidenceStatus");
                    break;
                default:
                    throw BusinessException.InvalidParameter(
                        $"数据集 {ReportConfigurationConstants.DatasetUnlinkedReceipt} 不支持的筛选字段：{key}");
            }
        }

        return new SalesOrderReceiptReconciliationQuery
        {
            CustomerId = customerId,
            Currency = currency,
            ReceiptStatus = receiptStatus,
            Page = page,
            PageSize = pageSize,
        };
    }


    private static IReadOnlyList<string> NormalizeFields(IReadOnlyList<string>? fields)
    {
        var selected = new List<string>();
        foreach (var raw in fields ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var field = Fields.First(f => string.Equals(f.Key, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!selected.Contains(field.Key, StringComparer.Ordinal))
                selected.Add(field.Key);
        }

        if (selected.Count == 0)
            selected.AddRange(Fields.Select(f => f.Key));

        return selected;
    }

    private static Dictionary<string, object?> BuildRow(
        SalesOrderReceiptReconciliationReceipt receipt, IReadOnlyList<string> fieldKeys)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["receiptId"] = receipt.ReceiptId,
            ["receiptNo"] = receipt.ReceiptNo,
            ["receiptDate"] = receipt.ReceiptDate,
            ["customerId"] = receipt.CustomerId,
            ["customerName"] = receipt.CustomerName,
            ["currency"] = receipt.Currency,
            ["amount"] = receipt.Amount,
            ["paymentMethod"] = receipt.PaymentMethod,
            ["status"] = receipt.Status,
            ["evidenceStatus"] = receipt.EvidenceStatus,
            ["evidenceText"] = receipt.EvidenceText,
            ["receiptLinkageStatus"] = receipt.ReceiptLinkageStatus,
            ["receiptLinkageText"] = receipt.ReceiptLinkageText,
            ["referenceField"] = receipt.ReferenceField,
            ["note"] = receipt.Note,
        };

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = source.TryGetValue(key, out var value) ? value : null;

        return row;
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
            SortingExplanation = "本数据集不支持任意排序：稳定按收款日期 → 收款单 Id 排序（与既有客户订单与收款核对报表未关联收款证据一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}

