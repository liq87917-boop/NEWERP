using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-301 Stage 2）的销售订单出货 / 财务进度数据集适配器：把既有
/// 「动态销售订单出货 / 财务进度报表」（dynamic:shipment-finance）的有限字段白名单（27 个字段）
/// 暴露为统一受控数据集。预览复用 ERP-032 的只读有界派生语义（订单行出货数量 + 收款链接状态 / 金额），
/// 金额按原币呈现、数量按基础单位、未知金额 / 未知数量照实保留 null，绝不跨币种合并或换算。
/// <para>行粒度为「一行一条已审核、未删除、授权范围内的销售订单」，绝不因出货明细 / 收款申请的一对多关系而
/// 重复累计订单头金额（订单金额只出现一次）。</para>
/// <para>每次目录 / 预览调用都重新校验既有「角色 → 菜单」销售订单（sales-order）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围（fail closed）。</para>
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

    private const string ShipmentNone = "none";
    private const string ShipmentPartial = "partial";
    private const string ShipmentComplete = "complete";
    private const string ShipmentOver = "over_shipped";
    private const string ShipmentUnknown = "unknown";

    private const string LinkLinked = "linked";
    private const string LinkPartial = "partial";
    private const string LinkUnlinked = "unlinked";
    private const string LinkUnknown = "unknown";

    private const string FilterNone = "none";
    private const string FilterShipped = "shipped";

    private const decimal AmountTolerance = 0.005m;
    private const int SingleOrderDocumentLimit = 200;
    private const int BatchDocumentCeiling = 2000;

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
        var query = BuildQuery(definition);

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var source = ApplyFilters(query, scope);
        var total = await source.CountAsync(cancellationToken);
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);

        var pageOrders = await source
            .OrderBy(o => o.CustomerId).ThenBy(o => (int)o.Currency).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        var rows = new List<Dictionary<string, object?>>();
        if (pageOrders.Count > 0)
        {
            var customerIds = pageOrders.Select(o => o.CustomerId).Where(c => c > 0).Distinct().ToList();
            var customerNames = customerIds.Count == 0
                ? new Dictionary<long, string>()
                : await _db.BaseCustomers.AsNoTracking()
                    .Where(c => customerIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.CustomerName })
                    .ToDictionaryAsync(c => c.Id, c => c.CustomerName, cancellationToken);

            var derived = await DeriveAsync(_db, pageOrders, cancellationToken);

            rows = pageOrders
                .Select(o => BuildRow(o, customerNames.TryGetValue(o.CustomerId, out var name) ? name : string.Empty, derived[o.Id], fieldKeys))
                .ToList();
        }

        var columns = fieldKeys.Select(BuildColumn).ToList();

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

    private static (int Page, int PageSize) ValidatePageBounds(ReportConfigurationPreviewParameters parameters)
    {
        if (parameters.Page < 1)
            throw BusinessException.InvalidParameter($"页码必须从 1 开始（收到 {parameters.Page}）");
        if (parameters.PageSize < 1 || parameters.PageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {parameters.PageSize}）");
        return (parameters.Page, parameters.PageSize);
    }

    private sealed record Query(
        long? CustomerId,
        Currency? Currency,
        DateTime? OrderDateFrom,
        DateTime? OrderDateTo,
        string? ShipmentStatus,
        string? FinanceLinkStatus);

    private static Query BuildQuery(ReportConfigurationDefinition definition)
    {
        DateTime? from = null;
        DateTime? to = null;
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
                    ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "orderDate", ref from, ref to);
                    break;
                case "customerid":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    customerId = ReportConfigurationDatasetTranslation.CoalesceLong(customerId, filter.Value, "customerId");
                    break;
                case "currency":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                    break;
                case "shipmentstatus":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    shipmentStatus = ReportConfigurationDatasetTranslation.CoalesceString(shipmentStatus, filter.Value, "shipmentStatus");
                    break;
                case "financelinkstatus":
                    ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                    financeLinkStatus = ReportConfigurationDatasetTranslation.CoalesceString(financeLinkStatus, filter.Value, "financeLinkStatus");
                    break;
                default:
                    throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetShipmentFinance} 不支持的筛选字段：{key}");
            }
        }

        var currencyValue = string.IsNullOrWhiteSpace(currency) ? (Currency?)null : NormalizeCurrency(currency!);
        var shipmentValue = string.IsNullOrWhiteSpace(shipmentStatus) ? null : NormalizeShipmentStatus(shipmentStatus!);
        var financeValue = string.IsNullOrWhiteSpace(financeLinkStatus) ? null : NormalizeFinanceLinkStatus(financeLinkStatus!);

        return new Query(customerId, currencyValue, from, to, shipmentValue, financeValue);
    }

    private static Currency? NormalizeCurrency(string currency)
    {
        if (!Enum.TryParse<Currency>(currency, true, out var parsed))
            throw BusinessException.InvalidParameter($"币种筛选取值非法：{currency}");
        return parsed;
    }

    private static string NormalizeShipmentStatus(string shipmentStatus)
        => shipmentStatus switch
        {
            FilterNone => FilterNone,
            FilterShipped => FilterShipped,
            _ => throw BusinessException.InvalidParameter($"出货状态筛选取值非法：{shipmentStatus}"),
        };

    private static string NormalizeFinanceLinkStatus(string financeLinkStatus)
        => financeLinkStatus switch
        {
            LinkLinked => LinkLinked,
            LinkPartial => LinkPartial,
            LinkUnlinked => LinkUnlinked,
            _ => throw BusinessException.InvalidParameter($"收款链接状态筛选取值非法：{financeLinkStatus}"),
        };

    private IQueryable<SalesOrder> ApplyFilters(Query query, SalespersonDataScope scope)
    {
        var source = _db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);

        source = SalespersonDataScopeService.FilterByCustomer(source, scope, o => o.CustomerId);

        if (query.CustomerId.HasValue) source = source.Where(o => o.CustomerId == query.CustomerId.Value);
        if (query.Currency.HasValue) source = source.Where(o => o.Currency == query.Currency.Value);
        if (query.OrderDateFrom.HasValue) source = source.Where(o => o.OrderDate >= query.OrderDateFrom.Value);
        if (query.OrderDateTo.HasValue) source = source.Where(o => o.OrderDate <= query.OrderDateTo.Value);

        if (query.ShipmentStatus == FilterNone)
        {
            source = source.Where(o => !_db.StockOuts.Any(s =>
                !s.IsDeleted && s.SalesOrderId == o.Id && s.Status == DocumentStatus.Approved));
        }
        else if (query.ShipmentStatus == FilterShipped)
        {
            source = source.Where(o => _db.StockOuts.Any(s =>
                !s.IsDeleted && s.SalesOrderId == o.Id && s.Status == DocumentStatus.Approved));
        }

        if (query.FinanceLinkStatus == LinkUnlinked)
        {
            source = source.Where(o =>
                !_db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                && !_db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id));
        }
        else if (query.FinanceLinkStatus == LinkLinked)
        {
            source = source.Where(o =>
                (_db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                    || _db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id))
                && !_db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                    && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))
                && !_db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                    && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency)));
        }
        else if (query.FinanceLinkStatus == LinkPartial)
        {
            source = source.Where(o =>
                (_db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                    || _db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id))
                && (_db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                        && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))
                    || _db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                        && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))));
        }

        return source;
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

    // ==================== ERP-032 派生（与 SalesOrderProgress 同源，只读有界） ====================

    private sealed record OrderDetailRow(long Id, long SalesOrderId, long ProductId, string ProductName, string Spec,
        string Unit, decimal Quantity);

    private sealed record ShipmentDocumentRow(long Id, long OrderId, string No, DateTime Date, DocumentStatus Status,
        decimal TotalQuantity);

    private sealed record ShipmentDetailRow(long StockOutId, long ProductId, string ProductName, decimal Quantity);

    private sealed record FinanceApplyRow(long OrderId, string Source, string No, DateTime Date, decimal Amount,
        string Currency, DocumentStatus Status, string ReferenceField)
    {
        public bool Counted => Status == DocumentStatus.Approved;
        public bool AwaitingAudit => Status is DocumentStatus.Pending or DocumentStatus.Submitted;
    }

    private sealed record CustomerLevelRow(long CustomerId, string Source, string DocumentNo, DateTime Date,
        decimal Amount, string Currency, string Status, string ReferenceField);

    private sealed class ShipmentResult
    {
        public decimal OrderedQuantity { get; init; }
        public decimal ShippedQuantity { get; init; }
        public decimal PendingQuantity { get; init; }
        public decimal OutstandingQuantity { get; init; }
        public string ShipmentStatus { get; init; } = ShipmentNone;
        public bool HasApprovedShipment { get; init; }
        public int ShipmentDocumentCount { get; init; }
        public int ApprovedShipmentCount { get; init; }
        public bool Truncated { get; init; }
    }

    private sealed class FinanceResult
    {
        public string LinkStatus { get; init; } = LinkUnlinked;
        public string LinkReason { get; init; } = string.Empty;
        public decimal? LinkedAmount { get; init; }
        public decimal? UncoveredAmount { get; init; }
        public decimal? SubmittedAmount { get; init; }
        public int OtherCurrencyRecordCount { get; init; }
        public int UnapprovedRecordCount { get; init; }
        public int UnattributedRecordCount { get; init; }
        public bool OverReceived { get; init; }
    }

    private sealed record DerivedOrder(long OrderId, ShipmentResult Shipment, FinanceResult Finance);

    private static int QueryLimitFor(int orderCount) => orderCount > 1 ? BatchDocumentCeiling : SingleOrderDocumentLimit;

    private static async Task<Dictionary<long, DerivedOrder>> DeriveAsync(
        IErpDbContext db, IReadOnlyList<SalesOrder> orders, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, DerivedOrder>();
        if (orders.Count == 0)
            return result;

        var orderIds = orders.Select(o => o.Id).ToList();
        var limit = QueryLimitFor(orders.Count);

        var detailRows = (await db.SalesOrderDetails.AsNoTracking()
                .Where(d => !d.IsDeleted && orderIds.Contains(d.SalesOrderId))
                .OrderBy(d => d.Id).Take(limit)
                .Select(d => new { d.Id, d.SalesOrderId, d.ProductId, d.ProductName, d.Spec, d.Unit, d.Quantity })
                .ToListAsync(cancellationToken))
            .Select(d => new OrderDetailRow(d.Id, d.SalesOrderId, d.ProductId, d.ProductName, d.Spec, d.Unit, d.Quantity))
            .ToList();
        var detailTruncated = detailRows.Count == limit;

        var stockOuts = (await db.StockOuts.AsNoTracking()
                .Where(s => !s.IsDeleted && s.SalesOrderId != null && orderIds.Contains(s.SalesOrderId.Value))
                .OrderBy(s => s.StockOutDate).ThenBy(s => s.Id).Take(limit)
                .Select(s => new { s.Id, s.SalesOrderId, s.StockOutNo, s.StockOutDate, s.Status, s.TotalQuantity })
                .ToListAsync(cancellationToken))
            .Select(s => new ShipmentDocumentRow(s.Id, s.SalesOrderId!.Value, s.StockOutNo, s.StockOutDate, s.Status,
                s.TotalQuantity))
            .ToList();
        var stockOutTruncated = stockOuts.Count == limit;

        var stockOutIds = stockOuts.Select(s => s.Id).ToList();
        var shipmentDetailRows = stockOutIds.Count == 0
            ? new List<ShipmentDetailRow>()
            : (await db.StockOutDetails.AsNoTracking()
                    .Where(d => !d.IsDeleted && stockOutIds.Contains(d.StockOutId))
                    .Take(limit)
                    .Select(d => new { d.StockOutId, d.ProductId, d.ProductName, d.Quantity })
                    .ToListAsync(cancellationToken))
                .Select(d => new ShipmentDetailRow(d.StockOutId, d.ProductId, d.ProductName, d.Quantity))
                .ToList();
        var shipmentDetailTruncated = shipmentDetailRows.Count == limit;

        var deposits = (await db.FinanceDepositApplies.AsNoTracking()
                .Where(a => !a.IsDeleted && a.SalesOrderId != null && orderIds.Contains(a.SalesOrderId.Value))
                .OrderBy(a => a.ApplyDate).ThenBy(a => a.Id).Take(limit)
                .Select(a => new { a.SalesOrderId, a.ApplyNo, a.ApplyDate, a.Amount, a.Currency, a.Status })
                .ToListAsync(cancellationToken))
            .Select(a => new FinanceApplyRow(a.SalesOrderId!.Value, "FinanceDepositApply", a.ApplyNo, a.ApplyDate,
                a.Amount, a.Currency.ToString(), a.Status, "FinanceDepositApply.SalesOrderId"))
            .ToList();
        var depositTruncated = deposits.Count == limit;

        var paymentApplies = (await db.FinancePaymentApplies.AsNoTracking()
                .Where(a => !a.IsDeleted && a.SalesOrderId != null && orderIds.Contains(a.SalesOrderId.Value))
                .OrderBy(a => a.ApplyDate).ThenBy(a => a.Id).Take(limit)
                .Select(a => new { a.SalesOrderId, a.ApplyNo, a.ApplyDate, a.Amount, a.Currency, a.Status })
                .ToListAsync(cancellationToken))
            .Select(a => new FinanceApplyRow(a.SalesOrderId!.Value, "FinancePaymentApply", a.ApplyNo, a.ApplyDate,
                a.Amount, a.Currency.ToString(), a.Status, "FinancePaymentApply.SalesOrderId"))
            .ToList();
        var paymentApplyTruncated = paymentApplies.Count == limit;

        var customerIds = orders.Select(o => o.CustomerId).Where(c => c > 0).Distinct().ToList();

        var receipts = customerIds.Count == 0
            ? new List<CustomerLevelRow>()
            : (await db.FinanceReceipts.AsNoTracking()
                    .Where(r => !r.IsDeleted && customerIds.Contains(r.CustomerId))
                    .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id).Take(limit)
                    .Select(r => new { r.CustomerId, r.ReceiptNo, r.ReceiptDate, r.Amount, r.Currency, r.Status })
                    .ToListAsync(cancellationToken))
                .Select(r => new CustomerLevelRow(r.CustomerId, "FinanceReceipt", r.ReceiptNo, r.ReceiptDate, r.Amount,
                    r.Currency.ToString(), r.Status.ToString(), "FinanceReceipt.CustomerId"))
                .ToList();
        var receiptTruncated = receipts.Count == limit;

        var containerSettlements = customerIds.Count == 0
            ? new List<CustomerLevelRow>()
            : (await db.FinanceContainerSettlements.AsNoTracking()
                    .Where(s => !s.IsDeleted && customerIds.Contains(s.CustomerId))
                    .OrderBy(s => s.SettlementDate).ThenBy(s => s.Id).Take(limit)
                    .Select(s => new { s.CustomerId, s.SettlementNo, s.SettlementDate, s.TotalAmount, s.Status })
                    .ToListAsync(cancellationToken))
                .Select(s => new CustomerLevelRow(s.CustomerId, "FinanceContainerSettlement", s.SettlementNo,
                    s.SettlementDate, s.TotalAmount, string.Empty, s.Status.ToString(), "FinanceContainerSettlement.CustomerId"))
                .ToList();
        var containerTruncated = containerSettlements.Count == limit;

        var bulkSettlements = customerIds.Count == 0
            ? new List<CustomerLevelRow>()
            : (await db.FinanceBulkSettlements.AsNoTracking()
                    .Where(s => !s.IsDeleted && customerIds.Contains(s.CustomerId))
                    .OrderBy(s => s.SettlementDate).ThenBy(s => s.Id).Take(limit)
                    .Select(s => new { s.CustomerId, s.SettlementNo, s.SettlementDate, s.TotalAmount, s.Status })
                    .ToListAsync(cancellationToken))
                .Select(s => new CustomerLevelRow(s.CustomerId, "FinanceBulkSettlement", s.SettlementNo,
                    s.SettlementDate, s.TotalAmount, string.Empty, s.Status.ToString(), "FinanceBulkSettlement.CustomerId"))
                .ToList();
        var bulkTruncated = bulkSettlements.Count == limit;

        foreach (var order in orders.OrderBy(o => o.Id))
        {
            var ownDetails = detailRows.Where(d => d.SalesOrderId == order.Id).ToList();
            var ownStockOuts = stockOuts.Where(s => s.OrderId == order.Id).ToList();
            var ownStockOutIds = ownStockOuts.Select(s => s.Id).ToHashSet();
            var ownShipmentDetails = shipmentDetailRows.Where(d => ownStockOutIds.Contains(d.StockOutId)).ToList();
            var shipmentTruncated = detailTruncated || stockOutTruncated || shipmentDetailTruncated;

            var shipment = BuildShipmentResult(ownDetails, ownStockOuts, ownShipmentDetails, shipmentTruncated);

            var ownApplies = deposits.Where(a => a.OrderId == order.Id)
                .Concat(paymentApplies.Where(a => a.OrderId == order.Id))
                .OrderBy(a => a.Date).ThenBy(a => a.No, StringComparer.Ordinal)
                .ToList();
            var customerLevel = receipts.Where(r => r.CustomerId == order.CustomerId)
                .Concat(containerSettlements.Where(s => s.CustomerId == order.CustomerId))
                .Concat(bulkSettlements.Where(s => s.CustomerId == order.CustomerId))
                .OrderBy(r => r.Date).ThenBy(r => r.Source, StringComparer.Ordinal)
                .ToList();

            var finance = BuildFinanceResult(order, ownApplies, customerLevel,
                depositTruncated || paymentApplyTruncated,
                receiptTruncated || containerTruncated || bulkTruncated);

            result[order.Id] = new DerivedOrder(order.Id, shipment, finance);
        }

        return result;
    }

    private static ShipmentResult BuildShipmentResult(
        List<OrderDetailRow> details, List<ShipmentDocumentRow> stockOuts,
        List<ShipmentDetailRow> shipmentDetails, bool truncated)
    {
        var statusById = stockOuts.ToDictionary(s => s.Id, s => s.Status);
        bool Counted(long stockOutId) => statusById.TryGetValue(stockOutId, out var status)
            && status == DocumentStatus.Approved;
        bool AwaitingAudit(long stockOutId) => statusById.TryGetValue(stockOutId, out var status)
            && status is DocumentStatus.Pending or DocumentStatus.Submitted;

        var shippedPool = shipmentDetails.Where(d => Counted(d.StockOutId))
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var pendingPool = shipmentDetails.Where(d => AwaitingAudit(d.StockOutId))
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var orderedDetails = details.OrderBy(d => d.Id).ToList();
        var lastDetailIdByProduct = orderedDetails.GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Max(d => d.Id));

        decimal orderedQuantity = 0m;
        decimal outstandingQuantity = 0m;
        foreach (var detail in orderedDetails)
        {
            orderedQuantity += detail.Quantity;

            var availableShipped = Take(shippedPool, detail.ProductId);
            var isLastLineOfProduct = detail.Id == lastDetailIdByProduct[detail.ProductId];
            var shipped = isLastLineOfProduct ? availableShipped : Math.Min(availableShipped, detail.Quantity);
            PutBack(shippedPool, detail.ProductId, availableShipped - shipped);

            var availablePending = Take(pendingPool, detail.ProductId);
            var pending = Math.Min(availablePending, Math.Max(0m, detail.Quantity - shipped));
            PutBack(pendingPool, detail.ProductId, availablePending - pending);

            outstandingQuantity += Math.Max(0m, detail.Quantity - shipped);
        }

        var shippedQuantity = shipmentDetails.Where(d => Counted(d.StockOutId)).Sum(d => d.Quantity);
        var pendingQuantity = shipmentDetails.Where(d => AwaitingAudit(d.StockOutId)).Sum(d => d.Quantity);

        return new ShipmentResult
        {
            OrderedQuantity = orderedQuantity,
            ShippedQuantity = shippedQuantity,
            PendingQuantity = pendingQuantity,
            OutstandingQuantity = outstandingQuantity,
            ShipmentStatus = truncated ? ShipmentUnknown : OrderShipmentStatusOf(orderedQuantity, shippedQuantity, outstandingQuantity),
            HasApprovedShipment = stockOuts.Any(s => s.Status == DocumentStatus.Approved),
            ShipmentDocumentCount = stockOuts.Count,
            ApprovedShipmentCount = stockOuts.Count(s => s.Status == DocumentStatus.Approved),
            Truncated = truncated,
        };
    }

    private static string OrderShipmentStatusOf(decimal ordered, decimal shipped, decimal outstanding)
    {
        if (shipped <= 0) return ShipmentNone;
        if (ordered <= 0 || shipped > ordered) return ShipmentOver;
        return outstanding > 0 ? ShipmentPartial : ShipmentComplete;
    }

    private static decimal Take(Dictionary<long, decimal> pool, long productId)
        => pool.TryGetValue(productId, out var value) ? value : 0m;

    private static void PutBack(Dictionary<long, decimal> pool, long productId, decimal remainder)
    {
        if (remainder > 0) pool[productId] = remainder;
        else pool.Remove(productId);
    }

    private static FinanceResult BuildFinanceResult(
        SalesOrder order, List<FinanceApplyRow> own, List<CustomerLevelRow> customerLevel,
        bool authoritativeTruncated, bool customerLevelTruncated)
    {
        var orderCurrency = order.Currency.ToString();
        var counted = own.Where(r => r.Counted && SameCurrency(r.Currency, orderCurrency)).ToList();
        var awaiting = own.Where(r => !r.Counted && r.AwaitingAudit && SameCurrency(r.Currency, orderCurrency)).ToList();
        var otherCurrency = own.Where(r => !SameCurrency(r.Currency, orderCurrency)).ToList();
        var unapproved = own
            .Where(r => SameCurrency(r.Currency, orderCurrency) && !r.Counted && !r.AwaitingAudit).ToList();

        decimal? linked = null;
        decimal? submitted = null;
        decimal? uncovered = null;
        string status;
        string reason;
        if (authoritativeTruncated)
        {
            status = LinkUnknown;
            reason = "指向本单的定金/货款申请单超过单次派生上限：无法确认收款引用是否完整，已关联金额与未覆盖金额记为未知（null，绝不静默给出不完整金额）。";
        }
        else if (own.Count == 0)
        {
            status = LinkUnlinked;
            reason = "没有任何定金申请单/货款申请单以 SalesOrderId 指向本单：不存在可用权威引用，已关联金额与未覆盖金额为未知（null），不用 0 顶替。";
        }
        else if (otherCurrency.Count == 0 && unapproved.Count == 0 && awaiting.Count == 0)
        {
            status = LinkLinked;
            linked = counted.Sum(r => r.Amount);
            submitted = 0m;
            uncovered = order.TotalAmount - linked.Value;
            reason = $"指向本单的 {own.Count} 条收款申请（定金/货款申请单）全部为「已审核 + 币种与本单一致（{orderCurrency}）」，已关联金额按同一套权威引用规则完整计入。";
        }
        else
        {
            status = LinkPartial;
            linked = counted.Sum(r => r.Amount);
            submitted = awaiting.Sum(r => r.Amount);
            uncovered = order.TotalAmount - linked.Value;
            var parts = new List<string>();
            if (awaiting.Count > 0) parts.Add($"{awaiting.Count} 条已提交/待提交（未审核，已单列）");
            if (otherCurrency.Count > 0) parts.Add($"{otherCurrency.Count} 条他币种（不汇总、不做汇率换算）");
            if (unapproved.Count > 0) parts.Add($"{unapproved.Count} 条非「已审核」状态（已驳回/已取消/已完成）");
            reason = $"部分可归属：指向本单的收款申请中有 {string.Join("、", parts)}，不计入已关联金额；"
                + (counted.Count == 0 ? "本单当前没有「已审核 + 币种一致」的收款申请，因此已关联金额为 0（有依据的 0，不是未知）。" : $"已关联金额 = 其中「已审核 + 币种一致」的 {counted.Count} 条合计；")
                + "未覆盖金额 = 订单金额 − 已关联金额，不等于未收款金额。";
        }

        if (customerLevel.Count > 0)
        {
            reason += $" 另有 {customerLevel.Count} 条客户级记录（收款单/装柜结算单/散货结算单只记录客户，没有订单级引用）无法按既有引用归属到本单：仅列出、不计入、不汇总。";
        }
        if (customerLevelTruncated)
        {
            reason += " 客户级记录超过单次查询上限：上述客户级计数不完整（不静默截断）。";
        }

        return new FinanceResult
        {
            LinkStatus = status,
            LinkReason = reason,
            LinkedAmount = linked,
            UncoveredAmount = uncovered,
            SubmittedAmount = submitted,
            OtherCurrencyRecordCount = otherCurrency.Count,
            UnapprovedRecordCount = unapproved.Count,
            UnattributedRecordCount = customerLevel.Count,
            OverReceived = linked.HasValue && linked.Value - order.TotalAmount > AmountTolerance,
        };
    }

    private static bool SameCurrency(string left, string right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> BuildRow(
        SalesOrder order, string customerName, DerivedOrder derived, IReadOnlyList<string> fieldKeys)
    {
        var shipment = derived.Shipment;
        var finance = derived.Finance;
        var shipmentKnown = !shipment.Truncated;

        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["orderId"] = order.Id,
            ["orderNo"] = order.OrderNo,
            ["orderDate"] = order.OrderDate,
            ["status"] = order.Status.ToString(),
            ["customerId"] = order.CustomerId,
            ["customerName"] = customerName,
            ["currency"] = order.Currency.ToString(),
            ["orderAmount"] = order.TotalAmount,
            ["recordedDepositAmount"] = order.DepositAmount,
            ["orderedQuantity"] = shipmentKnown ? shipment.OrderedQuantity : null,
            ["shippedQuantity"] = shipmentKnown ? shipment.ShippedQuantity : null,
            ["pendingShipmentQuantity"] = shipmentKnown ? shipment.PendingQuantity : null,
            ["outstandingQuantity"] = shipmentKnown ? shipment.OutstandingQuantity : null,
            ["shipmentStatus"] = shipmentKnown ? shipment.ShipmentStatus : ShipmentUnknown,
            ["hasApprovedShipment"] = shipment.HasApprovedShipment,
            ["shipmentDocumentCount"] = shipment.ShipmentDocumentCount,
            ["approvedShipmentCount"] = shipment.ApprovedShipmentCount,
            ["financeLinkStatus"] = finance.LinkStatus,
            ["financeLinkReason"] = finance.LinkReason,
            ["linkedAmount"] = finance.LinkedAmount,
            ["uncoveredAmount"] = finance.UncoveredAmount,
            ["submittedAmount"] = finance.SubmittedAmount,
            ["otherCurrencyRecordCount"] = finance.OtherCurrencyRecordCount,
            ["unapprovedRecordCount"] = finance.UnapprovedRecordCount,
            ["unattributedRecordCount"] = finance.UnattributedRecordCount,
            ["overReceived"] = finance.OverReceived,
            ["note"] = BuildNote(shipment, finance),
        };

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
        {
            row[key] = source.TryGetValue(key, out var value) ? value : null;
        }

        return row;
    }

    private static string BuildNote(ShipmentResult shipment, FinanceResult finance)
    {
        var notes = new List<string>();
        if (shipment.Truncated)
            notes.Add("出货数量未知：以本单为来源的出库单据超过单次派生上限，数量不完整（不静默截断，也不当作 0）。");
        else if (shipment.ShipmentStatus == ShipmentNone && !shipment.HasApprovedShipment)
            notes.Add("未出货（0 有依据）：没有以本单为来源且已审核的销售出库单。");
        else if (shipment.ShipmentStatus == ShipmentPartial)
            notes.Add($"部分出货：未出货 {shipment.OutstandingQuantity}，未出货数量只表示订单未覆盖部分。");

        switch (finance.LinkStatus)
        {
            case LinkUnlinked:
                notes.Add("收款未链接：没有任何定金/货款申请单指向本单，已关联金额未知（null，不是 0），不得当作已收款确认。");
                break;
            case LinkUnknown:
                notes.Add("收款链接未知：指向本单的收款申请超过单次派生上限，金额不完整（不静默截断）。");
                break;
            case LinkPartial:
                notes.Add("收款部分可归属：他币种/未审核/非已审核记录仅列出（不汇总、不换算），未覆盖金额只表示订单未覆盖部分，不是未收款金额。");
                break;
            default:
                if (finance.OverReceived) notes.Add("已关联金额超过订单金额（超收），请人工审核。");
                break;
        }

        return string.Join("；", notes);
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
