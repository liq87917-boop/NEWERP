using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用报表配置平台（ERP-303 Stage 2）的客户订单与收款核对数据集适配器：把既有
/// 「动态客户订单与收款核对报表」（dynamic:receipt-reconciliation）的订单证据字段白名单暴露为统一受控数据集。
/// 预览<strong>直接复用</strong> <see cref="SalesOrderReceiptReconciliation.ForQueryAsync"/> 与
/// <see cref="SalesOrderProgress"/>（ERP-046 / ERP-032 权威派生），不再复制任何核对 / 匹配算法。
/// <para>行粒度为「一行一条已审核、未删除、授权范围内的销售订单」；金额按原币呈现、数量按基础单位，
/// 未知金额 / 未知数量照实保留 null。未关联收款证据是独立的证据族，绝不分配、合入订单行，也绝不用 0 顶替未知金额；
/// 该证据族仍由既有 legacy 路由承载，本受控数据集只回显订单侧证据。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」销售订单（sales-order）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
/// </summary>
public sealed class ReceiptReconciliationReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public ReceiptReconciliationReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetReceiptReconciliation;

    private const string Label = "客户订单与收款核对";
    private const string Grain = "客户订单与收款核对证据（一行一条已审核、未删除、授权范围内的销售订单）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；订单与收款核对；不跨币种换算或合并";
    private const string RequiredMenuCode = "sales-order";
    private const string RequiredMenuText = "销售订单";
    private const string ReadOnlyText = "只读客户订单与收款核对数据集：仅按既有 ERP-046 / ERP-032 口径读取授权范围销售订单及其收款覆盖证据，不新增 / 修改 / 删除任何记录";
    private const string BoundaryText = "口径：字段仅限客户订单与收款核对订单证据白名单（订单身份 / 客户 / 原币 / 订单金额 / 出货数量与状态 / 收款覆盖状态与金额 / 收款引用证据 / 销项发票证据 / 计数与说明）；客户 / 币种 / 订单日期 / 出货状态 / 收款覆盖状态有界筛选（收款证据状态 / 订单状态筛选由既有 legacy 路由承载，本受控数据集不暴露）；未关联收款证据独立承载、绝不并入订单行；金额按原币、数量按基础单位、未知为 null；不执行任意 SQL、不做写入";
    private const string DisclaimerText = "本预览为只读客户订单与收款核对证据：未覆盖金额只是订单金额与权威计入金额之差，不是应收余额；未关联收款证据绝不分配或并入订单；未知金额 / 未知数量照实保留，绝不推算或修复";
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
        Field("amountDecimals", "币种小数位", ReportConfigurationConstants.TypeNumber, null, filterable: false),
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
        Field("receiptCoverageStatus", "收款覆盖状态", ReportConfigurationConstants.TypeText, null, filterable: true),
        Field("linkedReceiptAmount", "已关联收款金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("pendingReceiptAmount", "未审核收款金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("uncoveredAmount", "未覆盖金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("otherCurrencyReceiptCount", "他币种收款条数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("unapprovedReceiptCount", "非已审核收款条数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("unattributedReceiptCount", "无法归属收款条数", ReportConfigurationConstants.TypeNumber, null, filterable: false),
        Field("receiptAllocationStatus", "收款引用证据状态", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("recordedReceiptAllocationAmount", "有效收款引用金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
        Field("invoiceEvidenceStatus", "销项发票证据状态", ReportConfigurationConstants.TypeText, null, filterable: false),
        Field("recordedInvoicedAmount", "有效发票分摊金额", ReportConfigurationConstants.TypeNumber, "原币金额", filterable: false),
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

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(_db, query, scope);
        var orders = report.Groups.SelectMany(g => g.Orders).ToList();

        var columns = fieldKeys.Select(BuildColumn).ToList();
        var rows = orders.Select(o => BuildRow(o, fieldKeys)).ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = report.Total,
            MatchedCount = report.Total,
            Page = report.Page,
            PageSize = report.PageSize,
            TotalPages = report.TotalPages,
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

    private static SalesOrderReceiptReconciliationQuery BuildQuery(
        ReportConfigurationDefinition definition, int page, int pageSize)
    {
        DateTime? start = null;
        DateTime? end = null;
        long? customerId = null;
        string? currency = null;
        string? shipmentStatus = null;
        string? receiptLinkStatus = null;
        string? receiptStatus = null;
        string? orderStatus = null;

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
                case "receiptcoveragestatus":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    receiptLinkStatus = ApiDatasetFilterTranslation.CoalesceString(receiptLinkStatus, filter.Value, "receiptCoverageStatus");
                    break;
                case "receiptstatus":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    receiptStatus = ApiDatasetFilterTranslation.CoalesceString(receiptStatus, filter.Value, "receiptStatus");
                    break;
                case "orderstatus":
                    ApiDatasetFilterTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    orderStatus = ApiDatasetFilterTranslation.CoalesceString(orderStatus, filter.Value, "orderStatus");
                    break;
                default:
                    throw BusinessException.InvalidParameter(
                        $"数据集 {ReportConfigurationConstants.DatasetReceiptReconciliation} 不支持的筛选字段：{key}");
            }
        }

        return new SalesOrderReceiptReconciliationQuery
        {
            CustomerId = customerId,
            Currency = currency,
            OrderDateFrom = start,
            OrderDateTo = end,
            ShipmentStatus = shipmentStatus,
            ReceiptLinkStatus = receiptLinkStatus,
            ReceiptStatus = receiptStatus,
            OrderStatus = orderStatus,
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
        SalesOrderReceiptReconciliationOrderRow order, IReadOnlyList<string> fieldKeys)
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
            ["amountDecimals"] = order.AmountDecimals,
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
            ["receiptCoverageStatus"] = order.ReceiptCoverageStatus,
            ["linkedReceiptAmount"] = order.LinkedReceiptAmount,
            ["pendingReceiptAmount"] = order.PendingReceiptAmount,
            ["uncoveredAmount"] = order.UncoveredAmount,
            ["otherCurrencyReceiptCount"] = order.OtherCurrencyReceiptCount,
            ["unapprovedReceiptCount"] = order.UnapprovedReceiptCount,
            ["unattributedReceiptCount"] = order.UnattributedReceiptCount,
            ["receiptAllocationStatus"] = order.ReceiptAllocationStatus,
            ["recordedReceiptAllocationAmount"] = order.RecordedReceiptAllocationAmount,
            ["invoiceEvidenceStatus"] = order.InvoiceEvidenceStatus,
            ["recordedInvoicedAmount"] = order.RecordedInvoicedAmount,
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
            SortingExplanation = "本数据集不支持任意排序：稳定按客户 Id → 币种 → 订单日期 → 订单 Id 排序（与既有客户订单与收款核对报表一致）",
        };
    }

    private static ReportConfigurationColumnDto BuildColumn(string key)
    {
        var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        return new ReportConfigurationColumnDto(field.Key, field.Label, field.Type, field.CurrencyUnit);
    }
}


/// <summary>
/// Api 层受控数据集适配器的有界类型化筛选翻译：与基础设施层同名辅助解耦（Api 层不复用 Infrastructure 的 internal 辅助），
/// 只支持适配器显式声明的有限字段 / 操作符，任何不兼容操作显式拒绝（fail closed）。
/// </summary>
internal static class ApiDatasetFilterTranslation
{
    public static void EnsureOperator(ReportConfigurationFilter filter, string supportedOperator)
    {
        var op = (filter.Operator ?? string.Empty).Trim();
        if (!string.Equals(op, supportedOperator, StringComparison.OrdinalIgnoreCase))
        {
            throw BusinessException.InvalidParameter(
                $"字段 {filter.FieldKey} 在当前数据集适配中仅支持操作符 {supportedOperator}（收到 {op}）");
        }
    }

    public static long CoalesceLong(long? current, object? value, string fieldKey)
    {
        var parsed = RequireLong(value, fieldKey);
        if (current is null)
            return parsed;
        if (current.Value != parsed)
            throw BusinessException.InvalidParameter($"字段 {fieldKey} 的重复相等筛选值冲突（{current.Value} 与 {parsed}），无法合并");
        return current.Value;
    }

    public static string CoalesceString(string? current, object? value, string fieldKey)
    {
        var parsed = RequireString(value, fieldKey);
        if (current is null)
            return parsed;
        if (!string.Equals(current, parsed, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter($"字段 {fieldKey} 的重复相等筛选值冲突（{current} 与 {parsed}），无法合并");
        return current;
    }

    public static void ApplyDateFilter(
        ReportConfigurationFilter filter, string fieldKey, ref DateTime? start, ref DateTime? end)
    {
        var op = (filter.Operator ?? string.Empty).Trim();
        switch (op)
        {
            case ReportConfigurationConstants.OperatorEq:
            {
                var eq = RequireDate(filter.Value, fieldKey).Date;
                IntersectDayRange(ref start, ref end, eq, eq);
                break;
            }
            case ReportConfigurationConstants.OperatorGte:
                IntersectLowerDay(ref start, RequireDate(filter.Value, fieldKey).Date);
                break;
            case ReportConfigurationConstants.OperatorLte:
                IntersectUpperDay(ref end, RequireDate(filter.Value, fieldKey).Date);
                break;
            case ReportConfigurationConstants.OperatorGt:
                IntersectLowerDay(ref start, SafeAddDays(RequireDate(filter.Value, fieldKey).Date, 1, fieldKey, "严格大于"));
                break;
            case ReportConfigurationConstants.OperatorLt:
                IntersectUpperDay(ref end, SafeAddDays(RequireDate(filter.Value, fieldKey).Date, -1, fieldKey, "严格小于"));
                break;
            case ReportConfigurationConstants.OperatorBetween:
            {
                var lower = RequireDate(filter.Value, fieldKey).Date;
                var upper = RequireDate(filter.Value2, fieldKey).Date;
                if (lower > upper)
                    throw BusinessException.InvalidParameter($"字段 {fieldKey} 的 between 下界不能大于上界");
                IntersectDayRange(ref start, ref end, lower, upper);
                break;
            }
            default:
                throw BusinessException.InvalidParameter($"字段 {fieldKey} 不支持的日期筛选操作符: {op}");
        }
    }


    private static long RequireLong(object? value, string context)
    {
        var v = ToClr(value);
        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case short s: return s;
            case byte b: return b;
            case decimal m when m == decimal.Truncate(m): return (long)m;
            case double d when d == Math.Truncate(d): return (long)d;
            default:
                throw BusinessException.InvalidParameter($"{context} 必须是整数");
        }
    }

    private static string RequireString(object? value, string context)
    {
        var v = ToClr(value);
        if (v is string s)
            return s;
        throw BusinessException.InvalidParameter($"{context} 必须是字符串");
    }

    private static DateTime RequireDate(object? value, string context)
    {
        var v = ToClr(value);
        switch (v)
        {
            case DateTime dt: return dt;
            case DateTimeOffset dto: return dto.DateTime;
            case string s when DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.RoundtripKind, out var parsed):
                return parsed;
            default:
                throw BusinessException.InvalidParameter($"{context} 必须是日期");
        }
    }

    private static void IntersectLowerDay(ref DateTime? start, DateTime lower)
    {
        if (start is null || lower > start.Value)
            start = lower;
    }

    private static void IntersectUpperDay(ref DateTime? end, DateTime upper)
    {
        if (end is null || upper < end.Value)
            end = upper;
    }

    private static void IntersectDayRange(ref DateTime? start, ref DateTime? end, DateTime lower, DateTime upper)
    {
        IntersectLowerDay(ref start, lower);
        IntersectUpperDay(ref end, upper);
    }

    private static DateTime SafeAddDays(DateTime date, int days, string fieldKey, string context)
    {
        try
        {
            return date.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw BusinessException.InvalidParameter($"字段 {fieldKey} 的日期边界超出日历可表示范围（{context}）");
        }
    }

    private static object? ToClr(object? value)
        => value is System.Text.Json.JsonElement element ? JsonElementToClr(element) : value;

    private static object? JsonElementToClr(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                return element.GetString();
            case System.Text.Json.JsonValueKind.True:
                return true;
            case System.Text.Json.JsonValueKind.False:
                return false;
            case System.Text.Json.JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDecimal();
            case System.Text.Json.JsonValueKind.Array:
                return element.EnumerateArray().Select(JsonElementToClr).ToList();
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
            case System.Text.Json.JsonValueKind.Object:
            default:
                return null;
        }
    }
}

