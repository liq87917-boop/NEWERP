using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户订单与收款核对报表口径常量（ERP-046）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免各处自行拼写导致「没有订单级引用的收款单」被当成已收 0、或把未知覆盖当作已结清 / 未收全额。
/// <para>定位：**运营性的订单 / 收款证据核对视图（只读派生）** —— 它<strong>不是</strong>应收账款台账、
/// <strong>不是</strong>客户对账单、<strong>不是</strong>收款授权或结算结果，也<strong>不是</strong>账龄表。</para>
/// </summary>
public static class SalesOrderReceiptReconciliationSemantics
{
    // ==================== 1. 订单的收款覆盖状态（复用 ERP-032 / ERP-028 的收款链接分档） ====================

    /// <summary>权威引用完整：指向本单的收款申请（定金 / 货款申请单）全部可计入</summary>
    public const string CoverageLinked = SalesOrderProgress.LinkLinked;

    /// <summary>部分可归属：存在他币种 / 未审核 / 非已审核的收款申请，仅列出、不计入</summary>
    public const string CoveragePartial = SalesOrderProgress.LinkPartial;

    /// <summary>无可用权威引用：没有任何收款申请以 SalesOrderId 指向本单，金额未知（不用 0 顶替）</summary>
    public const string CoverageUnlinked = SalesOrderProgress.LinkUnlinked;

    /// <summary>未知：命中派生上限或单据不可用，无法确认收款覆盖是否完整</summary>
    public const string CoverageUnknown = SalesOrderProgress.LinkUnknown;

    // ==================== 2. 未关联收款证据（收款单没有订单级引用，永不自动匹配） ====================

    /// <summary>收款单的链接状态：永远为「未关联」（<c>FinanceReceipt</c> 只有 <c>CustomerId</c>，没有订单级引用）</summary>
    public const string ReceiptLinkageUnlinked = SalesOrderProgress.LinkUnlinked;

    /// <summary>未关联收款证据的既有引用字段（只记录客户；系统绝不按客户名 / 单号文本 / 日期 / 金额相似度猜测订单）</summary>
    public const string UnlinkedReferenceField = SalesOrderProgress.ReferenceReceipt;

    // ==================== 3. 收款证据状态筛选（既有 DocumentStatus 列） ====================

    /// <summary>有效证据（默认）：已审核，计入未关联收款金额</summary>
    public const string ReceiptStatusActive = "active";

    /// <summary>未审核（待提交 / 已提交）：仅列出，不计入有效合计</summary>
    public const string ReceiptStatusPending = "pending";

    /// <summary>历史证据（已驳回 / 已取消 / 已完成）：仅在显式选择该状态时可见，其金额绝不并入有效合计</summary>
    public const string ReceiptStatusHistorical = "historical";

    /// <summary>全部状态：三类证据同时列出，各自单列，历史 / 未审核金额仍不并入有效合计</summary>
    public const string ReceiptStatusAll = "all";

    /// <summary>支持的收款证据状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedReceiptStatuses =
        { ReceiptStatusActive, ReceiptStatusPending, ReceiptStatusHistorical, ReceiptStatusAll };

    /// <summary>是否有效收款证据（已审核）：唯一计入有效合计的状态</summary>
    public static bool IsActiveReceipt(DocumentStatus status) => status == DocumentStatus.Approved;

    /// <summary>是否未审核收款证据（待提交 / 已提交）：仅列出、不计入</summary>
    public static bool IsPendingReceipt(DocumentStatus status)
        => status is DocumentStatus.Pending or DocumentStatus.Submitted;

    /// <summary>是否历史收款证据（已驳回 / 已取消 / 已完成）：仅列出、不计入</summary>
    public static bool IsHistoricalReceipt(DocumentStatus status)
        => status is DocumentStatus.Rejected or DocumentStatus.Cancelled or DocumentStatus.Completed;

    /// <summary>收款单状态 → 证据分档（active / pending / historical）</summary>
    public static string EvidenceStatusOf(DocumentStatus status)
        => IsActiveReceipt(status)
            ? ReceiptStatusActive
            : IsPendingReceipt(status)
                ? ReceiptStatusPending
                : ReceiptStatusHistorical;

    /// <summary>证据分档 → 中文文案（明确是否计入有效合计）</summary>
    public static string EvidenceTextOf(DocumentStatus status)
        => IsActiveReceipt(status)
            ? "有效证据（已审核）：计入有效合计"
            : IsPendingReceipt(status)
                ? "未审核（待提交 / 已提交）：仅列出，不计入有效合计"
                : "历史证据（已驳回 / 已取消 / 已完成）：仅用于历史核对，绝不并入有效合计";

    // ==================== 4. 口径文案（接口、界面与文档同源） ====================

    /// <summary>收款证据状态筛选 → 中文文案</summary>
    public static string ReceiptStatusText(string? receiptStatus) => receiptStatus switch
    {
        ReceiptStatusPending => "仅未审核收款单（待提交 / 已提交；仅列出、不计入）",
        ReceiptStatusHistorical => "仅历史收款单（已驳回 / 已取消 / 已完成；仅列出、不计入）",
        ReceiptStatusAll => "全部状态（有效 + 未审核 + 历史；历史与未审核金额不并入有效合计）",
        _ => "仅有效收款证据（默认：已审核）",
    };

    /// <summary>订单收款覆盖状态 → 中文文案（未知不当作未收 / 已收 / 逾期）</summary>
    public static string CoverageText(string coverageStatus) => coverageStatus switch
    {
        CoverageLinked => "收款证据已完整可归属（指向本单的收款申请全部已审核且同币种）",
        CoveragePartial => "收款证据部分可归属（他币种 / 未审核记录仅列出、不计入、不换算）",
        CoverageUnlinked => "收款证据未关联：已关联收款金额与未覆盖金额未知（不按 0 处理，也不代表未收 / 逾期）",
        _ => "收款证据未知：命中单次派生上限，金额不完整（不静默截断）",
    };

    /// <summary>是否属于「未按权威引用完整覆盖」（部分可归属 / 无可用引用）</summary>
    public static bool IsUncovered(string coverageStatus)
        => coverageStatus is CoveragePartial or CoverageUnlinked;

    // ==================== 5. 订单状态筛选（既有 DocumentStatus 列；已取消订单默认不并入有效合计） ====================

    /// <summary>有效订单（默认）：排除「已取消」订单（软删除订单一律排除，无状态筛选可表达）</summary>
    public const string OrderStatusActive = "active";

    /// <summary>仅已取消订单（历史订单：仅在显式选择该状态时可见，金额仅作历史参考）</summary>
    public const string OrderStatusCancelled = "cancelled";

    /// <summary>全部未删除订单（有效 + 已取消；已取消订单单列计数，金额仅作历史参考）</summary>
    public const string OrderStatusAll = "all";

    /// <summary>支持的订单状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedOrderStatuses =
        { OrderStatusActive, OrderStatusCancelled, OrderStatusAll };

    /// <summary>订单状态筛选 → 中文文案</summary>
    public static string OrderStatusText(string? orderStatus) => orderStatus switch
    {
        OrderStatusCancelled => "仅已取消订单（历史订单，金额仅作历史参考）",
        OrderStatusAll => "全部未删除订单（有效 + 已取消；已取消单列计数）",
        _ => "仅有效订单（默认：排除已取消订单）",
    };

    /// <summary>派生口径说明（分组、数量来源、收款证据来源、未知处理、不猜测匹配）</summary>
    public const string RuleText =
        "本报表是客户订单与收款证据的运营核对视图（只读派生）：分组 = 客户 + 币种，不同币种分别成行、绝不合并为一个金额、也不做汇率换算；" +
        "订单侧「已订 / 已出 / 未出数量」与「收款链接」完全复用「销售订单出货 / 财务进度（ERP-032）」的同一套权威派生（已审核销售出库单；" +
        "定金申请单 / 货款申请单的 SalesOrderId 才是权威收款引用，「已审核 + 同币种」才计入已关联收款金额）；" +
        "已关联收款金额只是收款申请（定金 / 货款申请单）的权威引用金额，不是收款单金额、不是已收款确认、不是结算结果；" +
        "未覆盖金额 = 订单金额 − 已关联收款金额，只表示订单金额未被权威收款申请覆盖的部分，不得当作应收余额或据以催收；" +
        "收款单（FinanceReceipt）只记录客户、没有订单级引用，因此一律作为未关联证据单独列出，" +
        "系统绝不按客户名、订单号文本、日期或金额相似度把它归到任何销售订单；" +
        "没有权威引用、单据不可用或命中派生上限时金额一律记为未知（null），不用 0 顶替，也不代表已收 / 未收 / 逾期 / 已结清；" +
        "订单默认排除已取消（历史订单需显式选择订单状态筛选才可见，且单列计数、仅作历史参考），软删除订单一律排除；" +
        "收款证据默认仅统计有效证据（已审核），未审核与历史（已驳回 / 已取消 / 已完成）证据必须显式选择状态筛选才可见，其金额永不并入有效合计。";

    /// <summary>范围说明：合计与计数只统计本次返回页的订单，未关联收款证据只列出本页客户</summary>
    public const string ScopeNoteText =
        "以下分组汇总与计数只统计本次返回页的订单；total 为符合筛选条件的未删除销售订单总数（默认排除已取消订单）；" +
        "分页按客户 + 币种 + 订单日期 + 单据 Id 稳定排序；" +
        "未关联收款证据按本次页面上出现客户的收款单自身客户列与币种列有界读取（订单日期筛选不作用于收款单），" +
        "每张收款单只列出一次、只按自己的币种汇总，绝不与订单金额相加，也不做汇率换算；" +
        "订单侧汇总与未关联收款证据汇总相互独立，两表不得相加；收款单查询命中单次上限时显式标注「不完整」，不静默截断。";

    /// <summary>与应收账款台账 / 对账单 / 收款授权 / 结算结果 / 账龄表的边界说明（界面与文档同源）</summary>
    public const string LedgerBoundaryText =
        "本报表是运营性的订单 / 收款证据核对视图：不是应收账款台账、不是具有法律效力的客户对账单、" +
        "不是收款授权或结算结果，也不是账龄表 —— 不创建发票或应收记录、不推算账期与到期日、不做账龄分摊、" +
        "不判断是否已收讫或已结清、不产生催收依据；「未关联收款证据」与「未覆盖金额」都不得当作应收余额、不得据以催收。";
}

/// <summary>
/// 客户订单与收款核对报表查询条件（ERP-046；全部为只读筛选参数，非法取值直接拒绝而不静默忽略）。
/// </summary>
public sealed class SalesOrderReceiptReconciliationQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 50;

    /// <summary>客户筛选（留空 = 全部客户）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（枚举名，如 USD / CNY；留空 = 全部币种，不同币种绝不合并汇总）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>出货状态筛选（none 无已审核出库单 / shipped 存在已审核出库单；留空 = 全部）</summary>
    public string? ShipmentStatus { get; set; }

    /// <summary>收款链接状态筛选（linked / partial / unlinked；留空 = 全部；unknown 无法用既有列条件等价表达，故不提供）</summary>
    public string? ReceiptLinkStatus { get; set; }

    /// <summary>收款证据状态筛选（active 默认 / pending / historical / all；历史与未审核金额永不并入有效合计）</summary>
    public string? ReceiptStatus { get; set; }

    /// <summary>订单状态筛选（active 默认：排除已取消 / cancelled：仅已取消 / all：全部未删除；软删除订单一律排除）</summary>
    public string? OrderStatus { get; set; }

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
    internal string? ReceiptLinkStatusValue { get; private set; }

    /// <summary>归一化后的收款证据状态筛选值（active / pending / historical / all；默认 active）</summary>
    internal string ReceiptStatusValue { get; private set; } = SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive;

    /// <summary>归一化后的订单状态筛选值（active / cancelled / all；默认 active：排除已取消订单）</summary>
    internal string OrderStatusValue { get; private set; } = SalesOrderReceiptReconciliationSemantics.OrderStatusActive;

    /// <summary>
    /// 归一化并校验：币种 / 出货状态 / 收款链接状态 / 收款证据状态必须是既定取值，订单日期区间不得倒置，
    /// 关键字长度有界，分页参数钳制到有界范围；非法取值直接抛业务异常（参数错误），不静默忽略筛选条件。
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

        ReceiptLinkStatusValue = null;
        if (!string.IsNullOrWhiteSpace(ReceiptLinkStatus))
        {
            var value = ReceiptLinkStatus.Trim().ToLowerInvariant();
            if (value != SalesOrderReceiptReconciliationSemantics.CoverageLinked
                && value != SalesOrderReceiptReconciliationSemantics.CoveragePartial
                && value != SalesOrderReceiptReconciliationSemantics.CoverageUnlinked)
                throw BusinessException.InvalidParameter(
                    "收款链接状态筛选取值无效：只接受 linked / partial / unlinked" +
                    "（unknown 只在命中派生上限时出现，无法用既有列条件等价表达）");
            ReceiptLinkStatusValue = value;
        }

        // 收款证据状态：默认只显示有效证据（已审核）；历史 / 未审核必须显式选择才可见
        ReceiptStatusValue = SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive;
        if (!string.IsNullOrWhiteSpace(ReceiptStatus))
        {
            var value = ReceiptStatus.Trim().ToLowerInvariant();
            if (!SalesOrderReceiptReconciliationSemantics.SupportedReceiptStatuses.Contains(value, StringComparer.Ordinal))
                throw BusinessException.InvalidParameter(
                    "收款证据状态筛选取值无效：只接受 active（有效，默认）/ pending（未审核）/ historical（历史）/ all（全部）");
            ReceiptStatusValue = value;
        }

        if (OrderDateFrom.HasValue && OrderDateTo.HasValue && OrderDateFrom.Value > OrderDateTo.Value)
            throw BusinessException.InvalidParameter("订单日期区间无效：开始日期不能晚于结束日期");

        // 订单状态：默认排除已取消订单（历史订单需显式选择 cancelled / all 才可见）
        OrderStatusValue = SalesOrderReceiptReconciliationSemantics.OrderStatusActive;
        if (!string.IsNullOrWhiteSpace(OrderStatus))
        {
            var value = OrderStatus.Trim().ToLowerInvariant();
            if (!SalesOrderReceiptReconciliationSemantics.SupportedOrderStatuses.Contains(value, StringComparer.Ordinal))
                throw BusinessException.InvalidParameter(
                    "订单状态筛选取值无效：只接受 active（有效，默认）/ cancelled（仅已取消）/ all（全部未删除）");
            OrderStatusValue = value;
        }

        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        if (Keyword is { Length: > MaxKeywordLength })
            throw BusinessException.InvalidParameter(
                $"关键字长度不能超过 {MaxKeywordLength} 个字符");
        if (CustomerId is <= 0) CustomerId = null;
        Page = Page < 1 ? 1 : Page;
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize);
    }
}

/// <summary>
/// 报表中的一行销售订单（ERP-046，只读派生）：出货数量（已订 / 已出 / 待审 / 未出）、订单金额与**收款证据覆盖**
/// （覆盖状态 / 已关联收款金额 / 未覆盖金额 / 计数）。
/// <para>数量与金额为 null 一律表示未知（命中派生上限、单据不可用或没有权威引用），绝不用 0 顶替；
/// 已关联收款金额只来自「定金申请单 / 货款申请单的 SalesOrderId」这一权威引用，不是收款单金额。</para>
/// </summary>
public sealed class SalesOrderReceiptReconciliationOrderRow
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }

    /// <summary>订单状态（枚举名，原样回显）</summary>
    public string Status { get; init; } = string.Empty;

    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种（枚举名；金额只在本币种内汇总，绝不跨币种相加、不做汇率换算）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>币种金额小数位（仅用于展示，不做任何换算）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>订单金额（订单主表已落库总额，报表不重算）</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>已落库定金金额（订单字段原样回显，不是派生的收款金额）</summary>
    public decimal RecordedDepositAmount { get; init; }

    /// <summary>订单数量合计（订单明细口径）；null = 未知</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>已出货数量（已审核销售出库单明细口径，含订单外商品）；null = 未知</summary>
    public decimal? ShippedQuantity { get; init; }

    /// <summary>待审核出库数量（不计入已出货）；null = 未知</summary>
    public decimal? PendingShipmentQuantity { get; init; }

    /// <summary>未出货数量 = max(0, 订单数量 − 已出货数量)；null = 未知</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>超发数量 = max(0, 已出货 − 订单数量)；null = 未知</summary>
    public decimal? OverShippedQuantity { get; init; }

    /// <summary>整体出货状态：none / partial / complete / over_shipped / unknown（unknown = 数量不完整）</summary>
    public string ShipmentStatus { get; init; } = SalesOrderProgress.ShipmentNone;

    /// <summary>是否存在「以本单为来源、未删除、已审核」的销售出库单（与出货状态筛选条件完全一致）</summary>
    public bool HasApprovedShipment { get; init; }

    /// <summary>以本单为来源的出库单数（未删除，含未审核）</summary>
    public int ShipmentDocumentCount { get; init; }

    /// <summary>以本单为来源且已审核的出库单数</summary>
    public int ApprovedShipmentCount { get; init; }

    /// <summary>收款证据覆盖状态：linked / partial / unlinked / unknown（复用 ERP-032 的收款链接分档）</summary>
    public string ReceiptCoverageStatus { get; init; } = SalesOrderReceiptReconciliationSemantics.CoverageUnlinked;

    /// <summary>覆盖状态文案（显式说明未知与不得当作应收余额）</summary>
    public string ReceiptCoverageText { get; init; } = string.Empty;

    /// <summary>覆盖状态是否已知（false = 命中派生上限，任何金额 / 状态都不得作为对账依据）</summary>
    public bool ReceiptCoverageKnown { get; init; }

    /// <summary>已关联收款金额（权威引用 + 已审核 + 同币种）；null = 未知（不是 0，也不代表未收款）</summary>
    public decimal? LinkedReceiptAmount { get; init; }

    /// <summary>未审核（待提交 / 已提交）收款申请金额，仅单列不计入；null = 未知</summary>
    public decimal? PendingReceiptAmount { get; init; }

    /// <summary>未覆盖金额 = 订单金额 − 已关联收款金额；null = 未知；它不等于未收款金额，也不是应收余额</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>指向本单但币种与本单不一致的收款申请条数（仅列出、不计入、不换算）</summary>
    public int OtherCurrencyReceiptCount { get; init; }

    /// <summary>指向本单但非「已审核」的收款申请条数（已驳回 / 已取消 / 已完成等，仅列出、不计入）</summary>
    public int UnapprovedReceiptCount { get; init; }

    /// <summary>只记录客户、没有订单级引用的记录条数（收款单 / 装柜结算单 / 散货结算单，仅列出、不计入）</summary>
    public int UnattributedReceiptCount { get; init; }

    /// <summary>客户级记录是否命中单次查询上限（true = 上述计数不完整）</summary>
    public bool UnattributedReceiptsTruncated { get; init; }

    /// <summary>是否超收（已关联收款金额 &gt; 订单金额，按金额容差判定，需人工核对）</summary>
    public bool OverReceived { get; init; }

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不得当作应收余额）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一条**未关联**收款证据（ERP-046，只读派生）：收款单按既有 <c>FinanceReceipt.CustomerId</c> 读取，
/// 没有任何订单级引用，因此链接状态恒为 <c>unlinked</c>，金额绝不并入任何订单的对账合计，
/// 也绝不按客户名、订单号文本、日期或金额相似度自动匹配到订单。
/// </summary>
public sealed class SalesOrderReceiptReconciliationReceipt
{
    public long ReceiptId { get; init; }

    /// <summary>收款单号</summary>
    public string ReceiptNo { get; init; } = string.Empty;

    /// <summary>收款日期（订单日期筛选不作用于收款单，原样列出）</summary>
    public DateTime ReceiptDate { get; init; }

    public long CustomerId { get; init; }

    /// <summary>客户名（仅展示；缺失时留空而不臆造）</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种（收款单自身币种列；不同币种分别成行，绝不合并、不换算）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>收款金额（收款单已落库金额，按收款单自身币种原样列出）</summary>
    public decimal Amount { get; init; }

    /// <summary>付款方式（枚举名）</summary>
    public string PaymentMethod { get; init; } = string.Empty;

    /// <summary>单据状态（枚举名，原样回显）</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>证据分档：active（已审核）/ pending（待提交 / 已提交）/ historical（已驳回 / 已取消 / 已完成）</summary>
    public string EvidenceStatus { get; init; } = SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive;

    /// <summary>证据分档文案（明确历史 / 未审核不并入有效合计）</summary>
    public string EvidenceText { get; init; } = string.Empty;

    /// <summary>链接状态：恒为 unlinked（未关联证据）</summary>
    public string ReceiptLinkageStatus { get; init; } = SalesOrderReceiptReconciliationSemantics.ReceiptLinkageUnlinked;

    /// <summary>链接说明（为什么不匹配、系统绝不猜测）</summary>
    public string ReceiptLinkageText { get; init; } = string.Empty;

    /// <summary>建立引用所用的既有字段（= FinanceReceipt.CustomerId，只到客户级）</summary>
    public string ReferenceField { get; init; } = SalesOrderReceiptReconciliationSemantics.UnlinkedReferenceField;

    /// <summary>行级说明</summary>
    public string Note { get; init; } = string.Empty;
}


/// <summary>
/// 「客户 + 币种」分组（ERP-046，只读派生）：只有同客户 + 同币种才汇总金额；存在未知行时数量合计记 null（未知，不是 0）。
/// </summary>
public sealed class SalesOrderReceiptReconciliationGroup
{
    public long CustomerId { get; init; }

    /// <summary>客户名（仅展示；缺失时留空而不臆造）</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种（枚举名；本分组内金额只在本币种内汇总）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本分组订单数（本页）</summary>
    public int OrderCount { get; init; }

    /// <summary>本分组订单金额合计（同一币种内；不与其它币种合并）</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>订单数量合计（只统计数量已知的行）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已出货数量合计；null = 本分组存在数量未知的订单（未知，不是 0）</summary>
    public decimal? ShippedQuantity { get; init; }

    /// <summary>未出货数量合计；null = 本分组存在数量未知的订单（未知，不是 0）</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>覆盖状态为 linked 的订单数</summary>
    public int LinkedOrderCount { get; init; }

    /// <summary>覆盖状态为 partial 的订单数</summary>
    public int PartialOrderCount { get; init; }

    /// <summary>覆盖状态为 unlinked（无权威引用，金额未知）的订单数</summary>
    public int UnlinkedOrderCount { get; init; }

    /// <summary>覆盖状态为 unknown（命中派生上限）的订单数</summary>
    public int UnknownCoverageOrderCount { get; init; }

    /// <summary>存在已审核出库单的订单数</summary>
    public int ShippedOrderCount { get; init; }

    /// <summary>无已审核出库单的订单数</summary>
    public int UnshippedOrderCount { get; init; }

    /// <summary>出货数量未知（命中派生上限）的订单数</summary>
    public int UnknownShipmentOrderCount { get; init; }

    /// <summary>已取消订单数（仅在订单状态筛选为 cancelled / all 时大于 0；其金额仅作历史参考）</summary>
    public int CancelledOrderCount { get; init; }

    /// <summary>已关联收款金额合计（只汇总金额已知的行）；null = 本分组所有行金额均未知（未知，不是 0）</summary>
    public decimal? LinkedReceiptAmount { get; init; }

    /// <summary>未覆盖金额合计（只汇总金额已知的行）；null = 未知（不代表「全部未收」）</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>未审核（待提交 / 已提交）收款申请金额合计（仅单列）；null = 未知</summary>
    public decimal? PendingReceiptAmount { get; init; }

    /// <summary>本分组订单行（与本页顺序一致）</summary>
    public List<SalesOrderReceiptReconciliationOrderRow> Orders { get; init; } = new();

    /// <summary>本分组说明（未知 / 未关联 / 客户级记录的显式提示）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本页按币种的订单侧汇总（ERP-046）：不同币种分别成行，绝不折算成一个总额。</summary>
public sealed class SalesOrderReceiptReconciliationCurrencySummary
{
    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本币种涉及的客户数</summary>
    public int CustomerCount { get; init; }

    /// <summary>本币种订单数</summary>
    public int OrderCount { get; init; }

    /// <summary>本币种订单金额合计</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>覆盖状态为 linked 的订单数</summary>
    public int LinkedOrderCount { get; init; }

    /// <summary>覆盖状态为 partial 的订单数</summary>
    public int PartialOrderCount { get; init; }

    /// <summary>覆盖状态为 unlinked 的订单数</summary>
    public int UnlinkedOrderCount { get; init; }

    /// <summary>覆盖状态为 unknown 的订单数</summary>
    public int UnknownCoverageOrderCount { get; init; }

    /// <summary>存在已审核出库单的订单数</summary>
    public int ShippedOrderCount { get; init; }

    /// <summary>无已审核出库单的订单数</summary>
    public int UnshippedOrderCount { get; init; }

    /// <summary>出货数量未知的订单数</summary>
    public int UnknownShipmentOrderCount { get; init; }

    /// <summary>已取消订单数（仅在订单状态筛选为 cancelled / all 时大于 0；其金额仅作历史参考）</summary>
    public int CancelledOrderCount { get; init; }

    /// <summary>已关联收款金额合计；null = 本币种没有金额已知的行（未知，不是 0）</summary>
    public decimal? LinkedReceiptAmount { get; init; }

    /// <summary>未覆盖金额合计；null = 未知（不代表「全部未收」）</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>未审核收款申请金额合计（仅单列）；null = 未知</summary>
    public decimal? PendingReceiptAmount { get; init; }

    /// <summary>订单数量合计（只统计数量已知的行）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已出货数量合计；null = 本币种存在数量未知的订单</summary>
    public decimal? ShippedQuantity { get; init; }

    /// <summary>未出货数量合计；null = 本币种存在数量未知的订单</summary>
    public decimal? OutstandingQuantity { get; init; }
}

/// <summary>
/// 本页**未关联收款证据**按币种的汇总（ERP-046）：每张收款单只按自己的币种汇总，
/// 不同币种分别成行、绝不合并、绝不换算；该汇总与订单侧金额相互独立，两表不得相加。
/// </summary>
public sealed class SalesOrderReceiptReconciliationReceiptCurrencySummary
{
    /// <summary>收款单币种（收款单自身币种列）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本币种涉及的客户数</summary>
    public int CustomerCount { get; init; }

    /// <summary>本币种列出的收款单张数</summary>
    public int ReceiptCount { get; init; }

    /// <summary>有效证据（已审核）张数</summary>
    public int ActiveReceiptCount { get; init; }

    /// <summary>有效证据金额合计；null = 未知（命中收款单查询上限，金额不完整，不静默给出）</summary>
    public decimal? ActiveReceiptAmount { get; init; }

    /// <summary>未审核（待提交 / 已提交）张数（仅列出、不计入有效合计）</summary>
    public int PendingReceiptCount { get; init; }

    /// <summary>未审核金额合计；null = 未知（命中上限）</summary>
    public decimal? PendingReceiptAmount { get; init; }

    /// <summary>历史证据（已驳回 / 已取消 / 已完成）张数（仅历史核对，不并入有效合计）</summary>
    public int HistoricalReceiptCount { get; init; }

    /// <summary>历史证据金额合计；null = 未知（命中上限）</summary>
    public decimal? HistoricalReceiptAmount { get; init; }

    /// <summary>本币种收款单是否命中单次查询上限（true = 以上张数与金额不完整）</summary>
    public bool Truncated { get; init; }

    /// <summary>口径提示（未关联证据不得当作已收款 / 应收余额）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 客户订单与收款核对报表（ERP-046，只读派生；不落库、不改单据状态、不新增或修改任何表列）。
/// <para>报表只在本页范围内汇总，金额按币种分别成行（不存在跨币种总额字段）；未关联收款证据与订单金额相互独立。</para>
/// </summary>
public sealed class SalesOrderReceiptReconciliationReport
{
    // ============ 筛选回显（归一化后的实际取值） ============
    public long? CustomerId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateTime? OrderDateFrom { get; init; }
    public DateTime? OrderDateTo { get; init; }

    /// <summary>出货状态筛选（none / shipped；空 = 全部）</summary>
    public string ShipmentStatus { get; init; } = string.Empty;

    /// <summary>收款链接状态筛选（linked / partial / unlinked；空 = 全部）</summary>
    public string ReceiptLinkStatus { get; init; } = string.Empty;

    /// <summary>收款证据状态筛选（active / pending / historical / all）</summary>
    public string ReceiptStatus { get; init; } = SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive;

    /// <summary>收款证据状态文案</summary>
    public string ReceiptStatusText { get; init; } = string.Empty;

    /// <summary>订单状态筛选（active / cancelled / all）</summary>
    public string OrderStatus { get; init; } = SalesOrderReceiptReconciliationSemantics.OrderStatusActive;

    /// <summary>订单状态筛选文案</summary>
    public string OrderStatusText { get; init; } = string.Empty;

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

    // ============ 本页计数（收款覆盖 / 出货） ============
    public int LinkedOrderCount { get; init; }
    public int PartialOrderCount { get; init; }
    public int UnlinkedOrderCount { get; init; }

    /// <summary>命中派生上限、收款金额未知的订单数（本页）</summary>
    public int UnknownCoverageOrderCount { get; init; }

    /// <summary>存在已审核出库单的订单数（本页）</summary>
    public int ShippedOrderCount { get; init; }

    /// <summary>无已审核出库单的订单数（本页）</summary>
    public int UnshippedOrderCount { get; init; }

    /// <summary>出货数量未知（命中派生上限）的订单数（本页）</summary>
    public int UnknownShipmentOrderCount { get; init; }

    /// <summary>已取消订单数（本页；仅在订单状态筛选为 cancelled / all 时大于 0，其金额仅作历史参考）</summary>
    public int CancelledOrderCount { get; init; }

    // ============ 未关联收款证据（本页客户；与订单金额相互独立） ============
    /// <summary>本页列出的未关联收款单张数</summary>
    public int PageUnlinkedReceiptCount { get; init; }

    /// <summary>收款单是否命中单次查询上限（true = 以上张数与金额不完整，可收窄筛选）</summary>
    public bool PageUnlinkedReceiptTruncated { get; init; }

    /// <summary>本页未关联收款证据（逐张列出；链接状态恒为 unlinked）</summary>
    public List<SalesOrderReceiptReconciliationReceipt> UnlinkedReceipts { get; init; } = new();

    /// <summary>本页未关联收款证据按币种汇总（不同币种分别成行）</summary>
    public List<SalesOrderReceiptReconciliationReceiptCurrencySummary> UnlinkedReceiptCurrencies { get; init; } = new();

    // ============ 分组与币种汇总（只统计本页） ============
    /// <summary>「客户 + 币种」分组（本页）</summary>
    public List<SalesOrderReceiptReconciliationGroup> Groups { get; init; } = new();

    /// <summary>本页按币种汇总（不同币种分别成行，不做汇率换算）</summary>
    public List<SalesOrderReceiptReconciliationCurrencySummary> Currencies { get; init; } = new();

    // ============ 口径与边界（与派生逻辑同源） ============
    /// <summary>口径说明</summary>
    public string Rule { get; init; } = SalesOrderReceiptReconciliationSemantics.RuleText;

    /// <summary>范围说明（合计只统计本页）</summary>
    public string ScopeNote { get; init; } = SalesOrderReceiptReconciliationSemantics.ScopeNoteText;

    /// <summary>与应收账款台账 / 对账单 / 收款授权 / 账龄表的边界声明</summary>
    public string LedgerBoundary { get; init; } = SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText;
}


/// <summary>
/// 客户订单与收款核对报表派生（ERP-046，只读）。
/// <para><b>单一口径</b>：出货数量与收款链接全部转调 <see cref="SalesOrderProgress"/>（与 ERP-032 出货 / 财务进度报表同一套派生），
/// 报表不重复实现任何匹配规则，只做筛选、分组、汇总，以及「未关联收款证据」的独立列举。</para>
/// <para><b>未关联收款证据</b>：收款单（<c>FinanceReceipt</c>）只有客户列、没有任何订单级引用，
/// 因此按本次页面上出现客户的收款单<strong>自身客户列与币种列</strong>有界读取、逐张列出，链接状态恒为 unlinked，
/// 金额绝不并入订单侧合计，系统绝不按客户名 / 订单号文本 / 日期 / 金额相似度自动匹配到订单。</para>
/// <para><b>查询有界</b>：固定 12 次数据集访问（订单集合（筛选 / 计数 / 分页共用）+ 本页订单 + 客户名 + 逐单派生的 8 次 + 未关联收款证据 1 次），
/// 与页大小、订单数、单据数无关，无逐单查库（无 N+1）；全程不写库。</para>
/// </summary>
public static class SalesOrderReceiptReconciliation
{
    /// <summary>未关联收款证据的单次查询上限（命中时显式标注「不完整」，绝不静默截断）</summary>
    public const int UnlinkedReceiptLimit = SalesOrderProgress.BatchDocumentCeiling;

    /// <summary>客户订单与收款核对报表查询（GET /api/sales-orders/receipt-reconciliation-report，只读、分页有界）</summary>
    public static async Task<SalesOrderReceiptReconciliationReport> ForQueryAsync(IErpDbContext db,
        SalesOrderReceiptReconciliationQuery query)
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
        var orderRows = orders.Select(o => MapOrderRow(o, derived[o.Id], customerNames)).ToList();

        // 第 12 次：未关联收款证据（收款单只记录客户，无订单级引用；按本次筛选的客户 / 币种有界读取）
        var unlinked = await LoadUnlinkedReceiptsAsync(db, query, customerIds);
        var receiptRows = unlinked.Rows.Select(r => MapReceipt(r, customerNames)).ToList();

        return new SalesOrderReceiptReconciliationReport
        {
            CustomerId = query.CustomerId,
            Currency = query.CurrencyValue?.ToString() ?? string.Empty,
            OrderDateFrom = query.OrderDateFrom,
            OrderDateTo = query.OrderDateTo,
            ShipmentStatus = query.ShipmentStatusValue ?? string.Empty,
            ReceiptLinkStatus = query.ReceiptLinkStatusValue ?? string.Empty,
            ReceiptStatus = query.ReceiptStatusValue,
            ReceiptStatusText = SalesOrderReceiptReconciliationSemantics.ReceiptStatusText(query.ReceiptStatusValue),
            OrderStatus = query.OrderStatusValue,
            OrderStatusText = SalesOrderReceiptReconciliationSemantics.OrderStatusText(query.OrderStatusValue),
            Keyword = query.Keyword ?? string.Empty,
            Page = query.Page,
            PageSize = query.PageSize,
            Total = total,
            TotalPages = totalPages,
            PageOrderCount = orderRows.Count,
            LinkedOrderCount = orderRows.Count(r => r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageLinked),
            PartialOrderCount = orderRows.Count(r => r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoveragePartial),
            UnlinkedOrderCount = orderRows.Count(r => r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageUnlinked),
            UnknownCoverageOrderCount = orderRows.Count(r => r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageUnknown),
            ShippedOrderCount = orderRows.Count(r => r.HasApprovedShipment),
            UnshippedOrderCount = orderRows.Count(r => !r.HasApprovedShipment),
            UnknownShipmentOrderCount = orderRows.Count(r => r.ShipmentStatus == SalesOrderProgress.ShipmentUnknown),
            CancelledOrderCount = orderRows.Count(r => r.Status == DocumentStatus.Cancelled.ToString()),
            PageUnlinkedReceiptCount = receiptRows.Count,
            PageUnlinkedReceiptTruncated = unlinked.Truncated,
            UnlinkedReceipts = receiptRows,
            UnlinkedReceiptCurrencies = BuildReceiptCurrencySummaries(receiptRows, unlinked.Truncated),
            Groups = BuildGroups(orderRows),
            Currencies = BuildCurrencySummaries(orderRows),
        };
    }

    /// <summary>
    /// 报表筛选（只读）：客户 / 币种 / 订单日期（含首尾当天）/ 关键字，以及用与派生规则等价的既有列条件表达的
    /// 出货状态与收款链接状态筛选（与 ERP-032 报表同一套条件，避免两处口径分叉）。
    /// 不按备注文本、金额接近度或客户汇总口径猜测任何链接。
    /// </summary>
    private static IQueryable<SalesOrder> ApplyFilters(IErpDbContext db, SalesOrderReceiptReconciliationQuery query)
    {
        var source = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);

        // 订单状态：默认排除「已取消」订单（历史订单需显式选择 cancelled / all 才可见）；软删除订单一律排除
        if (query.OrderStatusValue == SalesOrderReceiptReconciliationSemantics.OrderStatusCancelled)
            source = source.Where(o => o.Status == DocumentStatus.Cancelled);
        else if (query.OrderStatusValue == SalesOrderReceiptReconciliationSemantics.OrderStatusActive)
            source = source.Where(o => o.Status != DocumentStatus.Cancelled);

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
        if (query.ReceiptLinkStatusValue == SalesOrderReceiptReconciliationSemantics.CoverageUnlinked)
        {
            source = source.Where(o =>
                !db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                && !db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id));
        }
        else if (query.ReceiptLinkStatusValue == SalesOrderReceiptReconciliationSemantics.CoverageLinked)
        {
            source = source.Where(o =>
                (db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id)
                    || db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id))
                && !db.FinanceDepositApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                    && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency))
                && !db.FinancePaymentApplies.Any(a => !a.IsDeleted && a.SalesOrderId == o.Id
                    && (a.Status != DocumentStatus.Approved || a.Currency != o.Currency)));
        }
        else if (query.ReceiptLinkStatusValue == SalesOrderReceiptReconciliationSemantics.CoveragePartial)
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

    /// <summary>
    /// 未关联收款证据：收款单只记录客户（无订单级引用），因此按本次页面上出现客户的收款单自身客户列有界读取；
    /// 收款证据状态与币种筛选均作用在收款单既有列上（不做任何换算），命中单次上限时返回「不完整」标记。
    /// </summary>
    private static async Task<UnlinkedReceiptResult> LoadUnlinkedReceiptsAsync(IErpDbContext db,
        SalesOrderReceiptReconciliationQuery query, IReadOnlyList<long> customerIds)
    {
        if (customerIds.Count == 0) return new UnlinkedReceiptResult(new List<FinanceReceipt>(), false);

        var source = db.FinanceReceipts.AsNoTracking()
            .Where(r => !r.IsDeleted && customerIds.Contains(r.CustomerId));

        // 证据状态：默认只读有效证据（已审核）；未审核 / 历史证据必须显式选择筛选值才可见
        if (query.ReceiptStatusValue == SalesOrderReceiptReconciliationSemantics.ReceiptStatusPending)
        {
            source = source.Where(r => r.Status == DocumentStatus.Pending || r.Status == DocumentStatus.Submitted);
        }
        else if (query.ReceiptStatusValue == SalesOrderReceiptReconciliationSemantics.ReceiptStatusHistorical)
        {
            source = source.Where(r => r.Status == DocumentStatus.Rejected
                || r.Status == DocumentStatus.Cancelled || r.Status == DocumentStatus.Completed);
        }
        else if (query.ReceiptStatusValue != SalesOrderReceiptReconciliationSemantics.ReceiptStatusAll)
        {
            source = source.Where(r => r.Status == DocumentStatus.Approved);
        }

        if (query.CurrencyValue.HasValue)
            source = source.Where(r => r.Currency == query.CurrencyValue.Value);

        var rows = await source
            .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id)
            .Take(UnlinkedReceiptLimit)
            .ToListAsync();
        return new UnlinkedReceiptResult(rows, rows.Count == UnlinkedReceiptLimit);
    }

    /// <summary>报表行：数量未知一律 null（不回落为 0），收款金额未知一律 null（不回落到 0）</summary>
    private static SalesOrderReceiptReconciliationOrderRow MapOrderRow(SalesOrder order,
        SalesOrderProgressResult progress, IReadOnlyDictionary<long, string> customerNames)
    {
        var shipment = progress.Shipment;
        var finance = progress.Finance;
        var shipmentKnown = !shipment.Truncated;
        var coverage = finance.LinkStatus;

        return new SalesOrderReceiptReconciliationOrderRow
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            Status = order.Status.ToString(),
            CustomerId = order.CustomerId,
            CustomerName = customerNames.TryGetValue(order.CustomerId, out var name) ? name : string.Empty,
            Currency = order.Currency.ToString(),
            AmountDecimals = CurrencyAmountRules.PrecisionOf(order.Currency.ToString()),
            OrderAmount = order.TotalAmount,
            RecordedDepositAmount = order.DepositAmount,
            OrderedQuantity = shipmentKnown ? shipment.OrderedQuantity : null,
            ShippedQuantity = shipmentKnown ? shipment.ShippedQuantity : null,
            PendingShipmentQuantity = shipmentKnown ? shipment.PendingQuantity : null,
            OutstandingQuantity = shipmentKnown ? shipment.OutstandingQuantity : null,
            OverShippedQuantity = shipmentKnown ? shipment.OverShippedQuantity : null,
            ShipmentStatus = shipmentKnown ? shipment.ShipmentStatus : SalesOrderProgress.ShipmentUnknown,
            HasApprovedShipment = shipment.HasApprovedShipment,
            ShipmentDocumentCount = shipment.ShipmentDocumentCount,
            ApprovedShipmentCount = shipment.ApprovedShipmentCount,
            ReceiptCoverageStatus = coverage,
            ReceiptCoverageText = SalesOrderReceiptReconciliationSemantics.CoverageText(coverage),
            ReceiptCoverageKnown = coverage != SalesOrderReceiptReconciliationSemantics.CoverageUnknown,
            LinkedReceiptAmount = finance.LinkedAmount,
            PendingReceiptAmount = finance.SubmittedAmount,
            UncoveredAmount = finance.UncoveredAmount,
            OtherCurrencyReceiptCount = finance.OtherCurrencyRecordCount,
            UnapprovedReceiptCount = finance.UnapprovedRecordCount,
            UnattributedReceiptCount = finance.UnattributedRecordCount,
            UnattributedReceiptsTruncated = finance.UnattributedRecordsTruncated,
            OverReceived = finance.OverReceived,
            Note = BuildOrderNote(shipment, finance),
        };
    }

    /// <summary>
    /// 未关联收款证据行：链接状态恒为 unlinked，金额只按收款单自身币种原样列出（不猜测订单、不并入订单合计）。
    /// </summary>
    private static SalesOrderReceiptReconciliationReceipt MapReceipt(FinanceReceipt receipt,
        IReadOnlyDictionary<long, string> customerNames)
    {
        var evidence = SalesOrderReceiptReconciliationSemantics.EvidenceStatusOf(receipt.Status);
        return new SalesOrderReceiptReconciliationReceipt
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            CustomerId = receipt.CustomerId,
            CustomerName = customerNames.TryGetValue(receipt.CustomerId, out var name) ? name : string.Empty,
            Currency = receipt.Currency.ToString(),
            Amount = receipt.Amount,
            PaymentMethod = receipt.PaymentMethod.ToString(),
            Status = receipt.Status.ToString(),
            EvidenceStatus = evidence,
            EvidenceText = SalesOrderReceiptReconciliationSemantics.EvidenceTextOf(receipt.Status),
            ReceiptLinkageStatus = SalesOrderReceiptReconciliationSemantics.ReceiptLinkageUnlinked,
            ReceiptLinkageText =
                "未关联证据：收款单只记录客户（FinanceReceipt.CustomerId），没有订单级引用，" +
                "系统绝不按客户名、订单号文本、日期或金额相似度把它匹配到任何销售订单。",
            ReferenceField = SalesOrderReceiptReconciliationSemantics.UnlinkedReferenceField,
            Note = evidence == SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive
                ? "已审核收款单：仅作为客户级未关联证据列出，未被计入任何订单的已关联收款金额，也不代表已结算或已收讫。"
                : SalesOrderReceiptReconciliationSemantics.IsPendingReceipt(receipt.Status)
                    ? "未审核收款单：仅列出、不计入有效合计；审核前不得作为对账或催收依据。"
                    : "历史收款单（已驳回 / 已取消 / 已完成）：仅用于历史核对，其金额绝不并入有效合计。",
        };
    }

    /// <summary>行级说明：显式说明「哪些数字是未知 / 未关联」，并提示不得当作应收余额</summary>
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
                notes.Add("收款证据未关联：没有任何定金 / 货款申请单以 SalesOrderId 指向本单，已关联收款金额与未覆盖金额记为未知（null，不是 0），"
                    + "不得当作未收款、逾期或已结清；收款单只记录客户，绝不按客户名、单号文本、日期或金额相似度自动关联本单。");
                break;
            case SalesOrderProgress.LinkUnknown:
                notes.Add("收款证据未知：指向本单的收款申请超过单次派生上限，金额不完整（不静默截断），不得当作已收 0 或未收全额。");
                break;
            case SalesOrderProgress.LinkPartial:
                notes.Add("收款证据部分可归属：他币种 / 未审核 / 非已审核的收款申请仅列出（不汇总、不换算），"
                    + "未覆盖金额只表示订单金额未被权威收款申请覆盖的部分，不是未收款金额，也不是逾期金额。");
                break;
            default:
                if (finance.OverReceived) notes.Add("已关联收款申请金额超过订单金额（超收），请人工核对。");
                break;
        }

        if (finance.UnattributedRecordCount > 0)
            notes.Add($"另有 {finance.UnattributedRecordCount} 条客户级记录（收款单 / 装柜结算单 / 散货结算单）只能按客户读取、没有订单级引用，"
                + "仅列出、不计入，也不自动匹配到本单。");
        notes.Add("本行不是应收余额、不是客户对账单、不是账龄：不推算账期与到期日，也不代表已结算或已收讫。");
        return string.Join(" ", notes);
    }

    /// <summary>
    /// 本页「客户 + 币种」分组：金额只在同客户 + 同币种内汇总；数量类合计只要存在未知行就记 null（未知，不是 0）。
    /// </summary>
    private static List<SalesOrderReceiptReconciliationGroup> BuildGroups(
        List<SalesOrderReceiptReconciliationOrderRow> rows)
        => rows.GroupBy(r => new { r.CustomerId, r.Currency })
            .OrderBy(g => g.Key.CustomerId).ThenBy(g => g.Key.Currency)
            .Select(g =>
            {
                var list = g.ToList();
                var linked = list.Count(r =>
                    r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageLinked);
                var partial = list.Count(r =>
                    r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoveragePartial);
                var unlinked = list.Count(r =>
                    r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageUnlinked);
                var coverageUnknown = list.Count(r =>
                    r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageUnknown);

                var notes = new List<string>();
                var cancelled = list.Count(r => r.Status == DocumentStatus.Cancelled.ToString());
                if (cancelled > 0)
                    notes.Add($"{cancelled} 张已取消订单：仅在显式选择订单状态筛选时可见，其数量 / 金额仅作历史参考，不并入有效订单口径");
                if (unlinked > 0)
                    notes.Add($"{unlinked} 张订单没有权威收款引用：已关联收款金额与未覆盖金额为未知（不是 0），不代表未收 / 逾期");
                if (partial > 0)
                    notes.Add($"{partial} 张订单存在他币种 / 未审核收款申请（仅列出、不计入、不换算）");
                if (coverageUnknown > 0)
                    notes.Add($"{coverageUnknown} 张订单的收款证据命中派生上限（金额不完整，不静默截断）");
                notes.Add($"仅统计本页「该客户 + {g.Key.Currency}」分组：金额绝不与其它币种合并，也不与未关联收款证据相加");

                var customerName = list.Select(r => r.CustomerName).FirstOrDefault(n => n.Length > 0);
                return new SalesOrderReceiptReconciliationGroup
                {
                    CustomerId = g.Key.CustomerId,
                    CustomerName = string.IsNullOrEmpty(customerName) ? string.Empty : customerName,
                    Currency = g.Key.Currency,
                    OrderCount = list.Count,
                    OrderAmount = list.Sum(r => r.OrderAmount),
                    OrderedQuantity = list.Where(r => r.OrderedQuantity.HasValue).Sum(r => r.OrderedQuantity!.Value),
                    ShippedQuantity = AllKnownSum(list.Select(r => r.ShippedQuantity)),
                    OutstandingQuantity = AllKnownSum(list.Select(r => r.OutstandingQuantity)),
                    LinkedOrderCount = linked,
                    PartialOrderCount = partial,
                    UnlinkedOrderCount = unlinked,
                    UnknownCoverageOrderCount = coverageUnknown,
                    CancelledOrderCount = cancelled,
                    ShippedOrderCount = list.Count(r => r.HasApprovedShipment),
                    UnshippedOrderCount = list.Count(r => !r.HasApprovedShipment),
                    UnknownShipmentOrderCount = list.Count(r => r.ShipmentStatus == SalesOrderProgress.ShipmentUnknown),
                    LinkedReceiptAmount = SumKnownMoney(list.Select(r => r.LinkedReceiptAmount)),
                    UncoveredAmount = SumKnownMoney(list.Select(r => r.UncoveredAmount)),
                    PendingReceiptAmount = SumKnownMoney(list.Select(r => r.PendingReceiptAmount)),
                    Orders = list,
                    Note = string.Join("；", notes),
                };
            })
            .ToList();

    /// <summary>
    /// 本页按币种的订单侧汇总：同币种内跨客户汇总，不同币种分别成行（不做汇率换算、不产生跨币种总额）。
    /// </summary>
    private static List<SalesOrderReceiptReconciliationCurrencySummary> BuildCurrencySummaries(
        List<SalesOrderReceiptReconciliationOrderRow> rows)
        => rows.GroupBy(r => r.Currency)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var list = g.ToList();
                return new SalesOrderReceiptReconciliationCurrencySummary
                {
                    Currency = g.Key,
                    CustomerCount = list.Select(r => r.CustomerId).Distinct().Count(),
                    OrderCount = list.Count,
                    OrderAmount = list.Sum(r => r.OrderAmount),
                    LinkedOrderCount = list.Count(r =>
                        r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageLinked),
                    PartialOrderCount = list.Count(r =>
                        r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoveragePartial),
                    UnlinkedOrderCount = list.Count(r =>
                        r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageUnlinked),
                    UnknownCoverageOrderCount = list.Count(r =>
                        r.ReceiptCoverageStatus == SalesOrderReceiptReconciliationSemantics.CoverageUnknown),
                    CancelledOrderCount = list.Count(r => r.Status == DocumentStatus.Cancelled.ToString()),
                    ShippedOrderCount = list.Count(r => r.HasApprovedShipment),
                    UnshippedOrderCount = list.Count(r => !r.HasApprovedShipment),
                    UnknownShipmentOrderCount = list.Count(r => r.ShipmentStatus == SalesOrderProgress.ShipmentUnknown),
                    LinkedReceiptAmount = SumKnownMoney(list.Select(r => r.LinkedReceiptAmount)),
                    UncoveredAmount = SumKnownMoney(list.Select(r => r.UncoveredAmount)),
                    PendingReceiptAmount = SumKnownMoney(list.Select(r => r.PendingReceiptAmount)),
                    OrderedQuantity = list.Where(r => r.OrderedQuantity.HasValue).Sum(r => r.OrderedQuantity!.Value),
                    ShippedQuantity = AllKnownSum(list.Select(r => r.ShippedQuantity)),
                    OutstandingQuantity = AllKnownSum(list.Select(r => r.OutstandingQuantity)),
                };
            })
            .ToList();

    /// <summary>
    /// 未关联收款证据按币种的汇总：每张收款单只按自己的币种汇总，不同币种分别成行、绝不合并；
    /// 命中单次上限时金额记 null（未知，不静默给出不完整数字）。
    /// </summary>
    private static List<SalesOrderReceiptReconciliationReceiptCurrencySummary> BuildReceiptCurrencySummaries(
        List<SalesOrderReceiptReconciliationReceipt> receipts, bool truncated)
        => receipts.GroupBy(r => r.Currency)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var list = g.ToList();
                var active = list
                    .Where(r => r.EvidenceStatus == SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive).ToList();
                var pending = list
                    .Where(r => r.EvidenceStatus == SalesOrderReceiptReconciliationSemantics.ReceiptStatusPending).ToList();
                var historical = list
                    .Where(r => r.EvidenceStatus == SalesOrderReceiptReconciliationSemantics.ReceiptStatusHistorical).ToList();
                return new SalesOrderReceiptReconciliationReceiptCurrencySummary
                {
                    Currency = g.Key,
                    CustomerCount = list.Select(r => r.CustomerId).Distinct().Count(),
                    ReceiptCount = list.Count,
                    ActiveReceiptCount = active.Count,
                    ActiveReceiptAmount = truncated ? null : active.Sum(r => r.Amount),
                    PendingReceiptCount = pending.Count,
                    PendingReceiptAmount = truncated ? null : pending.Sum(r => r.Amount),
                    HistoricalReceiptCount = historical.Count,
                    HistoricalReceiptAmount = truncated ? null : historical.Sum(r => r.Amount),
                    Truncated = truncated,
                    Note = truncated
                        ? "命中收款单单次查询上限：以上张数与金额不完整（不静默截断），请收窄客户 / 币种筛选或减少每页订单数。"
                        : "未关联收款证据：收款单没有任何订单级引用，金额只按本币种汇总、绝不并入订单侧金额，"
                            + "也不得当作已收款、已结算或应收余额；未审核与历史金额单列、不计入有效合计。",
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

    /// <summary>未关联收款证据的读取结果（Rows 有界；Truncated = 命中单次上限，数量与金额不完整）</summary>
    private sealed record UnlinkedReceiptResult(List<FinanceReceipt> Rows, bool Truncated);
}

