using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 代理服务费对账单月度汇总查询条件（ERP-110，全部为只读筛选参数）。
/// 按对账日期区间与客户 / 币种过滤，按「对账日期所属年月 + 客户 + 币种」稳定分页。
/// </summary>
public sealed class AgencyServiceFeeMonthlySummaryQuery
{
    /// <summary>对账日期开始（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateFrom { get; set; }

    /// <summary>对账日期结束（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateTo { get; set; }

    /// <summary>客户筛选（留空 = 全部客户）</summary>
    public long? CustomerId { get; set; }

    /// <summary>币种筛选（留空 = 全部币种，不同币种分别成组、绝不合并）</summary>
    public string? Currency { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出按上限截断）</summary>
    public int PageSize { get; set; } = AgencyServiceFeeMonthlySummaryRules.DefaultPageSize;
}

/// <summary>
/// 代理服务费对账单月度汇总视图（ERP-110，只读派生）：按「对账日期所属年月 + 客户 + 原币」分组，
/// 仅未删除且已登记的对账单计入原币合计，草稿与已作废单独计数；服务期间跨月不按期间分摊。
/// </summary>
public sealed class AgencyServiceFeeMonthlySummaryView
{
    /// <summary>对账日期开始（归一化后回显）</summary>
    public DateTime? StatementDateFrom { get; init; }

    /// <summary>对账日期结束（归一化后回显）</summary>
    public DateTime? StatementDateTo { get; init; }

    /// <summary>客户筛选回显（空 = 全部客户）</summary>
    public long? CustomerId { get; init; }

    /// <summary>币种筛选回显（空 = 全部币种）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>符合筛选条件的「年月 + 客户 + 币种」分组总数（分页前）</summary>
    public int Total { get; init; }

    /// <summary>当前页码</summary>
    public int Page { get; init; }

    /// <summary>每页条数</summary>
    public int PageSize { get; init; }

    /// <summary>总页数</summary>
    public int TotalPages { get; init; }

    /// <summary>是否命中分页截断（true = 本页之外仍有更多分组）</summary>
    public bool Truncated { get; init; }

    /// <summary>本页分组数</summary>
    public int GroupCount { get; init; }

    /// <summary>无匹配时显式提示</summary>
    public string EmptyText { get; init; } = string.Empty;

    /// <summary>本页分组行</summary>
    public List<AgencyServiceFeeMonthlySummaryRow> Rows { get; init; } = new();

    /// <summary>分组与金额口径（与 <see cref="AgencyServiceFeeMonthlySummaryRules.RuleText"/> 同源）</summary>
    public string RuleText { get; init; } = AgencyServiceFeeMonthlySummaryRules.RuleText;

    /// <summary>模块边界（与收入确认 / 应收 / 付款通知 / 税务 / 结算的分离）</summary>
    public string BoundaryText { get; init; } = AgencyServiceFeeMonthlySummaryRules.BoundaryText;

    /// <summary>只读边界</summary>
    public string ReadOnlyText { get; init; } = AgencyServiceFeeMonthlySummaryRules.ReadOnlyText;

    /// <summary>服务期间跨月不按期间分摊说明</summary>
    public string NoProrationText { get; init; } = AgencyServiceFeeMonthlySummaryRules.NoProrationText;

    /// <summary>币种隔离说明</summary>
    public string CurrencyIsolationText { get; init; } =
        AgencyServiceFeeMonthlySummaryRules.CurrencyIsolationText;

    /// <summary>证据口径说明</summary>
    public string EvidenceOnlyText { get; init; } = AgencyServiceFeeMonthlySummaryRules.EvidenceOnlyText;
}

/// <summary>
/// 一个月度汇总分组行（ERP-110）：同一「对账日期所属年月 + 客户 + 原币」内的对账单证据汇总。
/// <para>原币合计只统计未删除且已登记的对账单；草稿与已作废金额单独列示、绝不并入合计；
/// 服务期间跨月的对账单全额计入其对账日期所属月份，不按期间分摊。</para>
/// </summary>
public sealed class AgencyServiceFeeMonthlySummaryRow
{
    /// <summary>对账日期所属年份</summary>
    public int StatementYear { get; init; }

    /// <summary>对账日期所属月份（1 ~ 12）</summary>
    public int StatementMonth { get; init; }

    /// <summary>年月文案（<c>yyyy-MM</c>）</summary>
    public string StatementMonthText { get; init; } = string.Empty;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; init; }

    /// <summary>客户编码快照</summary>
    public string CustomerCode { get; init; } = string.Empty;

    /// <summary>客户名称快照（历史证据照常可读）</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>币种（原币，不做汇率换算、不跨币种合并）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>币种小数位</summary>
    public int AmountDecimals { get; init; }

    /// <summary>已登记（计入原币合计）张数</summary>
    public int RegisteredCount { get; init; }

    /// <summary>已登记原币合计（仅未删除且已登记的对账单持久化 TotalAmount 求和）</summary>
    public decimal RegisteredTotalAmount { get; init; }

    /// <summary>已登记原币合计文案（原币 + 币种精度）</summary>
    public string RegisteredTotalAmountText { get; init; } = string.Empty;

    /// <summary>草稿张数（单独计数，不计入原币合计）</summary>
    public int DraftCount { get; init; }

    /// <summary>草稿金额（单独列示，不计入原币合计）</summary>
    public decimal DraftTotalAmount { get; init; }

    /// <summary>草稿金额文案</summary>
    public string DraftTotalAmountText { get; init; } = string.Empty;

    /// <summary>已作废张数（单独计数，不计入原币合计）</summary>
    public int VoidedCount { get; init; }

    /// <summary>已作废金额（单独列示，不计入原币合计）</summary>
    public decimal VoidedTotalAmount { get; init; }

    /// <summary>已作废金额文案</summary>
    public string VoidedTotalAmountText { get; init; } = string.Empty;

    /// <summary>该分组内未删除对账单总张数（已登记 + 草稿 + 已作废）</summary>
    public int StatementCount { get; init; }
}
