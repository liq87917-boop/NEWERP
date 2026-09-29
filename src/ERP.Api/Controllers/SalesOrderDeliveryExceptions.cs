using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售交期异常工作台口径常量（ERP-102）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免「未知」被当成 0、或「明细交期优先」被当成「订单交期」。
/// </summary>
public static class SalesOrderDeliveryExceptionSemantics
{
    /// <summary>交期状态：已出齐（仅「以本单为来源、未删除、已审核」的销售出库覆盖订单数量）</summary>
    public const string DeliveryFulfilled = "fulfilled";

    /// <summary>交期状态：逾期（交期已过 as-of 基准日且仍未出齐）</summary>
    public const string DeliveryOverdue = "overdue";

    /// <summary>交期状态：即将到期（交期落在 as-of 后 <see cref="DueSoonDays"/> 天内且仍未出齐）</summary>
    public const string DeliveryDueSoon = "due_soon";

    /// <summary>交期状态：在途正常（交期未到，未出齐但尚未进入异常窗口）</summary>
    public const string DeliveryOnSchedule = "on_schedule";

    /// <summary>交期状态：未知（缺少交期日期或出货证据不完整，无法判定）</summary>
    public const string DeliveryUnknown = "unknown";

    /// <summary>「即将到期」窗口（天）：交期落在 [asOf, asOf + DueSoonDays] 内视为 due_soon</summary>
    public const int DueSoonDays = 7;

    /// <summary>出货状态复用 ERP-032 的同一套常量，不引入第二套字符串口径</summary>
    public const string ShipmentNone = SalesOrderProgress.ShipmentNone;
    public const string ShipmentPartial = SalesOrderProgress.ShipmentPartial;
    public const string ShipmentComplete = SalesOrderProgress.ShipmentComplete;
    public const string ShipmentOver = SalesOrderProgress.ShipmentOver;
    public const string ShipmentUnknown = SalesOrderProgress.ShipmentUnknown;

    /// <summary>口径说明（界面与文档同源）</summary>
    public static readonly string RuleText =
        "本工作台是销售订单的交期异常视图（只读派生）：交期状态按显式 as-of 基准日、既有订单交货日期（订单头）与明细交货日期（明细行优先）派生，"
        + "并复用 ERP-032 的出货证据——出货数量只取「以本单为来源、未删除、已审核」的销售出库单；不改写订单状态与已登记进度；"
        + "已出齐 = 已审核且未删除的出库已覆盖订单数量（未出数量为 0）；部分出货仍视为未出齐、按日期继续判定；"
        + "逾期 = 交期已过 as-of 且未出齐；即将到期 = 交期在 as-of 后 " + DueSoonDays + " 天内且未出齐；"
        + "缺少交期日期或出货证据不完整时为未知（unknown），绝不当作 0 或正常。";

    /// <summary>范围说明：合计与计数只统计本次返回页的订单（分页有界）</summary>
    public const string ScopeNoteText =
        "以下汇总与计数只统计本次返回页的订单；total 为符合筛选条件的未删除销售订单总数；"
        + "分页按客户 + 订单日期 + 单据 Id 稳定排序。";
}
/// <summary>销售交期异常工作台查询条件（全部为只读筛选参数）</summary>
public sealed class SalesOrderDeliveryExceptionQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>客户筛选（留空 = 全部客户）</summary>
    public long? CustomerId { get; set; }

    /// <summary>as-of 基准日（评估交期状态的参考日期；留空 = 今天）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>关键字（匹配订单号 / 外销合同号 / 客户 PO 号；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>解析后的 as-of 基准日（归一到当天）</summary>
    internal DateTime AsOf { get; private set; } = DateTime.Today;

    /// <summary>归一化并校验：分页参数钳制到有界范围，as-of 缺省为今天，关键字去除首尾空白，客户 Id 非法时置空。</summary>
    public void Normalize()
    {
        AsOf = (AsOfDate ?? DateTime.Today).Date;
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
        if (CustomerId is <= 0) CustomerId = null;
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
    }
}

/// <summary>明细行交货日期（只读派生；行级交期优先于订单头交期）</summary>
public sealed class SalesOrderLineDeliveryDate
{
    public long DetailId { get; init; }
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public DateTime DeliveryDate { get; init; }
}
/// <summary>单张销售订单的交期异常行（只读派生，不落库）</summary>
public sealed class SalesOrderDeliveryExceptionItem
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    /// <summary>订单头交货日期；null = 未知（未登记）</summary>
    public DateTime? HeaderDeliveryDate { get; init; }

    /// <summary>明细行交货日期（行级交期优先；为空表示没有行级交期）</summary>
    public List<SalesOrderLineDeliveryDate> LineDeliveryDates { get; init; } = new();

    /// <summary>判定交期（= 最早明细交期，无明细交期时回落订单头交期；null = 未知）</summary>
    public DateTime? EffectiveDeliveryDate { get; init; }

    /// <summary>已订数量（订单明细口径）；null = 未知（命中派生上限），不等于 0</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>已审核出货数量（复用 ERP-032 出货证据）；null = 未知，不等于 0</summary>
    public decimal? ApprovedShippedQuantity { get; init; }

    /// <summary>未出数量 = max(0, 已订 − 已出)；null = 未知，不等于 0</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>待审核出货数量（已提交 / 待提交，不计入已出）；null = 未知</summary>
    public decimal? PendingQuantity { get; init; }

    /// <summary>出货状态：none / partial / complete / over_shipped / unknown（复用 ERP-032）</summary>
    public string ShipmentStatus { get; init; } = SalesOrderProgress.ShipmentNone;

    /// <summary>交期状态：fulfilled / overdue / due_soon / on_schedule / unknown</summary>
    public string DeliveryStatus { get; init; } = SalesOrderDeliveryExceptionSemantics.DeliveryUnknown;

    /// <summary>说明（判定依据 / 缺什么 / 为什么不能当作正常）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本页交期状态计数（只统计本次返回页）</summary>
public sealed class SalesOrderDeliveryExceptionCounts
{
    public int Total { get; init; }
    public int Fulfilled { get; init; }
    public int Overdue { get; init; }
    public int DueSoon { get; init; }
    public int OnSchedule { get; init; }
    public int Unknown { get; init; }
}

/// <summary>销售交期异常工作台报表（只读派生，不落库）</summary>
public sealed class SalesOrderDeliveryExceptionReport
{
    public DateTime AsOfDate { get; init; }
    public long? CustomerId { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>口径说明（界面与文档同源）</summary>
    public string Rule { get; init; } = SalesOrderDeliveryExceptionSemantics.RuleText;

    /// <summary>范围说明（分页有界）</summary>
    public string ScopeNote { get; init; } = SalesOrderDeliveryExceptionSemantics.ScopeNoteText;

    public SalesOrderDeliveryExceptionCounts Counts { get; init; } = new();
    public List<SalesOrderDeliveryExceptionItem> Items { get; init; } = new();
}
/// <summary>
/// 销售交期异常工作台（ERP-102，只读派生）：按客户 + 显式 as-of 基准日过滤销售订单，
/// 报告订单头 / 明细行交货日期与「已订 / 已审核出货 / 未出」数量证据，并派生逾期 / 即将到期 / 已出齐 / 在途 / 未知交期状态。
/// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单状态与已登记进度。</para>
/// </summary>
public static class SalesOrderDeliveryExceptions
{
    /// <summary>
    /// 交期异常工作台查询（GET /api/sales-orders/delivery-exceptions，只读、分页有界）。
    /// 出货证据复用 ERP-032 的权威批量派生（固定查询次数，无逐单查库）。
    /// </summary>
    public static async Task<SalesOrderDeliveryExceptionReport> ForQueryAsync(IErpDbContext db,
        SalesOrderDeliveryExceptionQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();
        var asOf = query.AsOf;

        // 1~2 次：筛选 + 计数 + 本页 Id（分页按客户 → 订单日期 → 单据 Id 稳定排序，翻页不重不漏）
        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(o => o.CustomerId).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => o.Id)
            .ToListAsync();

        // 3 次：本页订单（不含明细；明细另行一次有界批量查询，避免漏加载被当成 0）
        var pageOrders = pageIds.Count == 0
            ? new List<SalesOrder>()
            : await db.SalesOrders.AsNoTracking().Where(o => pageIds.Contains(o.Id)).ToListAsync();
        var orders = pageOrders
            .OrderBy(o => o.CustomerId).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .ToList();

        // 4 次：本页明细行（一次批量取全，供「明细交期优先」取值；无逐单查库）
        var details = pageIds.Count == 0
            ? new List<SalesOrderDetail>()
            : await db.SalesOrderDetails.AsNoTracking()
                .Where(d => !d.IsDeleted && pageIds.Contains(d.SalesOrderId))
                .ToListAsync();
        var detailsByOrder = details.GroupBy(d => d.SalesOrderId).ToDictionary(g => g.Key, g => g.ToList());

        // 5 次：客户名（仅用于展示，缺失时留空而不臆造）
        var customerIds = orders.Select(o => o.CustomerId).Where(c => c > 0).Distinct().ToList();
        var customerNames = customerIds.Count == 0
            ? new Dictionary<long, string>()
            : (await db.BaseCustomers.AsNoTracking()
                    .Where(c => customerIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.CustomerName })
                    .ToListAsync())
                .ToDictionary(c => c.Id, c => c.CustomerName);

        // 6~13 次：出货证据复用 ERP-032 的权威批量派生（固定 8 次，无逐单查库）
        var derived = (await SalesOrderProgress.ForOrdersAsync(db, orders)).ToDictionary(r => r.OrderId);

        var items = orders.Select(o => BuildItem(o, detailsByOrder, customerNames, derived, asOf)).ToList();

        var counts = new SalesOrderDeliveryExceptionCounts
        {
            Total = items.Count,
            Fulfilled = items.Count(i => i.DeliveryStatus == SalesOrderDeliveryExceptionSemantics.DeliveryFulfilled),
            Overdue = items.Count(i => i.DeliveryStatus == SalesOrderDeliveryExceptionSemantics.DeliveryOverdue),
            DueSoon = items.Count(i => i.DeliveryStatus == SalesOrderDeliveryExceptionSemantics.DeliveryDueSoon),
            OnSchedule = items.Count(i => i.DeliveryStatus == SalesOrderDeliveryExceptionSemantics.DeliveryOnSchedule),
            Unknown = items.Count(i => i.DeliveryStatus == SalesOrderDeliveryExceptionSemantics.DeliveryUnknown),
        };

        return new SalesOrderDeliveryExceptionReport
        {
            AsOfDate = asOf,
            CustomerId = query.CustomerId,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = query.PageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)query.PageSize),
            Counts = counts,
            Items = items,
        };
    }
    /// <summary>报表筛选（只读）：客户 / 关键字；as-of 只是判定基准，不用于过滤订单日期。</summary>
    private static IQueryable<SalesOrder> ApplyFilters(IErpDbContext db, SalesOrderDeliveryExceptionQuery query)
    {
        var source = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);
        if (query.CustomerId.HasValue) source = source.Where(o => o.CustomerId == query.CustomerId.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword;
            source = source.Where(o =>
                o.OrderNo.Contains(keyword) || o.ContractNo.Contains(keyword) || o.CustomerPoNo.Contains(keyword));
        }
        return source;
    }

    private static SalesOrderDeliveryExceptionItem BuildItem(SalesOrder order,
        IReadOnlyDictionary<long, List<SalesOrderDetail>> detailsByOrder,
        IReadOnlyDictionary<long, string> customerNames,
        IReadOnlyDictionary<long, SalesOrderProgressResult> derived,
        DateTime asOf)
    {
        // 明细行交期优先：取最早明细交期；无明细交期时回落订单头交期；两者都缺则为未知。
        var orderDetails = detailsByOrder.TryGetValue(order.Id, out var ds) ? ds : new List<SalesOrderDetail>();
        var lineDates = orderDetails
            .Where(d => d.DeliveryDate.HasValue)
            .OrderBy(d => d.DeliveryDate)
            .Select(d => new SalesOrderLineDeliveryDate
            {
                DetailId = d.Id,
                ProductId = d.ProductId,
                ProductName = d.ProductName,
                Spec = d.Spec,
                DeliveryDate = d.DeliveryDate!.Value.Date,
            })
            .ToList();
        var effectiveDate = lineDates.Count > 0 ? lineDates[0].DeliveryDate : order.DeliveryDate?.Date;

        var hasResult = derived.TryGetValue(order.Id, out var result);
        var summary = hasResult ? result!.Shipment : null;
        var truncated = summary?.Truncated ?? true;
        var shipmentStatus = summary?.ShipmentStatus ?? SalesOrderProgress.ShipmentNone;

        var fulfilledComplete = !truncated
            && shipmentStatus is SalesOrderProgress.ShipmentComplete or SalesOrderProgress.ShipmentOver;

        var (deliveryStatus, note) = DeriveState(asOf, effectiveDate, fulfilledComplete, truncated);

        return new SalesOrderDeliveryExceptionItem
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            CustomerId = order.CustomerId,
            CustomerName = customerNames.TryGetValue(order.CustomerId, out var name) ? name : string.Empty,
            Currency = order.Currency.ToString(),
            Status = order.Status.ToString(),
            HeaderDeliveryDate = order.DeliveryDate?.Date,
            LineDeliveryDates = lineDates,
            EffectiveDeliveryDate = effectiveDate,
            OrderedQuantity = truncated ? null : summary?.OrderedQuantity,
            ApprovedShippedQuantity = truncated ? null : summary?.ShippedQuantity,
            OutstandingQuantity = truncated ? null : summary?.OutstandingQuantity,
            PendingQuantity = truncated ? null : summary?.PendingQuantity,
            ShipmentStatus = shipmentStatus,
            DeliveryStatus = deliveryStatus,
            Note = note,
        };
    }

    /// <summary>
    /// 交期状态派生（纯函数，便于单测日期边界）。判定顺序固定：出货证据不完整未知 → 已出齐 → 缺日期未知 → 逾期 / 即将到期 / 在途。
    /// 部分出货仍视为未出齐、继续按日期判定；超发（over_shipped）未出数量为 0，视为已出齐。
    /// </summary>
    internal static (string Status, string Note) DeriveState(DateTime asOf, DateTime? effectiveDate,
        bool fulfilledComplete, bool truncated)
    {
        if (truncated)
            return (SalesOrderDeliveryExceptionSemantics.DeliveryUnknown,
                "出货证据超过单次派生上限，数量不完整，无法判定交期状态（未知，绝不当作 0 或正常）");

        if (fulfilledComplete)
            return (SalesOrderDeliveryExceptionSemantics.DeliveryFulfilled,
                "已审核且未删除的销售出库已覆盖订单数量（未出数量为 0）");

        if (effectiveDate is null)
            return (SalesOrderDeliveryExceptionSemantics.DeliveryUnknown,
                "缺少订单交货日期与明细交货日期，无法判断");

        if (effectiveDate.Value < asOf)
            return (SalesOrderDeliveryExceptionSemantics.DeliveryOverdue,
                $"交期（{effectiveDate.Value:yyyy-MM-dd}）已过 as-of（{asOf:yyyy-MM-dd}），仍未出齐");

        if (effectiveDate.Value <= asOf.AddDays(SalesOrderDeliveryExceptionSemantics.DueSoonDays))
            return (SalesOrderDeliveryExceptionSemantics.DeliveryDueSoon,
                $"交期（{effectiveDate.Value:yyyy-MM-dd}）临近（as-of 后 {SalesOrderDeliveryExceptionSemantics.DueSoonDays} 天内），仍未出齐");

        return (SalesOrderDeliveryExceptionSemantics.DeliveryOnSchedule,
            $"交期（{effectiveDate.Value:yyyy-MM-dd}）未到，未出齐但尚未进入异常窗口");
    }
}




