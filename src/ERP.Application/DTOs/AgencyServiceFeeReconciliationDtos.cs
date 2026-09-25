using ERP.Application.Common;
using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 代理服务费对账与账龄工作台查询条件（ERP-072；全部为只读筛选参数，非法取值直接拒绝而不静默忽略）。
/// <para>到期日区间筛选只命中**登记了显式到期日**的对账单：未登记到期日的对账单没有到期日可筛
/// （其账龄不计算、单独成组），属口径内行为 —— 系统绝不按客户账期、协议文字或对账日期补一个到期日。</para>
/// </summary>
public sealed class AgencyServiceFeeReconciliationQuery
{
    /// <summary>对账单身份筛选（精确 Id；留空 = 不限；传入非正数直接拒绝）</summary>
    public long? StatementId { get; set; }

    /// <summary>客户筛选（留空 = 全部客户；不同客户分别成组，绝不合并）</summary>
    public long? CustomerId { get; set; }

    /// <summary>协议身份筛选（ERP-069 协议 Id 精确匹配，只用持久化字段；留空 = 不限）</summary>
    public long? AgreementId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成行、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>服务来源筛选（<c>sales-order</c> / <c>loading-list</c>；命中「至少一条对账单行引用该来源类型」；留空 = 不限）</summary>
    public string? SourceType { get; set; }

    /// <summary>关键字（匹配对账单号 / 客户编码与名称 / 协议号 / 备注；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>对账日期开始（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateFrom { get; set; }

    /// <summary>对账日期结束（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateTo { get; set; }

    /// <summary>显式到期日开始（含当天；**只命中登记了到期日的对账单**；留空 = 不限）</summary>
    public DateTime? DueDateFrom { get; set; }

    /// <summary>显式到期日结束（含当天；**只命中登记了到期日的对账单**；留空 = 不限）</summary>
    public DateTime? DueDateTo { get; set; }

    /// <summary>对账单状态筛选（recorded 默认 / draft / voided / all；草稿与已作废金额永不并入有效对账证据合计）</summary>
    public string? StatementStatus { get; set; }

    /// <summary>分配状态筛选（none / historical_only / partial / full；留空 = 全部；只用持久化分摊行判定）</summary>
    public string? AllocationState { get; set; }

    /// <summary>账龄 as-of 日期（显式；留空 = 当天；账龄只相对该日期与显式到期日计算）</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = AgencyServiceFeeReconciliationRules.DefaultPageSize;

    /// <summary>
    /// 归一化并校验：对账单 Id / 协议 Id / 币种 / 状态 / 分配状态 / 服务来源必须是合法取值，
    /// 日期区间不得倒置，关键字长度有界，分页参数钳制到有界范围；非法取值一律抛业务异常（参数错误），
    /// 不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        if (StatementId is <= 0)
            throw BusinessException.InvalidParameter(
                $"对账单 Id 必须为正整数：{StatementId}");

        if (CustomerId is <= 0) CustomerId = null;
        if (AgreementId is <= 0) AgreementId = null;

        Currency = string.IsNullOrWhiteSpace(Currency)
            ? null
            : AgencyServiceFeeStatementRules.NormalizeCurrencyStrict(Currency);

        SourceType = AgencyServiceFeeReconciliationRules.NormalizeSourceTypeFilter(SourceType);
        StatementStatus = AgencyServiceFeeReconciliationRules.NormalizeStatementStatusFilter(StatementStatus);
        AllocationState = AgencyServiceFeeReconciliationRules.NormalizeAllocationFilter(AllocationState);

        StatementDateFrom = StatementDateFrom?.Date;
        StatementDateTo = StatementDateTo?.Date;
        if (StatementDateFrom.HasValue && StatementDateTo.HasValue && StatementDateFrom > StatementDateTo)
        {
            throw BusinessException.InvalidParameter(
                $"对账日期开始 {StatementDateFrom:yyyy-MM-dd} 不能晚于结束 {StatementDateTo:yyyy-MM-dd}");
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
            else if (Keyword.Length > AgencyServiceFeeReconciliationRules.MaxKeywordLength)
            {
                throw BusinessException.InvalidParameter(
                    $"关键字长度不能超过 {AgencyServiceFeeReconciliationRules.MaxKeywordLength} 个字符");
            }
        }

        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = AgencyServiceFeeReconciliationRules.DefaultPageSize;
        if (PageSize > AgencyServiceFeeReconciliationRules.MaxPageSize)
            PageSize = AgencyServiceFeeReconciliationRules.MaxPageSize;
    }
}

/// <summary>
/// 工作台中的单张代理服务费对账单行（ERP-072，只读派生）。
/// <para>金额三类严格分列：<see cref="StatementAmount"/>（对账单服务端合计证据）、
/// <see cref="ActiveAllocatedAmount"/>（ERP-071 **有效**收款分摊合计）、
/// <see cref="RemainingAmount"/>（算术剩余证据 = 合计 − 有效分摊），三者不相加减成任何
/// 「应收余额 / 已收款 / 已结清 / 逾期」结论。</para>
/// <para>未知处理：命中系统有界上限时金额与计数为 <c>null</c>（未知），绝不用 0 顶替；
/// 超过源规则的情形按「无效证据」显示且**不裁剪、不修复**。</para>
/// </summary>
public sealed class AgencyServiceFeeReconciliationStatementRow
{
    // ============ 对账单身份 ============

    /// <summary>对账单证据 Id</summary>
    public long StatementId { get; init; }

    /// <summary>对账单号（登记当时的持久化文本）</summary>
    public string StatementNo { get; init; } = string.Empty;

    /// <summary>对账单身份文案（与 ERP-070 同源；空号显式说明未填）</summary>
    public string IdentityText { get; init; } = string.Empty;

    /// <summary>对账日期</summary>
    public DateTime StatementDate { get; init; }

    /// <summary>服务期间起始日期</summary>
    public DateTime ServicePeriodFrom { get; init; }

    /// <summary>服务期间结束日期</summary>
    public DateTime ServicePeriodTo { get; init; }

    /// <summary>服务期间文案</summary>
    public string ServicePeriodText { get; init; } = string.Empty;

    // ============ 客户（快照 + 当前可用性） ============

    /// <summary>客户 Id</summary>
    public long CustomerId { get; init; }

    /// <summary>客户编码快照</summary>
    public string CustomerCode { get; init; } = string.Empty;

    /// <summary>客户名称快照（客户改名 / 停用 / 删除后保持登记当时口径）</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>客户当前是否可用（存在、未删除且启用）；快照永远可读，不因停用 / 删除被回填</summary>
    public bool CustomerAvailable { get; init; }

    /// <summary>客户可用性文案（不可用时照实说明，绝不猜测与补全）</summary>
    public string CustomerAvailabilityText { get; init; } = string.Empty;

    // ============ 币种与对账单合计证据（原币；绝不换算、绝不跨币种合并） ============

    /// <summary>币种（对账单币种；分摊行必须与它一致才计入有效合计）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种小数位</summary>
    public int AmountDecimals { get; init; }

    /// <summary>对账单合计证据（服务端按已校验行计算的持久化 `TotalAmount`，原币）</summary>
    public decimal StatementAmount { get; init; }

    /// <summary>对账单合计文案（原币 + 币种精度）</summary>
    public string StatementAmountText { get; init; } = string.Empty;

    // ============ 对账单状态（草稿 / 已作废单独标注，排除在有效合计外） ============

    /// <summary>对账单状态（0 草稿 / 1 已登记 / 2 已作废）</summary>
    public int StatementStatus { get; init; }

    /// <summary>对账单状态文案</summary>
    public string StatementStatusText { get; init; } = string.Empty;

    /// <summary>是否计入有效对账与账龄合计（= 已登记）</summary>
    public bool IsActiveEvidence { get; init; }

    /// <summary>是否草稿（工作数据，尚未形成登记证据）</summary>
    public bool IsDraft { get; init; }

    /// <summary>是否已作废（历史证据，金额单独可见但不并入有效合计）</summary>
    public bool IsVoided { get; init; }

    /// <summary>登记时间</summary>
    public DateTime? RecordedAt { get; init; }

    /// <summary>登记人</summary>
    public string RecordedBy { get; init; } = string.Empty;

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因（历史留痕）</summary>
    public string VoidReason { get; init; } = string.Empty;

    // ============ 显式到期日（可选证据；留空 = 未知） ============

    /// <summary>显式到期日（原样呈现；<c>null</c> = 未登记）</summary>
    public DateTime? DueDate { get; init; }

    /// <summary>到期日是否已知（只按持久化到期日判定；未知 ≠ 当天到期）</summary>
    public bool DueDateKnown { get; init; }

    /// <summary>到期日文案（未知时显式说明「未登记 / 不推算」）</summary>
    public string DueDateText { get; init; } = string.Empty;

    // ============ 协议身份（ERP-069 快照 + 当前可用性） ============

    /// <summary>关联协议 Id</summary>
    public long AgreementId { get; init; }

    /// <summary>协议号快照</summary>
    public string AgreementNo { get; init; } = string.Empty;

    /// <summary>协议币种快照</summary>
    public string AgreementCurrency { get; init; } = string.Empty;

    /// <summary>协议费用条款文案快照（只作只读身份留痕，不参与任何金额派生）</summary>
    public string AgreementTermsText { get; init; } = string.Empty;

    /// <summary>关联协议当前是否可用（存在、未删除且仍为已登记）</summary>
    public bool AgreementAvailable { get; init; }

    /// <summary>协议可用性文案</summary>
    public string AgreementAvailabilityText { get; init; } = string.Empty;

    // ============ 服务来源（显式持久化行引用，按来源类型汇总） ============

    /// <summary>行数（全部持久化行的计数）；<c>null</c> = 未知（命中上限）</summary>
    public int? LineCount { get; init; }

    /// <summary>来源类型摘要（如「销售订单 ×2 / 装柜清单 ×1」；未知时显式说明）</summary>
    public string SourceSummaryText { get; init; } = string.Empty;

    /// <summary>来源身份摘要（有界条数；空 = 无持久化行）</summary>
    public List<string> SourceIdentities { get; init; } = new();

    /// <summary>服务来源证据是否命中系统有界上限（是则不给部分结论）</summary>
    public bool SourceTruncated { get; init; }

    // ============ 有效收款分摊证据（ERP-071，本维度） ============

    /// <summary>有效已分摊金额（原币）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>有效分摊行数；<c>null</c> = 未知（命中上限）</summary>
    public int? ActiveAllocationCount { get; init; }

    /// <summary>有效分摊涉及的收款单数；<c>null</c> = 未知（命中上限）</summary>
    public int? ActiveReceiptCount { get; init; }

    /// <summary>有效分摊文案（无分摊行时显式说明证据缺口 ≠ 未付款）</summary>
    public string ActiveAllocationText { get; init; } = string.Empty;

    // ============ 历史 / 无效分摊证据（保留可见，绝不并入有效合计） ============

    /// <summary>已作废分摊行数；<c>null</c> = 未知（命中上限）</summary>
    public int? VoidedAllocationCount { get; init; }

    /// <summary>已作废分摊金额；<c>null</c> = 未知（命中上限）</summary>
    public decimal? VoidedAllocationAmount { get; init; }

    /// <summary>无效 / 无法确认分摊行数（收款单已删除 / 已取消、客户或币种快照不一致）；<c>null</c> = 未知</summary>
    public int? InvalidAllocationCount { get; init; }

    /// <summary>无效 / 无法确认分摊金额；<c>null</c> = 未知</summary>
    public decimal? InvalidAllocationAmount { get; init; }

    /// <summary>历史证据文案（已作废与无效证据的可见性说明）</summary>
    public string HistoricalEvidenceText { get; init; } = string.Empty;

    // ============ 算术剩余证据 ============

    /// <summary>算术剩余证据 = 对账单合计 − 有效已分摊金额；<c>null</c> = 未知（不给部分合计）</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>剩余证据状态（known / over_allocated / unknown）</summary>
    public string RemainingState { get; init; } = AgencyServiceFeeReconciliationRules.RemainingKnown;

    /// <summary>剩余证据状态文案</summary>
    public string RemainingStateText { get; init; } = string.Empty;

    // ============ 分配状态与链接状态 ============

    /// <summary>分配状态（none / historical_only / partial / full / over_allocated / unknown）</summary>
    public string AllocationState { get; init; } = AgencyServiceFeeReconciliationRules.AllocationNone;

    /// <summary>分配状态文案</summary>
    public string AllocationStateText { get; init; } = string.Empty;

    /// <summary>链接状态（available / unavailable）：不可用时照实标注，绝不修复、改派</summary>
    public string LinkState { get; init; } = AgencyServiceFeeReconciliationRules.LinkStateAvailable;

    /// <summary>链接状态文案</summary>
    public string LinkStateText { get; init; } = string.Empty;

    /// <summary>是否存在无效 / 无法确认证据（单独成列，绝不并入有效合计）</summary>
    public bool HasInvalidOrUnavailableEvidence { get; init; }

    // ============ 账龄（只对有显式到期日的对账单计算） ============

    /// <summary>账龄分桶；<c>null</c> = 未知到期日（独立分组，不计算账龄、不入任何桶）</summary>
    public string? AgingBucket { get; init; }

    /// <summary>账龄分桶文案（未知到期日显式说明「不计算」）</summary>
    public string AgingBucketText { get; init; } = string.Empty;

    /// <summary>逾期天数（as-of − 到期日；≤ 0 = 未到期）；<c>null</c> = 未知到期日</summary>
    public int? OverdueDays { get; init; }

    /// <summary>账龄文案</summary>
    public string AgingText { get; init; } = string.Empty;

    // ============ 行说明 ============

    /// <summary>行说明（证据事实与未知 / 无效处理说明）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 账龄桶 / 「未知到期日」分组合计（ERP-072，只读派生；金额按原币，绝不跨币种合并）。
/// <para><see cref="IsAgingBucket"/> = <c>false</c> 表示这是**独立分组**（未知到期日），不参与任何账龄桶合计。</para>
/// </summary>
public sealed class AgencyServiceFeeReconciliationBucketTotal
{
    /// <summary>桶名（五个账龄桶之一，或 <c>unknown_due_date</c>）</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>桶中文文案</summary>
    public string BucketText { get; init; } = string.Empty;

    /// <summary>是否账龄桶（未知到期日为 false）</summary>
    public bool IsAgingBucket { get; init; }

    /// <summary>本桶内的对账单张数（只统计有效证据 = 已登记）</summary>
    public int StatementCount { get; init; }

    /// <summary>本桶对账单合计（原币；有效证据）</summary>
    public decimal StatementAmount { get; init; }

    /// <summary>本桶有效已分摊金额（原币）；<c>null</c> = 未知（命中上限或存在无效证据）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>本桶算术剩余证据（原币）；<c>null</c> = 未知（不给部分合计）</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>本桶剩余证据未知的对账单张数</summary>
    public int UnknownRemainingStatementCount { get; init; }

    /// <summary>桶说明</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 「未知到期日」独立分组（ERP-072，只读派生）：这些对账单没有登记显式到期日，因此账龄**不计算**，
/// 仍按「客户 + 币种」隔离，**绝不并入**任何账龄桶。
/// </summary>
public sealed class AgencyServiceFeeReconciliationUnknownDueDateGroup
{
    /// <summary>客户 Id</summary>
    public long CustomerId { get; init; }

    /// <summary>客户编码快照</summary>
    public string CustomerCode { get; init; } = string.Empty;

    /// <summary>客户名称快照</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种小数位</summary>
    public int AmountDecimals { get; init; }

    /// <summary>分组内对账单张数（只统计有效证据）</summary>
    public int StatementCount { get; init; }

    /// <summary>分组内对账单合计（原币）</summary>
    public decimal StatementAmount { get; init; }

    /// <summary>分组内有效已分摊金额（原币）；<c>null</c> = 未知</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>分组内算术剩余证据（原币）；<c>null</c> = 未知</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>分组内对账单身份（有界条数；超出只给计数）</summary>
    public List<string> StatementIdentities { get; init; } = new();

    /// <summary>分组说明（未知到期日不计算账龄，绝不推算）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 「客户 + 币种」证据分组（ERP-072，只读派生）：组内金额一律同一币种原币，绝不跨币种合并；
/// 只有**有效证据（已登记对账单）**计入 <see cref="StatementAmount"/> / <see cref="ActiveAllocatedAmount"/> /
/// <see cref="RemainingAmount"/> 与账龄桶合计，草稿与已作废只在计数与行内可见。
/// </summary>
public sealed class AgencyServiceFeeReconciliationGroup
{
    // ============ 客户（快照 + 当前可用性） ============

    /// <summary>客户 Id</summary>
    public long CustomerId { get; init; }

    /// <summary>客户编码快照</summary>
    public string CustomerCode { get; init; } = string.Empty;

    /// <summary>客户名称快照</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>客户当前是否可用</summary>
    public bool CustomerAvailable { get; init; }

    /// <summary>客户可用性文案</summary>
    public string CustomerAvailabilityText { get; init; } = string.Empty;

    // ============ 币种 ============

    /// <summary>币种（组内唯一币种）</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种小数位</summary>
    public int AmountDecimals { get; init; }

    // ============ 计数（本页） ============

    /// <summary>组内本页对账单张数（全部状态）</summary>
    public int StatementCount { get; init; }

    /// <summary>组内本页有效证据（已登记）张数</summary>
    public int ActiveEvidenceStatementCount { get; init; }

    /// <summary>组内本页草稿张数（不计入任何有效合计）</summary>
    public int DraftStatementCount { get; init; }

    /// <summary>组内本页已作废张数（历史证据，不计入任何有效合计）</summary>
    public int VoidedStatementCount { get; init; }

    /// <summary>组内本页有显式到期日的张数（有效证据）</summary>
    public int KnownDueDateStatementCount { get; init; }

    /// <summary>组内本页未知到期日的张数（账龄不计算，单独成组）</summary>
    public int UnknownDueDateStatementCount { get; init; }

    /// <summary>组内本页存在无效 / 无法确认证据的张数</summary>
    public int InvalidEvidenceStatementCount { get; init; }

    /// <summary>组内本页剩余证据未知的张数</summary>
    public int UnknownRemainingStatementCount { get; init; }

    /// <summary>组内本页「有效已分摊超过对账单合计」（无效证据）的张数</summary>
    public int OverAllocatedStatementCount { get; init; }

    // ============ 金额（原币；只统计有效证据） ============

    /// <summary>组内有效证据对账单合计（原币；恒可确认）</summary>
    public decimal StatementAmount { get; init; }

    /// <summary>组内有效已分摊金额（原币）；<c>null</c> = 未知（命中上限）</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>组内算术剩余证据（原币）；<c>null</c> = 未知（命中上限或存在无效证据行）</summary>
    public decimal? RemainingAmount { get; init; }

    // ============ 账龄与未知到期日（都不跨币种、不跨客户） ============

    /// <summary>组内账龄分桶合计（只统计有显式到期日的有效证据）</summary>
    public List<AgencyServiceFeeReconciliationBucketTotal> Buckets { get; init; } = new();

    /// <summary>组内「未知到期日」分组合计（**独立分组，不参与任何账龄桶**）</summary>
    public AgencyServiceFeeReconciliationBucketTotal UnknownDueDate { get; init; } = new();

    // ============ 组内行（本页，只读） ============

    /// <summary>组内本页对账单行（按稳定排序）</summary>
    public List<AgencyServiceFeeReconciliationStatementRow> Statements { get; init; } = new();

    /// <summary>组说明（币种隔离、证据三类分列、未知与无效处理）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 币种汇总（ERP-072，只读派生）：每个币种一行，**不存在**任何跨币种总额字段
/// （本类型刻意没有任何把多个币种相加的字段）。
/// </summary>
public sealed class AgencyServiceFeeReconciliationCurrencySummary
{
    /// <summary>币种</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>币种小数位</summary>
    public int AmountDecimals { get; init; }

    /// <summary>本币种本页对账单张数（全部状态）</summary>
    public int StatementCount { get; init; }

    /// <summary>本币种本页有效证据（已登记）张数</summary>
    public int ActiveEvidenceStatementCount { get; init; }

    /// <summary>本币种本页草稿张数</summary>
    public int DraftStatementCount { get; init; }

    /// <summary>本币种本页已作废张数</summary>
    public int VoidedStatementCount { get; init; }

    /// <summary>本币种本页有显式到期日的张数（有效证据）</summary>
    public int KnownDueDateStatementCount { get; init; }

    /// <summary>本币种本页未知到期日的张数</summary>
    public int UnknownDueDateStatementCount { get; init; }

    /// <summary>本币种本页存在无效 / 无法确认证据的张数</summary>
    public int InvalidEvidenceStatementCount { get; init; }

    /// <summary>本币种有效证据对账单合计（原币）</summary>
    public decimal StatementAmount { get; init; }

    /// <summary>本币种有效已分摊金额（原币）；<c>null</c> = 未知</summary>
    public decimal? ActiveAllocatedAmount { get; init; }

    /// <summary>本币种算术剩余证据（原币）；<c>null</c> = 未知</summary>
    public decimal? RemainingAmount { get; init; }

    /// <summary>本币种剩余证据未知的张数</summary>
    public int UnknownRemainingStatementCount { get; init; }

    /// <summary>本币种「有效已分摊超过对账单合计」（无效证据）的张数</summary>
    public int OverAllocatedStatementCount { get; init; }

    /// <summary>本币种账龄分桶合计（只统计有显式到期日的有效证据）</summary>
    public List<AgencyServiceFeeReconciliationBucketTotal> Buckets { get; init; } = new();

    /// <summary>本币种「未知到期日」分组合计（独立分组）</summary>
    public AgencyServiceFeeReconciliationBucketTotal UnknownDueDate { get; init; } = new();
}

/// <summary>
/// 代理服务费对账与账龄工作台报表（ERP-072，只读派生；不落库、不改对账单证据 / 分摊行 / 收款单 /
/// 协议 / 客户 / 订单 / 装柜清单 / 发票 / 库存 / 财务 / 税务记录，不新增或修改任何表列）。
/// <para>金额一律按原币分别成行：<see cref="Currencies"/> 每个币种一行，**不存在**任何跨币种总额字段。</para>
/// </summary>
public sealed class AgencyServiceFeeReconciliationReport
{
    // ============ 筛选回显（归一化后的实际取值） ============

    /// <summary>对账单身份筛选回显</summary>
    public long? StatementId { get; init; }

    /// <summary>客户筛选回显</summary>
    public long? CustomerId { get; init; }

    /// <summary>协议身份筛选回显</summary>
    public long? AgreementId { get; init; }

    /// <summary>币种筛选回显（空 = 全部币种）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>服务来源筛选回显（空 = 全部来源）</summary>
    public string SourceType { get; init; } = string.Empty;

    /// <summary>服务来源筛选文案</summary>
    public string SourceTypeText { get; init; } = string.Empty;

    /// <summary>关键字回显</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>对账日期开始回显</summary>
    public DateTime? StatementDateFrom { get; init; }

    /// <summary>对账日期结束回显</summary>
    public DateTime? StatementDateTo { get; init; }

    /// <summary>显式到期日开始回显（只命中登记了到期日的对账单）</summary>
    public DateTime? DueDateFrom { get; init; }

    /// <summary>显式到期日结束回显（只命中登记了到期日的对账单）</summary>
    public DateTime? DueDateTo { get; init; }

    /// <summary>对账单状态筛选回显（recorded 默认）</summary>
    public string StatementStatus { get; init; } = AgencyServiceFeeReconciliationRules.StatementStatusRecorded;

    /// <summary>对账单状态筛选文案</summary>
    public string StatementStatusText { get; init; } = string.Empty;

    /// <summary>分配状态筛选回显（空 = 全部）</summary>
    public string AllocationState { get; init; } = string.Empty;

    /// <summary>分配状态筛选文案</summary>
    public string AllocationStateText { get; init; } =
        "全部分配状态（none / historical_only / partial / full）";

    /// <summary>账龄 as-of 日期（显式；账龄只相对该日期与显式到期日计算）</summary>
    public DateTime AsOfDate { get; init; }

    /// <summary>as-of 日期文案（界面原样展示，明确账龄基准日）</summary>
    public string AsOfDateText { get; init; } = string.Empty;

    // ============ 分页 ============

    /// <summary>符合筛选条件的未删除对账单总数</summary>
    public int Total { get; init; }

    /// <summary>当前页码</summary>
    public int Page { get; init; }

    /// <summary>每页条数</summary>
    public int PageSize { get; init; }

    /// <summary>总页数</summary>
    public int TotalPages { get; init; }

    /// <summary>本页对账单张数</summary>
    public int PageStatementCount { get; init; }

    // ============ 本页计数（只统计本次返回页） ============

    /// <summary>本页有效证据（已登记）张数</summary>
    public int ActiveEvidencePageStatementCount { get; init; }

    /// <summary>本页草稿张数（不计入任何有效合计）</summary>
    public int DraftPageStatementCount { get; init; }

    /// <summary>本页已作废张数（历史证据，不计入任何有效合计）</summary>
    public int VoidedPageStatementCount { get; init; }

    /// <summary>本页有显式到期日的张数</summary>
    public int KnownDueDatePageStatementCount { get; init; }

    /// <summary>本页未知到期日的张数（账龄不计算，单独成组）</summary>
    public int UnknownDueDatePageStatementCount { get; init; }

    /// <summary>本页无任何持久化分摊行的张数</summary>
    public int NoAllocationPageStatementCount { get; init; }

    /// <summary>本页仅有历史 / 无效分摊行的张数</summary>
    public int HistoricalOnlyPageStatementCount { get; init; }

    /// <summary>本页存在无效 / 无法确认证据的张数</summary>
    public int InvalidEvidencePageStatementCount { get; init; }

    /// <summary>本页「有效已分摊超过对账单合计」（无效证据）的张数</summary>
    public int OverAllocatedPageStatementCount { get; init; }

    /// <summary>本页剩余证据未知的张数</summary>
    public int UnknownRemainingPageStatementCount { get; init; }

    /// <summary>本页服务来源证据命中有界上限的张数（来源摘要按「未知」显示）</summary>
    public int SourceTruncatedPageStatementCount { get; init; }

    // ============ 有界读取命中（未知处理） ============

    /// <summary>本次读取是否命中系统有界上限（分摊行装载上限）</summary>
    public bool Truncated { get; init; }

    /// <summary>命中上限说明（未命中为空）</summary>
    public string TruncatedNote { get; init; } = string.Empty;

    // ============ 汇总（只统计本页；按币种分离，绝无跨币种总额） ============

    /// <summary>币种汇总（每币种一行；**没有**跨币种总额）</summary>
    public List<AgencyServiceFeeReconciliationCurrencySummary> Currencies { get; init; } = new();

    /// <summary>「客户 + 币种」证据分组（含账龄桶与组内行）</summary>
    public List<AgencyServiceFeeReconciliationGroup> Groups { get; init; } = new();

    /// <summary>「未知到期日」独立分组（按客户 + 币种；不参与任何账龄桶）</summary>
    public List<AgencyServiceFeeReconciliationUnknownDueDateGroup> UnknownDueDateGroups { get; init; } = new();

    // ============ 口径文案（接口、界面与文档同源） ============

    /// <summary>派生口径</summary>
    public string RuleText { get; init; } = AgencyServiceFeeReconciliationRules.RuleText;

    /// <summary>账龄口径</summary>
    public string AgingRuleText { get; init; } = AgencyServiceFeeReconciliationRules.AgingRuleText;

    /// <summary>币种隔离口径</summary>
    public string NoCrossCurrencyText { get; init; } = AgencyServiceFeeReconciliationRules.NoCrossCurrencyText;

    /// <summary>范围口径</summary>
    public string ScopeText { get; init; } = AgencyServiceFeeReconciliationRules.ScopeText;

    /// <summary>模块边界</summary>
    public string BoundaryText { get; init; } = AgencyServiceFeeReconciliationRules.BoundaryText;

    /// <summary>未知到期日说明</summary>
    public string UnknownDueDateNote { get; init; } = AgencyServiceFeeReconciliationRules.UnknownDueDateNote;

    /// <summary>无效 / 无法确认证据说明</summary>
    public string InvalidEvidenceNote { get; init; } = AgencyServiceFeeReconciliationRules.InvalidEvidenceNote;

    /// <summary>来源失效 fail-closed 说明</summary>
    public string SourceFailClosedText { get; init; } = AgencyServiceFeeReconciliationRules.SourceFailClosedText;

    /// <summary>导出说明</summary>
    public string ExportNote { get; init; } = AgencyServiceFeeReconciliationRules.ExportNote;

    /// <summary>证据维度分离说明</summary>
    public string DimensionSeparationText { get; init; } =
        AgencyServiceFeeReconciliationRules.DimensionSeparationText;

    /// <summary>历史证据只读说明</summary>
    public string HistoricalEvidenceText { get; init; } =
        AgencyServiceFeeReconciliationRules.HistoricalEvidenceText;

    /// <summary>只读边界说明</summary>
    public string ReadOnlyText { get; init; } = AgencyServiceFeeReconciliationRules.ReadOnlyText;
}

/// <summary>
/// 单张对账单的分摊证据明细行（ERP-072，只读；含已作废历史）。
/// <para>只展示**既有持久化证据**：分摊金额、两侧与客户快照、登记 / 作废留痕与当前可用性标注；
/// 不重算、不改派、不修复任何来源记录。</para>
/// </summary>
public sealed class AgencyServiceFeeReconciliationAllocationRow
{
    /// <summary>分摊行 Id</summary>
    public long AllocationId { get; init; }

    /// <summary>收款单 Id</summary>
    public long ReceiptId { get; init; }

    /// <summary>收款单号快照</summary>
    public string ReceiptNo { get; init; } = string.Empty;

    /// <summary>收款日期快照</summary>
    public DateTime ReceiptDate { get; init; }

    /// <summary>收款单状态快照</summary>
    public int ReceiptStatus { get; init; }

    /// <summary>收款单状态文案快照</summary>
    public string ReceiptStatusText { get; init; } = string.Empty;

    /// <summary>收款单当前是否可用（存在、未删除且未取消）</summary>
    public bool ReceiptAvailable { get; init; }

    /// <summary>收款单可用性文案</summary>
    public string ReceiptAvailabilityText { get; init; } = string.Empty;

    /// <summary>分摊金额（原币）</summary>
    public decimal AllocatedAmount { get; init; }

    /// <summary>分摊金额文案（原币 + 币种精度）</summary>
    public string AmountText { get; init; } = string.Empty;

    /// <summary>币种</summary>
    public string Currency { get; init; } = CurrencyAmountRules.DefaultCurrency;

    /// <summary>分摊行状态（1 有效 / 2 已作废）</summary>
    public int Status { get; init; }

    /// <summary>分摊行状态文案</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>是否计入有效已分摊合计（= 有效且来源可确认）</summary>
    public bool IsEffective { get; init; }

    /// <summary>计入 / 不计入有效合计的原因说明</summary>
    public string EffectivenessText { get; init; } = string.Empty;

    /// <summary>登记时间</summary>
    public DateTime AllocatedAt { get; init; }

    /// <summary>登记人</summary>
    public string AllocatedBy { get; init; } = string.Empty;

    /// <summary>作废时间</summary>
    public DateTime? VoidedAt { get; init; }

    /// <summary>作废原因</summary>
    public string VoidReason { get; init; } = string.Empty;

    /// <summary>备注</summary>
    public string Remark { get; init; } = string.Empty;
}

/// <summary>
/// 单张对账单的对账证据明细（ERP-072，只读派生）：与列表行**完全同源**（同一派生方法），
/// 另附有界分摊行清单（含已作废历史）与授权复核说明。
/// <para>打开明细时**重新校验**登录身份与既有「角色 → 菜单」模块授权，并重新读取权威来源；
/// 未认证 / 未授权 / 对账单不存在或已删除一律**拒绝**（fail closed），不返回任何部分证据。</para>
/// </summary>
public sealed class AgencyServiceFeeReconciliationStatementDetail
{
    /// <summary>对账单 Id</summary>
    public long StatementId { get; init; }

    /// <summary>账龄 as-of 日期（显式；与列表同一基准）</summary>
    public DateTime AsOfDate { get; init; }

    /// <summary>与列表同源的对账单行（同一派生方法，界面不会出现两套口径）</summary>
    public AgencyServiceFeeReconciliationStatementRow Row { get; init; } = new();

    /// <summary>分摊行清单（有界，含已作废历史；按登记时间倒序）</summary>
    public List<AgencyServiceFeeReconciliationAllocationRow> Allocations { get; init; } = new();

    /// <summary>分摊行清单是否命中系统有界上限（是则不给部分结论）</summary>
    public bool AllocationsTruncated { get; init; }

    /// <summary>授权复核说明</summary>
    public string AuthorizationNote { get; init; } = string.Empty;

    /// <summary>派生口径</summary>
    public string RuleText { get; init; } = AgencyServiceFeeReconciliationRules.RuleText;

    /// <summary>账龄口径</summary>
    public string AgingRuleText { get; init; } = AgencyServiceFeeReconciliationRules.AgingRuleText;

    /// <summary>币种隔离口径</summary>
    public string NoCrossCurrencyText { get; init; } = AgencyServiceFeeReconciliationRules.NoCrossCurrencyText;

    /// <summary>模块边界</summary>
    public string BoundaryText { get; init; } = AgencyServiceFeeReconciliationRules.BoundaryText;

    /// <summary>无效 / 无法确认证据说明</summary>
    public string InvalidEvidenceNote { get; init; } = AgencyServiceFeeReconciliationRules.InvalidEvidenceNote;

    /// <summary>来源失效 fail-closed 说明</summary>
    public string SourceFailClosedText { get; init; } = AgencyServiceFeeReconciliationRules.SourceFailClosedText;
}

/// <summary>
/// 代理服务费对账与账龄工作台元数据（ERP-072，只读）：白名单、有界额度与接口 / 界面 / 文档同源的口径文案。
/// </summary>
public sealed class AgencyServiceFeeReconciliationMetadataDto
{
    /// <summary>支持的账龄分桶（互斥且完整）</summary>
    public List<string> SupportedBuckets { get; init; } = new();

    /// <summary>支持的分配状态筛选取值</summary>
    public List<string> SupportedAllocationFilters { get; init; } = new();

    /// <summary>支持的对账单状态筛选取值</summary>
    public List<string> SupportedStatementStatuses { get; init; } = new();

    /// <summary>支持的服务来源类型（复用 ERP-070 白名单）</summary>
    public List<string> SupportedSourceTypes { get; init; } = new();

    /// <summary>支持币种</summary>
    public List<string> SupportedCurrencies { get; init; } = new();

    /// <summary>默认每页条数</summary>
    public int DefaultPageSize { get; init; } = AgencyServiceFeeReconciliationRules.DefaultPageSize;

    /// <summary>每页条数上限</summary>
    public int MaxPageSize { get; init; } = AgencyServiceFeeReconciliationRules.MaxPageSize;

    /// <summary>本页分摊行装载上限</summary>
    public int MaxAllocationRowsPerPage { get; init; } =
        AgencyServiceFeeReconciliationRules.MaxAllocationRowsPerPage;

    /// <summary>明细分摊行上限</summary>
    public int MaxDetailsPerStatement { get; init; } = AgencyServiceFeeReconciliationRules.MaxDetailsPerStatement;

    /// <summary>明细要求的既有菜单编码</summary>
    public string RequiredMenuCode { get; init; } = AgencyServiceFeeReconciliationRules.RequiredMenuCode;

    /// <summary>明细要求菜单的中文文案</summary>
    public string RequiredMenuText { get; init; } = AgencyServiceFeeReconciliationRules.RequiredMenuText;

    /// <summary>派生口径</summary>
    public string RuleText { get; init; } = AgencyServiceFeeReconciliationRules.RuleText;

    /// <summary>账龄口径</summary>
    public string AgingRuleText { get; init; } = AgencyServiceFeeReconciliationRules.AgingRuleText;

    /// <summary>币种隔离口径</summary>
    public string NoCrossCurrencyText { get; init; } = AgencyServiceFeeReconciliationRules.NoCrossCurrencyText;

    /// <summary>范围口径</summary>
    public string ScopeText { get; init; } = AgencyServiceFeeReconciliationRules.ScopeText;

    /// <summary>模块边界</summary>
    public string BoundaryText { get; init; } = AgencyServiceFeeReconciliationRules.BoundaryText;

    /// <summary>未知到期日说明</summary>
    public string UnknownDueDateNote { get; init; } = AgencyServiceFeeReconciliationRules.UnknownDueDateNote;

    /// <summary>无效 / 无法确认证据说明</summary>
    public string InvalidEvidenceNote { get; init; } = AgencyServiceFeeReconciliationRules.InvalidEvidenceNote;

    /// <summary>来源失效 fail-closed 说明</summary>
    public string SourceFailClosedText { get; init; } = AgencyServiceFeeReconciliationRules.SourceFailClosedText;

    /// <summary>导出说明</summary>
    public string ExportNote { get; init; } = AgencyServiceFeeReconciliationRules.ExportNote;

    /// <summary>证据维度分离说明</summary>
    public string DimensionSeparationText { get; init; } =
        AgencyServiceFeeReconciliationRules.DimensionSeparationText;

    /// <summary>历史证据只读说明</summary>
    public string HistoricalEvidenceText { get; init; } =
        AgencyServiceFeeReconciliationRules.HistoricalEvidenceText;

    /// <summary>只读边界说明</summary>
    public string ReadOnlyText { get; init; } = AgencyServiceFeeReconciliationRules.ReadOnlyText;
}

