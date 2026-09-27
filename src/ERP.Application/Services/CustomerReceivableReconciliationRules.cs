using ERP.Application.Common;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 客户应收账款对账与账龄工作台的纯规则（ERP-074，无数据库依赖，便于逐条单测）：
/// 账龄分桶（只对**显式持久化到期日**计算）、分配状态与剩余证据状态、发票状态 / 分配状态筛选归一化、
/// 有界上限与接口、界面、文档同源的口径文案。
/// <para>关键口径：本工作台只是 ERP-055 客户销项发票证据与 ERP-073「客户收款 → 销项发票」分摊证据之上的
/// <strong>只读派生视图</strong>：不新增表、不新增列、不回填、不改写任何来源记录；算术剩余证据 = 发票含税总额 −
/// 有效 ERP-073 分摊合计；账龄只对显式持久化到期日计算、到期日为空则进入「未知到期日」分组（当前 ERP-055
/// 发票证据模型尚未持久化到期日，因此现有发票全部落入「未知到期日」分组）；不同币种分别成行、绝无跨币种总额。</para>
/// <para>边界：本规则只做校验与纯计算，不写库、不开票、不记账、不核销、不收款或付款、不催收或联系客户，
/// 也不改写发票证据、收款分摊证据、收款单、客户主数据、销售订单、装柜清单、单证、库存、费用、退税与结算记录。</para>
/// </summary>
public static class CustomerReceivableReconciliationRules
{
    // ==================== 1. 有界上限 ====================

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多发票证据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>本页分摊行单次装载上限（超出即按「未知」处理：绝不给部分合计）</summary>
    public const int MaxAllocationRowsPerPage = 2000;

    /// <summary>单张发票明细中返回的分摊行上限（有界）</summary>
    public const int MaxDetailsPerInvoice = 100;

    /// <summary>「未知到期日」分组中展示的发票身份条数上限（有界，超出只给计数）</summary>
    public const int MaxUnknownDueIdentitiesPerGroup = 10;

    /// <summary>打开明细时要求的既有菜单编码（复用既有「角色 → 菜单」授权口径；与客户资料菜单同码）</summary>
    public const string RequiredMenuCode = "customer";

    /// <summary>要求菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "客户资料";

    /// <summary>系统支持的币种（与发票 / 收款单 / 订单币种枚举同源）</summary>
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

    /// <summary>未知到期日分组的桶名（是独立分组、不是账龄桶）</summary>
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

    // ==================== 3. 分配状态（只按持久化 ERP-073 分摊行派生） ====================

    /// <summary>没有任何持久化分摊行（既无有效行也无历史行）</summary>
    public const string AllocationNone = "none";

    /// <summary>没有有效分摊行，但存在未删除的历史 / 无效分摊行（单独可见，绝不并入有效合计）</summary>
    public const string AllocationHistoricalOnly = "historical_only";

    /// <summary>有效已分摊金额 &gt; 0 且 &lt; 发票含税总额（部分分摊）</summary>
    public const string AllocationPartial = "partial";

    /// <summary>有效已分摊金额 ≥ 发票含税总额（全额分摊）</summary>
    public const string AllocationFull = "full";

    /// <summary>有效已分摊金额 &gt; 发票含税总额（与源规则矛盾：无效证据，不轧成 0 或负数）</summary>
    public const string AllocationOverAllocated = "over_allocated";

    /// <summary>命中系统有界读取上限：分摊证据不完整，分配状态与金额一律按「未知」显示</summary>
    public const string AllocationUnknown = "unknown";

    /// <summary>支持的分配状态筛选取值（over_allocated / unknown 只作为读取时派生状态，不提供为筛选）</summary>
    public static readonly string[] SupportedAllocationFilters =
    {
        AllocationNone, AllocationHistoricalOnly, AllocationPartial, AllocationFull,
    };

    /// <summary>分配状态中文文案（接口、界面与文档同源）</summary>
    public static string AllocationStateText(string state) => state switch
    {
        AllocationNone => "无持久化收款分摊行（证据缺口，不代表未付 / 已付 / 已结清 / 逾期）",
        AllocationHistoricalOnly => "仅有历史 / 无效分摊行（已作废 / 无效 / 无法确认：单独可见，绝不并入有效合计）",
        AllocationPartial => "部分分摊（有效已分摊金额小于发票含税总额，未分摊金额单独可见）",
        AllocationFull => "全额分摊（有效已分摊金额已覆盖发票含税总额）",
        AllocationOverAllocated => "无效证据：有效已分摊金额超过发票含税总额（按「未知」显示，绝不轧为 0 或负数）",
        _ => "未知（命中系统有界读取上限：分摊证据无法穷尽，不给部分合计）",
    };

    // ==================== 4. 剩余证据状态 ====================

    /// <summary>剩余证据可确认：发票含税总额 − 有效已分摊金额</summary>
    public const string RemainingKnown = "known";

    /// <summary>剩余证据未知（命中上限）：绝不用 0 顶替</summary>
    public const string RemainingUnknown = "unknown";

    /// <summary>剩余证据与源规则矛盾（有效已分摊金额 &gt; 发票含税总额）：无效证据，不做任何修复</summary>
    public const string RemainingOverAllocated = "over_allocated";

    /// <summary>剩余证据状态中文文案（接口、界面与文档同源）</summary>
    public static string RemainingStateText(string state) => state switch
    {
        RemainingKnown => "可确认（发票含税总额 − 有效已分摊金额）",
        RemainingOverAllocated => "无效证据：有效已分摊金额超过发票含税总额（绝不轧为 0，也不视为已结清）",
        _ => "未知（命中系统有界读取上限：不给部分合计）",
    };

    // ==================== 5. 发票状态筛选 ====================

    /// <summary>发票状态筛选：已登记（默认；草稿 / 已作废金额永不并入有效对账证据合计）</summary>
    public const string InvoiceStatusRecorded = "recorded";

    /// <summary>发票状态筛选：草稿（单列，不计入有效合计）</summary>
    public const string InvoiceStatusDraft = "draft";

    /// <summary>发票状态筛选：已作废（历史证据单列）</summary>
    public const string InvoiceStatusVoided = "voided";

    /// <summary>发票状态筛选：全部（含草稿 / 已作废历史）</summary>
    public const string InvoiceStatusAll = "all";

    /// <summary>支持的发票状态筛选取值</summary>
    public static readonly string[] SupportedInvoiceStatuses =
    {
        InvoiceStatusRecorded, InvoiceStatusDraft, InvoiceStatusVoided, InvoiceStatusAll,
    };

    /// <summary>发票状态筛选中文文案</summary>
    public static string InvoiceStatusText(string status) => status switch
    {
        InvoiceStatusRecorded => "已登记（有效证据）",
        InvoiceStatusDraft => "草稿（单列，不计入有效合计）",
        InvoiceStatusVoided => "已作废（历史证据单列）",
        InvoiceStatusAll => "全部状态",
        _ => "未知状态",
    };

    // ==================== 6. 筛选归一化（非法取值直接拒绝，不静默忽略） ====================

    /// <summary>归一化币种（去空白并大写；非法币种直接拒绝，复用系统币种白名单）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"不支持的币种：{currency}（仅支持 {string.Join(" / ", SupportedCurrencies)}，不做汇率换算、不跨币种合并）");
        }

        return value;
    }

    /// <summary>归一化发票状态筛选（null / 空 = 默认 recorded；非法取值直接拒绝）</summary>
    public static string? NormalizeInvoiceStatusFilter(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var value = status.Trim();
        if (value.Length == 0) return null;

        return value switch
        {
            InvoiceStatusRecorded or InvoiceStatusDraft or InvoiceStatusVoided or InvoiceStatusAll => value,
            _ => throw BusinessException.InvalidParameter(
                $"非法的发票状态筛选取值：{status}（仅支持 recorded / draft / voided / all）"),
        };
    }

    /// <summary>归一化分配状态筛选（null / 空 = 不过滤；非法取值直接拒绝）</summary>
    public static string? NormalizeAllocationFilter(string? allocationState)
    {
        if (string.IsNullOrWhiteSpace(allocationState)) return null;
        var value = allocationState.Trim();
        if (value.Length == 0) return null;

        return SupportedAllocationFilters.Contains(value, StringComparer.Ordinal)
            ? value
            : throw BusinessException.InvalidParameter(
                $"非法的分配状态筛选取值：{allocationState}（仅支持 none / historical_only / partial / full）");
    }
}
