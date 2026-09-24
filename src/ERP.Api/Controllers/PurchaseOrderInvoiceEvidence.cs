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
/// 采购订单发票证据口径常量（ERP-048）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免把「发票证据」写成「应付余额 / 已付款 / 已结算」，也避免把草稿 / 已作废 / 无效证据混进已开票金额。
/// <para>定位：<b>采购订单维度的发票证据只读视图</b> —— 它<strong>不是</strong>应付账款台账、<strong>不是</strong>付款授权或付款状态、
/// <strong>不是</strong>税务（进项）申报判断，也<strong>不是</strong>结算进度与账龄表。</para>
/// </summary>
public static class PurchaseOrderInvoiceEvidenceSemantics
{
    // ==================== 1. 有界上限 ====================

    /// <summary>单次批量查询最多接受的采购订单张数（列表按页取 Id；超出直接拒绝，不静默截断）</summary>
    public const int MaxBatchOrders = 200;

    /// <summary>
    /// 单次批量查询最多读取的持久化关联行数（有界）：命中上限时本次响应内全部金额与计数按「未知」返回，
    /// 绝不报出「只统计了一部分证据」的合计。
    /// </summary>
    public const int MaxBatchEvidenceRows = 5000;

    /// <summary>单张采购订单详情最多读取的持久化关联行数（有界）：命中上限时金额与计数按「未知」返回，明细只显示已读取部分</summary>
    public const int MaxOrderEvidenceRows = 500;

    // ==================== 2. 证据分桶 ====================

    /// <summary>已登记（未作废）证据：唯一计入「已开票金额」的分桶</summary>
    public const string BucketRecorded = "recorded";

    /// <summary>草稿证据（工作数据，尚未登记为证据）：单独分桶，绝不计入已开票金额</summary>
    public const string BucketDraft = "draft";

    /// <summary>已作废历史证据：单独分桶，绝不计入已开票金额（保留可读）</summary>
    public const string BucketVoided = "voided";

    /// <summary>无效历史证据（供应商 / 币种不一致或关联行快照自相矛盾）：不换算、不合并、不重新归属，绝不计入已开票金额</summary>
    public const string BucketInvalid = "invalid";

    /// <summary>无法确认的证据（关联行指向的发票或采购订单不存在 / 已删除）：金额未知，绝不计入已开票金额</summary>
    public const string BucketUnavailable = "unavailable";

    /// <summary>支持的分桶取值（超出范围不静默兜底）</summary>
    public static readonly string[] SupportedBuckets =
        { BucketRecorded, BucketDraft, BucketVoided, BucketInvalid, BucketUnavailable };

    /// <summary>分桶文案（接口、界面与文档同源）</summary>
    public static string BucketText(string bucket) => bucket switch
    {
        BucketRecorded => "已登记证据（计入已开票金额）",
        BucketDraft => "草稿证据（工作数据，不计入已开票金额）",
        BucketVoided => "已作废历史证据（不计入已开票金额）",
        BucketInvalid => "无效历史证据（供应商 / 币种或快照不一致：不换算、不合并、不改派）",
        _ => "无法确认的证据（发票或订单不存在 / 已删除：金额未知）",
    };

    /// <summary>是否属于「历史 / 无效证据」（= 不计入已开票金额的分桶）</summary>
    public static bool IsHistoricalBucket(string bucket)
        => bucket is BucketDraft or BucketVoided or BucketInvalid or BucketUnavailable;

    // ==================== 3. 覆盖状态短文案 ====================

    /// <summary>
    /// 覆盖状态短文案（覆盖状态本身复用 ERP-044 的同一套常量与长文案；
    /// 这里只加一档界面短标签，并显式区分「无任何证据」与「只有历史 / 无效证据」）。
    /// </summary>
    public static string CoverageLabel(string coverageStatus, bool hasEvidence) => coverageStatus switch
    {
        SupplierInvoiceReconciliationSemantics.CoverageFullyInvoiced => "已全额开票",
        SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced => "部分开票",
        SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced =>
            hasEvidence ? "未开票（仅有历史 / 无效证据）" : "无发票证据",
        _ => "未知",
    };

    // ==================== 4. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（金额来源、分桶、覆盖状态与未知处理）</summary>
    public const string RuleText =
        "本视图是采购订单的**发票证据口径**（只读派生）：已开票金额只按 ERP-043 的持久化关联行派生，且只统计**已登记且未作废**的发票证据；" +
        "草稿证据、已作废历史证据、无效证据（供应商 / 币种不一致或关联行快照自相矛盾）与无法确认证据（发票或订单已删除）一律单独分桶，" +
        "绝不计入已开票金额，也绝不折算、合并或改派到其他采购订单（不按单号 / 金额 / 日期相似度猜测归属）；" +
        "未开票金额 = 采购订单已落库总额 − 已开票金额（下限 0），只在订单可用且未取消时给出，订单已取消或不可用时按未知显示；" +
        "覆盖状态与订单可用性文案复用 ERP-044 的同一套口径常量；订单供应商 / 币种校验复用 ERP-043 的权威资格判定；" +
        "币种与金额精度复用 ERP-043 的 CurrencyAmountRules（JPY / KRW / VND / IDR 为 0 位小数，其余 2 位），不同币种绝不合并、不做汇率换算。";

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public const string ScopeText =
        "有界读取：批量接口一次最多 200 张采购订单、最多读取 5000 条持久化关联行；单张订单详情最多读取 500 条关联行。" +
        "命中读取上限时，本次响应的金额与计数一律按「未知」返回（不报出部分合计），并在备注中说明原因；" +
        "订单金额取采购订单已落库总额，发票金额取发票已落库金额，本视图不重算、不改写任何单据。";

    /// <summary>与应付账款台账 / 付款授权 / 税务申报 / 结算进度 / 账龄表的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本视图只是采购发票证据的只读呈现：不是应付账款台账或应付余额、不是付款授权与付款状态、不是税务（进项）申报或抵扣判断、" +
        "也不是结算进度与账龄表 —— 它不判断是否已付款、是否已结清或是否逾期，不推算账期与到期日、不做账龄分摊与税负计算，" +
        "也不产生任何记账、凭证、收付款或结算单；界面与接口一律把「已开票」标注为**采购发票证据**，不得当作欠款金额或可付款金额。";

    /// <summary>无任何发票证据时的提示（绝不能被读成已付款 / 已结清 / 已逾期）</summary>
    public const string NoEvidenceNote =
        "该采购订单没有任何登记发票关联行：这是**发票证据缺口**，不代表未付款、已结清或逾期，也不构成应付余额或付款依据。";

    /// <summary>命中读取上限时的提示（金额未知，不给部分合计）</summary>
    public const string TruncatedNote =
        "本次读取命中系统有界上限（关联行数量超出上限），无法穷尽该订单的发票证据：金额与计数一律按「未知」显示，不作部分合计。";

    /// <summary>无可用订单金额时的提示（订单已取消或不可用）</summary>
    public const string UnknownOrderNote =
        "采购订单已取消或已删除：订单金额与未开票余额按未知显示（不按 0 处理，也不推断付款或结算状态）。";
}
/// <summary>
/// 采购订单 Id 列表查询参数（ERP-048，只读）：逗号分隔的正整数，去重、保序、有界（超出上限直接拒绝）。
/// </summary>
public sealed class PurchaseOrderInvoiceEvidenceQuery
{
    /// <summary>逗号分隔的采购订单 Id（如 <c>12,34,56</c>；留空 = 不查询任何订单）</summary>
    public string? Ids { get; set; }

    /// <summary>归一化后的订单 Id（去重、保持提交顺序）</summary>
    public IReadOnlyList<long> OrderIds => _orderIds;

    private readonly List<long> _orderIds = new();

    /// <summary>
    /// 归一化并校验：只接受正整数 Id，去重（保留首次出现顺序）；非法片段或超出
    /// <see cref="PurchaseOrderInvoiceEvidenceSemantics.MaxBatchOrders"/> 一律拒绝，不静默丢弃、不静默截断。
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
                    $"订单 Id「{token}」无效：发票证据批量接口只接受逗号分隔的正整数采购订单 Id");

            if (_orderIds.Contains(id)) continue;
            _orderIds.Add(id);

            if (_orderIds.Count > PurchaseOrderInvoiceEvidenceSemantics.MaxBatchOrders)
                throw BusinessException.InvalidParameter(
                    $"一次最多查询 {PurchaseOrderInvoiceEvidenceSemantics.MaxBatchOrders} 张采购订单的发票证据（有界查询）："
                    + "本次提交超过上限，请按页分批查询");
        }
    }
}

/// <summary>
/// 采购订单发票证据汇总（ERP-048，只读派生；列表列与详情头部共用）。
/// <para>金额口径：<see cref="RecordedAllocatedAmount"/> 只统计**已登记且未作废**的持久化关联行（有效证据）；
/// 草稿 / 已作废 / 无效 / 无法确认分别单列，绝不并入已开票金额，也绝不折算、合并或改派到其他订单。</para>
/// <para>未知处理：命中读取上限或订单已取消 / 不可用时，金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 本汇总<strong>不是</strong>应付余额、付款状态、结算状态或账龄。</para>
/// </summary>
public sealed class PurchaseOrderInvoiceEvidenceSummary
{
    /// <summary>采购订单 Id</summary>
    public long PurchaseOrderId { get; init; }

    /// <summary>采购单号（订单已删除时按关联行快照显示）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单当前是否可用（存在且未删除）</summary>
    public bool OrderAvailable { get; init; }

    /// <summary>订单可用性状态：available / cancelled / unavailable（与 ERP-044 同口径）</summary>
    public string OrderState { get; init; } = SupplierInvoiceReconciliationSemantics.OrderStateUnavailable;

    /// <summary>订单可用性文案（已取消 / 已删除时照实说明）</summary>
    public string OrderStateText { get; init; } = string.Empty;

    /// <summary>订单当前单据状态文案（订单不存在 / 已删除时照实说明）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>订单币种（原币；与发票币种不一致时按无效证据处理，绝不换算）</summary>
    public string OrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用；复用 ERP-043 的币种精度口径）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>采购订单已落库总额；null = 未知（订单不存在 / 已删除），不等于 0</summary>
    public decimal? OrderedAmount { get; init; }

    /// <summary>是否读到至少一条持久化关联行（不代表有效证据，也不代表已付款）</summary>
    public bool HasInvoiceEvidence { get; init; }

    /// <summary>是否存在草稿 / 已作废 / 无效 / 无法确认的历史证据（必须单独查看，绝不计入已开票金额）</summary>
    public bool HasHistoricalEvidence { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>已开票金额（已登记且未作废的有效证据，原币）；null = 未知（命中上限）</summary>
    public decimal? RecordedAllocatedAmount { get; init; }

    /// <summary>参与已开票金额的发票张数（已登记且未作废，按发票去重）；null = 未知（命中上限）</summary>
    public int? RecordedInvoiceCount { get; init; }

    /// <summary>草稿证据金额（工作数据，原币；绝不计入已开票金额）；null = 未知（命中上限）</summary>
    public decimal? DraftAllocatedAmount { get; init; }

    /// <summary>草稿证据发票张数；null = 未知（命中上限）</summary>
    public int? DraftInvoiceCount { get; init; }

    /// <summary>已作废历史证据金额（原币；绝不计入已开票金额，保留可读）；null = 未知（命中上限）</summary>
    public decimal? VoidedAllocatedAmount { get; init; }

    /// <summary>已作废历史证据发票张数；null = 未知（命中上限）</summary>
    public int? VoidedInvoiceCount { get; init; }

    /// <summary>无效历史证据金额（供应商 / 币种或快照不一致；绝不换算、合并或改派）；null = 未知（命中上限）</summary>
    public decimal? InvalidAllocatedAmount { get; init; }

    /// <summary>无效历史证据关联行条数；null = 未知（命中上限）</summary>
    public int? InvalidAllocationCount { get; init; }

    /// <summary>无效历史证据发票张数（去重）；null = 未知（命中上限）</summary>
    public int? InvalidInvoiceCount { get; init; }

    /// <summary>无法确认的证据金额（发票或订单已删除：无法确认有效性）；null = 未知（命中上限）</summary>
    public decimal? UnavailableAllocatedAmount { get; init; }

    /// <summary>无法确认的证据关联行条数；null = 未知（命中上限）</summary>
    public int? UnavailableAllocationCount { get; init; }

    /// <summary>本次读取到的持久化关联行总条数（含历史 / 无效证据）；null = 未知（命中上限）</summary>
    public int? AllocationCount { get; init; }

    /// <summary>未开票金额 = 订单金额 − 已开票金额（下限 0）；null = 未知（订单不可用 / 已取消 / 命中上限）</summary>
    public decimal? RemainingUninvoicedAmount { get; init; }

    /// <summary>覆盖状态：fully_invoiced / partially_invoiced / not_invoiced / unknown（复用 ERP-044 常量）</summary>
    public string CoverageStatus { get; init; } = SupplierInvoiceReconciliationSemantics.CoverageUnknown;

    /// <summary>覆盖状态文案（复用 ERP-044 同一套文案）</summary>
    public string CoverageText { get; init; } =
        SupplierInvoiceReconciliationSemantics.CoverageText(SupplierInvoiceReconciliationSemantics.CoverageUnknown);

    /// <summary>覆盖状态界面短标签（区分「无发票证据」与「仅有历史 / 无效证据」）</summary>
    public string CoverageLabel { get; init; } = string.Empty;

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不作为应付余额或付款依据）</summary>
    public string EvidenceNote { get; init; } = string.Empty;
}
/// <summary>
/// 一行采购发票证据（ERP-048，只读派生；明细行 = 一条 ERP-043 持久化关联行）。
/// <para><see cref="Bucket"/> 表明该关联行是有效证据还是历史 / 无效证据：
/// 只有 <c>recorded</c> 计入已开票金额；<c>draft</c> / <c>voided</c> / <c>invalid</c> / <c>unavailable</c>
/// 一律单独列出并给出 <see cref="Reason"/>，系统绝不换算、合并或改派到其他订单。</para>
/// </summary>
public sealed class PurchaseOrderInvoiceEvidenceLine
{
    /// <summary>发票 Id（关联行指向的 ERP-043 发票）</summary>
    public long PurchaseInvoiceId { get; init; }

    /// <summary>发票类型（普票 / 专票；发票缺失时留空）</summary>
    public string InvoiceType { get; init; } = string.Empty;

    /// <summary>发票代码（可为空）</summary>
    public string InvoiceCode { get; init; } = string.Empty;

    /// <summary>发票号码</summary>
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>对外身份文案（类型 + 代码 − 号码，与 ERP-043 唯一性判定同口径）</summary>
    public string IdentityText { get; init; } = string.Empty;

    /// <summary>开票日期；null = 发票缺失无法确认</summary>
    public DateTime? InvoiceDate { get; init; }

    /// <summary>发票状态（0 草稿 / 1 已登记 / 2 已作废）；null = 发票缺失无法确认</summary>
    public int? InvoiceStatus { get; init; }

    /// <summary>发票状态文案（草稿 / 已登记 / 已作废 / 无法确认）</summary>
    public string InvoiceStatusText { get; init; } = string.Empty;

    /// <summary>是否已登记证据（唯一计入已开票金额的证据类型）</summary>
    public bool IsRecordedEvidence { get; init; }

    /// <summary>是否仍为草稿（工作数据，未形成登记证据）</summary>
    public bool IsDraft { get; init; }

    /// <summary>是否已作废（历史证据，保留可读）</summary>
    public bool IsVoided { get; init; }

    /// <summary>证据分桶：recorded / draft / voided / invalid / unavailable</summary>
    public string Bucket { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable;

    /// <summary>分桶文案</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>本行关联金额（原币；按持久化关联行原样呈现，不重算）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>币种（原币；关联行币种，与发票币种一致才可能是有效证据）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>发票供应商 Id（发票缺失时为 0）</summary>
    public long SupplierId { get; init; }

    /// <summary>供应商编码快照（发票侧）</summary>
    public string SupplierCode { get; init; } = string.Empty;

    /// <summary>供应商名称快照（发票侧）</summary>
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>关联行供应商 Id 快照（与发票供应商不一致即无效证据）</summary>
    public long AllocationSupplierId { get; init; }

    /// <summary>关联行订单币种快照（与发票币种不一致即无效证据，绝不换算）</summary>
    public string AllocationOrderCurrency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>登记时间（未登记为空）</summary>
    public DateTime? RecordedAt { get; init; }

    /// <summary>作废时间（未作废为空）</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因（作废必须留痕）</summary>
    public string VoidReason { get; init; } = string.Empty;

    /// <summary>本行说明：为什么计入 / 不计入已开票金额（无效时给出权威不一致原因）</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// 采购订单发票证据详情（ERP-048，只读派生）：汇总 + 逐条证据明细 + 口径文案。
/// <para><see cref="Lines"/> 为有界明细（最多 <see cref="PurchaseOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows"/> 条）；
/// 命中上限时汇总金额与计数按未知显示，明细仅展示已读取部分。</para>
/// </summary>
public sealed class PurchaseOrderInvoiceEvidenceDetail
{
    /// <summary>汇总（与该订单列表列同口径）</summary>
    public PurchaseOrderInvoiceEvidenceSummary Summary { get; init; } = new();

    /// <summary>逐条持久化关联行证据（有界：最多 500 条）</summary>
    public List<PurchaseOrderInvoiceEvidenceLine> Lines { get; init; } = new();

    /// <summary>本次返回的明细条数</summary>
    public int LineCount { get; init; }

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.ScopeText;

    /// <summary>与应付账款台账 / 付款授权 / 税务申报 / 结算进度 / 账龄表的边界说明</summary>
    public string Boundary { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.BoundaryText;

    /// <summary>发票金额等式口径（与 ERP-043 同源）</summary>
    public string AmountEquation { get; init; } = PurchaseInvoiceRules.AmountEquationText;

    /// <summary>发票关联口径（与 ERP-043 同源）</summary>
    public string LinkageRule { get; init; } = PurchaseInvoiceRules.LinkageRuleText;
}

/// <summary>
/// 采购订单发票证据批量汇总（ERP-048，只读派生；列表页一次请求取回本页订单的覆盖汇总，不逐行查库）。
/// </summary>
public sealed class PurchaseOrderInvoiceEvidenceBatch
{
    /// <summary>本次请求的订单 Id 张数（去重后）</summary>
    public int RequestedCount { get; init; }

    /// <summary>返回的汇总条数（与请求的订单 Id 一一对应，含未找到 / 已删除的订单）</summary>
    public int ItemCount { get; init; }

    /// <summary>本次读取是否命中系统有界上限（true = 全部金额与计数按未知返回）</summary>
    public bool Truncated { get; init; }

    /// <summary>按请求顺序返回的订单汇总</summary>
    public List<PurchaseOrderInvoiceEvidenceSummary> Items { get; init; } = new();

    /// <summary>派生口径说明（界面原样展示）</summary>
    public string Rule { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.RuleText;

    /// <summary>范围说明（有界读取与未知处理）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.ScopeText;

    /// <summary>与应付账款台账 / 付款授权 / 税务申报 / 结算进度 / 账龄表的边界说明</summary>
    public string Boundary { get; init; } = PurchaseOrderInvoiceEvidenceSemantics.BoundaryText;
}

/// <summary>
/// 采购订单发票证据派生（ERP-048，只读）：在既有采购订单列表 / 详情工作流中暴露**发票证据汇总**，
/// 全部金额只按 ERP-043 的持久化关联行派生，并复用 ERP-044 的覆盖状态与订单可用性口径。
/// <para>边界（重要）：本派生<strong>只读</strong>（全部 <c>AsNoTracking</c>、从不 <c>SaveChanges</c>），
/// 不改写采购订单状态 / 到货进度 / 结算进度文本、发票与关联行、库存与库存成本、收付款、供应商余额、费用或退税记录；
/// 也<strong>不</strong>认定应付余额、<strong>不</strong>授权付款、<strong>不</strong>判断税务抵扣或结算状态。</para>
/// <para>有界：固定 3 次数据集访问（订单 + 持久化关联行 + 关联发票），与订单张数 / 行数无关，绝无逐行查库；
/// 行数超出上限时金额与计数按「未知」返回，不报部分合计。</para>
/// </summary>
public static class PurchaseOrderInvoiceEvidence
{
    /// <summary>
    /// 单张采购订单的发票证据详情（汇总 + 有界证据明细）。
    /// <para>订单不存在或已删除时抛「数据不存在」（与 <c>/api/purchase-orders/{id}</c> 同口径）。</para>
    /// </summary>
    public static async Task<PurchaseOrderInvoiceEvidenceDetail> ForOrderAsync(IErpDbContext db, long orderId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orderId <= 0)
            throw BusinessException.InvalidParameter("采购订单 Id 无效：请指定有效的采购订单");

        var built = await BuildAsync(db, new[] { orderId },
            PurchaseOrderInvoiceEvidenceSemantics.MaxOrderEvidenceRows);

        if (!built.ByOrder.TryGetValue(orderId, out var evidence) || !evidence.Summary.OrderAvailable)
            throw BusinessException.NotFound("采购订单不存在");

        return new PurchaseOrderInvoiceEvidenceDetail
        {
            Summary = evidence.Summary,
            Lines = evidence.Lines,
            LineCount = evidence.Lines.Count
        };
    }

    /// <summary>
    /// 批量发票证据汇总（列表页用，只读有界）：一次请求取回本页订单的覆盖汇总，不逐行查库。
    /// <para>订单 Id 非法或超过 <see cref="PurchaseOrderInvoiceEvidenceSemantics.MaxBatchOrders"/> 一律拒绝；
    /// 请求为空的订单 Id 时直接返回空结果，不访问数据库。</para>
    /// </summary>
    public static async Task<PurchaseOrderInvoiceEvidenceBatch> ForOrdersAsync(
        IErpDbContext db, PurchaseOrderInvoiceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        if (query.OrderIds.Count == 0)
        {
            return new PurchaseOrderInvoiceEvidenceBatch
            {
                RequestedCount = 0,
                ItemCount = 0,
                Truncated = false
            };
        }

        var built = await BuildAsync(db, query.OrderIds,
            PurchaseOrderInvoiceEvidenceSemantics.MaxBatchEvidenceRows);

        var items = query.OrderIds
            .Select(id => built.ByOrder.TryGetValue(id, out var evidence)
                ? evidence.Summary
                : BuildSummaryForMissingOrder(id, built.Truncated))
            .ToList();

        return new PurchaseOrderInvoiceEvidenceBatch
        {
            RequestedCount = query.OrderIds.Count,
            ItemCount = items.Count,
            Truncated = built.Truncated,
            Items = items
        };
    }

    /// <summary>请求的订单 Id 未找到时的汇总（未知，不按 0；列表列仍能显示「未知」而不是静默丢行）</summary>
    private static PurchaseOrderInvoiceEvidenceSummary BuildSummaryForMissingOrder(long orderId, bool truncated)
        => BuildSummary(orderId, null, EmptyTotals(), 0, truncated, CurrencyAmountRules.DefaultCurrency, string.Empty);

    // ==================== 内部：批量读取与派生 ====================

    /// <summary>单张订单的证据派生结果（汇总 + 明细行）</summary>
    private sealed record OrderEvidence(PurchaseOrderInvoiceEvidenceSummary Summary,
        List<PurchaseOrderInvoiceEvidenceLine> Lines);

    /// <summary>一次批量派生的读取结果（按订单 Id 索引；<see cref="Truncated"/> = 命中行数上限）</summary>
    private sealed record BuildResult(Dictionary<long, OrderEvidence> ByOrder, bool Truncated);

    /// <summary>单条关联行的分桶累计（金额 / 行数 / 发票去重）</summary>
    private sealed class BucketTotals
    {
        /// <summary>分桶金额合计（原币，按持久化行汇总）</summary>
        public decimal Amount { get; private set; }

        /// <summary>分桶内的持久化关联行条数</summary>
        public int RowCount { get; private set; }

        /// <summary>分桶内的发票 Id（去重；用于「发票张数」计数）</summary>
        public HashSet<long> InvoiceIds { get; } = new();

        /// <summary>累计一条关联行</summary>
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
        foreach (var bucket in PurchaseOrderInvoiceEvidenceSemantics.SupportedBuckets)
            totals[bucket] = new BucketTotals();
        return totals;
    }

    /// <summary>
    /// 固定 3 次数据集访问：采购订单 + 持久化关联行（有界，含 1 条溢出探测）+ 关联发票；
    /// 全程只读、不写库，金额按持久化行原样汇总（不重算、不四舍五入改写）。
    /// </summary>
    private static async Task<BuildResult> BuildAsync(
        IErpDbContext db, IReadOnlyList<long> orderIds, int rowCap)
    {
        var orders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .ToListAsync();
        var orderById = orders.ToDictionary(o => o.Id);

        var allocations = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && orderIds.Contains(a.PurchaseOrderId))
            .OrderBy(a => a.PurchaseOrderId).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
            .Take(rowCap + 1)
            .ToListAsync();
        var truncated = allocations.Count > rowCap;
        if (truncated) allocations.RemoveRange(rowCap, allocations.Count - rowCap);

        var invoiceIds = allocations.Select(a => a.PurchaseInvoiceId).Distinct().ToList();
        var invoices = invoiceIds.Count == 0
            ? new List<PurchaseInvoice>()
            : await db.PurchaseInvoices.AsNoTracking()
                .Where(i => invoiceIds.Contains(i.Id))
                .ToListAsync();
        var invoiceById = invoices.ToDictionary(i => i.Id);

        var rowsByOrder = allocations
            .GroupBy(a => a.PurchaseOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<long, OrderEvidence>(orderIds.Count);
        foreach (var orderId in orderIds)
        {
            orderById.TryGetValue(orderId, out var order);
            var rows = rowsByOrder.TryGetValue(orderId, out var list)
                ? list
                : new List<PurchaseInvoiceAllocation>();
            result[orderId] = BuildOrderEvidence(orderId, order, rows, invoiceById, truncated);
        }

        return new BuildResult(result, truncated);
    }

    /// <summary>单张订单派生：逐条关联行分桶 → 汇总 + 明细（历史 / 无效证据单独列示）</summary>
    private static OrderEvidence BuildOrderEvidence(long orderId, PurchaseOrder? order,
        List<PurchaseInvoiceAllocation> rows, Dictionary<long, PurchaseInvoice> invoiceById, bool truncated)
    {
        var totals = EmptyTotals();
        var lines = new List<PurchaseOrderInvoiceEvidenceLine>(rows.Count);

        foreach (var row in rows)
        {
            invoiceById.TryGetValue(row.PurchaseInvoiceId, out var invoice);
            var (bucket, reason) = ClassifyAllocation(row, invoice, order);
            totals[bucket].Add(row.AllocatedAmount, row.PurchaseInvoiceId);
            lines.Add(MapLine(row, invoice, bucket, reason));
        }

        var fallbackCurrency = CurrencyAmountRules.NormalizeCurrency(
            order is not null && !order.IsDeleted
                ? order.Currency.ToString()
                : lines.Find(l => l.Currency.Length > 0)?.Currency);

        var summary = BuildSummary(orderId, order, totals, rows.Count, truncated, fallbackCurrency, string.Empty);
        return new OrderEvidence(summary, lines);
    }

    /// <summary>
    /// 单条持久化关联行的证据分类（ERP-048 的核心口径）：
    /// 只有「发票已登记且未作废、关联行快照自相一致、订单可用且供应商 / 币种与发票一致」才计入已开票金额；
    /// 其余一律落到草稿 / 已作废 / 无效 / 无法确认分桶，并给出可读原因（绝不换算、合并或改派）。
    /// <para>供应商 / 币种 / 订单可用性判定复用 ERP-043 的权威规则
    /// <see cref="PurchaseInvoiceRules.EvaluateOrderEligibility"/>，不引入第二套匹配算法。</para>
    /// </summary>
    private static (string Bucket, string Reason) ClassifyAllocation(
        PurchaseInvoiceAllocation allocation, PurchaseInvoice? invoice, PurchaseOrder? order)
    {
        if (invoice is null)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "关联行指向的发票不存在：证据有效性无法确认，金额不计入任何合计（不猜测归属到其他订单）");

        if (invoice.IsDeleted)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "关联行指向的发票已删除：证据有效性无法确认，金额不计入任何合计");

        if (allocation.AllocatedAmount <= 0)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"关联金额 {allocation.AllocatedAmount} 不是正数：不作为有效证据，不计入任何合计");

        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketVoided,
                "发票已作废：历史证据仅供核对，金额不计入已开票金额（作废保留可读）");

        if (order is null || order.IsDeleted)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "采购订单不存在或已删除：无法按权威口径核对证据，金额不计入已开票金额（不按 0 处理）");

        if (invoice.Status == PurchaseInvoiceRules.StatusDraft)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketDraft,
                "发票仍为草稿（工作数据）：尚未登记为证据，金额不计入已开票金额");

        if (invoice.Status != PurchaseInvoiceRules.StatusRecorded)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"发票状态 {invoice.Status} 无法识别：无法确认证据有效性，金额不计入任何合计");

        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var allocationCurrency = CurrencyAmountRules.NormalizeCurrency(allocation.Currency);
        if (allocation.SupplierId != invoice.SupplierId
            || !string.Equals(allocationCurrency, invoiceCurrency, StringComparison.Ordinal))
        {
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"关联行快照与发票不一致（关联行供应商 Id={allocation.SupplierId} / 币种 {allocationCurrency}，"
                + $"发票供应商 Id={invoice.SupplierId} / 币种 {invoiceCurrency}）："
                + "按无效历史证据处理，不换算、不合并、不改派到其他订单");
        }

        var snapshotOrderCurrency = CurrencyAmountRules.NormalizeCurrency(allocation.OrderCurrency);
        if (!string.Equals(snapshotOrderCurrency, invoiceCurrency, StringComparison.Ordinal))
        {
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid,
                $"关联行订单币种快照 {snapshotOrderCurrency} 与发票币种 {invoiceCurrency} 不一致："
                + "不做汇率换算，按无效历史证据处理");
        }

        if (order.Status == DocumentStatus.Cancelled)
            return (PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded,
                "采购订单已取消：该已登记证据金额仅作历史参考（覆盖状态与未开票金额按未知显示），不代表已付款");

        // 复用 ERP-043 的权威资格判定（订单可用、供应商一致、币种一致，不做汇率换算）
        var (eligible, text) = PurchaseInvoiceRules.EvaluateOrderEligibility(invoice, order);
        return eligible
            ? (PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded,
                text + "：已登记证据，计入已开票金额")
            : (PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid,
                text + "：该已登记证据按无效历史证据单列，不计入已开票金额，也不改派到其他订单");
    }

    /// <summary>关联行 → 证据明细行（纯映射；金额与币种按持久化行原样呈现，不做换算）</summary>
    private static PurchaseOrderInvoiceEvidenceLine MapLine(PurchaseInvoiceAllocation allocation,
        PurchaseInvoice? invoice, string bucket, string reason)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(
            invoice is not null && !string.IsNullOrWhiteSpace(invoice.Currency)
                ? invoice.Currency
                : allocation.Currency);

        return new PurchaseOrderInvoiceEvidenceLine
        {
            PurchaseInvoiceId = allocation.PurchaseInvoiceId,
            InvoiceType = invoice?.InvoiceType ?? string.Empty,
            InvoiceCode = invoice?.InvoiceCode ?? string.Empty,
            InvoiceNumber = invoice?.InvoiceNumber ?? string.Empty,
            IdentityText = invoice is null
                ? $"（发票 Id={allocation.PurchaseInvoiceId} 不存在或已删除）"
                : PurchaseInvoiceRules.IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
            InvoiceDate = invoice?.InvoiceDate,
            InvoiceStatus = invoice?.Status,
            InvoiceStatusText = invoice is null ? "无法确认（发票不存在或已删除）" : InvoiceStatusText(invoice.Status),
            IsRecordedEvidence = bucket == PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded,
            IsDraft = invoice?.Status == PurchaseInvoiceRules.StatusDraft,
            IsVoided = invoice?.Status == PurchaseInvoiceRules.StatusVoided,
            Bucket = bucket,
            BucketText = PurchaseOrderInvoiceEvidenceSemantics.BucketText(bucket),
            AllocatedAmount = allocation.AllocatedAmount,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            SupplierId = invoice?.SupplierId ?? 0,
            SupplierCode = invoice?.SupplierCode ?? string.Empty,
            SupplierName = invoice?.SupplierName ?? string.Empty,
            AllocationSupplierId = allocation.SupplierId,
            AllocationOrderCurrency = CurrencyAmountRules.NormalizeCurrency(allocation.OrderCurrency),
            RecordedAt = invoice?.RecordedAt,
            VoidedAt = invoice?.VoidedAt,
            VoidReason = invoice?.VoidReason ?? string.Empty,
            Reason = reason
        };
    }

    /// <summary>发票状态文案（未知状态码照实说明，绝不抛异常、也绝不按草稿兜底）</summary>
    private static string InvoiceStatusText(int status) => status switch
    {
        PurchaseInvoiceRules.StatusDraft => "草稿",
        PurchaseInvoiceRules.StatusRecorded => "已登记",
        PurchaseInvoiceRules.StatusVoided => "已作废",
        _ => $"无法识别（状态码 {status}）"
    };

    /// <summary>
    /// 汇总派生：订单可用性 → 覆盖状态（复用 ERP-044 常量与文案）、已开票金额只取已登记证据，
    /// 返回时把草稿 / 已作废 / 无效 / 无法确认分别单列；命中上限时金额与计数一律为 null（未知）。
    /// </summary>
    private static PurchaseOrderInvoiceEvidenceSummary BuildSummary(long orderId, PurchaseOrder? order,
        Dictionary<string, BucketTotals> totals, int rowCount, bool truncated, string fallbackCurrency,
        string orderNoFallback)
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
                ? "采购订单已取消：订单金额仅作历史参考，覆盖状态与未开票金额按未知显示"
                : $"采购订单可用（当前状态 {order!.Status}）";

        var currency = CurrencyAmountRules.NormalizeCurrency(
            available ? order!.Currency.ToString() : fallbackCurrency);

        var recorded = totals[PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded].Amount;
        var hasEvidence = rowCount > 0;
        var hasHistorical = PurchaseOrderInvoiceEvidenceSemantics.SupportedBuckets.Any(b =>
            PurchaseOrderInvoiceEvidenceSemantics.IsHistoricalBucket(b) && totals[b].RowCount > 0);

        decimal? remaining;
        string coverage;
        if (truncated || !available || cancelled)
        {
            remaining = null;
            coverage = SupplierInvoiceReconciliationSemantics.CoverageUnknown;
        }
        else
        {
            remaining = Math.Max(0m, order!.TotalAmount - recorded);
            coverage = recorded <= 0
                ? SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced
                : recorded < order.TotalAmount
                    ? SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced
                    : SupplierInvoiceReconciliationSemantics.CoverageFullyInvoiced;
        }

        decimal? Money(string bucket) => truncated ? null : totals[bucket].Amount;
        int? InvoiceCount(string bucket) => truncated ? null : totals[bucket].InvoiceIds.Count;
        int? RowCount(string bucket) => truncated ? null : totals[bucket].RowCount;

        return new PurchaseOrderInvoiceEvidenceSummary
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
            HasInvoiceEvidence = hasEvidence,
            HasHistoricalEvidence = hasHistorical,
            Truncated = truncated,
            RecordedAllocatedAmount = Money(PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded),
            RecordedInvoiceCount = InvoiceCount(PurchaseOrderInvoiceEvidenceSemantics.BucketRecorded),
            DraftAllocatedAmount = Money(PurchaseOrderInvoiceEvidenceSemantics.BucketDraft),
            DraftInvoiceCount = InvoiceCount(PurchaseOrderInvoiceEvidenceSemantics.BucketDraft),
            VoidedAllocatedAmount = Money(PurchaseOrderInvoiceEvidenceSemantics.BucketVoided),
            VoidedInvoiceCount = InvoiceCount(PurchaseOrderInvoiceEvidenceSemantics.BucketVoided),
            InvalidAllocatedAmount = Money(PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid),
            InvalidAllocationCount = RowCount(PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid),
            InvalidInvoiceCount = InvoiceCount(PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid),
            UnavailableAllocatedAmount = Money(PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable),
            UnavailableAllocationCount = RowCount(PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable),
            AllocationCount = truncated ? null : rowCount,
            RemainingUninvoicedAmount = remaining,
            CoverageStatus = coverage,
            CoverageText = SupplierInvoiceReconciliationSemantics.CoverageText(coverage),
            CoverageLabel = PurchaseOrderInvoiceEvidenceSemantics.CoverageLabel(coverage, hasEvidence),
            EvidenceNote = BuildEvidenceNote(available, cancelled, truncated, hasEvidence, totals, recorded,
                remaining, currency)
        };
    }

    /// <summary>
    /// 行级说明：已知什么 / 缺什么 / 为什么不能当应付余额或付款依据；历史 / 无效证据逐桶点名，避免被读成已开票。
    /// </summary>
    private static string BuildEvidenceNote(bool available, bool cancelled, bool truncated, bool hasEvidence,
        Dictionary<string, BucketTotals> totals, decimal recorded, decimal? remaining, string currency)
    {
        var sb = new StringBuilder();

        if (truncated)
            sb.Append(PurchaseOrderInvoiceEvidenceSemantics.TruncatedNote);
        else if (!available || cancelled)
            sb.Append(PurchaseOrderInvoiceEvidenceSemantics.UnknownOrderNote);
        else if (recorded > 0)
            sb.Append($"已开票金额 {recorded} {currency} 只按已登记且未作废的持久化关联行派生；"
                + $"未开票金额 = 订单金额 − 已开票金额（下限 0）= {remaining} {currency}。");
        else if (hasEvidence)
            sb.Append("该采购订单没有已登记（有效）发票证据：现有证据都是草稿 / 已作废 / 无效或无法确认，"
                + "均不计入已开票金额；这是发票证据缺口，不代表未付款、已结清或逾期。");
        else
            sb.Append(PurchaseOrderInvoiceEvidenceSemantics.NoEvidenceNote);

        if (!truncated)
        {
            AppendInvoiceBucket(sb, totals, PurchaseOrderInvoiceEvidenceSemantics.BucketDraft, "草稿证据");
            AppendInvoiceBucket(sb, totals, PurchaseOrderInvoiceEvidenceSemantics.BucketVoided, "已作废历史证据");
            AppendAllocationBucket(sb, totals, PurchaseOrderInvoiceEvidenceSemantics.BucketInvalid,
                "无效历史证据（供应商 / 币种或关联行快照不一致）");
            AppendAllocationBucket(sb, totals, PurchaseOrderInvoiceEvidenceSemantics.BucketUnavailable,
                "无法确认的证据（发票或采购订单已删除）");
        }

        sb.Append(" 以上金额一律为采购发票证据口径：不是应付余额、不是付款授权或付款状态、不是税务申报判断，"
            + "也不是结算状态或账龄；不得据此付款或判定逾期。");
        return sb.ToString();
    }

    /// <summary>按发票张数点名的分桶说明（草稿 / 已作废）</summary>
    private static void AppendInvoiceBucket(StringBuilder sb, Dictionary<string, BucketTotals> totals,
        string bucket, string label)
    {
        var item = totals[bucket];
        if (item.RowCount == 0) return;
        sb.Append($" 另有{label} {item.InvoiceIds.Count} 张发票（{item.RowCount} 条关联行，金额 {item.Amount}）："
            + "不计入已开票金额，可在明细中单独查看。");
    }

    /// <summary>按关联行条数点名的分桶说明（无效 / 无法确认：发票身份可能都不可用，因此不承诺发票张数）</summary>
    private static void AppendAllocationBucket(StringBuilder sb, Dictionary<string, BucketTotals> totals,
        string bucket, string label)
    {
        var item = totals[bucket];
        if (item.RowCount == 0) return;
        sb.Append($" 另有{label} {item.RowCount} 条关联行（金额 {item.Amount}）："
            + "不计入已开票金额，也不折算 / 合并 / 改派到其他订单，可在明细中单独查看原因。");
    }
}
