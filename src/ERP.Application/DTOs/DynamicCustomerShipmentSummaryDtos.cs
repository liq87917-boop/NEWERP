using System.Text.Json.Serialization;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态客户出货量证据报表（ERP-232）「全匹配」汇总的只读数据传输对象：
/// 在分页与选定列投影之前，对同一份完整的有界、作用域化、已筛选
/// <see cref="Interfaces.ReportDtos.CustomerShipmentItem"/> 列表做纯派生，
/// 独立呈现「原币金额汇总」与「精确单位数量汇总」两块面板，
/// 绝不跨币种 / 跨单位合计、绝不读取第二来源或求和当前页行。
/// <para>金额汇总仅在「原币金额汇总」面板出现，绝不复制到任何单位行；单位汇总只汇总已审核订单明细数量证据，
/// 绝不声称完整订购数量。</para>
/// </summary>

/// <summary>
/// 全匹配原币金额汇总行（ERP-232，只读派生）：把同一原币（现有原始分组键）的全部匹配客户 × 原币证据行合并为一行。
/// <see cref="Currency"/> 为现有原始分组键（已知枚举码如 USD，未知 / 无效币种统一为「未知币种」）；
/// 已知币种金额为签名原币合计（可为负 / 零），未知币种金额为 null（绝不回落为 0、绝不推断币种）。
/// <see cref="CustomerCount"/> 为去重客户数、<see cref="OrderCount"/> 为不相交已审核订单数合计。
/// </summary>
public sealed class DynamicCustomerShipmentCurrencySummaryDto
{
    /// <summary>原币币种分组键（现有原始键，未知 / 无效币种显式保留为「未知币种」）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>原币币种标签（如「USD 美元」；未知为「未知币种」）</summary>
    public string CurrencyLabel { get; set; } = string.Empty;

    /// <summary>原币金额合计（签名；已知币种为小计，未知币种为 null，绝不回落为 0）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalAmount { get; set; }

    /// <summary>去重客户数（该原币桶内的不同客户 Id 数）</summary>
    public int CustomerCount { get; set; }

    /// <summary>已审核订单数合计（各客户 × 原币行的不相交订单数之和）</summary>
    public int OrderCount { get; set; }

    /// <summary>金额证据标签（已知原币 / 未知币种仅计数证据）</summary>
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>
/// 全匹配精确单位数量汇总行（ERP-232，只读派生）：在每个原币桶内按明细原始单位身份（Ordinal、不归一化 / 换算）合并
/// 现有单位分组；已知单位给签名数量合计与明细条数合计，空白 / 未知单位数量为 null（绝不回落为 0、绝不换算单位），
/// 仅保留明细计数证据。本行绝不携带任何货币金额。
/// </summary>
public sealed class DynamicCustomerShipmentUnitSummaryDto
{
    /// <summary>所属原币桶（现有原始分组键）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>明细原始精确单位（非空原样保留；空白 / 未知归入「未知单位」）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>该单位签名数量合计；空白 / 未知单位为 null（不推断单位、不换算）</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Quantity { get; set; }

    /// <summary>该单位分组内的非删除明细条数合计（明细计数，非订单 / 客户去重数）</summary>
    public int DetailCount { get; set; }

    /// <summary>数量口径证据标签（已知单位 / 未知单位仅明细计数证据）</summary>
    public string QuantityLabel { get; set; } = string.Empty;
}

/// <summary>
/// 动态客户出货量证据报表「全匹配」汇总结果（ERP-232，只读派生）：在分页与选定列投影之前、
/// 基于全部匹配的有界作用域化已筛选客户 × 原币证据行纯派生。金额只在原币面板出现、绝不复制到单位行；
/// <see cref="CompletenessReasons"/> / <see cref="IncompleteBucketCount"/> 显式保留「数量证据不完整」的
/// 逐客户 × 原币完整度原因与不完整桶数；<see cref="CoverageText"/> 显式声明覆盖范围与「非实际出库 / 装柜 / 收款」证据依据。
/// </summary>
public sealed record DynamicCustomerShipmentSummaryDto(
    List<DynamicCustomerShipmentReportFieldDto> CurrencyColumns,
    List<DynamicCustomerShipmentReportFieldDto> UnitColumns,
    List<DynamicCustomerShipmentCurrencySummaryDto> CurrencyRows,
    List<DynamicCustomerShipmentUnitSummaryDto> UnitRows,
    int IncompleteBucketCount,
    List<string> CompletenessReasons,
    string CoverageText);
