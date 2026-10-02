using System.Text.Json.Serialization;
using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态业务员产值证据报表（ERP-239）「全匹配」汇总的只读数据传输对象：
/// 在分页与选定列投影之前，对同一份完整的有界、作用域化、已筛选
/// <see cref="ReportDtos.SalesmanOutputItem"/> 列表做纯派生，
/// 独立呈现「按原始原币合并全部匹配业务员 × 原币证据行」的汇总面板。
/// <para>每个桶对应一个持久化原始币种键；已知币种求和签名 <see cref="DynamicSalesmanOutputCurrencySummaryDto.TotalAmount"/>
/// （不先对每个业务员四舍五入）、已分配已审核订单数求和不相交 OrderCount、业务员数去重 SalesmanId；
/// 未知 / 无效币种保留各自原始键、金额为 null 且显式未知订单计数；利润与利润率恒为 null 并给出
/// 历史成本依据说明；绝不跨币种合计 / 比较、绝不做当前价成本 / 利润推算、绝不读取第二来源或求和当前页行。</para>
/// </summary>

/// <summary>
/// 全匹配原币汇总行（ERP-239，只读派生）：把同一原始原币键（现有原始分组键）的全部匹配业务员 × 原币证据行合并为一行。
/// <see cref="Currency"/> 为现有原始分组键（已知枚举码如 USD，未知 / 无效币种保留原始键如 "999"，绝不折叠为默认币种）；
/// 已知币种金额为签名原币合计（可为负 / 零），未知币种金额为 null（绝不回落为 0、绝不推断币种）。
/// <see cref="SalesmanCount"/> 为去重业务员数、<see cref="OrderCount"/> 为不相交已分配已审核订单数合计；
/// <see cref="TotalProfit"/> 与 <see cref="ProfitRate"/> 恒为 null 并附历史成本依据说明。
/// </summary>
public sealed class DynamicSalesmanOutputCurrencySummaryDto
{
    /// <summary>原币币种原始分组键（已知枚举码如 USD；未知 / 无效币种保留原始键如 "999"）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>原币币种标签（如「USD 美元」；未知为「未知币种」）</summary>
    public string CurrencyLabel { get; set; } = string.Empty;

    /// <summary>已知原币签名金额合计（未知币种为 null，绝不回落为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalAmount { get; set; }

    /// <summary>该原币桶内去重业务员数（不同 SalesmanId 数）</summary>
    public int SalesmanCount { get; set; }

    /// <summary>已分配已审核订单数合计（各业务员 × 原币行的不相交 OrderCount 之和）</summary>
    public int OrderCount { get; set; }

    /// <summary>利润（恒为 null：当前售价 / 成本价不能证明历史可比较成本 / 利润）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalProfit { get; set; }

    /// <summary>利润率（恒为 null：与利润同源）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ProfitRate { get; set; }

    /// <summary>金额证据标签（已知原币 / 未知币种仅计数证据）</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>利润 / 利润率历史成本依据说明（恒未知，不做当前价推算）</summary>
    public string ProfitBasis { get; set; } = string.Empty;
}

/// <summary>
/// 动态业务员产值证据报表「全匹配」汇总结果（ERP-239，只读派生）：在分页与选定列投影之前、
/// 基于全部匹配的有界、作用域化、已筛选业务员 × 原币证据行纯派生。
/// <see cref="CurrencyColumns"/> / <see cref="CurrencyRows"/> 为固定原币汇总面板列与行（与明细页选定列 / 当前页无关）；
/// <see cref="CoverageText"/> 显式声明覆盖范围与「非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款」证据依据；
/// <see cref="ProfitBasisText"/> 显式声明利润 / 利润率历史成本依据（恒 null、不回落为 0、不做当前价推算）。
/// </summary>
public sealed record DynamicSalesmanOutputSummaryDto(
    List<DynamicSalesmanOutputReportFieldDto> CurrencyColumns,
    List<DynamicSalesmanOutputCurrencySummaryDto> CurrencyRows,
    string CoverageText,
    string ProfitBasisText);
