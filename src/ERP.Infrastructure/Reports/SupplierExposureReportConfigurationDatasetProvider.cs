using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的数据集适配器：把动态供应商采购敞口报表（ERP-148）的有限字段白名单
/// 与能力 / 粒度 / 币种口径暴露为统一目录；预览复用既有「角色 → 菜单」采购订单模块授权与 ERP-031 供应商采购敞口
/// 的只读有界派生（供应商 / 币种 / 订单日期 / 链接状态 / 分页）。
/// <para>金额一律按订单原币分别成行，绝不跨币种换算或合并；链接状态（linked / ambiguous / unavailable）按持久化
/// 归属销售订单唯一性判定；已结算 / 未结算 / 已提交付款金额与收货数量需 ERP-026 权威结算 / 收货派生，本适配器
/// 按「未知」（null）保留、绝不回填为 0。每次目录 / 预览调用都重新校验菜单授权（fail closed）。</para>
/// </summary>
public sealed class SupplierExposureReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public SupplierExposureReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetSupplierExposure;

    private const string Label = "供应商采购敞口";
    private const string Grain = "供应商采购订单敞口证据（一行一条采购订单）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；不跨币种换算或合并";
    private const string RequiredMenuCode = DynamicSupplierExposureReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicSupplierExposureReportRules.RequiredMenuText;
    private const int DefaultPageSize = DynamicSupplierExposureReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicSupplierExposureReportRules.MaxPageSize;
    private const string ReadOnlyText = DynamicSupplierExposureReportRules.ReadOnlyText;
    private const string BoundaryText = DynamicSupplierExposureReportRules.BoundaryText;

    private static readonly IReadOnlyList<string> GroupingKeys = new[]
    {
        ReportConfigurationConstants.GroupNone,
    };

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

    private static readonly Dictionary<string, string> CurrencyUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["currency"] = "币种代码",
        ["orderedAmount"] = "原币金额",
        ["settledAmount"] = "原币金额",
        ["outstandingAmount"] = "原币金额",
        ["submittedAmount"] = "原币金额",
        ["orderedQuantity"] = "基础单位",
        ["receivedQuantity"] = "基础单位",
        ["outstandingQuantity"] = "基础单位",
        ["pendingQuantity"] = "基础单位",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "orderId", "supplierId",
    };

    private const string SortingExplanation =
        "本数据集不支持任意排序：稳定分页按供应商 → 币种 → 订单日期 → 订单 Id 升序";

    private const string LinkLinked = "linked";
    private const string LinkAmbiguous = "ambiguous";
    private const string LinkUnavailable = "unavailable";

    private static (bool Sortable, string? Reason) SortabilityOf(string key)
        => (false, "本数据集不支持任意排序：稳定分页按供应商 → 币种 → 订单日期 → 订单 Id 升序");

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

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
            // 无采购订单菜单授权 → 该数据集不暴露
            return null;
        }

        return BuildDataset(DynamicSupplierExposureReportRules.GetCatalogDto());
    }

    /// <summary>身份 + 既有「角色 → 菜单」采购订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览供应商采购敞口报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSupplierExposureReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSupplierExposureReportRules.RequiredMenuText}」"
                + $"（{DynamicSupplierExposureReportRules.RequiredMenuCode}）模块授权：拒绝预览供应商采购敞口报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
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

        // 每次请求重新校验身份 / 菜单授权（fail closed，先于任何读取）
        await EnsureAuthorizedAsync(userId, cancellationToken);

        var fieldKeys = DynamicSupplierExposureReportRules.NormalizeFields(definition.Fields);

        var request = new DynamicSupplierExposureReportRequest
        {
            Page = parameters.Page,
            PageSize = parameters.PageSize,
        };
        MapFilters(definition.Filters, request);

        request.SupplierId = DynamicSupplierExposureReportRules.NormalizeSupplierId(request.SupplierId);
        request.Currency = DynamicSupplierExposureReportRules.NormalizeCurrency(request.Currency);
        request.LinkStatus = DynamicSupplierExposureReportRules.NormalizeLinkStatus(request.LinkStatus);
        DynamicSupplierExposureReportRules.ValidateDateRange(request.OrderDateFrom, request.OrderDateTo);
        DynamicSupplierExposureReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;

        // 复用 ERP-031 供应商采购敞口的只读有界派生（筛选 → 分页 → 本页订单）。
        var (rows, total) = await QueryPageAsync(request, page, cancellationToken);

        var columns = fieldKeys
            .Select(k => DynamicSupplierExposureReportRules.GetField(k)!)
            .Select(f => new ReportConfigurationColumnDto(f.Key, f.Label, f.DataType, CurrencyUnitOf(f.Key)))
            .ToList();
        var projectedRows = rows
            .Select(r => DynamicSupplierExposureReportRules.BuildRow(r, fieldKeys))
            .ToList();

        return new ReportConfigurationPreviewDto
        {
            DatasetKey = DatasetKey,
            Columns = columns,
            Rows = projectedRows,
            Total = total,
            MatchedCount = total,
            Page = page,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)request.PageSize),
            GroupBy = DynamicSupplierExposureReportRules.GroupNone,
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                ReadOnlyText, BoundaryText, DynamicSupplierExposureReportRules.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static void MapFilters(
        IReadOnlyList<ReportConfigurationFilter>? filters, DynamicSupplierExposureReportRequest request)
    {
        DateTime? orderStart = null;
        DateTime? orderEnd = null;
        long? supplierId = null;
        string? currency = null;
        string? linkStatus = null;

        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (filter is null)
                    continue;

                var key = (filter.FieldKey ?? string.Empty).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "orderdate":
                        ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "orderDate", ref orderStart, ref orderEnd);
                        break;
                    case "supplierid":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        supplierId = ReportConfigurationDatasetTranslation.CoalesceLong(supplierId, filter.Value, "supplierId");
                        break;
                    case "currency":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                        break;
                    case "linkstatus":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        linkStatus = ReportConfigurationDatasetTranslation.CoalesceString(linkStatus, filter.Value, "linkStatus");
                        break;
                    default:
                        throw BusinessException.InvalidParameter(
                            $"数据集 {ReportConfigurationConstants.DatasetSupplierExposure} 不支持的筛选字段: {key}");
                }
            }
        }

        request.OrderDateFrom = orderStart;
        request.OrderDateTo = orderEnd;
        request.SupplierId = supplierId;
        request.Currency = currency;
        request.LinkStatus = linkStatus;
    }


    private async Task<(List<Dictionary<string, object?>> Rows, int Total)> QueryPageAsync(
        DynamicSupplierExposureReportRequest request, int page, CancellationToken cancellationToken)
    {
        var source = _db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);

        if (request.SupplierId is { } supplierId) source = source.Where(o => o.SupplierId == supplierId);

        if (request.Currency is { } currencyText && Enum.TryParse<Currency>(currencyText, true, out var currency))
            source = source.Where(o => o.Currency == currency);

        if (request.OrderDateFrom is { } from) source = source.Where(o => o.OrderDate >= from);
        if (request.OrderDateTo is { } to)
        {
            var toExclusive = to.AddDays(1);
            source = source.Where(o => o.OrderDate < toExclusive);
        }

        if (request.LinkStatus == LinkUnavailable)
        {
            source = source.Where(o => o.OwningSalesOrderId == null || o.OwningSalesOrderId <= 0 || o.SupplierId <= 0);
        }
        else if (request.LinkStatus == LinkAmbiguous)
        {
            source = source.Where(o => o.OwningSalesOrderId != null && o.OwningSalesOrderId > 0 && o.SupplierId > 0
                && _db.PurchaseOrders.Any(x => !x.IsDeleted && x.Id != o.Id && x.OwningSalesOrderId == o.OwningSalesOrderId));
        }
        else if (request.LinkStatus == LinkLinked)
        {
            source = source.Where(o => o.OwningSalesOrderId != null && o.OwningSalesOrderId > 0 && o.SupplierId > 0
                && !_db.PurchaseOrders.Any(x => !x.IsDeleted && x.Id != o.Id && x.OwningSalesOrderId == o.OwningSalesOrderId));
        }

        var total = await source.CountAsync(cancellationToken);
        var pageIds = await source
            .OrderBy(o => o.SupplierId).ThenBy(o => o.Currency).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var orders = new List<PurchaseOrder>();
        if (pageIds.Count > 0)
        {
            var loaded = await _db.PurchaseOrders.AsNoTracking()
                .Where(o => pageIds.Contains(o.Id))
                .ToListAsync(cancellationToken);
            var byId = loaded.ToDictionary(o => o.Id);
            orders = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var rows = await BuildRowsAsync(orders, cancellationToken);
        return (rows, total);
    }


    private async Task<List<Dictionary<string, object?>>> BuildRowsAsync(
        IReadOnlyList<PurchaseOrder> orders, CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
            return new List<Dictionary<string, object?>>();

        var supplierIds = orders.Select(o => o.SupplierId).Distinct().ToList();
        var supplierNames = (await _db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id) && !s.IsDeleted)
                .Select(s => new { s.Id, s.SupplierName })
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.Id, s => s.SupplierName);

        var eligibleOwningIds = orders
            .Where(o => o.OwningSalesOrderId is > 0 && o.SupplierId > 0)
            .Select(o => o.OwningSalesOrderId!.Value)
            .Distinct()
            .ToList();
        var siblingCounts = new Dictionary<long, int>();
        if (eligibleOwningIds.Count > 0)
        {
            var siblingRows = await _db.PurchaseOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && o.OwningSalesOrderId != null && eligibleOwningIds.Contains(o.OwningSalesOrderId.Value))
                .GroupBy(o => o.OwningSalesOrderId!.Value)
                .Select(g => new { SalesOrderId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            siblingCounts = siblingRows.ToDictionary(r => r.SalesOrderId, r => r.Count);
        }

        var rows = new List<Dictionary<string, object?>>(orders.Count);
        foreach (var order in orders)
        {
            var owningId = order.OwningSalesOrderId ?? 0;
            siblingCounts.TryGetValue(owningId, out var siblingCount);
            var (linkStatus, linkReason) = ComputeLinkStatus(order, siblingCount);

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["orderId"] = order.Id,
                ["orderNo"] = order.OrderNo,
                ["orderDate"] = order.OrderDate,
                ["status"] = order.Status.ToString(),
                ["supplierId"] = order.SupplierId,
                ["supplierName"] = supplierNames.TryGetValue(order.SupplierId, out var name) ? name : string.Empty,
                ["currency"] = order.Currency.ToString(),
                ["owningSalesOrderNo"] = order.OwningSalesOrderNo,
                ["recordedSettlementProgress"] = order.SettlementProgress,
                ["orderedAmount"] = order.TotalAmount,
                ["linkStatus"] = linkStatus,
                ["linkReason"] = linkReason,
                ["settledAmount"] = null,
                ["outstandingAmount"] = null,
                ["submittedAmount"] = null,
                ["overSettled"] = false,
                ["receiptStatus"] = null,
                ["orderedQuantity"] = null,
                ["receivedQuantity"] = null,
                ["outstandingQuantity"] = null,
                ["pendingQuantity"] = null,
                ["note"] = BuildOrderNote(linkStatus),
            });
        }

        return rows;
    }

    private static (string LinkStatus, string LinkReason) ComputeLinkStatus(PurchaseOrder order, int siblingCount)
    {
        var owningId = order.OwningSalesOrderId ?? 0;
        if (owningId <= 0)
        {
            return (LinkUnavailable,
                "本单未关联归属销售订单：付款单只记录供应商，货款申请单只引用销售订单，两者都不存在指向本单的既有引用，结算金额未知（不做推断）。");
        }

        if (order.SupplierId <= 0)
        {
            return (LinkUnavailable,
                "本单未维护供应商：付款单无法按供应商归属到本单，结算金额未知（不做推断）。");
        }

        if (siblingCount > 1)
        {
            return (LinkAmbiguous,
                $"归属销售订单下存在 {siblingCount} 张采购订单：付款单只能引用到销售订单，无法在既有引用下唯一归属到本单，结算金额未知（不做推断）。");
        }

        return (LinkLinked,
            "归属销售订单唯一：本单按既有引用可唯一归属；已结算 / 未结算金额与收货数量需 ERP-026 权威结算 / 收货派生，"
            + "本适配器按「未知」（null）保留、绝不回填为 0，也不作为应付余额。");
    }

    private static string BuildOrderNote(string linkStatus) => linkStatus switch
    {
        LinkLinked =>
            "已按权威引用归属（付款单 → 货款申请单 → 本单归属销售订单）；已结算 / 未结算金额与收货数量未知（不做推断，不跨币种合并）。",
        LinkAmbiguous =>
            "结算链接不唯一（归属销售订单下存在多张采购订单）：本单金额只作「未链接敞口」单列，结算与未结算金额均为未知（不用 0 顶替），不作为应付余额。",
        _ =>
            "无可用结算链接（未关联归属销售订单或未维护供应商）：本单金额只作「未链接敞口」单列，结算金额未知（不做推断），不作为应付余额。",
    };


    private static ReportConfigurationDatasetDto BuildDataset(DynamicSupplierExposureReportCatalogDto catalog)
    {
        var fields = catalog.Fields
            .Select(f => BuildField(f.Key, f.Label, f.DataType, f.Filterable))
            .ToList();

        return new ReportConfigurationDatasetDto(
            ReportConfigurationConstants.DatasetSupplierExposure,
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
            SortingExplanation = SortingExplanation,
        };
    }

    private static ReportConfigurationFieldDto BuildField(
        string key, string label, string dataType, bool filterable)
    {
        CurrencyUnits.TryGetValue(key, out var currencyUnit);
        var aggregatable = string.Equals(dataType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase)
            && !NonAggregatable.Contains(key);
        var sortability = SortabilityOf(key);

        return new ReportConfigurationFieldDto(
            key,
            label,
            dataType,
            currencyUnit,
            filterable,
            aggregatable,
            Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(dataType))
        {
            Sortable = sortability.Sortable,
            SortUnavailableReason = sortability.Reason,
        };
    }
}

