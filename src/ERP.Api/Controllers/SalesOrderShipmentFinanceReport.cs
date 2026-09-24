using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单出货 / 财务进度报表口径常量（ERP-032）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免各处自行拼写导致「无权威引用的收款」被当成已收 0、或未知数量被当成 0。
/// </summary>
public static class SalesOrderShipmentFinanceSemantics
{
    // ---------- 出货状态（与后端 SalesOrderProgress 一一对应） ----------
    /// <summary>未出货</summary>
    public const string ShipmentNone = SalesOrderProgress.ShipmentNone;

    /// <summary>部分出货</summary>
    public const string ShipmentPartial = SalesOrderProgress.ShipmentPartial;

    /// <summary>已出齐</summary>
    public const string ShipmentComplete = SalesOrderProgress.ShipmentComplete;

    /// <summary>超发</summary>
    public const string ShipmentOver = SalesOrderProgress.ShipmentOver;

    /// <summary>未知（命中单次派生上限）</summary>
    public const string ShipmentUnknown = SalesOrderProgress.ShipmentUnknown;

    // ---------- 出货状态筛选（只提供可用既有列条件精确表达的两档，避免筛选口径与派生口径分叉） ----------
    /// <summary>筛选：无「以本单为来源、未删除、已审核」的销售出库单</summary>
    public const string ShipmentFilterNone = "none";

    /// <summary>筛选：存在「以本单为来源、未删除、已审核」的销售出库单（部分 / 齐 / 超由派生状态区分）</summary>
    public const string ShipmentFilterShipped = "shipped";

    // ---------- 收款链接状态（与后端 SalesOrderProgress 一一对应） ----------
    /// <summary>权威引用完整</summary>
    public const string FinanceLinked = SalesOrderProgress.LinkLinked;

    /// <summary>部分可归属（他币种 / 未审核 / 非已审核记录仅列出）</summary>
    public const string FinancePartial = SalesOrderProgress.LinkPartial;

    /// <summary>无可用权威引用（金额未知，不用 0 顶替）</summary>
    public const string FinanceUnlinked = SalesOrderProgress.LinkUnlinked;

    /// <summary>未知（命中派生上限）</summary>
    public const string FinanceUnknown = SalesOrderProgress.LinkUnknown;

    /// <summary>口径说明（界面与文档同源）</summary>
    public const string RuleText = SalesOrderProgress.RuleText;

    /// <summary>与应收账款台账 / 账龄表的边界说明（界面与文档同源）</summary>
    public const string ReceivableDisclaimerText =
        "本报表是销售订单的出货 / 收款链接视图（只读派生）：不是应收账款台账，也不是账龄表 —— 不创建发票或应收记录、" +
        "不推算账期与到期日、不做账龄分摊，也不按客户汇总口径或备注文本猜测单据链接；" +
        "「未覆盖金额」只是订单金额与权威计入金额之差，不得当作应收余额或据以催收。";

    /// <summary>范围说明：合计与计数只统计本次返回页的订单（分页有界）</summary>
    public const string ScopeNoteText =
        "以下汇总与计数只统计本次返回页的订单；total 为符合筛选条件的未删除销售订单总数；" +
        "分页按客户 + 币种 + 订单日期 + 单据 Id 稳定排序。";

    /// <summary>是否属于「未按权威引用完整归属」（部分可归属 / 无可用引用），用于界面与测试统一判断</summary>
    public static bool IsUnlinked(string financeLinkStatus)
        => financeLinkStatus is FinancePartial or FinanceUnlinked;
}

/// <summary>销售订单出货 / 财务进度报表查询条件（全部为只读筛选参数）</summary>
public sealed class SalesOrderShipmentFinanceQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>客户筛选（留空 = 全部客户）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（枚举名，如 USD / CNY；留空 = 全部币种）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>出货状态筛选（none / shipped；留空 = 全部）</summary>
    public string? ShipmentStatus { get; set; }

    /// <summary>收款链接状态筛选（linked / partial / unlinked；留空 = 全部）</summary>
    public string? FinanceLinkStatus { get; set; }

    /// <summary>关键字（匹配订单号 / 外销合同号 / 客户 PO 号；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>解析后的币种（供数据库筛选使用；null = 全部币种）</summary>
    internal Currency? CurrencyValue { get; private set; }

    /// <summary>解析后的出货状态筛选值（none / shipped；null = 不筛选）</summary>
    internal string? ShipmentStatusValue { get; private set; }

    /// <summary>解析后的收款链接状态筛选值（linked / partial / unlinked；null = 不筛选）</summary>
    internal string? FinanceLinkStatusValue { get; private set; }

    /// <summary>
    /// 归一化并校验：币种 / 出货状态 / 收款链接状态必须是既定取值，订单日期区间不得倒置，分页参数钳制到有界范围；
    /// 非法取值直接抛业务异常（参数错误），不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        CurrencyValue = null;
        if (!string.IsNullOrWhiteSpace(Currency))
        {
            if (!Enum.TryParse<Currency>(Currency.Trim(), ignoreCase: true, out var currency))
                throw BusinessException.InvalidParameter(
                    "币种取值无效：只接受币种枚举名（CNY / USD / EUR / HKD / GBP / JPY）");
            CurrencyValue = currency;
        }

        ShipmentStatusValue = null;
        if (!string.IsNullOrWhiteSpace(ShipmentStatus))
        {
            var value = ShipmentStatus.Trim().ToLowerInvariant();
            if (value != SalesOrderShipmentFinanceSemantics.ShipmentFilterNone
                && value != SalesOrderShipmentFinanceSemantics.ShipmentFilterShipped)
                throw BusinessException.InvalidParameter(
                    "出货状态筛选取值无效：只接受 none（无已审核出库单）/ shipped（存在已审核出库单）");
            ShipmentStatusValue = value;
        }

        FinanceLinkStatusValue = null;
        if (!string.IsNullOrWhiteSpace(FinanceLinkStatus))
        {
            var value = FinanceLinkStatus.Trim().ToLowerInvariant();
            if (value != SalesOrderShipmentFinanceSemantics.FinanceLinked
                && value != SalesOrderShipmentFinanceSemantics.FinancePartial
                && value != SalesOrderShipmentFinanceSemantics.FinanceUnlinked)
                throw BusinessException.InvalidParameter(
                    "收款链接状态筛选取值无效：只接受 linked / partial / unlinked" +
                    "（unknown 只在命中派生上限时出现，无法用既有列条件等价表达）");
            FinanceLinkStatusValue = value;
        }

        if (OrderDateFrom.HasValue && OrderDateTo.HasValue && OrderDateFrom.Value > OrderDateTo.Value)
            throw BusinessException.InvalidParameter("订单日期区间无效：开始日期不能晚于结束日期");

        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        if (CustomerId is <= 0) CustomerId = null;
        Page = Page < 1 ? 1 : Page;
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize);
    }
}

/// <summary>
/// 报表中的一行销售订单：出货数量（已订 / 已出 / 待审 / 未出）与收款链接（状态 / 已关联 / 未覆盖 / 计数）。
/// 金额与数量为 null 一律表示未知（命中派生上限或无可用引用），绝不用 0 顶替。
/// </summary>
public sealed class SalesOrderShipmentFinanceOrder
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种（枚举名；金额只在本币种内汇总，绝不跨币种合并）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>订单金额（订单主表已落库总额，报表不重算）</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>已落库定金金额（订单字段原样回显，不是派生的收款金额）</summary>
    public decimal RecordedDepositAmount { get; init; }

    /// <summary>订单数量合计；null = 未知（命中派生上限）</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>已出货数量合计（已审核出库明细口径）；null = 未知，不等于 0</summary>
    public decimal? ShippedQuantity { get; init; }

    /// <summary>待审核出库数量合计（不计入已出货）；null = 未知</summary>
    public decimal? PendingShipmentQuantity { get; init; }

    /// <summary>未出货数量合计；null = 未知（不代表全都未出）</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>出货状态：none / partial / complete / over_shipped / unknown</summary>
    public string ShipmentStatus { get; init; } = SalesOrderProgress.ShipmentNone;

    /// <summary>是否存在「以本单为来源、未删除、已审核」的销售出库单（出货状态筛选与此条件完全一致）</summary>
    public bool HasApprovedShipment { get; init; }

    /// <summary>以本单为来源的出库单数（未删除，含未审核）</summary>
    public int ShipmentDocumentCount { get; init; }

    /// <summary>以本单为来源且已审核的出库单数</summary>
    public int ApprovedShipmentCount { get; init; }

    /// <summary>收款链接状态：linked / partial / unlinked / unknown</summary>
    public string FinanceLinkStatus { get; init; } = SalesOrderProgress.LinkUnlinked;

    /// <summary>收款链接状态说明（缺什么、为什么不推断；与派生逻辑同源）</summary>
    public string FinanceLinkReason { get; init; } = string.Empty;

    /// <summary>已关联金额（已审核 + 同币种收款申请）；null = 未知（无权威引用或命中派生上限），不是 0</summary>
    public decimal? LinkedAmount { get; init; }

    /// <summary>未覆盖金额 = 订单金额 − 已关联金额；null = 未知；负数表示超过订单金额（超收）</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>已提交 / 待提交（未审核、同币种）申请金额；null = 未知，仅单列不计入</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>他币种记录数（指向本单但币种不一致：仅列出、不计入、不做汇率换算）</summary>
    public int OtherCurrencyRecordCount { get; init; }

    /// <summary>非「已审核」记录数（已驳回 / 已取消 / 已完成等：仅列出、不计入）</summary>
    public int UnapprovedRecordCount { get; init; }

    /// <summary>无法归属本单的客户级记录数（收款单 / 装柜结算单 / 散货结算单只记录客户）</summary>
    public int UnattributedRecordCount { get; init; }

    /// <summary>是否超收（已关联金额 &gt; 订单金额）</summary>
    public bool OverReceived { get; init; }

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不能当作应收余额）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一个「客户 + 币种」分组（同分组才能安全汇总金额；不同客户或不同币种绝不合并）。
/// 已关联 / 未覆盖金额只汇总本分组内金额已知的订单；数量只在全部已知时才给合计（否则 null = 未知）。
/// </summary>
public sealed class SalesOrderShipmentFinanceGroup
{
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本分组的订单数</summary>
    public int OrderCount { get; init; }

    /// <summary>本分组订单金额合计（同客户 + 同币种，可安全汇总）</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>收款链接完整（linked）的订单数</summary>
    public int LinkedOrderCount { get; init; }

    /// <summary>部分可归属（partial）的订单数</summary>
    public int PartialOrderCount { get; init; }

    /// <summary>无可用权威引用（unlinked）的订单数（金额未知，不推断）</summary>
    public int UnlinkedOrderCount { get; init; }

    /// <summary>命中派生上限（unknown）的订单数（金额未知，不推断）</summary>
    public int UnknownFinanceOrderCount { get; init; }

    /// <summary>已出货（存在已审核出库单）的订单数</summary>
    public int ShippedOrderCount { get; init; }

    /// <summary>未出货（无已审核出库单）的订单数</summary>
    public int UnshippedOrderCount { get; init; }

    /// <summary>出货数量未知（命中派生上限）的订单数</summary>
    public int UnknownShipmentOrderCount { get; init; }

    /// <summary>已关联金额合计（仅金额已知的订单）；null = 本分组没有金额已知的订单（未知，不是 0）</summary>
    public decimal? LinkedAmount { get; init; }

    /// <summary>未覆盖金额合计（仅金额已知的订单）；null = 未知（不是「全部未收」）</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>已提交 / 待提交金额合计；null = 未知</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>订单数量合计（只统计本页本分组数量已知的行）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已出货数量合计；null = 本分组存在数量未知的订单（未知，不是 0）</summary>
    public decimal? ShippedQuantity { get; init; }

    /// <summary>未出货数量合计；null = 本分组存在数量未知的订单（未知，不是 0）</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>本分组超收订单数</summary>
    public int OverReceivedOrderCount { get; init; }

    /// <summary>本分组的订单行（与本页顺序一致）</summary>
    public List<SalesOrderShipmentFinanceOrder> Orders { get; init; } = new();
}

/// <summary>
/// 本页按币种的汇总：金额只在同一币种内汇总，不同币种分别成行，绝不折算成一个总额。
/// </summary>
public sealed class SalesOrderShipmentFinanceCurrencySummary
{
    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本币种涉及的客户数</summary>
    public int CustomerCount { get; init; }

    /// <summary>本币种订单数</summary>
    public int OrderCount { get; init; }

    /// <summary>本币种订单金额合计</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>收款链接完整的订单数</summary>
    public int LinkedOrderCount { get; init; }

    /// <summary>部分可归属的订单数</summary>
    public int PartialOrderCount { get; init; }

    /// <summary>无可用权威引用的订单数</summary>
    public int UnlinkedOrderCount { get; init; }

    /// <summary>命中派生上限（金额未知）的订单数</summary>
    public int UnknownFinanceOrderCount { get; init; }

    /// <summary>已出货订单数</summary>
    public int ShippedOrderCount { get; init; }

    /// <summary>未出货订单数</summary>
    public int UnshippedOrderCount { get; init; }

    /// <summary>出货数量未知的订单数</summary>
    public int UnknownShipmentOrderCount { get; init; }

    /// <summary>已关联金额合计（仅金额已知的订单）；null = 本币种没有金额已知的订单（未知，不是 0）</summary>
    public decimal? LinkedAmount { get; init; }

    /// <summary>未覆盖金额合计（仅金额已知的订单）；null = 未知（不是「全部未收」）</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>订单数量合计（只统计本页本币种数量已知的行）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已出货数量合计；null = 本币种存在数量未知的订单（未知，不是 0）</summary>
    public decimal? ShippedQuantity { get; init; }

    /// <summary>未出货数量合计；null = 本币种存在数量未知的订单（未知，不是 0）</summary>
    public decimal? OutstandingQuantity { get; init; }
}

/// <summary>
/// 销售订单出货 / 财务进度报表（ERP-032，只读派生；不落库、不改单据状态、不新增或修改任何表列）。
/// 报表只在本页范围内汇总，且金额按币种分别成行（不存在跨币种总额字段）。
/// </summary>
public sealed class SalesOrderShipmentFinanceReportView
{
    // ============ 筛选回显（归一化后的实际取值） ============
    public long? CustomerId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateTime? OrderDateFrom { get; init; }
    public DateTime? OrderDateTo { get; init; }

    /// <summary>出货状态筛选（none / shipped；空 = 全部）</summary>
    public string ShipmentStatus { get; init; } = string.Empty;

    /// <summary>收款链接状态筛选（linked / partial / unlinked；空 = 全部）</summary>
    public string FinanceLinkStatus { get; init; } = string.Empty;

    public string Keyword { get; init; } = string.Empty;

    // ============ 分页 ============
    public int Page { get; init; }
    public int PageSize { get; init; }

    /// <summary>符合筛选条件的未删除销售订单总数</summary>
    public int Total { get; init; }

    /// <summary>总页数（total = 0 时为 0）</summary>
    public int TotalPages { get; init; }

    /// <summary>本页返回的订单数</summary>
    public int PageOrderCount { get; init; }

    // ============ 本页计数（收款链接 / 出货） ============
    public int LinkedOrderCount { get; init; }
    public int PartialOrderCount { get; init; }
    public int UnlinkedOrderCount { get; init; }

    /// <summary>命中派生上限、金额未知的订单数（本页）</summary>
    public int UnknownFinanceOrderCount { get; init; }

    /// <summary>存在已审核出库单的订单数（本页）</summary>
    public int ShippedOrderCount { get; init; }

    /// <summary>无已审核出库单的订单数（本页）</summary>
    public int UnshippedOrderCount { get; init; }

    /// <summary>出货数量未知（命中派生上限）的订单数（本页）</summary>
    public int UnknownShipmentOrderCount { get; init; }

    // ============ 分组与币种汇总（只统计本页） ============
    /// <summary>「客户 + 币种」分组（本页）</summary>
    public List<SalesOrderShipmentFinanceGroup> Groups { get; init; } = new();

    /// <summary>本页按币种汇总（不同币种分别成行，不做汇率换算）</summary>
    public List<SalesOrderShipmentFinanceCurrencySummary> Currencies { get; init; } = new();

    // ============ 口径与边界（与派生逻辑同源） ============
    /// <summary>口径说明</summary>
    public string Rule { get; init; } = SalesOrderShipmentFinanceSemantics.RuleText;

    /// <summary>范围说明（合计只统计本页）</summary>
    public string ScopeNote { get; init; } = SalesOrderShipmentFinanceSemantics.ScopeNoteText;

    /// <summary>与应收账款台账 / 账龄表的边界声明</summary>
    public string ReceivableDisclaimer { get; init; } = SalesOrderShipmentFinanceSemantics.ReceivableDisclaimerText;
}

/// <summary>
/// 销售订单出货 / 财务进度报表派生（ERP-032，只读）。
/// <para><b>单一口径</b>：出货数量与收款链接全部转调 <see cref="SalesOrderProgress"/>（与逐单出货 / 收款进度同一套派生），
/// 报表不重复实现任何匹配规则，只做筛选、分组与汇总。</para>
/// <para><b>查询有界</b>：固定 11 次数据集访问（筛选 / 计数 / 分页共用的订单集合 + 本页订单 + 客户名，加上逐单派生的 8 次），
/// 与页大小、订单数、单据数无关，无逐单查库（无 N+1）；全程不写库。</para>
/// <para><b>状态筛选的口径一致性</b>：<c>shipmentStatus</c> 与 <c>financeLinkStatus</c> 用与派生规则等价的既有列条件在数据库侧筛选
/// （出货 = 是否存在「以本单为来源、未删除、已审核」的销售出库单；收款链接 = 是否存在指向本单的定金 / 货款申请单及其状态与币种），
/// 避免为筛选而在内存中做无界过滤，也避免两处口径分叉。</para>
/// </summary>
public static class SalesOrderShipmentFinanceReport
{
    /// <summary>出货 / 财务进度报表查询（GET /api/sales-orders/shipment-finance-report，只读、分页有界）</summary>
    public static async Task<SalesOrderShipmentFinanceReportView> ForQueryAsync(IErpDbContext db,
        SalesOrderShipmentFinanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        // 1~2 次：筛选 + 计数 + 本页 Id（分页按客户 + 币种 + 订单日期 + 单据 Id 稳定排序，翻页不重不漏）
        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)query.PageSize);
        var pageIds = await source
            .OrderBy(o => o.CustomerId).ThenBy(o => o.Currency).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => o.Id)
            .ToListAsync();

        // 3 次：本页订单（不含明细；明细由派生层自带一次有界查询，避免漏加载被当成 0）
        var pageOrders = pageIds.Count == 0
            ? new List<SalesOrder>()
            : await db.SalesOrders.AsNoTracking().Where(o => pageIds.Contains(o.Id)).ToListAsync();
        var orders = pageOrders
            .OrderBy(o => o.CustomerId).ThenBy(o => (int)o.Currency).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .ToList();

        // 4 次：客户名（仅用于展示，缺失时留空而不臆造）
        var customerIds = orders.Select(o => o.CustomerId).Where(c => c > 0).Distinct().ToList();
        var customerNames = customerIds.Count == 0
            ? new Dictionary<long, string>()
            : (await db.BaseCustomers.AsNoTracking()
                    .Where(c => customerIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.CustomerName })
                    .ToListAsync())
                .ToDictionary(c => c.Id, c => c.CustomerName);

        // 逐单派生：固定 8 次（订单明细 / 出库主表 / 出库明细 / 定金申请 / 货款申请 / 收款单 / 装柜结算 / 散货结算）
        var derived = (await SalesOrderProgress.ForOrdersAsync(db, orders)).ToDictionary(r => r.OrderId);
        var rows = orders.Select(o => BuildOrderRow(o, derived[o.Id], customerNames)).ToList();

        return new SalesOrderShipmentFinanceReportView
        {
            CustomerId = query.CustomerId,
            Currency = query.CurrencyValue?.ToString() ?? string.Empty,
            OrderDateFrom = query.OrderDateFrom,
            OrderDateTo = query.OrderDateTo,
            ShipmentStatus = query.ShipmentStatusValue ?? string.Empty,
            FinanceLinkStatus = query.FinanceLinkStatusValue ?? string.Empty,
            Keyword = query.Keyword ?? string.Empty,
            Page = query.Page,
            PageSize = query.PageSize,
            Total = total,
            TotalPages = totalPages,
            PageOrderCount = rows.Count,
            LinkedOrderCount = rows.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceLinked),
            PartialOrderCount = rows.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinancePartial),
            UnlinkedOrderCount = rows.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceUnlinked),
            UnknownFinanceOrderCount = rows.Count(r =>
                r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceUnknown),
            ShippedOrderCount = rows.Count(r => r.HasApprovedShipment),
            UnshippedOrderCount = rows.Count(r => !r.HasApprovedShipment),
            UnknownShipmentOrderCount = rows.Count(r =>
                r.ShipmentStatus == SalesOrderShipmentFinanceSemantics.ShipmentUnknown),
            Groups = BuildGroups(rows),
            Currencies = BuildCurrencySummaries(rows),
        };
    }

    /// <summary>
    /// 报表筛选（只读）：客户 / 币种 / 订单日期（含首尾当天）/ 关键字，以及用与派生规则等价的既有列条件表达的状态筛选。
    /// 不按备注文本、金额接近度或客户 / 供应商汇总口径猜测任何链接。
    /// </summary>
    private static IQueryable<SalesOrder> ApplyFilters(IErpDbContext db, SalesOrderShipmentFinanceQuery query)
    {
        var source = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);
        if (query.CustomerId.HasValue) source = source.Where(o => o.CustomerId == query.CustomerId.Value);
        if (query.CurrencyValue.HasValue) source = source.Where(o => o.Currency == query.CurrencyValue.Value);
        if (query.OrderDateFrom.HasValue) source = source.Where(o => o.OrderDate >= query.OrderDateFrom.Value);
        if (query.OrderDateTo.HasValue) source = source.Where(o => o.OrderDate <= query.OrderDateTo.Value);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // 关键字只匹配既有单号列（订单号 / 外销合同号 / 客户 PO 号）
            var keyword = query.Keyword;
            source = source.Where(o =>
                o.OrderNo.Contains(keyword) || o.ContractNo.Contains(keyword) || o.CustomerPoNo.Contains(keyword));
        }

        // 出货状态：与派生 HasApprovedShipment 完全一致的条件（存在 / 不存在已审核且未删除的出库单）
        if (query.ShipmentStatusValue == SalesOrderShipmentFinanceSemantics.ShipmentFilterNone)
        {
            source = source.Where(o => !db.StockOuts.Any(s =>
                !s.IsDeleted && s.SalesOrderId == o.Id && s.Status == DocumentStatus.Approved));
        }
        else if (query.ShipmentStatusValue == SalesOrderShipmentFinanceSemantics.ShipmentFilterShipped)
        {
            source = source.Where(o => db.StockOuts.Any(s =>
                !s.IsDeleted && s.SalesOrderId == o.Id && s.Status == DocumentStatus.Approved));
        }

        // 收款链接状态：与派生 linked / partial / unlinked 完全一致的条件（权威引用是否存在、是否全部「已审核 + 同币种」）
        if (query.FinanceLinkStatusValue == SalesOrderShipmentFinanceSemantics.FinanceUnlinked)
        {
            source = source.Where(o =>
                !db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                && !db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id));
        }
        else if (query.FinanceLinkStatusValue == SalesOrderShipmentFinanceSemantics.FinanceLinked)
        {
            source = source.Where(o =>
                (db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                    || db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id))
                && !db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                    && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))
                && !db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                    && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency)));
        }
        else if (query.FinanceLinkStatusValue == SalesOrderShipmentFinanceSemantics.FinancePartial)
        {
            source = source.Where(o =>
                (db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                    || db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id))
                && (db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                        && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))
                    || db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                        && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))));
        }

        return source;
    }

    /// <summary>报表行：数量未知一律 null（不回落为 0），收款金额未知一律 null（不回落到 0）</summary>
    private static SalesOrderShipmentFinanceOrder BuildOrderRow(SalesOrder order, SalesOrderProgressResult progress,
        IReadOnlyDictionary<long, string> customerNames)
    {
        var shipment = progress.Shipment;
        var finance = progress.Finance;
        var shipmentKnown = !shipment.Truncated;

        return new SalesOrderShipmentFinanceOrder
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            Status = order.Status.ToString(),
            CustomerId = order.CustomerId,
            CustomerName = customerNames.TryGetValue(order.CustomerId, out var name) ? name : string.Empty,
            Currency = order.Currency.ToString(),
            OrderAmount = order.TotalAmount,
            RecordedDepositAmount = order.DepositAmount,
            OrderedQuantity = shipmentKnown ? shipment.OrderedQuantity : null,
            ShippedQuantity = shipmentKnown ? shipment.ShippedQuantity : null,
            PendingShipmentQuantity = shipmentKnown ? shipment.PendingQuantity : null,
            OutstandingQuantity = shipmentKnown ? shipment.OutstandingQuantity : null,
            ShipmentStatus = shipmentKnown ? shipment.ShipmentStatus : SalesOrderProgress.ShipmentUnknown,
            HasApprovedShipment = shipment.HasApprovedShipment,
            ShipmentDocumentCount = shipment.ShipmentDocumentCount,
            ApprovedShipmentCount = shipment.ApprovedShipmentCount,
            FinanceLinkStatus = finance.LinkStatus,
            FinanceLinkReason = finance.LinkReason,
            LinkedAmount = finance.LinkedAmount,
            UncoveredAmount = finance.UncoveredAmount,
            SubmittedAmount = finance.SubmittedAmount,
            OtherCurrencyRecordCount = finance.OtherCurrencyRecordCount,
            UnapprovedRecordCount = finance.UnapprovedRecordCount,
            UnattributedRecordCount = finance.UnattributedRecordCount,
            OverReceived = finance.OverReceived,
            Note = BuildOrderNote(shipment, finance),
        };
    }

    /// <summary>行级说明：显式说明「哪些数字是未知 / 未链接」，并提示不得当作应收余额</summary>
    private static string BuildOrderNote(SalesOrderShipmentSummary shipment, SalesOrderFinanceProgress finance)
    {
        var notes = new List<string>();
        if (shipment.Truncated)
            notes.Add("出货数量未知：以本单为来源的出库单据超过单次派生上限，数量不完整（不静默截断，也不当作 0）。");
        else if (shipment.ShipmentStatus == SalesOrderProgress.ShipmentNone && !shipment.HasApprovedShipment)
            notes.Add("未出货（0 有依据）：没有以本单为来源且已审核的销售出库单。");
        else if (shipment.ShipmentStatus == SalesOrderProgress.ShipmentPartial)
            notes.Add($"部分出货：未出货 {shipment.OutstandingQuantity}，未出货数量只表示订单未覆盖部分。");

        switch (finance.LinkStatus)
        {
            case SalesOrderProgress.LinkUnlinked:
                notes.Add("收款未链接：没有任何定金 / 货款申请单指向本单，已关联金额未知（null，不是 0），不得当作已收款确认。");
                break;
            case SalesOrderProgress.LinkUnknown:
                notes.Add("收款链接未知：指向本单的收款申请超过单次派生上限，金额不完整（不静默截断）。");
                break;
            case SalesOrderProgress.LinkPartial:
                notes.Add("收款部分可归属：他币种 / 未审核 / 非已审核记录仅列出（不汇总、不换算），未覆盖金额只表示订单未覆盖部分，不是未收款金额。");
                break;
            default:
                if (finance.OverReceived) notes.Add("已关联金额超过订单金额（超收），请人工核对。");
                break;
        }

        if (finance.UnattributedRecordCount > 0)
            notes.Add($"另有 {finance.UnattributedRecordCount} 条客户级记录（收款单 / 装柜结算单 / 散货结算单）无法归属到本单，仅列出、不计入。");
        notes.Add("本行不是应收余额，也不是账龄：不推算账期与到期日。");
        return string.Join(" ", notes);
    }

    /// <summary>
    /// 本页「客户 + 币种」分组：金额只在同客户 + 同币种内汇总；数量类合计只要存在未知行就记为 null（未知，不是 0）。
    /// </summary>
    private static List<SalesOrderShipmentFinanceGroup> BuildGroups(List<SalesOrderShipmentFinanceOrder> rows)
        => rows.GroupBy(r => new { r.CustomerId, r.Currency })
            .OrderBy(g => g.Key.CustomerId).ThenBy(g => g.Key.Currency)
            .Select(g =>
            {
                var list = g.ToList();
                return new SalesOrderShipmentFinanceGroup
                {
                    CustomerId = g.Key.CustomerId,
                    CustomerName = list.Select(r => r.CustomerName).FirstOrDefault(n => n.Length > 0) ?? string.Empty,
                    Currency = g.Key.Currency,
                    OrderCount = list.Count,
                    OrderAmount = list.Sum(r => r.OrderAmount),
                    LinkedOrderCount = list.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceLinked),
                    PartialOrderCount = list.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinancePartial),
                    UnlinkedOrderCount = list.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceUnlinked),
                    UnknownFinanceOrderCount = list.Count(r =>
                        r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceUnknown),
                    ShippedOrderCount = list.Count(r => r.HasApprovedShipment),
                    UnshippedOrderCount = list.Count(r => !r.HasApprovedShipment),
                    UnknownShipmentOrderCount = list.Count(r =>
                        r.ShipmentStatus == SalesOrderShipmentFinanceSemantics.ShipmentUnknown),
                    LinkedAmount = SumKnownMoney(list.Select(r => r.LinkedAmount)),
                    UncoveredAmount = SumKnownMoney(list.Select(r => r.UncoveredAmount)),
                    SubmittedAmount = SumKnownMoney(list.Select(r => r.SubmittedAmount)),
                    OrderedQuantity = list.Where(r => r.OrderedQuantity.HasValue).Sum(r => r.OrderedQuantity!.Value),
                    ShippedQuantity = AllKnownSum(list.Select(r => r.ShippedQuantity)),
                    OutstandingQuantity = AllKnownSum(list.Select(r => r.OutstandingQuantity)),
                    OverReceivedOrderCount = list.Count(r => r.OverReceived),
                    Orders = list,
                };
            })
            .ToList();

    /// <summary>
    /// 本页按币种汇总：同币种内跨客户汇总，不同币种分别成行（不做汇率换算、不产生跨币种总额）。
    /// </summary>
    private static List<SalesOrderShipmentFinanceCurrencySummary> BuildCurrencySummaries(
        List<SalesOrderShipmentFinanceOrder> rows)
        => rows.GroupBy(r => r.Currency)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var list = g.ToList();
                return new SalesOrderShipmentFinanceCurrencySummary
                {
                    Currency = g.Key,
                    CustomerCount = list.Select(r => r.CustomerId).Distinct().Count(),
                    OrderCount = list.Count,
                    OrderAmount = list.Sum(r => r.OrderAmount),
                    LinkedOrderCount = list.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceLinked),
                    PartialOrderCount = list.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinancePartial),
                    UnlinkedOrderCount = list.Count(r => r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceUnlinked),
                    UnknownFinanceOrderCount = list.Count(r =>
                        r.FinanceLinkStatus == SalesOrderShipmentFinanceSemantics.FinanceUnknown),
                    ShippedOrderCount = list.Count(r => r.HasApprovedShipment),
                    UnshippedOrderCount = list.Count(r => !r.HasApprovedShipment),
                    UnknownShipmentOrderCount = list.Count(r =>
                        r.ShipmentStatus == SalesOrderShipmentFinanceSemantics.ShipmentUnknown),
                    LinkedAmount = SumKnownMoney(list.Select(r => r.LinkedAmount)),
                    UncoveredAmount = SumKnownMoney(list.Select(r => r.UncoveredAmount)),
                    OrderedQuantity = list.Where(r => r.OrderedQuantity.HasValue).Sum(r => r.OrderedQuantity!.Value),
                    ShippedQuantity = AllKnownSum(list.Select(r => r.ShippedQuantity)),
                    OutstandingQuantity = AllKnownSum(list.Select(r => r.OutstandingQuantity)),
                };
            })
            .ToList();

    /// <summary>金额汇总：只汇总金额已知的行；全部未知时返回 null（未知，不是 0）</summary>
    private static decimal? SumKnownMoney(IEnumerable<decimal?> values)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    /// <summary>数量汇总：只要存在未知行就返回 null（数量不完整时绝不给出总数）</summary>
    private static decimal? AllKnownSum(IEnumerable<decimal?> values)
    {
        var list = values.ToList();
        return list.Any(v => !v.HasValue) ? null : list.Sum(v => v!.Value);
    }
}
