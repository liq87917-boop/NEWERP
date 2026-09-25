using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购订单「已分配付款引用证据」口径常量（ERP-067）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免把「付款单 → 采购发票」的引用证据读成已付款 / 已结算 / 应付余额 / 逾期，也避免把已作废 / 发票失效 / 无效 /
/// 无法确认的引用行混进有效合计。
/// <para>定位：<b>采购订单维度的「已分配付款引用证据」只读视图</b>（证据来自 ERP-066 的持久化
/// 「付款单 → 供应商采购发票」引用行，经 ERP-043 / ERP-065 的「发票 → 采购订单」关联行归属到本订单）——
/// 它<strong>不是</strong>总账、<strong>不是</strong>法定供应商对账单、<strong>不是</strong>税务申报、
/// <strong>不是</strong>付款授权或结算确认，也<strong>不是</strong>银行付款凭证。</para>
/// <para>四类证据严格分列（绝不相互轧差）：订单金额（已订）/ 收货数量（已收，见执行进度）/ 供应商发票证据（已开票）/
/// 已分配付款引用证据（本视图）。</para>
/// </summary>
public static class PurchaseOrderInvoicePaymentEvidenceSemantics
{
    // ==================== 1. 有界上限 ====================

    /// <summary>列表批量查询最多接受的采购订单张数（列表按页取 Id；超出直接拒绝，不静默截断）</summary>
    public const int MaxBatchOrders = 200;

    /// <summary>
    /// 列表批量查询最多读取的持久化引用行数（有界，含溢出探测）：命中上限时本次响应内全部金额与计数按「未知」返回，
    /// 绝不报出「只统计了一部分证据」的合计。
    /// </summary>
    public const int MaxBatchEvidenceRows = 5000;

    /// <summary>单张采购订单详情最多读取的持久化引用行数（有界）：命中上限时金额与计数按「未知」返回，明细只显示已读取部分</summary>
    public const int MaxOrderEvidenceRows = 500;

    /// <summary>
    /// 报表侧（ERP-044 对账报表）一次最多聚合的发票张数（有界）：
    /// 超出时该批发票的已分配付款引用证据一律按「未知」（null）处理，绝不给部分合计。
    /// </summary>
    public const int MaxAggregateInvoices = 500;

    // ==================== 2. 证据分桶 ====================

    /// <summary>有效已分配付款引用证据（引用行有效、付款单可用、发票仍为已登记、快照与供应商 / 币种自相一致）：唯一计入有效合计的分桶</summary>
    public const string BucketRecorded = "recorded";

    /// <summary>已作废引用行（历史证据：保留原始值与作废原因，绝不计入有效合计）</summary>
    public const string BucketVoided = "voided";

    /// <summary>发票已失效（引用行仍有效，但引用的发票已草稿 / 已作废：发票侧证据不再有效，绝不计入有效合计）</summary>
    public const string BucketInvoiceInactive = "invoice_inactive";

    /// <summary>无效历史证据（供应商 / 币种不一致或引用行快照自相矛盾）：不换算、不合并、不改派，绝不计入有效合计</summary>
    public const string BucketInvalid = "invalid";

    /// <summary>无法确认的证据（引用的付款单或发票不存在 / 已删除）：金额不能确认，绝不并入有效合计</summary>
    public const string BucketUnavailable = "unavailable";

    /// <summary>支持的分桶取值（超出范围不静默兜底）</summary>
    public static readonly string[] SupportedBuckets =
        { BucketRecorded, BucketVoided, BucketInvoiceInactive, BucketInvalid, BucketUnavailable };

    /// <summary>分桶文案（接口、界面与文档同源）</summary>
    public static string BucketText(string bucket) => bucket switch
    {
        BucketRecorded => "有效已分配付款引用证据（计入有效合计）",
        BucketVoided => "已作废引用行（历史证据，不计入有效合计）",
        BucketInvoiceInactive => "发票已失效（引用行仍有效但发票已草稿 / 已作废：不计入有效合计）",
        BucketInvalid => "无效历史证据（供应商 / 币种或快照不一致：不换算、不合并、不改派）",
        _ => "无法确认的证据（付款单或发票不存在 / 已删除：金额无法确认）",
    };

    /// <summary>是否属于「历史 / 无效证据」（= 不计入有效合计的分桶）</summary>
    public static bool IsHistoricalBucket(string bucket)
        => bucket is BucketVoided or BucketInvoiceInactive or BucketInvalid or BucketUnavailable;

    // ==================== 3. 证据短标签 ====================

    /// <summary>有有效已分配付款引用证据</summary>
    public const string LabelRecorded = "有已分配付款引用证据";

    /// <summary>只有历史 / 无效已分配付款引用证据（绝不呈现为已付款 / 已结算）</summary>
    public const string LabelHistoricalOnly = "仅有历史 / 无效已分配付款引用证据";

    /// <summary>没有任何已分配付款引用证据（证据缺口，绝不呈现为未付款 / 已付款 / 逾期 / 欠款）</summary>
    public const string LabelNone = "无已分配付款引用证据";

    /// <summary>命中有界上限，证据无法穷尽</summary>
    public const string LabelUnknown = "未知（超出有界读取上限）";

    /// <summary>
    /// 证据短标签（界面与接口同源）：命中上限 → 未知；有有效证据 → 有已分配付款引用证据；
    /// 只有历史 / 无效证据 → 仅有历史 / 无效已分配付款引用证据；一条都没有 → 无已分配付款引用证据。
    /// </summary>
    public static string EvidenceLabel(bool truncated, decimal? activeAmount, bool hasAnyRow)
    {
        if (truncated) return LabelUnknown;
        if (activeAmount is > 0m) return LabelRecorded;
        return hasAnyRow ? LabelHistoricalOnly : LabelNone;
    }

    // ==================== 4. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（归属规则、分桶与未知处理）</summary>
    public const string RuleText =
        "本视图是采购订单的**已分配付款引用证据**口径（只读派生）：证据只来自 ERP-066 的持久化「付款单 → 供应商采购发票」引用行，"
        + "并只经 ERP-043 / ERP-065 的持久化「发票 → 采购订单」关联行把该发票归到本订单；系统绝不按单号、金额、日期或文本相似度猜测归属，"
        + "也不新增第二套对账引擎（分桶与资格判定复用 ERP-066 的权威规则）。"
        + "「订单归属金额」只把**仅关联到本订单**（该发票的有效关联行只指向本订单）的发票上的有效引用行计入；"
        + "当同一张发票还被其他采购订单关联时，该发票上的引用金额只作为**发票级金额**单列（不按订单拆分、不按比例摊派、不重复计入任何订单的订单归属金额）；"
        + "已作废引用行、发票已草稿 / 已作废、无效证据（供应商 / 币种或快照不一致）与无法确认证据（付款单或发票已删除）一律单独分桶，"
        + "绝不计入有效合计，也绝不折算、合并或改派；未分配金额与缺失证据一律按「无」或「未知」显示，绝不当成未付款、已付款、已结清、逾期或可抵扣。";

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public const string ScopeText =
        "有界读取：列表批量接口一次最多 200 张采购订单、最多读取 5000 条持久化引用行；单张订单详情最多读取 500 条引用行。"
        + "命中上限时，本次响应的金额与计数一律按「未知」返回（不报出部分合计），并在说明中给出原因；"
        + "读取为固定次数数据集访问（订单 + 订单侧关联行 + 发票 + 发票侧关联行 + 引用行 + 付款单 + 付款单侧有效合计），"
        + "与订单张数 / 行数无关，绝无逐行查库；全程只读，本视图不重算、不改写任何单据。";

    /// <summary>
    /// 与总账 / 法定供应商对账单 / 税务申报 / 付款授权 / 结算确认的边界说明（界面与文档同源）。
    /// </summary>
    public const string BoundaryText =
        "本视图只是**运营性的证据视图**：不是总账或应付账款余额、不是法定供应商对账单、不是税务（进项）申报或抵扣判断、"
        + "不是付款授权与付款执行、也不是结算确认或账龄表 —— 它不判断是否已付款、是否已结清或是否逾期，不推算账期与到期日，"
        + "不做账龄分摊与税负计算，也不产生任何记账、凭证、收付款或结算单；"
        + "「订单金额」「已开票金额」「已分配付款引用金额」是三类彼此独立的证据，绝不相加减、绝不轧差成一个应付余额或结算余额，"
        + "界面与接口一律把金额标注为**证据**，不得当作欠款金额、可付款金额或已付款金额。";

    /// <summary>证据维度分离说明（付款 → 发票 / 付款 → 订单 / 发票 → 订单 三个登记册互不合并）</summary>
    public const string SeparateDimensionText =
        "证据维度分离：ERP-066 的「付款单 → 采购发票」引用行、ERP-049 的「付款单 → 采购订单」引用行与 "
        + "ERP-043 / ERP-065 的「发票 → 采购订单」关联行是三个彼此独立的证据维度，绝不互相回写，也绝不把不同维度的金额相加后"
        + "当成「另一笔付款」、应付余额或结算结果。";

    /// <summary>无任何已分配付款引用证据时的提示（绝不能被读成未付款 / 已付款 / 已结清 / 逾期）</summary>
    public const string NoEvidenceNote =
        "该采购订单没有任何登记「付款单 → 采购发票」引用行：这是**已分配付款引用证据缺口**，不代表未付款、已付款、已结清、逾期或欠款，"
        + "也不构成应付余额、付款授权或结算依据。";

    /// <summary>命中有界上限时的提示（金额未知，不给部分合计）</summary>
    public const string TruncatedNote =
        "本次读取命中系统有界上限（持久化引用行数量超出上限），无法穷尽该订单的已分配付款引用证据："
        + "金额与计数一律按「未知」显示，不作部分合计。";

    /// <summary>无可用订单金额时的提示（订单已取消或不可用）</summary>
    public const string UnknownOrderNote =
        "采购订单已取消或已删除：订单金额按未知显示（不按 0 处理），也不推断付款、结算或逾期状态。";

    /// <summary>发票被多张订单共同关联时的归属说明（只作发票级金额，不按订单拆分）</summary>
    public const string NotAttributableNote =
        "其中部分引用行所在发票还被其他采购订单关联：这部分只作**发票级金额**单列（系统不按比例摊派、不猜测订单归属），"
        + "不计入本订单的订单归属金额，也不代表本订单已付款。";
}

/// <summary>
/// 采购订单 Id 列表查询参数（ERP-067，只读）：逗号分隔的正整数，去重、保序、有界（超出上限直接拒绝）。
/// <para>与 ERP-048 / ERP-050 的批量查询参数同口径，便于列表页复用同一套分批策略。</para>
/// </summary>
public sealed class PurchaseOrderInvoicePaymentEvidenceQuery
{
    /// <summary>逗号分隔的采购订单 Id（如 <c>12,34,56</c>；留空 = 不查询任何订单）</summary>
    public string? Ids { get; set; }

    /// <summary>归一化后的订单 Id（去重、保持提交顺序）</summary>
    public IReadOnlyList<long> OrderIds => _orderIds;

    private readonly List<long> _orderIds = new();

    /// <summary>
    /// 归一化并校验：只接受正整数 Id，去重（保留首次出现顺序）；非法片段或超出
    /// <see cref="PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchOrders"/> 一律拒绝，不静默丢弃、不静默截断。
    /// </summary>
    public void Normalize()
    {
        _orderIds.Clear();

        var raw = (Ids ?? string.Empty).Trim();
        if (raw.Length == 0) return;

        foreach (var token in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0) continue;

            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw BusinessException.InvalidParameter(
                    $"订单 Id「{token}」无效：已分配付款引用证据批量接口只接受逗号分隔的正整数采购订单 Id");

            if (_orderIds.Contains(id)) continue;
            _orderIds.Add(id);

            if (_orderIds.Count > PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchOrders)
                throw BusinessException.InvalidParameter(
                    $"一次最多查询 {PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchOrders} 张采购订单的已分配付款引用证据（有界查询）："
                    + "本次提交超过上限，请按页分批查询");
        }
    }
}

/// <summary>
/// 单张采购订单的「已分配付款引用证据」聚合（ERP-067，只读派生值；采购订单视图与 ERP-044 对账报表共用同一套分桶口径）。
/// <para><see cref="ActiveAmount"/> 是**发票级**有效金额（该订单关联发票上的全部有效引用行），
/// <see cref="AttributableAmount"/> 是其中可安全归属到本订单的部分（仅关联到本订单的发票）；
/// 有效 / 已作废 / 发票失效 / 无效 / 无法确认绝不合并。</para>
/// </summary>
public sealed record InvoicePaymentEvidenceAggregate(
    long PurchaseOrderId,
    decimal ActiveAmount,
    int ActiveCount,
    int ActivePaymentCount,
    decimal AttributableAmount,
    int AttributableInvoiceCount,
    int UnattributableInvoiceCount,
    decimal UnattributableAmount,
    int VoidedCount,
    decimal VoidedAmount,
    int InvoiceInactiveCount,
    decimal InvoiceInactiveAmount,
    int InvalidCount,
    decimal InvalidAmount,
    int UnavailableCount,
    decimal UnavailableAmount)
{
    /// <summary>该订单是否有任何持久化引用行（false = 已分配付款引用证据缺口）</summary>
    public bool HasAnyRow => ActiveCount + VoidedCount + InvoiceInactiveCount + InvalidCount + UnavailableCount > 0;

    /// <summary>该订单是否有历史 / 无效 / 无法确认证据（必须单独查看，绝不并入有效合计）</summary>
    public bool HasHistoricalRow => VoidedCount + InvoiceInactiveCount + InvalidCount + UnavailableCount > 0;
}

/// <summary>
/// 一批采购订单的「已分配付款引用证据」聚合结果（ERP-067，只读）：<see cref="Truncated"/> = 命中订单数 / 行数上限，
/// 此时调用方必须把金额与计数按「未知」处理，绝不使用部分数据。
/// </summary>
public sealed record InvoicePaymentEvidenceAggregateSet(
    IReadOnlyDictionary<long, InvoicePaymentEvidenceAggregate> ByOrder,
    bool Truncated)
{
    /// <summary>取某订单的聚合；订单无引用行或未参与聚合时返回全 0（订单可用性由调用方自行判定）</summary>
    public InvoicePaymentEvidenceAggregate Get(long orderId)
        => ByOrder.TryGetValue(orderId, out var aggregate) ? aggregate : Empty(orderId);

    /// <summary>全 0 聚合（无引用行；不代表未付款 / 已付款）</summary>
    public static InvoicePaymentEvidenceAggregate Empty(long orderId)
        => new(orderId, 0m, 0, 0, 0m, 0, 0, 0m, 0, 0m, 0, 0m, 0, 0m, 0, 0m);
}

/// <summary>
/// 单张供应商采购发票的「已分配付款引用证据」聚合（ERP-067，只读派生；ERP-044 对账报表的复用入口）。
/// <para>金额只按该发票的 ERP-066 持久化引用行派生：<see cref="ActiveAmount"/> 只统计有效引用行，
/// 已作废 / 发票失效 / 无效 / 无法确认分别单列；<see cref="UnallocatedPaymentAmount"/> 参与证据的付款单金额中
/// 未指向任何发票的部分（按付款单去重后派生，仅作上下文）。</para>
/// </summary>
public sealed record InvoiceAllocatedPaymentAggregate(
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
    /// <summary>该发票是否有任何持久化付款引用行（false = 已分配付款引用证据缺口）</summary>
    public bool HasAnyRow => ActiveCount + VoidedCount + InvoiceInactiveCount + InvalidCount + UnavailableCount > 0;

    /// <summary>该发票是否有历史 / 无效 / 无法确认证据（绝不并入有效合计）</summary>
    public bool HasHistoricalRow => VoidedCount + InvoiceInactiveCount + InvalidCount + UnavailableCount > 0;
}

/// <summary>
/// 一批供应商采购发票的「已分配付款引用证据」聚合结果（ERP-067；<see cref="Truncated"/> = 命中发票数 / 行数上限，
/// 此时对应发票的金额与计数必须按「未知」处理）。<see cref="Get"/> 返回 null 表示未知（不按 0 处理）。
/// </summary>
public sealed record InvoiceAllocatedPaymentAggregateSet(
    IReadOnlyDictionary<long, InvoiceAllocatedPaymentAggregate> ByInvoice,
    bool Truncated)
{
    /// <summary>取某发票的聚合；命中上限或未参与聚合时返回 null（未知，绝不按 0）</summary>
    public InvoiceAllocatedPaymentAggregate? Get(long invoiceId)
        => !Truncated && ByInvoice.TryGetValue(invoiceId, out var aggregate) ? aggregate : null;

    /// <summary>全 0 聚合（该发票没有任何引用行；不代表已付款 / 未付款）</summary>
    public static InvoiceAllocatedPaymentAggregate Empty(long invoiceId)
        => new(invoiceId, 0m, 0, 0, 0m, 0m, 0, 0m, 0, 0m, 0, 0m, 0, 0m);
}

/// <summary>
/// 采购订单「已分配付款引用证据」汇总（ERP-067，只读派生；列表列与详情头部共用）。
/// <para>金额口径：<see cref="ActiveAllocatedPaymentAmount"/> 只统计有效（未作废、发票仍为已登记、快照自相一致）的
/// 持久化引用行；<see cref="AttributableAllocatedPaymentAmount"/> 是其中可安全归属到本订单的部分；
/// 已作废 / 发票失效 / 无效 / 无法确认分别单列，绝不并入有效合计。</para>
/// <para>未知处理：命中读取上限或订单已取消 / 不可用时，金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 本汇总<strong>不是</strong>总账、法定供应商对账单、税务申报、付款授权或结算确认。</para>
/// </summary>
public sealed class PurchaseOrderInvoicePaymentEvidenceSummary
{
    /// <summary>采购订单 Id</summary>
    public long PurchaseOrderId { get; init; }

    /// <summary>采购单号（订单已删除时按持久化快照显示）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单当前是否可用（存在且未删除）</summary>
    public bool OrderAvailable { get; init; }

    /// <summary>订单可用性状态：available / cancelled / unavailable（与 ERP-044 同口径）</summary>
    public string OrderState { get; init; } = SupplierInvoiceReconciliationSemantics.OrderStateUnavailable;

    /// <summary>订单可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderStateText { get; init; } = string.Empty;

    /// <summary>订单当前单据状态文案（订单不存在 / 已删除时照实说明）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>订单币种（原币；不同币种绝不合并、不做汇率换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用；复用 ERP-043 的币种精度口径）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>采购订单已落库总额；null = 未知（订单不存在 / 已删除），不等于 0</summary>
    public decimal? OrderedAmount { get; init; }

    /// <summary>通过持久化关联行指向本订单的发票张数（含发票已删除的关联行）</summary>
    public int LinkedInvoiceCount { get; init; }

    /// <summary>是否读到至少一条持久化引用行（不代表有效证据，也不代表已付款）</summary>
    public bool HasEvidence { get; init; }

    /// <summary>是否存在已作废 / 发票失效 / 无效 / 无法确认的历史证据（必须单独查看，绝不并入有效合计）</summary>
    public bool HasHistoricalEvidence { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>有效已分配付款引用金额（发票级：本订单关联发票上的全部有效引用行合计，原币）；null = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedPaymentAmount { get; init; }

    /// <summary>可安全归属到本订单的有效金额（仅关联到本订单的发票上的有效引用行）；null = 未知（命中上限）</summary>
    public decimal? AttributableAllocatedPaymentAmount { get; init; }

    /// <summary>参与有效合计的持久化引用行条数；null = 未知（命中上限）</summary>
    public int? ActiveAllocationCount { get; init; }

    /// <summary>参与有效合计的付款单张数（按付款单去重）；null = 未知（命中上限）</summary>
    public int? ActivePaymentCount { get; init; }

    /// <summary>可归属有效金额涉及的发票张数（仅关联到本订单）；null = 未知（命中上限）</summary>
    public int? AttributableInvoiceCount { get; init; }

    /// <summary>只作发票级金额单列的发票张数（该发票还被其他采购订单关联）；null = 未知（命中上限）</summary>
    public int? UnattributableInvoiceCount { get; init; }

    /// <summary>只作发票级金额单列的金额（不按订单拆分、不摊派）；null = 未知（命中上限）</summary>
    public decimal? UnattributableAllocatedPaymentAmount { get; init; }

    /// <summary>已作废引用行条数（历史证据，仅可查看，绝不并入有效合计）；null = 未知</summary>
    public int? VoidedCount { get; init; }

    /// <summary>已作废引用行金额；null = 未知</summary>
    public decimal? VoidedAmount { get; init; }

    /// <summary>引用行仍有效但发票已草稿 / 已作废的条数（发票侧证据失效）；null = 未知</summary>
    public int? InvoiceInactiveCount { get; init; }

    /// <summary>发票已失效的引用金额；null = 未知</summary>
    public decimal? InvoiceInactiveAmount { get; init; }

    /// <summary>无效引用行条数（供应商 / 币种或快照不一致：不换算、不合并、不改派）；null = 未知</summary>
    public int? InvalidCount { get; init; }

    /// <summary>无效引用行金额；null = 未知</summary>
    public decimal? InvalidAmount { get; init; }

    /// <summary>无法确认的引用行条数（付款单或发票已删除 / 不存在）；null = 未知</summary>
    public int? UnavailableCount { get; init; }

    /// <summary>无法确认的引用金额；null = 未知</summary>
    public decimal? UnavailableAmount { get; init; }

    /// <summary>证据短标签（有 / 仅有历史无效 / 无 / 未知）</summary>
    public string EvidenceLabel { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.LabelUnknown;

    /// <summary>证据说明：已知什么 / 缺什么 / 为什么不能当总账、对账单、付款授权或结算凭证</summary>
    public string EvidenceNote { get; init; } = string.Empty;
}

/// <summary>
/// 一条「付款单 → 采购发票」已分配付款引用证据明细（ERP-067，只读派生）。
/// <para>金额按持久化引用行原样呈现（不重算、不四舍五入改写）；付款单 / 发票 / 供应商快照一律取自引用行本身的落库值，
/// 另附当前可用性与字段级不一致原因（<see cref="Reason"/>），绝不静默修复、改派或合并。</para>
/// </summary>
public sealed class PurchaseOrderInvoicePaymentEvidenceLine
{
    /// <summary>引用行 Id（ERP-066 <c>SupplierPaymentInvoiceAllocations.Id</c>）</summary>
    public long AllocationId { get; init; }

    /// <summary>付款单 Id（引用行指向的既有供应商付款单）</summary>
    public long PaymentId { get; init; }

    /// <summary>付款单号快照（付款单改名后仍按登记当时口径可读）</summary>
    public string PaymentNo { get; init; } = string.Empty;

    /// <summary>付款日期快照</summary>
    public DateTime PaymentDate { get; init; }

    /// <summary>付款单状态快照（DocumentStatus 取值）；null = 付款单缺失无法确认</summary>
    public int? PaymentStatus { get; init; }

    /// <summary>付款单状态文案（缺失时照实说明「无法确认」）</summary>
    public string PaymentStatusText { get; init; } = string.Empty;

    /// <summary>付款单金额快照（原币，仅作展示上下文，不做汇率换算）</summary>
    public decimal PaymentAmount { get; init; }

    /// <summary>
    /// 该付款单中未指向任何采购发票的金额（= 付款单金额快照 − 该付款单全部有效发票引用行合计，下限 0，仅作展示上下文）；
    /// null = 未知（付款单缺失 / 命中上限）。它不是银行未付金额、不是应付余额，也不扣除「付款 → 采购订单」引用金额。
    /// </summary>
    public decimal? PaymentUnallocatedAmount { get; init; }

    /// <summary>本行引用金额（原币；持久化行原样呈现）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>币种（原币；引用行币种，与付款单 / 发票币种一致才可能是有效证据）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>供应商 Id 快照（引用行写入时的服务端快照）</summary>
    public long SupplierId { get; init; }

    /// <summary>供应商编码快照</summary>
    public string SupplierCode { get; init; } = string.Empty;

    /// <summary>供应商名称快照</summary>
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>采购发票 Id（引用行指向的供应商采购发票）</summary>
    public long PurchaseInvoiceId { get; init; }

    /// <summary>发票类型快照（普票 / 专票 / 进口）</summary>
    public string InvoiceType { get; init; } = string.Empty;

    /// <summary>发票类型文案（与 ERP-065 同源；未知类型照实回显，不猜测）</summary>
    public string InvoiceTypeText { get; init; } = string.Empty;

    /// <summary>发票身份文案（类型 + 代码 − 号码；快照缺失时按既有口径回退，不臆造号码）</summary>
    public string InvoiceIdentityText { get; init; } = string.Empty;

    /// <summary>开票日期快照</summary>
    public DateTime InvoiceDate { get; init; }

    /// <summary>发票状态快照（0 草稿 / 1 已登记 / 2 已作废）；null = 发票缺失无法确认</summary>
    public int? InvoiceStatus { get; init; }

    /// <summary>发票状态文案</summary>
    public string InvoiceStatusText { get; init; } = string.Empty;

    /// <summary>发票含税总额快照（原币；仅作展示上下文）</summary>
    public decimal InvoiceGrossAmount { get; init; }

    /// <summary>发票当前币种（发票登记册的当前值；与引用行快照不一致即为无效证据，绝不换算）</summary>
    public string InvoiceCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>发票当前供应商（发票登记册的当前值；与引用行快照不一致即为无效证据，绝不改派）</summary>
    public long? InvoiceSupplierId { get; init; }

    /// <summary>
    /// 发票显式到期日（ERP-065 的**持久化**到期日）；null = 未提供（未知，不按供应商账期推算）。
    /// 本视图只用它作只读上下文，不做账龄分摊、不判断是否逾期。
    /// </summary>
    public DateTime? InvoiceDueDate { get; init; }

    /// <summary>到期日文案：显式到期日或「未提供（未知）」</summary>
    public string InvoiceDueDateText { get; init; } = string.Empty;

    /// <summary>发票付款条件快照（有界文本；未提供时留空，不解析、不推算账期）</summary>
    public string InvoicePaymentTerms { get; init; } = string.Empty;

    /// <summary>付款单当前是否可用（存在且未删除；**只读标注**）</summary>
    public bool PaymentAvailable { get; init; }

    /// <summary>付款单可用性文案（已删除时照实说明，历史证据仍可读）</summary>
    public string PaymentAvailabilityText { get; init; } = string.Empty;

    /// <summary>发票当前是否可用（存在、未删除且仍为已登记；**只读标注**）</summary>
    public bool InvoiceAvailable { get; init; }

    /// <summary>发票可用性文案（草稿 / 已作废 / 已删除时照实说明，历史证据仍可读）</summary>
    public string InvoiceAvailabilityText { get; init; } = string.Empty;

    /// <summary>引用行状态（1 有效 / 2 已作废；未知取值照实说明）</summary>
    public int Status { get; init; }

    /// <summary>引用行状态文案</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>是否有效证据（唯一计入有效合计的证据类型）</summary>
    public bool IsRecordedEvidence { get; init; }

    /// <summary>是否已作废（历史证据，保留可读）</summary>
    public bool IsVoided { get; init; }

    /// <summary>证据分桶：recorded / voided / invoice_inactive / invalid / unavailable</summary>
    public string Bucket { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable;

    /// <summary>分桶文案</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>本行是否可安全归属到本采购订单（发票仅关联到本订单）；false = 只作发票级金额，不按订单拆分</summary>
    public bool OrderAttributable { get; init; }

    /// <summary>归属说明（可归属 / 发票级金额 / 为什么不能归属）</summary>
    public string AttributionText { get; init; } = string.Empty;

    /// <summary>登记时间（引用行写入时间）</summary>
    public DateTime AllocatedAt { get; init; }

    /// <summary>作废时间（未作废为空）</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因（作废必须留痕）</summary>
    public string VoidReason { get; init; } = string.Empty;

    /// <summary>登记备注（只作说明，不参与任何金额派生）</summary>
    public string Remark { get; init; } = string.Empty;

    /// <summary>本行说明：为什么计入 / 不计入有效合计（无效时给出权威不一致原因）</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// 采购订单「已分配付款引用证据」详情（ERP-067，只读派生）：汇总 + 逐条证据明细 + 口径文案。
/// <para><see cref="Lines"/> 为有界明细（最多 <see cref="PurchaseOrderInvoicePaymentEvidenceSemantics.MaxOrderEvidenceRows"/> 条）；
/// 命中上限时汇总金额与计数按未知显示，明细仅展示已读取部分。</para>
/// </summary>
public sealed class PurchaseOrderInvoicePaymentEvidenceDetail
{
    /// <summary>汇总（与该订单列表列同口径）</summary>
    public PurchaseOrderInvoicePaymentEvidenceSummary Summary { get; init; } = new();

    /// <summary>逐条持久化引用行证据（有界：最多 500 条）</summary>
    public List<PurchaseOrderInvoicePaymentEvidenceLine> Lines { get; init; } = new();

    /// <summary>本次返回的明细条数</summary>
    public int LineCount { get; init; }

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.ScopeText;

    /// <summary>与总账 / 法定对账单 / 税务申报 / 付款授权 / 结算确认的边界说明</summary>
    public string Boundary { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.BoundaryText;

    /// <summary>证据维度分离说明（与 ERP-066 同源）</summary>
    public string SeparateDimension { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.SeparateDimensionText;

    /// <summary>引用行登记口径（与 ERP-066 同源，不引入第二套匹配算法）</summary>
    public string AllocationRule { get; init; } = SupplierPaymentInvoiceAllocationRules.RuleText;

    /// <summary>引用行登记册自身的模块边界（与 ERP-066 同源）</summary>
    public string AllocationBoundary { get; init; } = SupplierPaymentInvoiceAllocationRules.BoundaryText;

    /// <summary>发票 → 采购订单 关联口径（与 ERP-043 / ERP-065 同源）</summary>
    public string InvoiceLinkageRule { get; init; } = PurchaseInvoiceRules.LinkageRuleText;

    /// <summary>三个证据维度互不合并的声明（与 ERP-066 同源）</summary>
    public string EvidenceDimensionRule { get; init; } = SupplierPaymentInvoiceAllocationRules.SeparateEvidenceText;

    /// <summary>发票金额等式口径（与 ERP-043 同源）</summary>
    public string AmountEquation { get; init; } = PurchaseInvoiceRules.AmountEquationText;
}

/// <summary>
/// 采购订单「已分配付款引用证据」批量汇总（ERP-067，只读派生；列表页一次请求取回本页订单的汇总，不逐行查库）。
/// </summary>
public sealed class PurchaseOrderInvoicePaymentEvidenceBatch
{
    /// <summary>按请求顺序返回的订单汇总（未知订单也会返回「未知」汇总，不静默丢行）</summary>
    public List<PurchaseOrderInvoicePaymentEvidenceSummary> Items { get; init; } = new();

    /// <summary>本次返回的订单数</summary>
    public int ItemCount { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 全部金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.ScopeText;

    /// <summary>边界说明（不是总账 / 法定对账单 / 税务申报 / 付款授权 / 结算确认）</summary>
    public string Boundary { get; init; } = PurchaseOrderInvoicePaymentEvidenceSemantics.BoundaryText;
}

/// <summary>
/// 采购订单「已分配付款引用证据」派生（ERP-067，只读）。
/// <para>范围：证据只来自 ERP-066 的持久化「付款单 → 供应商采购发票」引用行，并只经 ERP-043 / ERP-065 的持久化
/// 「发票 → 采购订单」关联行归属到订单；**不重算、不改单据、不落库、不写库**。</para>
/// <para>查询有界：固定次数数据集访问（订单 + 订单侧关联行 + 发票 + 发票侧关联行 + 引用行 + 付款单 + 付款单侧有效合计），
/// 与订单张数 / 行数无关；命中上限一律按「未知」处理，绝不给部分合计。</para>
/// </summary>
public static class PurchaseOrderInvoicePaymentEvidence
{
    // ==================== 1. 单张采购订单 ====================

    /// <summary>
    /// 单张采购订单的已分配付款引用证据详情（只读派生）：订单不存在 / 已删除时按「未知」汇总返回
    /// （金额与计数为未知，绝不用 0 顶替），保证列表与详情行为一致。
    /// </summary>
    public static async Task<PurchaseOrderInvoicePaymentEvidenceDetail> ForOrderAsync(
        IErpDbContext db, long orderId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orderId <= 0)
            throw BusinessException.InvalidParameter("采购订单 Id 必须为正整数");

        var built = await BuildAsync(db, new[] { orderId },
            PurchaseOrderInvoicePaymentEvidenceSemantics.MaxOrderEvidenceRows);
        var evidence = built.ByOrder.TryGetValue(orderId, out var found)
            ? found
            : new OrderEvidence(BuildSummaryForMissingOrder(orderId, built.Truncated),
                new List<PurchaseOrderInvoicePaymentEvidenceLine>(), InvoicePaymentEvidenceAggregateSet.Empty(orderId));

        return new PurchaseOrderInvoicePaymentEvidenceDetail
        {
            Summary = evidence.Summary,
            Lines = evidence.Lines,
            LineCount = evidence.Lines.Count,
        };
    }

    // ==================== 2. 一批采购订单（列表页共用） ====================

    /// <summary>
    /// 一批采购订单的已分配付款引用证据汇总（列表页一次请求取回本页订单的汇总，不逐行查库）。
    /// <para>订单 Id 非法或超过 <see cref="PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchOrders"/> 一律拒绝；
    /// 订单不存在时也返回「未知」汇总，不静默丢行。</para>
    /// </summary>
    public static async Task<PurchaseOrderInvoicePaymentEvidenceBatch> ForOrdersAsync(
        IErpDbContext db, PurchaseOrderInvoicePaymentEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        query.Normalize();
        var orderIds = query.OrderIds;
        if (orderIds.Count == 0)
            return new PurchaseOrderInvoicePaymentEvidenceBatch();

        var built = await BuildAsync(db, orderIds,
            PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchEvidenceRows);

        var items = new List<PurchaseOrderInvoicePaymentEvidenceSummary>(orderIds.Count);
        foreach (var orderId in orderIds)
        {
            items.Add(built.ByOrder.TryGetValue(orderId, out var found)
                ? found.Summary
                : BuildSummaryForMissingOrder(orderId, built.Truncated));
        }

        return new PurchaseOrderInvoicePaymentEvidenceBatch
        {
            Items = items,
            ItemCount = items.Count,
            Truncated = built.Truncated,
        };
    }

    // ==================== 3. 发票级聚合（ERP-044 对账报表复用入口） ====================

    /// <summary>
    /// 一批供应商采购发票的已分配付款引用证据聚合（ERP-044 对账报表复用入口，只读有界）：
    /// 与采购订单视图**共用同一套分桶与资格判定**，避免出现第二套对账算法。
    /// <para>命中发票数上限或行数上限时 <see cref="InvoiceAllocatedPaymentAggregateSet.Truncated"/> 为 true，
    /// 调用方必须把相关金额与计数按「未知」处理（本次结果<strong>不含</strong>部分合计）。</para>
    /// </summary>
    public static async Task<InvoiceAllocatedPaymentAggregateSet> AggregatesForInvoicesAsync(
        IErpDbContext db, IReadOnlyList<long> invoiceIds,
        int invoiceCap = PurchaseOrderInvoicePaymentEvidenceSemantics.MaxAggregateInvoices)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(invoiceIds);

        var cap = invoiceCap <= 0 ? PurchaseOrderInvoicePaymentEvidenceSemantics.MaxAggregateInvoices : invoiceCap;
        var ids = invoiceIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
            return new InvoiceAllocatedPaymentAggregateSet(new Dictionary<long, InvoiceAllocatedPaymentAggregate>(), false);

        if (ids.Count > cap)
            return new InvoiceAllocatedPaymentAggregateSet(new Dictionary<long, InvoiceAllocatedPaymentAggregate>(), true);

        var rowCap = PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchEvidenceRows;

        var rows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && ids.Contains(a.PurchaseInvoiceId))
            .OrderBy(a => a.PurchaseInvoiceId).ThenBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .Take(rowCap + 1)
            .ToListAsync();
        var truncated = rows.Count > rowCap;
        if (truncated) rows.RemoveRange(rowCap, rows.Count - rowCap);

        var invoiceById = (await db.PurchaseInvoices.AsNoTracking()
                .Where(i => ids.Contains(i.Id)).ToListAsync())
            .ToDictionary(i => i.Id);

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var paymentById = paymentIds.Count == 0
            ? new Dictionary<long, FinancePayment>()
            : (await db.FinancePayments.AsNoTracking()
                    .Where(p => paymentIds.Contains(p.Id)).ToListAsync())
                .ToDictionary(p => p.Id);

        var activeTotalByPayment = await ActiveInvoiceAllocationTotalsAsync(db, paymentIds);

        var byInvoice = new Dictionary<long, InvoiceAllocatedPaymentAggregate>();
        foreach (var group in rows.GroupBy(r => r.PurchaseInvoiceId))
        {
            byInvoice[group.Key] = BuildInvoiceAggregate(group.Key, group.ToList(), invoiceById,
                paymentById, activeTotalByPayment);
        }

        return new InvoiceAllocatedPaymentAggregateSet(byInvoice, truncated);
    }

    // ==================== 内部：批量读取与派生 ====================

    /// <summary>单张订单的证据派生结果（汇总 + 明细行 + 供报表复用的聚合）</summary>
    private sealed record OrderEvidence(PurchaseOrderInvoicePaymentEvidenceSummary Summary,
        List<PurchaseOrderInvoicePaymentEvidenceLine> Lines, InvoicePaymentEvidenceAggregate Aggregate);

    /// <summary>一次批量派生的读取结果（按订单 Id 索引；<see cref="Truncated"/> = 命中行数上限）</summary>
    private sealed record BuildResult(Dictionary<long, OrderEvidence> ByOrder, bool Truncated);

    /// <summary>付款单侧有效（未作废）发票引用合计（数据库侧分组聚合结果，只读）</summary>
    private sealed record PaymentActiveTotalRow(long PaymentId, decimal Amount);

    /// <summary>付款单当前币种与本次有效引用金额（仅用于「未指向任何发票」的上下文派生，绝不跨币种相加）</summary>
    private sealed record ActivePaymentInfo(string Currency, decimal Amount);

    /// <summary>单条引用行的分桶累计（金额 / 行数 / 付款单去重）</summary>
    private sealed class BucketTotals
    {
        /// <summary>分桶金额合计（原币，按持久化引用行汇总）</summary>
        public decimal Amount { get; private set; }

        /// <summary>分桶内的持久化引用行条数</summary>
        public int RowCount { get; private set; }

        /// <summary>分桶内的付款单 Id（去重；用于「付款单张数」计数）</summary>
        public HashSet<long> PaymentIds { get; } = new();

        /// <summary>累计一条引用行</summary>
        public void Add(decimal amount, long paymentId)
        {
            Amount += amount;
            RowCount++;
            if (paymentId > 0) PaymentIds.Add(paymentId);
        }
    }

    /// <summary>全部为空的分桶字典（每个受支持分桶都有条目，避免键缺失时的隐式兜底）</summary>
    private static Dictionary<string, BucketTotals> EmptyTotals()
    {
        var totals = new Dictionary<string, BucketTotals>(StringComparer.Ordinal);
        foreach (var bucket in PurchaseOrderInvoicePaymentEvidenceSemantics.SupportedBuckets)
            totals[bucket] = new BucketTotals();
        return totals;
    }

    /// <summary>付款单侧有效（未作废）发票引用合计：数据库侧分组聚合，不装载明细行（有界，与行数无关）</summary>
    private static async Task<Dictionary<long, PaymentActiveTotalRow>> ActiveInvoiceAllocationTotalsAsync(
        IErpDbContext db, IReadOnlyList<long> paymentIds)
    {
        if (paymentIds.Count == 0) return new Dictionary<long, PaymentActiveTotalRow>();

        var totals = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive
                        && paymentIds.Contains(a.PaymentId))
            .GroupBy(a => a.PaymentId)
            .Select(g => new PaymentActiveTotalRow(g.Key, g.Sum(a => a.AllocatedAmount)))
            .ToListAsync();

        return totals.ToDictionary(t => t.PaymentId);
    }

    /// <summary>
    /// 逐条引用行分桶（权威判定顺序）：已作废 → 无法确认（付款单 / 发票缺失或已删除）→ 发票失效（草稿 / 已作废）→
    /// 无效（供应商 / 币种 / 快照不一致）→ 有效；每个分桶都给出**为什么**（不静默修复、不换算、不改派）。
    /// </summary>
    private static (string Bucket, string Reason) ClassifyRow(
        SupplierPaymentInvoiceAllocation row, FinancePayment? payment, PurchaseInvoice? invoice)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);

        if (row.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided,
                "引用行已作废：原始值、快照与作废原因保留可读，但不计入有效合计，也不代表付款被撤销。");

        if (row.Status != SupplierPaymentInvoiceAllocationRules.StatusActive)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用行状态「{row.Status}」不在有效范围内（仅 1 有效 / 2 已作废），无法确认其有效性：不计入有效合计。");

        if (payment is null || payment.IsDeleted)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable,
                "引用行指向的付款单不存在或已删除：金额无法确认，不计入有效合计，也不代表未付款或已付款。");

        if (invoice is null || invoice.IsDeleted)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable,
                "引用行指向的采购发票不存在或已删除：金额无法确认，不计入有效合计。");

        if (invoice.Status == PurchaseInvoiceRules.StatusDraft)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive,
                "引用的采购发票仍为草稿（尚未登记为证据）：发票侧证据不成立，不计入有效合计。");

        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive,
                "引用的采购发票已作废：发票侧证据失效，其金额仅作历史核对，不计入有效合计。");

        if (row.AllocatedAmount <= 0)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用金额 {row.AllocatedAmount} 非正数：无效证据，不计入有效合计。");

        if (row.AllocatedAmount > row.PaymentAmount)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用金额 {row.AllocatedAmount} 大于引用行付款单金额快照 {row.PaymentAmount}：快照自相矛盾，无效证据，不计入有效合计。");

        if (row.AllocatedAmount > row.InvoiceGrossAmount)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用金额 {row.AllocatedAmount} 大于引用行发票含税总额快照 {row.InvoiceGrossAmount}：快照自相矛盾，无效证据，不计入有效合计。");

        var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        if (!string.Equals(currency, paymentCurrency, StringComparison.Ordinal))
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用行币种 {currency} 与付款单币种 {paymentCurrency} 不一致：不换算、不合并、不改派，无效证据。");

        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        if (!string.Equals(currency, invoiceCurrency, StringComparison.Ordinal))
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用行币种 {currency} 与发票币种 {invoiceCurrency} 不一致：不换算、不合并、不改派，无效证据。");

        if (row.SupplierId != payment.SupplierId)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用行供应商快照 Id={row.SupplierId} 与付款单供应商 Id={payment.SupplierId} 不一致：不改派、不合并，无效证据。");

        if (row.SupplierId != invoice.SupplierId)
            return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                $"引用行供应商快照 Id={row.SupplierId} 与发票供应商 Id={invoice.SupplierId} 不一致：不改派、不合并，无效证据。");

        return (PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded,
            "有效证据：引用行未作废、付款单可用、发票仍为已登记、币种与供应商快照自相一致；"
            + "它只代表「这笔付款按登记指向了这张发票」，不代表已付款、已结算或已核销。");
    }

    /// <summary>
    /// 固定 7 次数据集访问：采购订单 + 订单侧关联行 + 本页发票 + 发票侧关联行 + 持久化引用行 +
    /// 付款单 + 付款单侧有效发票引用合计（数据库侧分组聚合，含每次读取的 1 条溢出探测）；全程只读、不写库，
    /// 金额按持久化行原样汇总（不重算、不改写）。
    /// </summary>
    private static async Task<BuildResult> BuildAsync(
        IErpDbContext db, IReadOnlyList<long> orderIds, int rowCap)
    {
        var orders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync();
        var orderById = orders.ToDictionary(o => o.Id);

        // 1) 订单侧关联行（ERP-043 / ERP-065 的「发票 → 采购订单」持久化关联行；有界）
        var linkRows = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && orderIds.Contains(a.PurchaseOrderId))
            .OrderBy(a => a.PurchaseOrderId).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
            .Take(rowCap + 1)
            .ToListAsync();
        var truncated = linkRows.Count > rowCap;
        if (truncated) linkRows.RemoveRange(rowCap, linkRows.Count - rowCap);

        var invoiceIds = linkRows.Select(a => a.PurchaseInvoiceId).Distinct().ToList();

        // 2) 本页发票（含已软删除：用于可用性标注，绝不臆造字段）
        var invoiceById = invoiceIds.Count == 0
            ? new Dictionary<long, PurchaseInvoice>()
            : (await db.PurchaseInvoices.AsNoTracking()
                    .Where(i => invoiceIds.Contains(i.Id)).ToListAsync())
                .ToDictionary(i => i.Id);

        // 3) 发票侧关联行 → 每张发票被哪些采购订单关联（判定「仅关联本订单」，从而安全归属金额）
        var invoiceOrderIds = new Dictionary<long, HashSet<long>>();
        if (invoiceIds.Count > 0)
        {
            var invoiceLinkRows = await db.PurchaseInvoiceAllocations.AsNoTracking()
                .Where(a => !a.IsDeleted && invoiceIds.Contains(a.PurchaseInvoiceId))
                .OrderBy(a => a.PurchaseInvoiceId).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
                .Take(rowCap + 1)
                .ToListAsync();
            if (invoiceLinkRows.Count > rowCap)
            {
                truncated = true;
                invoiceLinkRows.RemoveRange(rowCap, invoiceLinkRows.Count - rowCap);
            }

            foreach (var row in invoiceLinkRows)
            {
                if (!invoiceOrderIds.TryGetValue(row.PurchaseInvoiceId, out var set))
                    invoiceOrderIds[row.PurchaseInvoiceId] = set = new HashSet<long>();
                set.Add(row.PurchaseOrderId);
            }
        }

        // 4) ERP-066 的持久化「付款单 → 采购发票」引用行（有界）
        var payRows = invoiceIds.Count == 0
            ? new List<SupplierPaymentInvoiceAllocation>()
            : await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
                .Where(a => !a.IsDeleted && invoiceIds.Contains(a.PurchaseInvoiceId))
                .OrderBy(a => a.PurchaseInvoiceId).ThenBy(a => a.AllocatedAt).ThenBy(a => a.Id)
                .Take(rowCap + 1)
                .ToListAsync();
        if (payRows.Count > rowCap)
        {
            truncated = true;
            payRows.RemoveRange(rowCap, payRows.Count - rowCap);
        }

        // 5) 付款单（含已软删除：可用性判定；付款单本身不被本视图改写）
        var paymentIds = payRows.Select(r => r.PaymentId).Distinct().ToList();
        var paymentById = paymentIds.Count == 0
            ? new Dictionary<long, FinancePayment>()
            : (await db.FinancePayments.AsNoTracking()
                    .Where(p => paymentIds.Contains(p.Id)).ToListAsync())
                .ToDictionary(p => p.Id);

        // 6) 付款单侧有效发票引用合计（数据库侧分组聚合，用于「未指向任何发票」的上下文）
        var activeTotalByPayment = await ActiveInvoiceAllocationTotalsAsync(db, paymentIds);

        var linkRowsByOrder = linkRows.GroupBy(r => r.PurchaseOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var payRowsByInvoice = payRows.GroupBy(r => r.PurchaseInvoiceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<long, OrderEvidence>(orderIds.Count);
        foreach (var orderId in orderIds)
        {
            orderById.TryGetValue(orderId, out var order);
            var orderLinkRows = linkRowsByOrder.TryGetValue(orderId, out var found)
                ? found
                : new List<PurchaseInvoiceAllocation>();
            result[orderId] = BuildOrderEvidence(orderId, order, orderLinkRows, payRowsByInvoice,
                invoiceById, invoiceOrderIds, paymentById, activeTotalByPayment, truncated);
        }

        return new BuildResult(result, truncated);
    }

    /// <summary>引用行状态文案（对未知取值照实回显，不抛异常、不假定为有效）</summary>
    private static string SafeAllocationStatusText(int status)
        => status is SupplierPaymentInvoiceAllocationRules.StatusActive
            or SupplierPaymentInvoiceAllocationRules.StatusVoided
            ? SupplierPaymentInvoiceAllocationRules.StatusText(status)
            : $"未知（{status}）";

    /// <summary>发票到期日文案（ERP-065 口径：显式持久化到期日；未提供 = 未知，绝不按账期推算）</summary>
    private static string DueDateText(PurchaseInvoice? invoice)
        => invoice is null || invoice.IsDeleted
            ? "未知（发票不存在或已删除）"
            : invoice.DueDate is { } due
                ? due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "未提供（未知：不按供应商账期推算，也不用于账龄判断）";

    /// <summary>
    /// 单张订单派生：逐条引用行分桶 → 汇总 + 明细 + 报表复用聚合（历史 / 无效证据单独列示）。
    /// <para>归属规则：只有「该发票的有效关联行仅指向本订单」时，其上的有效引用行才计入**订单归属金额**；
    /// 发票还被其他采购订单关联时只作**发票级金额**单列，不按比例摊派、不猜测归属。</para>
    /// </summary>
    private static OrderEvidence BuildOrderEvidence(long orderId, PurchaseOrder? order,
        List<PurchaseInvoiceAllocation> linkRows,
        Dictionary<long, List<SupplierPaymentInvoiceAllocation>> payRowsByInvoice,
        Dictionary<long, PurchaseInvoice> invoiceById,
        Dictionary<long, HashSet<long>> invoiceOrderIds,
        Dictionary<long, FinancePayment> paymentById,
        Dictionary<long, PaymentActiveTotalRow> activeTotalByPayment,
        bool truncated)
    {
        var linkedInvoiceIds = linkRows.Select(r => r.PurchaseInvoiceId).Distinct().ToList();
        var totals = EmptyTotals();
        var lines = new List<PurchaseOrderInvoicePaymentEvidenceLine>();
        var activePaymentIds = new HashSet<long>();
        var attributableInvoiceIds = new HashSet<long>();
        var unattributableInvoiceIds = new HashSet<long>();
        var attributableAmount = 0m;
        var unattributableAmount = 0m;
        string? firstRowCurrency = null;

        foreach (var invoiceId in linkedInvoiceIds)
        {
            invoiceById.TryGetValue(invoiceId, out var invoice);
            var exclusivelyLinked = invoiceOrderIds.TryGetValue(invoiceId, out var linkedOrderIds)
                && linkedOrderIds.Count == 1 && linkedOrderIds.Contains(orderId);
            var rows = payRowsByInvoice.TryGetValue(invoiceId, out var found)
                ? found
                : new List<SupplierPaymentInvoiceAllocation>();

            foreach (var row in rows)
            {
                paymentById.TryGetValue(row.PaymentId, out var payment);
                activeTotalByPayment.TryGetValue(row.PaymentId, out var activeTotal);
                var (bucket, reason) = ClassifyRow(row, payment, invoice);
                var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
                firstRowCurrency ??= currency;
                var recorded = bucket == PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded;

                totals[bucket].Add(row.AllocatedAmount, row.PaymentId);
                if (recorded)
                {
                    activePaymentIds.Add(row.PaymentId);
                    if (exclusivelyLinked)
                    {
                        attributableInvoiceIds.Add(invoiceId);
                        attributableAmount += row.AllocatedAmount;
                    }
                    else
                    {
                        unattributableInvoiceIds.Add(invoiceId);
                        unattributableAmount += row.AllocatedAmount;
                    }
                }

                lines.Add(BuildLine(row, payment, invoice, activeTotal, bucket, reason,
                    currency, exclusivelyLinked));
            }
        }

        var aggregate = new InvoicePaymentEvidenceAggregate(orderId,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded].RowCount,
            activePaymentIds.Count,
            attributableAmount, attributableInvoiceIds.Count, unattributableInvoiceIds.Count, unattributableAmount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable].Amount);

        var orderCurrency = order is not null && !order.IsDeleted
            ? CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString())
            : (firstRowCurrency ?? CurrencyAmountRules.DefaultCurrency);
        var summary = BuildSummary(orderId, order, orderCurrency, totals, linkedInvoiceIds.Count, truncated, aggregate);
        return new OrderEvidence(summary, lines, aggregate);
    }

    /// <summary>汇总派生（命中上限时金额与计数一律 null（未知），绝不用 0 顶替；订单取消 / 不可用时订单金额未知）</summary>
    private static PurchaseOrderInvoicePaymentEvidenceSummary BuildSummary(long orderId, PurchaseOrder? order,
        string currency, Dictionary<string, BucketTotals> totals, int linkedInvoiceCount, bool truncated,
        InvoicePaymentEvidenceAggregate aggregate)
    {
        var available = order is not null && !order.IsDeleted;
        var cancelled = available && order!.Status == DocumentStatus.Cancelled;
        var state = !available
            ? SupplierInvoiceReconciliationSemantics.OrderStateUnavailable
            : cancelled
                ? SupplierInvoiceReconciliationSemantics.OrderStateCancelled
                : SupplierInvoiceReconciliationSemantics.OrderStateAvailable;
        var stateText = state switch
        {
            SupplierInvoiceReconciliationSemantics.OrderStateCancelled =>
                "采购订单已取消：订单金额仅作历史参考，已分配付款引用证据仍按持久化行如实列出，不推断未付款 / 已结清 / 逾期",
            SupplierInvoiceReconciliationSemantics.OrderStateAvailable =>
                $"采购订单可用（当前状态 {order!.Status}）",
            _ => "采购订单不存在或已删除：订单金额未知（不按 0 处理），证据快照仍可读",
        };

        var hasAnyRow = aggregate.HasAnyRow;
        var active = truncated ? (decimal?)null : aggregate.ActiveAmount;

        return new PurchaseOrderInvoicePaymentEvidenceSummary
        {
            PurchaseOrderId = orderId,
            OrderNo = order?.OrderNo ?? string.Empty,
            OrderAvailable = available,
            OrderState = state,
            OrderStateText = stateText,
            OrderStatusText = available ? order!.Status.ToString() : "订单不存在或已删除：状态无法确认",
            OrderCurrency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            OrderedAmount = available ? order!.TotalAmount : null,
            LinkedInvoiceCount = linkedInvoiceCount,
            HasEvidence = hasAnyRow,
            HasHistoricalEvidence = aggregate.HasHistoricalRow,
            Truncated = truncated,
            ActiveAllocatedPaymentAmount = active,
            AttributableAllocatedPaymentAmount = truncated ? null : aggregate.AttributableAmount,
            ActiveAllocationCount = truncated ? null : aggregate.ActiveCount,
            ActivePaymentCount = truncated ? null : aggregate.ActivePaymentCount,
            AttributableInvoiceCount = truncated ? null : aggregate.AttributableInvoiceCount,
            UnattributableInvoiceCount = truncated ? null : aggregate.UnattributableInvoiceCount,
            UnattributableAllocatedPaymentAmount = truncated ? null : aggregate.UnattributableAmount,
            VoidedCount = truncated ? null : aggregate.VoidedCount,
            VoidedAmount = truncated ? null : aggregate.VoidedAmount,
            InvoiceInactiveCount = truncated ? null : aggregate.InvoiceInactiveCount,
            InvoiceInactiveAmount = truncated ? null : aggregate.InvoiceInactiveAmount,
            InvalidCount = truncated ? null : aggregate.InvalidCount,
            InvalidAmount = truncated ? null : aggregate.InvalidAmount,
            UnavailableCount = truncated ? null : aggregate.UnavailableCount,
            UnavailableAmount = truncated ? null : aggregate.UnavailableAmount,
            EvidenceLabel = PurchaseOrderInvoicePaymentEvidenceSemantics.EvidenceLabel(truncated, active, hasAnyRow),
            EvidenceNote = BuildEvidenceNote(available, cancelled, truncated, hasAnyRow, totals, aggregate, currency),
        };
    }

    /// <summary>请求的订单 Id 未找到时的汇总（未知，不按 0；列表列仍能显示「未知」而不是静默丢行）</summary>
    private static PurchaseOrderInvoicePaymentEvidenceSummary BuildSummaryForMissingOrder(long orderId, bool truncated)
        => BuildSummary(orderId, null, CurrencyAmountRules.DefaultCurrency, EmptyTotals(), 0, truncated,
            InvoicePaymentEvidenceAggregateSet.Empty(orderId));

    /// <summary>
    /// 汇总说明：已知什么 / 缺什么 / 为什么不能当总账、法定对账单、税务申报、付款授权或结算确认；
    /// 历史 / 无效 / 无法确认证据逐桶点名（金额与条数都只作参考）。
    /// </summary>
    private static string BuildEvidenceNote(bool available, bool cancelled, bool truncated, bool hasAnyRow,
        Dictionary<string, BucketTotals> totals, InvoicePaymentEvidenceAggregate aggregate, string currency)
    {
        var sb = new StringBuilder();

        if (truncated)
        {
            sb.Append(PurchaseOrderInvoicePaymentEvidenceSemantics.TruncatedNote);
        }
        else if (!available || cancelled)
        {
            sb.Append(PurchaseOrderInvoicePaymentEvidenceSemantics.UnknownOrderNote);
        }
        else if (aggregate.ActiveAmount > 0m)
        {
            sb.Append($"有效已分配付款引用证据 {aggregate.ActiveAmount} {currency}（发票级："
                + $"付款单 {aggregate.ActivePaymentCount} 张 / 引用行 {aggregate.ActiveCount} 条），"
                + $"其中可安全归属本订单 {aggregate.AttributableAmount} {currency}"
                + $"（{aggregate.AttributableInvoiceCount} 张仅关联本订单的发票）；"
                + "该金额只代表「付款按登记指向了这些发票」，不参与「订单金额 − 已开票金额」的任何计算，"
                + "也不是总账或应付余额、不是法定供应商对账单、不是税务申报、不是付款授权或结算确认；"
                + "「付款单 → 采购订单」类证据单列，绝不相加。");

            if (aggregate.UnattributableAmount > 0m)
            {
                sb.Append(' ').Append(PurchaseOrderInvoicePaymentEvidenceSemantics.NotAttributableNote);
            }
        }
        else if (hasAnyRow)
        {
            sb.Append("该采购订单没有有效的已分配付款引用证据：现有引用行都是已作废 / 发票失效 / 无效或无法确认，"
                + "均不计入有效合计；这是**已分配付款引用证据缺口**，不代表未付款、已付款、已结清或逾期。");
        }
        else
        {
            sb.Append(PurchaseOrderInvoicePaymentEvidenceSemantics.NoEvidenceNote);
        }

        if (!truncated)
        {
            AppendBucket(sb, totals, PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided, "已作废引用行");
            AppendBucket(sb, totals, PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive,
                "发票已失效（草稿 / 已作废）的引用行");
            AppendBucket(sb, totals, PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid,
                "无效历史证据（供应商 / 币种或快照不一致）");
            AppendBucket(sb, totals, PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable,
                "无法确认的证据（付款单或发票已删除 / 不存在）");
        }

        sb.Append(' ').Append(PurchaseOrderInvoicePaymentEvidenceSemantics.SeparateDimensionText);
        return sb.ToString();
    }

    /// <summary>按引用行条数点名的分桶说明（历史 / 无效 / 无法确认：金额与付款单张数都只作参考）</summary>
    private static void AppendBucket(StringBuilder sb, Dictionary<string, BucketTotals> totals,
        string bucket, string label)
    {
        var item = totals[bucket];
        if (item.RowCount == 0) return;
        sb.Append($" 另有{label} {item.RowCount} 条引用行（金额 {item.Amount}，涉及 {item.PaymentIds.Count} 张付款单）："
            + "不计入有效合计，也不换算 / 合并 / 改派到任何采购订单，可在明细中单独查看原因。");
    }

    /// <summary>
    /// 单条引用行 → 明细行（只读映射）：付款单 / 发票与供应商快照取自引用行落库值，
    /// 当前可用性与状态用 ERP-066 的同一套文案函数，绝不静默修复、改派或折算。
    /// </summary>
    private static PurchaseOrderInvoicePaymentEvidenceLine BuildLine(
        SupplierPaymentInvoiceAllocation row,
        FinancePayment? payment,
        PurchaseInvoice? invoice,
        PaymentActiveTotalRow? activeTotal,
        string bucket,
        string reason,
        string currency,
        bool exclusivelyLinked)
    {
        var recorded = bucket == PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded;
        var paymentAvailable = SupplierPaymentInvoiceAllocationRules.IsPaymentSelectable(payment);
        var invoiceAvailable = SupplierPaymentInvoiceAllocationRules.IsInvoiceSelectable(invoice);
        var paymentUnallocated = paymentAvailable
            ? Math.Max(0m, payment!.Amount - (activeTotal?.Amount ?? row.AllocatedAmount))
            : (decimal?)null;

        return new PurchaseOrderInvoicePaymentEvidenceLine
        {
            AllocationId = row.Id,
            PaymentId = row.PaymentId,
            PaymentNo = row.PaymentNo,
            PaymentDate = row.PaymentDate,
            PaymentStatus = paymentAvailable ? (int)payment!.Status : null,
            PaymentStatusText = paymentAvailable
                ? SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment!.Status)
                : "无法确认（付款单不存在或已删除）",
            PaymentAmount = row.PaymentAmount,
            PaymentUnallocatedAmount = paymentUnallocated,
            AllocatedAmount = row.AllocatedAmount,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            SupplierId = row.SupplierId,
            SupplierCode = row.SupplierCode,
            SupplierName = row.SupplierName,
            PurchaseInvoiceId = row.PurchaseInvoiceId,
            InvoiceType = row.InvoiceType,
            InvoiceTypeText = PurchaseInvoiceRules.InvoiceTypeText(row.InvoiceType),
            InvoiceIdentityText = SupplierPaymentInvoiceAllocationRules.InvoiceIdentityText(
                row.InvoiceType, row.InvoiceCode, row.InvoiceNumber, row.InvoiceIdentityText),
            InvoiceDate = row.InvoiceDate,
            InvoiceStatus = invoice is not null && !invoice.IsDeleted ? invoice.Status : row.InvoiceStatus,
            InvoiceStatusText = invoice is not null && !invoice.IsDeleted
                ? SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status)
                : $"{SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(row.InvoiceStatus)}（引用行快照）",
            InvoiceGrossAmount = row.InvoiceGrossAmount,
            InvoiceCurrency = invoice is not null
                ? CurrencyAmountRules.NormalizeCurrency(invoice.Currency)
                : currency,
            InvoiceSupplierId = invoice is not null ? invoice.SupplierId : null,
            InvoiceDueDate = invoice is not null && !invoice.IsDeleted ? invoice.DueDate : null,
            InvoiceDueDateText = DueDateText(invoice),
            InvoicePaymentTerms = invoice is not null && !invoice.IsDeleted ? invoice.PaymentTerms : string.Empty,
            PaymentAvailable = paymentAvailable,
            PaymentAvailabilityText = SupplierPaymentInvoiceAllocationRules.PaymentAvailabilityText(payment),
            InvoiceAvailable = invoiceAvailable,
            InvoiceAvailabilityText = SupplierPaymentInvoiceAllocationRules.InvoiceAvailabilityText(invoice),
            Status = row.Status,
            StatusText = SafeAllocationStatusText(row.Status),
            IsRecordedEvidence = recorded,
            IsVoided = row.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided,
            Bucket = bucket,
            BucketText = PurchaseOrderInvoicePaymentEvidenceSemantics.BucketText(bucket),
            OrderAttributable = recorded && exclusivelyLinked,
            AttributionText = recorded
                ? (exclusivelyLinked
                    ? "可归属本订单：该发票的有效关联行仅指向本采购订单"
                    : "只作发票级金额：该发票还被其他采购订单关联，系统不按比例摊派、不猜测订单归属")
                : "不计入有效合计：不参与任何订单归属（按分桶说明处理）",
            AllocatedAt = row.AllocatedAt,
            VoidedAt = row.VoidedAt,
            VoidReason = row.VoidReason,
            Remark = row.Remark,
            Reason = recorded && !exclusivelyLinked
                ? reason + " 该发票还被其他采购订单关联：本行金额只作发票级金额单列，不计入本订单的订单归属金额。"
                : reason,
        };
    }

    /// <summary>
    /// 单张发票的已分配付款引用证据聚合（报表复用入口）：分桶与资格判定与采购订单视图共用同一套实现；
    /// 参与证据的付款单金额按付款单去重后求和（仅作上下文），未指向任何发票的部分按付款单侧**全部**有效发票引用行扣除，
    /// 且币种不一致时直接跳过（绝不折算、绝不跨币种相加）。
    /// </summary>
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
            if (bucket != PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded) continue;

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

            var activeTotal = activeTotalByPayment.TryGetValue(item.Key, out var total)
                ? total.Amount
                : item.Value.Amount;
            recordedPaymentAmount += payment.Amount;

            var remaining = payment.Amount - activeTotal;
            if (remaining > 0m) unallocatedPaymentAmount += remaining;
        }

        return new InvoiceAllocatedPaymentAggregate(invoiceId,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketRecorded].RowCount,
            activePaymentInfo.Count,
            recordedPaymentAmount,
            unallocatedPaymentAmount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketVoided].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvoiceInactive].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketInvalid].Amount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable].RowCount,
            totals[PurchaseOrderInvoicePaymentEvidenceSemantics.BucketUnavailable].Amount);
    }
}

