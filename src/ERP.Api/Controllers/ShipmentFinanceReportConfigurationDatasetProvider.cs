using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用报表配置平台（ERP-303 修正）的销售订单出货 / 财务进度数据集适配器：把既有
/// 「动态销售订单出货 / 财务进度报表」（dynamic:shipment-finance）的有限字段白名单（27 个字段）
/// 暴露为统一受控数据集。预览<strong>直接复用</strong> <see cref="SalesOrderShipmentFinanceReport.ForQueryAsync"/> 与
/// <see cref="SalesOrderProgress"/>（ERP-032 权威派生），不再复制任何出货 / 收款链接推导算法。
/// <para>行粒度为「一行一条已审核、未删除、授权范围内的销售订单」，金额按原币呈现、数量按基础单位、
/// 未知金额 / 未知数量照实保留 null，绝不跨币种合并或换算。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」销售订单（sales-order）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// <para>本适配器在 <see cref="Program"/> 中注册为运行时路径；基础设施层同名兼容类型不再注册。</para>
/// </summary>
public sealed class ShipmentFinanceReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public ShipmentFinanceReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetShipmentFinance;

    private const string Label = "销售订单出货/财务进度";
    private const string Grain = "销售订单出货 / 财务进度证据（一行一条已审核、未删除、授权范围内的销售订单）";
    private const string CurrencyUnitSemantics = "数量按基础单位；金额按原币呈现；不跨币种换算或合并";
    private const string RequiredMenuCode = "sales-order";
    private const string RequiredMenuText = "销售订单";
    private const string ReadOnlyText = "只读销售订单出货 / 财务进度数据集：仅按既有 ERP-032 口径读取已审核、未删除、授权范围销售订单及其出货 / 收款引用证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限销售订单出货 / 财务进度字段白名单（订单身份 / 客户 / 原币 / 订单金额 / 出货数量与状态 / 收款链接状态与金额 / 计数与说明）；客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态有界筛选；分页页码 ≥ 1、每页 1~200；金额按原币、数量按基础单位、未知为 null；不执行任意 SQL、不做写入";
    private const string DisclaimerText = "本预览为只读销售订单出货 / 财务进度证据：未覆盖金额只是订单金额与权威计入金额之差，不是应收余额；未知金额 / 未知数量照实保留，绝不推算或修复";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static readonly IReadOnlyList<string> GroupingKeys = new[] { ReportConfigurationConstants.GroupNone };

    private static readonly IReadOnlyList<string> SupportedCapabilities = new[]
    {
        ReportConfigurationConstants.CapabilityPreview,
        ReportConfigurationConstants.CapabilityDateRange,
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
        "orderId", "customerId", "recordedDepositAmount",
    };

    private static readonly IReadOnlyList<ReportConfigurationFieldDto> Fields = new List<ReportConfigurationFieldDto>
    {
        Field("orderId", "订单Id", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("orderNo", "订单号", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("orderDate", "订单日期", ReportConfigurationConstants.TypeDate, null, filterable: true),
        Field("status", "单据状态", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("customerId", "客户Id", ReportConfigurationConstants.TypeNumber, null, filterable: true),
        Field("customerName", "客户名称", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("currency", "币种", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("orderAmount", "订单金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("recordedDepositAmount", "已登记定金金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("orderedQuantity", "订单数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
        Field("shippedQuantity", "已出货数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
        Field("pendingShipmentQuantity", "待审核出库数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
        Field("outstandingQuantity", "未出货数量", ReportConfigurationConstants.TypeNumber, "基础单位", filterable: false),
        Field("shipmentStatus", "出货状态", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("hasApprovedShipment", "存在已审核出库单", ReportConfigurationConstants.TypeBoolean, null, filterable: false),
        Field("shipmentDocumentCount", "出库单张数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("approvedShipmentCount", "已审核出库单张数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("financeLinkStatus", "收款链接状态", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("financeLinkReason", "收款链接状态说明", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("linkedAmount", "已关联金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("uncoveredAmount", "未覆盖金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("submittedAmount", "已提交/待提交金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("otherCurrencyRecordCount", "他币种记录数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("unapprovedRecordCount", "非已审核记录数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("unattributedRecordCount", "无法归属记录数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("overReceived", "是否超收", ReportConfigurationConstants.TypeBoolean, null, filterable: false),
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

        var query = BuildQuery(definition, page, pageSize);
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var view = await SalesOrderShipmentFinanceReport.ForQueryAsync(_db, query, scope);
        var orders = view.Groups.SelectMany(g => g.Orders).ToList();

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = orders.Select(o => BuildRow(o, fieldKeys)).ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = view.Total,
            MatchedCount = view.Total,
            Page = view.Page,
            PageSize = view.PageSize,
            TotalPages = view.TotalPages,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics, ReadOnlyText, BoundaryText, DisclaimerText,
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

    private static SalesOrderShipmentFinanceQuery BuildQuery(
        ReportConfigurationDefinition definition, int page, int pageSize)
    {
        DateTime? start = null;
        DateTime? end = null;
        long? customerId = null;
        string? currency = null;
        string? shipmentStatus = null;
        string? financeLinkStatus = null;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey))
                continue;

            var key = filter.FieldKey.Trim();
            switch (key.ToLowerInvariant())
            {
                case "orderdate":
                    ApiDatasetFilterTranslation.ApplyDateFilter(filter, "orderDate", ref start, ref end);
                    break;
                case "customerid":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    customerId = ApiDatasetFilterTranslation.CoalesceLong(customerId, filter.Value, "customerId");
                    break;
                case "currency":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ApiDatasetFilterTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                case "shipmentstatus":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    shipmentStatus = ApiDatasetFilterTranslation.CoalesceString(shipmentStatus, filter.Value, "shipmentStatus");
                    break;
                case "financelinkstatus":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    financeLinkStatus = ApiDatasetFilterTranslation.CoalesceString(financeLinkStatus, filter.Value, "financeLinkStatus");
                    break;
                default:
                    throw BusinessException.InvalidParameter(
                        $"数据集 {ReportConfigurationConstants.DatasetShipmentFinance} 不支持的筛选字段：{key}");
            }
        }

        return new SalesOrderShipmentFinanceQuery
        {
            CustomerId = customerId,
            Currency = currency,
            OrderDateFrom = start,
            OrderDateTo = end,
            ShipmentStatus = shipmentStatus,
            FinanceLinkStatus = financeLinkStatus,
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

    private static Dictionary<string, object?> BuildRow(SalesOrderShipmentFinanceOrder order, IReadOnlyList<string> fieldKeys)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["orderId"] = order.OrderId,
            ["orderNo"] = order.OrderNo,
            ["orderDate"] = order.OrderDate,
            ["status"] = order.Status,
            ["customerId"] = order.CustomerId,
            ["customerName"] = order.CustomerName,
            ["currency"] = order.Currency,
            ["orderAmount"] = order.OrderAmount,
            ["recordedDepositAmount"] = order.RecordedDepositAmount,
            ["orderedQuantity"] = order.OrderedQuantity,
            ["shippedQuantity"] = order.ShippedQuantity,
            ["pendingShipmentQuantity"] = order.PendingShipmentQuantity,
            ["outstandingQuantity"] = order.OutstandingQuantity,
            ["shipmentStatus"] = order.ShipmentStatus,
            ["hasApprovedShipment"] = order.HasApprovedShipment,
            ["shipmentDocumentCount"] = order.ShipmentDocumentCount,
            ["approvedShipmentCount"] = order.ApprovedShipmentCount,
            ["financeLinkStatus"] = order.FinanceLinkStatus,
            ["financeLinkReason"] = order.FinanceLinkReason,
            ["linkedAmount"] = order.LinkedAmount,
            ["uncoveredAmount"] = order.UncoveredAmount,
            ["submittedAmount"] = order.SubmittedAmount,
            ["otherCurrencyRecordCount"] = order.OtherCurrencyRecordCount,
            ["unapprovedRecordCount"] = order.UnapprovedRecordCount,
            ["unattributedRecordCount"] = order.UnattributedRecordCount,
            ["overReceived"] = order.OverReceived,
            ["note"] = order.Note,
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
            SortingExplanation = "本数据集不支持任意排序：稳定按客户 Id → 币种 → 订单日期 → 订单 Id 排序（与既有出货/财务进度报表一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}

