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
/// 采购订单付款引用证据口径常量（ERP-050）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免把「付款引用证据」写成「已付款 / 已结算 / 应付余额 / 逾期」，也避免把已作废 / 无效 / 无法确认的引用行混进有效合计。
/// <para>定位：<b>采购订单维度的付款引用证据只读视图</b>（证据来自 ERP-049 的持久化引用行）——
/// 它<strong>不是</strong>银行付款凭证、<strong>不是</strong>应付账款台账、<strong>不是</strong>发票核销、
/// <strong>不是</strong>税务（进项）判断，也<strong>不是</strong>账龄表或结算结果。</para>
/// </summary>
public static class PurchaseOrderPaymentEvidenceSemantics
{
    // ==================== 1. 有界上限 ====================

    /// <summary>列表批量查询最多接受的采购订单张数（列表按页取 Id；超出直接拒绝，不静默截断）</summary>
    public const int MaxBatchOrders = 200;

    /// <summary>
    /// 列表批量查询最多读取的持久化引用行数（有界）：命中上限时本次响应内全部金额与计数按「未知」返回，
    /// 绝不报出「只统计了一部分证据」的合计。
    /// </summary>
    public const int MaxBatchEvidenceRows = 5000;

    /// <summary>单张采购订单详情最多读取的持久化引用行数（有界）：命中上限时金额与计数按「未知」返回，明细只显示已读取部分</summary>
    public const int MaxOrderEvidenceRows = 500;

    /// <summary>
    /// 报表侧（ERP-044 对账报表）一次最多聚合的采购订单张数（有界）：
    /// 超出时该批订单的付款引用证据一律按「未知」（null）处理，绝不给部分合计。
    /// </summary>
    public const int MaxAggregateOrders = 500;

    // ==================== 2. 证据分桶 ====================

    /// <summary>有效引用证据（付款单可用、引用行快照自相一致、订单可用且供应商 / 币种一致）：唯一计入有效合计的分桶</summary>
    public const string BucketRecorded = "recorded";

    /// <summary>已作废历史证据：单独分桶，绝不计入有效合计（保留可读，含作废原因）</summary>
    public const string BucketVoided = "voided";

    /// <summary>无效历史证据（供应商 / 币种不一致、快照自相矛盾或金额非正数）：不换算、不合并、不改派，绝不计入有效合计</summary>
    public const string BucketInvalid = "invalid";

    /// <summary>无法确认的证据（引用行指向的付款单不存在 / 已删除）：金额不能确认，绝不并入有效合计</summary>
    public const string BucketUnavailable = "unavailable";

    /// <summary>支持的分桶取值（超出范围不静默兜底）</summary>
    public static readonly string[] SupportedBuckets =
        { BucketRecorded, BucketVoided, BucketInvalid, BucketUnavailable };

    /// <summary>分桶文案（接口、界面与文档同源）</summary>
    public static string BucketText(string bucket) => bucket switch
    {
        BucketRecorded => "有效付款引用证据（计入有效合计）",
        BucketVoided => "已作废历史证据（不计入有效合计）",
        BucketInvalid => "无效历史证据（供应商 / 币种或快照不一致：不换算、不合并、不改派）",
        _ => "无法确认的证据（付款单不存在 / 已删除：金额无法确认）",
    };

    /// <summary>是否属于「历史 / 无效证据」（= 不计入有效合计的分桶）</summary>
    public static bool IsHistoricalBucket(string bucket)
        => bucket is BucketVoided or BucketInvalid or BucketUnavailable;

    // ==================== 3. 证据短标签 ====================

    /// <summary>有有效付款引用证据</summary>
    public const string LabelRecorded = "有付款引用证据";

    /// <summary>只有历史 / 无效付款引用证据（绝不呈现为已付款 / 已结算）</summary>
    public const string LabelHistoricalOnly = "仅有历史 / 无效付款引用证据";

    /// <summary>没有任何付款引用证据（证据缺口，绝不呈现为未付款 / 已付款 / 逾期 / 欠款）</summary>
    public const string LabelNone = "无付款引用证据";

    /// <summary>命中有界上限，证据无法穷尽</summary>
    public const string LabelUnknown = "未知（超出有界读取上限）";

    /// <summary>
    /// 证据短标签（界面与接口同源）：命中上限 → 未知；有有效证据 → 有付款引用证据；
    /// 只有历史 / 无效证据 → 仅有历史 / 无效付款引用证据；一条都没有 → 无付款引用证据。
    /// </summary>
    public static string EvidenceLabel(bool truncated, decimal? recordedAmount, bool hasAnyRow)
    {
        if (truncated) return LabelUnknown;
        if (recordedAmount is > 0) return LabelRecorded;
        return hasAnyRow ? LabelHistoricalOnly : LabelNone;
    }

    // ==================== 4. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（证据来源、分桶、未指向部分与未知处理）</summary>
    public const string RuleText =
        "本视图是采购订单的**付款引用证据口径**（只读派生）：全部金额只按 ERP-049 的持久化引用行"
        + "（SupplierPaymentAllocations）派生，且只统计**有效（未作废）**的引用行；"
        + "已作废历史证据、无效历史证据（供应商 / 币种不一致或快照自相矛盾）与无法确认证据（付款单不存在 / 已删除）"
        + "一律单独分桶列出，绝不并入有效合计，也不换算、不合并、不改派到其他订单；"
        + "「参与证据的付款单金额 − 本订单已引用金额」只表示这些付款单金额中**未指向本订单**的部分（可能指向其他采购订单），"
        + "它不是银行未付金额、不是应付余额、也不是发票余额；"
        + "没有任何引用行时只显示「无付款引用证据」（证据缺口），绝不推断为未付款 / 已付款 / 已结清 / 逾期或欠款。";

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public const string ScopeText =
        "有界读取：列表批量汇总一次最多 200 张采购订单、最多 5000 条持久化引用行；单张订单详情最多 500 条引用行；"
        + "命中上限时金额与计数一律按「未知」（null）返回，绝不报出部分合计，明细只显示已读取部分；"
        + "读取固定次数数据集访问（订单 + 引用行 + 付款单 + 付款单有效引用合计），与订单张数 / 行数无关，绝无逐行查库。";

    /// <summary>与付款凭证 / 应付账款台账 / 发票核销 / 结算 / 账龄的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本视图只是供应商付款单 → 采购订单 的**引用证据**只读呈现：不是银行付款凭证、不是付款授权或付款状态、"
        + "不是应付账款台账或应付余额、不是发票核销或进项（税务）判断、也不是结算结果与账龄表 —— "
        + "它不判断是否已付款、是否已结清或是否逾期，不推算账期与到期日，也不产生任何记账、凭证、收付款或结算单；"
        + "界面与接口一律把该金额标注为**付款引用证据**，不得当作已付款金额、欠款金额或可付款金额。";

    /// <summary>无任何付款引用证据时的提示（绝不能被读成未付款 / 已付款 / 已结清 / 逾期）</summary>
    public const string NoEvidenceNote =
        "该采购订单没有任何登记付款引用行：这是**付款引用证据缺口**，不代表未付款、已付款、已结清、逾期或欠款，"
        + "也不构成应付余额、付款授权或结算依据。";

    /// <summary>命中读取上限时的提示（金额未知，不给部分合计）</summary>
    public const string TruncatedNote =
        "本次读取命中系统有界上限（持久化引用行数量超出上限），无法穷尽该订单的付款引用证据："
        + "金额与计数一律按「未知」显示，不作部分合计。";

    /// <summary>无可用订单金额时的提示（订单已取消或不可用）</summary>
    public const string UnknownOrderNote =
        "采购订单已取消或已删除：订单金额按未知显示（不按 0 处理），也不推断付款、结算或逾期状态。";
}

/// <summary>
/// 采购订单 Id 列表查询参数（ERP-050，只读）：逗号分隔的正整数，去重、保序、有界（超出上限直接拒绝）。
/// <para>与 ERP-048 的 <see cref="PurchaseOrderInvoiceEvidenceQuery"/> 同口径，便于列表页复用同一套分批策略。</para>
/// </summary>
public sealed class PurchaseOrderPaymentEvidenceQuery
{
    /// <summary>逗号分隔的采购订单 Id（如 <c>12,34,56</c>；留空 = 不查询任何订单）</summary>
    public string? Ids { get; set; }

    /// <summary>归一化后的订单 Id（去重、保持提交顺序）</summary>
    public IReadOnlyList<long> OrderIds => _orderIds;

    private readonly List<long> _orderIds = new();

    /// <summary>
    /// 归一化并校验：只接受正整数 Id，去重（保留首次出现顺序）；非法片段或超出
    /// <see cref="PurchaseOrderPaymentEvidenceSemantics.MaxBatchOrders"/> 一律拒绝，不静默丢弃、不静默截断。
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
                    $"订单 Id「{token}」无效：付款引用证据批量接口只接受逗号分隔的正整数采购订单 Id");

            if (_orderIds.Contains(id)) continue;
            _orderIds.Add(id);

            if (_orderIds.Count > PurchaseOrderPaymentEvidenceSemantics.MaxBatchOrders)
                throw BusinessException.InvalidParameter(
                    $"一次最多查询 {PurchaseOrderPaymentEvidenceSemantics.MaxBatchOrders} 张采购订单的付款引用证据（有界查询）："
                    + "本次提交超过上限，请按页分批查询");
        }
    }
}

/// <summary>
/// 单张采购订单的付款引用证据聚合（ERP-050，只读派生值；采购订单视图与 ERP-044 对账报表共用同一套分桶口径）。
/// <para>金额只按 ERP-049 的持久化引用行派生；有效 / 已作废 / 无效 / 无法确认绝不合并。</para>
/// </summary>
public sealed record PaymentEvidenceAggregate(
    long PurchaseOrderId,
    decimal ActiveAmount,
    int ActiveCount,
    int ActivePaymentCount,
    decimal RecordedPaymentAmount,
    decimal UnallocatedPaymentAmount,
    int VoidedCount,
    decimal VoidedAmount,
    int InvalidCount,
    decimal InvalidAmount,
    int UnavailableCount,
    decimal UnavailableAmount)
{
    /// <summary>该订单是否有任何持久化引用行（false = 付款引用证据缺口）</summary>
    public bool HasAnyRow => ActiveCount + VoidedCount + InvalidCount + UnavailableCount > 0;

    /// <summary>该订单是否有历史 / 无效 / 无法确认证据（必须单独查看，绝不并入有效合计）</summary>
    public bool HasHistoricalRow => VoidedCount + InvalidCount + UnavailableCount > 0;
}

/// <summary>
/// 一批采购订单的付款引用证据聚合结果（ERP-050，只读）：<see cref="Truncated"/> = 命中订单数 / 行数上限，
/// 此时调用方必须把金额与计数按「未知」处理，绝不使用部分数据。
/// </summary>
public sealed record PaymentEvidenceAggregateSet(
    IReadOnlyDictionary<long, PaymentEvidenceAggregate> ByOrder,
    bool Truncated)
{
    /// <summary>取某订单的聚合；订单无引用行或未参与聚合时返回全 0（订单可用性由调用方自行判定）</summary>
    public PaymentEvidenceAggregate Get(long orderId)
        => ByOrder.TryGetValue(orderId, out var aggregate) ? aggregate : Empty(orderId);

    /// <summary>全 0 聚合（无引用行；不代表未付款 / 已付款）</summary>
    public static PaymentEvidenceAggregate Empty(long orderId)
        => new(orderId, 0m, 0, 0, 0m, 0m, 0, 0m, 0, 0m, 0, 0m);
}


/// <summary>
/// 采购订单付款引用证据汇总（ERP-050，只读派生；列表列与详情头部共用）。
/// <para>金额口径：<see cref="RecordedAllocatedAmount"/> 只统计**有效（未作废）**的持久化引用行；
/// 已作废 / 无效 / 无法确认分别单列，绝不并入有效合计，也绝不折算、合并或改派到其他订单。</para>
/// <para>未知处理：命中读取上限或订单已取消 / 不可用时，金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 本汇总<strong>不是</strong>付款凭证、应付余额、发票核销或结算状态。</para>
/// </summary>
public sealed class PurchaseOrderPaymentEvidenceSummary
{
    /// <summary>采购订单 Id</summary>
    public long PurchaseOrderId { get; init; }

    /// <summary>采购单号（订单已删除时按引用行快照显示）</summary>
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

    /// <summary>是否读到至少一条持久化引用行（不代表有效证据，也不代表已付款）</summary>
    public bool HasPaymentEvidence { get; init; }

    /// <summary>是否存在已作废 / 无效 / 无法确认的历史证据（必须单独查看，绝不并入有效合计）</summary>
    public bool HasHistoricalEvidence { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>有效已引用金额（付款单可用、快照自相一致的有效引用行合计，原币）；null = 未知（命中上限）</summary>
    public decimal? RecordedAllocatedAmount { get; init; }

    /// <summary>参与有效合计的付款单张数（按付款单去重）；null = 未知（命中上限）</summary>
    public int? RecordedPaymentCount { get; init; }

    /// <summary>本次读取到的持久化引用行总条数（含历史 / 无效证据）；null = 未知（命中上限）</summary>
    public int? AllocationCount { get; init; }

    /// <summary>
    /// 参与有效证据的付款单金额快照合计（按付款单去重后求和，仅作上下文）；null = 未知（命中上限）。
    /// 它<strong>不是</strong>应付余额，也<strong>不是</strong>银行付款金额。
    /// </summary>
    public decimal? RecordedPaymentAmount { get; init; }

    /// <summary>
    /// 未指向本订单的金额 = 参与证据的付款单金额快照合计 − 本订单有效已引用金额（下限 0）：
    /// 只表示这些付款单金额中<strong>未指向本订单</strong>的部分（可能指向其他采购订单），
    /// 不是银行未付金额、不是应付余额、也不是发票余额；null = 未知（命中上限）。
    /// </summary>
    public decimal? UnallocatedPaymentAmount { get; init; }

    /// <summary>已作废历史证据金额（原币；绝不计入有效合计，保留可读）；null = 未知（命中上限）</summary>
    public decimal? VoidedAllocatedAmount { get; init; }

    /// <summary>已作废历史证据引用行条数；null = 未知（命中上限）</summary>
    public int? VoidedAllocationCount { get; init; }

    /// <summary>无效历史证据金额（供应商 / 币种或快照不一致；绝不换算、合并或改派）；null = 未知（命中上限）</summary>
    public decimal? InvalidAllocatedAmount { get; init; }

    /// <summary>无效历史证据引用行条数；null = 未知（命中上限）</summary>
    public int? InvalidAllocationCount { get; init; }

    /// <summary>无法确认的证据金额（付款单不存在 / 已删除：无法确认有效性）；null = 未知（命中上限）</summary>
    public decimal? UnavailableAllocatedAmount { get; init; }

    /// <summary>无法确认的证据引用行条数；null = 未知（命中上限）</summary>
    public int? UnavailableAllocationCount { get; init; }

    /// <summary>证据短标签（有付款引用证据 / 仅有历史无效证据 / 无付款引用证据 / 未知）</summary>
    public string EvidenceLabel { get; init; } = PurchaseOrderPaymentEvidenceSemantics.LabelUnknown;

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不能作为已付款或应付余额）</summary>
    public string EvidenceNote { get; init; } = string.Empty;
}


/// <summary>
/// 一行付款引用证据（ERP-050，只读派生；明细行 = 一条 ERP-049 持久化引用行）。
/// <para><see cref="Bucket"/> 表明该引用行是有效证据还是历史 / 无效 / 无法确认证据：
/// 只有 <c>recorded</c> 计入有效合计；其余一律单独列出并给出 <see cref="Reason"/>，
/// 系统绝不换算、合并或改派到其他订单。</para>
/// </summary>
public sealed class PurchaseOrderPaymentEvidenceLine
{
    /// <summary>引用行 Id（ERP-049 <c>SupplierPaymentAllocations.Id</c>）</summary>
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

    /// <summary>付款单金额快照（原币；与引用金额同币种，仅作展示上下文，不做汇率换算）</summary>
    public decimal PaymentAmount { get; init; }

    /// <summary>本行引用金额（原币；按持久化引用行原样呈现，不重算、不四舍五入改写）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>币种（原币；引用行币种，与付款单币种一致才可能是有效证据）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>供应商 Id 快照（引用行写入时的服务端快照）</summary>
    public long SupplierId { get; init; }

    /// <summary>供应商编码快照</summary>
    public string SupplierCode { get; init; } = string.Empty;

    /// <summary>供应商名称快照</summary>
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>采购单号快照（引用行写入时口径）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; init; }

    /// <summary>引用行订单状态快照文案（取消后历史证据仍保留登记当时口径）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>引用行的订单币种快照（与引用行币种 / 付款单币种不一致即无效证据，绝不换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>付款单当前是否可用（存在且未删除；**只读标注**）</summary>
    public bool PaymentAvailable { get; init; }

    /// <summary>付款单可用性文案（已删除时照实说明，历史证据仍可读）</summary>
    public string PaymentAvailabilityText { get; init; } = string.Empty;

    /// <summary>采购订单当前可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderAvailabilityText { get; init; } = string.Empty;

    /// <summary>引用行状态（1 有效 / 2 已作废；未知取值照实说明）</summary>
    public int Status { get; init; }

    /// <summary>状态文案</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>是否有效证据（唯一计入有效合计的证据类型）</summary>
    public bool IsRecordedEvidence { get; init; }

    /// <summary>是否已作废（历史证据，保留可读）</summary>
    public bool IsVoided { get; init; }

    /// <summary>证据分桶：recorded / voided / invalid / unavailable</summary>
    public string Bucket { get; init; } = PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable;

    /// <summary>分桶文案</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>
    /// 该付款单中未指向本订单的金额（= 付款单金额快照 − 该付款单全部有效引用行合计，下限 0，仅作展示上下文）；
    /// null = 未知（付款单缺失 / 命中上限）。它不是银行未付金额、不是应付余额。
    /// </summary>
    public decimal? PaymentUnallocatedAmount { get; init; }

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
/// 采购订单付款引用证据详情（ERP-050，只读派生）：汇总 + 逐条证据明细 + 口径文案。
/// <para><see cref="Lines"/> 为有界明细（最多 <see cref="PurchaseOrderPaymentEvidenceSemantics.MaxOrderEvidenceRows"/> 条）；
/// 命中上限时汇总金额与计数按未知显示，明细仅展示已读取部分。</para>
/// </summary>
public sealed class PurchaseOrderPaymentEvidenceDetail
{
    /// <summary>汇总（与该订单列表列同口径）</summary>
    public PurchaseOrderPaymentEvidenceSummary Summary { get; init; } = new();

    /// <summary>逐条持久化引用行证据（有界：最多 500 条）</summary>
    public List<PurchaseOrderPaymentEvidenceLine> Lines { get; init; } = new();

    /// <summary>本次返回的明细条数</summary>
    public int LineCount { get; init; }

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = PurchaseOrderPaymentEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderPaymentEvidenceSemantics.ScopeText;

    /// <summary>与付款凭证 / 应付账款台账 / 发票核销 / 结算 / 账龄的边界说明</summary>
    public string Boundary { get; init; } = PurchaseOrderPaymentEvidenceSemantics.BoundaryText;

    /// <summary>引用行登记口径（与 ERP-049 同源，不引入第二套匹配算法）</summary>
    public string LinkageRule { get; init; } = SupplierPaymentAllocationRules.RuleText;

    /// <summary>ERP-049 登记册自身的模块边界（与 ERP-049 同源）</summary>
    public string AllocationBoundary { get; init; } = SupplierPaymentAllocationRules.BoundaryText;
}

/// <summary>
/// 采购订单付款引用证据批量汇总（ERP-050，只读派生；列表页一次请求取回本页订单的汇总，不逐行查库）。
/// </summary>
public sealed class PurchaseOrderPaymentEvidenceBatch
{
    /// <summary>本次请求的订单 Id 张数（去重后）</summary>
    public int RequestedCount { get; init; }

    /// <summary>返回的汇总条数（与请求的订单 Id 一一对应，含未找到 / 已删除的订单）</summary>
    public int ItemCount { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 全部金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>按请求顺序返回的订单汇总</summary>
    public List<PurchaseOrderPaymentEvidenceSummary> Items { get; init; } = new();

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = PurchaseOrderPaymentEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderPaymentEvidenceSemantics.ScopeText;

    /// <summary>与付款凭证 / 应付账款台账 / 发票核销 / 结算 / 账龄的边界说明</summary>
    public string Boundary { get; init; } = PurchaseOrderPaymentEvidenceSemantics.BoundaryText;

    /// <summary>引用行登记口径（与 ERP-049 同源）</summary>
    public string LinkageRule { get; init; } = SupplierPaymentAllocationRules.RuleText;
}



/// <summary>
/// 采购订单付款引用证据派生（ERP-050，只读）：在既有采购订单列表 / 详情工作流与 ERP-044 供应商发票对账报表中，
/// 暴露 **ERP-049 持久化付款引用行**的只读汇总，并复用 ERP-049 的权威资格判定与状态文案。
/// <para>边界（重要）：本派生<strong>只读</strong>（全部 <c>AsNoTracking</c>、从不 <c>SaveChanges</c>），
/// 不改写采购订单状态 / 到货进度 / 金额与明细、付款单审批与执行状态、发票与发票关联、库存与库存成本、
/// 供应商余额、费用或退税记录；也<strong>不</strong>执行付款、<strong>不</strong>认定应付余额、
/// <strong>不</strong>判断发票核销、结算结果、税务抵扣或账龄。</para>
/// <para>有界：固定 4 次数据集访问（订单 + 持久化引用行 + 付款单 + 付款单侧有效引用合计聚合），
/// 与订单张数 / 行数无关，绝无逐行查库；行数超出上限时金额与计数按「未知」返回，不报部分合计。</para>
/// </summary>
public static class PurchaseOrderPaymentEvidence
{
    /// <summary>
    /// 单张采购订单的付款引用证据详情（汇总 + 有界证据明细）。
    /// <para>订单不存在或已删除时抛「数据不存在」（与 <c>/api/purchase-orders/{id}</c> 同口径）。</para>
    /// </summary>
    public static async Task<PurchaseOrderPaymentEvidenceDetail> ForOrderAsync(IErpDbContext db, long orderId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orderId <= 0)
            throw BusinessException.InvalidParameter("采购订单 Id 无效：请指定有效的采购订单");

        var built = await BuildAsync(db, new[] { orderId },
            PurchaseOrderPaymentEvidenceSemantics.MaxOrderEvidenceRows);

        if (!built.ByOrder.TryGetValue(orderId, out var evidence) || !evidence.Summary.OrderAvailable)
            throw BusinessException.NotFound("采购订单不存在");

        return new PurchaseOrderPaymentEvidenceDetail
        {
            Summary = evidence.Summary,
            Lines = evidence.Lines,
            LineCount = evidence.Lines.Count
        };
    }

    /// <summary>
    /// 批量付款引用证据汇总（列表页用，只读有界）：一次请求取回本页订单的汇总，不逐行查库。
    /// <para>订单 Id 非法或超过 <see cref="PurchaseOrderPaymentEvidenceSemantics.MaxBatchOrders"/> 一律拒绝；
    /// 请求为空的订单 Id 时直接返回空结果，不访问数据库。</para>
    /// </summary>
    public static async Task<PurchaseOrderPaymentEvidenceBatch> ForOrdersAsync(
        IErpDbContext db, PurchaseOrderPaymentEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        if (query.OrderIds.Count == 0)
        {
            return new PurchaseOrderPaymentEvidenceBatch
            {
                RequestedCount = 0,
                ItemCount = 0,
                Truncated = false
            };
        }

        var built = await BuildAsync(db, query.OrderIds,
            PurchaseOrderPaymentEvidenceSemantics.MaxBatchEvidenceRows);

        var items = query.OrderIds
            .Select(id => built.ByOrder.TryGetValue(id, out var evidence)
                ? evidence.Summary
                : BuildSummaryForMissingOrder(id, built.Truncated))
            .ToList();

        return new PurchaseOrderPaymentEvidenceBatch
        {
            RequestedCount = query.OrderIds.Count,
            ItemCount = items.Count,
            Truncated = built.Truncated,
            Items = items
        };
    }


    /// <summary>
    /// 一批采购订单的付款引用证据聚合（ERP-044 报表复用入口，只读有界）：
    /// 与采购订单视图**共用同一套分桶与资格判定**，避免出现第二套对账算法。
    /// <para>命中订单数上限或行数上限时 <see cref="PaymentEvidenceAggregateSet.Truncated"/> 为 true，
    /// 调用方必须把相关金额与计数按「未知」处理（本次结果<strong>不含</strong>部分合计）。</para>
    /// </summary>
    public static async Task<PaymentEvidenceAggregateSet> AggregatesForOrdersAsync(
        IErpDbContext db, IReadOnlyList<long> orderIds,
        int orderCap = PurchaseOrderPaymentEvidenceSemantics.MaxAggregateOrders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(orderIds);

        var cap = orderCap <= 0 ? PurchaseOrderPaymentEvidenceSemantics.MaxAggregateOrders : orderCap;
        var ids = orderIds.Where(id => id > 0).Distinct().ToList();

        if (ids.Count == 0)
            return new PaymentEvidenceAggregateSet(new Dictionary<long, PaymentEvidenceAggregate>(), false);

        // 超出有界聚合上限：不给部分合计（调用方按未知显示）
        if (ids.Count > cap)
            return new PaymentEvidenceAggregateSet(new Dictionary<long, PaymentEvidenceAggregate>(), true);

        var built = await BuildAsync(db, ids, PurchaseOrderPaymentEvidenceSemantics.MaxBatchEvidenceRows);
        var byOrder = built.ByOrder.ToDictionary(pair => pair.Key, pair => pair.Value.Aggregate);
        return new PaymentEvidenceAggregateSet(byOrder, built.Truncated);
    }

    /// <summary>请求的订单 Id 未找到时的汇总（未知，不按 0；列表列仍能显示「未知」而不是静默丢行）</summary>
    private static PurchaseOrderPaymentEvidenceSummary BuildSummaryForMissingOrder(long orderId, bool truncated)
        => BuildSummary(orderId, null, EmptyTotals(), 0, truncated, CurrencyAmountRules.DefaultCurrency,
            PaymentEvidenceAggregateSet.Empty(orderId), string.Empty);

    // ==================== 内部：批量读取与派生 ====================

    /// <summary>单张订单的证据派生结果（汇总 + 明细行 + 供报表复用的聚合）</summary>
    private sealed record OrderEvidence(PurchaseOrderPaymentEvidenceSummary Summary,
        List<PurchaseOrderPaymentEvidenceLine> Lines, PaymentEvidenceAggregate Aggregate);

    /// <summary>一次批量派生的读取结果（按订单 Id 索引；<see cref="Truncated"/> = 命中行数上限）</summary>
    private sealed record BuildResult(Dictionary<long, OrderEvidence> ByOrder, bool Truncated);

    /// <summary>付款单侧有效引用合计（数据库侧分组聚合结果，只读）</summary>
    private sealed record PaymentActiveTotalRow(long PaymentId, decimal Amount);

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
            PaymentIds.Add(paymentId);
        }
    }

    /// <summary>全部为空的分桶字典（每个受支持分桶都有条目，避免键缺失时的隐式兜底）</summary>
    private static Dictionary<string, BucketTotals> EmptyTotals()
    {
        var totals = new Dictionary<string, BucketTotals>(StringComparer.Ordinal);
        foreach (var bucket in PurchaseOrderPaymentEvidenceSemantics.SupportedBuckets)
            totals[bucket] = new BucketTotals();
        return totals;
    }


    /// <summary>
    /// 固定 4 次数据集访问：采购订单 + 持久化引用行（有界，含 1 条溢出探测）+ 付款单 +
    /// 付款单侧有效引用合计（数据库侧分组聚合）；全程只读、不写库，金额按持久化行原样汇总（不重算、不改写）。
    /// </summary>
    private static async Task<BuildResult> BuildAsync(
        IErpDbContext db, IReadOnlyList<long> orderIds, int rowCap)
    {
        var orders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync();
        var orderById = orders.ToDictionary(o => o.Id);

        var rows = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && orderIds.Contains(a.PurchaseOrderId))
            .OrderBy(a => a.PurchaseOrderId).ThenBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .Take(rowCap + 1)
            .ToListAsync();
        var truncated = rows.Count > rowCap;
        if (truncated) rows.RemoveRange(rowCap, rows.Count - rowCap);

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var payments = paymentIds.Count == 0
            ? new List<FinancePayment>()
            : await db.FinancePayments.AsNoTracking()
                .Where(p => paymentIds.Contains(p.Id))
                .ToListAsync();
        var paymentById = payments.ToDictionary(p => p.Id);

        // 付款单侧有效引用合计：数据库侧分组聚合，不装载明细行（有界，与行数无关）
        var activeTotals = paymentIds.Count == 0
            ? new List<PaymentActiveTotalRow>()
            : await db.SupplierPaymentAllocations.AsNoTracking()
                .Where(a => !a.IsDeleted && a.Status == SupplierPaymentAllocationRules.StatusActive
                            && paymentIds.Contains(a.PaymentId))
                .GroupBy(a => a.PaymentId)
                .Select(g => new PaymentActiveTotalRow(g.Key, g.Sum(a => a.AllocatedAmount)))
                .ToListAsync();
        var activeTotalByPayment = activeTotals.ToDictionary(t => t.PaymentId);

        var rowsByOrder = rows
            .GroupBy(r => r.PurchaseOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<long, OrderEvidence>(orderIds.Count);
        foreach (var orderId in orderIds)
        {
            orderById.TryGetValue(orderId, out var order);
            var orderRows = rowsByOrder.TryGetValue(orderId, out var list)
                ? list
                : new List<SupplierPaymentAllocation>();
            result[orderId] = BuildOrderEvidence(orderId, order, orderRows, paymentById, activeTotalByPayment,
                truncated);
        }

        return new BuildResult(result, truncated);
    }

    /// <summary>单张订单派生：逐条引用行分桶 → 汇总 + 明细 + 报表复用聚合（历史 / 无效证据单独列示）</summary>
    private static OrderEvidence BuildOrderEvidence(long orderId, PurchaseOrder? order,
        List<SupplierPaymentAllocation> rows, Dictionary<long, FinancePayment> paymentById,
        Dictionary<long, PaymentActiveTotalRow> activeTotalByPayment, bool truncated)
    {
        var totals = EmptyTotals();
        var lines = new List<PurchaseOrderPaymentEvidenceLine>(rows.Count);
        var activePayments = new Dictionary<long, decimal>();   // 付款单 Id → 金额快照（有效证据按付款单去重）

        foreach (var row in rows)
        {
            paymentById.TryGetValue(row.PaymentId, out var payment);
            activeTotalByPayment.TryGetValue(row.PaymentId, out var activeTotal);
            var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
            var (bucket, reason) = ClassifyRow(row, payment, order);
            var unallocated = UnallocatedOf(payment, activeTotal, currency);

            totals[bucket].Add(row.AllocatedAmount, row.PaymentId);
            if (bucket == PurchaseOrderPaymentEvidenceSemantics.BucketRecorded)
            {
                activePayments[row.PaymentId] = SupplierPaymentAllocationRules.AuthoritativePaymentAmount(
                    row.PaymentAmount, currency);
            }

            lines.Add(MapLine(row, payment, bucket, reason, unallocated,
                order is not null && !order.IsDeleted));
        }

        var recorded = totals[PurchaseOrderPaymentEvidenceSemantics.BucketRecorded].Amount;
        var recordedPaymentAmount = activePayments.Values.Sum();
        var unallocatedPart = recordedPaymentAmount - recorded;
        if (unallocatedPart < 0) unallocatedPart = 0;

        var aggregate = new PaymentEvidenceAggregate(
            orderId,
            recorded,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketRecorded].RowCount,
            activePayments.Count,
            recordedPaymentAmount,
            unallocatedPart,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketVoided].RowCount,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketVoided].Amount,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketInvalid].RowCount,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketInvalid].Amount,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable].RowCount,
            totals[PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable].Amount);

        var fallbackCurrency = CurrencyAmountRules.NormalizeCurrency(
            order is not null && !order.IsDeleted
                ? order.Currency.ToString()
                : lines.Find(l => l.Currency.Length > 0)?.Currency);

        var summary = BuildSummary(orderId, order, totals, rows.Count, truncated, fallbackCurrency, aggregate,
            string.Empty);
        return new OrderEvidence(summary, lines, aggregate);
    }

    /// <summary>
    /// 付款单中未指向任何采购订单的金额（只按持久化有效行聚合派生，下限 0）：仅作展示上下文，
    /// 不是银行未付金额、不是应付余额；付款单缺失 / 已删除或命中上限时为 null（未知）。
    /// </summary>
    private static decimal? UnallocatedOf(FinancePayment? payment, PaymentActiveTotalRow? activeTotal,
        string currency)
    {
        if (payment is null || payment.IsDeleted || activeTotal is null) return null;

        var paymentAmount = SupplierPaymentAllocationRules.AuthoritativePaymentAmount(payment.Amount, currency);
        var unallocated = paymentAmount - activeTotal.Amount;
        return unallocated < 0 ? 0m : unallocated;
    }

    /// <summary>
    /// 单条持久化引用行的证据分类（ERP-050 的核心口径）：只有「引用行有效、付款单可用、
    /// 引用行快照自相一致（供应商 / 币种 / 订单币种）且订单可用、供应商与币种都一致」才计入有效合计；
    /// 其余一律落到已作废 / 无效 / 无法确认分桶，并给出可读原因（绝不换算、合并或改派）。
    /// <para>订单资格判定复用 ERP-049 的权威规则 <see cref="SupplierPaymentAllocationRules.EvaluateOrderEligibility"/>，
    /// 不引入第二套匹配算法。</para>
    /// </summary>
    private static (string Bucket, string Reason) ClassifyRow(
        SupplierPaymentAllocation row, FinancePayment? payment, PurchaseOrder? order)
    {
        if (row.Status == SupplierPaymentAllocationRules.StatusVoided)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketVoided,
                $"引用行已作废（原因：{row.VoidReason}）：历史证据仅供核对，金额不计入有效合计（作废保留可读）");
        }

        if (row.Status != SupplierPaymentAllocationRules.StatusActive)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
                $"引用行状态码 {row.Status} 无法识别：不能确认证据有效性，金额不计入有效合计");
        }

        if (row.AllocatedAmount <= 0)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
                $"引用金额 {row.AllocatedAmount} 不是正数：不作为有效证据，不计入有效合计");
        }

        if (payment is null || payment.IsDeleted)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable,
                "引用行指向的付款单不存在或已删除：证据有效性无法确认，金额不计入有效合计"
                + "（不按 0 处理，也不猜测归属到其他订单）");
        }

        if (row.SupplierId != payment.SupplierId)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
                $"引用行供应商快照（Id={row.SupplierId}）与付款单供应商（Id={payment.SupplierId}）不一致："
                + "按无效历史证据单列，不换算、不合并、也不改派到其他订单");
        }

        var rowCurrency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        if (!string.Equals(rowCurrency, paymentCurrency, StringComparison.Ordinal))
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
                $"引用行币种 {rowCurrency} 与付款单币种 {paymentCurrency} 不一致："
                + "按无效历史证据单列（不做汇率换算、不合并为同一金额）");
        }

        var orderCurrencySnapshot = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency);
        if (!string.Equals(orderCurrencySnapshot, rowCurrency, StringComparison.Ordinal))
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
                $"引用行订单币种快照 {orderCurrencySnapshot} 与引用行币种 {rowCurrency} 不一致："
                + "快照自相矛盾，按无效历史证据单列（不做汇率换算）");
        }

        if (order is null || order.IsDeleted)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable,
                "采购订单不存在或已删除：无法按权威口径核对引用，金额不计入有效合计（不按 0 处理）");
        }

        var (eligible, text) = SupplierPaymentAllocationRules.EvaluateOrderEligibility(
            payment.SupplierId, paymentCurrency, order);
        if (eligible)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketRecorded,
                text + "：有效引用证据，计入有效合计（不代表已付款 / 未付款 / 已结清）");
        }

        if (order.Status == DocumentStatus.Cancelled)
        {
            return (PurchaseOrderPaymentEvidenceSemantics.BucketRecorded,
                text + "：该已登记引用证据金额仅作历史参考（照实计入有效合计），不代表已付款或应付余额");
        }

        return (PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
            text + "：该已登记引用证据按无效历史证据单列，不计入有效合计，也不改派到其他订单");
    }


    /// <summary>
    /// 引用行 → 证据明细行（纯映射；金额 / 单号 / 日期 / 状态一律按**持久化行快照**原样呈现，
    /// 付款单当前可用性只作只读标注，绝不回写快照、也不做汇率换算）。
    /// </summary>
    private static PurchaseOrderPaymentEvidenceLine MapLine(SupplierPaymentAllocation row,
        FinancePayment? payment, string bucket, string reason, decimal? paymentUnallocated, bool orderAvailable)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency);
        var paymentAvailable = payment is not null && !payment.IsDeleted;

        return new PurchaseOrderPaymentEvidenceLine
        {
            AllocationId = row.Id,
            PaymentId = row.PaymentId,
            PaymentNo = row.PaymentNo ?? string.Empty,
            PaymentDate = row.PaymentDate,
            PaymentStatus = paymentAvailable ? (int)payment!.Status : null,
            PaymentStatusText = paymentAvailable
                ? SupplierPaymentAllocationRules.PaymentStatusText((int)payment!.Status)
                : "无法确认（付款单不存在或已删除，状态按快照保留）",
            PaymentAmount = row.PaymentAmount,
            AllocatedAmount = row.AllocatedAmount,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            SupplierId = row.SupplierId,
            SupplierCode = row.SupplierCode ?? string.Empty,
            SupplierName = row.SupplierName ?? string.Empty,
            OrderNo = row.OrderNo ?? string.Empty,
            OrderDate = row.OrderDate,
            OrderStatusText = SupplierPaymentAllocationRules.OrderStatusText(row.OrderStatus),
            OrderCurrency = orderCurrency,
            PaymentAvailable = paymentAvailable,
            PaymentAvailabilityText = SupplierPaymentAllocationRules.PaymentAvailabilityText(payment),
            OrderAvailabilityText = orderAvailable
                ? "采购订单可用"
                : "采购订单已删除或不存在：历史引用保留可读（订单可用性以引用行快照为准）",
            Status = row.Status,
            StatusText = StatusText(row.Status),
            IsRecordedEvidence = bucket == PurchaseOrderPaymentEvidenceSemantics.BucketRecorded,
            IsVoided = row.Status == SupplierPaymentAllocationRules.StatusVoided,
            Bucket = bucket,
            BucketText = PurchaseOrderPaymentEvidenceSemantics.BucketText(bucket),
            PaymentUnallocatedAmount = paymentUnallocated,
            AllocatedAt = row.AllocatedAt,
            VoidedAt = row.VoidedAt,
            VoidReason = row.VoidReason ?? string.Empty,
            Remark = row.Remark ?? string.Empty,
            Reason = reason
        };
    }

    /// <summary>引用行状态文案（未知状态码照实说明，绝不抛异常、也绝不按有效兜底）</summary>
    private static string StatusText(int status) => status switch
    {
        SupplierPaymentAllocationRules.StatusActive => "有效",
        SupplierPaymentAllocationRules.StatusVoided => "已作废",
        _ => $"无法识别（状态码 {status}）"
    };


    /// <summary>
    /// 汇总派生：订单可用性文案复用 ERP-044 常量；有效已引用金额只取有效（未作废）引用行，
    /// 返回时把已作废 / 无效 / 无法确认分别单列；命中上限时金额与计数一律为 null（未知）。
    /// </summary>
    private static PurchaseOrderPaymentEvidenceSummary BuildSummary(long orderId, PurchaseOrder? order,
        Dictionary<string, BucketTotals> totals, int rowCount, bool truncated, string fallbackCurrency,
        PaymentEvidenceAggregate aggregate, string orderNoFallback)
    {
        var available = order is not null && !order.IsDeleted;
        var cancelled = available && order!.Status == DocumentStatus.Cancelled;

        var orderState = !available
            ? SupplierInvoiceReconciliationSemantics.OrderStateUnavailable
            : cancelled
                ? SupplierInvoiceReconciliationSemantics.OrderStateCancelled
                : SupplierInvoiceReconciliationSemantics.OrderStateAvailable;

        var orderStateText = !available
            ? "采购订单不存在或已删除：订单金额未知（不按 0 处理）"
            : cancelled
                ? "采购订单已取消：订单金额仅作历史参考，付款引用证据不参与结算、逾期或欠款判定"
                : $"采购订单可用（当前状态 {order!.Status}）";

        var currency = CurrencyAmountRules.NormalizeCurrency(
            available ? order!.Currency.ToString() : fallbackCurrency);

        var recorded = totals[PurchaseOrderPaymentEvidenceSemantics.BucketRecorded].Amount;
        var hasAnyRow = rowCount > 0;
        var hasHistorical = PurchaseOrderPaymentEvidenceSemantics.SupportedBuckets.Any(b =>
            PurchaseOrderPaymentEvidenceSemantics.IsHistoricalBucket(b) && totals[b].RowCount > 0);
        var recordedPaymentAmount = truncated ? (decimal?)null : aggregate.RecordedPaymentAmount;
        var unallocated = truncated ? (decimal?)null : aggregate.UnallocatedPaymentAmount;

        decimal? Money(string bucket) => truncated ? null : totals[bucket].Amount;
        int? Rows(string bucket) => truncated ? null : totals[bucket].RowCount;

        return new PurchaseOrderPaymentEvidenceSummary
        {
            PurchaseOrderId = orderId,
            OrderNo = order?.OrderNo ?? orderNoFallback,
            OrderAvailable = available,
            OrderState = orderState,
            OrderStateText = orderStateText,
            OrderStatusText = available ? order!.Status.ToString() : "不存在或已删除",
            OrderCurrency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            OrderedAmount = available ? order!.TotalAmount : null,
            HasPaymentEvidence = hasAnyRow,
            HasHistoricalEvidence = hasHistorical,
            Truncated = truncated,
            RecordedAllocatedAmount = Money(PurchaseOrderPaymentEvidenceSemantics.BucketRecorded),
            RecordedPaymentCount = truncated ? null : aggregate.ActivePaymentCount,
            AllocationCount = truncated ? null : rowCount,
            RecordedPaymentAmount = recordedPaymentAmount,
            UnallocatedPaymentAmount = unallocated,
            VoidedAllocatedAmount = Money(PurchaseOrderPaymentEvidenceSemantics.BucketVoided),
            VoidedAllocationCount = Rows(PurchaseOrderPaymentEvidenceSemantics.BucketVoided),
            InvalidAllocatedAmount = Money(PurchaseOrderPaymentEvidenceSemantics.BucketInvalid),
            InvalidAllocationCount = Rows(PurchaseOrderPaymentEvidenceSemantics.BucketInvalid),
            UnavailableAllocatedAmount = Money(PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable),
            UnavailableAllocationCount = Rows(PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable),
            EvidenceLabel = PurchaseOrderPaymentEvidenceSemantics.EvidenceLabel(
                truncated, truncated ? null : recorded, hasAnyRow),
            EvidenceNote = BuildEvidenceNote(available, cancelled, truncated, hasAnyRow, totals, recorded,
                recordedPaymentAmount, unallocated, currency)
        };
    }


    /// <summary>
    /// 行级说明：已知什么 / 缺什么 / 为什么不能当银行付款、应付余额或结算依据；
    /// 历史 / 无效 / 无法确认证据逐桶点名，避免被读成已付款。
    /// </summary>
    private static string BuildEvidenceNote(bool available, bool cancelled, bool truncated, bool hasAnyRow,
        Dictionary<string, BucketTotals> totals, decimal recorded, decimal? recordedPaymentAmount,
        decimal? unallocated, string currency)
    {
        var sb = new StringBuilder();

        if (truncated)
            sb.Append(PurchaseOrderPaymentEvidenceSemantics.TruncatedNote);
        else if (!available || cancelled)
            sb.Append(PurchaseOrderPaymentEvidenceSemantics.UnknownOrderNote);
        else if (recorded > 0)
            sb.Append($"有效付款引用证据 {recorded} {currency} 只按有效（未作废）的持久化引用行派生；"
                + $"参与证据的付款单金额快照合计 {recordedPaymentAmount} {currency}，"
                + $"其中未指向本订单 {unallocated} {currency}"
                + "（可能指向其他采购订单，不是银行未付金额，也不是应付余额）。");
        else if (hasAnyRow)
            sb.Append("该采购订单没有有效付款引用证据：现有引用行都是已作废 / 无效或无法确认，均不计入有效合计；"
                + "这是付款引用证据缺口，不代表未付款、已付款、已结清或逾期。");
        else
            sb.Append(PurchaseOrderPaymentEvidenceSemantics.NoEvidenceNote);

        if (!truncated)
        {
            AppendBucket(sb, totals, PurchaseOrderPaymentEvidenceSemantics.BucketVoided, "已作废历史证据");
            AppendBucket(sb, totals, PurchaseOrderPaymentEvidenceSemantics.BucketInvalid,
                "无效历史证据（供应商 / 币种或快照不一致）");
            AppendBucket(sb, totals, PurchaseOrderPaymentEvidenceSemantics.BucketUnavailable,
                "无法确认的证据（付款单已删除或不存在）");
        }

        sb.Append(" 以上金额一律为付款引用证据口径：不是银行付款金额、不是应付余额、不是发票核销或结算结果，"
            + "也不得据此判定已付款 / 未付款 / 逾期或据以付款。");
        return sb.ToString();
    }

    /// <summary>按引用行条数点名的分桶说明（历史 / 无效 / 无法确认：金额与付款单张数都只作参考）</summary>
    private static void AppendBucket(StringBuilder sb, Dictionary<string, BucketTotals> totals,
        string bucket, string label)
    {
        var item = totals[bucket];
        if (item.RowCount == 0) return;
        sb.Append($" 另有{label} {item.RowCount} 条引用行（金额 {item.Amount}，涉及 {item.PaymentIds.Count} 张付款单）："
            + "不计入有效合计，也不换算 / 合并 / 改派到其他订单，可在明细中单独查看原因。");
    }
}
