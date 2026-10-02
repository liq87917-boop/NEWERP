using System.Text.Json.Serialization;
using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态业务员提成证据报表（ERP-247）「全匹配」汇总的只读数据传输对象：在分页与选定列投影之前，
/// 对同一份完整的有界、作用域化、已筛选 <see cref="ReportDtos.SalesCommissionItem"/> 列表做纯派生，
/// 独立呈现「按原始原币合并全部匹配业务员桶 × 原币证据行」的汇总面板。
/// <para>每个桶对应一个持久化原始币种键；已知币种求和签名 <see cref="DynamicSalesCommissionCurrencySummaryDto.SalesAmount"/>
/// （仅在全部金额已知时）、已审核订单数求和不相交 OrderCount、去重业务员桶数去重 SalesmanId
/// （未指定业务员桶单独计一个，非员工人数）；未知 / 无效币种保留各自原始键、金额为 null 且显式未知订单计数；
/// 利润 / 利润率 / 提成额恒为 null 并给出历史依据说明；跨币种去重业务员桶与不相交已审核订单数为全局派生；
/// 当前参考比例仅在全部匹配证据行一致携带同一已知比例与依据时保留，缺失 / 冲突保持 null 并给原因，
/// 绝不加权平均 / 求和 / 回退为 0；绝不跨币种合计金额、绝不累加百分比。</para>
/// </summary>

/// <summary>
/// 全匹配原币汇总行（ERP-247，只读派生）：把同一原始原币键（现有原始分组键）的全部匹配业务员桶 × 原币证据行合并为一行。
/// <see cref="Currency"/> 为现有原始分组键（已知枚举码如 USD，未知 / 无效币种保留原始键如 "999"，绝不折叠为默认币种）；
/// 已知币种金额为签名原币合计（仅在全部金额已知时非 null），未知币种金额为 null（绝不回落为 0、绝不推断币种）。
/// <see cref="ApprovedOrders"/> 为该币种内不相交已审核订单数合计、<see cref="UniqueSalesmanBuckets"/> 为去重业务员桶数
/// （未指定业务员桶单独计一个，非员工人数）；<see cref="Profit"/> / <see cref="ProfitRate"/> / <see cref="CommissionAmount"/>
/// 恒为 null 并附历史依据说明。
/// </summary>
public sealed class DynamicSalesCommissionCurrencySummaryDto
{
    /// <summary>原币币种原始分组键（已知枚举码如 USD；未知 / 无效币种保留原始键如 "999"）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>原币币种标签（如「USD 美元」；未知为「未知币种」）</summary>
    public string CurrencyLabel { get; set; } = string.Empty;

    /// <summary>该原币桶内不相交已审核、未删除销售订单数合计</summary>
    public int ApprovedOrders { get; set; }

    /// <summary>该原币桶内去重业务员桶数（不同 SalesmanId 数；未指定业务员桶单独计一个，非员工人数）</summary>
    public int UniqueSalesmanBuckets { get; set; }

    /// <summary>已知原币签名金额合计（仅在全部金额已知时非 null；未知 / 无效币种为 null，绝不回落为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SalesAmount { get; set; }

    /// <summary>金额完整度说明（全部已知时签名合计；存在未知 / 无效币种时仅计数证据、金额 null）</summary>
    public string AmountCompletenessText { get; set; } = string.Empty;

    /// <summary>利润（恒为 null：当前售价 / 成本价不能证明历史可比较成本 / 利润，绝不回落为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Profit { get; set; }

    /// <summary>利润率 %（恒为 null：与利润同源，绝不回落为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ProfitRate { get; set; }

    /// <summary>提成额（恒为 null：历史可比较成本 / 利润缺失，绝不从当前成本派生或推断为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CommissionAmount { get; set; }

    /// <summary>利润历史依据说明（恒未知）</summary>
    public string ProfitReasonText { get; set; } = string.Empty;

    /// <summary>利润率历史依据说明（恒未知，与利润同源）</summary>
    public string ProfitRateReasonText { get; set; } = string.Empty;

    /// <summary>提成额历史依据说明（恒未知）</summary>
    public string CommissionAmountReasonText { get; set; } = string.Empty;
}

/// <summary>
/// 动态业务员提成证据报表「全匹配」汇总结果（ERP-247，只读派生）：在分页与选定列投影之前、
/// 基于全部匹配的有界、作用域化、已筛选业务员桶 × 原币证据行纯派生。
/// <see cref="CurrencyColumns"/> / <see cref="CurrencyRows"/> 为固定原币汇总面板列与行（与明细页选定列 / 当前页无关）；
/// <see cref="GlobalUniqueSalesmanBuckets"/> / <see cref="GlobalApprovedOrders"/> 为跨币种去重的全局计数；
/// <see cref="CurrentReferenceRate"/> 仅在全部匹配证据行一致携带同一已知比例与依据时非 null（含显式 0），
/// 缺失 / 冲突保持 null 并给原因；<see cref="CoverageText"/> 显式声明覆盖范围与证据依据；<see cref="EmptyText"/> 显式声明无匹配证据。
/// </summary>
public sealed class DynamicSalesCommissionSummaryDto
{
    /// <summary>全匹配原币汇总列（固定，与明细页选定列 / 当前页无关）</summary>
    public List<DynamicSalesCommissionReportFieldDto> CurrencyColumns { get; set; } = new();

    /// <summary>全匹配原币汇总行（每个原始币种键一行，稳定排序）</summary>
    public List<DynamicSalesCommissionCurrencySummaryDto> CurrencyRows { get; set; } = new();

    /// <summary>跨币种去重的业务员桶总数（未指定业务员桶单独计一个，非员工人数）</summary>
    public int GlobalUniqueSalesmanBuckets { get; set; }

    /// <summary>不相交已审核、未删除销售订单数合计（各币种行 OrderCount 之和，绝无重复计数）</summary>
    public int GlobalApprovedOrders { get; set; }

    /// <summary>当前参考比例（仅在全部匹配证据行一致携带同一已知比例与依据时非 null，含显式 0；缺失 / 冲突为 null）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CurrentReferenceRate { get; set; }

    /// <summary>当前参考比例依据说明（缺失 / 冲突 / 无证据时的显式原因；一致已知时为空）</summary>
    public string CurrentReferenceRateReason { get; set; } = string.Empty;

    /// <summary>覆盖范围说明（全部匹配证据行，与当前页 / 选定列无关）</summary>
    public string CoverageText { get; set; } = string.Empty;

    /// <summary>利润 / 利润率 / 提成额历史依据说明（恒未知，不做当前价推算）</summary>
    public string ProfitBasisText { get; set; } = string.Empty;

    /// <summary>无匹配证据时的显式说明（空汇总时非空）</summary>
    public string EmptyText { get; set; } = string.Empty;
}
