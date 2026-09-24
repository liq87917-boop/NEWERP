using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商采购敞口报表口径常量（ERP-031）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免各处自行拼写导致「未链接敞口」被当成应付款余额、或未知金额被当成 0。
/// </summary>
public static class SupplierPurchaseExposureSemantics
{
    /// <summary>链接状态：既有引用可用（付款单 → 货款申请单 → 本单归属销售订单，且该销售订单下只有本单一张采购订单）</summary>
    public const string LinkLinked = PurchaseOrderProgress.LinkLinked;

    /// <summary>链接状态：既有引用无法唯一归属（归属销售订单下存在多张采购订单），金额未知（不推断）</summary>
    public const string LinkAmbiguous = PurchaseOrderProgress.LinkAmbiguous;

    /// <summary>链接状态：不存在可用的既有引用（未关联归属销售订单 / 未维护供应商），金额未知（不推断）</summary>
    public const string LinkUnavailable = PurchaseOrderProgress.LinkUnavailable;

    /// <summary>收货状态：本次派生命中单次查询上限，收货数量不完整（未知，不等于 0）</summary>
    public const string ReceiptUnknown = "unknown";

    /// <summary>口径说明（界面与文档同源）</summary>
    public const string RuleText =
        "本报表是采购订单的运营敞口视图（只读派生）：分组 = 供应商 + 币种，不同币种分别汇总、绝不合并为一个金额、也不做汇率换算；" +
        "订单金额取采购订单已落库总额；链接状态与已结算金额复用「采购订单执行进度 / 订单财务核对」的同一套权威引用规则——" +
        "付款单经货款申请单引用本单归属销售订单，且该销售订单下只有本单一张采购订单、付款单供应商与本单一致，仅「已审核且币种一致」的付款单计入已结算；" +
        "未结算金额只在链接可用时计算（未结算 = 订单金额 − 已结算，下限 0），链接不唯一或无可用引用时为未知（null），不用 0 顶替；" +
        "链接不唯一 / 缺失的订单金额只作「未链接敞口」单列，不并入任何权威已结算合计；" +
        "收货数量只按「以本单为来源、未删除、已审核」的采购入库明细派生（与执行进度同口径，入库明细已折算基础单位），" +
        "待提交 / 已提交入库单不计入已收，本次页内入库单超过单次派生上限时收货数量记为未知。";

    /// <summary>与应付账款台账 / 账龄表的边界说明（界面与文档同源）</summary>
    public const string PayableDisclaimerText =
        "本报表不是应付账款台账、也不是账龄表：不创建发票或应付记录、不推算账期与到期日、不做账龄分摊，" +
        "不按供应商汇总口径或备注文本猜测单据链接；「未链接敞口」只是尚未按既有引用归属到订单的订单金额，不得当作应付余额或据以付款。";

    /// <summary>范围说明：合计与计数只统计本次返回页的订单（分页有界）</summary>
    public const string ScopeNoteText =
        "以下汇总与计数只统计本次返回页的订单；total 为符合筛选条件的未删除采购订单总数；" +
        "分页按供应商 + 币种 + 订单日期 + 单据 Id 稳定排序。";

    /// <summary>链接状态是否属于「未链接敞口」（不唯一 / 无引用），用于界面与测试统一判断</summary>
    public static bool IsUnlinked(string linkStatus)
        => linkStatus is LinkAmbiguous or LinkUnavailable;
}

/// <summary>供应商采购敞口报表查询条件（全部为只读筛选参数）</summary>
public sealed class SupplierPurchaseExposureQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>币种筛选（枚举名，如 CNY / USD；留空 = 全部币种）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>链接状态筛选（linked / ambiguous / unavailable；留空 = 全部）</summary>
    public string? LinkStatus { get; set; }

    /// <summary>关键字（匹配采购单号 / 采购合同号 / 归属销售订单号；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>解析后的币种（供数据库筛选使用；null = 全部币种）</summary>
    internal Currency? CurrencyValue { get; private set; }

    /// <summary>
    /// 归一化并校验：币种 / 链接状态必须是既定取值，订单日期区间不得倒置，分页参数钳制到有界范围；
    /// 非法取值直接抛业务异常（参数错误），不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        if (SupplierId is <= 0) SupplierId = null;

        CurrencyValue = null;
        if (!string.IsNullOrWhiteSpace(Currency))
        {
            if (!Enum.TryParse<Currency>(Currency.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw BusinessException.InvalidParameter(
                    $"币种无效：{Currency}（应为 CNY / USD / EUR / HKD / GBP / JPY）");
            }

            CurrencyValue = parsed;
            Currency = parsed.ToString();
        }
        else
        {
            Currency = null;
        }

        if (!string.IsNullOrWhiteSpace(LinkStatus))
        {
            var status = LinkStatus.Trim().ToLowerInvariant();
            if (status != SupplierPurchaseExposureSemantics.LinkLinked
                && status != SupplierPurchaseExposureSemantics.LinkAmbiguous
                && status != SupplierPurchaseExposureSemantics.LinkUnavailable)
            {
                throw BusinessException.InvalidParameter(
                    $"链接状态无效：{LinkStatus}（应为 linked / ambiguous / unavailable）");
            }

            LinkStatus = status;
        }
        else
        {
            LinkStatus = null;
        }

        OrderDateFrom = OrderDateFrom?.Date;
        OrderDateTo = OrderDateTo?.Date;
        if (OrderDateFrom.HasValue && OrderDateTo.HasValue && OrderDateFrom > OrderDateTo)
        {
            throw BusinessException.InvalidParameter(
                $"订单日期开始 {OrderDateFrom:yyyy-MM-dd} 不能晚于结束 {OrderDateTo:yyyy-MM-dd}");
        }

        if (Keyword is not null)
        {
            Keyword = Keyword.Trim();
            if (Keyword.Length == 0) Keyword = null;
        }

        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
    }
}

/// <summary>供应商采购敞口报表的单张采购订单行（只读派生；金额未知一律用 null 表达，绝不回落为 0）</summary>
public sealed class SupplierPurchaseExposureOrder
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public string Status { get; init; } = string.Empty;

    public long SupplierId { get; init; }

    /// <summary>供应商名称（供应商资料缺失或已删除时为空串，不臆造）</summary>
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>归属销售订单号（结算引用链的入口；未关联时为空串）</summary>
    public string OwningSalesOrderNo { get; init; } = string.Empty;

    /// <summary>订单已落库的结算进度文本（人工维护，非派生值；原样回显）</summary>
    public string RecordedSettlementProgress { get; init; } = string.Empty;

    /// <summary>订单金额（采购订单已落库总额，采购订单唯一权威算法：Σ 明细数量 × 单价）</summary>
    public decimal OrderedAmount { get; init; }

    /// <summary>链接状态：linked / ambiguous / unavailable（与执行进度、财务核对同一套派生规则）</summary>
    public string LinkStatus { get; init; } = SupplierPurchaseExposureSemantics.LinkUnavailable;

    /// <summary>链接状态说明（缺什么、为什么不推断；与派生逻辑同源）</summary>
    public string LinkReason { get; init; } = string.Empty;

    /// <summary>已结算金额（仅链接可用且付款单已审核、币种一致）；null = 未知（无可用链接），不等于 0</summary>
    public decimal? SettledAmount { get; init; }

    /// <summary>未结算金额；仅链接可用时给出，null = 未知（无可用链接），不等于「全部未结算」</summary>
    public decimal? OutstandingAmount { get; init; }

    /// <summary>已提交 / 待提交付款单金额（同口径）；null = 未知</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>是否超付（已结算 &gt; 订单金额，按金额容差判定）</summary>
    public bool OverSettled { get; init; }

    /// <summary>收货状态：none / partial / complete / over_received；unknown = 本次派生命中上限（数量不完整）</summary>
    public string ReceiptStatus { get; init; } = PurchaseOrderProgress.ReceiptNone;

    /// <summary>订单数量合计；null = 未知（收货派生命中上限）</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>已收数量合计（已审核入库明细口径）；null = 未知</summary>
    public decimal? ReceivedQuantity { get; init; }

    /// <summary>未收数量合计；null = 未知（不代表全都未收）</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>待审核入库数量合计（不计入已收）；null = 未知</summary>
    public decimal? PendingQuantity { get; init; }

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不作为应付余额）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一个「供应商 + 币种」分组（同分组才能安全汇总金额；不同供应商或不同币种绝不合并）。
/// 链接不唯一的订单金额只计入 <see cref="UnlinkedOrderedAmount"/>，不并入任何权威已结算 / 未结算合计。
/// </summary>
public sealed class SupplierPurchaseExposureGroup
{
    public long SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本分组的订单数</summary>
    public int OrderCount { get; init; }

    /// <summary>本分组订单金额合计（同供应商 + 同币种，可安全汇总）</summary>
    public decimal OrderedAmount { get; init; }

    /// <summary>链接可用的订单数</summary>
    public int LinkedOrderCount { get; init; }

    /// <summary>链接不唯一的订单数（金额未知，不推断）</summary>
    public int AmbiguousOrderCount { get; init; }

    /// <summary>无可用引用的订单数（金额未知，不推断）</summary>
    public int UnlinkedOrderCount { get; init; }

    /// <summary>链接可用订单的订单金额合计</summary>
    public decimal LinkedOrderedAmount { get; init; }

    /// <summary>链接可用订单的已结算金额合计；null = 本分组没有链接可用的订单（未知，不是 0）</summary>
    public decimal? SettledAmount { get; init; }

    /// <summary>链接可用订单的未结算金额合计；null = 本分组没有链接可用的订单（未知，不是「全部未结算」）</summary>
    public decimal? OutstandingAmount { get; init; }

    /// <summary>链接可用订单的已提交 / 待提交付款金额合计；null = 未知</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>链接不唯一 / 无引用订单的订单金额合计（仅单列，不并入合计，绝不作为应付余额）</summary>
    public decimal UnlinkedOrderedAmount { get; init; }

    /// <summary>本分组超付订单数</summary>
    public int OverSettledOrderCount { get; init; }

    /// <summary>本分组的订单行（与本页顺序一致）</summary>
    public List<SupplierPurchaseExposureOrder> Orders { get; init; } = new();
}


/// <summary>
/// 本页按币种的暴露汇总：金额只在同一币种内汇总，不同币种分别成行，绝不折算成一个总额。
/// </summary>
public sealed class SupplierPurchaseExposureCurrencySummary
{
    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    public int SupplierCount { get; init; }
    public int OrderCount { get; init; }

    /// <summary>本币种订单金额合计</summary>
    public decimal OrderedAmount { get; init; }

    public int LinkedOrderCount { get; init; }
    public int AmbiguousOrderCount { get; init; }
    public int UnlinkedOrderCount { get; init; }

    /// <summary>链接可用订单的订单金额合计</summary>
    public decimal LinkedOrderedAmount { get; init; }

    /// <summary>链接可用订单的已结算金额合计；null = 本币种没有链接可用的订单（未知，不是 0）</summary>
    public decimal? SettledAmount { get; init; }

    /// <summary>链接可用订单的未结算金额合计；null = 未知（不是「全部未结算」）</summary>
    public decimal? OutstandingAmount { get; init; }

    /// <summary>链接不唯一 / 无引用订单的订单金额合计（仅单列，不作为应付余额）</summary>
    public decimal UnlinkedOrderedAmount { get; init; }
}

/// <summary>供应商采购敞口报表（ERP-031，只读派生；不落库、不改单据状态、不新增或修改任何表列）</summary>
public sealed class SupplierPurchaseExposureReport
{
    // ============ 筛选回显（归一化后的实际取值） ============
    public long? SupplierId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateTime? OrderDateFrom { get; init; }
    public DateTime? OrderDateTo { get; init; }
    public string LinkStatus { get; init; } = string.Empty;
    public string Keyword { get; init; } = string.Empty;

    // ============ 分页 ============
    /// <summary>符合筛选条件的未删除采购订单总数</summary>
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>本页订单数</summary>
    public int PageOrderCount { get; init; }

    /// <summary>本页链接可用的订单数</summary>
    public int LinkedOrderCount { get; init; }

    /// <summary>本页链接不唯一的订单数（金额未知）</summary>
    public int AmbiguousOrderCount { get; init; }

    /// <summary>本页无可用引用的订单数（金额未知）</summary>
    public int UnlinkedOrderCount { get; init; }

    /// <summary>本页收货数量未知的订单数（入库单数量超过单次派生上限）</summary>
    public int ReceiptUnknownCount { get; init; }

    /// <summary>本页按币种汇总（同一币种才汇总；不存在跨币种总额）</summary>
    public List<SupplierPurchaseExposureCurrencySummary> Currencies { get; init; } = new();

    /// <summary>本页「供应商 + 币种」分组</summary>
    public List<SupplierPurchaseExposureGroup> Groups { get; init; } = new();

    /// <summary>口径说明（与后端派生同源；界面原样展示）</summary>
    public string Rule { get; init; } = SupplierPurchaseExposureSemantics.RuleText;

    /// <summary>范围说明（合计只统计本页）</summary>
    public string ScopeNote { get; init; } = SupplierPurchaseExposureSemantics.ScopeNoteText;

    /// <summary>与应付账款台账 / 账龄表的边界说明（界面原样展示）</summary>
    public string PayableDisclaimer { get; init; } = SupplierPurchaseExposureSemantics.PayableDisclaimerText;
}


/// <summary>
/// 供应商采购敞口报表派生（ERP-031，只读）。
/// <para>范围：未删除的采购订单，按「供应商 + 币种」聚合；订单金额取订单已落库总额，不重算、不改单据。</para>
/// <para>收货 / 结算一律复用 <see cref="PurchaseOrderProgress"/> 的同一套权威口径（入库单 = 收货来源，付款单 →
/// 货款申请单 → 本单归属销售订单 = 结算来源），本报表不引入第二套匹配算法、不按文本或供应商口径猜测链接。</para>
/// <para>查询有界：固定次数查询（1 次计数 + 1 次分页 + 1 次本页订单明细 + 1 次供应商名 + 收货 2 次 + 结算 3 次），
/// 与页大小 / 行数无关，正常报表使用下不存在逐单查库；只读，不写库。</para>
/// </summary>
public static class SupplierPurchaseExposure
{
    /// <summary>派生分页报表（筛选 --&gt; 分页 --&gt; 批量派生，全程无逐单查询）</summary>
    public static async Task<SupplierPurchaseExposureReport> ForQueryAsync(
        IErpDbContext db, SupplierPurchaseExposureQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(o => o.SupplierId).ThenBy(o => o.Currency).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(o => o.Id)
            .ToListAsync();

        var items = new List<SupplierPurchaseExposureOrder>();
        if (pageIds.Count > 0)
        {
            // 分页已定：只为本页订单加载明细（避免对全部匹配订单 Include 明细），再按分页顺序还原
            var pageOrders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
                .Where(o => pageIds.Contains(o.Id))
                .ToListAsync();
            var byId = pageOrders.ToDictionary(o => o.Id);
            var ordered = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            items = await BuildItemsAsync(db, ordered);
        }

        var groups = BuildGroups(items);
        var currencies = BuildCurrencySummaries(items);

        return new SupplierPurchaseExposureReport
        {
            SupplierId = query.SupplierId,
            Currency = query.Currency ?? string.Empty,
            OrderDateFrom = query.OrderDateFrom,
            OrderDateTo = query.OrderDateTo,
            LinkStatus = query.LinkStatus ?? string.Empty,
            Keyword = query.Keyword ?? string.Empty,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            PageOrderCount = items.Count,
            LinkedOrderCount = items.Count(i => i.LinkStatus == SupplierPurchaseExposureSemantics.LinkLinked),
            AmbiguousOrderCount = items.Count(i => i.LinkStatus == SupplierPurchaseExposureSemantics.LinkAmbiguous),
            UnlinkedOrderCount = items.Count(i => i.LinkStatus == SupplierPurchaseExposureSemantics.LinkUnavailable),
            ReceiptUnknownCount = items.Count(i => i.ReceiptStatus == SupplierPurchaseExposureSemantics.ReceiptUnknown),
            Currencies = currencies,
            Groups = groups,
        };
    }

    /// <summary>
    /// 本页订单行派生：收货 / 结算复用 <see cref="PurchaseOrderProgress"/> 的批量派生（固定查询次数，无逐单查库），
    /// 供应商名称只按本页供应商 Id 批量解析（资料缺失或已删除时留空，不臆造）。
    /// </summary>
    private static async Task<List<SupplierPurchaseExposureOrder>> BuildItemsAsync(IErpDbContext db,
        List<PurchaseOrder> orders)
    {
        var supplierIds = orders.Select(o => o.SupplierId).Distinct().ToList();
        var supplierNames = (await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id) && !s.IsDeleted)
                .Select(s => new { s.Id, s.SupplierName })
                .ToListAsync())
            .ToDictionary(s => s.Id, s => s.SupplierName);

        var receipts = await PurchaseOrderProgress.ReceiptSummariesForOrdersAsync(db, orders);
        var settlements = await PurchaseOrderProgress.SettlementForOrdersAsync(db, orders);

        var items = new List<SupplierPurchaseExposureOrder>();
        foreach (var order in orders)
        {
            receipts.TryGetValue(order.Id, out var receipt);
            settlements.TryGetValue(order.Id, out var settlement);
            var receiptUnknown = receipt?.Truncated == true;
            var linkStatus = settlement?.LinkStatus ?? SupplierPurchaseExposureSemantics.LinkUnavailable;

            items.Add(new SupplierPurchaseExposureOrder
            {
                OrderId = order.Id,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                Status = order.Status.ToString(),
                SupplierId = order.SupplierId,
                SupplierName = supplierNames.TryGetValue(order.SupplierId, out var name) ? name : string.Empty,
                Currency = order.Currency.ToString(),
                OwningSalesOrderNo = order.OwningSalesOrderNo,
                RecordedSettlementProgress = order.SettlementProgress,
                OrderedAmount = order.TotalAmount,
                LinkStatus = linkStatus,
                LinkReason = settlement?.LinkReason ?? string.Empty,
                SettledAmount = settlement?.SettledAmount,
                OutstandingAmount = settlement?.OutstandingAmount,
                SubmittedAmount = settlement?.SubmittedAmount,
                OverSettled = settlement?.OverSettled ?? false,
                ReceiptStatus = receiptUnknown
                    ? SupplierPurchaseExposureSemantics.ReceiptUnknown
                    : receipt?.ReceiptStatus ?? PurchaseOrderProgress.ReceiptNone,
                OrderedQuantity = receiptUnknown ? null : receipt?.OrderedQuantity,
                ReceivedQuantity = receiptUnknown ? null : receipt?.ReceivedQuantity,
                OutstandingQuantity = receiptUnknown ? null : receipt?.OutstandingQuantity,
                PendingQuantity = receiptUnknown ? null : receipt?.PendingQuantity,
                Note = BuildOrderNote(linkStatus, receiptUnknown),
            });
        }

        return items;
    }

    /// <summary>行级说明：只写「已知什么 / 缺什么 / 为什么不作为应付余额」，与派生逻辑同源</summary>
    private static string BuildOrderNote(string linkStatus, bool receiptUnknown)
    {
        var note = linkStatus switch
        {
            SupplierPurchaseExposureSemantics.LinkLinked =>
                "已按权威引用归属：付款单 → 货款申请单 → 本单归属销售订单；未结算金额 = 订单金额 − 已结算金额。",
            SupplierPurchaseExposureSemantics.LinkAmbiguous =>
                "结算链接不唯一（归属销售订单下存在多张采购订单）：本单金额只作「未链接敞口」单列，" +
                "结算与未结算金额均为未知（不用 0 顶替），不作为应付余额。",
            _ => "无可用结算链接（未关联归属销售订单或未维护供应商）：本单金额只作「未链接敞口」单列，" +
                 "结算金额未知（不做推断），不作为应付余额。",
        };
        if (receiptUnknown)
        {
            note += "本次页内入库单数量超过单次派生上限，收货数量不完整，已按未知显示。";
        }

        return note;
    }

    /// <summary>
    /// 数据库侧筛选（全部为既有列条件）。
    /// <para>链接状态是派生值，这里用与派生规则等价的既有列条件筛选，避免为了筛选而做无界内存过滤：
    /// 未关联归属销售订单 / 未维护供应商 = unavailable；归属销售订单下存在另一张未删除采购订单 = ambiguous；其余 = linked。</para>
    /// </summary>
    private static IQueryable<PurchaseOrder> ApplyFilters(IErpDbContext db, SupplierPurchaseExposureQuery query)
    {
        var source = db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);
        if (query.SupplierId.HasValue) source = source.Where(o => o.SupplierId == query.SupplierId.Value);
        if (query.CurrencyValue is { } currency) source = source.Where(o => o.Currency == currency);
        if (query.OrderDateFrom is { } from) source = source.Where(o => o.OrderDate >= from);
        if (query.OrderDateTo is { } to)
        {
            var toExclusive = to.AddDays(1);
            source = source.Where(o => o.OrderDate < toExclusive);
        }

        if (query.Keyword is { } keyword)
        {
            source = source.Where(o => o.OrderNo.Contains(keyword) || o.ContractNo.Contains(keyword)
                                       || o.OwningSalesOrderNo.Contains(keyword));
        }

        if (query.LinkStatus == SupplierPurchaseExposureSemantics.LinkUnavailable)
        {
            source = source.Where(o => o.OwningSalesOrderId == null || o.OwningSalesOrderId <= 0 || o.SupplierId <= 0);
        }
        else if (query.LinkStatus == SupplierPurchaseExposureSemantics.LinkAmbiguous)
        {
            source = source.Where(o => o.OwningSalesOrderId != null && o.OwningSalesOrderId > 0 && o.SupplierId > 0
                && db.PurchaseOrders.Any(x => !x.IsDeleted && x.Id != o.Id && x.OwningSalesOrderId == o.OwningSalesOrderId));
        }
        else if (query.LinkStatus == SupplierPurchaseExposureSemantics.LinkLinked)
        {
            source = source.Where(o => o.OwningSalesOrderId != null && o.OwningSalesOrderId > 0 && o.SupplierId > 0
                && !db.PurchaseOrders.Any(x => !x.IsDeleted && x.Id != o.Id && x.OwningSalesOrderId == o.OwningSalesOrderId));
        }

        return source;
    }

    /// <summary>按「供应商 + 币种」分组：只有同分组（同供应商同币种）才汇总金额</summary>
    private static List<SupplierPurchaseExposureGroup> BuildGroups(List<SupplierPurchaseExposureOrder> items)
        => items.GroupBy(i => new { i.SupplierId, i.Currency })
            .OrderBy(g => g.Key.SupplierId).ThenBy(g => g.Key.Currency)
            .Select(g =>
            {
                var linked = g
                    .Where(i => i.LinkStatus == SupplierPurchaseExposureSemantics.LinkLinked)
                    .ToList();
                var orderedAmount = g.Sum(i => i.OrderedAmount);
                var linkedOrderedAmount = linked.Sum(i => i.OrderedAmount);
                return new SupplierPurchaseExposureGroup
                {
                    SupplierId = g.Key.SupplierId,
                    SupplierName = g.Select(i => i.SupplierName).FirstOrDefault(s => s.Length > 0) ?? string.Empty,
                    Currency = g.Key.Currency,
                    OrderCount = g.Count(),
                    OrderedAmount = orderedAmount,
                    LinkedOrderCount = linked.Count,
                    AmbiguousOrderCount = g.Count(i =>
                        i.LinkStatus == SupplierPurchaseExposureSemantics.LinkAmbiguous),
                    UnlinkedOrderCount = g.Count(i =>
                        i.LinkStatus == SupplierPurchaseExposureSemantics.LinkUnavailable),
                    LinkedOrderedAmount = linkedOrderedAmount,
                    SettledAmount = linked.Count == 0 ? null : linked.Sum(i => i.SettledAmount ?? 0m),
                    OutstandingAmount = linked.Count == 0 ? null : linked.Sum(i => i.OutstandingAmount ?? 0m),
                    SubmittedAmount = linked.Count == 0 ? null : linked.Sum(i => i.SubmittedAmount ?? 0m),
                    UnlinkedOrderedAmount = g
                        .Where(i => SupplierPurchaseExposureSemantics.IsUnlinked(i.LinkStatus))
                        .Sum(i => i.OrderedAmount),
                    OverSettledOrderCount = linked.Count(i => i.OverSettled),
                    Orders = g.ToList(),
                };
            })
            .ToList();

    /// <summary>本页按币种汇总：同一币种内汇总，不同币种分别成行（不做汇率换算、不产生跨币种总额）</summary>
    private static List<SupplierPurchaseExposureCurrencySummary> BuildCurrencySummaries(
        List<SupplierPurchaseExposureOrder> items)
        => items.GroupBy(i => i.Currency)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var linked = g
                    .Where(i => i.LinkStatus == SupplierPurchaseExposureSemantics.LinkLinked)
                    .ToList();
                var orderedAmount = g.Sum(i => i.OrderedAmount);
                var linkedOrderedAmount = linked.Sum(i => i.OrderedAmount);
                return new SupplierPurchaseExposureCurrencySummary
                {
                    Currency = g.Key,
                    SupplierCount = g.Select(i => i.SupplierId).Distinct().Count(),
                    OrderCount = g.Count(),
                    OrderedAmount = orderedAmount,
                    LinkedOrderCount = linked.Count,
                    AmbiguousOrderCount = g.Count(i =>
                        i.LinkStatus == SupplierPurchaseExposureSemantics.LinkAmbiguous),
                    UnlinkedOrderCount = g.Count(i =>
                        i.LinkStatus == SupplierPurchaseExposureSemantics.LinkUnavailable),
                    LinkedOrderedAmount = linkedOrderedAmount,
                    SettledAmount = linked.Count == 0 ? null : linked.Sum(i => i.SettledAmount ?? 0m),
                    OutstandingAmount = linked.Count == 0 ? null : linked.Sum(i => i.OutstandingAmount ?? 0m),
                    UnlinkedOrderedAmount = g
                        .Where(i => SupplierPurchaseExposureSemantics.IsUnlinked(i.LinkStatus))
                        .Sum(i => i.OrderedAmount),
                };
            })
            .ToList();
}

