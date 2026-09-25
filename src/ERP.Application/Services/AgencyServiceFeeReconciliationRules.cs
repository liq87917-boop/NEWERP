using ERP.Application.Common;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费**对账与账龄工作台**的纯规则（ERP-072，无数据库依赖，便于逐条单测）：
/// 账龄分桶（只对**显式持久化到期日**计算）、分配状态与剩余证据状态、状态 / 分配状态 / 服务来源筛选归一化、
/// 有界上限与接口、界面、文档同源的口径文案。
/// <para>派生口径（关键）：</para>
/// <list type="number">
/// <item>本工作台只是 ERP-070 对账单证据与 ERP-071 收款分摊证据之上的**只读派生视图**：
/// <strong>不新增表、不新增列、不回填、不改写任何来源记录</strong>；</item>
/// <item>单张对账单的<b>算术剩余证据</b> = 对账单 <c>TotalAmount</c>（服务端计算的合计证据）
/// − **有效** ERP-071 分摊行合计（有效 = 未作废且对账单当前仍为已登记、收款单当前仍可读未取消、
/// 快照客户与币种自洽；作废行只在历史可见、**绝不并入**有效合计）；</item>
/// <item>**账龄只对存在显式持久化到期日**的对账单计算，并以显式 as-of 日期为准；到期日为空时
/// **不计算账龄**，进入独立的「未知到期日」分组，<strong>绝不</strong>按客户账期、协议文字、对账日期或
/// 历史对账单推算；</item>
/// <item>草稿与已作废对账单**单独标注**并被排除在有效对账合计与账龄桶合计之外；</item>
/// <item>不同币种**分别成行**：绝不合并、绝不换算，模型与界面**没有任何跨币种总额**；</item>
/// <item>命中系统有界上限时金额与计数一律为「未知」（<c>null</c>），**绝不用 0 顶替、绝不给部分合计**。</item>
/// </list>
/// <para>边界：本规则只做**校验与纯计算**，不写库、不开票、不记账、不核销、不收款或付款、不催收或联系客户，
/// 也不改写对账单证据、收款分摊证据、收款单、协议证据、客户主数据、销售订单、装柜清单、单证、发票、
/// 库存、费用、退税与结算记录。</para>
/// </summary>
public static class AgencyServiceFeeReconciliationRules
{
    // ==================== 1. 有界上限 ====================

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多对账单证据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>本页分摊行单次装载上限（超出即按「未知」处理：绝不给部分合计）</summary>
    public const int MaxAllocationRowsPerPage = 2000;

    /// <summary>本页对账单行单次装载上限（超出即按「未知」处理：来源摘要不给部分结论）</summary>
    public const int MaxStatementLineRowsPerPage = 2000;

    /// <summary>单条对账单明细中返回的分摊行上限（有界）</summary>
    public const int MaxDetailsPerStatement = 100;

    /// <summary>单行展示的服务来源身份条数上限（有界，超出只给计数）</summary>
    public const int MaxSourceIdentitiesPerRow = 5;

    /// <summary>「未知到期日」分组中展示的对账单身份条数上限（有界，超出只给计数）</summary>
    public const int MaxUnknownDueIdentitiesPerGroup = 10;

    /// <summary>
    /// 打开明细时要求的**既有菜单编码**（复用既有「角色 → 菜单」授权口径；与客户资料菜单同码，
    /// 不新增任何菜单或权限模型）。
    /// </summary>
    public const string RequiredMenuCode = "customer";

    /// <summary>要求菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "客户资料";

    /// <summary>系统支持的币种（与对账单 / 协议 / 收款单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    // ==================== 2. 账龄分桶（互斥且完整；只对显式到期日计算） ====================

    /// <summary>未到期（as-of ≤ 显式到期日；含恰好到期当天）</summary>
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
    /// 不得按客户账期、协议文字、对账日期或历史对账单推算。
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

    // ==================== 3. 分配状态（只按持久化 ERP-071 分摊行派生） ====================

    /// <summary>没有任何持久化分摊行（既无有效行也无历史行）</summary>
    public const string AllocationNone = "none";

    /// <summary>没有有效分摊行，但存在未删除的历史 / 无效分摊行（已作废 / 无效：单独可见，绝不并入有效合计）</summary>
    public const string AllocationHistoricalOnly = "historical_only";

    /// <summary>有效已分摊金额 &gt; 0 且 &lt; 对账单合计（部分分摊）</summary>
    public const string AllocationPartial = "partial";

    /// <summary>有效已分摊金额 ≥ 对账单合计（全额分摊）</summary>
    public const string AllocationFull = "full";

    /// <summary>有效已分摊金额 &gt; 对账单合计（与 ERP-071 源规则矛盾：无效证据，按「未知」显示，不轧成 0 或负数）</summary>
    public const string AllocationOverAllocated = "over_allocated";

    /// <summary>命中系统有界读取上限：分摊证据不完整，分配状态与金额一律按「未知」显示（不给部分合计）</summary>
    public const string AllocationUnknown = "unknown";

    /// <summary>
    /// 支持的**分配状态筛选**取值（全部只用持久化字段即可判定）。
    /// <para>说明：<see cref="AllocationOverAllocated"/> 与 <see cref="AllocationUnknown"/> 只作为**读取时派生的行状态**
    /// 出现在结果中，不提供为筛选（它们需要 ERP-071 的行级资格判定与有界上限判定，不是持久化字段的直接比较）；
    /// 无效 / 无法确认的历史证据仍按行单独可见，绝不被修复、改派或合并。</para>
    /// </summary>
    public static readonly string[] SupportedAllocationFilters =
    {
        AllocationNone, AllocationHistoricalOnly, AllocationPartial, AllocationFull,
    };

    /// <summary>分配状态中文文案（接口、界面与文档同源）</summary>
    public static string AllocationStateText(string state) => state switch
    {
        AllocationNone => "无持久化收款分摊行（证据缺口，不代表未付 / 已付 / 已结清 / 逾期）",
        AllocationHistoricalOnly => "仅有历史 / 无效分摊行（已作废 / 无效 / 无法确认：单独可见，绝不并入有效合计）",
        AllocationPartial => "部分分摊（有效已分摊金额小于对账单合计，未分摊金额单独可见）",
        AllocationFull => "全额分摊（有效已分摊金额已覆盖对账单合计）",
        AllocationOverAllocated => "无效证据：有效已分摊金额超过对账单合计（与源规则矛盾，按「未知」显示，绝不轧为 0 或负数）",
        _ => "未知（命中系统有界读取上限：分摊证据无法穷尽，不给部分合计）",
    };

    // ==================== 4. 剩余证据状态 ====================

    /// <summary>剩余证据可确认：对账单合计 − 有效已分摊金额</summary>
    public const string RemainingKnown = "known";

    /// <summary>剩余证据未知（命中上限）：绝不用 0 顶替</summary>
    public const string RemainingUnknown = "unknown";

    /// <summary>剩余证据与源规则矛盾（有效已分摊金额 &gt; 对账单合计）：无效证据，不做任何修复</summary>
    public const string RemainingOverAllocated = "over_allocated";

    /// <summary>剩余证据状态中文文案（接口、界面与文档同源）</summary>
    public static string RemainingStateText(string state) => state switch
    {
        RemainingKnown => "可确认（对账单合计 − 有效已分摊金额）",
        RemainingOverAllocated => "无效证据：有效已分摊金额超过对账单合计（绝不轧为 0，也不视为已结清）",
        _ => "未知（命中系统有界读取上限：不给部分合计）",
    };

    // ==================== 5. 来源 / 链接可用性 ====================

    /// <summary>来源可用（对账单与全部分摊行指向的来源均可确认）</summary>
    public const string LinkStateAvailable = "available";

    /// <summary>来源不可用 / 无效（对账单已删除、分摊行指向的收款单已删除 / 已取消、快照客户或币种不一致）：按无效历史证据单列，不修复、不改派</summary>
    public const string LinkStateUnavailable = "unavailable";

    // ==================== 6. 对账单状态筛选 ====================

    /// <summary>对账单状态筛选：已登记证据（默认；只有它计入有效对账与账龄合计）</summary>
    public const string StatementStatusRecorded = "recorded";

    /// <summary>对账单状态筛选：仅草稿（工作数据，尚未形成登记证据）</summary>
    public const string StatementStatusDraft = "draft";

    /// <summary>对账单状态筛选：仅已作废（历史证据，单独可见但不并入有效合计）</summary>
    public const string StatementStatusVoided = "voided";

    /// <summary>对账单状态筛选：全部状态</summary>
    public const string StatementStatusAll = "all";

    /// <summary>支持的对账单状态筛选取值</summary>
    public static readonly string[] SupportedStatementStatuses =
    {
        StatementStatusRecorded, StatementStatusDraft, StatementStatusVoided, StatementStatusAll,
    };

    /// <summary>对账单状态筛选中文文案</summary>
    public static string StatementStatusFilterText(string status) => status switch
    {
        StatementStatusDraft => "仅草稿（未形成登记证据，排除在有效对账合计之外）",
        StatementStatusVoided => "仅已作废历史证据（单独可见，排除在有效对账合计之外）",
        StatementStatusAll => "全部状态（草稿 / 已登记 / 已作废）",
        _ => "仅已登记证据（默认；有效对账与账龄合计只统计它）",
    };

    // ==================== 7. 归一化与校验（非法取值一律拒绝，不静默兜底） ====================

    /// <summary>归一化分配状态筛选（null / 空 = 不过滤；非法取值直接拒绝）</summary>
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

    /// <summary>归一化对账单状态筛选（默认 recorded；非法取值直接拒绝）</summary>
    public static string NormalizeStatementStatusFilter(string? status)
    {
        var value = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return StatementStatusRecorded;
        if (!SupportedStatementStatuses.Contains(value, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"对账单状态无效：{status}（应为 recorded / draft / voided / all）");
        }
        return value;
    }

    /// <summary>归一化服务来源筛选（null / 空 = 不过滤；非法取值直接拒绝，复用 ERP-070 的来源白名单）</summary>
    public static string? NormalizeSourceTypeFilter(string? sourceType)
        => AgencyServiceFeeStatementRules.NormalizeSourceTypeFilter(sourceType);

    // ==================== 8. 纯派生（不访问数据库） ====================

    /// <summary>是否属于计入有效对账与账龄合计的对账单状态（草稿 / 已作废不算）</summary>
    public static bool IsActiveEvidence(int statementStatus)
        => statementStatus == AgencyServiceFeeStatementRules.StatusRecorded;

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

    /// <summary>
    /// 分配状态判定（依据持久化行派生；等于对账单合计时才是全额分摊）：
    /// <paramref name="activeAmount"/> 为 <c>null</c> = 未知（命中上限）。
    /// </summary>
    public static string AllocationStateOf(
        decimal? activeAmount, decimal statementAmount, bool hasAnyRow, bool hasInvalidEvidence)
    {
        if (activeAmount is null) return AllocationUnknown;
        if (activeAmount.Value > statementAmount) return AllocationOverAllocated;
        if (activeAmount.Value > 0m)
            return activeAmount.Value >= statementAmount ? AllocationFull : AllocationPartial;
        return hasAnyRow || hasInvalidEvidence ? AllocationHistoricalOnly : AllocationNone;
    }

    /// <summary>剩余证据状态判定（与 <see cref="AllocationStateOf"/> 同一套证据，绝不裁剪下限）</summary>
    public static string RemainingStateOf(string allocationState) => allocationState switch
    {
        AllocationUnknown => RemainingUnknown,
        AllocationOverAllocated => RemainingOverAllocated,
        _ => RemainingKnown,
    };

    /// <summary>是否存在分摊行（本维度，含已作废；<paramref name="allocationRowCount"/> 为 <c>null</c> = 未知）</summary>
    public static bool HasAnyAllocationRow(int? allocationRowCount) => allocationRowCount is > 0;

    // ==================== 9. 口径文案（接口、界面与文档同源） ====================

    /// <summary>派生口径说明（证据来源、剩余证据、未知处理）</summary>
    public const string RuleText =
        "本工作台是**只读的代理服务费对账与账龄视图**：证据只来自 ERP-070 的持久化对账单证据与 "
        + "ERP-071 的持久化「客户收款 → 代理服务费对账单」分摊行（口径与 ERP-071 同源，不新增第二套对账引擎）；"
        + "对账单合计取持久化 TotalAmount（服务端按已校验行计算），有效已分摊金额只统计**未作废**且来源仍可确认的分摊行；"
        + "算术剩余证据 = 对账单合计 − 有效已分摊金额（只有两侧都可确认时才给出；超出源规则的情形按「未知 / 无效」显示，**绝不轧为 0**）；"
        + "草稿与已作废对账单单独标注并**排除**在有效对账证据合计之外，已作废分摊行只在历史列可见；"
        + "系统绝不按单号、金额、日期或文本相似度猜测归属或补链接。";

    /// <summary>账龄口径说明（只对显式到期日计算；桶边界互斥且完整）</summary>
    public const string AgingRuleText =
        "账龄只对**存在显式持久化到期日**的对账单计算，并以显式 as-of 日期为准："
        + "未到期（as-of ≤ 到期日）、逾期 1 ~ 30 天、逾期 31 ~ 60 天、逾期 61 ~ 90 天、逾期 90 天以上 —— "
        + "五个桶**互斥且完整**覆盖有到期日的情形（边界取含：30 / 60 / 90 天归入本桶，次日起归入下一桶）；"
        + "到期日为空时账龄**不计算**，对账单进入独立的「未知到期日」分组，绝不按客户账期、协议文字、"
        + "对账日期或历史对账单推算到期日，也不把未知到期日并入任何账龄桶。";

    /// <summary>币种隔离说明（不同币种分别成行、绝不合并、绝无跨币种总额）</summary>
    public const string NoCrossCurrencyText =
        "币种隔离：所有金额（对账单合计、有效已分摊、算术剩余、账龄分桶合计）一律按**原币**分别成行，"
        + "不同币种**绝不**相加、合并或换算；本工作台**不提供**任何跨币种总额字段，"
        + "也不使用当前汇率或猜测汇率（只有源证据本身已持久化的权威换算才会被采用）。";

    /// <summary>范围说明（合计只统计本次返回页；有界读取与未知处理）</summary>
    public const string ScopeText =
        "范围：分组汇总、账龄分桶与币种汇总都只统计**本次返回页**的对账单；total 为符合筛选条件的未删除对账单总数；"
        + "分页按客户 → 币种 → 到期日（未知在前）→ 对账日期（倒序）→ 对账单 Id（倒序）稳定排序，单页最多 200 张。"
        + "读取为固定次数数据集访问（筛选 + 分页 + 本页对账单 + 本页收款分摊行 + 本页客户 + 本页协议 + 本页服务来源行），"
        + "与对账单张数 / 行数无关，绝无逐行查库；命中系统有界上限时金额与计数一律按「未知」显示，不给部分合计。";

    /// <summary>与总账 / 法定对账单 / 付款通知 / 收入确认 / 税务申报 / 结算确认的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本工作台是**运营性的只读对账与账龄视图**：显示的是本系统仓库内的**对账证据数字**，"
        + "**不是**总账或应收账款余额、**不是**经审计的客户对账单或具有法律效力的对账确认、"
        + "**不是**付款通知或催款函、**不是**收款授权与收款执行依据、**不是**收入确认、"
        + "**不是**税务（销项）申报与开票判断、**不是**结算确认或核销结果；"
        + "它不判断是否已收款、是否已结清、是否欠款、是否逾期已确认，不产生任何记账、凭证、开票、收付款或结算单，"
        + "也不得据以收款、开票、催收或进行任何法律意义上的对账确认。";

    /// <summary>未知到期日分组的说明（未知不计算账龄，也不视为当天到期）</summary>
    public const string UnknownDueDateNote =
        "「未知到期日」是独立分组：这些对账单没有登记显式到期日，因此账龄**不计算**，"
        + "系统不会按客户账期、协议文字、对账日期或历史对账单补一个到期日，"
        + "也不会把它们当作当天到期或已逾期；它们**不参与**任何账龄桶合计。";

    /// <summary>无效 / 无法确认证据的说明（保留可见，绝不修复、改派或合并）</summary>
    public const string InvalidEvidenceNote =
        "无效 / 无法确认的历史证据（分摊行指向的收款单已删除 / 已取消、对账单已作废或已删除、"
        + "快照客户或币种与对账单不一致）：一律**保持可见**并单独成列，金额**绝不**并入有效合计，"
        + "也**不被修复、改派、重算或合并**；缺失的证据按「无」或「未知」显示，"
        + "绝不当成未收款、已收款、已结清、已核销或已逾期。";

    /// <summary>来源失效时的 fail-closed 说明（打开明细时重新校验授权与来源）</summary>
    public const string SourceFailClosedText =
        "打开对账单证据明细时会**重新校验**当前登录身份与既有「角色 → 菜单」模块授权，并重新读取权威来源："
        + "未认证、未获该模块授权、对账单不存在或已删除时一律**拒绝**（fail closed），"
        + "既不返回部分证据、也不做任何来源修复或改派。";

    /// <summary>导出说明（导出与屏幕同一筛选、同一币种隔离、同一未知到期日语义）</summary>
    public const string ExportNote =
        "导出与屏幕**完全同源**：使用同一筛选条件、同一分页结果、同一币种隔离与同一「未知到期日」分组语义，"
        + "导出内容**不包含**任何跨币种总额，也不添加任何法律结论（不写「应收」「欠款」「逾期确认」「已对账」等断言）。";

    /// <summary>证据维度分离说明（收款分摊与销售订单收款引用、销项发票分摊绝不相加）</summary>
    public const string DimensionSeparationText =
        "证据维度分离：本工作台只使用「客户收款 → 代理服务费对账单」这一个证据维度（ERP-071）；"
        + "ERP-053 的「收款单 → 销售订单」收款引用与 ERP-055 的「销项发票 → 销售订单」分摊是**另外两个独立维度**；"
        + "三者**绝不相加**，也**绝不**被当成几张不同的收款单。";

    /// <summary>历史证据只读说明</summary>
    public const string HistoricalEvidenceText =
        "历史证据只读：已作废对账单、已作废分摊行、无效 / 无法确认链接一律**保留可读**（含原始金额、"
        + "双方快照、登记人与时间戳、作废原因），但**永不并入**有效对账合计与账龄桶合计；"
        + "系统不提供硬删除、静默改写、改派或合并，也不做任何生产回填。";

    /// <summary>只读边界说明（本工作台只读，不改写任何来源记录）</summary>
    public const string ReadOnlyText =
        "全程只读：本工作台不新增 / 不修改任何表与列，也不写入任何数据；"
        + "不改写对账单证据（含合计 / 到期日 / 服务期间 / 状态 / 行清单）、ERP-071 分摊行、收款单、"
        + "ERP-069 协议证据、客户主数据、销售订单、装柜清单、单证、发票、库存与库存成本、费用与退税、"
        + "结算与余额记录；不开票、不记账、不核销、不收款或付款、不催收或联系客户、不调用任何外部服务。";

    /// <summary>未登记到期日 / 缺失证据时的展示文案（绝不推算）</summary>
    public const string UnknownDueDateText = "未登记（到期日未知：不推算）";

    /// <summary>无分摊行时的展示文案（证据缺口 ≠ 未付款）</summary>
    public const string NoAllocationText = "无持久化分摊行（证据缺口，不代表未付 / 已付 / 已结清 / 逾期）";
}
