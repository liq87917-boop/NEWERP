using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的数据集适配器：把动态供应商对账与账龄报表（ERP-140）的有限字段白名单
/// 与能力 / 粒度 / 币种口径暴露为统一目录；预览复用既有「角色 → 菜单」采购订单模块授权与 ERP-068 供应商对账与账龄
/// 工作台的只读有界派生（供应商 / 币种 / 发票状态 / 日期 / 分页 + ERP-066 持久化付款引用行分桶）。
/// <para>金额一律按发票原币分别成行，绝不跨币种换算或合并；未知到期日不计算账龄、单独标注；草稿 / 已作废与
/// 无效 / 无法确认证据保持可见、绝不并入有效合计。每次目录 / 预览调用都重新校验菜单授权（fail closed）。</para>
/// </summary>
public sealed class SupplierAgingReportConfigurationDatasetProvider : IReportConfigurationDatasetProvider
{
    private readonly IErpDbContext _db;

    public SupplierAgingReportConfigurationDatasetProvider(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public string DatasetKey => ReportConfigurationConstants.DatasetSupplierAging;

    private const string Label = "供应商对账与账龄";
    private const string Grain = "供应商采购发票证据（一行一条发票证据）";
    private const string CurrencyUnitSemantics = "金额按原币呈现；账龄按自然日；不跨币种换算或合并";
    private const string RequiredMenuCode = DynamicSupplierAgingReportRules.RequiredMenuCode;
    private const string RequiredMenuText = DynamicSupplierAgingReportRules.RequiredMenuText;
    private const int DefaultPageSize = DynamicSupplierAgingReportRules.DefaultPageSize;
    private const int MaxPageSize = DynamicSupplierAgingReportRules.MaxPageSize;
    private const string ReadOnlyText = DynamicSupplierAgingReportRules.ReadOnlyText;
    private const string BoundaryText = DynamicSupplierAgingReportRules.BoundaryText;

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
        ["netAmount"] = "原币金额",
        ["taxAmount"] = "原币金额",
        ["grossAmount"] = "原币金额",
        ["activeAllocatedAmount"] = "原币金额",
        ["remainingAmount"] = "原币金额",
        ["voidedAllocationAmount"] = "原币金额",
        ["invoiceInactiveAllocationAmount"] = "原币金额",
        ["invalidAllocationAmount"] = "原币金额",
        ["unavailableAllocationAmount"] = "原币金额",
    };

    private static readonly HashSet<string> NonAggregatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoiceId", "supplierId", "amountDecimals", "invoiceStatus",
    };

    private const string SortingExplanation =
        "本数据集不支持任意排序：稳定分页按供应商 → 币种 → 到期日（未知在前）→ 开票日期（倒序）→ 发票 Id（倒序）";

    // ==================== ERP-068 口径常量（与 SupplierReconciliationAgingSemantics 同源） ====================

    private const string BucketNotDue = "not_due";
    private const string BucketOverdue1To30 = "overdue_1_30";
    private const string BucketOverdue31To60 = "overdue_31_60";
    private const string BucketOverdue61To90 = "overdue_61_90";
    private const string BucketOverdueOver90 = "overdue_over_90";
    private const string UnknownDueDateBucket = "unknown_due_date";

    private const string AllocationNone = "none";
    private const string AllocationHistoricalOnly = "historical_only";
    private const string AllocationPartial = "partial";
    private const string AllocationFull = "full";
    private const string AllocationOverAllocated = "over_allocated";
    private const string AllocationUnknown = "unknown";

    private const string RemainingKnown = "known";
    private const string RemainingUnknown = "unknown";
    private const string RemainingOverAllocated = "over_allocated";

    private static (bool Sortable, string? Reason) SortabilityOf(string key)
        => (false, "本数据集不支持任意排序：稳定分页按供应商 → 币种 → 到期日 → 开票日期（倒序）→ 发票 Id（倒序）");

    private static string? CurrencyUnitOf(string key)
        => CurrencyUnits.TryGetValue(key, out var unit) ? unit : null;

    private static bool IsActiveEvidence(int invoiceStatus)
        => invoiceStatus == PurchaseInvoiceRules.StatusRecorded;

    private static string? AgingBucketOf(DateTime? dueDate, DateTime asOfDate, out int? overdueDays)
    {
        if (dueDate is not { } due)
        {
            overdueDays = null;
            return null;
        }

        var days = (asOfDate.Date - due.Date).Days;
        overdueDays = days;
        if (days <= 0) return BucketNotDue;
        if (days <= 30) return BucketOverdue1To30;
        if (days <= 60) return BucketOverdue31To60;
        if (days <= 90) return BucketOverdue61To90;
        return BucketOverdueOver90;
    }

    private static string BucketText(string bucket) => bucket switch
    {
        BucketNotDue => "未到期（as-of ≤ 显式到期日）",
        BucketOverdue1To30 => "逾期 1 ~ 30 天",
        BucketOverdue31To60 => "逾期 31 ~ 60 天",
        BucketOverdue61To90 => "逾期 61 ~ 90 天",
        BucketOverdueOver90 => "逾期 90 天以上",
        UnknownDueDateBucket => "未知到期日（无显式到期日：不计算账龄，单独成组）",
        _ => "未知分桶",
    };

    private static string AgingText(string? bucket, int? overdueDays) => bucket switch
    {
        null => "未知到期日（无显式到期日：不计算账龄）",
        BucketNotDue => $"未到期（到期日尚有 {-overdueDays.GetValueOrDefault()} 天）",
        _ => $"{BucketText(bucket)}（逾期 {overdueDays.GetValueOrDefault()} 天）",
    };

    private static string AllocationStateText(string state) => state switch
    {
        AllocationNone => "无持久化付款引用行（证据缺口，不代表未付款 / 已付款 / 逾期）",
        AllocationHistoricalOnly => "仅有历史 / 无效引用行（已作废 / 发票失效 / 无效 / 无法确认：单独可见，绝不并入有效合计）",
        AllocationPartial => "部分分配（有效已分配金额小于含税总额，未分配金额单独可见）",
        AllocationFull => "整笔分配（有效已分配金额已覆盖含税总额）",
        AllocationOverAllocated => "无效证据：有效已分配金额超过含税总额（与源规则矛盾，按「未知」显示，绝不轧为 0 或负数）",
        _ => "未知（命中系统有界读取上限：分配证据无法穷尽，不给部分合计）",
    };

    private static string RemainingStateText(string state) => state switch
    {
        RemainingKnown => "可确认（含税总额 − 有效已分配金额）",
        RemainingOverAllocated => "无效证据：有效已分配金额超过含税总额（绝不轧为 0，也不视为已结清）",
        _ => "未知（命中系统有界读取上限：不给部分合计）",
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
            // 无采购订单菜单授权 → 该数据集不暴露
            return null;
        }

        return BuildDataset(DynamicSupplierAgingReportRules.GetCatalogDto());
    }

    /// <summary>身份 + 既有「角色 → 菜单」采购订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览供应商对账与账龄报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSupplierAgingReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSupplierAgingReportRules.RequiredMenuText}」"
                + $"（{DynamicSupplierAgingReportRules.RequiredMenuCode}）模块授权：拒绝预览供应商对账与账龄报表"
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

        var fieldKeys = DynamicSupplierAgingReportRules.NormalizeFields(definition.Fields);

        var request = new DynamicSupplierAgingReportRequest
        {
            Page = parameters.Page,
            PageSize = parameters.PageSize,
        };
        MapFilters(definition.Filters, request);

        request.SupplierId = DynamicSupplierAgingReportRules.NormalizeSupplierId(request.SupplierId);
        request.Currency = DynamicSupplierAgingReportRules.NormalizeCurrency(request.Currency);
        request.InvoiceStatus = DynamicSupplierAgingReportRules.NormalizeInvoiceStatus(request.InvoiceStatus);
        DynamicSupplierAgingReportRules.ValidateDateRange(request.InvoiceDateFrom, request.InvoiceDateTo, "开票日期");
        DynamicSupplierAgingReportRules.ValidateDateRange(request.DueDateFrom, request.DueDateTo, "到期日");
        DynamicSupplierAgingReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;

        var asOfDate = request.AsOfDate ?? DateTime.Today;

        // 复用 ERP-068 供应商对账与账龄的只读有界派生（筛选 → 分页 → 本页发票 → 分配证据分桶）。
        var (rows, total) = await QueryPageAsync(request, page, asOfDate, cancellationToken);

        var columns = fieldKeys
            .Select(k => DynamicSupplierAgingReportRules.GetField(k)!)
            .Select(f => new ReportConfigurationColumnDto(f.Key, f.Label, f.DataType, CurrencyUnitOf(f.Key)))
            .ToList();
        var projectedRows = rows
            .Select(r => DynamicSupplierAgingReportRules.BuildRow(r, fieldKeys))
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
            GroupBy = DynamicSupplierAgingReportRules.GroupNone,
            Groups = null,
            Evidence = new ReportConfigurationEvidenceContextDto(
                DatasetKey, Grain, CurrencyUnitSemantics,
                ReadOnlyText, BoundaryText, DynamicSupplierAgingReportRules.DisclaimerText,
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private static void MapFilters(
        IReadOnlyList<ReportConfigurationFilter>? filters, DynamicSupplierAgingReportRequest request)
    {
        DateTime? invoiceStart = null;
        DateTime? invoiceEnd = null;
        DateTime? dueStart = null;
        DateTime? dueEnd = null;
        long? supplierId = null;
        string? currency = null;
        string? invoiceStatus = null;

        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (filter is null)
                    continue;

                var key = (filter.FieldKey ?? string.Empty).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "invoicedate":
                        ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "invoiceDate", ref invoiceStart, ref invoiceEnd);
                        break;
                    case "duedate":
                        ReportConfigurationDatasetTranslation.ApplyDateFilter(filter, "dueDate", ref dueStart, ref dueEnd);
                        break;
                    case "supplierid":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        supplierId = ReportConfigurationDatasetTranslation.CoalesceLong(supplierId, filter.Value, "supplierId");
                        break;
                    case "currency":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        currency = ReportConfigurationDatasetTranslation.CoalesceString(currency, filter.Value, "currency");
                        break;
                    case "invoicestatus":
                        ReportConfigurationDatasetTranslation.EnsureOperator(filter, ReportConfigurationConstants.OperatorEq);
                        invoiceStatus = ReportConfigurationDatasetTranslation.CoalesceString(invoiceStatus, filter.Value, "invoiceStatus");
                        break;
                    default:
                        throw BusinessException.InvalidParameter(
                            $"数据集 {ReportConfigurationConstants.DatasetSupplierAging} 不支持的筛选字段: {key}");
                }
            }
        }

        request.InvoiceDateFrom = invoiceStart;
        request.InvoiceDateTo = invoiceEnd;
        request.DueDateFrom = dueStart;
        request.DueDateTo = dueEnd;
        request.SupplierId = supplierId;
        request.Currency = currency;
        request.InvoiceStatus = invoiceStatus;
    }


    private async Task<(List<Dictionary<string, object?>> Rows, int Total)> QueryPageAsync(
        DynamicSupplierAgingReportRequest request, int page, DateTime asOfDate, CancellationToken cancellationToken)
    {
        var source = _db.PurchaseInvoices.AsNoTracking().Where(i => !i.IsDeleted);

        if (request.SupplierId is { } supplierId) source = source.Where(i => i.SupplierId == supplierId);
        if (request.Currency is { } currency) source = source.Where(i => i.Currency == currency);

        switch (request.InvoiceStatus)
        {
            case DynamicSupplierAgingReportRules.InvoiceStatusDraft:
                source = source.Where(i => i.Status == PurchaseInvoiceRules.StatusDraft);
                break;
            case DynamicSupplierAgingReportRules.InvoiceStatusVoided:
                source = source.Where(i => i.Status == PurchaseInvoiceRules.StatusVoided);
                break;
            case DynamicSupplierAgingReportRules.InvoiceStatusAll:
                break;
            default:
                source = source.Where(i => i.Status == PurchaseInvoiceRules.StatusRecorded);
                break;
        }

        if (request.InvoiceDateFrom is { } invoiceFrom) source = source.Where(i => i.InvoiceDate >= invoiceFrom);
        if (request.InvoiceDateTo is { } invoiceTo) source = source.Where(i => i.InvoiceDate <= invoiceTo);
        if (request.DueDateFrom is { } dueFrom) source = source.Where(i => i.DueDate != null && i.DueDate >= dueFrom);
        if (request.DueDateTo is { } dueTo)
        {
            var dueToExclusive = dueTo.AddDays(1);
            source = source.Where(i => i.DueDate != null && i.DueDate < dueToExclusive);
        }

        var total = await source.CountAsync(cancellationToken);
        var pageIds = await source
            .OrderBy(i => i.SupplierId).ThenBy(i => i.Currency)
            .ThenBy(i => i.DueDate)
            .ThenByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
            .Skip((page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        var invoices = new List<PurchaseInvoice>();
        if (pageIds.Count > 0)
        {
            var loaded = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => pageIds.Contains(i.Id))
                .ToListAsync(cancellationToken);
            var byId = loaded.ToDictionary(i => i.Id);
            invoices = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var rows = await BuildRowsAsync(invoices, asOfDate, cancellationToken);
        return (rows, total);
    }

    private async Task<List<Dictionary<string, object?>>> BuildRowsAsync(
        IReadOnlyList<PurchaseInvoice> invoices, DateTime asOfDate, CancellationToken cancellationToken)
    {
        if (invoices.Count == 0)
            return new List<Dictionary<string, object?>>();

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var allocated = await AggregatesForInvoicesAsync(invoiceIds, cancellationToken);

        var supplierIds = invoices.Select(i => i.SupplierId).Distinct().ToList();
        var suppliers = (await _db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.Id);

        var rows = new List<Dictionary<string, object?>>(invoices.Count);
        foreach (var invoice in invoices)
        {
            var supplier = suppliers.TryGetValue(invoice.SupplierId, out var found) ? found : null;
            var aggregate = allocated.Truncated
                ? null
                : allocated.Get(invoice.Id) ?? InvoiceAllocatedPaymentAggregateSet.Empty(invoice.Id);
            rows.Add(MapInvoice(invoice, aggregate, supplier, asOfDate));
        }

        return rows;
    }


    private static Dictionary<string, object?> MapInvoice(
        PurchaseInvoice invoice, InvoiceAllocatedPaymentAggregate? allocated,
        BaseSupplier? supplier, DateTime asOfDate)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var gross = invoice.GrossAmount;

        var activeAmount = allocated?.ActiveAmount;
        var overAllocated = activeAmount is { } active && active > gross;
        decimal? remainingAmount = activeAmount is null || overAllocated ? null : gross - activeAmount.Value;
        var remainingState = overAllocated
            ? RemainingOverAllocated
            : activeAmount is null ? RemainingUnknown : RemainingKnown;

        string allocationState;
        if (allocated is null)
            allocationState = AllocationUnknown;
        else if (overAllocated)
            allocationState = AllocationOverAllocated;
        else if (activeAmount.GetValueOrDefault() <= 0m)
            allocationState = allocated.HasAnyRow ? AllocationHistoricalOnly : AllocationNone;
        else if (activeAmount.GetValueOrDefault() >= gross)
            allocationState = AllocationFull;
        else
            allocationState = AllocationPartial;

        var bucket = AgingBucketOf(invoice.DueDate, asOfDate, out var overdueDays);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoiceId"] = invoice.Id,
            ["invoiceType"] = invoice.InvoiceType,
            ["invoiceTypeText"] = PurchaseInvoiceRules.InvoiceTypeText(invoice.InvoiceType),
            ["invoiceCode"] = invoice.InvoiceCode,
            ["invoiceNumber"] = invoice.InvoiceNumber,
            ["invoiceIdentityText"] = PurchaseInvoiceRules.IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
            ["invoiceDate"] = invoice.InvoiceDate,
            ["supplierId"] = invoice.SupplierId,
            ["supplierCode"] = invoice.SupplierCode,
            ["supplierName"] = invoice.SupplierName,
            ["supplierAvailable"] = PurchaseInvoiceRules.IsSupplierSelectable(supplier),
            ["supplierAvailabilityText"] = PurchaseInvoiceRules.SupplierAvailabilityText(supplier),
            ["currency"] = currency,
            ["amountDecimals"] = CurrencyAmountRules.PrecisionOf(currency),
            ["netAmount"] = invoice.NetAmount,
            ["taxAmount"] = invoice.TaxAmount,
            ["grossAmount"] = gross,
            ["invoiceStatus"] = invoice.Status,
            ["invoiceStatusText"] = PurchaseInvoiceRules.StatusText(invoice.Status),
            ["isActiveEvidence"] = IsActiveEvidence(invoice.Status),
            ["isDraft"] = invoice.Status == PurchaseInvoiceRules.StatusDraft,
            ["isVoided"] = invoice.Status == PurchaseInvoiceRules.StatusVoided,
            ["dueDate"] = invoice.DueDate,
            ["dueDateKnown"] = invoice.DueDate.HasValue,
            ["dueDateText"] = PurchaseInvoiceRules.DueDateText(invoice.DueDate),
            ["paymentTerms"] = invoice.PaymentTerms,
            ["paymentTermsText"] = PurchaseInvoiceRules.PaymentTermsText(invoice.PaymentTerms),
            ["agingBucket"] = bucket,
            ["agingBucketText"] = bucket is null ? BucketText(UnknownDueDateBucket) : BucketText(bucket),
            ["overdueDays"] = overdueDays,
            ["agingText"] = AgingText(bucket, overdueDays),
            ["allocationState"] = allocationState,
            ["allocationStateText"] = AllocationStateText(allocationState),
            ["activeAllocatedAmount"] = activeAmount,
            ["activeAllocationCount"] = allocated?.ActiveCount,
            ["activePaymentCount"] = allocated?.ActivePaymentCount,
            ["remainingAmount"] = remainingAmount,
            ["remainingState"] = remainingState,
            ["remainingStateText"] = RemainingStateText(remainingState),
            ["voidedAllocationCount"] = allocated?.VoidedCount,
            ["voidedAllocationAmount"] = allocated?.VoidedAmount,
            ["invoiceInactiveAllocationCount"] = allocated?.InvoiceInactiveCount,
            ["invoiceInactiveAllocationAmount"] = allocated?.InvoiceInactiveAmount,
            ["invalidAllocationCount"] = allocated?.InvalidCount,
            ["invalidAllocationAmount"] = allocated?.InvalidAmount,
            ["unavailableAllocationCount"] = allocated?.UnavailableCount,
            ["unavailableAllocationAmount"] = allocated?.UnavailableAmount,
            ["hasAllocationHistory"] = allocated?.HasHistoricalRow ?? false,
            ["hasInvalidOrUnavailableEvidence"] = allocated is { } a && (a.InvalidCount > 0 || a.UnavailableCount > 0),
            ["historicalEvidenceText"] = BuildHistoricalEvidenceText(allocated, currency),
            ["note"] = BuildRowNote(invoice, allocated, overAllocated, allocationState),
        };
    }


    private static string BuildHistoricalEvidenceText(InvoiceAllocatedPaymentAggregate? allocated, string currency)
    {
        if (allocated is null)
            return "未知（命中系统有界上限：历史 / 无效引用证据无法穷尽，不给部分合计）";

        if (!allocated.HasHistoricalRow)
            return "无历史 / 无效引用证据（缺失证据按「无」显示，绝不当成未付款、已付款、已结清或逾期）";

        var parts = new List<string>();
        if (allocated.VoidedCount > 0)
            parts.Add($"已作废引用行 {allocated.VoidedCount} 条 / {allocated.VoidedAmount}");
        if (allocated.InvoiceInactiveCount > 0)
            parts.Add($"发票已失效（草稿 / 已作废）{allocated.InvoiceInactiveCount} 条 / {allocated.InvoiceInactiveAmount}");
        if (allocated.InvalidCount > 0)
            parts.Add($"无效证据（供应商 / 币种或快照不一致）{allocated.InvalidCount} 条 / {allocated.InvalidAmount}");
        if (allocated.UnavailableCount > 0)
            parts.Add($"无法确认证据（付款单或发票已删除）{allocated.UnavailableCount} 条 / {allocated.UnavailableAmount}");

        return string.Join("；", parts)
            + $"（币种 {currency}；历史 / 无效证据绝不并入有效合计，也不被修复、改派或合并）";
    }

    private static string BuildRowNote(
        PurchaseInvoice invoice, InvoiceAllocatedPaymentAggregate? allocated,
        bool overAllocated, string allocationState)
    {
        var sb = new StringBuilder();
        if (allocated is null)
        {
            sb.Append("命中系统有界上限：有效已分配与剩余证据按「未知」显示（不给部分合计），"
                + "也绝不代表未付款、已付款、已结清或逾期。");
        }
        else if (overAllocated)
        {
            sb.Append("有效已分配金额超过含税总额：与 ERP-066 源规则矛盾，按无效证据显示，剩余证据按「未知」，"
                + "绝不轧为 0、也不视为已结清。");
        }
        else if (allocationState == AllocationNone)
        {
            sb.Append("该发票没有任何持久化付款引用行：这是证据缺口，不代表未付款、已付款、已结清、逾期或欠款。");
        }
        else
        {
            sb.Append("剩余证据为仓库对账口径的算术派生（含税总额 − 有效已分配），"
                + "不是应付余额、不是付款授权，也不代表已结清。");
        }

        if (!invoice.DueDate.HasValue)
            sb.Append(" 到期日未登记（未知）：账龄不计算，单独成组（绝不按开票日期、付款条件或默认账期推算）。");
        if (invoice.Status == PurchaseInvoiceRules.StatusDraft)
            sb.Append(" 该发票仍为草稿：金额不计入有效应付证据合计。");
        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            sb.Append(" 该发票已作废：身份与金额保留可读，但不计入有效应付证据合计，也不做任何修复。");

        return sb.ToString().Trim();
    }


    // ==================== ERP-066 / ERP-067 分配证据分桶（与 PurchaseOrderInvoicePaymentEvidence 同源） ====================

    private const string EvidenceBucketRecorded = "recorded";
    private const string EvidenceBucketVoided = "voided";
    private const string EvidenceBucketInvoiceInactive = "invoice_inactive";
    private const string EvidenceBucketInvalid = "invalid";
    private const string EvidenceBucketUnavailable = "unavailable";

    private const int MaxAggregateInvoices = 500;
    private const int MaxBatchEvidenceRows = 5000;

    private sealed record InvoiceAllocatedPaymentAggregate(
        long InvoiceId,
        decimal ActiveAmount,
        int ActiveCount,
        int ActivePaymentCount,
        decimal RecordedPaymentAmount,
        decimal UnallocatedPaymentAmount,
        int VoidedCount,
        decimal VoidedAmount,
        int InvoiceInactiveCount,
        decimal InvoiceInactiveAmount,
        int InvalidCount,
        decimal InvalidAmount,
        int UnavailableCount,
        decimal UnavailableAmount)
    {
        public bool HasAnyRow => ActiveCount + VoidedCount + InvoiceInactiveCount + InvalidCount + UnavailableCount > 0;
        public bool HasHistoricalRow => VoidedCount + InvoiceInactiveCount + InvalidCount + UnavailableCount > 0;
    }

    private sealed record InvoiceAllocatedPaymentAggregateSet(
        IReadOnlyDictionary<long, InvoiceAllocatedPaymentAggregate> ByInvoice,
        bool Truncated)
    {
        public InvoiceAllocatedPaymentAggregate? Get(long invoiceId)
            => !Truncated && ByInvoice.TryGetValue(invoiceId, out var aggregate) ? aggregate : null;

        public static InvoiceAllocatedPaymentAggregate Empty(long invoiceId)
            => new(invoiceId, 0m, 0, 0, 0m, 0m, 0, 0m, 0, 0m, 0, 0m, 0, 0m);
    }

    private sealed record PaymentActiveTotalRow(long PaymentId, decimal Amount);

    private sealed record ActivePaymentInfo(string Currency, decimal Amount);

    private sealed class BucketTotals
    {
        public decimal Amount { get; private set; }
        public int RowCount { get; private set; }
        public HashSet<long> PaymentIds { get; } = new();

        public void Add(decimal amount, long paymentId)
        {
            Amount += amount;
            RowCount++;
            if (paymentId > 0) PaymentIds.Add(paymentId);
        }
    }

    private static Dictionary<string, BucketTotals> EmptyTotals()
    {
        var totals = new Dictionary<string, BucketTotals>(StringComparer.Ordinal)
        {
            [EvidenceBucketRecorded] = new BucketTotals(),
            [EvidenceBucketVoided] = new BucketTotals(),
            [EvidenceBucketInvoiceInactive] = new BucketTotals(),
            [EvidenceBucketInvalid] = new BucketTotals(),
            [EvidenceBucketUnavailable] = new BucketTotals(),
        };
        return totals;
    }


    private async Task<InvoiceAllocatedPaymentAggregateSet> AggregatesForInvoicesAsync(
        IReadOnlyList<long> invoiceIds, CancellationToken cancellationToken)
    {
        var ids = invoiceIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
            return new InvoiceAllocatedPaymentAggregateSet(new Dictionary<long, InvoiceAllocatedPaymentAggregate>(), false);
        if (ids.Count > MaxAggregateInvoices)
            return new InvoiceAllocatedPaymentAggregateSet(new Dictionary<long, InvoiceAllocatedPaymentAggregate>(), true);

        var rows = await _db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && ids.Contains(a.PurchaseInvoiceId))
            .OrderBy(a => a.PurchaseInvoiceId).ThenBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .Take(MaxBatchEvidenceRows + 1)
            .ToListAsync(cancellationToken);
        var truncated = rows.Count > MaxBatchEvidenceRows;
        if (truncated) rows.RemoveRange(MaxBatchEvidenceRows, rows.Count - MaxBatchEvidenceRows);

        var invoiceById = (await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => ids.Contains(i.Id)).ToListAsync(cancellationToken))
            .ToDictionary(i => i.Id);

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var paymentById = paymentIds.Count == 0
            ? new Dictionary<long, FinancePayment>()
            : (await _db.FinancePayments.AsNoTracking()
                    .Where(p => paymentIds.Contains(p.Id)).ToListAsync(cancellationToken))
                .ToDictionary(p => p.Id);

        var activeTotalByPayment = await ActiveInvoiceAllocationTotalsAsync(paymentIds, cancellationToken);

        var byInvoice = new Dictionary<long, InvoiceAllocatedPaymentAggregate>();
        foreach (var group in rows.GroupBy(r => r.PurchaseInvoiceId))
        {
            byInvoice[group.Key] = BuildInvoiceAggregate(group.Key, group.ToList(), invoiceById, paymentById, activeTotalByPayment);
        }

        return new InvoiceAllocatedPaymentAggregateSet(byInvoice, truncated);
    }

    private async Task<Dictionary<long, PaymentActiveTotalRow>> ActiveInvoiceAllocationTotalsAsync(
        IReadOnlyList<long> paymentIds, CancellationToken cancellationToken)
    {
        if (paymentIds.Count == 0) return new Dictionary<long, PaymentActiveTotalRow>();

        var totals = await _db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive
                        && paymentIds.Contains(a.PaymentId))
            .GroupBy(a => a.PaymentId)
            .Select(g => new PaymentActiveTotalRow(g.Key, g.Sum(a => a.AllocatedAmount)))
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(t => t.PaymentId);
    }


    private static (string Bucket, string Reason) ClassifyRow(
        SupplierPaymentInvoiceAllocation row, FinancePayment? payment, PurchaseInvoice? invoice)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);

        if (row.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided)
            return (EvidenceBucketVoided, "引用行已作废：原始值、快照与作废原因保留可读，但不计入有效合计，也不代表付款被撤销。");

        if (row.Status != SupplierPaymentInvoiceAllocationRules.StatusActive)
            return (EvidenceBucketInvalid, $"引用行状态「{row.Status}」不在有效范围内（仅 1 有效 / 2 已作废），无法确认其有效性：不计入有效合计。");

        if (payment is null || payment.IsDeleted)
            return (EvidenceBucketUnavailable, "引用行指向的付款单不存在或已删除：金额无法确认，不计入有效合计，也不代表未付款或已付款。");

        if (invoice is null || invoice.IsDeleted)
            return (EvidenceBucketUnavailable, "引用行指向的采购发票不存在或已删除：金额无法确认，不计入有效合计。");

        if (invoice.Status == PurchaseInvoiceRules.StatusDraft)
            return (EvidenceBucketInvoiceInactive, "引用的采购发票仍为草稿（尚未登记为证据）：发票侧证据不成立，不计入有效合计。");

        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            return (EvidenceBucketInvoiceInactive, "引用的采购发票已作废：发票侧证据失效，其金额仅作历史核对，不计入有效合计。");

        if (row.AllocatedAmount <= 0)
            return (EvidenceBucketInvalid, $"引用金额 {row.AllocatedAmount} 非正数：无效证据，不计入有效合计。");

        if (row.AllocatedAmount > row.PaymentAmount)
            return (EvidenceBucketInvalid, $"引用金额 {row.AllocatedAmount} 大于引用行付款单金额快照 {row.PaymentAmount}：快照自相矛盾，无效证据，不计入有效合计。");

        if (row.AllocatedAmount > row.InvoiceGrossAmount)
            return (EvidenceBucketInvalid, $"引用金额 {row.AllocatedAmount} 大于引用行发票含税总额快照 {row.InvoiceGrossAmount}：快照自相矛盾，无效证据，不计入有效合计。");

        var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        if (!string.Equals(currency, paymentCurrency, StringComparison.Ordinal))
            return (EvidenceBucketInvalid, $"引用行币种 {currency} 与付款单币种 {paymentCurrency} 不一致：不换算、不合并、不改派，无效证据。");

        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        if (!string.Equals(currency, invoiceCurrency, StringComparison.Ordinal))
            return (EvidenceBucketInvalid, $"引用行币种 {currency} 与发票币种 {invoiceCurrency} 不一致：不换算、不合并、不改派，无效证据。");

        if (row.SupplierId != payment.SupplierId)
            return (EvidenceBucketInvalid, $"引用行供应商快照 Id={row.SupplierId} 与付款单供应商 Id={payment.SupplierId} 不一致：不改派、不合并，无效证据。");

        if (row.SupplierId != invoice.SupplierId)
            return (EvidenceBucketInvalid, $"引用行供应商快照 Id={row.SupplierId} 与发票供应商 Id={invoice.SupplierId} 不一致：不改派、不合并，无效证据。");

        return (EvidenceBucketRecorded, "有效证据：引用行未作废、付款单可用、发票仍为已登记、币种与供应商快照自相一致；"
            + "它只代表「这笔付款按登记指向了这张发票」，不代表已付款、已结算或已核销。");
    }


    private static InvoiceAllocatedPaymentAggregate BuildInvoiceAggregate(long invoiceId,
        List<SupplierPaymentInvoiceAllocation> rows,
        Dictionary<long, PurchaseInvoice> invoiceById,
        Dictionary<long, FinancePayment> paymentById,
        Dictionary<long, PaymentActiveTotalRow> activeTotalByPayment)
    {
        invoiceById.TryGetValue(invoiceId, out var invoice);

        var totals = EmptyTotals();
        var activePaymentInfo = new Dictionary<long, ActivePaymentInfo>();

        foreach (var row in rows)
        {
            paymentById.TryGetValue(row.PaymentId, out var payment);
            var (bucket, _) = ClassifyRow(row, payment, invoice);
            totals[bucket].Add(row.AllocatedAmount, row.PaymentId);
            if (bucket != EvidenceBucketRecorded) continue;

            var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
            activePaymentInfo[row.PaymentId] = activePaymentInfo.TryGetValue(row.PaymentId, out var info)
                ? info with { Amount = info.Amount + row.AllocatedAmount }
                : new ActivePaymentInfo(currency, row.AllocatedAmount);
        }

        var recordedPaymentAmount = 0m;
        var unallocatedPaymentAmount = 0m;
        foreach (var item in activePaymentInfo)
        {
            if (!paymentById.TryGetValue(item.Key, out var payment)) continue;
            var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
            if (!string.Equals(paymentCurrency, item.Value.Currency, StringComparison.Ordinal)) continue;

            var activeTotal = activeTotalByPayment.TryGetValue(item.Key, out var total) ? total.Amount : item.Value.Amount;
            recordedPaymentAmount += payment.Amount;

            var remaining = payment.Amount - activeTotal;
            if (remaining > 0m) unallocatedPaymentAmount += remaining;
        }

        return new InvoiceAllocatedPaymentAggregate(invoiceId,
            totals[EvidenceBucketRecorded].Amount,
            totals[EvidenceBucketRecorded].RowCount,
            activePaymentInfo.Count,
            recordedPaymentAmount,
            unallocatedPaymentAmount,
            totals[EvidenceBucketVoided].RowCount,
            totals[EvidenceBucketVoided].Amount,
            totals[EvidenceBucketInvoiceInactive].RowCount,
            totals[EvidenceBucketInvoiceInactive].Amount,
            totals[EvidenceBucketInvalid].RowCount,
            totals[EvidenceBucketInvalid].Amount,
            totals[EvidenceBucketUnavailable].RowCount,
            totals[EvidenceBucketUnavailable].Amount);
    }


    private static ReportConfigurationDatasetDto BuildDataset(DynamicSupplierAgingReportCatalogDto catalog)
    {
        var fields = catalog.Fields
            .Select(f => BuildField(f.Key, f.Label, f.DataType, f.Filterable))
            .ToList();

        return new ReportConfigurationDatasetDto(
            ReportConfigurationConstants.DatasetSupplierAging,
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

