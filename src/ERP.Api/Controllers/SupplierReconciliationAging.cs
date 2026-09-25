using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商对账与账龄工作台口径常量（ERP-068）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免「剩余证据」被读成应付余额、把未知到期日当作当天 / 开票日期、把不同币种相加，或把草稿 / 已作废
/// 发票并入有效合计。
/// <para>定位：<b>只读的运营性对账与账龄视图</b> —— 证据只来自 ERP-065 的持久化供应商采购发票
/// （可选到期日与付款条件为**显式**登记证据）与 ERP-066 的持久化「付款单 → 采购发票」引用行
/// （经 ERP-067 同一套分桶与资格判定复用）。</para>
/// <para>它<strong>不是</strong>总账或应付账款余额、<strong>不是</strong>法定供应商对账单、
/// <strong>不是</strong>付款授权与付款执行、<strong>不是</strong>税务申报或抵扣判断、
/// <strong>不是</strong>结算确认，也<strong>不是</strong>任何形式的记账、核销或凭证。</para>
/// </summary>
public static class SupplierReconciliationAgingSemantics
{
    // ==================== 1. 有界上限 ====================

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多发票）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 50;

    // ==================== 2. 账龄分桶（互斥；只对存在显式到期日的发票计算） ====================

    /// <summary>未到期（as-of 日期 ≤ 显式到期日；含恰好到期当天）</summary>
    public const string BucketNotDue = "not_due";

    /// <summary>逾期 1 ~ 30 天（含 30）</summary>
    public const string BucketOverdue1To30 = "overdue_1_30";

    /// <summary>逾期 31 ~ 60 天（含 60）</summary>
    public const string BucketOverdue31To60 = "overdue_31_60";

    /// <summary>逾期 61 ~ 90 天（含 90）</summary>
    public const string BucketOverdue61To90 = "overdue_61_90";

    /// <summary>逾期 90 天以上（&gt; 90）</summary>
    public const string BucketOverdueOver90 = "overdue_over_90";

    /// <summary>支持的分桶取值（按展示顺序；互斥且完整覆盖「有显式到期日」的情形）</summary>
    public static readonly string[] SupportedBuckets =
    {
        BucketNotDue, BucketOverdue1To30, BucketOverdue31To60, BucketOverdue61To90, BucketOverdueOver90,
    };

    /// <summary>
    /// 未知到期日分组的桶名（**是独立分组、不是账龄桶**）：到期日为空时账龄**不计算**，
    /// 不得按开票日期、付款条件、供应商默认账期或历史发票推算。
    /// </summary>
    public const string UnknownDueDateBucket = "unknown_due_date";

    /// <summary>账龄分桶中文文案（接口、界面与文档同源；只陈述证据事实，不陈述欠款或催收结论）</summary>
    public static string BucketText(string bucket) => bucket switch
    {
        BucketNotDue => "未到期（as-of ≤ 显式到期日）",
        BucketOverdue1To30 => "逾期 1 ~ 30 天",
        BucketOverdue31To60 => "逾期 31 ~ 60 天",
        BucketOverdue61To90 => "逾期 61 ~ 90 天",
        BucketOverdueOver90 => "逾期 90 天以上",
        UnknownDueDateBucket => "未知到期日（无显式到期日：不计算账龄，单独成组）",
        _ => "未知分桶",
    };

    /// <summary>是否逾期桶（「未知到期日」不是账龄桶）</summary>
    public static bool IsOverdueBucket(string? bucket)
        => bucket is BucketOverdue1To30 or BucketOverdue31To60 or BucketOverdue61To90 or BucketOverdueOver90;

    // ==================== 3. 分配状态（只按持久化 ERP-066 引用行派生） ====================

    /// <summary>没有任何持久化引用行（既无有效行也无历史行）</summary>
    public const string AllocationNone = "none";

    /// <summary>没有有效引用行，但存在未删除的历史 / 无效引用行（已作废 / 发票失效 / 无效 / 无法确认：单独可见，绝不并入有效合计）</summary>
    public const string AllocationHistoricalOnly = "historical_only";

    /// <summary>有效已分配金额 &gt; 0 且 &lt; 含税总额（部分分配）</summary>
    public const string AllocationPartial = "partial";

    /// <summary>有效已分配金额 ≥ 含税总额（整笔分配）</summary>
    public const string AllocationFull = "full";

    /// <summary>有效已分配金额 &gt; 含税总额（与 ERP-066 源规则矛盾：无效证据，按「未知」显示，不轧成 0 或负数）</summary>
    public const string AllocationOverAllocated = "over_allocated";

    /// <summary>命中系统有界读取上限：聚合结果不完整，分配状态与金额一律按「未知」显示（不给部分合计）</summary>
    public const string AllocationUnknown = "unknown";

    /// <summary>
    /// 支持的**分配状态筛选**取值（全部只用持久化字段即可判定）。
    /// <para>说明：<see cref="AllocationOverAllocated"/> 与 <see cref="AllocationUnknown"/> 只作为**读取时派生的行状态**
    /// 出现在结果中，不提供为筛选（它们需要 ERP-066 的行级资格判定与有界上限判定，不是持久化字段的直接比较）；
    /// 无效 / 无法确认的历史证据仍按行单独可见，绝不被修复、改派或合并。</para>
    /// </summary>
    public static readonly string[] SupportedAllocationFilters =
    {
        AllocationNone, AllocationHistoricalOnly, AllocationPartial, AllocationFull,
    };

    /// <summary>分配状态中文文案（接口、界面与文档同源）</summary>
    public static string AllocationStateText(string state) => state switch
    {
        AllocationNone => "无持久化付款引用行（证据缺口，不代表未付款 / 已付款 / 逾期）",
        AllocationHistoricalOnly => "仅有历史 / 无效引用行（已作废 / 发票失效 / 无效 / 无法确认：单独可见，绝不并入有效合计）",
        AllocationPartial => "部分分配（有效已分配金额小于含税总额，未分配金额单独可见）",
        AllocationFull => "整笔分配（有效已分配金额已覆盖含税总额）",
        AllocationOverAllocated => "无效证据：有效已分配金额超过含税总额（与源规则矛盾，按「未知」显示，绝不轧为 0 或负数）",
        _ => "未知（命中系统有界读取上限：分配证据无法穷尽，不给部分合计）",
    };

    // ==================== 4. 剩余证据状态 ====================

    /// <summary>剩余证据可确认：含税总额 − 有效已分配金额（不裁剪下限，负数即转入无效证据）</summary>
    public const string RemainingKnown = "known";

    /// <summary>剩余证据未知（命中上限）：绝不用 0 顶替</summary>
    public const string RemainingUnknown = "unknown";

    /// <summary>剩余证据与源规则矛盾（有效已分配金额 &gt; 含税总额）：无效证据，不做任何修复</summary>
    public const string RemainingOverAllocated = "over_allocated";

    /// <summary>剩余证据状态中文文案（接口、界面与文档同源）</summary>
    public static string RemainingStateText(string state) => state switch
    {
        RemainingKnown => "可确认（含税总额 − 有效已分配金额）",
        RemainingOverAllocated => "无效证据：有效已分配金额超过含税总额（绝不轧为 0，也不视为已结清）",
        _ => "未知（命中系统有界读取上限：不给部分合计）",
    };

    // ==================== 5. 来源 / 链接可用性 ====================

    /// <summary>来源可用（发票持久化可读；付款引用行指向的付款单与发票都能确认）</summary>
    public const string LinkStateAvailable = "available";

    /// <summary>来源不可用 / 无效（源记录已删除、快照自相矛盾、供应商或币种不一致）：按无效历史证据单列，不修复、不改派</summary>
    public const string LinkStateUnavailable = "unavailable";

    // ==================== 6. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（证据来源、剩余证据、未知处理）</summary>
    public const string RuleText =
        "本工作台是**只读的供应商对账与账龄视图**：证据只来自 ERP-065 的持久化供应商采购发票与 "
        + "ERP-066 的持久化「付款单 → 采购发票」引用行（分桶与资格判定复用 ERP-067 的同一套权威口径，不新增第二套对账引擎）；"
        + "发票含税总额取持久化值，有效已分配金额只统计**未作废且经 ERP-066 资格判定通过**的引用行；"
        + "剩余证据 = 含税总额 − 有效已分配金额（只有两侧都可确认时才给出；超出源规则的情形按「未知 / 无效」显示，**绝不轧为 0**）；"
        + "草稿与已作废发票单独标注并**排除**在有效应付证据合计之外，已作废引用行只在历史列可见；"
        + "系统绝不按单号、金额、日期或文本相似度猜测归属或补链接。";

    /// <summary>账龄口径说明（只对显式到期日计算；桶边界互斥且完整）</summary>
    public const string AgingRuleText =
        "账龄只对**存在显式持久化到期日**的发票计算，并以显式 as-of 日期为准："
        + "未到期（as-of ≤ 到期日）、逾期 1 ~ 30 天、逾期 31 ~ 60 天、逾期 61 ~ 90 天、逾期 90 天以上 —— "
        + "五个桶**互斥且完整**覆盖有到期日的情形（边界取含：30 / 60 / 90 天归入本桶，次日起归入下一桶）；"
        + "到期日为空时账龄**不计算**，发票进入独立的「未知到期日」分组，绝不按开票日期、付款条件、"
        + "供应商默认账期或历史发票推算到期日，也不把未知到期日并入任何账龄桶。";

    /// <summary>币种隔离说明（不同币种分别成行、绝不合并、绝无跨币种总额）</summary>
    public const string NoCrossCurrencyText =
        "币种隔离：所有金额（含税总额、有效已分配、剩余证据、账龄分桶合计）一律按**原币**分别成行，"
        + "不同币种**绝不**相加、合并或换算；本工作台**不提供**任何跨币种总额字段，"
        + "也不使用当前汇率或猜测汇率（只有源证据本身已持久化的权威换算才会被采用）。";

    /// <summary>范围说明（合计只统计本次返回页；有界读取与未知处理）</summary>
    public const string ScopeText =
        "范围：分组汇总、账龄分桶与币种汇总都只统计**本次返回页**的发票；total 为符合筛选条件的未删除发票总数；"
        + "分页按供应商 → 币种 → 到期日（未知在前）→ 开票日期（倒序）→ 发票 Id（倒序）稳定排序，单页最多 200 张。"
        + "读取为固定次数数据集访问（筛选 + 分页 + 本页发票 + 本页供应商 + ERP-066 持久化引用行分桶），与发票张数 / 行数无关，"
        + "绝无逐行查库；命中系统有界上限时金额与计数一律按「未知」显示，不给部分合计。";

    /// <summary>与总账 / 法定对账单 / 付款授权 / 税务申报 / 结算确认的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本工作台是**运营性的只读对账与账龄视图**：显示的是本系统**仓库内的对账证据数字**，"
        + "**不是**总账或应付账款余额、**不是**经审计的法定供应商对账单、**不是**付款授权与付款执行依据、"
        + "**不是**税务（进项）申报与抵扣判断、**不是**结算确认或核销结果 —— "
        + "它不判断是否已付款、是否已结清、是否欠款，不产生任何记账、凭证、收付款或结算单，"
        + "也不得据以付款、开票、认证或进行任何法律意义上的对账确认。";

    /// <summary>未知到期日分组的说明（未知不计算账龄，也不视为当天到期）</summary>
    public const string UnknownDueDateNote =
        "「未知到期日」是独立分组：这些发票没有登记显式到期日，因此账龄**不计算**，"
        + "系统不会按开票日期、付款条件、供应商默认账期或历史发票补一个到期日，"
        + "也不会把它们当作当天到期或已逾期；它们**不参与**任何账龄桶合计。";

    /// <summary>无效 / 无法确认证据的说明（保留可见，绝不修复、改派或合并）</summary>
    public const string InvalidEvidenceNote =
        "无效 / 无法确认的历史证据（引用行指向的付款单或发票已删除、供应商或币种不一致、快照自相矛盾）："
        + "一律**保持可见**并单独成列，金额**绝不**并入有效合计，也**不被修复、改派、重算或合并**；"
        + "缺失的证据按「无」或「未知」显示，绝不当成未付款、已付款、已结清、已核销或已逾期。";

    /// <summary>来源失效时的 fail-closed 说明（打开明细时重新校验授权与来源）</summary>
    public const string SourceFailClosedText =
        "打开发票证据明细时会**重新校验**当前登录身份与既有「角色 → 菜单」模块授权，并重新读取权威来源："
        + "未认证、未获该模块授权、发票不存在或已删除时一律**拒绝**（fail closed），"
        + "既不返回部分证据、也不做任何来源修复或改派。";

    /// <summary>导出说明（导出与屏幕同一筛选、同一币种隔离、同一未知到期日语义）</summary>
    public const string ExportNote =
        "导出与屏幕**完全同源**：使用同一筛选条件、同一分页结果、同一币种隔离与同一「未知到期日」分组语义，"
        + "导出内容**不包含**任何跨币种总额，也不添加任何法律结论（不写「应付」「欠款」「逾期确认」「已对账」等断言）。";

    // ==================== 7. 派生方法（纯计算，不访问数据库） ====================

    /// <summary>是否属于计入有效应付证据合计的发票状态（草稿 / 已作废不算）</summary>
    public static bool IsActiveEvidence(int invoiceStatus)
        => invoiceStatus == PurchaseInvoiceRules.StatusRecorded;

    /// <summary>
    /// 账龄分桶（只按显式到期日与显式 as-of 日期派生，纯函数）：
    /// <paramref name="dueDate"/> 为 <c>null</c> 时返回 <c>null</c>（未知到期日，**不是**任何桶），
    /// 逾期天数 = as-of − 到期日（≤ 0 = 未到期）。
    /// </summary>
    public static string? AgingBucketOf(DateTime? dueDate, DateTime asOfDate, out int? overdueDays)
    {
        if (dueDate is not { } due)
        {
            overdueDays = null;
            return null;
        }

        var days = (asOfDate.Date - due.Date).Days;
        overdueDays = days;
        if (days <= 0) return BucketNotDue;
        if (days <= 30) return BucketOverdue1To30;
        if (days <= 60) return BucketOverdue31To60;
        if (days <= 90) return BucketOverdue61To90;
        return BucketOverdueOver90;
    }

    /// <summary>账龄文案（未知到期日显式说明「不计算」；其余给出逾期天数或未到期）</summary>
    public static string AgingText(string? bucket, int? overdueDays) => bucket switch
    {
        null => "未知到期日（无显式到期日：不计算账龄）",
        BucketNotDue => $"未到期（到期日尚有 {-overdueDays.GetValueOrDefault()} 天）",
        _ => $"{BucketText(bucket)}（逾期 {overdueDays.GetValueOrDefault()} 天）",
    };

    /// <summary>归一化分配状态筛选（null / 空 = 不过滤；非法取值直接拒绝，不静默兜底）</summary>
    public static string? NormalizeAllocationFilter(string? state)
    {
        var value = (state ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return null;
        if (!SupportedAllocationFilters.Contains(value, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"分配状态无效：{state}（应为 none / historical_only / partial / full）");
        }
        return value;
    }

    /// <summary>归一化发票（证据）状态筛选（默认 recorded；非法取值直接拒绝）</summary>
    public static string NormalizeInvoiceStatusFilter(string? status)
    {
        var value = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return SupplierInvoiceReconciliationSemantics.EvidenceRecorded;
        if (!SupplierInvoiceReconciliationSemantics.SupportedEvidenceStatuses.Contains(value, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"发票状态无效：{status}（应为 recorded / draft / voided / all）");
        }
        return value;
    }
}

/// <summary>
/// 供应商对账与账龄工作台查询条件（ERP-068；全部为只读筛选参数，非法取值直接拒绝而不静默忽略）。
/// <para>到期日区间筛选只命中**登记了显式到期日**的发票：未登记到期日的发票没有到期日可筛（其账龄不计算、
/// 单独成组），属口径内行为 —— 系统绝不按开票日期、付款条件或供应商默认账期补一个到期日。</para>
/// </summary>
public sealed class SupplierReconciliationAgingQuery
{
    /// <summary>发票身份筛选（精确 Id；留空 = 不限；传入非正数直接拒绝）</summary>
    public long? InvoiceId { get; set; }

    /// <summary>供应商筛选（留空 = 全部供应商；不同供应商分别成组）</summary>
    public long? SupplierId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成行、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>关键字（匹配发票号码 / 代码 / 供应商编码与名称 / 付款条件文本；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>开票日期开始（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateFrom { get; set; }

    /// <summary>开票日期结束（含当天；留空 = 不限）</summary>
    public DateTime? InvoiceDateTo { get; set; }

    /// <summary>显式到期日开始（含当天；**只命中登记了到期日的发票**；留空 = 不限）</summary>
    public DateTime? DueDateFrom { get; set; }

    /// <summary>显式到期日结束（含当天；**只命中登记了到期日的发票**；留空 = 不限）</summary>
    public DateTime? DueDateTo { get; set; }

    /// <summary>发票状态筛选（recorded 默认 / draft / voided / all；草稿与已作废金额永不并入有效应付证据合计）</summary>
    public string? InvoiceStatus { get; set; }

    /// <summary>分配状态筛选（none / historical_only / partial / full；留空 = 全部；只用持久化引用行判定）</summary>
    public string? AllocationState { get; set; }

    /// <summary>账龄 as-of 日期（显式；留空 = 当天；账龄只相对该日期与显式到期日计算）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = SupplierReconciliationAgingSemantics.DefaultPageSize;

    /// <summary>
    /// 归一化并校验：发票 Id / 币种 / 发票状态 / 分配状态必须是合法取值，日期区间不得倒置，关键字长度有界，
    /// 分页参数钳制到有界范围；非法取值一律抛业务异常（参数错误），不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        if (InvoiceId is <= 0)
            throw BusinessException.InvalidParameter($"发票 Id 必须为正整数：{InvoiceId}");

        if (SupplierId is <= 0) SupplierId = null;

        Currency = string.IsNullOrWhiteSpace(Currency)
            ? null
            : PurchaseInvoiceRules.NormalizeCurrencyStrict(Currency);

        InvoiceStatus = SupplierReconciliationAgingSemantics.NormalizeInvoiceStatusFilter(InvoiceStatus);
        AllocationState = SupplierReconciliationAgingSemantics.NormalizeAllocationFilter(AllocationState);

        InvoiceDateFrom = InvoiceDateFrom?.Date;
        InvoiceDateTo = InvoiceDateTo?.Date;
        if (InvoiceDateFrom.HasValue && InvoiceDateTo.HasValue && InvoiceDateFrom > InvoiceDateTo)
        {
            throw BusinessException.InvalidParameter(
                $"开票日期开始 {InvoiceDateFrom:yyyy-MM-dd} 不能晚于结束 {InvoiceDateTo:yyyy-MM-dd}");
        }

        DueDateFrom = DueDateFrom?.Date;
        DueDateTo = DueDateTo?.Date;
        if (DueDateFrom.HasValue && DueDateTo.HasValue && DueDateFrom > DueDateTo)
        {
            throw BusinessException.InvalidParameter(
                $"到期日开始 {DueDateFrom:yyyy-MM-dd} 不能晚于结束 {DueDateTo:yyyy-MM-dd}");
        }

        AsOfDate = (AsOfDate ?? DateTime.Today).Date;
        if (AsOfDate.Value.Year < 1900)
            throw BusinessException.InvalidParameter($"as-of 日期无效：{AsOfDate:yyyy-MM-dd}");

        if (Keyword is not null)
        {
            Keyword = Keyword.Trim();
            if (Keyword.Length == 0) Keyword = null;
            else if (Keyword.Length > SupplierReconciliationAgingSemantics.MaxKeywordLength)
            {
                throw BusinessException.InvalidParameter(
                    $"关键字长度不能超过 {SupplierReconciliationAgingSemantics.MaxKeywordLength} 个字符");
            }
        }

        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = SupplierReconciliationAgingSemantics.DefaultPageSize;
        if (PageSize > SupplierReconciliationAgingSemantics.MaxPageSize)
            PageSize = SupplierReconciliationAgingSemantics.MaxPageSize;
    }
}

/// <summary>
/// 工作台中的单张供应商采购发票行（ERP-068，只读派生）。
/// <para>金额三类严格分列：<see cref="GrossAmount"/>（发票含税总额证据）、
/// <see cref="ActiveAllocatedAmount"/>（ERP-066 有效「付款单 → 采购发票」引用行合计）、
/// <see cref="RemainingAmount"/>（算术剩余证据 = 含税总额 − 有效已分配），三者不相加减成任何
/// 「应付余额 / 已付款 / 已结清 / 逾期」结论。</para>
/// <para>未知处理：命中系统有界上限时金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 超过源规则的情形按「无效证据」显示且**不裁剪、不修复**。</para>
/// </summary>
public sealed class SupplierReconciliationAgingInvoice
{
    // ============ 发票身份 ============

    /// <summary>发票 Id</summary>
    public long InvoiceId { get; init; }

    /// <summary>发票类型（普票 / 专票 / 进口）</summary>
    public string InvoiceType { get; init; } = string.Empty;

    /// <summary>发票类型文案</summary>
    public string InvoiceTypeText { get; init; } = string.Empty;

    /// <summary>发票代码（普票 / 进口票可能为空）</summary>
    public string InvoiceCode { get; init; } = string.Empty;

    /// <summary>发票号码</summary>
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>发票身份文案（与 ERP-043 / ERP-065 同源）</summary>
    public string InvoiceIdentityText { get; init; } = string.Empty;

    /// <summary>开票日期</summary>
    public DateTime InvoiceDate { get; init; }

    // ============ 供应商（快照 + 当前可用性） ============

    public long SupplierId { get; init; }
    public string SupplierCode { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>供应商当前是否可用（存在、未删除且启用）；快照永远可读，不因停用 / 删除被回填</summary>
    public bool SupplierAvailable { get; init; }

    /// <summary>供应商可用性文案（不可用时照实说明，绝不猜测与补全）</summary>
    public string SupplierAvailabilityText { get; init; } = string.Empty;

    // ============ 币种与金额证据（原币；绝不换算、绝不跨币种合并） ============

    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;
    public int AmountDecimals { get; init; }
    public decimal NetAmount { get; init; }
    public decimal TaxAmount { get; init; }

    /// <summary>发票含税总额证据（价税合计，原币；持久化值原样呈现）</summary>
    public decimal GrossAmount { get; init; }

    // ============ 发票状态（草稿 / 已作废单独标注，排除在有效合计外） ============

    public int InvoiceStatus { get; init; }
    public string InvoiceStatusText { get; init; } = string.Empty;

    /// <summary>是否计入有效应付证据合计（= 已登记且未作废）</summary>
    public bool IsActiveEvidence { get; init; }

    /// <summary>是否草稿（工作数据，尚未形成登记证据）</summary>
    public bool IsDraft { get; init; }

    /// <summary>是否已作废（历史证据，金额单独可见但不并入有效合计）</summary>
    public bool IsVoided { get; init; }

    // ============ 显式到期日与付款条件（可选证据；留空 = 未知） ============

    public DateTime? DueDate { get; init; }

    /// <summary>到期日是否已知（只按持久化到期日判定；未知 ≠ 当天到期）</summary>
    public bool DueDateKnown { get; init; }

    /// <summary>到期日文案（未知时显式说明「未知」，绝不推算）</summary>
    public string DueDateText { get; init; } = string.Empty;

    public string PaymentTerms { get; init; } = string.Empty;
    public string PaymentTermsText { get; init; } = string.Empty;

    // ============ 账龄（只对有显式到期日的发票计算） ============

    /// <summary>账龄分桶；<c>null</c> = 未知到期日（独立分组，不计算账龄、不入任何桶）</summary>
    public string? AgingBucket { get; init; }

    /// <summary>账龄分桶文案（未知到期日显式说明「不计算」）</summary>
    public string AgingBucketText { get; init; } = string.Empty;

    /// <summary>逾期天数（as-of − 到期日；≤ 0 = 未到期）；<c>null</c> = 未知到期日</summary>
    public int? OverdueDays { get; init; }

    /// <summary>账龄说明文案（含未到期剩余天数 / 逾期天数 / 未知到期日声明）</summary>
    public string AgingText { get; init; } = string.Empty;

    // ============ 分配证据（ERP-066 / ERP-067 复用；有效与历史严格分列） ============

    /// <summary>分配状态（none / historical_only / partial / full / over_allocated / unknown）</summary>
    public string AllocationState { get; init; } = SupplierReconciliationAgingSemantics.AllocationUnknown;

    /// <summary>分配状态文案</summary>
    public string AllocationStateText { get; init; } = string.Empty;

    /// <summary>有效已分配金额（原币）；<c>null</c> = 未知（命中系统有界上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>参与有效合计的持久化引用行条数；<c>null</c> = 未知（命中上限）</summary>
    public int? ActiveAllocationCount { get; init; }

    /// <summary>参与有效合计的付款单张数（按付款单去重）；<c>null</c> = 未知（命中上限）</summary>
    public int? ActivePaymentCount { get; init; }

    /// <summary>算术剩余证据 = 含税总额 − 有效已分配金额（原币）；<c>null</c> = 未知或无效证据</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>剩余证据状态（known / unknown / over_allocated）</summary>
    public string RemainingState { get; init; } = SupplierReconciliationAgingSemantics.RemainingUnknown;

    /// <summary>剩余证据状态文案</summary>
    public string RemainingStateText { get; init; } = string.Empty;

    /// <summary>已作废引用行条数（历史证据，绝不并入有效合计）；<c>null</c> = 未知（命中上限）</summary>
    public int? VoidedAllocationCount { get; init; }

    /// <summary>已作废引用行金额（历史证据）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? VoidedAllocationAmount { get; init; }

    /// <summary>引用行仍有效但发票已草稿 / 已作废的条数（发票侧证据失效）；<c>null</c> = 未知</summary>
    public int? InvoiceInactiveAllocationCount { get; init; }

    /// <summary>引用行仍有效但发票已草稿 / 已作废的金额；<c>null</c> = 未知</summary>
    public decimal? InvoiceInactiveAllocationAmount { get; init; }

    /// <summary>无效证据条数（供应商 / 币种或快照不一致：不换算、不合并、不改派）；<c>null</c> = 未知</summary>
    public int? InvalidAllocationCount { get; init; }

    /// <summary>无效证据金额；<c>null</c> = 未知</summary>
    public decimal? InvalidAllocationAmount { get; init; }

    /// <summary>无法确认证据条数（付款单或发票不存在 / 已删除）；<c>null</c> = 未知</summary>
    public int? UnavailableAllocationCount { get; init; }

    /// <summary>无法确认证据金额；<c>null</c> = 未知</summary>
    public decimal? UnavailableAllocationAmount { get; init; }

    /// <summary>是否存在已作废 / 发票失效 / 无效 / 无法确认的历史证据（必须单独查看）</summary>
    public bool HasAllocationHistory { get; init; }

    /// <summary>是否存在无效或无法确认证据（保持可见，绝不修复、改派或合并）</summary>
    public bool HasInvalidOrUnavailableEvidence { get; init; }

    /// <summary>历史证据文案（无历史时为「无」；有历史时逐类说明，绝不并入有效合计）</summary>
    public string HistoricalEvidenceText { get; init; } = string.Empty;

    /// <summary>行级说明（未知 / 无效 / 缺证据时照实说明；绝不断言已付款、已结清或逾期）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 账龄分桶合计（或独立的「未知到期日」分组合计）（ERP-068，只读派生）。
/// <para>金额只汇总**本次返回页**中计入有效应付证据合计的发票（草稿 / 已作废不计入）；
/// 命中系统有界上限时金额为 <c>null</c>（未知），绝不给部分合计。</para>
/// </summary>
public sealed class SupplierReconciliationAgingBucketTotal
{
    /// <summary>分桶名（五个账龄桶之一，或 <see cref="SupplierReconciliationAgingSemantics.UnknownDueDateBucket"/>）</summary>
    public string Bucket { get; init; } = SupplierReconciliationAgingSemantics.UnknownDueDateBucket;

    /// <summary>分桶文案（接口、界面与文档同源）</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>是否账龄桶（false = 独立的「未知到期日」分组，不计算账龄）</summary>
    public bool IsAgingBucket =>
        !string.Equals(Bucket, SupplierReconciliationAgingSemantics.UnknownDueDateBucket, StringComparison.Ordinal);

    /// <summary>本桶发票张数（原始计数，恒可确认）</summary>
    public int InvoiceCount { get; init; }

    /// <summary>本桶含税总额合计（原币；发票行本身完整加载，恒可确认）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本桶有效已分配金额合计（原币）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>本桶算术剩余证据合计（原币）；<c>null</c> = 未知（命中上限或存在无效证据行）</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>本桶剩余证据未知的发票张数（命中上限或有效已分配超过含税总额）</summary>
    public int UnknownRemainingInvoiceCount { get; init; }

    /// <summary>本桶「有效已分配超过含税总额」（无效证据）的发票张数：绝不轧为 0，也不修复</summary>
    public int OverAllocatedInvoiceCount { get; init; }

    /// <summary>本桶说明（未知 / 无效 / 未知到期日时照实说明）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 「供应商 + 币种」分组（ERP-068，只读派生）：金额一律按原币汇总，不同币种分别成组、绝不合并；
/// 账龄桶与「未知到期日」分组严格分离。
/// </summary>
public sealed class SupplierReconciliationAgingGroup
{
    public long SupplierId { get; init; }
    public string SupplierCode { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;

    /// <summary>供应商当前是否可用（快照永远可读，不因停用 / 删除被回填）</summary>
    public bool SupplierAvailable { get; init; }

    public string SupplierAvailabilityText { get; init; } = string.Empty;

    /// <summary>币种（原币；不同币种绝不合并）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用；复用 ERP-043 的币种精度口径）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>本组发票张数（本次返回页内）</summary>
    public int InvoiceCount { get; init; }

    /// <summary>本组计入有效应付证据合计的发票张数（已登记未作废）</summary>
    public int ActiveEvidenceInvoiceCount { get; init; }

    /// <summary>本组草稿发票张数（工作数据，金额不计入任何有效合计）</summary>
    public int DraftInvoiceCount { get; init; }

    /// <summary>本组已作废发票张数（历史证据，金额不计入任何有效合计）</summary>
    public int VoidedInvoiceCount { get; init; }

    /// <summary>本组有显式到期日的发票张数（参与账龄桶）</summary>
    public int KnownDueDateInvoiceCount { get; init; }

    /// <summary>本组未知到期日的发票张数（账龄不计算，单独成组）</summary>
    public int UnknownDueDateInvoiceCount { get; init; }

    /// <summary>本组无任何持久化付款引用行的发票张数（证据缺口，不代表未付款 / 已付款 / 逾期）</summary>
    public int NoAllocationInvoiceCount { get; init; }

    /// <summary>本组只有已作废历史引用行的发票张数（历史证据单独可见）</summary>
    public int HistoricalOnlyInvoiceCount { get; init; }

    /// <summary>本组存在无效 / 无法确认证据的发票张数（保持可见，绝不修复或改派）</summary>
    public int InvalidEvidenceInvoiceCount { get; init; }

    /// <summary>本组有效证据合计的含税总额（原币；恒可确认）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本组有效证据合计的有效已分配金额（原币）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>本组有效证据合计的算术剩余金额（原币）；<c>null</c> = 未知（命中上限或存在无效证据行）</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>本组剩余证据未知的发票张数（命中上限或有效已分配超过含税总额）</summary>
    public int UnknownRemainingInvoiceCount { get; init; }

    /// <summary>本组「有效已分配超过含税总额」（无效证据）的发票张数</summary>
    public int OverAllocatedInvoiceCount { get; init; }

    /// <summary>本组账龄分桶合计（只统计有显式到期日的有效证据发票；顺序与常量一致）</summary>
    public List<SupplierReconciliationAgingBucketTotal> Buckets { get; init; } = new();

    /// <summary>本组「未知到期日」分组合计（**独立分组，不参与任何账龄桶**）</summary>
    public SupplierReconciliationAgingBucketTotal UnknownDueDate { get; init; } = new();

    /// <summary>本组发票行（本次返回页内，按分页顺序）</summary>
    public List<SupplierReconciliationAgingInvoice> Invoices { get; init; } = new();
}

/// <summary>
/// 「未知到期日」独立分组（ERP-068，只读派生）：这些发票没有显式到期日，账龄**不计算**，
/// 不参与任何账龄桶合计，也不被推算 / 补全到期日；分组仍按「供应商 + 币种」隔离。
/// </summary>
public sealed class SupplierReconciliationAgingUnknownDueDateGroup
{
    public long SupplierId { get; init; }
    public string SupplierCode { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;
    public int AmountDecimals { get; init; }

    /// <summary>本分组发票张数（本次返回页内）</summary>
    public int InvoiceCount { get; init; }

    /// <summary>本分组有效证据合计的含税总额（原币；恒可确认）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本分组有效证据合计的有效已分配金额（原币）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>本分组有效证据合计的算术剩余金额（原币）；<c>null</c> = 未知（命中上限或存在无效证据行）</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>本分组剩余证据未知的发票张数</summary>
    public int UnknownRemainingInvoiceCount { get; init; }

    /// <summary>本分组发票身份文案（有界：只含本次返回页内的发票）</summary>
    public List<string> InvoiceIdentities { get; init; } = new();

    /// <summary>分组说明（显式声明账龄不计算、不推算到期日）</summary>
    public string Note { get; init; } = SupplierReconciliationAgingSemantics.UnknownDueDateNote;
}

/// <summary>
/// 币种汇总（ERP-068，只读派生）：同一币种跨供应商汇总；**不存在任何跨币种总额字段**，
/// 不同币种绝不合并、不换算，账龄桶与「未知到期日」分组分别列出。
/// </summary>
public sealed class SupplierReconciliationAgingCurrencySummary
{
    /// <summary>币种（原币；每行一个币种）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种金额小数位（展示用）</summary>
    public int AmountDecimals { get; init; }

    /// <summary>本币种涉及的供应商数（去重）</summary>
    public int SupplierCount { get; init; }

    /// <summary>本币种发票张数（本次返回页内）</summary>
    public int InvoiceCount { get; init; }

    /// <summary>本币种计入有效应付证据合计的发票张数</summary>
    public int ActiveEvidenceInvoiceCount { get; init; }

    /// <summary>本币种草稿发票张数（不计入任何有效合计）</summary>
    public int DraftInvoiceCount { get; init; }

    /// <summary>本币种已作废发票张数（历史证据，不计入任何有效合计）</summary>
    public int VoidedInvoiceCount { get; init; }

    /// <summary>本币种有显式到期日的发票张数</summary>
    public int KnownDueDateInvoiceCount { get; init; }

    /// <summary>本币种未知到期日的发票张数（账龄不计算，单独成组）</summary>
    public int UnknownDueDateInvoiceCount { get; init; }

    /// <summary>本币种存在无效 / 无法确认证据的发票张数</summary>
    public int InvalidEvidenceInvoiceCount { get; init; }

    /// <summary>本币种有效证据合计的含税总额（原币；恒可确认）</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>本币种有效证据合计的有效已分配金额（原币）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>本币种有效证据合计的算术剩余金额（原币）；<c>null</c> = 未知（命中上限或存在无效证据行）</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>本币种剩余证据未知的发票张数</summary>
    public int UnknownRemainingInvoiceCount { get; init; }

    /// <summary>本币种「有效已分配超过含税总额」（无效证据）的发票张数</summary>
    public int OverAllocatedInvoiceCount { get; init; }

    /// <summary>本币种账龄分桶合计（只统计有显式到期日的有效证据发票）</summary>
    public List<SupplierReconciliationAgingBucketTotal> Buckets { get; init; } = new();

    /// <summary>本币种「未知到期日」分组合计（**独立分组，不参与任何账龄桶**）</summary>
    public SupplierReconciliationAgingBucketTotal UnknownDueDate { get; init; } = new();
}

/// <summary>
/// 供应商对账与账龄工作台报表（ERP-068，只读派生；不落库、不改发票 / 引用行 / 付款单 / 采购订单 /
/// 库存 / 财务 / 税务记录，不新增或修改任何表列）。
/// <para>金额一律按原币分别成行：<see cref="Currencies"/> 每个币种一行，**不存在**任何跨币种总额字段。</para>
/// </summary>
public sealed class SupplierReconciliationAgingReport
{
    // ============ 筛选回显（归一化后的实际取值） ============

    public long? InvoiceId { get; init; }
    public long? SupplierId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Keyword { get; init; } = string.Empty;
    public string InvoiceStatus { get; init; } = SupplierInvoiceReconciliationSemantics.EvidenceRecorded;
    public string InvoiceStatusText { get; init; } = string.Empty;
    public DateTime? InvoiceDateFrom { get; init; }
    public DateTime? InvoiceDateTo { get; init; }
    public DateTime? DueDateFrom { get; init; }
    public DateTime? DueDateTo { get; init; }

    /// <summary>分配状态筛选回显（空 = 全部）</summary>
    public string AllocationState { get; init; } = string.Empty;

    /// <summary>分配状态筛选文案</summary>
    public string AllocationStateText { get; init; } = "全部分配状态（none / historical_only / partial / full）";

    /// <summary>账龄 as-of 日期（显式；账龄只相对该日期与显式到期日计算）</summary>
    public DateTime AsOfDate { get; init; }

    /// <summary>as-of 日期文案（界面原样展示，明确账龄基准日）</summary>
    public string AsOfDateText { get; init; } = string.Empty;

    // ============ 分页 ============

    /// <summary>符合筛选条件的未删除发票总数</summary>
    public int Total { get; init; }

    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>本页发票张数</summary>
    public int PageInvoiceCount { get; init; }

    // ============ 本页计数（只统计本次返回页） ============

    /// <summary>本页计入有效应付证据合计的发票张数（已登记未作废）</summary>
    public int ActiveEvidencePageInvoiceCount { get; init; }

    /// <summary>本页草稿发票张数（不计入任何有效合计）</summary>
    public int DraftPageInvoiceCount { get; init; }

    /// <summary>本页已作废发票张数（历史证据，不计入任何有效合计）</summary>
    public int VoidedPageInvoiceCount { get; init; }

    /// <summary>本页有显式到期日的发票张数（参与账龄桶）</summary>
    public int KnownDueDatePageInvoiceCount { get; init; }

    /// <summary>本页未知到期日的发票张数（账龄不计算，单独成组）</summary>
    public int UnknownDueDatePageInvoiceCount { get; init; }

    /// <summary>本页无任何持久化付款引用行的发票张数（证据缺口）</summary>
    public int NoAllocationPageInvoiceCount { get; init; }

    /// <summary>本页仅有已作废历史引用行的发票张数</summary>
    public int HistoricalOnlyPageInvoiceCount { get; init; }

    /// <summary>本页存在无效 / 无法确认证据的发票张数（保持可见，绝不修复或改派）</summary>
    public int InvalidEvidencePageInvoiceCount { get; init; }

    /// <summary>本页「有效已分配超过含税总额」（无效证据）的发票张数</summary>
    public int OverAllocatedPageInvoiceCount { get; init; }

    /// <summary>本页剩余证据未知的发票张数（命中上限或无效证据）</summary>
    public int UnknownRemainingPageInvoiceCount { get; init; }

    // ============ 有界读取状态 ============

    /// <summary>本次读取是否命中系统有界上限（true = 金额一律按「未知」返回，不给部分合计）</summary>
    public bool Truncated { get; init; }

    /// <summary>命中上限时的说明（未命中为空串）</summary>
    public string TruncatedNote { get; init; } = string.Empty;

    // ============ 分组与汇总（账龄桶与未知到期日严格分离） ============

    /// <summary>本页币种汇总（每币种一行；不存在跨币种总额）</summary>
    public List<SupplierReconciliationAgingCurrencySummary> Currencies { get; init; } = new();

    /// <summary>本页「供应商 + 币种」分组（含账龄桶、未知到期日分组与发票行）</summary>
    public List<SupplierReconciliationAgingGroup> Groups { get; init; } = new();

    /// <summary>本页「未知到期日」独立分组（账龄不计算；仍按供应商 + 币种隔离）</summary>
    public List<SupplierReconciliationAgingUnknownDueDateGroup> UnknownDueDateGroups { get; init; } = new();

    // ============ 口径文案（接口、界面与文档同源） ============

    public string Rule { get; init; } = SupplierReconciliationAgingSemantics.RuleText;
    public string AgingRule { get; init; } = SupplierReconciliationAgingSemantics.AgingRuleText;
    public string CurrencyIsolationNote { get; init; } = SupplierReconciliationAgingSemantics.NoCrossCurrencyText;
    public string ScopeNote { get; init; } = SupplierReconciliationAgingSemantics.ScopeText;
    public string Boundary { get; init; } = SupplierReconciliationAgingSemantics.BoundaryText;
    public string UnknownDueDateNote { get; init; } = SupplierReconciliationAgingSemantics.UnknownDueDateNote;
    public string InvalidEvidenceNote { get; init; } = SupplierReconciliationAgingSemantics.InvalidEvidenceNote;
    public string SourceFailClosedNote { get; init; } = SupplierReconciliationAgingSemantics.SourceFailClosedText;
    public string ExportNote { get; init; } = SupplierReconciliationAgingSemantics.ExportNote;

    /// <summary>发票金额等式口径（与 ERP-043 / ERP-065 同源）</summary>
    public string AmountEquation { get; init; } = PurchaseInvoiceRules.AmountEquationText;

    /// <summary>付款引用行口径（与 ERP-066 同源）</summary>
    public string AllocationRule { get; init; } = SupplierPaymentInvoiceAllocationRules.RuleText;

    /// <summary>付款引用行边界（与 ERP-066 同源：不是付款凭证 / 核销 / 认证 / 供应商余额）</summary>
    public string AllocationBoundary { get; init; } = SupplierPaymentInvoiceAllocationRules.BoundaryText;
}

/// <summary>
/// 单张发票的对账与账龄证据明细（ERP-068，只读派生）：打开明细时**重新校验**身份与既有
/// 「角色 → 菜单」模块授权，并重新读取权威来源；未认证 / 未授权 / 发票不存在或已删除时一律拒绝（fail closed）。
/// </summary>
public sealed class SupplierReconciliationAgingInvoiceDetail
{
    /// <summary>发票 Id</summary>
    public long InvoiceId { get; init; }

    /// <summary>账龄 as-of 日期（与列表同一口径）</summary>
    public DateTime AsOfDate { get; init; }

    /// <summary>与列表**完全同源**的发票证据行（同一派生逻辑，不新增第二套口径）</summary>
    public SupplierReconciliationAgingInvoice Row { get; init; } = null!;

    /// <summary>派生口径说明（与列表同源）</summary>
    public string Rule { get; init; } = SupplierReconciliationAgingSemantics.RuleText;

    /// <summary>账龄口径说明（与列表同源）</summary>
    public string AgingRule { get; init; } = SupplierReconciliationAgingSemantics.AgingRuleText;

    /// <summary>边界说明（与列表同源）</summary>
    public string Boundary { get; init; } = SupplierReconciliationAgingSemantics.BoundaryText;

    /// <summary>授权复核说明（本次打开明细时已重新校验的身份与模块授权）</summary>
    public string AuthorizationNote { get; init; } = string.Empty;

    /// <summary>来源 fail-closed 说明（删除 / 不可用 / 未授权一律拒绝，不修复、不改派）</summary>
    public string SourceFailClosedNote { get; init; } = SupplierReconciliationAgingSemantics.SourceFailClosedText;
}

/// <summary>
/// 供应商对账与账龄工作台（ERP-068，只读派生）：按「供应商 + 币种」列出 ERP-065 持久化发票证据、
/// ERP-066 有效已分配付款引用证据与两者相减的算术剩余证据，并按**显式到期日**与**显式 as-of 日期**
/// 计算互斥账龄桶；未登记到期日的发票进入独立的「未知到期日」分组。
/// <para>复用而非重造：分配证据只经 <see cref="PurchaseOrderInvoicePaymentEvidence.AggregatesForInvoicesAsync"/>
/// （ERP-067 / ERP-066 的同一套分桶与资格判定）派生，量纲与文案同源，**不新增第二套对账算法**。</para>
/// <para>有界与只读：固定次数数据集访问（筛选 + 分页 + 本页发票 + 本页供应商 + 引用行分桶），与发票张数 / 行数无关，
/// 绝无逐行查库；全程只读，不落库、不改任何单据、引用行、付款单或表结构。</para>
/// </summary>
public static class SupplierReconciliationAging
{
    /// <summary>打开明细所需的既有菜单编码（= 采购订单模块；与 ERP-061 ~ ERP-067 的工作流同源）</summary>
    public const string RequiredMenuCode = AttachmentEvidenceRules.MenuCodePurchaseOrder;

    /// <summary>所需菜单的中文说明（与既有菜单台账同源）</summary>
    public const string RequiredMenuText = "采购订单";

    // ==================== 1. 列表 / 报表（只读、分页、有界） ====================

    /// <summary>派生分页对账与账龄报表（筛选 → 分页 → 批量派生，全程无逐行查询）</summary>
    public static async Task<SupplierReconciliationAgingReport> ForQueryAsync(
        IErpDbContext db, SupplierReconciliationAgingQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var asOfDate = query.AsOfDate ?? DateTime.Today;

        var source = ApplyFilters(db, query);
        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(i => i.SupplierId).ThenBy(i => i.Currency)
            .ThenBy(i => i.DueDate)
            .ThenByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(i => i.Id)
            .ToListAsync();

        var invoices = new List<PurchaseInvoice>();
        if (pageIds.Count > 0)
        {
            // 分页已定：只为本页发票加载实体，再按分页顺序还原（避免对全部匹配发票做无界装载）
            var loaded = await db.PurchaseInvoices.AsNoTracking()
                .Where(i => pageIds.Contains(i.Id))
                .ToListAsync();
            var byId = loaded.ToDictionary(i => i.Id);
            invoices = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var built = await BuildRowsAsync(db, invoices, asOfDate);
        var rows = built.Rows;
        var invoiceStatus = query.InvoiceStatus ?? SupplierInvoiceReconciliationSemantics.EvidenceRecorded;

        return new SupplierReconciliationAgingReport
        {
            InvoiceId = query.InvoiceId,
            SupplierId = query.SupplierId,
            Currency = query.Currency ?? string.Empty,
            Keyword = query.Keyword ?? string.Empty,
            InvoiceStatus = invoiceStatus,
            InvoiceStatusText = SupplierInvoiceReconciliationSemantics.EvidenceStatusText(invoiceStatus),
            InvoiceDateFrom = query.InvoiceDateFrom,
            InvoiceDateTo = query.InvoiceDateTo,
            DueDateFrom = query.DueDateFrom,
            DueDateTo = query.DueDateTo,
            AllocationState = query.AllocationState ?? string.Empty,
            AllocationStateText = query.AllocationState is null
                ? "全部分配状态（none / historical_only / partial / full）"
                : SupplierReconciliationAgingSemantics.AllocationStateText(query.AllocationState),
            AsOfDate = asOfDate,
            AsOfDateText = $"账龄基准日（as-of）：{asOfDate:yyyy-MM-dd}",
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
            PageInvoiceCount = rows.Count,
            ActiveEvidencePageInvoiceCount = rows.Count(r => r.IsActiveEvidence),
            DraftPageInvoiceCount = rows.Count(r => r.IsDraft),
            VoidedPageInvoiceCount = rows.Count(r => r.IsVoided),
            KnownDueDatePageInvoiceCount = rows.Count(r => r.DueDateKnown),
            UnknownDueDatePageInvoiceCount = rows.Count(r => !r.DueDateKnown),
            NoAllocationPageInvoiceCount = rows.Count(r =>
                r.AllocationState == SupplierReconciliationAgingSemantics.AllocationNone),
            HistoricalOnlyPageInvoiceCount = rows.Count(r =>
                r.AllocationState == SupplierReconciliationAgingSemantics.AllocationHistoricalOnly),
            InvalidEvidencePageInvoiceCount = rows.Count(r => r.HasInvalidOrUnavailableEvidence),
            OverAllocatedPageInvoiceCount = rows.Count(r =>
                r.RemainingState == SupplierReconciliationAgingSemantics.RemainingOverAllocated),
            UnknownRemainingPageInvoiceCount = rows.Count(r => r.RemainingAmount is null),
            Truncated = built.Truncated,
            TruncatedNote = built.Truncated
                ? "本次读取命中系统有界上限（ERP-066 持久化引用行数量超出上限）：所有分配证据金额与计数一律按「未知」显示，不给部分合计。"
                : string.Empty,
            Currencies = BuildCurrencySummaries(rows),
            Groups = BuildGroups(rows),
            UnknownDueDateGroups = BuildUnknownDueDateGroups(rows),
        };
    }

    /// <summary>
    /// 筛选（全部只用持久化字段）：发票 Id / 供应商 / 币种 / 发票状态 / 开票日期区间 / 显式到期日区间 /
    /// 分配状态（持久化引用行 DB 侧聚合比较）/ 关键字；到期日区间只命中「登记了到期日」的发票（未知到期日不可筛，
    /// 也绝不按开票日期补一个到期日）。
    /// </summary>
    private static IQueryable<PurchaseInvoice> ApplyFilters(
        IErpDbContext db, SupplierReconciliationAgingQuery query)
    {
        var source = db.PurchaseInvoices.AsNoTracking().Where(i => !i.IsDeleted);

        if (query.InvoiceId is { } invoiceId) source = source.Where(i => i.Id == invoiceId);
        if (query.SupplierId is { } supplierId) source = source.Where(i => i.SupplierId == supplierId);
        if (query.Currency is { } currency) source = source.Where(i => i.Currency == currency);

        switch (query.InvoiceStatus)
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

        if (query.DueDateFrom is { } dueFrom)
            source = source.Where(i => i.DueDate != null && i.DueDate >= dueFrom);

        if (query.DueDateTo is { } dueTo)
        {
            var dueToExclusive = dueTo.AddDays(1);
            source = source.Where(i => i.DueDate != null && i.DueDate < dueToExclusive);
        }

        switch (query.AllocationState)
        {
            // 无任何持久化引用行（既无有效行也无历史 / 无效行）
            case SupplierReconciliationAgingSemantics.AllocationNone:
                source = source.Where(i => !db.SupplierPaymentInvoiceAllocations
                    .Any(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id));
                break;

            // 没有有效引用行，但存在历史 / 无效引用行（保留可见，绝不并入有效合计）
            case SupplierReconciliationAgingSemantics.AllocationHistoricalOnly:
                source = source.Where(i =>
                    !db.SupplierPaymentInvoiceAllocations.Any(a => !a.IsDeleted
                        && a.PurchaseInvoiceId == i.Id
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive)
                    && db.SupplierPaymentInvoiceAllocations.Any(a => !a.IsDeleted && a.PurchaseInvoiceId == i.Id));
                break;

            case SupplierReconciliationAgingSemantics.AllocationPartial:
                source = source.Where(i => db.SupplierPaymentInvoiceAllocations
                        .Where(a => !a.IsDeleted
                            && a.PurchaseInvoiceId == i.Id
                            && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive)
                        .Sum(a => (decimal?)a.AllocatedAmount) > 0
                    && db.SupplierPaymentInvoiceAllocations
                        .Where(a => !a.IsDeleted
                            && a.PurchaseInvoiceId == i.Id
                            && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive)
                        .Sum(a => (decimal?)a.AllocatedAmount) < i.GrossAmount);
                break;

            case SupplierReconciliationAgingSemantics.AllocationFull:
                source = source.Where(i => db.SupplierPaymentInvoiceAllocations
                        .Where(a => !a.IsDeleted
                            && a.PurchaseInvoiceId == i.Id
                            && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive)
                        .Sum(a => (decimal?)a.AllocatedAmount) >= i.GrossAmount);
                break;
        }

        if (query.Keyword is { } keyword)
        {
            source = source.Where(i => i.InvoiceNumber.Contains(keyword)
                || i.InvoiceCode.Contains(keyword)
                || i.SupplierName.Contains(keyword)
                || i.SupplierCode.Contains(keyword)
                || i.PaymentTerms.Contains(keyword));
        }

        return source;
    }

    /// <summary>
    /// 本页发票行派生：本页 ERP-066 持久化引用行分桶（复用 ERP-067 的同一套权威派生，固定次数数据集访问）
    /// + 本页供应商可用性一次批量装载；全部只读，不写库。命中上限时 <c>Truncated = true</c>，
    /// 行内分配证据按「未知」处理，绝不给部分合计。
    /// </summary>
    private static async Task<(List<SupplierReconciliationAgingInvoice> Rows, bool Truncated)> BuildRowsAsync(
        IErpDbContext db, IReadOnlyList<PurchaseInvoice> invoices, DateTime asOfDate)
    {
        if (invoices.Count == 0)
            return (new List<SupplierReconciliationAgingInvoice>(), false);

        var invoiceIds = invoices.Select(i => i.Id).ToList();

        // 分配证据（ERP-066 / ERP-067 同源；固定次数数据集访问，无逐行查库）
        var allocated = await PurchaseOrderInvoicePaymentEvidence.AggregatesForInvoicesAsync(db, invoiceIds);

        // 供应商当前可用性（一次批量装载；供应商已停用 / 已删除时照实标注，历史快照照常可读）
        var supplierIds = invoices.Select(i => i.SupplierId).Distinct().ToList();
        var suppliers = (await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id)).ToListAsync())
            .ToDictionary(s => s.Id);

        var rows = new List<SupplierReconciliationAgingInvoice>(invoices.Count);
        foreach (var invoice in invoices)
        {
            var supplier = suppliers.TryGetValue(invoice.SupplierId, out var found) ? found : null;

            // 命中上限 → 未知（null，不给部分合计）；否则没有引用行的发票按全 0 聚合处理
            // （= 明确的「证据缺口」，与「未知」严格区分，绝不把缺口读成未付款 / 已付款）
            var aggregate = allocated.Truncated
                ? null
                : allocated.Get(invoice.Id) ?? InvoiceAllocatedPaymentAggregateSet.Empty(invoice.Id);

            rows.Add(MapInvoice(invoice, aggregate, supplier, asOfDate));
        }

        return (rows, allocated.Truncated);
    }

    /// <summary>
    /// 单张发票行的只读派生（纯函数 + 复用 ERP-067 的发票级聚合）：剩余证据 = 含税总额 − 有效已分配
    /// （两侧都可确认时才给出；超出源规则时按无效证据显示且绝不轧为 0）；账龄只按显式到期日与显式 as-of 日期计算；
    /// 草稿 / 已作废单独标注且不参与任何有效合计；缺失 / 无效链接保持可见，绝不修复或改派。
    /// </summary>
    private static SupplierReconciliationAgingInvoice MapInvoice(
        PurchaseInvoice invoice, InvoiceAllocatedPaymentAggregate? allocated,
        BaseSupplier? supplier, DateTime asOfDate)
    {
        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var gross = invoice.GrossAmount;

        var activeAmount = allocated?.ActiveAmount;
        var overAllocated = activeAmount is { } active && active > gross;
        decimal? remainingAmount = activeAmount is null || overAllocated ? null : gross - activeAmount.Value;
        var remainingState = overAllocated
            ? SupplierReconciliationAgingSemantics.RemainingOverAllocated
            : activeAmount is null
                ? SupplierReconciliationAgingSemantics.RemainingUnknown
                : SupplierReconciliationAgingSemantics.RemainingKnown;

        string allocationState;
        if (allocated is null)
            allocationState = SupplierReconciliationAgingSemantics.AllocationUnknown;
        else if (overAllocated)
            allocationState = SupplierReconciliationAgingSemantics.AllocationOverAllocated;
        else if (activeAmount.GetValueOrDefault() <= 0m)
            allocationState = allocated.HasAnyRow
                ? SupplierReconciliationAgingSemantics.AllocationHistoricalOnly
                : SupplierReconciliationAgingSemantics.AllocationNone;
        else if (activeAmount.GetValueOrDefault() >= gross)
            allocationState = SupplierReconciliationAgingSemantics.AllocationFull;
        else
            allocationState = SupplierReconciliationAgingSemantics.AllocationPartial;

        var bucket = SupplierReconciliationAgingSemantics.AgingBucketOf(invoice.DueDate, asOfDate, out var overdueDays);

        return new SupplierReconciliationAgingInvoice
        {
            InvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceTypeText = PurchaseInvoiceRules.InvoiceTypeText(invoice.InvoiceType),
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceIdentityText = PurchaseInvoiceRules.IdentityText(
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
            GrossAmount = gross,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = PurchaseInvoiceRules.StatusText(invoice.Status),
            IsActiveEvidence = SupplierReconciliationAgingSemantics.IsActiveEvidence(invoice.Status),
            IsDraft = invoice.Status == PurchaseInvoiceRules.StatusDraft,
            IsVoided = invoice.Status == PurchaseInvoiceRules.StatusVoided,
            DueDate = invoice.DueDate,
            DueDateKnown = invoice.DueDate.HasValue,
            DueDateText = PurchaseInvoiceRules.DueDateText(invoice.DueDate),
            PaymentTerms = invoice.PaymentTerms,
            PaymentTermsText = PurchaseInvoiceRules.PaymentTermsText(invoice.PaymentTerms),
            AgingBucket = bucket,
            AgingBucketText = bucket is null
                ? SupplierReconciliationAgingSemantics.BucketText(
                    SupplierReconciliationAgingSemantics.UnknownDueDateBucket)
                : SupplierReconciliationAgingSemantics.BucketText(bucket),
            OverdueDays = overdueDays,
            AgingText = SupplierReconciliationAgingSemantics.AgingText(bucket, overdueDays),

            // ============ 分配证据（ERP-066 / ERP-067 复用；有效与历史严格分列） ============

            AllocationState = allocationState,
            AllocationStateText = SupplierReconciliationAgingSemantics.AllocationStateText(allocationState),
            ActiveAllocatedAmount = activeAmount,
            ActiveAllocationCount = allocated?.ActiveCount,
            ActivePaymentCount = allocated?.ActivePaymentCount,
            RemainingAmount = remainingAmount,
            RemainingState = remainingState,
            RemainingStateText = SupplierReconciliationAgingSemantics.RemainingStateText(remainingState),
            VoidedAllocationCount = allocated?.VoidedCount,
            VoidedAllocationAmount = allocated?.VoidedAmount,
            InvoiceInactiveAllocationCount = allocated?.InvoiceInactiveCount,
            InvoiceInactiveAllocationAmount = allocated?.InvoiceInactiveAmount,
            InvalidAllocationCount = allocated?.InvalidCount,
            InvalidAllocationAmount = allocated?.InvalidAmount,
            UnavailableAllocationCount = allocated?.UnavailableCount,
            UnavailableAllocationAmount = allocated?.UnavailableAmount,
            HasAllocationHistory = allocated?.HasHistoricalRow ?? false,
            HasInvalidOrUnavailableEvidence = allocated is { } a && (a.InvalidCount > 0 || a.UnavailableCount > 0),
            HistoricalEvidenceText = BuildHistoricalEvidenceText(allocated, currency),
            Note = BuildRowNote(invoice, allocated, overAllocated, allocationState),
        };
    }

    /// <summary>历史 / 无效证据文案（无历史时显式写「无」；命中上限时写「未知」；绝不并入有效合计）</summary>
    private static string BuildHistoricalEvidenceText(
        InvoiceAllocatedPaymentAggregate? allocated, string currency)
    {
        if (allocated is null)
            return "未知（命中系统有界上限：历史 / 无效引用证据无法穷尽，不给部分合计）";

        if (!allocated.HasHistoricalRow)
            return "无历史 / 无效引用证据（缺失证据按「无」显示，绝不当成未付款、已付款、已结清或逾期）";

        var parts = new List<string>();
        if (allocated.VoidedCount > 0)
            parts.Add($"已作废引用行 {allocated.VoidedCount} 条 / {allocated.VoidedAmount}");
        if (allocated.InvoiceInactiveCount > 0)
            parts.Add($"发票已失效（草稿 / 已作废）{allocated.InvoiceInactiveCount} 条 / {allocated.InvoiceInactiveAmount}");
        if (allocated.InvalidCount > 0)
            parts.Add($"无效证据（供应商 / 币种或快照不一致）{allocated.InvalidCount} 条 / {allocated.InvalidAmount}");
        if (allocated.UnavailableCount > 0)
            parts.Add($"无法确认证据（付款单或发票已删除）{allocated.UnavailableCount} 条 / {allocated.UnavailableAmount}");

        return string.Join("；", parts)
            + $"（币种 {currency}；历史 / 无效证据绝不并入有效合计，也不被修复、改派或合并）";
    }

    /// <summary>
    /// 行级说明（未知 / 无效 / 证据缺口 / 未知到期日 / 草稿作废时照实说明）：
    /// 一律不断言已付款、已结清、已核销或逾期，也不把缺失证据读成欠款。
    /// </summary>
    private static string BuildRowNote(
        PurchaseInvoice invoice, InvoiceAllocatedPaymentAggregate? allocated,
        bool overAllocated, string allocationState)
    {
        var sb = new StringBuilder();
        if (allocated is null)
        {
            sb.Append("命中系统有界上限：有效已分配与剩余证据按「未知」显示（不给部分合计），"
                + "也绝不代表未付款、已付款、已结清或逾期。");
        }
        else if (overAllocated)
        {
            sb.Append("有效已分配金额超过含税总额：与 ERP-066 源规则矛盾，按无效证据显示，剩余证据按「未知」，"
                + "绝不轧为 0、也不视为已结清。");
        }
        else if (allocationState == SupplierReconciliationAgingSemantics.AllocationNone)
        {
            sb.Append("该发票没有任何持久化付款引用行：这是证据缺口，不代表未付款、已付款、已结清、逾期或欠款。");
        }
        else
        {
            sb.Append("剩余证据为仓库对账口径的算术派生（含税总额 − 有效已分配），"
                + "不是应付余额、不是付款授权，也不代表已结清。");
        }

        if (!invoice.DueDate.HasValue)
            sb.Append(" 到期日未登记（未知）：账龄不计算，单独成组（绝不按开票日期、付款条件或默认账期推算）。");
        if (invoice.Status == PurchaseInvoiceRules.StatusDraft)
            sb.Append(" 该发票仍为草稿：金额不计入有效应付证据合计。");
        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            sb.Append(" 该发票已作废：身份与金额保留可读，但不计入有效应付证据合计，也不做任何修复。");

        return sb.ToString().Trim();
    }

    // ==================== 2. 分组与汇总（账龄桶与「未知到期日」严格分离） ====================

    /// <summary>本页「供应商 + 币种」分组（金额只按原币汇总；不同币种绝不合并）</summary>
    private static List<SupplierReconciliationAgingGroup> BuildGroups(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows)
    {
        var groups = new List<SupplierReconciliationAgingGroup>();
        foreach (var group in rows.GroupBy(r => new { r.SupplierId, r.Currency }))
        {
            var invoices = group.ToList();
            var active = ActiveEvidence(invoices);
            var first = invoices[0];
            groups.Add(new SupplierReconciliationAgingGroup
            {
                SupplierId = first.SupplierId,
                SupplierCode = first.SupplierCode,
                SupplierName = first.SupplierName,
                SupplierAvailable = first.SupplierAvailable,
                SupplierAvailabilityText = first.SupplierAvailabilityText,
                Currency = first.Currency,
                AmountDecimals = first.AmountDecimals,
                InvoiceCount = invoices.Count,
                ActiveEvidenceInvoiceCount = active.Count,
                DraftInvoiceCount = invoices.Count(r => r.IsDraft),
                VoidedInvoiceCount = invoices.Count(r => r.IsVoided),
                KnownDueDateInvoiceCount = invoices.Count(r => r.DueDateKnown),
                UnknownDueDateInvoiceCount = invoices.Count(r => !r.DueDateKnown),
                NoAllocationInvoiceCount = invoices.Count(r =>
                    r.AllocationState == SupplierReconciliationAgingSemantics.AllocationNone),
                HistoricalOnlyInvoiceCount = invoices.Count(r =>
                    r.AllocationState == SupplierReconciliationAgingSemantics.AllocationHistoricalOnly),
                InvalidEvidenceInvoiceCount = invoices.Count(r => r.HasInvalidOrUnavailableEvidence),
                GrossAmount = SumAmount(active, r => r.GrossAmount),
                ActiveAllocatedAmount = SumKnown(active, r => r.ActiveAllocatedAmount),
                RemainingAmount = SumKnown(active, r => r.RemainingAmount),
                UnknownRemainingInvoiceCount = active.Count(r => r.RemainingAmount is null),
                OverAllocatedInvoiceCount = invoices.Count(r =>
                    r.RemainingState == SupplierReconciliationAgingSemantics.RemainingOverAllocated),
                Buckets = BuildBucketTotals(invoices),
                UnknownDueDate = BuildUnknownDueDateBucketTotal(invoices),
                Invoices = invoices,
            });
        }

        return groups;
    }

    /// <summary>本页币种汇总（每币种一行；**不存在**任何跨币种总额）</summary>
    private static List<SupplierReconciliationAgingCurrencySummary> BuildCurrencySummaries(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows)
    {
        var summaries = new List<SupplierReconciliationAgingCurrencySummary>();
        foreach (var group in rows.GroupBy(r => r.Currency))
        {
            var invoices = group.ToList();
            var active = ActiveEvidence(invoices);
            summaries.Add(new SupplierReconciliationAgingCurrencySummary
            {
                Currency = group.Key,
                AmountDecimals = invoices[0].AmountDecimals,
                SupplierCount = invoices.Select(r => r.SupplierId).Distinct().Count(),
                InvoiceCount = invoices.Count,
                ActiveEvidenceInvoiceCount = active.Count,
                DraftInvoiceCount = invoices.Count(r => r.IsDraft),
                VoidedInvoiceCount = invoices.Count(r => r.IsVoided),
                KnownDueDateInvoiceCount = invoices.Count(r => r.DueDateKnown),
                UnknownDueDateInvoiceCount = invoices.Count(r => !r.DueDateKnown),
                InvalidEvidenceInvoiceCount = invoices.Count(r => r.HasInvalidOrUnavailableEvidence),
                GrossAmount = SumAmount(active, r => r.GrossAmount),
                ActiveAllocatedAmount = SumKnown(active, r => r.ActiveAllocatedAmount),
                RemainingAmount = SumKnown(active, r => r.RemainingAmount),
                UnknownRemainingInvoiceCount = active.Count(r => r.RemainingAmount is null),
                OverAllocatedInvoiceCount = invoices.Count(r =>
                    r.RemainingState == SupplierReconciliationAgingSemantics.RemainingOverAllocated),
                Buckets = BuildBucketTotals(invoices),
                UnknownDueDate = BuildUnknownDueDateBucketTotal(invoices),
            });
        }

        return summaries;
    }

    /// <summary>本页「未知到期日」独立分组（按供应商 + 币种；账龄不计算，绝不并入任何账龄桶）</summary>
    private static List<SupplierReconciliationAgingUnknownDueDateGroup> BuildUnknownDueDateGroups(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows)
    {
        var groups = new List<SupplierReconciliationAgingUnknownDueDateGroup>();
        foreach (var group in rows.Where(r => !r.DueDateKnown)
                     .GroupBy(r => new { r.SupplierId, r.Currency }))
        {
            var invoices = group.ToList();
            var active = ActiveEvidence(invoices);
            groups.Add(new SupplierReconciliationAgingUnknownDueDateGroup
            {
                SupplierId = invoices[0].SupplierId,
                SupplierCode = invoices[0].SupplierCode,
                SupplierName = invoices[0].SupplierName,
                Currency = invoices[0].Currency,
                AmountDecimals = invoices[0].AmountDecimals,
                InvoiceCount = invoices.Count,
                GrossAmount = SumAmount(active, r => r.GrossAmount),
                ActiveAllocatedAmount = SumKnown(active, r => r.ActiveAllocatedAmount),
                RemainingAmount = SumKnown(active, r => r.RemainingAmount),
                UnknownRemainingInvoiceCount = active.Count(r => r.RemainingAmount is null),
                InvoiceIdentities = invoices.Select(r => r.InvoiceIdentityText).ToList(),
            });
        }

        return groups;
    }

    /// <summary>五个账龄桶合计（只统计**计入有效应付证据合计**且**有显式到期日**的发票）</summary>
    private static List<SupplierReconciliationAgingBucketTotal> BuildBucketTotals(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows)
    {
        var active = ActiveEvidence(rows);
        return SupplierReconciliationAgingSemantics.SupportedBuckets
            .Select(bucket => BuildBucketTotal(
                bucket,
                active.Where(r => r.AgingBucket == bucket).ToList(),
                string.Empty))
            .ToList();
    }

    /// <summary>「未知到期日」分组合计（独立分组；不参与任何账龄桶，也不被推算到期日）</summary>
    private static SupplierReconciliationAgingBucketTotal BuildUnknownDueDateBucketTotal(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows)
        => BuildBucketTotal(
            SupplierReconciliationAgingSemantics.UnknownDueDateBucket,
            ActiveEvidence(rows.Where(r => !r.DueDateKnown).ToList()),
            SupplierReconciliationAgingSemantics.UnknownDueDateNote);

    /// <summary>
    /// 单个桶 / 分组的合计：张数恒可确认；含税总额来自完整加载的发票行（恒可确认）；
    /// 有效已分配与剩余证据只要有一行未知（命中上限或无效证据）就整体按「未知」返回，绝不给部分合计。
    /// </summary>
    private static SupplierReconciliationAgingBucketTotal BuildBucketTotal(
        string bucket, IReadOnlyList<SupplierReconciliationAgingInvoice> rows, string note)
        => new()
        {
            Bucket = bucket,
            BucketText = SupplierReconciliationAgingSemantics.BucketText(bucket),
            InvoiceCount = rows.Count,
            GrossAmount = SumAmount(rows, r => r.GrossAmount),
            ActiveAllocatedAmount = SumKnown(rows, r => r.ActiveAllocatedAmount),
            RemainingAmount = SumKnown(rows, r => r.RemainingAmount),
            UnknownRemainingInvoiceCount = rows.Count(r => r.RemainingAmount is null),
            OverAllocatedInvoiceCount = rows.Count(r =>
                r.RemainingState == SupplierReconciliationAgingSemantics.RemainingOverAllocated),
            Note = note,
        };

    /// <summary>只统计计入有效应付证据合计的发票（草稿 / 已作废金额绝不并入任何有效合计）</summary>
    private static List<SupplierReconciliationAgingInvoice> ActiveEvidence(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows)
        => rows.Where(r => r.IsActiveEvidence).ToList();

    /// <summary>恒可确认金额求和（例如持久化含税总额：发票行本身完整加载）</summary>
    private static decimal SumAmount(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows,
        Func<SupplierReconciliationAgingInvoice, decimal> selector)
    {
        decimal sum = 0m;
        foreach (var row in rows) sum += selector(row);
        return sum;
    }

    /// <summary>可未知金额求和：只要有一行未知，整体按「未知」（null）返回，绝不用 0 顶替</summary>
    private static decimal? SumKnown(
        IReadOnlyList<SupplierReconciliationAgingInvoice> rows,
        Func<SupplierReconciliationAgingInvoice, decimal?> selector)
    {
        decimal sum = 0m;
        foreach (var row in rows)
        {
            var value = selector(row);
            if (value is null) return null;
            sum += value.Value;
        }

        return sum;
    }

    // ==================== 3. 单张发票明细（重新校验身份 + 模块授权，fail closed） ====================

    /// <summary>
    /// 单张发票的对账与账龄证据明细（只读派生）：打开明细时**重新校验**当前登录身份（<paramref name="userId"/>）
    /// 与既有「角色 → 菜单」模块授权（<see cref="RequiredMenuCode"/>），并重新读取权威来源。
    /// <para>fail closed：未认证 → 未认证错误；无该模块授权 → 权限不足；发票不存在或已删除 → 不存在；
    /// 任何一种都<strong>不返回任何证据</strong>、不返回部分金额，也不做来源修复或改派。</para>
    /// <para>返回值与列表行**完全同源**（同一派生方法），因此界面在列表与明细之间不会出现两套口径。</para>
    /// </summary>
    public static async Task<SupplierReconciliationAgingInvoiceDetail> ForInvoiceDetailAsync(
        IErpDbContext db, long invoiceId, long? userId, DateTime? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (invoiceId <= 0)
            throw BusinessException.InvalidParameter($"发票 Id 必须为正整数：{invoiceId}");

        var asOf = (asOfDate ?? DateTime.Today).Date;

        // 1) 身份（未认证 → 拒绝；不返回任何证据）
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查看发票对账证据明细", ErrorCodes.Unauthorized);

        // 2) 既有「角色 → 菜单」模块授权（未授权 → 拒绝；不返回任何证据）
        var menuCodes = await LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝打开对账证据明细"
                + "（fail closed，不返回任何证据、不做来源修复或改派）",
                ErrorCodes.Forbidden);
        }

        // 3) 权威来源：发票必须存在且未删除（已删除 / 不存在 → 拒绝，绝不修复、绝不改派）
        var invoice = await db.PurchaseInvoices.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted);
        if (invoice is null)
        {
            throw BusinessException.NotFound(
                "供应商采购发票不存在或已删除：无法确认对账与账龄证据"
                + "（fail closed，不返回部分证据，也不做任何修复或改派）");
        }

        var built = await BuildRowsAsync(db, new[] { invoice }, asOf);
        if (built.Rows.Count == 0)
            throw BusinessException.NotFound("供应商采购发票证据不可用：无法确认对账与账龄证据");

        return new SupplierReconciliationAgingInvoiceDetail
        {
            InvoiceId = invoiceId,
            AsOfDate = asOf,
            Row = built.Rows[0],
            AuthorizationNote =
                $"打开明细时已重新校验登录身份与「{RequiredMenuText}」（{RequiredMenuCode}）模块授权"
                + "（复用既有「角色 → 菜单」口径），并重新读取持久化发票与 ERP-066 引用行证据。",
        };
    }

    /// <summary>
    /// 当前账号被允许访问的菜单编码（fail closed：无身份 / 无角色 / 无菜单授权 → 空集合）：
    /// 复用**既有**授权口径 <c>SysUserRoles</c> → <c>SysRoleMenus</c> → <c>SysMenus.MenuCode</c>
    /// （忽略按钮型菜单与被删除角色 / 菜单）；每次请求都重新查询，因此撤销授权后立即收敛。
    /// </summary>
    private static async Task<HashSet<string>> LoadAuthorizedMenuCodesAsync(IErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        if (roleIds.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roleSet = roleIds.ToHashSet();
        var menuIds = await db.SysRoleMenus.AsNoTracking()
            .Where(rm => roleSet.Contains(rm.RoleId) && !rm.IsDeleted)
            .Select(rm => rm.MenuId)
            .ToListAsync();
        if (menuIds.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var menuIdSet = menuIds.ToHashSet();
        var codes = await db.SysMenus.AsNoTracking()
            .Where(m => menuIdSet.Contains(m.Id) && !m.IsDeleted && m.MenuType != MenuType.Button)
            .Select(m => m.MenuCode)
            .ToListAsync();

        return codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}