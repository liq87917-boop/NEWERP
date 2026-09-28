using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购交期异常工作台口径常量（ERP-099）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免「未知」被当成 0、或「晚确认」被当成「逾期」。
/// </summary>
public static class PurchaseOrderDeliveryExceptionSemantics
{
    /// <summary>交期状态：已收齐（仅「以本单为来源、未删除、已审核」的入库单收齐）</summary>
    public const string DeliveryReceived = "received";

    /// <summary>交期状态：逾期（交期已过 as-of 基准日且仍未收齐）</summary>
    public const string DeliveryOverdue = "overdue";

    /// <summary>交期状态：即将到期（交期落在 as-of 后 <see cref="DueSoonDays"/> 天内且仍未收齐）</summary>
    public const string DeliveryDueSoon = "due_soon";

    /// <summary>交期状态：晚确认（供应商确认交期晚于要求交期，或要求交期已过却仍未确认）</summary>
    public const string DeliveryLateConfirmation = "late_confirmation";

    /// <summary>交期状态：在途正常（交期未到，未收齐但尚未进入异常窗口）</summary>
    public const string DeliveryOnSchedule = "on_schedule";

    /// <summary>交期状态：未知（缺少交期日期或收货证据不完整，无法判定）</summary>
    public const string DeliveryUnknown = "unknown";

    /// <summary>收货状态：本次派生命中批量上限，数量不完整（未知，不等于 0）</summary>
    public const string ReceiptUnknown = "unknown";

    /// <summary>「即将到期」窗口（天）：交期落在 [asOf, asOf + DueSoonDays] 内视为 due_soon</summary>
    public const int DueSoonDays = 7;

    /// <summary>口径说明（界面与文档同源）</summary>
    public static readonly string RuleText =
        "本工作台是采购订单的交期异常视图（只读派生）：交期状态按显式 as-of 基准日、既有要求交期（订单交货日期）与供应商确认交期、"
        + "以及「以本单为来源、未删除、已审核」的采购入库收货证据派生，不改写订单上任何已登记进度；"
        + "已收齐 = 已审核且未删除的入库收齐；部分收货仍视为未收齐、按日期继续判定；"
        + "逾期 = 交期已过 as-of 且未收齐；即将到期 = 交期在 as-of 后 " + DueSoonDays + " 天内且未收齐；"
        + "晚确认 = 供应商确认交期晚于要求交期，或要求交期已过却仍未确认；"
        + "缺少交期日期或收货证据不完整时为未知（unknown），绝不当作 0 或正常。";

    /// <summary>范围说明：合计与计数只统计本次返回页的订单（分页有界）</summary>
    public const string ScopeNoteText =
        "以下汇总与计数只统计本次返回页的订单；total 为符合筛选条件的未删除采购订单总数；"
        + "分页按供应商 + 订单日期 + 单据 Id 稳定排序。";
}

/// <summary>采购交期异常工作台查询条件（全部为只读筛选参数）</summary>
public sealed class PurchaseOrderDeliveryExceptionQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>as-of 基准日（评估交期状态的参考日期；留空 = 今天）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>关键字（匹配采购单号 / 采购合同号 / 归属销售订单号；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>解析后的 as-of 基准日（归一到当天）</summary>
    internal DateTime AsOf { get; private set; } = DateTime.Today;

    /// <summary>归一化并校验：分页参数钳制到有界范围，as-of 缺省为今天，关键字去除首尾空白。</summary>
    public void Normalize()
    {
        AsOf = (AsOfDate ?? DateTime.Today).Date;
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
    }
}

/// <summary>单张采购订单的交期异常行（只读派生，不落库）</summary>
public sealed class PurchaseOrderDeliveryExceptionItem
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public long SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>单据状态（采购订单本身的审核状态）</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>要求交期（订单交货日期；null = 未知）</summary>
    public DateTime? RequestedDate { get; init; }

    /// <summary>供应商确认交期（null = 未知）</summary>
    public DateTime? ConfirmedDate { get; init; }

    /// <summary>订单数量（收货证据不完整时为 null = 未知）</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>已收数量（仅已审核未删除入库；不完整时为 null = 未知）</summary>
    public decimal? ReceivedQuantity { get; init; }

    /// <summary>未收数量（不完整时为 null = 未知）</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>待审数量（待提交 / 已提交入库，不计入已收；不完整时为 null = 未知）</summary>
    public decimal? PendingQuantity { get; init; }

    /// <summary>收货状态：none / partial / complete / over_received / unknown</summary>
    public string ReceiptStatus { get; init; } = PurchaseOrderDeliveryExceptionSemantics.ReceiptUnknown;

    /// <summary>最近一次已审核入库日期（null = 未知）</summary>
    public DateTime? LastReceiptDate { get; init; }

    /// <summary>交期状态：received / overdue / due_soon / late_confirmation / on_schedule / unknown</summary>
    public string DeliveryStatus { get; init; } = PurchaseOrderDeliveryExceptionSemantics.DeliveryUnknown;

    /// <summary>状态判定说明（未知原因 / 晚确认详情等）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本页交期状态计数（合计与计数只统计本次返回页的订单）</summary>
public sealed class PurchaseOrderDeliveryExceptionCounts
{
    public int Total { get; init; }
    public int Received { get; init; }
    public int Overdue { get; init; }
    public int DueSoon { get; init; }
    public int LateConfirmation { get; init; }
    public int OnSchedule { get; init; }
    public int Unknown { get; init; }
}

/// <summary>采购交期异常工作台报表（只读派生）</summary>
public sealed class PurchaseOrderDeliveryExceptionReport
{
    public DateTime AsOfDate { get; init; }
    public long? SupplierId { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>口径说明（界面与文档同源）</summary>
    public string Rule { get; init; } = PurchaseOrderDeliveryExceptionSemantics.RuleText;

    /// <summary>范围说明（界面与文档同源）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderDeliveryExceptionSemantics.ScopeNoteText;

    public PurchaseOrderDeliveryExceptionCounts Counts { get; init; } = new();
    public List<PurchaseOrderDeliveryExceptionItem> Items { get; init; } = new();
}

/// <summary>
/// 采购交期异常工作台派生（ERP-099，只读）。按供应商 + 显式 as-of 基准日过滤未删除采购订单，
/// 报告要求交期 / 供应商确认交期 / 已审核入库收货证据，并派生逾期 / 即将到期 / 晚确认 / 已收齐等交期状态。
/// <para>收货数量复用 ERP-026 的同一套权威批量派生（只统计「以本单为来源、未删除、已审核」的采购入库），
/// 不引入第二套收货算法；缺少交期日期或收货证据不完整时为未知，绝不当作 0 或正常。</para>
/// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单上任何已登记进度。</para>
/// <para>查询有界：分页 + 批量取数，固定次数数据集访问（无逐单查库）。</para>
/// </summary>
public static class PurchaseOrderDeliveryExceptions
{
    /// <summary>最近入库证据的单次查询上限（只影响「最近入库日期」证据，不影响收货数量口径）</summary>
    private const int EvidenceCeiling = 2000;

    /// <summary>派生交期异常报表。</summary>
    public static async Task<PurchaseOrderDeliveryExceptionReport> ForQueryAsync(
        IErpDbContext db, PurchaseOrderDeliveryExceptionQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();
        var asOf = query.AsOf;

        // 1) 筛选 + 稳定排序 + 分页（先取本页订单 Id，有界）
        var source = db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);
        if (query.SupplierId.HasValue)
            source = source.Where(o => o.SupplierId == query.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.ContractNo.Contains(kw)
                                       || o.OwningSalesOrderNo.Contains(kw));
        }

        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(o => o.SupplierId).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(o => o.Id)
            .ToListAsync();

        // 2) 加载本页订单（含明细，供收货派生使用）
        var pageOrders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
            .Where(o => pageIds.Contains(o.Id))
            .ToListAsync();
        var ordersById = pageOrders.ToDictionary(o => o.Id);

        // 3) 收货证据：复用 ERP-026 的权威批量派生（固定 2 次查询，无逐单查库）
        var receiptSummaries = await PurchaseOrderProgress.ReceiptSummariesForOrdersAsync(db, pageOrders);

        // 4) 最近一次已审核入库日期（有界批量，一次查询；按日期倒序截断以保证「最近日期」可靠）
        var receiptEvidence = await db.StockIns.AsNoTracking()
            .Where(s => s.PurchaseOrderId != null && pageIds.Contains(s.PurchaseOrderId.Value)
                        && !s.IsDeleted && s.Status == DocumentStatus.Approved)
            .OrderByDescending(s => s.StockInDate).ThenByDescending(s => s.Id)
            .Select(s => new { s.PurchaseOrderId, s.StockInDate })
            .Take(EvidenceCeiling)
            .ToListAsync();
        var lastReceiptByOrder = receiptEvidence
            .GroupBy(x => x.PurchaseOrderId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(x => x.StockInDate));

        // 5) 供应商名称（有界批量，一次查询）
        var supplierIds = pageOrders.Select(o => o.SupplierId).Distinct().ToList();
        var suppliers = await db.BaseSuppliers.AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.SupplierName })
            .ToListAsync();
        var supplierNameById = suppliers.ToDictionary(s => s.Id, s => s.SupplierName);

        // 6) 按稳定分页顺序组装行
        var items = new List<PurchaseOrderDeliveryExceptionItem>(pageIds.Count);
        foreach (var id in pageIds)
        {
            if (!ordersById.TryGetValue(id, out var order)) continue;

            var summary = receiptSummaries.GetValueOrDefault(id);
            var truncated = summary?.Truncated ?? false;
            var receiptStatus = truncated
                ? PurchaseOrderDeliveryExceptionSemantics.ReceiptUnknown
                : (summary?.ReceiptStatus ?? PurchaseOrderProgress.ReceiptNone);

            var receivedComplete = !truncated
                && summary is not null
                && summary.ReceiptStatus == PurchaseOrderProgress.ReceiptComplete;

            var requestedDate = order.DeliveryDate?.Date;
            var confirmedDate = order.SupplierConfirmedDate?.Date;

            var (deliveryStatus, note) = DeriveState(asOf, requestedDate, confirmedDate, receivedComplete);
            if (truncated)
                note += "；收货证据超过单次派生上限，数量未知";

            DateTime? lastReceiptDate = lastReceiptByOrder.TryGetValue(order.Id, out var lrd) ? lrd : null;

            items.Add(new PurchaseOrderDeliveryExceptionItem
            {
                OrderId = order.Id,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                SupplierId = order.SupplierId,
                SupplierName = supplierNameById.GetValueOrDefault(order.SupplierId, string.Empty),
                Currency = order.Currency.ToString(),
                Status = order.Status.ToString(),
                RequestedDate = requestedDate,
                ConfirmedDate = confirmedDate,
                OrderedQuantity = truncated ? null : summary?.OrderedQuantity,
                ReceivedQuantity = truncated ? null : summary?.ReceivedQuantity,
                OutstandingQuantity = truncated ? null : summary?.OutstandingQuantity,
                PendingQuantity = truncated ? null : summary?.PendingQuantity,
                ReceiptStatus = receiptStatus,
                LastReceiptDate = lastReceiptDate,
                DeliveryStatus = deliveryStatus,
                Note = note,
            });
        }

        var counts = new PurchaseOrderDeliveryExceptionCounts
        {
            Total = items.Count,
            Received = items.Count(i => i.DeliveryStatus == PurchaseOrderDeliveryExceptionSemantics.DeliveryReceived),
            Overdue = items.Count(i => i.DeliveryStatus == PurchaseOrderDeliveryExceptionSemantics.DeliveryOverdue),
            DueSoon = items.Count(i => i.DeliveryStatus == PurchaseOrderDeliveryExceptionSemantics.DeliveryDueSoon),
            LateConfirmation = items.Count(i => i.DeliveryStatus == PurchaseOrderDeliveryExceptionSemantics.DeliveryLateConfirmation),
            OnSchedule = items.Count(i => i.DeliveryStatus == PurchaseOrderDeliveryExceptionSemantics.DeliveryOnSchedule),
            Unknown = items.Count(i => i.DeliveryStatus == PurchaseOrderDeliveryExceptionSemantics.DeliveryUnknown),
        };

        return new PurchaseOrderDeliveryExceptionReport
        {
            AsOfDate = asOf,
            SupplierId = query.SupplierId,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = query.PageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)query.PageSize),
            Counts = counts,
            Items = items,
        };
    }

    /// <summary>
    /// 交期状态派生（纯函数，便于单测日期边界）。判定顺序：已收齐 → 缺日期未知 → 晚确认 → 逾期 / 即将到期 / 在途。
    /// </summary>
    private static (string Status, string Note) DeriveState(
        DateTime asOf, DateTime? requestedDate, DateTime? confirmedDate, bool receivedComplete)
    {
        if (receivedComplete)
            return (PurchaseOrderDeliveryExceptionSemantics.DeliveryReceived, "已按已审核且未删除的入库单收齐");

        if (requestedDate is null && confirmedDate is null)
            return (PurchaseOrderDeliveryExceptionSemantics.DeliveryUnknown, "缺少要求交期与供应商确认交期，无法判断");

        // 晚确认：供应商确认交期晚于要求交期，或要求交期已过却仍未确认
        if (confirmedDate is not null && requestedDate is not null && confirmedDate.Value > requestedDate.Value)
            return (PurchaseOrderDeliveryExceptionSemantics.DeliveryLateConfirmation,
                $"供应商确认交期（{confirmedDate.Value:yyyy-MM-dd}）晚于要求交期（{requestedDate.Value:yyyy-MM-dd}）");
        if (confirmedDate is null && requestedDate is not null && requestedDate.Value < asOf)
            return (PurchaseOrderDeliveryExceptionSemantics.DeliveryLateConfirmation,
                $"要求交期（{requestedDate.Value:yyyy-MM-dd}）已过 as-of（{asOf:yyyy-MM-dd}），供应商尚未确认交期");

        var dueDate = confirmedDate ?? requestedDate!.Value;
        if (dueDate < asOf)
            return (PurchaseOrderDeliveryExceptionSemantics.DeliveryOverdue,
                $"交期（{dueDate:yyyy-MM-dd}）已过 as-of（{asOf:yyyy-MM-dd}），仍未收齐");
        if (dueDate <= asOf.AddDays(PurchaseOrderDeliveryExceptionSemantics.DueSoonDays))
            return (PurchaseOrderDeliveryExceptionSemantics.DeliveryDueSoon,
                $"交期（{dueDate:yyyy-MM-dd}）临近（as-of 后 {PurchaseOrderDeliveryExceptionSemantics.DueSoonDays} 天内），仍未收齐");
        return (PurchaseOrderDeliveryExceptionSemantics.DeliveryOnSchedule,
            $"交期（{dueDate:yyyy-MM-dd}）未到，未收齐但尚未进入异常窗口");
    }
}
