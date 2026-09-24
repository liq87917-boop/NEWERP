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
/// 销售订单收款引用证据口径常量（ERP-054）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免把「收款引用证据」写成「已收款 / 已收讫 / 已结清 / 应收余额 / 逾期」，也避免把已作废 / 无效 /
/// 无法确认的引用行混进有效合计。
/// <para>定位：<b>销售订单维度的收款引用证据只读视图</b>（证据来自 ERP-053 的持久化引用行 <c>CustomerReceiptAllocations</c>）——
/// 它<strong>不是</strong>银行入账 / 到账凭证、<strong>不是</strong>应收账款台账或余额、<strong>不是</strong>货款核销、
/// <strong>不是</strong>客户对账单、<strong>不是</strong>税务（销项）判断，也<strong>不是</strong>账龄表或结算结果。</para>
/// <para>与 ERP-046 的关系：订单与收款核对报表复用本文件的分桶与聚合
/// （<see cref="SalesOrderReceiptEvidence.AggregatesForOrdersAsync"/>），把「已订 / 已出数量（ERP-032）」、
/// 「收款申请链接（ERP-028 / ERP-032）」与「收款引用登记证据（ERP-053）」作为**三类独立标注的证据**并列展示，
/// 不建立第二套匹配或对账算法。</para>
/// </summary>
public static class SalesOrderReceiptEvidenceSemantics
{
    // ==================== 1. 有界上限 ====================

    /// <summary>列表批量查询最多接受的销售订单张数（列表按页取 Id；超出直接拒绝，不静默截断）</summary>
    public const int MaxBatchOrders = 200;

    /// <summary>
    /// 列表批量查询最多读取的持久化引用行数（有界）：命中上限时本次响应内全部金额与计数按「未知」返回，
    /// 绝不报出「只统计了一部分证据」的合计。
    /// </summary>
    public const int MaxBatchEvidenceRows = 5000;

    /// <summary>单张销售订单详情最多读取的持久化引用行数（有界）：命中上限时金额与计数按「未知」返回，明细只显示已读取部分</summary>
    public const int MaxOrderEvidenceRows = 500;

    /// <summary>
    /// 报表侧（ERP-046 订单 / 收款核对报表）一次最多聚合的销售订单张数（有界）：
    /// 超出时该批订单的收款引用证据一律按「未知」（null）处理，绝不给部分合计。
    /// </summary>
    public const int MaxAggregateOrders = 500;

    // ==================== 2. 证据分桶 ====================

    /// <summary>有效引用证据（收款单可用、引用行快照自相一致、订单可用且客户 / 币种一致）：唯一计入有效合计的分桶</summary>
    public const string BucketRecorded = "recorded";

    /// <summary>已作废历史证据：单独分桶，绝不计入有效合计（保留可读，含作废原因）</summary>
    public const string BucketVoided = "voided";

    /// <summary>无效历史证据（客户 / 币种不一致、订单币种快照自相矛盾、金额非正数或状态码无法识别）：不换算、不合并、不改派，绝不计入有效合计</summary>
    public const string BucketInvalid = "invalid";

    /// <summary>无法确认的证据（引用行指向的收款单或销售订单不存在 / 已删除）：金额不能确认，绝不并入有效合计</summary>
    public const string BucketUnavailable = "unavailable";

    /// <summary>支持的分桶取值（超出范围不静默兜底）</summary>
    public static readonly string[] SupportedBuckets =
        { BucketRecorded, BucketVoided, BucketInvalid, BucketUnavailable };

    /// <summary>分桶文案（接口、界面与文档同源）</summary>
    public static string BucketText(string bucket) => bucket switch
    {
        BucketRecorded => "有效收款引用证据（计入有效合计）",
        BucketVoided => "已作废历史证据（不计入有效合计）",
        BucketInvalid => "无效历史证据（客户 / 币种或快照不一致：不换算、不合并、不改派）",
        _ => "无法确认的证据（收款单或销售订单不存在 / 已删除：金额无法确认）",
    };

    /// <summary>是否属于「历史 / 无效证据」（= 不计入有效合计的分桶）</summary>
    public static bool IsHistoricalBucket(string bucket)
        => bucket is BucketVoided or BucketInvalid or BucketUnavailable;

    // ==================== 3. 证据短标签与状态 ====================

    /// <summary>有有效收款引用证据</summary>
    public const string LabelRecorded = "有收款引用证据";

    /// <summary>只有历史 / 无效收款引用证据（绝不呈现为已收款 / 已收讫 / 已结清）</summary>
    public const string LabelHistoricalOnly = "仅有历史 / 无效收款引用证据";

    /// <summary>没有任何收款引用证据（证据缺口，绝不呈现为未收款 / 已收款 / 逾期 / 欠款）</summary>
    public const string LabelNone = "无收款引用证据";

    /// <summary>命中有界上限，证据无法穷尽</summary>
    public const string LabelUnknown = "未知（超出有界读取上限）";

    /// <summary>状态键：已登记有效收款引用证据</summary>
    public const string AllocationRecorded = "recorded";

    /// <summary>状态键：只有历史 / 无效收款引用证据</summary>
    public const string AllocationHistoricalOnly = "historical_only";

    /// <summary>状态键：没有任何收款引用证据（证据缺口）</summary>
    public const string AllocationNone = "none";

    /// <summary>状态键：命中有界上限，无法穷尽</summary>
    public const string AllocationUnknown = "unknown";

    /// <summary>
    /// 证据短标签（界面与接口同源）：命中上限 → 未知；有有效证据 → 有收款引用证据；
    /// 只有历史 / 无效证据 → 仅有历史 / 无效收款引用证据；一条都没有 → 无收款引用证据。
    /// </summary>
    public static string EvidenceLabel(bool truncated, decimal? recordedAmount, bool hasAnyRow)
    {
        if (truncated) return LabelUnknown;
        if (recordedAmount is > 0) return LabelRecorded;
        return hasAnyRow ? LabelHistoricalOnly : LabelNone;
    }

    /// <summary>证据状态键（前端按状态着色；与 <see cref="EvidenceLabel"/> 同源判定）</summary>
    public static string AllocationStatus(bool truncated, decimal? recordedAmount, bool hasAnyRow)
    {
        if (truncated) return AllocationUnknown;
        if (recordedAmount is > 0) return AllocationRecorded;
        return hasAnyRow ? AllocationHistoricalOnly : AllocationNone;
    }

    // ==================== 4. 订单可用性 ====================

    /// <summary>销售订单可用（存在且未删除）</summary>
    public const string OrderStateAvailable = "available";

    /// <summary>销售订单已取消（历史订单：证据只作历史参考）</summary>
    public const string OrderStateCancelled = "cancelled";

    /// <summary>销售订单不存在或已删除（订单金额未知，不按 0 处理）</summary>
    public const string OrderStateUnavailable = "unavailable";

    // ==================== 5. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（证据来源、分桶、未引用部分与未知处理）</summary>
    public const string RuleText =
        "本视图是销售订单的**收款引用证据口径**（只读派生）：全部金额只按 ERP-053 的持久化引用表"
        + "（CustomerReceiptAllocations）派生，且只统计**有效（未作废）**的引用行；"
        + "已作废历史证据、无效历史证据（客户 / 币种不一致或快照自相矛盾）与无法确认证据（收款单 / 订单不存在或已删除）"
        + "一律单独分桶列出，绝不并入有效合计，也不换算、不合并、不改派到其他订单；"
        + "「参与证据的收款单金额快照合计 − 本订单已引用金额」只表示这些收款单金额中**未指向本订单**的部分"
        + "（可能指向其他销售订单），它不是银行未到账金额、不是应收余额、也不是客户欠款；"
        + "「订单金额 − 有效收款引用证据合计」只表示订单金额中没有任何有效收款引用证据指向的部分，同样不是应收余额；"
        + "没有任何引用行时只显示「无收款引用证据」（证据缺口），绝不推断为未收款 / 已收款 / 已结清 / 逾期或欠款。";

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public const string ScopeText =
        "有界读取：列表批量汇总一次最多 200 张销售订单、最多 5000 条持久化引用行；单张订单详情最多 500 条引用行；"
        + "命中上限时金额与计数一律按「未知」（null）返回，绝不报出部分合计，明细只显示已读取部分；"
        + "读取固定次数数据集访问（订单 + 引用行 + 收款单 + 收款单侧有效引用合计），与订单张数 / 行数无关，绝无逐行查库。";

    /// <summary>与银行入账 / 应收台账 / 核销 / 对账单 / 账龄 / 结算的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本视图只是客户收款单 → 销售订单 的**引用证据**只读呈现：不是银行入账 / 到账凭证、不是收款授权或收款状态、"
        + "不是应收账款台账或应收余额、不是货款核销或销项（税务）判断、也不是客户对账单、结算结果与账龄表 —— "
        + "它不判断是否已收款、是否已结清或是否逾期，不推算账期与到期日，也不产生任何记账、凭证、收付款或结算单；"
        + "界面与接口一律把该金额标注为**收款引用证据**，不得当作已收款金额、欠款金额或可催收金额。";

    /// <summary>没有任何收款引用证据时的提示（绝不能被读成未收款 / 已收款 / 已结清 / 逾期）</summary>
    public const string NoEvidenceNote =
        "本销售订单没有任何已登记收款引用行：这是**收款引用证据缺口**，不代表未收款、已收款、已结清、逾期或欠款，"
        + "也不构成应收余额、收款授权或结算依据。";

    /// <summary>命中读取上限时的提示（金额未知，不给部分合计）</summary>
    public const string TruncatedNote =
        "本次读取命中系统有界上限（持久化引用行数量超出上限），无法穷尽该订单的收款引用证据："
        + "金额与计数一律按「未知」显示，不作部分合计。";

    /// <summary>无可信订单金额时的提示（订单已取消或不可用）</summary>
    public const string UnknownOrderNote =
        "销售订单已取消或已删除：订单金额按未知显示（不按 0 处理），也不推断收款、结算或逾期状态。";
}

/// <summary>
/// 销售订单 Id 列表查询参数（ERP-054，只读）：逗号分隔的正整数，去重、保序、有界（超出上限直接拒绝）。
/// <para>与 ERP-048 / ERP-050 的列表批量查询参数同口径，便于列表页复用同一套分批策略。</para>
/// </summary>
public sealed class SalesOrderReceiptEvidenceQuery
{
    /// <summary>逗号分隔的销售订单 Id（如 <c>12,34,56</c>；留空 = 不查询任何订单）</summary>
    public string? Ids { get; set; }

    /// <summary>归一化后的订单 Id（去重、保持提交顺序）</summary>
    public IReadOnlyList<long> OrderIds => _orderIds;

    private readonly List<long> _orderIds = new();

    /// <summary>
    /// 归一化并校验：只接受正整数 Id，去重（保留首次出现顺序）；非法片段或超出
    /// <see cref="SalesOrderReceiptEvidenceSemantics.MaxBatchOrders"/> 一律拒绝，不静默丢弃、不静默截断。
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
                    $"订单 Id「{token}」无效：收款引用证据批量接口只接受逗号分隔的正整数销售订单 Id");

            if (_orderIds.Contains(id)) continue;
            _orderIds.Add(id);

            if (_orderIds.Count > SalesOrderReceiptEvidenceSemantics.MaxBatchOrders)
                throw BusinessException.InvalidParameter(
                    $"一次最多查询 {SalesOrderReceiptEvidenceSemantics.MaxBatchOrders} 张销售订单的收款引用证据"
                    + "（列表页请按页分批请求，不要一次拉取全量）");
        }
    }
}

/// <summary>
/// 销售订单收款引用证据汇总（ERP-054，只读派生；列表列与详情头部共用）。
/// <para>金额口径：<see cref="RecordedAllocatedAmount"/> 只统计**有效（未作废）**的持久化引用行；
/// 已作废 / 无效 / 无法确认分别单列，绝不并入有效合计，也绝不折算、合并或改派到其他订单。</para>
/// <para>未知处理：命中读取上限或订单已取消 / 不可用时，金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 本汇总<strong>不是</strong>银行入账凭证、应收余额、货款核销或结算状态。</para>
/// </summary>
public sealed class SalesOrderReceiptEvidenceSummary
{
    /// <summary>销售订单 Id</summary>
    public long SalesOrderId { get; init; }

    /// <summary>销售订单号（订单已删除时按引用行快照显示）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单当前是否可用（存在且未删除）</summary>
    public bool OrderAvailable { get; init; }

    /// <summary>订单可用性状态：available / cancelled / unavailable</summary>
    public string OrderState { get; init; } = SalesOrderReceiptEvidenceSemantics.OrderStateUnavailable;

    /// <summary>订单可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderStateText { get; init; } = string.Empty;

    /// <summary>订单当前单据状态文案（订单不存在 / 已删除时照实说明）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>订单币种（原币；不同币种绝不合并、不做汇率换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用；复用 ERP-053 的币种精度口径）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>订单已落库总额；null = 未知（订单不存在 / 已删除），不等于 0</summary>
    public decimal? OrderedAmount { get; init; }

    /// <summary>是否读到至少一条持久化引用行（不代表有效证据，也不代表已收款）</summary>
    public bool HasReceiptAllocationEvidence { get; init; }

    /// <summary>是否存在已作废 / 无效 / 无法确认的历史证据（必须单独查看，绝不并入有效合计）</summary>
    public bool HasHistoricalEvidence { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>有效已引用金额（未作废、快照自相一致的有效引用行合计，原币）；null = 未知（命中上限）</summary>
    public decimal? RecordedAllocatedAmount { get; init; }

    /// <summary>参与有效合计的收款单张数（按收款单去重）；null = 未知（命中上限）</summary>
    public int? RecordedReceiptCount { get; init; }

    /// <summary>本次读取到的持久化引用行总条数（含历史 / 无效证据）；null = 未知（命中上限）</summary>
    public int? AllocationCount { get; init; }

    /// <summary>
    /// 参与有效证据的收款单金额快照合计（按收款单去重后求和，仅作上下文）；null = 未知（命中上限）。
    /// 它<strong>不是</strong>应收余额，也<strong>不是</strong>银行入账金额。
    /// </summary>
    public decimal? RecordedReceiptAmount { get; init; }

    /// <summary>
    /// 未指向本订单的金额（= 参与证据的收款单金额快照合计 − 本订单有效已引用金额，下限 0）：
    /// 只表示这些收款单金额中<strong>未指向本订单</strong>的部分（可能指向其他销售订单），
    /// 不是银行未到账金额、不是应收余额、也不是客户欠款；null = 未知（命中上限）。
    /// </summary>
    public decimal? UnreferencedReceiptAmount { get; init; }

    /// <summary>
    /// 订单金额中未被任何有效收款引用证据指向的部分（= 订单金额 − 有效已引用金额，下限 0）；
    /// 只是「没有有效引用证据指向」的金额，不是应收余额、不是未收款金额、也不是账龄依据；null = 未知。
    /// </summary>
    public decimal? UnreferencedOrderAmount { get; init; }

    /// <summary>已作废历史证据金额（原币；绝不计入有效合计，保留可读）；null = 未知（命中上限）</summary>
    public decimal? VoidedAllocatedAmount { get; init; }

    /// <summary>已作废历史证据引用行条数；null = 未知（命中上限）</summary>
    public int? VoidedAllocationCount { get; init; }

    /// <summary>无效历史证据金额（客户 / 币种或快照不一致；绝不换算、合并或改派）；null = 未知（命中上限）</summary>
    public decimal? InvalidAllocatedAmount { get; init; }

    /// <summary>无效历史证据引用行条数；null = 未知（命中上限）</summary>
    public int? InvalidAllocationCount { get; init; }

    /// <summary>无法确认的证据金额（收款单 / 订单不存在或已删除：无法确认有效性）；null = 未知（命中上限）</summary>
    public decimal? UnavailableAllocatedAmount { get; init; }

    /// <summary>无法确认的证据引用行条数；null = 未知（命中上限）</summary>
    public int? UnavailableAllocationCount { get; init; }

    /// <summary>证据状态键（recorded / historical_only / none / unknown；前端按状态着色）</summary>
    public string AllocationStatus { get; init; } = SalesOrderReceiptEvidenceSemantics.AllocationUnknown;

    /// <summary>证据短标签（有收款引用证据 / 仅有历史无效证据 / 无收款引用证据 / 未知）</summary>
    public string EvidenceLabel { get; init; } = SalesOrderReceiptEvidenceSemantics.LabelUnknown;

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不能作为已收款或应收余额）</summary>
    public string EvidenceNote { get; init; } = string.Empty;
}

/// <summary>
/// 一条收款引用证据（ERP-054，只读派生；明细行 = 一条 ERP-053 持久化引用行）。
/// <para><see cref="Bucket"/> 表明该引用行是有效证据还是历史 / 无效 / 无法确认证据；
/// 只有 <c>recorded</c> 计入有效合计；其余一律单独列出并给出 <see cref="Reason"/>；
/// 系统绝不换算、合并或改派到其他订单。</para>
/// </summary>
public sealed class SalesOrderReceiptEvidenceLine
{
    /// <summary>引用行 Id（ERP-053 <c>CustomerReceiptAllocations.Id</c>）</summary>
    public long AllocationId { get; init; }

    /// <summary>收款单 Id（引用行指向的既有客户收款单）</summary>
    public long ReceiptId { get; init; }

    /// <summary>收款单号快照（收款单改名后仍按登记当时口径可读）</summary>
    public string ReceiptNo { get; init; } = string.Empty;

    /// <summary>收款日期快照</summary>
    public DateTime ReceiptDate { get; init; }

    /// <summary>收款单状态快照（DocumentStatus 取值）；null = 收款单缺失无法确认</summary>
    public int? ReceiptStatus { get; init; }

    /// <summary>收款单状态文案（缺失时照实说明「无法确认」）</summary>
    public string ReceiptStatusText { get; init; } = string.Empty;

    /// <summary>收款单金额快照（原币；与引用金额同币种，仅作展示上下文，不做汇率换算）</summary>
    public decimal ReceiptAmount { get; init; }

    /// <summary>本行引用金额（原币；按持久化引用行原样呈现，不重算、不四舍五入改写）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>币种（原币；引用行币种，与收款单币种一致才可能是有效证据）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>客户 Id 快照（引用行写入时的服务端快照）</summary>
    public long CustomerId { get; init; }

    /// <summary>客户编码快照</summary>
    public string CustomerCode { get; init; } = string.Empty;

    /// <summary>客户名称快照</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>销售订单号快照（引用行写入时口径）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; init; }

    /// <summary>引用行订单状态快照文案（取消后历史证据仍保留登记当时口径）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>引用行的订单币种快照（与引用行币种 / 收款单币种不一致即无效证据，绝不换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>收款单当前是否可用（存在且未删除；**只读标注**）</summary>
    public bool ReceiptAvailable { get; init; }

    /// <summary>收款单可用性文案（已删除时照实说明，历史证据仍可读）</summary>
    public string ReceiptAvailabilityText { get; init; } = string.Empty;

    /// <summary>销售订单当前可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderAvailabilityText { get; init; } = string.Empty;

    /// <summary>引用行状态（1 有效 / 2 已作废；未知取值照实说明）</summary>
    public int Status { get; init; }

    /// <summary>状态文案</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>是否有效证据（唯一计入有效合计的证据类型）</summary>
    public bool IsRecordedEvidence { get; init; }

    /// <summary>是否已作废（历史证据，保留可读）</summary>
    public bool IsVoided { get; init; }

    /// <summary>证据分桶（recorded / voided / invalid / unavailable）</summary>
    public string Bucket { get; init; } = SalesOrderReceiptEvidenceSemantics.BucketUnavailable;

    /// <summary>分桶文案</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>
    /// 该收款单中未指向本订单的金额（= 收款单权威金额快照 − 该收款单全部有效引用行合计，下限 0）；
    /// 只是本登记册口径下该收款单尚未指向本订单的部分，不是银行未到账金额、不是应收余额；
    /// null = 未知（收款单缺失 / 已删除或命中上限）。
    /// </summary>
    public decimal? ReceiptUnreferencedAmount { get; init; }

    /// <summary>登记时间</summary>
    public DateTime AllocatedAt { get; init; }

    /// <summary>作废时间（未作废为 null）</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因（作废保留原始值与原因，绝不重写）</summary>
    public string VoidReason { get; init; } = string.Empty;

    /// <summary>备注（登记说明，不参与任何金额派生）</summary>
    public string Remark { get; init; } = string.Empty;

    /// <summary>计入 / 不计入有效合计的原因说明（无效时给出权威不一致原因）</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// 销售订单收款引用证据详情（ERP-054，只读派生）：汇总 + 逐条证据明细 + 口径文案。
/// <para><see cref="Lines"/> 为有界明细（最多 <see cref="SalesOrderReceiptEvidenceSemantics.MaxOrderEvidenceRows"/> 条）；
/// 命中上限时汇总金额与计数按未知显示，明细仅展示已读取部分。</para>
/// <para>已作废证据保留在 <see cref="Lines"/> 中（<c>voided</c> 分桶，含原始金额 / 快照 / 作废原因），
/// 供显式历史核对，永不并入有效合计。</para>
/// </summary>
public sealed class SalesOrderReceiptEvidenceDetail
{
    /// <summary>汇总（与该订单列表列同口径）</summary>
    public SalesOrderReceiptEvidenceSummary Summary { get; init; } = new();

    /// <summary>逐条持久化引用行证据（有界：最多 500 条；含已作废历史证据）</summary>
    public List<SalesOrderReceiptEvidenceLine> Lines { get; init; } = new();

    /// <summary>本次返回的明细条数</summary>
    public int LineCount { get; init; }

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = SalesOrderReceiptEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = SalesOrderReceiptEvidenceSemantics.ScopeText;

    /// <summary>与银行入账 / 应收台账 / 核销 / 对账单 / 账龄 / 结算的边界说明</summary>
    public string Boundary { get; init; } = SalesOrderReceiptEvidenceSemantics.BoundaryText;

    /// <summary>引用行登记口径（与 ERP-053 同源，不引入第二套匹配算法）</summary>
    public string LinkageRule { get; init; } = CustomerReceiptAllocationRules.RuleText;

    /// <summary>ERP-053 登记册自身的模块边界（与 ERP-053 同源）</summary>
    public string AllocationBoundary { get; init; } = CustomerReceiptAllocationRules.BoundaryText;
}

/// <summary>
/// 销售订单收款引用证据批量汇总（ERP-054，只读派生；列表页一次请求取回本页订单的汇总，不逐行查库）。
/// </summary>
public sealed class SalesOrderReceiptEvidenceBatch
{
    /// <summary>本次请求的订单 Id 张数（去重后）</summary>
    public int RequestedCount { get; init; }

    /// <summary>返回的汇总条数（与请求的订单 Id 一一对应，含未找到 / 已删除的订单）</summary>
    public int ItemCount { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 全部金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>按请求顺序返回的订单汇总</summary>
    public List<SalesOrderReceiptEvidenceSummary> Items { get; init; } = new();

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = SalesOrderReceiptEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = SalesOrderReceiptEvidenceSemantics.ScopeText;

    /// <summary>与银行入账 / 应收台账 / 核销 / 对账单 / 账龄 / 结算的边界说明</summary>
    public string Boundary { get; init; } = SalesOrderReceiptEvidenceSemantics.BoundaryText;

    /// <summary>引用行登记口径（与 ERP-053 同源）</summary>
    public string LinkageRule { get; init; } = CustomerReceiptAllocationRules.RuleText;
}

/// <summary>
/// 单张销售订单的收款引用证据聚合（ERP-046 报表复用入口，只读）。
/// <para>与订单视图**共用同一套分桶与资格判定**，避免出现第二套对账算法。</para>
/// </summary>
public sealed record SalesOrderReceiptEvidenceAggregate(
    long SalesOrderId,
    decimal RecordedAmount,
    int RecordedRowCount,
    int RecordedReceiptCount,
    decimal RecordedReceiptAmount,
    decimal UnreferencedReceiptAmount,
    int VoidedRowCount,
    decimal VoidedAmount,
    int InvalidRowCount,
    decimal InvalidAmount,
    int UnavailableRowCount,
    decimal UnavailableAmount);

/// <summary>
/// 一批销售订单的收款引用证据聚合结果（ERP-046 报表复用，只读有界）。
/// <para><see cref="Truncated"/> 为 true 时调用方必须把相关金额与计数按「未知」处理（不含部分合计）。</para>
/// </summary>
public sealed record SalesOrderReceiptEvidenceAggregateSet(
    IReadOnlyDictionary<long, SalesOrderReceiptEvidenceAggregate> ByOrder,
    bool Truncated)
{
    /// <summary>取某订单的聚合；订单无引用行或未参与聚合时返回全 0（订单可用性由调用方自行判定）</summary>
    public SalesOrderReceiptEvidenceAggregate Get(long salesOrderId)
        => ByOrder.TryGetValue(salesOrderId, out var aggregate) ? aggregate : Empty(salesOrderId);

    /// <summary>全 0 聚合（无引用行；不代表未收款 / 已收款）</summary>
    public static SalesOrderReceiptEvidenceAggregate Empty(long salesOrderId)
        => new(salesOrderId, 0m, 0, 0, 0m, 0m, 0, 0m, 0, 0m, 0, 0m);
}

/// <summary>
/// 销售订单收款引用证据派生（ERP-054，只读）：在既有销售订单列表 / 详情工作流与 ERP-046 客户订单 / 收款
/// 核对报表中，暴露 **ERP-053 持久化收款引用行**的只读汇总，并复用 ERP-053 的权威资格判定与状态文案。
/// <para>边界（重要）：本派生<strong>只读</strong>（全部 <c>AsNoTracking</c>、从不 <c>SaveChanges</c>），
/// 不改写销售订单状态 / 出货进度 / 金额与明细、客户收款单状态与金额、客户信用状态、发票、库存与库存成本、
/// 装柜与单证、佣金 / 回佣、费用或退税记录，也<strong>不</strong>执行收款、记账、核销、结算或催收。</para>
/// <para>有界：最多 4 次数据集访问（销售订单 + 持久化引用行 + 客户收款单 + 收款单侧有效引用合计），
/// 与订单张数 / 行数无关，绝无逐行查库；行数超出上限时金额与计数按「未知」返回，不报部分合计。</para>
/// </summary>
public static class SalesOrderReceiptEvidence
{
    /// <summary>
    /// 单张销售订单的收款引用证据详情（汇总 + 有界证据明细）。
    /// <para>订单不存在或已删除时抛「数据不存在」（与 <c>/api/sales-orders/{id}</c> 同口径）。</para>
    /// </summary>
    public static async Task<SalesOrderReceiptEvidenceDetail> ForOrderAsync(IErpDbContext db, long salesOrderId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId <= 0)
            throw BusinessException.InvalidParameter("销售订单 Id 必须是正整数");

        var built = await BuildAsync(db, new List<long> { salesOrderId },
            SalesOrderReceiptEvidenceSemantics.MaxOrderEvidenceRows);
        var evidence = built.ByOrder[salesOrderId];
        if (!evidence.Summary.OrderAvailable)
            throw BusinessException.NotFound("销售订单不存在或已删除");

        return new SalesOrderReceiptEvidenceDetail
        {
            Summary = evidence.Summary,
            Lines = evidence.Lines,
            LineCount = evidence.Lines.Count,
        };
    }

    /// <summary>
    /// 一批销售订单的收款引用证据汇总（列表页有界批量入口）：按请求顺序返回，与请求的订单 Id 一一对应
    /// （未找到 / 已删除的订单返回「订单不可用 + 金额未知」汇总，绝不静默丢行）。
    /// </summary>
    public static async Task<SalesOrderReceiptEvidenceBatch> ForOrdersAsync(IErpDbContext db,
        SalesOrderReceiptEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var ids = query.OrderIds;
        if (ids.Count == 0)
            return new SalesOrderReceiptEvidenceBatch { RequestedCount = 0, ItemCount = 0 };

        var built = await BuildAsync(db, ids, SalesOrderReceiptEvidenceSemantics.MaxBatchEvidenceRows);
        var items = ids.Select(id => built.ByOrder[id].Summary).ToList();

        return new SalesOrderReceiptEvidenceBatch
        {
            RequestedCount = ids.Count,
            ItemCount = items.Count,
            Truncated = built.Truncated,
            Items = items,
        };
    }

    /// <summary>
    /// 一批销售订单的收款引用证据聚合（ERP-046 报表复用入口，只读有界）：
    /// 与销售订单视图**共用同一套分桶与资格判定**，避免出现第二套对账算法。
    /// <para>命中订单数上限或行数上限时 <see cref="SalesOrderReceiptEvidenceAggregateSet.Truncated"/> 为 true，
    /// 调用方必须把相关金额与计数按「未知」处理（本次结果<strong>不含</strong>部分合计）。</para>
    /// </summary>
    public static async Task<SalesOrderReceiptEvidenceAggregateSet> AggregatesForOrdersAsync(
        IErpDbContext db, IReadOnlyList<long> salesOrderIds,
        int orderCap = SalesOrderReceiptEvidenceSemantics.MaxAggregateOrders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(salesOrderIds);

        var cap = orderCap <= 0 ? SalesOrderReceiptEvidenceSemantics.MaxAggregateOrders : orderCap;
        var ids = salesOrderIds.Where(id => id > 0).Distinct().ToList();

        if (ids.Count == 0)
            return new SalesOrderReceiptEvidenceAggregateSet(
                new Dictionary<long, SalesOrderReceiptEvidenceAggregate>(), false);

        // 超出有界聚合上限：不给部分合计（调用方按未知显示）
        if (ids.Count > cap)
            return new SalesOrderReceiptEvidenceAggregateSet(
                new Dictionary<long, SalesOrderReceiptEvidenceAggregate>(), true);

        var built = await BuildAsync(db, ids, SalesOrderReceiptEvidenceSemantics.MaxBatchEvidenceRows);
        var byOrder = built.ByOrder.ToDictionary(pair => pair.Key, pair => pair.Value.Aggregate);
        return new SalesOrderReceiptEvidenceAggregateSet(byOrder, built.Truncated);
    }

    /// <summary>请求的订单 Id 未找到时的汇总（未知，不按 0；列表列仍能显示「未知」而不是静默丢行）</summary>
    private static SalesOrderReceiptEvidenceSummary BuildSummaryForMissingOrder(long salesOrderId, bool truncated)
        => BuildSummary(salesOrderId, null, EmptyTotals(), 0, truncated, CurrencyAmountRules.DefaultCurrency,
            SalesOrderReceiptEvidenceAggregateSet.Empty(salesOrderId), string.Empty);

    // ==================== 内部：批量读取与派生 ====================

    /// <summary>单张订单的证据派生结果（汇总 + 明细行 + 供报表复用的聚合）</summary>
    private sealed record OrderEvidence(SalesOrderReceiptEvidenceSummary Summary,
        List<SalesOrderReceiptEvidenceLine> Lines, SalesOrderReceiptEvidenceAggregate Aggregate);

    /// <summary>一次批量派生的读取结果（按订单 Id 索引；<see cref="Truncated"/> = 命中行数上限）</summary>
    private sealed record BuildResult(Dictionary<long, OrderEvidence> ByOrder, bool Truncated);

    /// <summary>收款单侧有效引用合计（数据库侧分组聚合结果，只读）</summary>
    private sealed record ReceiptActiveTotalRow(long ReceiptId, decimal Amount);

    /// <summary>单条引用行的分桶累计（金额 / 行数 / 收款单去重）</summary>
    private sealed class BucketTotals
    {
        /// <summary>分桶金额合计（原币，按持久化引用行汇总）</summary>
        public decimal Amount { get; private set; }

        /// <summary>分桶内的持久化引用行条数</summary>
        public int RowCount { get; private set; }

        /// <summary>分桶内的收款单 Id（去重；用于「收款单张数」计数）</summary>
        public HashSet<long> ReceiptIds { get; } = new();

        /// <summary>累计一条引用行</summary>
        public void Add(decimal amount, long receiptId)
        {
            Amount += amount;
            RowCount++;
            ReceiptIds.Add(receiptId);
        }
    }

    /// <summary>全部为空的分桶字典（每个受支持分桶都有条目，避免键缺失时的隐式兜底）</summary>
    private static Dictionary<string, BucketTotals> EmptyTotals()
    {
        var totals = new Dictionary<string, BucketTotals>(StringComparer.Ordinal);
        foreach (var bucket in SalesOrderReceiptEvidenceSemantics.SupportedBuckets)
            totals[bucket] = new BucketTotals();
        return totals;
    }

    /// <summary>
    /// 固定 4 次数据集访问：销售订单 + 持久化引用行（有界，含 1 条溢出探测）+ 客户收款单 +
    /// 收款单侧有效引用合计（数据库侧分组聚合）；全程只读、不写库，金额按持久化行原样汇总（不重算、不改写）。
    /// </summary>
    private static async Task<BuildResult> BuildAsync(
        IErpDbContext db, IReadOnlyList<long> salesOrderIds, int rowCap)
    {
        var orders = await db.SalesOrders.AsNoTracking()
            .Where(o => salesOrderIds.Contains(o.Id))
            .ToListAsync();
        var orderById = orders.ToDictionary(o => o.Id);

        var rows = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && salesOrderIds.Contains(a.SalesOrderId))
            .OrderBy(a => a.SalesOrderId).ThenBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .Take(rowCap + 1)
            .ToListAsync();
        var truncated = rows.Count > rowCap;
        if (truncated) rows.RemoveRange(rowCap, rows.Count - rowCap);

        var receiptIds = rows.Select(r => r.ReceiptId).Distinct().ToList();
        var receipts = await db.FinanceReceipts.AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id))
            .ToListAsync();
        var receiptById = receipts.ToDictionary(r => r.Id);

        // 收款单侧有效引用合计：数据库侧分组聚合，不装载明细行（有界，与行数无关）
        var activeTotals = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive
                        && receiptIds.Contains(a.ReceiptId))
            .GroupBy(a => a.ReceiptId)
            .Select(g => new ReceiptActiveTotalRow(g.Key, g.Sum(a => a.AllocatedAmount)))
            .ToListAsync();
        var activeTotalByReceipt = activeTotals.ToDictionary(t => t.ReceiptId);

        var rowsByOrder = rows
            .GroupBy(r => r.SalesOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<long, OrderEvidence>(salesOrderIds.Count);
        foreach (var salesOrderId in salesOrderIds)
        {
            orderById.TryGetValue(salesOrderId, out var order);
            var orderRows = rowsByOrder.TryGetValue(salesOrderId, out var list)
                ? list
                : new List<CustomerReceiptAllocation>();
            result[salesOrderId] = BuildOrderEvidence(salesOrderId, order, orderRows, receiptById,
                activeTotalByReceipt, truncated);
        }

        return new BuildResult(result, truncated);
    }

    /// <summary>单张订单派生：逐条引用行分桶 → 汇总 + 明细 + 报表复用聚合（历史 / 无效证据单独列示）</summary>
    private static OrderEvidence BuildOrderEvidence(long salesOrderId, SalesOrder? order,
        List<CustomerReceiptAllocation> rows, IReadOnlyDictionary<long, FinanceReceipt> receiptById,
        IReadOnlyDictionary<long, ReceiptActiveTotalRow> activeTotalByReceipt, bool truncated)
    {
        var orderAvailable = order is not null && !order.IsDeleted;
        var totals = EmptyTotals();
        var lines = new List<SalesOrderReceiptEvidenceLine>(rows.Count);
        var recordedReceiptAmounts = new Dictionary<long, decimal>();

        foreach (var row in rows)
        {
            receiptById.TryGetValue(row.ReceiptId, out var receipt);
            var (bucket, reason) = ClassifyRow(row, receipt, order);
            var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
            totals[bucket].Add(row.AllocatedAmount, row.ReceiptId);

            decimal? receiptUnreferenced = null;
            if (bucket == SalesOrderReceiptEvidenceSemantics.BucketRecorded)
            {
                receiptUnreferenced = ReceiptUnreferencedOf(receipt, activeTotalByReceipt, currency);
                if (receipt is not null && !receipt.IsDeleted)
                {
                    recordedReceiptAmounts[row.ReceiptId] =
                        CustomerReceiptAllocationRules.AuthoritativeReceiptAmount(receipt.Amount, currency);
                }
            }

            lines.Add(MapLine(row, receipt, bucket, reason, receiptUnreferenced, orderAvailable));
        }

        var recorded = totals[SalesOrderReceiptEvidenceSemantics.BucketRecorded].Amount;
        var recordedReceiptAmount = recordedReceiptAmounts.Values.Sum();
        var unreferencedReceipt = recordedReceiptAmount - recorded;
        if (unreferencedReceipt < 0) unreferencedReceipt = 0;

        var aggregate = new SalesOrderReceiptEvidenceAggregate(
            salesOrderId,
            recorded,
            totals[SalesOrderReceiptEvidenceSemantics.BucketRecorded].RowCount,
            totals[SalesOrderReceiptEvidenceSemantics.BucketRecorded].ReceiptIds.Count,
            recordedReceiptAmount,
            unreferencedReceipt,
            totals[SalesOrderReceiptEvidenceSemantics.BucketVoided].RowCount,
            totals[SalesOrderReceiptEvidenceSemantics.BucketVoided].Amount,
            totals[SalesOrderReceiptEvidenceSemantics.BucketInvalid].RowCount,
            totals[SalesOrderReceiptEvidenceSemantics.BucketInvalid].Amount,
            totals[SalesOrderReceiptEvidenceSemantics.BucketUnavailable].RowCount,
            totals[SalesOrderReceiptEvidenceSemantics.BucketUnavailable].Amount);

        var fallbackCurrency = CurrencyAmountRules.NormalizeCurrency(
            orderAvailable ? order!.Currency.ToString() : lines.Find(l => l.Currency.Length > 0)?.Currency);

        var summary = BuildSummary(salesOrderId, order, totals, rows.Count, truncated, fallbackCurrency,
            aggregate, string.Empty);
        return new OrderEvidence(summary, lines, aggregate);
    }

    /// <summary>
    /// 收款单中未指向本订单的金额（只按持久化有效行聚合派生，下限 0）：仅作展示上下文，
    /// 不是银行未到账金额、不是应收余额；收款单缺失 / 已删除或命中上限时为 null（未知）。
    /// </summary>
    private static decimal? ReceiptUnreferencedOf(FinanceReceipt? receipt,
        IReadOnlyDictionary<long, ReceiptActiveTotalRow> activeTotalByReceipt, string currency)
    {
        if (receipt is null || receipt.IsDeleted) return null;
        if (!activeTotalByReceipt.TryGetValue(receipt.Id, out var activeTotal)) return null;

        var receiptAmount = CustomerReceiptAllocationRules.AuthoritativeReceiptAmount(
            receipt.Amount, currency);
        var unreferenced = receiptAmount - activeTotal.Amount;
        return unreferenced < 0 ? 0m : unreferenced;
    }

    /// <summary>
    /// 单条持久化引用行的证据分类（ERP-054 的核心口径）：只有「引用行有效、收款单可用、
    /// 引用行快照自相一致（客户 / 币种 / 订单币种）且订单可用、客户与币种都一致」才计入有效合计；
    /// 其余一律落到已作废 / 无效 / 无法确认分桶，并给出可读原因（绝不换算、合并或改派）。
    /// <para>订单资格判定复用 ERP-053 的权威规则 <see cref="CustomerReceiptAllocationRules.EvaluateOrderEligibility"/>，
    /// 不引入第二套匹配算法。</para>
    /// </summary>
    private static (string Bucket, string Reason) ClassifyRow(
        CustomerReceiptAllocation row, FinanceReceipt? receipt, SalesOrder? order)
    {
        if (row.Status == CustomerReceiptAllocationRules.StatusVoided)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketVoided,
                $"引用行已作废（原因：{row.VoidReason}）：历史证据仅供核对，金额不计入有效合计（作废保留可读）");
        }

        if (row.Status != CustomerReceiptAllocationRules.StatusActive)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketInvalid,
                $"引用行状态码 {row.Status} 无法识别：不能确认证据有效性，金额不计入有效合计");
        }

        if (row.AllocatedAmount <= 0)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketInvalid,
                $"引用金额 {row.AllocatedAmount} 不是正数：不作为有效证据，不计入有效合计");
        }

        if (receipt is null || receipt.IsDeleted)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketUnavailable,
                "引用行指向的收款单不存在或已删除：证据有效性无法确认，金额不计入有效合计"
                + "（不按 0 处理，也不猜测归属到其他订单）");
        }

        if (row.CustomerId != receipt.CustomerId)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketInvalid,
                $"引用行客户快照（Id={row.CustomerId}）与收款单客户（Id={receipt.CustomerId}）不一致："
                + "按无效历史证据单列，不合并、也不改派到其他订单");
        }

        var rowCurrency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var receiptCurrency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
        if (!string.Equals(rowCurrency, receiptCurrency, StringComparison.Ordinal))
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketInvalid,
                $"引用行币种 {rowCurrency} 与收款单币种 {receiptCurrency} 不一致："
                + "按无效历史证据单列（不做汇率换算、不合并为同一金额）");
        }

        var orderCurrencySnapshot = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency);
        if (!string.Equals(orderCurrencySnapshot, rowCurrency, StringComparison.Ordinal))
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketInvalid,
                $"引用行订单币种快照 {orderCurrencySnapshot} 与引用行币种 {rowCurrency} 不一致："
                + "快照自相矛盾，按无效历史证据单列（不做汇率换算）");
        }

        if (order is null || order.IsDeleted)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketUnavailable,
                "销售订单不存在或已删除：无法按权威口径核对引用，金额不计入有效合计（不按 0 处理）");
        }

        var (eligible, text) = CustomerReceiptAllocationRules.EvaluateOrderEligibility(
            receipt.CustomerId, receiptCurrency, order);
        if (eligible)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketRecorded,
                text + "：有效引用证据，计入有效合计（不代表已收款 / 未收款 / 已结清）");
        }

        if (order.Status == DocumentStatus.Cancelled)
        {
            return (SalesOrderReceiptEvidenceSemantics.BucketRecorded,
                text + "：该已登记引用证据金额仅作历史参考（照实计入有效合计），不代表已收款或应收余额");
        }

        return (SalesOrderReceiptEvidenceSemantics.BucketInvalid,
            text + "：该已登记引用证据按无效历史证据单列，不计入有效合计，也不改派到其他订单");
    }

    /// <summary>
    /// 引用行 → 证据明细行（纯映射；金额 / 单号 / 日期 / 状态一律按**持久化行快照**原样呈现，
    /// 收款单当前可用性只作只读标注，绝不回写快照、也不做汇率换算）。
    /// </summary>
    private static SalesOrderReceiptEvidenceLine MapLine(CustomerReceiptAllocation row,
        FinanceReceipt? receipt, string bucket, string reason, decimal? receiptUnreferenced, bool orderAvailable)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency);
        var receiptAvailable = receipt is not null && !receipt.IsDeleted;

        return new SalesOrderReceiptEvidenceLine
        {
            AllocationId = row.Id,
            ReceiptId = row.ReceiptId,
            ReceiptNo = row.ReceiptNo ?? string.Empty,
            ReceiptDate = row.ReceiptDate,
            ReceiptStatus = receiptAvailable ? (int)receipt!.Status : null,
            ReceiptStatusText = receiptAvailable
                ? CustomerReceiptAllocationRules.ReceiptStatusText((int)receipt!.Status)
                : "无法确认（收款单不存在或已删除，状态按快照保留）",
            ReceiptAmount = row.ReceiptAmount,
            AllocatedAmount = row.AllocatedAmount,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            CustomerId = row.CustomerId,
            CustomerCode = row.CustomerCode ?? string.Empty,
            CustomerName = row.CustomerName ?? string.Empty,
            OrderNo = row.OrderNo ?? string.Empty,
            OrderDate = row.OrderDate,
            OrderStatusText = CustomerReceiptAllocationRules.OrderStatusText(row.OrderStatus),
            OrderCurrency = orderCurrency,
            ReceiptAvailable = receiptAvailable,
            ReceiptAvailabilityText = CustomerReceiptAllocationRules.ReceiptAvailabilityText(receipt),
            OrderAvailabilityText = orderAvailable
                ? "销售订单可用"
                : "销售订单已删除或不存在：历史引用保留可读（订单可用性以引用行快照为准）",
            Status = row.Status,
            StatusText = StatusText(row.Status),
            IsRecordedEvidence = bucket == SalesOrderReceiptEvidenceSemantics.BucketRecorded,
            IsVoided = row.Status == CustomerReceiptAllocationRules.StatusVoided,
            Bucket = bucket,
            BucketText = SalesOrderReceiptEvidenceSemantics.BucketText(bucket),
            ReceiptUnreferencedAmount = receiptUnreferenced,
            AllocatedAt = row.AllocatedAt,
            VoidedAt = row.VoidedAt,
            VoidReason = row.VoidReason ?? string.Empty,
            Remark = row.Remark ?? string.Empty,
            Reason = reason,
        };
    }

    /// <summary>引用行状态文案（未知状态码照实说明，绝不抛异常、也绝不按有效兜底）</summary>
    private static string StatusText(int status) => status switch
    {
        CustomerReceiptAllocationRules.StatusActive => "有效",
        CustomerReceiptAllocationRules.StatusVoided => "已作废",
        _ => $"无法识别（状态码 {status}）",
    };

    /// <summary>
    /// 汇总派生：订单可用性文案与引用行分桶；有效已引用金额只取有效（未作废）引用行，
    /// 返回时把已作废 / 无效 / 无法确认分别单列；命中上限时金额与计数一律为 null（未知）。
    /// </summary>
    private static SalesOrderReceiptEvidenceSummary BuildSummary(long salesOrderId, SalesOrder? order,
        Dictionary<string, BucketTotals> totals, int rowCount, bool truncated, string fallbackCurrency,
        SalesOrderReceiptEvidenceAggregate aggregate, string orderNoFallback)
    {
        var available = order is not null && !order.IsDeleted;
        var cancelled = available && order!.Status == DocumentStatus.Cancelled;

        var orderState = !available
            ? SalesOrderReceiptEvidenceSemantics.OrderStateUnavailable
            : cancelled
                ? SalesOrderReceiptEvidenceSemantics.OrderStateCancelled
                : SalesOrderReceiptEvidenceSemantics.OrderStateAvailable;

        var orderStateText = !available
            ? "销售订单不存在或已删除：订单金额未知（不按 0 处理）"
            : cancelled
                ? "销售订单已取消：订单金额仅作历史参考，收款引用证据不参与收款确认、逾期或欠款判定"
                : $"销售订单可用（当前状态 {order!.Status}）";

        var currency = CurrencyAmountRules.NormalizeCurrency(
            available ? order!.Currency.ToString() : fallbackCurrency);

        var recorded = totals[SalesOrderReceiptEvidenceSemantics.BucketRecorded].Amount;
        var hasAnyRow = rowCount > 0;
        var hasHistorical = SalesOrderReceiptEvidenceSemantics.SupportedBuckets.Any(b =>
            SalesOrderReceiptEvidenceSemantics.IsHistoricalBucket(b) && totals[b].RowCount > 0);
        var recordedReceiptAmount = truncated ? (decimal?)null : aggregate.RecordedReceiptAmount;
        var unreferencedReceipt = truncated ? (decimal?)null : aggregate.UnreferencedReceiptAmount;

        var unreferencedOrder = truncated || !available || cancelled
            ? (decimal?)null
            : Math.Max(0m, order!.TotalAmount - recorded);

        decimal? Money(string bucket) => truncated ? null : totals[bucket].Amount;
        int? Rows(string bucket) => truncated ? null : totals[bucket].RowCount;

        return new SalesOrderReceiptEvidenceSummary
        {
            SalesOrderId = salesOrderId,
            OrderNo = order?.OrderNo ?? orderNoFallback,
            OrderAvailable = available,
            OrderState = orderState,
            OrderStateText = orderStateText,
            OrderStatusText = available ? order!.Status.ToString() : "不存在或已删除",
            OrderCurrency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            OrderedAmount = available ? order!.TotalAmount : null,
            HasReceiptAllocationEvidence = hasAnyRow,
            HasHistoricalEvidence = hasHistorical,
            Truncated = truncated,
            RecordedAllocatedAmount = Money(SalesOrderReceiptEvidenceSemantics.BucketRecorded),
            RecordedReceiptCount = truncated ? null : aggregate.RecordedReceiptCount,
            AllocationCount = truncated ? null : rowCount,
            RecordedReceiptAmount = recordedReceiptAmount,
            UnreferencedReceiptAmount = unreferencedReceipt,
            UnreferencedOrderAmount = unreferencedOrder,
            VoidedAllocatedAmount = Money(SalesOrderReceiptEvidenceSemantics.BucketVoided),
            VoidedAllocationCount = Rows(SalesOrderReceiptEvidenceSemantics.BucketVoided),
            InvalidAllocatedAmount = Money(SalesOrderReceiptEvidenceSemantics.BucketInvalid),
            InvalidAllocationCount = Rows(SalesOrderReceiptEvidenceSemantics.BucketInvalid),
            UnavailableAllocatedAmount = Money(SalesOrderReceiptEvidenceSemantics.BucketUnavailable),
            UnavailableAllocationCount = Rows(SalesOrderReceiptEvidenceSemantics.BucketUnavailable),
            AllocationStatus = SalesOrderReceiptEvidenceSemantics.AllocationStatus(
                truncated, truncated ? null : recorded, hasAnyRow),
            EvidenceLabel = SalesOrderReceiptEvidenceSemantics.EvidenceLabel(
                truncated, truncated ? null : recorded, hasAnyRow),
            EvidenceNote = BuildEvidenceNote(available, cancelled, truncated, hasAnyRow, totals, recorded,
                recordedReceiptAmount, unreferencedReceipt, unreferencedOrder, currency),
        };
    }

    /// <summary>
    /// 行级说明：已知什么 / 缺什么 / 为什么不能当银行入账、应收余额或结算依据；
    /// 历史 / 无效 / 无法确认证据逐桶点名，避免被读成已收款。
    /// </summary>
    private static string BuildEvidenceNote(bool available, bool cancelled, bool truncated, bool hasAnyRow,
        Dictionary<string, BucketTotals> totals, decimal recorded, decimal? recordedReceiptAmount,
        decimal? unreferencedReceipt, decimal? unreferencedOrder, string currency)
    {
        var sb = new StringBuilder();

        if (truncated)
            sb.Append(SalesOrderReceiptEvidenceSemantics.TruncatedNote);
        else if (!available || cancelled)
            sb.Append(SalesOrderReceiptEvidenceSemantics.UnknownOrderNote);
        else if (recorded > 0)
            sb.Append($"有效收款引用证据 {recorded} {currency} 只按有效（未作废）的持久化引用行派生；"
                + $"参与证据的收款单金额快照合计 {recordedReceiptAmount} {currency}，"
                + $"其中未指向本订单 {unreferencedReceipt} {currency}"
                + "（可能指向其他销售订单，不是银行未到账金额，也不是应收余额）；"
                + $"订单金额中未被任何有效收款引用证据指向的部分 {unreferencedOrder} {currency}"
                + "（只是没有有效引用证据指向，不是应收余额，也不是未收款金额）。");
        else if (hasAnyRow)
            sb.Append("本销售订单没有有效收款引用证据：现有引用行都是已作废 / 无效或无法确认，均不计入有效合计；"
                + "这是收款引用证据缺口，不代表未收款、已收款、已结清或逾期。");
        else
            sb.Append(SalesOrderReceiptEvidenceSemantics.NoEvidenceNote);

        if (!truncated)
        {
            AppendBucket(sb, totals, SalesOrderReceiptEvidenceSemantics.BucketVoided, "已作废历史证据");
            AppendBucket(sb, totals, SalesOrderReceiptEvidenceSemantics.BucketInvalid,
                "无效历史证据（客户 / 币种或快照不一致）");
            AppendBucket(sb, totals, SalesOrderReceiptEvidenceSemantics.BucketUnavailable,
                "无法确认的证据（收款单或订单已删除 / 不存在）");
        }

        sb.Append(" 以上金额一律为收款引用证据口径：不是银行入账金额、不是应收余额、不是货款核销或结算结果，"
            + "也不得据此判定已收款 / 未收款 / 逾期或据以催收。");
        return sb.ToString();
    }

    /// <summary>按引用行条数点名的分桶说明（历史 / 无效 / 无法确认：金额与收款单张数都只作参考）</summary>
    private static void AppendBucket(StringBuilder sb, Dictionary<string, BucketTotals> totals,
        string bucket, string label)
    {
        var item = totals[bucket];
        if (item.RowCount == 0) return;
        sb.Append($" 另有{label} {item.RowCount} 条引用行（金额 {item.Amount}，涉及 {item.ReceiptIds.Count} 张收款单）："
            + "不计入有效合计，也不换算 / 合并 / 改派到其他订单，可在明细中单独查看原因。");
    }
}
