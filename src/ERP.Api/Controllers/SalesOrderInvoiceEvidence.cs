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
/// 销售订单销项发票证据口径常量（ERP-056）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免把「销项发票证据」写成「已开票 / 已收讫 / 已结清 / 应收余额 / 逾期 / 应交税金」，
/// 也避免把草稿 / 已作废 / 无效 / 无法确认的分摊行混进有效合计。
/// <para>定位：<b>销售订单维度的销项发票证据只读视图</b>（证据来自 ERP-055 的持久化发票证据行
/// <c>CustomerSalesInvoiceEvidences</c> 与分摊行 <c>CustomerSalesInvoiceAllocations</c>）——
/// 它<strong>不是</strong>发票开具系统、<strong>不是</strong>税务申报或销项税金计算、
/// <strong>不是</strong>应收账款台账或余额、<strong>不是</strong>收款核销或结算结果，也<strong>不是</strong>账龄表。</para>
/// <para>与 ERP-046 / ERP-054 的关系：订单与收款核对报表复用本文件的分桶与聚合
/// （<see cref="SalesOrderInvoiceEvidence.AggregatesForOrdersAsync"/>），把「已订 / 已出数量（ERP-032）」、
/// 「收款申请链接（ERP-028 / ERP-032）」「收款引用登记证据（ERP-053 / ERP-054）」与
/// 「销项发票登记证据（ERP-055）」作为**四类独立标注的证据**并列展示，
/// 不建立第二套匹配或对账算法，也绝不把它们相加成一个应收账款余额。</para>
/// </summary>
public static class SalesOrderInvoiceEvidenceSemantics
{
    // ==================== 1. 有界上限 ====================

    /// <summary>列表批量查询最多接受的销售订单张数（列表按页取 Id；超出直接拒绝，不静默截断）</summary>
    public const int MaxBatchOrders = 200;

    /// <summary>
    /// 列表批量查询最多读取的持久化分摊行数（有界）：命中上限时本次响应内全部金额与计数按「未知」返回，
    /// 绝不报出「只统计了一部分证据」的合计。
    /// </summary>
    public const int MaxBatchEvidenceRows = 5000;

    /// <summary>单张销售订单详情最多读取的持久化分摊行数（有界）：命中上限时金额与计数按「未知」返回，明细只显示已读取部分</summary>
    public const int MaxOrderEvidenceRows = 500;

    /// <summary>
    /// 报表侧（ERP-046 订单 / 收款核对报表）一次最多聚合的销售订单张数（有界）：
    /// 超出时该批订单的销项发票证据一律按「未知」（null）处理，绝不给部分合计。
    /// </summary>
    public const int MaxAggregateOrders = 500;

    // ==================== 2. 证据分桶 ====================

    /// <summary>有效证据（发票「已登记」、分摊行未删除、快照自相一致、订单可用且客户 / 币种一致）：唯一计入有效合计的分桶</summary>
    public const string BucketRecorded = "recorded";

    /// <summary>草稿证据（发票仍为草稿：分摊只是工作数据，尚未形成登记证据，绝不计入有效合计）</summary>
    public const string BucketDraft = "draft";

    /// <summary>已作废历史证据：单独分桶，绝不计入有效合计（保留可读，含作废原因）</summary>
    public const string BucketVoided = "voided";

    /// <summary>无效历史证据（客户 / 币种不一致、订单币种快照自相矛盾、金额等式不成立或状态码无法识别）：不换算、不合并、不改派，绝不计入有效合计</summary>
    public const string BucketInvalid = "invalid";

    /// <summary>无法确认的证据（分摊行指向的发票证据或销售订单不存在 / 已删除）：金额不能确认，绝不并入有效合计</summary>
    public const string BucketUnavailable = "unavailable";

    /// <summary>支持的分桶取值（超出范围不静默兜底）</summary>
    public static readonly string[] SupportedBuckets =
        { BucketRecorded, BucketDraft, BucketVoided, BucketInvalid, BucketUnavailable };

    /// <summary>分桶文案（接口、界面与文档同源）</summary>
    public static string BucketText(string bucket) => bucket switch
    {
        BucketRecorded => "有效销项发票证据（计入有效合计）",
        BucketDraft => "草稿发票证据（发票未登记：不计入有效合计，仅作工作数据）",
        BucketVoided => "已作废历史证据（不计入有效合计）",
        BucketInvalid => "无效历史证据（客户 / 币种 / 金额等式或快照不一致：不换算、不合并、不改派）",
        _ => "无法确认的证据（发票证据或销售订单不存在 / 已删除：金额无法确认）",
    };

    /// <summary>是否属于「非有效证据」（= 不计入有效合计的分桶：草稿 / 已作废 / 无效 / 无法确认）</summary>
    public static bool IsNonActiveBucket(string bucket)
        => bucket is BucketDraft or BucketVoided or BucketInvalid or BucketUnavailable;

    // ==================== 3. 证据短标签与状态 ====================

    /// <summary>有有效销项发票证据</summary>
    public const string LabelRecorded = "有销项发票证据";

    /// <summary>只有非有效销项发票证据（草稿 / 已作废 / 无效 / 无法确认；绝不呈现为已开票 / 已收讫 / 已结清）</summary>
    public const string LabelNonActiveOnly = "仅有草稿 / 作废 / 无效销项发票证据";

    /// <summary>没有任何销项发票证据（证据缺口，绝不呈现为未开票 / 未收款 / 欠税 / 逾期）</summary>
    public const string LabelNone = "无销项发票证据";

    /// <summary>命中有界上限，证据无法穷尽</summary>
    public const string LabelUnknown = "未知（超出有界读取上限）";

    /// <summary>状态键：有有效销项发票证据</summary>
    public const string EvidenceRecorded = "recorded";

    /// <summary>状态键：只有草稿 / 已作废 / 无效 / 无法确认销项发票证据</summary>
    public const string EvidenceNonActiveOnly = "historical_only";

    /// <summary>状态键：没有任何销项发票证据（证据缺口）</summary>
    public const string EvidenceNone = "none";

    /// <summary>状态键：命中有界上限，无法穷尽</summary>
    public const string EvidenceUnknown = "unknown";

    /// <summary>
    /// 证据短标签（界面与接口同源）：命中上限 → 未知；有有效证据 → 有销项发票证据；
    /// 只有非有效证据 → 仅有草稿 / 作废 / 无效销项发票证据；一条都没有 → 无销项发票证据。
    /// </summary>
    public static string EvidenceLabel(bool truncated, decimal? recordedAmount, bool hasAnyRow)
    {
        if (truncated) return LabelUnknown;
        if (recordedAmount is > 0) return LabelRecorded;
        return hasAnyRow ? LabelNonActiveOnly : LabelNone;
    }

    /// <summary>证据状态键（前端按状态着色；与 <see cref="EvidenceLabel"/> 同源判定）</summary>
    public static string EvidenceStatusOf(bool truncated, decimal? recordedAmount, bool hasAnyRow)
    {
        if (truncated) return EvidenceUnknown;
        if (recordedAmount is > 0) return EvidenceRecorded;
        return hasAnyRow ? EvidenceNonActiveOnly : EvidenceNone;
    }

    /// <summary>发票证据分桶对应的行状态文案（未知状态码照实说明，绝不抛异常、也绝不按有效兜底）</summary>
    public static string InvoiceStatusText(int status) => status switch
    {
        CustomerSalesInvoiceEvidenceRules.StatusDraft => "草稿",
        CustomerSalesInvoiceEvidenceRules.StatusRecorded => "已登记",
        CustomerSalesInvoiceEvidenceRules.StatusVoided => "已作废",
        _ => $"无法识别（状态码 {status}）",
    };

    /// <summary>分摊行指向的发票证据状态码是否受支持（未知状态码按无效证据单列，绝不按有效兜底）</summary>
    public static bool IsSupportedInvoiceStatus(int status)
        => status is CustomerSalesInvoiceEvidenceRules.StatusDraft
            or CustomerSalesInvoiceEvidenceRules.StatusRecorded
            or CustomerSalesInvoiceEvidenceRules.StatusVoided;

    // ==================== 4. 订单可用性 ====================

    /// <summary>销售订单可用（存在且未删除）</summary>
    public const string OrderStateAvailable = "available";

    /// <summary>销售订单已取消（历史订单：证据只作历史参考）</summary>
    public const string OrderStateCancelled = "cancelled";

    /// <summary>销售订单不存在或已删除（订单金额未知，不按 0 处理）</summary>
    public const string OrderStateUnavailable = "unavailable";

    // ==================== 5. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（证据来源、分桶、未分摊部分与未知处理）</summary>
    public const string RuleText =
        "本视图是销售订单的**销项发票证据口径**（只读派生）：全部金额只按 ERP-055 的持久化发票证据行"
        + "（CustomerSalesInvoiceEvidences）与其分摊行（CustomerSalesInvoiceAllocations）派生，且只统计**已登记（未作废）**"
        + "发票下的**未删除**分摊行；草稿发票证据、已作废历史证据、无效历史证据（客户 / 币种 / 订单币种快照 / 金额等式不一致）"
        + "与无法确认证据（发票证据 / 订单不存在或已删除）一律单独分桶列出，绝不并入有效合计，也不换算、不合并、不改派到其他订单；"
        + "「参与证据的发票含税总额快照合计 − 本订单有效已分摊金额」只表示这些发票含税总额中**未指向本订单**的部分"
        + "（可能指向其他销售订单），它不是未开票金额、不是应收余额、也不是欠款；"
        + "「订单金额 − 有效销项发票证据合计」只表示订单金额中没有任何有效销项发票证据分摊到的部分，同样不是应收余额；"
        + "没有任何分摊行时只显示「无销项发票证据」（证据缺口），绝不推断为未开票 / 未收款 / 已收款 / 已结清 / 逾期或欠税。";

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public const string ScopeText =
        "有界读取：列表批量汇总一次最多 200 张销售订单、最多 5000 条持久化分摊行；单张订单详情最多 500 条分摊行；"
        + "命中上限时金额与计数一律按「未知」（null）返回，绝不报出部分合计，明细只显示已读取部分；"
        + "读取固定次数数据集访问（订单 + 分摊行 + 发票证据 + 发票侧分摊合计），与订单张数 / 行数无关，绝无逐行查库。";

    /// <summary>与发票开具 / 税务申报 / 应收台账 / 核销 / 对账单 / 账龄 / 结算的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本视图只是客户销项发票证据 → 销售订单 的**分摊证据**只读呈现：不是发票开具系统（不连税务局、不调用任何开票服务）、"
        + "不是税务申报与销项税金计算、不是应收账款台账或应收余额、不是收款核销或结算结果、也不是客户对账单与账龄表 —— "
        + "它不判断是否已开票、是否已收款、是否已结清或是否逾期，不推算账期与到期日，也不产生任何记账、凭证、开票或收付款记录；"
        + "界面与接口一律把该金额标注为**销项发票证据**，不得当作已开票金额、应交税金、欠款金额或可催收金额。";

    /// <summary>没有任何销项发票证据时的提示（绝不能被读成未开票 / 未收款 / 欠税 / 逾期）</summary>
    public const string NoEvidenceNote =
        "本销售订单没有任何已登记的销项发票分摊行：这是**销项发票证据缺口**，不代表未开票、未收款、已收款、已结清、"
        + "逾期或欠缴税金，也不构成应收余额、税务申报或结算依据。";

    /// <summary>命中读取上限时的提示（金额未知，不给部分合计）</summary>
    public const string TruncatedNote =
        "本次读取命中系统有界上限（持久化分摊行数量超出上限），无法穷尽该订单的销项发票证据："
        + "金额与计数一律按「未知」显示，不作部分合计。";

    /// <summary>无可信订单金额时的提示（订单已取消或不可用）</summary>
    public const string UnknownOrderNote =
        "销售订单已取消或已删除：订单金额按未知显示（不按 0 处理），也不推断开票、收款、结算、税金或逾期状态。";

    /// <summary>只有草稿证据时的提示（草稿只是工作数据，尚未形成登记证据）</summary>
    public const string DraftOnlyNote =
        "本销售订单现有销项发票分摊行都属于**草稿**发票：草稿只是工作数据，尚未形成登记证据，因此不计入有效合计"
        + "（登记后才会成为有效证据），也不代表未开票、未收款或欠税。";
}
/// <summary>
/// 销售订单 Id 列表查询参数（ERP-056，只读）：逗号分隔的正整数，去重、保序、有界（超出上限直接拒绝）。
/// <para>与 ERP-048 / ERP-050 / ERP-054 的列表批量查询参数同口径，便于列表页复用同一套分批策略。</para>
/// </summary>
public sealed class SalesOrderInvoiceEvidenceQuery
{
    /// <summary>逗号分隔的销售订单 Id（如 <c>12,34,56</c>；留空 = 不查询任何订单）</summary>
    public string? Ids { get; set; }

    /// <summary>归一化后的订单 Id（去重、保持提交顺序）</summary>
    public IReadOnlyList<long> OrderIds => _orderIds;

    private readonly List<long> _orderIds = new();

    /// <summary>
    /// 归一化并校验：只接受正整数 Id，去重（保留首次出现顺序）；非法片段或超出
    /// <see cref="SalesOrderInvoiceEvidenceSemantics.MaxBatchOrders"/> 一律拒绝，不静默丢弃、不静默截断。
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
                    $"订单 Id「{token}」无效：销项发票证据批量接口只接受逗号分隔的正整数销售订单 Id");

            if (_orderIds.Contains(id)) continue;
            _orderIds.Add(id);

            if (_orderIds.Count > SalesOrderInvoiceEvidenceSemantics.MaxBatchOrders)
                throw BusinessException.InvalidParameter(
                    $"一次最多查询 {SalesOrderInvoiceEvidenceSemantics.MaxBatchOrders} 张销售订单的销项发票证据"
                    + "（列表页请按页分批请求，不要一次拉取全量）");
        }
    }
}

/// <summary>
/// 销售订单销项发票证据汇总（ERP-056，只读派生；列表列与详情头部共用）。
/// <para>金额口径：<see cref="RecordedInvoicedAmount"/> 只统计**已登记（未作废）**发票下的未删除分摊行；
/// 草稿 / 已作废 / 无效 / 无法确认分别单列，绝不并入有效合计，也绝不折算、合并或改派到其他订单。</para>
/// <para>未知处理：命中读取上限或订单已取消 / 不可用时，金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 本汇总<strong>不是</strong>开票状态、应交税金、应收余额、收款核销或结算状态。</para>
/// </summary>
public sealed class SalesOrderInvoiceEvidenceSummary
{
    /// <summary>销售订单 Id</summary>
    public long SalesOrderId { get; init; }

    /// <summary>销售订单号（订单已删除时按分摊行快照显示）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单当前是否可用（存在且未删除）</summary>
    public bool OrderAvailable { get; init; }

    /// <summary>订单可用性状态：available / cancelled / unavailable</summary>
    public string OrderState { get; init; } = SalesOrderInvoiceEvidenceSemantics.OrderStateUnavailable;

    /// <summary>订单可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderStateText { get; init; } = string.Empty;

    /// <summary>订单当前单据状态文案（订单不存在 / 已删除时照实说明）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>订单币种（原币；不同币种绝不合并、不做汇率换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用；复用 ERP-043 / ERP-055 的币种精度口径）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>订单已落库总额；null = 未知（订单不存在 / 已删除），不等于 0</summary>
    public decimal? OrderedAmount { get; init; }

    /// <summary>是否读到至少一条持久化分摊行（不代表有效证据，也不代表已开票）</summary>
    public bool HasInvoiceEvidence { get; init; }

    /// <summary>是否存在草稿 / 已作废 / 无效 / 无法确认的非有效证据（必须单独查看，绝不并入有效合计）</summary>
    public bool HasNonActiveEvidence { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }
    /// <summary>有效已分摊金额（已登记发票下的未删除分摊行合计，原币）；null = 未知（命中上限）</summary>
    public decimal? RecordedInvoicedAmount { get; init; }

    /// <summary>参与有效合计的发票张数（按发票证据去重）；null = 未知（命中上限）</summary>
    public int? RecordedInvoiceCount { get; init; }

    /// <summary>本次读取到的持久化分摊行总条数（含草稿 / 已作废 / 无效 / 无法确认证据）；null = 未知（命中上限）</summary>
    public int? InvoiceAllocationCount { get; init; }

    /// <summary>
    /// 参与有效证据的发票含税总额快照合计（按发票去重后求和，仅作上下文）；null = 未知（命中上限）。
    /// 它<strong>不是</strong>应收余额，也<strong>不是</strong>已开票金额或应缴税金。
    /// </summary>
    public decimal? RecordedInvoiceGrossAmount { get; init; }

    /// <summary>
    /// 未指向本订单的发票金额（= 参与证据的发票含税总额快照合计 − 本订单有效已分摊金额，下限 0）：
    /// 只表示这些发票含税总额中<strong>未指向本订单</strong>的部分（可能指向其他销售订单），
    /// 不是未开票金额、不是应收余额、也不是欠款；null = 未知（命中上限）。
    /// </summary>
    public decimal? UnreferencedInvoiceAmount { get; init; }

    /// <summary>
    /// 订单金额中未被任何有效销项发票证据分摊到的部分（= 订单金额 − 有效已分摊金额，下限 0）；
    /// 只是「没有有效发票证据分摊到」的金额，不是应收余额、不是未开票金额、也不是账龄依据；null = 未知。
    /// </summary>
    public decimal? InvoiceUnreferencedOrderAmount { get; init; }

    /// <summary>草稿发票证据金额（发票未登记：仅工作数据，绝不计入有效合计）；null = 未知（命中上限）</summary>
    public decimal? DraftInvoiceAllocatedAmount { get; init; }

    /// <summary>草稿发票证据分摊行条数；null = 未知（命中上限）</summary>
    public int? DraftInvoiceAllocationCount { get; init; }

    /// <summary>已作废历史证据金额（原币；绝不计入有效合计，保留可读）；null = 未知（命中上限）</summary>
    public decimal? VoidedInvoiceAllocatedAmount { get; init; }

    /// <summary>已作废历史证据分摊行条数；null = 未知（命中上限）</summary>
    public int? VoidedInvoiceAllocationCount { get; init; }

    /// <summary>无效历史证据金额（客户 / 币种 / 金额等式或快照不一致；绝不换算、合并或改派）；null = 未知（命中上限）</summary>
    public decimal? InvalidInvoiceAllocatedAmount { get; init; }

    /// <summary>无效历史证据分摊行条数；null = 未知（命中上限）</summary>
    public int? InvalidInvoiceAllocationCount { get; init; }

    /// <summary>无法确认的证据金额（发票证据 / 订单不存在或已删除：无法确认有效性）；null = 未知（命中上限）</summary>
    public decimal? UnavailableInvoiceAllocatedAmount { get; init; }

    /// <summary>无法确认的证据分摊行条数；null = 未知（命中上限）</summary>
    public int? UnavailableInvoiceAllocationCount { get; init; }

    /// <summary>证据状态键（recorded / historical_only / none / unknown；前端按状态着色）</summary>
    public string InvoiceEvidenceStatus { get; init; } = SalesOrderInvoiceEvidenceSemantics.EvidenceUnknown;

    /// <summary>证据短标签（有销项发票证据 / 仅有草稿作废无效证据 / 无销项发票证据 / 未知）</summary>
    public string InvoiceEvidenceLabel { get; init; } = SalesOrderInvoiceEvidenceSemantics.LabelUnknown;

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不能作为已开票、应收余额或税金结论）</summary>
    public string InvoiceEvidenceNote { get; init; } = string.Empty;
}
/// <summary>
/// 一条销项发票证据（ERP-056，只读派生；明细行 = 一条 ERP-055 持久化分摊行）。
/// <para><see cref="Bucket"/> 表明该分摊行是有效证据还是草稿 / 已作废 / 无效 / 无法确认证据；
/// 只有 <c>recorded</c> 计入有效合计；其余一律单独列出并给出 <see cref="Reason"/>；
/// 系统绝不换算、合并或改派到其他订单。</para>
/// </summary>
public sealed class SalesOrderInvoiceEvidenceLine
{
    /// <summary>分摊行 Id（ERP-055 <c>CustomerSalesInvoiceAllocations.Id</c>）</summary>
    public long AllocationId { get; init; }

    /// <summary>发票证据 Id（分摊行指向的既有客户销项发票证据）</summary>
    public long InvoiceId { get; init; }

    /// <summary>发票类型（普票 / 专票 / 出口发票；缺失时照实说明）</summary>
    public string InvoiceType { get; init; } = string.Empty;

    /// <summary>发票代码快照（专票用；可为空）</summary>
    public string InvoiceCode { get; init; } = string.Empty;

    /// <summary>发票号码快照（发票上的号码，不是本系统生成的编号）</summary>
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>发票身份文案（类型 + 代码 + 号码；缺失时照实说明）</summary>
    public string InvoiceIdentityText { get; init; } = string.Empty;

    /// <summary>开票日期快照</summary>
    public DateTime InvoiceDate { get; init; }

    /// <summary>发票证据状态（0 草稿 / 1 已登记 / 2 已作废）；null = 发票证据缺失无法确认</summary>
    public int? InvoiceStatus { get; init; }

    /// <summary>发票证据状态文案（缺失或状态码未知时照实说明「无法识别 / 无法确认」）</summary>
    public string InvoiceStatusText { get; init; } = string.Empty;

    /// <summary>发票是否仍为草稿（草稿证据绝不计入有效合计）</summary>
    public bool IsDraft { get; init; }

    /// <summary>发票是否已登记（唯一可能计入有效合计的状态）</summary>
    public bool IsRecorded { get; init; }

    /// <summary>发票是否已作废（历史证据，保留可读）</summary>
    public bool IsVoided { get; init; }

    /// <summary>不含税金额快照（净额，原币；仅作展示，不参与有效合计）</summary>
    public decimal NetAmount { get; init; }

    /// <summary>税额快照（原币；仅作展示，<strong>不是</strong>应缴税金结论）</summary>
    public decimal TaxAmount { get; init; }

    /// <summary>含税总额快照（价税合计，原币；仅作展示，不参与有效合计）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本行分摊金额（原币；按持久化分摊行原样呈现，不重算、不四舍五入改写）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>币种（原币；分摊行币种，与发票证据币种一致才可能是有效证据）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>客户 Id 快照（分摊行写入时的服务端快照）</summary>
    public long CustomerId { get; init; }

    /// <summary>客户编码快照</summary>
    public string CustomerCode { get; init; } = string.Empty;

    /// <summary>客户名称快照</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>销售订单号快照（分摊行写入时口径）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单日期快照</summary>
    public DateTime OrderDate { get; init; }

    /// <summary>分摊行订单状态快照文案（取消后历史证据仍保留登记当时口径）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>分摊行的订单币种快照（与分摊行币种 / 发票币种不一致即无效证据，绝不换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>发票证据当前是否可用（存在且未删除；**只读标注**）</summary>
    public bool InvoiceAvailable { get; init; }

    /// <summary>发票证据可用性文案（已删除时照实说明，历史证据仍可读）</summary>
    public string InvoiceAvailabilityText { get; init; } = string.Empty;

    /// <summary>销售订单当前可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderAvailabilityText { get; init; } = string.Empty;

    /// <summary>证据分桶（recorded / draft / voided / invalid / unavailable）</summary>
    public string Bucket { get; init; } = SalesOrderInvoiceEvidenceSemantics.BucketUnavailable;

    /// <summary>分桶文案</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>是否有效证据（唯一计入有效合计的证据类型）</summary>
    public bool IsRecordedEvidence { get; init; }

    /// <summary>
    /// 该发票中未指向本订单的金额（= 发票含税总额快照 − 该发票全部未删除分摊行合计，下限 0）；
    /// 只是本登记册口径下该发票尚未指向本订单的部分，不是未开票金额、不是应收余额；
    /// null = 未知（发票证据缺失 / 已删除或命中上限）。
    /// </summary>
    public decimal? InvoiceUnreferencedAmount { get; init; }

    /// <summary>登记时间（分摊行创建时间）</summary>
    public DateTime AllocatedAt { get; init; }

    /// <summary>作废时间（发票未作废为 null）</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因（作废保留原始值与原因，绝不重写）</summary>
    public string VoidReason { get; init; } = string.Empty;

    /// <summary>备注（登记说明，不参与任何金额派生）</summary>
    public string Remark { get; init; } = string.Empty;

    /// <summary>计入 / 不计入有效合计的原因说明（无效时给出权威不一致原因）</summary>
    public string Reason { get; init; } = string.Empty;
}
/// <summary>
/// 销售订单销项发票证据详情（ERP-056，只读派生）：汇总 + 逐条证据明细 + 口径文案。
/// <para><see cref="Lines"/> 为有界明细（最多 <see cref="SalesOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows"/> 条）；
/// 命中上限时汇总金额与计数按未知显示，明细仅展示已读取部分。</para>
/// <para>草稿与已作废证据保留在 <see cref="Lines"/> 中（<c>draft</c> / <c>voided</c> 分桶，含原始金额 / 快照 / 作废原因），
/// 供显式历史核对，永不并入有效合计。</para>
/// </summary>
public sealed class SalesOrderInvoiceEvidenceDetail
{
    /// <summary>汇总（与该订单列表列同口径）</summary>
    public SalesOrderInvoiceEvidenceSummary Summary { get; init; } = new();

    /// <summary>逐条持久化分摊行证据（有界：最多 500 条；含草稿与已作废历史证据）</summary>
    public List<SalesOrderInvoiceEvidenceLine> Lines { get; init; } = new();

    /// <summary>本次返回的明细条数</summary>
    public int LineCount { get; init; }

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = SalesOrderInvoiceEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = SalesOrderInvoiceEvidenceSemantics.ScopeText;

    /// <summary>与发票开具 / 税务申报 / 应收台账 / 核销 / 对账单 / 账龄 / 结算的边界说明</summary>
    public string Boundary { get; init; } = SalesOrderInvoiceEvidenceSemantics.BoundaryText;

    /// <summary>分摊行登记口径（与 ERP-055 同源，不引入第二套匹配算法）</summary>
    public string LinkageRule { get; init; } = CustomerSalesInvoiceEvidenceRules.LinkageRuleText;

    /// <summary>ERP-055 登记册自身的模块边界（与 ERP-055 同源）</summary>
    public string InvoiceRegisterBoundary { get; init; } = CustomerSalesInvoiceEvidenceRules.BoundaryText;

    /// <summary>金额等式口径（与 ERP-055 同源，用于说明无效证据为何不计入）</summary>
    public string AmountEquationRule { get; init; } = CustomerSalesInvoiceEvidenceRules.AmountEquationText;
}

/// <summary>
/// 销售订单销项发票证据批量汇总（ERP-056，只读派生；列表页一次请求取回本页订单的汇总，不逐行查库）。
/// </summary>
public sealed class SalesOrderInvoiceEvidenceBatch
{
    /// <summary>本次请求的订单 Id 张数（去重后）</summary>
    public int RequestedCount { get; init; }

    /// <summary>返回的汇总条数（与请求的订单 Id 一一对应，含未找到 / 已删除的订单）</summary>
    public int ItemCount { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 全部金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>按请求顺序返回的订单汇总</summary>
    public List<SalesOrderInvoiceEvidenceSummary> Items { get; init; } = new();

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = SalesOrderInvoiceEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = SalesOrderInvoiceEvidenceSemantics.ScopeText;

    /// <summary>与发票开具 / 税务申报 / 应收台账 / 核销 / 对账单 / 账龄 / 结算的边界说明</summary>
    public string Boundary { get; init; } = SalesOrderInvoiceEvidenceSemantics.BoundaryText;

    /// <summary>分摊行登记口径（与 ERP-055 同源）</summary>
    public string LinkageRule { get; init; } = CustomerSalesInvoiceEvidenceRules.LinkageRuleText;
}

/// <summary>
/// 单张销售订单的销项发票证据聚合（ERP-046 报表复用入口，只读）。
/// <para>与订单视图**共用同一套分桶与资格判定**，避免出现第二套对账算法。</para>
/// </summary>
public sealed record SalesOrderInvoiceEvidenceAggregate(
    long SalesOrderId,
    decimal RecordedAmount,
    int RecordedRowCount,
    int RecordedInvoiceCount,
    decimal RecordedInvoiceGrossAmount,
    decimal UnreferencedInvoiceAmount,
    int DraftRowCount,
    decimal DraftAmount,
    int VoidedRowCount,
    decimal VoidedAmount,
    int InvalidRowCount,
    decimal InvalidAmount,
    int UnavailableRowCount,
    decimal UnavailableAmount)
{
    /// <summary>非有效证据（草稿 + 已作废 + 无效 + 无法确认）分摊行条数</summary>
    public int NonActiveRowCount => DraftRowCount + VoidedRowCount + InvalidRowCount + UnavailableRowCount;
}

/// <summary>
/// 一批销售订单的销项发票证据聚合结果（ERP-046 报表复用，只读有界）。
/// <para><see cref="Truncated"/> 为 true 时调用方必须把相关金额与计数按「未知」处理（不含部分合计）。</para>
/// </summary>
public sealed record SalesOrderInvoiceEvidenceAggregateSet(
    IReadOnlyDictionary<long, SalesOrderInvoiceEvidenceAggregate> ByOrder,
    bool Truncated)
{
    /// <summary>取某订单的聚合；订单无分摊行或未参与聚合时返回全 0（订单可用性由调用方自行判定）</summary>
    public SalesOrderInvoiceEvidenceAggregate Get(long salesOrderId)
        => ByOrder.TryGetValue(salesOrderId, out var aggregate) ? aggregate : Empty(salesOrderId);

    /// <summary>全 0 聚合（无分摊行；不代表未开票 / 已开票）</summary>
    public static SalesOrderInvoiceEvidenceAggregate Empty(long salesOrderId)
        => new(salesOrderId, 0m, 0, 0, 0m, 0m, 0, 0m, 0, 0m, 0, 0m, 0, 0m);
}
/// <summary>
/// 销售订单销项发票证据派生（ERP-056，只读）：在既有销售订单列表 / 详情工作流与 ERP-046 客户订单 / 收款
/// 核对报表中，暴露 **ERP-055 持久化发票证据行及其分摊行**的只读汇总，并复用 ERP-055 的权威资格判定与状态文案。
/// <para>边界（重要）：本派生<strong>只读</strong>（全部 <c>AsNoTracking</c>、从不 <c>SaveChanges</c>），
/// 不改写销售订单状态 / 出货进度 / 金额与明细、发票证据行与其分摊行、客户收款单与收款引用行、客户信用状态、
/// 库存与库存成本、装柜与单证、佣金 / 回佣、费用或退税记录，也<strong>不</strong>开票、不报税、不记账、不核销、不结算。</para>
/// <para>有界：最多 4 次数据集访问（销售订单 + 持久化分摊行 + 发票证据 + 发票侧分摊合计），
/// 与订单张数 / 行数无关，绝无逐行查库；行数超出上限时金额与计数按「未知」返回，不报部分合计。</para>
/// </summary>
public static class SalesOrderInvoiceEvidence
{
    /// <summary>
    /// 单张销售订单的销项发票证据详情（汇总 + 有界证据明细）。
    /// <para>订单不存在或已删除时抛「数据不存在」（与 <c>/api/sales-orders/{id}</c> 同口径）。</para>
    /// </summary>
    public static async Task<SalesOrderInvoiceEvidenceDetail> ForOrderAsync(IErpDbContext db, long salesOrderId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId <= 0)
            throw BusinessException.InvalidParameter("销售订单 Id 必须是正整数");

        var built = await BuildAsync(db, new List<long> { salesOrderId },
            SalesOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows);
        var evidence = built.ByOrder[salesOrderId];
        if (!evidence.Summary.OrderAvailable)
            throw BusinessException.NotFound("销售订单不存在或已删除");

        return new SalesOrderInvoiceEvidenceDetail
        {
            Summary = evidence.Summary,
            Lines = evidence.Lines,
            LineCount = evidence.Lines.Count,
        };
    }

    /// <summary>
    /// 一批销售订单的销项发票证据汇总（列表页有界批量入口）：按请求顺序返回，与请求的订单 Id 一一对应
    /// （未找到 / 已删除的订单返回「订单不可用 + 金额未知」汇总，绝不静默丢行）。
    /// </summary>
    public static async Task<SalesOrderInvoiceEvidenceBatch> ForOrdersAsync(IErpDbContext db,
        SalesOrderInvoiceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var ids = query.OrderIds;
        if (ids.Count == 0)
            return new SalesOrderInvoiceEvidenceBatch { RequestedCount = 0, ItemCount = 0 };

        var built = await BuildAsync(db, ids, SalesOrderInvoiceEvidenceSemantics.MaxBatchEvidenceRows);
        var items = ids.Select(id => built.ByOrder[id].Summary).ToList();

        return new SalesOrderInvoiceEvidenceBatch
        {
            RequestedCount = ids.Count,
            ItemCount = items.Count,
            Truncated = built.Truncated,
            Items = items,
        };
    }

    /// <summary>
    /// 一批销售订单的销项发票证据聚合（ERP-046 报表复用入口，只读有界）：
    /// 与销售订单视图**共用同一套分桶与资格判定**，避免出现第二套对账算法。
    /// <para>命中订单数上限或行数上限时 <see cref="SalesOrderInvoiceEvidenceAggregateSet.Truncated"/> 为 true，
    /// 调用方必须把相关金额与计数按「未知」处理（本次结果<strong>不含</strong>部分合计）。</para>
    /// </summary>
    public static async Task<SalesOrderInvoiceEvidenceAggregateSet> AggregatesForOrdersAsync(
        IErpDbContext db, IReadOnlyList<long> salesOrderIds,
        int orderCap = SalesOrderInvoiceEvidenceSemantics.MaxAggregateOrders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(salesOrderIds);

        var cap = orderCap <= 0 ? SalesOrderInvoiceEvidenceSemantics.MaxAggregateOrders : orderCap;
        var ids = salesOrderIds.Where(id => id > 0).Distinct().ToList();

        if (ids.Count == 0)
            return new SalesOrderInvoiceEvidenceAggregateSet(
                new Dictionary<long, SalesOrderInvoiceEvidenceAggregate>(), false);

        // 超出有界聚合上限：不给部分合计（调用方按未知显示）
        if (ids.Count > cap)
            return new SalesOrderInvoiceEvidenceAggregateSet(
                new Dictionary<long, SalesOrderInvoiceEvidenceAggregate>(), true);

        var built = await BuildAsync(db, ids, SalesOrderInvoiceEvidenceSemantics.MaxBatchEvidenceRows);
        var byOrder = built.ByOrder.ToDictionary(pair => pair.Key, pair => pair.Value.Aggregate);
        return new SalesOrderInvoiceEvidenceAggregateSet(byOrder, built.Truncated);
    }

    // ==================== 内部：批量读取与派生 ====================

    /// <summary>单张订单的证据派生结果（汇总 + 明细行 + 供报表复用的聚合）</summary>
    private sealed record OrderEvidence(SalesOrderInvoiceEvidenceSummary Summary,
        List<SalesOrderInvoiceEvidenceLine> Lines, SalesOrderInvoiceEvidenceAggregate Aggregate);

    /// <summary>一次批量派生的读取结果（按订单 Id 索引；<see cref="Truncated"/> = 命中行数上限）</summary>
    private sealed record BuildResult(Dictionary<long, OrderEvidence> ByOrder, bool Truncated);

    /// <summary>发票侧分摊合计（数据库侧分组聚合结果，只读）</summary>
    private sealed record InvoiceAllocationTotalRow(long InvoiceId, decimal Amount);

    /// <summary>单条分摊行的分桶累计（金额 / 行数 / 发票去重）</summary>
    private sealed class BucketTotals
    {
        /// <summary>分桶金额合计（原币，按持久化分摊行汇总）</summary>
        public decimal Amount { get; private set; }

        /// <summary>分桶内的持久化分摊行条数</summary>
        public int RowCount { get; private set; }

        /// <summary>分桶内的发票证据 Id（去重；用于「发票张数」计数）</summary>
        public HashSet<long> InvoiceIds { get; } = new();

        /// <summary>累计一条分摊行</summary>
        public void Add(decimal amount, long invoiceId)
        {
            Amount += amount;
            RowCount++;
            InvoiceIds.Add(invoiceId);
        }
    }

    /// <summary>全部为空的分桶字典（每个受支持分桶都有条目，避免键缺失时的隐式兜底）</summary>
    private static Dictionary<string, BucketTotals> EmptyTotals()
    {
        var totals = new Dictionary<string, BucketTotals>(StringComparer.Ordinal);
        foreach (var bucket in SalesOrderInvoiceEvidenceSemantics.SupportedBuckets)
            totals[bucket] = new BucketTotals();
        return totals;
    }

    /// <summary>
    /// 固定 4 次数据集访问：销售订单 + 持久化分摊行（有界，含 1 条溢出探测）+ 发票证据 +
    /// 发票侧未删除分摊合计（数据库侧分组聚合）；全程只读、不写库，金额按持久化行原样汇总（不重算、不改写）。
    /// </summary>
    private static async Task<BuildResult> BuildAsync(
        IErpDbContext db, IReadOnlyList<long> salesOrderIds, int rowCap)
    {
        var orders = await db.SalesOrders.AsNoTracking()
            .Where(o => salesOrderIds.Contains(o.Id))
            .ToListAsync();
        var orderById = orders.ToDictionary(o => o.Id);

        var rows = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && salesOrderIds.Contains(a.SalesOrderId))
            .OrderBy(a => a.SalesOrderId).ThenBy(a => a.CustomerSalesInvoiceEvidenceId).ThenBy(a => a.Id)
            .Take(rowCap + 1)
            .ToListAsync();
        var truncated = rows.Count > rowCap;
        if (truncated) rows.RemoveRange(rowCap, rows.Count - rowCap);

        var invoiceIds = rows.Select(r => r.CustomerSalesInvoiceEvidenceId).Distinct().ToList();
        var invoices = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .ToListAsync();
        var invoiceById = invoices.ToDictionary(i => i.Id);

        // 发票侧未删除分摊合计：数据库侧分组聚合，不装载明细行（有界，与行数无关）
        var allocationTotals = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId))
            .GroupBy(a => a.CustomerSalesInvoiceEvidenceId)
            .Select(g => new InvoiceAllocationTotalRow(g.Key, g.Sum(a => a.AllocatedAmount)))
            .ToListAsync();
        var allocationTotalByInvoice = allocationTotals.ToDictionary(t => t.InvoiceId);

        var rowsByOrder = rows
            .GroupBy(r => r.SalesOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<long, OrderEvidence>(salesOrderIds.Count);
        foreach (var salesOrderId in salesOrderIds)
        {
            orderById.TryGetValue(salesOrderId, out var order);
            var orderRows = rowsByOrder.TryGetValue(salesOrderId, out var list)
                ? list
                : new List<CustomerSalesInvoiceAllocation>();
            result[salesOrderId] = BuildOrderEvidence(salesOrderId, order, orderRows, invoiceById,
                allocationTotalByInvoice, truncated);
        }

        return new BuildResult(result, truncated);
    }

    /// <summary>
    /// 单张订单派生：逐条分摊行分桶 → 汇总 + 明细 + 报表复用聚合（草稿 / 已作废 / 无效 / 无法确认证据
    /// 必须单独列出，绝不并入有效合计）。
    /// </summary>
    private static OrderEvidence BuildOrderEvidence(long salesOrderId, SalesOrder? order,
        List<CustomerSalesInvoiceAllocation> rows,
        IReadOnlyDictionary<long, CustomerSalesInvoiceEvidence> invoiceById,
        IReadOnlyDictionary<long, InvoiceAllocationTotalRow> allocationTotalByInvoice, bool truncated)
    {
        var orderAvailable = order is not null && !order.IsDeleted;
        var totals = EmptyTotals();
        var lines = new List<SalesOrderInvoiceEvidenceLine>(rows.Count);
        var recordedInvoiceGrossAmounts = new Dictionary<long, decimal>();

        foreach (var row in rows)
        {
            invoiceById.TryGetValue(row.CustomerSalesInvoiceEvidenceId, out var invoice);
            var (bucket, reason) = ClassifyRow(row, invoice, order);
            var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
            totals[bucket].Add(row.AllocatedAmount, row.CustomerSalesInvoiceEvidenceId);

            decimal? invoiceUnreferenced = null;
            if (bucket == SalesOrderInvoiceEvidenceSemantics.BucketRecorded)
            {
                invoiceUnreferenced = InvoiceUnreferencedOf(invoice, allocationTotalByInvoice, currency);
                if (invoice is not null && !invoice.IsDeleted)
                {
                    recordedInvoiceGrossAmounts[row.CustomerSalesInvoiceEvidenceId] =
                        AuthoritativeInvoiceGrossAmount(invoice, currency);
                }
            }

            lines.Add(MapLine(row, invoice, bucket, reason, invoiceUnreferenced, orderAvailable));
        }

        var recorded = totals[SalesOrderInvoiceEvidenceSemantics.BucketRecorded].Amount;
        var recordedInvoiceGrossAmount = recordedInvoiceGrossAmounts.Values.Sum();
        var unreferencedInvoice = recordedInvoiceGrossAmount - recorded;
        if (unreferencedInvoice < 0) unreferencedInvoice = 0;

        var aggregate = new SalesOrderInvoiceEvidenceAggregate(
            salesOrderId,
            recorded,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketRecorded].RowCount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketRecorded].InvoiceIds.Count,
            recordedInvoiceGrossAmount,
            unreferencedInvoice,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketDraft].RowCount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketDraft].Amount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketVoided].RowCount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketVoided].Amount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketInvalid].RowCount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketInvalid].Amount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketUnavailable].RowCount,
            totals[SalesOrderInvoiceEvidenceSemantics.BucketUnavailable].Amount);

        var fallbackCurrency = CurrencyAmountRules.NormalizeCurrency(
            orderAvailable ? order!.Currency.ToString() : lines.Find(l => l.Currency.Length > 0)?.Currency);

        var summary = BuildSummary(salesOrderId, order, totals, rows.Count, truncated, fallbackCurrency,
            aggregate, string.Empty);
        return new OrderEvidence(summary, lines, aggregate);
    }

    /// <summary>
    /// 发票侧的权威含税总额（按币种精度取整后呈现；币种精度复用 ERP-043 / ERP-055 的唯一权威口径
    /// <see cref="CurrencyAmountRules"/>，不重算、不改写持久化值）。
    /// </summary>
    private static decimal AuthoritativeInvoiceGrossAmount(CustomerSalesInvoiceEvidence invoice, string currency)
        => CurrencyAmountRules.RoundAmount(invoice.GrossAmount, currency);

    /// <summary>
    /// 金额等式是否成立（复用 ERP-055 的权威口径：含税总额 = 净额 + 税额，且都按币种精度取整、含税总额大于 0、
    /// 税额不得为负）；不成立的历史证据按无效单列，绝不移项参与有效合计。
    /// </summary>
    private static bool AmountEquationHolds(CustomerSalesInvoiceEvidence invoice, string currency)
    {
        var net = CurrencyAmountRules.RoundAmount(invoice.NetAmount, currency);
        var tax = CurrencyAmountRules.RoundAmount(invoice.TaxAmount, currency);
        var gross = CurrencyAmountRules.RoundAmount(invoice.GrossAmount, currency);
        return gross > 0 && tax >= 0 && net + tax == gross;
    }

    /// <summary>
    /// 发票中未指向本订单的金额（只按持久化未删除分摊行聚合派生，下限 0）：仅作展示上下文，
    /// 不是未开票金额、不是应收余额；发票缺失 / 已删除或命中上限时为 null（未知）。
    /// </summary>
    private static decimal? InvoiceUnreferencedOf(CustomerSalesInvoiceEvidence? invoice,
        IReadOnlyDictionary<long, InvoiceAllocationTotalRow> allocationTotalByInvoice, string currency)
    {
        if (invoice is null || invoice.IsDeleted) return null;
        if (!allocationTotalByInvoice.TryGetValue(invoice.Id, out var total)) return null;

        var gross = AuthoritativeInvoiceGrossAmount(invoice, currency);
        var unreferenced = gross - total.Amount;
        return unreferenced < 0 ? 0m : unreferenced;
    }

    /// <summary>
    /// 单条持久化分摊行的证据分类（ERP-056 的核心口径）：只有「发票已登记（未作废）、分摊行未删除、
    /// 快照自相一致（客户 / 币种 / 订单币种 / 金额等式）且订单可用、客户与币种都一致」才计入有效合计；
    /// 其余一律落到草稿 / 已作废 / 无效 / 无法确认分桶，并给出可读原因（绝不换算、合并或改派）。
    /// <para>订单资格判定复用 ERP-055 的权威规则
    /// <see cref="CustomerSalesInvoiceEvidenceRules.EvaluateOrderEligibility"/>，不引入第二套匹配算法。</para>
    /// </summary>
    private static (string Bucket, string Reason) ClassifyRow(
        CustomerSalesInvoiceAllocation row, CustomerSalesInvoiceEvidence? invoice, SalesOrder? order)
    {
        if (invoice is null || invoice.IsDeleted)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "分摊行指向的发票证据不存在或已删除：证据有效性无法确认，金额不计入有效合计"
                + "（不按 0 处理，也不猜测归属到其他订单）");
        }

        if (invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusVoided)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketVoided,
                $"发票证据已作废（原因：{invoice.VoidReason}）：历史证据仅供核对，金额不计入有效合计（作废保留可读）");
        }

        if (invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusDraft)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketDraft,
                "发票证据仍是草稿：分摊只是工作数据，尚未形成登记证据，金额不计入有效合计"
                + "（登记后才会成为有效证据，也不代表未开票或欠税）");
        }

        if (!SalesOrderInvoiceEvidenceSemantics.IsSupportedInvoiceStatus(invoice.Status))
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"发票证据状态码 {invoice.Status} 无法识别：不能确认证据有效性，金额不计入有效合计");
        }

        if (row.AllocatedAmount <= 0)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"分摊金额 {row.AllocatedAmount} 不是正数：不作为有效证据，不计入有效合计");
        }

        if (row.CustomerId != invoice.CustomerId)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"分摊行客户快照（Id={row.CustomerId}）与发票证据客户（Id={invoice.CustomerId}）不一致："
                + "按无效历史证据单列，不合并、也不改派到其他订单");
        }

        var rowCurrency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        if (!string.Equals(rowCurrency, invoiceCurrency, StringComparison.Ordinal))
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"分摊行币种 {rowCurrency} 与发票证据币种 {invoiceCurrency} 不一致："
                + "按无效历史证据单列（不做汇率换算、不合并为同一金额）");
        }

        var orderCurrencySnapshot = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency);
        if (!string.Equals(orderCurrencySnapshot, rowCurrency, StringComparison.Ordinal))
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"分摊行订单币种快照 {orderCurrencySnapshot} 与分摊行币种 {rowCurrency} 不一致："
                + "快照自相矛盾，按无效历史证据单列（不做汇率换算）");
        }

        if (!AmountEquationHolds(invoice, invoiceCurrency))
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"发票证据金额等式不成立（净额 {invoice.NetAmount} + 税额 {invoice.TaxAmount} ≠ 含税总额 {invoice.GrossAmount}，"
                + $"或含税总额不大于 0 / 税额为负，{invoiceCurrency}）：按无效历史证据单列，不计入有效合计，也不重算金额");
        }

        if (order is null || order.IsDeleted)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "销售订单不存在或已删除：无法按权威口径核对分摊，金额不计入有效合计（不按 0 处理）");
        }

        var (eligible, text) = CustomerSalesInvoiceEvidenceRules.EvaluateOrderEligibility(
            invoice.CustomerId, invoiceCurrency, order);
        if (eligible)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketRecorded,
                text + "：有效销项发票证据，计入有效合计（不代表已开票 / 未开票 / 已收款 / 已结清）");
        }

        if (order.Status == DocumentStatus.Cancelled)
        {
            return (SalesOrderInvoiceEvidenceSemantics.BucketRecorded,
                text + "：该已登记发票证据金额仅作历史参考（照实计入有效合计），不代表已开票或应收余额");
        }

        return (SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
            text + "：该已登记发票证据按无效历史证据单列，不计入有效合计，也不改派到其他订单");
    }

    /// <summary>分摊行 → 只读证据明细（快照原样呈现；金额不重算、不改写，已作废 / 草稿 / 无效证据同样保留可读）</summary>
    private static SalesOrderInvoiceEvidenceLine MapLine(CustomerSalesInvoiceAllocation row,
        CustomerSalesInvoiceEvidence? invoice, string bucket, string reason, decimal? invoiceUnreferenced,
        bool orderAvailable)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var invoiceAvailable = invoice is not null && !invoice.IsDeleted;

        return new SalesOrderInvoiceEvidenceLine
        {
            AllocationId = row.Id,
            InvoiceId = row.CustomerSalesInvoiceEvidenceId,
            InvoiceType = invoice?.InvoiceType ?? string.Empty,
            InvoiceCode = invoice?.InvoiceCode ?? string.Empty,
            InvoiceNumber = invoice?.InvoiceNumber ?? string.Empty,
            InvoiceIdentityText = invoiceAvailable
                ? CustomerSalesInvoiceEvidenceRules.IdentityText(
                    invoice!.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)
                : "无法确认（发票证据不存在或已删除）",
            InvoiceDate = invoice?.InvoiceDate ?? default,
            InvoiceStatus = invoice?.Status,
            InvoiceStatusText = invoiceAvailable
                ? SalesOrderInvoiceEvidenceSemantics.InvoiceStatusText(invoice!.Status)
                : "无法确认（发票证据不存在或已删除）",
            IsDraft = invoiceAvailable && invoice!.Status == CustomerSalesInvoiceEvidenceRules.StatusDraft,
            IsRecorded = invoiceAvailable && invoice!.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded,
            IsVoided = invoiceAvailable && invoice!.Status == CustomerSalesInvoiceEvidenceRules.StatusVoided,
            NetAmount = invoice?.NetAmount ?? 0m,
            TaxAmount = invoice?.TaxAmount ?? 0m,
            GrossAmount = invoice?.GrossAmount ?? 0m,
            AllocatedAmount = row.AllocatedAmount,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            CustomerId = row.CustomerId,
            CustomerCode = row.CustomerCode ?? string.Empty,
            CustomerName = row.CustomerName ?? string.Empty,
            OrderNo = row.OrderNo ?? string.Empty,
            OrderDate = row.OrderDate,
            OrderStatusText = OrderStatusText(row.OrderStatus),
            OrderCurrency = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency),
            InvoiceAvailable = invoiceAvailable,
            InvoiceAvailabilityText = invoiceAvailable
                ? $"发票证据可用（{SalesOrderInvoiceEvidenceSemantics.InvoiceStatusText(invoice!.Status)}）"
                : "被引用的发票证据已删除或不存在（历史分摊快照仍可读）",
            OrderAvailabilityText = orderAvailable
                ? "销售订单可用"
                : "销售订单已删除或不存在：历史分摊保留可读（订单可用性以分摊行快照为准）",
            Bucket = bucket,
            BucketText = SalesOrderInvoiceEvidenceSemantics.BucketText(bucket),
            IsRecordedEvidence = bucket == SalesOrderInvoiceEvidenceSemantics.BucketRecorded,
            InvoiceUnreferencedAmount = invoiceUnreferenced,
            AllocatedAt = row.CreatedAt,
            VoidedAt = invoice?.VoidedAt,
            VoidReason = invoice?.VoidReason ?? string.Empty,
            Remark = row.Remark ?? string.Empty,
            Reason = reason,
        };
    }

    /// <summary>分摊行订单状态快照文案（未知状态码照实说明，绝不抛异常、也绝不按有效兜底）</summary>
    private static string OrderStatusText(int status)
    {
        var value = (DocumentStatus)status;
        return Enum.IsDefined(value) ? value.ToString() : $"无法识别（状态码 {status}）";
    }

    /// <summary>
    /// 汇总派生：订单可用性文案与分摊行分桶；有效已分摊金额只取有效（已登记且未作废）证据，
    /// 返回时把草稿 / 已作废 / 无效 / 无法确认分别单列；命中上限时金额与计数一律为 null（未知）。
    /// </summary>
    private static SalesOrderInvoiceEvidenceSummary BuildSummary(long salesOrderId, SalesOrder? order,
        Dictionary<string, BucketTotals> totals, int rowCount, bool truncated, string fallbackCurrency,
        SalesOrderInvoiceEvidenceAggregate aggregate, string orderNoFallback)
    {
        var available = order is not null && !order.IsDeleted;
        var cancelled = available && order!.Status == DocumentStatus.Cancelled;

        var orderState = !available
            ? SalesOrderInvoiceEvidenceSemantics.OrderStateUnavailable
            : cancelled
                ? SalesOrderInvoiceEvidenceSemantics.OrderStateCancelled
                : SalesOrderInvoiceEvidenceSemantics.OrderStateAvailable;

        var orderStateText = !available
            ? "销售订单不存在或已删除：订单金额未知（不按 0 处理）"
            : cancelled
                ? "销售订单已取消：订单金额仅作历史参考，销项发票证据不参与开票确认、税金、逾期或欠款判定"
                : $"销售订单可用（当前状态 {order!.Status}）";

        var currency = CurrencyAmountRules.NormalizeCurrency(
            available ? order!.Currency.ToString() : fallbackCurrency);

        var recorded = totals[SalesOrderInvoiceEvidenceSemantics.BucketRecorded].Amount;
        var hasAnyRow = rowCount > 0;
        var hasNonActive = SalesOrderInvoiceEvidenceSemantics.SupportedBuckets.Any(b =>
            SalesOrderInvoiceEvidenceSemantics.IsNonActiveBucket(b) && totals[b].RowCount > 0);
        var recordedInvoiceGrossAmount = truncated ? (decimal?)null : aggregate.RecordedInvoiceGrossAmount;
        var unreferencedInvoice = truncated ? (decimal?)null : aggregate.UnreferencedInvoiceAmount;

        var unreferencedOrder = truncated || !available || cancelled
            ? (decimal?)null
            : Math.Max(0m, order!.TotalAmount - recorded);

        decimal? Money(string bucket) => truncated ? null : totals[bucket].Amount;
        int? Rows(string bucket) => truncated ? null : totals[bucket].RowCount;

        return new SalesOrderInvoiceEvidenceSummary
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
            HasInvoiceEvidence = hasAnyRow,
            HasNonActiveEvidence = hasNonActive,
            Truncated = truncated,
            RecordedInvoicedAmount = Money(SalesOrderInvoiceEvidenceSemantics.BucketRecorded),
            RecordedInvoiceCount = truncated ? null : aggregate.RecordedInvoiceCount,
            InvoiceAllocationCount = truncated ? null : rowCount,
            RecordedInvoiceGrossAmount = recordedInvoiceGrossAmount,
            UnreferencedInvoiceAmount = unreferencedInvoice,
            InvoiceUnreferencedOrderAmount = unreferencedOrder,
            DraftInvoiceAllocatedAmount = Money(SalesOrderInvoiceEvidenceSemantics.BucketDraft),
            DraftInvoiceAllocationCount = Rows(SalesOrderInvoiceEvidenceSemantics.BucketDraft),
            VoidedInvoiceAllocatedAmount = Money(SalesOrderInvoiceEvidenceSemantics.BucketVoided),
            VoidedInvoiceAllocationCount = Rows(SalesOrderInvoiceEvidenceSemantics.BucketVoided),
            InvalidInvoiceAllocatedAmount = Money(SalesOrderInvoiceEvidenceSemantics.BucketInvalid),
            InvalidInvoiceAllocationCount = Rows(SalesOrderInvoiceEvidenceSemantics.BucketInvalid),
            UnavailableInvoiceAllocatedAmount = Money(SalesOrderInvoiceEvidenceSemantics.BucketUnavailable),
            UnavailableInvoiceAllocationCount = Rows(SalesOrderInvoiceEvidenceSemantics.BucketUnavailable),
            InvoiceEvidenceStatus = SalesOrderInvoiceEvidenceSemantics.EvidenceStatusOf(
                truncated, truncated ? null : recorded, hasAnyRow),
            InvoiceEvidenceLabel = SalesOrderInvoiceEvidenceSemantics.EvidenceLabel(
                truncated, truncated ? null : recorded, hasAnyRow),
            InvoiceEvidenceNote = BuildEvidenceNote(available, cancelled, truncated, hasAnyRow, totals, recorded,
                recordedInvoiceGrossAmount, unreferencedInvoice, unreferencedOrder, currency),
        };
    }

    /// <summary>
    /// 行级说明：已知什么 / 缺什么 / 为什么不能当已开票金额、应交税金、应收余额或结算依据；
    /// 草稿 / 已作废 / 无效 / 无法确认证据逐桶点名，避免被读成已开票或欠税。
    /// </summary>
    private static string BuildEvidenceNote(bool available, bool cancelled, bool truncated, bool hasAnyRow,
        Dictionary<string, BucketTotals> totals, decimal recorded, decimal? recordedInvoiceGrossAmount,
        decimal? unreferencedInvoice, decimal? unreferencedOrder, string currency)
    {
        var sb = new StringBuilder();

        if (truncated)
            sb.Append(SalesOrderInvoiceEvidenceSemantics.TruncatedNote);
        else if (!available || cancelled)
            sb.Append(SalesOrderInvoiceEvidenceSemantics.UnknownOrderNote);
        else if (recorded > 0)
            sb.Append($"有效销项发票证据 {recorded} {currency} 只按已登记（未作废）发票下的持久化分摊行派生；"
                + $"参与证据的发票含税总额快照合计 {recordedInvoiceGrossAmount} {currency}，"
                + $"其中未指向本订单 {unreferencedInvoice} {currency}"
                + "（可能指向其他销售订单，不是未开票金额，也不是应收余额）；"
                + $"订单金额中未被任何有效销项发票证据分摊到的部分 {unreferencedOrder} {currency}"
                + "（只是没有有效发票证据分摊到，不是应收余额，也不是未开票金额）。");
        else if (hasAnyRow)
        {
            var draftRowCount = totals[SalesOrderInvoiceEvidenceSemantics.BucketDraft].RowCount;
            sb.Append(draftRowCount > 0 && draftRowCount == NonActiveRowCount(totals)
                ? SalesOrderInvoiceEvidenceSemantics.DraftOnlyNote
                : "本销售订单没有有效销项发票证据：现有分摊行都是草稿 / 已作废 / 无效或无法确认，均不计入有效合计；"
                    + "这是销项发票证据缺口，不代表未开票、未收款、已开票或欠缴税金。");
        }
        else
        {
            sb.Append(SalesOrderInvoiceEvidenceSemantics.NoEvidenceNote);
        }

        if (!truncated)
        {
            AppendBucket(sb, totals, SalesOrderInvoiceEvidenceSemantics.BucketDraft,
                "草稿发票证据（未登记，仅工作数据）");
            AppendBucket(sb, totals, SalesOrderInvoiceEvidenceSemantics.BucketVoided, "已作废历史证据");
            AppendBucket(sb, totals, SalesOrderInvoiceEvidenceSemantics.BucketInvalid,
                "无效历史证据（客户 / 币种 / 金额等式或快照不一致）");
            AppendBucket(sb, totals, SalesOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "无法确认的证据（发票证据或订单已删除 / 不存在）");
        }

        sb.Append(" 以上金额一律为销项发票证据口径：不是已开票金额、不是应交税金、不是应收余额、"
            + "不是收款核销或结算结果，也不得据此判定已开票 / 未开票 / 逾期或据以催收。");
        return sb.ToString();
    }

    /// <summary>非有效证据（草稿 + 已作废 + 无效 + 无法确认）分摊行条数合计</summary>
    private static int NonActiveRowCount(Dictionary<string, BucketTotals> totals)
        => SalesOrderInvoiceEvidenceSemantics.SupportedBuckets
            .Where(SalesOrderInvoiceEvidenceSemantics.IsNonActiveBucket)
            .Sum(b => totals[b].RowCount);

    /// <summary>按分摊行条数点名的分桶说明（草稿 / 历史 / 无效 / 无法确认：金额与发票张数都只作参考）</summary>
    private static void AppendBucket(StringBuilder sb, Dictionary<string, BucketTotals> totals,
        string bucket, string label)
    {
        var item = totals[bucket];
        if (item.RowCount == 0) return;
        sb.Append($" 另有{label} {item.RowCount} 条分摊行（金额 {item.Amount}，涉及 {item.InvoiceIds.Count} 张发票）："
            + "不计入有效合计，也不换算 / 合并 / 改派到其他订单，可在明细中单独查看原因。");
    }
}












