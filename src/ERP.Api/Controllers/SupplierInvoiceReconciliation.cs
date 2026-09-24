using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商采购发票对账报表口径常量（ERP-044）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免各处自行拼写导致「未关联金额」被当成应付余额、或把未知金额当作 0 / 已付款。
/// <para>定位：**运营性的采购发票对账视图（只读派生）** —— 它<strong>不是</strong>发票口径的应付账款台账、
/// <strong>不是</strong>付款授权、<strong>不是</strong>税务申报报表，也<strong>不是</strong>账龄表。</para>
/// </summary>
public static class SupplierInvoiceReconciliationSemantics
{
    // ==================== 1. 证据状态筛选取值 ====================

    /// <summary>证据状态筛选：仅已登记且未作废（默认；= 已登记证据）</summary>
    public const string EvidenceRecorded = "recorded";

    /// <summary>证据状态筛选：仅草稿（工作数据，尚未形成登记证据）</summary>
    public const string EvidenceDraft = "draft";

    /// <summary>证据状态筛选：仅已作废（历史证据，仅用于历史核对，绝不并入有效合计）</summary>
    public const string EvidenceVoided = "voided";

    /// <summary>证据状态筛选：全部（草稿 + 已登记 + 已作废；已作废金额仍单独成列，不并入有效合计）</summary>
    public const string EvidenceAll = "all";

    /// <summary>支持的证据状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedEvidenceStatuses =
        { EvidenceRecorded, EvidenceDraft, EvidenceVoided, EvidenceAll };

    // ==================== 2. 订单发票覆盖状态 ====================

    /// <summary>订单已开票金额已达到订单金额（全额覆盖）</summary>
    public const string CoverageFullyInvoiced = "fully_invoiced";

    /// <summary>订单已开票金额大于 0 但小于订单金额（部分覆盖）</summary>
    public const string CoveragePartiallyInvoiced = "partially_invoiced";

    /// <summary>该订单没有任何有效发票关联行（尚未开票）</summary>
    public const string CoverageNotInvoiced = "not_invoiced";

    /// <summary>订单金额未知（订单已删除 / 不存在）或订单已取消：覆盖状态无法判定，按未知显示（不当作未开票）</summary>
    public const string CoverageUnknown = "unknown";

    // ==================== 3. 关联订单的可用性状态 ====================

    /// <summary>订单可用（存在且未删除；状态原样回显）</summary>
    public const string OrderStateAvailable = "available";

    /// <summary>订单已取消（金额仅作历史参考，不参与未开票余额与覆盖状态判定）</summary>
    public const string OrderStateCancelled = "cancelled";

    /// <summary>订单不存在或已删除（订单金额未知，绝不按 0 处理；关联快照仍可读）</summary>
    public const string OrderStateUnavailable = "unavailable";

    // ==================== 4. 收货（数量）状态 ====================

    /// <summary>收货数量未知（本次派生命中批量上限，不完整性无法归因到具体订单）</summary>
    public const string ReceiptUnknown = "unknown";

    // ==================== 5. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（分组、金额来源、未知处理）</summary>
    public const string RuleText =
        "本报表是采购发票对账的运营视图（只读派生）：分组 = 供应商 + 币种，不同币种分别成行、绝不合并为一个金额、也不做汇率换算；" +
        "采购订单金额取订单已落库总额；订单已开票金额只按 ERP-043 的**持久化关联行**派生，且只统计**未作废且未删除**发票的关联行" +
        "（作废发票的关联行单独成列，绝不并入有效合计）；未开票余额 = 订单金额 − 已开票金额（下限 0），只在订单金额已知且订单未取消时计算；" +
        "发票的未关联金额（部分关联 / 未关联）只按发票自身持久化关联行派生、单独成列，系统绝不按单号、金额或日期相似度把它归到任何采购订单；" +
        "收货数量与结算金额复用「采购订单执行进度（ERP-026）」的同一套权威口径（已审核入库单 / 付款单 → 货款申请单 → 归属销售订单），" +
        "链接不唯一、无可用引用或命中批量上限时一律显示「未知」（null），不用 0 顶替，也不代表已付款或已结清。";

    /// <summary>范围说明：合计与计数只统计本次返回页的发票</summary>
    public const string ScopeNoteText =
        "以下分组汇总与计数只统计本次返回页的发票；total 为符合筛选条件的未删除发票总数（证据状态筛选默认为「已登记」）；" +
        "分页按供应商 + 币种 + 开票日期（倒序）+ 发票 Id（倒序）稳定排序；订单侧金额（已开票金额 / 未开票余额）按订单全部有效发票派生，与本页筛选无关。";

    /// <summary>与应付账款台账 / 付款授权 / 税务申报 / 账龄表的边界说明（界面与文档同源）</summary>
    public const string LedgerBoundaryText =
        "本报表是**运营性的采购发票对账视图**：不是发票口径的应付账款台账、不是付款授权、不是税务申报报表，也不是账龄表 —— " +
        "不推算账期与到期日、不做账龄分摊、不做进项认证与税负计算、不判断是否已付款，也不产生任何记账、凭证、收付款或结算单；" +
        "「未关联金额」只是发票尚未按持久化关联行归属到采购订单的金额，「未开票余额」只是订单尚未被有效发票证据覆盖的金额，" +
        "两者都不得当作应付余额、不得据以付款，也不代表欠款已逾期。";

    /// <summary>关联状态是否属于「未关联 / 部分关联」（未关联金额不得猜测到任何订单）</summary>
    public static bool IsUnlinked(string linkageStatus)
        => linkageStatus is PurchaseInvoiceRules.LinkagePartial or PurchaseInvoiceRules.LinkageUnlinked;

    /// <summary>是否已作废证据（已作废金额绝不并入有效合计）</summary>
    public static bool IsVoidedEvidence(int invoiceStatus)
        => invoiceStatus == PurchaseInvoiceRules.StatusVoided;

    /// <summary>证据状态筛选 → 中文文案</summary>
    public static string EvidenceStatusText(string? evidenceStatus) => evidenceStatus switch
    {
        EvidenceDraft => "仅草稿（工作数据，尚未形成登记证据）",
        EvidenceVoided => "仅已作废历史证据",
        EvidenceAll => "全部状态（草稿 + 已登记 + 已作废）",
        _ => "仅已登记证据（默认：已登记且未作废）"
    };

    /// <summary>订单发票覆盖状态 → 中文文案（未知不当作未开票）</summary>
    public static string CoverageText(string coverageStatus) => coverageStatus switch
    {
        CoverageFullyInvoiced => "已全额开票（有效发票已覆盖订单金额）",
        CoveragePartiallyInvoiced => "部分开票（有效发票只覆盖部分订单金额）",
        CoverageNotInvoiced => "未开票（该订单没有任何有效发票关联行）",
        _ => "未知（订单金额未知或订单已取消，无法判定覆盖状态）"
    };
}

/// <summary>
/// 供应商采购发票对账报表查询条件（ERP-044；全部为只读筛选参数，非法取值直接拒绝而不静默忽略）。
/// <para>订单日期筛选依据 ERP-043 关联行中持久化的**订单日期快照**：因此「只看某段订单日期」只会返回
/// 已关联（至少一条关联行）的发票，未关联发票没有订单日期可筛，属口径内行为。</para>
/// </summary>
public sealed class SupplierInvoiceReconciliationQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多发票）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 50;

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>币种筛选（CNY / USD / EUR / HKD / GBP / JPY；留空 = 全部币种，不同币种分别汇总）</summary>
    public string? Currency { get; set; }

    /// <summary>订单日期开始（含当天；依据关联行的订单日期快照；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；依据关联行的订单日期快照；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>开票日期开始（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateFrom { get; set; }

    /// <summary>开票日期结束（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateTo { get; set; }

    /// <summary>关联状态筛选（linked 已全额关联 / partial 部分关联 / unlinked 未关联；留空 = 全部）</summary>
    public string? LinkageStatus { get; set; }

    /// <summary>证据状态筛选（recorded 默认 / draft / voided / all；已作废金额永不并入有效合计）</summary>
    public string? EvidenceStatus { get; set; }

    /// <summary>关键字（匹配发票号码 / 代码 / 供应商编码与名称 / 关联行的采购单号快照；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// 归一化并校验：币种 / 关联状态 / 证据状态必须是既定取值，日期区间不得倒置，关键字长度有界，
    /// 分页参数钳制到有界范围；非法取值直接抛业务异常（参数错误），不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        if (SupplierId is <= 0) SupplierId = null;

        Currency = string.IsNullOrWhiteSpace(Currency)
            ? null
            : PurchaseInvoiceRules.NormalizeCurrencyStrict(Currency);

        LinkageStatus = PurchaseInvoiceRules.NormalizeLinkageFilter(LinkageStatus);

        var evidence = (EvidenceStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (evidence.Length == 0) evidence = SupplierInvoiceReconciliationSemantics.EvidenceRecorded;
        if (!SupplierInvoiceReconciliationSemantics.SupportedEvidenceStatuses.Contains(evidence, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"证据状态无效：{EvidenceStatus}（应为 recorded / draft / voided / all）");
        }
        EvidenceStatus = evidence;

        OrderDateFrom = OrderDateFrom?.Date;
        OrderDateTo = OrderDateTo?.Date;
        if (OrderDateFrom.HasValue && OrderDateTo.HasValue && OrderDateFrom > OrderDateTo)
        {
            throw BusinessException.InvalidParameter(
                $"订单日期开始 {OrderDateFrom:yyyy-MM-dd} 不能晚于结束 {OrderDateTo:yyyy-MM-dd}");
        }

        InvoiceDateFrom = InvoiceDateFrom?.Date;
        InvoiceDateTo = InvoiceDateTo?.Date;
        if (InvoiceDateFrom.HasValue && InvoiceDateTo.HasValue && InvoiceDateFrom > InvoiceDateTo)
        {
            throw BusinessException.InvalidParameter(
                $"开票日期开始 {InvoiceDateFrom:yyyy-MM-dd} 不能晚于结束 {InvoiceDateTo:yyyy-MM-dd}");
        }

        if (Keyword is not null)
        {
            Keyword = Keyword.Trim();
            if (Keyword.Length == 0) Keyword = null;
            else if (Keyword.Length > MaxKeywordLength)
                throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        }

        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
    }
}

/// <summary>
/// 发票关联到的采购订单行（ERP-044，只读派生）。
/// <para>订单字段优先取**采购订单当前持久化值**（订单金额），订单快照字段（单号 / 日期 / 币种 / 供应商）取 ERP-043 关联行；
/// 订单被删除或取消时不推断金额：无法判定的金额一律为 null（未知），绝不按 0、已付款或已结清处理。</para>
/// </summary>
public sealed class SupplierInvoiceReconciliationOrderLine
{
    /// <summary>采购订单 Id（关联行持久化值）</summary>
    public long PurchaseOrderId { get; init; }

    /// <summary>采购单号（关联行快照：订单改名后仍按登记当时口径可读）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单日期（关联行快照）</summary>
    public DateTime OrderDate { get; init; }

    /// <summary>订单币种（关联行快照；与发票币种一致是 ERP-043 的关联前提，绝不换算）</summary>
    public string OrderCurrency { get; init; } = string.Empty;

    /// <summary>订单可用性状态：available / cancelled / unavailable</summary>
    public string OrderState { get; init; } = SupplierInvoiceReconciliationSemantics.OrderStateUnavailable;

    /// <summary>订单可用性文案（已取消 / 已删除时照实说明，历史关联仍可读）</summary>
    public string OrderStateText { get; init; } = string.Empty;

    /// <summary>订单当前单据状态文案（订单不存在 / 已删除时照实说明）</summary>
    public string OrderStatusText { get; init; } = string.Empty;

    /// <summary>订单金额（订单已落库总额）；null = 未知（订单不存在 / 已删除），不等于 0</summary>
    public decimal? OrderedAmount { get; init; }

    /// <summary>本发票关联到该订单的金额（本行持久化关联行金额，原币）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>该订单全部**有效（未作废且未删除）**发票的已关联金额合计（与本页筛选无关）</summary>
    public decimal InvoicedAmount { get; init; }

    /// <summary>该订单来自**已作废**发票的已关联金额合计（历史参考，绝不并入 <see cref="InvoicedAmount"/> 或任何有效合计）</summary>
    public decimal VoidedAllocatedAmount { get; init; }

    /// <summary>未开票余额 = 订单金额 − 已开票金额（下限 0）；null = 未知（订单金额未知或订单已取消）</summary>
    public decimal? RemainingUninvoicedAmount { get; init; }

    /// <summary>订单发票覆盖状态：fully_invoiced / partially_invoiced / not_invoiced / unknown</summary>
    public string CoverageStatus { get; init; } = SupplierInvoiceReconciliationSemantics.CoverageUnknown;

    /// <summary>订单发票覆盖状态文案</summary>
    public string CoverageText { get; init; } = SupplierInvoiceReconciliationSemantics.CoverageText(
        SupplierInvoiceReconciliationSemantics.CoverageUnknown);

    /// <summary>该订单的有效发票张数（未作废且未删除；与本页筛选无关）</summary>
    public int InvoiceCount { get; init; }

    /// <summary>该订单的持久化关联行条数（含已作废发票的关联行）</summary>
    public int AllocationCount { get; init; }

    // ============ 付款引用证据（ERP-050：只按 ERP-049 持久化引用行派生；与订单金额 / 已开票金额严格分列） ============

    /// <summary>
    /// 付款引用证据金额：该订单**有效（未作废）**付款引用行的合计（原币）；
    /// null = 未知（命中批量派生上限或订单不参与本次聚合）。
    /// <para>它只表示「付款单金额指向了本订单多少」的引用证据，<strong>不是</strong>银行付款金额、应付余额、
    /// 发票核销或结算结果，也不代表已付款 / 未付款 / 逾期。</para>
    /// </summary>
    public decimal? PaymentReferenceAmount { get; init; }

    /// <summary>付款引用证据的有效引用行条数；null = 未知</summary>
    public int? PaymentAllocationCount { get; init; }

    /// <summary>付款引用证据涉及的付款单张数（按付款单去重）；null = 未知</summary>
    public int? PaymentCount { get; init; }

    /// <summary>
    /// 参与证据的付款单金额中**未指向本订单**的部分（可能指向其他采购订单）；
    /// null = 未知。它不是银行未付金额、不是应付余额，也不是发票余额。
    /// </summary>
    public decimal? PaymentUnallocatedAmount { get; init; }

    /// <summary>已作废付款引用行条数（历史证据，仅可查看，绝不并入有效合计）；null = 未知</summary>
    public int? VoidedPaymentAllocationCount { get; init; }

    /// <summary>无效付款引用行条数（供应商 / 币种或快照不一致：不换算、不合并、不改派）；null = 未知</summary>
    public int? InvalidPaymentAllocationCount { get; init; }

    /// <summary>无法确认的付款引用行条数（付款单已删除 / 不存在）；null = 未知</summary>
    public int? UnavailablePaymentAllocationCount { get; init; }

    /// <summary>付款引用证据短标签（有付款引用证据 / 仅有历史无效证据 / 无付款引用证据 / 未知）</summary>
    public string PaymentEvidenceLabel { get; init; } = PurchaseOrderPaymentEvidenceSemantics.LabelUnknown;

    // ============ 收货 / 结算上下文（复用 ERP-026 权威口径；未知一律 null） ============

    /// <summary>收货状态：none / partial / complete / over_received / unknown（未知 = 命中批量派生上限）</summary>
    public string ReceiptStatus { get; init; } = PurchaseOrderProgress.ReceiptNone;

    /// <summary>订单数量合计（订单明细口径）；null = 未知</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>已收数量合计（已审核入库单口径）；null = 未知</summary>
    public decimal? ReceivedQuantity { get; init; }

    /// <summary>未收数量合计；null = 未知（不代表全都未收）</summary>
    public decimal? OutstandingQuantity { get; init; }

    /// <summary>待审核入库数量合计（不计入已收）；null = 未知</summary>
    public decimal? PendingQuantity { get; init; }

    /// <summary>结算引用状态：linked / ambiguous / unavailable（与执行进度、财务核对同一套派生规则）</summary>
    public string SettlementLinkStatus { get; init; } = PurchaseOrderProgress.LinkUnavailable;

    /// <summary>结算引用说明（缺什么、为什么不推断）</summary>
    public string SettlementLinkReason { get; init; } = string.Empty;

    /// <summary>已结算金额（仅引用可用且付款单已审核、币种一致）；null = 未知（不等于 0，也不代表已付款）</summary>
    public decimal? SettledAmount { get; init; }

    /// <summary>未结算金额；null = 未知（不代表全部未结算）</summary>
    public decimal? OutstandingSettlementAmount { get; init; }

    /// <summary>行级说明（已知什么 / 缺什么 / 为什么不作为应付余额）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一行采购发票对账证据（ERP-044，只读派生；分页与分组的主单位 = 一张发票）。
/// <para>金额口径：<see cref="GrossAmount"/> 为发票含税总额；<see cref="LinkedAmount"/> 只按该发票的持久化关联行合计，
/// <see cref="UnlinkedAmount"/> = 含税总额 − 已关联金额（下限 0）；两者都只统计**本发票自己的**关联行，
/// 系统绝不按单号 / 金额 / 日期相似度把未关联金额归到任何采购订单。</para>
/// </summary>
public sealed class SupplierInvoiceReconciliationInvoice
{
    public long InvoiceId { get; init; }

    /// <summary>发票类型（普票 / 专票）</summary>
    public string InvoiceType { get; init; } = string.Empty;

    /// <summary>发票代码（专票必填；普票可为空）</summary>
    public string InvoiceCode { get; init; } = string.Empty;

    /// <summary>发票号码（供应商发票上的号码）</summary>
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>对外身份文案（类型 + 代码 − 号码口径与唯一性判定一致）</summary>
    public string IdentityText { get; init; } = string.Empty;

    /// <summary>开票日期</summary>
    public DateTime InvoiceDate { get; init; }

    public long SupplierId { get; init; }

    /// <summary>供应商编码快照</summary>
    public string SupplierCode { get; init; } = string.Empty;

    /// <summary>供应商名称快照（登记当时口径）</summary>
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>供应商当前是否可用（存在且未删除且启用）；仅作只读标注</summary>
    public bool SupplierAvailable { get; init; }

    /// <summary>供应商可用性文案（停用 / 删除后照实说明，历史证据仍可读）</summary>
    public string SupplierAvailabilityText { get; init; } = string.Empty;

    /// <summary>币种（原币；不同币种绝不合并）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>币种金额小数位（展示用；不做汇率换算）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>不含税金额（净额，原币）</summary>
    public decimal NetAmount { get; init; }

    /// <summary>税额（原币）</summary>
    public decimal TaxAmount { get; init; }

    /// <summary>含税总额（价税合计，原币）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>发票状态（0 草稿 / 1 已登记 / 2 已作废）</summary>
    public int Status { get; init; }

    /// <summary>状态文案（草稿 / 已登记 / 已作废）</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>是否已作废（作废金额永不并入有效合计，仅历史核对）</summary>
    public bool IsVoided { get; init; }

    /// <summary>证据口径文案（已作废时明确说明不计入有效合计）</summary>
    public string EvidenceText { get; init; } = string.Empty;

    /// <summary>登记时间（未登记为空）</summary>
    public DateTime? RecordedAt { get; init; }

    /// <summary>作废时间（未作废为空）</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因（作废保留历史，必须留痕）</summary>
    public string VoidReason { get; init; } = string.Empty;

    public string Remark { get; init; } = string.Empty;

    /// <summary>本发票的持久化关联行条数</summary>
    public int AllocationCount { get; init; }

    /// <summary>已关联金额（= 本发票关联行金额合计，原币）</summary>
    public decimal LinkedAmount { get; init; }

    /// <summary>未关联金额（= 含税总额 − 已关联金额，下限 0；绝不被猜测到任何订单）</summary>
    public decimal UnlinkedAmount { get; init; }

    /// <summary>关联状态：linked 已全额关联 / partial 部分关联 / unlinked 未关联</summary>
    public string LinkageStatus { get; init; } = PurchaseInvoiceRules.LinkageUnlinked;

    /// <summary>关联状态文案（显式说明已关联 / 未关联金额）</summary>
    public string LinkageText { get; init; } = string.Empty;

    /// <summary>本发票关联到的采购订单行（只来自持久化关联行；未关联时为空）</summary>
    public List<SupplierInvoiceReconciliationOrderLine> Orders { get; init; } = new();

    /// <summary>行级说明（未关联 / 部分关联 / 已作废证据的口径说明）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一个「供应商 + 币种」对账分组（ERP-044；只有同分组才能安全汇总金额，不同币种绝不合并）。
/// <para>发票侧金额（含税 / 已关联 / 未关联）只统计本页**有效（未作废）**入选发票；
/// 已作废发票金额单独成列（<see cref="VoidedGrossAmount"/>），绝不并入有效合计。
/// 订单侧金额（已开票 / 未开票余额）按订单的**全部有效发票**派生，与本页筛选无关，因此不随发票日期筛选变化。</para>
/// </summary>
public sealed class SupplierInvoiceReconciliationGroup
{
    public long SupplierId { get; init; }

    /// <summary>供应商名称（供应商资料缺失或已删除时留空，不臆造）</summary>
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本分组的有效（未作废）发票张数</summary>
    public int InvoiceCount { get; init; }

    /// <summary>本分组有效发票含税总额合计（同供应商 + 同币种，可安全汇总）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本分组有效发票已关联金额合计</summary>
    public decimal LinkedAmount { get; init; }

    /// <summary>本分组有效发票未关联金额合计（仅单列，不代表应付余额）</summary>
    public decimal UnlinkedAmount { get; init; }

    /// <summary>本分组入选的已作废发票张数（仅证据状态筛选包含已作废时非 0）</summary>
    public int VoidedInvoiceCount { get; init; }

    /// <summary>本分组入选的已作废发票含税总额（历史参考，绝不并入有效合计）</summary>
    public decimal VoidedGrossAmount { get; init; }

    /// <summary>本分组关联到的采购订单张数（去重）</summary>
    public int OrderCount { get; init; }

    /// <summary>参与对账的订单金额合计（订单存在、未删除、未取消）</summary>
    public decimal OrderedAmount { get; init; }

    /// <summary>参与对账订单的已开票金额合计（仅未作废发票的关联行，按订单全部有效发票派生）</summary>
    public decimal InvoicedAmount { get; init; }

    /// <summary>参与对账订单的未开票余额合计（= 订单金额 − 已开票金额，逐单下限 0）</summary>
    public decimal RemainingUninvoicedAmount { get; init; }

    /// <summary>订单金额未知的关联订单张数（订单已删除 / 不存在：金额未知，不按 0 处理，也不计入合计）</summary>
    public int UnknownOrderCount { get; init; }

    /// <summary>已取消的关联订单张数（金额仅作历史参考，不参与未开票余额合计）</summary>
    public int CancelledOrderCount { get; init; }

    /// <summary>已全额开票的订单张数</summary>
    public int FullyInvoicedOrderCount { get; init; }

    /// <summary>部分开票的订单张数</summary>
    public int PartiallyInvoicedOrderCount { get; init; }

    /// <summary>未开票的订单张数（该订单没有任何有效发票关联行）</summary>
    public int NotInvoicedOrderCount { get; init; }

    /// <summary>本分组来自已作废发票的订单已关联金额合计（历史参考，绝不并入 <see cref="InvoicedAmount"/>）</summary>
    public decimal VoidedOrderAllocatedAmount { get; init; }

    // ============ 付款引用证据（ERP-050：与订单金额 / 已开票金额 / 未开票余额严格分列） ============

    /// <summary>本分组参与对账订单的付款引用证据金额合计（只按有效引用行派生，原币；不是银行付款金额或应付余额）</summary>
    public decimal PaymentReferenceAmount { get; init; }

    /// <summary>本分组有付款引用证据的订单张数（有效已引用金额 &gt; 0，去重）</summary>
    public int PaymentReferenceOrderCount { get; init; }

    /// <summary>本分组没有付款引用证据的订单张数（证据缺口，不代表未付款 / 已付款）</summary>
    public int NoPaymentReferenceOrderCount { get; init; }

    /// <summary>本分组付款引用证据未知的订单张数（命中批量派生上限）</summary>
    public int UnknownPaymentReferenceOrderCount { get; init; }

    /// <summary>本分组已作废 / 无效 / 无法确认的付款引用行条数合计（历史证据，绝不并入有效合计）</summary>
    public int HistoricalPaymentAllocationCount { get; init; }

    /// <summary>本分组收货数量未知的订单张数（命中批量派生上限）</summary>
    public int ReceiptUnknownCount { get; init; }

    /// <summary>本分组的发票行（与本页顺序一致）</summary>
    public List<SupplierInvoiceReconciliationInvoice> Invoices { get; init; } = new();
}

/// <summary>本页按币种的对账汇总（ERP-044）：金额只在同一币种内汇总，不同币种分别成行，绝不折算成一个总额。</summary>
public sealed class SupplierInvoiceReconciliationCurrencySummary
{
    /// <summary>币种（枚举名）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>本币种涉及的供应商数</summary>
    public int SupplierCount { get; init; }

    /// <summary>本币种有效发票张数</summary>
    public int InvoiceCount { get; init; }

    /// <summary>本币种有效发票含税总额合计</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本币种有效发票已关联金额合计</summary>
    public decimal LinkedAmount { get; init; }

    /// <summary>本币种有效发票未关联金额合计</summary>
    public decimal UnlinkedAmount { get; init; }

    /// <summary>本币种入选的已作废发票张数</summary>
    public int VoidedInvoiceCount { get; init; }

    /// <summary>本币种入选的已作废发票含税总额（历史参考）</summary>
    public decimal VoidedGrossAmount { get; init; }

    /// <summary>本币种关联订单张数（去重）</summary>
    public int OrderCount { get; init; }

    /// <summary>本币种参与对账订单金额合计</summary>
    public decimal OrderedAmount { get; init; }

    /// <summary>本币种参与对账订单的已开票金额合计</summary>
    public decimal InvoicedAmount { get; init; }

    /// <summary>本币种参与对账订单的未开票余额合计</summary>
    public decimal RemainingUninvoicedAmount { get; init; }

    // ============ 付款引用证据（ERP-050：与订单金额 / 已开票金额 / 未开票余额严格分列） ============

    /// <summary>本币种参与对账订单的付款引用证据金额合计（只按有效引用行派生；不是银行付款金额或应付余额）</summary>
    public decimal PaymentReferenceAmount { get; init; }

    /// <summary>本币种有付款引用证据的订单张数（有效已引用金额 &gt; 0，去重）</summary>
    public int PaymentReferenceOrderCount { get; init; }

    /// <summary>本币种没有付款引用证据的订单张数（证据缺口，不代表未付款 / 已付款 / 逾期）</summary>
    public int NoPaymentReferenceOrderCount { get; init; }

    /// <summary>本币种付款引用证据未知的订单张数（命中批量派生上限）</summary>
    public int UnknownPaymentReferenceOrderCount { get; init; }

    /// <summary>本币种已作废 / 无效 / 无法确认的付款引用行条数合计（历史证据，绝不并入有效合计）</summary>
    public int HistoricalPaymentAllocationCount { get; init; }
}

/// <summary>
/// 供应商采购发票对账报表（ERP-044，只读派生；不落库、不改发票 / 采购订单 / 库存 / 财务记录、不新增或修改任何表列）。
/// </summary>
public sealed class SupplierInvoiceReconciliationReport
{
    // ============ 筛选回显（归一化后的实际取值） ============
    public long? SupplierId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateTime? OrderDateFrom { get; init; }
    public DateTime? OrderDateTo { get; init; }
    public DateTime? InvoiceDateFrom { get; init; }
    public DateTime? InvoiceDateTo { get; init; }
    public string LinkageStatus { get; init; } = string.Empty;
    public string EvidenceStatus { get; init; } = SupplierInvoiceReconciliationSemantics.EvidenceRecorded;
    public string EvidenceStatusText { get; init; } = string.Empty;
    public string Keyword { get; init; } = string.Empty;

    // ============ 分页 ============
    /// <summary>符合筛选条件的未删除发票总数</summary>
    public int Total { get; init; }

    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>本页发票张数</summary>
    public int PageInvoiceCount { get; init; }

    /// <summary>本页已全额关联的发票张数</summary>
    public int LinkedInvoiceCount { get; init; }

    /// <summary>本页部分关联的发票张数（未关联金额单列，不猜测订单）</summary>
    public int PartialInvoiceCount { get; init; }

    /// <summary>本页未关联的发票张数（含税总额整笔未关联）</summary>
    public int UnlinkedInvoiceCount { get; init; }

    /// <summary>本页已作废的发票张数（仅证据状态筛选包含已作废时非 0）</summary>
    public int VoidedInvoiceCount { get; init; }

    /// <summary>本页涉及的采购订单张数（去重）</summary>
    public int OrderCount { get; init; }

    /// <summary>本页订单金额未知的采购订单张数（订单已删除 / 不存在）</summary>
    public int UnknownOrderCount { get; init; }

    /// <summary>本页收货数量未知的采购订单张数（命中批量派生上限）</summary>
    public int ReceiptUnknownCount { get; init; }

    /// <summary>本页按币种汇总（同一币种才汇总；不存在跨币种总额）</summary>
    public List<SupplierInvoiceReconciliationCurrencySummary> Currencies { get; init; } = new();

    /// <summary>本页「供应商 + 币种」分组</summary>
    public List<SupplierInvoiceReconciliationGroup> Groups { get; init; } = new();

    /// <summary>口径说明（与后端派生同源；界面原样展示）</summary>
    public string Rule { get; init; } = SupplierInvoiceReconciliationSemantics.RuleText;

    /// <summary>范围说明（合计只统计本页）</summary>
    public string ScopeNote { get; init; } = SupplierInvoiceReconciliationSemantics.ScopeNoteText;

    /// <summary>与应付账款台账 / 付款授权 / 税务申报 / 账龄表的边界说明（界面原样展示）</summary>
    public string LedgerBoundary { get; init; } = SupplierInvoiceReconciliationSemantics.LedgerBoundaryText;

    /// <summary>发票金额等式口径（与 ERP-043 同源）</summary>
    public string AmountEquation { get; init; } = PurchaseInvoiceRules.AmountEquationText;

    /// <summary>发票关联口径（与 ERP-043 同源）</summary>
    public string LinkageRule { get; init; } = PurchaseInvoiceRules.LinkageRuleText;

    // ============ 付款引用证据（ERP-050） ============

    /// <summary>本页有付款引用证据的订单张数（有效已引用金额 &gt; 0，去重）</summary>
    public int PaymentReferenceOrderCount { get; init; }

    /// <summary>本页没有付款引用证据的订单张数（证据缺口，不代表未付款 / 已付款 / 已结清 / 逾期）</summary>
    public int NoPaymentReferenceOrderCount { get; init; }

    /// <summary>本页付款引用证据未知的订单张数（命中批量派生上限：金额与计数按未知显示）</summary>
    public int UnknownPaymentReferenceOrderCount { get; init; }

    /// <summary>付款引用证据口径说明（与 ERP-050 同源；界面原样展示）</summary>
    public string PaymentEvidenceRule { get; init; } = PurchaseOrderPaymentEvidenceSemantics.RuleText;

    /// <summary>付款引用证据边界说明（与 ERP-050 同源；界面原样展示）</summary>
    public string PaymentEvidenceBoundary { get; init; } = PurchaseOrderPaymentEvidenceSemantics.BoundaryText;
}

/// <summary>
/// 供应商采购发票对账报表派生（ERP-044，只读）。
/// <para>范围：未删除的供应商发票（证据状态筛选默认为「已登记且未作废」），按「供应商 + 币种」分组；
/// 发票金额取发票已落库金额，订单金额取采购订单已落库总额，**不重算、不改单据**。</para>
/// <para>关联一律复用 ERP-043 的**持久化关联行**：本报表不引入第二套匹配算法、不按单号 / 金额 / 日期相似度猜测归属；
/// 收货与结算上下文复用 <see cref="PurchaseOrderProgress"/> 的同一套权威口径（ERP-026），未知一律为 null。</para>
/// <para>查询有界：固定次数数据集访问（计数 + 分页 Id + 本页发票 + 本页关联行 + 本页订单 + 订单关联行 +
/// 关联发票状态 + 供应商名 + 收货 2 次 + 结算 3 次），与页大小 / 行数无关；全程只读，不写库、不落库。</para>
/// </summary>
public static class SupplierInvoiceReconciliation
{
    /// <summary>派生分页对账报表（筛选 → 分页 → 批量派生，全程无逐行查询）</summary>
    public static async Task<SupplierInvoiceReconciliationReport> ForQueryAsync(
        IErpDbContext db, SupplierInvoiceReconciliationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(i => i.SupplierId).ThenBy(i => i.Currency)
            .ThenByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(i => i.Id)
            .ToListAsync();

        var invoices = new List<PurchaseInvoice>();
        if (pageIds.Count > 0)
        {
            // 分页已定：只为本页发票加载实体（避免对全部匹配发票做无界装载），再按分页顺序还原
            var loaded = await db.PurchaseInvoices.AsNoTracking()
                .Where(i => pageIds.Contains(i.Id))
                .ToListAsync();
            var byId = loaded.ToDictionary(i => i.Id);
            invoices = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var items = await BuildInvoicesAsync(db, invoices);
        var groups = BuildGroups(items);
        var currencies = BuildCurrencySummaries(items);
        var orderLines = items.SelectMany(i => i.Orders).ToList();
        var evidence = query.EvidenceStatus ?? SupplierInvoiceReconciliationSemantics.EvidenceRecorded;

        return new SupplierInvoiceReconciliationReport
        {
            SupplierId = query.SupplierId,
            Currency = query.Currency ?? string.Empty,
            OrderDateFrom = query.OrderDateFrom,
            OrderDateTo = query.OrderDateTo,
            InvoiceDateFrom = query.InvoiceDateFrom,
            InvoiceDateTo = query.InvoiceDateTo,
            LinkageStatus = query.LinkageStatus ?? string.Empty,
            EvidenceStatus = evidence,
            EvidenceStatusText = SupplierInvoiceReconciliationSemantics.EvidenceStatusText(evidence),
            Keyword = query.Keyword ?? string.Empty,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            PageInvoiceCount = items.Count,
            LinkedInvoiceCount = items.Count(i => i.LinkageStatus == PurchaseInvoiceRules.LinkageLinked),
            PartialInvoiceCount = items.Count(i => i.LinkageStatus == PurchaseInvoiceRules.LinkagePartial),
            UnlinkedInvoiceCount = items.Count(i => i.LinkageStatus == PurchaseInvoiceRules.LinkageUnlinked),
            VoidedInvoiceCount = items.Count(i => i.IsVoided),
            OrderCount = orderLines.Select(o => o.PurchaseOrderId).Distinct().Count(),
            UnknownOrderCount = orderLines
                .Where(o => o.OrderState == SupplierInvoiceReconciliationSemantics.OrderStateUnavailable)
                .Select(o => o.PurchaseOrderId).Distinct().Count(),
            ReceiptUnknownCount = orderLines
                .Where(o => o.ReceiptStatus == SupplierInvoiceReconciliationSemantics.ReceiptUnknown)
                .Select(o => o.PurchaseOrderId).Distinct().Count(),
            PaymentReferenceOrderCount = orderLines
                .Where(o => o.PaymentReferenceAmount is > 0m)
                .Select(o => o.PurchaseOrderId).Distinct().Count(),
            NoPaymentReferenceOrderCount = orderLines
                .Where(o => o.PaymentReferenceAmount == 0m)
                .Select(o => o.PurchaseOrderId).Distinct().Count(),
            UnknownPaymentReferenceOrderCount = orderLines
                .Where(o => o.PaymentReferenceAmount is null)
                .Select(o => o.PurchaseOrderId).Distinct().Count(),
            Currencies = currencies,
            Groups = groups,
        };
    }

    /// <summary>
    /// 数据库侧筛选（全部为既有列 / 既有持久化行的派生条件）。
    /// <para>关联状态是派生值，这里用与 ERP-043 台账等价的持久化关联行条件筛选（先派生已关联金额再过滤），
    /// 因此「只看未关联 / 部分关联」不会因分页而漏行；订单日期按关联行的订单日期快照筛选；
    /// 证据状态按发票状态筛选（默认只看已登记，已作废需显式选择）。</para>
    /// </summary>
    private static IQueryable<PurchaseInvoice> ApplyFilters(
        IErpDbContext db, SupplierInvoiceReconciliationQuery query)
    {
        var source = db.PurchaseInvoices.AsNoTracking().Where(i => !i.IsDeleted);

        if (query.SupplierId is { } supplierId) source = source.Where(i => i.SupplierId == supplierId);
        if (query.Currency is { } currency) source = source.Where(i => i.Currency == currency);

        switch (query.EvidenceStatus)
        {
            case SupplierInvoiceReconciliationSemantics.EvidenceDraft:
                source = source.Where(i => i.Status == PurchaseInvoiceRules.StatusDraft);
                break;
            case SupplierInvoiceReconciliationSemantics.EvidenceVoided:
                source = source.Where(i => i.Status == PurchaseInvoiceRules.StatusVoided);
                break;
            case SupplierInvoiceReconciliationSemantics.EvidenceAll:
                break;
            default:
                source = source.Where(i => i.Status == PurchaseInvoiceRules.StatusRecorded);
                break;
        }

        if (query.InvoiceDateFrom is { } invoiceFrom) source = source.Where(i => i.InvoiceDate >= invoiceFrom);
        if (query.InvoiceDateTo is { } invoiceTo) source = source.Where(i => i.InvoiceDate <= invoiceTo);

        if (query.OrderDateFrom is { } orderFrom)
        {
            source = source.Where(i => db.PurchaseInvoiceAllocations
                .Any(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id && a.OrderDate >= orderFrom));
        }

        if (query.OrderDateTo is { } orderTo)
        {
            var orderToExclusive = orderTo.AddDays(1);
            source = source.Where(i => db.PurchaseInvoiceAllocations
                .Any(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id && a.OrderDate < orderToExclusive));
        }

        switch (query.LinkageStatus)
        {
            case PurchaseInvoiceRules.LinkageUnlinked:
                source = source.Where(i => !db.PurchaseInvoiceAllocations
                    .Any(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id && a.AllocatedAmount > 0));
                break;
            case PurchaseInvoiceRules.LinkageLinked:
                source = source.Where(i => db.PurchaseInvoiceAllocations
                        .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id)
                        .Sum(a => (decimal?)a.AllocatedAmount) >= i.GrossAmount);
                break;
            case PurchaseInvoiceRules.LinkagePartial:
                source = source.Where(i => db.PurchaseInvoiceAllocations
                        .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id)
                        .Sum(a => (decimal?)a.AllocatedAmount) > 0
                    && db.PurchaseInvoiceAllocations
                        .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id)
                        .Sum(a => (decimal?)a.AllocatedAmount) < i.GrossAmount);
                break;
        }

        if (query.Keyword is { } keyword)
        {
            source = source.Where(i => i.InvoiceNumber.Contains(keyword)
                || i.InvoiceCode.Contains(keyword)
                || i.SupplierName.Contains(keyword)
                || i.SupplierCode.Contains(keyword)
                || db.PurchaseInvoiceAllocations.Any(a =>
                    !a.IsDeleted && a.PurchaseInvoiceId == i.Id && a.OrderNo.Contains(keyword)));
        }

        return source;
    }

    /// <summary>
    /// 本页发票行派生：本页关联行 / 本页订单 / 订单关联行 / 关联发票状态 / 供应商名各一次批量装载，
    /// 再叠加 ERP-026 的收货与结算批量派生；全部为只读派生，不写库、不落库。
    /// </summary>
    private static async Task<List<SupplierInvoiceReconciliationInvoice>> BuildInvoicesAsync(
        IErpDbContext db, IReadOnlyList<PurchaseInvoice> invoices)
    {
        if (invoices.Count == 0) return new List<SupplierInvoiceReconciliationInvoice>();

        var invoiceIds = invoices.Select(i => i.Id).ToList();

        // 1) 本页发票的持久化关联行（一次批量装载，无逐行查库）
        var allocationRows = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && invoiceIds.Contains(a.PurchaseInvoiceId))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();
        var allocationsByInvoice = allocationRows
            .GroupBy(a => a.PurchaseInvoiceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PurchaseInvoiceAllocation>)g.ToList());

        // 2) 本页关联到的采购订单（一次批量装载；订单已删除时仍按关联行快照显示）
        var orderIds = allocationRows.Select(a => a.PurchaseOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<long, PurchaseOrder>()
            : (await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
                    .Where(o => orderIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id);

        // 3) 订单侧权威聚合：只按持久化关联行派生（有效 = 发票未作废且未删除；作废单独成列）
        var orderAggregates = await BuildOrderAggregatesAsync(db, orderIds);

        // 4) 供应商名（本页发票 + 关联行快照涉及的供应商，一次批量装载；资料缺失时留空，不臆造）
        var supplierIds = invoices.Select(i => i.SupplierId)
            .Concat(allocationRows.Select(a => a.SupplierId))
            .Distinct().ToList();
        var suppliers = (await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id)).ToListAsync())
            .ToDictionary(s => s.Id);

        // 5) 收货 / 结算上下文：复用 ERP-026 的同一套权威批量派生（固定 5 次查询）
        var orderList = orderIds.Where(orders.ContainsKey).Select(id => orders[id]).ToList();
        var receipts = await PurchaseOrderProgress.ReceiptSummariesForOrdersAsync(db, orderList);
        var settlements = await PurchaseOrderProgress.SettlementForOrdersAsync(db, orderList);

        // 6) 付款引用证据（ERP-050）：复用采购订单视图的同一套派生（固定 4 次数据集访问，无逐行查库）
        var paymentEvidence = await PurchaseOrderPaymentEvidence.AggregatesForOrdersAsync(db, orderIds);

        var items = new List<SupplierInvoiceReconciliationInvoice>(invoices.Count);
        foreach (var invoice in invoices)
        {
            var rows = allocationsByInvoice.TryGetValue(invoice.Id, out var found)
                ? found
                : (IReadOnlyList<PurchaseInvoiceAllocation>)new List<PurchaseInvoiceAllocation>();
            items.Add(MapInvoice(invoice, rows, orders, orderAggregates, suppliers, receipts, settlements,
                paymentEvidence));
        }

        return items;
    }

    /// <summary>
    /// 订单侧权威聚合（只读）：某张采购订单的「有效发票已关联金额」与「已作废发票已关联金额」，
    /// 只按持久化关联行派生；关联行指向的发票已软删除或不存在时不并入任何合计（无法确认证据有效性）。
    /// </summary>
    private static async Task<Dictionary<long, OrderInvoiceAggregate>> BuildOrderAggregatesAsync(
        IErpDbContext db, IReadOnlyList<long> orderIds)
    {
        var result = new Dictionary<long, OrderInvoiceAggregate>();
        if (orderIds.Count == 0) return result;

        var rows = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && orderIds.Contains(a.PurchaseOrderId))
            .Select(a => new OrderAllocationRow(a.PurchaseOrderId, a.PurchaseInvoiceId, a.AllocatedAmount))
            .ToListAsync();
        if (rows.Count == 0) return result;

        var invoiceIds = rows.Select(r => r.PurchaseInvoiceId).Distinct().ToList();
        var invoiceStates = (await db.PurchaseInvoices.AsNoTracking()
                .Where(i => invoiceIds.Contains(i.Id))
                .Select(i => new InvoiceStateRow(i.Id, i.Status, i.IsDeleted))
                .ToListAsync())
            .ToDictionary(s => s.InvoiceId);

        foreach (var group in rows.GroupBy(r => r.PurchaseOrderId))
        {
            var activeAmount = 0m;
            var voidedAmount = 0m;
            var activeInvoices = new HashSet<long>();
            var voidedInvoices = new HashSet<long>();
            foreach (var row in group)
            {
                if (!invoiceStates.TryGetValue(row.PurchaseInvoiceId, out var state)) continue;
                if (state.IsDeleted) continue;

                if (state.Status == PurchaseInvoiceRules.StatusVoided)
                {
                    voidedAmount += row.AllocatedAmount;
                    voidedInvoices.Add(row.PurchaseInvoiceId);
                }
                else
                {
                    activeAmount += row.AllocatedAmount;
                    activeInvoices.Add(row.PurchaseInvoiceId);
                }
            }

            result[group.Key] = new OrderInvoiceAggregate(group.Count(), activeInvoices.Count,
                voidedInvoices.Count, activeAmount, voidedAmount);
        }

        return result;
    }

    /// <summary>发票实体 + 持久化关联行 → 对账发票行（纯映射；未关联金额只按本发票关联行派生）</summary>
    private static SupplierInvoiceReconciliationInvoice MapInvoice(
        PurchaseInvoice invoice,
        IReadOnlyList<PurchaseInvoiceAllocation> rows,
        Dictionary<long, PurchaseOrder> orders,
        Dictionary<long, OrderInvoiceAggregate> aggregates,
        Dictionary<long, BaseSupplier> suppliers,
        Dictionary<long, PurchaseOrderReceiptSummary> receipts,
        Dictionary<long, PurchaseOrderSettlementProgress> settlements,
        PaymentEvidenceAggregateSet paymentEvidence)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var linked = rows.Sum(a => a.AllocatedAmount);
        if (linked > invoice.GrossAmount) linked = invoice.GrossAmount;   // 防御：持久化关联行不应超出含税总额
        var unlinked = invoice.GrossAmount - linked;
        if (unlinked < 0) unlinked = 0;
        var linkage = PurchaseInvoiceRules.LinkageStatusOf(invoice.GrossAmount, linked);
        var voided = SupplierInvoiceReconciliationSemantics.IsVoidedEvidence(invoice.Status);

        suppliers.TryGetValue(invoice.SupplierId, out var supplier);

        var orderLines = new List<SupplierInvoiceReconciliationOrderLine>(rows.Count);
        foreach (var row in rows)
        {
            orderLines.Add(MapOrderLine(invoice, row, orders, aggregates, receipts, settlements, paymentEvidence));
        }

        return new SupplierInvoiceReconciliationInvoice
        {
            InvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            IdentityText = PurchaseInvoiceRules.IdentityText(
                invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
            InvoiceDate = invoice.InvoiceDate,
            SupplierId = invoice.SupplierId,
            SupplierCode = invoice.SupplierCode,
            SupplierName = invoice.SupplierName,
            SupplierAvailable = PurchaseInvoiceRules.IsSupplierSelectable(supplier),
            SupplierAvailabilityText = PurchaseInvoiceRules.SupplierAvailabilityText(supplier),
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            NetAmount = invoice.NetAmount,
            TaxAmount = invoice.TaxAmount,
            GrossAmount = invoice.GrossAmount,
            Status = invoice.Status,
            StatusText = PurchaseInvoiceRules.StatusText(invoice.Status),
            IsVoided = voided,
            EvidenceText = voided
                ? "已作废历史证据：仅供历史核对，其金额与关联行不计入任何有效合计"
                : (invoice.Status == PurchaseInvoiceRules.StatusDraft
                    ? "草稿工作数据：尚未登记为证据"
                    : "已登记证据：未作废，参与有效合计"),
            RecordedAt = invoice.RecordedAt,
            VoidedAt = invoice.VoidedAt,
            VoidReason = invoice.VoidReason,
            Remark = invoice.Remark,
            AllocationCount = rows.Count,
            LinkedAmount = linked,
            UnlinkedAmount = unlinked,
            LinkageStatus = linkage,
            LinkageText = PurchaseInvoiceRules.LinkageText(invoice.GrossAmount, linked, rows.Count, currency),
            Orders = orderLines,
            Note = BuildInvoiceNote(linkage, voided),
        };
    }

    /// <summary>关联行 → 订单行（订单金额取订单当前持久化总额；未知一律 null，绝不按 0 处理）</summary>
    private static SupplierInvoiceReconciliationOrderLine MapOrderLine(
        PurchaseInvoice invoice,
        PurchaseInvoiceAllocation row,
        Dictionary<long, PurchaseOrder> orders,
        Dictionary<long, OrderInvoiceAggregate> aggregates,
        Dictionary<long, PurchaseOrderReceiptSummary> receipts,
        Dictionary<long, PurchaseOrderSettlementProgress> settlements,
        PaymentEvidenceAggregateSet paymentEvidence)
    {
        orders.TryGetValue(row.PurchaseOrderId, out var order);
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
                "采购订单已取消：订单金额仅作历史参考，不参与未开票余额与覆盖状态判定",
            SupplierInvoiceReconciliationSemantics.OrderStateAvailable =>
                $"采购订单可用（当前状态 {order!.Status}）",
            _ => "采购订单不存在或已删除：订单金额未知（不按 0 处理），关联快照仍可读",
        };

        aggregates.TryGetValue(row.PurchaseOrderId, out var aggregate);
        var invoicedAmount = aggregate?.ActiveAmount ?? 0m;
        var voidedAllocatedAmount = aggregate?.VoidedAmount ?? 0m;
        var orderedAmount = available ? order!.TotalAmount : (decimal?)null;
        var reconciled = available && !cancelled;
        var remaining = reconciled
            ? Math.Max(0m, orderedAmount.GetValueOrDefault() - invoicedAmount)
            : (decimal?)null;
        var coverage = !reconciled
            ? SupplierInvoiceReconciliationSemantics.CoverageUnknown
            : invoicedAmount <= 0
                ? SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced
                : invoicedAmount < orderedAmount!.Value
                    ? SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced
                    : SupplierInvoiceReconciliationSemantics.CoverageFullyInvoiced;

        receipts.TryGetValue(row.PurchaseOrderId, out var receipt);
        settlements.TryGetValue(row.PurchaseOrderId, out var settlement);

        // 付款引用证据（ERP-050）：只复用采购订单视图的同一套派生结果与标签；无聚合结果 = 未知（不按 0）
        var hasPayment = paymentEvidence.ByOrder.TryGetValue(row.PurchaseOrderId, out var payment);
        // 订单不可用（不存在 / 已删除）时收货与结算上下文无从判定：记未知，绝不用 0 / 未收货顶替
        var receiptUnknown = !available || receipt?.Truncated == true;
        var settlementLinkStatus = settlement?.LinkStatus ?? PurchaseOrderProgress.LinkUnavailable;
        var settlementLinkReason = !available
            ? "采购订单不存在或已删除，无法按既有引用判定结算（金额未知，不推断）"
            : settlement?.LinkReason ?? string.Empty;

        return new SupplierInvoiceReconciliationOrderLine
        {
            PurchaseOrderId = row.PurchaseOrderId,
            OrderNo = row.OrderNo,
            OrderDate = row.OrderDate,
            OrderCurrency = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency),
            OrderState = state,
            OrderStateText = stateText,
            OrderStatusText = available ? order!.Status.ToString() : "不存在或已删除",
            OrderedAmount = orderedAmount,
            AllocatedAmount = row.AllocatedAmount,
            InvoicedAmount = invoicedAmount,
            VoidedAllocatedAmount = voidedAllocatedAmount,
            RemainingUninvoicedAmount = remaining,
            CoverageStatus = coverage,
            CoverageText = SupplierInvoiceReconciliationSemantics.CoverageText(coverage),
            InvoiceCount = aggregate?.ActiveInvoiceCount ?? 0,
            AllocationCount = aggregate?.AllocationCount ?? 1,
            ReceiptStatus = receiptUnknown
                ? SupplierInvoiceReconciliationSemantics.ReceiptUnknown
                : receipt?.ReceiptStatus ?? PurchaseOrderProgress.ReceiptNone,
            OrderedQuantity = receiptUnknown ? null : receipt?.OrderedQuantity,
            ReceivedQuantity = receiptUnknown ? null : receipt?.ReceivedQuantity,
            OutstandingQuantity = receiptUnknown ? null : receipt?.OutstandingQuantity,
            PendingQuantity = receiptUnknown ? null : receipt?.PendingQuantity,
            SettlementLinkStatus = settlementLinkStatus,
            SettlementLinkReason = settlementLinkReason,
            SettledAmount = settlement?.SettledAmount,
            OutstandingSettlementAmount = settlement?.OutstandingAmount,
            PaymentReferenceAmount = hasPayment ? payment!.ActiveAmount : null,
            PaymentAllocationCount = hasPayment ? payment!.ActiveCount : null,
            PaymentCount = hasPayment ? payment!.ActivePaymentCount : null,
            PaymentUnallocatedAmount = hasPayment ? payment!.UnallocatedPaymentAmount : null,
            VoidedPaymentAllocationCount = hasPayment ? payment!.VoidedCount : null,
            InvalidPaymentAllocationCount = hasPayment ? payment!.InvalidCount : null,
            UnavailablePaymentAllocationCount = hasPayment ? payment!.UnavailableCount : null,
            PaymentEvidenceLabel = PurchaseOrderPaymentEvidenceSemantics.EvidenceLabel(
                !hasPayment, hasPayment ? payment!.ActiveAmount : null, hasPayment && payment!.HasAnyRow),
            Note = BuildOrderLineNote(invoice, state, receiptUnknown, aggregate, hasPayment ? payment : null),
        };
    }

    /// <summary>订单侧关联行聚合输入行（只读）</summary>
    private sealed record OrderAllocationRow(long PurchaseOrderId, long PurchaseInvoiceId, decimal AllocatedAmount);

    /// <summary>关联行指向发票的状态（只读；用于区分有效证据与已作废 / 已删除发票）</summary>
    private sealed record InvoiceStateRow(long InvoiceId, int Status, bool IsDeleted);

    /// <summary>订单的发票关联聚合（只读派生值；作废金额与有效金额绝不合并）</summary>
    private sealed record OrderInvoiceAggregate(int AllocationCount, int ActiveInvoiceCount,
        int VoidedInvoiceCount, decimal ActiveAmount, decimal VoidedAmount);

    /// <summary>发票行说明：未关联 / 部分关联的金额只按本发票关联行派生，绝不猜测到任何订单</summary>
    private static string BuildInvoiceNote(string linkageStatus, bool voided)
    {
        var note = linkageStatus switch
        {
            PurchaseInvoiceRules.LinkageLinked =>
                "已全额关联：含税总额已全部按持久化关联行归属到采购订单。",
            PurchaseInvoiceRules.LinkagePartial =>
                "部分关联：未关联金额只作本发票的未关联金额单列，不猜测归属到任何订单（不按单号 / 金额 / 日期相似度匹配）。",
            _ => "未关联：整笔含税总额尚未按持久化关联行归属到任何采购订单，属运营证据缺口（不代表欠款或应付余额）。",
        };

        return voided
            ? note + " 该发票已作废：金额与关联行仅作历史核对，不计入任何有效合计。"
            : note;
    }

    /// <summary>订单行说明：已知什么 / 缺什么 / 为什么不作为应付余额（含三类证据分列说明）</summary>
    private static string BuildOrderLineNote(PurchaseInvoice invoice, string orderState,
        bool receiptUnknown, OrderInvoiceAggregate? aggregate, PaymentEvidenceAggregate? payment)
    {
        var note = orderState switch
        {
            SupplierInvoiceReconciliationSemantics.OrderStateAvailable =>
                "订单金额取采购订单已落库总额；已开票金额只按未作废发票的持久化关联行派生，"
                + "未开票余额 = 订单金额 − 已开票金额（下限 0）；付款引用证据单列，不参与上述计算。",
            SupplierInvoiceReconciliationSemantics.OrderStateCancelled =>
                "订单已取消：订单金额仅作历史参考，未开票余额与覆盖状态按未知显示（不推断、不作为应付余额）。",
            _ => "订单不存在或已删除：订单金额未知（不按 0 处理），已开票金额仍按持久化关联行如实列出，未开票余额为未知。",
        };

        if (aggregate is { VoidedInvoiceCount: > 0 })
        {
            note += $" 另有 {aggregate.VoidedInvoiceCount} 张已作废发票存在关联行，其金额只作历史参考、不计入有效合计。";
        }

        if (receiptUnknown)
        {
            note += " 本次页内入库 / 订单不可用导致收货与结算上下文不完整，相关数量与金额按未知显示（不代表 0 或已付款）。";
        }

        if (SupplierInvoiceReconciliationSemantics.IsVoidedEvidence(invoice.Status))
        {
            note += " 本发票已作废：本行关联金额属历史证据，不计入有效合计。";
        }

        // 付款引用证据（ERP-050）：三类证据（订单金额 / 已开票金额 / 付款引用金额）必须保持分列口径
        if (payment is null)
        {
            note += " 本次页内付款引用证据命中有界聚合上限：付款引用金额与计数按未知显示"
                + "（不代表未付款 / 已付款 / 已结清 / 逾期）。";
        }
        else if (payment.ActiveAmount > 0)
        {
            note += $" 付款引用证据 {payment.ActiveAmount} 只按有效（未作废）付款引用行派生"
                + $"（{payment.ActiveCount} 条引用行、{payment.ActivePaymentCount} 张付款单；"
                + $"参与证据的付款单金额中未指向本订单 {payment.UnallocatedPaymentAmount}，可能指向其他采购订单）："
                + "它不是银行付款金额、不是应付余额或发票余额，也不代表已付款或已结算。";

            if (payment.HasHistoricalRow)
            {
                note += $" 另有已作废 {payment.VoidedCount} 条 / 无效 {payment.InvalidCount} 条 / 无法确认 {payment.UnavailableCount} 条"
                    + "付款引用行，只作历史核对、不计入有效合计。";
            }
        }
        else
        {
            note += " 该订单没有有效付款引用证据（付款引用证据缺口，不代表未付款、已付款、已结清或逾期）。";
        }

        return note;
    }

    /// <summary>按「供应商 + 币种」分组：只有同分组（同供应商同币种）才汇总金额</summary>
    private static List<SupplierInvoiceReconciliationGroup> BuildGroups(
        List<SupplierInvoiceReconciliationInvoice> items)
        => items.GroupBy(i => new { i.SupplierId, i.Currency })
            .OrderBy(g => g.Key.SupplierId).ThenBy(g => g.Key.Currency)
            .Select(g => BuildGroup(g.Key.SupplierId, g.Key.Currency, g.ToList()))
            .ToList();

    /// <summary>单个「供应商 + 币种」分组聚合（有效发票与已作废发票金额分列，绝不合并）</summary>
    private static SupplierInvoiceReconciliationGroup BuildGroup(long supplierId, string currency,
        List<SupplierInvoiceReconciliationInvoice> invoices)
    {
        var active = invoices.Where(i => !i.IsVoided).ToList();
        var voided = invoices.Where(i => i.IsVoided).ToList();

        // 同一订单可能出现在多张发票的关联行上：订单金额 / 已开票金额 / 未开票余额按订单去重后汇总
        var distinctOrders = active.SelectMany(i => i.Orders)
            .GroupBy(o => o.PurchaseOrderId)
            .Select(g => g.First())
            .ToList();
        var reconciled = distinctOrders
            .Where(o => o.OrderState == SupplierInvoiceReconciliationSemantics.OrderStateAvailable)
            .ToList();

        // 作废发票的关联金额按本页全部分组的订单（含已作废发票的关联行）去重后单列，绝不并入有效合计
        var allOrders = invoices.SelectMany(i => i.Orders)
            .GroupBy(o => o.PurchaseOrderId)
            .Select(g => g.First())
            .ToList();

        // 付款引用证据（ERP-050）：只在参与对账的订单（可用、未取消）上汇总金额；
        // 未知（命中批量上限）与「无证据（金额 0）」分别计数，缺失绝不按 0 计入金额，也不代表未付款。
        var paymentKnown = reconciled.Where(o => o.PaymentReferenceAmount is not null).ToList();
        var paymentHistorical = distinctOrders.Sum(o =>
            (o.VoidedPaymentAllocationCount ?? 0) + (o.InvalidPaymentAllocationCount ?? 0)
            + (o.UnavailablePaymentAllocationCount ?? 0));

        return new SupplierInvoiceReconciliationGroup
        {
            SupplierId = supplierId,
            SupplierName = invoices.Select(i => i.SupplierName).FirstOrDefault(n => n.Length > 0) ?? string.Empty,
            Currency = currency,
            InvoiceCount = active.Count,
            GrossAmount = active.Sum(i => i.GrossAmount),
            LinkedAmount = active.Sum(i => i.LinkedAmount),
            UnlinkedAmount = active.Sum(i => i.UnlinkedAmount),
            VoidedInvoiceCount = voided.Count,
            VoidedGrossAmount = voided.Sum(i => i.GrossAmount),
            OrderCount = distinctOrders.Count,
            OrderedAmount = reconciled.Sum(o => o.OrderedAmount ?? 0m),
            InvoicedAmount = reconciled.Sum(o => o.InvoicedAmount),
            RemainingUninvoicedAmount = reconciled.Sum(o => o.RemainingUninvoicedAmount ?? 0m),
            UnknownOrderCount = distinctOrders.Count(o =>
                o.OrderState == SupplierInvoiceReconciliationSemantics.OrderStateUnavailable),
            CancelledOrderCount = distinctOrders.Count(o =>
                o.OrderState == SupplierInvoiceReconciliationSemantics.OrderStateCancelled),
            FullyInvoicedOrderCount = reconciled.Count(o =>
                o.CoverageStatus == SupplierInvoiceReconciliationSemantics.CoverageFullyInvoiced),
            PartiallyInvoicedOrderCount = reconciled.Count(o =>
                o.CoverageStatus == SupplierInvoiceReconciliationSemantics.CoveragePartiallyInvoiced),
            NotInvoicedOrderCount = reconciled.Count(o =>
                o.CoverageStatus == SupplierInvoiceReconciliationSemantics.CoverageNotInvoiced),
            VoidedOrderAllocatedAmount = allOrders.Sum(o => o.VoidedAllocatedAmount),
            PaymentReferenceAmount = paymentKnown.Sum(o => o.PaymentReferenceAmount ?? 0m),
            PaymentReferenceOrderCount = paymentKnown.Count(o => o.PaymentReferenceAmount > 0m),
            NoPaymentReferenceOrderCount = paymentKnown.Count(o => o.PaymentReferenceAmount == 0m),
            UnknownPaymentReferenceOrderCount = distinctOrders.Count(o => o.PaymentReferenceAmount is null),
            HistoricalPaymentAllocationCount = paymentHistorical,
            ReceiptUnknownCount = distinctOrders.Count(o =>
                o.ReceiptStatus == SupplierInvoiceReconciliationSemantics.ReceiptUnknown),
            Invoices = invoices,
        };
    }

    /// <summary>本页按币种汇总：同一币种内汇总，不同币种分别成行（不做汇率换算、不产生跨币种总额）</summary>
    private static List<SupplierInvoiceReconciliationCurrencySummary> BuildCurrencySummaries(
        List<SupplierInvoiceReconciliationInvoice> items)
        => items.GroupBy(i => i.Currency)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var active = g.Where(i => !i.IsVoided).ToList();
                var voided = g.Where(i => i.IsVoided).ToList();
                // 同一订单在页内可能被多张发票关联：按订单去重后再汇总订单侧金额
                var distinctGroupOrders = active.SelectMany(i => i.Orders)
                    .GroupBy(o => o.PurchaseOrderId)
                    .Select(rows => rows.First())
                    .ToList();
                var reconciledOrders = distinctGroupOrders
                    .Where(o => o.OrderState == SupplierInvoiceReconciliationSemantics.OrderStateAvailable)
                    .ToList();
                // 付款引用证据（ERP-050）：金额只在参与对账且证据已知的订单上汇总；未知与无证据分别计数
                var paymentKnownOrders = reconciledOrders.Where(o => o.PaymentReferenceAmount is not null).ToList();

                return new SupplierInvoiceReconciliationCurrencySummary
                {
                    Currency = g.Key,
                    SupplierCount = g.Select(i => i.SupplierId).Distinct().Count(),
                    InvoiceCount = active.Count,
                    GrossAmount = active.Sum(i => i.GrossAmount),
                    LinkedAmount = active.Sum(i => i.LinkedAmount),
                    UnlinkedAmount = active.Sum(i => i.UnlinkedAmount),
                    VoidedInvoiceCount = voided.Count,
                    VoidedGrossAmount = voided.Sum(i => i.GrossAmount),
                    OrderCount = reconciledOrders.Count,
                    OrderedAmount = reconciledOrders.Sum(o => o.OrderedAmount ?? 0m),
                    InvoicedAmount = reconciledOrders.Sum(o => o.InvoicedAmount),
                    RemainingUninvoicedAmount = reconciledOrders.Sum(o => o.RemainingUninvoicedAmount ?? 0m),
                    PaymentReferenceAmount = paymentKnownOrders.Sum(o => o.PaymentReferenceAmount ?? 0m),
                    PaymentReferenceOrderCount = paymentKnownOrders.Count(o => o.PaymentReferenceAmount > 0m),
                    NoPaymentReferenceOrderCount = paymentKnownOrders.Count(o => o.PaymentReferenceAmount == 0m),
                    UnknownPaymentReferenceOrderCount = distinctGroupOrders.Count(o => o.PaymentReferenceAmount is null),
                    HistoricalPaymentAllocationCount = distinctGroupOrders.Sum(o =>
                        (o.VoidedPaymentAllocationCount ?? 0) + (o.InvalidPaymentAllocationCount ?? 0)
                        + (o.UnavailablePaymentAllocationCount ?? 0)),
                };
            })
            .ToList();
}
